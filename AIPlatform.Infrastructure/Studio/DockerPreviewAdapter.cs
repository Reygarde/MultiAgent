using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class DockerPreviewAdapter : IPreviewRuntime
{
    private readonly IContainerProcess process;
    private readonly PreviewProxy? proxy;
    private readonly IControlledPreviewBrowser? browser;
    private readonly HashSet<Guid> browserSessions = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string prefix;
    private readonly string stagingRoot;
    private readonly Dictionary<Guid, string> directories = [];
    private bool cleanupFaulted;

    public DockerPreviewAdapter(IContainerProcess process, StudioOptions studio, PreviewProxy? proxy = null,
        IControlledPreviewBrowser? browser = null)
    {
        this.process = process;
        this.proxy = proxy;
        this.browser = browser;
        var store = SafePaths.ValidateStorageDirectory(studio.DataDirectory);
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(store.ToUpperInvariant())))[..16].ToLowerInvariant();
        prefix = "aiplatform-preview-" + scope + "-";
        stagingRoot = SafePaths.ValidateStorageDirectory(Path.Combine(Path.GetTempPath(), prefix.TrimEnd('-')));
        var storePrefix = store.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var stagingPrefix = stagingRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (stagingPrefix.StartsWith(storePrefix, StringComparison.OrdinalIgnoreCase) ||
            storePrefix.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
            throw new StudioValidationException("Preview staging and Studio storage must be disjoint.");
    }

    // HTTP relay alone cannot prevent direct requests from an ordinary browser.
    public bool IsAvailable => proxy is not null && browser?.IsolationVerified == true;

    public Task<PreviewPlan> CreatePlanAsync(PreviewSourceIdentity sources, CancellationToken cancellationToken) =>
        throw new PreviewUnavailableException("A controlled browser with verified isolation is required before Preview approval.");

    public Task<PreviewReadiness> StartAsync(Guid sessionId, PreviewPlan plan,
        PreviewSourceSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new PreviewUnavailableException("Controlled browser isolation is not available.");
        return StartProxySessionAsync(sessionId, plan, snapshot, cancellationToken);
    }

    public async Task<bool> IsSessionHealthyAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return !browserSessions.Contains(sessionId) || browser?.IsRunning(sessionId) == true; }
        finally { gate.Release(); }
    }

    internal async Task<PreviewReadiness> StartProxySessionAsync(Guid sessionId, PreviewPlan plan,
        PreviewSourceSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (proxy is null || plan.Transport != DockerPreviewHttpTransport.Policy ||
            !plan.Exposures.SequenceEqual(new[] { PreviewProxy.Exposure }))
            throw new StudioValidationException("The Preview proxy transport must be part of the approved plan.");
        var ready = false;
        var containerStarted = false;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(plan.MaximumSessionSeconds);
        try
        {
            var health = await StartContainerAsync(sessionId, plan, snapshot, cancellationToken);
            containerStarted = true;
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (!directories.ContainsKey(sessionId))
                    throw new IOException("The container stopped before proxy startup.");
                var access = await proxy.StartAsync(sessionId, Name(sessionId),
                    expiresAt, cancellationToken);
                if (browser is not null)
                {
                    await browser.OpenAsync(sessionId, access.Address, access.Token, expiresAt, cancellationToken);
                    browserSessions.Add(sessionId);
                }
                ready = true;
                return health with { Url = access.Address };
            }
            finally { gate.Release(); }
        }
        finally
        {
            if (!ready && containerStarted)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await StopAsync(sessionId, cleanup.Token);
            }
        }
    }

    internal async Task<string> ResolveImageAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var engine = await process.RunAsync(["info", "--format", "{{.OSType}}"], timeout.Token);
            if (engine.ExitCode != 0 || engine.Output.Trim() != "linux")
                throw new PreviewUnavailableException("Docker Linux is required.");
            var image = await process.RunAsync(
                ["image", "inspect", "--format", "{{.Id}} {{.Os}} {{.Architecture}}", FullStackTemplate.Image], timeout.Token);
            var fields = image.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (image.ExitCode != 0 || fields.Length != 3 ||
                !Regex.IsMatch(fields[0], "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant) ||
                fields[1] != "linux" || fields[2] != "amd64")
                throw new PreviewUnavailableException("The offline Linux amd64 full-stack image is missing or invalid.");
            return fields[0];
        }
        catch (Win32Exception)
        {
            throw new PreviewUnavailableException("Docker CLI is unavailable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PreviewUnavailableException("Docker image inspection timed out.");
        }
    }

    internal async Task<PreviewReadiness> StartContainerAsync(
        Guid sessionId, PreviewPlan plan, PreviewSourceSnapshot snapshot, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cleanupFaulted) throw new PreviewUnavailableException("Previous Docker cleanup is unconfirmed.");
            ValidateSnapshot(plan, snapshot);
            if (directories.ContainsKey(sessionId))
                throw new StudioValidationException("This Docker Preview session already started.");
            if (await ResolveImageAsync(cancellationToken) != plan.ImageDigest)
                throw new StudioValidationException("The Docker image changed after plan approval.");
            var directory = Path.Combine(stagingRoot, sessionId.ToString("N"));
            SafePaths.RejectLinks(directory);
            if (Directory.Exists(directory)) throw new StudioValidationException("Preview staging already exists.");
            directories.Add(sessionId, directory);
            var ready = false;
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(TimeSpan.FromSeconds(plan.StartupTimeoutSeconds));
                var token = startup.Token;
                Directory.CreateDirectory(directory);
                foreach (var file in snapshot.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var path = file.Path == "Generated.slnx"
                        ? Path.Combine(directory, file.Path) : SafePaths.Resolve(directory, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        81920, FileOptions.Asynchronous);
                    await stream.WriteAsync(file.Content.ToArray(), token);
                }
                var args = CreateArguments(sessionId, directory, plan);
                await RequireSuccessAsync(args, token);
                await RequireSuccessAsync(["start", Name(sessionId)], token);
                var health = await process.RunAsync(
                    ["exec", Name(sessionId), "node", "--input-type=module", "-e", HealthCommand], token);
                if (health.ExitCode != 0 || health.Output.Trim() != "AIPLATFORM_PREVIEW_READY")
                    throw new IOException("The backend and frontend did not pass Docker Preview health checks.");
                var state = await process.RunAsync(
                    ["inspect", "--format", "{{.State.Running}}", Name(sessionId)], token);
                if (state.ExitCode != 0 || state.Output.Trim() != "true")
                    throw new IOException("Docker Preview exited during its health checks.");
                ready = true;
                return new(true, true);
            }
            finally
            {
                if (!ready)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await StopCoreAsync(sessionId, cleanup.Token);
                }
            }
        }
        finally { gate.Release(); }
    }

    internal IReadOnlyList<string> CreateArguments(Guid sessionId, string directory, PreviewPlan plan)
    {
        ValidateIsolation(plan);
        if (sessionId == Guid.Empty || Path.GetFullPath(directory) != Path.Combine(stagingRoot, sessionId.ToString("N")) ||
            directory.IndexOfAny([',', '\r', '\n']) >= 0)
            throw new StudioValidationException("Only controlled Preview staging may be mounted.");
        SafePaths.RejectLinks(directory);
        return
        [
            "create", "--pull=never", "--name", Name(sessionId),
            "--label", "org.aiplatform.preview.owner=" + prefix,
            "--network=none", "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges:true",
            "--user", "10001:10001", "--cpus", plan.Isolation.CpuCount.ToString(CultureInfo.InvariantCulture),
            "--memory", $"{plan.Isolation.MemoryMegabytes}m", "--memory-swap", $"{plan.Isolation.MemoryMegabytes}m",
            "--pids-limit", plan.Isolation.PidLimit.ToString(CultureInfo.InvariantCulture), "--ulimit", "nofile=1024:1024",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=128m,uid=10001,gid=10001",
            "--tmpfs", "/work:rw,nosuid,nodev,size=512m,uid=10001,gid=10001",
            "--mount", $"type=bind,source={directory},target=/source,readonly",
            "--workdir", "/work", "--env", "HOME=/tmp", "--env", "DOTNET_CLI_HOME=/tmp",
            "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "--env", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
            "--env", "DOTNET_NOLOGO=1", "--env", "NUGET_PACKAGES=/opt/nuget", "--env", "DOTNET_PROCESSOR_COUNT=2",
            "--env", "LD_LIBRARY_PATH=/opt/nuget/sqlitepclraw.lib.e_sqlite3/2.1.13/runtimes/linux-x64/native",
            "--entrypoint", "/bin/sh", plan.ImageDigest, "-c", StartupCommand(plan)
        ];
    }

    public async Task StopAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await StopCoreAsync(sessionId, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task StopCoreAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty) throw new StudioValidationException("A Preview session ID is required.");
        var completed = false;
        try
        {
            Exception? browserFailure = null;
            if (browser is not null)
            {
                try { await browser.CloseAsync(sessionId, cancellationToken); }
                catch (Exception exception) when (exception is IOException or PreviewUnavailableException or
                                                   OperationCanceledException or InvalidOperationException or Win32Exception)
                { browserFailure = exception; }
            }
            browserSessions.Remove(sessionId);
            if (proxy is not null) await proxy.StopAsync(sessionId, cancellationToken);
            var remaining = await ListContainersAsync(cancellationToken);
            if (remaining.Contains(Name(sessionId), StringComparer.Ordinal))
                await RequireSuccessAsync(["rm", "--force", Name(sessionId)], cancellationToken);
            if ((await ListContainersAsync(cancellationToken)).Contains(Name(sessionId), StringComparer.Ordinal))
                throw new IOException("Docker Preview container removal could not be confirmed.");
            var directory = directories.GetValueOrDefault(sessionId) ?? Path.Combine(stagingRoot, sessionId.ToString("N"));
            DeleteStaging(directory);
            directories.Remove(sessionId);
            if (browserFailure is not null)
                throw new IOException("Docker stopped, but controlled browser cleanup remains unconfirmed.", browserFailure);
            completed = true;
        }
        finally { if (!completed) cleanupFaulted = true; }
    }

    public async Task CleanupOrphansAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cleanupFaulted = true;
            Exception? browserFailure = null;
            if (browser is not null)
            {
                try { await browser.CloseAllAsync(cancellationToken); }
                catch (Exception exception) when (exception is IOException or PreviewUnavailableException or
                                                   OperationCanceledException or InvalidOperationException or Win32Exception)
                { browserFailure = exception; }
            }
            browserSessions.Clear();
            if (proxy is not null) await proxy.StopAllAsync(cancellationToken);
            foreach (var container in await ListContainersAsync(cancellationToken))
                await RequireSuccessAsync(["rm", "--force", container], cancellationToken);
            if ((await ListContainersAsync(cancellationToken)).Length != 0)
                throw new IOException("Orphan Docker Preview containers remain.");
            SafePaths.RejectLinks(stagingRoot);
            if (Directory.Exists(stagingRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
                {
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                        throw new IOException("Unexpected Preview staging directory; manual inspection required.");
                    DeleteStaging(directory);
                }
            }
            directories.Clear();
            if (browserFailure is not null)
                throw new IOException("Docker orphans removed, but browser recovery remains unconfirmed.", browserFailure);
            cleanupFaulted = false;
        }
        finally { gate.Release(); }
    }

    private async Task<string[]> ListContainersAsync(CancellationToken cancellationToken)
    {
        var result = await process.RunAsync(
            ["ps", "--all", "--filter", "label=org.aiplatform.preview.owner=" + prefix, "--format", "{{.Names}}"],
            cancellationToken);
        if (result.ExitCode != 0) throw new IOException("Docker Preview inventory failed.");
        var names = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (names.Any(name => !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _)))
            throw new IOException("Unexpected Docker Preview resource identity.");
        return names;
    }

    private async Task RequireSuccessAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if ((await process.RunAsync(arguments, cancellationToken)).ExitCode != 0)
            throw new IOException("Docker Preview operation failed: " + arguments[0]);
    }

    private string Name(Guid id) => prefix + id.ToString("N");

    private void DeleteStaging(string directory)
    {
        if (Path.GetDirectoryName(directory) != stagingRoot ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            throw new IOException("Refused an unowned staging cleanup path.");
        SafePaths.RejectLinks(directory);
        if (!Directory.Exists(directory)) return;
        // Reject every reparse point before deletion; never follow generated links.
        Scan(directory);
        Directory.Delete(directory, recursive: true);
        static void Scan(string path)
        {
            SafePaths.RejectLinks(path);
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                SafePaths.RejectLinks(child);
                if (Directory.Exists(child)) Scan(child);
            }
        }
    }

    internal static void ValidateSnapshot(PreviewPlan plan, PreviewSourceSnapshot snapshot)
    {
        ValidateIsolation(plan);
        if (snapshot.Identity.MissionId != plan.MissionId || snapshot.Identity.WorkflowVersion != plan.WorkflowVersion ||
            snapshot.Identity.ValidationFingerprint != plan.ValidationFingerprint ||
            !snapshot.Identity.Files.SequenceEqual(plan.SourceFiles) || snapshot.Files.Length != plan.SourceFiles.Length ||
            snapshot.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != snapshot.Files.Length ||
            snapshot.Files.Sum(file => (long)file.Content.Length) > FullStackTemplate.MaxProjectBytes)
            throw new StudioValidationException("Preview snapshot does not match the plan.");
        foreach (var file in snapshot.Files)
            if (!plan.SourceFiles.Any(expected => expected.Path == file.Path &&
                expected.Sha256 == Convert.ToHexString(SHA256.HashData(file.Content.AsSpan()))))
                throw new StudioValidationException("Preview snapshot bytes do not match the plan.");
    }

    private static void ValidateIsolation(PreviewPlan plan)
    {
        _ = plan.ComputeFingerprint();
        if (plan.Image != FullStackTemplate.Image || plan.Isolation.NetworkPolicy != "none" ||
            !plan.Isolation.ReadOnlyRoot || !plan.Isolation.NonRoot || !plan.Isolation.DropCapabilities ||
            !plan.Isolation.NoNewPrivileges || plan.Isolation.CpuCount is < 1 or > 4 ||
            plan.Isolation.MemoryMegabytes is < 512 or > 4096 || plan.Isolation.PidLimit is < 16 or > 128 ||
            plan.MaximumSessionSeconds is < 1 or > 1800 ||
            !(plan.Exposures.IsEmpty && plan.Transport is null ||
              plan.Transport == DockerPreviewHttpTransport.Policy &&
              plan.Exposures.SequenceEqual(new[] { PreviewProxy.Exposure })) ||
            plan.Commands.Length != 1 || plan.Commands[0].Service != "isolated-fullstack" ||
            !plan.Commands[0].Arguments.SequenceEqual(new[] { "/bin/sh", "-c", StartupCommand(plan) }) ||
            plan.TemporaryStorage.Length != 2 ||
            plan.TemporaryStorage[0] != new PreviewTemporaryStorage("work", "/work", "512 MiB tmpfs; removed with container") ||
            plan.TemporaryStorage[1] != new PreviewTemporaryStorage("sqlite", "/tmp", "128 MiB tmpfs; removed with container") ||
            plan.SourceFiles.Any(file => file.Path != "Generated.slnx" && !FullStackTemplate.IsPath(file.Path)))
            throw new StudioValidationException("Unsafe or unsupported Docker Preview configuration.");
    }

    internal static string StartupCommand(PreviewPlan plan)
    {
        var manifest = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            string.Join("\n", plan.SourceFiles.Select(file => $"{file.Sha256.ToLowerInvariant()}  {file.Path}")) + "\n"));
        return "set -eu; cp -R /source/. /work/; " +
            "test -z \"$(find . -type l -print -quit)\"; " +
            $"test \"$(find . -type f | wc -l)\" -eq {plan.SourceFiles.Length}; " +
            $"printf '%s' '{manifest}' | base64 -d | sha256sum -c --status; " +
            FullStackBuildProfile.Command("BackendBuild") + "; " +
            FullStackBuildProfile.Command("FrontendBuild") + "; " +
            "ASPNETCORE_URLS=http://127.0.0.1:5080 ConnectionStrings__Default='Data Source=/tmp/preview.db' " +
            "dotnet /work/backend/Api/bin/Release/net10.0/Api.dll & api=$!; " +
            "/opt/aiplatform/node_modules/.bin/vite preview --config /work/frontend/vite.config.ts " +
            "--host 127.0.0.1 --port 5081 --strictPort & frontend=$!; " +
            "trap 'kill \"$api\" \"$frontend\" 2>/dev/null || true' EXIT; " +
            $"remaining={plan.MaximumSessionSeconds}; " +
            "while [ \"$remaining\" -gt 0 ]; do kill -0 \"$api\"; kill -0 \"$frontend\"; sleep 1; remaining=$((remaining-1)); done";
    }

    private const string HealthCommand = """
        for (let i = 0; i < 120; i++) {
          try {
            const api = await fetch("http://127.0.0.1:5080/api/tasks", {redirect:"error", signal:AbortSignal.timeout(1000)});
            const ui = await fetch("http://127.0.0.1:5081/", {redirect:"error", signal:AbortSignal.timeout(1000)});
            if (api.ok && Array.isArray(await api.json()) && ui.ok && (await ui.text()).includes("<html")) {
              console.log("AIPLATFORM_PREVIEW_READY"); process.exit(0);
            }
          } catch {}
          await new Promise(resolve => setTimeout(resolve, 250));
        }
        console.error("Preview services did not become ready"); process.exit(1);
        """;
}
