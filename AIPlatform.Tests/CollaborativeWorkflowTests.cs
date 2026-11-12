using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class CollaborativeWorkflowTests
{
    [Fact]
    public async Task RealAgentContractsExchangeBoundedContextAndRequirePostGenerationConsent()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator DATA_MARKER", MissionKind.Project, ApproveSandboxExecution: true));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var pending = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, pending.Status);
        Assert.False(pending.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(6, fixture.Provider.Requests.Count);
        Assert.Equal("Calculator", pending.ProjectName);
        Assert.NotNull(pending.Workflow!.Plan);
        Assert.NotNull(pending.Workflow.Review);
        Assert.Single(pending.Workflow.Revisions!);
        var developerCalls = fixture.Provider.Requests.Where(request => request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("content", out _)).ToArray();
        Assert.Equal(4, developerCalls.Length);
        using var testInput = JsonDocument.Parse(developerCalls.Last().Messages.Last().Content);
        Assert.Contains(testInput.RootElement.GetProperty("sources").EnumerateArray(),
            item => item.GetProperty("path").GetString() == "src/App/Program.cs");
        Assert.All(fixture.Provider.Requests, request => Assert.Contains("untrusted data", request.Messages[0].Content));
        Assert.All(fixture.Provider.Requests, request => Assert.DoesNotContain("DATA_MARKER", request.Messages[0].Content));
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var verified = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.Verified, verified.Status);
        Assert.Equal(new[] { "Build", "Tests" }, fixture.Sandbox.Calls);
        Assert.All(verified.Verification, result => Assert.True(result.DurationMilliseconds >= 0));
        Assert.All(verified.Workflow!.Steps, step => Assert.Equal(WorkflowStatus.Completed, step.Status));
        Assert.Equal(6, fixture.Provider.Requests.Count); // Deterministic Test/Orchestrator do not call a model.
    }

    [Fact]
    public async Task CompilerFailurePersistsDiagnosticsAndRequiresFreshApprovalForEveryCorrection()
    {
        using var fixture = new WorkflowFixture();
        fixture.Sandbox.Result = stage => stage == "Build"
            ? new(stage, "Failed", 1, "CS1002: semicolon expected.")
            : throw new InvalidOperationException("Tests must not run after failed build.");
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        for (var version = 1; version <= 4; version++)
        {
            var before = fixture.Store.Get(mission.Id)!;
            Assert.Equal(MissionStatus.AwaitingApproval, before.Status);
            Assert.False(before.SandboxApproved);
            var testSource = before.Workflow!.Draft!.Single(file => file.Path.EndsWith("UnitTests.cs")).Content;
            await fixture.Approve(mission.Id);
            await fixture.Processor.ProcessAsync(mission.Id, default);
            var after = fixture.Store.Get(mission.Id)!;
            Assert.Equal(version, after.Verification.Count);
            Assert.Equal(testSource, after.Workflow!.Draft!.Single(file => file.Path.EndsWith("UnitTests.cs")).Content);
            Assert.Equal(version == 4 ? MissionStatus.BuildFailed : MissionStatus.AwaitingApproval, after.Status);
        }
        var final = fixture.Store.Get(mission.Id)!;
        Assert.Equal(3, final.CorrectionAttempts);
        Assert.Equal(4, final.Workflow!.Revisions!.Count);
        Assert.Equal(4, fixture.Sandbox.Calls.Count);
        Assert.All(final.Verification, result => Assert.Contains("CS1002", result.Log));
        Assert.Contains(fixture.Provider.Requests, request => request.Messages.Last().Content.Contains("CS1002"));
    }

    [Fact]
    public async Task StaleFilesCannotExecuteAndExplicitResumeRereviewsThem()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        await fixture.Approve(mission.Id);
        var path = Path.Combine(fixture.Workspaces.GetDirectory(mission.Id), "src", "App", "Program.cs");
        await File.AppendAllTextAsync(path, "\n// changed after approval");
        await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Empty(fixture.Sandbox.Calls);
        fixture.Store.Update(mission.Id, current => current with { Status = MissionStatus.Failed });
        Assert.True(fixture.Store.Resume(mission.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(MissionStatus.AwaitingApproval, fixture.Store.Get(mission.Id)!.Status);
        Assert.Contains("changed after approval", fixture.Provider.Requests.Last().Messages.Last().Content);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task RefusalAndRestartPreserveWorkspaceAndNeverAutomaticallyExecute()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var plan = await fixture.Plans.GetAsync(mission.Id, default);
        Assert.True(fixture.Store.DecideApproval(mission.Id, false, plan.Fingerprint));
        Assert.Equal(MissionStatus.GeneratedUnverified, fixture.Store.Get(mission.Id)!.Status);
        Assert.True(fixture.Store.Resume(mission.Id));
        fixture.Store.Update(mission.Id, current => current with { Status = MissionStatus.Running });
        using var restarted = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(MissionStatus.Cancelled, restarted.Get(mission.Id)!.Status);
        Assert.False(restarted.Get(mission.Id)!.SandboxApproved);
        Assert.NotEmpty(restarted.Get(mission.Id)!.Workflow!.Revisions!);
        Assert.True(restarted.Resume(mission.Id));
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(5, fixture.Workspaces.List(mission.Id).Count);
    }

    [Fact]
    public async Task PendingApprovalSurvivesRestartWithoutRequeuing()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        using var restarted = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry);
        Assert.Equal(MissionStatus.AwaitingApproval, restarted.Get(mission.Id)!.Status);
        Assert.Equal(WorkflowStatus.WaitingForApproval, restarted.Get(mission.Id)!.Workflow!.Steps.Single(step => step.Kind == "Approval").Status);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("""{"name":"bad","description":"bad","decisions":[],"files":[]}""")]
    public async Task InvalidPlanNeverReachesDeveloperOrDocker(string response)
    {
        using var fixture = new WorkflowFixture(responseOverride: _ => response);
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Single(fixture.Provider.Requests);
        Assert.False(fixture.Store.Get(mission.Id)!.HasWorkspace);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 2, 1)]
    public async Task DockerUnavailableOrInvalidTestCountersNeverVerify(bool available, int total, int failed)
    {
        using var fixture = new WorkflowFixture(available: available, corrections: 0);
        fixture.Sandbox.Result = stage => new(stage, "Succeeded", 0, "output", stage == "Tests" ? total : null, FailedTests: failed);
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        if (available)
        {
            await fixture.Approve(mission.Id);
            await fixture.Processor.ProcessAsync(mission.Id, default);
        }
        Assert.Equal(available ? MissionStatus.TestsFailed : MissionStatus.GeneratedUnverified, fixture.Store.Get(mission.Id)!.Status);
    }

    [Fact]
    public async Task CancellationReachesModelAndStopsDownstreamSteps()
    {
        using var fixture = new WorkflowFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Processor.ProcessAsync(mission.Id, cancellation.Token));
        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.Sandbox.Calls);
        fixture.Store.Cancel(mission.Id);
        fixture.Store.Update(mission.Id, current => current with { Status = MissionStatus.Cancelled });
        Assert.DoesNotContain(fixture.Store.Get(mission.Id)!.Workflow!.Steps, step => step.Status == WorkflowStatus.Running);
    }

    [Fact]
    public async Task ActivePlanningCancellationReachesProvider()
    {
        using var fixture = new WorkflowFixture();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "";
        });
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([provider]), fixture.Options),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options);
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        var running = processor.ProcessAsync(mission.Id, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Single(provider.Requests);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidFileOrReviewOutputStopsBeforeApprovalAndDocker(bool invalidFile)
    {
        var calls = 0;
        using var fixture = new WorkflowFixture(responseOverride: request =>
        {
            calls++;
            var properties = request.ResponseSchema!.Value.GetProperty("properties");
            if (properties.TryGetProperty("decisions", out _))
                return JsonSerializer.Serialize(WorkflowFixture.Plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (properties.TryGetProperty("content", out _))
            {
                if (invalidFile) return """{"path":"../outside.cs","content":"untrusted"}""";
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                var file = StudioFixture.Manifest().Files.Single(file => file.Path ==
                    input.RootElement.GetProperty("file").GetProperty("path").GetString());
                return JsonSerializer.Serialize(file, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            return """{"summary":"Bad","findings":[{"path":"outside.cs","severity":"Error","message":"invented"}]}""";
        });
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.NotEqual(MissionStatus.AwaitingApproval, fixture.Store.Get(mission.Id)!.Status);
        Assert.Equal(invalidFile ? 2 : 6, calls);
    }

    [Fact]
    public void ProjectOnlyDeveloperCanBeManuallySelectedWithoutEnteringChatRouting()
    {
        using var fixture = new WorkflowFixture();
        var platform = fixture.Platform with { Agents = fixture.Platform.Agents.Select(agent =>
            agent.Role == "Developer" ? agent with { Id = "CustomDeveloper", TaskTypes = ["Project"] } : agent).ToList() };
        var registry = new AgentRegistry(platform);
        using var store = new MissionStore(fixture.Options, platform, registry);
        var mission = store.Submit(new("Create Calculator", MissionKind.Project, "CustomDeveloper"));
        Assert.Equal("CustomDeveloper", mission.SelectedAgent);
        Assert.DoesNotContain(registry.Candidates, agent => agent.Id == "CustomDeveloper");
    }

    [Fact]
    public async Task AgentTimeoutIsExplicitAndCanBeResumed()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        var provider = new FakeProvider(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return ""; });
        var agents = new WorkflowAgents(new LlmProviderResolver([provider]), fixture.Options);
        var planner = mission.Workflow!.Agents.Single(agent => agent.Role == "Planner") with { TimeoutSeconds = 1 };
        await Assert.ThrowsAsync<LlmTimeoutException>(() => agents.PlanAsync(mission, planner, default));
    }

    [Fact]
    public void CyclesUnknownDependenciesAndMissingToolPermissionsAreRejected()
    {
        Assert.Throws<StudioValidationException>(() => WorkflowGraph.Validate(
            [new("a", "Plan", "planner", ["b"]), new("b", "Generate", "dev", ["a"])]));
        Assert.Throws<StudioValidationException>(() => WorkflowGraph.Validate([new("a", "Plan", "planner", ["unknown"])]));
        Assert.Throws<StudioValidationException>(() => WorkflowGraph.Next(new(Guid.NewGuid(), Guid.NewGuid(), [], [],
            [new("a", "Plan", "planner", [], WorkflowStatus.Skipped), new("b", "Generate", "dev", ["a"])])));
        using var fixture = new WorkflowFixture();
        var invalid = fixture.Platform with { Agents = fixture.Platform.Agents.Select(agent =>
            agent.Role == "Review" ? agent with { AllowedTools = [] } : agent).ToList() };
        using var store = new MissionStore(fixture.Options, invalid, new AgentRegistry(invalid));
        Assert.Throws<StudioValidationException>(() => store.Submit(new("Create", MissionKind.Project)));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task ProjectManagementPreservesLegacyAuditAndRejectsActiveDeletion()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.False(fixture.Store.DeleteProject(mission.Id));
        var plan = await fixture.Plans.GetAsync(mission.Id, default);
        fixture.Store.DecideApproval(mission.Id, false, plan.Fingerprint);
        Assert.True(fixture.Store.RenameProject(mission.Id, "Arithmetic", "Short description"));
        Assert.Equal("Arithmetic", fixture.Store.Get(mission.Id)!.ProjectName);
        Assert.True(fixture.Store.DeleteProject(mission.Id));
        Assert.True(fixture.Store.Get(mission.Id)!.Deleted);
        Assert.False(Directory.Exists(fixture.Workspaces.GetDirectory(mission.Id)));
        Assert.NotEmpty(fixture.Store.Get(mission.Id)!.Events);
        Assert.False(fixture.Store.Resume(mission.Id));
        var legacy = fixture.Store.Submit(new("Ordinary question"));
        Assert.Null(legacy.Workflow);
        Assert.False(fixture.Store.Resume(legacy.Id));
    }

    [Fact]
    public void ConfigurationPersistsAndCannotGrantShellOrCrossRoleTools()
    {
        using var fixture = new WorkflowFixture();
        var registry = new ConfigurableRegistry(fixture.Platform, fixture.Options);
        var configuration = registry.GetConfiguration();
        var changed = configuration with { Agents = configuration.Agents.Select(agent =>
            agent.Role == "Developer" ? agent with { Name = "Coder" } : agent).ToArray() };
        registry.Save(changed);
        Assert.Equal("Coder", new ConfigurableRegistry(fixture.Platform, fixture.Options).Agents.Single(agent => agent.Role == "Developer").Name);
        Assert.Throws<StudioValidationException>(() => registry.Save(changed with
        {
            Agents = changed.Agents.Select(agent => agent.Role == "Review"
                ? agent with { AllowedTools = ["GenerateFiles", "Shell"] } : agent).ToArray()
        }));
        Assert.Throws<StudioValidationException>(() => registry.Save(changed with
        {
            Models = changed.Models.Select(model => model with { MaxOutputTokens = model.ContextSize }).ToArray()
        }));
    }
}

internal sealed class WorkflowFixture : IDisposable
{
    public StudioOptions Options { get; }
    public PlatformConfiguration Platform { get; }
    public AgentRegistry Registry { get; }
    public WorkspaceStore Workspaces { get; }
    public MissionStore Store { get; }
    public ExecutionPlanService Plans { get; }
    public FakeProvider Provider { get; }
    public WorkflowSandbox Sandbox { get; }
    public CollaborativeProcessor Processor { get; }
    public static ProjectPlan Plan => new("Calculator", "Offline calculator library", ["Small deterministic library."],
    [
        new("src/App/App.csproj", "Application project", []),
        new("src/App/Program.cs", "Public implementation", ["src/App/App.csproj"]),
        new("tests/App.Tests/App.Tests.csproj", "Offline test project", ["src/App/App.csproj"]),
        new("tests/App.Tests/UnitTests.cs", "Actual unit tests", ["src/App/Program.cs", "tests/App.Tests/App.Tests.csproj"])
    ]);

    public WorkflowFixture(bool available = true, int corrections = 3, Func<LlmRequest, string>? responseOverride = null)
    {
        Options = new() { DataDirectory = Path.Combine(Path.GetTempPath(), "AIPlatform-workflow-" + Guid.NewGuid().ToString("N")),
            CollaborativeWorkflows = true, MaxCorrectionAttempts = corrections };
        var original = TestSupport.Configuration();
        Platform = original with
        {
            Models = original.Models.Select(model => model with { ContextSize = 8192, MaxOutputTokens = 2048 }).ToList(),
            Agents = [.. original.Agents.Select(agent => agent.Id == "DeveloperAgent"
                ? agent with { Role = "Developer", AllowedTools = ["GenerateFiles"], TaskTypes = ["Chat", "Project"] } : agent),
                .. new[] { "Planner", "Review", "Test", "Orchestrator" }.Select(role => new AgentDefinition
                {
                    Id = role + "Agent", Role = role, Description = role, SystemPrompt = role,
                    ModelId = role is "Review" or "Test" ? "developer" : "general",
                    TaskTypes = ["Project"], AllowedTools = AgentPermissions.ToolsFor(role)
                })]
        };
        Registry = new(Platform);
        Workspaces = new(Options);
        Store = new(Options, Platform, Registry, Workspaces);
        Plans = new(Workspaces, new(), Options);
        Sandbox = new(available);
        Provider = new((request, cancellationToken) =>
        {
            if (responseOverride is not null) return Task.FromResult(responseOverride(request));
            var properties = request.ResponseSchema!.Value.GetProperty("properties");
            object response;
            if (properties.TryGetProperty("decisions", out _)) response = Plan;
            else if (properties.TryGetProperty("content", out _))
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                var path = input.RootElement.GetProperty("file").GetProperty("path").GetString();
                response = StudioFixture.Manifest().Files.Single(file => file.Path == path);
            }
            else response = new ReviewReport("Source review complete; not executed.", []);
            return Task.FromResult(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        });
        Processor = new(Store, new WorkflowAgents(new LlmProviderResolver([Provider]), Options), Workspaces, Plans, Sandbox, Options);
    }

    public async Task Approve(Guid id)
    {
        var plan = await Plans.GetAsync(id, default);
        Assert.True(Store.DecideApproval(id, true, plan.Fingerprint));
    }

    public void Dispose()
    {
        Store.Dispose();
        if (Directory.Exists(Options.DataDirectory)) Directory.Delete(Options.DataDirectory, recursive: true);
    }
}

internal sealed class WorkflowSandbox(bool available) : ISandboxRunner
{
    public List<string> Calls { get; } = [];
    public Func<string, VerificationResult>? Result { get; set; }
    public Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SandboxAvailability(available, available ? "Ready" : "Unavailable"));
    public Task<VerificationResult> RunAsync(Guid workspaceId, string stage, CancellationToken cancellationToken)
    {
        Calls.Add(stage);
        return Task.FromResult(Result?.Invoke(stage) ?? new(stage, "Succeeded", 0, "Simulated Docker", stage == "Tests" ? 2 : null));
    }
}
