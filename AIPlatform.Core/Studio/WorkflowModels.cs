using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIPlatform.Core.Studio;

public enum WorkflowStatus { Pending, Running, Completed, Failed, WaitingForApproval, Cancelled, Skipped }

public sealed record PlannedFile(string Path, string Purpose, IReadOnlyList<string> DependsOn);
/// <summary>Human approval of a saved generation plan, never authorization to execute generated code.</summary>
public sealed record WorkflowPlanApproval(int Version, string Fingerprint, bool? Approved = null,
    DateTimeOffset? DecidedAt = null);
public sealed record WorkflowPlanAmendment(int PreviousVersion, DateTimeOffset PreparedAt,
    ProjectPlan PreviousPlan, IReadOnlyList<WorkflowCriterion> PreviousCriteria,
    IReadOnlyList<ReactRequirementTrace> PreviousTrace, WorkflowPlanApproval PreviousApproval,
    IReadOnlyList<WorkflowModelSequence> PreviousSelections, IReadOnlyList<GeneratedFile> PreviousDraft,
    string DeveloperModel, ModelCompatibility Compatibility, IReadOnlyList<FailureDiagnostic> RetainedSourceDiagnostics);
public sealed record ProjectPlan(string Name, string Description, IReadOnlyList<string> Decisions,
    IReadOnlyList<PlannedFile> Files)
{
    public string? OriginalRequest { get; init; }
    public IReadOnlyList<WorkflowCriterion>? AcceptanceCriteria { get; init; }
    public IReadOnlyList<PlannedCriterionCoverage>? CriterionCoverage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Dependencies { get; init; }
}
public sealed record ReviewFinding(string Path, string Severity, string Message);
public sealed record ReviewReport(string Summary, IReadOnlyList<ReviewFinding> Findings,
    string? Problem = null, IReadOnlyList<string>? ModifiedFiles = null, string? Justification = null,
    IReadOnlyList<string>? Risks = null, string? PlanCompliance = null, string? Recommendation = null)
{
    public IReadOnlyList<WorkflowCriterionReview>? CriterionReviews { get; init; }
}
public sealed record WorkflowCriterion(string Id, string Requirement);
public sealed record ReactRequirementTrace(string Id, string OriginalText, string NormalizedText,
    IReadOnlyList<string> Categories, IReadOnlyList<string> CriterionIds, string? Diagnostic = null);
public sealed record ReactRequirementNormalization(IReadOnlyList<WorkflowCriterion> Criteria,
    IReadOnlyList<ReactRequirementTrace> Trace);
public sealed record PlannedCriterionCoverage(string CriterionId, IReadOnlyList<string> Paths, string Explanation);
public sealed record WorkflowSourceEvidence(string Path, string Quote);
public sealed record WorkflowCriterionReview(string CriterionId, string Verdict, string Explanation,
    IReadOnlyList<WorkflowSourceEvidence> Evidence, string? Defect = null, string? Uncertainty = null);
public sealed record WorkflowCriterionExecution(string CriterionId, string TestFile, string TestName,
    int Attempt, int Version, string SourceFingerprint, string Provenance);
public enum ReactSoftwareCorrectionCallState
{
    Prepared, InFlight, DefinitelyNotSent, MayHaveBeenSent, ResponseReceived, ValidationRejected, Validated
}
public sealed record ReactCorrectionCallAttempt(Guid MissionId, Guid WorkflowId, int WorkflowVersion,
    string PlanFingerprint, Guid CorrectionId, Guid CallId, int GroupIndex, IReadOnlyList<string> GroupPaths,
    string SourceFingerprint, string CallFingerprint);
public sealed record ReactCorrectionResponseReceipt(ReactCorrectionCallAttempt? Attempt,
    Guid ReservationId, string? ResponseFingerprint, DateTimeOffset ReceivedAt);
public sealed record ReactCorrectionValidationRejection(ReactCorrectionResponseReceipt Receipt,
    IReadOnlyList<FailureDiagnostic> ExistingDiagnostics, IReadOnlyList<FailureDiagnostic> NewDiagnostics,
    IReadOnlyList<FailureDiagnostic> CorrectedDiagnostics, string Reason, Guid ValidationId,
    string? CandidateFingerprint, string? ResultFingerprint);
public sealed record ReactReadyCandidateResumption(Guid Id, Guid MissionId, Guid WorkflowId, int WorkflowVersion,
    Guid ReadyCorrectionId, Guid DeveloperCorrectionId, string PlanFingerprint, string CandidateFingerprint,
    string ReadyFingerprint, string WorkflowFingerprint, DateTimeOffset RequestedAt)
{
    public DateTimeOffset? RevalidatedAt { get; init; }
    public IReadOnlyList<FailureDiagnostic> Diagnostics { get; init; } = [];
    public string? RevalidationFingerprint { get; init; }
}
/// <summary>A private Planner validation failure; values must not be copied into general logs.</summary>
public sealed record PlannerValidationDiagnostic(string? CriterionId, string Rule, string Field,
    string? Path, string Explanation);
public sealed record PlannerDependencyNormalization(string Field, string Path, string Package);
/// <summary>A diagnostic-only Planner result, never an approved or executable mission plan.</summary>
public sealed record PlannerAttemptDiagnostic(Guid MissionId, Guid RunId, Guid AttemptId, int AttemptNumber,
    string Model, DateTimeOffset CreatedAt, string Status, JsonElement? Plan,
    IReadOnlyList<WorkflowCriterion> Criteria, IReadOnlyList<PlannerValidationDiagnostic> Errors,
    bool RawTraceAvailable = false,
    IReadOnlyList<PlannerDependencyNormalization>? DependencyNormalizations = null);
/// <summary>A private Reviewer validation error, never a finding against application code.</summary>
public sealed record ReviewerValidationDiagnostic(string? CriterionId, string Rule, string Field,
    string? Path, string? Quote, string Explanation);
/// <summary>A diagnostic-only model report bound to its exact review inputs, not an accepted review.</summary>
public sealed record ReviewerAttemptDiagnostic(Guid MissionId, Guid RunId, Guid AttemptId, int AttemptNumber,
    string StepId, string SourceFingerprint, string? RoutingPath, string Model, DateTimeOffset CreatedAt,
    string Status, JsonElement? Report, IReadOnlyList<ReviewerValidationDiagnostic> Errors);
public sealed record FailureDiagnostic(string Type, string? Project, string? File, int? Line, int? Column,
    string? Code, string Message);
public sealed record DiagnosticReport(string Stage, string Command, int? ExitCode,
    IReadOnlyList<FailureDiagnostic> Items, string Excerpt);
public sealed record ProjectRevision(int Version, DateTimeOffset CreatedAt, string Reason,
    string Fingerprint, IReadOnlyList<GeneratedFile> Files, ExecutionPlan? ExecutionPlan = null,
    Guid? WorkspaceId = null, Guid? SourceValidationRunId = null)
{
    public ExecutionPlan? PreparedExecutionPlan { get; init; }
    [JsonIgnore]
    public string ExecutionFingerprint => PreparedExecutionPlan?.Fingerprint ?? Fingerprint;
}
public sealed record CorrectionProposal(int Version, DateTimeOffset CreatedAt,
    IReadOnlyList<GeneratedFile> Files, ReviewReport? Review = null);
public sealed record WorkflowStep(string Id, string Kind, string AgentId, IReadOnlyList<string> DependsOn,
    WorkflowStatus Status = WorkflowStatus.Pending, int Attempts = 0,
    DateTimeOffset? StartedAt = null, double? DurationMilliseconds = null,
    string? Input = null, string? Output = null, string? Error = null);
public enum SoftwareAgentCallDeliveryState { Unknown, DefinitelyNotSent, MayHaveBeenSent }
public sealed record ReactSoftwareCorrection(Guid Id, string Trigger, string Reason,
    IReadOnlyList<GeneratedFile> Original, IReadOnlyList<GeneratedFile> Candidate,
    IReadOnlyList<LlmRequest> Calls, int ReviewerCapacity, IReadOnlyList<string> Decisions,
    int CompletedGroups = 0, bool InFlight = false, bool Ready = false)
{
    public string? Failure { get; init; }
    public ReactSoftwareCorrectionCallState CallState { get; init; } = ReactSoftwareCorrectionCallState.Prepared;
    public int? ActiveGroupIndex { get; init; }
    public Guid? ActiveReservationId { get; init; }
    public ReactCorrectionCallAttempt? ActiveAttempt { get; init; }
    public ReactCorrectionResponseReceipt? ResponseReceipt { get; init; }
    public ReactCorrectionValidationRejection? ValidationRejection { get; init; }
    public Guid? RetryAuthorizationId { get; init; }
    public Guid? RetriedFromId { get; init; }
    public Guid? ReadyResumptionId { get; init; }
    public Guid? RevalidatedFromId { get; init; }
}
public sealed record MissionWorkflow(Guid WorkflowId, Guid MissionId, IReadOnlyList<AgentDefinition> Agents,
    IReadOnlyList<ModelDefinition> Models, IReadOnlyList<WorkflowStep> Steps,
    ProjectPlan? Plan = null, ReviewReport? Review = null,
    IReadOnlyList<GeneratedFile>? Draft = null, IReadOnlyList<ProjectRevision>? Revisions = null,
    int Version = 1, string? Diagnostics = null, string? FinalReport = null,
    IReadOnlyList<GeneratedFile>? Proposal = null, IReadOnlyList<string>? CorrectionPaths = null,
    int? CorrectionLimit = null, IReadOnlyList<CorrectionProposal>? Proposals = null)
{
    public bool AutomaticReactCorrections { get; init; }
    public long SoftwareCheckpointVersion { get; init; }
    public string? PendingSoftwareAgentCall { get; init; }
    public int PendingSoftwareReservationCount { get; init; }
    public Guid? PendingSoftwareCallReservationId { get; init; }
    public int? PendingSoftwareCallReservationCount { get; init; }
    public SoftwareAgentCallDeliveryState PendingSoftwareCallDeliveryState { get; init; } =
        SoftwareAgentCallDeliveryState.Unknown;
    public IReadOnlyList<ReactSoftwareCorrection> SoftwareCorrections { get; init; } = [];
    public IReadOnlyList<ReactReadyCandidateResumption> ReadyCandidateResumptions { get; init; } = [];
    public bool AutomaticModelSelection { get; init; }
    public IReadOnlyList<WorkflowModelSequence> ModelSelections { get; init; } = [];
    public IReadOnlyList<WorkflowModelInvocation> ModelInvocations { get; init; } = [];
    public IReadOnlyList<WorkflowCriterion> AcceptanceCriteria { get; init; } = [];
    public IReadOnlyList<ReactRequirementTrace> RequirementTrace { get; init; } = [];
    public IReadOnlyList<WorkflowCriterionExecution> CriterionExecutions { get; init; } = [];
    public bool RecordPlannerTrace { get; init; }
    public bool RequirePlanApproval { get; init; }
    public WorkflowPlanApproval? PlanApproval { get; init; }
    public IReadOnlyList<WorkflowPlanAmendment> PlanAmendments { get; init; } = [];
    public int PlannerGenerationLimit { get; init; }
    public bool ReviewerCorrectionEnabled { get; init; }
    public ReviewerCorrectionResumeState? ReviewerCorrectionResume { get; init; }
    public IReadOnlyList<ExecutionPlanDifference>? ExecutionPlanDifferences { get; init; }

    // Provider-side resource failures may roll back step counters, but must not reopen the hard call budget.
    public int CountPlannerAttempts() => Math.Max(Steps.Single(step => step.Kind == "Plan").Attempts,
        ModelInvocations.Count(item => item.Attempt.Role == "Planner"));
}

/// <summary>Stable routing chain for one agent/file or operation across corrections and resumption.</summary>
public sealed record WorkflowModelSequence(string Key, string AgentId, ModelRoutingDecision Decision);
/// <summary>One actual provider invocation or resource block, linked to the existing model quality record.</summary>
public sealed record WorkflowModelInvocation(string StepId, string Sequence, CustomAgentModelAttempt Attempt);
/// <summary>Durable state for a source-bound retry of an interrupted Reviewer report correction.</summary>
public sealed record ReviewerCorrectionResumeState(Guid Id, string StepId, Guid RejectedAttemptId,
    string SourceFingerprint, string OriginalFailure, string Status, DateTimeOffset RequestedAt);

public static class WorkflowSourceFingerprint
{
    public static string Compute(MissionSnapshot mission)
    {
        var flow = mission.Workflow ?? throw new StudioValidationException("Mission workflow is missing.");
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { flow.Version, mission.Task, flow.AcceptanceCriteria, flow.Plan, flow.Draft })));
    }
}

public static class WorkflowGraph
{
    public static void Validate(IReadOnlyList<WorkflowStep> steps)
    {
        if (steps.Count is < 1 or > 100 || steps.Select(step => step.Id).Distinct().Count() != steps.Count)
            throw new StudioValidationException("Workflow identifiers must be unique within the bounded graph.");
        var completed = new HashSet<string>();
        while (completed.Count < steps.Count)
        {
            var ready = steps.Where(step => !completed.Contains(step.Id) &&
                step.DependsOn.All(completed.Contains)).ToArray();
            if (ready.Length == 0) throw new StudioValidationException("Workflow has cyclic or unknown dependencies.");
            foreach (var step in ready) completed.Add(step.Id);
        }
    }

    public static WorkflowStep Next(MissionWorkflow workflow)
    {
        Validate(workflow.Steps);
        return workflow.Steps.FirstOrDefault(step => step.Status == WorkflowStatus.Pending &&
            step.DependsOn.All(id => workflow.Steps.Single(dependency => dependency.Id == id).Status ==
                WorkflowStatus.Completed))
            ?? throw new StudioValidationException("No runnable workflow step has satisfied dependencies.");
    }
}
