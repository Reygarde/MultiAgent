using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Text.RegularExpressions;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class ExecutionPlanService(
    IWorkspaceStore workspaces, SandboxOptions sandbox, StudioOptions studio,
    IMissionService? missions = null, IContainerProcess? process = null) : IExecutionPlanService
{
    public async Task<ExecutionPlan> GetAsync(Guid id, CancellationToken cancellationToken, int? workflowVersion = null)
    {
        var files = new List<WorkspaceContent>();
        foreach (var file in workspaces.List(id))
            files.Add(await workspaces.ReadAsync(id, file.Path, cancellationToken));
        if (files.Count == 0)
            throw new StudioValidationException("No generated files are available for approval.");
        var mission = missions?.Get(id);
        var templateId = mission?.TemplateId;
        if (templateId is not null && templateId != DiscordTemplate.Id && templateId != ReactTemplate.Id &&
            templateId != TwitchTemplate.Id && templateId != FullStackTemplate.Id)
            throw new StudioValidationException("Unknown project template.");
        var image = sandbox.Image;
        if (templateId is not null)
        {
            if (process is null) throw new InvalidOperationException("Docker image inspection is required for project templates.");
            image = templateId == ReactTemplate.Id ? ReactTemplate.Image :
                templateId == TwitchTemplate.Id ? TwitchTemplate.Image :
                templateId == FullStackTemplate.Id ? FullStackTemplate.Image : DiscordTemplate.Image;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var inspected = await process.RunAsync(["image", "inspect", "--format", "{{.Id}}", image], timeout.Token);
                var imageId = inspected.Output.Trim();
                if (inspected.ExitCode == 0 && Regex.IsMatch(imageId, @"^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant))
                    image = imageId;
            }
            catch (Win32Exception) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        var operations = new List<string>
        {
            "Read-only mount of this mission workspace; copy sources to an ephemeral container directory.",
            "Offline dotnet build Generated.slnx; no network, ports, secrets or Docker socket.",
            "Offline dotnet test tests/App.Tests/App.Tests.csproj with the three pinned test packages.",
            $"At most {studio.MaxCorrectionAttempts} targeted LLM corrections. Modified versions require new human approval in collaborative workflows.",
            "Non-root UID 10001, read-only root, no capabilities, no-new-privileges, 128 processes maximum.",
            "Force-remove each named container on completion, cancellation or timeout. No execution on Windows."
        };
        if (templateId == DiscordTemplate.Id)
            operations.Insert(0, "Discord template v1: Discord.Net.WebSocket 3.20.2 and pinned offline xUnit packages. Missing cache prevents verification.");
        if (templateId == TwitchTemplate.Id)
        {
            operations[1] = "Offline dotnet build Generated.slnx --configuration Release; no network, ports, secrets or Docker socket.";
            operations[2] = "Offline dotnet test tests/App.Tests/App.Tests.csproj --configuration Release with exact cached xUnit packages.";
            operations.Insert(0, "Twitch template v1: native .NET EventSub/Helix; no runtime packages, credentials or live Twitch connection. xunit [2.9.3], xunit.runner.visualstudio [3.1.4], Microsoft.NET.Test.Sdk [17.14.1].");
        }
        if (templateId == ReactTemplate.Id)
        {
            operations[1] = "Offline TypeScript tsc --noEmit, then Vite production build using fixed trusted configuration.";
            operations[2] = "Offline Vitest with jsdom and React Testing Library; require a nonempty consistent passing JSON report.";
            operations.Insert(0, "React template v1: exact approved package.json and package-lock.json; preinstalled dependencies only. No npm install or project scripts.");
        }
        if (templateId == FullStackTemplate.Id)
        {
            operations[1] = "Offline .NET Release build and xUnit, TypeScript, Vite, Vitest, then real API-client HTTP integration on container loopback.";
            operations[2] = "Node and .NET dependencies are preinstalled and locked; --network=none, no host ports, and temporary SQLite only.";
            operations[3] = "Stop on any validation failure; preserve diagnostics and request human review. No automatic LLM correction.";
            operations.Insert(0, "Full-stack template v1: immutable shared OpenAPI contract; exact React lockfile and Microsoft.Data.Sqlite 10.0.15.");
        }
        if (templateId == FullStackTemplate.Id && mission?.Workflow is { } workflow)
        {
            var runner = new DockerSandboxRunner(sandbox, workspaces, process!);
            var fileHashes = files.OrderBy(file => file.Path, StringComparer.Ordinal).Select(file =>
                new ExecutionPlanFile(file.Path, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Content))))).ToArray();
            var stages = new[] { "BackendBuild", "BackendTests", "TypeScript", "FrontendBuild", "FrontendTests", "Integration" }
                .Select(stage => new ExecutionPlanStage(stage,
                    runner.CreateArguments("plan-container", "plan-workspace", stage, image, templateId, fileHashes))).ToArray();
            var canonical = new CanonicalExecutionPlan(1, id, workflowVersion ?? workflow.Version, templateId,
                fileHashes,
                image, sandbox.Enabled, sandbox.MemoryMegabytes, sandbox.CpuCount, sandbox.TimeoutSeconds,
                workflow.Agents.Single(agent => agent.Enabled && agent.Role == "Test").TimeoutSeconds,
                false, operations.ToArray(), stages);
            var fingerprint = canonical.ComputeFingerprint();
            return new(fingerprint, image, sandbox.MemoryMegabytes, sandbox.CpuCount,
                sandbox.TimeoutSeconds, 0, operations, canonical);
        }
        var content = JsonSerializer.Serialize(new { files, Image = image, sandbox.MemoryMegabytes,
            sandbox.CpuCount, sandbox.TimeoutSeconds, studio.MaxCorrectionAttempts, operations });
        if (templateId == ReactTemplate.Id)
            content += "\n" + JsonSerializer.Serialize(new[]
            {
                ReactBuildProfile.Command("TypeScript"), ReactBuildProfile.Command("Build"), ReactBuildProfile.Command("Tests")
            });
        if (templateId == TwitchTemplate.Id)
            content += "\n" + JsonSerializer.Serialize(new[]
            {
                DockerSandboxRunner.DotNetCommand("Build", true), DockerSandboxRunner.DotNetCommand("Tests", true)
            });
        if (templateId == FullStackTemplate.Id)
            content += "\n" + JsonSerializer.Serialize(new[]
            {
                FullStackBuildProfile.Command("BackendBuild"), FullStackBuildProfile.Command("BackendTests"),
                FullStackBuildProfile.Command("TypeScript"), FullStackBuildProfile.Command("FrontendBuild"),
                FullStackBuildProfile.Command("FrontendTests"), FullStackBuildProfile.Command("Integration")
            });
        if (templateId == FullStackTemplate.Id)
            content += "\n" + JsonSerializer.Serialize(
                new DockerSandboxRunner(sandbox, workspaces, process!).CreateArguments(
                    "plan-container", "plan-workspace", "Integration", image, templateId));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new(hash, image, sandbox.MemoryMegabytes, sandbox.CpuCount,
            sandbox.TimeoutSeconds, studio.MaxCorrectionAttempts, operations);
    }
}
