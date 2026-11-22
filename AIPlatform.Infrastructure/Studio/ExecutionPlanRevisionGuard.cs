using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static class ExecutionPlanRevisionGuard
{
    public static IReadOnlyList<ExecutionPlanDifference> Compare(
        ProjectRevision revision, ExecutionPlan current, string? templateId, bool staticReview = false)
    {
        if (!staticReview && templateId == MissionReactTemplate.Id && revision.PreparedExecutionPlan is { } prepared)
            revision = revision with { Fingerprint = prepared.Fingerprint, ExecutionPlan = prepared };
        if (templateId is not (ReactTemplate.Id or MissionReactTemplate.Id) ||
            revision.ExecutionPlan?.ReactDescriptor is not { } expected)
            return CompareFingerprint(revision.Fingerprint, current.Fingerprint);

        var differences = new List<ExecutionPlanDifference>();
        var savedPlan = revision.ExecutionPlan;
        if (savedPlan.Fingerprint != revision.Fingerprint)
            differences.Add(new("executionPlan.fingerprint", revision.Fingerprint, savedPlan.Fingerprint));

        differences.AddRange(expected.CompareManifest(revision.Files));
        var recomputed = expected.ComputeFingerprint(revision.Files);
        if (recomputed != revision.Fingerprint)
            differences.Add(new("executionPlan.recomputedFingerprint", revision.Fingerprint, recomputed));

        if (current.ReactDescriptor is not { } actual)
            differences.Add(new("executionPlan.reactDescriptor", "present", "missing"));
        else
            differences.AddRange(expected.CompareTo(actual));

        if (current.Fingerprint != revision.Fingerprint)
            differences.Add(new("fingerprint", revision.Fingerprint, current.Fingerprint));
        return differences;
    }

    private static IReadOnlyList<ExecutionPlanDifference> CompareFingerprint(string expected, string actual) =>
        expected == actual ? [] : [new("fingerprint", expected, actual)];
}
