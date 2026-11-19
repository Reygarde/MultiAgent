using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure;
using AIPlatform.Infrastructure.Studio;
using System.Text.Json;

namespace AIPlatform.Tests;

public sealed class CustomAgentDevelopmentPlanTests
{
    [Fact]
    [Trait("Category", "RealOllama")]
    public void RealBrowserEvidenceCanBeAssociatedWithTheExistingPlan()
    {
        var directory = Environment.GetEnvironmentVariable("AIPLATFORM_TEST_EVIDENCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        var options = new StudioOptions { DataDirectory = directory };
        var configuration = TestSupport.Configuration();
        var workspaces = new WorkspaceStore(options);
        using var missions = new MissionStore(options, configuration, new AgentRegistry(configuration), workspaces);
        var store = new CustomAgentStore(options);
        var team = store.ListTeams().Single();
        var plan = store.ListDevelopmentPlans(team.Id).Single();
        var task = plan.Tasks.Single(item => item.Id == "task-1");
        var index = task.AcceptanceCriteria.ToList().FindIndex(item =>
            item == "Tasks persist to localStorage on reload and are restored on initial load");
        Assert.True(index >= 0);
        var run = store.GetCollaborationRun(team.Id, plan.Tasks.Last().CollaborationRunId!.Value)!;
        Assert.Empty(run.BrowserScenarios ?? []);
        var report = run.BrowserTest!;
        var scenario = report.Scenarios.Single();
        Assert.Equal("Passed", report.Status);
        Assert.Equal("Passed", scenario.Status);
        var service = new CustomAgentService(store, new FakeOllamaModelCatalog(),
            new FakeProvider((_, _) => throw new InvalidOperationException("No model call is needed for evidence.")),
            new AgentRegistry(configuration), TimeProvider.System, workspaces, missions);
        var reviewed = service.ReviewDevelopmentCriterion(team.Id, plan.Id, task.Id, index, "User", "Verified",
            "The built-in browser scenario adds a task, reloads and checks that it remains visible.",
            run.Id, report.Id, scenario.Id)!;
        Assert.Equal("AwaitingValidation", reviewed.Status);
        var saved = new CustomAgentStore(options).GetDevelopmentPlan(team.Id, plan.Id)!;
        Assert.Equal("Verified", saved.Tasks.Single(item => item.Id == task.Id).CriterionReviews![index].Status);
        Assert.Equal(report.Id, saved.Tasks.Single(item => item.Id == task.Id).CriterionReviews![index].ReportId);
    }

    [Fact]
    public async Task CriterionReviewsRequireEvidenceAndInvalidateWhenWorkspaceChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIPlatform-criteria-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration, new AgentRegistry(configuration), workspaces);
            var provider = new FakeProvider((_, _) => Task.FromResult(
                """{"tasks":[{"id":"search","title":"Search","description":"Add search.","dependencies":[],"acceptanceCriteria":["Search filters tasks.","Search survives reload."]}]}"""));
            var store = new CustomAgentStore(options);
            var service = new CustomAgentService(store, new FakeOllamaModelCatalog(), provider,
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions);
            var team = await CreateTeamAsync(service);
            var project = await missions.CreateCustomAgentProjectAsync(
                "Existing project", CustomAgentProjectType.React, CancellationToken.None);
            var plan = (await service.CreateDevelopmentPlanAsync(team.Id, "Add search",
                new(CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!), CancellationToken.None))!;
            var legacy = plan with
            {
                Status = "Completed",
                Tasks = [plan.Tasks[0] with { Status = "Completed" }]
            };
            Assert.True(store.UpdateDevelopmentPlan(legacy));
            Assert.Equal("AwaitingValidation", service.GetDevelopmentPlan(team.Id, plan.Id)!.Status);
            Assert.Null(service.GetDevelopmentPlan(team.Id, plan.Id)!.Tasks[0].CriterionReviews);
            var scenarioId = Guid.NewGuid().ToString("N");
            var failureId = Guid.NewGuid();
            var successId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var failedScenario = new CustomAgentBrowserScenarioResult(
                scenarioId, "Search", "Failed", "expectVisible", "Not found", [], null);
            var passedScenario = failedScenario with { Status = "Passed", FailedStep = null, Error = null };
            var failedReport = new CustomAgentBrowserTestReport(
                failureId, "Failed", "http://127.0.0.1:5173/", now, 0, 1, null, [failedScenario]);
            var passedReport = new CustomAgentBrowserTestReport(
                successId, "Passed", "http://127.0.0.1:5173/", now, 1, 0, null, [passedScenario]);
            var run = new CustomAgentCollaborationRun(Guid.NewGuid(), team.Id, "Search", "Completed",
                null, plan.Workspace, now, now, [], BrowserTest: passedReport,
                BrowserTestHistory: [failedReport, passedReport]);
            store.AddCollaborationRun(run);
            Assert.Throws<StudioValidationException>(() =>
                service.ReviewDevelopmentCriterion(team.Id, plan.Id, "search", 0, "User", "Verified", ""));
            Assert.Throws<StudioValidationException>(() =>
                service.ReviewDevelopmentCriterion(team.Id, plan.Id, "search", 0, "User", "Verified",
                    "Search scenario failed.", run.Id, failureId, scenarioId));
            var failed = service.ReviewDevelopmentCriterion(team.Id, plan.Id, "search", 0,
                "User", "Failed", "Browser search scenario failed.", run.Id, failureId, scenarioId)!;
            Assert.Equal("AwaitingValidation", failed.Status);
            Assert.Equal("Failed", failed.Tasks[0].CriterionReviews![0].Status);
            var optional = service.ReviewDevelopmentCriterion(team.Id, plan.Id, "search", 1,
                "Optional", "Unverified", null)!;
            Assert.Equal("AwaitingValidation", optional.Status);
            var completed = service.ReviewDevelopmentCriterion(team.Id, plan.Id, "search", 0,
                "User", "Verified", "Browser search scenario passed.", run.Id, successId, scenarioId)!;
            Assert.Equal("Completed", completed.Status);
            var reloaded = new CustomAgentStore(options).GetDevelopmentPlan(team.Id, plan.Id)!;
            Assert.Equal("Browser search scenario passed.", reloaded.Tasks[0].CriterionReviews![0].Evidence);
            Assert.Equal("Optional", reloaded.Tasks[0].CriterionReviews![1].Category);
            Assert.Contains(reloaded.Tasks[0].CriterionReviewHistory!,
                item => item.Review.Status == "Failed" && item.Review.Evidence == "Browser search scenario failed.");

            var original = (await workspaces.ReadAsync(project.Id, "src/App.tsx", CancellationToken.None)).Content;
            await workspaces.ApplyReviewedChangesAsync(project.Id, AgentReactTemplate.Id,
                [new("src/App.tsx", original, original + "\n// changed after verification\n")], _ => true,
                CancellationToken.None);
            var stale = service.GetDevelopmentPlan(team.Id, plan.Id)!;
            Assert.Equal("AwaitingValidation", stale.Status);
            Assert.Equal("Unverified", stale.Tasks[0].CriterionReviews![0].Status);
            Assert.Equal("Browser search scenario passed.", stale.Tasks[0].CriterionReviews![0].Evidence);
            Assert.Equal(3, stale.Tasks[0].CriterionReviewHistory!.Count);
            Assert.Throws<StudioValidationException>(() =>
                service.ReviewDevelopmentCriterion(team.Id, plan.Id, "search", 0, "User", "Verified",
                    "Old browser result.", run.Id, successId, scenarioId));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedPlanEvolutionUsesLiveFilesAndPersistsProjectConversation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIPlatform-development-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new StudioOptions { DataDirectory = directory, MaxDevelopmentPlanAttempts = 6 };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var responses = new Queue<string>(
            [
                """{"tasks":[{"id":"initial","title":"Initial tasks","description":"Create the task application.","dependencies":[],"acceptanceCriteria":["Tasks can be added."]}]}""",
                """{"tasks":[{"id":"search","title":"Search tasks","description":"Add search without removing existing controls.","dependencies":["initial"],"acceptanceCriteria":["Search filters tasks."]}]}"""
            ]);
            var provider = new FakeProvider((_, _) => Task.FromResult(responses.Dequeue()));
            var store = new CustomAgentStore(options);
            var service = new CustomAgentService(store, new FakeOllamaModelCatalog(), provider,
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions);
            var team = await CreateTeamAsync(service);
            var project = await missions.CreateCustomAgentProjectAsync(
                "Existing project", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);
            var created = (await service.CreateDevelopmentPlanAsync(
                team.Id, "Initial task application", workspace, CancellationToken.None))!;
            var finishedTask = created.Tasks[0] with { Status = "Completed", Attempts = 1, Summary = "Original work preserved" };
            var completed = created with { Status = "Completed", Attempts = 1, Tasks = [finishedTask] };
            Assert.True(store.UpdateDevelopmentPlan(completed));
            var original = (await workspaces.ReadAsync(project.Id, "src/App.tsx", CancellationToken.None)).Content;
            var manual = original + "\n// MANUAL_EDIT_RETAINED\n";
            await workspaces.ApplyReviewedChangesAsync(project.Id, AgentReactTemplate.Id,
                [new("src/App.tsx", original, manual)], _ => true, CancellationToken.None);

            var extended = (await service.ExtendDevelopmentPlanAsync(
                team.Id, created.Id, "Add search", CancellationToken.None))!;

            Assert.Equal("Ready", extended.Status);
            Assert.Equal(finishedTask.Id, extended.Tasks[0].Id);
            Assert.Equal("Completed", extended.Tasks[0].Status);
            Assert.Equal(finishedTask.Attempts, extended.Tasks[0].Attempts);
            Assert.Equal(finishedTask.Summary, extended.Tasks[0].Summary);
            Assert.Equal(finishedTask.AcceptanceCriteria, extended.Tasks[0].AcceptanceCriteria);
            Assert.Equal("initial", extended.Tasks[1].Dependencies.Single());
            Assert.Equal(manual, (await workspaces.ReadAsync(project.Id, "src/App.tsx", CancellationToken.None)).Content);
            Assert.Contains("MANUAL_EDIT_RETAINED", provider.Requests.Last().Messages[0].Content);
            var reloaded = new CustomAgentStore(options);
            var persisted = reloaded.GetDevelopmentPlan(team.Id, created.Id)!;
            var conversation = reloaded.GetConversation(persisted.ConversationAgentId!.Value, persisted.ConversationId!.Value)!;
            Assert.Equal(project.Id, conversation.Workspace!.WorkspaceId);
            Assert.Equal(new[] { "Initial task application", "Add search" },
                conversation.Messages.Select(message => message.Content));
            Assert.Equal("search", conversation.Messages[1].DevelopmentTaskIds!.Single());
            Assert.Equal(created.Id, conversation.Messages[1].DevelopmentPlanId);
            Assert.Throws<StudioValidationException>(() =>
                service.SetWorkspace(conversation.AgentId, conversation.Id, null));
            Assert.Throws<StudioValidationException>(() =>
                service.DeleteConversation(conversation.AgentId, conversation.Id));
            Assert.Throws<StudioValidationException>(() =>
                store.SaveDevelopmentPlanConversation(persisted, conversation,
                    persisted.UpdatedAt.AddMinutes(-1), conversation.UpdatedAt));
            Assert.Equal(2, new CustomAgentStore(options)
                .GetConversation(conversation.AgentId, conversation.Id)!.Messages.Count);
            Assert.Throws<StudioValidationException>(() =>
                service.StartDevelopmentPlan(team.Id, created.Id, false, false));
            Assert.Equal("Completed", reloaded.GetDevelopmentPlan(team.Id, created.Id)!.Tasks[0].Status);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PlansPersistExtensionsValidateDependenciesAndRequireExplicitRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIPlatform-development-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new StudioOptions { DataDirectory = directory, MaxDevelopmentPlanAttempts = 6 };
            var configuration = TestSupport.Configuration();
            var workspaces = new WorkspaceStore(options);
            using var missions = new MissionStore(options, configuration,
                new AgentRegistry(configuration), workspaces);
            var invalidPlan = JsonSerializer.Serialize(new
            {
                tasks = new[]
                {
                    new
                    {
                        id = "task-screen", title = "Create task screen",
                        description = "Add the task screen to the existing React scaffold.",
                        dependencies = Array.Empty<string>(),
                        acceptanceCriteria = new[] { new string('x', 241) }
                    }
                }
            });
            var responses = new Queue<string>(
            [
                invalidPlan,
                """{"tasks":[{"id":"task-screen","title":"Create task screen","description":"Add the task screen to the existing React scaffold.","dependencies":[],"acceptanceCriteria":["The screen renders task controls."]},{"id":"persist-tasks","title":"Persist tasks","description":"Save tasks in localStorage after the screen exists.","dependencies":["task-screen"],"acceptanceCriteria":["Tasks survive reload."]}]}""",
                """{"tasks":[{"id":"browser-check","title":"Verify task workflow","description":"Exercise the task workflow in the browser.","dependencies":["persist-tasks"],"acceptanceCriteria":["Add and remove tasks successfully."]}]}"""
            ]);
            var provider = new FakeProvider((_, _) => Task.FromResult(responses.Dequeue()));
            var store = new CustomAgentStore(options);
            var service = new CustomAgentService(store, new FakeOllamaModelCatalog(), provider,
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions);
            var team = await CreateTeamAsync(service);
            var project = await missions.CreateCustomAgentProjectAsync(
                "Incremental tasks", CustomAgentProjectType.React, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);

            var created = await service.CreateDevelopmentPlanAsync(
                team.Id, "Create and verify a task application.", workspace, CancellationToken.None);
            Assert.NotNull(created);
            Assert.Equal("Ready", created.Status);
            Assert.Equal(new[] { "task-screen", "persist-tasks" }, created.Tasks.Select(task => task.Id));
            Assert.Equal("task-screen", created.Tasks[1].Dependencies.Single());
            Assert.Contains("src/App.tsx", created.Memory.KeyFiles);

            var extended = await service.ExtendDevelopmentPlanAsync(
                team.Id, created.Id, "Add browser verification.", CancellationToken.None);
            Assert.NotNull(extended);
            Assert.Equal(new[] { "task-screen", "persist-tasks", "browser-check" },
                extended.Tasks.Select(task => task.Id));
            Assert.Equal("persist-tasks", extended.Tasks[2].Dependencies.Single());
            Assert.Equal(3, provider.Requests.Count);
            Assert.Contains("previous plan failed validation", provider.Requests.ToArray()[1].Messages[0].Content);
            Assert.Contains("must never reference the task itself",
                provider.Requests.ToArray()[1].Messages[0].Content);
            Assert.Contains("Existing plan tasks", provider.Requests.ToArray()[2].Messages[1].Content);

            var reloadedStore = new CustomAgentStore(options);
            var persistedTasks = reloadedStore.GetDevelopmentPlan(team.Id, extended.Id)!.Tasks;
            Assert.Equal(extended.Tasks.Select(task => task.Id), persistedTasks.Select(task => task.Id));
            Assert.Equal(extended.Tasks[2].Dependencies, persistedTasks[2].Dependencies);

            var invalidCycle = extended with
            {
                Id = Guid.NewGuid(),
                Tasks =
                [
                    extended.Tasks[0] with { Dependencies = ["persist-tasks"] },
                    extended.Tasks[1] with { Dependencies = ["task-screen"] },
                    extended.Tasks[2]
                ]
            };
            Assert.Throws<InvalidDataException>(() => reloadedStore.AddDevelopmentPlan(invalidCycle));

            Assert.Throws<StudioValidationException>(() =>
                service.StartDevelopmentPlan(team.Id, extended.Id, workspaceAuthorized: false, executionAuthorized: false));
            Assert.Equal("Ready", service.GetDevelopmentPlan(team.Id, extended.Id)!.Status);

            var interrupted = extended with
            {
                Status = "Running",
                CurrentTaskId = "task-screen",
                Tasks = extended.Tasks.Select(task => task.Id == "task-screen"
                    ? task with { Status = "Running", Attempts = 1 }
                    : task).ToArray(),
                Attempts = 1
            };
            Assert.True(reloadedStore.UpdateDevelopmentPlan(interrupted));
            var recovered = service.GetDevelopmentPlan(team.Id, extended.Id)!;
            Assert.Equal("Paused", recovered.Status);
            Assert.Equal("Pending", recovered.Tasks.Single(task => task.Id == "task-screen").Status);
            Assert.Equal(3, provider.Requests.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<CustomAgentTeam> CreateTeamAsync(CustomAgentService service)
    {
        var planner = await service.CreateAgentAsync(
            new("Planner", "", "Plan.", "qwen3:4b"), CancellationToken.None);
        var developer = await service.CreateAgentAsync(
            new("Developer", "", "Implement.", "qwen2.5-coder:7b"), CancellationToken.None);
        var reviewer = await service.CreateAgentAsync(
            new("Reviewer", "", "Review.", "qwen3:4b"), CancellationToken.None);
        return service.SaveTeam(null, new("Incremental plan team",
            [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1),
                new(reviewer.Id, "Reviewer", 2)]))!;
    }
}
