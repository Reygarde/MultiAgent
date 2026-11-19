using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlatform.API;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class SystemModelRoutingTests
{
    [Fact]
    public async Task GlobalGenerationBudgetBlocksEscalationWithoutCallingTheReserveModel()
    {
        using var fixture = new WorkflowFixture(corrections: 3, responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["criterionReviews"] is not null)
                result["criterionReviews"]![0]!["evidence"]![0]!["quote"] = "not source";
            return result.ToJsonString();
        });
        var mission = fixture.Store.Submit(new("Display requested information", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, AutomaticModelSelection: true, MaxTotalGenerations: 6));
        await Assert.ThrowsAsync<GenerationBudgetExhaustedException>(() =>
            Processor(fixture, new Router(), new Library()).ProcessAsync(mission.Id, default));
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(6, fixture.Provider.Requests.Count);
        Assert.All(fixture.Provider.Requests, request => Assert.True(request.RequireAutomaticAdmissibility));
        Assert.Equal(6, current.GenerationReservations!.Count);
        Assert.Equal(2, current.GenerationReservations.Count(item => item.Role == "Reviewer"));
        Assert.DoesNotContain(fixture.Provider.Requests, request => request.Model.Name == "reserve:7b");
        Assert.Contains(current.Workflow!.ModelInvocations, item =>
            item.Attempt.ModelName == "reserve:7b" && item.Attempt.Outcome == "Blocked" &&
            item.Attempt.FailureKind == "GenerationBudgetExhausted");
        Assert.Equal(MissionStatus.Failed, current.Status);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReviewerCorrectionReusesAutomaticRoutingEscalationAndCounters(int rejected)
    {
        var reviews = 0;
        using var fixture = new WorkflowFixture(corrections: 2, responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["criterionReviews"] is null) return result.ToJsonString();
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (++reviews > 1)
            {
                Assert.Equal("QuoteMismatch", input.RootElement.GetProperty("validationErrors")[0].GetProperty("rule").GetString());
                Assert.Equal("not source", input.RootElement.GetProperty("rejectedReport")
                    .GetProperty("criterionReviews")[0].GetProperty("evidence")[0].GetProperty("quote").GetString());
            }
            if (reviews <= rejected) result["criterionReviews"]![0]!["evidence"]![0]!["quote"] = "not source";
            return result.ToJsonString();
        });
        var mission = fixture.Store.Submit(new("Initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, AutomaticModelSelection: true));
        var processor = Processor(fixture, new Router(), new Library());
        if (rejected == 3)
            await Assert.ThrowsAsync<StudioValidationException>(() => processor.ProcessAsync(mission.Id, default));
        else await processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        var invocations = current.Workflow!.ModelInvocations.Where(item => item.Attempt.Role == "Reviewer").ToArray();
        Assert.Equal(Math.Min(3, rejected + 1), reviews);
        Assert.Equal(reviews, invocations.Length);
        Assert.Equal(reviews - 1, current.CorrectionAttempts);
        Assert.Equal(new[] { "review:7b", "review:7b", "reserve:7b" }.Take(reviews),
            invocations.Select(item => item.Attempt.ModelName));
        var diagnostics = new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).ReviewerAttempts(mission.Id);
        Assert.Equal(invocations.Select(item => item.Attempt.Id), diagnostics.Select(item => item.AttemptId));
        Assert.Equal(invocations.Select(item => item.Attempt.ModelName), diagnostics.Select(item => item.Model));
        Assert.DoesNotContain(current.Workflow.Steps, step => step.Kind == "Correct");
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericReactContractKeepsExistingAutomaticRoutingAndApproval(bool requirePlanApproval)
    {
        using var fixture = new WorkflowFixture(corrections: 0, responseOverride: MissionReactContractTests.Respond);
        var router = new Router();
        var mission = fixture.Store.Submit(new("Display initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, AutomaticModelSelection: true, RequirePlanApproval: requirePlanApproval));
        await Processor(fixture, router, new Library()).ProcessAsync(mission.Id, default);
        if (requirePlanApproval)
        {
            var waiting = fixture.Store.Get(mission.Id)!;
            Assert.Equal(MissionStatus.AwaitingPlanApproval, waiting.Status);
            Assert.Single(fixture.Provider.Requests);
            Assert.Single(waiting.Workflow!.ModelInvocations);
            Assert.True(fixture.Store.DecidePlanApproval(mission.Id, true, waiting.Workflow.PlanApproval!.Fingerprint));
            await Processor(fixture, router, new Library()).ProcessAsync(mission.Id, default);
        }
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal(new[] { "plan:7b", "react:7b", "review:7b" },
            fixture.Provider.Requests.Select(request => request.Model.Name).Distinct());
        Assert.False(current.SandboxApproved);
        Assert.Empty(current.Workflow!.CriterionExecutions);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(0, current.CorrectionAttempts);
        var diagnostic = Assert.Single(new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).PlannerAttempts(mission.Id));
        var invocation = current.Workflow.ModelInvocations.Single(item => item.Attempt.Role == "Planner").Attempt;
        Assert.Equal(invocation.Id, diagnostic.AttemptId);
        Assert.Equal(invocation.ModelName, diagnostic.Model);
        Assert.Equal(current.Workflow.WorkflowId, diagnostic.RunId);
    }

    [Theory]
    [InlineData("Backend", "fullstack-react-dotnet frontend React, C# sources, xunit", "DotNet")]
    [InlineData("Frontend", "React TypeScript with Vitest assertion sources", "ReactTypeScript")]
    [InlineData("Frontend", "src/App.tsx TS18004 error", "Debugging")]
    [InlineData("Reviewer", "Review CS1002 compiler diagnostics", "Review")]
    [InlineData("Planner", "Plan React + .NET with diagnostics", "Planning")]
    public void ClassifierHonorsSystemRolesAndRealDiagnostics(string role, string context, string category) =>
        Assert.Equal(category, LocalModelRouter.Classify(context, role));

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public async Task InvalidOutputStopsAtTheFrozenPlannerAndSharedCorrectionLimitsWithoutCycling(bool historical, int calls)
    {
        using var fixture = new WorkflowFixture(responseOverride: _ => "invalid JSON");
        var processor = Processor(fixture, new Router(), new Library());
        var mission = fixture.Store.Submit(new("Create app", MissionKind.Project, AutomaticModelSelection: true));
        if (historical)
            fixture.Store.Update(mission.Id, item => item with { Workflow = item.Workflow! with { PlannerGenerationLimit = 0 } });
        await Assert.ThrowsAsync<LlmProviderException>(() => processor.ProcessAsync(mission.Id, default));
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(calls - 1, current.CorrectionAttempts);
        Assert.Equal(new[] { "plan:7b", "plan:7b", "reserve:7b", "reserve:7b" }.Take(calls),
            current.Workflow!.ModelInvocations.Select(item => item.Attempt.ModelName));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task RejectedPlannerUsesExactFeedbackAndExistingEscalationBeforeApproval(int rejected, bool approval)
    {
        var calls = 0;
        using var fixture = new WorkflowFixture(responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (!request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("decisions", out _))
            {
                Assert.True(calls > rejected);
                return result.ToJsonString();
            }
            if (++calls > 1)
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                Assert.Equal("MissingTest", input.RootElement.GetProperty("validationErrors")[0].GetProperty("rule").GetString());
                Assert.True(input.RootElement.TryGetProperty("rejectedPlan", out _));
            }
            if (calls <= rejected) result["criterionCoverage"]![0]!["paths"] = new JsonArray("src/App.tsx");
            return result.ToJsonString();
        });
        var router = new Router();
        var processor = Processor(fixture, router, new Library());
        var mission = fixture.Store.Submit(new("Initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, AutomaticModelSelection: true, RequirePlanApproval: approval));
        if (rejected == 3)
            await Assert.ThrowsAsync<StudioValidationException>(() => processor.ProcessAsync(mission.Id, default));
        else await processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        var invocations = current.Workflow!.ModelInvocations.Where(item => item.Attempt.Role == "Planner").ToArray();
        Assert.Equal(Math.Min(3, rejected + 1), calls);
        Assert.Equal(calls, invocations.Length);
        Assert.Equal(calls - 1, current.CorrectionAttempts);
        Assert.Equal(new[] { "plan:7b", "plan:7b", "reserve:7b" }.Take(calls),
            invocations.Select(item => item.Attempt.ModelName));
        Assert.All(invocations.Take(rejected), item => Assert.Equal("Failed", item.Attempt.Outcome));
        Assert.Single(current.Workflow.ModelSelections, item => item.Key.EndsWith(":Plan", StringComparison.Ordinal));
        Assert.Empty(fixture.Sandbox.Calls);
        if (rejected == 3)
        {
            Assert.Null(current.Workflow.Plan);
            Assert.Null(current.Workflow.PlanApproval);
            Assert.False(current.HasWorkspace);
            return;
        }
        Assert.Equal(approval ? MissionStatus.AwaitingPlanApproval : MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal("Succeeded", invocations[^1].Attempt.Outcome);
        if (!approval) return;
        Assert.Equal(calls, fixture.Provider.Requests.Count);
        Assert.True(fixture.Store.DecidePlanApproval(mission.Id, true, current.Workflow.PlanApproval!.Fingerprint));
        await processor.ProcessAsync(mission.Id, default);
        Assert.Equal(calls, fixture.Store.Get(mission.Id)!.Workflow!.ModelInvocations.Count(item => item.Attempt.Role == "Planner"));
        Assert.False(fixture.Store.Get(mission.Id)!.SandboxApproved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PlannerRoutingAlsoRespectsASmallerSharedCorrectionBudget(int budget)
    {
        using var fixture = new WorkflowFixture(corrections: budget, responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            result["criterionCoverage"]![0]!["paths"] = new JsonArray("src/App.tsx");
            return result.ToJsonString();
        });
        var mission = fixture.Store.Submit(new("Initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, AutomaticModelSelection: true));
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            Processor(fixture, new Router(), new Library()).ProcessAsync(mission.Id, default));
        Assert.Equal(budget + 1, fixture.Provider.Requests.Count);
        Assert.Equal(budget, fixture.Store.Get(mission.Id)!.CorrectionAttempts);
        Assert.Null(fixture.Store.Get(mission.Id)!.Workflow!.Plan);
    }

    [Fact]
    public async Task ProviderResourceFailuresCannotReopenTheThreeCallCeilingOnExplicitResume()
    {
        using var fixture = new WorkflowFixture(responseOverride: _ =>
            throw new LlmMemoryException("Simulated provider allocation failure."));
        var mission = fixture.Store.Submit(new("Initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, AutomaticModelSelection: true));
        var processor = Processor(fixture, new Router(), new Library());
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Assert.ThrowsAsync<LlmMemoryException>(() => processor.ProcessAsync(mission.Id, default));
            Assert.Equal(attempt, fixture.Provider.Requests.Count);
            Assert.Equal(attempt, fixture.Store.Get(mission.Id)!.Workflow!.CountPlannerAttempts());
            Assert.Equal(0, fixture.Store.Get(mission.Id)!.CorrectionAttempts);
            fixture.Store.Update(mission.Id, current => current with { Status = MissionStatus.Failed });
            Assert.Equal(attempt < 3, fixture.Store.Resume(mission.Id));
        }
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.False(reopened.Resume(mission.Id));
        Assert.Equal(3, fixture.Provider.Requests.Count);
    }

    [Fact]
    public async Task RoutesPlannerBackendFrontendAndReviewerWithActualBoundedContextAndHistory()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var router = new Router();
        var library = new Library();
        var processor = Processor(fixture, router, library);
        var mission = fixture.Store.Submit(new("Create a full-stack task application", MissionKind.Project,
            TemplateId: FullStackTemplate.Id, AutomaticModelSelection: true));

        await processor.ProcessAsync(mission.Id, default);

        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal(new[] { "dotnet:7b", "plan:7b", "react:7b", "review:7b" },
            fixture.Provider.Requests.Select(request => request.Model.Name).Distinct().Order());
        Assert.All(fixture.Provider.Requests, request => Assert.True(request.ExclusiveModel));
        Assert.Contains(router.Inputs, input => input.Contains("file: backend/Api/TaskService.cs") &&
            input.Contains("Actual bounded context:") && input.Contains("\"sources\""));
        Assert.Contains(router.Inputs, input => input.Contains("file: frontend/src/App.tsx"));
        Assert.Equal(fixture.Provider.Requests.Count, current.Workflow!.ModelInvocations.Count);
        var response = WorkflowResponse.From(current.Workflow);
        Assert.Equal("dotnet:7b", response.Steps.Single(step => step.Kind == "BackendGenerate").ModelName);
        Assert.All(response.Steps.Where(step => step.ModelInvocations?.Count > 0),
            step => Assert.Equal("Automatic", step.SelectionMode));
        Assert.DoesNotContain("Actual bounded context", JsonSerializer.Serialize(response));
        Assert.False(current.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.All(current.Workflow.ModelInvocations, item => Assert.NotNull(item.Attempt.CompletedAt));
        Assert.All(current.Workflow.ModelInvocations.Where(item => item.Attempt.Role == "Developer"),
            item => Assert.Equal("Unavailable", item.Attempt.TestsStatus));
        new WorkflowModelRouting(fixture.Store, router, library, fixture.Options).RecordVerification(mission.Id,
            new("BackendBuild", "Failed", 1, "Compiler error", Diagnostics:
                new("BackendBuild", "dotnet build", 1,
                    [new("Compiler", null, "backend/Api/TaskService.cs", 1, 1, "CS1002", "Expected semicolon")], "")));
        var validated = fixture.Store.Get(mission.Id)!.Workflow!;
        Assert.All(validated.ModelInvocations.Where(item => item.Attempt.Role == "Developer" &&
            item.Sequence.Contains(":frontend/")), item => Assert.Null(item.Attempt.FailureKind));
        Assert.Equal("Generation", validated.ModelInvocations.Single(item => item.Attempt.Role == "Developer" &&
            item.Sequence.EndsWith(":backend/Api/TaskService.cs")).Attempt.FailureKind);

        var observations = new LocalModelRouter(library, new CustomAgentStore(fixture.Options), fixture.Options, fixture.Store);
        Assert.Equal(current.Workflow.ModelInvocations.Count, observations.Observations().Count);
        var saved = JsonSerializer.Serialize(fixture.Store.Get(mission.Id));
        observations.ResetStatistics();
        Assert.Empty(observations.Observations());
        Assert.Equal(saved, JsonSerializer.Serialize(fixture.Store.Get(mission.Id)));
    }

    [Fact]
    public async Task ExistingManualMissionsKeepFrozenModelsAndNeverCallTheRouterOrMemoryPreflight()
    {
        using var fixture = new WorkflowFixture();
        var router = new Router();
        var library = new Library { BlockMemory = true };
        var mission = fixture.Store.Submit(new("Create a .NET application", MissionKind.Project));
        await Processor(fixture, router, library).ProcessAsync(mission.Id, default);
        var flow = fixture.Store.Get(mission.Id)!.Workflow!;
        Assert.False(flow.AutomaticModelSelection);
        Assert.Empty(router.Inputs);
        Assert.Empty(flow.ModelInvocations);
        Assert.All(fixture.Provider.Requests, request =>
            Assert.Contains(flow.Models, model => model == request.Model));
        Assert.All(WorkflowResponse.From(flow).Steps, step => Assert.Equal("Manual", step.SelectionMode));
        Assert.All(fixture.Provider.Requests, request => Assert.False(request.ExclusiveModel));
    }

    [Fact]
    public async Task TwoQualityFailuresUseExistingEscalationWithoutRerunningCompletedSteps()
    {
        var failures = 0;
        using var fixture = new WorkflowFixture(fullStack: true, responseOverride: request =>
        {
            if (Path(request) == "backend/Api/TaskService.cs" && ++failures <= 2) return "not JSON";
            return Response(request);
        });
        var router = new Router();
        var processor = Processor(fixture, router, new Library());
        var mission = fixture.Store.Submit(new("Full-stack app", MissionKind.Project,
            TemplateId: FullStackTemplate.Id, AutomaticModelSelection: true));
        await processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        var attempts = current.Workflow!.ModelInvocations.Where(item =>
            item.Sequence.EndsWith(":backend/Api/TaskService.cs") && item.Attempt.Role == "Developer").ToArray();
        Assert.Equal(new[] { "dotnet:7b", "dotnet:7b", "reserve:7b" }, attempts.Select(item => item.Attempt.ModelName));
        Assert.Equal(2, current.CorrectionAttempts);
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.Single(router.Inputs, input => input.Contains("operation: BackendGenerate; file: backend/Api/TaskService.cs;"));
        Assert.Single(current.Workflow.ModelInvocations, item => item.Attempt.Role == "Planner");
        Assert.Contains("Two consecutive", attempts.Last().Attempt.SelectionReason);
        Assert.False(current.SandboxApproved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryBlockDoesNotConsumeGenerationOrCorrectionAttemptsAndSurvivesRestart(bool providerRace)
    {
        using var fixture = new WorkflowFixture(fullStack: true, responseOverride: request =>
        {
            if (providerRace) throw new LlmMemoryException("Simulated allocation refused before generation.");
            return Response(request);
        });
        var router = new Router();
        var library = new Library { BlockMemory = !providerRace };
        var processor = Processor(fixture, router, library);
        var mission = fixture.Store.Submit(new("Full-stack app", MissionKind.Project,
            TemplateId: FullStackTemplate.Id, AutomaticModelSelection: true));
        await Assert.ThrowsAsync<LlmMemoryException>(() => processor.ProcessAsync(mission.Id, default));
        var blocked = fixture.Store.Get(mission.Id)!;
        Assert.Equal(0, blocked.CorrectionAttempts);
        Assert.Equal(0, blocked.Workflow!.Steps.Single(step => step.Id == "plan").Attempts);
        Assert.All(blocked.Workflow.ModelInvocations, item => Assert.Equal("Blocked", item.Attempt.Outcome));
        if (!providerRace) Assert.Empty(fixture.Provider.Requests);
        using var restarted = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.True(restarted.Resume(mission.Id));
        library.BlockMemory = false;
        var routing = new WorkflowModelRouting(restarted, router, library, fixture.Options);
        var resumed = new CollaborativeProcessor(restarted,
            new WorkflowAgents(new LlmProviderResolver([new FakeProvider((request, _) => Task.FromResult(Response(request)))]),
                fixture.Options, routing), fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options, routing);
        await resumed.ProcessAsync(mission.Id, default);
        Assert.Equal(MissionStatus.AwaitingApproval, restarted.Get(mission.Id)!.Status);
        Assert.Equal(0, restarted.Get(mission.Id)!.CorrectionAttempts);
        Assert.Single(router.Inputs, input => input.Contains("operation: Plan;"));
    }

    [Fact]
    public async Task CompilationCorrectionsRetainFileSequenceAndRequireFreshApprovals()
    {
        using var fixture = new WorkflowFixture();
        var failedBuilds = 0;
        fixture.Sandbox.Result = stage => stage == "Build" && ++failedBuilds <= 2
            ? new(stage, "Failed", 1, "src/App/Program.cs(1,1): error CS1002: ; expected")
            : new(stage, "Succeeded", 0, "Simulated success", stage == "Tests" ? 2 : null,
                FailedTests: stage == "Tests" ? 0 : null);
        var processor = Processor(fixture, new Router(), new Library());
        var mission = fixture.Store.Submit(new("Build C# application", MissionKind.Project, AutomaticModelSelection: true));
        await processor.ProcessAsync(mission.Id, default);
        for (var index = 0; index < 2; index++)
        {
            await fixture.Approve(mission.Id);
            await processor.ProcessAsync(mission.Id, default);
            Assert.Equal(MissionStatus.AwaitingApproval, fixture.Store.Get(mission.Id)!.Status);
            Assert.False(fixture.Store.Get(mission.Id)!.SandboxApproved);
        }
        var current = fixture.Store.Get(mission.Id)!;
        var attempts = current.Workflow!.ModelInvocations.Where(item => item.Attempt.Role == "Developer" &&
            item.Sequence.EndsWith(":src/App/Program.cs")).ToArray();
        Assert.Equal(new[] { "dotnet:7b", "dotnet:7b", "reserve:7b" }, attempts.Select(item => item.Attempt.ModelName));
        Assert.Equal(2, current.CorrectionAttempts);
        Assert.Equal(3, current.Workflow.Revisions!.Count);
        Assert.All(attempts.Skip(1), item => Assert.Equal("Debugging", item.Attempt.TaskCategory));
        Assert.Equal(2, fixture.Sandbox.Calls.Count);
    }

    [Fact]
    public async Task EngineInputContextFailureIsBlockedRatherThanCountedAsModelQuality()
    {
        using var fixture = new WorkflowFixture(responseOverride: _ =>
            throw new StudioValidationException("The prompt needs about 9000 input tokens, but only 6000 are available."));
        var processor = Processor(fixture, new Router(), new Library());
        var mission = fixture.Store.Submit(new("Create app", MissionKind.Project, AutomaticModelSelection: true));
        await Assert.ThrowsAsync<StudioValidationException>(() => processor.ProcessAsync(mission.Id, default));
        var current = fixture.Store.Get(mission.Id)!;
        var attempt = Assert.Single(current.Workflow!.ModelInvocations).Attempt;
        Assert.Equal("Context", attempt.FailureKind);
        Assert.Equal("Blocked", attempt.Outcome);
        Assert.Equal(0, current.Workflow.Steps.Single(step => step.Id == "plan").Attempts);
        Assert.Equal(0, current.CorrectionAttempts);
    }

    [Fact]
    public async Task ConnectionFailureStopsWithoutQualityEscalationOrAutomaticRetry()
    {
        using var fixture = new WorkflowFixture(responseOverride: _ => throw new LlmProviderException("Connection unavailable."));
        var processor = Processor(fixture, new Router(), new Library());
        var mission = fixture.Store.Submit(new("Build app", MissionKind.Project, AutomaticModelSelection: true));
        await Assert.ThrowsAsync<LlmProviderException>(() => processor.ProcessAsync(mission.Id, default));
        var attempt = Assert.Single(fixture.Store.Get(mission.Id)!.Workflow!.ModelInvocations).Attempt;
        Assert.Equal("Infrastructure", attempt.FailureKind);
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(0, fixture.Store.Get(mission.Id)!.CorrectionAttempts);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(15)]
    public async Task OutputTruncationRetainsDraftAndExplicitResumeStartsAtMissingFileWithinFrozenBudget(int limit)
    {
        var truncate = true;
        using var fixture = new WorkflowFixture(responseOverride: request =>
        {
            if (Path(request) == "src/App.test.tsx" && truncate)
                throw new LlmOutputLimitException(request.Model.Name, request.Model.MaxOutputTokens, 2048, 900, 0);
            return MissionReactContractTests.Respond(request);
        });
        var router = new Router();
        var processor = Processor(fixture, router, new Library());
        var mission = fixture.Store.Submit(new("Display initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, AutomaticModelSelection: true,
            RequirePlanApproval: true, MaxTotalGenerations: limit));
        await processor.ProcessAsync(mission.Id, default);
        var waiting = fixture.Store.Get(mission.Id)!;
        Assert.Contains($"React {MissionReactTemplate.ReactVersion}", fixture.Provider.Requests.First().Messages[0].Content);
        Assert.True(fixture.Store.DecidePlanApproval(mission.Id, true, waiting.Workflow!.PlanApproval!.Fingerprint));
        var truncation = await Assert.ThrowsAsync<LlmOutputLimitException>(() => processor.ProcessAsync(mission.Id, default));
        fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Failed, Error = truncation.Message });
        var failed = fixture.Store.Get(mission.Id)!;
        Assert.Equal(4, failed.GenerationReservations!.Count);
        Assert.Equal(0, failed.CorrectionAttempts);
        Assert.False(failed.HasWorkspace);
        Assert.Empty(failed.Workflow!.Revisions ?? []);
        var generated = failed.Workflow.Draft!.Where(file => MissionReactTemplate.IsEditable(file.Path)).ToArray();
        Assert.Equal(new[] { "src/model.ts", "src/App.tsx" }, generated.Select(file => file.Path));
        Assert.Equal("OutputLimit", failed.Workflow.ModelInvocations.Last().Attempt.FailureKind);
        Assert.Equal("Failed", failed.Workflow.ModelInvocations.Last().Attempt.Outcome);
        Assert.Single(fixture.Provider.Requests, request => Path(request) == "src/App.test.tsx");
        var missionPath = System.IO.Path.Combine(fixture.Options.DataDirectory, "missions", mission.Id.ToString("N") + ".json");
        var snapshot = File.ReadAllBytes(missionPath);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(snapshot, File.ReadAllBytes(missionPath));
        if (limit == 4)
        {
            Assert.False(reopened.Resume(mission.Id));
            Assert.Equal(4, reopened.Get(mission.Id)!.GenerationReservations!.Count);
            return;
        }
        Assert.True(reopened.Resume(mission.Id));
        truncate = false;
        var routing = new WorkflowModelRouting(reopened, router, new Library(), fixture.Options);
        var resumed = new CollaborativeProcessor(reopened,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options, routing),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options, routing);
        await resumed.ProcessAsync(mission.Id, default);
        var current = reopened.Get(mission.Id)!;
        Assert.Equal(6, current.GenerationReservations!.Count);
        Assert.Equal(15, current.MaxTotalGenerations);
        Assert.Equal("src/App.test.tsx", Path(fixture.Provider.Requests.ElementAt(4)));
        Assert.Single(fixture.Provider.Requests, request => Path(request) == "src/model.ts");
        Assert.Single(fixture.Provider.Requests, request => Path(request) == "src/App.tsx");
        Assert.Equal(failed.Workflow.PlanApproval, current.Workflow!.PlanApproval);
        foreach (var file in generated)
            Assert.Equal(file.Content, current.Workflow.Draft!.Single(item => item.Path == file.Path).Content);
        Assert.All(fixture.Provider.Requests.Where(request => Path(request) is not null), request =>
        {
            var profile = current.Workflow.Models.Single(item =>
                item.Id == current.Workflow.Agents.Single(agent => agent.Role == "Developer").ModelId);
            Assert.Equal(profile.MaxOutputTokens, request.Model.MaxOutputTokens);
            Assert.Equal(profile.ContextSize, request.Model.ContextSize);
            Assert.Equal(profile.Temperature, request.Model.Temperature);
        });
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.False(current.SandboxApproved);
    }

    private static CollaborativeProcessor Processor(WorkflowFixture fixture, Router router, Library library)
    {
        var routing = new WorkflowModelRouting(fixture.Store, router, library, fixture.Options);
        return new(fixture.Store, new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options, routing),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options, routing);
    }

    private static string? Path(LlmRequest request)
    {
        using var document = JsonDocument.Parse(request.Messages.Last().Content);
        return document.RootElement.TryGetProperty("file", out var file) ? file.GetProperty("path").GetString() : null;
    }

    private static string Response(LlmRequest request)
    {
        var properties = request.ResponseSchema!.Value.GetProperty("properties");
        object response = properties.TryGetProperty("decisions", out _) ? WorkflowFixture.Plan :
            properties.TryGetProperty("content", out _) ? FullStackTemplate.Files.Single(file => file.Path == Path(request)) :
            new ReviewReport("Reviewed current sources, not executed.", [], "None", [], "Matches contract.", [], "Compliant.", "Validate.");
        return JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class Router : IModelRouter
    {
        public List<string> Inputs { get; } = [];
        public Task<ModelRoutingDecision> SelectAsync(string task, string role, int contextTokens, CancellationToken token)
        {
            Inputs.Add(task);
            var category = role == "Planner" ? "Planning" : role == "Reviewer" ? "Review" :
                role == "Frontend" || task.Contains("file: frontend/") ? "ReactTypeScript" : "DotNet";
            var name = category switch { "Planning" => "plan:7b", "Review" => "review:7b", "ReactTypeScript" => "react:7b", _ => "dotnet:7b" };
            return Task.FromResult(new ModelRoutingDecision(name, category, "Simulated specialization; no performance guarantee.", "Low",
                [new(name, 10, "Primary", new("Compatible", "Simulated hardware", contextTokens)),
                 new("reserve:7b", 5, "Alternative", new("Compatible", "Simulated hardware", contextTokens))]));
        }
        public void ResetStatistics() => throw new NotSupportedException();
    }

    private sealed class Library : IModelLibraryService
    {
        public bool BlockMemory { get; set; }
        public ModelRoutingProfile SaveSpecializations(string name, string[]? specializations) => throw new NotSupportedException();
        public ModelRoutingProfile ClearQualification(string name) => throw new NotSupportedException();
        public Task<ModelRoutingProfile> QualifyAsync(string name, int context, bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task GuardExecutionAsync(string name, int context, CancellationToken token, bool automatic = false) =>
            BlockMemory ? throw new LlmMemoryException("Simulated insufficient RAM/VRAM; no generation started.") : Task.CompletedTask;
        public Task<ModelLibrarySnapshot> SnapshotAsync(int context, CancellationToken token) => throw new NotSupportedException();
        public Task<long> VerifyAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public IReadOnlyList<ModelDownload> Downloads() => throw new NotSupportedException();
        public ModelDownload StartDownload(string name, bool confirmed) => throw new NotSupportedException();
        public bool CancelDownload(Guid id) => throw new NotSupportedException();
        public Task DeleteAsync(string name, bool confirmed, CancellationToken token) => throw new NotSupportedException();
    }
}
