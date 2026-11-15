using System.Security.Cryptography;

namespace AIPlatform.Core.Studio;

public enum HyperVPreviewState
{
    Provisioning,
    Provisioned,
    Starting,
    Ready,
    Stopping,
    Stopped,
    Expired,
    Failed,
    CleanupFailed,
}

public enum HyperVPreviewPowerState
{
    Unknown,
    Off,
    Running,
    Paused,
}

public enum HyperVPreviewVmState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed,
    Destroyed,
}

public enum HyperVPreviewStopMode
{
    Graceful,
    Force,
}

public enum HyperVPreviewInputKind
{
    PointerMove,
    PointerButton,
    Key,
    Text,
}

public enum HyperVPreviewChannelRole
{
    Control,
    Frames,
}

public enum HyperVPreviewLogCode
{
    GuestStarted,
    BrowserStarted,
    Ready,
    BrowserExited,
    GuestStopped,
    ProtocolRejected,
}

public static class HyperVPreviewPolicy
{
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan SupervisorStopDelay = TimeSpan.FromSeconds(170);
    public static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ForcedStopTimeout = TimeSpan.FromSeconds(3);
    public const int HeaderBytes = 48;
    public const int MaximumMessageBytes = 4 * 1024 * 1024;
    public const int MaximumControlPayloadBytes = 4096;
    public static readonly TimeSpan ControlHeaderTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ControlPayloadTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ControlWriteTimeout = TimeSpan.FromSeconds(2);
    public const int MaximumMessagesPerSecond = 200;
    public const int MessageBurst = 256;
    public const int MaximumFrameRate = 10;
    public const int MaximumPendingFrames = 1;
    public const int FrameBufferCount = 2;
    public const int MaximumFrameBytesPerSecond = 40 * 1024 * 1024;
    public const int FrameWidth = 960;
    public const int FrameHeight = 540;
    public const int FrameStride = FrameWidth * 4;
    public const int FrameBytes = FrameStride * FrameHeight;
    public const int MaximumInputBytes = 512;
    public const int MaximumTextBytes = 256;
    public const int MaximumInputEventsPerSecond = 120;
    public const int MaximumPointerMovesPerSecond = 30;
    public const int MaximumLogEventBytes = 1024;
    public const int MaximumLogEventsPerSecond = 10;
    public const int LogRingBytes = 64 * 1024;
    public const int MaximumLogEvents = 64;
}

public sealed record HyperVPreviewVmLease(Guid SessionId, Guid VmId, string ResourceId);

public sealed record HyperVPreviewConnectionChallenges(byte[] Control, byte[] Frames)
{
    public static HyperVPreviewConnectionChallenges Create()
    {
        var control = RandomNumberGenerator.GetBytes(32);
        var frames = RandomNumberGenerator.GetBytes(32);
        while (control.AsSpan().SequenceEqual(frames))
            frames = RandomNumberGenerator.GetBytes(32);
        return new HyperVPreviewConnectionChallenges(control, frames);
    }

    public void Validate()
    {
        if (Control is null || Frames is null ||
            Control.Length != 32 || Frames.Length != 32 ||
            Control.AsSpan().SequenceEqual(Frames))
            throw new ArgumentException("Control and frame channels require distinct 32-byte challenges.");
    }
}

public sealed record HyperVPreviewReadiness(bool IsReady, string? Detail = null);

public sealed record HyperVPreviewFrame(long Sequence, byte[] BgraPixels)
{
    public void Validate()
    {
        if (Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(Sequence));
        if (BgraPixels is null || BgraPixels.Length != HyperVPreviewPolicy.FrameBytes)
            throw new ArgumentException("Preview frames must be fixed-size BGRA8.", nameof(BgraPixels));
    }
}

public sealed record HyperVPreviewInput(
    HyperVPreviewInputKind Kind,
    int X = 0,
    int Y = 0,
    int Code = 0,
    byte[]? TextUtf8 = null)
{
    public void Validate()
    {
        if (X is < 0 or >= HyperVPreviewPolicy.FrameWidth ||
            Y is < 0 or >= HyperVPreviewPolicy.FrameHeight ||
            Code is < 0 or > ushort.MaxValue ||
            (TextUtf8?.Length ?? 0) > HyperVPreviewPolicy.MaximumInputBytes)
            throw new ArgumentOutOfRangeException(nameof(HyperVPreviewInput));

        if (Kind == HyperVPreviewInputKind.Text)
        {
            if (TextUtf8 is null || TextUtf8.Length > HyperVPreviewPolicy.MaximumTextBytes)
                throw new ArgumentException("Preview text input exceeds its byte limit.", nameof(TextUtf8));
            _ = new System.Text.UTF8Encoding(false, true).GetString(TextUtf8);
        }
        else if (TextUtf8 is not null)
        {
            throw new ArgumentException("Text bytes are only valid for text input.", nameof(TextUtf8));
        }
    }
}

public sealed record HyperVPreviewLogEvent(DateTimeOffset Timestamp, HyperVPreviewLogCode Code);

public sealed record HyperVPreviewSessionSnapshot(
    Guid SessionId,
    HyperVPreviewState State,
    Guid? VmId,
    DateTimeOffset? StartedAt,
    bool ForcedStop);

public sealed record HyperVPreviewCleanupResult(bool ForcedStop, bool ResourcesReleased);

public interface IHyperVPreviewVmRuntime
{
    // Provisioning must roll back any partial allocation if it cannot return its exact lease.
    Task<HyperVPreviewVmLease> ProvisionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken);
    Task<HyperVPreviewPowerState> GetPowerStateAsync(
        HyperVPreviewVmLease lease,
        CancellationToken cancellationToken);
    Task<HyperVPreviewVmState> GetStateAsync(
        HyperVPreviewVmLease lease,
        CancellationToken cancellationToken);
    Task StopAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewStopMode mode,
        CancellationToken cancellationToken);
    Task RemoveAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken);
}

public interface IHyperVVmControl
{
    // Create must roll back partial allocations on failure and return only an exact lease.
    Task<HyperVPreviewVmLease> CreateAsync(Guid sessionId, CancellationToken cancellationToken);
    Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken);
    Task StopAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewStopMode mode,
        CancellationToken cancellationToken);
    Task DestroyAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken);
    Task<HyperVPreviewVmState> GetStateAsync(
        HyperVPreviewVmLease lease,
        CancellationToken cancellationToken);
}

public interface IHyperVPreviewSupervisor
{
    Task ArmAsync(
        HyperVPreviewVmLease lease,
        TimeSpan maximumLifetime,
        Func<CancellationToken, Task> onExpired,
        CancellationToken cancellationToken);
    Task DisarmAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken);
}

public interface IHyperVPreviewBroker
{
    Task<IPreviewGuestAgent> ConnectAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewConnectionChallenges challenges,
        CancellationToken cancellationToken);
}

public interface IPreviewGuestAgent : IAsyncDisposable
{
    Task<HyperVPreviewReadiness> WaitUntilReadyAsync(CancellationToken cancellationToken);
    Task<HyperVPreviewFrame?> ReadFrameAsync(CancellationToken cancellationToken);
    Task SendInputAsync(HyperVPreviewInput input, CancellationToken cancellationToken);
    Task<IReadOnlyList<HyperVPreviewLogEvent>> ReadLogsAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public static class HyperVPreviewLifecycle
{
    public static bool CanTransition(HyperVPreviewState current, HyperVPreviewState next) =>
        (current, next) switch
        {
            (HyperVPreviewState.Provisioning, HyperVPreviewState.Provisioned or
                HyperVPreviewState.Failed) => true,
            (HyperVPreviewState.Provisioned, HyperVPreviewState.Starting or
                HyperVPreviewState.Stopping or HyperVPreviewState.Failed) => true,
            (HyperVPreviewState.Starting, HyperVPreviewState.Ready or
                HyperVPreviewState.Stopping or HyperVPreviewState.Failed) => true,
            (HyperVPreviewState.Ready, HyperVPreviewState.Stopping) => true,
            (HyperVPreviewState.Stopping, HyperVPreviewState.Stopped or
                HyperVPreviewState.Expired or HyperVPreviewState.Failed or
                HyperVPreviewState.CleanupFailed) => true,
            (HyperVPreviewState.CleanupFailed, HyperVPreviewState.Stopping) => true,
            _ => false,
        };

    public static HyperVPreviewState Transition(
        HyperVPreviewState current,
        HyperVPreviewState next)
    {
        if (!CanTransition(current, next))
            throw new InvalidOperationException($"Invalid Hyper-V Preview transition: {current} -> {next}.");
        return next;
    }
}

public sealed class HyperVPreviewSessionController
{
    private readonly IHyperVPreviewVmRuntime _runtime;
    private readonly IHyperVPreviewBroker _broker;
    private readonly IHyperVPreviewSupervisor _supervisor;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, SessionRecord> _sessions = [];

    public HyperVPreviewSessionController(
        IHyperVPreviewVmRuntime runtime,
        IHyperVPreviewBroker broker,
        IHyperVPreviewSupervisor supervisor,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime;
        _broker = broker;
        _supervisor = supervisor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<HyperVPreviewSessionSnapshot> StartAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A non-empty session id is required.", nameof(sessionId));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.ContainsKey(sessionId) ||
                _sessions.Values.Any(session => session.State is
                    HyperVPreviewState.Provisioning or HyperVPreviewState.Provisioned or
                    HyperVPreviewState.Starting or HyperVPreviewState.Ready or
                    HyperVPreviewState.Stopping or HyperVPreviewState.CleanupFailed))
                throw new InvalidOperationException("Only one Hyper-V Preview session may be active.");

            var record = new SessionRecord(sessionId);
            _sessions.Add(sessionId, record);
            try
            {
                var lease = await _runtime.ProvisionAsync(sessionId, cancellationToken);
                ValidateLease(sessionId, lease);
                record.Lease = lease;
                record.SetState(HyperVPreviewState.Provisioned);
                await _supervisor.ArmAsync(
                    record.Lease,
                    HyperVPreviewPolicy.SupervisorStopDelay,
                    token => ExpireAsync(sessionId, token),
                    cancellationToken);

                record.SetState(HyperVPreviewState.Starting);
                using var lifetime = new CancellationTokenSource(HyperVPreviewPolicy.MaximumLifetime);
                using var readiness = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.Token);
                readiness.CancelAfter(HyperVPreviewPolicy.ReadinessTimeout);
                record.StartedAt = _timeProvider.GetUtcNow();

                await _runtime.StartAsync(record.Lease, readiness.Token);
                var challenges = HyperVPreviewConnectionChallenges.Create();
                challenges.Validate();
                record.Agent = await _broker.ConnectAsync(record.Lease, challenges, readiness.Token);
                var ready = await record.Agent.WaitUntilReadyAsync(readiness.Token);
                if (!ready.IsReady)
                    throw new InvalidOperationException("The guest did not report Preview readiness.");

                record.SetState(HyperVPreviewState.Ready);
                return record.Snapshot();
            }
            catch (Exception startError)
            {
                if (record.Lease is null)
                {
                    record.ResourcesReleased = true;
                    record.SetState(HyperVPreviewState.Failed);
                    throw;
                }

                record.SetState(HyperVPreviewState.Stopping);
                var cleanup = await CleanupAsync(record);
                record.ForcedStop = cleanup.ForcedStop;
                record.ResourcesReleased = cleanup.ResourcesReleased;
                record.SetState(cleanup.ResourcesReleased
                    ? HyperVPreviewState.Failed
                    : HyperVPreviewState.CleanupFailed);
                if (cleanup.Errors.Count > 0)
                    throw new AggregateException("Hyper-V Preview startup failed and cleanup reported errors.",
                        [startError, .. cleanup.Errors]);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HyperVPreviewCleanupResult> StopAsync(
        Guid sessionId,
        bool expired,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A non-empty session id is required.", nameof(sessionId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_sessions.TryGetValue(sessionId, out var record))
                return new HyperVPreviewCleanupResult(false, true);
            if (record.State is HyperVPreviewState.Stopped or HyperVPreviewState.Expired ||
                record.State == HyperVPreviewState.Failed && record.ResourcesReleased)
                return new HyperVPreviewCleanupResult(record.ForcedStop, true);
            if (record.Lease is null)
                throw new InvalidOperationException("The session has no provisioned VM lease.");
            if (record.State is not (HyperVPreviewState.Ready or HyperVPreviewState.CleanupFailed))
                throw new InvalidOperationException($"Cannot stop a session in state {record.State}.");

            record.SetState(HyperVPreviewState.Stopping);
            var cleanup = await CleanupAsync(record);
            record.ForcedStop = cleanup.ForcedStop;
            record.ResourcesReleased = cleanup.ResourcesReleased;
            record.SetState(cleanup.ResourcesReleased
                ? expired ? HyperVPreviewState.Expired : HyperVPreviewState.Stopped
                : HyperVPreviewState.CleanupFailed);
            if (cleanup.Errors.Count > 0)
                throw new AggregateException("Hyper-V Preview cleanup reported errors.", cleanup.Errors);
            return new HyperVPreviewCleanupResult(cleanup.ForcedStop, cleanup.ResourcesReleased);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HyperVPreviewSessionSnapshot?> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _sessions.TryGetValue(sessionId, out var record) ? record.Snapshot() : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CleanupOutcome> CleanupAsync(SessionRecord record)
    {
        var errors = new List<Exception>();
        var agentDisposed = record.Agent is null;
        if (record.Agent is not null)
        {
            try
            {
                await record.Agent.StopAsync(CancellationToken.None);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                await record.Agent.DisposeAsync();
                agentDisposed = true;
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            record.Agent = null;
        }

        var lease = record.Lease!;
        var forcedStop = false;
        var powerState = HyperVPreviewPowerState.Unknown;
        try
        {
            powerState = await _runtime.GetPowerStateAsync(lease, CancellationToken.None);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        if (powerState != HyperVPreviewPowerState.Off)
        {
            using var gracefulTimeout = new CancellationTokenSource(
                HyperVPreviewPolicy.GracefulStopTimeout);
            try
            {
                await _runtime.StopAsync(lease, HyperVPreviewStopMode.Graceful, gracefulTimeout.Token);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                powerState = await _runtime.GetPowerStateAsync(lease, CancellationToken.None);
            }
            catch (Exception error)
            {
                errors.Add(error);
                powerState = HyperVPreviewPowerState.Unknown;
            }
        }

        if (powerState != HyperVPreviewPowerState.Off)
        {
            forcedStop = true;
            using var forcedTimeout = new CancellationTokenSource(HyperVPreviewPolicy.ForcedStopTimeout);
            try
            {
                await _runtime.StopAsync(lease, HyperVPreviewStopMode.Force, forcedTimeout.Token);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                powerState = await _runtime.GetPowerStateAsync(lease, CancellationToken.None);
            }
            catch (Exception error)
            {
                errors.Add(error);
                powerState = HyperVPreviewPowerState.Unknown;
            }
        }

        var removed = false;
        if (powerState == HyperVPreviewPowerState.Off)
        {
            try
            {
                await _runtime.RemoveAsync(lease, CancellationToken.None);
                removed = true;
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        else
        {
            errors.Add(new InvalidOperationException("VM power-off was not confirmed; resources were retained."));
        }

        var supervisorDisarmed = false;
        if (removed)
        {
            try
            {
                await _supervisor.DisarmAsync(lease, CancellationToken.None);
                supervisorDisarmed = true;
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        return new CleanupOutcome(forcedStop, agentDisposed && removed && supervisorDisarmed, errors);
    }

    private async Task ExpireAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await StopAsync(sessionId, expired: true, cancellationToken);
    }

    private static void ValidateLease(Guid sessionId, HyperVPreviewVmLease lease)
    {
        if (lease is null || lease.SessionId != sessionId || lease.VmId == Guid.Empty ||
            string.IsNullOrWhiteSpace(lease.ResourceId) || lease.ResourceId.Length > 256 ||
            lease.ResourceId.Any(char.IsControl))
            throw new InvalidOperationException("The Hyper-V runtime returned an invalid session lease.");
    }

    private sealed class SessionRecord(Guid sessionId)
    {
        public Guid SessionId { get; } = sessionId;
        public HyperVPreviewState State { get; private set; } = HyperVPreviewState.Provisioning;
        public HyperVPreviewVmLease? Lease { get; set; }
        public IPreviewGuestAgent? Agent { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public bool ForcedStop { get; set; }
        public bool ResourcesReleased { get; set; }

        public void SetState(HyperVPreviewState next) =>
            State = HyperVPreviewLifecycle.Transition(State, next);

        public HyperVPreviewSessionSnapshot Snapshot() =>
            new(SessionId, State, Lease?.VmId, StartedAt, ForcedStop);
    }

    private sealed record CleanupOutcome(bool ForcedStop, bool ResourcesReleased, List<Exception> Errors);
}
