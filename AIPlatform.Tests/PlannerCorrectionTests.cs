using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class PlannerCorrectionTests
{
    [Theory]
    [InlineData("react", "", 1)]
    [InlineData("react-dom", "", 1)]
    [InlineData("vitest", "", 1)]
    [InlineData("@testing-library/react", "", 1)]
    [InlineData("react|@testing-library/react", "", 2)]
    [InlineData("src/model.ts", "src/model.ts", 0)]
    [InlineData("react|src/model.ts|vitest", "src/model.ts", 2)]
    [InlineData("react|react-dom|vitest|@testing-library/react|src/model.ts", "src/model.ts", 4)]
    public void OnlyExactScaffoldPackagesAreRemovedFromInternalDependencies(
        string dependencies, string retained, int removed)
    {
        var plan = DependencyPlan(dependencies.Split('|'));
        var normalized = MissionReactTemplate.NormalizePlanDependencies(plan, out var changes);

        Assert.Equal(retained.Length == 0 ? [] : retained.Split('|'), normalized.Files[0].DependsOn);
        Assert.Equal(new[] { "src/App.tsx" }, normalized.Files[1].DependsOn);
        Assert.Equal(plan.Dependencies, normalized.Dependencies);
        Assert.Equal(dependencies.Split('|'), plan.Files[0].DependsOn);
        Assert.Equal(removed, changes.Count);
        Assert.All(changes, change =>
        {
            Assert.Equal("src/App.tsx", change.Path);
            Assert.Contains(change.Package, MissionReactTemplate.AllowedDependencies);
        });
        MissionReactTemplate.ValidatePlan(normalized);
    }

    [Theory]
    [InlineData("unknown", "UnknownDependency")]
    [InlineData("case", "UnknownDependency")]
    [InlineData("whitespace", "UnknownDependency")]
    [InlineData("ambiguous", "DuplicatePlannedPath")]
    [InlineData("collision", "UnauthorizedPath")]
    [InlineData("cycle", "CyclicDependencies")]
    [InlineData("oversized", "InvalidDependencies")]
    public void NormalizationNeverHidesUnknownAmbiguousCyclicOrOversizedDependencies(string defect, string rule)
    {
        var dependency = defect switch
        {
            "unknown" => "react-testing-library",
            "case" => "React",
            "whitespace" => " react",
            "cycle" => "src/App.test.tsx",
            "collision" => "react",
            _ => "src/model.ts"
        };
        var plan = DependencyPlan(defect == "oversized"
            ? Enumerable.Repeat("react", 13).ToArray() : [dependency]);
        if (defect == "ambiguous")
            plan = plan with { Files = [.. plan.Files, plan.Files[2] with { Path = "src/MODEL.ts" }] };
        if (defect == "collision")
            plan = plan with { Files = [.. plan.Files, new("react", "Ambiguous package/file name", [])] };

        var normalized = MissionReactTemplate.NormalizePlanDependencies(plan, out var changes);

        Assert.Empty(changes);
        Assert.Equal(plan.Files[0].DependsOn, normalized.Files[0].DependsOn);
        var error = Assert.Throws<StudioValidationException>(() => MissionReactTemplate.ValidatePlan(normalized));
        Assert.Contains(error.PlannerDiagnostics!, diagnostic => diagnostic.Rule == rule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("src/missing.ts")]
    [InlineData("react-testing-library")]
    public async Task NormalizedPlansUseNoExtraCallUnlessAnotherValidationStillFails(string? unknownReference)
    {
        var calls = 0;
        using var fixture = new WorkflowFixture(responseOverride: request =>
        {
            Assert.True(IsPlan(request));
            calls++;
            var result = JsonNode.Parse(Response(request, false))!;
            if (calls == 1)
            {
                result["files"]![0]!["dependsOn"] = new JsonArray("react", "react-dom", "src/model.ts");
                result["files"]![1]!["dependsOn"] = unknownReference is not null
                    ? new JsonArray("vitest", "@testing-library/react", "src/App.tsx", unknownReference)
                    : new JsonArray("vitest", "@testing-library/react", "src/App.tsx");
            }
            else
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                var error = Assert.Single(input.RootElement.GetProperty("validationErrors").EnumerateArray());
                Assert.Equal("UnknownDependency", error.GetProperty("rule").GetString());
                Assert.Equal("files[1].dependsOn", error.GetProperty("field").GetString());
                Assert.Equal(unknownReference, error.GetProperty("path").GetString());
                var explanation = error.GetProperty("explanation").GetString()!;
                Assert.Contains("exact paths of other planned files, not npm packages", explanation);
                Assert.Contains("Unknown names are not converted", explanation);
                foreach (var package in MissionReactTemplate.AllowedDependencies)
                    Assert.Contains(package, explanation);
                Assert.Contains(unknownReference!, input.RootElement.GetProperty("rejectedPlan")
                    .GetProperty("files")[1].GetProperty("dependsOn").EnumerateArray()
                    .Select(value => value.GetString()));
            }
            Assert.Contains("Never put npm packages in files[].dependsOn", request.Messages[0].Content);
            var schema = request.ResponseSchema!.Value.GetProperty("properties");
            Assert.Contains("npm package names", schema.GetProperty("dependencies").GetProperty("description").GetString());
            Assert.Contains("No npm packages", schema.GetProperty("files").GetProperty("items")
                .GetProperty("properties").GetProperty("dependsOn").GetProperty("description").GetString());
            Assert.Equal(MissionReactTemplate.AllowedDependencies, schema.GetProperty("dependencies")
                .GetProperty("items").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
            Assert.Contains($"Exact allowed npm package names: {string.Join(", ", MissionReactTemplate.AllowedDependencies)}.",
                request.Messages[0].Content);
            var exampleStart = request.Messages[0].Content.IndexOf("{\"name\":\"Counter\"", StringComparison.Ordinal);
            Assert.True(exampleStart >= 0);
            var exampleEnd = request.Messages[0].Content.IndexOf(" Tests use Vitest", exampleStart, StringComparison.Ordinal);
            Assert.True(exampleEnd > exampleStart);
            var example = JsonSerializer.Deserialize<ProjectPlan>(
                request.Messages[0].Content[exampleStart..exampleEnd], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            MissionReactTemplate.ValidatePlan(example with
            {
                OriginalRequest = "Initial value 0",
                AcceptanceCriteria = [new("C1", "Initial value 0")]
            });
            return result.ToJsonString();
        });
        var mission = fixture.Store.Submit(new("Initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, RequirePlanApproval: true, MaxTotalGenerations: 12));

        await fixture.Processor.ProcessAsync(mission.Id, default);

        var current = fixture.Store.Get(mission.Id)!;
        var expected = unknownReference is not null ? 2 : 1;
        Assert.Equal(MissionStatus.AwaitingPlanApproval, current.Status);
        Assert.Equal(expected, calls);
        Assert.Equal(expected, current.GenerationReservations!.Count);
        Assert.Equal(12 - expected, current.MaxTotalGenerations - current.GenerationReservations.Count);
        Assert.Equal(expected - 1, current.CorrectionAttempts);
        Assert.Equal(new[] { "src/model.ts" }, current.Workflow!.Plan!.Files[0].DependsOn);
        Assert.Equal(new[] { "src/App.tsx" }, current.Workflow.Plan.Files[1].DependsOn);
        Assert.Equal(4, current.Workflow.Plan.Dependencies!.Count);
        Assert.Null(current.Workflow.Draft?.SingleOrDefault(file => file.Path == "src/App.tsx"));
        Assert.Empty(fixture.Sandbox.Calls);
        var attempts = new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).PlannerAttempts(mission.Id);
        Assert.Equal(expected, attempts.Count);
        Assert.Equal(unknownReference is not null ? "Rejected" : "Accepted", attempts[0].Status);
        Assert.Equal(4, attempts[0].DependencyNormalizations!.Count);
        Assert.Equal("files[0].dependsOn[0]", attempts[0].DependencyNormalizations![0].Field);
        Assert.Equal("react", attempts[0].Plan!.Value.GetProperty("files")[0].GetProperty("dependsOn")[0].GetString());
        Assert.False(attempts[0].RawTraceAvailable);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(current.GenerationReservations, reopened.Get(mission.Id)!.GenerationReservations);
        Assert.Equal(current.MaxTotalGenerations, reopened.Get(mission.Id)!.MaxTotalGenerations);
        Assert.Equal(attempts[0].DependencyNormalizations,
            new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).PlannerAttempts(mission.Id)[0].DependencyNormalizations);
    }

    private static ProjectPlan DependencyPlan(IReadOnlyList<string> dependencies) =>
        new("Counter", "Requested counter", ["Use Vitest."],
        [
            new("src/App.tsx", "Counter component", dependencies),
            new("src/App.test.tsx", "Counter tests", ["src/App.tsx"]),
            new("src/model.ts", "Counter state", [])
        ])
        {
            OriginalRequest = "Initial value 0",
            AcceptanceCriteria = [new("C1", "Initial value 0")],
            Dependencies = ["react", "vitest"],
            CriterionCoverage = [new("C1", ["src/App.tsx", "src/App.test.tsx"], "Implement and test initial value.")]
        };

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task OnlyAValidatedPlanCanContinueWithinThreeGenerations(int rejected, bool approval)
    {
        var calls = 0;
        using var fixture = new WorkflowFixture(responseOverride: request =>
        {
            if (!IsPlan(request))
            {
                Assert.True(calls > rejected);
                return MissionReactContractTests.Respond(request);
            }
            calls++;
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (calls > 1)
            {
                Assert.Equal("- Initial value 0\n- Increment", input.RootElement.GetProperty("request").GetString());
                Assert.Equal(new[] { "C1", "C2" }, input.RootElement.GetProperty("acceptanceCriteria")
                    .EnumerateArray().Select(item => item.GetProperty("id").GetString()));
                Assert.Equal("src/App.tsx", input.RootElement.GetProperty("rejectedPlan")
                    .GetProperty("criterionCoverage")[0].GetProperty("paths")[0].GetString());
                var diagnostic = Assert.Single(input.RootElement.GetProperty("validationErrors").EnumerateArray());
                Assert.Equal("C1", diagnostic.GetProperty("criterionId").GetString());
                Assert.Equal("MissingTest", diagnostic.GetProperty("rule").GetString());
                Assert.Equal("criterionCoverage.paths", diagnostic.GetProperty("field").GetString());
                Assert.Contains("Preserve every original criterion", request.Messages[0].Content);
                Assert.Contains("SAME response schema", request.Messages[0].Content);
                Assert.Contains(".test.ts or .test.tsx", request.Messages[0].Content);
                Assert.Contains("untrusted data", request.Messages[0].Content);
            }
            else Assert.False(input.RootElement.TryGetProperty("rejectedPlan", out _));
            return Response(request, calls <= rejected);
        });
        var mission = fixture.Store.Submit(new("- Initial value 0\n- Increment", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, RequirePlanApproval: approval));
        if (rejected == 3)
            await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        else await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        var expected = Math.Min(3, rejected + 1);
        Assert.Equal(expected, calls);
        Assert.Equal(expected, current.Workflow!.Steps.Single(step => step.Kind == "Plan").Attempts);
        Assert.Equal(expected - 1, current.CorrectionAttempts);
        var attempts = new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).PlannerAttempts(mission.Id);
        Assert.Equal(Enumerable.Range(1, expected), attempts.Select(item => item.AttemptNumber));
        Assert.Equal(Math.Min(3, rejected), attempts.Count(item => item.Status == "Rejected"));
        Assert.All(attempts.Where(item => item.Status == "Rejected"), item =>
            Assert.Equal("MissingTest", Assert.Single(item.Errors).Rule));
        Assert.All(attempts, item => Assert.False(item.RawTraceAvailable));
        Assert.Equal(Math.Min(3, rejected), current.Events.Count(item => item.Step == "PlanValidation"));
        Assert.Equal(Math.Min(2, rejected), current.Events.Count(item => item.Step == "PlanCorrection"));
        Assert.False(current.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Empty(current.Verification);
        if (rejected == 3)
        {
            Assert.Null(current.Workflow.Plan);
            Assert.Null(current.Workflow.PlanApproval);
            Assert.False(current.HasWorkspace);
            Assert.All(current.Workflow.Steps.Where(step => step.Kind != "Plan"),
                step => Assert.Equal(WorkflowStatus.Pending, step.Status));
            fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Failed });
            Assert.False(fixture.Store.Resume(mission.Id));
            return;
        }
        Assert.Equal(approval ? MissionStatus.AwaitingPlanApproval : MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal(new[] { "C1", "C2" }, current.Workflow.Plan!.AcceptanceCriteria!.Select(item => item.Id));
        Assert.Equal(mission.Workflow!.AcceptanceCriteria, current.Workflow.Plan.AcceptanceCriteria);
        if (!approval) return;
        Assert.Equal(calls, fixture.Provider.Requests.Count);
        Assert.False(current.HasWorkspace);
        Assert.True(fixture.Store.DecidePlanApproval(mission.Id, true, current.Workflow.PlanApproval!.Fingerprint));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(expected, calls);
        Assert.Equal(MissionStatus.AwaitingApproval, fixture.Store.Get(mission.Id)!.Status);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(2, 1, 2)]
    public async Task SharedCorrectionBudgetCanStopBeforeThePlannerCeiling(int limit, int consumed, int generations)
    {
        using var fixture = new WorkflowFixture(corrections: limit, responseOverride: request => Response(request, true));
        var mission = fixture.Store.Submit(new("Initial value 0", MissionKind.Project, TemplateId: MissionReactTemplate.Id));
        fixture.Store.Update(mission.Id, item => item with { CorrectionAttempts = consumed });
        await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Equal(generations, fixture.Provider.Requests.Count);
        Assert.Equal(limit, fixture.Store.Get(mission.Id)!.CorrectionAttempts);
        Assert.False(fixture.Store.Get(mission.Id)!.HasWorkspace);
    }

    [Fact]
    public async Task CancellationAndRestartRetainTheLastRejectionAndNeverResetTheCeiling()
    {
        using var fixture = new WorkflowFixture(responseOverride: request => Response(request, true));
        var mission = fixture.Store.Submit(new("Initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, RequirePlanApproval: true));
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new FakeProvider(async (request, token) =>
        {
            if (++calls == 1) return Response(request, true);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "";
        });
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([provider]), fixture.Options),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options);
        var running = processor.ProcessAsync(mission.Id, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(fixture.Store.Get(mission.Id)!.Workflow!.Plan);
        Assert.False(fixture.Store.Get(mission.Id)!.HasWorkspace);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(MissionStatus.Cancelled, reopened.Get(mission.Id)!.Status);
        Assert.True(reopened.Resume(mission.Id));
        var resumedProvider = new FakeProvider((request, _) =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            Assert.Equal("MissingTest", input.RootElement.GetProperty("validationErrors")[0].GetProperty("rule").GetString());
            return Task.FromResult(Response(request, false));
        });
        var resumed = new CollaborativeProcessor(reopened,
            new WorkflowAgents(new LlmProviderResolver([resumedProvider]), fixture.Options),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options);
        await resumed.ProcessAsync(mission.Id, default);
        var current = reopened.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingPlanApproval, current.Status);
        Assert.Equal(3, current.Workflow!.Steps.Single(step => step.Kind == "Plan").Attempts);
        Assert.Equal(2, current.CorrectionAttempts);
        Assert.Equal(2, calls);
        Assert.Single(resumedProvider.Requests);
        var attempts = new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).PlannerAttempts(mission.Id);
        Assert.Equal(new[] { "Rejected", "Cancelled", "Accepted" }, attempts.Select(item => item.Status));
        Assert.Equal(new[] { 1, 2, 3 }, attempts.Select(item => item.AttemptNumber));
        Assert.False(current.HasWorkspace);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task InventedCriteriaAreRejectedRatherThanManufacturingCoverage()
    {
        using var fixture = new WorkflowFixture(responseOverride: request =>
        {
            var result = JsonNode.Parse(Response(request, false))!;
            result["criterionCoverage"]![0]!["criterionId"] = "invented";
            return result.ToJsonString();
        });
        var mission = fixture.Store.Submit(new("Initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, RequirePlanApproval: true));
        await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Equal(3, fixture.Provider.Requests.Count);
        Assert.Null(fixture.Store.Get(mission.Id)!.Workflow!.Plan);
        Assert.Single(fixture.Store.Get(mission.Id)!.Workflow!.AcceptanceCriteria);
        Assert.All(new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).PlannerAttempts(mission.Id), item =>
            Assert.Contains(item.Errors, error => error.Rule == "UnknownCriterion" && error.CriterionId == "invented"));
    }

    [Fact]
    public async Task OtherProjectContractsUseTheSameCorrectionBoundaryWithoutChangingTheirValidator()
    {
        var calls = 0;
        using var fixture = new WorkflowFixture(responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (++calls == 2)
                Assert.Contains("Required planned path missing: src/App/Program.cs",
                    input.RootElement.GetProperty("validationErrors")[0].GetProperty("explanation").GetString());
            var plan = calls == 1 ? WorkflowFixture.Plan with
            {
                Files = WorkflowFixture.Plan.Files.Select(file => file.Path == "src/App/Program.cs"
                    ? file with { Path = "src/App/Other.cs" } : file).ToArray()
            } : WorkflowFixture.Plan;
            return JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        var mission = fixture.Store.Submit(new("Create the requested project", MissionKind.Project, RequirePlanApproval: true));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(2, calls);
        Assert.Equal(MissionStatus.AwaitingPlanApproval, fixture.Store.Get(mission.Id)!.Status);
        Assert.Empty(fixture.Store.Get(mission.Id)!.Workflow!.AcceptanceCriteria);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task HistoricalMissionWithoutThePolicyKeepsItsSingleAttemptBehavior()
    {
        using var fixture = new WorkflowFixture(responseOverride: request => Response(request, true));
        var mission = fixture.Store.Submit(new("Initial value 0", MissionKind.Project, TemplateId: MissionReactTemplate.Id));
        fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Failed });
        var path = Path.Combine(fixture.Options.DataDirectory, "missions", mission.Id.ToString("N") + ".json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["workflow"]!.AsObject().Remove("plannerGenerationLimit");
        File.WriteAllText(path, json.ToJsonString());
        var bytes = File.ReadAllBytes(path);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(0, reopened.Get(mission.Id)!.Workflow!.PlannerGenerationLimit);
        Assert.True(reopened.Resume(mission.Id));
        var processor = new CollaborativeProcessor(reopened,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options);
        await Assert.ThrowsAsync<StudioValidationException>(() => processor.ProcessAsync(mission.Id, default));
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(0, reopened.Get(mission.Id)!.CorrectionAttempts);
    }

    private static bool IsPlan(LlmRequest request) =>
        request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("decisions", out _);

    private static string Response(LlmRequest request, bool rejected)
    {
        var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
        if (rejected) result["criterionCoverage"]![0]!["paths"] = new JsonArray("src/App.tsx");
        return result.ToJsonString();
    }
}
