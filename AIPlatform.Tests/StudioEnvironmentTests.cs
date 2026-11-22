using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using AIPlatform.API.Services;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class StudioEnvironmentTests
{
    [Fact]
    public async Task WindowsHostProbeDetectsWebView2RegisteredIn32BitView()
    {
        if (!OperatingSystem.IsWindows() || !HasWebView2Registration(RegistryView.Registry32))
            return;

        var snapshot = await new WindowsEnvironmentHostProbe().ReadAsync(CancellationToken.None);

        Assert.Equal(EnvironmentState.Ready, snapshot.WebView2.Status);
    }

    [Fact]
    public async Task VerifiedImagesNeedBothAValidPackageAndMatchingInstalledIds()
    {
        using var fixture = new EnvironmentFixture();
        await fixture.WritePackageAsync();
        fixture.Docker.ImageIds = fixture.ExpectedImages();

        var beforeVerification = await fixture.Service.GetAsync(CancellationToken.None);
        Assert.All(beforeVerification.SandboxImages, image => Assert.Equal(EnvironmentState.Unverified, image.Status));
        Assert.Equal(EnvironmentState.Unverified, beforeVerification.ProvisioningPackage.Status);

        var verified = await fixture.Provisioning.VerifyAsync(CancellationToken.None);
        Assert.Equal(EnvironmentState.Ready, verified.Status);
        var afterVerification = await fixture.Service.GetAsync(CancellationToken.None);
        Assert.All(afterVerification.SandboxImages, image => Assert.Equal(EnvironmentState.Ready, image.Status));
        Assert.Equal(EnvironmentState.Ready, afterVerification.ProvisioningPackage.Status);

        fixture.Docker.ImageIds[ReactTemplate.Image] = "sha256:" + new string('f', 64);
        var changedImage = await fixture.Service.GetAsync(CancellationToken.None);
        Assert.Equal(EnvironmentState.Incompatible,
            changedImage.SandboxImages.Single(image => image.ImageReference == ReactTemplate.Image).Status);
    }

    [Fact]
    public async Task MissingSandboxImagesAreReportedWithoutLoadingOrDownloadingThem()
    {
        using var fixture = new EnvironmentFixture();
        await fixture.WritePackageAsync();
        await fixture.Provisioning.VerifyAsync(CancellationToken.None);

        var report = await fixture.Service.GetAsync(CancellationToken.None);

        Assert.All(report.SandboxImages, image => Assert.Equal(EnvironmentState.Missing, image.Status));
        Assert.DoesNotContain(fixture.Docker.Calls,
            arguments => arguments.FirstOrDefault() is "load" or "pull");
    }

    [Theory]
    [InlineData(EnvironmentState.Missing)]
    [InlineData(EnvironmentState.Incompatible)]
    public async Task ReportsMissingOrIncompatibleNodeWithoutInstallingIt(EnvironmentState nodeState)
    {
        using var fixture = new EnvironmentFixture();
        fixture.Host.Snapshot = fixture.Host.Snapshot with
        {
            Node = new("node", "Node.js", nodeState, "Synthetic Node.js diagnostic.")
        };

        var report = await fixture.Service.GetAsync(CancellationToken.None);

        Assert.Equal(nodeState, report.Node.Status);
        Assert.DoesNotContain(fixture.Docker.Calls, arguments => arguments.FirstOrDefault() == "load");
    }

    [Fact]
    public async Task ReportsOllamaAndDockerUnavailabilitySeparately()
    {
        using var fixture = new EnvironmentFixture();
        fixture.Models.Snapshot = new(false, "Synthetic Ollama connection failure.", []);
        fixture.Docker.EngineAvailable = false;

        var report = await fixture.Service.GetAsync(CancellationToken.None);

        Assert.Equal(EnvironmentState.Missing, report.Ollama.Status);
        Assert.All(report.Models, model => Assert.Equal(EnvironmentState.Unverified, model.Status));
        Assert.Equal(EnvironmentState.Missing, report.DockerEngine.Status);
        Assert.All(report.SandboxImages, image => Assert.Equal(EnvironmentState.Unverified, image.Status));
    }

    [Fact]
    public async Task ReportsMissingConfiguredModelEvenWhenOllamaResponds()
    {
        using var fixture = new EnvironmentFixture();
        var now = DateTimeOffset.UtcNow;
        fixture.CustomAgents.AddAgent(new CustomAgentProfile(Guid.NewGuid(), "Custom environment agent", "",
            "Synthetic model check.", "custom-missing:7b", now, now, ["custom-fallback:latest"]));
        fixture.Models.Snapshot = new(true, null,
        [
            new("configured-general", true, false, false),
            new("configured-coder", false, false, false)
        ]);

        var report = await fixture.Service.GetAsync(CancellationToken.None);

        Assert.Equal(EnvironmentState.Ready, report.Ollama.Status);
        Assert.Equal(EnvironmentState.Missing,
            report.Models.Single(model => model.Name == "configured-coder").Status);
        Assert.Equal(EnvironmentState.Missing,
            report.Models.Single(model => model.Name == "custom-missing:7b").Status);
        Assert.Equal(EnvironmentState.Missing,
            report.Models.Single(model => model.Name == "custom-fallback:latest").Status);
    }

    [Fact]
    public async Task CorruptProvisioningHashIsRejectedBeforeAnyDockerCommand()
    {
        using var fixture = new EnvironmentFixture();
        await fixture.WritePackageAsync(corruptHash: true);

        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Provisioning.VerifyAsync(CancellationToken.None));

        Assert.Empty(fixture.Docker.Calls);
        Assert.Null(fixture.Catalog.GetExpectedImageId(ReactTemplate.Image));
    }

    [Fact]
    public async Task ChangedRecipeFingerprintIsRejectedBeforeAnyDockerCommand()
    {
        using var fixture = new EnvironmentFixture();
        await fixture.WritePackageAsync(corruptSourceFingerprint: true);

        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Provisioning.VerifyAsync(CancellationToken.None));

        Assert.Empty(fixture.Docker.Calls);
        Assert.Null(fixture.Catalog.GetExpectedImageId(ReactTemplate.Image));
    }

    [Fact]
    public async Task ImportRequiresExplicitConfirmationAndVerifiesEveryImportedImageId()
    {
        using var fixture = new EnvironmentFixture();
        var fingerprint = await fixture.WritePackageAsync();
        fixture.Docker.ImageIds = fixture.ExpectedImages();

        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Provisioning.ImportAsync(false, fingerprint, CancellationToken.None));
        Assert.DoesNotContain(fixture.Docker.Calls, arguments => arguments.FirstOrDefault() == "load");

        var images = await fixture.Provisioning.ImportAsync(true, fingerprint, CancellationToken.None);

        Assert.Equal(SandboxImageDefinitions.All.Count, images.Count);
        Assert.All(images, image => Assert.Equal(EnvironmentState.Ready, image.Status));
        Assert.Contains(fixture.Docker.Calls, arguments => arguments.FirstOrDefault() == "load");
        Assert.True(fixture.Catalog.IsTrustedImage(FullStackTemplate.Image,
            fixture.ExpectedImages()[FullStackTemplate.Image]));
    }

    [Fact]
    public async Task ImportRejectsAnImageIdThatDiffersFromTheVerifiedPackage()
    {
        using var fixture = new EnvironmentFixture();
        var fingerprint = await fixture.WritePackageAsync();
        fixture.Docker.ImageIds = fixture.ExpectedImages();
        fixture.Docker.ImageIds[ReactTemplate.Image] = "sha256:" + new string('f', 64);

        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Provisioning.ImportAsync(true, fingerprint, CancellationToken.None));

        Assert.Contains(fixture.Docker.Calls, arguments => arguments.FirstOrDefault() == "load");
        Assert.False(fixture.Catalog.IsTrustedImage(ReactTemplate.Image,
            fixture.Docker.ImageIds[ReactTemplate.Image]));
    }

    [Fact]
    public async Task RestartAfterRestoreRequiresProvisioningVerificationAgain()
    {
        using var fixture = new EnvironmentFixture();
        await fixture.WritePackageAsync();
        fixture.Docker.ImageIds = fixture.ExpectedImages();
        await fixture.Provisioning.VerifyAsync(CancellationToken.None);

        var (restartedService, restartedCatalog) = fixture.CreateRestartedService();
        var report = await restartedService.GetAsync(CancellationToken.None);

        Assert.All(report.SandboxImages, image => Assert.Equal(EnvironmentState.Unverified, image.Status));
        Assert.Equal(EnvironmentState.Unverified, report.ProvisioningPackage.Status);
        Assert.False(restartedCatalog.IsTrustedImage(ReactTemplate.Image,
            fixture.ExpectedImages()[ReactTemplate.Image]));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool HasWebView2Registration(RegistryView view)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var clients = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients");
            if (clients is null) continue;
            foreach (var childName in clients.GetSubKeyNames())
            {
                using var child = clients.OpenSubKey(childName);
                if (string.Equals(child?.GetValue("name") as string,
                        "Microsoft Edge WebView2 Runtime", StringComparison.OrdinalIgnoreCase) &&
                    Version.TryParse(child?.GetValue("pv") as string, out _))
                    return true;
            }
        }
        return false;
    }

    private sealed class EnvironmentFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AIPlatform-environment-" + Guid.NewGuid().ToString("N"));
        public FakeHostProbe Host { get; } = new();
        public FakeHardwareProbe Hardware { get; } = new();
        public FakeModelStatus Models { get; } = new();
        public FakeDockerProcess Docker { get; } = new();
        public SandboxImageCatalog Catalog { get; } = new();
        public CustomAgentStore CustomAgents { get; }
        public SandboxProvisioningService Provisioning { get; }
        public StudioEnvironmentService Service { get; }

        public EnvironmentFixture()
        {
            var packageDirectory = Path.Combine(root, "sandbox-provisioning");
            Directory.CreateDirectory(packageDirectory);
            CustomAgents = new(new StudioOptions { DataDirectory = Path.Combine(root, "StudioData") });
            Provisioning = new(packageDirectory, Docker, Catalog);
            Service = CreateService(Provisioning, Catalog);
        }

        public (StudioEnvironmentService Service, SandboxImageCatalog Catalog) CreateRestartedService()
        {
            var catalog = new SandboxImageCatalog();
            var provisioning = new SandboxProvisioningService(Path.Combine(root, "sandbox-provisioning"), Docker, catalog);
            return (CreateService(provisioning, catalog), catalog);
        }

        private StudioEnvironmentService CreateService(SandboxProvisioningService provisioning,
            SandboxImageCatalog catalog) =>
            new(Host, Hardware, Models, new AgentRegistry(TestSupport.Configuration()), CustomAgents,
                Docker, provisioning, catalog);

        public Dictionary<string, string> ExpectedImages() => SandboxImageDefinitions.All
            .Select((image, index) => (image.ImageReference, Id: "sha256:" + (char)('a' + index) + new string((char)('a' + index), 63)))
            .ToDictionary(item => item.ImageReference, item => item.Id, StringComparer.Ordinal);

        public async Task<string> WritePackageAsync(bool corruptHash = false, bool corruptSourceFingerprint = false)
        {
            var packageDirectory = Path.Combine(root, "sandbox-provisioning");
            var archive = Encoding.UTF8.GetBytes("synthetic sandbox image archive; never imported into Docker");
            var archivePath = Path.Combine(packageDirectory, "aiplatform-sandbox-images-1.0.0.tar");
            await File.WriteAllBytesAsync(archivePath, archive);
            var archiveHash = Convert.ToHexString(SHA256.HashData(archive));
            if (corruptHash) archiveHash = new string('0', 64);
            var manifest = new SandboxProvisioningManifest(1, "1.0.0", "windows-x64",
                DateTimeOffset.UtcNow, Path.GetFileName(archivePath), archive.LongLength, archiveHash,
                SandboxImageDefinitions.All.Select((definition, index) => new SandboxProvisionedImage(
                    definition.ImageReference, definition.Version, definition.Dockerfile,
                    corruptSourceFingerprint ? new string('b', 64) : definition.SourceFingerprint,
                    "sha256:" + (char)('a' + index) + new string((char)('a' + index), 63))).ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllBytesAsync(Path.Combine(packageDirectory, "manifest.json"), bytes);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeHostProbe : IEnvironmentHostProbe
    {
        public EnvironmentHostSnapshot Snapshot { get; set; } = new("Windows 11", "X64",
            new("system", "Windows x64", EnvironmentState.Ready, "Synthetic Windows."),
            new("webview2", "WebView2", EnvironmentState.Ready, "Synthetic runtime.", "130.0"),
            new("node", "Node.js", EnvironmentState.Ready, "Synthetic Node.", "22.0.0"),
            new("docker-desktop", "Docker Desktop", EnvironmentState.Ready, "Synthetic installation."));

        public Task<EnvironmentHostSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(Snapshot);
    }

    private sealed class FakeHardwareProbe : IHardwareProbe
    {
        public Task<HardwareSnapshot> ReadAsync(CancellationToken token) =>
            Task.FromResult(new HardwareSnapshot(32L << 30, 20L << 30,
                [new GpuMemory("Synthetic GPU", 12L << 30, 10L << 30)], 100L << 30, []));
    }

    private sealed class FakeModelStatus : ILocalModelStatus
    {
        public LocalModelsSnapshot Snapshot { get; set; } = new(true, null,
        [
            new("configured-general", true, false, false),
            new("configured-coder", true, false, false)
        ]);

        public Task<LocalModelsSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);
    }

    private sealed class FakeDockerProcess : IContainerProcess
    {
        public bool EngineAvailable { get; set; } = true;
        public Dictionary<string, string> ImageIds { get; set; } = [];
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToArray());
            if (arguments.SequenceEqual(["info", "--format", "{{.OSType}}"]))
                return Task.FromResult(new ContainerProcessResult(EngineAvailable ? 0 : 1,
                    EngineAvailable ? "linux" : "Docker unavailable"));
            if (arguments.SequenceEqual(["load", "--input", Path.GetFullPath(arguments.Last())]))
                return Task.FromResult(new ContainerProcessResult(0, "Loaded synthetic archive"));
            if (arguments.Count == 5 && arguments[0] == "image" && arguments[1] == "inspect")
            {
                var reference = arguments[^1];
                return Task.FromResult(ImageIds.TryGetValue(reference, out var imageId)
                    ? new ContainerProcessResult(0, imageId)
                    : new ContainerProcessResult(1, "image not found"));
            }
            throw new InvalidOperationException("Unexpected synthetic Docker command.");
        }
    }
}
