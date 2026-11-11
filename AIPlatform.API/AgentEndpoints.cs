using AIPlatform.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AIPlatform.API;

public static class AgentEndpoints
{
    public static void MapAgents(this WebApplication app)
    {
        app.MapGet("/api/agents", (IAgentRegistry registry, AIPlatform.Core.Studio.StudioOptions studio, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TypedResults.Ok(registry.Agents.Select(agent => new AgentResponse(
                agent.Id, agent.Description, agent.Capabilities, agent.ModelId,
                agent.Id == registry.Router.Id, agent.Id == studio.DeveloperAgentId)).ToArray());
        })
        .WithName("GetAgents")
        .WithSummary("List configured agents")
        .WithDescription("Includes the routing agent and all execution agents, without exposing system prompts.");

        app.MapPost("/api/agents/run", async Task<Results<Ok<RunAgentResponse>, ValidationProblem>> (
            RunAgentRequest request, IMissionOrchestrator orchestrator, PlatformConfiguration configuration,
            ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Task) || request.Task.Length > configuration.MaxTaskLength)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["task"] = [$"Provide a non-empty task of at most {configuration.MaxTaskLength} characters."]
                });

            var result = await orchestrator.RunAsync(request.Task, cancellationToken);
            if (result.UsedFallback)
                loggerFactory.CreateLogger("AgentRouting").LogWarning("Routing fallback used: {Reason}", result.Reason);
            return TypedResults.Ok(new RunAgentResponse(
                result.SelectedAgent, result.SelectedModel, result.Reason, result.Response, result.UsedFallback));
        })
        .WithName("RunAgent")
        .WithSummary("Route and execute a mission")
        .WithDescription("Selects a configured execution agent. Generated code is returned as text and is never executed.")
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .ProducesProblem(StatusCodes.Status504GatewayTimeout);
    }
}
