using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed record HyperVPreviewSupervisionResult(bool Expired, Exception? Error);

public sealed class HyperVPreviewSupervisor : IHyperVPreviewSupervisor, IAsyncDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PendingExpiry> _pending = [];
    private readonly HashSet<Guid> _usedSessions = [];
    private readonly Dictionary<Guid, HyperVPreviewSupervisionResult> _recentResults = [];
    private readonly Queue<Guid> _resultOrder = [];
    private const int ResultHistoryLimit = 64;
    private bool _disposed;

    public HyperVPreviewSupervisor(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public Task ArmAsync(
        HyperVPreviewVmLease lease,
        TimeSpan maximumLifetime,
        Func<CancellationToken, Task> onExpired,
        CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(onExpired);
        if (maximumLifetime <= TimeSpan.Zero ||
            maximumLifetime > HyperVPreviewPolicy.MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(maximumLifetime));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_usedSessions.Add(lease.SessionId))
                throw new InvalidOperationException("A supervisor has already been armed for this session.");

            var pending = new PendingExpiry(this, lease, onExpired);
            pending.Timer = _timeProvider.CreateTimer(
                static state =>
                {
                    var expiry = (PendingExpiry)state!;
                    expiry.Owner.OnTimer(expiry);
                },
                pending,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _pending.Add(lease.SessionId, pending);
            if (!pending.Timer.Change(maximumLifetime, Timeout.InfiniteTimeSpan))
            {
                _pending.Remove(lease.SessionId);
                pending.Timer.Dispose();
                throw new InvalidOperationException("Could not arm the Preview expiry timer.");
            }
        }

        return Task.CompletedTask;
    }

    public Task DisarmAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_pending.TryGetValue(lease.SessionId, out var pending))
                return Task.CompletedTask;
            if (pending.Lease != lease)
                throw new InvalidOperationException("Supervisor lease identity mismatch.");
            if (pending.Fired)
                return Task.CompletedTask;

            _pending.Remove(lease.SessionId);
            pending.Timer.Dispose();
            Complete(pending, new HyperVPreviewSupervisionResult(false, null));
        }

        return Task.CompletedTask;
    }

    public Task<HyperVPreviewSupervisionResult> ObserveExpiryAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_pending.TryGetValue(sessionId, out var pending))
                return pending.Completion.Task.WaitAsync(cancellationToken);
            if (_recentResults.TryGetValue(sessionId, out var result))
                return Task.FromResult(result);
            throw new KeyNotFoundException("No supervisor expiry is registered for this session.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<HyperVPreviewSupervisionResult>[] firing;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            firing = _pending.Values.Where(pending => pending.Fired)
                .Select(pending => pending.Completion.Task)
                .ToArray();
            foreach (var pending in _pending.Values.Where(pending => !pending.Fired).ToArray())
            {
                _pending.Remove(pending.Lease.SessionId);
                pending.Timer.Dispose();
                Complete(pending, new HyperVPreviewSupervisionResult(false, null));
            }
        }

        if (firing.Length > 0)
            await Task.WhenAll(firing);
    }

    private void OnTimer(PendingExpiry pending)
    {
        lock (_sync)
        {
            if (!_pending.TryGetValue(pending.Lease.SessionId, out var current) ||
                !ReferenceEquals(current, pending) || pending.Fired)
                return;
            pending.Fired = true;
        }

        _ = RunExpiryAsync(pending);
    }

    private async Task RunExpiryAsync(PendingExpiry pending)
    {
        HyperVPreviewSupervisionResult result;
        try
        {
            await pending.OnExpired(CancellationToken.None);
            result = new HyperVPreviewSupervisionResult(true, null);
        }
        catch (Exception error)
        {
            result = new HyperVPreviewSupervisionResult(true, error);
        }
        try
        {
            pending.Timer.Dispose();
        }
        catch (Exception error)
        {
            result = result with
            {
                Error = result.Error is null ? error : new AggregateException(result.Error, error),
            };
        }
        lock (_sync)
        {
            if (_pending.TryGetValue(pending.Lease.SessionId, out var current) &&
                ReferenceEquals(current, pending))
                _pending.Remove(pending.Lease.SessionId);
            AddResult(pending.Lease.SessionId, result);
            pending.Completion.TrySetResult(result);
        }
    }

    private void Complete(PendingExpiry pending, HyperVPreviewSupervisionResult result)
    {
        AddResult(pending.Lease.SessionId, result);
        pending.Completion.TrySetResult(result);
    }

    private void AddResult(Guid sessionId, HyperVPreviewSupervisionResult result)
    {
        _recentResults[sessionId] = result;
        _resultOrder.Enqueue(sessionId);
        while (_resultOrder.Count > ResultHistoryLimit)
            _recentResults.Remove(_resultOrder.Dequeue());
    }

    private static void ValidateLease(HyperVPreviewVmLease lease)
    {
        if (lease is null || lease.SessionId == Guid.Empty || lease.VmId == Guid.Empty ||
            string.IsNullOrWhiteSpace(lease.ResourceId) || lease.ResourceId.Length > 256 ||
            lease.ResourceId.Any(char.IsControl))
            throw new ArgumentException("Invalid Hyper-V supervisor lease.", nameof(lease));
    }

    private sealed class PendingExpiry(
        HyperVPreviewSupervisor owner,
        HyperVPreviewVmLease lease,
        Func<CancellationToken, Task> onExpired)
    {
        public HyperVPreviewSupervisor Owner { get; } = owner;
        public HyperVPreviewVmLease Lease { get; } = lease;
        public Func<CancellationToken, Task> OnExpired { get; } = onExpired;
        public TaskCompletionSource<HyperVPreviewSupervisionResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ITimer Timer { get; set; } = null!;
        public bool Fired { get; set; }
    }
}
