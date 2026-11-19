namespace AIPlatform.Infrastructure.Studio;

public sealed class OllamaModelOperations
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}
