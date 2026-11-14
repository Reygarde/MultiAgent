using AIPlatform.Core.Studio;

namespace AIPlatform.API;

/// <summary>Creates a queued chat or project mission; sandbox approval is explicit and scoped to this mission.</summary>
public sealed record CreateMissionRequest(
    string? Task, MissionKind Kind = MissionKind.Chat, string? AgentId = null, bool ApproveSandboxExecution = false,
    string? TemplateId = null);

/// <summary>Immutable view of a persisted Studio mission.</summary>
public sealed record MissionResponse(
    Guid Id, string Task, MissionKind Kind, string? RequestedAgent, bool SandboxApproved,
    MissionStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? SelectedAgent, string? SelectedModel, string? Reason, bool UsedFallback,
    string? Response, string? Error, bool HasWorkspace, int CorrectionAttempts,
    IReadOnlyList<MissionEventResponse> Events, IReadOnlyList<VerificationResponse> Verification,
    WorkflowResponse? Workflow = null, string? ProjectName = null, string? ProjectDescription = null, bool Deleted = false,
    string? TemplateId = null)
{
    public static MissionResponse From(MissionSnapshot mission) => new(
        mission.Id, mission.Task, mission.Kind, mission.RequestedAgent, mission.SandboxApproved,
        mission.Status, mission.CreatedAt, mission.UpdatedAt, mission.SelectedAgent, mission.SelectedModel,
        mission.Reason, mission.UsedFallback, mission.Response, mission.Error, mission.HasWorkspace,
        mission.CorrectionAttempts, mission.Events.Select(MissionEventResponse.From).ToArray(),
        mission.Verification.Select(VerificationResponse.From).ToArray(),
        mission.Workflow is { } flow ? WorkflowResponse.From(flow) : null,
        mission.ProjectName, mission.ProjectDescription, mission.Deleted, mission.TemplateId);
}

/// <summary>Observable workflow state, excluding raw prompts, draft source and internal model reasoning.</summary>
public sealed record WorkflowResponse(Guid WorkflowId, Guid MissionId, int Version,
    IReadOnlyList<WorkflowStepResponse> Steps, ProjectPlan? Plan, ReviewReport? Review,
    IReadOnlyList<RevisionResponse> Revisions, string? FinalReport, int? CorrectionLimit,
    IReadOnlyList<ProposalResponse> Proposals)
{
    public static WorkflowResponse From(MissionWorkflow flow)
    {
        int? previousVersion = null;
        var steps = flow.Steps.Select(step =>
        {
            var version = StepVersion(step, previousVersion);
            if (version > flow.Version) version = null;
            if (version is { } known) previousVersion = Math.Max(previousVersion ?? known, known);
            var agent = flow.Agents.Single(agent => agent.Id == step.AgentId);
            return new WorkflowStepResponse(step.Id, step.Kind, step.AgentId, step.DependsOn,
                step.Status, step.Attempts, step.StartedAt, step.DurationMilliseconds,
                step.Input, step.Output, step.Error,
                agent.Role is "Test" or "Orchestrator" ? null : flow.Models.Single(model => model.Id == agent.ModelId).Name,
                version);
        }).ToArray();
        return new(flow.WorkflowId, flow.MissionId, flow.Version, steps, flow.Plan, flow.Review, (flow.Revisions ?? []).Select(revision =>
            new RevisionResponse(revision.Version, revision.CreatedAt, revision.Reason, revision.Fingerprint,
                revision.Files.Select(file => file.Path).ToArray())).ToArray(), flow.FinalReport, flow.CorrectionLimit,
        GetProposals(flow).Select(item => new ProposalResponse(item.Version, item.CreatedAt, item.Review)).ToArray());
    }

    private static int? StepVersion(WorkflowStep step, int? previousVersion)
    {
        if (step.Id == "plan" && step.Kind == "Plan") return 1;
        var separator = step.Id.LastIndexOf('-');
        if (separator < 0 || !int.TryParse(step.Id.AsSpan(separator + 1), out var suffix) || suffix < 1) return null;
        var prefix = step.Id[..separator];
        // Resumption and report suffixes are graph positions, not revision numbers.
        if (prefix == "resume-review" && step.Kind == "Review") return previousVersion + 1;
        if (prefix == "report" && step.Kind == "Report") return previousVersion;
        var expected = step.Kind switch
        {
            "Generate" => "generate",
            "Review" => "review",
            "Correct" => "correct",
            "ErrorAnalysis" => "analysis",
            "Approval" => "approval",
            "TypeScript" => "typescript",
            "Build" => "build",
            "Tests" => "tests",
            "ManualRevision" => "manual-revision",
            "BackendBuild" => "backend-build",
            "BackendTests" => "backend-tests",
            "FrontendBuild" => "frontend-build",
            "FrontendTests" => "frontend-tests",
            "Integration" => "integration",
            _ => null
        };
        return prefix == expected ? suffix : null;
    }

    public static IReadOnlyList<CorrectionProposal> GetProposals(MissionWorkflow flow)
    {
        var proposals = (flow.Proposals ?? []).ToList();
        if (flow.Proposal is not null && proposals.All(item => item.Version != flow.Version) &&
            flow.Steps.Any(step => step.Id == $"correct-{flow.Version}" && step.Status == WorkflowStatus.Completed))
            proposals.Add(new(flow.Version, flow.Steps.Last(step => step.Id == $"correct-{flow.Version}").StartedAt
                ?? DateTimeOffset.MinValue, flow.Proposal, flow.Review));
        return proposals.OrderBy(item => item.Version).ToArray();
    }
}
/// <summary>Immutable project version metadata and explicitly changed source paths.</summary>
public sealed record RevisionResponse(int Version, DateTimeOffset CreatedAt, string Reason,
    string Fingerprint, IReadOnlyList<string> Files);
/// <summary>Persisted correction proposal and its review, whether or not it was applied.</summary>
public sealed record ProposalResponse(int Version, DateTimeOffset CreatedAt, ReviewReport? Review);
/// <summary>Source changes between one reviewed version and its previous recorded version.</summary>
public sealed record RevisionComparisonResponse(int Version, int? PreviousVersion,
    IReadOnlyList<RevisionFileChangeResponse> Changes, bool Proposed, ReviewReport? Review);
/// <summary>Before and after contents of a changed file within the mission workspace.</summary>
public sealed record RevisionFileChangeResponse(string Path, string? Before, string? After);
/// <summary>Actual execution step and frozen model assignment; version is derived for display, not persisted.</summary>
public sealed record WorkflowStepResponse(string Id, string Kind, string AgentId, IReadOnlyList<string> DependsOn,
    WorkflowStatus Status, int Attempts, DateTimeOffset? StartedAt, double? DurationMilliseconds,
    string? Input, string? Output, string? Error, string? ModelName, int? Version = null);
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
    double? DurationMilliseconds, int Attempt, int? WarningCount, int? ErrorCount, int? FailedTests,
    DiagnosticReport? Diagnostics)
{
    public static VerificationResponse From(VerificationResult item) =>
        new(item.Stage, item.Status, item.ExitCode, item.Log, item.TotalTests, item.DurationMilliseconds, item.Attempt,
            item.WarningCount, item.ErrorCount, item.FailedTests, item.Diagnostics);
}

/// <summary>Decision bound to the reviewed workspace and execution configuration fingerprint.</summary>
public sealed record DecideMissionApprovalRequest(bool Approved, string Fingerprint);

/// <summary>Human-reviewed replacements for editable full-stack backend files and their current plan fingerprint.</summary>
public sealed record ManualBackendRevisionRequest(string Fingerprint, IReadOnlyList<ManualBackendFileRequest> Files);
/// <summary>A single replacement backend source file.</summary>
public sealed record ManualBackendFileRequest(string Path, string Content);

/// <summary>Human-reviewed replacements for editable full-stack frontend files and their current plan fingerprint.</summary>
public sealed record ManualFrontendRevisionRequest(string Fingerprint, IReadOnlyList<ManualFrontendFileRequest> Files);
/// <summary>A single replacement frontend source file.</summary>
public sealed record ManualFrontendFileRequest(string Path, string Content);

/// <summary>Bounded offline build/test operations awaiting human approval; no arbitrary commands are accepted.</summary>
public sealed record ExecutionPlanResponse(
    string Fingerprint, string Image, int MemoryMegabytes, int CpuCount, int TimeoutSeconds,
    int MaxCorrectionAttempts, IReadOnlyList<string> Operations, CanonicalExecutionPlan? Canonical = null)
{
    public static ExecutionPlanResponse From(ExecutionPlan plan) => new(plan.Fingerprint, plan.Image,
        plan.MemoryMegabytes, plan.CpuCount, plan.TimeoutSeconds, plan.MaxCorrectionAttempts, plan.Operations, plan.Canonical);
}

/// <summary>Workspace file metadata; no absolute host path is exposed.</summary>
public sealed record WorkspaceFileResponse(string Path, long Size);

/// <summary>UTF-8 text preview of a validated generated file.</summary>
public sealed record WorkspaceContentResponse(string Path, string Content);

/// <summary>Static project metadata; scripts are listed by name and never executed.</summary>
public sealed record ProjectAnalysisResponse(bool? GitRepositoryDetected, IReadOnlyList<string> Languages,
    IReadOnlyList<string> Frameworks, IReadOnlyList<string> Solutions, IReadOnlyList<string> Projects,
    IReadOnlyList<string> PackageManifests, IReadOnlyList<string> TestProjects, IReadOnlyList<string> EntryPoints,
    IReadOnlyList<string> Directories, IReadOnlyList<ProjectDependency> Dependencies,
    IReadOnlyList<ProjectScript> Scripts, IReadOnlyList<string> DockerFiles, IReadOnlyList<AnalysisIssue> Issues)
{
    public static ProjectAnalysisResponse From(ProjectAnalysis analysis) => new(analysis.GitRepositoryDetected,
        analysis.Languages, analysis.Frameworks, analysis.Solutions, analysis.Projects, analysis.PackageManifests,
        analysis.TestProjects, analysis.EntryPoints, analysis.Directories, analysis.Dependencies, analysis.Scripts,
        analysis.DockerFiles, analysis.Issues);
}

/// <summary>Configured model's observed local installation, load and active-request state.</summary>
public sealed record LocalModelResponse(string Name, bool Installed, bool Loaded, bool InUse);

/// <summary>Ollama telemetry availability; an unavailable state never implies a model is uninstalled.</summary>
public sealed record LocalModelsResponse(bool Available, string? Error, IReadOnlyList<LocalModelResponse> Models);

/// <summary>Availability of offline Docker isolation; execution still requires mission approval.</summary>
public sealed record SandboxResponse(bool Available, string Reason, bool EngineAvailable = false);
