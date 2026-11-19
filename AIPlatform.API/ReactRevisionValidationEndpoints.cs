using AIPlatform.Infrastructure.Studio;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AIPlatform.API;

/// <summary>Latest technical report, if a validation has been started.</summary>
public sealed record ReactRevisionValidationReportResponse(ReactRevisionValidationReport? Report);

public static class ReactRevisionValidationEndpoints
{
    public static void MapReactRevisionValidation(this WebApplication app)
    {
        var routes = app.MapGroup("/api/missions/{id:guid}/react-revision-validation");
        routes.MapGet("/report", async Task<Results<Ok<ReactRevisionValidationReportResponse>, NotFound>> (
            Guid id, IReactRevisionValidation validations, CancellationToken token) =>
        {
            try { return TypedResults.Ok(new ReactRevisionValidationReportResponse(await validations.GetReportAsync(id, token))); }
            catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        }).WithName("GetReactRevisionValidationReport").WithSummary("Read the latest independent React validation report");

        routes.MapPost("/prepare", async Task<Results<Ok<ReactRevisionValidationPreparation>, NotFound, ProblemHttpResult>> (
            Guid id, IReactRevisionValidation validations, CancellationToken token) =>
        {
            try { return TypedResults.Ok(await validations.PrepareAsync(id, token)); }
            catch (KeyNotFoundException) { return TypedResults.NotFound(); }
            catch (ReactRevisionValidationConflictException error) { return Conflict(error); }
        }).WithName("PrepareReactRevisionValidation").WithSummary("Prepare an independent React validation without running Docker")
            .ProducesProblem(StatusCodes.Status409Conflict);

        routes.MapPost("/authorize", async Task<Results<Ok<ReactRevisionValidationReport>, NotFound, ProblemHttpResult>> (
            Guid id, AuthorizeReactRevisionValidationRequest request, IReactRevisionValidation validations,
            IHostApplicationLifetime lifetime, CancellationToken token) =>
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.ApplicationStopping);
            try { return TypedResults.Ok(await validations.AuthorizeAsync(id, request, cancel.Token)); }
            catch (KeyNotFoundException) { return TypedResults.NotFound(); }
            catch (ReactRevisionValidationConflictException error) { return Conflict(error); }
        }).WithName("AuthorizeReactRevisionValidation")
            .WithSummary("Explicitly authorize and run the exact prepared React revision in the existing offline sandbox")
            .ProducesProblem(StatusCodes.Status409Conflict);

        routes.MapPost("/correction-plan", async Task<Results<Ok<ReactCorrectionPlan>, NotFound, ProblemHttpResult>> (
            Guid id, PrepareReactCorrectionRequest request, IReactRevisionCorrection corrections, CancellationToken token) =>
        {
            try { return TypedResults.Ok(await corrections.PrepareAsync(id, request, token)); }
            catch (KeyNotFoundException) { return TypedResults.NotFound(); }
            catch (ReactRevisionValidationConflictException error) { return Conflict(error); }
        }).WithName("PrepareReactRevisionCorrection").WithSummary("Check correction scope, prompt sizes and budget without generation")
            .ProducesProblem(400).ProducesProblem(409);

        routes.MapPost("/correct", async Task<Results<Created<ReactCorrectionPublication>, NotFound, ProblemHttpResult>> (
            Guid id, StartReactCorrectionRequest request, IReactRevisionCorrection corrections,
            IHostApplicationLifetime lifetime, CancellationToken token) =>
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.ApplicationStopping);
            try
            {
                var published = await corrections.StartAsync(id, request, cancel.Token);
                return TypedResults.Created($"/api/missions/{id}/revisions/{published.Version}", published);
            }
            catch (KeyNotFoundException) { return TypedResults.NotFound(); }
            catch (ReactRevisionValidationConflictException error) { return Conflict(error); }
        }).WithName("CorrectReactRevision").WithSummary("Publish the complete Developer correction without running Docker")
            .ProducesProblem(400).ProducesProblem(409);

        var drafts = app.MapGroup("/api/missions/{id:guid}/react-draft-repair");
        drafts.MapGet("/", async Task<Results<Ok<ReactDraftRepairStatus>, NotFound>> (
            Guid id, IReactDraftRepair repairs, CancellationToken token) =>
        {
            try
            {
                var status = await repairs.StatusAsync(id, token);
                return status is null ? TypedResults.NotFound() : TypedResults.Ok(status);
            }
            catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        }).WithName("GetReactDraftRepair").WithSummary("Read durable draft correction progress without generation");
        drafts.MapPost("/prepare", async Task<Results<Ok<ReactDraftRepairPlan>, NotFound>> (
            Guid id, PrepareReactDraftRepairRequest request, IReactDraftRepair repairs, CancellationToken token) =>
        {
            try { return TypedResults.Ok(await repairs.PrepareAsync(id, request, token)); }
            catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        }).WithName("PrepareReactDraftRepair").WithSummary("Project deterministic repairs and measure Developer/Reviewer calls without generation")
            .ProducesProblem(400);
        drafts.MapPost("/start", async Task<Results<Ok<ReactDraftRepairStatus>, NotFound>> (
            Guid id, StartReactDraftRepairRequest request, IReactDraftRepair repairs,
            IHostApplicationLifetime lifetime, CancellationToken token) =>
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.ApplicationStopping);
            try { return TypedResults.Ok(await repairs.StartAsync(id, request, cancel.Token)); }
            catch (KeyNotFoundException) { return TypedResults.NotFound(); }
        }).WithName("StartReactDraftRepair").WithSummary("Explicitly generate scoped draft corrections; never enqueue, publish or run Docker")
            .ProducesProblem(400);
    }

    private static ProblemHttpResult Conflict(ReactRevisionValidationConflictException error) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict,
            title: "React revision validation unavailable", detail: error.Message);
}
