using AIPlatform.Core.Studio;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AIPlatform.API;

public static class StudioEndpoints
{
    public static void MapStudio(this WebApplication app)
    {
        app.MapPost("/api/missions", (CreateMissionRequest request, IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var mission = missions.Submit(new(request.Task ?? "", request.Kind, request.AgentId, request.ApproveSandboxExecution));
            return TypedResults.Accepted($"/api/missions/{mission.Id}", MissionResponse.From(mission));
        }).WithName("CreateMission").WithSummary("Queue a Studio mission")
            .WithDescription("Returns immediately with a mission identifier and Location for polling.")
            .ProducesValidationProblem().ProducesProblem(429);

        app.MapGet("/api/missions", (IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(missions.List().Select(MissionResponse.From).ToArray());
        }).WithName("ListMissions").WithSummary("List persisted Studio missions")
            .WithDescription("Includes real events, final answers and isolated verification logs.");

        app.MapGet("/api/missions/{id:guid}", Results<Ok<MissionResponse>, NotFound> (
            Guid id, IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var mission = missions.Get(id);
            return mission is null ? TypedResults.NotFound() : TypedResults.Ok(MissionResponse.From(mission));
        }).WithName("GetMission").WithSummary("Poll a mission's state")
            .WithDescription("Poll real execution states including AwaitingApproval, Verified, BuildFailed and TestsFailed.");

        app.MapGet("/api/missions/{id:guid}/execution-plan",
            async Task<Results<Ok<ExecutionPlanResponse>, NotFound, Conflict>> (
                Guid id, IMissionService missions, IExecutionPlanService plans, CancellationToken token) =>
        {
            var mission = missions.Get(id);
            if (mission is null)
                return TypedResults.NotFound();
            if (mission.Status != MissionStatus.AwaitingApproval)
                return TypedResults.Conflict();
            return TypedResults.Ok(ExecutionPlanResponse.From(await plans.GetAsync(id, token)));
        }).WithName("GetMissionExecutionPlan").WithSummary("Review this mission's bounded execution plan")
            .WithDescription("Includes the workspace/configuration fingerprint, offline image, limits and allowed operations.");

        app.MapPost("/api/missions/{id:guid}/approval",
            async Task<Results<Accepted, NotFound, Conflict>> (
                Guid id, DecideMissionApprovalRequest request, IMissionService missions,
                IExecutionPlanService plans, CancellationToken token) =>
        {
            var mission = missions.Get(id);
            if (mission is null)
                return TypedResults.NotFound();
            if (mission.Status != MissionStatus.AwaitingApproval)
                return TypedResults.Conflict();
            var plan = await plans.GetAsync(id, token);
            if (!string.Equals(request.Fingerprint, plan.Fingerprint, StringComparison.Ordinal))
                return TypedResults.Conflict();
            return missions.DecideApproval(id, request.Approved, plan.Fingerprint)
                ? TypedResults.Accepted($"/api/missions/{id}") : TypedResults.Conflict();
        }).WithName("DecideMissionApproval").WithSummary("Authorize or refuse the reviewed offline workflow")
            .WithDescription("A stale plan returns 409. Approval covers only this workspace, bounded corrections, build and tests.");

        app.MapPost("/api/missions/{id:guid}/cancel", Results<Accepted, NotFound, Conflict> (
            Guid id, IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (missions.Get(id) is null)
                return TypedResults.NotFound();
            return missions.Cancel(id) ? TypedResults.Accepted($"/api/missions/{id}") : TypedResults.Conflict();
        }).WithName("CancelMission").WithSummary("Request mission cancellation")
            .WithDescription("Cancels queued or active work. A terminal mission returns 409.");

        app.MapGet("/api/missions/{id:guid}/events", Results<Ok<MissionEventResponse[]>, NotFound> (
            Guid id, int? after, IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var mission = missions.Get(id);
            return mission is null ? TypedResults.NotFound() : TypedResults.Ok(
                mission.Events.Where(item => item.Sequence > (after ?? 0)).Select(MissionEventResponse.From).ToArray());
        }).WithName("GetMissionEvents").WithSummary("Read real mission events")
            .WithDescription("Optional after sequence filters events already received by the client.");

        app.MapGet("/api/missions/{id:guid}/verification", Results<Ok<VerificationResponse[]>, NotFound> (
            Guid id, IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var mission = missions.Get(id);
            return mission is null ? TypedResults.NotFound() :
                TypedResults.Ok(mission.Verification.Select(VerificationResponse.From).ToArray());
        }).WithName("GetMissionVerification").WithSummary("Get build and test logs")
            .WithDescription("Returns exit codes only when a container command was actually executed.");

        app.MapGet("/api/workspaces", (IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(missions.List().Where(item => item.HasWorkspace).Select(MissionResponse.From).ToArray());
        }).WithName("ListWorkspaces").WithSummary("List generated project workspaces")
            .WithDescription("Workspace identifiers match their owning mission identifiers.");

        app.MapGet("/api/workspaces/{id:guid}/files", Results<Ok<WorkspaceFileResponse[]>, NotFound> (
            Guid id, IMissionService missions, IWorkspaceStore workspaces, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (missions.Get(id)?.HasWorkspace != true)
                return TypedResults.NotFound();
            return TypedResults.Ok(workspaces.List(id).Select(item => new WorkspaceFileResponse(item.Path, item.Size)).ToArray());
        }).WithName("ListWorkspaceFiles").WithSummary("List generated source files")
            .WithDescription("Rejects symlinks and never exposes files outside the mission workspace.");

        app.MapGet("/api/workspaces/{id:guid}/file", async Task<Results<Ok<WorkspaceContentResponse>, NotFound>> (
            Guid id, string path, IMissionService missions, IWorkspaceStore workspaces, CancellationToken token) =>
        {
            if (missions.Get(id)?.HasWorkspace != true)
                return TypedResults.NotFound();
            try
            {
                var file = await workspaces.ReadAsync(id, path, token);
                return TypedResults.Ok(new WorkspaceContentResponse(file.Path, file.Content));
            }
            catch (FileNotFoundException)
            {
                return TypedResults.NotFound();
            }
        }).WithName("GetWorkspaceFile").WithSummary("Read a generated text file")
            .WithDescription("The path is a relative source path returned by the files endpoint.")
            .ProducesProblem(400);

        app.MapGet("/api/models/local", async (ILocalModelStatus status, CancellationToken token) =>
        {
            var result = await status.GetAsync(token);
            return TypedResults.Ok(new LocalModelsResponse(result.Available, result.Error,
                result.Models.Select(item => new LocalModelResponse(item.Name, item.Installed, item.Loaded, item.InUse)).ToArray()));
        }).WithName("GetLocalModels").WithSummary("Observe configured models in local Ollama")
            .WithDescription("Installed, loaded and in-use states are distinct. Telemetry failures are explicit.");

        app.MapGet("/api/sandbox", async (ISandboxRunner sandbox, CancellationToken token) =>
        {
            var availability = await sandbox.GetAvailabilityAsync(token);
            return TypedResults.Ok(new SandboxResponse(availability.Available, availability.Reason, availability.EngineAvailable));
        }).WithName("GetSandbox").WithSummary("Check offline container isolation")
            .WithDescription("Does not pull an image, install Docker or execute generated code.");
    }
}
