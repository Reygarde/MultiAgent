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
    IReadOnlyList<MissionEventResponse> Events, IReadOnlyList<VerificationResponse> Verification,
    WorkflowResponse? Workflow = null, string? ProjectName = null, string? ProjectDescription = null, bool Deleted = false)
{
    public static MissionResponse From(MissionSnapshot mission) => new(
        mission.Id, mission.Task, mission.Kind, mission.RequestedAgent, mission.SandboxApproved,
        mission.Status, mission.CreatedAt, mission.UpdatedAt, mission.SelectedAgent, mission.SelectedModel,
        mission.Reason, mission.UsedFallback, mission.Response, mission.Error, mission.HasWorkspace,
        mission.CorrectionAttempts, mission.Events.Select(MissionEventResponse.From).ToArray(),
        mission.Verification.Select(VerificationResponse.From).ToArray(),
        mission.Workflow is { } flow ? WorkflowResponse.From(flow) : null,
        mission.ProjectName, mission.ProjectDescription, mission.Deleted);
}

/// <summary>Observable workflow state, excluding raw prompts, draft source and internal model reasoning.</summary>
public sealed record WorkflowResponse(Guid WorkflowId, Guid MissionId, int Version,
    IReadOnlyList<WorkflowStepResponse> Steps, ProjectPlan? Plan, ReviewReport? Review,
    IReadOnlyList<RevisionResponse> Revisions, string? FinalReport)
{
    public static WorkflowResponse From(MissionWorkflow flow) => new(flow.WorkflowId, flow.MissionId, flow.Version,
        flow.Steps.Select(step =>
        {
            var agent = flow.Agents.Single(agent => agent.Id == step.AgentId);
            return new WorkflowStepResponse(step.Id, step.Kind, step.AgentId, step.DependsOn,
                step.Status, step.Attempts, step.StartedAt, step.DurationMilliseconds,
                step.Input, step.Output, step.Error,
                agent.Role is "Test" or "Orchestrator" ? null : flow.Models.Single(model => model.Id == agent.ModelId).Name);
        }).ToArray(), flow.Plan, flow.Review, (flow.Revisions ?? []).Select(revision =>
            new RevisionResponse(revision.Version, revision.CreatedAt, revision.Reason, revision.Fingerprint,
                revision.Files.Select(file => file.Path).ToArray())).ToArray(), flow.FinalReport);
}
/// <summary>Immutable project version metadata and explicitly changed source paths.</summary>
public sealed record RevisionResponse(int Version, DateTimeOffset CreatedAt, string Reason,
    string Fingerprint, IReadOnlyList<string> Files);
/// <summary>Actual execution step and frozen model assignment; null model means deterministic C#.</summary>
public sealed record WorkflowStepResponse(string Id, string Kind, string AgentId, IReadOnlyList<string> DependsOn,
    WorkflowStatus Status, int Attempts, DateTimeOffset? StartedAt, double? DurationMilliseconds,
    string? Input, string? Output, string? Error, string? ModelName);
/// <summary>Short human-readable project metadata, independent of the original mission prompt.</summary>
public sealed record UpdateProjectRequest(string Name, string Description);

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
