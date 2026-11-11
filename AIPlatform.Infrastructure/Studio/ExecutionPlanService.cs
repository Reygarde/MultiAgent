using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class ExecutionPlanService(
    IWorkspaceStore workspaces, SandboxOptions sandbox, StudioOptions studio) : IExecutionPlanService
{
    public async Task<ExecutionPlan> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var files = new List<WorkspaceContent>();
        foreach (var file in workspaces.List(id))
            files.Add(await workspaces.ReadAsync(id, file.Path, cancellationToken));
        if (files.Count == 0)
            throw new StudioValidationException("No generated files are available for approval.");
        var operations = new[]
        {
            "Read-only mount of this mission workspace; copy sources to an ephemeral container directory.",
            "Offline dotnet build Generated.slnx; no network, ports, secrets or Docker socket.",
            "Offline dotnet test tests/App.Tests/App.Tests.csproj with the three pinned test packages.",
            $"At most {studio.MaxCorrectionAttempts} targeted LLM corrections, each revalidated, followed by new containers.",
            "Non-root UID 10001, read-only root, no capabilities, no-new-privileges, 128 processes maximum.",
            "Force-remove each named container on completion, cancellation or timeout. No execution on Windows."
        };
        var content = JsonSerializer.Serialize(new { files, sandbox.Image, sandbox.MemoryMegabytes,
            sandbox.CpuCount, sandbox.TimeoutSeconds, studio.MaxCorrectionAttempts, operations });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new(hash, sandbox.Image, sandbox.MemoryMegabytes, sandbox.CpuCount,
            sandbox.TimeoutSeconds, studio.MaxCorrectionAttempts, operations);
    }
}
