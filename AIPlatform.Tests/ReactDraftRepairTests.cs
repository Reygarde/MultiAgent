using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlatform.API;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure;
using AIPlatform.Infrastructure.Studio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace AIPlatform.Tests;

public sealed class ReactDraftRepairTests(ITestOutputHelper output)
{
    private static PrepareReactDraftRepairRequest Patches(Fixture fixture) => new(fixture.Scope.Groups.Select(group =>
        group with { PatchTargets = group.Paths.Select(path => new ReactPatchTarget(path, "broken")).ToArray() }).ToArray());

    [Fact]
    public async Task ExactPatchesPreserveOriginalsAndPersistTheirDiffAcrossCheckpointReload()
    {
        using var fixture = new Fixture();
        var scope = new PrepareReactDraftRepairRequest([new(["src/App.tsx", "src/App.test.tsx"], "Fix both diagnostics.")
        { PatchTargets = [new("src/App.tsx", "broken"), new("src/App.test.tsx", "broken")] }]);
        var plan = await fixture.Service.PrepareAsync(fixture.Id, scope, default);
        Assert.True(plan.CanStart);
        var result = await fixture.Service.StartAsync(fixture.Id, new(scope, plan.Fingerprint), default);
        Assert.True(result.Ready);
        Assert.Equal(12, result.ConsumedGenerations);
        Assert.Equal(2, result.PatchEdits.Count);
        Assert.All(result.PatchEdits, edit =>
        {
            Assert.Equal(("broken", "fixed"), (edit.Original, edit.Replacement));
            Assert.NotEqual(edit.OriginalSha256, edit.CandidateSha256);
        });
        using var reopened = fixture.OpenStore();
        Assert.Equal(fixture.Original, reopened.GetDraftRepair(fixture.Id)!.Original);
        Assert.Equal(result.PatchEdits, reopened.GetDraftRepair(fixture.Id)!.PatchEdits);
        Assert.Single(fixture.Provider.Requests);
        Assert.False(reopened.Get(fixture.Id)!.HasWorkspace);
    }

    [Fact]
    public async Task DisjointPatchesUseOriginalOffsetsAndAllowExactDeletionWithoutNormalizingNewlines()
    {
        using var fixture = new Fixture("delete-comment");
        var source = fixture.Original.Single(file => file.Path == "src/App.tsx").Content + "\r\n// obsolete\r\n";
        fixture.Core.Store.Update(fixture.Id, mission => mission with { Workflow = mission.Workflow! with
        { Draft = mission.Workflow.Draft!.Select(file => file.Path == "src/App.tsx" ? file with { Content = source } : file).ToArray() } });
        var scope = new PrepareReactDraftRepairRequest([new(["src/App.tsx", "src/App.test.tsx"], "Fix diagnostics and remove the obsolete comment.")
        { PatchTargets = [new("src/App.tsx", "broken"), new("src/App.tsx", "// obsolete\r\n"), new("src/App.test.tsx", "broken")] }]);
        var plan = await fixture.Service.PrepareAsync(fixture.Id, scope, default);
        var result = await fixture.Service.StartAsync(fixture.Id, new(scope, plan.Fingerprint), default);
        Assert.Equal(3, result.PatchEdits.Count);
        Assert.Contains(result.PatchEdits, edit => edit.Replacement == "");
        var state = fixture.Core.Store.GetDraftRepair(fixture.Id)!;
        Assert.Equal(source, state.Original.Single(file => file.Path == "src/App.tsx").Content);
        Assert.Equal(source.Replace("broken", "fixed", StringComparison.Ordinal).Replace("// obsolete\r\n", "", StringComparison.Ordinal),
            state.Candidate.Single(file => file.Path == "src/App.tsx").Content);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("duplicate-property")]
    [InlineData("missing")]
    [InlineData("missing-replacement")]
    [InlineData("null-replacement")]
    [InlineData("wrong-original")]
    [InlineData("unchanged")]
    [InlineData("invalid-static")]
    [InlineData("truncated")]
    public async Task RejectedPatchGroupsCannotChangeSourcesOrReplayTheChargedGeneration(string failure)
    {
        using var fixture = new Fixture(failure);
        var scope = new PrepareReactDraftRepairRequest([new(["src/App.tsx", "src/App.test.tsx"], "Fix both diagnostics.")
        { PatchTargets = [new("src/App.tsx", "broken"), new("src/App.test.tsx", "broken")] }]);
        var plan = await fixture.Service.PrepareAsync(fixture.Id, scope, default);
        Assert.NotNull(await Record.ExceptionAsync(() =>
            fixture.Service.StartAsync(fixture.Id, new(scope, plan.Fingerprint), default)));
        var state = fixture.Core.Store.GetDraftRepair(fixture.Id)!;
        Assert.Equal(fixture.Original, state.Candidate);
        Assert.Equal(fixture.Original, fixture.Core.Store.GetLatestForRepair(fixture.Id).Workflow!.Draft);
        Assert.Empty(state.PatchEdits);
        Assert.Equal(0, state.CompletedGroups);
        Assert.Equal(12, fixture.Core.Store.GetLatestForRepair(fixture.Id).GenerationReservations!.Count);
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Service.StartAsync(fixture.Id, new(scope, plan.Fingerprint), default));
        Assert.Single(fixture.Provider.Requests);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("duplicate")]
    [InlineData("overlap")]
    [InlineData("outside")]
    [InlineData("absent")]
    [InlineData("newlines")]
    public async Task UnsafePatchAnchorsAreRefusedBeforeGeneration(string defect)
    {
        using var fixture = new Fixture();
        if (defect == "ambiguous")
            fixture.Core.Store.Update(fixture.Id, mission => mission with { Workflow = mission.Workflow! with
            { Draft = mission.Workflow.Draft!.Select(file => file.Path == "src/App.tsx"
                ? file with { Content = file.Content + "\n// broken" } : file).ToArray() } });
        if (defect == "newlines")
            fixture.Core.Store.Update(fixture.Id, mission => mission with { Workflow = mission.Workflow! with
            { Draft = mission.Workflow.Draft!.Select(file => file.Path == "src/App.tsx"
                ? file with { Content = file.Content.Replace(";", ";\r\n", StringComparison.Ordinal) } : file).ToArray() } });
        var scope = Patches(fixture);
        var targets = scope.Groups[0].PatchTargets!;
        targets = defect switch
        {
            "duplicate" => [targets[0], targets[0]],
            "overlap" => [targets[0], new("src/App.tsx", "export const broken")],
            "outside" => [new("src/main.tsx", "broken")],
            "absent" => [new("src/App.tsx", "not present")],
            "newlines" => [new("src/App.tsx", "export const broken = 1;\n")],
            _ => targets
        };
        scope = new([scope.Groups[0] with { PatchTargets = targets }, scope.Groups[1]]);
        await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Service.PrepareAsync(fixture.Id, scope, default));
        Assert.Empty(fixture.Provider.Requests);
        Assert.Equal(11, fixture.Core.Store.GetLatestForRepair(fixture.Id).GenerationReservations!.Count);
    }

    [Fact]
    public async Task ANewImportDiagnosticInAnotherFileRejectsTheWholePatchGroup()
    {
        using var fixture = new Fixture();
        fixture.Checker.ExtraIssues = files => files.Single(file => file.Path == "src/App.tsx").Content.Contains("fixed", StringComparison.Ordinal)
            ? [new("src/App.test.tsx", 1, 1, "REACT_LOCAL_IMPORT", "Simulated import regression")] : [];
        var scope = Patches(fixture);
        var plan = await fixture.Service.PrepareAsync(fixture.Id, scope, default);
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Service.StartAsync(fixture.Id, new(scope, plan.Fingerprint), default));
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(0, fixture.Core.Store.GetDraftRepair(fixture.Id)!.CompletedGroups);
        Assert.Equal(fixture.Original, fixture.Core.Store.GetLatestForRepair(fixture.Id).Workflow!.Draft);
    }

    [Fact]
    public async Task PatchCheckpointResumesWithoutLosingDiffOrReviewerCoverage()
    {
        using var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        fixture.Checker.AfterCheck = files =>
        {
            if (files.Single(file => file.Path == "src/App.tsx").Content.Contains("fixed", StringComparison.Ordinal) &&
                files.Single(file => file.Path == "src/App.test.tsx").Content.Contains("broken", StringComparison.Ordinal)) stop.Cancel();
        };
        var scope = Patches(fixture);
        var plan = await fixture.Service.PrepareAsync(fixture.Id, scope, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.StartAsync(fixture.Id, new(scope, plan.Fingerprint), stop.Token));
        using var reopened = fixture.OpenStore();
        Assert.Single(reopened.GetDraftRepair(fixture.Id)!.PatchEdits);
        fixture.Checker.AfterCheck = null;
        var result = await fixture.CreateService(reopened).StartAsync(fixture.Id, new(scope, plan.Fingerprint), default);
        Assert.Equal(2, result.PatchEdits.Count);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(13, result.ConsumedGenerations);
        Assert.Equal(plan.ReviewerGroups.SelectMany(group => group.Criteria),
            result.Plan.ReviewerGroups.SelectMany(group => group.Criteria));
    }

    [Fact]
    public async Task MeasureFullFilesAndPatchContractsWithoutAssumingPatchesAreCheaper()
    {
        foreach (var scenario in new[] { "small-anchor", "large-file-small-anchor", "whole-file-anchor" })
        {
            using var fixture = new Fixture();
            if (scenario == "large-file-small-anchor")
                fixture.Core.Store.Update(fixture.Id, mission => mission with { Workflow = mission.Workflow! with
                { Draft = mission.Workflow.Draft!.Select(file => file.Path == "src/App.tsx"
                    ? file with { Content = file.Content + "\n/*" + new string('x', 1800) + "*/" } : file).ToArray() } });
            var scope = Patches(fixture);
            if (scenario == "whole-file-anchor")
                scope = new(scope.Groups.Select(group => group with
                { PatchTargets = group.Paths.Select(path => new ReactPatchTarget(path,
                    fixture.Original.Single(file => file.Path == path).Content)).ToArray() }).ToArray());
            var patch = await fixture.Service.PrepareAsync(fixture.Id, scope, default);
            var complete = await fixture.Service.PrepareAsync(fixture.Id, fixture.Scope, default);
            Assert.Equal(JsonSerializer.Serialize(complete.ReviewerGroups), JsonSerializer.Serialize(patch.ReviewerGroups));
            var group = patch.DeveloperGroups[0];
            Assert.Equal(complete.DeveloperGroups[0].ContextCharacters, group.CompleteFilesContextCharacters);
            Assert.Equal(complete.DeveloperGroups[0].BaselineJsonBytes, group.CompleteFilesJsonBytes);
            if (scenario == "whole-file-anchor") Assert.True(group.BaselineJsonBytes > group.CompleteFilesJsonBytes);
            else Assert.True(group.BaselineJsonBytes < group.CompleteFilesJsonBytes);
            output.WriteLine(JsonSerializer.Serialize(new { scenario, group.ContextCharacters, group.CompleteFilesContextCharacters,
                group.BaselineJsonBytes, group.CompleteFilesJsonBytes, group.EstimatedOutputTokens, group.SafeOutputTokens }));
            Assert.Empty(fixture.Provider.Requests);
        }
    }
    [Fact]
    public async Task ReviewerCapacityIsAtomicAcrossProcesses()
    {
        const string workerKey = "AIPLATFORM_ECONOMIC_BUDGET_WORKER";
        if (Environment.GetEnvironmentVariable(workerKey) is { } worker)
        {
            var input = JsonSerializer.Deserialize<BudgetWorker>(worker)!;
            Assert.StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIPlatform-workflow-"),
                System.IO.Path.GetFullPath(input.Directory));
            var platform = TestSupport.Configuration();
            using var store = new MissionStore(new() { DataDirectory = input.Directory }, platform, new AgentRegistry(platform));
            var provider = BudgetedLlmProvider.Wrap(new FakeProvider((_, _) => Task.FromResult("simulated")));
            var successes = 0;
            for (var index = 0; index < 12; index++)
            {
                using var scope = store.EnterGenerationScope(input.MissionId, "Reviewer");
                try { await provider.CompleteAsync(TestSupport.Request(), default); successes++; }
                catch (StudioValidationException) { }
            }
            Assert.InRange(successes, 0, 1);
            File.WriteAllText(System.IO.Path.Combine(input.Directory, $"economic-worker-{input.WorkerId:N}.json"),
                JsonSerializer.Serialize(successes));
            return;
        }
        using var fixture = new Fixture();
        var plan = await fixture.Service.PrepareAsync(fixture.Id, fixture.Scope, default);
        await fixture.Service.StartAsync(fixture.Id, new(fixture.Scope, plan.Fingerprint), default);
        var workers = Enumerable.Range(0, 2).Select(_ => new BudgetWorker(
            fixture.Core.Options.DataDirectory, fixture.Id, Guid.NewGuid())).ToArray();
        await Task.WhenAll(workers.Select(async input =>
        {
            using var process = new Process { StartInfo = new("dotnet") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "test", typeof(ReactDraftRepairTests).Assembly.Location, "--filter",
                    "FullyQualifiedName=AIPlatform.Tests.ReactDraftRepairTests.ReviewerCapacityIsAtomicAcrossProcesses",
                    "--logger", "console;verbosity=minimal" }
            } };
            process.StartInfo.Environment[workerKey] = JsonSerializer.Serialize(input);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await output + await error);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }));
        Assert.Equal(1, workers.Sum(input => JsonSerializer.Deserialize<int>(File.ReadAllText(
            System.IO.Path.Combine(input.Directory, $"economic-worker-{input.WorkerId:N}.json")))));
        var current = fixture.Core.Store.GetLatestForRepair(fixture.Id);
        Assert.Equal(14, current.GenerationReservations!.Count);
        Assert.Single(current.GenerationReservations, item => item.CapacityId is not null && item.Role == "Reviewer");
    }

    private sealed record BudgetWorker(string Directory, Guid MissionId, Guid WorkerId);

    [Fact]
    public async Task OriginalDraftIsCheckedAgainAtTheAtomicProviderBoundary()
    {
        using var fixture = new Fixture();
        var plan = await fixture.Service.PrepareAsync(fixture.Id, fixture.Scope, default);
        var provider = new ChangedSourceProvider(() => fixture.Core.Store.Update(fixture.Id, mission => mission with
        {
            Workflow = mission.Workflow! with { Draft = mission.Workflow.Draft!.Select(file =>
                file.Path == "src/App.tsx" ? file with { Content = file.Content + "\n// concurrent edit" } : file).ToArray() }
        }));
        var service = new ReactDraftRepair(fixture.Core.Store, fixture.Core.Workspaces, fixture.Checker, fixture.Checker,
            new LlmProviderResolver([provider]), new(), fixture.Gate, fixture.Core.Options);
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            service.StartAsync(fixture.Id, new(fixture.Scope, plan.Fingerprint), default));
        var current = fixture.Core.Store.GetLatestForRepair(fixture.Id);
        Assert.Equal(11, current.GenerationReservations!.Count);
        Assert.Contains("// concurrent edit", current.Workflow!.Draft!.Single(file => file.Path == "src/App.tsx").Content);
    }

    private sealed class ChangedSourceProvider(Action beforeReservation) : IGenerationBudgetedProvider
    {
        public string Name => "Ollama";
        public Task<string> CompleteAsync(LlmRequest request, CancellationToken token)
        {
            beforeReservation();
            LlmGenerationScope.Reserve(request, token);
            throw new InvalidOperationException("The changed source should prevent generation.");
        }
    }

    [Fact]
    public async Task OfficialApiExposesPreflightButRefusesAnUnapprovedFingerprint()
    {
        using var fixture = new Fixture();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
                services.RemoveAll<StudioOptions>();
                services.AddSingleton(fixture.Core.Options);
                services.RemoveAll<IReactDraftRepair>();
                services.AddSingleton<IReactDraftRepair>(fixture.Service);
            });
        });
        using var client = factory.CreateClient();
        var route = $"/api/missions/{fixture.Id}/react-draft-repair";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route + "/")).StatusCode);
        var preparation = await client.PostAsJsonAsync(route + "/prepare", fixture.Scope);
        preparation.EnsureSuccessStatusCode();
        var plan = await preparation.Content.ReadFromJsonAsync<ReactDraftRepairPlan>();
        Assert.True(plan!.CanStart);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route + "/start",
            new StartReactDraftRepairRequest(fixture.Scope, "not-the-approved-fingerprint"))).StatusCode);
        var openApi = await client.GetAsync("/openapi/v1.json");
        openApi.EnsureSuccessStatusCode();
        Assert.Contains("react-draft-repair", await openApi.Content.ReadAsStringAsync());
        Assert.Empty(fixture.Provider.Requests);
        Assert.Equal(11, fixture.Core.Store.GetLatestForRepair(fixture.Id).GenerationReservations!.Count);
    }

    [Fact]
    public async Task OneCallCanCorrectSeveralExactPathsWithoutPublishingAPartialCandidate()
    {
        using var fixture = new Fixture();
        var scope = new PrepareReactDraftRepairRequest([
            new(["src/App.tsx", "src/App.test.tsx"], "Coordinate the public interface and its tests; fix both diagnostics.")]);
        var plan = await fixture.Service.PrepareAsync(fixture.Id, scope, default);
        var result = await fixture.Service.StartAsync(fixture.Id, new(scope, plan.Fingerprint), default);
        Assert.True(result.Ready);
        Assert.Equal(12, result.ConsumedGenerations);
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(2, fixture.Core.Store.GetDraftRepair(fixture.Id)!.Plan.DeveloperGroups.Single().Paths.Length);
    }
    [Fact]
    public async Task PreparationIsReadOnlyAndCountsReviewerWithoutConsumingCapacity()
    {
        using var fixture = new Fixture();
        var before = File.ReadAllBytes(fixture.Path);
        var plan = await fixture.Service.PrepareAsync(fixture.Id, fixture.Scope, default);
        Assert.True(plan.CanStart, string.Join("; ", plan.Blockers));
        Assert.Equal((11, 4), (plan.ConsumedGenerations, plan.RemainingGenerations));
        Assert.Equal(2, plan.DeveloperGroups.Count);
        Assert.Single(plan.ReviewerGroups);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Empty(fixture.Provider.Requests);
        Assert.Null(fixture.Core.Store.GetDraftRepair(fixture.Id));
    }

    [Fact]
    public async Task CompleteCandidateIsAppliedAtomicallyAndStaleWritersCannotEraseBudgetOrSources()
    {
        using var fixture = new Fixture();
        using var stale = fixture.OpenStore();
        var plan = await fixture.Service.PrepareAsync(fixture.Id, fixture.Scope, default);
        var result = await fixture.Service.StartAsync(fixture.Id, new(fixture.Scope, plan.Fingerprint), default);
        Assert.True(result.Ready);
        Assert.Equal(13, result.ConsumedGenerations);
        var state = fixture.Core.Store.GetDraftRepair(fixture.Id)!;
        Assert.Equal(fixture.Original, state.Original);
        Assert.DoesNotContain(state.Candidate, file => file.Content.Contains("broken", StringComparison.Ordinal));
        var current = fixture.Core.Store.Get(fixture.Id)!;
        Assert.Equal(MissionStatus.Failed, current.Status);
        Assert.False(current.HasWorkspace);
        Assert.Empty(current.Workflow!.Revisions ?? []);
        Assert.Empty(current.Verification);
        Assert.Throws<StudioValidationException>(() => stale.Update(fixture.Id, mission => mission with { Response = "stale metadata" }));
        using (stale.EnterGenerationScope(fixture.Id, "Developer"))
            Assert.Throws<StudioValidationException>(() => LlmGenerationScope.Reserve(TestSupport.Request(), default));
        using (stale.EnterGenerationScope(fixture.Id, "Reviewer"))
            LlmGenerationScope.Reserve(TestSupport.Request(), default);
        using (fixture.Core.Store.EnterGenerationScope(fixture.Id, "Reviewer"))
            Assert.Throws<StudioValidationException>(() => LlmGenerationScope.Reserve(TestSupport.Request(), default));
        Assert.Equal(14, stale.Get(fixture.Id)!.GenerationReservations!.Count);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        fixture.Core.Store.Update(fixture.Id, mission => mission with { HasWorkspace = true });
        Assert.True(fixture.Core.Store.DeleteProject(fixture.Id));
        Assert.True(fixture.Core.Store.Get(fixture.Id)!.Deleted);
        Assert.Null(fixture.Core.Store.GetDraftRepair(fixture.Id));
        Assert.Equal(14, fixture.Core.Store.Get(fixture.Id)!.GenerationReservations!.Count);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("unchanged")]
    [InlineData("invalid-static")]
    [InlineData("truncated")]
    public async Task FailedGroupCannotPublishOrReplayAlreadyChargedCall(string failure)
    {
        using var fixture = new Fixture(failure);
        var before = fixture.Core.Store.Get(fixture.Id)!;
        var plan = await fixture.Service.PrepareAsync(fixture.Id, fixture.Scope, default);
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.Service.StartAsync(fixture.Id, new(fixture.Scope, plan.Fingerprint), default)));
        var current = fixture.Core.Store.GetLatestForRepair(fixture.Id);
        Assert.Equal(before.Workflow!.Draft, current.Workflow!.Draft);
        Assert.Equal(12, current.GenerationReservations!.Count);
        Assert.Equal(2, current.CorrectionAttempts);
        Assert.False(current.HasWorkspace);
        Assert.Empty(current.Workflow.Revisions ?? []);
        Assert.False(fixture.Core.Store.Resume(fixture.Id));
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Service.StartAsync(fixture.Id, new(fixture.Scope, plan.Fingerprint), default));
        Assert.Single(fixture.Provider.Requests);
    }

    [Fact]
    public async Task InterruptedBetweenGroupsResumesFromDurableCandidateAndRefreshesDependencies()
    {
        using var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        fixture.Checker.AfterCheck = files =>
        {
            if (files.Single(file => file.Path == "src/App.tsx").Content.Contains("fixed", StringComparison.Ordinal) &&
                files.Single(file => file.Path == "src/App.test.tsx").Content.Contains("broken", StringComparison.Ordinal))
                stop.Cancel();
        };
        var plan = await fixture.Service.PrepareAsync(fixture.Id, fixture.Scope, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.StartAsync(fixture.Id, new(fixture.Scope, plan.Fingerprint), stop.Token));
        Assert.Equal(1, fixture.Core.Store.GetDraftRepair(fixture.Id)!.CompletedGroups);
        Assert.Equal(fixture.Original, fixture.Core.Store.GetLatestForRepair(fixture.Id).Workflow!.Draft);
        using var reopened = fixture.OpenStore();
        fixture.Checker.AfterCheck = null;
        var result = await fixture.CreateService(reopened).StartAsync(fixture.Id, new(fixture.Scope, plan.Fingerprint), default);
        Assert.True(result.Ready);
        Assert.Equal(13, result.ConsumedGenerations);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        using var secondInput = JsonDocument.Parse(fixture.Provider.Requests.Last().Messages[1].Content);
        Assert.Contains("fixed", secondInput.RootElement.GetProperty("dependencies")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task OutputSafetyAndInsufficientReviewCapacityRefuseBeforeProviderOrCheckpoint()
    {
        using var fixture = new Fixture();
        fixture.Core.Store.Update(fixture.Id, mission => mission with { Workflow = mission.Workflow! with
        { Draft = mission.Workflow.Draft!.Select(file => file.Path == "src/App.tsx"
            ? file with { Content = file.Content + "\n/*" + new string('x', 2400) + "*/" } : file).ToArray() } });
        var plan = await fixture.Service.PrepareAsync(fixture.Id, fixture.Scope, default);
        Assert.False(plan.CanStart);
        Assert.Contains(plan.Blockers, message => message.Contains("output", StringComparison.Ordinal));
        await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Service.StartAsync(fixture.Id, new(fixture.Scope, plan.Fingerprint), default));
        Assert.Empty(fixture.Provider.Requests);
        Assert.Null(fixture.Core.Store.GetDraftRepair(fixture.Id));
        using (fixture.Core.Store.EnterGenerationScope(fixture.Id, "Developer"))
            for (var index = 0; index < 2; index++) LlmGenerationScope.Reserve(TestSupport.Request(), default);
        plan = await fixture.Service.PrepareAsync(fixture.Id, fixture.Scope, default);
        Assert.Contains(plan.Blockers, message => message.Contains("budget", StringComparison.Ordinal));
        Assert.Equal(13, fixture.Core.Store.GetLatestForRepair(fixture.Id).GenerationReservations!.Count);
    }

    private sealed class Checker : IReactSourceCheck, IReactSourceRepair
    {
        public Action<IReadOnlyList<GeneratedFile>>? AfterCheck { get; set; }
        public Func<IReadOnlyList<GeneratedFile>, IReadOnlyList<ReactSourceIssue>>? ExtraIssues { get; set; }
        public Task<IReadOnlyList<ReactSourceIssue>> CheckAsync(IReadOnlyList<GeneratedFile> files, CancellationToken token)
        {
            AfterCheck?.Invoke(files);
            return Task.FromResult<IReadOnlyList<ReactSourceIssue>>(files.Where(file =>
                file.Content.Contains("broken", StringComparison.Ordinal)).Select(file =>
                new ReactSourceIssue(file.Path, 1, 1, "TS1005", "Simulated syntax diagnostic"))
                .Concat(ExtraIssues?.Invoke(files) ?? []).ToArray());
        }
        public async Task<ReactSourceRepairResult> RepairAsync(IReadOnlyList<GeneratedFile> files,
            IReadOnlyList<string> allowedPaths, CancellationToken token)
        {
            var issues = await CheckAsync(files, token);
            return new(files.Select(file => file with { }).ToArray(), issues, issues, [], []);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public WorkflowFixture Core { get; } = new(maxOutputTokens: 3072);
        public Guid Id { get; }
        public Checker Checker { get; } = new();
        public FakeProvider Provider { get; }
        public MissionExecutionGate Gate { get; } = new();
        public ReactDraftRepair Service => CreateService(Core.Store);
        public string Path => System.IO.Path.Combine(Core.Options.DataDirectory, "missions", Id.ToString("N") + ".json");
        public GeneratedFile[] Original { get; } = [.. MissionReactTemplate.Files,
            new("src/App.tsx", "export const broken = 1; export default function App() { return <main/> }"),
            new("src/App.test.tsx", "import App from './App'; export const broken = App;")];
        public PrepareReactDraftRepairRequest Scope { get; } = new([
            new(["src/App.tsx"], "Fix the attached diagnostic while preserving the public default export."),
            new(["src/App.test.tsx"], "Use the corrected App interface and fix the attached diagnostic.")]);

        public Fixture(string? failure = null)
        {
            var mission = Core.Store.Submit(new("Add records.", MissionKind.Project,
                TemplateId: MissionReactTemplate.Id, MaxTotalGenerations: 15));
            Id = mission.Id;
            using (Core.Store.EnterGenerationScope(Id, "Developer"))
                for (var index = 0; index < 11; index++) LlmGenerationScope.Reserve(TestSupport.Request(), default);
            Core.Store.Update(Id, current => current with
            {
                Status = MissionStatus.Failed, Error = "Static check failed", CorrectionAttempts = 1,
                Workflow = current.Workflow! with { Draft = Original,
                    Plan = new("Synthetic records", "Synthetic repair fixture", ["Preserve exports."],
                        [new("src/App.tsx", "Interface", []), new("src/App.test.tsx", "Functional tests", ["src/App.tsx"])])
                    {
                        OriginalRequest = current.Task, AcceptanceCriteria = current.Workflow.AcceptanceCriteria,
                        CriterionCoverage = current.Workflow.AcceptanceCriteria.Select(item => new PlannedCriterionCoverage(
                            item.Id, ["src/App.tsx", "src/App.test.tsx"], "Implementation and functional test.")).ToArray()
                    } }
            });
            Provider = new((request, _) =>
            {
                if (failure == "truncated") throw new LlmOutputLimitException(request.Model.Name, 3072, 3072, 40, 0);
                using var input = JsonDocument.Parse(request.Messages[1].Content);
                if (input.RootElement.TryGetProperty("patchTargets", out var patchInput))
                {
                    var patches = patchInput.Deserialize<ReactPatchTarget[]>(Json)!.Select(target => new
                    {
                        target.Path, target.Original, Replacement = failure == "delete-comment" && target.Original == "// obsolete\r\n" ? "" :
                            failure == "unchanged" ? target.Original :
                            target.Original.Replace("broken", failure == "invalid-static" ? "brokenAgain" : "fixed", StringComparison.Ordinal)
                    });
                    var reply = JsonSerializer.SerializeToNode(new { patches }, Json)!;
                    var array = reply["patches"]!.AsArray();
                    if (failure == "extra") array[0]!["path"] = "src/main.tsx";
                    if (failure == "wrong-original") array[0]!["original"] = "different text";
                    if (failure == "missing-replacement") array[0]!.AsObject().Remove("replacement");
                    if (failure == "null-replacement") array[0]!["replacement"] = null;
                    if (failure == "missing") array.RemoveAt(0);
                    if (failure == "duplicate") array[1] = array[0]!.DeepClone();
                    var text = reply.ToJsonString();
                    if (failure == "duplicate-property") text = "{\"patches\":[],\"patches\":" + array.ToJsonString() + "}";
                    return Task.FromResult(failure == "invalid-json" ? "{\"patches\":[" : text);
                }
                var targets = input.RootElement.GetProperty("targets").Deserialize<GeneratedFile[]>(Json)!;
                var files = targets.Select(file => file with {
                    Content = failure == "unchanged" ? file.Content : file.Content.Replace("broken",
                        failure == "invalid-static" ? "brokenAgain" : "fixed", StringComparison.Ordinal) }).ToArray();
                if (failure == "extra") files = [.. files, new("src/main.tsx", "unauthorized")];
                if (failure == "missing") files = [];
                var answer = JsonSerializer.Serialize(new { files }, Json);
                if (failure == "duplicate") answer = "{\"files\":[],\"files\":" + JsonSerializer.Serialize(files, Json) + "}";
                return Task.FromResult(failure == "invalid-json" ? "{\"files\":[" : answer);
            });
        }
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public MissionStore OpenStore() => new(Core.Options, Core.Platform, Core.Registry, Core.Workspaces);
        public ReactDraftRepair CreateService(MissionStore store) =>
            new(store, Core.Workspaces, Checker, Checker, new LlmProviderResolver([Provider]), new(), Gate, Core.Options);
        public void Dispose() { Gate.Dispose(); Core.Dispose(); }
    }
}
