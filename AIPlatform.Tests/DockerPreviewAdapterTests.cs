using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AIPlatform.Tests;

public sealed class DockerPreviewAdapterTests
{
    [Fact]
    public async Task ConcreteAdapterIsRegisteredButIncompletePreviewCannotBeAuthorized()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development"));
        var runtime = factory.Services.GetRequiredService<IPreviewRuntime>();
        Assert.NotNull(factory.Services.GetRequiredService<PreviewProxy>());
        Assert.IsType<ControlledPreviewBrowser>(factory.Services.GetRequiredService<IControlledPreviewBrowser>());
        Assert.IsType<DockerPreviewAdapter>(runtime);
        Assert.False(runtime.IsAvailable);
        var (plan, snapshot) = Sources();
        await Assert.ThrowsAsync<PreviewUnavailableException>(() => runtime.CreatePlanAsync(snapshot.Identity, default));
        await Assert.ThrowsAsync<PreviewUnavailableException>(() => runtime.StartAsync(Guid.NewGuid(), plan, snapshot, default));
    }

    [Fact]
    public async Task StartsOnlyImmutableImageAndControlledSnapshotWithHardenedOptions()
    {
        var process = new DockerDouble();
        var adapter = Adapter(process);
        var (plan, snapshot) = Sources();
        var id = Guid.NewGuid();
        Assert.Equal(DockerDouble.Image, await adapter.ResolveImageAsync(default));
        var health = await adapter.StartContainerAsync(id, plan, snapshot, default);
        Assert.True(health.BackendReady && health.FrontendReady);
        var args = process.Calls.Single(call => call[0] == "create");
        foreach (var option in new[] { "--network=none", "--read-only", "--cap-drop=ALL",
                     "--security-opt=no-new-privileges:true", "--pull=never" })
            Assert.Contains(option, args);
        Assert.Contains(DockerDouble.Image, args);
        Assert.Equal("10001:10001", args[Array.IndexOf(args, "--user") + 1]);
        Assert.Equal("2048m", args[Array.IndexOf(args, "--memory") + 1]);
        Assert.Equal("128", args[Array.IndexOf(args, "--pids-limit") + 1]);
        Assert.DoesNotContain(args, arg => arg.Contains("docker.sock") || arg.Contains("StudioData") ||
            arg.Contains("0.0.0.0") || arg == "--publish" || arg == "-p" || arg.Contains("network=host"));
        var mount = args[Array.IndexOf(args, "--mount") + 1];
        Assert.EndsWith("target=/source,readonly", mount);
        var directory = mount["type=bind,source=".Length..mount.IndexOf(",target=", StringComparison.Ordinal)];
        Assert.True(Directory.Exists(directory));
        Assert.Contains("sha256sum -c --status", args[^1]);
        await Assert.ThrowsAsync<StudioValidationException>(() => adapter.StartContainerAsync(id, plan, snapshot, default));
        await adapter.StopAsync(id, default);
        await adapter.StopAsync(id, default);
        Assert.Empty(process.Containers);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("start")]
    [InlineData("health")]
    [InlineData("cancel")]
    public async Task FailureOrInterruptionCannotLeakContainer(string failure)
    {
        var process = new DockerDouble { Failure = failure };
        var adapter = Adapter(process);
        var (plan, snapshot) = Sources();
        await Assert.ThrowsAnyAsync<Exception>(() => adapter.StartContainerAsync(Guid.NewGuid(), plan, snapshot, default));
        Assert.Empty(process.Containers);
        if (failure != "image") Assert.Contains(process.Calls, call => call[0] == "rm");
    }

    [Fact]
    public async Task CleanupFailureIsExplicitAndBlocksFurtherStartsUntilRecovery()
    {
        var process = new DockerDouble();
        var adapter = Adapter(process);
        var (plan, snapshot) = Sources();
        var id = Guid.NewGuid();
        await adapter.StartContainerAsync(id, plan, snapshot, default);
        process.Failure = "rm";
        await Assert.ThrowsAsync<IOException>(() => adapter.StopAsync(id, default));
        Assert.NotEmpty(process.Containers);
        await Assert.ThrowsAsync<PreviewUnavailableException>(() =>
            adapter.StartContainerAsync(Guid.NewGuid(), plan, snapshot, default));
        process.Failure = null;
        await adapter.CleanupOrphansAsync(default);
        Assert.Empty(process.Containers);
    }

    [Fact]
    public async Task RejectsUnsafeConfigurationAndAlteredSnapshotBeforeAnyDockerCommand()
    {
        var process = new DockerDouble();
        var adapter = Adapter(process);
        var (plan, snapshot) = Sources();
        var unsafePlan = plan with { Isolation = plan.Isolation with { NetworkPolicy = "host" } };
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            adapter.StartContainerAsync(Guid.NewGuid(), unsafePlan, snapshot, default));
        var altered = snapshot with { Files = [new(snapshot.Files[0].Path, ImmutableArray.Create((byte)0))] };
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            adapter.StartContainerAsync(Guid.NewGuid(), plan, altered, default));
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task MissingImageRefusesResolutionWithoutPullOrRun()
    {
        var process = new DockerDouble { Failure = "image" };
        await Assert.ThrowsAsync<PreviewUnavailableException>(() => Adapter(process).ResolveImageAsync(default));
        Assert.DoesNotContain(process.Calls, call => call[0] is "pull" or "run" or "create" or "start");
    }

    [Fact]
    public async Task SessionManagerUsesConcreteAdapterAndRefusesIncompletePlanWithoutCallingDocker()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var process = new DockerDouble();
        var adapter = new DockerPreviewAdapter(process, fixture.Options);
        var manager = new PreviewSessionManager(new PreviewSourceVerifier(fixture.Store, fixture.Workspaces),
            adapter, new PreviewAuthorizationStore());
        await Assert.ThrowsAsync<PreviewUnavailableException>(() => manager.PrepareAsync(Guid.NewGuid(), default));
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task OrphanRecoveryRejectsUnownedNamesRatherThanRemovingThem()
    {
        var process = new DockerDouble();
        process.Containers.Add("another-users-container");
        var adapter = Adapter(process);
        await Assert.ThrowsAsync<IOException>(() => adapter.CleanupOrphansAsync(default));
        Assert.DoesNotContain(process.Calls, call => call[0] == "rm");
    }

    [Fact]
    public async Task ChangedEffectiveImageRejectsStartupBeforeContainerCreation()
    {
        var process = new DockerDouble();
        var adapter = Adapter(process);
        var (plan, snapshot) = Sources();
        plan = plan with { ImageDigest = "sha256:" + new string('b', 64) };
        await Assert.ThrowsAsync<StudioValidationException>(() => adapter.StartContainerAsync(Guid.NewGuid(), plan, snapshot, default));
        Assert.DoesNotContain(process.Calls, call => call[0] == "create");
    }

    [Fact]
    public async Task NewAdapterCleansPreviousInstancesContainerAndStaging()
    {
        var process = new DockerDouble();
        var options = new StudioOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "PreviewAdapterTests-" + Guid.NewGuid().ToString("N"))
        };
        var first = new DockerPreviewAdapter(process, options);
        var (plan, snapshot) = Sources();
        await first.StartContainerAsync(Guid.NewGuid(), plan, snapshot, default);
        var args = process.Calls.Single(call => call[0] == "create");
        var mount = args[Array.IndexOf(args, "--mount") + 1];
        var directory = mount["type=bind,source=".Length..mount.IndexOf(",target=", StringComparison.Ordinal)];
        await new DockerPreviewAdapter(process, options).CleanupOrphansAsync(default);
        Assert.Empty(process.Containers);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void RejectsStagingInsideStudioStorage()
    {
        Assert.Throws<StudioValidationException>(() => new DockerPreviewAdapter(new DockerDouble(),
            new StudioOptions { DataDirectory = Path.GetTempPath() }));
    }

    private static DockerPreviewAdapter Adapter(DockerDouble process) => new(process,
        new StudioOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "PreviewAdapterTests-" + Guid.NewGuid().ToString("N")) });

    internal static (PreviewPlan, PreviewSourceSnapshot) Sources()
    {
        var content = Encoding.UTF8.GetBytes("verified bytes");
        var path = "frontend/src/App.tsx";
        var files = ImmutableArray.Create(new ExecutionPlanFile(path, Convert.ToHexString(SHA256.HashData(content))));
        var identity = new PreviewSourceIdentity(Guid.NewGuid(), 9, new string('A', 64), files);
        var plan = new PreviewPlan(1, identity.MissionId, 9, identity.ValidationFingerprint, files,
            FullStackTemplate.Image, DockerDouble.Image, [new("isolated-fullstack", ["placeholder"])],
            new("none", true, true, true, true, 2, 2048, 128), [],
            [new("work", "/work", "512 MiB tmpfs; removed with container"),
                new("sqlite", "/tmp", "128 MiB tmpfs; removed with container")], 600, "remove container and staging", 30);
        plan = plan with { Commands = [new("isolated-fullstack", ["/bin/sh", "-c", DockerPreviewAdapter.StartupCommand(plan)])] };
        return (plan, new(identity, [new(path, ImmutableArray.CreateRange(content))]));
    }

    internal sealed class DockerDouble : IContainerProcess
    {
        public const string Image = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public string? Failure { get; set; }
        public List<string[]> Calls { get; } = [];
        public HashSet<string> Containers { get; } = [];

        public Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = arguments.ToArray();
            Calls.Add(args);
            switch (args[0])
            {
                case "info": return Result(0, "linux");
                case "image": return Failure == "image" ? Result(1, "") : Result(0, Image + " linux amd64");
                case "create":
                    Containers.Add(args[Array.IndexOf(args, "--name") + 1]);
                    return Result(0, "container-id");
                case "start": return Result(Failure == "start" ? 1 : 0, "");
                case "exec":
                    if (Failure == "cancel") throw new OperationCanceledException(cancellationToken);
                    return Failure == "health" ? Result(1, "failed") : Result(0, "AIPLATFORM_PREVIEW_READY");
                case "inspect": return Result(0, "true");
                case "ps": return Result(0, string.Join("\n", Containers));
                case "rm":
                    if (Failure == "rm") return Result(1, "failed");
                    Containers.Remove(args[^1]);
                    return Result(0, "");
                default: throw new InvalidOperationException("Unexpected Docker operation.");
            }
        }

        private static Task<ContainerProcessResult> Result(int code, string output) =>
            Task.FromResult(new ContainerProcessResult(code, output));
    }
}
