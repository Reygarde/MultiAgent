using System.Text.Json;

namespace AIPlatform.Core;

public sealed record LlmMessage(string Role, string Content);

public sealed record LlmRequest(
    ModelDefinition Model,
    IReadOnlyList<LlmMessage> Messages,
    JsonElement? ResponseSchema = null);

public interface ILLMProvider
{
    string Name { get; }
    Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}

public interface ILLMProviderResolver
{
    ILLMProvider Resolve(string name);
}

public sealed class LlmProviderResolver(IEnumerable<ILLMProvider> providers) : ILLMProviderResolver
{
    private readonly IReadOnlyDictionary<string, ILLMProvider> providers =
        providers.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);

    public ILLMProvider Resolve(string name) =>
        providers.TryGetValue(name, out var provider)
            ? provider
            : throw new InvalidOperationException($"LLM provider '{name}' is not registered.");
}

public class LlmProviderException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class LlmTimeoutException(string message, Exception? innerException = null)
    : LlmProviderException(message, innerException);
