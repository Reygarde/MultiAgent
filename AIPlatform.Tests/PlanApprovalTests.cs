using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class PlanApprovalTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(ReactTemplate.Id)]
    [InlineData(FullStackTemplate.Id)]
    public async Task ExistingProjectContractsCanUseTheSameOptionalGate(string? template)
    {
        using var fixture = new WorkflowFixture(corrections: 0, fullStack: template == FullStackTemplate.Id,
            responseOverride: template == ReactTemplate.Id ? MissionReactContractTests.Respond : null);
        var mission = fixture.Store.Submit(new("Create the requested project", MissionKind.Project,
            TemplateId: template, RequirePlanApproval: true));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var waiting = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingPlanApproval, waiting.Status);
        Assert.Single(fixture.Provider.Requests);
        Assert.True(fixture.Store.DecidePlanApproval(mission.Id, true, waiting.Workflow!.PlanApproval!.Fingerprint));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(MissionStatus.AwaitingApproval, fixture.Store.Get(mission.Id)!.Status);
        Assert.Single(fixture.Provider.Requests, request =>
            request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("decisions", out _));
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public void TextOnlyMissionsCannotRequestAProjectPlanApproval()
    {
        using var fixture = Fixture();
        Assert.Throws<StudioValidationException>(() =>
            fixture.Store.Submit(new("Explain the requested information", RequirePlanApproval: true)));
        Assert.Empty(fixture.Store.List());
        Assert.Empty(fixture.Provider.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptInStopsAfterOnePlannerCallAndRepeatedProcessingCannotStartDeveloper(bool required)
    {
        using var fixture = Fixture();
        var mission = Submit(fixture, required);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(required ? MissionStatus.AwaitingPlanApproval : MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal(required ? 1 : 5, fixture.Provider.Requests.Count);
        Assert.Equal(required, current.Workflow!.PlanApproval is not null);
        Assert.False(current.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
        if (!required) return;
        Assert.False(current.HasWorkspace);
        Assert.False(current.IsTerminal);
        Assert.Null(current.CompletedAt);
        Assert.Equal(WorkflowStatus.Completed, current.Workflow.Steps.Single(step => step.Kind == "Plan").Status);
        Assert.All(current.Workflow.Steps.Where(step => step.Kind != "Plan"),
            step => Assert.Equal(WorkflowStatus.Pending, step.Status));
        var before = JsonSerializer.Serialize(current);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Store.Get(mission.Id)));
        Assert.Single(fixture.Provider.Requests);
        Assert.False(fixture.Store.Resume(mission.Id));
        Assert.False(fixture.Store.DecideApproval(mission.Id, true, current.Workflow.PlanApproval!.Fingerprint));
    }

    [Fact]
    public async Task ApprovedPlanSurvivesRestartAndContinuesWithoutCallingPlannerOrAuthorizingExecution()
    {
        using var fixture = Fixture();
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var waiting = fixture.Store.Get(mission.Id)!;
        var path = Path.Combine(fixture.Options.DataDirectory, "missions", mission.Id.ToString("N") + ".json");
        var bytes = File.ReadAllBytes(path);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(JsonSerializer.Serialize(waiting), JsonSerializer.Serialize(reopened.Get(mission.Id)));
        Assert.True(reopened.DecidePlanApproval(mission.Id, true, waiting.Workflow!.PlanApproval!.Fingerprint));
        var plans = new ExecutionPlanService(fixture.Workspaces, new() { Enabled = true }, fixture.Options,
            reopened, new WorkflowImageProcess());
        var processor = new CollaborativeProcessor(reopened,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
            fixture.Workspaces, plans, fixture.Sandbox, fixture.Options);
        await processor.ProcessAsync(mission.Id, default);
        var current = reopened.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.True(current.Workflow!.PlanApproval!.Approved);
        Assert.NotNull(current.Workflow.PlanApproval.DecidedAt);
        Assert.Equal(JsonSerializer.Serialize(waiting.Workflow.Plan), JsonSerializer.Serialize(current.Workflow.Plan));
        Assert.Equal(waiting.Events, current.Events.Take(waiting.Events.Count));
        Assert.Equal(waiting.CorrectionAttempts, current.CorrectionAttempts);
        Assert.Equal(5, fixture.Provider.Requests.Count);
        Assert.Single(fixture.Provider.Requests, request =>
            request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("decisions", out _));
        Assert.False(current.SandboxApproved);
        Assert.Null(current.ApprovalFingerprint);
        Assert.Empty(current.Verification);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task RefusalIsRecordedAndGenericResumeCannotBypassIt()
    {
        using var fixture = Fixture();
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var waiting = fixture.Store.Get(mission.Id)!;
        Assert.True(fixture.Store.DecidePlanApproval(mission.Id, false, waiting.Workflow!.PlanApproval!.Fingerprint));
        var refused = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.Cancelled, refused.Status);
        Assert.False(refused.Workflow!.PlanApproval!.Approved);
        Assert.Contains(refused.Events, item => item.Step == "PlanApproval" && item.Status == StepStatus.Cancelled);
        Assert.Equal(waiting.Events, refused.Events.Take(waiting.Events.Count));
        Assert.False(fixture.Store.Resume(mission.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Single(fixture.Provider.Requests);
        Assert.False(refused.HasWorkspace);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task ConcurrentDecisionsCommitOnlyOneApprovalAndOneQueueEntry()
    {
        using var fixture = Fixture();
        var mission = Submit(fixture);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var queue = fixture.Store.ReadQueue(timeout.Token).GetAsyncEnumerator();
        Assert.True(await queue.MoveNextAsync());
        Assert.Equal(mission.Id, queue.Current);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var fingerprint = fixture.Store.Get(mission.Id)!.Workflow!.PlanApproval!.Fingerprint;
        var decisions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            fixture.Store.DecidePlanApproval(mission.Id, true, fingerprint))));
        Assert.Single(decisions, accepted => accepted);
        Assert.True(await queue.MoveNextAsync());
        Assert.Equal(mission.Id, queue.Current);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(5, fixture.Provider.Requests.Count);
        Assert.Single(fixture.Store.Get(mission.Id)!.Events,
            item => item.Step == "PlanApproval" && item.Status == StepStatus.Succeeded);
        using var noExtra = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await using var emptyQueue = fixture.Store.ReadQueue(noExtra.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await emptyQueue.MoveNextAsync());
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("plan")]
    [InlineData("criteria")]
    [InlineData("version")]
    public async Task StaleOrModifiedPlansCannotBeApproved(string change)
    {
        using var fixture = Fixture();
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var fingerprint = fixture.Store.Get(mission.Id)!.Workflow!.PlanApproval!.Fingerprint;
        if (change != "fingerprint")
            fixture.Store.Update(mission.Id, current => current with
            {
                Workflow = change switch
                {
                    "plan" => current.Workflow! with { Plan = current.Workflow.Plan! with { Description = "Changed plan" } },
                    "criteria" => current.Workflow! with { AcceptanceCriteria = [new("C1", "Changed criterion")] },
                    _ => current.Workflow! with { Version = current.Workflow.Version + 1 }
                }
            });
        Assert.False(fixture.Store.DecidePlanApproval(mission.Id, true,
            change == "fingerprint" ? new string('0', 64) : fingerprint));
        Assert.Equal(MissionStatus.AwaitingPlanApproval, fixture.Store.Get(mission.Id)!.Status);
        Assert.Single(fixture.Provider.Requests);
        Assert.False(fixture.Store.Resume(mission.Id));
    }

    [Fact]
    public async Task PlanChangedAfterApprovalIsBlockedBeforeDeveloper()
    {
        using var fixture = Fixture();
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.True(fixture.Store.DecidePlanApproval(mission.Id, true,
            fixture.Store.Get(mission.Id)!.Workflow!.PlanApproval!.Fingerprint));
        fixture.Store.Update(mission.Id, current => current with
        {
            Workflow = current.Workflow! with { Plan = current.Workflow.Plan! with { Name = "Changed after approval" } }
        });
        await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Single(fixture.Provider.Requests);
        Assert.False(fixture.Store.Get(mission.Id)!.HasWorkspace);
    }

    [Fact]
    public async Task RejectedPlanKeepsDiagnosticsWithoutCreatingAnApproval()
    {
        using var fixture = new WorkflowFixture(corrections: 0, responseOverride: request =>
        {
            var response = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            response["criterionCoverage"]![0]!["paths"] = new JsonArray("src/App.tsx", "src/model.ts");
            return response.ToJsonString();
        });
        var mission = Submit(fixture);
        await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        var current = fixture.Store.Get(mission.Id)!;
        Assert.NotEqual(MissionStatus.AwaitingPlanApproval, current.Status);
        Assert.Null(current.Workflow!.PlanApproval);
        Assert.Null(current.Workflow.Plan);
        var diagnostic = Assert.Single(new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).PlannerAttempts(mission.Id));
        Assert.Contains(diagnostic.Errors, item => item.CriterionId == "C1" && item.Rule == "MissingTest");
        Assert.Single(fixture.Provider.Requests);
    }

    private static WorkflowFixture Fixture() => new(corrections: 0, responseOverride: MissionReactContractTests.Respond);
    private static MissionSnapshot Submit(WorkflowFixture fixture, bool required = true) =>
        fixture.Store.Submit(new("Display the requested information", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, RequirePlanApproval: required));
}
