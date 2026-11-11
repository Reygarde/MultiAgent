using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class StudioMissionTests
{
    [Fact]
    public void PersistsUniqueMissionsAndMarksInterruptedOnRestart()
    {
        using var fixture = new StudioFixture();
        var first = fixture.Missions.Submit(new("First"));
        var second = fixture.Missions.Submit(new("Second"));
        Assert.NotEqual(first.Id, second.Id);
        using var reloaded = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry);
        Assert.Equal(2, reloaded.List().Count);
        Assert.Equal(MissionStatus.Cancelled, reloaded.Get(first.Id)!.Status);
        Assert.Contains("restart", reloaded.Get(first.Id)!.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsInvalidMissions(string task)
    {
        using var fixture = new StudioFixture();
        Assert.Throws<StudioValidationException>(() => fixture.Missions.Submit(new(task)));
        Assert.Throws<StudioValidationException>(() => fixture.Missions.Submit(new("Hello", AgentId: "InventedAgent")));
        Assert.Throws<StudioValidationException>(() => fixture.Missions.Submit(new("Hello", AgentId: "RouterAgent")));
        Assert.Throws<StudioValidationException>(() => fixture.Missions.Submit(new("Hello", (MissionKind)42)));
        Assert.Throws<StudioValidationException>(() => fixture.Missions.Submit(new("Create app", MissionKind.Project, "GeneralAgent")));
    }

    [Fact]
    public async Task CancelsQueuedMissionWithoutInvokingGeneration()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create an API", MissionKind.Project));
        Assert.True(fixture.Missions.Cancel(mission.Id));
        var generator = new FakeProjectGenerator();
        await fixture.Processor(generator, new FakeSandbox()).ProcessAsync(mission.Id, CancellationToken.None);
        Assert.Equal(0, generator.Calls);
        Assert.Equal(MissionStatus.Cancelled, fixture.Missions.Get(mission.Id)!.Status);
        Assert.False(fixture.Missions.Cancel(mission.Id));
    }

    [Fact]
    public async Task ChatRoutesAndExecutesWithRealEvents()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Write C# code"));
        await fixture.Processor(new FakeProjectGenerator(), new FakeSandbox()).ProcessAsync(mission.Id, CancellationToken.None);
        var result = fixture.Missions.Get(mission.Id)!;
        Assert.Equal(MissionStatus.Completed, result.Status);
        Assert.Equal("DeveloperAgent", result.SelectedAgent);
        Assert.Equal("configured-coder", result.SelectedModel);
        Assert.Equal("Answer", result.Response);
        Assert.False(result.HasWorkspace);
        Assert.Contains(result.Events, item => item.Step == "Routing" && item.Status == StepStatus.Succeeded);
        Assert.Equal(Enumerable.Range(1, result.Events.Count), result.Events.Select(item => item.Sequence));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnavailableOrUnapprovedSandboxNeverExecutesCode(bool available, bool approved)
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create API", MissionKind.Project, ApproveSandboxExecution: approved));
        var sandbox = new FakeSandbox(available);
        await fixture.Processor(new FakeProjectGenerator(), sandbox).ProcessAsync(mission.Id, CancellationToken.None);
        var result = fixture.Missions.Get(mission.Id)!;
        Assert.Equal(available ? MissionStatus.AwaitingApproval : MissionStatus.GeneratedUnverified, result.Status);
        Assert.True(result.HasWorkspace);
        Assert.Equal(5, fixture.Workspaces.List(mission.Id).Count);
        Assert.Empty(sandbox.Calls);
        Assert.All(result.Verification, item => Assert.Null(item.ExitCode));
        Assert.DoesNotContain(result.Events, item => item.Step is "Build" or "Tests" && item.Status == StepStatus.Succeeded);
    }

    [Fact]
    public async Task ApprovedAvailableSandboxRunsBuildThenTests()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create API", MissionKind.Project, ApproveSandboxExecution: true));
        var sandbox = new FakeSandbox(true);
        await fixture.Processor(new FakeProjectGenerator(), sandbox).ProcessAsync(mission.Id, CancellationToken.None);
        Assert.Equal(new[] { "Build", "Tests" }, sandbox.Calls);
        Assert.Equal(MissionStatus.Verified, fixture.Missions.Get(mission.Id)!.Status);
    }

    [Fact]
    public async Task CorrectionAttemptsAreBoundedAndNeverRunTestsAfterFailedBuild()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create API", MissionKind.Project, ApproveSandboxExecution: true));
        var generator = new FakeProjectGenerator((call, previous, diagnostics, _) =>
        {
            if (call > 1)
            {
                Assert.NotNull(previous);
                Assert.Contains("CS1002", diagnostics);
            }
            return Task.FromResult(StudioFixture.Manifest());
        });
        var sandbox = new FakeSandbox(true) { Result = stage => new(stage, "Failed", 1, "CS1002 compilation failed") };
        await fixture.Processor(generator, sandbox).ProcessAsync(mission.Id, CancellationToken.None);
        Assert.Equal(4, generator.Calls);
        Assert.Equal(3, fixture.Missions.Get(mission.Id)!.CorrectionAttempts);
        Assert.Equal(MissionStatus.BuildFailed, fixture.Missions.Get(mission.Id)!.Status);
        Assert.Equal(new[] { "Build", "Build", "Build", "Build" }, sandbox.Calls);
    }

    [Fact]
    public async Task InvalidManifestCanBeCorrectedBeforeAnyExecution()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create API", MissionKind.Project));
        var generator = new FakeProjectGenerator((call, _, diagnostics, _) =>
        {
            if (call == 1)
                return Task.FromResult(new ProjectManifest("Bad", [new("../bad.cs", "bad")]));
            Assert.NotNull(diagnostics);
            return Task.FromResult(StudioFixture.Manifest());
        });
        await fixture.Processor(generator, new FakeSandbox()).ProcessAsync(mission.Id, CancellationToken.None);
        Assert.Equal(2, generator.Calls);
        Assert.Equal(MissionStatus.GeneratedUnverified, fixture.Missions.Get(mission.Id)!.Status);
    }

    [Fact]
    public async Task CorrectionCanRecoverFromFailedTests()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create API", MissionKind.Project, ApproveSandboxExecution: true));
        var tests = 0;
        var sandbox = new FakeSandbox(true)
        {
            Result = stage => stage == "Tests" && ++tests == 1
                ? new(stage, "Failed", 1, "Assertion failed")
                : new(stage, "Succeeded", 0, "OK", stage == "Tests" ? 1 : null)
        };
        var generator = new FakeProjectGenerator();
        await fixture.Processor(generator, sandbox).ProcessAsync(mission.Id, CancellationToken.None);
        Assert.Equal(2, generator.Calls);
        Assert.Equal(new[] { "Build", "Tests", "Build", "Tests" }, sandbox.Calls);
        Assert.Equal(MissionStatus.Verified, fixture.Missions.Get(mission.Id)!.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovalResumesWithoutRegenerationOrRefusesWithoutExecution(bool approved)
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create library", MissionKind.Project));
        var generator = new FakeProjectGenerator();
        var sandbox = new FakeSandbox(true);
        var processor = fixture.Processor(generator, sandbox);
        await processor.ProcessAsync(mission.Id, CancellationToken.None);
        Assert.Equal(MissionStatus.AwaitingApproval, fixture.Missions.Get(mission.Id)!.Status);
        Assert.Empty(sandbox.Calls);
        var plans = new ExecutionPlanService(fixture.Workspaces, new(), fixture.Options);
        var plan = await plans.GetAsync(mission.Id, CancellationToken.None);
        Assert.True(fixture.Missions.DecideApproval(mission.Id, approved, plan.Fingerprint));
        Assert.False(fixture.Missions.DecideApproval(mission.Id, approved, plan.Fingerprint));
        await processor.ProcessAsync(mission.Id, CancellationToken.None);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(approved ? MissionStatus.Verified : MissionStatus.GeneratedUnverified,
            fixture.Missions.Get(mission.Id)!.Status);
        Assert.Equal(approved ? 2 : 0, sandbox.Calls.Count);
    }

    [Fact]
    public async Task ApprovalSurvivesRestartButChangedSourcesInvalidateIt()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create library", MissionKind.Project));
        var sandbox = new FakeSandbox(true);
        var processor = fixture.Processor(new FakeProjectGenerator(), sandbox);
        await processor.ProcessAsync(mission.Id, CancellationToken.None);
        using (var reloaded = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry))
        {
            Assert.Equal(MissionStatus.AwaitingApproval, reloaded.Get(mission.Id)!.Status);
            Assert.True(reloaded.Cancel(mission.Id));
            Assert.DoesNotContain(reloaded.Get(mission.Id)!.Events.GroupBy(item => item.Step).Select(group => group.Last()),
                item => item.Status == StepStatus.Running);
        }
        var plan = await new ExecutionPlanService(fixture.Workspaces, new(), fixture.Options)
            .GetAsync(mission.Id, CancellationToken.None);
        Assert.True(fixture.Missions.DecideApproval(mission.Id, true, plan.Fingerprint));
        await fixture.Workspaces.WriteAsync(mission.Id, StudioFixture.Manifest().Files.Select(file =>
            file.Path == "src/App/Program.cs" ? file with { Content = file.Content + "\n// changed" } : file).ToArray(),
            CancellationToken.None);
        await Assert.ThrowsAsync<StudioValidationException>(() => processor.ProcessAsync(mission.Id, CancellationToken.None));
        Assert.Empty(sandbox.Calls);
    }

    [Fact]
    public async Task TargetedCorrectionPreservesUnchangedFilesAndTests()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create API", MissionKind.Project, ApproveSandboxExecution: true));
        var generator = new FakeProjectGenerator((call, _, _, _) => Task.FromResult(call == 1
            ? StudioFixture.Manifest() : new ProjectManifest("Targeted fix",
                [StudioFixture.Manifest().Files[1] with { Content = StudioFixture.Manifest().Files[1].Content + "\n// fixed" }])));
        var builds = 0;
        var sandbox = new FakeSandbox(true)
        {
            Result = stage => stage == "Build" && ++builds == 1
                ? new(stage, "Failed", 1, "CS1002")
                : new(stage, "Succeeded", 0, "OK", stage == "Tests" ? 1 : null)
        };
        await fixture.Processor(generator, sandbox).ProcessAsync(mission.Id, CancellationToken.None);
        Assert.Equal(MissionStatus.Verified, fixture.Missions.Get(mission.Id)!.Status);
        Assert.Equal(5, fixture.Workspaces.List(mission.Id).Count);
        Assert.Equal(new[] { 1, 2, 1 }, fixture.Missions.Get(mission.Id)!.Verification.Select(item => item.Attempt));
        var test = await fixture.Workspaces.ReadAsync(mission.Id, "tests/App.Tests/UnitTests.cs", CancellationToken.None);
        Assert.Equal(StudioFixture.Manifest().Files[3].Content, test.Content);
        Assert.All(fixture.Missions.Get(mission.Id)!.Verification,
            result => Assert.True(result.DurationMilliseconds >= 0 && result.Attempt > 0));
    }

    [Fact]
    public async Task ZeroTestsCannotProduceVerifiedAndTestsFailureIsDistinct()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create API", MissionKind.Project, ApproveSandboxExecution: true));
        var sandbox = new FakeSandbox(true) { Result = stage => new(stage, "Succeeded", 0, "Zero tests", 0) };
        await fixture.Processor(new FakeProjectGenerator(), sandbox, fixture.Options with { MaxCorrectionAttempts = 0 })
            .ProcessAsync(mission.Id, CancellationToken.None);
        Assert.Equal(MissionStatus.TestsFailed, fixture.Missions.Get(mission.Id)!.Status);
    }

    [Fact]
    public async Task ValidationCorrectionBeforeApprovalDoesNotCountAsAContainerAttempt()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create library", MissionKind.Project));
        var generator = new FakeProjectGenerator((call, _, _, _) => Task.FromResult(call == 1
            ? new ProjectManifest("Invalid", [new("../bad.cs", "not allowed")]) : StudioFixture.Manifest()));
        var sandbox = new FakeSandbox(true);
        var processor = fixture.Processor(generator, sandbox);
        await processor.ProcessAsync(mission.Id, CancellationToken.None);
        var plan = await new ExecutionPlanService(fixture.Workspaces, new(), fixture.Options)
            .GetAsync(mission.Id, CancellationToken.None);
        Assert.True(fixture.Missions.DecideApproval(mission.Id, true, plan.Fingerprint));
        await processor.ProcessAsync(mission.Id, CancellationToken.None);
        var result = fixture.Missions.Get(mission.Id)!;
        Assert.Equal(MissionStatus.Verified, result.Status);
        Assert.Equal(1, result.CorrectionAttempts);
        Assert.All(result.Verification, item => Assert.Equal(1, item.Attempt));
    }

    [Fact]
    public async Task GenerationCancellationPropagatesWithoutStartingBuild()
    {
        using var fixture = new StudioFixture();
        using var cancellation = new CancellationTokenSource();
        var generator = new FakeProjectGenerator((_, _, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(StudioFixture.Manifest());
        });
        var sandbox = new FakeSandbox(true);
        var mission = fixture.Missions.Submit(new("Create API", MissionKind.Project, ApproveSandboxExecution: true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Processor(generator, sandbox).ProcessAsync(mission.Id, cancellation.Token));
        Assert.Empty(sandbox.Calls);
        Assert.Empty(fixture.Workspaces.List(mission.Id));
    }

    [Fact]
    public void CapacityLimitIsEnforcedBeforeCreatingAnotherMission()
    {
        using var fixture = new StudioFixture();
        using var store = new MissionStore(fixture.Options with { MaxMissions = 1 }, fixture.Platform, fixture.Registry);
        store.Submit(new("First"));
        Assert.Throws<StudioCapacityException>(() => store.Submit(new("Second")));
        Assert.Single(store.List());
    }
}
