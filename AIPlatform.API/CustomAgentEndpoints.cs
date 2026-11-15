using AIPlatform.Core.Studio;
using AIPlatform.API.Services;
using AIPlatform.Infrastructure.Studio;
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

        app.MapPost("/api/custom-agents/{id:guid}/conversations/{conversationId:guid}/proposals/{proposalId:guid}/native-outcome",
            async Task<Results<Ok<CustomAgentConversationResponse>, NotFound, ProblemHttpResult>> (
                Guid id, Guid conversationId, Guid proposalId, RecordExternalProposalOutcomeRequest request,
                ICustomAgentService agents, DesktopSession desktop, CancellationToken token) =>
        {
            if (!desktop.Enabled)
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Native external-folder outcomes are available only in AIPlatform Studio Desktop.");
            var conversation = await agents.RecordExternalProposalOutcomeAsync(
                id, conversationId, proposalId, request.BackupId, request.Restored, token);
            return conversation is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentConversationResponse.From(conversation));
        }).WithName("RecordExternalCustomAgentProposalOutcome")
            .WithSummary("Persist a completed native external-file apply or restore")
            .WithDescription("Records only the result and recovery identifier. File access is performed exclusively by the Tauri desktop process.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
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

        app.MapGet("/api/custom-agent-teams", (ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(agents.ListTeams().Select(CustomAgentTeamResponse.From).ToArray());
        }).WithName("ListCustomAgentTeams").WithSummary("List saved custom-agent teams")
            .WithDescription("Returns teams separately from system-agent mission workflows.");

        app.MapPost("/api/custom-agent-teams",
            async Task<Created<CustomAgentTeamResponse>> (
                CustomAgentTeamRequest request, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var team = agents.SaveTeam(null, new(request.Name, request.Members))
                ?? throw new InvalidOperationException("The new custom-agent team could not be saved.");
            var response = CustomAgentTeamResponse.From(team);
            return TypedResults.Created($"/api/custom-agent-teams/{team.Id}", response);
        }).WithName("CreateCustomAgentTeam").WithSummary("Save a Planner, Developer, and Reviewer team")
            .WithDescription("Each role uses the selected custom agent's own system instructions and installed Ollama model.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPut("/api/custom-agent-teams/{id:guid}",
            Results<Ok<CustomAgentTeamResponse>, NotFound> (
                Guid id, CustomAgentTeamRequest request, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var team = agents.SaveTeam(id, new(request.Name, request.Members));
            return team is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentTeamResponse.From(team));
        }).WithName("UpdateCustomAgentTeam").WithSummary("Update a saved custom-agent team")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapDelete("/api/custom-agent-teams/{id:guid}",
            Results<NoContent, NotFound> (Guid id, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return agents.DeleteTeam(id) ? TypedResults.NoContent() : TypedResults.NotFound();
        }).WithName("DeleteCustomAgentTeam").WithSummary("Delete a saved custom-agent team")
            .WithDescription("Existing collaboration run history remains in local storage.");

        app.MapPost("/api/custom-agent-teams/{id:guid}/projects",
            async Task<Results<Created<MissionResponse>, NotFound>> (
                Guid id, CreateCustomAgentProjectRequest request, ICustomAgentService agents,
                IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!agents.ListTeams().Any(team => team.Id == id))
                return TypedResults.NotFound();
            if (!Enum.TryParse<CustomAgentProjectType>(request.ProjectType, true, out var projectType) ||
                !Enum.IsDefined(projectType))
                throw new StudioValidationException("Project type must be React or DotNet.");
            var mission = await missions.CreateCustomAgentProjectAsync(request.Name, projectType, token);
            return TypedResults.Created($"/api/missions/{mission.Id}", MissionResponse.From(mission));
        }).WithName("CreateCustomAgentProject").WithSummary("Create a Studio project for a custom-agent team")
            .WithDescription("Creates a trusted React/Vite or .NET 10 scaffold without queuing system agents or applying generated changes.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        app.MapGet("/api/studio/projects/{id:guid}/local-application",
            IResult (Guid id, IMissionService missions, ILocalCommandExecutor commands, DesktopSession desktop) =>
        {
            if (!desktop.Enabled || !commands.Available)
                return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Local applications are available only in AIPlatform Studio Desktop on Windows.");
            var mission = missions.Get(id);
            if (mission is null || !TryGetLocalProjectType(mission, out var projectType))
                return TypedResults.NotFound();
            return TypedResults.Ok(LocalApplicationResponse.From(commands.GetApplication(id, projectType)));
        }).WithName("GetStudioLocalApplication").WithSummary("Get the in-memory local application status")
            .WithDescription("Reports only a server process owned by this Desktop backend; no server is restarted after a backend restart.");

        app.MapPost("/api/studio/projects/{id:guid}/local-application/start",
            async Task<IResult> (
                Guid id, StartLocalApplicationRequest request, IMissionService missions,
                ILocalCommandExecutor commands, DesktopSession desktop, CancellationToken token) =>
        {
            if (!desktop.Enabled || !commands.Available)
                return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Local applications are available only in AIPlatform Studio Desktop on Windows.");
            var mission = missions.Get(id);
            if (mission is null || !TryGetLocalProjectType(mission, out var projectType))
                return TypedResults.NotFound();
            if (!request.ExecutionAuthorized)
                return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Explicit authorization to execute this trusted local project is required.");
            var application = await commands.StartApplicationAsync(id, projectType, true, token);
            return TypedResults.Ok(LocalApplicationResponse.From(application));
        }).WithName("StartStudioLocalApplication").WithSummary("Start a trusted Studio development server")
            .WithDescription("Starts only the fixed npm run dev or dotnet run command in the selected workspace. Project scripts can execute arbitrary local code; Job Objects and timeouts are process controls, not security isolation.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost("/api/studio/projects/{id:guid}/local-application/stop",
            async Task<IResult> (
                Guid id, IMissionService missions, ILocalCommandExecutor commands,
                DesktopSession desktop, CancellationToken token) =>
        {
            if (!desktop.Enabled || !commands.Available)
                return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Local applications are available only in AIPlatform Studio Desktop on Windows.");
            var mission = missions.Get(id);
            if (mission is null || !TryGetLocalProjectType(mission, out var projectType))
                return TypedResults.NotFound();
            var application = await commands.StopApplicationAsync(id, token) ??
                commands.GetApplication(id, projectType);
            return TypedResults.Ok(LocalApplicationResponse.From(application));
        }).WithName("StopStudioLocalApplication").WithSummary("Stop the owned local development server");

        app.MapGet("/api/custom-agent-teams/{id:guid}/runs",
            Results<Ok<CustomAgentCollaborationRunResponse[]>, NotFound> (
                Guid id, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!agents.ListTeams().Any(team => team.Id == id))
                return TypedResults.NotFound();
            return TypedResults.Ok(agents.ListCollaborationRuns(id)
                .Select(CustomAgentCollaborationRunResponse.From).ToArray());
        }).WithName("ListCustomAgentCollaborationRuns").WithSummary("List saved collaboration runs");

        app.MapGet("/api/custom-agent-teams/{id:guid}/runs/{runId:guid}",
            Results<Ok<CustomAgentCollaborationRunResponse>, NotFound> (
                Guid id, Guid runId, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var run = agents.GetCollaborationRun(id, runId);
            return run is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentCollaborationRunResponse.From(run));
        }).WithName("GetCustomAgentCollaborationRun").WithSummary("Get a collaboration run and its contributions");

        app.MapPost("/api/custom-agent-teams/{id:guid}/runs",
            Results<Accepted<CustomAgentCollaborationRunResponse>, NotFound> (
                Guid id, StartCustomAgentCollaborationRequest request,
                ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (request.ExternalFiles?.Any(file => file is null) == true)
                throw new StudioValidationException("External workspace snapshots cannot contain null files.");
            var run = agents.StartCollaboration(id, request.Task, request.Workspace,
                request.ExternalFiles?.Select(file => new CustomAgentWorkspaceFile(file.Path, file.Content)).ToArray(),
                request.Mode, request.WorkspaceAuthorized, request.ExecutionAuthorized);
            return run is null
                ? TypedResults.NotFound()
                : TypedResults.Accepted($"/api/custom-agent-teams/{id}/runs/{run.Id}",
                    CustomAgentCollaborationRunResponse.From(run));
        }).WithName("StartCustomAgentCollaboration").WithSummary("Start a sequential custom-agent collaboration")
            .WithDescription("Uses the existing Planner, Developer, Reviewer workflow. Autonomous mode is restricted to Studio projects and Desktop local-execution consent.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost("/api/custom-agent-teams/{id:guid}/runs/{runId:guid}/cancel",
            Results<NoContent, NotFound, Conflict> (
                Guid id, Guid runId, ICustomAgentService agents, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var run = agents.GetCollaborationRun(id, runId);
            if (run is null)
                return TypedResults.NotFound();
            return agents.CancelCollaboration(id, runId)
                ? TypedResults.NoContent()
                : TypedResults.Conflict();
        }).WithName("CancelCustomAgentCollaboration").WithSummary("Cancel a queued or running collaboration");

        app.MapPost("/api/custom-agent-teams/{id:guid}/runs/{runId:guid}/decision",
            async Task<Results<Ok<CustomAgentCollaborationRunResponse>, NotFound>> (
                Guid id, Guid runId, DecideCustomAgentCollaborationRequest request,
                ICustomAgentService agents, CancellationToken token) =>
        {
            var run = await agents.DecideCollaborationProposalAsync(id, runId, request.Approved, token);
            return run is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentCollaborationRunResponse.From(run));
        }).WithName("DecideCustomAgentCollaborationProposal")
            .WithSummary("Apply or refuse a completed Studio collaboration proposal")
            .WithDescription("Applying is restricted to Studio workspaces and revalidates the workspace before writing.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost("/api/custom-agent-teams/{id:guid}/runs/{runId:guid}/restore",
            async Task<Results<Ok<CustomAgentCollaborationRunResponse>, NotFound, ProblemHttpResult>> (
                Guid id, Guid runId, ICustomAgentService agents, DesktopSession desktop, CancellationToken token) =>
        {
            if (!desktop.Enabled)
                return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Autonomous restore is available only in AIPlatform Studio Desktop.");
            var run = await agents.RestoreAutonomousChangesAsync(id, runId, token);
            return run is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentCollaborationRunResponse.From(run));
        }).WithName("RestoreAutonomousCustomAgentRun")
            .WithSummary("Restore Studio files changed by an autonomous run")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost("/api/custom-agent-teams/{id:guid}/runs/{runId:guid}/native-outcome",
            async Task<Results<Ok<CustomAgentCollaborationRunResponse>, NotFound, ProblemHttpResult>> (
                Guid id, Guid runId, RecordExternalProposalOutcomeRequest request,
                ICustomAgentService agents, DesktopSession desktop, CancellationToken token) =>
        {
            if (!desktop.Enabled)
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Native external-folder outcomes are available only in AIPlatform Studio Desktop.");
            var run = await agents.RecordExternalCollaborationOutcomeAsync(
                id, runId, request.BackupId, request.Restored, token);
            return run is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(CustomAgentCollaborationRunResponse.From(run));
        }).WithName("RecordNativeCustomAgentCollaborationOutcome")
            .WithSummary("Persist an external-folder apply or restore performed by Tauri")
            .WithDescription("The Tauri process performs all file access using its selected-folder authorization.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static bool TryGetLocalProjectType(MissionSnapshot mission, out CustomAgentProjectType projectType)
    {
        projectType = mission.TemplateId switch
        {
            AgentReactTemplate.Id => CustomAgentProjectType.React,
            AgentDotNetTemplate.Id => CustomAgentProjectType.DotNet,
            _ => (CustomAgentProjectType)(-1)
        };
        return mission.HasWorkspace && !mission.Deleted && !mission.Archived && Enum.IsDefined(projectType);
    }
}
