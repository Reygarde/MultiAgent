using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed record SandboxOptions
{
    public bool Enabled { get; init; }
    public string Image { get; init; } = "aiplatform-sandbox:net10";
    public int TimeoutSeconds { get; init; } = 120;
    public int MemoryMegabytes { get; init; } = 2048;
    public int CpuCount { get; init; } = 2;

    public void Validate()
    {
        if (!Regex.IsMatch(Image, @"^[a-z0-9][a-z0-9./:_-]{1,150}(@sha256:[a-f0-9]{64})?$", RegexOptions.CultureInvariant) ||
            TimeoutSeconds is < 5 or > 600 || MemoryMegabytes is < 512 or > 4096 || CpuCount is < 1 or > 4)
            throw new ArgumentException("Invalid sandbox image or resource limits.");
    }
}

public sealed class DockerSandboxRunner(
    SandboxOptions options, IWorkspaceStore workspaces, IContainerProcess process,
    IMissionService? missions = null, IExecutionPlanService? plans = null,
    ISandboxImageTrust? imageTrust = null) : ISandboxRunner
{
    private bool cleanupFaulted;

    public async Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
        => await GetAvailabilityAsync(options.Image, cancellationToken);

    public async Task<SandboxAvailability> GetAvailabilityAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var templateId = missions?.Get(workspaceId)?.TemplateId;
        if (templateId is not null && templateId != DiscordTemplate.Id && templateId != ReactTemplate.Id &&
            templateId != MissionReactTemplate.Id &&
            templateId != TwitchTemplate.Id && templateId != FullStackTemplate.Id)
            throw new StudioValidationException("Unknown project template.");
        return await GetAvailabilityAsync(templateId is ReactTemplate.Id or MissionReactTemplate.Id ? ReactTemplate.Image :
            templateId == DiscordTemplate.Id ? DiscordTemplate.Image :
            templateId == TwitchTemplate.Id ? TwitchTemplate.Image :
            templateId == FullStackTemplate.Id ? FullStackTemplate.Image : options.Image, cancellationToken);
    }

    internal async Task<SandboxAvailability> GetAvailabilityAsync(string imageName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref cleanupFaulted))
            return new(false, "Container cleanup could not be confirmed. Sandbox disabled until inspection and server restart.");
        if (!options.Enabled)
            return new(false, "Sandbox disabled in configuration. No Docker probe, build or test was executed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var info = await process.RunAsync(["info", "--format", "{{.OSType}}"], timeout.Token);
            if (info.ExitCode != 0 || info.Output.Trim() != "linux")
                return new(false, "Docker Linux engine unavailable. No build or test will run on the host.");
            var image = await process.RunAsync(["image", "inspect", "--format", "{{.Id}}", imageName], timeout.Token);
            if (image.ExitCode != 0)
                return new(false, $"The required offline sandbox image {imageName} is not installed. No image is downloaded automatically.", true);
            if (imageTrust is not null)
            {
                if (imageTrust.GetExpectedImageId(imageName) is null)
                    return new(false, $"The provisioning manifest has not been verified for sandbox image {imageName}.", true);
                if (!imageTrust.IsTrustedImage(imageName, image.Output.Trim()))
                    return new(false, $"The installed image {imageName} does not match the verified Studio provisioning manifest.", true);
            }
            return new(true, "Offline Docker image available. Explicit human approval is still required per mission.", true);
        }
        catch (Win32Exception)
        {
            return new(false, "Docker is not installed. Automatic build and tests are unavailable; host execution is forbidden.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "Docker availability check timed out. Build and tests are unavailable.");
        }
    }

    public async Task<VerificationResult> RunAsync(Guid workspaceId, string stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled)
            throw new InvalidOperationException("Sandbox execution is disabled.");
        if (Volatile.Read(ref cleanupFaulted))
            throw new InvalidOperationException("Sandbox disabled after an unconfirmed container cleanup.");
        var mission = missions?.Get(workspaceId);
        var fullStack = mission?.TemplateId == FullStackTemplate.Id;
        if (stage is not ("TypeScript" or "Build" or "Tests") &&
            !(fullStack && FullStackBuildProfile.IsStage(stage)))
            throw new ArgumentException("Unknown validation stage.", nameof(stage));
        if (stage == "TypeScript" && mission?.TemplateId is not (ReactTemplate.Id or MissionReactTemplate.Id or FullStackTemplate.Id))
            throw new StudioValidationException("TypeScript validation requires a React or full-stack profile.");
        if (mission?.TemplateId is not null && mission.TemplateId != DiscordTemplate.Id && mission.TemplateId != ReactTemplate.Id &&
            mission.TemplateId != MissionReactTemplate.Id &&
            mission.TemplateId != TwitchTemplate.Id && mission.TemplateId != FullStackTemplate.Id)
            throw new StudioValidationException("Unknown project template.");
        if (stage != "TypeScript" && FullStackBuildProfile.IsStage(stage) &&
            mission?.TemplateId != FullStackTemplate.Id)
            throw new StudioValidationException("This validation stage requires the full-stack profile.");
        var image = options.Image;
        IReadOnlyList<ExecutionPlanFile>? approvedFiles = null;
        if (mission?.TemplateId is not null)
        {
            var plan = await (plans ?? throw new InvalidOperationException("Template execution plan is required."))
                .GetAsync(workspaceId, cancellationToken);
            if (!mission.SandboxApproved || mission.ApprovalFingerprint != plan.Fingerprint ||
                !Regex.IsMatch(plan.Image, @"^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant))
                throw new StudioValidationException("The template image or workspace is unavailable or not approved.");
            if (fullStack && (mission.Workflow?.Revisions?.LastOrDefault()?.ExecutionPlan is not { Canonical: { } canonical } saved ||
                saved.Fingerprint != plan.Fingerprint || canonical.WorkflowVersion != mission.Workflow.Version ||
                canonical.ComputeFingerprint() != plan.Fingerprint || canonical.AutomaticCorrections ||
                !canonical.Stages.Any(item => item.Name == stage)))
                throw new StudioValidationException("The approved canonical plan for this workflow version is unavailable or changed.");
            if (fullStack)
            {
                var actualCanonical = plan.Canonical ?? throw new StudioValidationException("Canonical plan missing.");
                approvedFiles = actualCanonical.Files;
                var expected = actualCanonical.Stages.Single(item => item.Name == stage).Arguments;
                if (!expected.SequenceEqual(CreateArguments("plan-container", "plan-workspace", stage, plan.Image,
                    mission.TemplateId, approvedFiles), StringComparer.Ordinal))
                    throw new StudioValidationException("The container command differs from the approved canonical plan.");
            }
            image = plan.Image;
            if (imageTrust is not null && !imageTrust.IsTrustedImage(
                    mission.TemplateId is ReactTemplate.Id or MissionReactTemplate.Id ? ReactTemplate.Image :
                    mission.TemplateId == DiscordTemplate.Id ? DiscordTemplate.Image :
                    mission.TemplateId == TwitchTemplate.Id ? TwitchTemplate.Image : FullStackTemplate.Image, image))
                throw new StudioValidationException("The approved image no longer matches the verified Studio provisioning manifest.");
        }
        var directory = workspaces.GetDirectory(workspaceId);
        SafePaths.RejectLinks(directory);
        // Enumerate all entries before mounting; refuse junctions even in unused subdirectories.
        workspaces.List(workspaceId);
        return await RunContainerAsync(directory, stage, image, mission?.TemplateId, approvedFiles, cancellationToken);
    }

    internal async Task<VerificationResult> RunIndependentReactAsync(string directory, string stage, string image,
        IReadOnlyList<ExecutionPlanFile> files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!options.Enabled || Volatile.Read(ref cleanupFaulted))
            throw new StudioValidationException("Sandbox execution is disabled or previous cleanup could not be confirmed.");
        if (!Regex.IsMatch(image, @"\Asha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant))
            throw new StudioValidationException("Independent React validation requires a pinned Docker image ID.");
        if (imageTrust is not null && !imageTrust.IsTrustedImage(ReactTemplate.Image, image))
            throw new StudioValidationException("The React image does not match the verified Studio provisioning manifest.");
        SafePaths.RejectLinks(directory);
        return await RunContainerAsync(directory, stage, image, MissionReactTemplate.Id, files, token);
    }

    private async Task<VerificationResult> RunContainerAsync(string directory, string stage, string image,
        string? templateId, IReadOnlyList<ExecutionPlanFile>? approvedFiles, CancellationToken cancellationToken)
    {
        if (directory.Contains(',') || directory.Contains('\n') || directory.Contains('\r'))
            throw new StudioValidationException("The configured workspace root is not a valid Docker mount path.");
        var name = "aiplatform-" + Guid.NewGuid().ToString("N");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        ContainerProcessResult result;
        try
        {
            result = await process.RunAsync(CreateArguments(name, directory, stage, image, templateId, approvedFiles), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(stage, "Failed", null, $"Container exceeded the {options.TimeoutSeconds}s limit; verification did not complete.");
        }
        finally
        {
            // Killing docker.exe does not guarantee the daemon has stopped its container.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var removed = await process.RunAsync(["rm", "--force", name], cleanup.Token);
                if (removed.ExitCode != 0 && !removed.Output.Contains("No such container", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Docker container cleanup failed: " + removed.Output);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or Win32Exception)
            {
                Volatile.Write(ref cleanupFaulted, true);
                throw new IOException("Container cleanup could not be confirmed. Sandbox disabled until inspection and restart.", exception);
            }
        }

        var warnings = CountDiagnostics("Warning", result.Output);
        var errors = CountDiagnostics("Error", result.Output);
        var reactCounts = stage == "Tests" && templateId is (ReactTemplate.Id or MissionReactTemplate.Id)
            ? ReactBuildProfile.TestCounts(result.Output) : null;
        var fullStackCounts = templateId == FullStackTemplate.Id &&
            stage is ("FrontendTests" or "Integration")
                ? FullStackBuildProfile.TestCounts(result.Output) : null;
        var testStage = stage is "Tests" or "BackendTests" or "FrontendTests" or "Integration";
        var counts = testStage ? templateId switch
        {
            ReactTemplate.Id or MissionReactTemplate.Id => reactCounts is { } react ? new TestCounts(react.Total, react.Failed) : null,
            FullStackTemplate.Id when stage == "BackendTests" => GetTestCounts(result.Output),
            FullStackTemplate.Id => fullStackCounts is { } full ? new TestCounts(full.Total, full.Failed) : null,
            _ => GetTestCounts(result.Output)
        } : null;
        if (testStage && result.ExitCode == 0 && counts is not { Total: > 0, Failed: 0 })
        {
            return new(stage, "Failed", result.ExitCode,
                result.Output + "\nNo valid non-empty passing test report was produced; tests are not validated.",
                WarningCount: warnings, ErrorCount: errors);
        }
        return new(stage, result.ExitCode == 0 ? "Succeeded" : "Failed", result.ExitCode, result.Output,
            counts?.Total, WarningCount: warnings, ErrorCount: errors, FailedTests: counts?.Failed);
    }

    public IReadOnlyList<string> CreateArguments(string name, string directory, string stage, string? image = null,
        string? templateId = null, IReadOnlyList<ExecutionPlanFile>? approvedFiles = null)
    {
        if (templateId == FullStackTemplate.Id && !FullStackBuildProfile.IsStage(stage) ||
            templateId != FullStackTemplate.Id && stage is not ("Build" or "Tests") &&
            !(stage == "TypeScript" && templateId is (ReactTemplate.Id or MissionReactTemplate.Id)))
            throw new ArgumentException("Unauthorized container stage.", nameof(stage));
        var command = templateId is ReactTemplate.Id or MissionReactTemplate.Id ? ReactBuildProfile.Command(stage) :
            templateId == FullStackTemplate.Id ? FullStackBuildProfile.Command(stage) :
            DotNetCommand(stage, templateId == TwitchTemplate.Id);
        if (approvedFiles is not null)
        {
            if (approvedFiles.Count == 0 || approvedFiles.Any(file => templateId switch
                {
                    FullStackTemplate.Id => file.Path != "Generated.slnx" && !FullStackTemplate.IsPath(file.Path),
                    MissionReactTemplate.Id => !MissionReactTemplate.IsEditable(file.Path) &&
                        !MissionReactTemplate.Files.Any(baseline => baseline.Path == file.Path),
                    _ => true
                }))
                throw new StudioValidationException("Invalid approved file manifest.");
            var manifest = string.Join("\n", approvedFiles.Select(file => $"{file.Sha256.ToLowerInvariant()}  {file.Path}")) + "\n";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest));
            command = $"test -z \"$(find . -type l -print -quit)\" && " +
                $"test \"$(find . -type f | wc -l)\" -eq {approvedFiles.Count} && " +
                $"printf '%s' '{encoded}' | base64 -d | sha256sum -c --status && " + command;
        }
        return
        [
            "run", "--pull=never", "--name", name,
            "--network=none", "--read-only", "--cap-drop=ALL",
            "--security-opt=no-new-privileges:true",
            "--cpus", options.CpuCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--memory", $"{options.MemoryMegabytes}m", "--memory-swap", $"{options.MemoryMegabytes}m",
            "--pids-limit", "128", "--ulimit", "nofile=1024:1024",
            "--user", "10001:10001",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=128m,uid=10001,gid=10001",
            "--tmpfs", "/work:rw,nosuid,nodev,size=512m,uid=10001,gid=10001",
            "--mount", $"type=bind,source={directory},target=/source,readonly",
            "--workdir", "/work", "--env", "HOME=/tmp", "--env", "DOTNET_CLI_HOME=/tmp",
            "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "--env", "DOTNET_NOLOGO=1",
            "--env", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1", "--env", "NUGET_PACKAGES=/opt/nuget",
            "--env", "DOTNET_PROCESSOR_COUNT=2",
            "--entrypoint", "/bin/sh", image ?? options.Image, "-c", "cp -R /source/. /work/ && " + command
        ];
    }

    internal static string DotNetCommand(string stage, bool release)
    {
        var configuration = release ? " --configuration Release" : "";
        return stage == "Build"
            ? $"dotnet build Generated.slnx{configuration} --nologo -p:RestoreSources=/opt/empty-feed -p:NuGetAudit=false -p:RestorePackagesPath=/opt/nuget"
            : $"dotnet test tests/App.Tests/App.Tests.csproj{configuration} --nologo -p:RestoreSources=/opt/empty-feed -p:NuGetAudit=false " +
              "-p:RestorePackagesPath=/opt/nuget --logger 'trx;LogFileName=verification.trx' --results-directory /work/results " +
              "> /work/test.log 2>&1; result=$?; head -c 24000 /work/test.log; " +
              "if [ $(wc -c < /work/test.log) -gt 24000 ]; then printf '\\n[Test log truncated at 24000 bytes.]\\n'; fi; " +
              "printf '\\nAIPLATFORM_TRX_BEGIN\\n'; " +
              "if [ -f /work/results/verification.trx ]; then cat /work/results/verification.trx; fi; " +
              "printf '\\nAIPLATFORM_TRX_END\\n'; exit \"$result\"";
    }

    private static int? CountDiagnostics(string kind, string output)
    {
        var matches = Regex.Matches(output, $@"(\d+) {kind}\(s\)", RegexOptions.CultureInvariant);
        return matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, out var count) ? count : null;
    }

    private sealed record TestCounts(int Total, int Failed);

    private static TestCounts? GetTestCounts(string output)
    {
        const string begin = "\nAIPLATFORM_TRX_BEGIN\n";
        const string end = "\nAIPLATFORM_TRX_END\n";
        var start = output.LastIndexOf(begin, StringComparison.Ordinal);
        var finish = output.LastIndexOf(end, StringComparison.Ordinal);
        if (start < 0 || finish <= start)
            return null;
        var report = output[(start + begin.Length)..finish].TrimStart('\uFEFF');
        try
        {
            using var reader = XmlReader.Create(new StringReader(report),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
            var document = XDocument.Load(reader);
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var root = document.Root;
            if (root is null || root.Name != ns + "TestRun")
                return null;
            var summary = root.Element(ns + "ResultSummary");
            var counters = summary?.Element(ns + "Counters");
            if (!int.TryParse(counters?.Attribute("total")?.Value, out var total) ||
                !int.TryParse(counters?.Attribute("passed")?.Value, out var passed) ||
                !int.TryParse(counters?.Attribute("executed")?.Value, out var executed) ||
                !int.TryParse(counters?.Attribute("failed")?.Value, out var failed) ||
                total <= 0 || total != executed || passed + failed != total ||
                (failed == 0 && summary?.Attribute("outcome")?.Value != "Completed"))
                return null;
            var results = root.Element(ns + "Results")?.Elements(ns + "UnitTestResult").ToArray() ?? [];
            if (results.Length != total || results.Count(item => item.Attribute("outcome")?.Value == "Passed") != passed ||
                results.Count(item => item.Attribute("outcome")?.Value == "Failed") != failed)
                return null;
            return new(total, failed);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
