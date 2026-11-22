using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIPlatform.API.Services;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class StudioBackupTests
{
    [Fact]
    public async Task BackupRestorePreservesProjectsAndRevokesNontransferableAuthorizations()
    {
        using var fixture = new BackupFixture();
        await fixture.CreateWorkspaceFileAsync("src/App/App.csproj", "<Project />");
        fixture.WriteSettings("""{"theme":"dark","apiToken":"secret-value"}""");
        var archive = await fixture.CreateArchiveAsync();
        using (var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
        using (var reader = new StreamReader(zip.GetEntry("desktop/settings.json")!.Open()))
        {
            var settings = await reader.ReadToEndAsync();
            Assert.Contains("dark", settings);
            Assert.DoesNotContain("secret-value", settings);
            Assert.DoesNotContain("apiToken", settings, StringComparison.OrdinalIgnoreCase);
        }

        var preview = await fixture.InspectAsync(archive);
        Assert.Equal(1, preview.MissionIdConflicts);
        Assert.Equal(1, preview.ExecutionApprovalsToRevoke);
        Assert.Equal(1, preview.ExternalAuthorizationsToRevoke);
        Assert.True(preview.SpaceAvailable);

        var result = await fixture.Service.RestoreAsync(preview.Id,
            new(true, AcknowledgeMissionIdConflicts: true), CancellationToken.None);

        Assert.Equal(1, result.MissionsRestored);
        Assert.Equal(1, result.ExecutionApprovalsRevoked);
        Assert.Equal(1, result.ExternalAuthorizationsRevoked);
        Assert.True(result.RestartRequired);
        var missionPath = Path.Combine(fixture.DataDirectory, "missions", fixture.MissionId.ToString("N") + ".json");
        var restored = JsonSerializer.Deserialize<MissionSnapshot>(await File.ReadAllTextAsync(missionPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() }
            })!;
        Assert.Equal(MissionStatus.Verified, restored.Status);
        Assert.False(restored.SandboxApproved);
        Assert.Null(restored.ApprovalFingerprint);
        Assert.Contains(restored.Events, item => item.Step == "Restore");
        var restoredProject = await File.ReadAllBytesAsync(
            Path.Combine(fixture.DataDirectory, "workspaces", fixture.MissionId.ToString("N"), "src", "App", "App.csproj"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("<Project />"))),
            Convert.ToHexString(SHA256.HashData(restoredProject)));
        var customAgentState = await File.ReadAllTextAsync(Path.Combine(fixture.DataDirectory, "custom-agents", "state.json"));
        Assert.Contains("\"authorizationId\":null", customAgentState, StringComparison.OrdinalIgnoreCase);
        var restoredSettings = await File.ReadAllTextAsync(fixture.SettingsPath);
        Assert.Contains("dark", restoredSettings);
        Assert.DoesNotContain("secret-value", restoredSettings);
    }

    [Fact]
    public async Task RestoreFailureAfterDataSwapRollsBackExistingFiles()
    {
        using var fixture = new BackupFixture(fault: new CheckpointFault("after-data-swap"));
        await fixture.CreateWorkspaceFileAsync("src/App/App.csproj", "<Project />");
        var archive = await fixture.CreateArchiveAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.DataDirectory, "agents.json"), "current-state");
        var preview = await fixture.InspectAsync(archive);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RestoreAsync(preview.Id,
            new(true, true), CancellationToken.None));

        Assert.Equal("current-state", await File.ReadAllTextAsync(Path.Combine(fixture.DataDirectory, "agents.json")));
        Assert.True(File.Exists(Path.Combine(fixture.DataDirectory, "workspaces", fixture.MissionId.ToString("N"),
            "src", "App", "App.csproj")));
    }

    [Fact]
    public async Task BackupRejectsChangesMadeDuringSnapshot()
    {
        using var fixture = new BackupFixture(fault: new CheckpointFault("after-archive-copied", throwAfterAction: false));
        await fixture.CreateWorkspaceFileAsync("src/App/App.csproj", "<Project />");
        fixture.Fault.Action = () => File.WriteAllText(
            Path.Combine(fixture.DataDirectory, "workspaces", fixture.MissionId.ToString("N"), "src", "App", "App.csproj"),
            "<Project Changed=\"true\" />");

        await Assert.ThrowsAsync<StudioBackupException>(() => fixture.Service.CreateAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(fixture.TemporaryDirectory, "*.zip"));
    }

    [Fact]
    public async Task BackupDoesNotExportPotentialCredentialsFromProjectFiles()
    {
        using var fixture = new BackupFixture();
        await fixture.CreateWorkspaceFileAsync("src/App/Configuration.cs", """var apiToken = "synthetic-secret-value";""");

        await Assert.ThrowsAsync<StudioBackupException>(() => fixture.Service.CreateAsync(CancellationToken.None));

        Assert.Empty(Directory.GetFiles(fixture.TemporaryDirectory, "*.zip"));
    }

    [Theory]
    [InlineData("data/../../outside.txt", "0.6.6", false)]
    [InlineData("data/file.txt", "99.0.0", false)]
    [InlineData("data/file.txt", "0.6.6", true)]
    public async Task InspectRejectsUnsafeVersionOrCorruptArchive(string path, string version, bool corrupt)
    {
        using var fixture = new BackupFixture();
        var archive = CreateArchive(path, version, corrupt);
        await Assert.ThrowsAsync<StudioBackupException>(() => fixture.InspectAsync(archive));
    }

    [Fact]
    public async Task RestoreRequiresExplicitMissionConflictAcknowledgement()
    {
        using var fixture = new BackupFixture();
        var archive = await fixture.CreateArchiveAsync();
        var preview = await fixture.InspectAsync(archive);

        await Assert.ThrowsAsync<StudioBackupException>(() => fixture.Service.RestoreAsync(preview.Id,
            new(true, AcknowledgeMissionIdConflicts: false), CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(fixture.DataDirectory, "missions", fixture.MissionId.ToString("N") + ".json")));
    }

    private static byte[] CreateArchive(string path, string version, bool corrupt)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var content = Encoding.UTF8.GetBytes(corrupt ? "altered!" : "expected");
            if (path == "data/file.txt")
            {
                var entry = zip.CreateEntry(path);
                using var output = entry.Open();
                output.Write(content);
                if (corrupt) content = Encoding.UTF8.GetBytes("expected");
            }
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                formatVersion = 1,
                applicationVersion = version,
                createdAt = DateTimeOffset.UtcNow,
                schemaVersions = new Dictionary<string, int>
                {
                    ["studio-data-layout"] = 1,
                    ["mission-snapshot"] = 1,
                    ["agent-configuration"] = 1,
                },
                files = path == "data/file.txt"
                    ? new[] { new { path, length = 8, sha256 = Convert.ToHexString(SHA256.HashData(content)) } }
                    : new[] { new { path, length = 0, sha256 = new string('0', 64) } },
                includedCategories = Array.Empty<string>(),
                exclusions = Array.Empty<string>()
            });
            using var manifestStream = zip.CreateEntry("manifest.json").Open();
            manifestStream.Write(manifest);
        }
        return memory.ToArray();
    }

    [Fact]
    public async Task InspectRejectsZipSymbolicLinks()
    {
        using var fixture = new BackupFixture();
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            var content = Encoding.UTF8.GetBytes("target");
            var link = zip.CreateEntry("data/link");
            link.ExternalAttributes = 0xA000 << 16;
            using (var output = link.Open()) output.Write(content);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                formatVersion = 1,
                applicationVersion = "0.6.6",
                createdAt = DateTimeOffset.UtcNow,
                schemaVersions = new Dictionary<string, int>
                {
                    ["studio-data-layout"] = 1,
                    ["mission-snapshot"] = 1,
                    ["agent-configuration"] = 1,
                },
                files = new[] { new
                {
                    path = "data/link", length = content.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(content)),
                } },
                includedCategories = Array.Empty<string>(),
                exclusions = Array.Empty<string>(),
            });
            using var outputManifest = zip.CreateEntry("manifest.json").Open();
            outputManifest.Write(manifest);
        }

        await Assert.ThrowsAsync<StudioBackupException>(() => fixture.InspectAsync(archive.ToArray()));
    }

    private sealed class BackupFixture : IDisposable
    {
        private readonly StudioOptions options;
        private readonly AgentRegistry registry;
        private readonly PlatformConfiguration platform;
        private readonly WorkspaceStore workspaces;
        private readonly CustomAgentStore customAgents;
        private readonly MissionStore missions;
        private readonly MissionExecutionGate missionGate = new();
        private readonly OllamaModelOperations modelOperations = new();

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AIPlatform-backup-tests-" + Guid.NewGuid().ToString("N"));
        public string DataDirectory => options.DataDirectory;
        public string SettingsPath => Path.Combine(Root, "local", "AIPlatformStudio", "settings.json");
        public string TemporaryDirectory => Path.Combine(Root, "transfer");
        public Guid MissionId { get; } = Guid.NewGuid();
        public CheckpointFault Fault { get; }
        public StudioBackupService Service { get; }

        public BackupFixture(CheckpointFault? fault = null)
        {
            Fault = fault ?? new(null);
            options = new() { DataDirectory = Path.Combine(Root, "StudioData") };
            Directory.CreateDirectory(Path.Combine(DataDirectory, "missions"));
            Directory.CreateDirectory(Path.Combine(DataDirectory, "custom-agents"));
            Directory.CreateDirectory(Path.Combine(DataDirectory, "workspaces", MissionId.ToString("N")));
            var now = DateTimeOffset.UtcNow;
            var mission = new MissionSnapshot(MissionId, "Synthetic migration test", MissionKind.Project,
                null, true, MissionStatus.Verified, now, now, null, null, null, false, null, null,
                true, 0, [], [])
            {
                ApprovalFingerprint = "old-approval-fingerprint"
            };
            File.WriteAllText(Path.Combine(DataDirectory, "missions", MissionId.ToString("N") + ".json"),
                JsonSerializer.Serialize(mission, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            platform = TestSupport.Configuration();
            registry = new(platform);
            workspaces = new(options);
            missions = new(options, platform, registry, workspaces);
            customAgents = new(options);
            var agent = new CustomAgentProfile(Guid.NewGuid(), "Migration agent", "", "Test.", "qwen3:4b",
                now, now);
            customAgents.AddAgent(agent);
            var conversation = new CustomAgentConversation(Guid.NewGuid(), agent.Id, "External project",
                now, now, [])
            {
                Workspace = new(CustomAgentWorkspaceKind.External, null, "outside-project", "old-execution-grant")
            };
            customAgents.CreateConversation(conversation);
            WriteSettings("""{"theme":"light"}""");
            Service = new(options, platform, new StudioMaintenanceGate(), missionGate, modelOperations, missions,
                customAgents, new QuiescentTwitchBotService(),
                new FakeLocalCommandExecutor(new Queue<LocalCommandResult>()), Path.Combine(Root, "local"),
                Fault, temporaryRoot: TemporaryDirectory);
        }

        public async Task CreateWorkspaceFileAsync(string relativePath, string content)
        {
            var path = Path.Combine(DataDirectory, "workspaces", MissionId.ToString("N"),
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }

        public void WriteSettings(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, json);
        }

        public async Task<byte[]> CreateArchiveAsync()
        {
            var download = await Service.CreateAsync(CancellationToken.None);
            var response = new MemoryStream();
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Response.Body = response;
            await download.ExecuteAsync(context);
            return response.ToArray();
        }

        public Task<StudioBackupPreview> InspectAsync(byte[] archive) =>
            Service.InspectAsync(new MemoryStream(archive), archive.Length, CancellationToken.None);

        public void Dispose()
        {
            missions.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class CheckpointFault(string? checkpoint, Action? action = null, bool throwAfterAction = true) : IStudioBackupFaultInjector
    {
        public Action? Action { get; set; } = action;
        public void Checkpoint(string name)
        {
            if (!string.Equals(name, checkpoint, StringComparison.Ordinal)) return;
            Action?.Invoke();
            if (throwAfterAction)
                throw new InvalidOperationException("Synthetic failure at " + name);
        }
    }

    private sealed class QuiescentTwitchBotService : ITwitchBotService
    {
        public TwitchSettingsView GetSettings() => new("", false);
        public TwitchSettingsView SaveSettings(string clientId) => throw new NotSupportedException();
        public IReadOnlyList<TwitchBotView> List() => [];
        public TwitchBotView? Get(Guid id) => null;
        public TwitchBotView Create(string name, string channel) => throw new NotSupportedException();
        public TwitchBotView Update(Guid id, string name, string channel, IReadOnlyList<TwitchCommand> commands) =>
            throw new NotSupportedException();
        public bool Delete(Guid id) => false;
        public Task<TwitchDeviceAuthorization> BeginConnectAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TwitchConnectResult> PollConnectAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TwitchBotView> StartAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TwitchBotView> StopAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public TwitchCommandTestResult TestCommand(Guid id, string input) => throw new NotSupportedException();
    }
}
