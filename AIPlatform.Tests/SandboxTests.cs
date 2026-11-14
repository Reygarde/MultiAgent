using System.ComponentModel;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class SandboxTests
{
    private sealed class ProcessStub(Func<IReadOnlyList<string>, CancellationToken, Task<ContainerProcessResult>> run) : IContainerProcess
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            return run(arguments, cancellationToken);
        }
    }

    [Fact]
    public async Task MissingDockerIsExplicitlyUnavailable()
    {
        using var fixture = new StudioFixture();
        var process = new ProcessStub((_, _) => throw new Win32Exception("Docker not found"));
        var runner = new DockerSandboxRunner(new() { Enabled = true }, fixture.Workspaces, process);
        var result = await runner.GetAvailabilityAsync(CancellationToken.None);
        Assert.False(result.Available);
        Assert.Contains("host execution is forbidden", result.Reason);
        Assert.Equal(new[] { "info", "--format", "{{.OSType}}" }, process.Calls.Single());
    }

    [Fact]
    public async Task DoesNotPullMissingImages()
    {
        using var fixture = new StudioFixture();
        var process = new ProcessStub((arguments, _) => Task.FromResult(arguments[0] == "info"
            ? new ContainerProcessResult(0, "linux\n") : new(1, "No image")));
        var runner = new DockerSandboxRunner(new() { Enabled = true }, fixture.Workspaces, process);
        Assert.False((await runner.GetAvailabilityAsync(CancellationToken.None)).Available);
        Assert.DoesNotContain(process.Calls, arguments => arguments.Contains("pull") || arguments.Contains("run"));
    }

    [Fact]
    public void CommandsContainFixedIsolationAndReadOnlyWorkspaceMount()
    {
        using var fixture = new StudioFixture();
        var process = new ProcessStub((_, _) => throw new InvalidOperationException());
        var runner = new DockerSandboxRunner(new() { Enabled = true }, fixture.Workspaces, process);
        var arguments = runner.CreateArguments("test", fixture.Workspaces.GetDirectory(Guid.NewGuid()), "Build");
        Assert.Contains("--network=none", arguments);
        Assert.Contains("--read-only", arguments);
        Assert.Contains("--cap-drop=ALL", arguments);
        Assert.Contains("--security-opt=no-new-privileges:true", arguments);
        Assert.Contains("--pull=never", arguments);
        Assert.Contains("--memory", arguments);
        Assert.Contains("--memory-swap", arguments);
        Assert.Contains("--pids-limit", arguments);
        Assert.Contains("10001:10001", arguments);
        Assert.Contains(arguments, value => value.StartsWith("type=bind,source=") && value.EndsWith(",target=/source,readonly"));
        Assert.DoesNotContain(arguments, value => value.Contains("docker.sock") || value.Contains("--privileged"));
        Assert.Throws<ArgumentException>(() => runner.CreateArguments("test", "anything", "powershell"));
    }

    [Fact]
    public async Task DisabledSandboxCannotRunAnyCommand()
    {
        using var fixture = new StudioFixture();
        var process = new ProcessStub((_, _) => throw new InvalidOperationException("Must not start"));
        var runner = new DockerSandboxRunner(new(), fixture.Workspaces, process);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Guid.NewGuid(), "Build", CancellationToken.None));
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task SuccessfulTestCommandWithoutRealTestReportIsNotSuccess()
    {
        using var fixture = new StudioFixture();
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        var process = new ProcessStub((_, _) => Task.FromResult(new ContainerProcessResult(0, "No test is available")));
        var runner = new DockerSandboxRunner(new() { Enabled = true }, fixture.Workspaces, process);
        Assert.Equal("Failed", (await runner.RunAsync(id, "Tests", CancellationToken.None)).Status);
        Assert.Equal("rm", process.Calls.Last()[0]);
    }

    [Fact]
    public async Task ParsesPassingTrxAndCleansContainer()
    {
        using var fixture = new StudioFixture();
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        var process = new ProcessStub((arguments, _) => Task.FromResult(new ContainerProcessResult(0,
            arguments[0] == "run" ? "\nAIPLATFORM_TRX_BEGIN\n" +
                "\uFEFF" + """<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult outcome="Passed" /><UnitTestResult outcome="Passed" /><UnitTestResult outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="3" executed="3" passed="3" failed="0" /></ResultSummary></TestRun>""" +
                "\nAIPLATFORM_TRX_END\n" : "removed")));
        var runner = new DockerSandboxRunner(new() { Enabled = true }, fixture.Workspaces, process);
        var result = await runner.RunAsync(id, "Tests", CancellationToken.None);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(3, result.TotalTests);
        Assert.Equal("rm", process.Calls.Last()[0]);
    }

    [Theory]
    [InlineData(0, 0, "")]
    [InlineData(1, 0, "")]
    [InlineData(1, 1, "<UnitTestResult outcome=\"Failed\" />")]
    public async Task EmptyInconsistentOrFailingReportCannotPassDespiteZeroExitCode(int total, int failed, string results)
    {
        using var fixture = new StudioFixture();
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        var report = "\nAIPLATFORM_TRX_BEGIN\n" +
            $"""<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{results}</Results><ResultSummary outcome="Completed"><Counters total="{total}" executed="{total}" passed="{total - failed}" failed="{failed}" /></ResultSummary></TestRun>""" +
            "\nAIPLATFORM_TRX_END\n";
        var process = new ProcessStub((arguments, _) => Task.FromResult(new ContainerProcessResult(0,
            arguments[0] == "run" ? report : "removed")));
        var runner = new DockerSandboxRunner(new() { Enabled = true }, fixture.Workspaces, process);
        Assert.Equal("Failed", (await runner.RunAsync(id, "Tests", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task FullStackCannotExecuteWithoutCanonicalApproval()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("No unapproved execution", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var process = new ProcessStub((arguments, _) => Task.FromResult(new ContainerProcessResult(0,
            arguments[0] == "image" ? "sha256:" + new string('a', 64) : "unexpected")));
        var options = new SandboxOptions { Enabled = true };
        var plans = new ExecutionPlanService(fixture.Workspaces, options, fixture.Options, fixture.Store, process);
        var runner = new DockerSandboxRunner(options, fixture.Workspaces, process, fixture.Store, plans);
        await Assert.ThrowsAsync<StudioValidationException>(() => runner.RunAsync(mission.Id, "BackendBuild", default));
        Assert.DoesNotContain(process.Calls, call => call[0] == "run");
    }

    [Fact]
    public async Task FullStackBackendTestsReadTrxRatherThanVitestResults()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("Full-stack report regression", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        fixture.Workspaces.Create(mission.Id);
        await fixture.Workspaces.WriteAsync(mission.Id, FullStackTemplate.Files, default, FullStackTemplate.Id);
        var report = "\nAIPLATFORM_TRX_BEGIN\n" +
            """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary></TestRun>""" +
            "\nAIPLATFORM_TRX_END\n";
        var process = new ProcessStub((arguments, _) => Task.FromResult(new ContainerProcessResult(0,
            arguments[0] == "image" ? "sha256:" + new string('a', 64) :
            arguments[0] == "run" ? report : "removed")));
        var options = new SandboxOptions { Enabled = true };
        var plans = new ExecutionPlanService(fixture.Workspaces, options, fixture.Options, fixture.Store, process);
        var plan = await plans.GetAsync(mission.Id, default);
        fixture.Store.Update(mission.Id, snapshot => snapshot with
        {
            HasWorkspace = true, Status = MissionStatus.AwaitingApproval,
            Workflow = snapshot.Workflow! with
            {
                Revisions = [new(1, DateTimeOffset.UtcNow, "Report regression.", plan.Fingerprint, FullStackTemplate.Files, plan)]
            }
        });
        Assert.True(fixture.Store.DecideApproval(mission.Id, true, plan.Fingerprint));
        var runner = new DockerSandboxRunner(options, fixture.Workspaces, process, fixture.Store, plans);

        var result = await runner.RunAsync(mission.Id, "BackendTests", default);

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(1, result.TotalTests);
        Assert.Equal(0, result.FailedTests);
        var command = process.Calls.Single(call => call[0] == "run");
        Assert.Contains("sha256:" + new string('a', 64), command);
        Assert.Contains(command, argument => argument.Contains("sha256sum -c --status", StringComparison.Ordinal));
        Assert.Contains(command, argument => argument.Contains("find . -type l -print -quit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanupFailureDisablesFutureContainerExecutions()
    {
        using var fixture = new StudioFixture();
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        var process = new ProcessStub((arguments, _) => Task.FromResult(
            arguments[0] == "rm" ? new ContainerProcessResult(1, "Daemon unreachable") : new(0, "OK")));
        var runner = new DockerSandboxRunner(new() { Enabled = true }, fixture.Workspaces, process);
        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(id, "Build", CancellationToken.None));
        var count = process.Calls.Count;
        Assert.False((await runner.GetAvailabilityAsync(CancellationToken.None)).Available);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(id, "Build", CancellationToken.None));
        Assert.Equal(count, process.Calls.Count);
    }

    [Fact]
    public async Task CancellationForcesContainerCleanup()
    {
        using var fixture = new StudioFixture();
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        using var cancellation = new CancellationTokenSource();
        var process = new ProcessStub((arguments, token) =>
        {
            if (arguments[0] == "run")
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            Assert.False(token.IsCancellationRequested);
            return Task.FromResult(new ContainerProcessResult(0, "removed"));
        });
        var runner = new DockerSandboxRunner(new() { Enabled = true }, fixture.Workspaces, process);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(id, "Build", cancellation.Token));
        Assert.Equal(new[] { "run", "rm" }, process.Calls.Select(arguments => arguments[0]));
    }
}
