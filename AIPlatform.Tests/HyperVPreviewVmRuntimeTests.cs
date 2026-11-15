using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class HyperVPreviewVmRuntimeTests
{
    [Fact]
    public async Task ConfirmsLifecycleTransitionsAndMakesStopAndDestroyIdempotent()
    {
        var control = new FakeVmControl();
        var runtime = new HyperVPreviewVmRuntime(control);
        var lease = await runtime.ProvisionAsync(Guid.NewGuid(), default);

        Assert.Equal(HyperVPreviewVmState.Created, await runtime.GetStateAsync(lease, default));
        await runtime.StartAsync(lease, default);
        await runtime.StartAsync(lease, default);
        Assert.Equal(1, control.StartCalls);
        Assert.Equal(HyperVPreviewVmState.Running, await runtime.GetStateAsync(lease, default));

        await runtime.StopAsync(lease, HyperVPreviewStopMode.Graceful, default);
        await runtime.StopAsync(lease, HyperVPreviewStopMode.Graceful, default);
        Assert.Equal(1, control.StopCalls);
        Assert.Equal(HyperVPreviewVmState.Stopped, await runtime.GetStateAsync(lease, default));

        await runtime.RemoveAsync(lease, default);
        await runtime.RemoveAsync(lease, default);
        Assert.Equal(1, control.DestroyCalls);
        Assert.Equal(HyperVPreviewVmState.Destroyed, await runtime.GetStateAsync(lease, default));
        Assert.Equal(HyperVPreviewPowerState.Off, await runtime.GetPowerStateAsync(lease, default));
    }

    [Fact]
    public async Task RejectsInvalidTransitionsAndNeverReportsUnconfirmedDestruction()
    {
        var control = new FakeVmControl { DestroyConfirms = false };
        var runtime = new HyperVPreviewVmRuntime(control);
        var lease = await runtime.ProvisionAsync(Guid.NewGuid(), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.RemoveAsync(lease, default));
        Assert.Equal(HyperVPreviewVmState.Created, await runtime.GetStateAsync(lease, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.StartAsync(new HyperVPreviewVmLease(Guid.NewGuid(), Guid.NewGuid(), "foreign"), default));

        await runtime.StartAsync(lease, default);
        await runtime.StopAsync(lease, HyperVPreviewStopMode.Graceful, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.RemoveAsync(lease, default));
        Assert.Equal(HyperVPreviewVmState.Stopped, await runtime.GetStateAsync(lease, default));
    }

    [Fact]
    public async Task FailedGracefulStopCanBeEscalatedAndReportedStateIsConfirmed()
    {
        var control = new FakeVmControl { GracefulStopLeavesRunning = true };
        var runtime = new HyperVPreviewVmRuntime(control);
        var lease = await runtime.ProvisionAsync(Guid.NewGuid(), default);
        await runtime.StartAsync(lease, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.StopAsync(lease, HyperVPreviewStopMode.Graceful, default));
        Assert.Equal(HyperVPreviewVmState.Failed, await runtime.GetStateAsync(lease, default));
        Assert.Equal(HyperVPreviewPowerState.Running, await runtime.GetPowerStateAsync(lease, default));

        await runtime.StopAsync(lease, HyperVPreviewStopMode.Force, default);
        Assert.Equal(HyperVPreviewVmState.Stopped, await runtime.GetStateAsync(lease, default));
        Assert.Equal(new[] { HyperVPreviewStopMode.Graceful, HyperVPreviewStopMode.Force }, control.StopModes);
    }

    [Fact]
    public async Task ConcurrentStartAndStopCallsAreSerializedAndDoNotDuplicateAdapterCommands()
    {
        var control = new FakeVmControl { HoldStart = true, HoldStop = true };
        var runtime = new HyperVPreviewVmRuntime(control);
        var lease = await runtime.ProvisionAsync(Guid.NewGuid(), default);

        var firstStart = runtime.StartAsync(lease, default);
        await control.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondStart = runtime.StartAsync(lease, default);
        control.ReleaseStart.TrySetResult();
        await Task.WhenAll(firstStart, secondStart);
        Assert.Equal(1, control.StartCalls);

        var firstStop = runtime.StopAsync(lease, HyperVPreviewStopMode.Graceful, default);
        await control.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondStop = runtime.StopAsync(lease, HyperVPreviewStopMode.Graceful, default);
        control.ReleaseStop.TrySetResult();
        await Task.WhenAll(firstStop, secondStop);
        Assert.Equal(1, control.StopCalls);
        Assert.Equal(HyperVPreviewVmState.Stopped, await runtime.GetStateAsync(lease, default));
    }

    [Fact]
    public async Task PropagatesCreateFailureAfterAdapterRollsBackItsPartialAllocation()
    {
        var control = new FakeVmControl { FailCreateAfterPartialAllocation = true };
        var runtime = new HyperVPreviewVmRuntime(control);

        await Assert.ThrowsAsync<IOException>(() => runtime.ProvisionAsync(Guid.NewGuid(), default));

        Assert.Equal(1, control.PartialAllocations);
        Assert.Equal(1, control.PartialAllocationRollbacks);
    }

    [Fact]
    public async Task RejectsDuplicateVmIdentityWithoutDestroyingTheExistingVm()
    {
        var control = new FakeVmControl();
        var runtime = new HyperVPreviewVmRuntime(control);
        var firstLease = await runtime.ProvisionAsync(Guid.NewGuid(), default);
        control.NextVmId = firstLease.VmId;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.ProvisionAsync(Guid.NewGuid(), default));

        Assert.Equal(0, control.DestroyCalls);
        Assert.Equal(HyperVPreviewVmState.Created, await runtime.GetStateAsync(firstLease, default));
    }

    [Fact]
    public void VmLifecycleRejectsIllegalTransitions()
    {
        Assert.True(HyperVPreviewVmLifecycle.CanTransition(
            HyperVPreviewVmState.Created, HyperVPreviewVmState.Starting));
        Assert.True(HyperVPreviewVmLifecycle.CanTransition(
            HyperVPreviewVmState.Failed, HyperVPreviewVmState.Stopping));
        Assert.False(HyperVPreviewVmLifecycle.CanTransition(
            HyperVPreviewVmState.Destroyed, HyperVPreviewVmState.Running));
        Assert.Throws<InvalidOperationException>(() =>
            HyperVPreviewVmLifecycle.Transition(
                HyperVPreviewVmState.Destroyed, HyperVPreviewVmState.Running));
    }

    private sealed class FakeVmControl : IHyperVVmControl
    {
        private readonly Dictionary<Guid, HyperVPreviewVmState> _states = [];
        private readonly TaskCompletionSource _releaseStart =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStop =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DestroyConfirms { get; init; } = true;
        public bool GracefulStopLeavesRunning { get; init; }
        public bool FailCreateAfterPartialAllocation { get; init; }
        public bool HoldStart { get; init; }
        public bool HoldStop { get; init; }
        public Guid? NextVmId { get; set; }
        public int PartialAllocations { get; private set; }
        public int PartialAllocationRollbacks { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int DestroyCalls { get; private set; }
        public TaskCompletionSource StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStart => _releaseStart;
        public TaskCompletionSource ReleaseStop => _releaseStop;
        public List<HyperVPreviewStopMode> StopModes { get; } = [];

        public Task<HyperVPreviewVmLease> CreateAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            if (FailCreateAfterPartialAllocation)
            {
                PartialAllocations++;
                PartialAllocationRollbacks++;
                throw new IOException("test partial allocation was rolled back");
            }

            var vmId = NextVmId ?? Guid.NewGuid();
            var lease = new HyperVPreviewVmLease(sessionId, vmId, $"vm-{sessionId:N}");
            if (!_states.ContainsKey(vmId))
                _states.Add(lease.VmId, HyperVPreviewVmState.Created);
            return Task.FromResult(lease);
        }

        public async Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
        {
            StartCalls++;
            StartEntered.TrySetResult();
            if (HoldStart)
                await _releaseStart.Task.WaitAsync(cancellationToken);
            _states[lease.VmId] = HyperVPreviewVmState.Running;
        }

        public async Task StopAsync(
            HyperVPreviewVmLease lease,
            HyperVPreviewStopMode mode,
            CancellationToken cancellationToken)
        {
            StopCalls++;
            StopModes.Add(mode);
            StopEntered.TrySetResult();
            if (HoldStop)
                await _releaseStop.Task.WaitAsync(cancellationToken);
            if (mode == HyperVPreviewStopMode.Force || !GracefulStopLeavesRunning)
                _states[lease.VmId] = HyperVPreviewVmState.Stopped;
        }

        public Task DestroyAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
        {
            DestroyCalls++;
            if (DestroyConfirms)
                _states[lease.VmId] = HyperVPreviewVmState.Destroyed;
            return Task.CompletedTask;
        }

        public Task<HyperVPreviewVmState> GetStateAsync(
            HyperVPreviewVmLease lease,
            CancellationToken cancellationToken) =>
            Task.FromResult(_states[lease.VmId]);
    }
}
