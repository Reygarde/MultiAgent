using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class ReviewerCorrectionResumeTests
{
    private const string FiveCriteria =
        "1. Display the initial value 0.\n2. Add one with the increment button.\n3. Remove one with the decrement button.\n4. Reset the value to 0.\n5. Display a readable interface.";
    private const string ContextFailure =
        "Agent ReviewAgent context budget exceeded for ReadSources: 12730 characters, limit 10240";

    [Fact]
    public async Task TargetedResumeReusesRejectedReportAndPreservesStepPlanSourcesAndBudget()
    {
        using var fixture = new WorkflowFixture(available: false,
            responseOverride: MissionReactContractTests.Respond);
        var (mission, diagnostics) = await CreateInterruptedReviewAsync(fixture);
        var service = new ReviewerCorrectionResumeService(
            fixture.Store, fixture.Workspaces, diagnostics, fixture.Options);
        var sourceFiles = mission.Workflow!.Draft!.ToArray();
        var revisionCount = mission.Workflow.Revisions!.Count;
        var requestsBefore = fixture.Provider.Requests.Count;
        var generationsBefore = mission.GenerationReservations!.Count;

        var eligibility = await service.GetEligibilityAsync(mission.Id, default);
        Assert.True(eligibility!.Available, eligibility.Reason);
        var queued = await service.ResumeAsync(mission.Id, default);
        Assert.True(queued.Accepted, queued.Reason);

        var pending = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.Queued, pending.Status);
        Assert.Equal(mission.Workflow.Version, pending.Workflow!.Version);
        Assert.Equal("review-1", pending.Workflow.ReviewerCorrectionResume!.StepId);
        Assert.Equal(WorkflowStatus.Pending, pending.Workflow.Steps.Single(step => step.Kind == "Review").Status);
        Assert.Equal(generationsBefore, pending.GenerationReservations!.Count);
        Assert.Equal(revisionCount, pending.Workflow.Revisions!.Count);
        Assert.Equal(sourceFiles, pending.Workflow.Draft);

        await fixture.Processor.ProcessAsync(mission.Id, default);

        var completed = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.GeneratedUnverified, completed.Status);
        Assert.Equal(mission.Workflow.Version, completed.Workflow!.Version);
        Assert.Equal(WorkflowStatus.Completed, completed.Workflow.Steps.Single(step => step.Kind == "Review").Status);
        Assert.Equal("Completed", completed.Workflow.ReviewerCorrectionResume!.Status);
        Assert.Equal(generationsBefore + 1, completed.GenerationReservations!.Count);
        Assert.Equal(requestsBefore + 1, fixture.Provider.Requests.Count);
        Assert.Equal(sourceFiles, completed.Workflow.Draft);
        Assert.Equal(revisionCount, completed.Workflow.Revisions!.Count);

        using var correctedInput = JsonDocument.Parse(fixture.Provider.Requests.Last().Messages[1].Content);
        Assert.Equal(new[] { "C1", "C2", "C3", "C4", "C5" },
            correctedInput.RootElement.GetProperty("acceptanceCriteria").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()));
        Assert.Equal("Rejected report", correctedInput.RootElement.GetProperty("rejectedReport")
            .GetProperty("summary").GetString());
        Assert.Equal(4, correctedInput.RootElement.GetProperty("validationErrors").GetArrayLength());
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task ChangedWorkspaceSourcesRefuseRetryWithoutQueueingOrGenerating()
    {
        using var fixture = new WorkflowFixture(available: false,
            responseOverride: MissionReactContractTests.Respond);
        var (mission, diagnostics) = await CreateInterruptedReviewAsync(fixture);
        var changed = mission.Workflow!.Draft!.Select(file => file.Path == "src/App.tsx"
            ? file with { Content = file.Content + "\n// changed" } : file).ToArray();
        await fixture.Workspaces.WriteAsync(mission.Id, changed, default, mission.TemplateId);
        var service = new ReviewerCorrectionResumeService(
            fixture.Store, fixture.Workspaces, diagnostics, fixture.Options);

        var eligibility = await service.GetEligibilityAsync(mission.Id, default);

        Assert.False(eligibility!.Available);
        Assert.Contains("a changé", eligibility.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MissionStatus.Failed, fixture.Store.Get(mission.Id)!.Status);
        Assert.Null(fixture.Store.Get(mission.Id)!.Workflow!.ReviewerCorrectionResume);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task MissingRejectedReportOrSourceFingerprintRefusesRetry()
    {
        using var fixture = new WorkflowFixture(available: false,
            responseOverride: MissionReactContractTests.Respond);
        var (mission, diagnostics) = await CreateInterruptedReviewAsync(fixture);
        var service = new ReviewerCorrectionResumeService(
            fixture.Store, fixture.Workspaces, diagnostics, fixture.Options);

        diagnostics.DeleteReviewerAttempts(mission.Id);
        var missing = await service.GetEligibilityAsync(mission.Id, default);
        Assert.False(missing!.Available);
        Assert.Contains("rapport Reviewer rejeté", missing.Reason);

        var flow = fixture.Store.Get(mission.Id)!.Workflow!;
        var review = flow.Steps.Single(step => step.Kind == "Review");
        var context = new LlmDiagnosticContext(mission.Id, flow.WorkflowId, review.Id, Guid.NewGuid());
        diagnostics.BeginReviewer(context, "simulated-reviewer", 2, "", null);
        using var report = JsonDocument.Parse("""{"summary":"Unbound rejected report"}""");
        diagnostics.RecordReviewerResult(context, report.RootElement);
        diagnostics.FinishReviewer(context, "Rejected",
            [new("C2", "MissingCriterion", "criterionReviews", null, null, "Criterion missing.")]);

        var unbound = await service.GetEligibilityAsync(mission.Id, default);
        Assert.False(unbound!.Available);
        Assert.Contains("empreinte", unbound.Reason);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task ExhaustedCorrectionOrGenerationBudgetRefusesRetry()
    {
        using (var fixture = new WorkflowFixture(available: false, corrections: 0,
            responseOverride: MissionReactContractTests.Respond))
        {
            var (mission, diagnostics) = await CreateInterruptedReviewAsync(fixture);
            var service = new ReviewerCorrectionResumeService(
                fixture.Store, fixture.Workspaces, diagnostics, fixture.Options);
            var eligibility = await service.GetEligibilityAsync(mission.Id, default);
            Assert.False(eligibility!.Available);
            Assert.Contains("corrections Reviewer", eligibility.Reason);
        }

        using (var fixture = new WorkflowFixture(available: false,
            responseOverride: MissionReactContractTests.Respond))
        {
            var (mission, diagnostics) = await CreateInterruptedReviewAsync(fixture, maxGenerations: 5);
            Assert.Equal(5, mission.GenerationReservations!.Count);
            var service = new ReviewerCorrectionResumeService(
                fixture.Store, fixture.Workspaces, diagnostics, fixture.Options);
            var eligibility = await service.GetEligibilityAsync(mission.Id, default);
            Assert.False(eligibility!.Available);
            Assert.Contains("budget global", eligibility.Reason);
        }
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsQueueOnlyOneTargetedResume()
    {
        using var fixture = new WorkflowFixture(available: false,
            responseOverride: MissionReactContractTests.Respond);
        var (mission, diagnostics) = await CreateInterruptedReviewAsync(fixture);
        var service = new ReviewerCorrectionResumeService(
            fixture.Store, fixture.Workspaces, diagnostics, fixture.Options);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.ResumeAsync(mission.Id, default)));

        Assert.All(results, result => Assert.True(result.Accepted, result.Reason));
        Assert.Single(results, result => !result.AlreadyAccepted);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.Queued, current.Status);
        Assert.Equal("Queued", current.Workflow!.ReviewerCorrectionResume!.Status);
        Assert.Equal("review-1", current.Workflow.ReviewerCorrectionResume.StepId);
        Assert.Equal(mission.GenerationReservations!.Count, current.GenerationReservations!.Count);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task RestartDuringCorrectionRetainsReservationAndAllowsSourceBoundRetry()
    {
        using var fixture = new WorkflowFixture(available: false,
            responseOverride: MissionReactContractTests.Respond);
        var (mission, diagnostics) = await CreateInterruptedReviewAsync(fixture);
        var service = new ReviewerCorrectionResumeService(
            fixture.Store, fixture.Workspaces, diagnostics, fixture.Options);
        Assert.True((await service.ResumeAsync(mission.Id, default)).Accepted);

        var queued = fixture.Store.Get(mission.Id)!;
        var priorRequest = fixture.Provider.Requests.Last();
        using (fixture.Store.EnterGenerationScope(mission.Id, "Reviewer"))
            await BudgetedLlmProvider.Wrap(fixture.Provider).CompleteAsync(priorRequest, default);
        fixture.Store.Update(mission.Id, current => current with
        {
            Status = MissionStatus.Running,
            Workflow = current.Workflow! with
            {
                ReviewerCorrectionResume = current.Workflow.ReviewerCorrectionResume! with { Status = "Running" },
                Steps = current.Workflow.Steps.Select(step => step.Kind == "Review"
                    ? step with { Status = WorkflowStatus.Running, Error = null } : step).ToArray()
            }
        });

        using var restarted = new MissionStore(
            fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        var recovered = restarted.Get(mission.Id)!;
        Assert.Equal(MissionStatus.Failed, recovered.Status);
        Assert.Equal(MissionStore.ReviewerCorrectionRestartFailure, recovered.Error);
        Assert.Equal("Interrupted", recovered.Workflow!.ReviewerCorrectionResume!.Status);
        Assert.Equal("review-1", recovered.Workflow.ReviewerCorrectionResume.StepId);
        Assert.Equal(queued.GenerationReservations!.Count + 1, recovered.GenerationReservations!.Count);
        Assert.Contains(diagnostics.ReviewerAttempts(mission.Id), attempt =>
            attempt.AttemptId == recovered.Workflow.ReviewerCorrectionResume.RejectedAttemptId &&
            attempt.Status == "Rejected");

        var afterRestart = new ReviewerCorrectionResumeService(
            restarted, fixture.Workspaces, diagnostics, fixture.Options);
        var eligibility = await afterRestart.GetEligibilityAsync(mission.Id, default);
        Assert.True(eligibility!.Available, eligibility.Reason);
        var resumed = await afterRestart.ResumeAsync(mission.Id, default);
        Assert.True(resumed.Accepted, resumed.Reason);
        Assert.Equal("Queued", restarted.Get(mission.Id)!.Workflow!.ReviewerCorrectionResume!.Status);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task GeneralResumeStillCreatesItsExistingNewReviewVersion()
    {
        using var fixture = new WorkflowFixture(available: false,
            responseOverride: MissionReactContractTests.Respond);
        var (mission, _) = await CreateInterruptedReviewAsync(fixture);
        Assert.True(fixture.Store.Resume(mission.Id));
        var resumed = fixture.Store.Get(mission.Id)!;
        Assert.Equal(mission.Workflow!.Version + 1, resumed.Workflow!.Version);
        Assert.StartsWith("resume-review-", resumed.Workflow.Steps.Last(step => step.Kind == "Review").Id);
        Assert.Null(resumed.Workflow.ReviewerCorrectionResume);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task CorrectionStillRejectsInventedSourceCitations()
    {
        using var fixture = new WorkflowFixture(available: false,
            responseOverride: request =>
            {
                var response = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
                if (request.Messages[1].Content.Contains("\"rejectedReport\"", StringComparison.Ordinal))
                    response["criterionReviews"]![1]!["evidence"]![0]!["quote"] = "invented citation";
                return response.ToJsonString();
            });
        var (mission, diagnostics) = await CreateInterruptedReviewAsync(fixture);
        var service = new ReviewerCorrectionResumeService(
            fixture.Store, fixture.Workspaces, diagnostics, fixture.Options);
        Assert.True((await service.ResumeAsync(mission.Id, default)).Accepted);

        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Processor.ProcessAsync(mission.Id, default));

        Assert.Contains(diagnostics.ReviewerAttempts(mission.Id)
            .SelectMany(attempt => attempt.Errors), error => error.Rule == "QuoteMismatch");
        Assert.Null(fixture.Store.Get(mission.Id)!.Workflow!.Review);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    private static async Task<(MissionSnapshot Mission, OllamaDiagnosticStore Diagnostics)> CreateInterruptedReviewAsync(
        WorkflowFixture fixture, int maxGenerations = 10)
    {
        var submitted = fixture.Store.Submit(new(FiveCriteria, MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, MaxTotalGenerations: maxGenerations));
        await fixture.Processor.ProcessAsync(submitted.Id, default);
        var generated = fixture.Store.Get(submitted.Id)!;
        var flow = generated.Workflow!;
        var step = flow.Steps.Single(item => item.Kind == "Review");
        var fingerprint = WorkflowSourceFingerprint.Compute(generated);
        var context = new LlmDiagnosticContext(generated.Id, flow.WorkflowId, step.Id, Guid.NewGuid());
        var diagnostics = new OllamaDiagnosticStore(fixture.Options, TimeProvider.System);
        diagnostics.BeginReviewer(context, "simulated-reviewer", 2, fingerprint, null);
        using var report = JsonDocument.Parse("""
            {"summary":"Rejected report","criterionReviews":[{"criterionId":"C1","verdict":"Satisfied"}]}
            """);
        diagnostics.RecordReviewerResult(context, report.RootElement);
        diagnostics.FinishReviewer(context, "Rejected",
        [
            new("C2", "MissingCriterion", "criterionReviews", null, null, "C2 is required."),
            new("C3", "MissingCriterion", "criterionReviews", null, null, "C3 is required."),
            new("C4", "MissingCriterion", "criterionReviews", null, null, "C4 is required."),
            new("C5", "MissingCriterion", "criterionReviews", null, null, "C5 is required.")
        ]);
        await Task.Delay(2);
        var failed = fixture.Store.Update(generated.Id, current => current with
        {
            Status = MissionStatus.Failed,
            Error = ContextFailure,
            SandboxApproved = false,
            ApprovalFingerprint = null,
            Workflow = current.Workflow! with
            {
                Review = null,
                FinalReport = null,
                Steps = current.Workflow.Steps.Where(item => item.Kind is not
                        ("Approval" or "TypeScript" or "Build" or "Tests"))
                    .Select(item => item.Id == step.Id
                    ? item with { Status = WorkflowStatus.Failed, Error = ContextFailure } : item).ToArray()
            }
        });
        return (failed, diagnostics);
    }
}
