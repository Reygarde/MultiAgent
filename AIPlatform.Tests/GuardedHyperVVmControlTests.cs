using AIPlatform.Core.Studio;
using AIPlatform.PreviewSupervisor;

namespace AIPlatform.Tests;

public sealed class GuardedHyperVVmControlTests
{
    [Fact]
    public async Task HostInspectorMapsStructuredAccessDeniedResponse()
    {
        var inspector = new WindowsHyperVHostInspector(new FakeQueryRunner(
            false,
            13,
            """{"schemaVersion":1,"success":false,"errorKind":"accessDenied","logicalProcessorCount":null,"freePhysicalMemoryKiB":null,"freeSystemDriveBytes":null,"virtualMachines":null}"""));

        var inspection = await inspector.InspectAsync(default);

        Assert.Equal(HyperVHostInspectionStatus.AccessDenied, inspection.Status);
        Assert.Null(inspection.Snapshot);
    }

    [Fact]
    public async Task HostInspectorRejectsDuplicateJsonProperties()
    {
        var inspector = new WindowsHyperVHostInspector(new FakeQueryRunner(
            false,
            0,
            """{"schemaVersion":1,"schemaVersion":1,"success":true,"errorKind":null,"logicalProcessorCount":8,"freePhysicalMemoryKiB":8000000,"freeSystemDriveBytes":50000000000,"virtualMachines":[]}"""));

        var inspection = await inspector.InspectAsync(default);

        Assert.Equal(HyperVHostInspectionStatus.QueryFailed, inspection.Status);
        Assert.Equal(nameof(InvalidDataException), inspection.ErrorCode);
    }

    [Fact]
    public async Task HostInspectorParsesOnlyValidImmutableVmIdentities()
    {
        var vmId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var inspector = new WindowsHyperVHostInspector(new FakeQueryRunner(
            false,
            0,
            $$"""{"schemaVersion":1,"success":true,"errorKind":null,"logicalProcessorCount":8,"freePhysicalMemoryKiB":8000000,"freeSystemDriveBytes":50000000000,"virtualMachines":[{"vmId":"{{vmId:D}}","state":"Running","ownerSessionId":"{{sessionId:D}}"}]}"""));

        var inspection = await inspector.InspectAsync(default);

        Assert.Equal(HyperVHostInspectionStatus.Available, inspection.Status);
        var vm = Assert.Single(inspection.Snapshot!.VirtualMachines);
        Assert.Equal(vmId, vm.VmId);
        Assert.Equal(sessionId, vm.OwnerSessionId);
        Assert.Equal(HyperVObservedVmState.Running, vm.State);
    }

    [Fact]
    public async Task HostInspectionAccessDeniedBlocksMutation()
    {
        using var journal = new TestJournal();
        _ = AddSession(journal.Value, journal.SessionId);
        var host = new FakeHostInspector
        {
            Inspection = new HyperVHostInspection(
                HyperVHostInspectionStatus.AccessDenied,
                ErrorCode: "AccessDenied"),
        };
        var mutations = new FakeMutationBackend(host);
        var control = new GuardedHyperVVmControl(journal.Value, host, mutations: mutations);

        var error = await Assert.ThrowsAsync<HyperVHostUnavailableException>(
            () => control.CreateAsync(journal.SessionId, default));

        Assert.Equal(HyperVHostInspectionStatus.AccessDenied, error.Status);
        Assert.Equal(0, mutations.CreateCalls);
    }

    [Fact]
    public async Task UnknownSessionAndUnmanagedUserVmAreRejected()
    {
        using var journal = new TestJournal();
        var lease = CreateLease(journal.SessionId);
        var record = AddSession(journal.Value, journal.SessionId, lease, SupervisedSessionState.Provisioned);
        var host = CreateHost(new HyperVObservedVm(lease.VmId, HyperVObservedVmState.Off, null));
        var mutations = new FakeMutationBackend(host);
        var authorization = new OneTimeAuthorization();
        authorization.Grant(HyperVPreviewMutation.Start, lease.SessionId, lease.VmId);
        var control = new GuardedHyperVVmControl(
            journal.Value, host, authorization, mutations);

        await Assert.ThrowsAsync<HyperVSessionNotManagedException>(
            () => control.FindBySessionIdAsync(Guid.NewGuid(), default));
        var error = await Assert.ThrowsAsync<HyperVResourceReconciliationRequiredException>(
            () => control.StartAsync(record.Lease!, default));

        Assert.Equal(HyperVResourceReconciliationDisposition.IdentityMismatch,
            error.Reconciliation.Disposition);
        Assert.Equal(0, mutations.StartCalls);
    }

    [Fact]
    public async Task ForgedLeaseCannotAuthorizeOperationOnManagedVm()
    {
        using var journal = new TestJournal();
        var lease = CreateLease(journal.SessionId);
        _ = AddSession(journal.Value, journal.SessionId, lease, SupervisedSessionState.Provisioned);
        var host = CreateHost(Observed(lease, HyperVObservedVmState.Off));
        var mutations = new FakeMutationBackend(host);
        var authorization = new OneTimeAuthorization();
        authorization.Grant(HyperVPreviewMutation.Start, lease.SessionId, lease.VmId);
        var control = new GuardedHyperVVmControl(
            journal.Value, host, authorization, mutations);

        var error = await Assert.ThrowsAsync<HyperVResourceReconciliationRequiredException>(
            () => control.StartAsync(lease with { ResourceId = "forged" }, default));

        Assert.Equal(HyperVResourceReconciliationDisposition.IdentityMismatch,
            error.Reconciliation.Disposition);
        Assert.Equal(0, mutations.StartCalls);
    }

    [Fact]
    public async Task MissingOneTimeAuthorizationRejectsCreate()
    {
        using var journal = new TestJournal();
        _ = AddSession(journal.Value, journal.SessionId);
        var host = CreateHost();
        var mutations = new FakeMutationBackend(host);
        var control = new GuardedHyperVVmControl(journal.Value, host, mutations: mutations);

        await Assert.ThrowsAsync<HyperVMutationNotAuthorizedException>(
            () => control.CreateAsync(journal.SessionId, default));

        Assert.Equal(0, mutations.CreateCalls);
    }

    [Fact]
    public async Task LowMemoryBlocksCreateBeforeAuthorizationOrMutation()
    {
        using var journal = new TestJournal();
        _ = AddSession(journal.Value, journal.SessionId);
        var host = CreateHost();
        host.Inspection = host.Inspection with
        {
            Snapshot = host.Inspection.Snapshot! with
            {
                FreePhysicalMemoryKiB = GuardedHyperVVmControl.MinimumFreeMemoryKiB - 1,
            },
        };
        var mutations = new FakeMutationBackend(host);
        var authorization = new OneTimeAuthorization();
        authorization.Grant(HyperVPreviewMutation.Create, journal.SessionId, null);
        var control = new GuardedHyperVVmControl(
            journal.Value, host, authorization, mutations);

        await Assert.ThrowsAsync<HyperVHostResourceGateException>(
            () => control.CreateAsync(journal.SessionId, default));

        Assert.Equal(0, mutations.CreateCalls);
        Assert.True(authorization.HasGrant(HyperVPreviewMutation.Create, journal.SessionId, null));
    }

    [Fact]
    public async Task PartialCreateFailureIsReportedAsAnUnjournaledOrphan()
    {
        using var journal = new TestJournal();
        _ = AddSession(journal.Value, journal.SessionId);
        var host = CreateHost();
        var mutations = new FakeMutationBackend(host) { FailCreateAfterAllocation = true };
        var authorization = new OneTimeAuthorization();
        authorization.Grant(HyperVPreviewMutation.Create, journal.SessionId, null);
        var control = new GuardedHyperVVmControl(
            journal.Value, host, authorization, mutations);

        await Assert.ThrowsAsync<IOException>(() => control.CreateAsync(journal.SessionId, default));
        var error = await Assert.ThrowsAsync<HyperVResourceReconciliationRequiredException>(
            () => control.FindBySessionIdAsync(journal.SessionId, default));

        Assert.Equal(HyperVResourceReconciliationDisposition.UnjournaledOrphan,
            error.Reconciliation.Disposition);
        Assert.Equal(0, mutations.DestroyCalls);
    }

    [Fact]
    public async Task TimedOutUncancellableCreateRemainsUnresolvedUntilOrphanIsVisible()
    {
        using var journal = new TestJournal();
        _ = AddSession(journal.Value, journal.SessionId);
        var host = CreateHost();
        var mutations = new FakeMutationBackend(host) { HoldCreate = true };
        var authorization = new OneTimeAuthorization();
        authorization.Grant(HyperVPreviewMutation.Create, journal.SessionId, null);
        var control = new GuardedHyperVVmControl(
            journal.Value,
            host,
            authorization,
            mutations,
            operationRunner: new BoundedHyperVMutationRunner(TimeSpan.FromMilliseconds(50)));

        var timeout = await Assert.ThrowsAsync<HyperVOperationOutcomeUnknownException>(
            () => control.CreateAsync(journal.SessionId, default));
        Assert.Equal(HyperVPreviewMutation.Create, timeout.Operation);

        var unresolved = await Assert.ThrowsAsync<HyperVResourceReconciliationRequiredException>(
            () => control.FindBySessionIdAsync(journal.SessionId, default));
        Assert.Equal(HyperVResourceReconciliationDisposition.AllocationIntentUnresolved,
            unresolved.Reconciliation.Disposition);

        mutations.ReleaseCreate();
        await mutations.CreateCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var orphan = await Assert.ThrowsAsync<HyperVResourceReconciliationRequiredException>(
            () => control.FindBySessionIdAsync(journal.SessionId, default));

        Assert.Equal(HyperVResourceReconciliationDisposition.UnjournaledOrphan,
            orphan.Reconciliation.Disposition);
        Assert.Equal(0, mutations.DestroyCalls);
    }

    [Fact]
    public async Task ExactOneTimeAuthorizationAllowsFakeCreateAndStart()
    {
        using var journal = new TestJournal();
        var record = AddSession(journal.Value, journal.SessionId);
        var host = CreateHost();
        var mutations = new FakeMutationBackend(host);
        var authorization = new OneTimeAuthorization();
        authorization.Grant(HyperVPreviewMutation.Create, journal.SessionId, null);
        var control = new GuardedHyperVVmControl(
            journal.Value, host, authorization, mutations);

        var lease = await control.CreateAsync(journal.SessionId, default);
        Assert.Equal(HyperVPreviewOwnershipTag.Format(journal.SessionId, lease.VmId), lease.ResourceId);

        record = journal.Value.Append(record with
        {
            Lease = lease,
            State = SupervisedSessionState.Provisioned,
        });
        authorization.Grant(HyperVPreviewMutation.Start, journal.SessionId, lease.VmId);
        await control.StartAsync(lease, default);

        Assert.Equal(1, mutations.CreateCalls);
        Assert.Equal(1, mutations.StartCalls);
        Assert.Equal(HyperVPreviewVmState.Running, await control.GetStateAsync(lease, default));
    }

    private static SupervisorSessionRecord AddSession(
        DurableSupervisorJournal journal,
        Guid sessionId,
        HyperVPreviewVmLease? lease = null,
        SupervisedSessionState state = SupervisedSessionState.Provisioning)
    {
        var now = DateTimeOffset.UtcNow;
        var uptime = Environment.TickCount64;
        var record = journal.Append(new SupervisorSessionRecord(
            0,
            0,
            sessionId,
            Guid.NewGuid(),
            null,
            SupervisedSessionState.Provisioning,
            now,
            now + SupervisorPolicy.MaximumLifetime,
            now - TimeSpan.FromMilliseconds(uptime),
            uptime,
            checked(uptime + (long)SupervisorPolicy.MaximumLifetime.TotalMilliseconds),
            0,
            [],
            0,
            null,
            null));

        if (state == SupervisedSessionState.Provisioning)
            return record;
        if (state != SupervisedSessionState.Provisioned || lease is null)
            throw new ArgumentException("Only a provisioned test session can include a lease.");
        return journal.Append(record with { Lease = lease, State = state });
    }

    private static HyperVPreviewVmLease CreateLease(Guid sessionId)
    {
        var vmId = Guid.NewGuid();
        return new HyperVPreviewVmLease(sessionId, vmId, HyperVPreviewOwnershipTag.Format(sessionId, vmId));
    }

    private static HyperVObservedVm Observed(
        HyperVPreviewVmLease lease,
        HyperVObservedVmState state) =>
        new(lease.VmId, state, lease.SessionId);

    private static FakeHostInspector CreateHost(params HyperVObservedVm[] machines) =>
        new()
        {
            Inspection = new HyperVHostInspection(
                HyperVHostInspectionStatus.Available,
                new HyperVHostSnapshot(
                    8,
                    GuardedHyperVVmControl.MinimumFreeMemoryKiB + 1,
                    GuardedHyperVVmControl.MinimumFreeSystemDriveBytes + 1,
                    machines)),
        };

    private sealed class TestJournal : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), "AIPlatformHyperVControlTests", Guid.NewGuid().ToString("N"));

        public TestJournal()
        {
            Directory.CreateDirectory(_directory);
            Value = DurableSupervisorJournal.Open(Path.Combine(_directory, "sessions.journal"));
            SessionId = Guid.NewGuid();
        }

        public DurableSupervisorJournal Value { get; }
        public Guid SessionId { get; }

        public void Dispose()
        {
            Value.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeHostInspector : IHyperVHostInspector
    {
        public HyperVHostInspection Inspection { get; set; } =
            new(HyperVHostInspectionStatus.Available, new HyperVHostSnapshot(8, 8_000_000, 50_000_000_000, []));

        public Task<HyperVHostInspection> InspectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Inspection);
        }

        public void SetVirtualMachines(IReadOnlyList<HyperVObservedVm> machines)
        {
            var snapshot = Inspection.Snapshot
                ?? throw new InvalidOperationException("The fake host has no available snapshot.");
            Inspection = Inspection with { Snapshot = snapshot with { VirtualMachines = machines } };
        }
    }

    private sealed class FakeQueryRunner(
        bool timedOut,
        int exitCode,
        string output) : IHyperVHostQueryRunner
    {
        public Task<HyperVHostQueryExecution> ExecuteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HyperVHostQueryExecution(timedOut, exitCode, output));
        }
    }

    private sealed class OneTimeAuthorization : IHyperVMutationAuthorizationStore
    {
        private readonly HashSet<(HyperVPreviewMutation Operation, Guid SessionId, Guid? VmId)> _grants = [];

        public bool TryConsume(
            HyperVPreviewMutation operation,
            Guid sessionId,
            Guid? vmId,
            DateTimeOffset now) => _grants.Remove((operation, sessionId, vmId));

        public void Grant(HyperVPreviewMutation operation, Guid sessionId, Guid? vmId) =>
            _grants.Add((operation, sessionId, vmId));

        public bool HasGrant(HyperVPreviewMutation operation, Guid sessionId, Guid? vmId) =>
            _grants.Contains((operation, sessionId, vmId));
    }

    private sealed class FakeMutationBackend(FakeHostInspector host) : IHyperVVmMutationBackend
    {
        private readonly TaskCompletionSource _createRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int DestroyCalls { get; private set; }
        public bool FailCreateAfterAllocation { get; init; }
        public bool HoldCreate { get; init; }
        public TaskCompletionSource CreateCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HyperVPreviewVmLease> CreateAsync(
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            if (HoldCreate)
                await _createRelease.Task;

            var lease = CreateLease(sessionId);
            host.SetVirtualMachines([Observed(lease, HyperVObservedVmState.Off)]);
            CreateCompleted.TrySetResult();
            if (FailCreateAfterAllocation)
                throw new IOException("Simulated failure after VM allocation.");
            return lease;
        }

        public Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
        {
            StartCalls++;
            host.SetVirtualMachines([Observed(lease, HyperVObservedVmState.Running)]);
            return Task.CompletedTask;
        }

        public Task StopAsync(
            HyperVPreviewVmLease lease,
            HyperVPreviewStopMode mode,
            CancellationToken cancellationToken)
        {
            host.SetVirtualMachines([Observed(lease, HyperVObservedVmState.Off)]);
            return Task.CompletedTask;
        }

        public Task DestroyAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
        {
            DestroyCalls++;
            host.SetVirtualMachines([]);
            return Task.CompletedTask;
        }

        public void ReleaseCreate() => _createRelease.TrySetResult();
    }
}
