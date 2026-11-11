namespace AIPlatform.Core.Studio;

public sealed class MissionExecutionGate(int concurrency = 1) : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(concurrency, concurrency);

    public Task EnterAsync(CancellationToken cancellationToken) => semaphore.WaitAsync(cancellationToken);
    public void Exit() => semaphore.Release();
    public void Dispose() => semaphore.Dispose();
}
