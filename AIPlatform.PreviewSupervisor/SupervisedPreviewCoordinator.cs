using AIPlatform.Core.Studio;

namespace AIPlatform.PreviewSupervisor;

public sealed class SupervisedPreviewCoordinator : IAsyncDisposable
{
    private readonly IRecoverableHyperVVmControl _control;
    private readonly DurableSupervisorJournal _journal;
    private readonly ISupervisorClock _clock;
    private readonly SupervisorOptions _options;
    private readonly Func<Guid> _newSessionId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, SupervisorSessionRecord> _sessions = [];
    private readonly Dictionary<Guid, ITimer> _timers = [];
    private bool _initialized;
    private bool _disposed;

    public SupervisedPreviewCoordinator(
        IRecoverableHyperVVmControl control,
        DurableSupervisorJournal journal,
        ISupervisorClock? clock = null,
        SupervisorOptions? options = null,
        Func<Guid>? newSessionId = null)
    {
        _control = control;
        _journal = journal;
        _clock = clock ?? new SystemSupervisorClock();
        _options = options ?? SupervisorOptions.Default;
        _options.Validate();
        _newSessionId = newSessionId ?? Guid.NewGuid;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (_initialized)
                throw new InvalidOperationException("Supervisor coordinator is already initialized.");

            foreach (var record in _journal.ReadLatest())
            {
                _sessions.Add(record.SessionId, record);
                if (!IsTerminal(record.State))
                    await RecoverRecordAsync(record);
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisorIpcResponse> HandleAsync(
        SupervisorIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        SupervisorIpcCodec.ValidateRequest(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfUnavailable();
            if (!_initialized)
                throw new InvalidOperationException("Supervisor recovery must finish before IPC is enabled.");

            return request.Command switch
            {
                SupervisorCommand.CreateSession => await CreateSessionAsync(request),
                SupervisorCommand.GetStatus => GetStatus(request),
                SupervisorCommand.Heartbeat => await HeartbeatAsync(request),
                SupervisorCommand.Stop => await StopAsync(request),
                SupervisorCommand.RetryCleanup => await RetryCleanupAsync(request),
                SupervisorCommand.ReserveSocketChannel => await ReserveSocketChannelAsync(request),
                _ => throw new SupervisorProtocolException("UnauthorizedCommand"),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || !_initialized)
                return;
            foreach (var session in _sessions.Values.Where(record => !IsTerminal(record.State)).ToArray())
                await BeginCleanupAsync(session, IsExpired(session)
                    ? SupervisorTerminalState.Expired
                    : SupervisorTerminalState.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var timer in _timers.Values)
                timer.Dispose();
            _timers.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SupervisorIpcResponse> CreateSessionAsync(SupervisorIpcRequest request)
    {
        if (_sessions.Values.Any(record => record.CreationRequestId == request.RequestId))
            throw new SupervisorProtocolException("Replay");
        if (_sessions.Values.Any(record => !IsTerminal(record.State)))
            throw new SupervisorProtocolException("SessionAlreadyActive");

        var sessionId = _newSessionId();
        if (sessionId == Guid.Empty || _sessions.ContainsKey(sessionId))
            throw new InvalidOperationException("Session id generator returned a reused or empty id.");
        var now = _clock.UtcNow.ToUniversalTime();
        var uptime = _clock.UptimeMilliseconds;
        var lifetimeMilliseconds = checked((long)_options.MaximumLifetime.TotalMilliseconds);
        var record = new SupervisorSessionRecord(
            JournalSequence: 0,
            Revision: 0,
            SessionId: sessionId,
            CreationRequestId: request.RequestId,
            Lease: null,
            State: SupervisedSessionState.Provisioning,
            CreatedAtUtc: now,
            ExpiresAtUtc: now + _options.MaximumLifetime,
            BootEpochUtc: _clock.BootEpochUtc.ToUniversalTime(),
            CreatedUptimeMilliseconds: uptime,
            ExpiresAtUptimeMilliseconds: checked(uptime + lifetimeMilliseconds),
            LastCommandSequence: 0,
            RecentCommandIds: [],
            LastHeartbeatUptimeMilliseconds: 0,
            RequestedTerminalState: null,
            CleanupFailure: null);
        record = Persist(record);
        ArmExpiry(record);

        try
        {
            using var createTimeout = CreateDeadlineCancellation(record);
            var lease = await _control.CreateAsync(sessionId, createTimeout.Token);
            ValidateLease(sessionId, lease);
            record = Persist(record with
            {
                Lease = lease,
                State = SupervisedSessionState.Provisioned,
            });
            if (IsExpired(record))
            {
                record = await BeginCleanupAsync(record, SupervisorTerminalState.Expired);
                return Response(request, accepted: false, "ExpiredDuringProvisioning", record);
            }

            using var startTimeout = CreateDeadlineCancellation(record);
            await _control.StartAsync(lease, startTimeout.Token);
            using var stateTimeout = CreateDeadlineCancellation(record);
            var state = await _control.GetStateAsync(lease, stateTimeout.Token);
            if (state != HyperVPreviewVmState.Running)
                throw new InvalidOperationException("Hyper-V control did not confirm Running.");
            record = Persist(record with { State = SupervisedSessionState.Running });
            if (IsExpired(record))
                record = await BeginCleanupAsync(record, SupervisorTerminalState.Expired);

            return Response(request, accepted: record.State == SupervisedSessionState.Running,
                record.State == SupervisedSessionState.Running ? null : "ExpiredDuringStartup", record);
        }
        catch (Exception error) when (error is not SupervisorProtocolException)
        {
            if (_sessions.TryGetValue(sessionId, out var current) && !IsTerminal(current.State))
            {
                var target = error is OperationCanceledException || IsExpired(current)
                    ? SupervisorTerminalState.Expired
                    : SupervisorTerminalState.Stopped;
                current = await BeginCleanupAsync(current, target);
                var code = current.State == SupervisedSessionState.CleanupFailed
                    ? "StartupFailedCleanupFailed"
                    : "StartupFailed";
                return Response(request, accepted: false, code, current);
            }

            Console.Error.WriteLine($"Supervisor create failed for session {sessionId:N}: {error.GetType().Name}");
            throw;
        }
    }

    private SupervisorIpcResponse GetStatus(SupervisorIpcRequest request)
    {
        var record = RequireSession(request.SessionId!.Value);
        return Response(request, accepted: true, errorCode: null, record);
    }

    private async Task<SupervisorIpcResponse> HeartbeatAsync(SupervisorIpcRequest request)
    {
        var record = RequireSession(request.SessionId!.Value);
        ValidateNextCommand(record, request);
        if (record.State != SupervisedSessionState.Running)
            throw new SupervisorProtocolException("SessionNotRunning");
        if (IsExpired(record))
        {
            record = await BeginCleanupAsync(record, SupervisorTerminalState.Expired);
            return Response(request, accepted: false, "Expired", record);
        }

        var uptime = _clock.UptimeMilliseconds;
        if (record.LastHeartbeatUptimeMilliseconds > 0 &&
            uptime - record.LastHeartbeatUptimeMilliseconds <
            (long)SupervisorPolicy.MinimumHeartbeatInterval.TotalMilliseconds)
            throw new SupervisorProtocolException("HeartbeatRateLimited");

        record = Persist(RecordCommand(record, request) with
        {
            LastHeartbeatUptimeMilliseconds = uptime,
        });
        return Response(request, accepted: true, errorCode: null, record);
    }

    private async Task<SupervisorIpcResponse> StopAsync(SupervisorIpcRequest request)
    {
        var record = RequireSession(request.SessionId!.Value);
        ValidateNextCommand(record, request);
        if (IsTerminal(record.State))
            throw new SupervisorProtocolException("SessionTerminal");
        record = RecordCommand(record, request);
        var target = IsExpired(record)
            ? SupervisorTerminalState.Expired
            : SupervisorTerminalState.Stopped;
        record = await BeginCleanupAsync(record, target);
        return Response(request, accepted: record.State == SupervisedSessionState.Stopped,
            record.State == SupervisedSessionState.Stopped ? null : "CleanupFailed", record);
    }

    private async Task<SupervisorIpcResponse> RetryCleanupAsync(SupervisorIpcRequest request)
    {
        var record = RequireSession(request.SessionId!.Value);
        ValidateNextCommand(record, request);
        if (record.State != SupervisedSessionState.CleanupFailed ||
            record.RequestedTerminalState is null)
            throw new SupervisorProtocolException("CleanupRetryNotAllowed");

        var terminalTarget = record.RequestedTerminalState
            ?? throw new InvalidOperationException("Cleanup retry has no terminal target.");
        record = RecordCommand(record, request);
        record = await BeginCleanupAsync(record, terminalTarget);
        return Response(request, accepted: IsTerminal(record.State),
            IsTerminal(record.State) ? null : "CleanupFailed", record);
    }

    private async Task<SupervisorIpcResponse> ReserveSocketChannelAsync(
        SupervisorIpcRequest request)
    {
        var record = RequireSession(request.SessionId!.Value);
        ValidateNextCommand(record, request);
        if (record.State != SupervisedSessionState.Running)
            throw new SupervisorProtocolException("SessionNotRunning");
        if (record.Lease is null || record.Lease != request.Lease)
            throw new SupervisorProtocolException("LeaseMismatch");
        if (IsExpired(record))
        {
            record = await BeginCleanupAsync(record, SupervisorTerminalState.Expired);
            return Response(request, accepted: false, "Expired", record);
        }

        var role = request.ChannelRole!.Value;
        if (record.ReservedChannels.Contains(role))
            throw new SupervisorProtocolException("ChannelAlreadyReserved");

        record = Persist(RecordCommand(record, request) with
        {
            ReservedChannels = [.. record.ReservedChannels, role],
        });
        return Response(request, accepted: true, errorCode: null, record);
    }

    private async Task RecoverRecordAsync(SupervisorSessionRecord record)
    {
        var target = record.RequestedTerminalState ??
            (IsExpired(record) || _clock.UptimeMilliseconds < record.CreatedUptimeMilliseconds
                ? SupervisorTerminalState.Expired
                : SupervisorTerminalState.Stopped);
        await BeginCleanupAsync(record, target);
    }

    private async Task<SupervisorSessionRecord> BeginCleanupAsync(
        SupervisorSessionRecord record,
        SupervisorTerminalState target)
    {
        var retryingCleanup = record.State == SupervisedSessionState.CleanupFailed;
        CancelExpiry(record.SessionId);
        if (IsTerminal(record.State))
            return record;

        record = Persist(record with
        {
            State = SupervisedSessionState.Stopping,
            RequestedTerminalState = target,
            CleanupFailure = null,
        });

        try
        {
            var lease = record.Lease ?? await FindBySessionIdAsync(record, target, retryingCleanup);
            if (lease is null)
                return Persist(record with { State = ToState(target) });
            ValidateLease(record.SessionId, lease);
            if (record.Lease is not null && record.Lease != lease)
                return await FailCleanupAsync(record, SupervisorCleanupFailure.LeaseMismatch);
            if (record.Lease is null)
                record = Persist(record with { Lease = lease });

            var vmState = await GetVmStateAsync(record, lease, target, retryingCleanup);
            if (!Enum.IsDefined(vmState))
                return await FailCleanupAsync(record, SupervisorCleanupFailure.StateQueryFailed);
            if (vmState == HyperVPreviewVmState.Destroyed)
                return Persist(record with { State = ToState(target) });

            if (vmState is not (HyperVPreviewVmState.Created or HyperVPreviewVmState.Stopped))
            {
                using var graceful = CreateCleanupCancellation(
                    record, target, SupervisorPolicy.GracefulStopTimeout, retryingCleanup);
                try
                {
                    await _control.StopAsync(lease, HyperVPreviewStopMode.Graceful, graceful.Token);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or
                                                   InvalidOperationException or TimeoutException)
                {
                    Console.Error.WriteLine($"Graceful stop failed for {record.SessionId:N}: {error.GetType().Name}");
                }

                vmState = await GetVmStateAsync(record, lease, target, retryingCleanup);
                if (vmState is not (HyperVPreviewVmState.Created or HyperVPreviewVmState.Stopped or
                    HyperVPreviewVmState.Destroyed))
                {
                    using var forced = CreateCleanupCancellation(
                        record, target, SupervisorPolicy.ForcedStopTimeout, retryingCleanup);
                    try
                    {
                        await _control.StopAsync(lease, HyperVPreviewStopMode.Force, forced.Token);
                    }
                    catch (Exception error) when (error is OperationCanceledException or IOException or
                                                       InvalidOperationException or TimeoutException)
                    {
                        Console.Error.WriteLine($"Forced stop failed for {record.SessionId:N}: {error.GetType().Name}");
                    }

                    vmState = await GetVmStateAsync(record, lease, target, retryingCleanup);
                }
            }

            if (vmState == HyperVPreviewVmState.Destroyed)
                return Persist(record with { State = ToState(target) });
            if (vmState is not (HyperVPreviewVmState.Created or HyperVPreviewVmState.Stopped))
                return await FailCleanupAsync(record, SupervisorCleanupFailure.StopUnconfirmed);

            try
            {
                using var destroyTimeout = CreateCleanupCancellation(
                    record, target, SupervisorPolicy.DestroyTimeout, retryingCleanup);
                await _control.DestroyAsync(lease, destroyTimeout.Token);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or
                                               TimeoutException or OperationCanceledException)
            {
                Console.Error.WriteLine($"Destroy failed for {record.SessionId:N}: {error.GetType().Name}");
            }

            var finalState = await GetVmStateAsync(record, lease, target, retryingCleanup);
            if (finalState == HyperVPreviewVmState.Destroyed)
                return Persist(record with { State = ToState(target) });

            var remaining = await FindBySessionIdAsync(record, target, retryingCleanup);
            if (remaining is null)
                return Persist(record with { State = ToState(target) });
            return await FailCleanupAsync(record, SupervisorCleanupFailure.DestroyUnconfirmed);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Cleanup failed for {record.SessionId:N}: {error.GetType().Name}");
            return await FailCleanupAsync(record, record.Lease is null
                ? SupervisorCleanupFailure.ResourceLookupFailed
                : SupervisorCleanupFailure.StateQueryFailed);
        }
    }

    private async Task<SupervisorSessionRecord> FailCleanupAsync(
        SupervisorSessionRecord record,
        SupervisorCleanupFailure failure)
    {
        try
        {
            return Persist(record with
            {
                State = SupervisedSessionState.CleanupFailed,
                CleanupFailure = failure,
            });
        }
        catch (IOException error)
        {
            Console.Error.WriteLine($"Could not persist cleanup failure for {record.SessionId:N}: {error.GetType().Name}");
            throw;
        }
    }

    private void ArmExpiry(SupervisorSessionRecord record)
    {
        var remaining = record.ExpiresAtUptimeMilliseconds - _clock.UptimeMilliseconds;
        var delay = remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(remaining);
        var timer = _clock.CreateTimer(
            static state =>
            {
                var (owner, sessionId) = ((SupervisedPreviewCoordinator, Guid))state!;
                _ = owner.RunExpiryAsync(sessionId);
            },
            (this, record.SessionId),
            delay,
            Timeout.InfiniteTimeSpan);
        if (_timers.Remove(record.SessionId, out var previous))
            previous.Dispose();
        _timers.Add(record.SessionId, timer);
    }

    private CancellationTokenSource CreateDeadlineCancellation(SupervisorSessionRecord record)
    {
        var remaining = record.ExpiresAtUptimeMilliseconds - _clock.UptimeMilliseconds;
        if (remaining <= 0)
            return new CancellationTokenSource(TimeSpan.Zero);
        return new CancellationTokenSource(TimeSpan.FromMilliseconds(remaining));
    }

    private async Task<HyperVPreviewVmState> GetVmStateAsync(
        SupervisorSessionRecord record,
        HyperVPreviewVmLease lease,
        SupervisorTerminalState target,
        bool retryingCleanup)
    {
        using var timeout = CreateCleanupCancellation(
            record, target, SupervisorPolicy.StateQueryTimeout, retryingCleanup);
        return await _control.GetStateAsync(lease, timeout.Token);
    }

    private async Task<HyperVPreviewVmLease?> FindBySessionIdAsync(
        SupervisorSessionRecord record,
        SupervisorTerminalState target,
        bool retryingCleanup)
    {
        using var timeout = CreateCleanupCancellation(
            record, target, SupervisorPolicy.StateQueryTimeout, retryingCleanup);
        return await _control.FindBySessionIdAsync(record.SessionId, timeout.Token);
    }

    private CancellationTokenSource CreateCleanupCancellation(
        SupervisorSessionRecord record,
        SupervisorTerminalState target,
        TimeSpan operationTimeout,
        bool retryingCleanup = false)
    {
        var boundedMilliseconds = operationTimeout.TotalMilliseconds;
        if (target == SupervisorTerminalState.Expired && !retryingCleanup)
        {
            var cleanupRemaining = record.ExpiresAtUptimeMilliseconds +
                                   (long)SupervisorPolicy.CleanupMargin.TotalMilliseconds -
                                   _clock.UptimeMilliseconds;
            boundedMilliseconds = Math.Min(boundedMilliseconds, cleanupRemaining);
        }
        return new CancellationTokenSource(boundedMilliseconds <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(boundedMilliseconds));
    }

    private async Task RunExpiryAsync(Guid sessionId)
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed || !_sessions.TryGetValue(sessionId, out var record) ||
                    record.State is not (SupervisedSessionState.Provisioning or
                        SupervisedSessionState.Provisioned or SupervisedSessionState.Running))
                    return;
                if (!IsExpired(record))
                {
                    ArmExpiry(record);
                    return;
                }
                await BeginCleanupAsync(record, SupervisorTerminalState.Expired);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Expiry handling failed for {sessionId:N}: {error.GetType().Name}");
        }
    }

    private SupervisorSessionRecord Persist(SupervisorSessionRecord record)
    {
        var persisted = _journal.Append(record);
        _sessions[record.SessionId] = persisted;
        return persisted;
    }

    private SupervisorSessionRecord RequireSession(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var record))
            throw new SupervisorProtocolException("UnknownSession");
        return record;
    }

    private static void ValidateNextCommand(
        SupervisorSessionRecord record,
        SupervisorIpcRequest request)
    {
        if (record.RecentCommandIds.Contains(request.RequestId))
            throw new SupervisorProtocolException("Replay");
        if (request.Sequence != record.LastCommandSequence + 1)
            throw new SupervisorProtocolException("OutOfSequence");
        if (record.RecentCommandIds.Count >= SupervisorPolicy.MaximumRecentCommands)
            throw new SupervisorProtocolException("CommandLimitReached");
    }

    private static SupervisorSessionRecord RecordCommand(
        SupervisorSessionRecord record,
        SupervisorIpcRequest request)
    {
        var ids = record.RecentCommandIds.ToList();
        ids.Add(request.RequestId);
        return record with
        {
            LastCommandSequence = request.Sequence,
            RecentCommandIds = ids,
        };
    }

    private void CancelExpiry(Guid sessionId)
    {
        if (_timers.Remove(sessionId, out var timer))
            timer.Dispose();
    }

    private bool IsExpired(SupervisorSessionRecord record) =>
        _clock.UptimeMilliseconds >= record.ExpiresAtUptimeMilliseconds ||
        _clock.UtcNow >= record.ExpiresAtUtc ||
        _clock.UptimeMilliseconds < record.CreatedUptimeMilliseconds ||
        IsBootEpochDifferent(record);

    private bool IsBootEpochDifferent(SupervisorSessionRecord record) =>
        (_clock.BootEpochUtc - record.BootEpochUtc).Duration() > SupervisorPolicy.BootEpochTolerance;

    private static bool IsTerminal(SupervisedSessionState state) =>
        state is SupervisedSessionState.Stopped or SupervisedSessionState.Expired;

    private static SupervisedSessionState ToState(SupervisorTerminalState target) =>
        target == SupervisorTerminalState.Expired
            ? SupervisedSessionState.Expired
            : SupervisedSessionState.Stopped;

    private static SupervisorIpcResponse Response(
        SupervisorIpcRequest request,
        bool accepted,
        string? errorCode,
        SupervisorSessionRecord record) =>
        new(
            SupervisorPolicy.ProtocolVersion,
            request.RequestId,
            accepted,
            errorCode,
            ToSnapshot(record));

    private static SupervisedSessionSnapshot ToSnapshot(SupervisorSessionRecord record) =>
        new(
            record.SessionId,
            record.State,
            record.Lease?.VmId,
            record.CreatedAtUtc,
            record.ExpiresAtUtc,
            record.LastCommandSequence,
            record.CleanupFailure)
        {
            ReservedChannels = [.. record.ReservedChannels],
        };

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ValidateLease(Guid sessionId, HyperVPreviewVmLease lease)
    {
        if (lease is null || lease.SessionId != sessionId || lease.VmId == Guid.Empty ||
            string.IsNullOrWhiteSpace(lease.ResourceId) || lease.ResourceId.Length > 256 ||
            lease.ResourceId.Any(char.IsControl))
            throw new InvalidOperationException("Hyper-V control returned an invalid session lease.");
    }
}
