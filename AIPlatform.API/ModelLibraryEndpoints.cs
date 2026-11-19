using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using AIPlatform.API.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AIPlatform.API;

/// <summary>Explicit local model operation; confirmation is never inferred.</summary>
public sealed record ModelOperationRequest(string Name, bool Confirmed = false);
/// <summary>Checks an exact remote registry tag without downloading weights.</summary>
public sealed record VerifyModelRequest(string Name);
/// <summary>Verified tag and manifest bytes, not a performance or memory guarantee.</summary>
public sealed record VerifiedModelResponse(string Name, long DownloadBytes);
/// <summary>Explicit reset of selection statistics only.</summary>
public sealed record ResetModelStatisticsRequest(bool Confirmed = false);
/// <summary>Non-generative preview of an automatic selection.</summary>
public sealed record PreviewModelRoutingRequest(string Task, string Role, int ContextTokens = 4096);
/// <summary>Overrides recognized specializations; null restores catalogue defaults.</summary>
public sealed record UpdateModelSpecializationsRequest(string Name, string[]? Specializations);
/// <summary>Qualifies only an already resident model; never loads it or requests generation.</summary>
public sealed record QualifyModelRequest(string Name, int ContextTokens, bool Confirmed = false);

public static class ModelLibraryEndpoints
{
    public static void MapModelLibrary(this WebApplication app)
    {
        var group = app.MapGroup("/api/model-library").WithTags("Model library");
        group.MapGet("/", async (int? context, IModelLibraryService service, CancellationToken token) =>
        {
            if (context is < 512 or > ModelContextLimits.Maximum)
                throw new StudioValidationException($"Context must be 512–{ModelContextLimits.Maximum} tokens.");
            return TypedResults.Ok(await service.SnapshotAsync(context ?? 4096, token));
        }).WithName("GetModelLibrary").WithSummary("Local hardware, catalogue and actual Ollama inventory")
            .WithDescription("Read-only, never installs or loads weights. Unknown metadata stays unknown. Catalogue sizes are approximate.");
        group.MapPost("/verify", async (VerifyModelRequest request, IModelLibraryService service, CancellationToken token) =>
            TypedResults.Ok(new VerifiedModelResponse(ModelLibraryService.ValidateName(request.Name),
                await service.VerifyAsync(request.Name, token))))
            .WithName("VerifyOllamaTag").WithSummary("Verify a tag against the public Ollama registry without downloading weights");
        group.MapGet("/downloads", (IModelLibraryService service, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(service.Downloads());
        }).WithName("GetModelDownloads").WithSummary("Get actual Ollama download progress and interrupted operations");
        group.MapGet("/downloads/{id:guid}",
            Results<Ok<ModelDownload>, NotFound> (Guid id, IModelLibraryService service, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var job = service.Downloads().FirstOrDefault(item => item.Id == id);
            return job is null ? TypedResults.NotFound() : TypedResults.Ok(job);
        }).WithName("GetModelDownload").WithSummary("Get one model download");
        group.MapPost("/downloads", Results<Accepted<ModelDownload>, ProblemHttpResult> (
            ModelOperationRequest request, IModelLibraryService service, DesktopSession desktop, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!desktop.Enabled) return TypedResults.Problem(statusCode: 403, title: "Model installation requires Studio Desktop.");
            var job = service.StartDownload(request.Name, request.Confirmed);
            return TypedResults.Accepted($"/api/model-library/downloads/{job.Id}", job);
        }).WithName("InstallOllamaModel").WithSummary("Explicitly install one verified local Ollama tag")
            .WithDescription("Downloads via local Ollama only after confirmation; no RAM-based download ban. May use Internet bandwidth and large disk space.")
            .ProducesProblem(400).ProducesProblem(409);
        group.MapPost("/downloads/{id:guid}/cancel",
            Results<NoContent, NotFound, ProblemHttpResult> (
                Guid id, IModelLibraryService service, DesktopSession desktop, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!desktop.Enabled) return TypedResults.Problem(statusCode: 403, title: "Model cancellation requires Studio Desktop.");
            return service.CancelDownload(id) ? TypedResults.NoContent() : TypedResults.NotFound();
        }).WithName("CancelModelDownload").WithSummary("Cancel a pull; Ollama may retain resumable layers");
        group.MapPost("/delete", async Task<Results<NoContent, ProblemHttpResult>> (
            ModelOperationRequest request, IModelLibraryService service, DesktopSession desktop, CancellationToken token) =>
        {
            if (!desktop.Enabled) return TypedResults.Problem(statusCode: 403, title: "Model deletion requires Studio Desktop.");
            await service.DeleteAsync(request.Name, request.Confirmed, token);
            return TypedResults.NoContent();
        }).WithName("DeleteLibraryModel").WithSummary("Delete a model only after confirmation and dependency checks")
            .WithDescription("Configured or active uses block deletion. Reassign them explicitly in Agents/Teams/workflows first.")
            .ProducesProblem(400).ProducesProblem(409);
        group.MapPut("/specializations", (UpdateModelSpecializationsRequest request, IModelLibraryService service, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(service.SaveSpecializations(request.Name, request.Specializations));
        }).WithName("UpdateModelSpecializations").WithSummary("Persist explicit model specializations or restore catalogue defaults")
            .ProducesProblem(400);
        group.MapPost("/qualification", async (QualifyModelRequest request, IModelLibraryService service, CancellationToken token) =>
            TypedResults.Ok(await service.QualifyAsync(request.Name, request.ContextTokens, request.Confirmed, token)))
            .WithName("QualifyResidentModel").WithSummary("Explicitly record a real resident model's digest, context and memory allocations")
            .WithDescription("Observation only: never loads weights. Unverified or insufficient resources block qualification.")
            .ProducesProblem(400);
        group.MapPost("/qualification/clear", (ModelOperationRequest request, IModelLibraryService service, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!request.Confirmed) throw new StudioValidationException("Explicit qualification removal confirmation required.");
            return TypedResults.Ok(service.ClearQualification(request.Name));
        }).WithName("ClearModelQualification").WithSummary("Remove measured evidence without changing model weights or missions")
            .ProducesProblem(400);
        group.MapPost("/routing/preview", async (PreviewModelRoutingRequest request, IModelRouter router, CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(request.Task) || request.Task.Length > 8000 ||
                request.Role is not ("Planner" or "Developer" or "Reviewer" or "Chat") ||
                request.ContextTokens is < 512 or > ModelContextLimits.Maximum)
                throw new StudioValidationException($"Provide a bounded task, known role and context 512–{ModelContextLimits.Maximum}.");
            return TypedResults.Ok(await router.SelectAsync(request.Task, request.Role, request.ContextTokens, token));
        }).WithName("PreviewModelRouting").WithSummary("Explain a deterministic heuristic choice without calling an LLM");
        group.MapGet("/routing/statistics", (LocalModelRouter router, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(router.Observations().GroupBy(item => new { item.ModelName, item.TaskCategory }).Select(group => new
            {
                group.Key.ModelName, group.Key.TaskCategory, Count = group.Count(),
                GenerationFailures = group.Count(item => item.FailureKind == "Generation"),
                CompilationsPassed = group.Count(item => item.CompilationStatus == "Passed"),
                ValidatedAttempts = group.Count(item => item.Outcome == "Succeeded"),
                ResourceOrInfrastructureFailures = group.Count(item => item.FailureKind is not (null or "Generation")),
                FunctionalSuccessNotImpliedByCompilation = true
            }));
        }).WithName("GetModelRoutingStatistics").WithSummary("Summarize real saved attempts; compilation is not functional verification");
        group.MapPost("/routing/statistics/reset", (ResetModelStatisticsRequest request, IModelRouter router, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!request.Confirmed) throw new StudioValidationException("Explicit statistics reset confirmation required.");
            router.ResetStatistics();
            return TypedResults.NoContent();
        }).WithName("ResetModelRoutingStatistics").WithSummary("Exclude prior observations from ranking without deleting mission history");
    }
}
