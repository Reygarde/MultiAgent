namespace AIPlatform.Core.Studio;

public enum WorkflowStatus { Pending, Running, Completed, Failed, WaitingForApproval, Cancelled, Skipped }

public sealed record PlannedFile(string Path, string Purpose, IReadOnlyList<string> DependsOn);
public sealed record ProjectPlan(string Name, string Description, IReadOnlyList<string> Decisions,
    IReadOnlyList<PlannedFile> Files);
public sealed record ReviewFinding(string Path, string Severity, string Message);
public sealed record ReviewReport(string Summary, IReadOnlyList<ReviewFinding> Findings,
    string? Problem = null, IReadOnlyList<string>? ModifiedFiles = null, string? Justification = null,
    IReadOnlyList<string>? Risks = null, string? PlanCompliance = null, string? Recommendation = null);
public sealed record FailureDiagnostic(string Type, string? Project, string? File, int? Line, int? Column,
    string? Code, string Message);
public sealed record DiagnosticReport(string Stage, string Command, int? ExitCode,
    IReadOnlyList<FailureDiagnostic> Items, string Excerpt);
public sealed record ProjectRevision(int Version, DateTimeOffset CreatedAt, string Reason,
    string Fingerprint, IReadOnlyList<GeneratedFile> Files, ExecutionPlan? ExecutionPlan = null);
public sealed record CorrectionProposal(int Version, DateTimeOffset CreatedAt,
    IReadOnlyList<GeneratedFile> Files, ReviewReport? Review = null);
public sealed record WorkflowStep(string Id, string Kind, string AgentId, IReadOnlyList<string> DependsOn,
    WorkflowStatus Status = WorkflowStatus.Pending, int Attempts = 0,
    DateTimeOffset? StartedAt = null, double? DurationMilliseconds = null,
    string? Input = null, string? Output = null, string? Error = null);
public sealed record MissionWorkflow(Guid WorkflowId, Guid MissionId, IReadOnlyList<AgentDefinition> Agents,
    IReadOnlyList<ModelDefinition> Models, IReadOnlyList<WorkflowStep> Steps,
    ProjectPlan? Plan = null, ReviewReport? Review = null,
    IReadOnlyList<GeneratedFile>? Draft = null, IReadOnlyList<ProjectRevision>? Revisions = null,
    int Version = 1, string? Diagnostics = null, string? FinalReport = null,
    IReadOnlyList<GeneratedFile>? Proposal = null, IReadOnlyList<string>? CorrectionPaths = null,
    int? CorrectionLimit = null, IReadOnlyList<CorrectionProposal>? Proposals = null);

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
