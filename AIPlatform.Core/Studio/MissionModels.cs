using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIPlatform.Core.Studio;

public enum MissionKind { Chat, Project }
public enum CustomAgentProjectType { React, DotNet, FullStack }
public enum MissionStatus { Queued, Running, Completed, GeneratedUnverified, Failed, Cancelled, AwaitingApproval, BuildFailed, TestsFailed, Verified, AwaitingPlanApproval }
public enum StepStatus { Running, Succeeded, Failed, Unavailable, Skipped, Cancelled }

public sealed record MissionSubmission(
    string Task, MissionKind Kind = MissionKind.Chat, string? AgentId = null, bool ApproveSandboxExecution = false,
    string? TemplateId = null, bool AutomaticModelSelection = false, bool RecordPlannerTrace = false,
    bool RequirePlanApproval = false, int? MaxTotalGenerations = null, bool AutomaticReactCorrections = false,
    string? SizingProfileId = null);

/// <summary>A durably reserved generation unit; never automatically refunded, including uncertain sends.</summary>
public sealed record MissionGenerationReservation(Guid Id, string Role, string Model, DateTimeOffset ReservedAt,
    Guid? CapacityId = null);

public sealed record MissionEvent(
    int Sequence, DateTimeOffset Timestamp, string Step, StepStatus Status, string Message, double? DurationMilliseconds = null);

public sealed record VerificationResult(
    string Stage, string Status, int? ExitCode, string Log, int? TotalTests = null,
    double? DurationMilliseconds = null, int Attempt = 0,
    int? WarningCount = null, int? ErrorCount = null, int? FailedTests = null,
    DiagnosticReport? Diagnostics = null);

public sealed record MissionSnapshot(
    Guid Id, string Task, MissionKind Kind, string? RequestedAgent,
    bool SandboxApproved, MissionStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? SelectedAgent, string? SelectedModel,
    string? Reason, bool UsedFallback, string? Response, string? Error,
    bool HasWorkspace, int CorrectionAttempts, IReadOnlyList<MissionEvent> Events,
    IReadOnlyList<VerificationResult> Verification, string? ApprovalFingerprint = null,
    MissionWorkflow? Workflow = null, string? ProjectName = null, string? ProjectDescription = null,
    bool Deleted = false, string? TemplateId = null, bool Archived = false, DateTimeOffset? ArchivedAt = null,
    DateTimeOffset? CompletedAt = null, int? MaxTotalGenerations = null,
    IReadOnlyList<MissionGenerationReservation>? GenerationReservations = null,
    string? GenerationBudgetWarning = null, bool GenerationBudgetExhausted = false,
    ProjectSizingProfile? Sizing = null)
{
    public bool IsTerminal => Status is not (MissionStatus.Queued or MissionStatus.Running or MissionStatus.AwaitingApproval or MissionStatus.AwaitingPlanApproval);
}

public sealed record GeneratedFile(string Path, string Content);
public sealed record ReviewedWorkspaceChange(string Path, string? Before, string? After);
// For corrections, Files contains replacements only; omitted files are preserved.
public sealed record ProjectManifest(string Summary, IReadOnlyList<GeneratedFile> Files);
public sealed record ExecutionPlanFile(string Path, string Sha256);
public sealed record ExecutionPlanStage(string Name, IReadOnlyList<string> Arguments);
public sealed record ExecutionPlanDifference(string Field, string? Expected, string? Actual);

public sealed record ReactExecutionPlanDescriptor(
    int SchemaVersion,
    string TemplateId,
    IReadOnlyList<ExecutionPlanFile> Files,
    string ImageReference,
    string EffectiveImage,
    string? ImageDigest,
    bool SandboxEnabled,
    int MemoryMegabytes,
    int CpuCount,
    int TimeoutSeconds,
    int MaxCorrectionAttempts,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> Commands)
{
    public string ComputeFingerprint(IReadOnlyList<GeneratedFile> files)
    {
        var sortedFiles = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        var content = JsonSerializer.Serialize(new
        {
            files = sortedFiles.Select(file => new WorkspaceContent(file.Path, file.Content)).ToArray(),
            Image = EffectiveImage,
            Enabled = SandboxEnabled,
            MemoryMegabytes,
            CpuCount,
            TimeoutSeconds,
            MaxCorrectionAttempts,
            operations = Operations
        });
        content += "\n" + JsonSerializer.Serialize(Commands);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    public ReactExecutionPlanDescriptor WithFiles(IReadOnlyList<GeneratedFile> files) =>
        this with { Files = Manifest(files) };

    public IReadOnlyList<ExecutionPlanDifference> CompareTo(ReactExecutionPlanDescriptor actual)
    {
        var differences = new List<ExecutionPlanDifference>();
        Add("schemaVersion", SchemaVersion.ToString(), actual.SchemaVersion.ToString());
        Add("templateId", TemplateId, actual.TemplateId);
        Add("imageReference", ImageReference, actual.ImageReference);
        Add("effectiveImage", EffectiveImage, actual.EffectiveImage);
        Add("imageDigest", ImageDigest, actual.ImageDigest);
        Add("sandbox.enabled", SandboxEnabled.ToString(), actual.SandboxEnabled.ToString());
        Add("sandbox.memoryMegabytes", MemoryMegabytes.ToString(), actual.MemoryMegabytes.ToString());
        Add("sandbox.cpuCount", CpuCount.ToString(), actual.CpuCount.ToString());
        Add("sandbox.timeoutSeconds", TimeoutSeconds.ToString(), actual.TimeoutSeconds.ToString());
        Add("maxCorrectionAttempts", MaxCorrectionAttempts.ToString(), actual.MaxCorrectionAttempts.ToString());
        Add("operations", JsonSerializer.Serialize(Operations), JsonSerializer.Serialize(actual.Operations));
        Add("reactCommands", JsonSerializer.Serialize(Commands), JsonSerializer.Serialize(actual.Commands));

        var expectedFiles = Files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);
        var actualFiles = actual.Files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);
        foreach (var path in expectedFiles.Keys.Union(actualFiles.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            expectedFiles.TryGetValue(path, out var expected);
            actualFiles.TryGetValue(path, out var current);
            Add($"files[{path}].sha256", expected, current);
        }
        return differences;

        void Add(string field, string? expected, string? current)
        {
            if (!string.Equals(expected, current, StringComparison.Ordinal))
                differences.Add(new(field, expected, current));
        }
    }

    public IReadOnlyList<ExecutionPlanDifference> CompareManifest(IReadOnlyList<GeneratedFile> files)
    {
        var expected = Files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);
        var actual = Manifest(files).ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);
        var differences = new List<ExecutionPlanDifference>();
        foreach (var path in expected.Keys.Union(actual.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            expected.TryGetValue(path, out var oldHash);
            actual.TryGetValue(path, out var newHash);
            if (!string.Equals(oldHash, newHash, StringComparison.Ordinal))
                differences.Add(new($"files[{path}].sha256", oldHash, newHash));
        }
        return differences;
    }

    public static IReadOnlyList<ExecutionPlanFile> Manifest(IReadOnlyList<GeneratedFile> files) =>
        files.OrderBy(file => file.Path, StringComparer.Ordinal).Select(file =>
            new ExecutionPlanFile(file.Path,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Content))))).ToArray();
}

public sealed record CanonicalExecutionPlan(
    int SchemaVersion, Guid MissionId, int WorkflowVersion, string TemplateId,
    IReadOnlyList<ExecutionPlanFile> Files, string Image, bool SandboxEnabled,
    int MemoryMegabytes, int CpuCount, int TimeoutSeconds, int AgentTimeoutSeconds,
    bool AutomaticCorrections, IReadOnlyList<string> Operations,
    IReadOnlyList<ExecutionPlanStage> Stages)
{
    public string ComputeFingerprint() => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
}
public sealed record ExecutionPlan(
    string Fingerprint, string Image, int MemoryMegabytes, int CpuCount, int TimeoutSeconds,
    int MaxCorrectionAttempts, IReadOnlyList<string> Operations, CanonicalExecutionPlan? Canonical = null,
    ReactExecutionPlanDescriptor? ReactDescriptor = null);

public interface IExecutionPlanService
{
    Task<ExecutionPlan> GetAsync(Guid id, CancellationToken cancellationToken, int? workflowVersion = null);
}
public sealed record WorkspaceFile(string Path, long Size);
public sealed record WorkspaceContent(string Path, string Content);
public sealed record SandboxAvailability(bool Available, string Reason, bool EngineAvailable = false);
public sealed record LocalModelState(string Name, bool Installed, bool Loaded, bool InUse);
public sealed record LocalModelsSnapshot(bool Available, string? Error, IReadOnlyList<LocalModelState> Models);

public interface IMissionService
{
    IDisposable EnterGenerationScope(Guid id, string role);
    MissionSnapshot Submit(MissionSubmission submission);
    Task<MissionSnapshot> CreateCustomAgentProjectAsync(
        string? name, CustomAgentProjectType projectType, CancellationToken cancellationToken, int? maxTotalGenerations = null);
    IReadOnlyList<MissionSnapshot> List();
    MissionSnapshot? Get(Guid id);
    bool Cancel(Guid id);
    bool DecideApproval(Guid id, bool approved, string fingerprint);
    bool DecidePlanApproval(Guid id, bool approved, string fingerprint);
    bool Resume(Guid id);
    bool PrepareRevalidation(Guid id, DateTimeOffset expectedUpdatedAt, ExecutionPlan plan);
    bool RenameProject(Guid id, string name, string description);
    bool DeleteProject(Guid id);
    bool Archive(Guid id);
    bool Restore(Guid id);
    bool RecordWorkspaceEdit(Guid id, DateTimeOffset expectedUpdatedAt, Guid agentId, Guid conversationId,
        Guid proposalId, IReadOnlyList<string> changedPaths, IReadOnlyList<GeneratedFile> files);
}

public interface IWorkspaceStore
{
    void Create(Guid id);
    void Delete(Guid id);
    void ValidateFiles(IReadOnlyList<GeneratedFile> files, string? templateId = null, bool allowPartialTemplate = false,
        ProjectSizingProfile? sizing = null);
    Task WriteAsync(Guid id, IReadOnlyList<GeneratedFile> files, CancellationToken cancellationToken, string? templateId = null,
        ProjectSizingProfile? sizing = null);
    IReadOnlyList<WorkspaceFile> List(Guid id, ProjectSizingProfile? sizing = null);
    Task<WorkspaceContent> ReadAsync(Guid id, string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<GeneratedFile>> ApplyReviewedChangesAsync(
        Guid id, string? templateId, IReadOnlyList<ReviewedWorkspaceChange> changes,
        Func<IReadOnlyList<GeneratedFile>, bool> commit, CancellationToken cancellationToken);
    Task ReplaceFullStackFilesAsync(Guid id, ManualRevisionRole role, IReadOnlyList<GeneratedFile> files, CancellationToken cancellationToken);
    string GetDirectory(Guid id);
}

public enum ManualRevisionRole { Backend, Frontend }

public interface IManualRevisionService
{
    Task<ExecutionPlan?> ApplyAsync(Guid id, string expectedFingerprint, ManualRevisionRole role,
        IReadOnlyList<GeneratedFile> replacements, CancellationToken cancellationToken);
}

public interface IProjectExport
{
    Task<byte[]> CreateAsync(Guid workspaceId, CancellationToken cancellationToken);
}

public interface IProjectGenerator
{
    Task<ProjectManifest> GenerateAsync(
        AgentDefinition agent, string task, ProjectManifest? previous, string? diagnostics,
        CancellationToken cancellationToken);
}

public interface ISandboxRunner
{
    Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);
    Task<SandboxAvailability> GetAvailabilityAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        GetAvailabilityAsync(cancellationToken);
    Task<VerificationResult> RunAsync(Guid workspaceId, string stage, CancellationToken cancellationToken);
}

public interface ILocalModelStatus
{
    Task<LocalModelsSnapshot> GetAsync(CancellationToken cancellationToken);
}

public sealed class StudioValidationException(string message) : Exception(message)
{
    public IReadOnlyList<PlannerValidationDiagnostic>? PlannerDiagnostics { get; init; }
    public IReadOnlyList<ReviewerValidationDiagnostic>? ReviewerDiagnostics { get; init; }
}
public sealed class StudioCapacityException(string message) : Exception(message);

public sealed record StudioOptions
{
    public ProjectSizingProfile[]? SizingProfiles { get; init; }
    public IReadOnlyList<ProjectSizingProfile> AvailableSizingProfiles => SizingProfiles ?? ProjectSizingProfile.Defaults;
    public StudioOptions ForMission(MissionSnapshot mission) => mission.Sizing?.Apply(this) ?? this;
    public int CorrectionLimitFor(MissionSnapshot mission) => Math.Min(
        mission.Workflow?.CorrectionLimit ?? ForMission(mission).MaxCorrectionAttempts,
        ForMission(mission).MaxCorrectionAttempts);
    public string DataDirectory { get; init; } = "..\\StudioData";
    public string DeveloperAgentId { get; init; } = "DeveloperAgent";
    public int MaxMissions { get; init; } = 200;
    public int MaxCorrectionAttempts { get; init; } = 3;
    public int DefaultMaxTotalGenerations { get; init; } = 10;
    public int MaxDevelopmentPlanTasks { get; init; } = 20;
    public int MaxDevelopmentPlanAttempts { get; init; } = 40;
    public int MissionTimeoutSeconds { get; init; } = 900;
    public int GenerationMaxOutputTokens { get; init; } = 6000;
    public int GenerationContextSize { get; init; } = 8192;
    public int MaxFiles { get; init; } = 40;
    public int MaxFileBytes { get; init; } = 65536;
    public int MaxProjectBytes { get; init; } = 262144;
    public int MaxImportFiles { get; init; } = 2000;
    public int MaxImportFileBytes { get; init; } = 262144;
    public int MaxImportBytes { get; init; } = 16777216;
    public bool CollaborativeWorkflows { get; init; }
    public int OllamaTraceMaxBytes { get; init; } = 1048576;
    public int OllamaTraceMaxFiles { get; init; } = 20;
    public int OllamaTraceRetentionDays { get; init; } = 7;

    public void Validate()
    {
        var profiles = AvailableSizingProfiles;
        if (profiles.Count is < 1 or > 8 || profiles.Any(profile => profile is null) ||
            profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Count)
            throw new ArgumentException("Provide 1 to 8 distinct bounded sizing profiles.");
        foreach (var profile in profiles) profile.Validate();
        if (string.IsNullOrWhiteSpace(DataDirectory) || string.IsNullOrWhiteSpace(DeveloperAgentId) ||
            MaxMissions is < 1 or > 10000 || MaxCorrectionAttempts is < 0 or > 3 ||
            DefaultMaxTotalGenerations is < 1 or > 1000 ||
            MaxDevelopmentPlanTasks is < 1 or > CustomAgentLimits.MaxDevelopmentPlanTasks ||
            MaxDevelopmentPlanAttempts is < 1 or > 200 ||
            MissionTimeoutSeconds is < 10 or > 7200 || MaxFiles is < 3 or > 100 ||
            MaxFileBytes is < 1024 or > 1048576 || MaxProjectBytes < MaxFileBytes ||
            MaxProjectBytes > 4194304 || GenerationContextSize is < 2048 or > ModelContextLimits.Maximum ||
            MaxImportFiles is < 1 or > 10000 || MaxImportFileBytes is < 1024 or > 1048576 ||
            MaxImportBytes < MaxImportFileBytes || MaxImportBytes > 67108864 ||
            GenerationMaxOutputTokens < 256 || GenerationMaxOutputTokens >= GenerationContextSize ||
            OllamaTraceMaxBytes is < 1024 or > 16777216 || OllamaTraceMaxFiles is < 1 or > 100 ||
            OllamaTraceRetentionDays is < 1 or > 30)
            throw new ArgumentException("Invalid Studio limits or data directory.");
    }
}
