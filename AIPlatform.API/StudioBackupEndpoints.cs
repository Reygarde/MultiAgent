using AIPlatform.API.Services;
using AIPlatform.Core.Studio;

namespace AIPlatform.API;

public static class StudioBackupEndpoints
{
    public static IEndpointRouteBuilder MapStudioBackup(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/studio-backup").WithTags("Studio backup");

        group.MapPost("/create", async (StudioBackupService backups, CancellationToken token) =>
        {
            try
            {
                var archive = await backups.CreateAsync(token);
                return (IResult)archive;
            }
            catch (StudioBackupException exception)
            {
                return TypedResults.Problem(statusCode: exception.StatusCode, title: exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
            }
        })
            .WithName("CreateStudioBackup")
            .WithSummary("Create a verified, versioned Studio backup")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/inspect", async Task<IResult> (HttpRequest request, StudioBackupService backups, CancellationToken token) =>
        {
            if (request.ContentType is null ||
                !request.ContentType.StartsWith("application/zip", StringComparison.OrdinalIgnoreCase))
                return TypedResults.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType,
                    title: "Upload the backup archive as application/zip.");
            try
            {
                var preview = await backups.InspectAsync(request.Body, request.ContentLength, token);
                return TypedResults.Ok(preview);
            }
            catch (StudioBackupException exception)
            {
                return TypedResults.Problem(statusCode: exception.StatusCode, title: exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
            }
        })
            .WithName("InspectStudioBackup")
            .WithSummary("Verify a backup archive and preview its effects without changing Studio data")
            .Produces<StudioBackupPreview>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        group.MapPost("/restore/{id:guid}", async Task<IResult> (Guid id, StudioBackupRestoreRequest request,
            StudioBackupService backups, CancellationToken token) =>
        {
            try
            {
                return TypedResults.Ok(await backups.RestoreAsync(id, request, token));
            }
            catch (StudioBackupException exception)
            {
                return TypedResults.Problem(statusCode: exception.StatusCode, title: exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
            }
        })
            .WithName("RestoreStudioBackup")
            .WithSummary("Restore a previously verified archive after explicit confirmation")
            .Produces<StudioBackupRestoreResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }
}
