using AIPlatform.API.Services;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AIPlatform.API;

/// <summary>Explicit approval bound to the displayed Preview plan.</summary>
public sealed record ApprovePreviewRequest(string Fingerprint);

public static class PreviewEndpoints
{
    public static void MapPreview(this WebApplication app)
    {
        app.MapPost("/api/missions/{id:guid}/previews",
            async Task<Results<Created<PreviewSession>, NotFound, Conflict>> (
                Guid id, IMissionService missions, PreviewSessionManager sessions, CancellationToken token) =>
        {
            if (missions.Get(id) is null) return TypedResults.NotFound();
            try
            {
                var session = await sessions.PrepareAsync(id, token);
                return TypedResults.Created($"/api/previews/{session.Id}", session);
            }
            catch (StudioValidationException) { return TypedResults.Conflict(); }
        }).AddEndpointFilter(RequireDesktop)
            .WithName("PreparePreview").WithSummary("Prepare a preview plan without executing the application")
            .WithDescription("Requires the authenticated desktop session. Returns 503 until a secure runtime is available.");

        var group = app.MapGroup("/api/previews").AddEndpointFilter(RequireDesktop);
        group.MapGet("/{id:guid}/plan", async Task<Results<Ok<PreviewSession>, NotFound>> (
            Guid id, PreviewSessionManager sessions, CancellationToken token) =>
            await sessions.GetAsync(id, token) is { } session
                ? TypedResults.Ok(session) : TypedResults.NotFound())
            .WithName("GetPreviewPlan").WithSummary("Read the exact preview plan and fingerprint");

        group.MapPost("/{id:guid}/approval", async Task<Results<Ok<PreviewSession>, NotFound, Conflict>> (
            Guid id, ApprovePreviewRequest request, PreviewSessionManager sessions, CancellationToken token) =>
        {
            if (await sessions.GetAsync(id, token) is null) return TypedResults.NotFound();
            try
            {
                if (!await sessions.AuthorizeAsync(id, request.Fingerprint, token)) return TypedResults.Conflict();
                return TypedResults.Ok((await sessions.GetAsync(id, token))!);
            }
            catch (StudioValidationException) { return TypedResults.Conflict(); }
        }).WithName("ApprovePreview").WithSummary("Explicitly approve the current preview plan")
            .WithDescription("The approval is separate from build validation and is usable once.");

        group.MapPost("/{id:guid}/start", async Task<Results<Ok<PreviewSession>, NotFound, Conflict>> (
            Guid id, PreviewSessionManager sessions, CancellationToken token) =>
        {
            if (await sessions.GetAsync(id, token) is null) return TypedResults.NotFound();
            try
            {
                if (!await sessions.StartAsync(id, token)) return TypedResults.Conflict();
                return TypedResults.Ok((await sessions.GetAsync(id, token))!);
            }
            catch (StudioValidationException) { return TypedResults.Conflict(); }
        }).WithName("StartPreview").WithSummary("Start an explicitly approved preview")
            .WithDescription("Rechecks the plan and sources, then consumes approval exactly once.");

        group.MapGet("/{id:guid}", async Task<Results<Ok<PreviewSession>, NotFound>> (
            Guid id, PreviewSessionManager sessions, CancellationToken token) =>
            await sessions.GetAsync(id, token) is { } session
                ? TypedResults.Ok(session) : TypedResults.NotFound())
            .WithName("GetPreview").WithSummary("Read the preview session state");

        group.MapGet("/{id:guid}/logs",
            async Task<Results<Ok<IReadOnlyList<PreviewLogEvent>>, NotFound>> (
                Guid id, PreviewSessionManager sessions, CancellationToken token) =>
            await sessions.GetLogsAsync(id, token) is { } events
                ? TypedResults.Ok(events) : TypedResults.NotFound())
            .WithName("GetPreviewLogs").WithSummary("Read bounded preview lifecycle events")
            .WithDescription("Only predefined lifecycle events are included; application output is not exposed.");

        group.MapPost("/{id:guid}/stop", async Task<Results<Ok<PreviewSession>, NotFound>> (
            Guid id, PreviewSessionManager sessions, CancellationToken token) =>
        {
            if (!await sessions.StopAsync(id, token)) return TypedResults.NotFound();
            return TypedResults.Ok((await sessions.GetAsync(id, token))!);
        }).WithName("StopPreview").WithSummary("Stop a preview and confirm cleanup");
    }

    private static async ValueTask<object?> RequireDesktop(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        var desktop = context.HttpContext.RequestServices.GetRequiredService<DesktopSession>();
        if (!desktop.Enabled) return TypedResults.Problem(statusCode: 403, title: "Preview requires an authenticated desktop session.");
        if (request.Headers.TryGetValue("Origin", out var origin) && !DesktopSession.IsAllowedOrigin(origin.ToString()))
            return TypedResults.Problem(statusCode: 403, title: "This origin is not allowed.");
        if (!desktop.Authorize(request.Headers.Authorization))
            return TypedResults.Problem(statusCode: 401, title: "The desktop session token is required.");
        return await next(context);
    }
}
