using System.ComponentModel;
using System.Text.RegularExpressions;
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
        if (!Regex.IsMatch(Image, @"^[a-z0-9][a-z0-9./:_-]{1,150}$", RegexOptions.CultureInvariant) ||
            TimeoutSeconds is < 5 or > 600 || MemoryMegabytes is < 512 or > 4096 || CpuCount is < 1 or > 4)
            throw new ArgumentException("Invalid sandbox image or resource limits.");
    }
}

public sealed class DockerSandboxRunner(
    SandboxOptions options, IWorkspaceStore workspaces, IContainerProcess process) : ISandboxRunner
{
    private bool cleanupFaulted;

    public async Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref cleanupFaulted))
            return new(false, "Container cleanup could not be confirmed. Sandbox disabled until inspection and server restart.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var info = await process.RunAsync(["info", "--format", "{{.OSType}}"], timeout.Token);
            if (info.ExitCode != 0 || info.Output.Trim() != "linux")
                return new(false, "Docker Linux engine unavailable. No build or test will run on the host.");
            if (!options.Enabled)
                return new(false, "Sandbox disabled in configuration. Enable only after reviewing the isolation settings.");
            var image = await process.RunAsync(["image", "inspect", "--format", "{{.Id}}", options.Image], timeout.Token);
            return image.ExitCode == 0
                ? new(true, "Offline Docker image available. Explicit human approval is still required per mission.")
                : new(false, "The offline sandbox image is not installed. No image is downloaded automatically.");
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
        if (!options.Enabled)
            throw new InvalidOperationException("Sandbox execution is disabled.");
        if (Volatile.Read(ref cleanupFaulted))
            throw new InvalidOperationException("Sandbox disabled after an unconfirmed container cleanup.");
        if (stage is not ("Build" or "Tests"))
            throw new ArgumentException("Only Build and Tests stages are authorized.", nameof(stage));
        var directory = workspaces.GetDirectory(workspaceId);
        SafePaths.RejectLinks(directory);
        // Enumerate all entries before mounting; refuse junctions even in unused subdirectories.
        workspaces.List(workspaceId);
        if (directory.Contains(',') || directory.Contains('\n') || directory.Contains('\r'))
            throw new StudioValidationException("The configured workspace root is not a valid Docker mount path.");
        var name = "aiplatform-" + Guid.NewGuid().ToString("N");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        ContainerProcessResult result;
        try
        {
            result = await process.RunAsync(CreateArguments(name, directory, stage), timeout.Token);
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

        int? total = null;
        if (stage == "Tests" && result.ExitCode == 0)
        {
            total = GetTestCount(result.Output);
            if (total is null or <= 0)
                return new(stage, "Failed", result.ExitCode,
                    result.Output + "\nNo valid non-empty TRX test result was produced; tests are not validated.");
        }
        return new(stage, result.ExitCode == 0 ? "Succeeded" : "Failed", result.ExitCode, result.Output, total);
    }

    public IReadOnlyList<string> CreateArguments(string name, string directory, string stage)
    {
        if (stage is not ("Build" or "Tests"))
            throw new ArgumentException("Unauthorized container stage.", nameof(stage));
        var command = stage == "Build"
            ? "dotnet build Generated.slnx --nologo -p:RestoreSources=/opt/empty-feed -p:NuGetAudit=false -p:RestorePackagesPath=/opt/nuget"
            : "dotnet test tests/App.Tests/App.Tests.csproj --nologo -p:RestoreSources=/opt/empty-feed -p:NuGetAudit=false " +
              "-p:RestorePackagesPath=/opt/nuget --logger 'trx;LogFileName=verification.trx' --results-directory /work/results " +
              "&& cat /work/results/verification.trx";
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
            "--entrypoint", "/bin/sh", options.Image, "-c", "cp -R /source/. /work/ && " + command
        ];
    }

    private static int? GetTestCount(string output)
    {
        var start = output.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0)
            start = output.IndexOf("<TestRun", StringComparison.Ordinal);
        var end = output.LastIndexOf("</TestRun>", StringComparison.Ordinal);
        if (start < 0 || end < start)
            return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(output[start..(end + "</TestRun>".Length)]),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
            var document = XDocument.Load(reader);
            var counters = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Counters");
            if (!int.TryParse(counters?.Attribute("total")?.Value, out var total) ||
                !int.TryParse(counters.Attribute("passed")?.Value, out var passed) || total != passed)
                return null;
            return total;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
