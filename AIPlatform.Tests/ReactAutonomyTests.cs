using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlatform.API;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class ReactAutonomyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulSoftwarePipelinePreservesPlanAndDockerApprovalGates(bool approvePlan)
    {
        using var fixture = new WorkflowFixture(responseOverride: MissionReactContractTests.Respond, sourceCheck: new Checker());
        var mission = fixture.Store.Submit(new("Display initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, RequirePlanApproval: approvePlan, AutomaticReactCorrections: true));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        if (approvePlan)
        {
            var waiting = fixture.Store.Get(mission.Id)!;
            Assert.Equal(MissionStatus.AwaitingPlanApproval, waiting.Status);
            Assert.Single(fixture.Provider.Requests);
            Assert.True(fixture.Store.DecidePlanApproval(mission.Id, true, waiting.Workflow!.PlanApproval!.Fingerprint));
            await fixture.Processor.ProcessAsync(mission.Id, default);
        }
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal(5, current.GenerationReservations!.Count);
        Assert.Null(current.Workflow!.PendingSoftwareAgentCall);
        Assert.Empty(current.Verification);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Single(current.Workflow.Revisions!);
        Assert.True(WorkflowResponse.From(current.Workflow).AutomaticReactCorrections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaticErrorsUseAstOrExactPatchesOnAnIsolatedCandidate(bool deterministic)
    {
        var checker = new Checker { Deterministic = deterministic };
        var correctionCalls = 0;
        using var fixture = new WorkflowFixture(sourceCheck: checker, responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
            {
                correctionCalls++;
                Assert.True(input.RootElement.TryGetProperty("patchTargets", out _));
                return Correction(request, correctionCalls);
            }
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["path"]?.GetValue<string>() == "src/App.tsx")
                result["content"] = LargeApp();
            return result.ToJsonString();
        });
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        var repair = Assert.Single(current.Workflow!.SoftwareCorrections);
        Assert.True(repair.Ready);
        Assert.Equal(deterministic ? 0 : 1, correctionCalls);
        Assert.Contains("BROKEN", repair.Original.Single(file => file.Path == "src/App.tsx").Content);
        Assert.DoesNotContain("BROKEN", repair.Candidate.Single(file => file.Path == "src/App.tsx").Content);
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal(deterministic ? 5 : 6, current.GenerationReservations!.Count);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReviewerDefectsAutomaticallyProduceBoundedGroupsAndReevaluation(int cycles)
    {
        var reviews = 0;
        var corrections = 0;
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _)) return Correction(request, ++corrections);
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["criterionReviews"] is not null && ++reviews <= cycles)
                result["findings"] = JsonSerializer.SerializeToNode(new[]
                    { new ReviewFinding("src/App.tsx", "Error", $"Distinct functional defect {reviews}") }, Json);
            return result.ToJsonString();
        });
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(cycles, corrections);
        Assert.Equal(cycles + 1, reviews);
        Assert.Equal(cycles, current.CorrectionAttempts);
        Assert.Equal(5 + cycles * 2, current.GenerationReservations!.Count);
        Assert.Equal(1 + cycles, current.Workflow!.Revisions!.Last().Version);
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.All(current.Workflow.SoftwareCorrections, state => Assert.True(state.Ready));
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("repeated")]
    [InlineData("unchanged")]
    [InlineData("limit")]
    public async Task StopsWithoutPublishingAnInvalidOrNonprogressingCandidate(string stop)
    {
        var repairs = 0;
        using var fixture = new WorkflowFixture(corrections: stop == "limit" ? 0 : 3, sourceCheck: new Checker(),
            responseOverride: request =>
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                if (input.RootElement.TryGetProperty("allowedPaths", out _))
                {
                    repairs++;
                    if (stop == "unchanged")
                        return JsonSerializer.Serialize(new { files = input.RootElement.GetProperty("targets") }, Json);
                    return Correction(request, repairs);
                }
                var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
                if (result["criterionReviews"] is not null)
                    result["findings"] = JsonSerializer.SerializeToNode(new[]
                        { new ReviewFinding("src/App.tsx", "Error", "Repeated functional defect") }, Json);
                return result.ToJsonString();
            });
        var mission = Submit(fixture, stop == "budget" ? 6 : 20);
        if (stop == "limit")
        {
            await fixture.Processor.ProcessAsync(mission.Id, default);
            Assert.Equal(MissionStatus.Failed, fixture.Store.Get(mission.Id)!.Status);
            Assert.Contains("limit", fixture.Store.Get(mission.Id)!.Workflow!.FinalReport!, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var error = await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
            Assert.Contains(stop == "budget" ? "budget" : "progress", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Single(current.Workflow!.Revisions!);
        Assert.Equal(stop is "budget" or "limit" ? 0 : 1, repairs);
        Assert.False(current.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task AmbiguousDeveloperCallIsChargedAndNeverReplayedOnResume()
    {
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _)) throw new LlmTimeoutException("Ambiguous timeout");
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["path"]?.GetValue<string>() == "src/App.tsx") result["content"] = LargeApp();
            return result.ToJsonString();
        });
        var mission = Submit(fixture);
        await Assert.ThrowsAsync<LlmTimeoutException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Failed });
        var before = fixture.Provider.Requests.Count;
        Assert.False(fixture.Store.Resume(mission.Id));
        Assert.True(fixture.Store.Get(mission.Id)!.Workflow!.SoftwareCorrections.Last().InFlight);
        Assert.False(fixture.Store.Get(mission.Id)!.HasWorkspace);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.False(reopened.Resume(mission.Id));
        Assert.Equal(before, fixture.Provider.Requests.Count);
        Assert.Equal(before, reopened.Get(mission.Id)!.GenerationReservations!.Count);
    }

    [Fact]
    public async Task CompletedGroupCheckpointSurvivesRestartWithoutReplayingPaidCalls()
    {
        using var cancellation = new CancellationTokenSource();
        var checker = new Checker { AfterFirstGroup = cancellation.Cancel };
        var repairs = 0;
        using var fixture = new WorkflowFixture(sourceCheck: checker, responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _)) return Correction(request, ++repairs);
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["path"]?.GetValue<string>() is { } path && path is "src/App.tsx" or "src/model.ts")
                result["content"] = (path == "src/App.tsx" ? "export default function App() { return <main>Mission</main> }\n" : "") +
                    "export const broken = BROKEN;\n/*" + new string('x', 760) + "*/";
            return result.ToJsonString();
        });
        var mission = Submit(fixture);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Processor.ProcessAsync(mission.Id, cancellation.Token));
        Assert.Equal(1, repairs);
        var retained = fixture.Store.Get(mission.Id)!.Workflow!.SoftwareCorrections.Last();
        Assert.False(retained.InFlight);
        Assert.Equal(1, retained.CompletedGroups);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Throws<StudioValidationException>(() =>
            fixture.Store.Update(mission.Id, item => item with { Reason = "Obsolete process after restart" }));
        Assert.True(reopened.Resume(mission.Id));
        var processor = new CollaborativeProcessor(reopened,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options), fixture.Workspaces,
            fixture.Plans, fixture.Sandbox, fixture.Options, reactSourceCheck: checker);
        await processor.ProcessAsync(mission.Id, default);
        Assert.Equal(2, repairs);
        Assert.Equal(MissionStatus.AwaitingApproval, reopened.Get(mission.Id)!.Status);
        Assert.True(reopened.Get(mission.Id)!.Workflow!.SoftwareCorrections.Last().Ready);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task DockerDiagnosticCorrectionDiscardsOldAuthorizationAndWaitsForNewConsent()
    {
        var repairs = 0;
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            return input.RootElement.TryGetProperty("allowedPaths", out _) ? Correction(request, ++repairs) :
                MissionReactContractTests.Respond(request);
        });
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var previous = fixture.Store.Get(mission.Id)!.Workflow!.Revisions!.Last().Fingerprint;
        await fixture.Approve(mission.Id);
        fixture.Sandbox.Result = stage => new(stage, "Failed", 1, "src/App.tsx(1,1): error TS2322: simulated mismatch");
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(1, repairs);
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.False(current.SandboxApproved);
        Assert.Null(current.ApprovalFingerprint);
        Assert.NotEqual(previous, current.Workflow!.Revisions!.Last().Fingerprint);
        Assert.Equal(new[] { "TypeScript" }, fixture.Sandbox.Calls);
        Assert.False(fixture.Store.DecideApproval(mission.Id, true, previous));
        Assert.Equal(new[] { "TypeScript" }, fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task StaleProcessCannotOverwriteSoftwareCheckpointsOrTheirGenerationLedger()
    {
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: MissionReactContractTests.Respond);
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        using var stale = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        fixture.Store.Update(mission.Id, item => item with { Reason = "Current process checkpoint" });
        Assert.Throws<StudioValidationException>(() => stale.Update(mission.Id, item => item with { Reason = "Stale overwrite" }));
        Assert.Equal("Current process checkpoint", fixture.Store.GetLatestForRepair(mission.Id).Reason);
        Assert.Equal(5, fixture.Store.GetLatestForRepair(mission.Id).GenerationReservations!.Count);
        var response = JsonSerializer.Serialize(WorkflowResponse.From(fixture.Store.Get(mission.Id)!.Workflow!), Json);
        Assert.DoesNotContain("systemPrompt", response);
        Assert.DoesNotContain("responseSchema", response);
    }

    [Fact]
    public async Task DockerInfrastructureFailureDoesNotWasteADeveloperGeneration()
    {
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: MissionReactContractTests.Respond);
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        await fixture.Approve(mission.Id);
        fixture.Sandbox.Result = stage => new(stage, "Failed", 1, "Docker daemon unavailable; image missing");
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.BuildFailed, current.Status);
        Assert.Equal(5, current.GenerationReservations!.Count);
        Assert.Empty(current.Workflow!.SoftwareCorrections);
        Assert.Contains("infrastructure", current.Workflow.FinalReport);
        Assert.False(current.SandboxApproved);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task InterruptedPlannerDeveloperOrReviewerCallCannotBeReplayed(int failAt)
    {
        var calls = 0;
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: request =>
            ++calls == failAt ? throw new LlmTimeoutException("Ambiguous agent timeout") :
                MissionReactContractTests.Respond(request));
        var mission = Submit(fixture);
        await Assert.ThrowsAsync<LlmTimeoutException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        var current = fixture.Store.Get(mission.Id)!;
        Assert.NotNull(current.Workflow!.PendingSoftwareAgentCall);
        Assert.Equal(failAt, current.GenerationReservations!.Count);
        fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Failed });
        Assert.False(fixture.Store.Resume(mission.Id));
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.False(reopened.Resume(mission.Id));
        Assert.Equal(failAt, calls);
    }

    private static MissionSnapshot Submit(WorkflowFixture fixture, int budget = 20) =>
        fixture.Store.Submit(new("Display initial value 0", MissionKind.Project, TemplateId: MissionReactTemplate.Id,
            MaxTotalGenerations: budget, AutomaticReactCorrections: true));

    private static string LargeApp() => string.Join("\n", Enumerable.Range(0, 100).Select(index =>
        index == 50 ? "const broken = BROKEN;" : $"// synthetic unique padding line {index}")) +
        "\nexport default function App() { return <main>Mission</main> }";

    private static string Correction(LlmRequest request, int attempt)
    {
        using var input = JsonDocument.Parse(request.Messages.Last().Content);
        if (input.RootElement.TryGetProperty("patchTargets", out var targets))
            return JsonSerializer.Serialize(new { patches = targets.EnumerateArray().Select(target => new
            {
                path = target.GetProperty("path").GetString(), original = target.GetProperty("original").GetString(),
                replacement = target.GetProperty("original").GetString()!.Replace("BROKEN", "0", StringComparison.Ordinal)
            }) }, Json);
        return JsonSerializer.Serialize(new { files = input.RootElement.GetProperty("targets").EnumerateArray().Select(file =>
            new GeneratedFile(file.GetProperty("path").GetString()!,
                file.GetProperty("content").GetString()!.Replace("BROKEN", "0", StringComparison.Ordinal) + $"\n// software correction {attempt}")) }, Json);
    }

    private sealed class Checker : IReactSourceCheck, IReactSourceRepair
    {
        public bool Deterministic { get; init; }
        public Action? AfterFirstGroup { get; set; }
        public Task<IReadOnlyList<ReactSourceIssue>> CheckAsync(IReadOnlyList<GeneratedFile> files, CancellationToken token)
        {
            IReadOnlyList<ReactSourceIssue> issues = files.SelectMany(file => file.Content.Split('\n')
                .Select((line, index) => (line, index)).Where(item => item.line.Contains("BROKEN", StringComparison.Ordinal))
                .Select(item => new ReactSourceIssue(file.Path, item.index + 1, 1, "TS2304", "Undefined BROKEN"))).ToArray();
            if (AfterFirstGroup is { } action && issues.Count == 1 && issues[0].File == "src/model.ts" &&
                files.Single(file => file.Path == "src/App.tsx").Content.Contains("software correction", StringComparison.Ordinal))
            {
                AfterFirstGroup = null;
                action();
            }
            return Task.FromResult(issues);
        }
        public async Task<ReactSourceRepairResult> RepairAsync(IReadOnlyList<GeneratedFile> files,
            IReadOnlyList<string> allowedPaths, CancellationToken token)
        {
            var before = await CheckAsync(files, token);
            var candidate = Deterministic ? files.Select(file => allowedPaths.Contains(file.Path)
                ? file with { Content = file.Content.Replace("BROKEN", "0", StringComparison.Ordinal) } : file).ToArray() : files;
            return new(candidate, before, await CheckAsync(candidate, token), [], []);
        }
    }
}
