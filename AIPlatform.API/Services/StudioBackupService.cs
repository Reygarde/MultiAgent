using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.API.Services;

public sealed record StudioBackupPreview(Guid Id, string ApplicationVersion, long ArchiveBytes, long DataBytes,
    int Files, int Missions, int Projects, int MissionIdConflicts, int RedactedSettingsValues,
    int ExternalPathsNotTransferred, int ExecutionApprovalsToRevoke, int PendingMissionsToStop,
    int ExternalAuthorizationsToRevoke, long RequiredFreeBytes, long AvailableFreeBytes, bool SpaceAvailable,
    bool ReplacesAllCurrentData, bool SettingsIncluded,
    IReadOnlyList<string> ReplacedCategories, IReadOnlyList<string> Exclusions,
    IReadOnlyList<string> DependenciesToInstall, IReadOnlyList<string> Warnings);

public sealed record StudioBackupRestoreRequest(bool ConfirmReplaceAllCurrentData,
    bool AcknowledgeMissionIdConflicts);

public sealed record StudioBackupRestoreResult(string Status, int MissionsRestored, int ProjectsRestored,
    int ExecutionApprovalsRevoked, int PendingMissionsStopped, int ExternalAuthorizationsRevoked,
    int SourceFingerprintsPreserved, bool RestartRequired, IReadOnlyList<string> DependenciesToInstall,
    IReadOnlyList<string> Warnings);

public sealed class StudioBackupException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public interface IStudioBackupFaultInjector
{
    void Checkpoint(string name);
}

public sealed class StudioBackupService
{
    public const long MaximumArchiveBytes = 536870912;
    private const long MaximumExpandedBytes = 2147483648;
    private const int MaximumEntries = 100000;
    private const long MaximumEntryBytes = 268435456;
    private const int MaximumManifestBytes = 16777216;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        MaxDepth = 64
    };
    private static readonly JsonSerializerOptions MissionJson = new(Json)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly Regex SensitiveName = new(
        "(?:secret|token|password|credential|connectionstring|private.?key|api.?key|access.?key)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly string[] TextExtensions =
        [".json", ".jsonl", ".cs", ".csproj", ".md", ".txt", ".ts", ".tsx", ".js", ".jsx", ".css",
         ".html", ".xml", ".yaml", ".yml", ".toml", ".config", ".rs", ".sh", ".ps1", ".env",
         ".py", ".bat", ".cmd", ".ini", ".properties", ".conf", ".props", ".targets", ".sln", ".slnx",
         ".java", ".go", ".sql", ".graphql", ".vue", ".svelte"];
    private static readonly string[] Dependencies =
        ["Ollama and selected model weights", "Docker Desktop and pinned sandbox images",
         "Node.js and the packaged React/TypeScript helper", ".NET SDK and any required local workloads"];
    private static readonly string[] IncludedCategories =
        ["Missions, drafts, plans, budgets, revisions and histories", "Generated projects in Studio workspaces",
         "Agents and model configuration", "Custom agents, teams, conversations and development history",
         "Model specializations and routing profiles", "Structured and raw local diagnostics and reports",
         "Twitch bot configuration (never OAuth credentials)", "Browser-test artifacts and validation reports"];
    private static readonly string[] Exclusions =
        ["Ollama model weights and caches", "Docker images and containers", "Windows Credential Manager entries and OAuth tokens",
         "Desktop bearer tokens, process logs, binaries and machine-installed helpers",
         "User-selected external project files and external-workspace rollback folders",
         "Temporary .tmp files and active process-local preview authorizations"];

    private readonly string dataDirectory;
    private readonly string settingsPath;
    private readonly string temporaryRoot;
    private readonly string applicationVersion;
    private readonly PlatformConfiguration platformConfiguration;
    private readonly StudioMaintenanceGate maintenance;
    private readonly MissionExecutionGate missionGate;
    private readonly OllamaModelOperations modelOperations;
    private readonly IMissionService missions;
    private readonly ICustomAgentStore customAgents;
    private readonly ITwitchBotService twitchBots;
    private readonly ILocalCommandExecutor localCommands;
    private readonly IStudioBackupFaultInjector? faultInjector;
    private readonly ConcurrentDictionary<Guid, PreparedRestore> prepared = new();
    private readonly SemaphoreSlim operation = new(1, 1);

    public StudioBackupService(StudioOptions options, PlatformConfiguration platformConfiguration,
        StudioMaintenanceGate maintenance,
        MissionExecutionGate missionGate, OllamaModelOperations modelOperations, IMissionService missions,
        ICustomAgentStore customAgents, ITwitchBotService twitchBots, ILocalCommandExecutor localCommands, string localAppData,
        IStudioBackupFaultInjector? faultInjector = null, string applicationVersion = "0.6.6",
        string? temporaryRoot = null)
    {
        dataDirectory = SafePaths.ValidateStorageDirectory(options.DataDirectory);
        this.platformConfiguration = platformConfiguration;
        settingsPath = Path.Combine(localAppData, "AIPlatformStudio", "settings.json");
        this.temporaryRoot = temporaryRoot ?? Path.Combine(Path.GetTempPath(), "AIPlatformStudio", "transfers");
        this.maintenance = maintenance;
        this.missionGate = missionGate;
        this.modelOperations = modelOperations;
        this.missions = missions;
        this.customAgents = customAgents;
        this.twitchBots = twitchBots;
        this.localCommands = localCommands;
        this.faultInjector = faultInjector;
        this.applicationVersion = Version.Parse(applicationVersion).ToString(3);
        SafePaths.RejectLinks(this.temporaryRoot);
        Directory.CreateDirectory(this.temporaryRoot);
    }

    public async Task<StudioBackupDownload> CreateAsync(CancellationToken token)
    {
        PruneExpired();
        await operation.WaitAsync(token);
        try
        {
            using var maintenanceLease = await maintenance.EnterMaintenanceAsync(token);
            await missionGate.EnterAsync(token);
            try
            {
                await modelOperations.Gate.WaitAsync(token);
                try
                {
                    EnsureQuiescent();
                    var output = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N") + ".zip");
                    SafePaths.RejectLinks(output);
                    await CreateArchiveAsync(output, token);
                    return new(output, $"AIPlatformStudio-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.aiplatform.zip");
                }
                finally { modelOperations.Gate.Release(); }
            }
            finally { missionGate.Exit(); }
        }
        finally { operation.Release(); }
    }

    public async Task<StudioBackupPreview> InspectAsync(Stream archive, long? contentLength, CancellationToken token)
    {
        PruneExpired();
        if (contentLength is > MaximumArchiveBytes)
            throw new StudioBackupException("The selected archive exceeds the 512 MiB transfer limit.");
        await operation.WaitAsync(token);
        var root = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        try
        {
            using var maintenanceLease = await maintenance.EnterMaintenanceAsync(token);
            Directory.CreateDirectory(root);
            var archivePath = Path.Combine(root, "upload.zip");
            var archiveBytes = await CopyBoundedAsync(archive, archivePath, MaximumArchiveBytes, token);
            var extracted = Path.Combine(root, "verified");
            Directory.CreateDirectory(extracted);
            Manifest manifest;
            try { manifest = await VerifyAndExtractAsync(archivePath, extracted, token); }
            catch (StudioBackupException) { throw; }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or EndOfStreamException)
            {
                throw new StudioBackupException("The selected archive is corrupt or contains invalid metadata.");
            }
            await missionGate.EnterAsync(token);
            try
            {
                await modelOperations.Gate.WaitAsync(token);
                try
                {
                    EnsureQuiescent();
                    var currentFingerprint = await CurrentFingerprintTextAsync(token);
                    var incomingMissionIds = MissionIds(extracted, manifest);
                    var existingMissionIds = CurrentMissionIds();
                    var conflicts = incomingMissionIds.Intersect(existingMissionIds).Count();
                    var missionCount = incomingMissionIds.Count;
                    var projects = CountProjects(extracted, manifest);
                    var settings = manifest.Files.SingleOrDefault(file => file.Path == "desktop/settings.json");
                    var redactions = settings is null ? 0 : RedactionCount(await File.ReadAllBytesAsync(
                        Resolve(extracted, settings.Path), token));
                    var externalPaths = await CountExternalPathsAsync(extracted, manifest, token);
                    var externalAuthorizations = await CountExternalAuthorizationsAsync(extracted, manifest, token);
                    var approvedMissions = CountApprovedMissions(extracted, manifest);
                    var pendingMissions = CountPendingMissions(extracted, manifest);
                    var requiredFreeBytes = manifest.Files.Where(file => file.Path.StartsWith("data/", StringComparison.Ordinal))
                        .Sum(file => file.Length) + 64L * 1024 * 1024;
                    var availableFreeBytes = GetAvailableFreeBytes(dataDirectory);
                    var id = Guid.NewGuid();
                    var expires = DateTimeOffset.UtcNow.AddMinutes(15);
                    prepared[id] = new(id, expires, root, extracted, archivePath, manifest, currentFingerprint,
                        missionCount, projects, conflicts, redactions, externalPaths, externalAuthorizations,
                        approvedMissions, pendingMissions, settings is not null, requiredFreeBytes, availableFreeBytes);
                    root = "";
                    return ToPreview(prepared[id], new FileInfo(archivePath).Length);
                }
                finally { modelOperations.Gate.Release(); }
            }
            finally { missionGate.Exit(); }
        }
        catch
        {
            if (root.Length > 0) DeleteTemporary(root);
            throw;
        }
        finally { operation.Release(); }
    }

    public async Task<StudioBackupRestoreResult> RestoreAsync(Guid previewId,
        StudioBackupRestoreRequest request, CancellationToken token)
    {
        PruneExpired();
        if (!prepared.TryGetValue(previewId, out var item) || item.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new StudioBackupException("The verified archive preview expired. Select and verify the archive again.");
        if (!request.ConfirmReplaceAllCurrentData ||
            item.MissionIdConflicts > 0 && !request.AcknowledgeMissionIdConflicts)
            throw new StudioBackupException("Explicit confirmation of full replacement and any mission ID conflicts is required.");
        if (!prepared.TryRemove(previewId, out item))
            throw new StudioBackupException("The archive preview was already consumed.");

        try { await operation.WaitAsync(token); }
        catch
        {
            DeleteTemporary(item.Root);
            throw;
        }
        string? stagedData = null;
        string? rollbackData = null;
        string? stagedSettings = null;
        string? rollbackSettings = null;
        var dataMoved = false;
        var settingsMoved = false;
        var restoreCommitted = false;
        IStudioMaintenanceLease? maintenanceLease = null;
        try
        {
            maintenanceLease = await maintenance.EnterMaintenanceAsync(token);
            await missionGate.EnterAsync(token);
            try
            {
                await modelOperations.Gate.WaitAsync(token);
                try
                {
                    EnsureQuiescent();
                    var currentFingerprint = await CurrentFingerprintAsync(token);
                    if (!CryptographicOperations.FixedTimeEquals(
                            Convert.FromHexString(item.CurrentFingerprint), currentFingerprint))
                        throw new StudioBackupException("Studio data changed after preview. Verify the archive again before restoring.");
                    var bytesNeeded = item.Manifest.Files.Where(file => file.Path.StartsWith("data/", StringComparison.Ordinal))
                        .Sum(file => file.Length);
                    EnsureFreeSpace(dataDirectory, bytesNeeded);
                    var parent = Path.GetDirectoryName(dataDirectory)!;
                    Directory.CreateDirectory(parent);
                    stagedData = Path.Combine(parent, ".StudioData-restore-" + Guid.NewGuid().ToString("N"));
                    rollbackData = Path.Combine(parent, ".StudioData-before-" + Guid.NewGuid().ToString("N"));
                    SafePaths.RejectLinks(stagedData);
                    SafePaths.RejectLinks(rollbackData);
                    Directory.CreateDirectory(stagedData);
                    await MaterializeDataAsync(item, stagedData, token);
                    ValidateStagedData(stagedData);
                    var settingsEntry = item.Manifest.Files.SingleOrDefault(file => file.Path == "desktop/settings.json");
                    if (settingsEntry is not null)
                    {
                        stagedSettings = settingsPath + ".restore-" + Guid.NewGuid().ToString("N");
                        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                        SafePaths.RejectLinks(stagedSettings);
                        await WriteSafeSettingsAsync(Resolve(item.ExtractedDirectory, settingsEntry.Path), stagedSettings, token);
                    }
                    var currentSettingsExists = File.Exists(settingsPath);
                    if (currentSettingsExists)
                    {
                        rollbackSettings = settingsPath + ".before-" + Guid.NewGuid().ToString("N");
                        SafePaths.RejectLinks(settingsPath);
                        SafePaths.RejectLinks(rollbackSettings);
                        File.Copy(settingsPath, rollbackSettings, overwrite: false);
                    }
                    var stagedCurrentFingerprint = await CurrentFingerprintAsync(token);
                    if (!CryptographicOperations.FixedTimeEquals(
                            Convert.FromHexString(item.CurrentFingerprint), stagedCurrentFingerprint))
                        throw new StudioBackupException("Studio data changed while preparing restore. Nothing was replaced; verify the archive again.",
                            StatusCodes.Status409Conflict);
                    faultInjector?.Checkpoint("before-data-swap");
                    Directory.Move(dataDirectory, rollbackData);
                    dataMoved = true;
                    Directory.Move(stagedData, dataDirectory);
                    stagedData = null;
                    faultInjector?.Checkpoint("after-data-swap");
                    if (stagedSettings is not null)
                    {
                        File.Move(stagedSettings, settingsPath, overwrite: true);
                        stagedSettings = null;
                    }
                    else if (File.Exists(settingsPath))
                        File.Delete(settingsPath);
                    settingsMoved = true;
                    faultInjector?.Checkpoint("after-settings-swap");

                    var result = new StudioBackupRestoreResult("Restored",
                        item.Missions, item.Projects, item.ExecutionApprovalsRevoked, item.PendingMissionsStopped,
                        item.ExternalAuthorizationsRevoked, item.Manifest.Files.Count(file => file.Path.StartsWith("data/workspaces/", StringComparison.Ordinal)),
                        true, Dependencies, Warnings(item));
                    maintenanceLease.PreserveUntilRestart();
                    restoreCommitted = true;
                    if (rollbackData is not null && Directory.Exists(rollbackData))
                    {
                        try
                        {
                            Directory.Delete(rollbackData, recursive: true);
                            rollbackData = null;
                        }
                        catch (IOException)
                        {
                            result = result with { Warnings = [.. result.Warnings,
                                "The pre-restore data copy could not be removed and remains at " + rollbackData + "."] };
                            rollbackData = null;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            result = result with { Warnings = [.. result.Warnings,
                                "The pre-restore data copy could not be removed and remains at " + rollbackData + "."] };
                            rollbackData = null;
                        }
                    }
                    if (rollbackSettings is not null && File.Exists(rollbackSettings))
                    {
                        try
                        {
                            File.Delete(rollbackSettings);
                            rollbackSettings = null;
                        }
                        catch (IOException)
                        {
                            result = result with { Warnings = [.. result.Warnings,
                                "The previous Desktop settings copy could not be removed and remains at " + rollbackSettings + "."] };
                            rollbackSettings = null;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            result = result with { Warnings = [.. result.Warnings,
                                "The previous Desktop settings copy could not be removed and remains at " + rollbackSettings + "."] };
                            rollbackSettings = null;
                        }
                    }
                    return result;
                }
                catch
                {
                    string? failedData = null;
                    if (dataMoved)
                    {
                        if (Directory.Exists(dataDirectory))
                        {
                            failedData = dataDirectory + ".failed-" + Guid.NewGuid().ToString("N");
                            Directory.Move(dataDirectory, failedData);
                        }
                        if (rollbackData is not null && Directory.Exists(rollbackData))
                            Directory.Move(rollbackData, dataDirectory);
                        if (failedData is not null && Directory.Exists(failedData))
                            Directory.Delete(failedData, recursive: true);
                    }
                    if (settingsMoved)
                    {
                        if (rollbackSettings is not null && File.Exists(rollbackSettings))
                            File.Move(rollbackSettings, settingsPath, overwrite: true);
                        else if (File.Exists(settingsPath)) File.Delete(settingsPath);
                    }
                    throw;
                }
                finally { modelOperations.Gate.Release(); }
            }
            finally { missionGate.Exit(); }
        }
        finally
        {
            maintenanceLease?.Dispose();
            if (stagedData is not null) DeleteTemporary(stagedData);
            if (stagedSettings is not null) DeleteTemporary(stagedSettings);
            if (rollbackSettings is not null && File.Exists(rollbackSettings) && (!settingsMoved || restoreCommitted))
                File.Delete(rollbackSettings);
            if (item.Root.Length > 0) DeleteTemporary(item.Root);
            operation.Release();
        }
    }

    private void EnsureQuiescent()
    {
        var activeMissions = missions.List().Where(mission =>
            mission.Status is MissionStatus.Queued or MissionStatus.Running).ToArray();
        if (activeMissions.Length > 0)
            throw new StudioBackupException("Stop or wait for queued/running missions before backing up or restoring Studio.",
                StatusCodes.Status409Conflict);
        var activeRuns = customAgents.ListTeams().SelectMany(team => customAgents.ListCollaborationRuns(team.Id))
            .Any(run => run.Status is "Queued" or "Running");
        if (activeRuns)
            throw new StudioBackupException("Wait for active custom-agent collaborations before backing up or restoring Studio.",
                StatusCodes.Status409Conflict);
        if (twitchBots.List().Any(bot => bot.State is "Starting" or "Running" or "Reconnecting"))
            throw new StudioBackupException("Stop running Twitch bots before backing up or restoring Studio.",
                StatusCodes.Status409Conflict);
        if (localCommands.GetRunningApplications().Count > 0)
            throw new StudioBackupException("Stop local applications and previews before backing up or restoring Studio.",
                StatusCodes.Status409Conflict);
    }

    private async Task CreateArchiveAsync(string output, CancellationToken token)
    {
        try
        {
            var source = await ReadSnapshotAsync(token);
            var settingsBytes = await ReadSanitizedSettingsAsync(token);
            var files = new List<ManifestFile>();
            long total = 0;
            await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var item in source.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var name = "data/" + item.RelativePath;
                    CheckForSecretsAtPath(name, item.FullPath);
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    var (length, hash) = await CopyHashedAsync(item.FullPath, target, MaximumEntryBytes, token);
                    if (length != item.Length || !string.Equals(hash, item.Sha256, StringComparison.Ordinal))
                        throw new StudioBackupException("Studio data changed while being copied; no backup was created.");
                    total += length;
                    if (total > MaximumExpandedBytes) throw new StudioBackupException("Studio data exceeds the 2 GiB backup limit.");
                    files.Add(new(name, length, hash));
                }
                if (settingsBytes is not null)
                {
                    if (ProjectExport.ContainsPotentialSecret(new UTF8Encoding(false, true).GetString(settingsBytes.Value.Bytes)))
                        throw new StudioBackupException("Sensitive values remain in Desktop settings after redaction; the backup was not created.");
                    var entry = archive.CreateEntry("desktop/settings.json", CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    await target.WriteAsync(settingsBytes.Value.Bytes, token);
                    files.Add(new("desktop/settings.json", settingsBytes.Value.Bytes.LongLength, Hash(settingsBytes.Value.Bytes)));
                }
                var manifest = CreateManifest(files, applicationVersion);
                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
                if (manifestBytes.Length > MaximumManifestBytes)
                    throw new StudioBackupException("The backup manifest exceeds its limit.");
                await using var manifestStream = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
                await manifestStream.WriteAsync(manifestBytes, token);
            }

            faultInjector?.Checkpoint("after-archive-copied");
            var after = await ReadSnapshotAsync(token);
            var expectedSettingsHash = settingsBytes?.SourceHash ?? "";
            if (!SameSnapshot(source, after) || expectedSettingsHash != after.SettingsHash)
            {
                throw new StudioBackupException("Studio data changed during backup; no archive was returned. Stop active work and retry.");
            }
            if (new FileInfo(output).Length > MaximumArchiveBytes)
                throw new StudioBackupException("The compressed archive exceeds the 512 MiB transfer limit.");
        }
        catch
        {
            if (File.Exists(output)) File.Delete(output);
            throw;
        }
    }

    private async Task<Manifest> VerifyAndExtractAsync(string archivePath, string destination, CancellationToken token)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > MaximumEntries + 1)
            throw new StudioBackupException("The archive entry count is invalid.");
        var manifestEntries = archive.Entries.Where(entry => entry.FullName == "manifest.json").ToArray();
        if (manifestEntries.Length != 1)
            throw new StudioBackupException("The archive must contain exactly one manifest.");
        var manifestEntry = manifestEntries[0];
        ValidateEntry(manifestEntry);
        if (manifestEntry.Length > MaximumManifestBytes) throw new StudioBackupException("The archive manifest is oversized.");
        Manifest manifest;
        await using (var manifestStream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<Manifest>(manifestStream, Json, token)
                ?? throw new StudioBackupException("The archive manifest is invalid.");
        ValidateManifest(manifest, applicationVersion);
        var declared = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries.Where(entry => entry.FullName != "manifest.json"))
        {
            token.ThrowIfCancellationRequested();
            ValidateEntry(entry);
            if (!seen.Add(entry.FullName) || !declared.TryGetValue(entry.FullName, out var expected))
                throw new StudioBackupException("The archive contains a duplicate or undeclared path.");
            if (entry.Length != expected.Length || entry.Length > MaximumEntryBytes)
                throw new StudioBackupException("An archive entry length differs from its manifest or exceeds the file limit.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes) throw new StudioBackupException("The expanded archive exceeds 2 GiB.");
            var target = Resolve(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            SafePaths.RejectLinks(target);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var (length, digest) = await CopyHashedAsync(input, output, MaximumEntryBytes, token);
            if (length != expected.Length || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(digest), Convert.FromHexString(expected.Sha256)))
                throw new StudioBackupException("Archive integrity verification failed for " + entry.FullName + ".");
        }
        if (seen.Count != declared.Count || seen.Count != archive.Entries.Count - 1)
            throw new StudioBackupException("The archive file inventory does not exactly match its manifest.");
        foreach (var file in manifest.Files)
        {
            if (file.Path.StartsWith("data/", StringComparison.Ordinal))
                CheckForSecrets(file, destination);
        }
        ValidateMissionFiles(destination, manifest);
        return manifest;
    }

    private async Task MaterializeDataAsync(PreparedRestore item, string target, CancellationToken token)
    {
        var missionsWritten = new HashSet<Guid>();
        foreach (var file in item.Manifest.Files.Where(file => file.Path.StartsWith("data/", StringComparison.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            var relative = file.Path["data/".Length..];
            var source = Resolve(item.ExtractedDirectory, file.Path);
            var destination = Resolve(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            SafePaths.RejectLinks(destination);
            if (relative.StartsWith("missions/", StringComparison.Ordinal) && relative.EndsWith(".json", StringComparison.Ordinal))
            {
                await using var input = File.OpenRead(source);
                var mission = await JsonSerializer.DeserializeAsync<MissionSnapshot>(input, MissionJson, token)
                    ?? throw new StudioBackupException("A mission snapshot is invalid.");
                var sanitized = SanitizeMission(mission, Path.GetFileNameWithoutExtension(relative), missionsWritten);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await JsonSerializer.SerializeAsync(output, sanitized, MissionJson, token);
            }
            else if (relative == "custom-agents/state.json")
            {
                if (file.Length > CustomAgentLimits.MaxStoredBytes)
                    throw new StudioBackupException("Custom-agent storage exceeds its supported size limit.");
                var content = RevokeExternalAuthorizations(await File.ReadAllBytesAsync(source, token));
                await File.WriteAllBytesAsync(destination, content, token);
            }
            else
            {
                await using var input = File.OpenRead(source);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
                var (_, digest) = await CopyHashedAsync(input, output, MaximumEntryBytes, token);
                if (!string.Equals(digest, file.Sha256, StringComparison.Ordinal))
                    throw new StudioBackupException("A staged source fingerprint changed while preparing restore.");
            }
        }
    }

    private void ValidateStagedData(string stagedData)
    {
        var stagedOptions = new StudioOptions { DataDirectory = stagedData };
        try
        {
            _ = new ConfigurableRegistry(platformConfiguration, stagedOptions);
            var customAgentStore = new CustomAgentStore(stagedOptions);
            _ = customAgentStore.ListTeams();
            _ = customAgentStore.ListAgents();
            var twitchStore = new TwitchBotStore(stagedOptions);
            _ = twitchStore.GetClientId();
            _ = twitchStore.List();
        }
        catch (Exception exception) when (exception is InvalidDataException or StudioValidationException or JsonException or ArgumentException)
        {
            throw new StudioBackupException("The archive contains Studio configuration that is invalid or unsupported.");
        }
    }

    private MissionSnapshot SanitizeMission(MissionSnapshot mission, string fileName, HashSet<Guid> seen)
    {
        if (!Guid.TryParseExact(fileName, "N", out var id) || mission.Id != id || !seen.Add(id))
            throw new StudioBackupException("Mission IDs are duplicated or do not match their file names.");
        var now = DateTimeOffset.UtcNow;
        var pending = mission.Status is MissionStatus.Queued or MissionStatus.Running;
        var oldEvents = mission.Events ?? [];
        var eventSequence = oldEvents.Select(item => item.Sequence).DefaultIfEmpty(0).Max() + 1;
        var message = "Restored from a Studio backup. Previous execution approval was revoked; review and authorize again.";
        var workflow = mission.Workflow;
        if (workflow is not null)
            workflow = workflow with
            {
                Steps = workflow.Steps.Select(step => step.Status == WorkflowStatus.Running ||
                    pending && step.Status is (WorkflowStatus.Pending or WorkflowStatus.WaitingForApproval)
                    ? step with { Status = WorkflowStatus.Cancelled, Error = message } : step).ToArray()
            };
        mission = mission with
        {
            Status = pending ? MissionStatus.Cancelled : mission.Status,
            Error = pending ? message : mission.Error,
            SandboxApproved = false,
            ApprovalFingerprint = null,
            UpdatedAt = now,
            Workflow = workflow,
            Events = [.. oldEvents, new(eventSequence, now, "Restore", StepStatus.Skipped, message)]
        };
        return mission;
    }

    private static byte[] RevokeExternalAuthorizations(byte[] content)
    {
        var root = ParseJsonNode(content) ?? throw new StudioBackupException("Custom-agent storage is invalid.");
        Revoke(root);
        return JsonSerializer.SerializeToUtf8Bytes(root, Json);

        static void Revoke(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in obj.Select(item => item.Key).ToArray())
                {
                    if (string.Equals(key, "authorizationId", StringComparison.OrdinalIgnoreCase))
                        obj[key] = null;
                    else if (obj[key] is { } child) Revoke(child);
                }
            }
            else if (node is JsonArray array)
                foreach (var child in array.Where(item => item is not null)) Revoke(child!);
        }
    }

    private async Task<Snapshot> ReadSnapshotAsync(CancellationToken token)
    {
        var files = new List<SourceFile>();
        if (!Directory.Exists(dataDirectory)) return new(files, await CurrentSettingsHashAsync(token));
        SafePaths.RejectLinks(dataDirectory);
        long total = 0;
        Walk(dataDirectory, dataDirectory);
        if (files.Count > MaximumEntries || total > MaximumExpandedBytes)
            throw new StudioBackupException("Studio data exceeds the bounded file-count or 2 GiB backup limit.");
        var settingsHash = await CurrentSettingsHashAsync(token);
        var ordered = new List<SourceFile>(files.Count);
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            var (length, digest) = await HashFileAsync(file.FullPath, token);
            if (length != file.Length)
                throw new StudioBackupException("Studio data changed while its inventory was being captured.");
            ordered.Add(file with { Sha256 = digest });
        }
        return new(ordered, settingsHash);

        void Walk(string directory, string root)
        {
            token.ThrowIfCancellationRequested();
            SafePaths.RejectLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                SafePaths.RejectLinks(path);
                if (Directory.Exists(path)) Walk(path, root);
                else
                {
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    ValidateRelativePath(relative);
                    if (relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(path);
                    if (info.Length > MaximumEntryBytes) throw new StudioBackupException("A Studio file exceeds the 256 MiB transfer limit.");
                    total = checked(total + info.Length);
                    files.Add(new(relative, path, info.Length));
                }
            }
        }
    }

    private async Task<byte[]> CurrentFingerprintAsync(CancellationToken token)
    {
        var snapshot = await ReadSnapshotAsync(token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in snapshot.Files) Append(hash, file.RelativePath, file.Length, file.Sha256);
        Append(hash, "desktop/settings.raw", 0, snapshot.SettingsHash ?? "");
        return hash.GetHashAndReset();
    }

    private async Task<string> CurrentFingerprintTextAsync(CancellationToken token) =>
        Convert.ToHexString(await CurrentFingerprintAsync(token));

    private async Task<string> CurrentSettingsHashAsync(CancellationToken token)
    {
        if (!File.Exists(settingsPath)) return "";
        SafePaths.RejectLinks(settingsPath);
        if (new FileInfo(settingsPath).Length > MaximumManifestBytes)
            throw new StudioBackupException("Desktop settings exceed the backup size limit.");
        var (length, hash) = await HashFileAsync(settingsPath, token);
        return length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + hash;
    }

    private async Task<(byte[] Bytes, string SourceHash)?> ReadSanitizedSettingsAsync(CancellationToken token)
    {
        if (!File.Exists(settingsPath)) return null;
        SafePaths.RejectLinks(settingsPath);
        if (new FileInfo(settingsPath).Length > MaximumManifestBytes)
            throw new StudioBackupException("Desktop settings exceed the backup size limit.");
        var source = await File.ReadAllBytesAsync(settingsPath, token);
        JsonNode node;
        try { node = ParseJsonNode(source) ?? throw new JsonException(); }
        catch (JsonException exception) { throw new StudioBackupException("Desktop settings are not valid JSON: " + exception.Message); }
        SanitizeSettings(node);
        return (JsonSerializer.SerializeToUtf8Bytes(node, Json),
            source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Hash(source));

        static void SanitizeSettings(JsonNode node)
        {
            if (node is JsonObject obj)
                foreach (var key in obj.Select(item => item.Key).ToArray())
                {
                    if (SensitiveName.IsMatch(key)) obj.Remove(key);
                    else if (string.Equals(key, "DataDirectory", StringComparison.OrdinalIgnoreCase))
                        obj[key] = "StudioData";
                    else if (obj[key] is { } value) SanitizeSettings(value);
                }
            else if (node is JsonArray array)
                foreach (var child in array.Where(item => item is not null)) SanitizeSettings(child!);
        }
    }

    private async Task WriteSafeSettingsAsync(string source, string destination, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(source, token);
        var node = ParseJsonNode(bytes) ?? throw new StudioBackupException("Desktop settings are invalid.");
        RedactSensitiveSettings(node);
        await File.WriteAllBytesAsync(destination, JsonSerializer.SerializeToUtf8Bytes(node, Json), token);
    }

    private async Task<long> CopyBoundedAsync(Stream input, string path, long maximum, CancellationToken token)
    {
        long total = 0;
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximum) throw new StudioBackupException("The selected archive exceeds the 512 MiB transfer limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        await output.FlushAsync(token);
        return total;
    }

    private static async Task<(long Length, string Sha256)> CopyHashedAsync(string path, Stream target,
        long maximum, CancellationToken token)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await CopyHashedAsync(source, target, maximum, token);
    }

    private static async Task<(long Length, string Sha256)> CopyHashedAsync(Stream source, Stream target,
        long maximum, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token);
            if (read == 0) break;
            length = checked(length + read);
            if (length > maximum) throw new StudioBackupException("A file exceeds the 256 MiB transfer limit.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return (length, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static async Task<(long Length, string Sha256)> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            length += read;
            hash.AppendData(buffer, 0, read);
        }
        return (length, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private async Task<int> CountExternalPathsAsync(string root, Manifest manifest, CancellationToken token)
    {
        var count = 0;
        foreach (var file in manifest.Files.Where(file => file.Path is "data/custom-agents/state.json" or
                     "desktop/settings.json"))
        {
            token.ThrowIfCancellationRequested();
            if (file.Length > MaximumManifestBytes)
                throw new StudioBackupException("Storage metadata exceeds the maximum size for path inspection.");
            var node = ParseJsonNode(await File.ReadAllBytesAsync(Resolve(root, file.Path), token))
                ?? throw new StudioBackupException("Storage metadata is invalid JSON.");
            Visit(node);
        }
        return count;

        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj)
                {
                    if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) &&
                        (pair.Key.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                         pair.Key.Contains("directory", StringComparison.OrdinalIgnoreCase) ||
                         pair.Key.Contains("root", StringComparison.OrdinalIgnoreCase)) &&
                        IsAbsolutePath(text)) count++;
                    Visit(pair.Value);
                }
            else if (node is JsonArray array)
                foreach (var child in array) Visit(child);
        }
    }

    private void CheckForSecrets(ManifestFile file, string root)
    {
        var path = Resolve(root, file.Path);
        CheckForSecretsAtPath(file.Path, path);
    }

    private static void CheckForSecretsAtPath(string archivePath, string path)
    {
        if (!TextExtensions.Contains(Path.GetExtension(archivePath), StringComparer.OrdinalIgnoreCase)) return;
        var bytes = File.ReadAllBytes(path);
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (ProjectExport.ContainsPotentialSecret(text))
                throw new StudioBackupException($"Potential credentials detected in {archivePath}. Remove or redact them before creating/restoring a backup.");
        }
        catch (DecoderFallbackException) { }
    }

    private static void ValidateMissionFiles(string root, Manifest manifest)
    {
        var ids = new HashSet<Guid>();
        foreach (var file in manifest.Files.Where(file => file.Path.StartsWith("data/missions/", StringComparison.Ordinal) &&
                     file.Path.EndsWith(".json", StringComparison.Ordinal)))
        {
            var relative = file.Path["data/missions/".Length..];
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(relative), "N", out var fileId))
                throw new StudioBackupException("A mission snapshot has an unsafe or invalid ID file name.");
            using var stream = File.OpenRead(Resolve(root, file.Path));
            var mission = JsonSerializer.Deserialize<MissionSnapshot>(stream, MissionJson)
                ?? throw new StudioBackupException("A mission snapshot is invalid.");
            if (mission.Id != fileId || !ids.Add(mission.Id))
                throw new StudioBackupException("The archive contains duplicate mission IDs or mismatched snapshot names.");
        }
    }

    private static HashSet<Guid> MissionIds(string root, Manifest manifest)
    {
        var ids = new HashSet<Guid>();
        foreach (var file in manifest.Files.Where(file => file.Path.StartsWith("data/missions/", StringComparison.Ordinal) &&
                     file.Path.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = File.OpenRead(Resolve(root, file.Path));
            var mission = JsonSerializer.Deserialize<MissionSnapshot>(stream, MissionJson)!;
            ids.Add(mission.Id);
        }
        return ids;
    }

    private HashSet<Guid> CurrentMissionIds() => missions.List().Select(mission => mission.Id).ToHashSet();

    private static int CountProjects(string root, Manifest manifest) =>
        manifest.Files.Where(file => file.Path.StartsWith("data/workspaces/", StringComparison.Ordinal))
            .Select(file => file.Path.Split('/').ElementAtOrDefault(2)).Where(id => id is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static int CountPendingMissions(string root, Manifest manifest) =>
        manifest.Files.Where(file => file.Path.StartsWith("data/missions/", StringComparison.Ordinal) &&
            file.Path.EndsWith(".json", StringComparison.Ordinal))
            .Select(file =>
            {
                using var stream = File.OpenRead(Resolve(root, file.Path));
                return JsonSerializer.Deserialize<MissionSnapshot>(stream, MissionJson)!;
            })
            .Count(mission => mission.Status is MissionStatus.Queued or MissionStatus.Running);

    private static int CountApprovedMissions(string root, Manifest manifest) =>
        manifest.Files.Where(file => file.Path.StartsWith("data/missions/", StringComparison.Ordinal) &&
            file.Path.EndsWith(".json", StringComparison.Ordinal))
            .Select(file =>
            {
                using var stream = File.OpenRead(Resolve(root, file.Path));
                return JsonSerializer.Deserialize<MissionSnapshot>(stream, MissionJson)!;
            })
            .Count(mission => mission.SandboxApproved || mission.ApprovalFingerprint is not null);

    private static async Task<int> CountExternalAuthorizationsAsync(string root, Manifest manifest, CancellationToken token)
    {
        var file = manifest.Files.SingleOrDefault(item => item.Path == "data/custom-agents/state.json");
        if (file is null) return 0;
        if (file.Length > CustomAgentLimits.MaxStoredBytes)
            throw new StudioBackupException("Custom-agent storage exceeds its supported size limit.");
        var node = ParseJsonNode(await File.ReadAllBytesAsync(Resolve(root, file.Path), token));
        var count = 0;
        Visit(node);
        return count;

        void Visit(JsonNode? current)
        {
            if (current is JsonObject obj)
                foreach (var pair in obj)
                {
                    if (string.Equals(pair.Key, "authorizationId", StringComparison.OrdinalIgnoreCase) &&
                        pair.Value is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id))
                        count++;
                    Visit(pair.Value);
                }
            else if (current is JsonArray array)
                foreach (var child in array) Visit(child);
        }
    }

    private static void ValidateManifest(Manifest manifest, string currentApplicationVersion)
    {
        if (manifest.FormatVersion != 1 || manifest.Files is not { Count: <= MaximumEntries } ||
            !Version.TryParse(manifest.ApplicationVersion, out var archiveVersion) ||
            !Version.TryParse(currentApplicationVersion, out var currentVersion) ||
            archiveVersion > currentVersion ||
            manifest.SchemaVersions is null ||
            manifest.SchemaVersions.GetValueOrDefault("studio-data-layout") != 1 ||
            manifest.SchemaVersions.GetValueOrDefault("mission-snapshot") != 1 ||
            manifest.SchemaVersions.GetValueOrDefault("agent-configuration") != 1 ||
            manifest.SchemaVersions.Any(pair => pair.Value != 1) ||
            manifest.Files.Any(file => file is null || file.Length < 0 ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit)))
            throw new StudioBackupException("The archive format, application version or storage schema is incompatible.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            ValidateEntryPath(file.Path);
            if (!paths.Add(file.Path) || !(file.Path.StartsWith("data/", StringComparison.Ordinal) ||
                file.Path == "desktop/settings.json"))
                throw new StudioBackupException("The manifest contains a duplicate or out-of-scope path.");
            total = checked(total + file.Length);
            if (file.Length > MaximumEntryBytes || total > MaximumExpandedBytes)
                throw new StudioBackupException("The archive manifest exceeds the bounded size limits.");
        }
    }

    private static void ValidateEntry(ZipArchiveEntry entry)
    {
        ValidateEntryPath(entry.FullName);
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixMode == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
            entry.FullName.EndsWith("/", StringComparison.Ordinal))
            throw new StudioBackupException("Symbolic links and directory entries are not allowed in backup archives.");
    }

    private static void ValidateEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Contains('\\') ||
            path.StartsWith("/", StringComparison.Ordinal) || path.Contains(':') ||
            path.Any(char.IsControl) || path.EndsWith("/", StringComparison.Ordinal))
            throw new StudioBackupException("The archive contains an unsafe path.");
        foreach (var segment in path.Split('/'))
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                !segment.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-') ||
                Regex.IsMatch(segment, @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\..*)?\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new StudioBackupException("The archive contains a traversal or invalid path segment.");
    }

    private static void ValidateRelativePath(string path) => ValidateEntryPath(path);

    private static string Resolve(string root, string relative)
    {
        ValidateEntryPath(relative);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new StudioBackupException("The archive path escapes its staging directory.");
        SafePaths.RejectLinks(full);
        return full;
    }

    private static Manifest CreateManifest(IReadOnlyList<ManifestFile> files, string appVersion) => new(1, appVersion,
        DateTimeOffset.UtcNow,
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["studio-data-layout"] = 1, ["mission-snapshot"] = 1, ["agent-configuration"] = 1
        }, files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), IncludedCategories, Exclusions);

    private static StudioBackupPreview ToPreview(PreparedRestore item, long archiveBytes) => new(item.Id,
        item.Manifest.ApplicationVersion, archiveBytes,
        item.Manifest.Files.Sum(file => file.Path.StartsWith("data/", StringComparison.Ordinal) ? file.Length : 0),
        item.Manifest.Files.Count, item.Missions, item.Projects, item.MissionIdConflicts,
        item.RedactedSettingsValues, item.ExternalPathsNotTransferred, item.ExecutionApprovalsRevoked,
        item.PendingMissionsStopped, item.ExternalAuthorizationsRevoked, item.RequiredFreeBytes,
        item.AvailableFreeBytes, item.AvailableFreeBytes >= item.RequiredFreeBytes, true, item.SettingsIncluded,
        IncludedCategories, Exclusions, Dependencies, Warnings(item));

    private static IReadOnlyList<string> Warnings(PreparedRestore item)
    {
        var values = new List<string>
        {
            "All current internal Studio data will be replaced; this is not a merge.",
            "Docker/build and local execution approvals are revoked for every restored mission.",
            "External project files are not included; absolute external paths may need to be selected again.",
            "Ollama models, Docker images, credentials, application binaries and helper runtimes must be installed separately."
        };
        if (item.MissionIdConflicts > 0)
            values.Add($"{item.MissionIdConflicts} mission IDs also exist here. Confirming replaces those records; no merge is attempted.");
        if (item.ExternalPathsNotTransferred > 0)
            values.Add($"{item.ExternalPathsNotTransferred} absolute external path references were found; verify or reselect those folders.");
        if (!item.SettingsIncluded) values.Add("The archive has no Desktop settings file; the current settings file will be removed.");
        if (item.RedactedSettingsValues > 0)
            values.Add($"{item.RedactedSettingsValues} sensitive settings values were excluded and must be entered again.");
        return values;
    }

    private static void RedactSensitiveSettings(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(item => item.Key).ToArray())
            {
                if (SensitiveName.IsMatch(key)) obj.Remove(key);
                else if (string.Equals(key, "DataDirectory", StringComparison.OrdinalIgnoreCase))
                    obj[key] = "StudioData";
                else if (obj[key] is { } child) RedactSensitiveSettings(child);
            }
        else if (node is JsonArray array)
            foreach (var child in array.Where(item => item is not null)) RedactSensitiveSettings(child!);
    }

    private static int RedactionCount(byte[] bytes)
    {
        var node = ParseJsonNode(bytes) ?? throw new StudioBackupException("Desktop settings JSON is invalid.");
        var count = 0;
        Count(node);
        return count;
        void Count(JsonNode current)
        {
            if (current is JsonObject obj)
                foreach (var pair in obj.ToArray())
                    if (SensitiveName.IsMatch(pair.Key)) { count++; obj.Remove(pair.Key); }
                    else if (pair.Value is { } nested) Count(nested);
            else if (current is JsonArray array)
                foreach (var child in array.Where(item => item is not null)) Count(child!);
        }
    }

    private static bool IsAbsolutePath(string value) =>
        Path.IsPathFullyQualified(value) || Regex.IsMatch(value, @"\A[A-Za-z]:[\\/]", RegexOptions.CultureInvariant);

    private static void Append(IncrementalHash hash, string path, long length, string digest)
    {
        var bytes = Encoding.UTF8.GetBytes($"{path}\0{length}\0{digest}\n");
        hash.AppendData(bytes);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static JsonNode? ParseJsonNode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];
        return JsonNode.Parse(bytes);
    }

    private static bool SameSnapshot(Snapshot first, Snapshot second) =>
        first.SettingsHash == second.SettingsHash &&
        first.Files.Select(file => file.RelativePath).SequenceEqual(second.Files.Select(file => file.RelativePath), StringComparer.Ordinal) &&
        first.Files.Zip(second.Files).All(pair => pair.First.Length == pair.Second.Length &&
            string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.Ordinal));

    private static void EnsureFreeSpace(string storagePath, long needed)
    {
        if (GetAvailableFreeBytes(storagePath) < needed + 64L * 1024 * 1024)
            throw new StudioBackupException("There is not enough free disk space for a staged, transactional restore.",
                StatusCodes.Status409Conflict);
    }

    private static long GetAvailableFreeBytes(string path)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        return drive.AvailableFreeSpace;
    }

    private static void DeleteTemporary(string path)
    {
        if (Directory.Exists(path))
        {
            SafePaths.RejectLinks(path);
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            SafePaths.RejectLinks(path);
            File.Delete(path);
        }
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in prepared)
            if (pair.Value.ExpiresAt <= now && prepared.TryRemove(pair.Key, out var expired))
                DeleteTemporary(expired.Root);
    }

    private sealed record SourceFile(string RelativePath, string FullPath, long Length, string Sha256 = "");
    private sealed record Snapshot(IReadOnlyList<SourceFile> Files, string? SettingsHash);
    private sealed record Manifest(int FormatVersion, string ApplicationVersion, DateTimeOffset CreatedAt,
        IReadOnlyDictionary<string, int> SchemaVersions, IReadOnlyList<ManifestFile> Files,
        IReadOnlyList<string> IncludedCategories, IReadOnlyList<string> Exclusions);
    private sealed record ManifestFile(string Path, long Length, string Sha256);
    private sealed record PreparedRestore(Guid Id, DateTimeOffset ExpiresAt, string Root, string ExtractedDirectory,
        string ArchivePath, Manifest Manifest, string CurrentFingerprint, int Missions, int Projects,
        int MissionIdConflicts, int RedactedSettingsValues, int ExternalPathsNotTransferred,
        int ExternalAuthorizationsRevoked, int ExecutionApprovalsRevoked, int PendingMissionsStopped,
        bool SettingsIncluded, long RequiredFreeBytes, long AvailableFreeBytes);

    public sealed class StudioBackupDownload(string path, string fileName) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = "application/zip";
            httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            httpContext.Response.ContentLength = new FileInfo(path).Length;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await stream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
