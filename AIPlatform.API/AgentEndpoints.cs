using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
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
                agent.Id == registry.Router.Id, agent.Enabled && (studio.CollaborativeWorkflows
                    ? agent.Role == "Developer" && agent.TaskTypes.Contains("Project") : agent.Id == studio.DeveloperAgentId),
                agent.Name, agent.Role, agent.Enabled, agent.AllowedTools, agent.TaskTypes)).ToArray());
        })
        .WithName("GetAgents")
        .WithSummary("List configured agents")
        .WithDescription("Includes the routing agent and all execution agents, without exposing system prompts.");

        app.MapGet("/api/agents/configuration", (IAgentConfigurationService configuration, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var value = configuration.GetConfiguration();
            return TypedResults.Ok(new AgentConfigurationResponse(value.Agents, value.Models));
        }).WithName("GetAgentConfiguration").WithSummary("Read editable local agent and model settings")
            .WithDescription("Local administration only. Includes system instructions, roles and constrained tool permissions.");

        app.MapPut("/api/agents/configuration", async Task<Results<Ok<AgentConfigurationResponse>, Conflict>> (
            UpdateAgentConfigurationRequest request, IAgentConfigurationService configuration,
            IMissionService missions, MissionExecutionGate gate, CancellationToken token) =>
        {
            if (missions.List().Any(mission => !mission.IsTerminal)) return TypedResults.Conflict();
            await gate.EnterAsync(token);
            try
            {
                if (missions.List().Any(mission => !mission.IsTerminal)) return TypedResults.Conflict();
                configuration.Save(new(request.Agents, request.Models));
                var saved = configuration.GetConfiguration();
                return TypedResults.Ok(new AgentConfigurationResponse(saved.Agents, saved.Models));
            }
            finally { gate.Exit(); }
        }).WithName("UpdateAgentConfiguration").WithSummary("Validate and persist local agent/model configuration")
            .WithDescription("Requires no queued, running or approval-waiting missions. Existing workflows retain their original settings. Never downloads models.")
            .ProducesProblem(400);

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
