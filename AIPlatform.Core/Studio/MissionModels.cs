namespace AIPlatform.Core.Studio;

public enum MissionKind { Chat, Project }
public enum MissionStatus { Queued, Running, Completed, GeneratedUnverified, Failed, Cancelled, AwaitingApproval, BuildFailed, TestsFailed, Verified }
public enum StepStatus { Running, Succeeded, Failed, Unavailable, Skipped, Cancelled }

public sealed record MissionSubmission(
    string Task, MissionKind Kind = MissionKind.Chat, string? AgentId = null, bool ApproveSandboxExecution = false);

public sealed record MissionEvent(
    int Sequence, DateTimeOffset Timestamp, string Step, StepStatus Status, string Message, double? DurationMilliseconds = null);

public sealed record VerificationResult(
    string Stage, string Status, int? ExitCode, string Log, int? TotalTests = null,
    double? DurationMilliseconds = null, int Attempt = 0,
    int? WarningCount = null, int? ErrorCount = null, int? FailedTests = null);

public sealed record MissionSnapshot(
    Guid Id, string Task, MissionKind Kind, string? RequestedAgent,
    bool SandboxApproved, MissionStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? SelectedAgent, string? SelectedModel,
    string? Reason, bool UsedFallback, string? Response, string? Error,
    bool HasWorkspace, int CorrectionAttempts, IReadOnlyList<MissionEvent> Events,
    IReadOnlyList<VerificationResult> Verification, string? ApprovalFingerprint = null)
{
    public bool IsTerminal => Status is not (MissionStatus.Queued or MissionStatus.Running or MissionStatus.AwaitingApproval);
}

public sealed record GeneratedFile(string Path, string Content);
// For corrections, Files contains replacements only; omitted files are preserved.
public sealed record ProjectManifest(string Summary, IReadOnlyList<GeneratedFile> Files);
public sealed record ExecutionPlan(
    string Fingerprint, string Image, int MemoryMegabytes, int CpuCount, int TimeoutSeconds,
    int MaxCorrectionAttempts, IReadOnlyList<string> Operations);

public interface IExecutionPlanService
{
    Task<ExecutionPlan> GetAsync(Guid id, CancellationToken cancellationToken);
}
public sealed record WorkspaceFile(string Path, long Size);
public sealed record WorkspaceContent(string Path, string Content);
public sealed record SandboxAvailability(bool Available, string Reason, bool EngineAvailable = false);
public sealed record LocalModelState(string Name, bool Installed, bool Loaded, bool InUse);
public sealed record LocalModelsSnapshot(bool Available, string? Error, IReadOnlyList<LocalModelState> Models);

public interface IMissionService
{
    MissionSnapshot Submit(MissionSubmission submission);
    IReadOnlyList<MissionSnapshot> List();
    MissionSnapshot? Get(Guid id);
    bool Cancel(Guid id);
    bool DecideApproval(Guid id, bool approved, string fingerprint);
}

public interface IWorkspaceStore
{
    void Create(Guid id);
    Task WriteAsync(Guid id, IReadOnlyList<GeneratedFile> files, CancellationToken cancellationToken);
    IReadOnlyList<WorkspaceFile> List(Guid id);
    Task<WorkspaceContent> ReadAsync(Guid id, string path, CancellationToken cancellationToken);
    string GetDirectory(Guid id);
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
    Task<VerificationResult> RunAsync(Guid workspaceId, string stage, CancellationToken cancellationToken);
}

public interface ILocalModelStatus
{
    Task<LocalModelsSnapshot> GetAsync(CancellationToken cancellationToken);
}

public sealed class StudioValidationException(string message) : Exception(message);
public sealed class StudioCapacityException(string message) : Exception(message);

public sealed record StudioOptions
{
    public string DataDirectory { get; init; } = "..\\StudioData";
    public string DeveloperAgentId { get; init; } = "DeveloperAgent";
    public int MaxMissions { get; init; } = 200;
    public int MaxCorrectionAttempts { get; init; } = 3;
    public int MissionTimeoutSeconds { get; init; } = 900;
    public int GenerationMaxOutputTokens { get; init; } = 6000;
    public int GenerationContextSize { get; init; } = 8192;
    public int MaxFiles { get; init; } = 30;
    public int MaxFileBytes { get; init; } = 65536;
    public int MaxProjectBytes { get; init; } = 262144;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory) || string.IsNullOrWhiteSpace(DeveloperAgentId) ||
            MaxMissions is < 1 or > 10000 || MaxCorrectionAttempts is < 0 or > 3 ||
            MissionTimeoutSeconds is < 10 or > 7200 || MaxFiles is < 3 or > 100 ||
            MaxFileBytes is < 1024 or > 1048576 || MaxProjectBytes < MaxFileBytes ||
            MaxProjectBytes > 4194304 || GenerationContextSize is < 2048 or > 32768 ||
            GenerationMaxOutputTokens < 256 || GenerationMaxOutputTokens >= GenerationContextSize)
            throw new ArgumentException("Invalid Studio limits or data directory.");
    }
}
