using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class GenerationBudgetTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyChatRoutingAndAnswerAlsoShareTheMissionBudget(int limit)
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Explain the requested information", MaxTotalGenerations: limit));
        var sandbox = new FakeSandbox();
        var processor = fixture.Processor(new FakeProjectGenerator(), sandbox);
        if (limit == 1)
            await Assert.ThrowsAsync<GenerationBudgetExhaustedException>(() => processor.ProcessAsync(mission.Id, default));
        else await processor.ProcessAsync(mission.Id, default);
        var current = fixture.Missions.Get(mission.Id)!;
        Assert.Equal(limit, current.GenerationReservations!.Count);
        Assert.Equal("Routing", current.GenerationReservations[0].Role);
        Assert.Equal(limit == 1 ? MissionStatus.Failed : MissionStatus.Completed, current.Status);
        Assert.Empty(sandbox.Calls);
    }

    [Fact]
    public void InsufficientKnownMinimumIsReportedBeforeAnyGeneration()
    {
        using var fixture = new WorkflowFixture();
        var mission = Submit(fixture, 3);
        Assert.Contains("at least 4", mission.GenerationBudgetWarning);
        Assert.Empty(mission.GenerationReservations!);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Equal(MissionStatus.Queued, mission.Status);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task EveryDeveloperFileAndReviewShareOneHardLimit(int limit)
    {
        using var fixture = new WorkflowFixture(available: false, responseOverride: MissionReactContractTests.Respond);
        var mission = Submit(fixture, limit);
        if (limit == 4)
            await Assert.ThrowsAsync<GenerationBudgetExhaustedException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        else await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(limit, fixture.Provider.Requests.Count);
        Assert.Equal(limit, current.GenerationReservations!.Count);
        Assert.Equal(1, current.GenerationReservations.Count(item => item.Role == "Planner"));
        Assert.Equal(3, current.GenerationReservations.Count(item => item.Role == "Developer"));
        Assert.Equal(limit == 5 ? 1 : 0, current.GenerationReservations.Count(item => item.Role == "Reviewer"));
        Assert.NotNull(current.Workflow!.Plan);
        Assert.True(current.HasWorkspace);
        Assert.Contains(current.Workflow.Draft!, file => file.Path == "src/App.tsx");
        Assert.Equal(limit == 4 ? MissionStatus.Failed : MissionStatus.GeneratedUnverified, current.Status);
        if (limit == 4)
        {
            Assert.StartsWith("GenerationBudgetExhausted", current.Error);
            Assert.Null(current.Workflow.Review);
            Assert.False(fixture.Store.Resume(mission.Id));
            Assert.Equal("Blocked", new OllamaDiagnosticStore(fixture.Options, TimeProvider.System)
                .ReviewerAttempts(mission.Id).Last().Status);
        }
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task PlannerAndReviewerCorrectionsConsumeTheSameBudgetWithoutReplacingCorrectionLimits()
    {
        var plans = 0;
        var reviews = 0;
        using var fixture = new WorkflowFixture(available: false, corrections: 2, responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["decisions"] is not null && ++plans == 1)
                result["criterionCoverage"]![0]!["paths"] = new JsonArray("src/App.tsx");
            if (result["criterionReviews"] is not null && ++reviews == 1)
                result["criterionReviews"]![0]!["evidence"]![0]!["quote"] = "not source";
            return result.ToJsonString();
        });
        var mission = Submit(fixture, 7);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.GeneratedUnverified, current.Status);
        Assert.Equal(2, current.CorrectionAttempts);
        Assert.Equal(7, current.GenerationReservations!.Count);
        Assert.Equal(2, current.GenerationReservations.Count(item => item.Role == "Planner"));
        Assert.Equal(3, current.GenerationReservations.Count(item => item.Role == "Developer"));
        Assert.Equal(2, current.GenerationReservations.Count(item => item.Role == "Reviewer"));
        Assert.Equal(7, fixture.Provider.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReservationsCannotOvercommitEvenAcrossStoreInstances(bool multipleStores)
    {
        using var fixture = new WorkflowFixture();
        var mission = Submit(fixture, 3);
        fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Cancelled });
        using var other = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        var provider = new FakeProvider((_, _) => Task.FromResult("response"));
        var resolver = new LlmProviderResolver([provider]);
        var attempts = Enumerable.Range(0, 20).Select(index => Task.Run(async () =>
        {
            var store = multipleStores && index % 2 == 0 ? other : fixture.Store;
            using var scope = store.EnterGenerationScope(mission.Id, "Developer");
            return await Record.ExceptionAsync(() => resolver.Resolve("Ollama").CompleteAsync(TestSupport.Request(), default));
        })).ToArray();
        var results = await Task.WhenAll(attempts);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(3, results.Count(item => item is null));
        Assert.All(results.Where(item => item is not null), item => Assert.IsType<GenerationBudgetExhaustedException>(item));
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        var current = reopened.Get(mission.Id)!;
        Assert.Equal(3, current.GenerationReservations!.Count);
        Assert.Equal(3, current.GenerationReservations.Select(item => item.Id).Distinct().Count());
        Assert.Equal(MissionStatus.Failed, current.Status);
        // A stale successful writer cannot erase reservations or the exhausted state.
        fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Completed, Error = null });
        Assert.Equal(MissionStatus.Failed, fixture.Store.Get(mission.Id)!.Status);
        Assert.Equal(3, fixture.Store.Get(mission.Id)!.GenerationReservations!.Count);
    }

    [Fact]
    public async Task CancelledInFlightCallsRemainReservedButPrecancelledCallsNeverReserve()
    {
        using var fixture = new WorkflowFixture();
        var mission = Submit(fixture, 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = BudgetedLlmProvider.Wrap(new FakeProvider(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }));
        using var scope = fixture.Store.EnterGenerationScope(mission.Id, "Reviewer");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(TestSupport.Request(), cancelled.Token));
        Assert.Empty(fixture.Store.Get(mission.Id)!.GenerationReservations!);
        using var inFlight = new CancellationTokenSource();
        var running = provider.CompleteAsync(TestSupport.Request(), inFlight.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(fixture.Store.Get(mission.Id)!.GenerationReservations!);
        inFlight.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Single(fixture.Store.Get(mission.Id)!.GenerationReservations!);
    }

    [Fact]
    public async Task NativeOllamaAndResolverReserveExactlyOnceAndKeepFailuresConservatively()
    {
        using var fixture = new WorkflowFixture();
        var mission = Submit(fixture, 1);
        var sends = 0;
        using var handler = new StubHttpHandler((_, _) =>
        {
            sends++;
            throw new HttpRequestException("Simulated uncertain send");
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        using var scope = fixture.Store.EnterGenerationScope(mission.Id, "Planner");
        await Assert.ThrowsAsync<LlmProviderException>(() =>
            new LlmProviderResolver([provider]).Resolve("Ollama").CompleteAsync(TestSupport.Request(), default));
        Assert.Single(fixture.Store.Get(mission.Id)!.GenerationReservations!);
        await Assert.ThrowsAsync<GenerationBudgetExhaustedException>(() => provider.CompleteAsync(TestSupport.Request(), default));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task TimeoutDoesNotRefundAReservedUnit()
    {
        using var fixture = new WorkflowFixture();
        var mission = Submit(fixture, 1);
        var provider = BudgetedLlmProvider.Wrap(new FakeProvider((_, _) =>
            throw new LlmTimeoutException("Simulated provider timeout")));
        using var scope = fixture.Store.EnterGenerationScope(mission.Id, "Developer");
        await Assert.ThrowsAsync<LlmTimeoutException>(() => provider.CompleteAsync(TestSupport.Request(), default));
        Assert.Single(fixture.Store.Get(mission.Id)!.GenerationReservations!);
        await Assert.ThrowsAsync<GenerationBudgetExhaustedException>(() => provider.CompleteAsync(TestSupport.Request(), default));
    }

    [Fact]
    public async Task KnownLocalValidationFailureBeforeTheBoundaryConsumesNothing()
    {
        using var fixture = new WorkflowFixture();
        var mission = Submit(fixture, 1);
        using var client = new HttpClient(new StubHttpHandler((_, _) => throw new InvalidOperationException("No HTTP expected")));
        using var provider = new OllamaProvider(client, new());
        using var scope = fixture.Store.EnterGenerationScope(mission.Id, "Planner");
        var request = TestSupport.Request();
        await Assert.ThrowsAsync<ArgumentException>(() => provider.CompleteAsync(request with
        { Model = request.Model with { Provider = "wrong" } }, default));
        Assert.Empty(fixture.Store.Get(mission.Id)!.GenerationReservations!);
    }

    [Fact]
    public async Task CrashRecoveryRetainsUncertainReservationsAndResumeDoesNotResetTheFrozenLimit()
    {
        using var fixture = new WorkflowFixture(responseOverride: MissionReactContractTests.Respond);
        var mission = Submit(fixture, 2);
        using (fixture.Store.EnterGenerationScope(mission.Id, "Uncertain"))
            LlmGenerationScope.Reserve(TestSupport.Request(), default);
        using var reopened = new MissionStore(fixture.Options with { DefaultMaxTotalGenerations = 99 },
            fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(2, reopened.Get(mission.Id)!.MaxTotalGenerations);
        Assert.Single(reopened.Get(mission.Id)!.GenerationReservations!);
        Assert.True(reopened.Resume(mission.Id));
        var processor = new CollaborativeProcessor(reopened,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options);
        await Assert.ThrowsAsync<GenerationBudgetExhaustedException>(() => processor.ProcessAsync(mission.Id, default));
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(new[] { "Uncertain", "Planner" },
            reopened.Get(mission.Id)!.GenerationReservations!.Select(item => item.Role));
        Assert.False(reopened.Resume(mission.Id));
        Assert.NotNull(reopened.Get(mission.Id)!.Workflow!.Plan);
    }

    [Fact]
    public async Task LegacySnapshotsWithoutThePolicyAreNotRecountedOrRewrittenOnLoad()
    {
        using var fixture = new WorkflowFixture();
        var mission = Submit(fixture, 1);
        fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Failed });
        var path = Path.Combine(fixture.Options.DataDirectory, "missions", mission.Id.ToString("N") + ".json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json.AsObject().Remove("maxTotalGenerations");
        json.AsObject().Remove("generationReservations");
        json.AsObject().Remove("generationBudgetExhausted");
        File.WriteAllText(path, json.ToJsonString());
        var bytes = File.ReadAllBytes(path);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Null(reopened.Get(mission.Id)!.MaxTotalGenerations);
        var provider = BudgetedLlmProvider.Wrap(new FakeProvider((_, _) => Task.FromResult("legacy")));
        using (reopened.EnterGenerationScope(mission.Id, "Developer"))
            for (var index = 0; index < 3; index++) await provider.CompleteAsync(TestSupport.Request(), default);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Null(reopened.Get(mission.Id)!.GenerationReservations);
    }

    [Fact]
    public async Task PersistenceFailurePreventsAnyProviderCall()
    {
        using var fixture = new WorkflowFixture();
        var mission = Submit(fixture, 1);
        var temporary = Path.Combine(fixture.Options.DataDirectory, "missions", mission.Id.ToString("N") + ".json.tmp");
        Directory.CreateDirectory(temporary);
        var provider = new FakeProvider((_, _) => Task.FromResult("not allowed"));
        using var scope = fixture.Store.EnterGenerationScope(mission.Id, "Developer");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            BudgetedLlmProvider.Wrap(provider).CompleteAsync(TestSupport.Request(), default));
        Assert.Empty(provider.Requests);
        Assert.Empty(fixture.Store.Get(mission.Id)!.GenerationReservations!);
    }

    private static MissionSnapshot Submit(WorkflowFixture fixture, int limit) =>
        fixture.Store.Submit(new("Display requested information", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, MaxTotalGenerations: limit));
}
