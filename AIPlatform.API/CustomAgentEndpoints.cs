using AIPlatform.Core.Studio;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AIPlatform.API;

public static class CustomAgentEndpoints
{
    public static void MapCustomAgents(this WebApplication app)
    {
        app.MapGet("/api/custom-agents", (ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(agents.ListAgents().Select(CustomAgentResponse.From).ToArray());
        }).WithName("ListCustomAgents").WithSummary("List user-created AI agents")
            .WithDescription("Returns persisted custom agents separately from the system agents used by workflows.");

        app.MapGet("/api/custom-agents/models", async (ICustomAgentService agents, CancellationToken token) =>
        {
            var models = await agents.ListInstalledModelsAsync(token);
            return TypedResults.Ok(models);
        }).WithName("ListCustomAgentModels").WithSummary("List models actually installed in local Ollama")
            .WithDescription("Returns live Ollama tags only. This endpoint never downloads or invents a model.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        app.MapGet("/api/custom-agents/{id:guid}",
            Results<Ok<CustomAgentResponse>, NotFound> (Guid id, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var agent = agents.GetAgent(id);
            return agent is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentResponse.From(agent));
        }).WithName("GetCustomAgent").WithSummary("Get a user-created AI agent")
            .WithDescription("Does not include workflow system agents.");

        app.MapPost("/api/custom-agents",
            async Task<Created<CustomAgentResponse>> (
                CustomAgentRequest request, ICustomAgentService agents, CancellationToken token) =>
        {
            var agent = await agents.CreateAgentAsync(
                new(request.Name, request.Description, request.SystemPrompt, request.ModelName), token);
            var response = CustomAgentResponse.From(agent);
            return TypedResults.Created($"/api/custom-agents/{agent.Id}", response);
        }).WithName("CreateCustomAgent").WithSummary("Create a persisted custom AI agent")
            .WithDescription("Accepts only an Ollama model currently installed on this device.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        app.MapPut("/api/custom-agents/{id:guid}",
            async Task<Results<Ok<CustomAgentResponse>, NotFound>> (
                Guid id, CustomAgentRequest request, ICustomAgentService agents, CancellationToken token) =>
        {
            var agent = await agents.UpdateAgentAsync(
                id, new(request.Name, request.Description, request.SystemPrompt, request.ModelName), token);
            return agent is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentResponse.From(agent));
        }).WithName("UpdateCustomAgent").WithSummary("Update a custom AI agent")
            .WithDescription("Validates the selected model against the live Ollama model list.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/custom-agents/{id:guid}/duplicate",
            Results<Created<CustomAgentResponse>, NotFound> (
                Guid id, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var agent = agents.DuplicateAgent(id);
            if (agent is null)
                return TypedResults.NotFound();
            return TypedResults.Created($"/api/custom-agents/{agent.Id}", CustomAgentResponse.From(agent));
        }).WithName("DuplicateCustomAgent").WithSummary("Duplicate a custom AI agent")
            .WithDescription("Copies configuration only; conversation history is not copied.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapDelete("/api/custom-agents/{id:guid}",
            Results<NoContent, NotFound> (Guid id, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return agents.DeleteAgent(id) ? TypedResults.NoContent() : TypedResults.NotFound();
        }).WithName("DeleteCustomAgent").WithSummary("Delete a custom AI agent")
            .WithDescription("Deletes that agent and its conversations. The client must confirm before this action.");

        app.MapGet("/api/custom-agents/{id:guid}/conversations",
            Results<Ok<CustomAgentConversationSummaryResponse[]>, NotFound> (
                Guid id, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (agents.GetAgent(id) is null)
                return TypedResults.NotFound();
            return TypedResults.Ok(agents.ListConversations(id)
                .Select(CustomAgentConversationSummaryResponse.From).ToArray());
        }).WithName("ListCustomAgentConversations").WithSummary("List conversation history for a custom agent")
            .WithDescription("Returns titles and timestamps; message contents are loaded separately.");

        app.MapPost("/api/custom-agents/{id:guid}/conversations",
            Results<Created<CustomAgentConversationResponse>, NotFound> (
                Guid id, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var conversation = agents.CreateConversation(id);
            if (conversation is null)
                return TypedResults.NotFound();
            return TypedResults.Created(
                $"/api/custom-agents/{id}/conversations/{conversation.Id}",
                CustomAgentConversationResponse.From(conversation));
        }).WithName("CreateCustomAgentConversation").WithSummary("Start a new custom-agent conversation")
            .WithDescription("Conversation history is retained locally and starts empty.");

        app.MapGet("/api/custom-agents/{id:guid}/conversations/{conversationId:guid}",
            Results<Ok<CustomAgentConversationResponse>, NotFound> (
                Guid id, Guid conversationId, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var conversation = agents.GetConversation(id, conversationId);
            return conversation is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentConversationResponse.From(conversation));
        }).WithName("GetCustomAgentConversation").WithSummary("Load a custom-agent conversation")
            .WithDescription("Returns persisted user and assistant messages in chronological order.");

        app.MapPost("/api/custom-agents/{id:guid}/conversations/{conversationId:guid}/workspace",
            Results<Ok<CustomAgentConversationResponse>, NotFound> (
                Guid id, Guid conversationId, SetCustomAgentWorkspaceRequest request,
                ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var conversation = agents.SetWorkspace(id, conversationId, request.Workspace);
            return conversation is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentConversationResponse.From(conversation));
        }).WithName("SetCustomAgentWorkspace").WithSummary("Select or clear a conversation workspace")
            .WithDescription("Persists a Studio project or external-folder display name. External file snapshots are never persisted.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost("/api/custom-agents/{id:guid}/conversations/{conversationId:guid}/messages",
            async Task<Results<Ok<CustomAgentConversationResponse>, NotFound>> (
                Guid id, Guid conversationId, SendCustomAgentMessageRequest request,
                ICustomAgentService agents, CancellationToken token) =>
        {
            if (request.ExternalFiles?.Any(file => file is null) == true)
                    throw new StudioValidationException("External workspace snapshots cannot contain null files.");
            var conversation = await agents.SendMessageAsync(
                    id, conversationId, request.Message, token,
                    request.ExternalFiles?.Select(file => new CustomAgentWorkspaceFile(file.Path, file.Content)).ToArray());
            return conversation is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentConversationResponse.From(conversation));
        }).WithName("SendCustomAgentMessage").WithSummary("Send a message to Ollama through a custom agent")
            .WithDescription("Uses the agent instructions and saved context. Workspace access is limited to selected text snapshots; no command or shell tools are exposed.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        app.MapPost("/api/custom-agents/{id:guid}/conversations/{conversationId:guid}/proposals/{proposalId:guid}/decision",
            async Task<Results<Ok<CustomAgentConversationResponse>, NotFound>> (
                Guid id, Guid conversationId, Guid proposalId, DecideCustomAgentProposalRequest request,
                ICustomAgentService agents, CancellationToken token) =>
        {
            var conversation = await agents.DecideProposalAsync(
                id, conversationId, proposalId, request.Approved, token);
            return conversation is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentConversationResponse.From(conversation));
        }).WithName("DecideCustomAgentProposal").WithSummary("Apply or refuse a proposed Studio project change")
            .WithDescription("Applying is restricted to eligible Studio projects, revalidates the workspace snapshot, and records the edit in mission history.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapDelete("/api/custom-agents/{id:guid}/conversations/{conversationId:guid}",
            Results<NoContent, NotFound> (
                Guid id, Guid conversationId, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return agents.DeleteConversation(id, conversationId)
                ? TypedResults.NoContent()
                : TypedResults.NotFound();
        }).WithName("DeleteCustomAgentConversation").WithSummary("Delete a custom-agent conversation")
            .WithDescription("The client must confirm before deleting conversation history.");
    }
}
