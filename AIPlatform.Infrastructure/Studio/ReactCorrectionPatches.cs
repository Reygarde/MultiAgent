using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

/// <summary>Exact original text selected during preflight, never a fuzzy search pattern.</summary>
public sealed record ReactPatchTarget(string Path, string Original);
/// <summary>One traceable replacement, with offsets relative to the group's original file.</summary>
public sealed record ReactAppliedPatch(string Path, int Start, string Original, string Replacement,
    string OriginalSha256, string CandidateSha256);

internal static class ReactCorrectionPatches
{
    private static readonly JsonSerializerOptions StrictJson = new(ReactRevisionCorrection.Json)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true
    };
    private sealed record Patch(string Path, string Original, string Replacement);
    private sealed record Reply(Patch[] Patches);
    internal sealed record Applied(GeneratedFile[] Files, ReactAppliedPatch[] Edits);
    internal sealed record DiagnosticChanges(IReadOnlyList<FailureDiagnostic> Existing,
        IReadOnlyList<FailureDiagnostic> New, IReadOnlyList<FailureDiagnostic> Corrected);

    public static string SourceHash(IEnumerable<GeneratedFile> files) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            files.OrderBy(file => file.Path, StringComparer.Ordinal), ReactRevisionCorrection.Json)));

    public static bool HasReadyCandidate(MissionSnapshot mission, ReactSoftwareCorrection correction,
        IReadOnlyList<GeneratedFile> draft)
    {
        if (mission.Workflow?.Plan is not { } plan || correction.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(correction.Trigger) || !correction.Ready || correction.InFlight ||
            correction.Failure is not null || correction.ValidationRejection is not null || correction.Original is not { Count: > 0 } ||
            correction.Candidate is not { Count: > 0 } || correction.Calls is null ||
            correction.CompletedGroups != correction.Calls.Count)
            return false;
        var manifest = plan.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        bool Matches(IReadOnlyList<GeneratedFile> files) => files.Count == manifest.Count &&
            files.All(file => file is not null && file.Content is not null) &&
            manifest.SetEquals(files.Select(file => file.Path));
        if (!Matches(correction.Original) || !Matches(correction.Candidate) || !Matches(draft) ||
            SourceHash(correction.Candidate) != SourceHash(draft))
            return false;
        if (correction.Calls.Count == 0)
            return correction.CallState == ReactSoftwareCorrectionCallState.Prepared &&
                correction.ActiveAttempt is null && correction.ActiveGroupIndex is null &&
                correction.ActiveReservationId is null && correction.ResponseReceipt is null &&
                mission.GenerationReservations?.Any(item => item.CapacityId == correction.Id) != true;
        if (correction.CallState != ReactSoftwareCorrectionCallState.Validated ||
            correction.ResponseReceipt is not { Attempt: { } attempt, ResponseFingerprint: { } responseHash } receipt ||
            receipt.ReceivedAt == default || !IsSha256(responseHash) ||
            correction.ActiveAttempt is not { } active || !SameAttempt(attempt, active) ||
            attempt.MissionId != mission.Id || attempt.WorkflowId != mission.Workflow.WorkflowId ||
            attempt.WorkflowVersion != mission.Workflow.Version ||
            attempt.PlanFingerprint != MissionStore.PlanFingerprint(mission, attempt.WorkflowVersion) ||
            attempt.CorrectionId != correction.Id || attempt.GroupIndex != correction.Calls.Count - 1 ||
            correction.ActiveGroupIndex != attempt.GroupIndex ||
            correction.ActiveReservationId != receipt.ReservationId ||
            attempt.SourceFingerprint is not { } sourceFingerprint || !IsSha256(sourceFingerprint) ||
            attempt.CallFingerprint is not { } callFingerprint || !IsSha256(callFingerprint) ||
            mission.GenerationReservations is not { } reservations)
            return false;
        var charged = reservations.Where(item => item.CapacityId == correction.Id).ToArray();
        return charged.Length == correction.Calls.Count &&
            charged.Select((item, index) => item.Role == "Developer" &&
                item.Model == correction.Calls[index].Model.Name && item.CallId is not null).All(valid => valid) &&
            charged.Last().Id == receipt.ReservationId && charged.Last().CallId == attempt.CallId;
    }

    public static string RequestFingerprint(LlmRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            request, ReactRevisionCorrection.Json)));

    public static string ReadyFingerprint(ReactSoftwareCorrection correction) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(correction, ReactRevisionCorrection.Json)));

    public static string ReadyWorkflowFingerprint(MissionSnapshot mission) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            mission.TemplateId, mission.Task, mission.Sizing,
            mission.Workflow!.Version, mission.Workflow.CorrectionLimit,
            mission.Workflow.Agents, mission.Workflow.Models,
            Revisions = mission.Workflow.Revisions ?? [],
            DeveloperRoutes = mission.Workflow.ModelSelections.Where(selection =>
                mission.Workflow.Agents.Any(agent => agent.Id == selection.AgentId && agent.Role == "Developer"))
        }, ReactRevisionCorrection.Json)));

    public static string ReadyRevalidationFingerprint(ReactReadyCandidateResumption resumption) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            resumption with { RevalidationFingerprint = null }, ReactRevisionCorrection.Json)));

    public static string ReadyCorrectionTrigger(ReactSoftwareCorrection ready, ReactReadyCandidateResumption resumption) =>
        ready.Trigger + ":ready-revalidation:" + resumption.Id.ToString("N");

    public static bool HasReadyResumption(MissionSnapshot mission, ReactReadyCandidateResumption resumption,
        bool requireRejection = false)
    {
        if (mission.TemplateId != MissionReactTemplate.Id || mission.Deleted || mission.Archived ||
            mission.Workflow is not { AutomaticReactCorrections: true, Plan: not null } flow ||
            flow.MissionId != mission.Id || resumption.Id == Guid.Empty || resumption.DeveloperCorrectionId == Guid.Empty ||
            resumption.DeveloperCorrectionId == resumption.ReadyCorrectionId || flow.WorkflowId == Guid.Empty ||
            resumption.RequestedAt == default || resumption.MissionId != mission.Id ||
            resumption.WorkflowId != flow.WorkflowId || resumption.WorkflowVersion != flow.Version ||
            resumption.PlanFingerprint != MissionStore.PlanFingerprint(mission, flow.Version) ||
            resumption.WorkflowFingerprint != ReadyWorkflowFingerprint(mission) ||
            flow.RequirePlanApproval && (flow.PlanApproval is not { Approved: true } approval ||
                approval.Fingerprint != MissionStore.PlanFingerprint(mission, approval.Version)) ||
            flow.ReadyCandidateResumptions.Count(item => item.Id == resumption.Id) != 1 ||
            flow.ReadyCandidateResumptions.Count(item => item.ReadyCorrectionId == resumption.ReadyCorrectionId) != 1 ||
            flow.SoftwareCorrections.Select(item => item.Id).Distinct().Count() != flow.SoftwareCorrections.Count ||
            flow.SoftwareCorrections.Count(item => item.Id == resumption.ReadyCorrectionId) != 1)
            return false;
        var stored = flow.ReadyCandidateResumptions.Single(item => item.Id == resumption.Id);
        if (stored.RevalidationFingerprint != resumption.RevalidationFingerprint ||
            ReadyRevalidationFingerprint(stored) != ReadyRevalidationFingerprint(resumption))
            return false;
        var ready = flow.SoftwareCorrections.Single(item => item.Id == resumption.ReadyCorrectionId);
        if (!HasReadyCandidate(mission, ready, ready.Candidate) || ready.Calls.Count != 0 ||
            resumption.CandidateFingerprint != SourceHash(ready.Candidate) ||
            resumption.ReadyFingerprint != ReadyFingerprint(ready) ||
            flow.PendingSoftwareAgentCall is not null)
            return false;
        var child = flow.SoftwareCorrections.LastOrDefault(item =>
            item.Trigger == ReadyCorrectionTrigger(ready, resumption));
        if (child is not null &&
            (child.ReadyResumptionId != resumption.Id || child.RevalidatedFromId != ready.Id ||
             child.Original is not { Count: > 0 } || SourceHash(child.Original) != resumption.CandidateFingerprint ||
             flow.SoftwareCorrections.Count(item => item.Id == resumption.DeveloperCorrectionId) != 1 ||
             child.Candidate is null || child.Candidate.Count != ready.Candidate.Count ||
             child.Candidate.Any(file => file is null || file.Content is null ||
                 ready.Candidate.All(source => source.Path != file.Path) ||
                 !MissionReactTemplate.IsEditable(file.Path) &&
                 ready.Candidate.Single(source => source.Path == file.Path).Content != file.Content) ||
             child.Candidate.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != child.Candidate.Count ||
             child.Calls is not { Count: > 0 }))
            return false;
        if (child is not null)
        {
            try
            {
                if (child.Calls.Any(call => AllowedPaths(call).Any(path =>
                    !MissionReactTemplate.IsEditable(path) || flow.Plan.Files.All(file => file.Path != path))))
                    return false;
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return false;
            }
        }
        var draft = flow.Proposal ?? flow.Draft ?? [];
        if (draft.Any(file => file is null || file.Content is null) ||
            SourceHash(draft) != resumption.CandidateFingerprint &&
            (child is not { Ready: true } || !HasReadyCandidate(mission, child, draft)))
            return false;
        if (resumption.RevalidatedAt is null)
            return !requireRejection && child is null && resumption.Diagnostics is { Count: 0 } &&
                resumption.RevalidationFingerprint is null;
        return resumption.RevalidatedAt >= resumption.RequestedAt &&
            resumption.Diagnostics is { Count: > 0 and <= 60 } &&
            resumption.Diagnostics.All(issue => issue.Type == "ReactSource" && issue.File is { } path &&
                MissionReactTemplate.IsEditable(path) && flow.Plan.Files.Any(file => file.Path == path) &&
                issue.Line > 0 && issue.Column > 0 && !string.IsNullOrWhiteSpace(issue.Code) && issue.Message is not null) &&
            resumption.RevalidationFingerprint == ReadyRevalidationFingerprint(resumption);
    }

    public static bool IsUnreservedReadyCorrection(MissionSnapshot mission, ReactSoftwareCorrection correction)
    {
        var authorization = mission.Workflow?.ReadyCandidateResumptions.FirstOrDefault(item =>
            item.Id == correction.ReadyResumptionId);
        if (authorization is null || !HasReadyResumption(mission, authorization, requireRejection: true) ||
            !correction.InFlight || correction.CallState != ReactSoftwareCorrectionCallState.InFlight ||
            correction.Failure is not null || correction.ActiveReservationId is not null ||
            correction.ResponseReceipt is not null || correction.ValidationRejection is not null ||
            correction.ActiveAttempt is not { } attempt || correction.ActiveGroupIndex != correction.CompletedGroups ||
            attempt.MissionId != mission.Id || attempt.WorkflowId != mission.Workflow!.WorkflowId ||
            attempt.WorkflowVersion != mission.Workflow.Version || attempt.CorrectionId != correction.Id ||
            attempt.GroupIndex != correction.CompletedGroups || attempt.CallId == Guid.Empty ||
            attempt.PlanFingerprint != MissionStore.PlanFingerprint(mission, attempt.WorkflowVersion) ||
            attempt.SourceFingerprint != SourceHash(correction.Candidate) ||
            attempt.CallFingerprint is not { } fingerprint || !IsSha256(fingerprint) ||
            correction.CompletedGroups < 0 || correction.CompletedGroups >= correction.Calls.Count ||
            mission.GenerationReservations is not { } reservations)
            return false;
        return reservations.Count(item => item.CapacityId == correction.Id) == correction.CompletedGroups &&
            reservations.All(item => item.CallId != attempt.CallId) &&
            attempt.GroupPaths is not null && attempt.GroupPaths.SequenceEqual(
                AllowedPaths(correction.Calls[correction.CompletedGroups]), StringComparer.Ordinal);
    }

    public static string ResponseFingerprint(string response) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response)));

    public static string ValidationFingerprint(ReactCorrectionValidationRejection rejection) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            rejection.ValidationId,
            rejection.Receipt.Attempt,
            rejection.Receipt.ReservationId,
            rejection.Receipt.ResponseFingerprint,
            rejection.Receipt.ReceivedAt,
            rejection.CandidateFingerprint,
            rejection.ExistingDiagnostics,
            rejection.NewDiagnostics,
            rejection.CorrectedDiagnostics,
            rejection.Reason
        }, ReactRevisionCorrection.Json)));

    public static string[] AllowedPaths(LlmRequest call)
    {
        using var input = JsonDocument.Parse(call.Messages[1].Content);
        return input.RootElement.GetProperty("allowedPaths").Deserialize<string[]>()!;
    }

    public static bool HasRetryableValidationRejection(MissionSnapshot mission, ReactSoftwareCorrection correction)
    {
        var rejection = correction.ValidationRejection;
        if (rejection is null) return false;
        if (rejection.Receipt is not { } receipt) return false;
        var storedReceipt = correction.ResponseReceipt;
        var attempt = receipt.Attempt;
        var storedAttempt = storedReceipt?.Attempt;
        var activeAttempt = correction.ActiveAttempt;
        if (correction.Ready || correction.InFlight || correction.Failure is null ||
            correction.CallState != ReactSoftwareCorrectionCallState.ValidationRejected ||
            correction.ActiveGroupIndex is not { } groupIndex || groupIndex != correction.CompletedGroups ||
            correction.ActiveReservationId is not { } activeReservationId ||
            storedReceipt is null || attempt is null || storedAttempt is null || activeAttempt is null ||
            receipt.ReservationId != activeReservationId || receipt.ReceivedAt == default ||
            receipt.ResponseFingerprint is not { } responseFingerprint || !IsSha256(responseFingerprint) ||
            rejection.ValidationId == Guid.Empty || rejection.CandidateFingerprint is not { } candidateFingerprint ||
            !IsSha256(candidateFingerprint) || rejection.ResultFingerprint is not { } resultFingerprint ||
            !IsSha256(resultFingerprint) || ValidationFingerprint(rejection) != resultFingerprint ||
            rejection.Reason != correction.Failure ||
            attempt.MissionId != mission.Id || mission.Workflow is not { } flow ||
            attempt.MissionId != flow.MissionId || attempt.WorkflowId != flow.WorkflowId ||
            attempt.WorkflowVersion != flow.Version ||
            attempt.PlanFingerprint != MissionStore.PlanFingerprint(mission, attempt.WorkflowVersion) ||
            attempt.CorrectionId != correction.Id || attempt.CallId == Guid.Empty ||
            attempt.CallId != activeAttempt.CallId || attempt.GroupIndex != groupIndex ||
            !SameAttempt(attempt, storedAttempt) || !SameAttempt(attempt, activeAttempt) ||
            attempt.SourceFingerprint != SourceHash(correction.Candidate) ||
            !IsSha256(attempt.CallFingerprint) ||
            attempt.GroupPaths is not { Count: >= 1 and <= 4 } ||
            attempt.GroupPaths.Any(path => string.IsNullOrWhiteSpace(path) || !MissionReactTemplate.IsEditable(path)) ||
            attempt.GroupPaths.Distinct(StringComparer.Ordinal).Count() != attempt.GroupPaths.Count ||
            groupIndex < 0 || groupIndex >= correction.Calls.Count || mission.GenerationReservations is not { } reservations)
            return false;

        var call = correction.Calls[groupIndex];
        string[] callPaths;
        try { callPaths = AllowedPaths(call); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
        var matches = reservations.Where(item => item.Id == receipt.ReservationId).ToArray();
        if (matches.Length != 1 || mission.Workflow?.Plan is not { } plan ||
            mission.Workflow.RequirePlanApproval && mission.Workflow.PlanApproval?.Approved != true ||
            attempt.GroupPaths.Any(path => plan.Files.All(file => file.Path != path)))
            return false;
        var reservation = matches[0];
        return reservation is { Role: "Developer" } && reservation.CapacityId == correction.Id &&
            reservation.Model == call.Model.Name && reservation.CallId == attempt.CallId &&
            attempt.GroupPaths.SequenceEqual(callPaths, StringComparer.Ordinal) &&
            reservations.Count(item => item.CapacityId == correction.Id) == correction.CompletedGroups + 1 &&
            mission.Workflow?.SoftwareCorrections.LastOrDefault()?.Id == correction.Id;
    }

    private static bool SameAttempt(ReactCorrectionCallAttempt left, ReactCorrectionCallAttempt right) =>
        left.MissionId == right.MissionId && left.WorkflowId == right.WorkflowId &&
        left.WorkflowVersion == right.WorkflowVersion && left.PlanFingerprint == right.PlanFingerprint &&
        left.CorrectionId == right.CorrectionId && left.CallId == right.CallId &&
        left.GroupIndex == right.GroupIndex && left.SourceFingerprint == right.SourceFingerprint &&
        left.CallFingerprint == right.CallFingerprint &&
        left.GroupPaths is not null && right.GroupPaths is not null &&
        left.GroupPaths.SequenceEqual(right.GroupPaths, StringComparer.Ordinal);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    public static ReactPatchTarget[]? Targets(LlmRequest call)
    {
        using var input = JsonDocument.Parse(call.Messages[1].Content);
        return input.RootElement.TryGetProperty("patchTargets", out var targets)
            ? targets.Deserialize<ReactPatchTarget[]>(StrictJson)
                ?? throw new StudioValidationException("Missing prepared patch targets.")
            : null;
    }

    public static ReactPatchTarget[]? CreatePrimarySyntaxTargets(string path, string content,
        IReadOnlyList<ReactSourceIssue> issues)
    {
        var syntaxIssues = issues.Where(issue => issue.Code.StartsWith("TS", StringComparison.Ordinal)).ToArray();
        if (!syntaxIssues.Any(issue => issue.Code == "TS1002") ||
            syntaxIssues.Any(issue => issue.Code is not ("TS1002" or "TS1005" or "TS1128")))
            return null;

        var lines = content.Split('\n');
        var primaryIssues = issues.Where(issue => issue.Code == "TS1002" ||
            !issue.Code.StartsWith("TS", StringComparison.Ordinal)).ToArray();
        if (primaryIssues.Length is < 1 or > 8 ||
            primaryIssues.Any(issue => issue.Line < 1 || issue.Line > lines.Length))
            return null;

        var targets = new List<ReactPatchTarget>();
        foreach (var lineNumber in primaryIssues.Select(issue => issue.Line).Distinct().Order())
        {
            var original = lines[lineNumber - 1];
            if (string.IsNullOrWhiteSpace(original) ||
                content.IndexOf(original, StringComparison.Ordinal) != content.LastIndexOf(original, StringComparison.Ordinal))
                return null;
            targets.Add(new(path, original));
        }
        return targets.ToArray();
    }

    public static void ValidateTargets(ReactPatchTarget[] targets, string[] paths,
        IReadOnlyDictionary<string, GeneratedFile> sources)
    {
        if (targets.Length is < 1 or > 8 || targets.Any(target => target is null ||
                target.Path is null || !paths.Contains(target.Path, StringComparer.Ordinal) ||
                !MissionReactTemplate.IsEditable(target.Path) || !sources.ContainsKey(target.Path) ||
                string.IsNullOrWhiteSpace(target.Original)) ||
            targets.Distinct().Count() != targets.Length ||
            !targets.Select(target => target.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .SequenceEqual(paths.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new StudioValidationException("Patch targets must be distinct, cover every exact allowed path and contain nonempty original text.");
        foreach (var group in targets.GroupBy(target => target.Path, StringComparer.Ordinal))
        {
            var end = -1;
            foreach (var span in group.Select(target => (Start: ExactSourceReplacement.FindUnique(sources[group.Key].Content, target.Original),
                Length: target.Original.Length)).OrderBy(span => span.Start))
            {
                if (span.Start < end) throw new StudioValidationException("Overlapping patches are forbidden.");
                end = span.Start + span.Length;
            }
        }
    }

    public static int BaselineBytes(LlmRequest call, string[] paths, IReadOnlyDictionary<string, GeneratedFile> sources)
    {
        var targets = Targets(call);
        if (targets is null)
            return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { files = paths.Select(path => sources[path]) }, ReactRevisionCorrection.Json));
        ValidateTargets(targets, paths, sources);
        // Size probe only: an unchanged replacement is never accepted as a correction.
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new {
            patches = targets.Select(target => new { target.Path, target.Original, Replacement = target.Original })
        }, ReactRevisionCorrection.Json));
    }

    public static Applied Apply(string answer, LlmRequest call, string[] paths,
        IReadOnlyDictionary<string, GeneratedFile> sources)
    {
        var targets = Targets(call);
        if (targets is null) return new(ReactRevisionCorrection.ReadReplacements(answer, paths), []);
        ValidateTargets(targets, paths, sources);
        Reply reply;
        try { reply = JsonSerializer.Deserialize<Reply>(answer, StrictJson) ?? throw new JsonException(); }
        catch (JsonException)
        {
            throw new StudioValidationException("The Developer returned incomplete or invalid patch JSON; no patch was applied.");
        }
        if (reply.Patches is not { } patches || patches.Length != targets.Length ||
            patches.Any(patch => patch is null || patch.Replacement is null || patch.Original == patch.Replacement ||
                !targets.Contains(new(patch.Path, patch.Original))) ||
            patches.Select(patch => new ReactPatchTarget(patch.Path, patch.Original)).Distinct().Count() != targets.Length)
            throw new StudioValidationException("The Developer must return every exact prepared patch once, with a changed replacement and no extra patch.");
        var files = new List<GeneratedFile>();
        var edits = new List<ReactAppliedPatch>();
        foreach (var group in patches.GroupBy(patch => patch.Path, StringComparer.Ordinal))
        {
            var original = sources[group.Key].Content;
            var content = original;
            var spans = group.Select(patch => (Patch: patch, Start: ExactSourceReplacement.FindUnique(original, patch.Original)))
                .OrderByDescending(span => span.Start).ToArray();
            foreach (var span in spans)
                content = content[..span.Start] + span.Patch.Replacement + content[(span.Start + span.Patch.Original.Length)..];
            var before = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)));
            var after = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            foreach (var span in spans)
                edits.Add(new(group.Key, span.Start, span.Patch.Original, span.Patch.Replacement, before, after));
            files.Add(new(group.Key, content));
        }
        return new(files.ToArray(), edits.ToArray());
    }

    public static void ValidateDiagnostics(IReadOnlyList<ReactSourceIssue> before, IReadOnlyList<ReactSourceIssue> after,
        string[] paths)
    {
        var changes = ClassifyDiagnostics(before, after);
        if (changes.New.Count > 0 || after.Any(issue => paths.Contains(issue.File, StringComparer.Ordinal)))
            throw new StudioValidationException("The correction left target diagnostics or introduced a static regression; candidate rejected.\n" +
                ReactSourceCheck.Describe(after.Take(20)));
    }

    public static DiagnosticChanges ClassifyDiagnostics(IReadOnlyList<ReactSourceIssue> before,
        IReadOnlyList<ReactSourceIssue> after)
    {
        static (string File, int Line, int Column, string Code, string Message) Key(ReactSourceIssue issue) =>
            (issue.File, issue.Line, issue.Column, issue.Code, issue.Message);
        static FailureDiagnostic ToDiagnostic(ReactSourceIssue issue) =>
            new("ReactSource", null, issue.File, issue.Line, issue.Column, issue.Code, issue.Message);

        var beforeCounts = before.GroupBy(Key).ToDictionary(group => group.Key, group => group.Count());
        var afterCounts = after.GroupBy(Key).ToDictionary(group => group.Key, group => group.Count());
        var existing = new List<FailureDiagnostic>();
        var introduced = new List<FailureDiagnostic>();
        foreach (var issue in after)
        {
            var key = Key(issue);
            if (beforeCounts.GetValueOrDefault(key) > 0)
            {
                existing.Add(ToDiagnostic(issue));
                beforeCounts[key]--;
            }
            else introduced.Add(ToDiagnostic(issue));
        }

        var corrected = new List<FailureDiagnostic>();
        foreach (var issue in before)
        {
            var key = Key(issue);
            if (afterCounts.GetValueOrDefault(key) > 0)
            {
                afterCounts[key]--;
                continue;
            }
            corrected.Add(ToDiagnostic(issue));
        }
        return new(existing, introduced, corrected);
    }
}
