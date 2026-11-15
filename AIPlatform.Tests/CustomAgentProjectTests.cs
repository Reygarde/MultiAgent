using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure;
using AIPlatform.Infrastructure.Studio;
using Xunit.Abstractions;

namespace AIPlatform.Tests;

public sealed class CustomAgentProjectTests
{
    private readonly ITestOutputHelper testOutput;

    public CustomAgentProjectTests(ITestOutputHelper testOutput) => this.testOutput = testOutput;

    [Theory]
    [InlineData(CustomAgentProjectType.React, AgentReactTemplate.Id)]
    [InlineData(CustomAgentProjectType.DotNet, AgentDotNetTemplate.Id)]
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

    [Fact]
    public async Task TeamProposalCreatesAndAppliesDotNetFilesOnlyAfterApproval()
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
                "Task manager", CustomAgentProjectType.DotNet, CancellationToken.None);
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.Studio, project.Id, project.ProjectName!);

            var queued = customAgents.StartCollaboration(team.Id,
                "Add a task endpoint and service.", workspace, null)!;
            var completed = await WaitForRunAsync(customAgents, team.Id, queued.Id);

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

    [Fact]
    public async Task AutonomousRunExecutesBuildCorrectsFailurePersistsAndRestoresFiles()
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
                    new string('x', 1500) + " src/App.test.tsx: ACTIONABLE_FAILURE_DETAIL",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new("npm run build", 0, false, "built", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new("npm test", 0, false, "tests passed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]);
            var executor = new FakeLocalCommandExecutor(calls);
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"Update the task application and validate it.","changes":[]}""");
                if (system.Contains("You are the Developer making a minimal correction.", StringComparison.Ordinal))
                {
                    Assert.Contains("LOCAL_BUILD_OR_TEST_FAILED", request.Messages[1].Content);
                    Assert.Contains("ACTIONABLE_FAILURE_DETAIL", request.Messages[1].Content);
                    Assert.Contains("export function addTask(tasks: Task[], _title: string)", system);
                    Assert.True(request.ResponseSchema!.Value.GetProperty("properties")
                        .TryGetProperty("files", out var filesSchema));
                    Assert.Equal("array", filesSchema.GetProperty("type").GetString());
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
                new ProjectAnalyzer(options), studioOptions: options, localCommands: executor);
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
            var options = new StudioOptions { DataDirectory = directory, MaxCorrectionAttempts = 2 };
            var configuration = TestSupport.Configuration() with
            {
                Models =
                [
                    new() { Id = "general", Provider = "Ollama", Name = "qwen3.5:9b", ContextSize = 4096, MaxOutputTokens = 2048 },
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
            var service = new CustomAgentService(store, catalog, provider,
                new AgentRegistry(configuration), TimeProvider.System, workspaces, missions,
                new ProjectAnalyzer(options), studioOptions: options,
                localCommands: new LocalCommandExecutor(workspaces, options, desktopEnabled: true));
            var planner = await service.CreateAgentAsync(
                new("Planner", "", "Plan concisely.", "qwen3.5:9b"), CancellationToken.None);
            var developer = await service.CreateAgentAsync(
                new("Developer", "", "Implement the requested application; output complete source files only.", developerModel), CancellationToken.None);
            var reviewer = await service.CreateAgentAsync(
                new("Reviewer", "", "Review correctness and report only concrete, file-specific issues.", "qwen3.5:9b"), CancellationToken.None);
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

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                completed.Status,
                completed.Error,
                validation = completed.Validation,
                changedFiles = completed.AppliedChanges?.Select(change => change.Path),
                developerModel,
                commands = completed.ExecutionResults?.Select(result => new
                {
                    result.Command, result.ExitCode, result.TimedOut,
                    output = result.Output[..Math.Min(result.Output.Length, 800)]
                }),
                contributions = completed.Contributions.Select(item => new
                {
                    item.Role, item.ModelName, responseLength = item.Content.Length
                })
            }));
            Assert.True(completed.Status == "Completed", JsonSerializer.Serialize(new
            {
                completed.Error,
                completed.Validation,
                changedFiles = completed.AppliedChanges?.Select(change => change.Path),
                commands = completed.ExecutionResults?.Select(result => new
                {
                    result.Command, result.ExitCode, result.TimedOut,
                    output = result.Output[..Math.Min(result.Output.Length, 1200)]
                })
            }));
            Assert.Equal("Passed", completed.Validation?.CompilationStatus);
            Assert.Contains(completed.Contributions, item => item.Role == "Planner" && item.ModelName == "qwen3.5:9b");
            Assert.Contains(completed.Contributions, item => item.Role == "Developer" && item.ModelName == developerModel);
            Assert.Contains(completed.Contributions, item => item.Role == "Reviewer" && item.ModelName == "qwen3.5:9b");
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
                studioOptions: options, localCommands: executor);
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

internal sealed class FakeLocalCommandExecutor(Queue<LocalCommandResult> results) : ILocalCommandExecutor
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

    public Task<LocalCommandResult> RunAsync(
        Guid authorizationId, Guid workspaceId, CustomAgentProjectType projectType,
        LocalCommandKind command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Add(command);
        return Task.FromResult(results.Dequeue());
    }
}
