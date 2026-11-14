using AIPlatform.Core.Studio;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AIPlatform.API;

public static class StudioEndpoints
{
    public static void MapStudio(this WebApplication app)
    {
        app.MapGet("/api/missions/{id:guid}/workflow", Results<Ok<WorkflowResponse>, NotFound> (
            Guid id, IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return missions.Get(id)?.Workflow is { } flow
                ? TypedResults.Ok(WorkflowResponse.From(flow)) : TypedResults.NotFound();
        }).WithName("GetMissionWorkflow").WithSummary("Read persisted workflow steps and shared results")
            .WithDescription("Legacy missions have no workflow and return 404. Excludes raw prompts and source contents.");

        app.MapGet("/api/missions/{id:guid}/revisions/{version:int}",
            Results<Ok<RevisionComparisonResponse>, NotFound> (
                Guid id, int version, IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var mission = missions.Get(id);
            if (mission is null || mission.Deleted || mission.Workflow is not { } flow)
                return TypedResults.NotFound();
            var revisions = flow.Revisions ?? [];
            var proposals = WorkflowResponse.GetProposals(flow);
            var history = revisions.Select(item => (item.Version, item.Files, Proposed: false, Review: (ReviewReport?)null))
                .Concat(proposals.Where(item => revisions.All(revision => revision.Version != item.Version))
                    .Select(item => (item.Version, item.Files, Proposed: true, item.Review)))
                .OrderBy(item => item.Version).ToArray();
            var index = Array.FindIndex(history, item => item.Version == version);
            if (index < 0) return TypedResults.NotFound();
            var current = history[index];
            var prior = history.Take(index).Where(item => current.Proposed || !item.Proposed).ToArray();
            var before = (prior.Length > 0 ? prior[^1].Files : []).ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal);
            var after = current.Files.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal);
            var changes = before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Where(path => before.GetValueOrDefault(path) != after.GetValueOrDefault(path))
                .Select(path => new RevisionFileChangeResponse(path, before.GetValueOrDefault(path), after.GetValueOrDefault(path)))
                .ToArray();
            return TypedResults.Ok(new RevisionComparisonResponse(version, prior.Length > 0 ? prior[^1].Version : null,
                changes, current.Proposed, current.Review));
        }).WithName("GetRevisionComparison").WithSummary("Compare revisions and correction proposals")
            .WithDescription("Returns changed files and their before/after content, including unapplied proposals; deleted projects return 404.");

        app.MapPost("/api/missions/{id:guid}/resume", Results<Accepted, NotFound, Conflict> (
            Guid id, IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (missions.Get(id) is null) return TypedResults.NotFound();
            return missions.Resume(id) ? TypedResults.Accepted($"/api/missions/{id}") : TypedResults.Conflict();
        }).WithName("ResumeMission").WithSummary("Explicitly resume an interrupted collaborative mission")
            .WithDescription("Retains prior results, discards prior approvals and rereviews existing files. Verified, deleted and legacy missions cannot resume.");

        app.MapPost("/api/missions/{id:guid}/revalidate",
            async Task<Results<Ok<ExecutionPlanResponse>, NotFound, Conflict>> (
                Guid id, IMissionService missions, IWorkspaceStore workspaces, IExecutionPlanService plans,
                ISandboxRunner sandbox, CancellationToken token) =>
        {
            var mission = missions.Get(id);
            if (mission is null) return TypedResults.NotFound();
            if (mission.Status is not (MissionStatus.GeneratedUnverified or MissionStatus.AwaitingApproval) ||
                mission.TemplateId != "fullstack-react-dotnet" || mission.Workflow?.Revisions?.LastOrDefault() is not { } revision)
                return TypedResults.Conflict();
            if (!(await sandbox.GetAvailabilityAsync(id, token)).Available)
                return TypedResults.Conflict();
            var plan = await plans.GetAsync(id, token, mission.Workflow.Version + 1);
            var files = workspaces.List(id).Where(file => file.Path != "Generated.slnx").ToArray();
            if (files.Length != revision.Files.Count ||
                files.Any(file => revision.Files.All(expected => expected.Path != file.Path)))
                return TypedResults.Conflict();
            foreach (var file in files)
                if ((await workspaces.ReadAsync(id, file.Path, token)).Content !=
                    revision.Files.Single(expected => expected.Path == file.Path).Content)
                    return TypedResults.Conflict();
            if (plan.Fingerprint != (await plans.GetAsync(id, token, mission.Workflow.Version + 1)).Fingerprint)
                return TypedResults.Conflict();
            return missions.PrepareRevalidation(id, mission.UpdatedAt, plan)
                ? TypedResults.Ok(ExecutionPlanResponse.From(plan)) : TypedResults.Conflict();
        }).WithName("PrepareMissionRevalidation").WithSummary("Prepare a new full-stack validation plan without executing it")
            .WithDescription("Requires identical reviewed sources and an available offline image. Preserves prior evidence and corrections, discards approval, and waits for new human consent.");

        app.MapPost("/api/missions/{id:guid}/manual-backend-revision",
            async Task<Results<Ok<ExecutionPlanResponse>, NotFound, Conflict>> (
                Guid id, ManualBackendRevisionRequest request, IMissionService missions,
                IManualRevisionService revisions, CancellationToken token) =>
        {
            if (missions.Get(id) is null) return TypedResults.NotFound();
            if (request.Files is null || string.IsNullOrWhiteSpace(request.Fingerprint) ||
                request.Files.Any(file => file is null || string.IsNullOrWhiteSpace(file.Path) ||
                    string.IsNullOrWhiteSpace(file.Content)))
                throw new StudioValidationException("A current fingerprint and backend replacements are required.");
            var plan = await revisions.ApplyAsync(id, request.Fingerprint, ManualRevisionRole.Backend,
                request.Files.Select(file => new GeneratedFile(file.Path, file.Content)).ToArray(), token);
            return plan is null ? TypedResults.Conflict() : TypedResults.Ok(ExecutionPlanResponse.From(plan));
        }).WithName("ApplyManualBackendRevision")
            .WithSummary("Apply an explicitly reviewed full-stack backend revision")
            .WithDescription("Accepts only editable backend files and a current fingerprint. Never executes code, calls an agent or approves Docker.");

        app.MapPost("/api/missions/{id:guid}/manual-frontend-revision",
            async Task<Results<Ok<ExecutionPlanResponse>, NotFound, Conflict>> (
                Guid id, ManualFrontendRevisionRequest request, IMissionService missions,
                IManualRevisionService revisions, CancellationToken token) =>
        {
            if (missions.Get(id) is null) return TypedResults.NotFound();
            if (request.Files is null || string.IsNullOrWhiteSpace(request.Fingerprint) ||
                request.Files.Any(file => file is null || string.IsNullOrWhiteSpace(file.Path) ||
                    string.IsNullOrWhiteSpace(file.Content)))
                throw new StudioValidationException("A current fingerprint and frontend replacements are required.");
            var plan = await revisions.ApplyAsync(id, request.Fingerprint, ManualRevisionRole.Frontend,
                request.Files.Select(file => new GeneratedFile(file.Path, file.Content)).ToArray(), token);
            return plan is null ? TypedResults.Conflict() : TypedResults.Ok(ExecutionPlanResponse.From(plan));
        }).WithName("ApplyManualFrontendRevision")
            .WithSummary("Apply an explicitly reviewed full-stack frontend revision")
            .WithDescription("Accepts only editable frontend files and a current fingerprint. Never executes code, calls an agent or approves Docker.");

        app.MapPut("/api/workspaces/{id:guid}", Results<Ok<MissionResponse>, NotFound> (
            Guid id, UpdateProjectRequest request, IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return missions.RenameProject(id, request.Name, request.Description)
                ? TypedResults.Ok(MissionResponse.From(missions.Get(id)!)) : TypedResults.NotFound();
        }).WithName("UpdateProject").WithSummary("Rename a generated project")
            .WithDescription("Name at most 80 characters; description at most 1000. Does not change source or approval fingerprints.")
            .ProducesProblem(400);

        app.MapDelete("/api/workspaces/{id:guid}", Results<NoContent, NotFound, Conflict> (
            Guid id, IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (missions.Get(id) is null) return TypedResults.NotFound();
            return missions.DeleteProject(id) ? TypedResults.NoContent() : TypedResults.Conflict();
        }).WithName("DeleteProject").WithSummary("Delete a terminal mission's isolated project sources")
            .WithDescription("Never deletes an active or approval-waiting workspace. Retains mission audit metadata, logs and workflow.");

        app.MapPost("/api/missions", (CreateMissionRequest request, IMissionService missions, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var mission = missions.Submit(new(request.Task ?? "", request.Kind, request.AgentId,
                request.ApproveSandboxExecution, request.TemplateId));
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
            var plan = await plans.GetAsync(id, token);
            if (mission.TemplateId == "fullstack-react-dotnet" &&
                (mission.Workflow?.Revisions?.LastOrDefault() is not { ExecutionPlan: { } saved } ||
                 saved.Fingerprint != plan.Fingerprint || saved.Canonical?.WorkflowVersion != mission.Workflow.Version ||
                 saved.Canonical.ComputeFingerprint() != plan.Fingerprint))
                return TypedResults.Conflict();
            return TypedResults.Ok(ExecutionPlanResponse.From(plan));
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
            if (request.Approved && mission.Workflow is { } flow &&
                (flow.Revisions?.LastOrDefault() is not { } revision ||
                 revision.Version != flow.Version || revision.Fingerprint != plan.Fingerprint))
                return TypedResults.Conflict();
            if (request.Approved && mission.TemplateId == "fullstack-react-dotnet" &&
                (mission.Workflow?.Revisions?.LastOrDefault()?.ExecutionPlan is not { Canonical: { } canonical } saved ||
                 saved.Fingerprint != plan.Fingerprint || canonical.WorkflowVersion != mission.Workflow.Version ||
                 canonical.ComputeFingerprint() != plan.Fingerprint))
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

        app.MapGet("/api/workspaces/{id:guid}/analysis",
            async Task<Results<Ok<ProjectAnalysisResponse>, NotFound, Conflict>> (
                Guid id, IWorkspaceAnalysis workspaceAnalysis, CancellationToken token) =>
        {
            var result = await workspaceAnalysis.AnalyzeAsync(id, token);
            return result.Status switch
            {
                WorkspaceAnalysisStatus.NotFound => TypedResults.NotFound(),
                WorkspaceAnalysisStatus.Conflict => TypedResults.Conflict(),
                WorkspaceAnalysisStatus.Available when result.Analysis is not null =>
                    TypedResults.Ok(ProjectAnalysisResponse.From(result.Analysis)),
                _ => throw new InvalidOperationException("Invalid workspace analysis result.")
            };
        }).WithName("AnalyzeWorkspace").WithSummary("Analyze an existing isolated workspace statically")
            .WithDescription("Does not execute scripts, inspect external directories, call Ollama, or persist the report.")
            .ProducesProblem(400);

        app.MapGet("/api/workspaces/{id:guid}/export",
            async Task<Results<FileContentHttpResult, NotFound, Conflict>> (
                Guid id, IMissionService missions, IProjectExport exports, HttpContext context, CancellationToken token) =>
        {
            var mission = missions.Get(id);
            if (mission is null || !mission.HasWorkspace || mission.Deleted) return TypedResults.NotFound();
            if (mission.Status is MissionStatus.Queued or MissionStatus.Running) return TypedResults.Conflict();
            var archive = await exports.CreateAsync(id, token);
            var current = missions.Get(id);
            if (current is null || current.Deleted || current.UpdatedAt != mission.UpdatedAt)
                return TypedResults.Conflict();
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.File(archive, "application/zip", $"aiplatform-{id:N}.zip");
        }).WithName("ExportWorkspace").WithSummary("Download eligible project sources as a ZIP")
            .WithDescription("Excludes non-source files and blocks suspected credentials; inspect the archive before sharing.")
            .ProducesProblem(400);

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
