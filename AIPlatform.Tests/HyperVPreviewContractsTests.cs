using AIPlatform.Core.Studio;

namespace AIPlatform.Tests;

public sealed class HyperVPreviewContractsTests
{
    [Fact]
    public async Task StartsOnlyAfterProvisioningAndSupervisorArmingThenStopsAndReleasesResources()
    {
        var runtime = new FakeVmRuntime();
        var broker = new FakeBroker();
        var supervisor = new FakeSupervisor();
        runtime.OnStart = supervisor.ObserveStart;
        var controller = new HyperVPreviewSessionController(runtime, broker, supervisor);
        var sessionId = Guid.NewGuid();

        var started = await controller.StartAsync(sessionId, default);

        Assert.Equal(HyperVPreviewState.Ready, started.State);
        Assert.Equal(runtime.Lease!.VmId, started.VmId);
        Assert.True(supervisor.ArmedBeforeStart);
        Assert.Equal(HyperVPreviewPolicy.SupervisorStopDelay, supervisor.ArmedLifetime);
        Assert.True(broker.Agent.ReadinessTokenIsCancellable);
        Assert.Equal(32, broker.Challenges!.Control.Length);
        Assert.Equal(32, broker.Challenges.Frames.Length);
        Assert.False(broker.Challenges.Control.SequenceEqual(broker.Challenges.Frames));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(Guid.NewGuid(), default));
        Assert.Equal(1, runtime.ProvisionCount);

        var stopped = await controller.StopAsync(sessionId, expired: false, default);

        Assert.False(stopped.ForcedStop);
        Assert.True(stopped.ResourcesReleased);
        Assert.Equal(HyperVPreviewState.Stopped, (await controller.GetAsync(sessionId, default))!.State);
        Assert.Equal(new[] { HyperVPreviewStopMode.Graceful }, runtime.StopModes);
        Assert.Equal(1, runtime.RemoveCount);
        Assert.Equal(1, supervisor.DisarmCount);
    }

    [Fact]
    public async Task FailedReadinessCleansUpAndNeverReturnsReady()
    {
        var runtime = new FakeVmRuntime();
        var broker = new FakeBroker { IsReady = false };
        var supervisor = new FakeSupervisor();
        var controller = new HyperVPreviewSessionController(runtime, broker, supervisor);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.StartAsync(Guid.NewGuid(), default));

        var session = Assert.IsType<HyperVPreviewSessionSnapshot>(
            await controller.GetAsync(runtime.Lease!.SessionId, default));
        Assert.Equal(HyperVPreviewState.Failed, session.State);
        Assert.Equal(1, runtime.RemoveCount);
        Assert.Equal(1, supervisor.DisarmCount);
        Assert.True(broker.Agent.Disposed);
    }

    [Fact]
    public async Task ForcesShutdownWhenGracefulStopDoesNotPowerOffAndMarksExpiration()
    {
        var runtime = new FakeVmRuntime { GracefulStopKeepsRunning = true };
        var controller = new HyperVPreviewSessionController(runtime, new FakeBroker(), new FakeSupervisor());
        var started = await controller.StartAsync(Guid.NewGuid(), default);

        var result = await controller.StopAsync(started.SessionId, expired: true, default);

        Assert.True(result.ForcedStop);
        Assert.True(result.ResourcesReleased);
        Assert.Equal(new[] { HyperVPreviewStopMode.Graceful, HyperVPreviewStopMode.Force }, runtime.StopModes);
        Assert.Equal(HyperVPreviewState.Expired,
            (await controller.GetAsync(started.SessionId, default))!.State);
    }

    [Fact]
    public async Task UnconfirmedForcedStopRetainsResourcesAndBlocksNewSessions()
    {
        var runtime = new FakeVmRuntime
        {
            GracefulStopKeepsRunning = true,
            ForceStopFails = true,
        };
        var controller = new HyperVPreviewSessionController(runtime, new FakeBroker(), new FakeSupervisor());
        var started = await controller.StartAsync(Guid.NewGuid(), default);

        await Assert.ThrowsAsync<AggregateException>(() =>
            controller.StopAsync(started.SessionId, expired: false, default));

        Assert.Equal(HyperVPreviewState.CleanupFailed,
            (await controller.GetAsync(started.SessionId, default))!.State);
        Assert.Equal(0, runtime.RemoveCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.StartAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public void LifecycleAndPayloadContractsRejectInvalidTransitionsAndOutOfBoundsData()
    {
        Assert.True(HyperVPreviewLifecycle.CanTransition(
            HyperVPreviewState.Provisioning, HyperVPreviewState.Provisioned));
        Assert.False(HyperVPreviewLifecycle.CanTransition(
            HyperVPreviewState.Ready, HyperVPreviewState.Provisioned));
        Assert.Throws<InvalidOperationException>(() =>
            HyperVPreviewLifecycle.Transition(HyperVPreviewState.Ready, HyperVPreviewState.Provisioned));

        new HyperVPreviewInput(HyperVPreviewInputKind.PointerMove, 959, 539).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HyperVPreviewInput(HyperVPreviewInputKind.PointerMove, 960, 539).Validate());
        new HyperVPreviewInput(HyperVPreviewInputKind.Text, TextUtf8: [0xC3, 0xA9]).Validate();
        Assert.Throws<System.Text.DecoderFallbackException>(() =>
            new HyperVPreviewInput(HyperVPreviewInputKind.Text, TextUtf8: [0xFF]).Validate());
        Assert.Throws<ArgumentException>(() =>
            new HyperVPreviewInput(HyperVPreviewInputKind.Text, TextUtf8: new byte[257]).Validate());
        new HyperVPreviewFrame(1, new byte[HyperVPreviewPolicy.FrameBytes]).Validate();
        Assert.Throws<ArgumentException>(() => new HyperVPreviewFrame(1, []).Validate());
        var challenges = HyperVPreviewConnectionChallenges.Create();
        challenges.Validate();
        Assert.False(challenges.Control.SequenceEqual(challenges.Frames));
        Assert.Throws<ArgumentException>(() =>
            new HyperVPreviewConnectionChallenges(new byte[31], new byte[32]).Validate());
    }

    private sealed class FakeVmRuntime : IHyperVPreviewVmRuntime
    {
        private HyperVPreviewVmState _state = HyperVPreviewVmState.Created;

        public HyperVPreviewVmLease? Lease { get; private set; }
        public int ProvisionCount { get; private set; }
        public int RemoveCount { get; private set; }
        public bool GracefulStopKeepsRunning { get; init; }
        public bool ForceStopFails { get; init; }
        public Action? OnStart { get; set; }
        public List<HyperVPreviewStopMode> StopModes { get; } = [];

        public Task<HyperVPreviewVmLease> ProvisionAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            ProvisionCount++;
            Lease = new HyperVPreviewVmLease(sessionId, Guid.NewGuid(), $"resource-{sessionId:N}");
            return Task.FromResult(Lease);
        }

        public Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
        {
            Assert.True(cancellationToken.CanBeCanceled);
            OnStart?.Invoke();
            _state = HyperVPreviewVmState.Running;
            return Task.CompletedTask;
        }

        public Task<HyperVPreviewPowerState> GetPowerStateAsync(
            HyperVPreviewVmLease lease,
            CancellationToken cancellationToken) => Task.FromResult(_state switch
            {
                HyperVPreviewVmState.Created or HyperVPreviewVmState.Stopped or
                    HyperVPreviewVmState.Destroyed => HyperVPreviewPowerState.Off,
                HyperVPreviewVmState.Running => HyperVPreviewPowerState.Running,
                _ => HyperVPreviewPowerState.Unknown,
            });

        public Task<HyperVPreviewVmState> GetStateAsync(
            HyperVPreviewVmLease lease,
            CancellationToken cancellationToken) => Task.FromResult(_state);

        public Task StopAsync(
            HyperVPreviewVmLease lease,
            HyperVPreviewStopMode mode,
            CancellationToken cancellationToken)
        {
            StopModes.Add(mode);
            if (mode == HyperVPreviewStopMode.Force && ForceStopFails)
                throw new IOException("test forced stop failure");
            if (mode == HyperVPreviewStopMode.Force || !GracefulStopKeepsRunning)
                _state = HyperVPreviewVmState.Stopped;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
        {
            Assert.Equal(HyperVPreviewVmState.Stopped, _state);
            RemoveCount++;
            _state = HyperVPreviewVmState.Destroyed;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSupervisor : IHyperVPreviewSupervisor
    {
        private bool _armed;

        public bool ArmedBeforeStart { get; private set; }
        public TimeSpan ArmedLifetime { get; private set; }
        public int DisarmCount { get; private set; }
        public Func<CancellationToken, Task>? OnExpired { get; private set; }

        public Task ArmAsync(
            HyperVPreviewVmLease lease,
            TimeSpan maximumLifetime,
            Func<CancellationToken, Task> onExpired,
            CancellationToken cancellationToken)
        {
            _armed = true;
            ArmedLifetime = maximumLifetime;
            OnExpired = onExpired;
            return Task.CompletedTask;
        }

        public Task DisarmAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
        {
            _armed = false;
            DisarmCount++;
            return Task.CompletedTask;
        }

        public void ObserveStart() => ArmedBeforeStart = _armed;
    }

    private sealed class FakeBroker : IHyperVPreviewBroker
    {
        public bool IsReady { get; init; } = true;
        public FakeGuestAgent Agent { get; } = new();
        public HyperVPreviewConnectionChallenges? Challenges { get; private set; }

        public Task<IPreviewGuestAgent> ConnectAsync(
            HyperVPreviewVmLease lease,
            HyperVPreviewConnectionChallenges challenges,
            CancellationToken cancellationToken)
        {
            challenges.Validate();
            Challenges = challenges;
            Agent.IsReady = IsReady;
            return Task.FromResult<IPreviewGuestAgent>(Agent);
        }
    }

    private sealed class FakeGuestAgent : IPreviewGuestAgent
    {
        public bool IsReady { get; set; } = true;
        public bool Disposed { get; private set; }
        public bool ReadinessTokenIsCancellable { get; private set; }

        public Task<HyperVPreviewReadiness> WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            ReadinessTokenIsCancellable = cancellationToken.CanBeCanceled;
            return Task.FromResult(new HyperVPreviewReadiness(IsReady));
        }

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

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
