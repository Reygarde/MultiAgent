using AIPlatform.Core;

namespace AIPlatform.API;

public static class ModelEndpoints
{
    public static void MapModels(this WebApplication app)
    {
        app.MapGet("/api/models", (IAgentRegistry registry, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TypedResults.Ok(registry.Models.Select(model => new ModelResponse(
                model.Id, model.Provider, model.Name, model.ContextSize, model.MaxOutputTokens,
                model.Temperature, registry.Agents.Where(agent => agent.ModelId == model.Id)
                    .Select(agent => agent.Id).ToArray())).ToArray());
        })
        .WithName("GetModels")
        .WithSummary("List configured models and agent assignments")
        .WithDescription("Returns the allowlist from configuration, not the list of models installed in Ollama.");
    }
}
