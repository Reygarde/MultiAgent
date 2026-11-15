using AIPlatform.Core.Studio;

namespace AIPlatform.PreviewSupervisor;

public enum HyperVPreviewMutation
{
    Create,
    Start,
    GracefulStop,
    ForcedStop,
    Destroy,
}

public enum HyperVResourceReconciliationDisposition
{
    OwnedAndPresent,
    UnjournaledOrphan,
    AllocationIntentUnresolved,
    TrackedVmMissing,
    IdentityMismatch,
    MultipleCandidates,
}

public sealed record HyperVResourceReconciliation(
    Guid SessionId,
    HyperVResourceReconciliationDisposition Disposition,
    HyperVPreviewVmLease? Lease,
    Guid? CandidateVmId);

public interface IHyperVMutationAuthorizationStore
{
    bool TryConsume(
        HyperVPreviewMutation operation,
        Guid sessionId,
        Guid? vmId,
        DateTimeOffset now);
}

public interface IHyperVVmMutationBackend
{
    Task<HyperVPreviewVmLease> CreateAsync(Guid sessionId, CancellationToken cancellationToken);
    Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken);
    Task StopAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewStopMode mode,
        CancellationToken cancellationToken);
    Task DestroyAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken);
}

public interface IHyperVResourceCleanupVerifier
{
    Task<bool> IsCleanAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken);
}

public sealed class DenyAllHyperVMutationAuthorizationStore : IHyperVMutationAuthorizationStore
{
    public bool TryConsume(
        HyperVPreviewMutation operation,
        Guid sessionId,
        Guid? vmId,
        DateTimeOffset now) => false;
}

public sealed class DisabledHyperVVmMutationBackend : IHyperVVmMutationBackend
{
    private static HyperVMutationDisabledException Disabled() => new();

    public Task<HyperVPreviewVmLease> CreateAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromException<HyperVPreviewVmLease>(Disabled());

    public Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken) =>
        Task.FromException(Disabled());

    public Task StopAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewStopMode mode,
        CancellationToken cancellationToken) =>
        Task.FromException(Disabled());

    public Task DestroyAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken) =>
        Task.FromException(Disabled());
}

public sealed class UnavailableHyperVResourceCleanupVerifier : IHyperVResourceCleanupVerifier
{
    public Task<bool> IsCleanAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

public sealed class HyperVMutationDisabledException()
    : NotSupportedException("Hyper-V mutation commands are not implemented or enabled in this build.");

public sealed class HyperVMutationNotAuthorizedException(HyperVPreviewMutation operation)
    : UnauthorizedAccessException($"Hyper-V operation {operation} has no matching one-time authorization.");

public sealed class HyperVSessionNotManagedException(Guid sessionId)
    : UnauthorizedAccessException($"Hyper-V session {sessionId} is not present in the supervisor journal.");

public sealed class HyperVResourceReconciliationRequiredException(
    HyperVResourceReconciliation reconciliation)
    : IOException($"Hyper-V resource requires manual reconciliation: {reconciliation.Disposition}.")
{
    public HyperVResourceReconciliation Reconciliation { get; } = reconciliation;
}

public sealed class HyperVOperationOutcomeUnknownException(
    HyperVPreviewMutation operation,
    Guid sessionId,
    string reason)
    : IOException($"Hyper-V operation {operation} for session {sessionId:N} has an unknown outcome ({reason}).")
{
    public HyperVPreviewMutation Operation { get; } = operation;
    public Guid SessionId { get; } = sessionId;
}

public sealed class HyperVHostUnavailableException(HyperVHostInspectionStatus status, string? errorCode)
    : IOException($"Hyper-V host inspection is unavailable: {status} ({errorCode ?? "no detail"}).")
{
    public HyperVHostInspectionStatus Status { get; } = status;
}

public sealed class HyperVHostResourceGateException(string reason)
    : InvalidOperationException($"Hyper-V host resource gate failed: {reason}.")
{
    public string Reason { get; } = reason;
}

public static class HyperVPreviewOwnershipTag
{
    public static string Format(Guid sessionId, Guid vmId)
    {
        if (sessionId == Guid.Empty || vmId == Guid.Empty)
            throw new ArgumentException("Ownership tags require non-empty immutable identifiers.");
        return $"AIPlatformPreview/65J/{sessionId:N}/{vmId:D}";
    }

    public static bool Matches(HyperVObservedVm vm, HyperVPreviewVmLease lease) =>
        vm.VmId == lease.VmId &&
        vm.OwnerSessionId == lease.SessionId &&
        lease.ResourceId == Format(lease.SessionId, lease.VmId);
}

public static class HyperVPreviewResourceReconciler
{
    public static HyperVResourceReconciliation Reconcile(
        SupervisorSessionRecord session,
        IReadOnlyList<HyperVObservedVm> inventory)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(inventory);

        var candidates = inventory
            .Where(vm => vm.OwnerSessionId == session.SessionId)
            .ToArray();
        if (session.Lease is null)
        {
            return new HyperVResourceReconciliation(
                session.SessionId,
                candidates.Length switch
                {
                    0 => HyperVResourceReconciliationDisposition.AllocationIntentUnresolved,
                    1 => HyperVResourceReconciliationDisposition.UnjournaledOrphan,
                    _ => HyperVResourceReconciliationDisposition.MultipleCandidates,
                },
                Lease: null,
                CandidateVmId: candidates.FirstOrDefault()?.VmId);
        }

        var lease = session.Lease;
        if (candidates.Any(vm => vm.VmId != lease.VmId))
            return new HyperVResourceReconciliation(
                session.SessionId,
                HyperVResourceReconciliationDisposition.MultipleCandidates,
                lease,
                candidates.First(vm => vm.VmId != lease.VmId).VmId);

        var exactVm = inventory.SingleOrDefault(vm => vm.VmId == lease.VmId);
        if (exactVm is null)
            return new HyperVResourceReconciliation(
                session.SessionId,
                HyperVResourceReconciliationDisposition.TrackedVmMissing,
                lease,
                CandidateVmId: null);
        if (!HyperVPreviewOwnershipTag.Matches(exactVm, lease))
            return new HyperVResourceReconciliation(
                session.SessionId,
                HyperVResourceReconciliationDisposition.IdentityMismatch,
                lease,
                exactVm.VmId);
        return new HyperVResourceReconciliation(
            session.SessionId,
            HyperVResourceReconciliationDisposition.OwnedAndPresent,
            lease,
            exactVm.VmId);
    }
}

public sealed class BoundedHyperVMutationRunner
{
    private readonly TimeSpan _maximumDuration;

    public BoundedHyperVMutationRunner(TimeSpan? maximumDuration = null)
    {
        _maximumDuration = maximumDuration ?? TimeSpan.FromSeconds(10);
        if (_maximumDuration <= TimeSpan.Zero || _maximumDuration > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
    }

    public async Task<T> RunAsync<T>(
        HyperVPreviewMutation operation,
        Guid sessionId,
        Func<CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A non-empty session id is required.", nameof(sessionId));
        cancellationToken.ThrowIfCancellationRequested();

        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(_maximumDuration);
        Task<T> pending;
        pending = execute(operationTimeout.Token);

        try
        {
            return await pending.WaitAsync(_maximumDuration, cancellationToken);
        }
        catch (TimeoutException)
        {
            operationTimeout.Cancel();
            ObserveLateFailure(pending, operation, sessionId);
            throw new HyperVOperationOutcomeUnknownException(operation, sessionId, "timeout");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationTimeout.Cancel();
            ObserveLateFailure(pending, operation, sessionId);
            throw new HyperVOperationOutcomeUnknownException(operation, sessionId, "caller-cancelled");
        }
        catch (OperationCanceledException)
        {
            ObserveLateFailure(pending, operation, sessionId);
            throw new HyperVOperationOutcomeUnknownException(operation, sessionId, "adapter-cancelled");
        }
    }

    public async Task RunAsync(
        HyperVPreviewMutation operation,
        Guid sessionId,
        Func<CancellationToken, Task> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _ = await RunAsync(
            operation,
            sessionId,
            async token =>
            {
                await execute(token);
                return true;
            },
            cancellationToken);
    }

    private static void ObserveLateFailure<T>(
        Task<T> pending,
        HyperVPreviewMutation operation,
        Guid sessionId)
    {
        _ = pending.ContinueWith(
            completed =>
            {
                if (completed.Exception is { } error)
                    Console.Error.WriteLine(
                        $"Late Hyper-V operation {operation} for {sessionId:N} failed: {error.GetBaseException().GetType().Name}");
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

public sealed class GuardedHyperVVmControl : IRecoverableHyperVVmControl
{
    public const long MinimumFreeMemoryKiB = 4_718_592;
    public const long MinimumFreeSystemDriveBytes = 15L * 1024 * 1024 * 1024;

    private readonly DurableSupervisorJournal _journal;
    private readonly IHyperVHostInspector _hostInspector;
    private readonly IHyperVMutationAuthorizationStore _authorization;
    private readonly IHyperVVmMutationBackend _mutations;
    private readonly IHyperVResourceCleanupVerifier _cleanupVerifier;
    private readonly BoundedHyperVMutationRunner _operationRunner;
    private readonly TimeProvider _clock;

    public GuardedHyperVVmControl(
        DurableSupervisorJournal journal,
        IHyperVHostInspector hostInspector,
        IHyperVMutationAuthorizationStore? authorization = null,
        IHyperVVmMutationBackend? mutations = null,
        IHyperVResourceCleanupVerifier? cleanupVerifier = null,
        BoundedHyperVMutationRunner? operationRunner = null,
        TimeProvider? clock = null)
    {
        _journal = journal;
        _hostInspector = hostInspector;
        _authorization = authorization ?? new DenyAllHyperVMutationAuthorizationStore();
        _mutations = mutations ?? new DisabledHyperVVmMutationBackend();
        _cleanupVerifier = cleanupVerifier ?? new UnavailableHyperVResourceCleanupVerifier();
        _operationRunner = operationRunner ?? new BoundedHyperVMutationRunner();
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<HyperVPreviewVmLease> CreateAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A non-empty session id is required.", nameof(sessionId));
        var record = RequireSession(sessionId);
        if (record.State != SupervisedSessionState.Provisioning || record.Lease is not null)
            throw new HyperVMutationNotAuthorizedException(HyperVPreviewMutation.Create);

        var snapshot = await ReadHostSnapshotAsync(cancellationToken);
        EnsureNoCandidateForSession(record, snapshot);
        EnsureHostResources(snapshot);
        DemandAuthorization(HyperVPreviewMutation.Create, sessionId, vmId: null);

        var lease = await _operationRunner.RunAsync(
            HyperVPreviewMutation.Create,
            sessionId,
            token => _mutations.CreateAsync(sessionId, token),
            cancellationToken);
        ValidateLease(sessionId, lease);

        var afterCreate = await ReadHostSnapshotAsync(CancellationToken.None);
        var createdRecord = record with
        {
            Lease = lease,
            State = SupervisedSessionState.Provisioned,
        };
        var reconciliation = HyperVPreviewResourceReconciler.Reconcile(
            createdRecord,
            afterCreate.VirtualMachines);
        if (reconciliation.Disposition != HyperVResourceReconciliationDisposition.OwnedAndPresent ||
            afterCreate.VirtualMachines.Single(vm => vm.VmId == lease.VmId).State != HyperVObservedVmState.Off)
            throw new HyperVOperationOutcomeUnknownException(
                HyperVPreviewMutation.Create,
                sessionId,
                "created VM identity/state was not confirmed");
        return lease;
    }

    public async Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
    {
        var record = RequireLease(lease);
        if (record.State != SupervisedSessionState.Provisioned)
            throw new HyperVMutationNotAuthorizedException(HyperVPreviewMutation.Start);
        var snapshot = await ReadHostSnapshotAsync(cancellationToken);
        var vm = RequireOwnedVm(record, lease, snapshot);
        if (vm.State != HyperVObservedVmState.Off)
            throw new HyperVMutationNotAuthorizedException(HyperVPreviewMutation.Start);
        EnsureHostResources(snapshot);
        DemandAuthorization(HyperVPreviewMutation.Start, lease.SessionId, lease.VmId);

        await _operationRunner.RunAsync(
            HyperVPreviewMutation.Start,
            lease.SessionId,
            token => _mutations.StartAsync(lease, token),
            cancellationToken);
        var afterStart = await ReadHostSnapshotAsync(CancellationToken.None);
        if (RequireOwnedVm(record, lease, afterStart).State != HyperVObservedVmState.Running)
            throw new HyperVOperationOutcomeUnknownException(
                HyperVPreviewMutation.Start,
                lease.SessionId,
                "Running was not confirmed");
    }

    public async Task StopAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewStopMode mode,
        CancellationToken cancellationToken)
    {
        var record = RequireLease(lease);
        if (record.State is not (SupervisedSessionState.Provisioned or SupervisedSessionState.Running or
                SupervisedSessionState.Stopping or SupervisedSessionState.CleanupFailed) ||
            !Enum.IsDefined(mode))
            throw new HyperVMutationNotAuthorizedException(
                mode == HyperVPreviewStopMode.Force
                    ? HyperVPreviewMutation.ForcedStop
                    : HyperVPreviewMutation.GracefulStop);

        var snapshot = await ReadHostSnapshotAsync(cancellationToken);
        var vm = RequireOwnedVm(record, lease, snapshot);
        if (vm.State == HyperVObservedVmState.Off)
            return;
        var operation = mode == HyperVPreviewStopMode.Force
            ? HyperVPreviewMutation.ForcedStop
            : HyperVPreviewMutation.GracefulStop;
        DemandAuthorization(operation, lease.SessionId, lease.VmId);
        await _operationRunner.RunAsync(
            operation,
            lease.SessionId,
            token => _mutations.StopAsync(lease, mode, token),
            cancellationToken);
        var afterStop = await ReadHostSnapshotAsync(CancellationToken.None);
        if (RequireOwnedVm(record, lease, afterStop).State != HyperVObservedVmState.Off)
            throw new HyperVOperationOutcomeUnknownException(operation, lease.SessionId, "Off was not confirmed");
    }

    public async Task DestroyAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
    {
        var record = RequireLease(lease);
        if (record.State is not (SupervisedSessionState.Stopping or SupervisedSessionState.CleanupFailed))
            throw new HyperVMutationNotAuthorizedException(HyperVPreviewMutation.Destroy);

        var snapshot = await ReadHostSnapshotAsync(cancellationToken);
        var vm = RequireOwnedVm(record, lease, snapshot);
        if (vm.State != HyperVObservedVmState.Off)
            throw new HyperVMutationNotAuthorizedException(HyperVPreviewMutation.Destroy);
        DemandAuthorization(HyperVPreviewMutation.Destroy, lease.SessionId, lease.VmId);
        await _operationRunner.RunAsync(
            HyperVPreviewMutation.Destroy,
            lease.SessionId,
            token => _mutations.DestroyAsync(lease, token),
            cancellationToken);

        var afterDestroy = await ReadHostSnapshotAsync(CancellationToken.None);
        if (afterDestroy.VirtualMachines.Any(candidate => candidate.VmId == lease.VmId) ||
            !await _cleanupVerifier.IsCleanAsync(lease, CancellationToken.None))
            throw new HyperVOperationOutcomeUnknownException(
                HyperVPreviewMutation.Destroy,
                lease.SessionId,
                "VM absence and owned artifact cleanup were not both confirmed");
    }

    public async Task<HyperVPreviewVmState> GetStateAsync(
        HyperVPreviewVmLease lease,
        CancellationToken cancellationToken)
    {
        var record = RequireLease(lease);
        var snapshot = await ReadHostSnapshotAsync(cancellationToken);
        var reconciliation = HyperVPreviewResourceReconciler.Reconcile(record, snapshot.VirtualMachines);
        if (reconciliation.Disposition == HyperVResourceReconciliationDisposition.TrackedVmMissing)
        {
            if (await _cleanupVerifier.IsCleanAsync(lease, cancellationToken))
                return HyperVPreviewVmState.Destroyed;
            throw new HyperVResourceReconciliationRequiredException(reconciliation);
        }
        var vm = RequireOwnedVm(record, lease, snapshot);
        return vm.State switch
        {
            HyperVObservedVmState.Off => HyperVPreviewVmState.Stopped,
            HyperVObservedVmState.Running => HyperVPreviewVmState.Running,
            HyperVObservedVmState.Starting => HyperVPreviewVmState.Starting,
            HyperVObservedVmState.Stopping => HyperVPreviewVmState.Stopping,
            _ => HyperVPreviewVmState.Failed,
        };
    }

    public async Task<HyperVPreviewVmLease?> FindBySessionIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var record = RequireSession(sessionId);
        var snapshot = await ReadHostSnapshotAsync(cancellationToken);
        var reconciliation = HyperVPreviewResourceReconciler.Reconcile(record, snapshot.VirtualMachines);
        if (reconciliation.Disposition == HyperVResourceReconciliationDisposition.OwnedAndPresent)
            return record.Lease;
        if (reconciliation.Disposition == HyperVResourceReconciliationDisposition.TrackedVmMissing &&
            record.Lease is not null &&
            await _cleanupVerifier.IsCleanAsync(record.Lease, cancellationToken))
            return null;
        throw new HyperVResourceReconciliationRequiredException(reconciliation);
    }

    private async Task<HyperVHostSnapshot> ReadHostSnapshotAsync(CancellationToken cancellationToken)
    {
        var inspection = await _hostInspector.InspectAsync(cancellationToken);
        if (inspection.Status != HyperVHostInspectionStatus.Available || inspection.Snapshot is null)
            throw new HyperVHostUnavailableException(inspection.Status, inspection.ErrorCode);
        return inspection.Snapshot;
    }

    private static HyperVObservedVm RequireOwnedVm(
        SupervisorSessionRecord record,
        HyperVPreviewVmLease lease,
        HyperVHostSnapshot snapshot)
    {
        var reconciliation = HyperVPreviewResourceReconciler.Reconcile(record, snapshot.VirtualMachines);
        if (reconciliation.Disposition != HyperVResourceReconciliationDisposition.OwnedAndPresent)
            throw new HyperVResourceReconciliationRequiredException(reconciliation);
        return snapshot.VirtualMachines.Single(vm => vm.VmId == lease.VmId);
    }

    private static void EnsureNoCandidateForSession(
        SupervisorSessionRecord record,
        HyperVHostSnapshot snapshot)
    {
        if (snapshot.VirtualMachines.Any(vm => vm.OwnerSessionId == record.SessionId))
            throw new HyperVResourceReconciliationRequiredException(
                HyperVPreviewResourceReconciler.Reconcile(record, snapshot.VirtualMachines));
    }

    private static void EnsureHostResources(HyperVHostSnapshot snapshot)
    {
        if (snapshot.FreePhysicalMemoryKiB < MinimumFreeMemoryKiB)
            throw new HyperVHostResourceGateException("less than 4.5 GiB physical memory is available");
        if (snapshot.FreeSystemDriveBytes < MinimumFreeSystemDriveBytes)
            throw new HyperVHostResourceGateException("less than 15 GiB is available on the system drive");
    }

    private void DemandAuthorization(
        HyperVPreviewMutation operation,
        Guid sessionId,
        Guid? vmId)
    {
        if (!_authorization.TryConsume(operation, sessionId, vmId, _clock.GetUtcNow()))
            throw new HyperVMutationNotAuthorizedException(operation);
    }

    private SupervisorSessionRecord RequireSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A non-empty session id is required.", nameof(sessionId));
        return _journal.ReadLatest().SingleOrDefault(record => record.SessionId == sessionId)
            ?? throw new HyperVSessionNotManagedException(sessionId);
    }

    private SupervisorSessionRecord RequireLease(HyperVPreviewVmLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var record = RequireSession(lease.SessionId);
        if (record.Lease != lease)
            throw new HyperVResourceReconciliationRequiredException(
                new HyperVResourceReconciliation(
                    lease.SessionId,
                    HyperVResourceReconciliationDisposition.IdentityMismatch,
                    record.Lease,
                    lease.VmId));
        return record;
    }

    private static void ValidateLease(Guid sessionId, HyperVPreviewVmLease lease)
    {
        if (lease is null || lease.SessionId != sessionId || lease.VmId == Guid.Empty ||
            lease.ResourceId != HyperVPreviewOwnershipTag.Format(sessionId, lease.VmId))
            throw new HyperVOperationOutcomeUnknownException(
                HyperVPreviewMutation.Create,
                sessionId,
                "lease identity or ownership marker is invalid");
    }
}
