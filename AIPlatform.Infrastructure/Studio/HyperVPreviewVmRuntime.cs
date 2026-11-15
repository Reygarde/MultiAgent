using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static class HyperVPreviewVmLifecycle
{
    public static bool CanTransition(HyperVPreviewVmState current, HyperVPreviewVmState next) =>
        (current, next) switch
        {
            (HyperVPreviewVmState.Created, HyperVPreviewVmState.Starting or
                HyperVPreviewVmState.Stopping or HyperVPreviewVmState.Stopped or
                HyperVPreviewVmState.Failed or HyperVPreviewVmState.Destroyed) => true,
            (HyperVPreviewVmState.Starting, HyperVPreviewVmState.Running or
                HyperVPreviewVmState.Stopping or HyperVPreviewVmState.Failed) => true,
            (HyperVPreviewVmState.Running, HyperVPreviewVmState.Stopping or
                HyperVPreviewVmState.Failed) => true,
            (HyperVPreviewVmState.Stopping, HyperVPreviewVmState.Stopped or
                HyperVPreviewVmState.Failed) => true,
            (HyperVPreviewVmState.Stopped, HyperVPreviewVmState.Stopping or
                HyperVPreviewVmState.Destroyed or HyperVPreviewVmState.Failed) => true,
            (HyperVPreviewVmState.Failed, HyperVPreviewVmState.Stopping or
                HyperVPreviewVmState.Stopped or HyperVPreviewVmState.Destroyed) => true,
            _ => false,
        };

    public static HyperVPreviewVmState Transition(
        HyperVPreviewVmState current,
        HyperVPreviewVmState next)
    {
        if (!CanTransition(current, next))
            throw new InvalidOperationException($"Invalid VM transition: {current} -> {next}.");
        return next;
    }
}

public sealed class HyperVPreviewVmRuntime : IHyperVPreviewVmRuntime
{
    private readonly IHyperVVmControl _control;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, VmRecord> _sessions = [];

    public HyperVPreviewVmRuntime(IHyperVVmControl control) => _control = control;

    public async Task<HyperVPreviewVmLease> ProvisionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A non-empty session id is required.", nameof(sessionId));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.ContainsKey(sessionId))
                throw new InvalidOperationException("A VM lease already exists for this session.");

            var lease = await _control.CreateAsync(sessionId, cancellationToken);
            ValidateLease(sessionId, lease);
            if (_sessions.Values.Any(record => record.Lease.VmId == lease.VmId))
                throw new InvalidOperationException("The VM control returned a duplicate VM id.");

            _sessions.Add(sessionId, new VmRecord(lease));
            return lease;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = RequireRecord(lease);
            var adapterState = await ReadStateAsync(lease, cancellationToken);
            if (record.State == HyperVPreviewVmState.Running &&
                adapterState == HyperVPreviewVmState.Running)
                return;
            if (record.State != HyperVPreviewVmState.Created ||
                adapterState != HyperVPreviewVmState.Created)
                throw new InvalidOperationException(
                    $"Cannot start VM from runtime/adapter state {record.State}/{adapterState}.");

            record.SetState(HyperVPreviewVmState.Starting);
            try
            {
                await _control.StartAsync(lease, cancellationToken);
                var confirmedState = await ReadStateAsync(lease, cancellationToken);
                if (confirmedState != HyperVPreviewVmState.Running)
                    throw new InvalidOperationException("The VM adapter did not confirm Running.");
                record.SetState(HyperVPreviewVmState.Running);
            }
            catch
            {
                record.SetState(HyperVPreviewVmState.Failed);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HyperVPreviewPowerState> GetPowerStateAsync(
        HyperVPreviewVmLease lease,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireRecord(lease);
            return ToPowerState(await ReadStateAsync(lease, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HyperVPreviewVmState> GetStateAsync(
        HyperVPreviewVmLease lease,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = RequireRecord(lease);
            var state = await ReadStateAsync(lease, cancellationToken);
            if (state == HyperVPreviewVmState.Destroyed)
            {
                SetDestroyed(record);
                return state;
            }
            if (record.State == HyperVPreviewVmState.Failed)
                return record.State;
            if (state != record.State)
                throw new InvalidOperationException(
                    $"VM state differs from its runtime state: {record.State}/{state}.");
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewStopMode mode,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = RequireRecord(lease);
            var adapterState = await ReadStateAsync(lease, cancellationToken);
            if (adapterState == HyperVPreviewVmState.Destroyed)
            {
                SetDestroyed(record);
                return;
            }

            if (adapterState is HyperVPreviewVmState.Created or HyperVPreviewVmState.Stopped)
            {
                if (adapterState == HyperVPreviewVmState.Stopped)
                    SetStopped(record);
                else if (record.State is not (HyperVPreviewVmState.Created or HyperVPreviewVmState.Failed))
                    record.SetState(HyperVPreviewVmState.Failed);
                return;
            }

            if (record.State != HyperVPreviewVmState.Stopping)
                record.SetState(HyperVPreviewVmState.Stopping);
            try
            {
                await _control.StopAsync(lease, mode, cancellationToken);
                var confirmedState = await ReadStateAsync(lease, cancellationToken);
                if (confirmedState != HyperVPreviewVmState.Stopped)
                    throw new InvalidOperationException("The VM adapter did not confirm Stopped.");
                record.SetState(HyperVPreviewVmState.Stopped);
            }
            catch
            {
                if (record.State == HyperVPreviewVmState.Stopping)
                    record.SetState(HyperVPreviewVmState.Failed);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = RequireRecord(lease);
            var adapterState = await ReadStateAsync(lease, cancellationToken);
            if (adapterState == HyperVPreviewVmState.Destroyed)
            {
                SetDestroyed(record);
                return;
            }
            if (adapterState is not (HyperVPreviewVmState.Created or HyperVPreviewVmState.Stopped))
                throw new InvalidOperationException("A VM must be confirmed stopped before destruction.");

            if (adapterState == HyperVPreviewVmState.Stopped)
                SetStopped(record);
            else if (record.State is not (HyperVPreviewVmState.Created or HyperVPreviewVmState.Failed))
                record.SetState(HyperVPreviewVmState.Failed);

            await _control.DestroyAsync(lease, cancellationToken);
            var confirmedState = await ReadStateAsync(lease, cancellationToken);
            if (confirmedState != HyperVPreviewVmState.Destroyed)
                throw new InvalidOperationException("The VM adapter did not confirm destruction.");
            record.SetState(HyperVPreviewVmState.Destroyed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void SetStopped(VmRecord record)
    {
        if (record.State == HyperVPreviewVmState.Stopped)
            return;
        if (record.State == HyperVPreviewVmState.Destroyed)
            throw new InvalidOperationException("A destroyed VM cannot return to a powered-off state.");
        if (record.State is not (HyperVPreviewVmState.Created or HyperVPreviewVmState.Failed))
            record.SetState(HyperVPreviewVmState.Failed);
        record.SetState(HyperVPreviewVmState.Stopped);
    }

    private static void SetDestroyed(VmRecord record)
    {
        if (record.State == HyperVPreviewVmState.Destroyed)
            return;
        if (record.State is not (HyperVPreviewVmState.Failed or HyperVPreviewVmState.Stopped))
            record.SetState(HyperVPreviewVmState.Failed);
        record.SetState(HyperVPreviewVmState.Destroyed);
    }

    private VmRecord RequireRecord(HyperVPreviewVmLease lease)
    {
        if (!_sessions.TryGetValue(lease.SessionId, out var record) || record.Lease != lease)
            throw new InvalidOperationException("The VM lease is unknown or does not match.");
        return record;
    }

    private async Task<HyperVPreviewVmState> ReadStateAsync(
        HyperVPreviewVmLease lease,
        CancellationToken cancellationToken)
    {
        var state = await _control.GetStateAsync(lease, cancellationToken);
        if (!Enum.IsDefined(state))
            throw new InvalidOperationException("The VM adapter returned an unknown state.");
        return state;
    }

    private static HyperVPreviewPowerState ToPowerState(HyperVPreviewVmState state) =>
        state switch
        {
            HyperVPreviewVmState.Created or HyperVPreviewVmState.Stopped or
                HyperVPreviewVmState.Destroyed => HyperVPreviewPowerState.Off,
            HyperVPreviewVmState.Running => HyperVPreviewPowerState.Running,
            _ => HyperVPreviewPowerState.Unknown,
        };

    private static void ValidateLease(Guid sessionId, HyperVPreviewVmLease lease)
    {
        if (lease is null || lease.SessionId != sessionId || lease.VmId == Guid.Empty ||
            string.IsNullOrWhiteSpace(lease.ResourceId) || lease.ResourceId.Length > 256 ||
            lease.ResourceId.Any(char.IsControl))
            throw new InvalidOperationException("The VM adapter returned an invalid lease.");
    }

    private sealed class VmRecord(HyperVPreviewVmLease lease)
    {
        public HyperVPreviewVmLease Lease { get; } = lease;
        public HyperVPreviewVmState State { get; private set; } = HyperVPreviewVmState.Created;

        public void SetState(HyperVPreviewVmState next) =>
            State = HyperVPreviewVmLifecycle.Transition(State, next);
    }
}
