using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class ReactTemplateTests
{
    [Fact]
    public async Task TemplatePreservesRealLockfileAndExportsAllReactSourcesWithoutDotnetSolution()
    {
        using var fixture = new WorkflowFixture();
        fixture.Workspaces.ValidateFiles(ReactTemplate.Files, ReactTemplate.Id);
        using var manifest = JsonDocument.Parse(ReactTemplate.Files.Single(file => file.Path == "package.json").Content);
        using var document = JsonDocument.Parse(ReactTemplate.Files.Single(file => file.Path == "package-lock.json").Content);
        var packages = document.RootElement.GetProperty("packages");
        foreach (var kind in new[] { "dependencies", "devDependencies" })
            foreach (var item in manifest.RootElement.GetProperty(kind).EnumerateObject())
            {
                Assert.Equal(item.Value.GetString(), packages.GetProperty("").GetProperty(kind).GetProperty(item.Name).GetString());
                Assert.Equal(item.Value.GetString(), packages.GetProperty("node_modules/" + item.Name).GetProperty("version").GetString());
            }
        Assert.True(packages.EnumerateObject().Count() > 100);
        foreach (var package in packages.EnumerateObject().Where(item => item.Name != ""))
        {
            Assert.StartsWith("https://registry.npmjs.org/", package.Value.GetProperty("resolved").GetString());
            Assert.StartsWith("sha512-", package.Value.GetProperty("integrity").GetString());
        }
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        await fixture.Workspaces.WriteAsync(id, ReactTemplate.Files, default, ReactTemplate.Id);
        Assert.DoesNotContain(fixture.Workspaces.List(id), file => file.Path == "Generated.slnx");
        Assert.Equal(ReactTemplate.Files.Single(file => file.Path == "package-lock.json").Content,
            (await fixture.Workspaces.ReadAsync(id, "package-lock.json", default)).Content);
        using var zip = new ZipArchive(new MemoryStream(await new ProjectExport(fixture.Workspaces, fixture.Options).CreateAsync(id, default)));
        Assert.Equal(ReactTemplate.Files.Count, zip.Entries.Count);
        Assert.Contains(zip.Entries, entry => entry.FullName == "src/App.test.tsx");
    }

    [Theory]
    [InlineData("package.json", """{"scripts":{"postinstall":"bad"},"dependencies":{"unknown":"1.0.0"}}""")]
    [InlineData("package-lock.json", "{}")]
    [InlineData("tsconfig.json", """{"compilerOptions":{"strict":false}}""")]
    [InlineData("vite.config.ts", "export default {}")]
    [InlineData("src/App.tsx", """const token = "secret-value";""")]
    [InlineData("src/App.tsx", "// @ts-nocheck\nexport default function App() { return null }")]
    [InlineData("src/tasks.test.ts", "import { test, expect } from 'vitest'; test('bypass', () => expect(true).toBe(true));")]
    public void ConfigurationDependenciesLockfileAndSecretsAreProtected(string path, string content)
    {
        using var fixture = new WorkflowFixture();
        var files = ReactTemplate.Files.Select(file => file.Path == path ? file with { Content = content } : file).ToArray();
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(files, ReactTemplate.Id));
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(ReactTemplate.Files, DiscordTemplate.Id));
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(
            [.. ReactTemplate.Files, new("../outside.ts", "bad")], ReactTemplate.Id));
    }

    [Fact]
    public async Task IncrementalReactWorkflowRequiresConsentAndRunsAllThreeStages()
    {
        using var fixture = new WorkflowFixture(responseOverride: Respond);
        var mission = fixture.Store.Submit(new("Create task manager", MissionKind.Project, TemplateId: ReactTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var pending = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, pending.Status);
        Assert.False(pending.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(3, fixture.Provider.Requests.Count(request =>
            request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("content", out _)));
        var reviewInput = fixture.Provider.Requests.Last().Messages.Last().Content;
        Assert.DoesNotContain("\"path\":\"package-lock.json\",\"content\"", reviewInput);
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(new[] { "TypeScript", "Build", "Tests" }, fixture.Sandbox.Calls);
        Assert.Equal(MissionStatus.Verified, fixture.Store.Get(mission.Id)!.Status);
    }

    [Fact]
    public async Task TypeScriptFailureStopsBuildAndCorrectionPreservesTestsAndRequiresNewConsent()
    {
        using var fixture = new WorkflowFixture(responseOverride: Respond);
        fixture.Sandbox.Result = stage => stage == "TypeScript"
            ? new(stage, "Failed", 1, "src/App.tsx(1,1): error TS2304: Cannot find name 'missing'.")
            : throw new InvalidOperationException("No later stage may run.");
        var mission = fixture.Store.Submit(new("Create task manager", MissionKind.Project, TemplateId: ReactTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var before = fixture.Store.Get(mission.Id)!;
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var after = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, after.Status);
        Assert.False(after.SandboxApproved);
        Assert.Null(after.ApprovalFingerprint);
        Assert.Equal(new[] { "TypeScript" }, fixture.Sandbox.Calls);
        Assert.Equal(new[] { "src/App.tsx" }, after.Workflow!.CorrectionPaths);
        foreach (var file in before.Workflow!.Draft!.Where(file => ReactTemplate.IsTest(file.Path)))
            Assert.Equal(file.Content, after.Workflow.Draft!.Single(item => item.Path == file.Path).Content);
        Assert.Equal("tsc --noEmit (offline)", after.Verification[0].Diagnostics!.Command);
        Assert.Single(after.Verification[0].Diagnostics!.Items);
        using var correction = JsonDocument.Parse(fixture.Provider.Requests.Last(request =>
            request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("content", out _)).Messages.Last().Content);
        Assert.Empty(correction.RootElement.GetProperty("previousCorrections").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, correction.RootElement.GetProperty("templateBaseline").ValueKind);
        Assert.True(fixture.Store.DecideApproval(mission.Id, false, (await fixture.Plans.GetAsync(mission.Id, default)).Fingerprint));
        Assert.Single(fixture.Sandbox.Calls);
        Assert.Equal(MissionStatus.GeneratedUnverified, fixture.Store.Get(mission.Id)!.Status);
    }

    [Fact]
    public async Task RunnerUsesPinnedImageOfflineAndRejectsChangedImageApproval()
    {
        using var fixture = new WorkflowFixture(responseOverride: Respond);
        var mission = fixture.Store.Submit(new("Create task manager", MissionKind.Project, TemplateId: ReactTemplate.Id));
        var process = new ReactProcess();
        var options = new SandboxOptions { Enabled = true };
        var plans = new ExecutionPlanService(fixture.Workspaces, options, fixture.Options, fixture.Store, process);
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
            fixture.Workspaces, plans, fixture.Sandbox, fixture.Options);
        await processor.ProcessAsync(mission.Id, default);
        var runner = new DockerSandboxRunner(options, fixture.Workspaces, process, fixture.Store, plans);
        var plan = await plans.GetAsync(mission.Id, default);
        Assert.Contains(plan.Operations, operation => operation.Contains("TypeScript"));
        await Assert.ThrowsAsync<StudioValidationException>(() => runner.RunAsync(mission.Id, "TypeScript", default));
        Assert.True(fixture.Store.DecideApproval(mission.Id, true, plan.Fingerprint));
        process.ImageId = "sha256:" + new string('b', 64);
        await Assert.ThrowsAsync<StudioValidationException>(() => runner.RunAsync(mission.Id, "TypeScript", default));
        process.ImageId = plan.Image;
        var files = new List<WorkspaceContent>();
        foreach (var file in fixture.Workspaces.List(mission.Id))
            files.Add(await fixture.Workspaces.ReadAsync(mission.Id, file.Path, default));
        var legacyPlan = JsonSerializer.Serialize(new { files, Image = plan.Image, options.MemoryMegabytes,
            options.CpuCount, options.TimeoutSeconds, fixture.Options.MaxCorrectionAttempts, operations = plan.Operations });
        var legacyFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyPlan)));
        fixture.Store.Update(mission.Id, current => current with { ApprovalFingerprint = legacyFingerprint });
        await Assert.ThrowsAsync<StudioValidationException>(() => runner.RunAsync(mission.Id, "TypeScript", default));
        fixture.Store.Update(mission.Id, current => current with { ApprovalFingerprint = plan.Fingerprint });
        var result = await runner.RunAsync(mission.Id, "TypeScript", default);
        Assert.Equal("Succeeded", result.Status);
        var arguments = process.Calls.Single(args => args[0] == "run");
        Assert.Contains("--network=none", arguments);
        Assert.Contains("--read-only", arguments);
        Assert.Contains("10001:10001", arguments);
        Assert.Contains(plan.Image, arguments);
        Assert.Contains("tsc --noEmit", arguments.Last());
        Assert.DoesNotContain("npm ", arguments.Last());
        Assert.Contains(process.Calls, args => args[0] == "rm");
        foreach (var stage in new[] { "Build", "Tests" })
        {
            var args = runner.CreateArguments("test", fixture.Workspaces.GetDirectory(mission.Id), stage, plan.Image, ReactTemplate.Id);
            Assert.Contains(stage == "Build" ? "vite build" : "vitest run", args.Last());
        }
    }

    [Fact]
    public async Task PersistedReactProjectCanBeReviewedAfterRestartWithoutReusingApproval()
    {
        using var fixture = new WorkflowFixture(responseOverride: Respond);
        var mission = fixture.Store.Submit(new("Create task manager", MissionKind.Project, TemplateId: ReactTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        await fixture.Approve(mission.Id);
        using var restarted = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(MissionStatus.Cancelled, restarted.Get(mission.Id)!.Status);
        Assert.True(restarted.Resume(mission.Id));
        var processor = new CollaborativeProcessor(restarted,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options);
        await processor.ProcessAsync(mission.Id, default);
        Assert.Equal(MissionStatus.AwaitingApproval, restarted.Get(mission.Id)!.Status);
        Assert.False(restarted.Get(mission.Id)!.SandboxApproved);
        Assert.Null(restarted.Get(mission.Id)!.ApprovalFingerprint);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(ReactTemplate.Files.Count, fixture.Workspaces.List(mission.Id).Count);
    }

    [Theory]
    [InlineData(true, 1, 0, "passed", true)]
    [InlineData(true, 0, 0, "passed", false)]
    [InlineData(false, 1, 0, "passed", false)]
    [InlineData(true, 2, 0, "passed", false)]
    [InlineData(true, 1, 1, "failed", false)]
    public void VitestRequiresConsistentNonemptyPassingAssertions(bool success, int total, int failed, string status, bool valid)
    {
        var report = JsonSerializer.Serialize(new { success, numTotalTests = total, numPassedTests = total,
            numFailedTests = failed, numPendingTests = 0, numFailedTestSuites = 0,
            testResults = new[] { new { status, assertionResults = new[] { new { status } } } } });
        Assert.Equal(valid, ReactBuildProfile.TestCounts("\nAIPLATFORM_VITEST_BEGIN\n" + report + "\nAIPLATFORM_VITEST_END\n") is not null);
        Assert.Null(ReactBuildProfile.TestCounts("exit 0"));
    }

    [Fact]
    public void VitestFailureRetainsBoundedDiagnosticAndRelevantSourcePath()
    {
        var report = JsonSerializer.Serialize(new { testResults = new[] { new {
            name = "/work/src/App.test.tsx", assertionResults = new[] { new {
                status = "failed", fullName = "deletes a task", failureMessages = new[] { "Expected task to be removed." } } } } } });
        var result = DiagnosticParser.Parse(new("Tests", "Failed", 1,
            "\nAIPLATFORM_VITEST_BEGIN\n" + report + "\nAIPLATFORM_VITEST_END\n"), ReactTemplate.Id);
        Assert.Equal("vitest run (offline)", result.Command);
        Assert.Equal("src/App.test.tsx", Assert.Single(result.Items).File);
        Assert.Contains("Expected task to be removed.", result.Items[0].Message);
    }

    private static string Respond(LlmRequest request)
    {
        var properties = request.ResponseSchema!.Value.GetProperty("properties");
        object response;
        if (properties.TryGetProperty("decisions", out _)) response = WorkflowFixture.Plan;
        else if (properties.TryGetProperty("content", out _))
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            response = ReactTemplate.Files.Single(file => file.Path == input.RootElement.GetProperty("file").GetProperty("path").GetString());
        }
        else response = new ReviewReport("Simulated review only.", [], "Review", [], "No blocking defect.", [], "Matches plan.", "Verify in Docker.");
        return JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class ReactProcess : IContainerProcess
    {
        public string ImageId { get; set; } = "sha256:" + new string('a', 64);
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            return Task.FromResult(new ContainerProcessResult(0, arguments[0] == "image" ? ImageId : ""));
        }
    }
}
