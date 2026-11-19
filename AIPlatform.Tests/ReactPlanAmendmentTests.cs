using System.Text.Json;
using AIPlatform.API;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class ReactPlanAmendmentTests
{
    [Fact]
    public async Task PreparationArchivesPriorPlanAndDraftWithoutGenerationAndRequiresFreshBoundApproval()
    {
        using var fixture = Fixture();
        var initial = await FailedAsync(fixture);
        var calls = fixture.Provider.Requests.Count;
        using var gate = new MissionExecutionGate();
        var library = new Library();
        var service = new ReactPlanAmendment(fixture.Store, library, new SourceCheck(), gate, fixture.Options);
        var request = Request(initial);
        var pending = await service.PrepareAsync(initial.Id, request, default);
        Assert.Equal(MissionStatus.AwaitingPlanApproval, pending.Status);
        Assert.Null(pending.Workflow!.PlanApproval!.Approved);
        Assert.Equal(2, pending.Workflow.PlanApproval.Version);
        Assert.NotEqual(initial.Workflow!.PlanApproval!.Fingerprint, pending.Workflow.PlanApproval.Fingerprint);
        Assert.Equal(JsonSerializer.Serialize(initial.Workflow.Draft), JsonSerializer.Serialize(pending.Workflow.Draft));
        Assert.Equal(JsonSerializer.Serialize(initial.Workflow.Models), JsonSerializer.Serialize(pending.Workflow.Models));
        Assert.Equal(JsonSerializer.Serialize(initial.GenerationReservations), JsonSerializer.Serialize(pending.GenerationReservations));
        Assert.Equal(initial.CorrectionAttempts, pending.CorrectionAttempts);
        Assert.Equal(15, pending.MaxTotalGenerations);
        Assert.Equal(4, pending.GenerationReservations!.Count);
        var history = Assert.Single(pending.Workflow.PlanAmendments);
        Assert.Equal(JsonSerializer.Serialize(initial.Workflow.Plan), JsonSerializer.Serialize(history.PreviousPlan));
        Assert.Equal(initial.Workflow.PlanApproval, history.PreviousApproval);
        Assert.Equal(JsonSerializer.Serialize(initial.Workflow.ModelSelections), JsonSerializer.Serialize(history.PreviousSelections));
        Assert.Equal(JsonSerializer.Serialize(initial.Workflow.Draft), JsonSerializer.Serialize(history.PreviousDraft));
        Assert.Equal("TS1005", Assert.Single(history.RetainedSourceDiagnostics).Code);
        Assert.All(pending.Workflow.ModelSelections.Where(item => item.AgentId == "DeveloperAgent"), item =>
            Assert.Equal("coder:7b", Assert.Single(item.Decision.Alternatives).Model));
        Assert.Contains(library.Contexts, context => context == initial.Workflow.Models.Single(model => model.Id ==
            initial.Workflow.Agents.Single(agent => agent.Role == "Developer").ModelId).ContextSize);
        Assert.Equal(calls, fixture.Provider.Requests.Count);
        await fixture.Processor.ProcessAsync(initial.Id, default);
        Assert.Equal(calls, fixture.Provider.Requests.Count);
        Assert.False(fixture.Store.Resume(initial.Id));
        Assert.False(fixture.Store.DecidePlanApproval(initial.Id, true, initial.Workflow.PlanApproval.Fingerprint));
        Assert.False(fixture.Store.DecideApproval(initial.Id, true, pending.Workflow.PlanApproval.Fingerprint));
        Assert.False(pending.SandboxApproved);
        Assert.False(pending.HasWorkspace);
        Assert.Empty(fixture.Sandbox.Calls);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        var restored = reopened.Get(initial.Id)!;
        Assert.Null(restored.Workflow!.PlanApproval!.Approved);
        Assert.Single(restored.Workflow.PlanAmendments);
        var response = WorkflowResponse.From(restored.Workflow);
        Assert.Equal(new[] { "src/model.ts", "src/App.tsx" }, Assert.Single(response.PlanAmendments!).RetainedPaths);
        Assert.Equal(new[] { "src/App.test.tsx" }, response.PlanAmendments![0].RemainingPaths);
        Assert.DoesNotContain("PreviousDraft", JsonSerializer.Serialize(response));
        var approval = restored.Workflow.PlanApproval.Fingerprint;
        reopened.Update(initial.Id, mission => mission with { Workflow = mission.Workflow! with
        { ModelSelections = mission.Workflow.ModelSelections.Where(item => item.AgentId != "DeveloperAgent").ToArray() } });
        Assert.False(reopened.DecidePlanApproval(initial.Id, true, approval));
    }

    [Theory]
    [InlineData("resources")]
    [InlineData("stale")]
    [InlineData("path")]
    [InlineData("version")]
    [InlineData("patch-version")]
    [InlineData("metadata")]
    [InlineData("budget")]
    public async Task RefusalLeavesSnapshotAndBudgetUntouched(string failure)
    {
        using var fixture = Fixture();
        var initial = await FailedAsync(fixture, failure == "budget" ? 5 : 15);
        var request = Request(initial);
        if (failure == "stale") request = request with { ExpectedUpdatedAt = initial.UpdatedAt.AddSeconds(-1) };
        if (failure == "path") request = request with { Plan = request.Plan with { Files = request.Plan.Files.Skip(1).ToArray() } };
        if (failure == "version") request = request with { Plan = request.Plan with { Decisions = ["Use React 18"] } };
        if (failure == "patch-version") request = request with { Plan = request.Plan with
        { Decisions = ["Use React " + MissionReactTemplate.ReactVersion + "0"] } };
        var path = Path.Combine(fixture.Options.DataDirectory, "missions", initial.Id.ToString("N") + ".json");
        var before = File.ReadAllBytes(path);
        using var gate = new MissionExecutionGate();
        var service = new ReactPlanAmendment(fixture.Store, new Library(failure == "resources", failure == "metadata"),
            new SourceCheck(), gate, fixture.Options);
        var error = await Record.ExceptionAsync(() => service.PrepareAsync(initial.Id, request, default));
        Assert.True(error is StudioValidationException or ModelOperationConflictException);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(4, fixture.Provider.Requests.Count);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task ExplicitNewApprovalResumesAtMissingFileUsingPreparedCoderAndNoPlanner()
    {
        using var fixture = Fixture();
        var initial = await FailedAsync(fixture);
        using var gate = new MissionExecutionGate();
        var library = new Library();
        var pending = await new ReactPlanAmendment(fixture.Store, library, new SourceCheck(), gate, fixture.Options)
            .PrepareAsync(initial.Id, Request(initial), default);
        Assert.True(fixture.Store.DecidePlanApproval(initial.Id, true, pending.Workflow!.PlanApproval!.Fingerprint));
        var provider = new FakeProvider((request, _) => Task.FromResult(MissionReactContractTests.Respond(request)));
        var router = new ReviewOnlyRouter();
        library.AllowExecution = true;
        var routing = new WorkflowModelRouting(fixture.Store, router, library, fixture.Options);
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([provider]), fixture.Options, routing),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options,
            routing);
        await processor.ProcessAsync(initial.Id, default);
        var completed = fixture.Store.Get(initial.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, completed.Status);
        Assert.All(initial.Workflow!.Draft!, file => Assert.Contains(completed.Workflow!.Revisions![0].Files, retained => retained == file));
        Assert.Equal(2, provider.Requests.Count);
        var first = provider.Requests.First();
        using var input = JsonDocument.Parse(first.Messages.Last().Content);
        Assert.Equal("src/App.test.tsx", input.RootElement.GetProperty("file").GetProperty("path").GetString());
        Assert.Equal("coder:7b", first.Model.Name);
        Assert.Single(completed.Workflow!.PlanAmendments);
        Assert.Single(completed.Workflow.Revisions!);
        Assert.Equal(6, completed.GenerationReservations!.Count);
        Assert.Empty(fixture.Sandbox.Calls);
        fixture.Store.RequireApprovedPlan(initial.Id);
    }

    private static WorkflowFixture Fixture() => new(responseOverride: request =>
    {
        using var json = JsonDocument.Parse(request.Messages.Last().Content);
        if (json.RootElement.TryGetProperty("file", out var file) && file.GetProperty("path").GetString() == "src/App.test.tsx")
            throw new LlmOutputLimitException(request.Model.Name, request.Model.MaxOutputTokens, null, 300, 0);
        return MissionReactContractTests.Respond(request);
    });

    private static async Task<MissionSnapshot> FailedAsync(WorkflowFixture fixture, int maximum = 15)
    {
        var mission = fixture.Store.Submit(new("Modifier et supprimer des rapports.", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, RequirePlanApproval: true, MaxTotalGenerations: maximum));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.True(fixture.Store.DecidePlanApproval(mission.Id, true,
            fixture.Store.Get(mission.Id)!.Workflow!.PlanApproval!.Fingerprint));
        await Assert.ThrowsAsync<LlmOutputLimitException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        return fixture.Store.Update(mission.Id, current => current with
        {
            Status = MissionStatus.Failed,
            Workflow = current.Workflow! with
            {
                AutomaticModelSelection = true,
                ModelSelections = [new("DeveloperAgent:src/App.test.tsx", "DeveloperAgent",
                    new("planner:4b", "ReactTypeScript", "Historical selection", "Low",
                        [new("planner:4b", 15, "Previously loaded", new("Compatible", "Historical", 8192))]))]
            }
        });
    }

    private static PrepareReactPlanAmendmentRequest Request(MissionSnapshot mission) => new(mission.UpdatedAt,
        mission.Workflow!.PlanApproval!.Fingerprint,
        mission.Workflow.Plan! with { Decisions = ["Use React " + MissionReactTemplate.ReactVersion] }, "coder:7b");

    private sealed class SourceCheck : IReactSourceCheck
    {
        public Task<IReadOnlyList<ReactSourceIssue>> CheckAsync(IReadOnlyList<GeneratedFile> files, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ReactSourceIssue>>([new("src/App.tsx", 22, 21, "TS1005", "Expected comma.")]);
    }

    private sealed class Library(bool insufficient = false, bool unknownContext = false) : IModelLibraryService
    {
        public List<int> Contexts { get; } = [];
        public bool AllowExecution { get; set; }
        public Task<ModelLibrarySnapshot> SnapshotAsync(int context, CancellationToken token)
        {
            Contexts.Add(context);
            return Task.FromResult(new ModelLibrarySnapshot(new(null, null, [], null, []),
                [new(new("coder:7b", "Code", null, ["Code"], ""), true,
                    new("coder:7b", null, null, null, 1, null, "qwen2", unknownContext ? null : 32768, null, null, null, null, true, null),
                    null, new(insufficient ? "Insufficient" : "Compatible", "Simulated resources", context), [])], []));
        }
        public ModelRoutingProfile SaveSpecializations(string name, string[]? specializations) => throw new NotSupportedException();
        public ModelRoutingProfile ClearQualification(string name) => throw new NotSupportedException();
        public Task<ModelRoutingProfile> QualifyAsync(string name, int context, bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task GuardExecutionAsync(string name, int context, CancellationToken token, bool automatic = false) =>
            AllowExecution ? Task.CompletedTask : throw new InvalidOperationException("No execution permitted.");
        public Task<long> VerifyAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public IReadOnlyList<ModelDownload> Downloads() => throw new NotSupportedException();
        public ModelDownload StartDownload(string name, bool confirmed) => throw new NotSupportedException();
        public bool CancelDownload(Guid id) => throw new NotSupportedException();
        public Task DeleteAsync(string name, bool confirmed, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class ReviewOnlyRouter : IModelRouter
    {
        public Task<ModelRoutingDecision> SelectAsync(string task, string role, int contextTokens, CancellationToken token)
        {
            Assert.Equal("Reviewer", role);
            return Task.FromResult(new ModelRoutingDecision("coder:7b", "Review", "Simulated review", "Low",
                [new("coder:7b", 0, "Test", new("Compatible", "Simulated", contextTokens))]));
        }
        public void ResetStatistics() => throw new NotSupportedException();
    }
}
