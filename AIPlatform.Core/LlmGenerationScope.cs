namespace AIPlatform.Core;

/// <summary>Propagates one mission's durable reservation boundary through asynchronous agent calls.</summary>
public sealed class LlmGenerationScope : IDisposable
{
    private sealed record Context(Action<LlmRequest, string, Guid> Reserve,
        Action<Guid, bool> RecordDelivery, string Role);
    private static readonly AsyncLocal<Context?> Current = new();
    private readonly Context? previous = Current.Value;

    public LlmGenerationScope(Action<LlmRequest, string, Guid> reserve,
        Action<Guid, bool> recordDelivery, string role) =>
        Current.Value = new(reserve, recordDelivery, role);

    private LlmGenerationScope(string role) =>
        Current.Value = previous is null ? null : previous with { Role = role };

    public static IDisposable ForRole(string role) => new LlmGenerationScope(role);

    public static Guid? Reserve(LlmRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Current.Value is not { } context) return null;
        var reservationId = Guid.NewGuid();
        context.Reserve(request, context.Role, reservationId);
        return reservationId;
    }

    public static void MarkRequestMayHaveBeenSent(Guid? reservationId) =>
        RecordDelivery(reservationId, true);

    public static void MarkRequestDefinitelyNotSent(Guid? reservationId) =>
        RecordDelivery(reservationId, false);

    private static void RecordDelivery(Guid? reservationId, bool mayHaveBeenSent)
    {
        if (reservationId is { } id && Current.Value is { } context)
            context.RecordDelivery(id, mayHaveBeenSent);
    }

    public void Dispose() => Current.Value = previous;
}

/// <summary>A provider that reserves through LlmGenerationScope before starting generation.</summary>
public interface IGenerationBudgetedProvider : ILLMProvider;

/// <summary>Applies the same reservation boundary to providers without a native implementation.</summary>
public sealed class BudgetedLlmProvider(ILLMProvider inner) : IGenerationBudgetedProvider
{
    public string Name => inner.Name;

    public static ILLMProvider Wrap(ILLMProvider provider) =>
        provider is IGenerationBudgetedProvider ? provider : new BudgetedLlmProvider(provider);

    public Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        LlmGenerationScope.Reserve(request, cancellationToken);
        return inner.CompleteAsync(request, cancellationToken);
    }
}

public sealed class GenerationBudgetExhaustedException()
    : Exception("GenerationBudgetExhausted: no generation unit remains. Existing files, plans and diagnostics are retained.");
