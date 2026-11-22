namespace AIPlatform.API.Services;

public interface IStudioMaintenanceLease : IDisposable
{
    void PreserveUntilRestart();
}

public sealed class StudioMaintenanceGate
{
    private readonly object sync = new();
    private int activeRequests;
    private bool maintenance;
    private TaskCompletionSource drained = Completed();
    private TaskCompletionSource released = Completed();

    public async Task<IDisposable> EnterRequestAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (sync)
            {
                if (!maintenance)
                {
                    if (activeRequests++ == 0) drained = NewSignal();
                    return new Lease(this, false);
                }
                wait = released.Task;
            }
            await wait.WaitAsync(token);
        }
    }

    public async Task<IStudioMaintenanceLease> EnterMaintenanceAsync(CancellationToken token)
    {
        Task wait;
        lock (sync)
        {
            if (maintenance)
                throw new InvalidOperationException("Another Studio backup or restore is already in progress.");
            maintenance = true;
            released = NewSignal();
            wait = activeRequests == 0 ? Task.CompletedTask : drained.Task;
        }
        try
        {
            await wait.WaitAsync(token);
            return new Lease(this, true);
        }
        catch
        {
            ReleaseMaintenance();
            throw;
        }
    }

    private void LeaveRequest()
    {
        lock (sync)
            if (--activeRequests == 0) drained.TrySetResult();
    }

    private void ReleaseMaintenance()
    {
        lock (sync)
        {
            maintenance = false;
            released.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource Completed()
    {
        var result = NewSignal();
        result.SetResult();
        return result;
    }

    private sealed class Lease(StudioMaintenanceGate owner, bool isMaintenance) : IStudioMaintenanceLease
    {
        private bool keepClosed;
        private bool disposed;

        public void PreserveUntilRestart()
        {
            if (!isMaintenance) throw new InvalidOperationException("Only a maintenance lease can be preserved.");
            keepClosed = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (isMaintenance)
            {
                if (!keepClosed) owner.ReleaseMaintenance();
            }
            else owner.LeaveRequest();
        }
    }
}
