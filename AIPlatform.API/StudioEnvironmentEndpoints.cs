using AIPlatform.API.Services;

namespace AIPlatform.API;

public sealed record SandboxProvisioningImportRequest(bool Confirmed, string Fingerprint);

public static class StudioEnvironmentEndpoints
{
    public static void MapStudioEnvironment(this WebApplication app)
    {
        var group = app.MapGroup("/api/environment").WithTags("Environment");

        group.MapGet("", async (StudioEnvironmentService environment, CancellationToken token) =>
            TypedResults.Ok(await environment.GetAsync(token)))
            .WithName("GetStudioEnvironment")
            .WithSummary("Diagnose the local Studio environment")
            .WithDescription("Reads system telemetry and checks installed tools, Ollama, Docker and verified sandbox images. It does not install, download, import or execute project code.");

        group.MapPost("/sandbox-provisioning/verify",
            async (ISandboxProvisioningService provisioning, CancellationToken token) =>
                TypedResults.Ok(await provisioning.VerifyAsync(token)))
            .WithName("VerifySandboxProvisioning")
            .WithSummary("Verify packaged sandbox image hashes")
            .WithDescription("Hashes the bundled offline image archives without importing or executing them.");

        group.MapPost("/sandbox-provisioning/import",
            async (SandboxProvisioningImportRequest request, ISandboxProvisioningService provisioning,
                CancellationToken token) =>
                TypedResults.Ok(await provisioning.ImportAsync(request.Confirmed, request.Fingerprint, token)))
            .WithName("ImportSandboxProvisioning")
            .WithSummary("Import verified sandbox images after confirmation")
            .WithDescription("Requires an explicit confirmation and a verified package fingerprint. It does not run containers or generated code.");
    }
}
