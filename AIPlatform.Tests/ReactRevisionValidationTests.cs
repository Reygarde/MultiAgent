using System.Text.Json;
using System.Text.Json.Serialization;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIPlatform.Tests;

public sealed class ReactRevisionValidationTests
{
    [Fact]
    public async Task RequiresExplicitSingleUseAuthorizationAndRunsExistingSandboxWithoutMutatingMission()
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        var service = fixture.Service();
        var before = File.ReadAllBytes(fixture.MissionPath);
        var prepared = await service.PrepareAsync(fixture.Id, default);
        Assert.Empty(fixture.Process.Calls);
        Assert.Null(await service.GetReportAsync(fixture.Id, default));
        var request = new AuthorizeReactRevisionValidationRequest(true, prepared.RequestId, prepared.Plan.Fingerprint);
        foreach (var invalid in new[] { request with { Approved = false }, request with { RequestId = Guid.Empty },
                     request with { Fingerprint = "stale" } })
            await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => service.AuthorizeAsync(fixture.Id, invalid, default));
        Assert.Empty(fixture.Process.Calls);

        var report = await service.AuthorizeAsync(fixture.Id, request, default);
        Assert.Equal("Failed", report.Status); // A TypeScript failure does not hide the Vite and Vitest diagnostics.
        Assert.Equal(new[] { "TypeScript", "Build", "Tests" }, report.Results.Select(result => result.Stage));
        Assert.Equal(new int?[] { 2, 0, 0 }, report.Results.Select(result => result.ExitCode));
        Assert.Equal(1, report.Results[2].TotalTests);
        Assert.Contains(report.Results[0].Diagnostics!.Items, item => item.Code == "TS2322");
        var runs = fixture.Process.Calls.Where(args => args[0] == "run").ToArray();
        Assert.Equal(3, runs.Length);
        for (var i = 0; i < runs.Length; i++)
        {
            var actual = runs[i];
            var mount = Assert.Single(actual, argument => argument.StartsWith("type=bind,"));
            Assert.StartsWith($"type=bind,source={Path.Combine(fixture.Options.DataDirectory, "react-revision-validation", "workspaces")}", mount);
            Assert.EndsWith(",target=/source,readonly", mount);
            Assert.Contains("--network=none", actual);
            Assert.Contains("--read-only", actual);
            Assert.Contains("--cap-drop=ALL", actual);
            Assert.Contains("10001:10001", actual);
            Assert.Contains(Fixture.Image, actual);
            Assert.Contains("sha256sum -c --status", actual[^1]);
            Assert.EndsWith(ReactBuildProfile.Command(report.Results[i].Stage), actual[^1]);
            Assert.Equal(prepared.Plan.Stages[i].Arguments,
                actual.Select((argument, index) => index == 3 ? "<container>" :
                    argument == mount ? "type=bind,source=<revision snapshot>,target=/source,readonly" : argument));
        }
        Assert.Equal(3, fixture.Process.Calls.Count(args => args[0] == "rm"));
        Assert.Equal(before, File.ReadAllBytes(fixture.MissionPath));
        Assert.Equal(MissionStatus.Failed, fixture.Store.Get(fixture.Id)!.Status);
        Assert.Equal(13, fixture.Store.Get(fixture.Id)!.GenerationReservations!.Count);
        Assert.Equal(fixture.Files.Select(file => file.Path).Order(),
            fixture.Workspaces.List(fixture.Id).Select(file => file.Path).Order());
        var restored = Assert.IsType<ReactRevisionValidationReport>(await fixture.Service().GetReportAsync(fixture.Id, default));
        Assert.Equal(report.RunId, restored.RunId);
        Assert.Equal(report.Plan.Fingerprint, restored.Plan.Fingerprint);
        Assert.Equal(report.Results.Select(result => result.Log), restored.Results.Select(result => result.Log));
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => service.AuthorizeAsync(fixture.Id, request, default));
        Assert.Equal(3, fixture.Process.Calls.Count(args => args[0] == "run"));
    }

    [Fact]
    public async Task RefusesChangedWorkspaceAndUnpublishedDraft()
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync(hasRevision: false);
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => fixture.Service().PrepareAsync(fixture.Id, default));
        await fixture.PublishAsync();
        var service = fixture.Service();
        var prepared = await service.PrepareAsync(fixture.Id, default);
        var file = SafePaths.Resolve(fixture.Workspaces.GetDirectory(fixture.Id), "src/App.tsx");
        await File.AppendAllTextAsync(file, "\n// changed outside the publication");
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => service.AuthorizeAsync(fixture.Id,
            new(true, prepared.RequestId, prepared.Plan.Fingerprint), default));
        Assert.Empty(fixture.Process.Calls);
    }

    [Fact]
    public async Task ConfigurationChangeIssuesDifferentFingerprintAndRejectsPriorAuthorization()
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        var initial = await fixture.Service().PrepareAsync(fixture.Id, default);
        var restarted = fixture.Service(fixture.Sandbox with { MemoryMegabytes = 1024, TimeoutSeconds = 75 });
        var updated = await restarted.PrepareAsync(fixture.Id, default);
        Assert.Equal(initial.Plan.RevisionFingerprint, updated.Plan.RevisionFingerprint);
        Assert.NotEqual(initial.Plan.Fingerprint, updated.Plan.Fingerprint);
        Assert.Contains("1024m", updated.Plan.Stages[0].Arguments);
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => restarted.AuthorizeAsync(fixture.Id,
            new(true, initial.RequestId, initial.Plan.Fingerprint), default));
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => restarted.AuthorizeAsync(fixture.Id,
            new(true, updated.RequestId, initial.Plan.Fingerprint), default));
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() =>
            fixture.Service(fixture.Sandbox with { Enabled = false }).PrepareAsync(fixture.Id, default));
        Assert.Empty(fixture.Process.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TargetedCorrectionCommitsTheWholeCandidateOrLeavesV1Untouched(bool validReply)
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        var validation = fixture.Service();
        var prepared = await validation.PrepareAsync(fixture.Id, default);
        var report = await validation.AuthorizeAsync(fixture.Id, new(true, prepared.RequestId, prepared.Plan.Fingerprint), default);
        var initial = fixture.Store.Get(fixture.Id)!;
        var initialRevision = Assert.Single(initial.Workflow!.Revisions!);
        var calls = fixture.Process.Calls.Count;
        var model = new SimulatedDeveloper(validReply);
        var correction = new ReactRevisionCorrection(fixture.Store, fixture.Workspaces, validation,
            new LlmProviderResolver([model]), new AIPlatform.Infrastructure.OllamaOptions(), fixture.Gate);
        var request = new PrepareReactCorrectionRequest(report.RunId, initialRevision.Fingerprint,
            [new(["src/App.tsx"], "Change only the requested title."),
             new(["src/App.test.tsx"], "Update the associated test.")]);
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() =>
            correction.PrepareAsync(fixture.Id, request with { ReportId = Guid.NewGuid() }, default));
        await Assert.ThrowsAsync<StudioValidationException>(() => correction.PrepareAsync(fixture.Id,
            request with { Groups = [new(["vite.config.ts"], "try a protected path")] }, default));
        var plan = await correction.PrepareAsync(fixture.Id, request, default);
        Assert.Equal(7, plan.RemainingGenerations);
        Assert.Equal(2, plan.Groups.Count);
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() =>
            correction.StartAsync(fixture.Id, new(request, "stale"), default));
        Assert.Empty(model.Calls);

        if (validReply)
        {
            var result = await correction.StartAsync(fixture.Id, new(request, plan.Fingerprint), default);
            Assert.Equal(2, result.Version);
            Assert.Equal(2, result.ChangedPaths.Length);
            Assert.Equal(15, result.TotalGenerations);
            var next = fixture.Store.Get(fixture.Id)!.Workflow!.Revisions!;
            Assert.Equal(initialRevision.Fingerprint, next[0].Fingerprint);
            Assert.Equal(initialRevision.Files, next[0].Files);
            Assert.Equal(report.RunId, next[1].SourceValidationRunId);
            Assert.NotEqual(fixture.BaseWorkspaces.GetDirectory(fixture.Id), fixture.Workspaces.GetDirectory(fixture.Id));
            var newValidation = await validation.PrepareAsync(fixture.Id, default);
            Assert.Equal(result.Fingerprint, newValidation.Plan.RevisionFingerprint);
        }
        else
        {
            await Assert.ThrowsAsync<StudioValidationException>(() =>
                correction.StartAsync(fixture.Id, new(request, plan.Fingerprint), default));
            Assert.Single(fixture.Store.Get(fixture.Id)!.Workflow!.Revisions!);
            Assert.Equal(fixture.BaseWorkspaces.GetDirectory(fixture.Id), fixture.Workspaces.GetDirectory(fixture.Id));
        }
        Assert.Equal(calls, fixture.Process.Calls.Count);
        Assert.Equal(2, model.Calls.Count);
        Assert.Contains("TS2322", model.Calls[0].Messages[1].Content);
        Assert.Equal(MissionStatus.Failed, fixture.Store.Get(fixture.Id)!.Status);
        Assert.Equal(15, fixture.Store.Get(fixture.Id)!.GenerationReservations!.Count);
        Assert.Equal(initialRevision.Files.Single(file => file.Path == "src/App.tsx").Content,
            (await fixture.BaseWorkspaces.ReadAsync(fixture.Id, "src/App.tsx", default)).Content);
        Assert.Equal(initial.Workflow.Plan, fixture.Store.Get(fixture.Id)!.Workflow!.Plan);
    }

    [Theory]
    [InlineData(13)]
    [InlineData(20)]
    public async Task DiagnosticTargetingHonorsBudgetAndRejectsAnInvalidCandidateWithoutPublication(int limit)
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync(generationLimit: limit);
        var validation = fixture.Service();
        var prepared = await validation.PrepareAsync(fixture.Id, default);
        var report = await validation.AuthorizeAsync(fixture.Id, new(true, prepared.RequestId, prepared.Plan.Fingerprint), default);
        var initial = fixture.Store.Get(fixture.Id)!;
        var model = new SimulatedDeveloper(true);
        var check = new RejectingSourceCheck();
        var correction = new ReactRevisionCorrection(fixture.Store, fixture.Workspaces, validation,
            new LlmProviderResolver([model]), new AIPlatform.Infrastructure.OllamaOptions(), fixture.Gate,
            fixture.Options with { MaxCorrectionAttempts = 1 }, check);
        var revision = Assert.Single(initial.Workflow!.Revisions!);
        var scope = new PrepareReactCorrectionRequest(report.RunId, revision.Fingerprint, []);
        if (limit == 13)
        {
            await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => correction.PrepareAsync(fixture.Id, scope, default));
            Assert.Empty(model.Calls);
            Assert.Equal(0, fixture.Store.Get(fixture.Id)!.CorrectionAttempts);
            return;
        }
        var plan = await correction.PrepareAsync(fixture.Id, scope, default);
        Assert.Equal("src/App.tsx", Assert.Single(Assert.Single(plan.Groups).Paths));
        var failure = await Assert.ThrowsAsync<StudioValidationException>(() =>
            correction.StartAsync(fixture.Id, new(scope, plan.Fingerprint), default));
        Assert.Contains("TS1002", failure.Message);
        using var input = JsonDocument.Parse(Assert.Single(model.Calls).Messages[1].Content);
        var diagnostic = Assert.Single(input.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("TS2322", diagnostic.GetProperty("code").GetString());
        Assert.Equal(1, diagnostic.GetProperty("line").GetInt32());
        Assert.Equal(2, diagnostic.GetProperty("column").GetInt32());
        Assert.Equal(7, input.RootElement.GetProperty("remainingGenerations").GetInt32());
        var after = fixture.Store.Get(fixture.Id)!;
        Assert.Equal(revision.Fingerprint, Assert.Single(after.Workflow!.Revisions!).Fingerprint);
        Assert.Equal(MissionStatus.Failed, after.Status);
        Assert.Equal(14, after.GenerationReservations!.Count);
        Assert.Equal(1, after.CorrectionAttempts);
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => correction.PrepareAsync(fixture.Id, scope, default));
        Assert.Single(model.Calls);
    }

    [Fact]
    public void RollupSuiteLoadAndAssertionFailuresAreKeptDistinct()
    {
        var build = DiagnosticParser.Parse(new("Build", "Failed", 1,
            """src/Editor.tsx (2:15): "Item" is not exported by "src/Item.tsx"."""),
            MissionReactTemplate.Id);
        var item = Assert.Single(build.Items);
        Assert.Equal(("Vite", "src/Editor.tsx", (int?)2, (int?)15), (item.Type, item.File, item.Line, item.Column));
        var tests = DiagnosticParser.Parse(new("Tests", "Failed", 1, "\nAIPLATFORM_VITEST_BEGIN\n" +
            """{"testResults":[{"name":"/work/src/Editor.test.tsx","status":"failed","message":"jest is not defined at src/Editor.test.tsx:3:4","assertionResults":[]},{"name":"/work/src/App.test.tsx","status":"failed","message":"assertions failed","assertionResults":[{"status":"failed","fullName":"loads saved records","failureMessages":["Expected a saved record"]}]}]}""" +
            "\nAIPLATFORM_VITEST_END\n"), MissionReactTemplate.Id);
        Assert.Collection(tests.Items,
            load => Assert.Equal(("VitestSuite", "src/Editor.test.tsx", (int?)3), (load.Type, load.File, load.Line)),
            assertion => Assert.Equal(("Vitest", "src/App.test.tsx", "loads saved records: Expected a saved record"),
                (assertion.Type, assertion.File, assertion.Message)));
    }

    private sealed class RejectingSourceCheck : IReactSourceCheck
    {
        public Task<IReadOnlyList<ReactSourceIssue>> CheckAsync(IReadOnlyList<GeneratedFile> files, CancellationToken token)
        {
            return Task.FromResult<IReadOnlyList<ReactSourceIssue>>(files.Single(file => file.Path == "src/App.tsx").Content
                .EndsWith("// corrected", StringComparison.Ordinal)
                ? [new("src/App.tsx", 1, 2, "TS1002", "Unterminated string literal.")] : []);
        }

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishedRevisionUsesTheSameExactPatchContractWithoutTouchingTheOriginal(bool valid)
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        var validation = fixture.Service();
        var prepared = await validation.PrepareAsync(fixture.Id, default);
        var report = await validation.AuthorizeAsync(fixture.Id, new(true, prepared.RequestId, prepared.Plan.Fingerprint), default);
        var original = fixture.Store.Get(fixture.Id)!.Workflow!.Revisions!.Single();
        var provider = new SimulatedDeveloper(valid);
        var correction = new ReactRevisionCorrection(fixture.Store, fixture.Workspaces, validation, new LlmProviderResolver([provider]),
            new AIPlatform.Infrastructure.OllamaOptions(), fixture.Gate, fixture.Options, new PassingSourceCheck());
        var scope = new PrepareReactCorrectionRequest(report.RunId, original.Fingerprint, [
            new(["src/App.tsx"], "Correct the reported defect; preserve the component interface.")
            { PatchTargets = [new("src/App.tsx", "Project")] }]);
        var plan = await correction.PrepareAsync(fixture.Id, scope, default);
        Assert.Equal("Patches", Assert.Single(plan.Groups).ReplyContract);
        if (valid)
        {
            await correction.StartAsync(fixture.Id, new(scope, plan.Fingerprint), default);
            Assert.Equal(2, fixture.Store.Get(fixture.Id)!.Workflow!.Revisions!.Count);
            Assert.Contains("Corrected", fixture.Store.Get(fixture.Id)!.Workflow!.Draft!.Single(file => file.Path == "src/App.tsx").Content);
        }
        else
        {
            await Assert.ThrowsAsync<StudioValidationException>(() => correction.StartAsync(fixture.Id, new(scope, plan.Fingerprint), default));
            Assert.Single(fixture.Store.Get(fixture.Id)!.Workflow!.Revisions!);
        }
        Assert.Contains("Project", (await fixture.BaseWorkspaces.ReadAsync(fixture.Id, "src/App.tsx", default)).Content);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(fixture.Store.Get(fixture.Id)!.Workflow!.Revisions![0]));
        Assert.Single(provider.Calls);
        Assert.Equal(14, fixture.Store.Get(fixture.Id)!.GenerationReservations!.Count);
    }

    private sealed class PassingSourceCheck : IReactSourceCheck
    {
        public Task<IReadOnlyList<ReactSourceIssue>> CheckAsync(IReadOnlyList<GeneratedFile> files, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ReactSourceIssue>>([]);
    }

    private sealed class SimulatedDeveloper(bool valid) : ILLMProvider
    {
        public string Name => "Ollama";
        public List<LlmRequest> Calls { get; } = [];

        public Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            using var input = JsonDocument.Parse(request.Messages[1].Content);
            if (input.RootElement.TryGetProperty("patchTargets", out var targets))
                return Task.FromResult(JsonSerializer.Serialize(new { patches = targets.Deserialize<ReactPatchTarget[]>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Select(target => new {
                        Path = valid ? target.Path : "src/main.tsx", target.Original, Replacement = "Corrected" }) },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var files = input.RootElement.GetProperty("targets").Deserialize<GeneratedFile[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            files[0] = files[0] with { Content = files[0].Content + "\n// corrected",
                Path = !valid && Calls.Count == 2 ? "src/main.tsx" : files[0].Path };
            return Task.FromResult(JsonSerializer.Serialize(new { files }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public const string Image = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public Guid Id { get; } = Guid.NewGuid();
        public StudioOptions Options { get; } = new() { DataDirectory = Path.Combine(Path.GetTempPath(), $"react-validation-{Guid.NewGuid():N}") };
        public SandboxOptions Sandbox { get; } = new() { Enabled = true };
        public SimulatedDocker Process { get; } = new();
        public WorkspaceStore BaseWorkspaces => new(Options);
        public WorkspaceStore Workspaces => new(Options, id =>
            Store?.Get(id)?.Workflow?.Revisions?.LastOrDefault(revision => revision.WorkspaceId.HasValue)?.WorkspaceId);
        public MissionStore Store { get; private set; } = null!;
        public MissionExecutionGate Gate { get; } = new();
        public string MissionPath => Path.Combine(Options.DataDirectory, "missions", $"{Id:N}.json");
        public GeneratedFile[] Files { get; } = [.. MissionReactTemplate.Files, new("src/App.tsx",
            "export default function App() { return <main>Project</main> }"),
            new("src/App.test.tsx", "import { test } from 'vitest'; test('test', () => {})")];

        public async Task PublishAsync(bool hasRevision = true, int generationLimit = 20)
        {
            Store?.Dispose();
            if (!Directory.Exists(Workspaces.GetDirectory(Id))) Workspaces.Create(Id);
            await Workspaces.WriteAsync(Id, Files, default, MissionReactTemplate.Id);
            var now = DateTimeOffset.UtcNow;
            var descriptor = new ReactExecutionPlanDescriptor(1, MissionReactTemplate.Id,
                ReactExecutionPlanDescriptor.Manifest(Files), ReactTemplate.Image, Image, Image, true,
                Sandbox.MemoryMegabytes, Sandbox.CpuCount, Sandbox.TimeoutSeconds, 0, [], []);
            var hash = descriptor.ComputeFingerprint(Files);
            var plan = new ExecutionPlan(hash, Image, Sandbox.MemoryMegabytes, Sandbox.CpuCount,
                Sandbox.TimeoutSeconds, 0, [], ReactDescriptor: descriptor);
            var revision = new ProjectRevision(1, now, "Published", hash, Files, plan);
            var workflow = new MissionWorkflow(Guid.NewGuid(), Id,
                [new() { Id = "developer", Role = "Developer", ModelId = "coder", AllowedTools = ["GenerateFiles"] }],
                [new() { Id = "coder", Provider = "Ollama", Name = "local-coder" }], [], Draft: Files,
                Revisions: hasRevision ? [revision] : [], Version: 2);
            var mission = new MissionSnapshot(Id, "React", MissionKind.Project, null, false, MissionStatus.Failed,
                now, now, null, null, null, false, null, "Reviewer failed", true, 0, [], [],
                Workflow: workflow, TemplateId: MissionReactTemplate.Id, MaxTotalGenerations: generationLimit,
                GenerationReservations: Enumerable.Range(1, 13).Select(_ => new MissionGenerationReservation(
                    Guid.NewGuid(), "Developer", "model", now)).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(MissionPath)!);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            json.Converters.Add(new JsonStringEnumConverter());
            await File.WriteAllTextAsync(MissionPath, JsonSerializer.Serialize(mission, json));
            var platform = TestSupport.Configuration();
            Store = new(Options, platform, new AgentRegistry(platform), Workspaces);
        }

        public ReactRevisionValidation Service(SandboxOptions? options = null)
        {
            var current = options ?? Sandbox;
            return new(Store, Workspaces, new(current, Workspaces, Process, Store), current, Options, Gate,
                TimeProvider.System, NullLogger<ReactRevisionValidation>.Instance);
        }

        public void Dispose()
        {
            Store?.Dispose();
            Gate.Dispose();
            if (Directory.Exists(Options.DataDirectory)) Directory.Delete(Options.DataDirectory, recursive: true);
        }
    }

    private sealed class SimulatedDocker : IContainerProcess
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            ContainerProcessResult result = arguments[0] switch
            {
                "info" => new(0, "linux"),
                "image" => new(0, Fixture.Image),
                "rm" => new(0, ""),
                "run" when arguments[^1].Contains("tsc --noEmit") => new(2, "src/App.tsx(1,2): error TS2322: Type mismatch."),
                "run" when arguments[^1].Contains("vite build") => new(0, "Vite built the project."),
                "run" when arguments[^1].Contains("vitest run") => new(0, "\nAIPLATFORM_VITEST_BEGIN\n" +
                    """{"success":true,"numTotalTests":1,"numPassedTests":1,"numFailedTests":0,"numPendingTests":0,"numFailedTestSuites":0,"testResults":[{"status":"passed","assertionResults":[{"status":"passed"}]}]}""" +
                    "\nAIPLATFORM_VITEST_END\n"),
                _ => throw new InvalidOperationException("Unexpected simulated command.")
            };
            return Task.FromResult(result);
        }
    }
}
