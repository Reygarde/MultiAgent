using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed partial class CollaborativeProcessor
{
    private static MissionWorkflow ClearPendingSoftwareAgentCall(MissionWorkflow flow) => flow with
    {
        PendingSoftwareAgentCall = null,
        PendingSoftwareReservationCount = 0,
        PendingSoftwareCallReservationId = null,
        PendingSoftwareCallReservationCount = null,
        PendingSoftwareCallDeliveryState = SoftwareAgentCallDeliveryState.Unknown
    };

    private static string SourceHash(IEnumerable<GeneratedFile> files) =>
        ReactCorrectionPatches.SourceHash(files);

    private void BeginSoftwareAgentCall(Guid id, string operation)
    {
        var mission = store.Get(id)!;
        if (!mission.Workflow!.AutomaticReactCorrections) return;
        if (mission.Workflow.PendingSoftwareAgentCall is not null)
            throw new StudioValidationException("An agent call has no durable result checkpoint. Automatic replay is forbidden.");
        store.Update(id, item => item with { Workflow = item.Workflow! with
        {
            PendingSoftwareAgentCall = operation,
            PendingSoftwareReservationCount = item.GenerationReservations!.Count,
            PendingSoftwareCallReservationId = null,
            PendingSoftwareCallReservationCount = null,
            PendingSoftwareCallDeliveryState = SoftwareAgentCallDeliveryState.Unknown
        } });
    }

    private async Task<IReadOnlyList<GeneratedFile>> RepairReactCandidateAsync(Guid id, string trigger,
        IReadOnlyList<GeneratedFile> original, IReadOnlyList<string> semanticTargets, string reason, CancellationToken token,
        ReactReadyCandidateResumption? readyResumption = null)
    {
        if (reactSourceCheck is null)
            throw new StudioValidationException("Trusted React static checks are required for autonomous correction.");
        var mission = store.Get(id)!;
        var flow = mission.Workflow!;
        MissionStore.RequireApprovedPlan(mission);
        var state = flow.SoftwareCorrections.LastOrDefault(item => item.Trigger == trigger);
        if (state is { Ready: true })
        {
            var authorization = flow.ReadyCandidateResumptions.LastOrDefault(item => item.ReadyCorrectionId == state.Id);
            var child = authorization is null ? null : flow.SoftwareCorrections.LastOrDefault(item =>
                item.Trigger == ReactCorrectionPatches.ReadyCorrectionTrigger(state, authorization));
            if (child is not null)
            {
                if (!ReactCorrectionPatches.HasReadyResumption(mission, authorization!, requireRejection: true))
                    throw new StudioValidationException("Ready revalidation checkpoint no longer matches its explicit authorization.");
                return await RepairReactCandidateAsync(id, child.Trigger, original, [], reason, token, authorization);
            }
            if (!ReactCorrectionPatches.HasReadyCandidate(mission, state, original))
                throw new StudioValidationException("Ready correction candidate lacks matching source-bound checkpoint evidence.");
            workspaces.ValidateFiles(state.Candidate, MissionReactTemplate.Id, sizing: mission.Sizing);
            var retainedIssues = await reactSourceCheck.CheckAsync(state.Candidate, token);
            if (retainedIssues.Count > 0)
            {
                if (authorization is null || !ReactCorrectionPatches.HasReadyResumption(mission, authorization))
                    throw new StudioValidationException("Ready correction candidate failed current static validation; explicit source-bound resumption is required:\n" +
                        ReactSourceCheck.Describe(retainedIssues));
                var diagnostics = retainedIssues.Select(issue => new FailureDiagnostic("ReactSource", null,
                    issue.File, issue.Line, issue.Column, issue.Code, issue.Message)).ToArray();
                if (authorization.RevalidatedAt is null)
                {
                    var rejection = authorization with { RevalidatedAt = DateTimeOffset.UtcNow, Diagnostics = diagnostics };
                    rejection = rejection with { RevalidationFingerprint = ReactCorrectionPatches.ReadyRevalidationFingerprint(rejection) };
                    store.Update(id, current =>
                    {
                        if (!ReactCorrectionPatches.HasReadyResumption(current, authorization) ||
                            current.Workflow!.ReadyCandidateResumptions.Single(item => item.Id == authorization.Id).RevalidatedAt is not null)
                            throw new StudioValidationException("Ready revalidation ownership changed.");
                        return current with
                        {
                            Workflow = current.Workflow with
                            {
                                ReadyCandidateResumptions = current.Workflow.ReadyCandidateResumptions.Select(item =>
                                    item.Id == authorization.Id ? rejection : item).ToArray()
                            },
                            Events = [.. current.Events, new(current.Events.Count + 1, rejection.RevalidatedAt!.Value,
                                "ReadyCandidateRevalidation", StepStatus.Failed,
                                $"Authentic Ready checkpoint {state.Id} rejected by current static validation; {diagnostics.Length} diagnostics retained. No LLM response receipt.")]
                        };
                    });
                    authorization = rejection;
                }
                else if (!authorization.Diagnostics.SequenceEqual(diagnostics))
                    throw new StudioValidationException("Current diagnostics differ from the persisted Ready revalidation; inspection is required.");
                token.ThrowIfCancellationRequested();
                return await RepairReactCandidateAsync(id, ReactCorrectionPatches.ReadyCorrectionTrigger(state, authorization),
                    original, [], "Static diagnostics", token, authorization);
            }
            return state.Candidate;
        }
        if (readyResumption is not null && !ReactCorrectionPatches.HasReadyResumption(mission, readyResumption, requireRejection: true))
            throw new StudioValidationException("Ready correction lacks its durable revalidation authorization.");
        if (state is null)
        {
            var allowed = flow.Plan!.Files.Where(file => MissionReactTemplate.IsEditable(file.Path))
                .Select(file => file.Path).ToArray();
            var before = await reactSourceCheck.CheckAsync(original, token);
            ReactSourceRepairResult? projection = null;
            var candidate = original;
            if (readyResumption is null)
            {
                var repairer = reactSourceRepair ?? reactSourceCheck as IReactSourceRepair ??
                    throw new StudioValidationException("Trusted deterministic React repairs are unavailable; correction blocked.");
                projection = await repairer.RepairAsync(original, allowed, token);
                candidate = projection.Files;
            }
            workspaces.ValidateFiles(candidate, MissionReactTemplate.Id, sizing: mission.Sizing);
            MissionReactTemplate.Validate(candidate);
            if (!SourceManifestMatches(flow, candidate) || candidate.Any(file => !allowed.Contains(file.Path) &&
                original.Single(source => source.Path == file.Path).Content != file.Content))
                throw new StudioValidationException("AST repair exceeded the approved editable manifest.");
            var issues = await reactSourceCheck.CheckAsync(candidate, token);
            if (projection is not null && !projection.Issues.SequenceEqual(issues))
                throw new StudioValidationException("AST repair and independent source validation disagree.");
            if (readyResumption is not null && !readyResumption.Diagnostics.SequenceEqual(issues.Select(issue =>
                new FailureDiagnostic("ReactSource", null, issue.File, issue.Line, issue.Column, issue.Code, issue.Message))))
                throw new StudioValidationException("Ready revalidation diagnostics changed before correction preparation.");
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
            var decisions = projection is not null
                ? projection.Edits.Select(edit => $"AST {edit.Code}: {edit.File} ({edit.OriginalSha256} -> {edit.CandidateSha256})").ToList()
                : new List<string> { $"Explicit Ready revalidation {readyResumption!.Id}; AST candidate retained without renormalization." };
            var pending = paths.OrderBy(path => flow.Plan.Files.ToList().FindIndex(file => file.Path == path)).ToList();
            while (pending.Count > 0)
            {
                var group = new List<string> { pending[0] };
                var preparedCall = PrepareCorrectionGroup(mission, flow, developer, model, group, sources, issues, reason, remaining);
                foreach (var path in pending.Skip(1).ToArray())
                {
                    if (group.Count == (mission.Sizing?.MaxFilesPerRepairGroup ?? 4)) break;
                    var related = flow.Plan.Files.Any(file => group.Contains(file.Path) && file.DependsOn.Contains(path) ||
                        file.Path == path && file.DependsOn.Any(group.Contains));
                    if (!related) continue;
                    if (group.Append(path).Select(target => flow.ModelSelections.SingleOrDefault(sequence =>
                            sequence.Key == developer.Id + ":" + target)?.Decision.Model ?? model.Name).Distinct().Count() != 1)
                        continue;
                    var combined = PrepareCorrectionGroup(mission, flow, developer, model,
                        [.. group, path], sources, issues, reason, remaining);
                    if (!CorrectionFits(combined.Call, sources)) continue;
                    group.Add(path);
                    preparedCall = combined;
                }
                var call = preparedCall.Call;
                var preflight = MeasureCorrection(call, sources);
                if (!preflight.ContextFits || !preflight.OutputFits)
                {
                    var contract = ReactCorrectionPatches.Targets(call) is null ? "complete-files" : "exact-patches";
                    var reasons = new List<string>();
                    if (!preflight.ContextFits)
                        reasons.Add($"prompt estimate {preflight.PromptCharacters} chars exceeds context headroom {preflight.ContextCharacterLimit} chars");
                    if (!preflight.OutputFits)
                        reasons.Add($"estimated output {preflight.EstimatedOutputTokens} tokens exceeds safety ceiling {preflight.OutputTokenLimit} tokens");
                    var message = $"React correction preflight refused [{string.Join(", ", group)}]: contract={contract}; " +
                        $"prompt estimate={preflight.PromptCharacters}/{preflight.ContextCharacterLimit} chars; " +
                        $"output estimate={preflight.EstimatedOutputTokens}/{preflight.OutputTokenLimit} tokens (20% reserve); " +
                        $"selection={preparedCall.Selection}; reason={string.Join("; ", reasons)}. No model was called.";
                    store.AddEvent(id, "SoftwareCorrectionPreflight", StepStatus.Failed, message);
                    throw new StudioValidationException(message);
                }
                calls.Add(call);
                decisions.Add($"Contract: {(ReactCorrectionPatches.Targets(call) is null ? "complete-files" : "exact-patches")}; " +
                    $"files={string.Join(", ", group)}; prompt estimate={preflight.PromptCharacters}/{preflight.ContextCharacterLimit} chars; " +
                    $"output estimate={preflight.EstimatedOutputTokens}/{preflight.OutputTokenLimit} tokens (20% reserve); " +
                    $"selection={preparedCall.Selection}");
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
            state = new(readyResumption?.DeveloperCorrectionId ?? Guid.NewGuid(), trigger, reason, original, candidate,
                calls, reviewerCapacity, decisions)
            {
                ReadyResumptionId = readyResumption?.Id,
                RevalidatedFromId = readyResumption?.ReadyCorrectionId
            };
            var prepared = state;
            store.Update(id, current =>
            {
                if (current.Workflow!.SoftwareCorrections.Any(item => item.Trigger == trigger) ||
                    SourceHash(current.Workflow.Proposal ?? current.Workflow.Draft ?? []) != SourceHash(original) ||
                    current.MaxTotalGenerations - current.GenerationReservations!.Count < calls.Count + reviewerCapacity ||
                    calls.Count > 0 && current.CorrectionAttempts >= options.CorrectionLimitFor(current) ||
                    readyResumption is not null && !ReactCorrectionPatches.HasReadyResumption(current, readyResumption, requireRejection: true))
                    throw new StudioValidationException("Candidate or correction capacity changed before checkpoint claim.");
                return current with
                {
                    CorrectionAttempts = current.CorrectionAttempts + (calls.Count > 0 ? 1 : 0),
                    SandboxApproved = false, ApprovalFingerprint = null,
                    Workflow = current.Workflow with { SoftwareCorrections = [.. current.Workflow.SoftwareCorrections, prepared] }
                };
            });
            store.AddEvent(id, "SoftwareCorrection", StepStatus.Running, string.Join("\n", decisions));
        }
        else if (state.Failure is not null)
        {
            var rejected = state;
            if (!ReactCorrectionPatches.HasRetryableValidationRejection(mission, rejected) ||
                rejected.RetryAuthorizationId is not { } authorizationId ||
                SourceHash(original) != SourceHash(rejected.Original))
                throw new StudioValidationException(
                    "The rejected correction has no explicit, source-bound response receipt; it cannot be retried.");

            var candidate = rejected.Candidate;
            workspaces.ValidateFiles(candidate, MissionReactTemplate.Id, sizing: mission.Sizing);
            MissionReactTemplate.Validate(candidate);
            if (!SourceManifestMatches(flow, candidate))
                throw new StudioValidationException("The retained correction candidate no longer matches the approved manifest.");
            var issues = await reactSourceCheck.CheckAsync(candidate, token);
            var developer = flow.Agents.Single(agent => agent.Enabled && agent.Role == "Developer");
            if (!developer.AllowedTools.Contains("GenerateFiles"))
                throw new StudioValidationException("Developer correction permission is missing.");
            var model = flow.Models.Single(item => item.Id == developer.ModelId);
            var remaining = (mission.MaxTotalGenerations ??
                throw new StudioValidationException("Autonomous corrections require a persistent generation budget.")) -
                mission.GenerationReservations!.Count;
            var calls = new List<LlmRequest>();
            var decisions = new List<string>
            {
                $"Explicit retry of rejected group {rejected.ValidationRejection!.Receipt.Attempt!.GroupIndex}; " +
                $"prior reservation {rejected.ValidationRejection.Receipt.ReservationId} remains consumed."
            };
            var sources = candidate.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var retryGroups = rejected.Calls.Skip(rejected.CompletedGroups).ToArray();
            if (retryGroups.Length == 0 || retryGroups.Length > (mission.Sizing?.MaxRepairGroups ?? 40))
                throw new StudioValidationException("No bounded correction groups remain for an explicit retry.");

            foreach (var previousCall in retryGroups)
            {
                var targetPaths = Paths(previousCall);
                var prepared = PrepareCorrectionGroup(mission, flow, developer, model, targetPaths,
                    sources, issues, rejected.Reason, remaining - calls.Count);
                var preflight = MeasureCorrection(prepared.Call, sources);
                if (!preflight.ContextFits || !preflight.OutputFits)
                {
                    var contract = ReactCorrectionPatches.Targets(prepared.Call) is null ? "complete-files" : "exact-patches";
                    var reasons = new List<string>();
                    if (!preflight.ContextFits)
                        reasons.Add($"prompt estimate {preflight.PromptCharacters} chars exceeds context headroom {preflight.ContextCharacterLimit} chars");
                    if (!preflight.OutputFits)
                        reasons.Add($"estimated output {preflight.EstimatedOutputTokens} tokens exceeds safety ceiling {preflight.OutputTokenLimit} tokens");
                    var message = $"React correction preflight refused retry [{string.Join(", ", targetPaths)}]: contract={contract}; " +
                        $"prompt estimate={preflight.PromptCharacters}/{preflight.ContextCharacterLimit} chars; " +
                        $"output estimate={preflight.EstimatedOutputTokens}/{preflight.OutputTokenLimit} tokens (20% reserve); " +
                        $"selection={prepared.Selection}; reason={string.Join("; ", reasons)}. No model was called.";
                    store.AddEvent(id, "SoftwareCorrectionPreflight", StepStatus.Failed, message);
                    throw new StudioValidationException(message);
                }
                calls.Add(prepared.Call);
                decisions.Add($"Retry contract: {(ReactCorrectionPatches.Targets(prepared.Call) is null ? "complete-files" : "exact-patches")}; " +
                    $"files={string.Join(", ", targetPaths)}; prompt estimate={preflight.PromptCharacters}/{preflight.ContextCharacterLimit} chars; " +
                    $"output estimate={preflight.EstimatedOutputTokens}/{preflight.OutputTokenLimit} tokens (20% reserve); " +
                    $"selection={prepared.Selection}");
            }

            var retryPaths = calls.SelectMany(Paths).Distinct(StringComparer.Ordinal).ToArray();
            var grown = candidate.Select(file => retryPaths.Contains(file.Path, StringComparer.Ordinal)
                ? file with { Content = file.Content + "\n/*" + new string('x', (int)Math.Ceiling(file.Content.Length * .25)) + "*/" }
                : file).ToArray();
            var reviewerCapacity = WorkflowAgents.PreviewReactReview(
                mission with { Workflow = flow with { Draft = grown } }, token).Count;
            if (remaining < calls.Count + reviewerCapacity)
                throw new StudioValidationException(
                    $"Correction budget insufficient: {calls.Count} retry Developer + {reviewerCapacity} Reviewer required, {remaining} remaining.");
            if (mission.CorrectionAttempts >= options.CorrectionLimitFor(mission))
                throw new StudioValidationException("Autonomous correction cycle limit reached.");

            var retry = new ReactSoftwareCorrection(Guid.NewGuid(), trigger, rejected.Reason,
                rejected.Original, candidate, calls, reviewerCapacity, decisions)
            {
                RetriedFromId = rejected.Id,
                RetryAuthorizationId = authorizationId,
                ReadyResumptionId = rejected.ReadyResumptionId,
                RevalidatedFromId = rejected.RevalidatedFromId
            };
            store.Update(id, current =>
            {
                var latest = current.Workflow!.SoftwareCorrections.LastOrDefault();
                if (latest?.Id != rejected.Id || latest.RetryAuthorizationId != authorizationId ||
                    !ReactCorrectionPatches.HasRetryableValidationRejection(current, latest) ||
                    SourceHash(current.Workflow.Proposal ?? current.Workflow.Draft ?? []) != SourceHash(original) ||
                    current.MaxTotalGenerations - current.GenerationReservations!.Count < calls.Count + reviewerCapacity ||
                    current.CorrectionAttempts >= options.CorrectionLimitFor(current))
                    throw new StudioValidationException("The rejected candidate or retry capacity changed before checkpoint claim.");
                var now = DateTimeOffset.UtcNow;
                return current with
                {
                    CorrectionAttempts = current.CorrectionAttempts + 1,
                    SandboxApproved = false,
                    ApprovalFingerprint = null,
                    Workflow = current.Workflow with
                    {
                        SoftwareCorrections = [.. current.Workflow.SoftwareCorrections, retry]
                    },
                    Events = [.. current.Events, new(current.Events.Count + 1, now, "SoftwareCorrectionRetry",
                        StepStatus.Running, $"New correction checkpoint {retry.Id} prepared from rejected checkpoint {rejected.Id}; " +
                        "the prior call and reservation will not be replayed. " + string.Join("\n", decisions))]
                };
            });
            mission = store.Get(id)!;
            flow = mission.Workflow!;
            state = retry;
        }
        if (SourceHash(original) != SourceHash(state.Original))
            throw new StudioValidationException("The original correction sources changed; checkpoint retained.");
        if (state.InFlight)
            throw new StudioValidationException("An interrupted Developer call has an ambiguous result. Automatic replay is forbidden.");
        if (state.Failure is not null)
            throw new StudioValidationException("A charged correction was rejected; inspect the retained diagnostics before another attempt: " + state.Failure);
        var agent = flow.Agents.Single(item => item.Enabled && item.Role == "Developer");
        if (!agent.AllowedTools.Contains("GenerateFiles"))
            throw new StudioValidationException("Developer correction permission is missing.");
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
            call = call with { CallId = Guid.NewGuid() };
            var attempt = new ReactCorrectionCallAttempt(mission.Id, flow.WorkflowId, flow.Version,
                MissionStore.PlanFingerprint(mission, flow.Version), state.Id, call.CallId!.Value,
                index, paths, SourceHash(state.Candidate), ReactCorrectionPatches.RequestFingerprint(call));
            Save(state with
            {
                InFlight = true,
                CallState = ReactSoftwareCorrectionCallState.InFlight,
                ActiveGroupIndex = index,
                ActiveReservationId = null,
                ActiveAttempt = attempt,
                ResponseReceipt = null,
                ValidationRejection = null
            });
            string answer;
            using (store.EnterGenerationScope(id, "Developer", state.Id))
                answer = await agents.CorrectReactGroupAsync(call, agent, token);

            var receivedMission = store.Get(id)!;
            state = receivedMission.Workflow!.SoftwareCorrections.Last();
            var reservations = receivedMission.GenerationReservations
                ?? throw new StudioValidationException("A returned Developer response has no durable reservation ledger.");
            var reservation = reservations.SingleOrDefault(item =>
                item.Id == state.ActiveReservationId && item.CapacityId == state.Id &&
                item.Role == "Developer" && item.Model == call.Model.Name && item.CallId == call.CallId);
            if (state.ActiveGroupIndex != index || !state.InFlight || reservation is null ||
                state.ActiveAttempt is not { } persistedAttempt || persistedAttempt.CallId != call.CallId ||
                ReactCorrectionPatches.RequestFingerprint(call) != persistedAttempt.CallFingerprint ||
                reservations.Count(item => item.CapacityId == state.Id) != index + 1)
                throw new StudioValidationException("A returned Developer response lacks its matching durable reservation and group checkpoint.");
            var receipt = new ReactCorrectionResponseReceipt(persistedAttempt, reservation.Id,
                ReactCorrectionPatches.ResponseFingerprint(answer), DateTimeOffset.UtcNow);
            Save(state with
            {
                CallState = ReactSoftwareCorrectionCallState.ResponseReceived,
                ResponseReceipt = receipt
            }, ("SoftwareCorrectionResponseReceived", StepStatus.Succeeded,
                $"Developer response received for group {index + 1} [{string.Join(", ", paths)}]; " +
                $"call {persistedAttempt.CallId}; reservation {reservation.Id}."));
            token.ThrowIfCancellationRequested();
            void Reject(StudioValidationException exception, IReadOnlyList<ReactSourceIssue>? checkedIssues,
                IReadOnlyList<GeneratedFile>? evaluatedCandidate)
            {
                var changes = checkedIssues is null
                    ? new ReactCorrectionPatches.DiagnosticChanges(
                        before.Select(issue => new FailureDiagnostic("ReactSource", null, issue.File,
                            issue.Line, issue.Column, issue.Code, issue.Message)).ToArray(), [], [])
                    : ReactCorrectionPatches.ClassifyDiagnostics(before, checkedIssues);
                var rejection = new ReactCorrectionValidationRejection(receipt, changes.Existing,
                    changes.New, changes.Corrected, exception.Message, Guid.NewGuid(),
                    evaluatedCandidate is null ? null : SourceHash(evaluatedCandidate), null);
                rejection = rejection with
                {
                    ResultFingerprint = ReactCorrectionPatches.ValidationFingerprint(rejection)
                };
                var details = $"group {index + 1} [{string.Join(", ", paths)}] rejected; " +
                    $"call {persistedAttempt.CallId}; validation {rejection.ValidationId}; " +
                    $"candidate={(rejection.CandidateFingerprint is null ? "unavailable" : rejection.CandidateFingerprint)}; " +
                    $"reason={exception.Message}; " +
                    $"pre-existing retained={Describe(changes.Existing)}; new={Describe(changes.New)}; " +
                    $"corrected={Describe(changes.Corrected)}.";
                Save(state with
                {
                    InFlight = false,
                    Failure = exception.Message,
                    CallState = ReactSoftwareCorrectionCallState.ValidationRejected,
                    ResponseReceipt = receipt,
                    ValidationRejection = rejection
                }, ("SoftwareCorrectionValidation", StepStatus.Failed, details));
            }

            ReactCorrectionPatches.Applied applied;
            GeneratedFile[] candidate;
            GeneratedFile[]? evaluatedCandidate = null;
            try
            {
                applied = ReactCorrectionPatches.Apply(answer, call, paths, sources);
                foreach (var file in applied.Files) sources[file.Path] = file;
                candidate = sources.Values.ToArray();
                evaluatedCandidate = candidate;
                workspaces.ValidateFiles(candidate, MissionReactTemplate.Id, sizing: mission.Sizing);
                MissionReactTemplate.Validate(candidate);
            }
            catch (StudioValidationException exception)
            {
                Reject(exception, null, evaluatedCandidate);
                throw;
            }

            var after = await reactSourceCheck.CheckAsync(candidate, token);
            try
            {
                ReactCorrectionPatches.ValidateDiagnostics(before, after, paths);
                if (SourceHash(candidate) == SourceHash(state.Candidate) || flow.SoftwareCorrections.Any(previous =>
                    SourceHash(candidate) == SourceHash(previous.Original)))
                    throw new StudioValidationException("No progress or repeated source state; candidate not published.");
            }
            catch (StudioValidationException exception)
            {
                Reject(exception, after, candidate);
                throw;
            }
            Save(state with
            {
                Candidate = candidate,
                InFlight = false,
                CompletedGroups = index + 1,
                CallState = ReactSoftwareCorrectionCallState.Validated,
                Failure = null,
                ValidationRejection = null
            }, ("SoftwareCorrectionGroupValidated", StepStatus.Succeeded,
                $"Group {index + 1} [{string.Join(", ", paths)}] passed static validation; " +
                $"call {persistedAttempt.CallId} was checkpointed."));
        }
        var finalIssues = await reactSourceCheck.CheckAsync(state.Candidate, token);
        if (finalIssues.Count > 0)
            throw new StudioValidationException("Corrected candidate is still statically invalid; no publication.\n" + ReactSourceCheck.Describe(finalIssues));
        if (!state.Ready) Save(state with { Ready = true });
        store.AddEvent(id, "SoftwareCorrection", StepStatus.Succeeded, "Complete candidate passed static checks; Reviewer and independent execution authorization remain required.");
        return state.Candidate;

        static string Describe(IReadOnlyList<FailureDiagnostic> diagnostics) => diagnostics.Count == 0
            ? "none"
            : string.Join(" | ", diagnostics.Take(20).Select(item =>
                $"{item.File}:{item.Line}:{item.Column} {item.Code}: {item.Message}"));

        void Save(ReactSoftwareCorrection next,
            (string Step, StepStatus Status, string Message)? eventInfo = null)
        {
            var expected = state!;
            store.Update(id, current =>
            {
                var latest = current.Workflow!.SoftwareCorrections.Last();
                if (latest.Id != expected.Id || latest.CompletedGroups != expected.CompletedGroups ||
                    latest.InFlight != expected.InFlight || latest.CallState != expected.CallState ||
                    latest.ActiveReservationId != expected.ActiveReservationId ||
                    latest.ActiveAttempt?.CallId != expected.ActiveAttempt?.CallId)
                    throw new StudioValidationException("Correction checkpoint ownership changed.");
                if (next.CompletedGroups > latest.CompletedGroups &&
                    current.GenerationReservations!.Count(item => item.CapacityId == latest.Id) != next.CompletedGroups)
                    throw new StudioValidationException("A correction result requires its exact durable generation reservation.");
                var events = eventInfo is { } info
                    ? current.Events.Append(new(current.Events.Count + 1, DateTimeOffset.UtcNow,
                        info.Step, info.Status, info.Message)).ToArray()
                    : current.Events;
                return current with
                {
                    SelectedAgent = next.InFlight ? agent.Id : current.SelectedAgent,
                    SelectedModel = next.InFlight ? next.Calls[next.CompletedGroups].Model.Name : current.SelectedModel,
                    Reason = next.InFlight ? "Bounded automatic Developer correction; no generated-code execution." : current.Reason,
                    Events = events,
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
        => ReactCorrectionPatches.AllowedPaths(call);

    private readonly record struct CorrectionCallMeasurement(int PromptCharacters, int ContextCharacterLimit,
        int EstimatedOutputTokens, int OutputTokenLimit, bool ContextFits, bool OutputFits);

    private static CorrectionCallMeasurement MeasureCorrection(LlmRequest call,
        IReadOnlyDictionary<string, GeneratedFile> sources)
    {
        var promptCharacters = ReactRevisionCorrection.CallSize(call);
        var contextCharacterLimit = (call.Model.ContextSize - call.Model.MaxOutputTokens) * 2;
        var estimatedOutputTokens = (int)Math.Ceiling(
            ReactCorrectionPatches.BaselineBytes(call, Paths(call), sources) * 1.25);
        var outputTokenLimit = (int)Math.Floor(call.Model.MaxOutputTokens * .8);
        return new(promptCharacters, contextCharacterLimit, estimatedOutputTokens, outputTokenLimit,
            promptCharacters <= contextCharacterLimit, estimatedOutputTokens <= outputTokenLimit);
    }

    private static bool CorrectionFits(LlmRequest call, IReadOnlyDictionary<string, GeneratedFile> sources)
    {
        var measurement = MeasureCorrection(call, sources);
        return measurement.ContextFits && measurement.OutputFits;
    }

    private static (LlmRequest Call, string Selection) PrepareCorrectionGroup(MissionSnapshot mission,
        MissionWorkflow flow, AgentDefinition developer, ModelDefinition model, IReadOnlyList<string> targets,
        IReadOnlyDictionary<string, GeneratedFile> sources, IReadOnlyList<ReactSourceIssue> issues,
        string reason, int remaining)
    {
        var routes = targets.Select(path => flow.ModelSelections.SingleOrDefault(sequence =>
            sequence.Key == developer.Id + ":" + path)?.Decision.Model ?? model.Name).Distinct().ToArray();
        if (routes.Length != 1)
            throw new StudioValidationException("Correction targets have incompatible frozen routes; no automatic rerouting.");
        var selected = model with { Name = routes[0] };
        var diagnostics = issues.Where(issue => targets.Contains(issue.File, StringComparer.Ordinal)).Cast<object>()
            .Append(new { message = reason }).ToArray();
        var scope = new ReactCorrectionGroup(targets.ToArray(),
            "Fix the attached defects together; preserve every original criterion and business test.");
        var complete = ReactRevisionCorrection.BuildGroupCall(
            mission, developer, selected, scope, sources, diagnostics, remaining);
        const string noSafePatch = "complete-files selected: no safe, unique patch anchors were available";
        var anchors = new List<ReactPatchTarget>();
        foreach (var path in targets)
        {
            var lines = sources[path].Content.Split('\n');
            var located = issues.Where(issue => issue.File == path).ToArray();
            if (located.Length == 0 || located.Any(issue => issue.Line < 1 || issue.Line > lines.Length))
                return (complete, noSafePatch);
            if (located.Any(issue => issue.Code == "TS1002"))
            {
                var syntaxTargets = ReactCorrectionPatches.CreatePrimarySyntaxTargets(
                    path, sources[path].Content, located);
                if (syntaxTargets is null) return (complete, noSafePatch);
                anchors.AddRange(syntaxTargets);
                continue;
            }
            var start = Math.Max(0, located.Min(issue => issue.Line) - 3);
            var end = Math.Min(lines.Length, located.Max(issue => issue.Line) + 2);
            var anchor = string.Join("\n", lines[start..end]);
            if (string.IsNullOrWhiteSpace(anchor) ||
                sources[path].Content.IndexOf(anchor, StringComparison.Ordinal) !=
                sources[path].Content.LastIndexOf(anchor, StringComparison.Ordinal))
                return (complete, noSafePatch);
            anchors.Add(new(path, anchor));
        }
        if (anchors.Count is < 1 or > 8) return (complete, noSafePatch);
        var patches = ReactRevisionCorrection.BuildGroupCall(mission, developer, selected,
            scope with { PatchTargets = anchors.ToArray() }, sources, diagnostics, remaining);
        var patchMeasurements = MeasureCorrection(patches, sources);
        var completeMeasurements = MeasureCorrection(complete, sources);
        if (patchMeasurements.ContextFits && patchMeasurements.OutputFits &&
            patchMeasurements.EstimatedOutputTokens < completeMeasurements.EstimatedOutputTokens)
            return (patches, "exact-patches selected: unique anchors fit and reduce estimated output");
        if (!patchMeasurements.ContextFits && patchMeasurements.OutputFits)
        {
            var compactPatches = ReactRevisionCorrection.BuildGroupCall(mission, developer, selected,
                scope with { PatchTargets = anchors.ToArray() }, sources, diagnostics, remaining,
                compactPatchContext: true);
            var compactMeasurements = MeasureCorrection(compactPatches, sources);
            if (compactMeasurements.ContextFits && compactMeasurements.OutputFits &&
                compactMeasurements.EstimatedOutputTokens < completeMeasurements.EstimatedOutputTokens)
                return (compactPatches,
                    $"compact exact-patches selected after full-context prompt estimate " +
                    $"{patchMeasurements.PromptCharacters}/{patchMeasurements.ContextCharacterLimit} chars; " +
                    $"compact prompt estimate {compactMeasurements.PromptCharacters}/{compactMeasurements.ContextCharacterLimit} chars");

            var compactRejection = !compactMeasurements.ContextFits
                ? $"compact exact-patches prompt estimate {compactMeasurements.PromptCharacters} exceeds {compactMeasurements.ContextCharacterLimit} chars"
                : !compactMeasurements.OutputFits
                    ? $"compact exact-patches output estimate {compactMeasurements.EstimatedOutputTokens} exceeds {compactMeasurements.OutputTokenLimit} tokens"
                    : "compact exact-patches did not reduce estimated output";
            return (complete,
                $"exact-patches rejected: full-context prompt estimate {patchMeasurements.PromptCharacters}/" +
                $"{patchMeasurements.ContextCharacterLimit} chars; {compactRejection}; no smaller safe strategy fit");
        }
        var patchRejection = new List<string>();
        if (!patchMeasurements.ContextFits)
            patchRejection.Add($"prompt estimate {patchMeasurements.PromptCharacters} exceeds {patchMeasurements.ContextCharacterLimit} chars");
        if (!patchMeasurements.OutputFits)
            patchRejection.Add($"output estimate {patchMeasurements.EstimatedOutputTokens} exceeds {patchMeasurements.OutputTokenLimit} tokens");
        return (complete, patchRejection.Count == 0
            ? "complete-files selected: exact-patches did not reduce estimated output"
            : $"exact-patches rejected: {string.Join("; ", patchRejection)}");
    }

    private static bool SourceManifestMatches(MissionWorkflow flow, IReadOnlyList<GeneratedFile> candidate) =>
        flow.Plan!.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal)
            .SetEquals(candidate.Select(file => file.Path));
}
