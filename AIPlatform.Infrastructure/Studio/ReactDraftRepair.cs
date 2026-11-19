using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

/// <summary>Ordered, explicitly scoped draft correction groups; preparation never calls a model.</summary>
public sealed record PrepareReactDraftRepairRequest(ReactCorrectionGroup[] Groups);
/// <summary>Explicit generation request bound to the exact successful preflight.</summary>
public sealed record StartReactDraftRepairRequest(PrepareReactDraftRepairRequest Scope, string Fingerprint);
/// <summary>Prompt and conservative output measurements including dependency growth.</summary>
public sealed record ReactDraftGroupMeasure(string[] Paths, int ContextCharacters, int StressedContextCharacters, int BaselineJsonBytes,
    int EstimatedOutputTokens, int SafeOutputTokens, string[] Dependencies)
{
    public string ReplyContract { get; init; } = "Files";
    public int CompleteFilesJsonBytes { get; init; }
    public int CompleteFilesContextCharacters { get; init; }
}
/// <summary>Read-only AST projection, protected review capacity and reasons to refuse generation.</summary>
public sealed record ReactDraftRepairPlan(string Fingerprint, string SourcePlanFingerprint, string Model,
    int ConsumedGenerations, int RemainingGenerations, int ContextLimit, string OutputEstimateMethod,
    IReadOnlyList<ReactDraftGroupMeasure> DeveloperGroups,
    IReadOnlyList<WorkflowAgents.ReactReviewGroupMeasure> ReviewerGroups,
    IReadOnlyList<WorkflowAgents.ReactReviewGroupMeasure> ProjectedReviewerGroups,
    IReadOnlyList<ReactSourceIssue> Before, IReadOnlyList<ReactSourceIssue> RemainingDiagnostics,
    IReadOnlyList<ReactSourceEdit> Edits, IReadOnlyList<ReactRepairRefusal> Refusals,
    IReadOnlyList<string> Blockers)
{
    public bool CanStart => Blockers.Count == 0;
}
public sealed record ReactDraftRepairCheckpoint(Guid Id, Guid MissionId, string SourceFingerprint, int BaseReservations,
    ReactDraftRepairPlan Plan, IReadOnlyList<GeneratedFile> Original, IReadOnlyList<GeneratedFile> Candidate,
    LlmRequest[] Calls, int CompletedGroups = 0, bool InFlight = false, bool Ready = false)
{
    public int CorrectionAttemptsBefore { get; init; }
    public IReadOnlyList<ReactAppliedPatch> PatchEdits { get; init; } = [];
}
/// <summary>Durable repair progress and validated patch excerpts, without complete source files or provider requests.</summary>
public sealed record ReactDraftRepairStatus(Guid Id, int CompletedGroups, bool InFlight, bool Ready,
    int ConsumedGenerations, ReactDraftRepairPlan Plan)
{
    public IReadOnlyList<ReactAppliedPatch> PatchEdits { get; init; } = [];
}

public interface IReactDraftRepair
{
    Task<ReactDraftRepairPlan> PrepareAsync(Guid id, PrepareReactDraftRepairRequest scope, CancellationToken token);
    Task<ReactDraftRepairStatus> StartAsync(Guid id, StartReactDraftRepairRequest request, CancellationToken token);
    Task<ReactDraftRepairStatus?> StatusAsync(Guid id, CancellationToken token);
}
public sealed class ReactDraftRepair(MissionStore missions, IWorkspaceStore workspaces, IReactSourceCheck checks,
    IReactSourceRepair repairs, ILLMProviderResolver providers, OllamaOptions ollama, MissionExecutionGate gate,
    StudioOptions options) : IReactDraftRepair
{
    private static readonly JsonSerializerOptions Json = ReactRevisionCorrection.Json;
    private const string EstimateMethod =
        "No exact tokenizer or persisted successful per-file eval_count is available. " +
        "Conservative UTF-8 byte upper bound of baseline JSON plus 25% growth; no reasoning (think=false). " +
        "Require 20% output headroom. New code can exceed this estimate; this is not a completion guarantee.";

    internal static string SourcePlanFingerprint(MissionSnapshot mission) => Hash(new
    {
        mission.Id, mission.Task, mission.TemplateId, mission.Workflow!.Version, mission.Workflow.Plan,
        mission.Workflow.PlanApproval, mission.Workflow.AcceptanceCriteria, mission.Workflow.RequirementTrace,
        mission.Workflow.Agents, mission.Workflow.Models, mission.Workflow.CorrectionLimit
    });

    private static string Hash(object value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));

    public async Task<ReactDraftRepairPlan> PrepareAsync(Guid id, PrepareReactDraftRepairRequest scope, CancellationToken token)
    {
        var mission = missions.GetLatestForRepair(id);
        if (missions.GetDraftRepair(id) is not null)
            throw new StudioValidationException("A durable repair already exists. Inspect its status and resume its saved fingerprint; do not prepare a competing repair.");
        return (await BuildAsync(mission, scope, token)).Plan;
    }

    public Task<ReactDraftRepairStatus?> StatusAsync(Guid id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = missions.GetLatestForRepair(id);
        var state = missions.GetDraftRepair(id);
        return Task.FromResult(state is null ? null : Status(state));
    }

    private ReactDraftRepairStatus Status(ReactDraftRepairCheckpoint state) =>
        new(state.Id, state.CompletedGroups, state.InFlight, state.Ready,
            missions.GetLatestForRepair(state.MissionId).GenerationReservations!.Count, state.Plan)
            { PatchEdits = state.PatchEdits };

    private async Task<(ReactDraftRepairPlan Plan, ReactSourceRepairResult Projection, LlmRequest[] Calls)>
        BuildAsync(MissionSnapshot mission, PrepareReactDraftRepairRequest scope, CancellationToken token)
    {
        var flow = mission.Workflow ?? throw new StudioValidationException("A React workflow is required.");
        MissionStore.RequireApprovedPlan(mission);
        if (mission.TemplateId != MissionReactTemplate.Id || mission.Status != MissionStatus.Failed ||
            mission.HasWorkspace || mission.Deleted || mission.Archived || flow.Plan is null || flow.Draft is null ||
            flow.Revisions?.Count > 0 || flow.RequirePlanApproval && flow.PlanApproval?.Approved != true)
            throw new StudioValidationException("An approved failed React v2 draft without publication is required.");
        var original = flow.Draft;
        workspaces.ValidateFiles(original, mission.TemplateId, sizing: mission.Sizing);
        MissionReactTemplate.Validate(original);
        MissionReactTemplate.ValidatePlan(flow.Plan, mission.Sizing);
        if (flow.Plan.Files.Any(file => original.All(source => source.Path != file.Path)))
            throw new StudioValidationException("Finish the planned draft before grouped repair.");
        if (!flow.Plan.Files.Where(file => MissionReactTemplate.IsEditable(file.Path)).Select(file => file.Path)
            .Order(StringComparer.Ordinal).SequenceEqual(original.Where(file => MissionReactTemplate.IsEditable(file.Path))
                .Select(file => file.Path).Order(StringComparer.Ordinal)))
            throw new StudioValidationException("The editable draft manifest must match the approved plan exactly.");
        if (scope?.Groups is not { Length: > 0 } || scope.Groups.Length > (mission.Sizing?.MaxRepairGroups ?? 3) ||
            scope.Groups.Any(group => group is null || group.Paths is not { Length: > 0 } ||
                group.Paths.Length > (mission.Sizing?.MaxFilesPerRepairGroup ?? 4) ||
                string.IsNullOrWhiteSpace(group.Instructions) || group.Instructions.Length > 1200))
            throw new StudioValidationException("Repair groups or paths exceed the mission limits, or coordination instructions are missing.");
        var allowed = scope.Groups.SelectMany(group => group.Paths).ToArray();
        if (allowed.Distinct(StringComparer.Ordinal).Count() != allowed.Length ||
            allowed.Any(path => path is null || !MissionReactTemplate.IsEditable(path) ||
                original.All(file => file.Path != path) || flow.Plan.Files.All(file => file.Path != path)))
            throw new StudioValidationException("Duplicate, absent or unauthorized repair target.");
        var developer = flow.Agents.Single(agent => agent.Enabled && agent.Role == "Developer");
        var model = flow.Models.Single(item => item.Id == developer.ModelId);
        if (!developer.AllowedTools.Contains("GenerateFiles") || model.Provider != "Ollama" ||
            !Uri.TryCreate(ollama.BaseUrl, UriKind.Absolute, out var url) || !url.IsLoopback)
            throw new StudioValidationException("A permitted Developer and frozen local Ollama profile are required.");
        var routes = allowed.Select(path => flow.ModelSelections.SingleOrDefault(item =>
            item.Key == developer.Id + ":" + path)?.Decision.Model ?? model.Name).Distinct(StringComparer.Ordinal).ToArray();
        if (routes.Length != 1)
            throw new StudioValidationException("Group targets have incompatible frozen Developer routes; do not silently reroute them.");
        model = model with { Name = routes[0] };
        var projection = await repairs.RepairAsync(original, original.Where(file => MissionReactTemplate.IsEditable(file.Path))
            .Select(file => file.Path).ToArray(), token);
        var candidate = projection.Files;
        workspaces.ValidateFiles(candidate, mission.TemplateId, sizing: mission.Sizing);
        MissionReactTemplate.Validate(candidate);
        var rechecked = await checks.CheckAsync(candidate, token);
        if (!projection.Issues.SequenceEqual(rechecked))
            throw new StudioValidationException("AST projection and independent source recheck disagree.");
        var sources = candidate.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var consumed = mission.GenerationReservations?.Count ?? throw new StudioValidationException("Missing global generation ledger.");
        var remaining = (mission.MaxTotalGenerations ?? throw new StudioValidationException("A global generation limit is required.")) - consumed;
        var context = (model.ContextSize - model.MaxOutputTokens) * 2;
        var calls = scope.Groups.Select((group, index) => ReactRevisionCorrection.BuildGroupCall(mission, developer, model,
            group, sources, rechecked.Where(issue => group.Paths.Contains(issue.File, StringComparer.Ordinal)),
            remaining - index)).ToArray();
        var grown = candidate.Select(file => allowed.Contains(file.Path, StringComparer.Ordinal)
            ? file with { Content = file.Content + "\n/*" + new string('x', (int)Math.Ceiling(file.Content.Length * 0.25)) + "*/" }
            : file).ToArray();
        var stressed = grown.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var measures = calls.Select((call, index) =>
        {
            var targets = scope.Groups[index].Paths.Select(path => sources[path]).ToArray();
            var completeBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { files = targets }, Json));
            var bytes = ReactCorrectionPatches.BaselineBytes(call, scope.Groups[index].Paths, sources);
            var completeCall = scope.Groups[index].PatchTargets is null ? call :
                ReactRevisionCorrection.BuildGroupCall(mission, developer, model, scope.Groups[index] with { PatchTargets = null },
                    sources, rechecked.Where(issue => scope.Groups[index].Paths.Contains(issue.File, StringComparer.Ordinal)),
                    remaining - index);
            using var input = JsonDocument.Parse(call.Messages[1].Content);
            return new ReactDraftGroupMeasure(scope.Groups[index].Paths, ReactRevisionCorrection.CallSize(call),
                ReactRevisionCorrection.CallSize(ReactRevisionCorrection.RefreshDependencies(call, stressed, int.MaxValue)),
                bytes, (int)Math.Ceiling(bytes * 1.25), (int)Math.Floor(model.MaxOutputTokens * 0.8),
                input.RootElement.GetProperty("dependencies").EnumerateArray().Select(file => file.GetProperty("path").GetString()!).ToArray())
            {
                ReplyContract = scope.Groups[index].PatchTargets is null ? "Files" : "Patches",
                CompleteFilesJsonBytes = completeBytes,
                CompleteFilesContextCharacters = ReactRevisionCorrection.CallSize(completeCall)
            };
        }).ToArray();
        var scopedMission = mission with { Workflow = flow with { Draft = candidate } };
        var review = WorkflowAgents.PreviewReactReview(scopedMission, token);
        var boundedReview = WorkflowAgents.PreviewReactReview(mission with { Workflow = flow with { Draft = grown } }, token);
        var blockers = new List<string>();
        if (mission.GenerationBudgetExhausted || remaining < calls.Length + boundedReview.Count)
            blockers.Add($"Global budget needs {calls.Length} Developer + {boundedReview.Count} Reviewer (25% source-growth stress), but only {remaining} remain.");
        if (mission.CorrectionAttempts >= options.CorrectionLimitFor(mission))
            blockers.Add("The correction attempt limit has been reached.");
        if (rechecked.Any(issue => !allowed.Contains(issue.File, StringComparer.Ordinal)))
            blockers.Add("Residual diagnostics identify files outside the proposed Developer groups.");
        foreach (var group in measures)
        {
            if (group.ContextCharacters > context)
                blockers.Add($"[{string.Join(", ", group.Paths)}]: input {group.ContextCharacters} exceeds {context} characters.");
            else if (group.StressedContextCharacters > context)
                blockers.Add($"[{string.Join(", ", group.Paths)}]: input with growing dependencies {group.StressedContextCharacters} exceeds {context} characters.");
            if (group.EstimatedOutputTokens > group.SafeOutputTokens)
                blockers.Add($"[{string.Join(", ", group.Paths)}]: conservative output {group.EstimatedOutputTokens} exceeds safe {group.SafeOutputTokens} tokens.");
        }
        var sourcePlan = SourcePlanFingerprint(mission);
        var fingerprint = Hash(new { Source = WorkflowSourceFingerprint.Compute(mission), sourcePlan, consumed,
            mission.CorrectionAttempts, EffectiveCorrectionLimit = options.CorrectionLimitFor(mission),
            scope, projection.Edits, Calls = calls, Review = review, BoundedReview = boundedReview, EstimateMethod });
        return (new(fingerprint, sourcePlan, model.Name, consumed, remaining, context, EstimateMethod, measures, boundedReview, review,
            projection.Before, rechecked, projection.Edits, projection.Refusals, blockers), projection, calls);
    }

    public async Task<ReactDraftRepairStatus> StartAsync(Guid id, StartReactDraftRepairRequest request, CancellationToken token)
    {
        await gate.EnterAsync(token);
        try
        {
            var state = missions.GetDraftRepair(id);
            if (state is null)
            {
                var mission = missions.GetLatestForRepair(id);
                var prepared = await BuildAsync(mission, request.Scope, token);
                if (prepared.Plan.Fingerprint != request.Fingerprint || !prepared.Plan.CanStart)
                    throw new StudioValidationException("Preflight refused or fingerprint changed. No generation was requested.");
                state = new(Guid.NewGuid(), id, WorkflowSourceFingerprint.Compute(mission),
                    prepared.Plan.ConsumedGenerations, prepared.Plan, mission.Workflow!.Draft!, prepared.Projection.Files, prepared.Calls)
                    { CorrectionAttemptsBefore = mission.CorrectionAttempts };
                missions.BeginDraftRepair(state);
            }
            if (state.Plan.Fingerprint != request.Fingerprint)
                throw new StudioValidationException("The durable repair belongs to a different preflight.");
            if (state.Ready) return Status(state);
            var current = missions.GetLatestForRepair(id);
            if (SourcePlanFingerprint(current) != state.Plan.SourcePlanFingerprint ||
                !current.Workflow!.Draft!.SequenceEqual(state.Original) && !current.Workflow.Draft!.SequenceEqual(state.Candidate))
                throw new StudioValidationException("Original draft or frozen plan changed. Repair stopped.");
            var paid = current.GenerationReservations!.Count(item => item.CapacityId == state.Id && item.Role == "Developer");
            if (paid != state.CompletedGroups)
                throw new StudioValidationException("An interrupted or failed generation was already charged without a complete checkpoint. No automatic replay.");
            if (state.InFlight)
                state = missions.SaveDraftRepair(id, state.Id, item => item with { InFlight = false });
            for (var index = state.CompletedGroups; index < state.Calls.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                current = missions.GetLatestForRepair(id);
                if (current.Status != MissionStatus.Failed || current.Deleted || current.Archived ||
                    SourcePlanFingerprint(current) != state.Plan.SourcePlanFingerprint ||
                    !current.Workflow!.Draft!.SequenceEqual(state.Original))
                    throw new StudioValidationException("Mission or original draft changed during correction.");
                var candidate = state.Candidate.ToDictionary(file => file.Path, StringComparer.Ordinal);
                var measure = state.Plan.DeveloperGroups[index];
                var previousIssues = await checks.CheckAsync(state.Candidate, token);
                var call = ReactRevisionCorrection.RefreshDependencies(state.Calls[index], candidate, state.Plan.ContextLimit,
                    previousIssues.Where(issue => measure.Paths.Contains(issue.File, StringComparer.Ordinal)),
                    current.MaxTotalGenerations!.Value - current.GenerationReservations!.Count);
                var baseline = ReactCorrectionPatches.BaselineBytes(call, measure.Paths, candidate);
                if (Math.Ceiling(baseline * 1.25) > measure.SafeOutputTokens)
                    throw new StudioValidationException("A changed candidate exceeds the output safety margin. No next generation requested.");
                state = missions.SaveDraftRepair(id, state.Id, item => !item.InFlight && item.CompletedGroups == index
                    ? item with { InFlight = true,
                        Calls = item.Calls.Select((previous, callIndex) => callIndex == index ? call : previous).ToArray() }
                    : throw new StudioValidationException("Another process owns this repair group."));
                string answer;
                var developer = current.Workflow.Agents.Single(agent => agent.Enabled && agent.Role == "Developer");
                using (missions.EnterGenerationScope(id, "Developer", state.Id))
                    answer = await ReactRevisionCorrection.GenerateGroupAsync(providers.Resolve(call.Model.Provider), call, developer, token);
                var applied = ReactCorrectionPatches.Apply(answer, call, measure.Paths, candidate);
                var replacements = applied.Files;
                var diagnosed = previousIssues.Where(issue =>
                    measure.Paths.Contains(issue.File, StringComparer.Ordinal)).Select(issue => issue.File).ToHashSet(StringComparer.Ordinal);
                if (replacements.Any(file => diagnosed.Contains(file.Path) && candidate[file.Path].Content == file.Content))
                    throw new StudioValidationException("A diagnosed target was returned unchanged; candidate not applied.");
                foreach (var file in replacements) candidate[file.Path] = file;
                var files = state.Candidate.Select(file => candidate[file.Path]).ToArray();
                workspaces.ValidateFiles(files, MissionReactTemplate.Id, sizing: current.Sizing);
                MissionReactTemplate.Validate(files);
                var issues = await checks.CheckAsync(files, token);
                ReactCorrectionPatches.ValidateDiagnostics(previousIssues, issues, measure.Paths);
                state = missions.SaveDraftRepair(id, state.Id, item => item with {
                    Candidate = files, CompletedGroups = index + 1, InFlight = false,
                    PatchEdits = [.. item.PatchEdits, .. applied.Edits] });
            }
            current = missions.GetLatestForRepair(id);
            if ((await checks.CheckAsync(state.Candidate, token)).Count != 0)
                throw new StudioValidationException("The complete candidate is still invalid; original draft retained.");
            if (state.Candidate.SequenceEqual(state.Original))
                throw new StudioValidationException("The complete candidate contains no applicable change; original draft retained.");
            var actualReview = WorkflowAgents.PreviewReactReview(current with {
                Workflow = current.Workflow! with { Draft = state.Candidate } }, token);
            if (actualReview.Count > state.Plan.ReviewerGroups.Count)
                throw new StudioValidationException("Corrected sources require additional Reviewer calls; protected capacity is insufficient. Original retained.");
            missions.CommitDraftRepair(id, state.Id);
            return Status(missions.GetDraftRepair(id)!);
        }
        finally { gate.Exit(); }
    }
}
