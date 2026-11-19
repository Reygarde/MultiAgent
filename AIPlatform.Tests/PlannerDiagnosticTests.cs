using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class PlannerDiagnosticTests
{
    [Theory]
    [InlineData("missing", "MissingCoverage", "C1", "criterionCoverage")]
    [InlineData("unknown", "UnknownCriterion", "CX", "criterionCoverage.criterionId")]
    [InlineData("explanation", "InvalidExplanation", "C1", "criterionCoverage.explanation")]
    [InlineData("duplicate", "DuplicatePath", "C1", "criterionCoverage.paths")]
    [InlineData("unsafe", "UnauthorizedPath", "C1", "criterionCoverage.paths")]
    [InlineData("unplanned", "PathNotPlanned", "C1", "criterionCoverage.paths")]
    [InlineData("implementation", "MissingImplementation", "C1", "criterionCoverage.paths")]
    [InlineData("test", "MissingTest", "C1", "criterionCoverage.paths")]
    [InlineData("scaffold", "UnauthorizedPath", "C1", "criterionCoverage.paths")]
    [InlineData("name", "InvalidName", null, "name")]
    [InlineData("purpose", "InvalidPurpose", null, "files[0].purpose")]
    [InlineData("duplicate-file", "DuplicatePlannedPath", null, "files.path")]
    [InlineData("dependency", "UnknownDependency", null, "files[0].dependsOn")]
    [InlineData("cycle", "CyclicDependencies", null, "files.dependsOn")]
    public async Task RejectedStructuredPlanIsPreservedWithExactPrivateDiagnostics(
        string defect, string rule, string? criterion, string field)
    {
        string? actual = null;
        using var fixture = new WorkflowFixture(corrections: 0, responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            var coverage = result["criterionCoverage"]!.AsArray();
            switch (defect)
            {
                case "missing": coverage.RemoveAt(0); break;
                case "unknown": coverage[0]!["criterionId"] = "CX"; break;
                case "explanation": coverage[0]!["explanation"] = " "; break;
                case "duplicate": coverage[0]!["paths"]!.AsArray().Add("src/App.tsx"); break;
                case "unsafe": coverage[0]!["paths"]![0] = "src/../sensitive-demo-path.tsx"; break;
                case "unplanned": coverage[0]!["paths"]![0] = "src/sensitive-demo-path.tsx"; break;
                case "implementation": coverage[0]!["paths"] = new JsonArray("src/App.test.tsx"); break;
                case "test": coverage[0]!["paths"] = new JsonArray("src/App.tsx"); break;
                case "scaffold": coverage[0]!["paths"]![0] = "tsconfig.json"; break;
                case "name": result["name"] = " "; break;
                case "purpose": result["files"]![0]!["purpose"] = " "; break;
                case "duplicate-file": result["files"]!.AsArray().Add(result["files"]![0]!.DeepClone()); break;
                case "dependency": result["files"]![0]!["dependsOn"] = new JsonArray("src/missing.ts"); break;
                case "cycle":
                    result["files"]![0]!["dependsOn"] = new JsonArray("src/App.test.tsx");
                    break;
            }
            return actual = result.ToJsonString();
        });
        var mission = fixture.Store.Submit(new("Counter initial value 0\nIncrement", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id));
        var exception = await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        var store = new OllamaDiagnosticStore(fixture.Options, TimeProvider.System);
        var artifact = Assert.Single(store.PlannerAttempts(mission.Id));
        var error = Assert.Single(artifact.Errors, item => item.Rule == rule && item.CriterionId == criterion);
        Assert.Equal(field, error.Field);
        if (defect == "unplanned") Assert.Equal("src/sensitive-demo-path.tsx", error.Path);
        Assert.DoesNotContain("sensitive-demo-path", exception.Message);
        Assert.Equal(exception.PlannerDiagnostics, artifact.Errors);
        Assert.Equal("Rejected", artifact.Status);
        Assert.Equal(JsonNode.Parse(actual!)!.ToJsonString(), JsonNode.Parse(artifact.Plan!.Value.GetRawText())!.ToJsonString());
        Assert.Equal(mission.Workflow!.WorkflowId, artifact.RunId);
        Assert.Equal(1, artifact.AttemptNumber);
        Assert.Equal(fixture.Provider.Requests.Single().Model.Name, artifact.Model);
        Assert.Equal(mission.Workflow.AcceptanceCriteria, artifact.Criteria);
        Assert.False(artifact.RawTraceAvailable);
        Assert.Null(store.PlannerRawResponse(mission.Id, artifact.AttemptId));
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Null(current.Workflow!.Plan);
        Assert.Null(current.Workflow.Draft);
        Assert.False(current.HasWorkspace);
        Assert.Empty(current.Verification);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.False(fixture.Store.DecideApproval(mission.Id, true, "not-an-approved-plan"));
        Assert.All(fixture.Provider.Requests, request => Assert.Null(request.DiagnosticContext));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidPlansAreAcceptedButRawBodiesRequireSeparateExplicitConsent(bool enabled)
    {
        OllamaDiagnosticStore? diagnostics = null;
        const string wireResponse = "{\"done\":true,\"message\":{\"content\":\"private exact wire envelope\"}}";
        using var fixture = new WorkflowFixture(corrections: 0, responseOverride: request =>
        {
            if (request.DiagnosticContext is { } context)
            {
                diagnostics!.RecordRequest(context, request.Model.Name, Encoding.UTF8.GetBytes("{\"messages\":[]}"));
                diagnostics.RecordResponse(context, 200, Encoding.UTF8.GetBytes(wireResponse));
            }
            return MissionReactContractTests.Respond(request);
        });
        diagnostics = new(fixture.Options, TimeProvider.System);
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options, diagnosticStore: diagnostics),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options);
        var mission = fixture.Store.Submit(new("Counter initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, RecordPlannerTrace: enabled));
        await processor.ProcessAsync(mission.Id, default);
        Assert.Equal(MissionStatus.AwaitingApproval, fixture.Store.Get(mission.Id)!.Status);
        var artifact = Assert.Single(diagnostics.PlannerAttempts(mission.Id));
        Assert.Equal("Accepted", artifact.Status);
        Assert.Empty(artifact.Errors);
        Assert.Equal(enabled, artifact.RawTraceAvailable);
        Assert.Equal(enabled ? wireResponse : null, diagnostics.PlannerRawResponse(mission.Id, artifact.AttemptId));
        Assert.Equal(enabled ? 1 : 0, fixture.Provider.Requests.Count(request => request.DiagnosticContext is not null));
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidStructuredResponsesRetainAvailableJsonButNeverInventAPlan(bool parseable)
    {
        using var fixture = new WorkflowFixture(corrections: 0, responseOverride: _ =>
            parseable ? "{\"unexpectedField\":\"diagnostic-only value\"}" : "not JSON");
        var mission = fixture.Store.Submit(new("Display counter initial value 0", MissionKind.Project, TemplateId: MissionReactTemplate.Id));
        await Assert.ThrowsAsync<LlmProviderException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        var artifact = Assert.Single(new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).PlannerAttempts(mission.Id));
        Assert.Equal("Failed", artifact.Status);
        Assert.Equal(parseable, artifact.Plan.HasValue);
        if (parseable) Assert.Equal("diagnostic-only value", artifact.Plan!.Value.GetProperty("unexpectedField").GetString());
        Assert.False(artifact.RawTraceAvailable);
        Assert.Equal("ProviderOrStructuredResponseFailure", artifact.Errors.Single().Rule);
        Assert.Null(fixture.Store.Get(mission.Id)!.Workflow!.Plan);
        Assert.Single(fixture.Provider.Requests);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public void PrivateArtifactsUseExistingSizeRetentionAndDeletionAndNeverRewriteMissionHistory()
    {
        using var fixture = new WorkflowFixture();
        var options = fixture.Options with { OllamaTraceMaxFiles = 2, OllamaTraceMaxBytes = 2048 };
        var diagnostics = new OllamaDiagnosticStore(options, TimeProvider.System);
        var mission = fixture.Store.Submit(new("Display counter initial value 0", MissionKind.Project, TemplateId: MissionReactTemplate.Id));
        var history = JsonSerializer.Serialize(fixture.Store.Get(mission.Id));
        var first = new LlmDiagnosticContext(mission.Id, mission.Workflow!.WorkflowId, "plan", Guid.NewGuid());
        using var result = JsonDocument.Parse("{\"files\":[]}");
        diagnostics.BeginPlanner(first, "fake-model", 1, mission.Workflow.AcceptanceCriteria, false);
        diagnostics.RecordPlannerResult(first, result.RootElement);
        Assert.Throws<LlmDiagnosticException>(() => diagnostics.RecordRequest(first, "fake-model", "{}"u8.ToArray()));
        Assert.Throws<StudioValidationException>(() => diagnostics.DeletePlannerAttempts(mission.Id));
        diagnostics.FinishPlanner(first, "Rejected", []);
        var second = first with { AttemptId = Guid.NewGuid() };
        diagnostics.BeginPlanner(second, "fake-model", 2, mission.Workflow.AcceptanceCriteria, false);
        using var huge = JsonDocument.Parse("{\"name\":\"" + new string('x', 4096) + "\"}");
        Assert.Throws<LlmDiagnosticException>(() => diagnostics.RecordPlannerResult(second, huge.RootElement));
        diagnostics.FinishPlanner(second, "Failed", []);
        var other = first with { WorkspaceId = Guid.NewGuid(), AttemptId = Guid.NewGuid() };
        diagnostics.BeginPlanner(other, "fake-model", 1, [], false);
        diagnostics.FinishPlanner(other, "Rejected", []);
        Assert.Single(diagnostics.PlannerAttempts(mission.Id));
        diagnostics.DeletePlannerAttempts(mission.Id);
        Assert.Empty(diagnostics.PlannerAttempts(mission.Id));
        Assert.Single(diagnostics.PlannerAttempts(other.WorkspaceId));
        var path = Assert.Single(Directory.GetFiles(Path.Combine(options.DataDirectory, "ollama-traces")));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
        Assert.Empty(diagnostics.PlannerAttempts(other.WorkspaceId));
        Assert.Equal(history, JsonSerializer.Serialize(fixture.Store.Get(mission.Id)));
    }
}
