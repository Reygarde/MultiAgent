using System.Text.Json;

namespace AIPlatform.Core;

public sealed record LlmMessage(string Role, string Content);

public sealed record LlmDiagnosticContext(Guid WorkspaceId, Guid RunId, string TaskId, Guid AttemptId);

public sealed record LlmRequest(
    ModelDefinition Model,
    IReadOnlyList<LlmMessage> Messages,
    JsonElement? ResponseSchema = null, bool ExclusiveModel = false,
    LlmDiagnosticContext? DiagnosticContext = null)
{
    public bool RequireAutomaticAdmissibility { get; init; }
    public Guid? CallId { get; init; }
}

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
        providers.ToDictionary(provider => provider.Name, BudgetedLlmProvider.Wrap, StringComparer.OrdinalIgnoreCase);

    public ILLMProvider Resolve(string name) =>
        providers.TryGetValue(name, out var provider)
            ? provider
            : throw new InvalidOperationException($"LLM provider '{name}' is not registered.");
}

public class LlmProviderException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class LlmTimeoutException(string message, Exception? innerException = null)
    : LlmProviderException(message, innerException);

public sealed class LlmMemoryException(string message) : LlmProviderException(message);

public sealed class LlmContextException(string message) : LlmProviderException(message);

public sealed class LlmOutputLimitException(string model, int limit, int? evaluatedTokens,
    int contentCharacters, int thinkingCharacters)
    : LlmProviderException($"Ollama reached the output token limit before completing the response " +
        $"(model {model}, num_predict={limit}, eval_count={evaluatedTokens?.ToString() ?? "unavailable"}, " +
        $"content characters={contentCharacters}, thinking characters={thinkingCharacters}). " +
        "The partial response was rejected; review the model and file scope before an explicit retry.")
{
    public int? EvaluatedTokens { get; } = evaluatedTokens;
    public int ContentCharacters { get; } = contentCharacters;
    public int ThinkingCharacters { get; } = thinkingCharacters;
}

public sealed class LlmDiagnosticException(string message) : LlmProviderException(message);
