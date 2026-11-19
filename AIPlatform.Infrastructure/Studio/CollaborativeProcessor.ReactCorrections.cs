using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed partial class CollaborativeProcessor
{
    private static string SourceHash(IEnumerable<GeneratedFile> files) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            files.OrderBy(file => file.Path, StringComparer.Ordinal), ReactRevisionCorrection.Json)));

    private void BeginSoftwareAgentCall(Guid id, string operation)
    {
        var mission = store.Get(id)!;
        if (!mission.Workflow!.AutomaticReactCorrections) return;
        if (mission.Workflow.PendingSoftwareAgentCall is not null)
            throw new StudioValidationException("An agent call has no durable result checkpoint. Automatic replay is forbidden.");
        store.Update(id, item => item with { Workflow = item.Workflow! with
        {
            PendingSoftwareAgentCall = operation,
            PendingSoftwareReservationCount = item.GenerationReservations!.Count
        } });
    }

    private async Task<IReadOnlyList<GeneratedFile>> RepairReactCandidateAsync(Guid id, string trigger,
        IReadOnlyList<GeneratedFile> original, IReadOnlyList<string> semanticTargets, string reason, CancellationToken token)
    {
        if (reactSourceCheck is null)
            throw new StudioValidationException("Trusted React static checks are required for autonomous correction.");
        var mission = store.Get(id)!;
        var flow = mission.Workflow!;
        MissionStore.RequireApprovedPlan(mission);
        var state = flow.SoftwareCorrections.LastOrDefault(item => item.Trigger == trigger);
        if (state is null)
        {
            var allowed = flow.Plan!.Files.Where(file => MissionReactTemplate.IsEditable(file.Path))
                .Select(file => file.Path).ToArray();
            var before = await reactSourceCheck.CheckAsync(original, token);
            var repairer = reactSourceRepair ?? reactSourceCheck as IReactSourceRepair ??
                throw new StudioValidationException("Trusted deterministic React repairs are unavailable; correction blocked.");
            var projection = await repairer.RepairAsync(original, allowed, token);
            var candidate = projection.Files;
            workspaces.ValidateFiles(candidate, MissionReactTemplate.Id, sizing: mission.Sizing);
            MissionReactTemplate.Validate(candidate);
            if (!SourceManifestMatches(flow, candidate) || candidate.Any(file => !allowed.Contains(file.Path) &&
                original.Single(source => source.Path == file.Path).Content != file.Content))
                throw new StudioValidationException("AST repair exceeded the approved editable manifest.");
            var issues = await reactSourceCheck.CheckAsync(candidate, token);
            if (!projection.Issues.SequenceEqual(issues))
                throw new StudioValidationException("AST repair and independent source validation disagree.");
            if (semanticTargets.Count == 0 && before.Count == 0 && issues.Count == 0) return candidate;
            var paths = semanticTargets.Concat(issues.Select(issue => issue.File)).Distinct(StringComparer.Ordinal).ToArray();
            if (paths.Any(path => !allowed.Contains(path)))
                throw new StudioValidationException("A diagnostic requires an unauthorized scaffold change; autonomous correction stopped.");
            if (flow.SoftwareCorrections.Any(previous => previous.Reason == reason && reason != "Static diagnostics"))
                throw new StudioValidationException("No progress: the same Reviewer/Docker defects recurred after correction.");
            var developer = flow.Agents.Single(agent => agent.Enabled && agent.Role == "Developer");
            if (!developer.AllowedTools.Contains("GenerateFiles"))
                throw new StudioValidationException("Developer correction permission is missing.");
            var model = flow.Models.Single(item => item.Id == developer.ModelId);
            var remaining = (mission.MaxTotalGenerations ??
                throw new StudioValidationException("Autonomous corrections require a persistent generation budget.")) -
                mission.GenerationReservations!.Count;
            var sources = candidate.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var calls = new List<LlmRequest>();
            var decisions = projection.Edits.Select(edit => $"AST {edit.Code}: {edit.File} ({edit.OriginalSha256} -> {edit.CandidateSha256})").ToList();
            var pending = paths.OrderBy(path => flow.Plan.Files.ToList().FindIndex(file => file.Path == path)).ToList();
            while (pending.Count > 0)
            {
                var group = new List<string> { pending[0] };
                var call = Prepare(group);
                foreach (var path in pending.Skip(1).ToArray())
                {
                    if (group.Count == (mission.Sizing?.MaxFilesPerRepairGroup ?? 4)) break;
                    var related = flow.Plan.Files.Any(file => group.Contains(file.Path) && file.DependsOn.Contains(path) ||
                        file.Path == path && file.DependsOn.Any(group.Contains));
                    if (!related) continue;
                    if (group.Append(path).Select(target => flow.ModelSelections.SingleOrDefault(sequence =>
                            sequence.Key == developer.Id + ":" + target)?.Decision.Model ?? model.Name).Distinct().Count() != 1)
                        continue;
                    var combined = Prepare([.. group, path]);
                    if (!Fits(combined)) continue;
                    group.Add(path);
                    call = combined;
                }
                if (!Fits(call))
                    throw new StudioValidationException($"Correction preflight refused [{string.Join(", ", group)}]: context/output headroom insufficient. No model was called.");
                calls.Add(call);
                decisions.Add($"{(ReactCorrectionPatches.Targets(call) is null ? "Files" : "Patches")}: {string.Join(", ", group)}; " +
                    $"input {ReactRevisionCorrection.CallSize(call)} characters; conservative output {Output(call)} tokens / {model.MaxOutputTokens}.");
                pending.RemoveAll(group.Contains);
            }
            if (calls.Count > (mission.Sizing?.MaxRepairGroups ?? 40))
                throw new StudioValidationException("The correction exceeds the frozen repair group limit; no model was called.");
            var grown = candidate.Select(file => paths.Contains(file.Path)
                ? file with { Content = file.Content + "\n/*" + new string('x', (int)Math.Ceiling(file.Content.Length * .25)) + "*/" }
                : file).ToArray();
            var reviewerCapacity = WorkflowAgents.PreviewReactReview(mission with { Workflow = flow with { Draft = grown } }, token).Count;
            if (remaining < calls.Count + reviewerCapacity)
                throw new StudioValidationException($"Correction budget insufficient: {calls.Count} Developer + {reviewerCapacity} Reviewer required, {remaining} remaining.");
            if (calls.Count > 0 && mission.CorrectionAttempts >= options.CorrectionLimitFor(mission))
                throw new StudioValidationException("Autonomous correction cycle limit reached.");
            state = new(Guid.NewGuid(), trigger, reason, original, candidate, calls, reviewerCapacity, decisions);
            var prepared = state;
            store.Update(id, current =>
            {
                if (current.Workflow!.SoftwareCorrections.Any(item => item.Trigger == trigger) ||
                    SourceHash(current.Workflow.Proposal ?? current.Workflow.Draft ?? []) != SourceHash(original) ||
                    current.MaxTotalGenerations - current.GenerationReservations!.Count < calls.Count + reviewerCapacity)
                    throw new StudioValidationException("Candidate or correction capacity changed before checkpoint claim.");
                return current with
                {
                    CorrectionAttempts = current.CorrectionAttempts + (calls.Count > 0 ? 1 : 0),
                    SandboxApproved = false, ApprovalFingerprint = null,
                    Workflow = current.Workflow with { SoftwareCorrections = [.. current.Workflow.SoftwareCorrections, prepared] }
                };
            });
            store.AddEvent(id, "SoftwareCorrection", StepStatus.Running, string.Join("\n", decisions));

            int Output(LlmRequest request) => (int)Math.Ceiling(
                ReactCorrectionPatches.BaselineBytes(request, Paths(request), sources) * 1.25);
            bool Fits(LlmRequest request) => ReactRevisionCorrection.CallSize(request) <=
                (request.Model.ContextSize - request.Model.MaxOutputTokens) * 2 &&
                Output(request) <= Math.Floor(request.Model.MaxOutputTokens * .8);
            LlmRequest Prepare(List<string> targets)
            {
                var routes = targets.Select(path => flow.ModelSelections.SingleOrDefault(sequence =>
                    sequence.Key == developer.Id + ":" + path)?.Decision.Model ?? model.Name).Distinct().ToArray();
                if (routes.Length != 1)
                    throw new StudioValidationException("Correction targets have incompatible frozen routes; no automatic rerouting.");
                var selected = model with { Name = routes[0] };
                var diagnostics = issues.Where(issue => targets.Contains(issue.File)).Cast<object>()
                    .Append(new { message = reason }).ToArray();
                var scope = new ReactCorrectionGroup(targets.ToArray(), "Fix the attached defects together; preserve every original criterion and business test.");
                var complete = ReactRevisionCorrection.BuildGroupCall(mission, developer, selected, scope, sources, diagnostics, remaining);
                var anchors = new List<ReactPatchTarget>();
                foreach (var path in targets)
                {
                    var lines = sources[path].Content.Split('\n');
                    var located = issues.Where(issue => issue.File == path).ToArray();
                    if (located.Length == 0 || located.Any(issue => issue.Line < 1 || issue.Line > lines.Length)) return complete;
                    var start = Math.Max(0, located.Min(issue => issue.Line) - 3);
                    var end = Math.Min(lines.Length, located.Max(issue => issue.Line) + 2);
                    var anchor = string.Join("\n", lines[start..end]);
                    if (string.IsNullOrWhiteSpace(anchor) ||
                        sources[path].Content.IndexOf(anchor, StringComparison.Ordinal) != sources[path].Content.LastIndexOf(anchor, StringComparison.Ordinal))
                        return complete;
                    anchors.Add(new(path, anchor));
                }
                var patches = ReactRevisionCorrection.BuildGroupCall(mission, developer, selected,
                    scope with { PatchTargets = anchors.ToArray() }, sources, diagnostics, remaining);
                return Fits(patches) && Output(patches) < Output(complete) ? patches : complete;
            }
        }
        if (SourceHash(original) != SourceHash(state.Original))
            throw new StudioValidationException("The original correction sources changed; checkpoint retained.");
        if (state.InFlight)
            throw new StudioValidationException("An interrupted Developer call has an ambiguous result. Automatic replay is forbidden.");
        if (state.Failure is not null)
            throw new StudioValidationException("A charged correction was rejected; inspect the retained diagnostics before another attempt: " + state.Failure);
        var agent = flow.Agents.Single(item => item.Enabled && item.Role == "Developer");
        while (state.CompletedGroups < state.Calls.Count)
        {
            token.ThrowIfCancellationRequested();
            var index = state.CompletedGroups;
            var sources = state.Candidate.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var before = await reactSourceCheck.CheckAsync(state.Candidate, token);
            var call = ReactRevisionCorrection.RefreshDependencies(state.Calls[index], sources,
                (state.Calls[index].Model.ContextSize - state.Calls[index].Model.MaxOutputTokens) * 2,
                before.Where(issue => Paths(state.Calls[index]).Contains(issue.File)).Cast<object>()
                    .Append(new { message = state.Reason }), Remaining(id));
            call = call with { RequireAutomaticAdmissibility = flow.AutomaticModelSelection };
            var paths = Paths(call);
            var estimate = (int)Math.Ceiling(ReactCorrectionPatches.BaselineBytes(call, paths, sources) * 1.25);
            if (estimate > call.Model.MaxOutputTokens * .8 || ReactRevisionCorrection.CallSize(call) >
                (call.Model.ContextSize - call.Model.MaxOutputTokens) * 2)
                throw new StudioValidationException("Refreshed correction no longer fits its frozen profile.");
            Save(state with { InFlight = true });
            string answer;
            using (store.EnterGenerationScope(id, "Developer", state.Id))
                answer = await agents.CorrectReactGroupAsync(call, agent, token);
            try
            {
                var applied = ReactCorrectionPatches.Apply(answer, call, paths, sources);
                foreach (var file in applied.Files) sources[file.Path] = file;
                var candidate = sources.Values.ToArray();
                workspaces.ValidateFiles(candidate, MissionReactTemplate.Id, sizing: mission.Sizing);
                MissionReactTemplate.Validate(candidate);
                var after = await reactSourceCheck.CheckAsync(candidate, token);
                ReactCorrectionPatches.ValidateDiagnostics(before, after, paths);
                if (SourceHash(candidate) == SourceHash(state.Candidate) || flow.SoftwareCorrections.Any(previous =>
                    SourceHash(candidate) == SourceHash(previous.Original)))
                    throw new StudioValidationException("No progress or repeated source state; candidate not published.");
                Save(state with { Candidate = candidate, InFlight = false, CompletedGroups = index + 1 });
            }
            catch (StudioValidationException exception)
            {
                Save(state with { InFlight = false, Failure = exception.Message });
                throw;
            }
        }
        var finalIssues = await reactSourceCheck.CheckAsync(state.Candidate, token);
        if (finalIssues.Count > 0)
            throw new StudioValidationException("Corrected candidate is still statically invalid; no publication.\n" + ReactSourceCheck.Describe(finalIssues));
        if (!state.Ready) Save(state with { Ready = true });
        store.AddEvent(id, "SoftwareCorrection", StepStatus.Succeeded, "Complete candidate passed static checks; Reviewer and independent execution authorization remain required.");
        return state.Candidate;

        void Save(ReactSoftwareCorrection next)
        {
            var expected = state!;
            store.Update(id, current =>
            {
                var latest = current.Workflow!.SoftwareCorrections.Last();
                if (latest.Id != expected.Id || latest.CompletedGroups != expected.CompletedGroups || latest.InFlight != expected.InFlight)
                    throw new StudioValidationException("Correction checkpoint ownership changed.");
                if (next.CompletedGroups > latest.CompletedGroups &&
                    current.GenerationReservations!.Count(item => item.CapacityId == latest.Id) != next.CompletedGroups)
                    throw new StudioValidationException("A correction result requires its exact durable generation reservation.");
                return current with
                {
                    SelectedAgent = next.InFlight ? agent.Id : current.SelectedAgent,
                    SelectedModel = next.InFlight ? next.Calls[next.CompletedGroups].Model.Name : current.SelectedModel,
                    Reason = next.InFlight ? "Bounded automatic Developer correction; no generated-code execution." : current.Reason,
                    Workflow = current.Workflow with
                    { SoftwareCorrections = [.. current.Workflow.SoftwareCorrections.SkipLast(1), next] } };
            });
            state = next;
        }
    }

    private int Remaining(Guid id)
    {
        var mission = store.Get(id)!;
        return (mission.MaxTotalGenerations ?? 0) - (mission.GenerationReservations?.Count ?? 0);
    }

    private static string[] Paths(LlmRequest call)
    {
        using var input = JsonDocument.Parse(call.Messages[1].Content);
        return input.RootElement.GetProperty("allowedPaths").Deserialize<string[]>()!;
    }

    private static bool SourceManifestMatches(MissionWorkflow flow, IReadOnlyList<GeneratedFile> candidate) =>
        flow.Plan!.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal)
            .SetEquals(candidate.Select(file => file.Path));
}
