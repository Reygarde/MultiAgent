using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class CustomAgentModelPolicyTests
{
    private static readonly CustomAgentProfile Agent = new(Guid.NewGuid(), "Developer", "", "Implement.",
        "qwen2.5-coder:7b", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static readonly string[] Installed = ["qwen2.5-coder:7b", "qwen3.5:9b", "qwen2.5-coder:14b"];

    [Theory]
    [InlineData(0, true, "qwen2.5-coder:7b")]
    [InlineData(1, true, "qwen2.5-coder:7b")]
    [InlineData(2, true, "qwen3.5:9b")]
    [InlineData(3, false, "qwen2.5-coder:7b")]
    public void ChangesOnlyAfterTwoQualityFailuresAndExplicitTeamOptIn(int failures, bool enabled, string expected)
    {
        var result = CustomAgentModelPolicy.Select(Agent, new(Agent.Id, "Developer", 1), enabled,
            Installed, Enumerable.Range(0, failures).Select(_ => Attempt("Generation")).ToArray());
        Assert.Equal(expected, result.Model);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Context")]
    [InlineData("Timeout")]
    [InlineData("Memory")]
    [InlineData("Environment")]
    [InlineData("Infrastructure")]
    [InlineData("Cancelled")]
    public void SuccessAndNonQualityFailuresBreakTheStreak(string? lastFailure)
    {
        var result = CustomAgentModelPolicy.Select(Agent, new(Agent.Id, "Developer", 1), true,
            Installed, [Attempt("Generation"), Attempt(lastFailure)]);
        Assert.Equal(Agent.ModelName, result.Model);
        Assert.False(result.Reason.Contains("Two consecutive", StringComparison.Ordinal));
    }

    [Fact]
    public void SkipsMissingModelsAndHonorsUserOrderWithoutGrantingAttempts()
    {
        var result = CustomAgentModelPolicy.Select(Agent,
            new(Agent.Id, "Developer", 1, ["missing:9b", "qwen2.5-coder:14b", "qwen3.5:9b"]), true,
            Installed, [Attempt("Generation"), Attempt("Generation")]);
        Assert.Equal("qwen2.5-coder:14b", result.Model);
        Assert.DoesNotContain("missing:9b", result.Candidates);
        Assert.Contains("missing:9b", result.Reason);
        var exhausted = CustomAgentModelPolicy.Select(Agent, new(Agent.Id, "Developer", 1), true,
            [Agent.ModelName], [Attempt("Generation"), Attempt("Generation")]);
        Assert.Equal(Agent.ModelName, exhausted.Model);
        Assert.Contains("exhausted", exhausted.Reason);
    }

    [Theory]
    [InlineData("src/App.tsx(68,32): error TS18004: No value exists in scope.", "Generation")]
    [InlineData("FAIL src/App.test.tsx > updates a task: AssertionError", "Generation")]
    [InlineData("NuGet restore: service index unavailable.", "Environment")]
    [InlineData("npm ERR! code ENOTFOUND registry.npmjs.org", "Environment")]
    [InlineData("MSB3027: Cannot copy a file in use.", "Environment")]
    [InlineData("Failed to restore packages: network unavailable.", "Environment")]
    public void SeparatesBuildAndTestQualityFromEnvironment(string message, string expected)
    {
        var validation = new CustomAgentValidationReport("Passed", "Failed", 0,
            [new("src/App.tsx", "LOCAL_BUILD_OR_TEST_FAILED", message)], []);
        Assert.Equal(expected, CustomAgentModelPolicy.Classify(validation));
        Assert.Equal("Environment", CustomAgentModelPolicy.Classify(validation with { CompilationStatus = "TimedOut" }));
    }

    [Fact]
    public void ClassifiesResourceExceptionsWithoutTreatingThemAsQuality()
    {
        Assert.Equal("Memory", CustomAgentModelPolicy.Classify(new LlmMemoryException("memory")));
        Assert.Equal("Timeout", CustomAgentModelPolicy.Classify(new LlmTimeoutException("timeout")));
        Assert.Equal("Context", CustomAgentModelPolicy.Classify(new LlmContextException("context")));
        Assert.Equal("Infrastructure", CustomAgentModelPolicy.Classify(new LlmDiagnosticException("trace capacity")));
        Assert.Equal("Context", CustomAgentModelPolicy.Classify(
            new StudioValidationException("The prompt needs about 9000 input tokens, but only 6000 are available.")));
    }

    [Theory]
    [InlineData(true, true, "qwen3.5:9b", false)]
    [InlineData(false, true, "qwen2.5-coder:7b", false)]
    [InlineData(true, false, "qwen2.5-coder:7b", false)]
    [InlineData(true, true, "qwen2.5-coder:7b", true)]
    public async Task FailedPlanRetriesPreserveHistoryPermissionsRollbackAndLimits(
        bool enabled, bool fallbackInstalled, string expectedThirdModel, bool automatic)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIPlatformModelEscalation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new StudioOptions { DataDirectory = directory, MaxDevelopmentPlanAttempts = 3 };
            var configuration = TestSupport.Configuration() with
            {
                Models = [.. TestSupport.Configuration().Models, new()
                {
                    Id = "local-coder", Provider = "Ollama", Name = Agent.ModelName,
                    ContextSize = 8192, MaxOutputTokens = 2048
                }]
            };
            var registry = new AgentRegistry(configuration);
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration, registry, workspaces);
            const string snippet = "const body = JSON.stringify({ title: 'test' });";
            var calls = new List<string>();
            var provider = new FakeProvider((request, _) =>
            {
                if (request.Messages[0].Content.Contains("Planner for an autonomous multi-step", StringComparison.Ordinal))
                    return Task.FromResult("""{"tasks":[{"id":"done","title":"Done","description":"Existing work","dependencies":[],"acceptanceCriteria":["Preserved"]}]}""");
                Assert.Contains("You are the Developer", request.Messages[0].Content);
                Assert.Equal(enabled || automatic, request.ExclusiveModel);
                Assert.Equal(automatic, request.RequireAutomaticAdmissibility);
                calls.Add(request.Model.Name);
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    replacement = calls.Count < 3 ? "const body = JSON.stringify(missingTitle);" : "const body = JSON.stringify('test');"
                }));
            });
            var commands = new Queue<LocalCommandResult>();
            for (var attempt = 0; attempt < 3; attempt++)
                foreach (var command in new[] { "dotnet restore", "npm install", "dotnet build", "npm run build" })
                    commands.Enqueue(new(command, attempt < 2 && command == "npm run build" ? 2 : 0, false,
                        attempt < 2 && command == "npm run build" ? "src/App.tsx(68,32): error TS18004: No value exists in scope." : "Passed",
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            var catalog = new FakeOllamaModelCatalog(fallbackInstalled
                ? [Agent.ModelName, "qwen3:4b", "qwen3.5:9b"] : [Agent.ModelName, "qwen3:4b"]);
            var store = new CustomAgentStore(options);
            var router = new FakeRouter();
            var service = new CustomAgentService(store, catalog, provider, registry, TimeProvider.System, workspaces, missions,
                studioOptions: options, localCommands: new FakeLocalCommandExecutor(commands),
                browserTestRunner: new FakeStudioBrowserTestRunner(), modelRouter: router);
            var planner = await service.CreateAgentAsync(new("Planner", "", "Plan.", "qwen3:4b"), CancellationToken.None);
            var developer = await service.CreateAgentAsync(new("Developer", "", "Implement.", Agent.ModelName,
                AutomaticModelSelection: automatic), CancellationToken.None);
            var reviewer = await service.CreateAgentAsync(new("Reviewer", "", "Review.", "qwen3:4b"), CancellationToken.None);
            var team = service.SaveTeam(null, new("Escalation test",
                [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)], enabled, automatic))!;
            var project = await missions.CreateCustomAgentProjectAsync("Test fixture", CustomAgentProjectType.FullStack, CancellationToken.None);
            var original = AgentFullStackTemplate.Files.Single(file => file.Path == "frontend/src/App.tsx").Content;
            var app = snippet + "\n" + original;
            await workspaces.ApplyReviewedChangesAsync(project.Id, AgentFullStackTemplate.Id,
                [new("frontend/src/App.tsx", original, app)], _ => true, CancellationToken.None);
            var plan = (await service.CreateDevelopmentPlanAsync(team.Id, "Preserve completed work",
                new(CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!), CancellationToken.None))!;
            var done = plan.Tasks[0] with { Status = "Completed", Attempts = 2 };
            plan = plan with
            {
                Status = "Paused",
                Tasks = [done, done with { Id = "pending-tests", Status = "Pending", Attempts = 0, Order = 1 }]
            };
            Assert.True(store.UpdateDevelopmentPlan(plan));
            service.StartTargetedCorrection(team.Id, plan.Id, new("frontend/src/App.tsx", snippet,
                "[FromBody] string title, bool completed", "PUT returned 400", "Send a JSON string.", CompilationOnly: true), true, true);
            var oldRunIds = new List<Guid>();
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (service.GetDevelopmentPlan(team.Id, plan.Id)!.Status == "Running")
                    await Task.Delay(20, timeout.Token);
                plan = new CustomAgentStore(options).GetDevelopmentPlan(team.Id, plan.Id)!;
                var task = plan.Tasks[2];
                Assert.Equal(attempt, plan.Attempts);
                Assert.Equal(attempt, task.Attempts);
                Assert.Equal(JsonSerializer.Serialize(done), JsonSerializer.Serialize(plan.Tasks[0]));
                Assert.Equal("Pending", plan.Tasks[1].Status);
                var run = new CustomAgentStore(options).GetCollaborationRun(team.Id, task.CollaborationRunId!.Value)!;
                oldRunIds.Add(run.Id);
                var modelAttempt = Assert.Single(run.ModelAttempts!);
                Assert.Equal(attempt, task.ModelAttempts!.Count);
                Assert.All(run.ModelAttempts!, item => Assert.Equal("Developer", item.Role));
                Assert.All(run.Contributions, item => Assert.Equal(item.ModelName, calls.Last()));
                if (attempt < 3)
                {
                    Assert.Equal("Failed", plan.Status);
                    Assert.NotNull(run.ChangesRolledBackAt);
                    Assert.True(run.ModelAttempts!.Last().RolledBack);
                    Assert.NotNull(modelAttempt.CompletedAt);
                    Assert.Equal(app, (await workspaces.ReadAsync(project.Id, "frontend/src/App.tsx", CancellationToken.None)).Content);
                    Assert.Equal("Generation", run.ModelAttempts!.Last().FailureKind);
                    Assert.Equal("Failed", run.ModelAttempts!.Last().CompilationStatus);
                    Assert.Contains(run.Validation!.Diagnostics, diagnostic => diagnostic.Message.Contains("TS18004"));
                    Assert.Throws<StudioValidationException>(() =>
                        service.RetryDevelopmentTask(team.Id, plan.Id, task.Id, false, true, compilationOnly: true));
                    Assert.Equal(attempt, store.GetDevelopmentPlan(team.Id, plan.Id)!.Attempts);
                    service.RetryDevelopmentTask(team.Id, plan.Id, task.Id, true, true, compilationOnly: true);
                }
                else
                {
                    Assert.Equal("Paused", plan.Status);
                    Assert.Equal("Succeeded", run.ModelAttempts!.Last().Outcome);
                    Assert.Equal("Unavailable", modelAttempt.TestsStatus);
                    Assert.Equal("Passed", run.ModelAttempts!.Last().CompilationStatus);
                }
            }
            Assert.Equal(new[] { automatic ? "qwen3.5:9b" : Agent.ModelName,
                automatic ? "qwen3.5:9b" : Agent.ModelName, expectedThirdModel }, calls);
            Assert.Equal(automatic ? 1 : 0, router.Calls);
            Assert.All(oldRunIds, id => Assert.NotNull(new CustomAgentStore(options).GetCollaborationRun(team.Id, id)));
            var exhausted = plan with { Status = "Failed", Tasks = plan.Tasks.Select(task =>
                task.Order == 2 ? task with { Status = "Failed" } : task).ToArray() };
            Assert.True(store.UpdateDevelopmentPlan(exhausted));
            Assert.Throws<StudioValidationException>(() => service.RetryDevelopmentTask(
                team.Id, plan.Id, exhausted.Tasks[2].Id, true, true, compilationOnly: true));
            Assert.Equal(3, calls.Count);
        }

        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeRouter : IModelRouter
    {
        public int Calls { get; private set; }
        public Task<ModelRoutingDecision> SelectAsync(string task, string role, int contextTokens, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new ModelRoutingDecision("qwen3.5:9b", "Debugging", "Simulated decision", "Low",
                new[] { "qwen3.5:9b", Agent.ModelName }.Select(model =>
                    new ModelRoutingChoice(model, 1, "Simulated hardware", new("Compatible", "Simulated", contextTokens))).ToArray()));
        }
        public void ResetStatistics() => throw new InvalidOperationException("Not requested");
    }

    [Fact]
    public async Task DiscoversOnlyInstalledCompletionCapableModelsWithoutLoadingThem()
    {
        var paths = new List<string>();
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            paths.Add(message.RequestUri!.AbsolutePath);
            if (message.RequestUri.AbsolutePath == "/api/tags")
                return StubHttpHandler.Json("""{"models":[{"name":"coder:7b"},{"name":"embed:latest"}]}""");
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            return StubHttpHandler.Json(json.RootElement.GetProperty("model").GetString() == "coder:7b"
                ? """{"capabilities":["completion"]}""" : """{"capabilities":["embedding"]}""");
        });
        using var client = new HttpClient(handler);
        var catalog = new OllamaModelCatalog(client, new());
        Assert.Equal(new[] { "coder:7b" }, await catalog.GetCompatibleInstalledModelsAsync(CancellationToken.None));
        Assert.Equal(new[] { "/api/tags", "/api/show", "/api/show" }, paths);
    }

    [Theory]
    [InlineData("Memory")]
    [InlineData("Timeout")]
    [InlineData("Context")]
    [InlineData("Environment")]
    public async Task ResourceAndEnvironmentFailuresAreAuditedWithoutEscalation(string failure)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIPlatformModelResource-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration() with
            {
                Models = [.. TestSupport.Configuration().Models, new()
                {
                    Id = "resource-test", Provider = "Ollama", Name = Agent.ModelName,
                    ContextSize = failure == "Context" ? 1536 : 8192, MaxOutputTokens = 1024
                }]
            };
            var registry = new AgentRegistry(configuration);
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration, registry, workspaces);
            var developerCalls = 0;
            var provider = new FakeProvider((request, _) =>
            {
                if (request.Messages[0].Content.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Update the task view."}""");
                Assert.Contains("You are the Developer", request.Messages[0].Content);
                developerCalls++;
                if (failure == "Memory") throw new LlmMemoryException("Ollama returned a memory allocation failure.");
                if (failure == "Timeout") throw new LlmTimeoutException("Ollama request exceeded its configured timeout.");
                return Task.FromResult("""{"files":[{"path":"frontend/src/App.tsx","operation":"update","content":"export default function App() { return <main>Tasks</main> }"}]}""");
            });
            var executor = new FakeLocalCommandExecutor(new([
                new("dotnet restore", 1, false, "NuGet: network service index unavailable.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]));
            var store = new CustomAgentStore(options);
            var service = new CustomAgentService(store,
                new FakeOllamaModelCatalog([Agent.ModelName, "qwen3:4b", "qwen3.5:9b"]), provider, registry,
                TimeProvider.System, workspaces, missions, studioOptions: options, localCommands: executor,
                browserTestRunner: new FakeStudioBrowserTestRunner());
            var planner = await service.CreateAgentAsync(new("Planner", "", "Plan.", "qwen3:4b"), CancellationToken.None);
            var developer = await service.CreateAgentAsync(new("Developer", "", "Implement.", Agent.ModelName), CancellationToken.None);
            var reviewer = await service.CreateAgentAsync(new("Reviewer", "", "Review.", "qwen3:4b"), CancellationToken.None);
            var team = service.SaveTeam(null, new("Resource failures",
                [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)], true))!;
            var project = await missions.CreateCustomAgentProjectAsync("Resource fixture", CustomAgentProjectType.FullStack, CancellationToken.None);
            var run = service.StartCollaboration(team.Id, "Update the task view.",
                new(CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!), null, "Autonomous", true, true)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            do
            {
                await Task.Delay(20, timeout.Token);
                run = service.GetCollaborationRun(team.Id, run.Id)!;
            } while (run.Status is "Running" or "Queued");
            Assert.Equal("Failed", run.Status);
            Assert.Equal(failure == "Context" ? 0 : 1, developerCalls);
            var attempt = Assert.Single(new CustomAgentStore(options).GetCollaborationRun(team.Id, run.Id)!
                .ModelAttempts!, item => item.Role == "Developer");
            Assert.Equal(Agent.ModelName, attempt.ModelName);
            Assert.Equal(failure, attempt.FailureKind);
            Assert.Null(attempt.NextModel);
            Assert.Equal("Failed", attempt.Outcome);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static CustomAgentModelAttempt Attempt(string? failure) => new(Guid.NewGuid(), Guid.NewGuid(), Agent.Id,
        "Developer", Agent.ModelName, "Test selection", DateTimeOffset.UtcNow, true, Installed,
        failure is null ? "Succeeded" : "Failed", failure);
}
