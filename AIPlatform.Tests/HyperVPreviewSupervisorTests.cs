using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class HyperVPreviewSupervisorTests
{
    [Fact]
    public async Task ExpiresOnInjectedClockAndRunsCallbackOnlyOnce()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new HyperVPreviewSupervisor(clock);
        var lease = CreateLease();
        var calls = 0;
        await supervisor.ArmAsync(
            lease,
            HyperVPreviewPolicy.MaximumLifetime,
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            },
            default);
        var completion = supervisor.ObserveExpiryAsync(lease.SessionId, default);

        clock.Advance(HyperVPreviewPolicy.MaximumLifetime - TimeSpan.FromTicks(1));
        Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromTicks(1));
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Expired);
        Assert.Null(result.Error);
        Assert.Equal(1, calls);
        Assert.Equal(result, await supervisor.ObserveExpiryAsync(lease.SessionId, default));
    }

    [Fact]
    public async Task DisarmCancelsPendingExpiryAndDoesNotCallTheCallback()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new HyperVPreviewSupervisor(clock);
        var lease = CreateLease();
        var calls = 0;
        await supervisor.ArmAsync(lease, TimeSpan.FromSeconds(10), _ =>
        {
            calls++;
            return Task.CompletedTask;
        }, default);
        var completion = supervisor.ObserveExpiryAsync(lease.SessionId, default);

        await supervisor.DisarmAsync(lease, default);
        clock.Advance(TimeSpan.FromSeconds(10));

        var result = await completion;
        Assert.False(result.Expired);
        Assert.Null(result.Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RejectsExcessiveLifetimeDuplicateArmingAndMismatchedLease()
    {
        var supervisor = new HyperVPreviewSupervisor(new ManualTimeProvider());
        var lease = CreateLease();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => supervisor.ArmAsync(
            lease,
            HyperVPreviewPolicy.MaximumLifetime + TimeSpan.FromTicks(1),
            _ => Task.CompletedTask,
            default));
        await supervisor.ArmAsync(lease, TimeSpan.FromSeconds(5), _ => Task.CompletedTask, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.ArmAsync(
            lease,
            TimeSpan.FromSeconds(5),
            _ => Task.CompletedTask,
            default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            supervisor.DisarmAsync(lease with { VmId = Guid.NewGuid() }, default));
        await supervisor.DisarmAsync(lease, default);
    }

    [Fact]
    public async Task ReturnsCallbackFailureInsteadOfReportingSuccessfulExpiry()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new HyperVPreviewSupervisor(clock);
        var lease = CreateLease();
        await supervisor.ArmAsync(
            lease,
            TimeSpan.FromSeconds(1),
            _ => Task.FromException(new IOException("simulated expiry failure")),
            default);
        var completion = supervisor.ObserveExpiryAsync(lease.SessionId, default);

        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Expired);
        Assert.IsType<IOException>(result.Error);
    }

    [Fact]
    public async Task DisposingSupervisorCancelsOutstandingTimersAndRejectsFutureArming()
    {
        var clock = new ManualTimeProvider();
        var supervisor = new HyperVPreviewSupervisor(clock);
        var lease = CreateLease();
        var calls = 0;
        await supervisor.ArmAsync(lease, TimeSpan.FromSeconds(10), _ =>
        {
            calls++;
            return Task.CompletedTask;
        }, default);
        var completion = supervisor.ObserveExpiryAsync(lease.SessionId, default);

        await supervisor.DisposeAsync();
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.False((await completion).Expired);
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => supervisor.ArmAsync(
            CreateLease(),
            TimeSpan.FromSeconds(10),
            _ => Task.CompletedTask,
            default));
    }

    [Fact]
    public async Task ExpiryCallbackStopsAndDestroysOnlyItsSessionLease()
    {
        var clock = new ManualTimeProvider();
        var control = new FakeVmControl();
        var runtime = new HyperVPreviewVmRuntime(control);
        var supervisor = new HyperVPreviewSupervisor(clock);
        var controller = new HyperVPreviewSessionController(runtime, new ReadyBroker(), supervisor, clock);
        var started = await controller.StartAsync(Guid.NewGuid(), default);
        var completion = supervisor.ObserveExpiryAsync(started.SessionId, default);

        clock.Advance(HyperVPreviewPolicy.SupervisorStopDelay - TimeSpan.FromTicks(1));
        Assert.Equal(HyperVPreviewState.Ready,
            (await controller.GetAsync(started.SessionId, default))!.State);
        clock.Advance(TimeSpan.FromTicks(1));
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Expired);
        Assert.Null(result.Error);
        Assert.Equal(HyperVPreviewVmState.Destroyed,
            await runtime.GetStateAsync(control.Lease!, default));
        Assert.Equal(HyperVPreviewState.Expired,
            (await controller.GetAsync(started.SessionId, default))!.State);
        Assert.Equal(1, control.DestroyCalls);
    }

    [Fact]
    public async Task CanceledStartRollsBackItsExactCreatedLease()
    {
        var clock = new ManualTimeProvider();
        var control = new FakeVmControl { CancelStart = true };
        var runtime = new HyperVPreviewVmRuntime(control);
        var supervisor = new HyperVPreviewSupervisor(clock);
        var controller = new HyperVPreviewSessionController(runtime, new ReadyBroker(), supervisor, clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.StartAsync(Guid.NewGuid(), default));

        Assert.Equal(1, control.DestroyCalls);
        Assert.Equal(HyperVPreviewVmState.Destroyed,
            await runtime.GetStateAsync(control.Lease!, default));
    }

    [Fact]
    public async Task CrashedStartCleansUpAndPreservesTheOriginalFailure()
    {
        var clock = new ManualTimeProvider();
        var control = new FakeVmControl { StartFailure = new IOException("simulated adapter crash") };
        var runtime = new HyperVPreviewVmRuntime(control);
        var supervisor = new HyperVPreviewSupervisor(clock);
        var controller = new HyperVPreviewSessionController(runtime, new ReadyBroker(), supervisor, clock);

        await Assert.ThrowsAsync<IOException>(() => controller.StartAsync(Guid.NewGuid(), default));

        Assert.Equal(1, control.DestroyCalls);
        Assert.Equal(HyperVPreviewVmState.Destroyed,
            await runtime.GetStateAsync(control.Lease!, default));
    }

    private static HyperVPreviewVmLease CreateLease() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "test-resource");

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private readonly DateTimeOffset _origin = DateTimeOffset.UnixEpoch;
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => _origin + TimeSpan.FromTicks(_ticks);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            _ticks += amount.Ticks;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue(_ticks);
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private long? _dueAt;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                    return false;
                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner.GetTimestamp() + dueTime.Ticks;
                return true;
            }

            public void Dispose()
            {
                _disposed = true;
                _dueAt = null;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue(long now)
            {
                if (_disposed || _dueAt is not { } dueAt || dueAt > now)
                    return;
                _dueAt = _period == Timeout.InfiniteTimeSpan
                    ? null
                    : dueAt + _period.Ticks;
                callback(state);
            }
        }
    }

    private sealed class FakeVmControl : IHyperVVmControl
    {
        private readonly Dictionary<Guid, HyperVPreviewVmState> _states = [];
        public HyperVPreviewVmLease? Lease { get; private set; }
        public int DestroyCalls { get; private set; }
        public bool CancelStart { get; init; }
        public IOException? StartFailure { get; init; }

        public Task<HyperVPreviewVmLease> CreateAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            Lease = new HyperVPreviewVmLease(sessionId, Guid.NewGuid(), $"vm-{sessionId:N}");
            _states.Add(Lease.VmId, HyperVPreviewVmState.Created);
            return Task.FromResult(Lease);
        }

        public Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
        {
            if (StartFailure is not null)
                throw StartFailure;
            if (CancelStart)
                throw new OperationCanceledException(cancellationToken);
            _states[lease.VmId] = HyperVPreviewVmState.Running;
            return Task.CompletedTask;
        }

        public Task StopAsync(
            HyperVPreviewVmLease lease,
            HyperVPreviewStopMode mode,
            CancellationToken cancellationToken)
        {
            _states[lease.VmId] = HyperVPreviewVmState.Stopped;
            return Task.CompletedTask;
        }

        public Task DestroyAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
        {
            DestroyCalls++;
            _states[lease.VmId] = HyperVPreviewVmState.Destroyed;
            return Task.CompletedTask;
        }

        public Task<HyperVPreviewVmState> GetStateAsync(
            HyperVPreviewVmLease lease,
            CancellationToken cancellationToken) => Task.FromResult(_states[lease.VmId]);
    }

    private sealed class ReadyBroker : IHyperVPreviewBroker
    {
        public Task<IPreviewGuestAgent> ConnectAsync(
            HyperVPreviewVmLease lease,
            HyperVPreviewConnectionChallenges challenges,
            CancellationToken cancellationToken)
        {
            challenges.Validate();
            return Task.FromResult<IPreviewGuestAgent>(new ReadyGuestAgent());
        }
    }

    private sealed class ReadyGuestAgent : IPreviewGuestAgent
    {
        public Task<HyperVPreviewReadiness> WaitUntilReadyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HyperVPreviewReadiness(true));

        public Task<HyperVPreviewFrame?> ReadFrameAsync(CancellationToken cancellationToken) =>
            Task.FromResult<HyperVPreviewFrame?>(null);

        public Task SendInputAsync(HyperVPreviewInput input, CancellationToken cancellationToken)
        {
            input.Validate();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HyperVPreviewLogEvent>> ReadLogsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HyperVPreviewLogEvent>>([]);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
