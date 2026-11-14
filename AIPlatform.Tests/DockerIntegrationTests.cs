using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure;
using AIPlatform.Infrastructure.Studio;
using Xunit.Abstractions;

namespace AIPlatform.Tests;

public sealed class RealDockerFactAttribute : FactAttribute
{
    public RealDockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AIPLATFORM_TEST_DOCKER") != "1")
            Skip = "Opt-in real Docker execution: set AIPLATFORM_TEST_DOCKER=1 after reviewing the offline execution plan.";
    }
}

public sealed class RealOllamaDockerFactAttribute : FactAttribute
{
    public RealOllamaDockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AIPLATFORM_TEST_DOCKER") != "1" ||
            Environment.GetEnvironmentVariable("AIPLATFORM_TEST_OLLAMA") != "1")
            Skip = "Opt-in real Ollama correction and Docker execution: enable both flags after human authorization.";
    }
}

[Trait("Category", "DockerIntegration")]
public sealed class DockerIntegrationTests(ITestOutputHelper output)
{
    private static SandboxOptions Options => new()
    {
        Enabled = true,
        Image = Environment.GetEnvironmentVariable("AIPLATFORM_TEST_IMAGE") ?? "aiplatform-sandbox:net10"
    };

    internal static ProjectManifest Calculator(bool broken = false, bool failingTest = false, bool slow = false)
    {
        var source = broken
            ? "namespace App; public static class Calculator { public static int Add(int a, int b) => a + ; public static int Subtract(int a, int b) => a-b; }"
            : "namespace App; public static class Calculator { public static int Add(int a, int b) => a+b; public static int Subtract(int a, int b) => a-b; }";
        var test = $$"""
            using Xunit;
            using App;
            public class CalculatorTests
            {
                [Fact] public void Addition() { Assert.Equal({{(failingTest ? 5 : 4)}}, Calculator.Add(2, 2)); }
                [Fact] public void Subtraction() { {{(slow ? "System.Threading.Thread.Sleep(60000);" : "")}} Assert.Equal(3, Calculator.Subtract(5, 2)); }
                [Fact] public void Isolation()
                {
                    var status = System.IO.File.ReadAllText("/proc/self/status");
                    Assert.Contains("NoNewPrivs:\t1", status);
                    Assert.Contains("CapEff:\t0000000000000000", status);
                    Assert.Contains("Uid:\t10001\t10001\t10001\t10001", status);
                    Assert.Equal(new[] { "lo" }, System.IO.Directory.GetDirectories("/sys/class/net").Select(System.IO.Path.GetFileName));
                    Assert.ThrowsAny<System.IO.IOException>(() => System.IO.File.WriteAllText("/source/untrusted.txt", "forbidden"));
                    Assert.ThrowsAny<System.IO.IOException>(() => System.IO.File.WriteAllText("/root-write.txt", "forbidden"));
                    System.IO.File.WriteAllText("/tmp/allowed.txt", "ephemeral");
                }
            }
            """;
        return new("Deterministic Calculator with arithmetic and isolation assertions.",
        [
            new("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><OutputType>Library</OutputType></PropertyGroup></Project>
                """),
            new("src/App/Program.cs", source),
            StudioFixture.Manifest().Files[2] with { Content = StudioFixture.Manifest().Files[2].Content.Replace(
                "<IsTestProject>true</IsTestProject>", "<IsTestProject>true</IsTestProject><ImplicitUsings>enable</ImplicitUsings>") },
            new("tests/App.Tests/UnitTests.cs", test)
        ]);
    }

    [RealDockerFact]
    public async Task FullStackRestoreUsesEphemeralObjInsteadOfInheritedRootOwnedSeed()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var image = "aiplatform-fullstack-permissions-test:" + Guid.NewGuid().ToString("N");
        var name = "aiplatform-fullstack-permissions-" + Guid.NewGuid().ToString("N");
        var process = new ContainerProcess();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var build = await process.RunAsync([
                "build", "--pull=false", "--network=none", "--target", "seed",
                "--file", Path.Combine(root, "sandbox", "FullStack.Dockerfile"),
                "--tag", image, Path.Combine(root, "sandbox")
            ], timeout.Token);
            output.WriteLine(build.Output);
            Assert.True(build.ExitCode == 0, build.Output);

            var probe = await process.RunAsync([
                "run", "--pull=never", "--name", name,
                "--network=none", "--read-only", "--cap-drop=ALL",
                "--security-opt=no-new-privileges:true", "--user", "10001:10001",
                "--memory=1024m", "--memory-swap=1024m", "--cpus=1", "--pids-limit=128",
                "--tmpfs", "/tmp:rw,nosuid,nodev,size=512m,uid=10001,gid=10001",
                "--entrypoint", "sh", image, "-c", """
                set -eu
                test "$(id -u)" = 10001
                stat -c '%u:%g %a' /seed/obj | grep -Fx '0:0 755'
                test ! -w /seed/obj
                test "$(ls /sys/class/net)" = lo
                grep -Eq 'NoNewPrivs:[[:space:]]+1' /proc/self/status
                grep -Eq 'CapEff:[[:space:]]+0000000000000000' /proc/self/status
                if dotnet restore /seed/SandboxPackages.csproj --force \
                    --configfile /opt/fullstack-seed/FullStackNuGet.Config \
                    --source /opt/empty-feed --packages /opt/nuget \
                    -p:NuGetAudit=false >/tmp/inherited-seed.log 2>&1; then
                    echo 'Expected inherited /seed/obj to reject writes'; exit 1
                fi
                grep -q '/seed/obj/' /tmp/inherited-seed.log
                grep -Eq 'Permission denied|Access to the path|Read-only file system' /tmp/inherited-seed.log
                echo INHERITED_SEED_WRITE_FAILURE_REPRODUCED
                cp -R /opt/nuget /tmp/fullstack-nuget
                if sh /opt/fullstack-restore.sh /opt/empty-feed >/tmp/restore.log 2>&1; then
                    echo 'Expected missing SQLite packages in the base image'; exit 1
                fi
                cat /tmp/restore.log
                grep -q 'error NU1101' /tmp/restore.log
                if grep -Eq 'Permission denied|Access to the path|Read-only file system|error NU1301' /tmp/restore.log; then
                    echo 'Restore used forbidden storage or network sources'; exit 1
                fi
                test -s /tmp/fullstack-obj/project.assets.json
                test ! -w /opt/fullstack-seed
                test ! -w /opt/nuget
                echo FULLSTACK_EPHEMERAL_RESTORE_OK
                """.ReplaceLineEndings("\n")
            ], timeout.Token);
            output.WriteLine(probe.Output);
            Assert.True(probe.ExitCode == 0, probe.Output);
            Assert.Contains("FULLSTACK_EPHEMERAL_RESTORE_OK", probe.Output, StringComparison.Ordinal);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var removed = await process.RunAsync(["rm", "--force", name], cleanup.Token);
            var removedImage = await process.RunAsync(["image", "rm", image], cleanup.Token);
            Assert.True(removed.ExitCode == 0 ||
                removed.Output.Contains("No such container", StringComparison.OrdinalIgnoreCase), removed.Output);
            Assert.True(removedImage.ExitCode == 0 ||
                removedImage.Output.Contains("No such image", StringComparison.OrdinalIgnoreCase), removedImage.Output);
        }
    }

    [RealDockerFact]
    public async Task FullStackReferenceBuildsTestsAndIntegratesOffline()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("Deterministic full-stack infrastructure reference",
            MissionKind.Project, TemplateId: FullStackTemplate.Id));
        var files = FullStackTemplate.Files;
        fixture.Workspaces.Create(mission.Id);
        await fixture.Workspaces.WriteAsync(mission.Id, files, default, FullStackTemplate.Id);
        var options = Options with { Image = FullStackTemplate.Image };
        var process = new ObservingProcess();
        var plans = new ExecutionPlanService(fixture.Workspaces, options, fixture.Options, fixture.Store, process);
        var plan = await plans.GetAsync(mission.Id, default);
        Assert.StartsWith("sha256:", plan.Image);
        if (Environment.GetEnvironmentVariable("AIPLATFORM_TEST_FULLSTACK_IMAGE") is { } approvedImage)
            Assert.Equal(approvedImage, plan.Image);
        fixture.Store.Update(mission.Id, snapshot => snapshot with
        {
            HasWorkspace = true, Status = MissionStatus.AwaitingApproval,
            Workflow = snapshot.Workflow! with
            {
                Revisions = [new(1, DateTimeOffset.UtcNow, "Trusted deterministic template; no LLM.", plan.Fingerprint, files)]
            }
        });
        Assert.True(fixture.Store.DecideApproval(mission.Id, true, plan.Fingerprint));
        var runner = new DockerSandboxRunner(options, fixture.Workspaces, process, fixture.Store, plans);
        foreach (var stage in new[]
                 { "BackendBuild", "BackendTests", "TypeScript", "FrontendBuild", "FrontendTests", "Integration" })
        {
            var result = await runner.RunAsync(mission.Id, stage, default);
            output.WriteLine(stage + ": " + result.Status + "\n" + result.Log);
            Assert.True(result.Status == "Succeeded", result.Log);
            if (stage is "BackendTests" or "FrontendTests" or "Integration")
            {
                Assert.Equal(stage == "Integration" ? 1 : 2, result.TotalTests);
                Assert.Equal(0, result.FailedTests);
            }
        }

        Assert.Equal(6, process.Names.Count);
        await process.AssertRemovedAsync();
        foreach (var file in files)
            Assert.Equal(file.Content, (await fixture.Workspaces.ReadAsync(mission.Id, file.Path, default)).Content);
        Assert.Empty(fixture.Provider.Requests);
    }

    [RealDockerFact]
    public async Task ReactReferenceBuildsAndTestsWithWritableCachesAndReadOnlyDependencies()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Deterministic React runner reference", MissionKind.Project,
            TemplateId: ReactTemplate.Id));
        var files = ReactReferenceFiles();
        fixture.Workspaces.Create(mission.Id);
        await fixture.Workspaces.WriteAsync(mission.Id, files, default, ReactTemplate.Id);
        var options = Options with { Image = ReactTemplate.Image };
        var process = new ObservingProcess();
        var plans = new ExecutionPlanService(fixture.Workspaces, options, fixture.Options, fixture.Store, process);
        var plan = await plans.GetAsync(mission.Id, default);
        Assert.StartsWith("sha256:", plan.Image);
        fixture.Store.Update(mission.Id, snapshot => snapshot with
        {
            HasWorkspace = true, Status = MissionStatus.AwaitingApproval,
            Workflow = snapshot.Workflow! with
            {
                Revisions = [new(1, DateTimeOffset.UtcNow, "Hand-written reference; no LLM.", plan.Fingerprint, files)]
            }
        });
        Assert.True(fixture.Store.DecideApproval(mission.Id, true, plan.Fingerprint));
        var runner = new DockerSandboxRunner(options, fixture.Workspaces, process, fixture.Store, plans);
        foreach (var stage in new[] { "TypeScript", "Build", "Tests" })
        {
            var result = await runner.RunAsync(mission.Id, stage, default);
            output.WriteLine(stage + ": " + result.Status + "\n" + result.Log);
            Assert.True(result.Status == "Succeeded", result.Log);
            if (stage == "Tests")
            {
                Assert.Equal(10, result.TotalTests);
                Assert.Equal(0, result.FailedTests);
            }
        }

        var name = "aiplatform-react-permissions-" + Guid.NewGuid().ToString("N");
        var arguments = runner.CreateArguments(name, fixture.Workspaces.GetDirectory(mission.Id),
            "Build", plan.Image, ReactTemplate.Id).ToArray();
        arguments[^1] += """
             && node --input-type=module -e '
            import assert from "node:assert/strict";
            import fs from "node:fs";
            assert.equal(process.getuid(), 10001);
            const status = fs.readFileSync("/proc/self/status", "utf8");
            assert.match(status, /NoNewPrivs:\s+1/);
            assert.match(status, /CapEff:\s+0000000000000000/);
            assert.deepEqual(fs.readdirSync("/sys/class/net"), ["lo"]);
            assert.equal(fs.lstatSync("/work/node_modules").isDirectory(), true);
            for (const dir of ["/tmp", "/work", "/work/node_modules/.vite-temp", "/work/node_modules/.vite"]) {
              fs.mkdirSync(dir, {recursive: true});
              fs.writeFileSync(dir + "/reference-probe", "ephemeral");
            }
            for (const path of ["/root-probe", "/source/reference-probe", "/opt/aiplatform/node_modules/reference-probe"]) {
              assert.throws(() => fs.writeFileSync(path, "forbidden"), /EROFS|EACCES|ENOENT/);
            }
            console.log("REFERENCE_ISOLATION_OK");
            '
            """;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            var probe = await process.RunAsync(arguments, timeout.Token);
            output.WriteLine(probe.Output);
            Assert.True(probe.ExitCode == 0, probe.Output);
            Assert.Contains("REFERENCE_ISOLATION_OK", probe.Output);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var removed = await process.RunAsync(["rm", "--force", name], cleanup.Token);
            Assert.True(removed.ExitCode == 0 || removed.Output.Contains("No such container",
                StringComparison.OrdinalIgnoreCase), removed.Output);
        }
        Assert.Equal(4, process.Names.Count);
        await process.AssertRemovedAsync();
        Assert.Equal(files.Count, fixture.Workspaces.List(mission.Id).Count);
        foreach (var file in files)
            Assert.Equal(file.Content, (await fixture.Workspaces.ReadAsync(mission.Id, file.Path, default)).Content);
        Assert.Empty(fixture.Provider.Requests);
    }

    private static IReadOnlyList<GeneratedFile> ReactReferenceFiles() => ReactTemplate.Files.Select(file => file.Path switch
    {
        "src/tasks.ts" => file with { Content = """
            export interface Task { id: string; title: string; completed: boolean }
            export type Filter = 'all' | 'active' | 'completed'
            export function visibleTasks(tasks: Task[], filter: Filter): Task[] {
              return tasks.filter(task => filter === 'all' || (filter === 'completed' ? task.completed : !task.completed))
            }
            export function addTask(tasks: Task[], title: string): Task[] {
              return title.trim() ? [...tasks, { id: crypto.randomUUID(), title: title.trim(), completed: false }] : tasks
            }
            export function deleteTask(tasks: Task[], id: string): Task[] {
              return tasks.filter(task => task.id !== id)
            }
            export function toggleTask(tasks: Task[], id: string): Task[] {
              return tasks.map(task => task.id === id ? { ...task, completed: !task.completed } : task)
            }
            """ },
        "src/App.tsx" => file with { Content = """
            import { useState } from 'react'
            import type { Task, Filter } from './tasks'
            import { addTask, deleteTask, toggleTask, visibleTasks } from './tasks'
            import TaskItem from './components/TaskItem'
            export default function App() {
              const [tasks, setTasks] = useState<Task[]>([])
              const [title, setTitle] = useState('')
              const [filter, setFilter] = useState<Filter>('all')
              return <main><h1>Reference</h1><form onSubmit={event => {
                event.preventDefault(); setTasks(previous => addTask(previous, title)); setTitle('')
              }}>
                <input aria-label="Nouvelle tâche" value={title} onChange={event => setTitle(event.target.value)} />
                <button type="submit">Ajouter</button>
              </form>
              <select aria-label="Filtrer les tâches" value={filter}
                onChange={event => {
                  const value = event.target.value
                  if (value === 'all' || value === 'active' || value === 'completed') setFilter(value)
                }}>
                <option value="all">Toutes</option><option value="active">Actives</option>
                <option value="completed">Terminées</option>
              </select>
              <ul>{visibleTasks(tasks, filter).map(task => <TaskItem key={task.id} task={task}
                onToggle={id => setTasks(previous => toggleTask(previous, id))}
                onDelete={id => setTasks(previous => deleteTask(previous, id))} />)}</ul></main>
            }
            """ },
        _ => file
    }).ToArray();

    [RealDockerFact]
    public async Task BuildsAndTestsCalculatorWithActualIsolationAndCleanup()
    {
        using var fixture = new StudioFixture();
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        await fixture.Workspaces.WriteAsync(id, Calculator().Files, CancellationToken.None);
        var process = new ObservingProcess();
        var runner = new DockerSandboxRunner(Options, fixture.Workspaces, process);
        var availability = await runner.GetAvailabilityAsync(CancellationToken.None);
        Assert.True(availability.Available, availability.Reason);
        var build = await runner.RunAsync(id, "Build", CancellationToken.None);
        output.WriteLine(build.Log);
        Assert.True(build.Status == "Succeeded", build.Log);
        var tests = await runner.RunAsync(id, "Tests", CancellationToken.None);
        output.WriteLine(tests.Log);
        Assert.True(tests.Status == "Succeeded", tests.Log);
        Assert.Equal(3, tests.TotalTests);
        Assert.Equal(0, tests.FailedTests);
        Assert.Equal(2, process.Names.Count);
        await process.AssertRemovedAsync();
        Assert.Equal(5, fixture.Workspaces.List(id).Count);
    }

    [RealDockerFact]
    public async Task CorrectsActualCompilerErrorUsingTargetedReplacementAndNewContainers()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create Calculator library", MissionKind.Project,
            "DeveloperAgent", ApproveSandboxExecution: true));
        var generator = new FakeProjectGenerator((call, previous, diagnostics, _) =>
        {
            if (call == 1)
                return Task.FromResult(Calculator(broken: true));
            Assert.Contains("error CS", diagnostics);
            Assert.NotNull(previous);
            return Task.FromResult(new ProjectManifest("Corrected arithmetic syntax.", [Calculator().Files[1]]));
        });
        var process = new ObservingProcess();
        var runner = new DockerSandboxRunner(Options, fixture.Workspaces, process);
        await fixture.Processor(generator, runner).ProcessAsync(mission.Id, CancellationToken.None);
        var result = fixture.Missions.Get(mission.Id)!;
        foreach (var item in result.Verification)
            output.WriteLine(item.Log);
        Assert.Equal(MissionStatus.Verified, result.Status);
        Assert.Equal(1, result.CorrectionAttempts);
        Assert.Equal(new[] { "Failed", "Succeeded", "Succeeded" }, result.Verification.Select(item => item.Status));
        Assert.Equal(3, process.Names.Distinct().Count());
        await process.AssertRemovedAsync();
    }

    [RealDockerFact]
    public async Task ReportsRealFailingTestsWithoutCallingThemVerified()
    {
        using var fixture = new StudioFixture();
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        await fixture.Workspaces.WriteAsync(id, Calculator(failingTest: true).Files, CancellationToken.None);
        var process = new ObservingProcess();
        var runner = new DockerSandboxRunner(Options, fixture.Workspaces, process);
        var tests = await runner.RunAsync(id, "Tests", CancellationToken.None);
        output.WriteLine(tests.Log);
        Assert.Equal("Failed", tests.Status);
        Assert.Equal(3, tests.TotalTests);
        Assert.Equal(1, tests.FailedTests);
        Assert.NotEqual(0, tests.ExitCode);
        await process.AssertRemovedAsync();
    }

    [RealOllamaDockerFact]
    [Trait("Category", "OllamaIntegration")]
    public async Task ActualQwenCorrectsSeededCompilerErrorThenDockerBuildAndTestsPass()
    {
        using var fixture = new StudioFixture();
        var platform = fixture.Platform with
        {
            Models = fixture.Platform.Models.Select(model => model with
            {
                Name = model.Id == "developer" ? "qwen2.5-coder:7b" : "qwen3:4b"
            }).ToList()
        };
        var registry = new AgentRegistry(platform);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OllamaProvider(http, new()
        {
            BaseUrl = "http://localhost:11434", RequestTimeoutSeconds = 180, KeepAliveSeconds = 120
        });
        var resolver = new LlmProviderResolver([provider]);
        var generator = new SeededErrorGenerator(new ProjectGenerator(registry, resolver, fixture.Options));
        var process = new ObservingProcess();
        var sandbox = new DockerSandboxRunner(Options, fixture.Workspaces, process);
        var mission = fixture.Missions.Submit(new(
            "Crée une bibliothèque .NET 10 avec une classe Calculator et des tests pour l'addition et la soustraction.",
            MissionKind.Project, "DeveloperAgent", ApproveSandboxExecution: true));
        var processor = new MissionProcessor(fixture.Missions, registry, new LlmAgentRouter(registry, resolver),
            resolver, generator, fixture.Workspaces, sandbox, fixture.Options,
            new ExecutionPlanService(fixture.Workspaces, Options, fixture.Options));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await processor.ProcessAsync(mission.Id, timeout.Token);
        var result = fixture.Missions.Get(mission.Id)!;
        foreach (var item in result.Verification)
            output.WriteLine(item.Log);
        output.WriteLine($"Real model: {result.SelectedModel}; corrections: {result.CorrectionAttempts}; status: {result.Status}");
        Assert.Equal("qwen2.5-coder:7b", result.SelectedModel);
        Assert.Equal(MissionStatus.Verified, result.Status);
        Assert.InRange(result.CorrectionAttempts, 1, 3);
        Assert.Equal("Failed", result.Verification[0].Status);
        Assert.True(result.Verification[0].ErrorCount > 0);
        Assert.Equal(3, result.Verification.Last().TotalTests);
        Assert.Equal(Calculator().Files[3].Content,
            (await fixture.Workspaces.ReadAsync(mission.Id, "tests/App.Tests/UnitTests.cs", timeout.Token)).Content);
        await process.AssertRemovedAsync();
    }

    private sealed class SeededErrorGenerator(IProjectGenerator real) : IProjectGenerator
    {
        private bool seeded;
        public Task<ProjectManifest> GenerateAsync(AgentDefinition agent, string task,
            ProjectManifest? previous, string? diagnostics, CancellationToken token)
        {
            if (seeded)
                return real.GenerateAsync(agent, task, previous, diagnostics, token);
            seeded = true;
            return Task.FromResult(Calculator(broken: true));
        }
    }

    [RealDockerFact]
    public async Task CancelsAndTimesOutRealContainersAndAlwaysRemovesThem()
    {
        using var fixture = new StudioFixture();
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        await fixture.Workspaces.WriteAsync(id, Calculator(slow: true).Files, CancellationToken.None);
        var process = new ObservingProcess();
        var runner = new DockerSandboxRunner(Options, fixture.Workspaces, process);
        using var cancellation = new CancellationTokenSource();
        var running = runner.RunAsync(id, "Tests", cancellation.Token);
        try
        {
            await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        finally
        {
            cancellation.Cancel();
            try { await running; }
            catch (OperationCanceledException) { }
        }
        await process.AssertRemovedAsync();
        var timed = new DockerSandboxRunner(Options with { TimeoutSeconds = 5 }, fixture.Workspaces, process);
        var result = await timed.RunAsync(id, "Tests", CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.Null(result.ExitCode);
        Assert.Contains("5s limit", result.Log);
        await process.AssertRemovedAsync();
    }

    private sealed class ObservingProcess : IContainerProcess
    {
        private readonly ContainerProcess inner = new();
        public List<string> Names { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
        {
            if (arguments[0] != "run")
                return await inner.RunAsync(arguments, token);
            var name = arguments[arguments.ToList().IndexOf("--name") + 1];
            Names.Add(name);
            var running = inner.RunAsync(arguments, token);
            try
            {
                using var inspectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                ContainerProcessResult inspection;
                do
                {
                    inspection = await inner.RunAsync(["inspect", name], inspectTimeout.Token);
                    if (inspection.ExitCode != 0)
                        await Task.Delay(50, inspectTimeout.Token);
                } while (inspection.ExitCode != 0 && !running.IsCompleted);
                Assert.True(inspection.ExitCode == 0, inspection.Output +
                    (running.IsCompletedSuccessfully ? (await running).Output : ""));
                using var json = JsonDocument.Parse(inspection.Output);
                var container = json.RootElement[0];
                var host = container.GetProperty("HostConfig");
                Assert.Equal("none", host.GetProperty("NetworkMode").GetString());
                Assert.True(host.GetProperty("ReadonlyRootfs").GetBoolean());
                Assert.False(host.GetProperty("Privileged").GetBoolean());
                Assert.Equal(2L * 1024 * 1024 * 1024, host.GetProperty("Memory").GetInt64());
                Assert.Equal(2L * 1024 * 1024 * 1024, host.GetProperty("MemorySwap").GetInt64());
                Assert.Equal(2_000_000_000, host.GetProperty("NanoCpus").GetInt64());
                Assert.Equal(128, host.GetProperty("PidsLimit").GetInt64());
                Assert.Contains(host.GetProperty("CapDrop").EnumerateArray(), item => item.GetString() == "ALL");
                Assert.Contains(host.GetProperty("SecurityOpt").EnumerateArray(),
                    item => item.GetString() == "no-new-privileges:true");
                Assert.Equal("10001:10001", container.GetProperty("Config").GetProperty("User").GetString());
                var bind = Assert.Single(container.GetProperty("Mounts").EnumerateArray(),
                    item => item.GetProperty("Type").GetString() == "bind");
                Assert.False(bind.GetProperty("RW").GetBoolean());
                Assert.Equal("/source", bind.GetProperty("Destination").GetString());
                Assert.DoesNotContain(container.GetProperty("Mounts").EnumerateArray(),
                    item => item.GetProperty("Destination").GetString()!.Contains("docker.sock"));
                Started.TrySetResult();
            }
            catch
            {
                // Observe the owned docker process before propagating an assertion; the runner's finally removes it.
                await running;
                throw;
            }
            return await running;
        }

        public async Task AssertRemovedAsync()
        {
            foreach (var name in Names)
            {
                var result = await inner.RunAsync(["inspect", name], CancellationToken.None);
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("no such", result.Output, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
