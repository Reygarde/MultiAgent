using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIPlatform.Core.Studio;

public enum MissionKind { Chat, Project }
public enum MissionStatus { Queued, Running, Completed, GeneratedUnverified, Failed, Cancelled, AwaitingApproval, BuildFailed, TestsFailed, Verified }
public enum StepStatus { Running, Succeeded, Failed, Unavailable, Skipped, Cancelled }

public sealed record MissionSubmission(
    string Task, MissionKind Kind = MissionKind.Chat, string? AgentId = null, bool ApproveSandboxExecution = false,
    string? TemplateId = null);

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
    bool Deleted = false, string? TemplateId = null)
{
    public bool IsTerminal => Status is not (MissionStatus.Queued or MissionStatus.Running or MissionStatus.AwaitingApproval);
}

public sealed record GeneratedFile(string Path, string Content);
// For corrections, Files contains replacements only; omitted files are preserved.
public sealed record ProjectManifest(string Summary, IReadOnlyList<GeneratedFile> Files);
public sealed record ExecutionPlanFile(string Path, string Sha256);
public sealed record ExecutionPlanStage(string Name, IReadOnlyList<string> Arguments);
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
    int MaxCorrectionAttempts, IReadOnlyList<string> Operations, CanonicalExecutionPlan? Canonical = null);

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
    MissionSnapshot Submit(MissionSubmission submission);
    IReadOnlyList<MissionSnapshot> List();
    MissionSnapshot? Get(Guid id);
    bool Cancel(Guid id);
    bool DecideApproval(Guid id, bool approved, string fingerprint);
    bool Resume(Guid id);
    bool PrepareRevalidation(Guid id, DateTimeOffset expectedUpdatedAt, ExecutionPlan plan);
    bool RenameProject(Guid id, string name, string description);
    bool DeleteProject(Guid id);
}

public interface IWorkspaceStore
{
    void Create(Guid id);
    void Delete(Guid id);
    void ValidateFiles(IReadOnlyList<GeneratedFile> files, string? templateId = null, bool allowPartialTemplate = false);
    Task WriteAsync(Guid id, IReadOnlyList<GeneratedFile> files, CancellationToken cancellationToken, string? templateId = null);
    IReadOnlyList<WorkspaceFile> List(Guid id);
    Task<WorkspaceContent> ReadAsync(Guid id, string path, CancellationToken cancellationToken);
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
    public int MaxFiles { get; init; } = 40;
    public int MaxFileBytes { get; init; } = 65536;
    public int MaxProjectBytes { get; init; } = 262144;
    public int MaxImportFiles { get; init; } = 2000;
    public int MaxImportFileBytes { get; init; } = 262144;
    public int MaxImportBytes { get; init; } = 16777216;
    public bool CollaborativeWorkflows { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory) || string.IsNullOrWhiteSpace(DeveloperAgentId) ||
            MaxMissions is < 1 or > 10000 || MaxCorrectionAttempts is < 0 or > 3 ||
            MissionTimeoutSeconds is < 10 or > 7200 || MaxFiles is < 3 or > 100 ||
            MaxFileBytes is < 1024 or > 1048576 || MaxProjectBytes < MaxFileBytes ||
            MaxProjectBytes > 4194304 || GenerationContextSize is < 2048 or > 32768 ||
            MaxImportFiles is < 1 or > 10000 || MaxImportFileBytes is < 1024 or > 1048576 ||
            MaxImportBytes < MaxImportFileBytes || MaxImportBytes > 67108864 ||
            GenerationMaxOutputTokens < 256 || GenerationMaxOutputTokens >= GenerationContextSize)
            throw new ArgumentException("Invalid Studio limits or data directory.");
    }
}
