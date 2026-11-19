using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure;
using AIPlatform.Infrastructure.Studio;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AIPlatform.Tests;

public sealed class CustomAgentProjectTests
{
    private readonly ITestOutputHelper testOutput;

    public CustomAgentProjectTests(ITestOutputHelper testOutput) => this.testOutput = testOutput;

    [Theory]
    [InlineData(CustomAgentProjectType.React, AgentReactTemplate.Id)]
    [InlineData(CustomAgentProjectType.DotNet, AgentDotNetTemplate.Id)]
    [InlineData(CustomAgentProjectType.FullStack, AgentFullStackTemplate.Id)]
    public async Task CreatesPersistedScaffoldWithoutQueueingSystemGeneration(
        CustomAgentProjectType type, string expectedTemplateId)
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);

            var project = await missions.CreateCustomAgentProjectAsync(
                "Task manager", type, CancellationToken.None);

            Assert.Equal(MissionStatus.Completed, project.Status);
            Assert.True(project.HasWorkspace);
            Assert.Equal("Task manager", project.ProjectName);
            Assert.Equal(expectedTemplateId, project.TemplateId);
            Assert.Equal("Project", project.Events.Single().Step);
            Assert.Equal(WorkflowStatus.Completed, project.Workflow!.Steps.Single().Status);
            Assert.True(workspaces.List(project.Id).Count >= 4);
            Assert.DoesNotContain(missions.List(), item => item.Status == MissionStatus.Queued);

            using var restarted = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            Assert.Equal(project.Id, restarted.Get(project.Id)!.Id);
            Assert.Equal(MissionStatus.Completed, restarted.Get(project.Id)!.Status);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task TeamProposalCreatesAndAppliesDotNetFilesOnlyAfterApproval(int generationLimit)
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"1. Add the requested route. 2. Add its service and test.","changes":[]}""");
                if (system.Contains("read-only Reviewer", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"The route, service, and test are consistent.","requiresCorrection":false}""");
                if (system.Contains("You are the Developer", StringComparison.Ordinal))
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        response = "Added a task route, service, and focused unit test.",
                        changes = new[]
                        {
                            new
                            {
                                operation = "modify", path = "src/App/Program.cs", summary = "Expose task route.",
                                content = "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.MapGet(\"/tasks\", () => TaskCatalog.All);\napp.MapGet(\"/health\", () => Results.Ok(new { status = HealthStatus.Get() }));\napp.Run();\npublic static class HealthStatus { public static string Get() => \"ok\"; }\npublic static class TaskCatalog { public static string[] All => [\"Review task\"]; }\n"
                            },
                            new
                            {
                                operation = "create", path = "src/App/TaskService.cs", summary = "Add task service.",
                                content = "public sealed class TaskService { public string[] GetAll() => TaskCatalog.All; }\n"
                            },
                            new
                            {
                                operation = "create", path = "tests/App.Tests/TaskServiceTests.cs", summary = "Test task service.",
                                content = "using Xunit;\npublic sealed class TaskServiceTests { [Fact] public void ReturnsTasks() => Assert.Contains(\"Review task\", new TaskService().GetAll()); }\n"
                            }
                        }
                    }));
                throw new InvalidOperationException("Unexpected collaboration stage.");
            });
            var customAgents = new CustomAgentService(new CustomAgentStore(options),
                new FakeOllamaModelCatalog(), provider, new AgentRegistry(configuration),
                TimeProvider.System, workspaces, missions);
            var planner = await customAgents.CreateAgentAsync(
                new("Planner", "", "Plan.", "qwen3:4b"), CancellationToken.None);
            var developer = await customAgents.CreateAgentAsync(
                new("Developer", "", "Implement.", "qwen2.5-coder:7b"), CancellationToken.None);
            var reviewer = await customAgents.CreateAgentAsync(
                new("Reviewer", "", "Review.", "qwen3:4b"), CancellationToken.None);
            var team = customAgents.SaveTeam(null, new("Task app team",
                [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)]))!;
            var project = await missions.CreateCustomAgentProjectAsync(
                "Task manager", CustomAgentProjectType.DotNet, CancellationToken.None, generationLimit);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);

            var queued = customAgents.StartCollaboration(team.Id,
                "Add a task endpoint and service.", workspace, null)!;
            var completed = await WaitForRunAsync(customAgents, team.Id, queued.Id);

            Assert.Equal(Math.Min(generationLimit, 3), missions.Get(project.Id)!.GenerationReservations!.Count);
            Assert.Equal(new[] { "Planner", "Developer", "Reviewer" }.Take(generationLimit),
                missions.Get(project.Id)!.GenerationReservations!.Select(item => item.Role));
            if (generationLimit == 2)
            {
                Assert.Equal("Failed", completed.Status);
                Assert.Contains("GenerationBudgetExhausted", completed.Error);
                Assert.Equal(2, provider.Requests.Count);
                Assert.Equal(MissionStatus.Failed, missions.Get(project.Id)!.Status);
                return;
            }
            Assert.Equal("AwaitingApproval", completed.Status);
            Assert.Equal(new[] { "Planner", "Developer", "Reviewer" },
                completed.Contributions.Select(item => item.Role));
            Assert.Equal(project.Id, completed.Proposal!.Workspace!.WorkspaceId);
            Assert.Equal(3, completed.Proposal.Changes.Count);
            Assert.True(completed.Proposal.CanApply);
            Assert.Equal(AgentDotNetTemplate.Files.Single(file => file.Path == "src/App/Program.cs").Content,
                (await workspaces.ReadAsync(project.Id, "src/App/Program.cs", CancellationToken.None)).Content);
            Assert.Equal(MissionStatus.Completed, missions.Get(project.Id)!.Status);

            var applied = await customAgents.DecideCollaborationProposalAsync(
                team.Id, completed.Id, true, CancellationToken.None);

            Assert.Equal("Applied", applied!.Proposal!.Status);
            Assert.Equal(MissionStatus.GeneratedUnverified, missions.Get(project.Id)!.Status);
            Assert.Contains("TaskCatalog", (await workspaces.ReadAsync(
                project.Id, "src/App/Program.cs", CancellationToken.None)).Content);
            Assert.Contains(workspaces.List(project.Id), file => file.Path == "src/App/TaskService.cs");
            Assert.Contains(workspaces.List(project.Id), file => file.Path == "tests/App.Tests/TaskServiceTests.cs");

            var restartedAgents = new CustomAgentStore(options);
            Assert.Equal("Applied", restartedAgents.GetCollaborationRun(team.Id, completed.Id)!.Proposal!.Status);
            using var restartedMissions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            Assert.Equal(MissionStatus.GeneratedUnverified, restartedMissions.Get(project.Id)!.Status);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(8192, true)]
    [InlineData(4096, false)]
    public async Task FullStackCorrectionDeduplicatesSourcesAndReservesSchemaAndOutputBudget(
        int contextSize, bool fits)
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration() with
            {
                Models = [.. TestSupport.Configuration().Models, new()
                {
                    Id = "local-coder", Provider = "Ollama", Name = "qwen2.5-coder:7b",
                    ContextSize = contextSize, MaxOutputTokens = 2048
                }]
            };
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration, new AgentRegistry(configuration), workspaces);
            var corrected = false;
            var proposedApp = "import './Missing.css';\n// pending-source-marker\n" +
                new string(' ', 4000) + "\nexport default function App() { return <main>Tasks</main> }";
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                var input = request.Messages[1].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult(JsonSerializer.Serialize(new { response = new string('p', 600) }));
                if (system.Contains("You are the Developer", StringComparison.Ordinal))
                {
                    var bytes = System.Text.Encoding.UTF8.GetByteCount(system + input) +
                        System.Text.Encoding.UTF8.GetByteCount(request.ResponseSchema!.Value.GetRawText());
                    Assert.True((bytes + 2) / 3 + request.Model.MaxOutputTokens + 768 <= request.Model.ContextSize);
                    Assert.Contains("[FromBody] string title, bool completed", system);
                    Assert.DoesNotContain("irrelevant-store-marker", system);
                    if (system.Contains("minimal correction", StringComparison.Ordinal))
                    {
                        corrected = true;
                        Assert.Contains("LOCAL_IMPORT_MISSING", input);
                        Assert.Contains("pending-source-marker", system);
                        Assert.DoesNotContain("pending-source-marker", input);
                        return Task.FromResult("""{"files":[{"path":"frontend/src/App.tsx","operation":"update","content":"export default function App() { return <main>Tasks</main> }"}]}""");
                    }
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        files = new[] { new { path = "frontend/src/App.tsx", operation = "update", content = proposedApp } }
                    }));
                }
                if (system.Contains("Reviewer", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Consistent.","requiresCorrection":false}""");
                throw new InvalidOperationException("Unexpected collaboration stage.");
            });
            var service = new CustomAgentService(new CustomAgentStore(options), new FakeOllamaModelCatalog(),
                provider, new AgentRegistry(configuration), TimeProvider.System, workspaces, missions);
            var team = await CreateTeamAsync(service);
            var project = await missions.CreateCustomAgentProjectAsync(
                "Full stack", CustomAgentProjectType.FullStack, CancellationToken.None);
            var module = AgentFullStackTemplate.Files.Single(file => file.Path == "backend/src/App/AppModule.cs").Content;
            await workspaces.ApplyReviewedChangesAsync(project.Id, AgentFullStackTemplate.Id,
                [
                    new("backend/src/App/AppModule.cs", module,
                        module + "\n// PUT signature: [FromBody] string title, bool completed"),
                    new("backend/src/App/TaskStore.cs", null, "// irrelevant-store-marker")
                ], _ => true, CancellationToken.None);
            var queued = service.StartCollaboration(team.Id, "add-frontend-ui: implement only the frontend.",
                new(CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!), null)!;
            var completed = await WaitForRunAsync(service, team.Id, queued.Id);
            if (!fits)
            {
                Assert.Equal("Failed", completed.Status);
                Assert.Contains("input tokens", completed.Error);
                Assert.False(corrected);
                return;
            }
            Assert.True(completed.Status == "AwaitingApproval", completed.Error);
            Assert.True(corrected);
            Assert.Equal(1, completed.Validation!.CorrectionAttempts);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryingOneTaskPausesWithoutRepeatingCompletedOrStartingPendingTasks(bool resume)
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration, new AgentRegistry(configuration), workspaces);
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("Planner for an autonomous multi-step", StringComparison.Ordinal))
                    return Task.FromResult("""{"tasks":[{"id":"done","title":"Done","description":"Existing work","dependencies":[],"acceptanceCriteria":["Existing work remains"]},{"id":"frontend","title":"Frontend","description":"Update frontend","dependencies":["done"],"acceptanceCriteria":["Frontend builds"]},{"id":"tests","title":"Tests","description":"Add tests","dependencies":["frontend"],"acceptanceCriteria":["Tests pass"]}]}""");
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Update the frontend."}""");
                if (system.Contains("You are the Developer", StringComparison.Ordinal))
                {
                    if (resume)
                        Assert.Contains("Preserve the actual PUT contract.", request.Messages[1].Content);
                    return Task.FromResult("""{"files":[{"path":"frontend/src/App.tsx","operation":"update","content":"export default function App() { return <main>Ready</main> }"}]}""");
                }
                if (system.Contains("Reviewer", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Consistent.","requiresCorrection":false}""");
                throw new InvalidOperationException("Unexpected collaboration stage: " + system[..Math.Min(system.Length, 200)]);
            });
            var executor = new FakeLocalCommandExecutor(new(Enumerable.Range(0, 6)
                .Select(_ => new LocalCommandResult("validation", 0, false, "Passed",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))));
            var store = new CustomAgentStore(options);
            var service = new CustomAgentService(store, new FakeOllamaModelCatalog(), provider,
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions,
                studioOptions: options, localCommands: executor, browserTestRunner: new FakeStudioBrowserTestRunner());
            var team = await CreateTeamAsync(service);
            var project = await missions.CreateCustomAgentProjectAsync(
                "Existing full-stack", CustomAgentProjectType.FullStack, CancellationToken.None);
            var plan = (await service.CreateDevelopmentPlanAsync(team.Id, "Complete the existing frontend",
                new(CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!), CancellationToken.None))!;
            var completedTask = plan.Tasks[0] with { Status = "Completed", Attempts = 2 };
            plan = plan with
            {
                Status = resume ? "Paused" : "Failed",
                Tasks = [completedTask, plan.Tasks[1] with { Status = resume ? "Pending" : "Failed", Attempts = 1 }, plan.Tasks[2]]
            };
            Assert.True(store.UpdateDevelopmentPlan(plan));
            if (resume)
                service.ResumeDevelopmentPlan(team.Id, plan.Id, true, true, true, "Preserve the actual PUT contract.");
            else
                service.RetryDevelopmentTask(team.Id, plan.Id, "frontend", true, true, taskOnly: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (service.GetDevelopmentPlan(team.Id, plan.Id)!.Status == "Running")
                await Task.Delay(20, timeout.Token);
            var saved = new CustomAgentStore(options).GetDevelopmentPlan(team.Id, plan.Id)!;
            Assert.Equal("Paused", saved.Status);
            Assert.Equal(JsonSerializer.Serialize(completedTask), JsonSerializer.Serialize(saved.Tasks[0]));
            Assert.Equal("Completed", saved.Tasks[1].Status);
            Assert.Equal(2, saved.Tasks[1].Attempts);
            Assert.Equal("Pending", saved.Tasks[2].Status);
            Assert.Equal(0, saved.Tasks[2].Attempts);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellingCorrectionPreservesLatestExecutionAndBrowserResults()
        => await VerifyUnchangedFunctionalCorrectionAsync(cancel: true);

    [Fact]
    public async Task UnchangedCorrectionsPreserveFunctionalFailureAndStopAtTheLimit()
        => await VerifyUnchangedFunctionalCorrectionAsync(cancel: false);

    [Fact]
    public async Task UnchangedCorrectionsEscalateWithoutIncreasingTheCorrectionLimit()
        => await VerifyUnchangedFunctionalCorrectionAsync(cancel: false, escalation: true);

    private static async Task VerifyUnchangedFunctionalCorrectionAsync(bool cancel, bool escalation = false)
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory, MaxCorrectionAttempts = 2 };
            var configuration = TestSupport.Configuration() with
            {
                Models = [.. TestSupport.Configuration().Models, new()
                {
                    Id = "local-coder", Provider = "Ollama", Name = "qwen2.5-coder:7b",
                    ContextSize = 8192, MaxOutputTokens = 2048
                }, new()
                {
                    Id = "fallback-coder", Provider = "Ollama", Name = "qwen3.5:9b",
                    ContextSize = 8192, MaxOutputTokens = 2048
                }]
            };
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration, new AgentRegistry(configuration), workspaces);
            const string app = "export default function App() { return <main>Tasks</main> } // /api/tasks";
            var enteredCorrection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var corrections = 0;
            var developerModels = new List<string>();
            var provider = new FakeProvider(async (request, token) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return """{"response":"Fix the PUT integration in the frontend."}""";
                if (system.Contains("making a minimal correction", StringComparison.Ordinal))
                {
                    corrections++;
                    Assert.Contains("PUT returned 400", request.Messages[1].Content);
                    enteredCorrection.TrySetResult(true);
                    if (cancel)
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                if (system.Contains("You are the Developer", StringComparison.Ordinal))
                {
                    developerModels.Add(request.Model.Name);
                    return JsonSerializer.Serialize(new
                    {
                        files = new[] { new { path = "frontend/src/App.tsx", operation = "update", content = app } }
                    });
                }
                throw new InvalidOperationException("Reviewer must not run for an unresolved functional failure.");
            });
            var executor = new FakeLocalCommandExecutor(new(Enumerable.Range(0, 6)
                .Select(_ => new LocalCommandResult("validation", 0, false, "Passed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))));
            var report = new CustomAgentBrowserTestReport(Guid.NewGuid(), "Failed", "http://127.0.0.1:5173/",
                DateTimeOffset.UtcNow, 0, 1, null,
                [new(Guid.NewGuid().ToString("N"), "PUT integration", "Failed", "click", "PUT returned 400", [], null)]);
            var browser = new FakeStudioBrowserTestRunner(new([report]));
            var store = new CustomAgentStore(options);
            var service = new CustomAgentService(store,
                new FakeOllamaModelCatalog(["qwen3:4b", "qwen2.5-coder:7b", "qwen3.5:9b"]), provider,
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions,
                studioOptions: options, localCommands: executor, browserTestRunner: browser);
            var team = await CreateTeamAsync(service);
            if (escalation)
                team = service.SaveTeam(team.Id, new(team.Name, team.Members, true))!;
            var project = await missions.CreateCustomAgentProjectAsync(
                "Full stack", CustomAgentProjectType.FullStack, CancellationToken.None);
            var queued = service.StartCollaboration(team.Id, "add-frontend-ui: fix PUT.",
                new(CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!), null,
                "Autonomous", true, true)!;
            if (cancel)
            {
                await enteredCorrection.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(service.CancelCollaboration(team.Id, queued.Id));
            }
            var completed = await WaitForRunAsync(service, team.Id, queued.Id);
            Assert.Equal(cancel ? "Cancelled" : "Failed", completed.Status);
            var persisted = new CustomAgentStore(options).GetCollaborationRun(team.Id, queued.Id)!;
            Assert.Equal(6, persisted.ExecutionResults!.Count);
            Assert.Equal(report.Id, persisted.BrowserTest!.Id);
            Assert.Contains(persisted.Validation!.Diagnostics, diagnostic => diagnostic.Message.Contains("PUT returned 400"));
            Assert.Equal(cancel ? 1 : 2, corrections);
            Assert.Equal(1, browser.Calls);
            if (!cancel)
                Assert.Contains(persisted.Validation.Diagnostics, diagnostic => diagnostic.Code == "NO_EFFECTIVE_CHANGES");
            if (escalation)
            {
                Assert.Equal(new[] { "qwen2.5-coder:7b", "qwen2.5-coder:7b", "qwen3.5:9b" }, developerModels);
                Assert.Equal(3, persisted.ModelAttempts!.Count(attempt => attempt.Role == "Developer"));
                Assert.Contains(persisted.ModelAttempts!, attempt => attempt.NextModel == "qwen3.5:9b");
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("unchanged")]
    [InlineData("ambiguous")]
    [InlineData("compile-error")]
    [InlineData("legacy-compile-error")]
    [InlineData("legacy-compile-only")]
    [InlineData("rollback-conflict")]
    [InlineData("cancelled")]
    public async Task TargetedIntegrationCorrectionUsesOnlyTheDeveloperAndOneUniqueSnippet(string outcome)
    {
        var directory = NewDirectory();
        try
        {
            var legacy = outcome is "legacy-compile-error" or "legacy-compile-only";
            var compilationOnly = outcome == "legacy-compile-only";
            var options = new StudioOptions { DataDirectory = directory };
            var diagnostics = new OllamaDiagnosticStore(options, TimeProvider.System);
            LlmDiagnosticContext? capturedTraceContext = null;
            var configuration = TestSupport.Configuration() with
            {
                Models = [.. TestSupport.Configuration().Models, new()
                {
                    Id = "local-coder", Provider = "Ollama", Name = "qwen2.5-coder:7b",
                    ContextSize = 8192, MaxOutputTokens = 1536
                }]
            };
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration, new AgentRegistry(configuration), workspaces);
            const string snippet = """
                  const response = await fetch(`/api/tasks/${id}?completed=${completed}`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ title: tasks.find(task => task.id === id)?.title })
                  });
                """;
            var replacement = snippet.Replace(
                "JSON.stringify({ title: tasks.find(task => task.id === id)?.title })",
                "JSON.stringify(tasks.find(task => task.id === id)?.title)", StringComparison.Ordinal);
            var invalidReplacement = snippet.Replace(
                "JSON.stringify({ title: tasks.find(task => task.id === id)?.title })",
                "JSON.stringify({ title })", StringComparison.Ordinal);
            const string compilerError = "src/App.tsx(68,32): error TS18004: No value exists in scope for the shorthand property 'title'. Either declare one or provide an initializer.";
            var plannerCalls = 0;
            var developerCalls = 0;
            var reviewerCalls = 0;
            var enteredBuild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("Planner for an autonomous multi-step", StringComparison.Ordinal))
                {
                    plannerCalls++;
                    return Task.FromResult("""{"tasks":[{"id":"done","title":"Done","description":"Existing work","dependencies":[],"acceptanceCriteria":["Existing work remains"]}]}""");
                }
                if (system.Contains("You are the Developer", StringComparison.Ordinal))
                {
                    developerCalls++;
                    capturedTraceContext = request.DiagnosticContext;
                    Assert.Equal(compilationOnly, capturedTraceContext is not null);
                    Assert.Equal("qwen2.5-coder:7b", request.Model.Name);
                    using var input = JsonDocument.Parse(request.Messages[1].Content);
                    var source = input.RootElement.GetProperty("sourceContext").GetProperty("Content").GetString()!;
                    Assert.Contains("async function toggleTask(id: string, completed: boolean)", source);
                    Assert.Contains("const tasks = [{ id: 'one', title: 'test' }];", source);
                    Assert.Contains("if (!response.ok) throw new Error('PUT failed');", source);
                    Assert.Contains("CURRENT CODE", input.RootElement.GetProperty("sourceContext").GetProperty("label").GetString());
                    Assert.Contains("NOT verified errors", input.RootElement.GetProperty("CompilationDiagnostic").GetString());
                    Assert.Contains("FUNCTIONAL GOAL", input.RootElement.GetProperty("instruction").GetString());
                    Assert.Equal("[FromBody] string title, bool completed", input.RootElement.GetProperty("ApiSignature").GetString());
                    Assert.Equal("PUT returned HTTP 400", input.RootElement.GetProperty("HttpDiagnostic").GetString());
                    if (developerCalls > 1 || legacy)
                    {
                        Assert.Contains("TS18004", input.RootElement.GetProperty("CompilationDiagnostic").GetString());
                        Assert.Contains("JSON.stringify({ title })", input.RootElement.GetProperty("previousRejectedCandidate")
                            .GetProperty("PreviousCandidateSnippet").GetString());
                        Assert.NotEqual(Guid.Empty, input.RootElement.GetProperty("previousRejectedCandidate")
                            .GetProperty("PreviousCandidateRunId").GetGuid());
                    }
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        replacement = outcome == "unchanged" ? snippet
                            : (outcome is "compile-error" or "rollback-conflict" or "cancelled") && developerCalls == 1 ? invalidReplacement : replacement
                    }));
                }
                if (system.Contains("Reviewer", StringComparison.Ordinal))
                {
                    reviewerCalls++;
                    return Task.FromResult("""{"response":"Consistent.","requiresCorrection":false}""");
                }
                throw new InvalidOperationException("No Planner is allowed during the targeted correction.");
            });
            var commands = new Queue<LocalCommandResult>();
            if (outcome is "compile-error" or "rollback-conflict")
            {
                foreach (var command in new[] { "dotnet restore", "npm install", "dotnet build", "dotnet test" })
                    commands.Enqueue(new(command, 0, false, "Passed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                commands.Enqueue(new("npm run build", 1, false, compilerError, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            }
            foreach (var command in new[] { "dotnet restore", "npm install", "dotnet build", "dotnet test", "npm run build", "npm test" })
            {
                if (compilationOnly && command is "dotnet test" or "npm test")
                    continue;
                commands.Enqueue(new(command, 0, false, "Passed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            }
            var executionWorkspaceId = Guid.Empty;
            var executor = new FakeLocalCommandExecutor(commands, async (command, token) =>
            {
                if (command != LocalCommandKind.NpmBuild)
                    return;
                var current = await workspaces.ReadAsync(executionWorkspaceId, "frontend/src/App.tsx", token);
                var invalid = (outcome is "compile-error" or "rollback-conflict" or "cancelled") && developerCalls == 1;
                Assert.Contains(invalid ? invalidReplacement : replacement, current.Content);
                if (outcome == "cancelled")
                {
                    enteredBuild.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                if (outcome == "rollback-conflict")
                    await workspaces.ApplyReviewedChangesAsync(executionWorkspaceId, AgentFullStackTemplate.Id,
                        [new("frontend/src/App.tsx", current.Content, current.Content + "\n// concurrent-edit")], _ => true, token);
            });
            var store = new CustomAgentStore(options);
            var browser = new FakeStudioBrowserTestRunner();
            var service = new CustomAgentService(store, new FakeOllamaModelCatalog(), provider,
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions,
                studioOptions: options, localCommands: executor, browserTestRunner: browser, diagnosticStore: diagnostics);
            var team = await CreateTeamAsync(service);
            var project = await missions.CreateCustomAgentProjectAsync("Full stack", CustomAgentProjectType.FullStack, CancellationToken.None);
            executionWorkspaceId = project.Id;
            var original = AgentFullStackTemplate.Files.Single(file => file.Path == "frontend/src/App.tsx").Content;
            var app = "// outside-snippet-marker\nconst tasks = [{ id: 'one', title: 'test' }];\n" +
                "async function toggleTask(id: string, completed: boolean) {\n" + snippet +
                "\nif (!response.ok) throw new Error('PUT failed');\n}\n" + original;
            if (outcome == "ambiguous")
                app += "\n" + snippet;
            await workspaces.ApplyReviewedChangesAsync(project.Id, AgentFullStackTemplate.Id,
                [new("frontend/src/App.tsx", original, app)], _ => true, CancellationToken.None);
            var plan = (await service.CreateDevelopmentPlanAsync(team.Id, "Preserve existing work",
                new(CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!), CancellationToken.None))!;
            var done = plan.Tasks[0] with { Status = "Completed", Attempts = 3 };
            plan = plan with { Status = "Paused", Tasks = [done, done with { Id = "tests", Status = "Pending", Attempts = 0, Order = 1 }] };
            Assert.True(store.UpdateDevelopmentPlan(plan));
            var correction = new CustomAgentTargetedCorrection("frontend/src/App.tsx", snippet,
                "[FromBody] string title, bool completed", "PUT returned HTTP 400", "Send a JSON string.");
            if (legacy)
            {
                var invalidApp = app.Replace(snippet, invalidReplacement, StringComparison.Ordinal);
                await workspaces.ApplyReviewedChangesAsync(project.Id, AgentFullStackTemplate.Id,
                    [new("frontend/src/App.tsx", app, invalidApp)], _ => true, CancellationToken.None);
                var diagnostic = new CustomAgentValidationDiagnostic("frontend/src/App.tsx", "LOCAL_BUILD_OR_TEST_FAILED", compilerError);
                var validation = new CustomAgentValidationReport("Passed", "Failed", 0,
                    [diagnostic], [new(0, "Errors", [diagnostic])]);
                var previous = new CustomAgentCollaborationRun(Guid.NewGuid(), team.Id, "Fix PUT", "Failed", null,
                    plan.Workspace, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [],
                    Validation: validation, Mode: "Autonomous",
                    AppliedChanges: [new("frontend/src/App.tsx", "Previous Ollama correction", app, invalidApp)]);
                store.AddCollaborationRun(previous);
                plan = plan with
                {
                    Status = "Failed", Attempts = 10,
                    Tasks = [.. plan.Tasks, new("integration", "Fix PUT", correction.HttpDiagnostic, [done.Id],
                        [correction.ExpectedFormat], "Failed", 2, previous.Id, 1, Validation: validation)
                        { TargetedCorrection = correction }]
                };
                Assert.True(store.UpdateDevelopmentPlan(plan));
                Assert.Throws<StudioValidationException>(() => service.RetryDevelopmentTask(team.Id, plan.Id, "integration", false, true));
                Assert.Equal("Failed", store.GetDevelopmentPlan(team.Id, plan.Id)!.Status);
                service.RetryDevelopmentTask(team.Id, plan.Id, "integration", true, true,
                    compilationOnly: compilationOnly, recordOllamaTrace: compilationOnly);
            }
            else
            {
                Assert.Throws<StudioValidationException>(() => service.StartTargetedCorrection(team.Id, plan.Id, correction, false, true));
                service.StartTargetedCorrection(team.Id, plan.Id, correction, true, true);
            }
            if (outcome == "cancelled")
            {
                await enteredBuild.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var running = store.GetDevelopmentPlan(team.Id, plan.Id)!;
                Assert.True(service.CancelCollaboration(team.Id, running.Tasks[2].CollaborationRunId!.Value));
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (service.GetDevelopmentPlan(team.Id, plan.Id)!.Status == "Running")
                await Task.Delay(20, timeout.Token);
            var saved = new CustomAgentStore(options).GetDevelopmentPlan(team.Id, plan.Id)!;
            var succeeded = outcome == "changed" || legacy;
            Assert.True(saved.Status == (succeeded ? "Paused" : "Failed"), saved.Error);
            Assert.Equal(1, plannerCalls);
            Assert.Equal(outcome == "ambiguous" ? 0 : 1, developerCalls);
            Assert.Equal(JsonSerializer.Serialize(done), JsonSerializer.Serialize(saved.Tasks[0]));
            Assert.Equal("Pending", saved.Tasks[1].Status);
            Assert.Equal(succeeded ? "Completed" : "Failed", saved.Tasks[2].Status);
            Assert.Equal(legacy ? 2 : 1, saved.Tasks[2].Attempts);
            Assert.Equal(legacy ? 11 : 1, saved.Attempts);
            Assert.Equal(succeeded ? app.Replace(snippet, replacement, StringComparison.Ordinal)
                    : outcome == "rollback-conflict" ? app.Replace(snippet, invalidReplacement, StringComparison.Ordinal) + "\n// concurrent-edit" : app,
                (await workspaces.ReadAsync(project.Id, "frontend/src/App.tsx", CancellationToken.None)).Content);
            Assert.Equal(succeeded && !compilationOnly ? 1 : 0, reviewerCalls);
            if (compilationOnly)
            {
                Assert.Equal(0, browser.Calls);
                Assert.Equal(new[] { LocalCommandKind.DotNetRestore, LocalCommandKind.NpmInstall,
                    LocalCommandKind.DotNetBuild, LocalCommandKind.NpmBuild }, executor.Commands);
                var trace = Assert.Single(Directory.GetFiles(Path.Combine(directory, "ollama-traces"), "*.json"));
                using var json = JsonDocument.Parse(File.ReadAllBytes(trace));
                Assert.Equal(saved.Tasks[2].CollaborationRunId, json.RootElement.GetProperty("context").GetProperty("runId").GetGuid());
                Assert.Equal("integration", json.RootElement.GetProperty("context").GetProperty("taskId").GetString());
                Assert.Equal(capturedTraceContext!.AttemptId, json.RootElement.GetProperty("context").GetProperty("attemptId").GetGuid());
                Assert.True(json.RootElement.GetProperty("applied").GetBoolean());
                Assert.Equal("RunSucceeded", json.RootElement.GetProperty("outcome").GetString());
            }
            if (outcome == "cancelled")
            {
                var cancelled = new CustomAgentStore(options).GetCollaborationRun(team.Id, saved.Tasks[2].CollaborationRunId!.Value)!;
                Assert.Equal("Cancelled", cancelled.Status);
                Assert.NotNull(cancelled.ChangesRolledBackAt);
                Assert.Equal(4, cancelled.ExecutionResults!.Count);
                Assert.Contains(cancelled.Validation!.Diagnostics, diagnostic => diagnostic.Code == "INITIAL_HTTP_FAILURE");
            }
            if (outcome == "rollback-conflict")
            {
                var failedRun = new CustomAgentStore(options).GetCollaborationRun(team.Id, saved.Tasks[2].CollaborationRunId!.Value)!;
                Assert.Null(failedRun.ChangesRolledBackAt);
                Assert.Contains(failedRun.Validation!.Diagnostics, diagnostic => diagnostic.Code == "ROLLBACK_FAILED");
                Assert.Contains(failedRun.Validation.Diagnostics, diagnostic => diagnostic.Message.Contains("TS18004"));
                Assert.Contains(failedRun.Validation.Diagnostics, diagnostic => diagnostic.Code == "INITIAL_HTTP_FAILURE");
                Assert.Contains("Rollback failed", failedRun.Error);
                Assert.Throws<StudioValidationException>(() =>
                    service.RetryDevelopmentTask(team.Id, plan.Id, saved.Tasks[2].Id, true, true));
                Assert.Equal(1, developerCalls);
            }
            if (outcome == "compile-error")
            {
                var failedRun = store.GetCollaborationRun(team.Id, saved.Tasks[2].CollaborationRunId!.Value)!;
                Assert.NotNull(failedRun.ChangesRolledBackAt);
                Assert.Equal("Failed", failedRun.Validation!.CompilationStatus);
                Assert.Contains(failedRun.Validation.Diagnostics, diagnostic => diagnostic.Message.Contains("TS18004"));
                Assert.Contains(failedRun.Validation.Diagnostics, diagnostic => diagnostic.Code == "INITIAL_HTTP_FAILURE" &&
                    diagnostic.Message == correction.HttpDiagnostic);
                Assert.Contains(failedRun.Validation.History, attempt =>
                    attempt.Diagnostics.Any(diagnostic => diagnostic.Message.Contains("TS18004")));
                Assert.Equal(compilerError, failedRun.ExecutionResults!.Last().Output);
                Assert.Contains("JSON.stringify({ title })", Assert.Single(failedRun.AppliedChanges!).After);
                Assert.Equal(5, executor.Commands.Count);
                await service.RestoreAutonomousChangesAsync(team.Id, failedRun.Id, CancellationToken.None);
                Assert.Equal(app, (await workspaces.ReadAsync(project.Id, "frontend/src/App.tsx", CancellationToken.None)).Content);
                Assert.Throws<StudioValidationException>(() =>
                    service.RetryDevelopmentTask(team.Id, plan.Id, saved.Tasks[2].Id, false, true));
                Assert.Equal("Failed", store.GetDevelopmentPlan(team.Id, plan.Id)!.Status);
                service.RetryDevelopmentTask(team.Id, plan.Id, saved.Tasks[2].Id, true, true);
                while (service.GetDevelopmentPlan(team.Id, plan.Id)!.Status == "Running")
                    await Task.Delay(20, timeout.Token);
                var resumed = new CustomAgentStore(options).GetDevelopmentPlan(team.Id, plan.Id)!;
                Assert.True(resumed.Status == "Paused", resumed.Error);
                Assert.Equal(2, resumed.Attempts);
                Assert.Equal(2, resumed.Tasks[2].Attempts);
                Assert.Equal(2, developerCalls);
                Assert.Equal(1, plannerCalls);
                Assert.Equal(1, reviewerCalls);
                Assert.Equal(JsonSerializer.Serialize(done), JsonSerializer.Serialize(resumed.Tasks[0]));
                Assert.Equal("Pending", resumed.Tasks[1].Status);
                Assert.Equal(app.Replace(snippet, replacement, StringComparison.Ordinal),
                    (await workspaces.ReadAsync(project.Id, "frontend/src/App.tsx", CancellationToken.None)).Content);
                var preserved = new CustomAgentStore(options).GetCollaborationRun(team.Id, failedRun.Id)!;
                Assert.Equal(JsonSerializer.Serialize(failedRun), JsonSerializer.Serialize(preserved));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TeamCannotOverwriteExistingFileOmittedFromModelContext()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Update the requested module."}""");
                if (system.Contains("You are the Developer", StringComparison.Ordinal))
                    return Task.FromResult("""{"files":[{"operation":"update","path":"src/z-last.ts","content":"export const value = 'replaced'"}]}""");
                throw new InvalidOperationException("Unexpected collaboration stage.");
            });
            var service = new CustomAgentService(new CustomAgentStore(options), new FakeOllamaModelCatalog(),
                provider, new AgentRegistry(configuration), TimeProvider.System, workspaces, missions,
                new ProjectAnalyzer(options));
            var team = await CreateTeamAsync(service);
            var project = await missions.CreateCustomAgentProjectAsync(
                "Existing files", CustomAgentProjectType.React, CancellationToken.None);
            var changes = Enumerable.Range(1, 5)
                .Select(index => new ReviewedWorkspaceChange($"src/a{index}.ts", null, $"export const a{index} = {index}"))
                .Append(new ReviewedWorkspaceChange("src/z-last.ts", null, "export const value = 'manual'"))
                .ToArray();
            await workspaces.ApplyReviewedChangesAsync(project.Id, AgentReactTemplate.Id, changes,
                _ => true, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);
            var queued = service.StartCollaboration(team.Id, "Update the module.", workspace, null)!;
            var completed = await WaitForRunAsync(service, team.Id, queued.Id);

            Assert.Equal("Failed", completed.Status);
            Assert.Contains("not included in the model context", completed.Error);
            Assert.Equal("export const value = 'manual'",
                (await workspaces.ReadAsync(project.Id, "src/z-last.ts", CancellationToken.None)).Content);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReactTeamCorrectsStaticImportErrorAndPersistsDiagnosticsBeforeApproval()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Update the app and add its imported module.","changes":[]}""");
                if (system.Contains("You are the Developer making a minimal correction.", StringComparison.Ordinal))
                {
                    Assert.Contains("LOCAL_IMPORT_MISSING", request.Messages[1].Content);
                    Assert.Contains("export function addTask(tasks: Task[], _title: string)", system);
                    return Task.FromResult("""{"response":"Added the missing panel module.","changes":[{"operation":"create","path":"src/Panel.tsx","summary":"Add the imported panel.","content":"export default function Panel() { return null }"}]}""");
                }
                if (system.Contains("read-only Reviewer", StringComparison.Ordinal))
                {
                    Assert.Contains("Passed", request.Messages[1].Content);
                    return Task.FromResult("""{"response":"The corrected local import resolves.","requiresCorrection":false}""");
                }
                if (system.Contains("You are the Developer.", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Updated the app to use a panel.","changes":[{"operation":"modify","path":"src/App.tsx","summary":"Import the panel.","content":"import Panel from './Panel'; export default Panel;"}]}""");
                throw new InvalidOperationException("Unexpected collaboration stage.");
            });
            var service = new CustomAgentService(new CustomAgentStore(options),
                new FakeOllamaModelCatalog(), provider, new AgentRegistry(configuration),
                TimeProvider.System, workspaces, missions);
            var planner = await service.CreateAgentAsync(
                new("Planner", "", "Plan.", "qwen3:4b"), CancellationToken.None);
            var developer = await service.CreateAgentAsync(
                new("Developer", "", "Implement.", "qwen2.5-coder:7b"), CancellationToken.None);
            var reviewer = await service.CreateAgentAsync(
                new("Reviewer", "", "Review.", "qwen3:4b"), CancellationToken.None);
            var team = service.SaveTeam(null, new("React team",
                [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)]))!;
            var project = await missions.CreateCustomAgentProjectAsync(
                "React task app", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);

            var queued = service.StartCollaboration(team.Id, "Add a panel.", workspace, null)!;
            var completed = await WaitForRunAsync(service, team.Id, queued.Id);

            Assert.Equal("AwaitingApproval", completed.Status);
            Assert.Equal(1, completed.Validation!.CorrectionAttempts);
            Assert.Equal(new[] { "Errors", "Passed" }, completed.Validation.History.Select(item => item.Status));
            Assert.Contains(completed.Validation.History[0].Diagnostics,
                diagnostic => diagnostic.Code == "LOCAL_IMPORT_MISSING");
            Assert.Equal(new[] { "src/App.tsx", "src/Panel.tsx" },
                completed.Proposal!.Changes.Select(change => change.Path).Order(StringComparer.Ordinal));
            Assert.Equal("modify", completed.Proposal.Changes.Single(change => change.Path == "src/App.tsx").Operation);
            Assert.Equal("create", completed.Proposal.Changes.Single(change => change.Path == "src/Panel.tsx").Operation);
            Assert.DoesNotContain(workspaces.List(project.Id), file => file.Path == "src/Panel.tsx");

            var restarted = new CustomAgentStore(options).GetCollaborationRun(team.Id, completed.Id)!;
            Assert.Equal(completed.Validation!.StaticStatus, restarted.Validation!.StaticStatus);
            Assert.Equal(completed.Validation.CorrectionAttempts, restarted.Validation.CorrectionAttempts);
            Assert.Equal(completed.Validation.History.Select(attempt => attempt.Status),
                restarted.Validation.History.Select(attempt => attempt.Status));
            Assert.Equal(completed.Validation.History.SelectMany(attempt => attempt.Diagnostics)
                    .Select(diagnostic => diagnostic.Code),
                restarted.Validation.History.SelectMany(attempt => attempt.Diagnostics)
                    .Select(diagnostic => diagnostic.Code));
            Assert.Equal("Pending", restarted.Proposal!.Status);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReactTeamIgnoresAndReportsChangesToProtectedTemplateTests()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var protectedTest = AgentReactTemplate.Files.Single(file => file.Path == "src/App.test.tsx").Content;
            var unchangedTasks = AgentReactTemplate.Files.Single(file => file.Path == "src/tasks.ts").Content;
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Implement the requested task UI.","changes":[]}""");
                if (system.Contains("read-only Reviewer", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"The app change is consistent.","requiresCorrection":false}""");
                if (system.Contains("You are the Developer", StringComparison.Ordinal))
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        response = "Updated the task UI and its tests.",
                        files = new[]
                        {
                            new
                            {
                                operation = "update", path = "src/App.test.tsx",
                                content = protectedTest + "\n// model test change"
                            },
                            new
                            {
                                operation = "update", path = "src/tasks.ts", content = unchangedTasks
                            },
                            new
                            {
                                operation = "update", path = "src/App.tsx",
                                content = "export default function App() { return <main>Tasks</main> }"
                            }
                        }
                    }));
                throw new InvalidOperationException("Unexpected collaboration stage.");
            });
            var service = new CustomAgentService(new CustomAgentStore(options),
                new FakeOllamaModelCatalog(), provider, new AgentRegistry(configuration),
                TimeProvider.System, workspaces, missions);
            var planner = await service.CreateAgentAsync(
                new("Planner", "", "Plan.", "qwen3:4b"), CancellationToken.None);
            var developer = await service.CreateAgentAsync(
                new("Developer", "", "Implement.", "qwen2.5-coder:7b"), CancellationToken.None);
            var reviewer = await service.CreateAgentAsync(
                new("Reviewer", "", "Review.", "qwen3:4b"), CancellationToken.None);
            var team = service.SaveTeam(null, new("Protected test team",
                [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)]))!;
            var project = await missions.CreateCustomAgentProjectAsync(
                "Protected React tests", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);

            var queued = service.StartCollaboration(team.Id, "Create a task UI.", workspace, null)!;
            var completed = await WaitForRunAsync(service, team.Id, queued.Id);

            Assert.Equal("AwaitingApproval", completed.Status);
            Assert.Equal(new[] { "src/App.tsx" }, completed.Proposal!.Changes.Select(change => change.Path));
            Assert.Contains("Protected file operations were ignored: src/App.test.tsx",
                completed.Contributions.Single(item => item.Role == "Developer").Content);
            Assert.Contains("No-op file operations were ignored: src/tasks.ts",
                completed.Contributions.Single(item => item.Role == "Developer").Content);
            Assert.Equal(protectedTest,
                (await workspaces.ReadAsync(project.Id, "src/App.test.tsx", CancellationToken.None)).Content);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutonomousRunExecutesBuildCorrectsFailurePersistsAndRestoresFiles(bool helperTestsAlsoFailed)
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory, MaxCorrectionAttempts = 3 };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var calls = new Queue<LocalCommandResult>(
            [
                new("npm install --no-audit --no-fund", 0, false, "installed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new("npm run build", 0, false, "built", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new("npm test", 1, false,
                    helperTestsAlsoFailed
                        ? " FAIL  src/App.test.tsx > adds and deletes a real task\n" +
                          "TestingLibraryElementError: Unable to find checkbox Alpha\n" +
                          new string('x', 1600) + "\n ❯ src/App.test.tsx:12:17\n" +
                          " FAIL  src/tasks.test.ts > toggles completion\n" +
                          "AssertionError: expected false to be true\n ❯ src/tasks.test.ts:25:32\n"
                        : "src/App.tsx(8,48): error TS2345: Argument of type 'string' is not assignable to parameter of type 'Filter'.",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new("npm run build", 0, false, "built", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new("npm test", 0, false, "tests passed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]);
            var executor = new FakeLocalCommandExecutor(calls);
            string? correctionSystem = null;
            string? correctionRequest = null;
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Update the task application and validate it.","changes":[]}""");
                if (system.Contains("You are the Developer making a minimal correction.", StringComparison.Ordinal))
                {
                    correctionSystem = system;
                    correctionRequest = request.Messages[1].Content;
                    return Task.FromResult("""{"files":[{"operation":"update","path":"src/App.tsx","content":"export default function App() { return <main><h1>Tasks</h1></main> }"}]}""");
                }
                if (system.Contains("You are the Developer.", StringComparison.Ordinal))
                {
                    Assert.True(request.ResponseSchema!.Value.GetProperty("properties")
                        .TryGetProperty("files", out var filesSchema));
                    Assert.Equal("array", filesSchema.GetProperty("type").GetString());
                    return Task.FromResult("""{"files":[{"operation":"update","path":"src/App.tsx","content":"export default function App() { return <main><h1>Task app</h1></main> }"}]}""");
                }
                if (system.Contains("Reviewer", StringComparison.Ordinal))
                {
                    Assert.Equal(5, executor.Commands.Count);
                    return Task.FromResult("""{"response":"The task view and build/test results are consistent.","requiresCorrection":false}""");
                }
                throw new InvalidOperationException("Unexpected collaboration stage.");
            });
            var store = new CustomAgentStore(options);
            var service = new CustomAgentService(store, new FakeOllamaModelCatalog(), provider,
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions,
                new ProjectAnalyzer(options), studioOptions: options, localCommands: executor,
                browserTestRunner: new FakeStudioBrowserTestRunner());
            var planner = await service.CreateAgentAsync(
                new("Planner", "", "Plan.", "qwen3:4b"), CancellationToken.None);
            var developer = await service.CreateAgentAsync(
                new("Developer", "", "Implement.", "qwen2.5-coder:7b"), CancellationToken.None);
            var reviewer = await service.CreateAgentAsync(
                new("Reviewer", "", "Review.", "qwen3:4b"), CancellationToken.None);
            var team = service.SaveTeam(null, new("Autonomous React team",
                [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)]))!;
            var project = await missions.CreateCustomAgentProjectAsync(
                "Trusted task app", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);
            var original = AgentReactTemplate.Files.Single(file => file.Path == "src/App.tsx").Content;

            var queued = service.StartCollaboration(team.Id, "Create a task view.", workspace, null,
                "Autonomous", workspaceAuthorized: true, executionAuthorized: true)!;
            var completed = await WaitForRunAsync(service, team.Id, queued.Id);

            Assert.True(completed.Status == "Completed", completed.Error);
            Assert.Equal("Autonomous", completed.Mode);
            Assert.Equal("Passed", completed.Validation!.CompilationStatus);
            Assert.Equal(1, completed.Validation.CorrectionAttempts);
            Assert.Contains("LOCAL_BUILD_OR_TEST_FAILED", correctionRequest);
            if (helperTestsAlsoFailed)
            {
                Assert.Contains("src/tasks.test.ts", correctionRequest);
                Assert.Contains("Unable to find checkbox Alpha", correctionRequest);
                Assert.Contains("adds and deletes a real task", correctionRequest);
                Assert.Contains("expected false to be true", correctionRequest);
                Assert.Contains("toggles completion", correctionRequest);
                Assert.Contains("src/App.test.tsx:12:17", correctionRequest);
                Assert.Contains("src/tasks.test.ts:25:32", correctionRequest);
                Assert.Contains("src/App.test.tsx", correctionSystem);
                Assert.Contains("src/tasks.test.ts", correctionSystem);
                Assert.Contains("Fix src/tasks.ts directly", correctionSystem);
                Assert.Contains("If the React UI tests also fail", correctionSystem);
            }
            else
            {
                Assert.Contains("TS2345", correctionRequest);
                Assert.Contains("validate the value before calling useState<Filter>", correctionSystem);
                Assert.Contains("Do not cast an arbitrary string to Filter", correctionSystem);
            }
            Assert.Equal(5, completed.ExecutionResults!.Count);
            Assert.Contains(completed.Contributions, item => item.Role == "Planner");
            Assert.Contains(completed.Contributions, item => item.Role == "Developer");
            Assert.Contains(completed.Contributions, item => item.Role == "Reviewer");
            Assert.Contains("Tasks", (await workspaces.ReadAsync(project.Id, "src/App.tsx", CancellationToken.None)).Content);
            Assert.Equal(5, executor.Commands.Count);

            var persisted = new CustomAgentStore(options).GetCollaborationRun(team.Id, completed.Id)!;
            Assert.Equal(completed.ExecutionResults, persisted.ExecutionResults);
            var restored = await service.RestoreAutonomousChangesAsync(team.Id, completed.Id, CancellationToken.None);
            Assert.Equal("Restored", restored!.Status);
            Assert.Equal(original, (await workspaces.ReadAsync(
                project.Id, "src/App.tsx", CancellationToken.None)).Content);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AutonomousRunSendsBrowserFailureToDeveloperAndRetestsTheCorrection()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory, MaxCorrectionAttempts = 3 };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var now = DateTimeOffset.UtcNow;
            var executor = new FakeLocalCommandExecutor(new Queue<LocalCommandResult>(
            [
                new("npm install --no-audit --no-fund", 0, false, "installed", now, now),
                new("npm run build", 0, false, "built", now, now),
                new("npm test", 0, false, "passed", now, now),
                new("npm run build", 0, false, "built", now, now),
                new("npm test", 0, false, "passed", now, now)
            ]));
            var scenarioId = Guid.NewGuid().ToString("N");
            var browserTests = new FakeStudioBrowserTestRunner(new Queue<CustomAgentBrowserTestReport>(
            [
                new(Guid.NewGuid(), "Failed", "http://127.0.0.1:5173/", now, 0, 1, null,
                    [new(scenarioId, "Task appears", "Failed", "2. expectVisible",
                        "The task did not appear.", [], null)]),
                new(Guid.NewGuid(), "Passed", "http://127.0.0.1:5173/", now, 1, 0, null,
                    [new(scenarioId, "Task appears", "Passed", null, null, [], null)])
            ]));
            var developerCorrections = 0;
            string? correctionSystem = null;
            string? correctionRequest = null;
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""
                        {"response":"Create a visible task heading.",
                         "browserScenarios":[{"name":"Task appears","path":"/","steps":[
                           {"action":"expectVisible","locatorType":"text","locator":"Task manager",
                            "value":null,"role":null,"index":null}]}]}
                        """);
                if (system.Contains("You are the Developer making a minimal correction.", StringComparison.Ordinal))
                {
                    developerCorrections++;
                    correctionSystem = system;
                    correctionRequest = request.Messages[1].Content;
                    return Task.FromResult("""
                        {"files":[{"path":"src/App.tsx","operation":"update",
                          "content":"export default function App() { return <main><h1>Task manager</h1></main> }"}]}
                        """);
                }
                if (system.Contains("You are the Developer.", StringComparison.Ordinal))
                    return Task.FromResult("""
                        {"files":[{"path":"src/App.tsx","operation":"update",
                          "content":"export default function App() { return <main><p>Task manager</p></main> }"}]}
                        """);
                if (system.Contains("You are the final read-only Reviewer", StringComparison.Ordinal))
                {
                    Assert.Equal(5, executor.Commands.Count);
                    Assert.Contains("Passed", request.Messages[1].Content);
                    Assert.DoesNotContain("\"exitCode\":2", request.Messages[1].Content);
                    Assert.DoesNotContain("\"exitCode\":1", request.Messages[1].Content);
                    return Task.FromResult("""{"response":"The task heading is visible and browser verification passed.","requiresCorrection":false}""");
                }
                throw new InvalidOperationException("Unexpected autonomous stage.");
            });
            var service = new CustomAgentService(new CustomAgentStore(options),
                new FakeOllamaModelCatalog(), provider, new AgentRegistry(configuration),
                TimeProvider.System, workspaces, missions, new ProjectAnalyzer(options),
                studioOptions: options, localCommands: executor, browserTestRunner: browserTests);
            var team = await CreateTeamAsync(service);
            var project = await missions.CreateCustomAgentProjectAsync(
                "Browser correction", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);

            var queued = service.StartCollaboration(team.Id, "Create a visible task heading.", workspace, null,
                "Autonomous", workspaceAuthorized: true, executionAuthorized: true)!;
            var completed = await WaitForRunAsync(service, team.Id, queued.Id);

            Assert.True(completed.Status == "Completed", completed.Error);
            Assert.Equal(1, developerCorrections);
            Assert.Contains("BROWSER_FUNCTIONAL_TEST_FAILED", correctionRequest);
            Assert.Contains("Do not add package dependencies", correctionSystem);
            Assert.Contains("global crypto.randomUUID()", correctionSystem);
            Assert.Contains("persist every task update so added tasks survive reloads", correctionSystem);
            Assert.Contains("found the new task before reload but not after reload", correctionSystem);
            using var correctionInput = JsonDocument.Parse(correctionRequest!);
            var availablePaths = correctionInput.RootElement.GetProperty("availableWorkspacePaths")
                .EnumerateArray().Select(path => path.GetString()).ToArray();
            Assert.Contains("src/App.tsx", availablePaths);
            Assert.DoesNotContain("src/App.test.tsx", availablePaths);
            Assert.Equal(1, completed.Validation!.CorrectionAttempts);
            Assert.Equal(new[] { "Failed", "Passed" },
                completed.BrowserTestHistory!.Select(report => report.Status));
            Assert.Equal("Passed", new CustomAgentStore(options)
                .GetCollaborationRun(team.Id, completed.Id)!.BrowserTest!.Status);
            Assert.Equal(2, browserTests.Calls);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AutonomousRunRejectsInvalidPlannerBrowserScenariosAndUsesBuiltInChecks()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var now = DateTimeOffset.UtcNow;
            var executor = new FakeLocalCommandExecutor(new Queue<LocalCommandResult>(
            [
                new("npm install --no-audit --no-fund", 0, false, "installed", now, now),
                new("npm run build", 0, false, "built", now, now),
                new("npm test", 0, false, "passed", now, now)
            ]));
            var browserTests = new FakeStudioBrowserTestRunner();
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""
                        {"response":"Implement a task screen.",
                         "browserScenarios":[{"name":"Add task","path":"/","steps":[
                           {"action":"fill","locatorType":"label","locator":"Nouvelle tâche",
                            "value":null,"role":null,"index":null}]}]}
                        """);
                if (system.Contains("You are the Developer.", StringComparison.Ordinal))
                    return Task.FromResult("""
                        {"files":[{"path":"src/App.tsx","operation":"update",
                          "content":"export default function App() { return <main><h1>Tasks</h1></main> }"}]}
                        """);
                if (system.Contains("You are the final read-only Reviewer", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Build, tests, and browser checks passed.","requiresCorrection":false}""");
                throw new InvalidOperationException("Unexpected autonomous stage.");
            });
            var store = new CustomAgentStore(options);
            var service = new CustomAgentService(store, new FakeOllamaModelCatalog(), provider,
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions,
                new ProjectAnalyzer(options), studioOptions: options, localCommands: executor,
                browserTestRunner: browserTests);
            var team = await CreateTeamAsync(service);
            var project = await missions.CreateCustomAgentProjectAsync(
                "Invalid browser plan", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);

            var queued = service.StartCollaboration(team.Id, "Create a task screen.", workspace, null,
                "Autonomous", workspaceAuthorized: true, executionAuthorized: true)!;
            var completed = await WaitForRunAsync(service, team.Id, queued.Id);

            Assert.Equal("Completed", completed.Status);
            Assert.Empty(completed.BrowserScenarios!);
            Assert.Contains("scénario navigateur invalide", completed.Contributions
                .Single(item => item.Role == "Planner").Content);
            Assert.Equal(1, browserTests.Calls);
            Assert.Empty(browserTests.LastScenarios!);
            Assert.Equal("Passed", new CustomAgentStore(options)
                .GetCollaborationRun(team.Id, completed.Id)!.BrowserTest!.Status);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AutonomousRunRequiresSeparateWorkspaceAndExecutionAuthorization()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var service = new CustomAgentService(new CustomAgentStore(options),
                new FakeOllamaModelCatalog(), new FakeProvider((_, _) => Task.FromResult("unused")),
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions,
                new ProjectAnalyzer(options), studioOptions: options, localCommands: new FakeLocalCommandExecutor(new([])));
            var team = await CreateTeamAsync(service);
            var project = await missions.CreateCustomAgentProjectAsync(
                "Trusted task app", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);

            Assert.Throws<StudioValidationException>(() => service.StartCollaboration(
                team.Id, "Create a task view.", workspace, null, "Autonomous",
                workspaceAuthorized: false, executionAuthorized: true));
            Assert.Throws<StudioValidationException>(() => service.StartCollaboration(
                team.Id, "Create a task view.", workspace, null, "Autonomous",
                workspaceAuthorized: true, executionAuthorized: false));
            Assert.Empty(service.ListCollaborationRuns(team.Id));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LocalExecutorTimesOutAndTerminatesChildProcesses()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var node = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
            .Select(path => Path.Combine(path, "node.exe"))
            .FirstOrDefault(File.Exists);
        if (node is null) return;

        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory, MissionTimeoutSeconds = 10 };
            var workspaces = new WorkspaceStore(options);
            var workspaceId = Guid.NewGuid();
            workspaces.Create(workspaceId);
            var workspace = workspaces.GetDirectory(workspaceId);
            var script = Path.Combine(workspace, "hang.cjs");
            await File.WriteAllTextAsync(script, """
                const { spawn } = require('node:child_process')
                const child = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 60000)'], { stdio: 'ignore' })
                require('node:fs').writeFileSync('childpid.txt', String(child.pid))
                console.log(`CHILD_PID=${child.pid}`)
                setTimeout(() => {}, 60000)
                """);
            var executor = new LocalCommandExecutor(workspaces, options, desktopEnabled: true);
            ProcessStartInfo CreateStartInfo() => new()
            {
                FileName = node,
                Arguments = $"\"{script}\"",
                WorkingDirectory = workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var result = await executor.RunProcessAsync(
                CreateStartInfo(), "node hang.cjs", CancellationToken.None);

            Assert.True(result.TimedOut);
            Assert.Null(result.ExitCode);
            var marker = "CHILD_PID=";
            var index = result.Output.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index >= 0, result.Output);
            var pidText = result.Output[(index + marker.Length)..].Split(
                new[] { '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries).First();
            Assert.True(int.TryParse(pidText, out var childPid));
            var childStopped = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    using var child = Process.GetProcessById(childPid);
                    childStopped = child.HasExited;
                }
                catch (ArgumentException)
                {
                    childStopped = true;
                }
                if (childStopped) break;
                await Task.Delay(100);
            }
            Assert.True(childStopped, $"Child process {childPid} survived the Job Object timeout.");

            var cancellationFile = Path.Combine(workspaces.GetDirectory(workspaceId), "childpid.txt");
            File.Delete(cancellationFile);
            using var cancellation = new CancellationTokenSource();
            var runningCommand = executor.RunProcessAsync(
                CreateStartInfo(), "node hang.cjs", cancellation.Token);
            for (var attempt = 0; attempt < 100 && !File.Exists(cancellationFile); attempt++)
                await Task.Delay(100);
            Assert.True(File.Exists(cancellationFile), "The child process did not start before cancellation.");
            var cancelledChildPid = int.Parse(await File.ReadAllTextAsync(cancellationFile));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runningCommand);
            await AssertProcessStoppedAsync(cancelledChildPid);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LocalApplicationRequiresSeparateExecutionAuthorization()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var executor = new LocalCommandExecutor(new WorkspaceStore(options), options, desktopEnabled: true);
            await Assert.ThrowsAsync<StudioValidationException>(() => executor.StartApplicationAsync(
                Guid.NewGuid(), CustomAgentProjectType.React, executionAuthorized: false, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RealLocalApplication")]
    public async Task RealReactAndDotNetApplicationsStartRespondAndStopInTemporaryWorkspaces()
    {
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(Environment.GetEnvironmentVariable("AIPLATFORM_RUN_REAL_LOCAL_APPLICATION"), "true",
                StringComparison.OrdinalIgnoreCase))
            return;

        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        LocalCommandExecutor? executor = null;
        try
        {
            var options = new StudioOptions { DataDirectory = directory, MissionTimeoutSeconds = 300 };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration, new AgentRegistry(configuration), workspaces);
            executor = new LocalCommandExecutor(workspaces, options, desktopEnabled: true);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(4)
            };

            var react = await missions.CreateCustomAgentProjectAsync(
                "Local React smoke test", CustomAgentProjectType.React, CancellationToken.None);
            var authorizationId = executor.Authorize(react.Id);
            TcpListener? occupiedPort = null;
            try
            {
                var install = await executor.RunAsync(authorizationId, react.Id, CustomAgentProjectType.React,
                    LocalCommandKind.NpmInstall, CancellationToken.None);
                Assert.True(install.Succeeded, install.Output);
                var build = await executor.RunAsync(authorizationId, react.Id, CustomAgentProjectType.React,
                    LocalCommandKind.NpmBuild, CancellationToken.None);
                Assert.True(build.Succeeded, build.Output);

                try
                {
                    occupiedPort = new TcpListener(IPAddress.Loopback, 5173);
                    occupiedPort.Start();
                }
                catch (SocketException)
                {
                    occupiedPort?.Stop();
                    occupiedPort = null;
                }
                var started = await executor.StartApplicationAsync(
                    react.Id, CustomAgentProjectType.React, executionAuthorized: true, CancellationToken.None);
                var duplicate = await executor.StartApplicationAsync(
                    react.Id, CustomAgentProjectType.React, executionAuthorized: true, CancellationToken.None);
                Assert.Equal(started.ProcessId, duplicate.ProcessId);
                var ready = await WaitForLocalApplicationAsync(executor, react.Id, CustomAgentProjectType.React);
                var reactUrl = new Uri(Assert.IsType<string>(ready.Url));
                if (occupiedPort is not null)
                {
                    Assert.NotEqual(5173, reactUrl.Port);
                    occupiedPort.Stop();
                    occupiedPort = null;
                }
                testOutput.WriteLine($"React server ready at {reactUrl}; command: {ready.Command}");
                Assert.True(reactUrl.IsLoopback, reactUrl.ToString());
                using (var response = await http.GetAsync(reactUrl))
                    Assert.True((int)response.StatusCode < 500, $"React server returned HTTP {(int)response.StatusCode}.");
                var reactPid = Assert.IsType<int>(ready.ProcessId);

                var stopped = await executor.StopApplicationAsync(react.Id, CancellationToken.None);
                Assert.Equal("Stopped", stopped?.Status);
                await AssertProcessStoppedAsync(reactPid);
                await AssertLocalUrlUnavailableAsync(http, reactUrl);
                testOutput.WriteLine("React server process stopped and its URL no longer responded.");

                var startingAgain = await executor.StartApplicationAsync(
                    react.Id, CustomAgentProjectType.React, executionAuthorized: true, CancellationToken.None);
                var cancelledStart = await executor.StopApplicationAsync(react.Id, CancellationToken.None);
                Assert.Equal("Stopped", cancelledStart?.Status);
                if (startingAgain.ProcessId is { } cancelledPid)
                    await AssertProcessStoppedAsync(cancelledPid);
                occupiedPort?.Stop();
            }
            finally
            {
                occupiedPort?.Stop();
                executor.Revoke(authorizationId);
            }

            var dotnet = await missions.CreateCustomAgentProjectAsync(
                "Local .NET smoke test", CustomAgentProjectType.DotNet, CancellationToken.None);
            await executor.StartApplicationAsync(
                dotnet.Id, CustomAgentProjectType.DotNet, executionAuthorized: true, CancellationToken.None);
            var dotnetReady = await WaitForLocalApplicationAsync(executor, dotnet.Id, CustomAgentProjectType.DotNet);
            var dotnetUrl = new Uri(Assert.IsType<string>(dotnetReady.Url));
            testOutput.WriteLine($".NET server ready at {dotnetUrl}; command: {dotnetReady.Command}");
            Assert.True(dotnetUrl.IsLoopback, dotnetUrl.ToString());
            var dotnetHealthUrl = new Uri(dotnetUrl, "/health");
            using (var response = await http.GetAsync(dotnetHealthUrl))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await executor.StopAsync(CancellationToken.None);
            Assert.Equal("Stopped", executor.GetApplication(dotnet.Id, CustomAgentProjectType.DotNet).Status);
            await AssertLocalUrlUnavailableAsync(http, dotnetHealthUrl);
            if (dotnetReady.ProcessId is { } dotnetPid)
                await AssertProcessStoppedAsync(dotnetPid);
        }
        finally
        {
            if (executor is not null)
                await executor.StopAsync(CancellationToken.None);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<LocalApplicationSnapshot> WaitForLocalApplicationAsync(
        LocalCommandExecutor executor, Guid workspaceId, CustomAgentProjectType projectType)
    {
        LocalApplicationSnapshot status = executor.GetApplication(workspaceId, projectType);
        for (var attempt = 0; attempt < 240 && (status.Status is "Starting" or "Stopping"); attempt++)
        {
            await Task.Delay(250);
            status = executor.GetApplication(workspaceId, projectType);
        }
        Assert.True(status.Status == "Running",
            $"Expected a running server, got {status.Status}. {status.Error}{Environment.NewLine}{string.Join(Environment.NewLine, status.Output)}");
        Assert.NotEmpty(status.Output);
        return status;
    }

    private static async Task AssertLocalUrlUnavailableAsync(HttpClient client, Uri url)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url);
            }
            catch (HttpRequestException)
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"The local application still responded after stop: {url}");
    }

    private static async Task AssertProcessStoppedAsync(int processId)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return;
            }
            catch (ArgumentException)
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"Child process {processId} survived cancellation.");
    }

    [Fact]
    [Trait("Category", "RealOllama")]
    public async Task RealOllamaAutonomousReactRunUsesOnlyATemporaryWorkspace()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AIPLATFORM_RUN_REAL_OLLAMA"), "true",
                StringComparison.OrdinalIgnoreCase))
            return;

        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            var developerModel = Environment.GetEnvironmentVariable("AIPLATFORM_TEST_DEVELOPER_MODEL");
            if (string.IsNullOrWhiteSpace(developerModel))
                developerModel = "qwen3.5:9b";
            var plannerModel = Environment.GetEnvironmentVariable("AIPLATFORM_TEST_PLANNER_MODEL");
            if (string.IsNullOrWhiteSpace(plannerModel))
                plannerModel = "qwen3:4b";
            var options = new StudioOptions { DataDirectory = directory, MaxCorrectionAttempts = 3 };
            var configuration = TestSupport.Configuration() with
            {
                Models =
                [
                    new() { Id = "general", Provider = "Ollama", Name = plannerModel, ContextSize = 4096, MaxOutputTokens = 2048 },
                    new() { Id = "developer", Provider = "Ollama", Name = developerModel, ContextSize = 4096, MaxOutputTokens = 2048 }
                ]
            };
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            using var provider = new OllamaProvider(client, new OllamaOptions
            {
                RequestTimeoutSeconds = 600,
                KeepAliveSeconds = 120,
                UnloadPreviousModel = true
            });
            var catalog = new OllamaModelCatalog(client, new OllamaOptions());
            var store = new CustomAgentStore(options);
            var nodeExecutable = Environment.GetEnvironmentVariable("ProgramFiles") is { } programFiles
                ? Path.Combine(programFiles, "nodejs", "node.exe") : "";
            if (!File.Exists(nodeExecutable))
                nodeExecutable = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
                    .Select(path => Path.Combine(path, "node.exe")).FirstOrDefault(File.Exists) ?? "";
            if (!string.IsNullOrEmpty(nodeExecutable))
            {
                var nodeDirectory = new DirectoryInfo(Path.GetDirectoryName(nodeExecutable)!);
                nodeExecutable = Path.Combine(
                    nodeDirectory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? nodeDirectory.FullName,
                    Path.GetFileName(nodeExecutable));
            }
            var helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "AIPlatform.Web", "scripts", "studio-browser-tests.mjs"));
            var browserOptions = new StudioBrowserTestOptions(nodeExecutable, helper);
            var localExecutor = new LocalCommandExecutor(workspaces, options, desktopEnabled: true, browserOptions);
            var service = new CustomAgentService(store, catalog, provider,
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions,
                new ProjectAnalyzer(options), studioOptions: options,
                localCommands: localExecutor,
                browserTestRunner: new StudioBrowserTestRunner(missions, localExecutor, options, browserOptions, true),
                logger: new TestOutputLogger<CustomAgentService>(testOutput));
            var planner = await service.CreateAgentAsync(
                new("Planner", "", "Plan concisely.", plannerModel), CancellationToken.None);
            var developer = await service.CreateAgentAsync(
                new("Developer", "", "Implement the requested application; output complete source files only.", developerModel), CancellationToken.None);
            var reviewer = await service.CreateAgentAsync(
                new("Reviewer", "", "Review correctness and report only concrete, file-specific issues.", plannerModel), CancellationToken.None);
            var team = service.SaveTeam(null, new("Temporary Ollama team",
                [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)]))!;
            var project = await missions.CreateCustomAgentProjectAsync(
                "Temporary Ollama task app", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);
            var queued = service.StartCollaboration(team.Id,
                "Crée une application React de gestion de tâches avec ajout, suppression et sauvegarde localStorage.",
                workspace, null, "Autonomous", workspaceAuthorized: true, executionAuthorized: true)!;

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            CustomAgentCollaborationRun completed;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                completed = service.GetCollaborationRun(team.Id, queued.Id)!;
                if (completed.Status is not ("Queued" or "Running"))
                    break;
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
            }

            testOutput.WriteLine(JsonSerializer.Serialize(new
            {
                completed.Status,
                completed.Error,
                validation = completed.Validation,
                browserTest = completed.BrowserTest,
                browserScenarios = completed.BrowserScenarios,
                changedFiles = completed.AppliedChanges?.Select(change => change.Path),
                plannerModel,
                developerModel,
                commands = completed.ExecutionResults?.Select(result => new
                {
                    result.Command, result.ExitCode, result.TimedOut,
                    output = result.Output[..Math.Min(result.Output.Length, 800)]
                }),
                contributions = completed.Contributions.Select(item => new
                {
                    item.Role, item.ModelName,
                    content = item.Content[..Math.Min(item.Content.Length, 2000)]
                })
            }));
            Assert.True(completed.Status == "Completed", JsonSerializer.Serialize(new
            {
                completed.Error,
                completed.Validation,
                browserTest = completed.BrowserTest,
                changedFiles = completed.AppliedChanges?.Select(change => change.Path),
                commands = completed.ExecutionResults?.Select(result => new
                {
                    result.Command, result.ExitCode, result.TimedOut,
                    output = result.Output[..Math.Min(result.Output.Length, 1200)]
                })
            }));
            Assert.Equal("Passed", completed.Validation?.CompilationStatus);
            Assert.True(completed.BrowserTest?.Status == "Passed", completed.BrowserTest?.Error);
            Assert.Contains(completed.Contributions, item => item.Role == "Planner" && item.ModelName == plannerModel);
            Assert.Contains(completed.Contributions, item => item.Role == "Developer" && item.ModelName == developerModel);
            Assert.Contains(completed.Contributions, item => item.Role == "Reviewer" && item.ModelName == plannerModel);
            var changes = completed.AppliedChanges ?? [];
            Assert.NotEmpty(changes);
            Assert.Contains(changes, change => change.Path == "src/tasks.ts");
            Assert.Contains(changes, change => change.Path == "src/App.tsx");
            Assert.Contains(changes, change => change.After?.Contains("localStorage", StringComparison.Ordinal) == true);
            Assert.Contains(completed.ExecutionResults!, result => result.Command == "npm run build" && result.ExitCode == 0);
            Assert.Contains(completed.ExecutionResults!, result => result.Command == "npm test" && result.ExitCode == 0);

            if (completed.Status is "Completed" or "Failed")
            {
                var restored = await service.RestoreAutonomousChangesAsync(team.Id, completed.Id, CancellationToken.None);
                Assert.Equal("Restored", restored!.Status);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RealOllama")]
    public async Task RealOllamaEvolvesTheSameReactProject()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AIPLATFORM_RUN_REAL_OLLAMA"), "true",
                StringComparison.OrdinalIgnoreCase))
            return;

        var resumeDirectory = Environment.GetEnvironmentVariable("AIPLATFORM_TEST_EVOLVE_DIRECTORY");
        var directory = string.IsNullOrWhiteSpace(resumeDirectory) ? NewDirectory() : Path.GetFullPath(resumeDirectory);
        Directory.CreateDirectory(directory);
        testOutput.WriteLine($"Real Ollama project data: {directory}");
        var options = new StudioOptions
        {
            DataDirectory = directory, MaxCorrectionAttempts = 3,
            MaxDevelopmentPlanAttempts = 12, MaxDevelopmentPlanTasks = 20
        };
        var configuration = TestSupport.Configuration() with
        {
            Models =
            [
                new() { Id = "general", Provider = "Ollama", Name = "qwen3:4b", ContextSize = 8192, MaxOutputTokens = 2048 },
                new() { Id = "developer", Provider = "Ollama", Name = "qwen2.5-coder:7b", ContextSize = 8192, MaxOutputTokens = 3072 }
            ]
        };
        var workspaces = new WorkspaceStore(options);
        using var missions = new MissionStore(options, configuration, new AgentRegistry(configuration), workspaces);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var provider = new OllamaProvider(client, new OllamaOptions
        {
            RequestTimeoutSeconds = 600, KeepAliveSeconds = 120, UnloadPreviousModel = true
        });
        var store = new CustomAgentStore(options);
        var node = Environment.GetEnvironmentVariable("ProgramFiles") is { } programFiles
            ? Path.Combine(programFiles, "nodejs", "node.exe") : "";
        if (!File.Exists(node))
            node = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
                .Select(path => Path.Combine(path, "node.exe")).FirstOrDefault(File.Exists) ?? "";
        if (!string.IsNullOrEmpty(node))
        {
            var parent = new DirectoryInfo(Path.GetDirectoryName(node)!);
            node = Path.Combine(parent.ResolveLinkTarget(true)?.FullName ?? parent.FullName, Path.GetFileName(node));
        }
        var helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "AIPlatform.Web", "scripts", "studio-browser-tests.mjs"));
        var browserOptions = new StudioBrowserTestOptions(node, helper);
        var executor = new LocalCommandExecutor(workspaces, options, desktopEnabled: true, browserOptions);
        var browser = new StudioBrowserTestRunner(missions, executor, options, browserOptions, true);
        var service = new CustomAgentService(store, new OllamaModelCatalog(client, new OllamaOptions()), provider,
            new AgentRegistry(configuration), TimeProvider.System, workspaces, missions,
            new ProjectAnalyzer(options), studioOptions: options, localCommands: executor,
            browserTestRunner: browser, logger: new TestOutputLogger<CustomAgentService>(testOutput));
        CustomAgentTeam team;
        MissionSnapshot project;
        CustomAgentDevelopmentPlan plan;
        if (string.IsNullOrWhiteSpace(resumeDirectory))
        {
            var planner = await service.CreateAgentAsync(
                new("Planner", "", "Create small independently verifiable tasks.", "qwen3:4b"), CancellationToken.None);
            var developer = await service.CreateAgentAsync(
                new("Developer", "", "Preserve existing behavior and implement only the requested change.", "qwen2.5-coder:7b"),
                CancellationToken.None);
            var reviewer = await service.CreateAgentAsync(
                new("Reviewer", "", "Review exact changes and validation.", "qwen3:4b"), CancellationToken.None);
            team = service.SaveTeam(null, new("Evolution test",
                [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)]))!;
            project = await missions.CreateCustomAgentProjectAsync(
                "Temporary task application", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);
            plan = (await service.CreateDevelopmentPlanAsync(team.Id,
                "Crée une application de gestion de tâches avec ajout, suppression et sauvegarde localStorage.",
                workspace, CancellationToken.None))!;
        }
        else
        {
            team = store.ListTeams().Single();
            plan = store.ListDevelopmentPlans(team.Id).Single();
            project = missions.Get(plan.Workspace.WorkspaceId!.Value)
                ?? throw new InvalidOperationException("The saved test project no longer exists.");
        }

        async Task<CustomAgentDevelopmentPlan> WaitForPlanAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(40));
            while (true)
            {
                var current = service.GetDevelopmentPlan(team.Id, plan.Id)!;
                if (current.Status is not ("Running" or "Pausing" or "Cancelling"))
                {
                    testOutput.WriteLine(JsonSerializer.Serialize(current));
                    return current;
                }
                await Task.Delay(1000, timeout.Token);
            }
        }

        var retryEvolution = Environment.GetEnvironmentVariable("AIPLATFORM_TEST_RETRY_EVOLUTION") == "true";
        if (retryEvolution)
        {
            var failed = plan.Tasks.Single(task => task.Status == "Failed");
            var restored = await service.RestoreAutonomousChangesAsync(team.Id, failed.CollaborationRunId!.Value,
                CancellationToken.None);
            Assert.Equal("Restored", restored!.Status);
            foreach (var (criterion, index) in failed.AcceptanceCriteria.Select((text, index) => (text, index)))
            {
                if (criterion.Contains("persists state", StringComparison.OrdinalIgnoreCase) ||
                    criterion.Contains("only completed tasks when a term", StringComparison.OrdinalIgnoreCase))
                    service.ReviewDevelopmentCriterion(team.Id, plan.Id, failed.Id, index, "Optional", "Unverified", null);
            }
        }
        else if (plan.Status == "Failed")
            service.RetryDevelopmentTask(team.Id, plan.Id, plan.Tasks.Single(task => task.Status == "Failed").Id, true, true);
        else if (plan.Status == "Paused")
            service.ResumeDevelopmentPlan(team.Id, plan.Id, true, true);
        else if (plan.Status == "Ready")
            service.StartDevelopmentPlan(team.Id, plan.Id, true, true);
        var initial = await WaitForPlanAsync();
        Assert.True(retryEvolution ? initial.Status == "Failed" : initial.Status is "Completed" or "AwaitingValidation", initial.Error);
        var previousTasks = initial.Tasks.Where(task => task.Status == "Completed")
            .Select(task => (task.Id, task.Attempts, task.CollaborationRunId)).ToArray();
        var baseline = await browser.RunAsync(project.Id, null, true, CancellationToken.None);
        testOutput.WriteLine("Initial browser: " + JsonSerializer.Serialize(baseline));
        Assert.True(baseline.Status == "Passed", JsonSerializer.Serialize(baseline));

        const string request = "Ajoute une recherche et un filtre permettant d'afficher uniquement les tâches terminées. " +
            "Donne à la recherche le libellé accessible « Rechercher des tâches » et conserve le filtre « Filtrer les tâches ».";
        if (retryEvolution)
            service.RetryDevelopmentTask(team.Id, plan.Id, initial.Tasks.Single(task => task.Status == "Failed").Id, true, true);
        else
        {
            var extended = (await service.ExtendDevelopmentPlanAsync(team.Id, plan.Id, request, CancellationToken.None))!;
            Assert.Equal("Ready", extended.Status);
            Assert.True(extended.Tasks.Count > previousTasks.Length);
            Assert.Equal(previousTasks, extended.Tasks.Take(previousTasks.Length)
                .Select(task => (task.Id, task.Attempts, task.CollaborationRunId)));
            service.StartDevelopmentPlan(team.Id, plan.Id, true, true);
        }
        var final = await WaitForPlanAsync();
        Assert.True(final.Status == "AwaitingValidation", final.Error);
        Assert.Equal(previousTasks, final.Tasks.Take(previousTasks.Length)
            .Select(task => (task.Id, task.Attempts, task.CollaborationRunId)));
        Assert.All(final.Tasks.Skip(previousTasks.Length),
            task => Assert.Equal("Passed", task.Validation?.CompilationStatus));

        var regression = await browser.RunAsync(project.Id, null, true, CancellationToken.None);
        testOutput.WriteLine("Regression browser: " + JsonSerializer.Serialize(regression));
        Assert.True(regression.Status == "Passed", JsonSerializer.Serialize(regression));
        var scenarios = new CustomAgentBrowserScenario[]
        {
            new(Guid.NewGuid().ToString("N"), "Recherche des tâches", "/",
            [
                new("fill", "label", "Nouvelle tâche", "{{testValue}}"),
                new("click", "role", "Ajouter", null, "button"),
                new("fill", "label", "Rechercher des tâches", "unmatched-xyz"),
                new("expectHidden", "text", "{{testValue}}"),
                new("fill", "label", "Rechercher des tâches", "{{testValue}}"),
                new("expectVisible", "text", "{{testValue}}")
            ]),
            new(Guid.NewGuid().ToString("N"), "Filtre des tâches terminées", "/",
            [
                new("fill", "label", "Nouvelle tâche", "{{testValue}}"),
                new("click", "role", "Ajouter", null, "button"),
                new("select", "label", "Filtrer les tâches", "completed"),
                new("expectHidden", "text", "{{testValue}}"),
                new("select", "label", "Filtrer les tâches", "all"),
                new("click", "role", "{{testValue}}", null, "checkbox"),
                new("select", "label", "Filtrer les tâches", "completed"),
                new("expectVisible", "text", "{{testValue}}"),
                new("reload"),
                new("expectVisible", "text", "{{testValue}}")
            ])
        };
        var functionality = await browser.RunAsync(project.Id, scenarios, true, CancellationToken.None);
        testOutput.WriteLine("Evolution browser: " + JsonSerializer.Serialize(functionality));
        Assert.True(functionality.Status == "Passed", JsonSerializer.Serialize(functionality));
        var reloaded = new CustomAgentStore(options);
        var persisted = reloaded.GetDevelopmentPlan(team.Id, plan.Id)!;
        var conversation = reloaded.GetConversation(persisted.ConversationAgentId!.Value, persisted.ConversationId!.Value)!;
        Assert.Equal("AwaitingValidation", persisted.Status);
        Assert.Equal(project.Id, conversation.Workspace!.WorkspaceId);
        Assert.Equal(request, conversation.Messages.Last().Content);
    }

    [Fact]
    [Trait("Category", "RealOllama")]
    public async Task RealOllamaExecutesAndPersistsAThreeTaskDevelopmentPlan()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AIPLATFORM_RUN_REAL_OLLAMA"), "true",
                StringComparison.OrdinalIgnoreCase))
            return;

        var resumeDirectory = Environment.GetEnvironmentVariable("AIPLATFORM_TEST_RESUME_DATA_DIRECTORY");
        var preserveDirectory = !string.IsNullOrWhiteSpace(resumeDirectory);
        var directory = preserveDirectory ? Path.GetFullPath(resumeDirectory!) : NewDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            var plannerModel = Environment.GetEnvironmentVariable("AIPLATFORM_TEST_PLANNER_MODEL");
            if (string.IsNullOrWhiteSpace(plannerModel))
                plannerModel = "qwen3:4b";
            var developerModel = Environment.GetEnvironmentVariable("AIPLATFORM_TEST_DEVELOPER_MODEL");
            if (string.IsNullOrWhiteSpace(developerModel))
                developerModel = "qwen2.5-coder:7b";
            var options = new StudioOptions
            {
                DataDirectory = directory,
                MaxCorrectionAttempts = 3,
                MaxDevelopmentPlanAttempts = 8,
                MaxDevelopmentPlanTasks = 3
            };
            var configuration = TestSupport.Configuration() with
            {
                Models =
                [
                    new() { Id = "general", Provider = "Ollama", Name = plannerModel, ContextSize = 8192, MaxOutputTokens = 2048 },
                    new() { Id = "developer", Provider = "Ollama", Name = developerModel, ContextSize = 8192, MaxOutputTokens = 3072 }
                ]
            };
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            using var provider = new OllamaProvider(client, new OllamaOptions
            {
                RequestTimeoutSeconds = 600,
                KeepAliveSeconds = 120,
                UnloadPreviousModel = true
            });
            var catalog = new OllamaModelCatalog(client, new OllamaOptions());
            var store = new CustomAgentStore(options);
            var nodeExecutable = Environment.GetEnvironmentVariable("ProgramFiles") is { } programFiles
                ? Path.Combine(programFiles, "nodejs", "node.exe") : "";
            if (!File.Exists(nodeExecutable))
                nodeExecutable = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
                    .Select(path => Path.Combine(path, "node.exe")).FirstOrDefault(File.Exists) ?? "";
            if (!string.IsNullOrEmpty(nodeExecutable))
            {
                var nodeDirectory = new DirectoryInfo(Path.GetDirectoryName(nodeExecutable)!);
                nodeExecutable = Path.Combine(
                    nodeDirectory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? nodeDirectory.FullName,
                    Path.GetFileName(nodeExecutable));
            }
            var helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "AIPlatform.Web", "scripts", "studio-browser-tests.mjs"));
            var browserOptions = new StudioBrowserTestOptions(nodeExecutable, helper);
            var localExecutor = new LocalCommandExecutor(workspaces, options, desktopEnabled: true, browserOptions);
            var service = new CustomAgentService(store, catalog, provider,
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions,
                new ProjectAnalyzer(options), studioOptions: options,
                localCommands: localExecutor,
                browserTestRunner: new StudioBrowserTestRunner(missions, localExecutor, options, browserOptions, true),
                logger: new TestOutputLogger<CustomAgentService>(testOutput));
            CustomAgentTeam team;
            CustomAgentDevelopmentPlan plan;
            CustomAgentDevelopmentPlan? started;
            if (preserveDirectory)
            {
                var planId = Guid.Parse(Environment.GetEnvironmentVariable("AIPLATFORM_TEST_RESUME_PLAN_ID")
                    ?? throw new InvalidOperationException("A development-plan ID is required for resume mode."));
                var saved = store.ListTeams()
                    .SelectMany(savedTeam => store.ListDevelopmentPlans(savedTeam.Id)
                        .Select(savedPlan => (Team: savedTeam, Plan: savedPlan)))
                    .Single(item => item.Plan.Id == planId);
                team = saved.Team;
                plan = service.GetDevelopmentPlan(team.Id, planId)
                    ?? throw new InvalidOperationException("The persisted development plan was not found.");
                Assert.Equal("Completed", plan.Tasks[0].Status);
                if (plan.Status == "Paused")
                {
                    Assert.Equal("Pending", plan.Tasks[1].Status);
                    Assert.Equal("Pending", plan.Tasks[2].Status);
                    started = service.ResumeDevelopmentPlan(
                        team.Id, plan.Id, workspaceAuthorized: true, executionAuthorized: true);
                }
                else
                {
                    Assert.Equal("Failed", plan.Status);
                    Assert.Equal("Completed", plan.Tasks[1].Status);
                    Assert.Equal("Failed", plan.Tasks[2].Status);
                    var failedTask = plan.Tasks[2];
                    var retryContext = " The previous npm run build failed with TS2345 in src/App.tsx: the persisted " +
                        "filter is a Filter union ('all' | 'active' | 'completed'), but localStorage.getItem returns " +
                        "string | null. Initialize useState<Filter> only after checking those three allowed values " +
                        "and fall back to 'all'; do not pass an arbitrary string or cast it to Filter.";
                    plan = plan with
                    {
                        Tasks = plan.Tasks.Select(task => task.Id == failedTask.Id
                            ? task with { Description = task.Description + retryContext }
                            : task).ToArray()
                    };
                    Assert.True(store.UpdateDevelopmentPlan(plan));
                    started = service.RetryDevelopmentTask(
                        team.Id, plan.Id, plan.Tasks[2].Id,
                        workspaceAuthorized: true, executionAuthorized: true);
                }
            }
            else
            {
                var planner = await service.CreateAgentAsync(
                    new("Planner", "", "Create small, independently verifiable implementation tasks.", plannerModel),
                    CancellationToken.None);
                var developer = await service.CreateAgentAsync(
                    new("Developer", "", "Implement only the current task and preserve completed work.", developerModel),
                    CancellationToken.None);
                var reviewer = await service.CreateAgentAsync(
                    new("Reviewer", "", "Review exact changes, validations, and browser results.", plannerModel),
                    CancellationToken.None);
                team = service.SaveTeam(null, new("Temporary incremental Ollama team",
                    [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1),
                        new(reviewer.Id, "Reviewer", 2)]))!;
                var project = await missions.CreateCustomAgentProjectAsync(
                    "Temporary incremental task app", CustomAgentProjectType.React, CancellationToken.None);
                var workspace = new CustomAgentWorkspaceSelection(
                    CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);
                plan = (await service.CreateDevelopmentPlanAsync(team.Id,
                    "Crée une petite application React de gestion de tâches en exactement trois étapes, ordonnées et dépendantes. " +
                    "La première étape doit satisfaire tous les tests existants dans src/App.test.tsx et src/tasks.test.ts : " +
                    "champ accessible « Nouvelle tâche », bouton « Ajouter », ajout, suppression, complétion et filtres; " +
                    "addTask doit écarter un titre blanc, rogner les titres, ajouter sans muter la liste; " +
                    "deleteTask doit retirer uniquement l’identifiant demandé sans mutation; toggleTask doit inverser " +
                    "uniquement la tâche visée sans mutation. La première étape doit aussi gérer les filtres et localStorage, " +
                    "car le parcours navigateur intégré vérifie l’ajout, le rechargement, puis la suppression. Ne reporte " +
                    "aucun comportement vérifié par les tests ou le parcours navigateur aux étapes suivantes. " +
                    "Les étapes suivantes ajoutent un état vide accessible et des vérifications supplémentaires sans casser les tests. " +
                    "npm run build et npm test doivent réussir après chaque étape.",
                    workspace, CancellationToken.None))!;
                Assert.Equal(3, plan.Tasks.Count);
                Assert.Equal(Enumerable.Range(0, plan.Tasks.Count), plan.Tasks.Select(task => task.Order));
                Assert.All(plan.Tasks, task => Assert.NotEmpty(task.AcceptanceCriteria));
                Assert.Contains(plan.Tasks.Skip(1), task => task.Dependencies.Count > 0);
                started = service.StartDevelopmentPlan(
                    team.Id, plan.Id, workspaceAuthorized: true, executionAuthorized: true);
            }
            Assert.Equal("Running", started!.Status);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(40));
            CustomAgentDevelopmentPlan completed;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                completed = service.GetDevelopmentPlan(team.Id, plan.Id)!;
                if (completed.Status is not ("Running" or "Pausing" or "Cancelling"))
                    break;
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
            }

            var childRuns = completed.Tasks.Where(task => task.CollaborationRunId.HasValue)
                .Select(task => service.GetCollaborationRun(team.Id, task.CollaborationRunId!.Value))
                .Where(run => run is not null).ToArray();
            testOutput.WriteLine(JsonSerializer.Serialize(new
            {
                completed.Status,
                completed.Error,
                attempts = completed.Attempts,
                tasks = completed.Tasks.Select(task => new
                {
                    task.Id, task.Title, task.Description, task.Status, task.Attempts,
                    task.Summary, task.Error, task.ChangedFiles,
                    validation = task.Validation is null ? null : new
                    {
                        task.Validation.StaticStatus, task.Validation.CompilationStatus,
                        diagnostics = task.Validation.Diagnostics.Select(item => new
                        {
                            item.Path, item.Code,
                            message = item.Message[..Math.Min(item.Message.Length, 400)]
                        })
                    }
                }),
                childRuns = childRuns.Select(run => new
                {
                    run!.Status, run.Error, run.Validation, run.BrowserTest,
                    commands = run.ExecutionResults?.Select(command => new
                    {
                        command.Command, command.ExitCode, command.TimedOut,
                        output = command.Output[..Math.Min(command.Output.Length, 800)]
                    }),
                    contributions = run.Contributions.Select(item => new
                    {
                        item.Role,
                        content = item.Content[..Math.Min(item.Content.Length, 700)]
                    })
                }),
                plannerModel,
                developerModel
            }));

            Assert.Equal("AwaitingValidation", completed.Status);
            Assert.All(completed.Tasks, task =>
            {
                Assert.Equal("Completed", task.Status);
                Assert.Equal("Passed", task.Validation?.CompilationStatus);
                Assert.NotEmpty(task.ChangedFiles ?? []);
            });
            Assert.Equal(completed.Tasks.Count, childRuns.Length);
            Assert.All(childRuns, run =>
            {
                Assert.Equal("Completed", run!.Status);
                Assert.Equal("Passed", run.Validation?.CompilationStatus);
                Assert.Contains(run.ExecutionResults ?? [],
                    result => result.Command == "npm run build" && result.ExitCode == 0);
                Assert.Contains(run.ExecutionResults ?? [],
                    result => result.Command == "npm test" && result.ExitCode == 0);
            });
            Assert.Equal(completed.Tasks.Select(task => task.Id),
                new CustomAgentStore(options).GetDevelopmentPlan(team.Id, plan.Id)!.Tasks.Select(task => task.Id));
        }
        finally
        {
            if (!preserveDirectory && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AutonomousRunReformatsAnEmptyFileResponseBeforeExecutingCommands()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory, MaxCorrectionAttempts = 2 };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var now = DateTimeOffset.UtcNow;
            var executor = new FakeLocalCommandExecutor(new Queue<LocalCommandResult>(
            [
                new("npm install --no-audit --no-fund", 0, false, "installed", now, now),
                new("npm run build", 0, false, "built", now, now),
                new("npm test", 0, false, "passed", now, now)
            ]));
            var developerCalls = 0;
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Implement the task screen and validate it."}""");
                if (system.Contains("You are the Developer making a minimal correction.", StringComparison.Ordinal))
                {
                    Assert.Contains("NO_FILE_CHANGES", request.Messages[1].Content);
                    Assert.Contains("export function addTask(tasks: Task[], _title: string)", system);
                    return Task.FromResult("""{"files":[{"path":"src/App.tsx","operation":"update","content":"export default function App() { return <main><h1>Tasks</h1></main> }"}]}""");
                }
                if (system.Contains("You are the Developer.", StringComparison.Ordinal))
                {
                    developerCalls++;
                    return Task.FromResult("""{"files":[]}""");
                }
                if (system.Contains("You are the final read-only Reviewer", StringComparison.Ordinal))
                {
                    Assert.Equal(3, executor.Commands.Count);
                    return Task.FromResult("""{"response":"Build and tests passed for the changed task screen.","requiresCorrection":false}""");
                }
                throw new InvalidOperationException("Unexpected autonomous stage.");
            });
            var service = new CustomAgentService(new CustomAgentStore(options),
                new FakeOllamaModelCatalog(), provider, new AgentRegistry(configuration),
                TimeProvider.System, workspaces, missions, new ProjectAnalyzer(options),
                studioOptions: options, localCommands: executor,
                browserTestRunner: new FakeStudioBrowserTestRunner());
            var team = await CreateTeamAsync(service);
            var project = await missions.CreateCustomAgentProjectAsync(
                "Empty response recovery", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);

            var queued = service.StartCollaboration(team.Id, "Create a task screen.", workspace, null,
                "Autonomous", workspaceAuthorized: true, executionAuthorized: true)!;
            var completed = await WaitForRunAsync(service, team.Id, queued.Id);

            Assert.True(completed.Status == "Completed", completed.Error);
            Assert.Equal(1, developerCalls);
            Assert.Equal(1, completed.Validation!.CorrectionAttempts);
            Assert.Contains(completed.AppliedChanges!, change => change.Path == "src/App.tsx");
            Assert.Equal(3, executor.Commands.Count);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReactTeamStopsAfterTwoUnsuccessfulStaticCorrections()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Update the app component.","changes":[]}""");
                if (system.Contains("You are the Developer making a minimal correction.", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Retained the unresolved import.","changes":[{"operation":"modify","path":"src/App.tsx","summary":"Attempt a minimal correction.","content":"import Missing from './Missing'; export default Missing;"}]}""");
                if (system.Contains("read-only Reviewer", StringComparison.Ordinal))
                {
                    Assert.Contains("LOCAL_IMPORT_MISSING", request.Messages[1].Content);
                    return Task.FromResult("""{"response":"The import remains unresolved.","requiresCorrection":true}""");
                }

                if (system.Contains("You are the Developer.", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Added an unresolved import.","changes":[{"operation":"modify","path":"src/App.tsx","summary":"Import a missing module.","content":"import Missing from './Missing'; export default Missing;"}]}""");
                throw new InvalidOperationException("Unexpected collaboration stage.");
            });
            var service = new CustomAgentService(new CustomAgentStore(options),
                new FakeOllamaModelCatalog(), provider, new AgentRegistry(configuration),
                TimeProvider.System, workspaces, missions);
            var planner = await service.CreateAgentAsync(
                new("Planner", "", "Plan.", "qwen3:4b"), CancellationToken.None);
            var developer = await service.CreateAgentAsync(
                new("Developer", "", "Implement.", "qwen2.5-coder:7b"), CancellationToken.None);
            var reviewer = await service.CreateAgentAsync(
                new("Reviewer", "", "Review.", "qwen3:4b"), CancellationToken.None);
            var team = service.SaveTeam(null, new("Bounded correction team",
                [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)]))!;
            var project = await missions.CreateCustomAgentProjectAsync(
                "Unresolved import", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);

            var queued = service.StartCollaboration(team.Id, "Add a module.", workspace, null)!;
            var completed = await WaitForRunAsync(service, team.Id, queued.Id);

            Assert.Equal("AwaitingApproval", completed.Status);
            Assert.Equal(CustomAgentLimits.MaxCorrectionAttempts, completed.Validation!.CorrectionAttempts);
            Assert.Equal(3, completed.Validation.History.Count);
            Assert.All(completed.Validation.History,
                attempt => Assert.Contains(attempt.Diagnostics, item => item.Code == "LOCAL_IMPORT_MISSING"));
            Assert.Equal(3, completed.Contributions.Count(item => item.Role == "Developer"));
            Assert.Equal(AgentReactTemplate.Files.Single(file => file.Path == "src/App.tsx").Content,
                (await workspaces.ReadAsync(project.Id, "src/App.tsx", CancellationToken.None)).Content);
            Assert.Equal("Pending", completed.Proposal!.Status);
            Assert.Equal("modify", completed.Proposal.Changes.Single().Operation);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReactProjectAcceptsAdditionalSourceButProtectsItsTrustedTemplate()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var workspaces = new WorkspaceStore(options);
            var id = Guid.NewGuid();
            workspaces.Create(id);
            await workspaces.WriteAsync(id, AgentReactTemplate.Files, CancellationToken.None, AgentReactTemplate.Id);

            Assert.True(AgentReactTemplate.IsEditable("src/components/FilterBar.tsx"));
            const string filterBar = "export default function FilterBar() { return <nav aria-label=\"Filters\" /> }";
            Assert.False(ProjectExport.ContainsPotentialSecret(filterBar));
            await workspaces.ApplyReviewedChangesAsync(id, AgentReactTemplate.Id,
                [new ReviewedWorkspaceChange("src/components/FilterBar.tsx", null,
                    filterBar)],
                _ => true, CancellationToken.None);

            Assert.Contains(workspaces.List(id), file => file.Path == "src/components/FilterBar.tsx");
            await Assert.ThrowsAsync<StudioValidationException>(() =>
                workspaces.ApplyReviewedChangesAsync(id, AgentReactTemplate.Id,
                    [new ReviewedWorkspaceChange("package.json",
                        AgentReactTemplate.Files.Single(file => file.Path == "package.json").Content,
                        "{\"scripts\":{\"build\":\"unknown-command\"}}")],
                    _ => true, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<CustomAgentCollaborationRun> WaitForRunAsync(
        CustomAgentService service, Guid teamId, Guid runId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var run = service.GetCollaborationRun(teamId, runId)!;
            if (run.Status is not ("Queued" or "Running"))
                return run;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "AIPlatform-agent-project-tests-" + Guid.NewGuid().ToString("N"));

    private static async Task<CustomAgentTeam> CreateTeamAsync(CustomAgentService service)
    {
        var planner = await service.CreateAgentAsync(
            new("Planner", "", "Plan.", "qwen3:4b"), CancellationToken.None);
        var developer = await service.CreateAgentAsync(
            new("Developer", "", "Implement.", "qwen2.5-coder:7b"), CancellationToken.None);
        var reviewer = await service.CreateAgentAsync(
            new("Reviewer", "", "Review.", "qwen3:4b"), CancellationToken.None);
        return service.SaveTeam(null, new("Team",
            [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)]))!;
    }
}

internal sealed class FakeLocalCommandExecutor(
    Queue<LocalCommandResult> results,
    Func<LocalCommandKind, CancellationToken, Task>? onRun = null) : ILocalCommandExecutor
{
    public bool Available => true;
    public List<LocalCommandKind> Commands { get; } = [];

    public Guid Authorize(Guid workspaceId) => Guid.NewGuid();
    public void Revoke(Guid authorizationId) { }

    public LocalApplicationSnapshot GetApplication(Guid workspaceId, CustomAgentProjectType projectType) =>
        new(workspaceId, "Stopped", null, null, [], null, null, DateTimeOffset.UtcNow);

    public Task<LocalApplicationSnapshot> StartApplicationAsync(
        Guid workspaceId, CustomAgentProjectType projectType, bool executionAuthorized,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The fake executor does not start local servers.");

    public Task<LocalApplicationSnapshot?> StopApplicationAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        Task.FromResult<LocalApplicationSnapshot?>(null);

    public async Task<LocalCommandResult> RunAsync(
        Guid authorizationId, Guid workspaceId, CustomAgentProjectType projectType,
        LocalCommandKind command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Add(command);
        if (onRun is not null)
            await onRun(command, cancellationToken);
        return results.Dequeue();
    }

    public Task<LocalCommandResult> RunBrowserHelperAsync(
        Guid authorizationId, Guid workspaceId, string input, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The fake executor does not run browser helpers.");
}

internal sealed class FakeStudioBrowserTestRunner(
    Queue<CustomAgentBrowserTestReport>? reports = null) : IStudioBrowserTestRunner
{
    private readonly Queue<CustomAgentBrowserTestReport> results = reports ?? new(
        [new(Guid.NewGuid(), "Passed", "http://127.0.0.1:5173/", DateTimeOffset.UtcNow, 1, 0,
            null, [new(Guid.NewGuid().ToString("N"), "Page content", "Passed", null, null, [], null)])]);

    public bool Available => true;
    public int Calls { get; private set; }
    public IReadOnlyList<CustomAgentBrowserScenario>? LastScenarios { get; private set; }

    public Task<CustomAgentBrowserTestReport> RunAsync(
        Guid workspaceId, IReadOnlyList<CustomAgentBrowserScenario>? scenarios,
        bool executionAuthorized, CancellationToken cancellationToken) => RunAuthorizedAsync(
        workspaceId, Guid.NewGuid(), scenarios, cancellationToken);

    public Task<CustomAgentBrowserTestReport> RunAuthorizedAsync(
        Guid workspaceId, Guid authorizationId, IReadOnlyList<CustomAgentBrowserScenario>? scenarios,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastScenarios = scenarios;
        return Task.FromResult(results.Dequeue());
    }

    public Task<byte[]?> ReadScreenshotAsync(
        Guid workspaceId, Guid testId, Guid scenarioId, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]?>(null);
}

internal sealed class TestOutputLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
            output.WriteLine($"{logLevel}: {formatter(state, exception)}{Environment.NewLine}{exception}");
    }
}
