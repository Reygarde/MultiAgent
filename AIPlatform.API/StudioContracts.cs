using AIPlatform.Core.Studio;

namespace AIPlatform.API;

/// <summary>Creates a queued chat or project mission; sandbox approval is explicit and scoped to this mission.</summary>
public sealed record CreateMissionRequest(
    string? Task, MissionKind Kind = MissionKind.Chat, string? AgentId = null, bool ApproveSandboxExecution = false);

/// <summary>Immutable view of a persisted Studio mission.</summary>
public sealed record MissionResponse(
    Guid Id, string Task, MissionKind Kind, string? RequestedAgent, bool SandboxApproved,
    MissionStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? SelectedAgent, string? SelectedModel, string? Reason, bool UsedFallback,
    string? Response, string? Error, bool HasWorkspace, int CorrectionAttempts,
    IReadOnlyList<MissionEventResponse> Events, IReadOnlyList<VerificationResponse> Verification)
{
    public static MissionResponse From(MissionSnapshot mission) => new(
        mission.Id, mission.Task, mission.Kind, mission.RequestedAgent, mission.SandboxApproved,
        mission.Status, mission.CreatedAt, mission.UpdatedAt, mission.SelectedAgent, mission.SelectedModel,
        mission.Reason, mission.UsedFallback, mission.Response, mission.Error, mission.HasWorkspace,
        mission.CorrectionAttempts, mission.Events.Select(MissionEventResponse.From).ToArray(),
        mission.Verification.Select(VerificationResponse.From).ToArray());
}

/// <summary>Timestamped real execution event with monotonically increasing sequence.</summary>
public sealed record MissionEventResponse(int Sequence, DateTimeOffset Timestamp, string Step, StepStatus Status,
    string Message, double? DurationMilliseconds)
{
    public static MissionEventResponse From(MissionEvent item) =>
        new(item.Sequence, item.Timestamp, item.Step, item.Status, item.Message, item.DurationMilliseconds);
}

/// <summary>Actual container verification result or explicit unavailability; never inferred from generated text.</summary>
public sealed record VerificationResponse(string Stage, string Status, int? ExitCode, string Log, int? TotalTests,
    double? DurationMilliseconds, int Attempt, int? WarningCount, int? ErrorCount, int? FailedTests)
{
    public static VerificationResponse From(VerificationResult item) =>
        new(item.Stage, item.Status, item.ExitCode, item.Log, item.TotalTests, item.DurationMilliseconds, item.Attempt,
            item.WarningCount, item.ErrorCount, item.FailedTests);
}

/// <summary>Decision bound to the reviewed workspace and execution configuration fingerprint.</summary>
public sealed record DecideMissionApprovalRequest(bool Approved, string Fingerprint);

/// <summary>Bounded offline build/test operations awaiting human approval; no arbitrary commands are accepted.</summary>
public sealed record ExecutionPlanResponse(
    string Fingerprint, string Image, int MemoryMegabytes, int CpuCount, int TimeoutSeconds,
    int MaxCorrectionAttempts, IReadOnlyList<string> Operations)
{
    public static ExecutionPlanResponse From(ExecutionPlan plan) => new(plan.Fingerprint, plan.Image,
        plan.MemoryMegabytes, plan.CpuCount, plan.TimeoutSeconds, plan.MaxCorrectionAttempts, plan.Operations);
}

/// <summary>Workspace file metadata; no absolute host path is exposed.</summary>
public sealed record WorkspaceFileResponse(string Path, long Size);

/// <summary>UTF-8 text preview of a validated generated file.</summary>
public sealed record WorkspaceContentResponse(string Path, string Content);

/// <summary>Configured model's observed local installation, load and active-request state.</summary>
public sealed record LocalModelResponse(string Name, bool Installed, bool Loaded, bool InUse);

/// <summary>Ollama telemetry availability; an unavailable state never implies a model is uninstalled.</summary>
public sealed record LocalModelsResponse(bool Available, string? Error, IReadOnlyList<LocalModelResponse> Models);

/// <summary>Availability of offline Docker isolation; execution still requires mission approval.</summary>
public sealed record SandboxResponse(bool Available, string Reason, bool EngineAvailable = false);
