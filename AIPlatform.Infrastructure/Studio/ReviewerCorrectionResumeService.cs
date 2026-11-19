using System.Text.Json;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed record ReviewerCorrectionResumeEligibility(bool Available, bool InProgress, string Reason);
public sealed record ReviewerCorrectionResumeResult(bool Accepted, bool AlreadyAccepted, string Reason);
public sealed record ReviewerCorrectionResumeQueueResult(bool Accepted, bool AlreadyAccepted, string Reason);

public sealed class ReviewerCorrectionResumeService(
    MissionStore missions, IWorkspaceStore workspaces, OllamaDiagnosticStore diagnostics, StudioOptions options)
{
    public async Task<ReviewerCorrectionResumeEligibility?> GetEligibilityAsync(Guid id, CancellationToken token)
    {
        var mission = missions.Get(id);
        return mission is null ? null : await AssessAsync(mission, token);
    }

    public async Task<ReviewerCorrectionResumeResult> ResumeAsync(Guid id, CancellationToken token)
    {
        var mission = missions.Get(id);
        if (mission is null) return new(false, false, "Mission introuvable.");

        var eligibility = await AssessAsync(mission, token);
        if (eligibility.InProgress) return new(true, true, eligibility.Reason);
        if (!eligibility.Available) return new(false, false, eligibility.Reason);

        var workflow = mission.Workflow!;
        var review = LatestReviewStep(workflow)!;
        var fingerprint = WorkflowSourceFingerprint.Compute(mission);
        var attempt = FindRejectedAttempt(diagnostics.ReviewerAttempts(id), mission, review, fingerprint)!;
        var originalFailure = workflow.ReviewerCorrectionResume is { Status: "Interrupted" } previous
            ? previous.OriginalFailure : review.Error!;
        var resume = new ReviewerCorrectionResumeState(Guid.NewGuid(), review.Id, attempt.AttemptId,
            attempt.SourceFingerprint, originalFailure, "Queued", DateTimeOffset.UtcNow);
        return diagnostics.WithReviewerAttempts(id, attempts =>
        {
            var currentAttempt = FindRejectedAttempt(attempts, mission, review, fingerprint);
            if (currentAttempt?.AttemptId != attempt.AttemptId)
                return new ReviewerCorrectionResumeResult(
                    false, false, "Le rapport rejeté a été supprimé ou remplacé pendant la vérification.");
            var result = missions.QueueReviewerCorrectionResume(id, mission.UpdatedAt, resume);
            return new ReviewerCorrectionResumeResult(result.Accepted, result.AlreadyAccepted, result.Reason);
        });
    }

    private async Task<ReviewerCorrectionResumeEligibility> AssessAsync(MissionSnapshot mission, CancellationToken token)
    {
        var flow = mission.Workflow;
        if (flow?.ReviewerCorrectionResume is { Status: "Queued" or "Running" } active &&
            mission.Status is MissionStatus.Queued or MissionStatus.Running)
            return new(false, true, "La correction Reviewer ciblée est déjà en file ou en cours.");
        if (mission.Deleted || mission.Archived)
            return Refuse("La mission est supprimée ou archivée.");
        if (mission.Status != MissionStatus.Failed || flow is null)
            return Refuse("La reprise ciblée exige une mission en échec sur son étape Reviewer.");
        if (mission.TemplateId != MissionReactTemplate.Id || !flow.ReviewerCorrectionEnabled)
            return Refuse("La reprise ciblée est disponible uniquement pour les missions React v2 avec correction Reviewer activée.");
        if (mission.GenerationBudgetExhausted || mission.MaxTotalGenerations is not { } generationLimit ||
            mission.GenerationReservations is not { } reservations || reservations.Count >= generationLimit)
            return Refuse("Le budget global de générations ne permet pas une nouvelle correction Reviewer.");

        var correctionLimit = options.CorrectionLimitFor(mission);
        if (mission.CorrectionAttempts >= correctionLimit)
            return Refuse("Le budget de corrections Reviewer est épuisé.");
        if (flow.RequirePlanApproval && flow.PlanApproval?.Approved != true)
            return Refuse("Le plan n’a pas l’approbation persistée requise.");
        if (flow.Steps.Any(step => step.Status == WorkflowStatus.Running))
            return Refuse("Une étape de la mission est encore en cours.");

        var reviewStep = LatestReviewStep(flow);
        if (reviewStep is null || reviewStep.Id != $"review-{flow.Version}" ||
            reviewStep.Status != WorkflowStatus.Failed || reviewStep.Attempts < 1)
            return Refuse("L’étape Reviewer en échec n’est pas celle liée au rejet d’origine.");

        var interruptedByRestart = flow.ReviewerCorrectionResume is { Status: "Interrupted" } &&
            mission.Error == MissionStore.ReviewerCorrectionRestartFailure &&
            reviewStep.Error == MissionStore.ReviewerCorrectionRestartFailure;
        var originalFailure = flow.ReviewerCorrectionResume is { Status: "Interrupted" } prior
            ? prior.OriginalFailure : reviewStep.Error;
        if (!interruptedByRestart && !IsContextInterruption(reviewStep.Error))
            return Refuse("L’échec n’est pas une interruption technique reconnue de la correction Reviewer.");
        if (originalFailure is null || !IsContextInterruption(originalFailure))
            return Refuse("La cause technique initiale de l’interruption n’est pas vérifiable.");
        if (flow.Review is not null || flow.Plan is null || flow.Draft is not { Count: > 0 } draft ||
            flow.Revisions?.LastOrDefault() is not { } revision || revision.Version != flow.Version ||
            string.IsNullOrWhiteSpace(revision.Fingerprint) || revision.Fingerprint.Length != 64 ||
            !revision.Fingerprint.All(Uri.IsHexDigit) || revision.Files is not { Count: > 0 } revisionFiles)
            return Refuse("Le plan, la version ou l’empreinte persistée des sources est absente.");
        if (flow.Steps.Any(step => step.Kind == "Review" && step.Id != reviewStep.Id &&
            step.Status is WorkflowStatus.Running or WorkflowStatus.Completed))
            return Refuse("Une autre étape Reviewer a remplacé l’étape rejetée.");

        if (!SameSources(revisionFiles, draft))
            return Refuse("Les sources de la révision ne correspondent plus au brouillon lié à la revue.");
        var workspace = workspaces.List(mission.Id);
        if (workspace.Count != draft.Count ||
            workspace.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != workspace.Count ||
            draft.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != draft.Count ||
            workspace.Any(file => draft.All(source => source.Path != file.Path)))
            return Refuse("Les fichiers actuels ne correspondent pas exactement à l’ensemble des sources de la revue.");
        foreach (var file in draft)
        {
            token.ThrowIfCancellationRequested();
            var current = await workspaces.ReadAsync(mission.Id, file.Path, token);
            if (!string.Equals(current.Content, file.Content, StringComparison.Ordinal))
                return Refuse($"La source {file.Path} a changé depuis la revue rejetée.");
        }

        var fingerprint = WorkflowSourceFingerprint.Compute(mission);
        if (string.IsNullOrWhiteSpace(fingerprint))
            return Refuse("L’empreinte des sources de la revue est absente.");
        var privateAttempts = diagnostics.ReviewerAttempts(mission.Id);
        if (privateAttempts.Any(item => item.RunId == flow.WorkflowId &&
            item.StepId == reviewStep.Id && string.IsNullOrWhiteSpace(item.SourceFingerprint) &&
            item.Status == "Rejected"))
            return Refuse("Le rapport rejeté ne possède pas d’empreinte de sources persistée.");
        var rejected = FindRejectedAttempt(privateAttempts, mission, reviewStep, fingerprint);
        if (rejected is null || rejected.Report is not { ValueKind: JsonValueKind.Object } ||
            rejected.Errors is not { Count: > 0 } || rejected.AttemptNumber < 1 ||
            string.IsNullOrWhiteSpace(rejected.Model))
            return Refuse("Le rapport Reviewer rejeté ou ses diagnostics privés exacts sont absents.");

        var reviewerInvocations = flow.ModelInvocations.Where(item => item.StepId == reviewStep.Id &&
            item.Attempt.Role == "Reviewer").ToArray();
        if (reviewerInvocations.Length > 0 &&
            reviewerInvocations.All(item => item.Attempt.Id != rejected.AttemptId ||
                item.Attempt.ModelName != rejected.Model))
            return Refuse("L’identité du rapport rejeté ne correspond à aucun appel Reviewer de cette étape.");
        if (flow.AutomaticModelSelection &&
            !reviewerInvocations.Any(item => item.Attempt.Id == rejected.AttemptId &&
                item.Attempt.ModelName == rejected.Model))
            return Refuse("L’identité de l’appel Reviewer routé ne correspond pas au rapport privé.");

        if (flow.ReviewerCorrectionResume is { Status: "Completed" or "Superseded" })
            return Refuse("Cette reprise ciblée a déjà été terminée ou remplacée.");

        return new(true, false, "Reprise ciblée autorisée : l’étape, le rapport rejeté, le plan et toutes les sources sont identiques.");
    }

    private static ReviewerAttemptDiagnostic? FindRejectedAttempt(
        IReadOnlyList<ReviewerAttemptDiagnostic> allAttempts, MissionSnapshot mission,
        WorkflowStep step, string sourceFingerprint)
    {
        var attempts = allAttempts
            .Where(item => item.MissionId == mission.Id && item.RunId == mission.Workflow!.WorkflowId &&
                item.StepId == step.Id && item.RoutingPath is null &&
                item.SourceFingerprint == sourceFingerprint)
            .OrderBy(item => item.CreatedAt).ToArray();
        var latestReport = attempts.LastOrDefault(item => item.Status is "Rejected" or "Accepted");
        return latestReport is { Status: "Rejected" } ? latestReport : null;
    }

    private static WorkflowStep? LatestReviewStep(MissionWorkflow workflow) =>
        workflow.Steps.LastOrDefault(step => step.Kind == "Review");

    private static bool SameSources(IReadOnlyList<GeneratedFile> revision, IReadOnlyList<GeneratedFile> draft)
    {
        if (revision.Count != draft.Count ||
            revision.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != revision.Count ||
            draft.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != draft.Count)
            return false;
        var expected = revision.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal);
        return draft.All(file => expected.TryGetValue(file.Path, out var content) &&
            string.Equals(content, file.Content, StringComparison.Ordinal));
    }

    private static bool IsContextInterruption(string? error) =>
        error?.StartsWith("Agent ReviewAgent context budget exceeded for ReadSources:", StringComparison.Ordinal) == true ||
        error?.StartsWith("Reviewer correction context remains too large", StringComparison.Ordinal) == true;

    private static ReviewerCorrectionResumeEligibility Refuse(string reason) => new(false, false, reason);
}
