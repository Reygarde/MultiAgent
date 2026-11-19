using System.Text.Json;
using System.Text.RegularExpressions;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

/// <summary>Explicit amendment of a failed, unpublished React plan, without generation or approval.</summary>
public sealed record PrepareReactPlanAmendmentRequest(DateTimeOffset ExpectedUpdatedAt,
    string PreviousApprovalFingerprint, ProjectPlan Plan, string DeveloperModel);

public interface IReactPlanAmendment
{
    Task<MissionSnapshot> PrepareAsync(Guid id, PrepareReactPlanAmendmentRequest request, CancellationToken token);
}

public sealed class ReactPlanAmendment(MissionStore missions, IModelLibraryService library,
    IReactSourceCheck sourceCheck, MissionExecutionGate gate, StudioOptions options) : IReactPlanAmendment
{
    public async Task<MissionSnapshot> PrepareAsync(Guid id, PrepareReactPlanAmendmentRequest request, CancellationToken token)
    {
        await gate.EnterAsync(token);
        try
        {
            var mission = missions.Get(id) ?? throw new KeyNotFoundException("Mission not found.");
            var flow = mission.Workflow;
            if (mission.TemplateId != MissionReactTemplate.Id || mission.Status != MissionStatus.Failed ||
                mission.Deleted || mission.Archived || mission.HasWorkspace || mission.GenerationBudgetExhausted ||
                flow is not { AutomaticModelSelection: true, RequirePlanApproval: true, Plan: not null,
                    PlanApproval.Approved: true, Draft: not null } ||
                flow.Revisions?.Count > 0 || flow.Proposal is not null || flow.PlanAmendments.Count >= 3 ||
                !flow.Steps.Select(step => step.Kind).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "Generate", "Plan", "Review" }) ||
                flow.Steps.Single(step => step.Kind == "Plan").Status != WorkflowStatus.Completed ||
                flow.Steps.Single(step => step.Kind == "Generate").Status != WorkflowStatus.Failed ||
                flow.Steps.Single(step => step.Kind == "Review").Status != WorkflowStatus.Pending)
                throw new ModelOperationConflictException("Only a failed unpublished initial React generation with an approved plan can be amended.");
            if (mission.UpdatedAt != request.ExpectedUpdatedAt ||
                flow.PlanApproval.Fingerprint != request.PreviousApprovalFingerprint)
                throw new ModelOperationConflictException("The mission or its previous approval changed; inspect it again.");
            if (request.Plan is null || request.Plan.Files is null ||
                JsonSerializer.Serialize(flow.Plan.Files) != JsonSerializer.Serialize(request.Plan.Files))
                throw new StudioValidationException("This bounded amendment must preserve every planned path, purpose and dependency.");
            var normalized = MissionReactTemplate.NormalizeRequirements(mission.Task, mission.Sizing);
            var plan = request.Plan with { OriginalRequest = mission.Task, AcceptanceCriteria = normalized.Criteria };
            MissionReactTemplate.ValidatePlan(plan, mission.Sizing);
            var reactVersions = plan.Decisions.SelectMany(decision => Regex.Matches(decision,
                    @"\bReact\s+(?<version>\d+(?:\.\d+)*(?:[-+][0-9A-Za-z.-]+)?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
                .Select(match => match.Groups["version"].Value).ToArray();
            if (!reactVersions.Contains(MissionReactTemplate.ReactVersion, StringComparer.Ordinal) ||
                reactVersions.Any(version => version != MissionReactTemplate.ReactVersion &&
                    version != MissionReactTemplate.ReactVersion.Split('.')[0]))
                throw new StudioValidationException("The amended decisions must state the exact locked React version and no conflicting major version.");
            var paths = plan.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            if (flow.Draft.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != flow.Draft.Count ||
                flow.Draft.Any(file => !paths.Contains(file.Path)) ||
                MissionReactTemplate.Files.Any(baseline =>
                    flow.Draft.SingleOrDefault(file => file.Path == baseline.Path)?.Content != baseline.Content))
                throw new StudioValidationException("The retained draft must match the planned paths and immutable scaffold.");
            var remaining = plan.Files.Count(file => flow.Draft.All(source => source.Path != file.Path));
            if (mission.MaxTotalGenerations is not { } limit || mission.GenerationReservations is not { } reservations ||
                limit - reservations.Count < remaining + 1)
                throw new ModelOperationConflictException("The remaining budget cannot cover the missing files and at least one review.");
            var developer = flow.Agents.Single(agent => agent.Enabled && agent.Role == "Developer");
            var profile = flow.Models.Single(model => model.Id == developer.ModelId);
            if (profile.Provider != "Ollama")
                throw new StudioValidationException("The frozen Developer must use local Ollama.");
            var inventory = await library.SnapshotAsync(profile.ContextSize, token);
            var name = ModelLibraryService.ValidateName(request.DeveloperModel);
            var model = inventory.Models.SingleOrDefault(item => item.Catalogue.Name == name);
            if (inventory.OllamaError is not null || model is not { Installed: true, Metadata.Completion: true } ||
                model.Metadata.ContextLength is not { } contextLength || contextLength < profile.ContextSize ||
                !model.Catalogue.Specializations.Contains("Code") ||
                model.Compatibility.Status is not ("Compatible" or "Borderline"))
                throw new ModelOperationConflictException("The requested code model is not admissible: " +
                    (inventory.OllamaError ?? model?.Compatibility.Reason ?? "installation, completion or context not verified."));
            var draftPaths = flow.Draft.Where(file => MissionReactTemplate.IsEditable(file.Path))
                .Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            var issues = (await sourceCheck.CheckAsync(flow.Draft, token)).Where(issue => draftPaths.Contains(issue.File))
                .Select(issue => new FailureDiagnostic("ReactSource", null, issue.File, issue.Line, issue.Column,
                    issue.Code, issue.Message)).ToArray();
            if (issues.Length > 0 && (limit - reservations.Count < remaining + 1 + issues.Select(issue => issue.File).Distinct().Count() ||
                mission.CorrectionAttempts >= options.CorrectionLimitFor(mission)))
                throw new ModelOperationConflictException("Known retained-source errors exceed the remaining generation or correction budget.");
            var decision = new ModelRoutingDecision(name, "ReactTypeScript",
                "Explicit code-model choice for this human-assisted plan amendment; resources must be checked again at execution.",
                "Moderate", [new(name, 0, model.Compatibility.Reason, model.Compatibility)]);
            token.ThrowIfCancellationRequested();
            return missions.PrepareReactPlanAmendment(id, mission.UpdatedAt, plan, normalized, decision, issues);
        }
        finally { gate.Exit(); }
    }
}
