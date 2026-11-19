using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIPlatform.API;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIPlatform.Tests;

public sealed class WorkflowApiTests
{
    [Fact]
    public async Task SizingProfileIsSelectedByIdAndFrozenInTheHttpResponseWithoutCallingAModel()
    {
        using var fixture = new WorkflowFixture();
        using var factory = Factory(fixture).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>()));
        using var client = factory.CreateClient();
        var capacity = (await client.GetFromJsonAsync<MissionCapacityResponse>("/api/missions/capacity"))!;
        Assert.Contains(capacity.SizingProfiles!, profile => profile.Id == "complex");
        foreach (var invalid in new[]
        {
            new CreateMissionRequest("Display item", MissionKind.Chat, SizingProfileId: "small"),
            new CreateMissionRequest("Display item", MissionKind.Project, SizingProfileId: "small"),
            new CreateMissionRequest("Display item", MissionKind.Project,
                TemplateId: MissionReactTemplate.Id, SizingProfileId: "unknown")
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/missions", invalid)).StatusCode);
        var response = await client.PostAsJsonAsync("/api/missions", new CreateMissionRequest(
            "Display item", MissionKind.Project, TemplateId: MissionReactTemplate.Id, SizingProfileId: "complex",
            MaxTotalGenerations: 9));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var mission = (await response.Content.ReadFromJsonAsync<MissionResponse>(JsonOptions))!;
        Assert.Equal("complex", mission.Sizing!.Id);
        Assert.Equal(12, mission.Workflow!.CorrectionLimit);
        Assert.Equal(9, mission.MaxTotalGenerations);
        Assert.Equal(0, mission.TotalGenerations);
        Assert.False(mission.SandboxApproved);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task GenerationBudgetRoundTripsAndStopsBeforeReviewerWithoutChangingTheLimit()
    {
        using var fixture = new WorkflowFixture(responseOverride: MissionReactContractTests.Respond);
        using var factory = Factory(fixture).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>()));
        using var client = factory.CreateClient();
        foreach (var invalid in new[] { 0, -1, 1001 })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/missions",
                new CreateMissionRequest("Display information", MissionKind.Project,
                    TemplateId: MissionReactTemplate.Id, MaxTotalGenerations: invalid))).StatusCode);
        var response = await client.PostAsJsonAsync("/api/missions",
            new CreateMissionRequest("Display information", MissionKind.Project,
                TemplateId: MissionReactTemplate.Id, MaxTotalGenerations: 4));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<MissionResponse>(JsonOptions))!;
        Assert.Equal(4, created.MaxTotalGenerations);
        Assert.Equal(0, created.TotalGenerations);
        Assert.Equal(4, created.RemainingGenerations);
        await Assert.ThrowsAsync<GenerationBudgetExhaustedException>(() =>
            factory.Services.GetRequiredService<CollaborativeProcessor>().ProcessAsync(created.Id, default));
        var final = (await client.GetFromJsonAsync<MissionResponse>($"/api/missions/{created.Id}", JsonOptions))!;
        Assert.Equal(4, final.MaxTotalGenerations);
        Assert.Equal(4, final.TotalGenerations);
        Assert.Equal(0, final.RemainingGenerations);
        Assert.Equal(4, final.GenerationReservations!.Count);
        Assert.Equal(MissionStatus.Failed, final.Status);
        Assert.StartsWith("GenerationBudgetExhausted", final.Error);
        var defaults = (await client.GetFromJsonAsync<MissionCapacityResponse>("/api/missions/capacity"))!;
        Assert.Equal(fixture.Options.DefaultMaxTotalGenerations, defaults.DefaultMaxTotalGenerations);
        Assert.Throws<StudioValidationException>(() => factory.Services.GetRequiredService<MissionStore>()
            .Update(created.Id, mission => mission with { MaxTotalGenerations = 20 }));
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanApprovalRequiresTheSavedFingerprintAndDoesNotAuthorizeExecution(bool approved)
    {
        using var fixture = new WorkflowFixture(corrections: 0, responseOverride: MissionReactContractTests.Respond);
        using var factory = Factory(fixture).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>()));
        using var client = factory.CreateClient();
        if (approved)
        {
            var openApi = await client.GetAsync("/openapi/v1.json");
            openApi.EnsureSuccessStatusCode();
            Assert.Contains("/api/missions/{id}/plan-approval", await openApi.Content.ReadAsStringAsync());
        }
        var creation = await client.PostAsJsonAsync("/api/missions",
            new CreateMissionRequest("Display requested information", MissionKind.Project,
                TemplateId: MissionReactTemplate.Id, RequirePlanApproval: true));
        creation.EnsureSuccessStatusCode();
        var mission = (await creation.Content.ReadFromJsonAsync<MissionResponse>(JsonOptions))!;
        var store = factory.Services.GetRequiredService<MissionStore>();
        await factory.Services.GetRequiredService<CollaborativeProcessor>().ProcessAsync(mission.Id, default);
        var waiting = (await client.GetFromJsonAsync<MissionResponse>($"/api/missions/{mission.Id}", JsonOptions))!;
        Assert.Equal(MissionStatus.AwaitingPlanApproval, waiting.Status);
        Assert.True(waiting.Workflow!.RequirePlanApproval);
        Assert.NotNull(waiting.Workflow.Plan);
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/resume", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval",
            new DecideMissionApprovalRequest(true, waiting.Workflow.PlanApproval!.Fingerprint))).StatusCode);
        var endpoint = $"/api/missions/{mission.Id}/plan-approval";
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/missions/{Guid.NewGuid()}/plan-approval",
            new DecidePlanApprovalRequest(approved, "unknown"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(endpoint,
            new DecidePlanApprovalRequest(approved, "stale"))).StatusCode);
        var history = JsonSerializer.Serialize(store.Get(mission.Id));
        await client.GetAsync($"/api/missions/{mission.Id}/workflow");
        Assert.Equal(history, JsonSerializer.Serialize(store.Get(mission.Id)));
        var decision = new DecidePlanApprovalRequest(approved, waiting.Workflow.PlanApproval.Fingerprint);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(endpoint, decision)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(endpoint, decision)).StatusCode);
        var current = store.Get(mission.Id)!;
        Assert.Equal(approved ? MissionStatus.Queued : MissionStatus.Cancelled, current.Status);
        Assert.Equal(approved, current.Workflow!.PlanApproval!.Approved);
        Assert.False(current.SandboxApproved);
        Assert.Null(current.ApprovalFingerprint);
        Assert.Single(fixture.Provider.Requests);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task PrivateReviewerInspectionAndDeletionPreservePlannerHistoryAndBudgets()
    {
        using var fixture = new WorkflowFixture();
        using var factory = Factory(fixture).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>()));
        using var client = factory.CreateClient();
        var creation = await client.PostAsJsonAsync("/api/missions",
            new CreateMissionRequest("Counter initial value 0", MissionKind.Project, TemplateId: MissionReactTemplate.Id));
        creation.EnsureSuccessStatusCode();
        var mission = (await creation.Content.ReadFromJsonAsync<MissionResponse>(JsonOptions))!;
        var missions = factory.Services.GetRequiredService<MissionStore>();
        var diagnostics = factory.Services.GetRequiredService<OllamaDiagnosticStore>();
        var context = new LlmDiagnosticContext(mission.Id, mission.Workflow!.WorkflowId, "review-1", Guid.NewGuid());
        diagnostics.BeginReviewer(context, "simulated-reviewer", 1, "source-hash", null);
        using var report = JsonDocument.Parse("""{"summary":"Rejected report","criterionReviews":[]}""");
        diagnostics.RecordReviewerResult(context, report.RootElement);
        diagnostics.FinishReviewer(context, "Rejected",
            [new("C1", "QuoteMismatch", "criterionReviews[0].evidence[0].quote", "src/App.tsx", "bad quote", "Copy source exactly.")]);
        var planner = context with { AttemptId = Guid.NewGuid(), TaskId = "plan" };
        diagnostics.BeginPlanner(planner, "simulated-planner", 1, missions.Get(mission.Id)!.Workflow!.AcceptanceCriteria, false);
        diagnostics.FinishPlanner(planner, "Accepted", []);
        var response = await client.GetAsync($"/api/missions/{mission.Id}/reviewer-diagnostics");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var entry = Assert.Single((await response.Content.ReadFromJsonAsync<ReviewerAttemptDiagnostic[]>())!);
        Assert.Equal("Rejected", entry.Status);
        Assert.Equal("bad quote", Assert.Single(entry.Errors).Quote);
        Assert.Equal("Rejected report", entry.Report!.Value.GetProperty("summary").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/missions/{Guid.NewGuid()}/reviewer-diagnostics")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.DeleteAsync($"/api/missions/{mission.Id}/reviewer-diagnostics")).StatusCode);
        missions.Update(mission.Id, item => item with { Status = MissionStatus.Failed });
        var history = JsonSerializer.Serialize(missions.Get(mission.Id));
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/missions/{mission.Id}/reviewer-diagnostics")).StatusCode);
        Assert.Empty(diagnostics.ReviewerAttempts(mission.Id));
        Assert.Single(diagnostics.PlannerAttempts(mission.Id));
        Assert.Equal(history, JsonSerializer.Serialize(missions.Get(mission.Id)));
        Assert.Null(missions.Get(mission.Id)!.Workflow!.Review);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.Sandbox.Calls);
        var openApi = await client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/missions/{id}/reviewer-diagnostics", openApi);
    }

    [Fact]
    public async Task ReviewerCorrectionResumeApiChecksEligibilityAndQueuesIdempotently()
    {
        using var fixture = new WorkflowFixture(available: false,
            responseOverride: MissionReactContractTests.Respond);
        using var factory = Factory(fixture).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>()));
        using var client = factory.CreateClient();
        const string requestText =
            "Initial value 0\nButton +1 increments\nButton -1 decrements\nReset returns to 0\nReadable interface";
        var creation = await client.PostAsJsonAsync("/api/missions",
            new CreateMissionRequest(requestText, MissionKind.Project, TemplateId: MissionReactTemplate.Id));
        creation.EnsureSuccessStatusCode();
        var created = (await creation.Content.ReadFromJsonAsync<MissionResponse>(JsonOptions))!;
        var store = factory.Services.GetRequiredService<MissionStore>();
        await factory.Services.GetRequiredService<CollaborativeProcessor>().ProcessAsync(created.Id, default);
        var generated = store.Get(created.Id)!;
        var workflow = generated.Workflow!;
        var review = workflow.Steps.Single(step => step.Kind == "Review");
        var fingerprint = WorkflowSourceFingerprint.Compute(generated);
        var diagnostics = factory.Services.GetRequiredService<OllamaDiagnosticStore>();
        var context = new LlmDiagnosticContext(created.Id, workflow.WorkflowId, review.Id, Guid.NewGuid());
        diagnostics.BeginReviewer(context, "simulated-reviewer", 2, fingerprint, null);
        using var rejected = JsonDocument.Parse("""{"summary":"Original rejected report"}""");
        diagnostics.RecordReviewerResult(context, rejected.RootElement);
        diagnostics.FinishReviewer(context, "Rejected",
        [
            new("C2", "MissingCriterion", "criterionReviews", null, null, "C2 is missing."),
            new("C3", "MissingCriterion", "criterionReviews", null, null, "C3 is missing."),
            new("C4", "MissingCriterion", "criterionReviews", null, null, "C4 is missing."),
            new("C5", "MissingCriterion", "criterionReviews", null, null, "C5 is missing.")
        ]);
        await Task.Delay(2);
        store.Update(created.Id, current => current with
        {
            Status = MissionStatus.Failed,
            Error = "Agent ReviewAgent context budget exceeded for ReadSources: 12730 characters, limit 10240",
            Workflow = current.Workflow! with
            {
                Review = null,
                Steps = current.Workflow.Steps.Select(step => step.Id == review.Id
                    ? step with
                    {
                        Status = WorkflowStatus.Failed,
                        Error = "Agent ReviewAgent context budget exceeded for ReadSources: 12730 characters, limit 10240"
                    } : step).ToArray()
            }
        });

        var endpoint = $"/api/missions/{created.Id}/reviewer-correction-resume";
        var availability = await client.GetAsync(endpoint);
        availability.EnsureSuccessStatusCode();
        Assert.True(availability.Headers.CacheControl!.NoStore);
        var eligibility = (await availability.Content.ReadFromJsonAsync<ReviewerCorrectionResumeEligibilityResponse>())!;
        Assert.True(eligibility.Available, eligibility.Reason);

        var before = store.Get(created.Id)!;
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(endpoint, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(endpoint, null)).StatusCode);
        var queued = store.Get(created.Id)!;
        Assert.Equal(MissionStatus.Queued, queued.Status);
        Assert.Equal(before.Workflow!.Version, queued.Workflow!.Version);
        Assert.Equal(review.Id, queued.Workflow.ReviewerCorrectionResume!.StepId);
        Assert.Equal(before.GenerationReservations!.Count, queued.GenerationReservations!.Count);
        Assert.Equal(5, fixture.Provider.Requests.Count);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Contains("/api/missions/{id}/reviewer-correction-resume",
            await client.GetStringAsync("/openapi/v1.json"));
    }

    [Fact]
    public async Task PrivatePlannerInspectionAndDeletionDoNotChangeHistoryOrExposeRawResponses()
    {
        using var fixture = new WorkflowFixture();
        using var factory = Factory(fixture).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>()));
        using var client = factory.CreateClient();
        var creation = await client.PostAsJsonAsync("/api/missions",
            new CreateMissionRequest("Counter initial value 0", MissionKind.Project,
                TemplateId: MissionReactTemplate.Id, RecordPlannerTrace: true));
        creation.EnsureSuccessStatusCode();
        var mission = (await creation.Content.ReadFromJsonAsync<MissionResponse>(JsonOptions))!;
        var missions = factory.Services.GetRequiredService<MissionStore>();
        Assert.True(missions.Get(mission.Id)!.Workflow!.RecordPlannerTrace);
        var diagnostics = factory.Services.GetRequiredService<OllamaDiagnosticStore>();
        var context = new LlmDiagnosticContext(mission.Id, mission.Workflow!.WorkflowId, "plan", Guid.NewGuid());
        diagnostics.BeginPlanner(context, "simulated-planner", 1,
            missions.Get(mission.Id)!.Workflow!.AcceptanceCriteria, true);
        using var plan = JsonDocument.Parse("{\"name\":\"Rejected counter\",\"files\":[]}");
        diagnostics.RecordPlannerResult(context, plan.RootElement);
        diagnostics.RecordResponse(context, 200, "{\"raw-private-body\":true}"u8.ToArray());
        diagnostics.FinishPlanner(context, "Rejected",
            [new("C1", "MissingTest", "criterionCoverage.paths", null, "A recognized business test is required.")]);
        var response = await client.GetAsync($"/api/missions/{mission.Id}/planner-diagnostics");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("raw-private-body", text);
        var entry = Assert.Single((await response.Content.ReadFromJsonAsync<PlannerAttemptDiagnostic[]>())!);
        Assert.Equal("Rejected", entry.Status);
        Assert.Equal("C1", entry.Errors.Single().CriterionId);
        Assert.True(entry.RawTraceAvailable);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/missions/{mission.Id}/planner-diagnostics/{context.AttemptId}/raw")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/missions/{Guid.NewGuid()}/planner-diagnostics")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.DeleteAsync($"/api/missions/{mission.Id}/planner-diagnostics")).StatusCode);
        missions.Update(mission.Id, item => item with { Status = MissionStatus.Failed });
        var history = JsonSerializer.Serialize(missions.Get(mission.Id));
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/missions/{mission.Id}/planner-diagnostics")).StatusCode);
        Assert.Empty(diagnostics.PlannerAttempts(mission.Id));
        Assert.Equal(history, JsonSerializer.Serialize(missions.Get(mission.Id)));
        Assert.Null(missions.Get(mission.Id)!.Workflow!.Plan);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissionRoutingOptInRoundTripsWithoutStartingGenerationOrChangingManualDefaults(bool automatic)
    {
        using var fixture = new WorkflowFixture();
        using var factory = Factory(fixture).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>()));
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/missions",
            new CreateMissionRequest("Create a .NET app", MissionKind.Project, AutomaticModelSelection: automatic));
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<MissionResponse>(JsonOptions))!;
        Assert.Equal(automatic, result.Workflow!.AutomaticModelSelection);
        Assert.False(result.SandboxApproved);
        Assert.All(result.Workflow.Steps, step => Assert.Equal(automatic ? "Automatic" : "Manual", step.SelectionMode));
        var saved = factory.Services.GetRequiredService<IMissionService>().Get(result.Id)!;
        Assert.Equal(automatic, saved.Workflow!.AutomaticModelSelection);
        Assert.Empty(saved.Workflow.ModelInvocations);
        Assert.Empty(fixture.Provider.Requests);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task ManualBackendRevisionCreatesCanonicalVersionAndPreservesHistoryWithoutExecution()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("Manual backend revision", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var before = fixture.Store.Get(mission.Id)!;
        var oldPlan = await fixture.Plans.GetAsync(mission.Id, default);
        var paths = new[] { "backend/Api/TaskService.cs", "backend/Api/SqliteTaskRepository.cs", "backend/Api/TaskEndpoints.cs" };
        var replacements = before.Workflow!.Revisions!.Last().Files.Where(file => paths.Contains(file.Path))
            .Select(file => new ManualBackendFileRequest(file.Path, file.Content + "\n// human-reviewed correction")).ToArray();
        var providerCalls = fixture.Provider.Requests.Count;
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/missions/{mission.Id}/manual-backend-revision",
            new ManualBackendRevisionRequest(oldPlan.Fingerprint, replacements));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        response.EnsureSuccessStatusCode();
        var plan = (await response.Content.ReadFromJsonAsync<ExecutionPlanResponse>())!;
        var current = factory.Services.GetRequiredService<MissionStore>().Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal(before.Workflow.Version + 1, current.Workflow!.Version);
        Assert.False(current.SandboxApproved);
        Assert.Null(current.ApprovalFingerprint);
        Assert.Equal(before.CorrectionAttempts, current.CorrectionAttempts);
        Assert.Equal(before.Events, current.Events.Take(before.Events.Count));
        Assert.Equal(before.Verification, current.Verification);
        Assert.Equal(JsonSerializer.Serialize(before.Workflow.Revisions),
            JsonSerializer.Serialize(current.Workflow.Revisions!.Take(before.Workflow.Revisions!.Count)));
        Assert.Equal(plan.Fingerprint, current.Workflow.Revisions!.Last().Fingerprint);
        Assert.Equal(plan.Fingerprint, plan.Canonical!.ComputeFingerprint());
        Assert.False(plan.Canonical.AutomaticCorrections);
        foreach (var file in current.Workflow.Revisions!.Last().Files)
        {
            var expected = replacements.SingleOrDefault(item => item.Path == file.Path)?.Content ??
                before.Workflow.Revisions!.Last().Files.Single(item => item.Path == file.Path).Content;
            Assert.Equal(expected, (await fixture.Workspaces.ReadAsync(mission.Id, file.Path, default)).Content);
        }
        Assert.Equal(providerCalls, fixture.Provider.Requests.Count);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval",
            new { approved = true, fingerprint = oldPlan.Fingerprint })).StatusCode);
        var comparison = await client.GetFromJsonAsync<RevisionComparisonResponse>(
            $"/api/missions/{mission.Id}/revisions/{current.Workflow.Version}");
        Assert.Equal(paths.Order(StringComparer.Ordinal), comparison!.Changes.Select(change => change.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ManualBackendRevisionAfterBuildFailureAndRestartPreservesEvidence()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        fixture.Sandbox.Result = stage => new(stage, "Failed", 1, "Compilation error");
        var mission = fixture.Store.Submit(new("Failed backend revision", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var before = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.BuildFailed, before.Status);
        var oldPlan = before.Workflow!.Revisions!.Last().ExecutionPlan!;
        var file = before.Workflow.Revisions!.Last().Files.Single(item => item.Path == "backend/Api/TaskService.cs");
        var providerCalls = fixture.Provider.Requests.Count;
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/missions/{mission.Id}/manual-backend-revision",
            new ManualBackendRevisionRequest(oldPlan.Fingerprint, [new(file.Path, file.Content + "\n// reviewed")]));

        response.EnsureSuccessStatusCode();
        var plan = (await response.Content.ReadFromJsonAsync<ExecutionPlanResponse>())!;
        var after = factory.Services.GetRequiredService<MissionStore>().Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, after.Status);
        Assert.Equal(before.Workflow.Version + 1, after.Workflow!.Version);
        Assert.Equal(before.Events, after.Events.Take(before.Events.Count));
        Assert.Equal(JsonSerializer.Serialize(before.Verification), JsonSerializer.Serialize(after.Verification));
        Assert.Equal(JsonSerializer.Serialize(before.Workflow.Revisions),
            JsonSerializer.Serialize(after.Workflow.Revisions!.Take(before.Workflow.Revisions!.Count)));
        Assert.Equal(before.CorrectionAttempts, after.CorrectionAttempts);
        Assert.False(after.SandboxApproved);
        Assert.Null(after.ApprovalFingerprint);
        Assert.Equal(plan.Fingerprint, after.Workflow.Revisions!.Last().Fingerprint);
        Assert.Equal(plan.Fingerprint, plan.Canonical!.ComputeFingerprint());
        Assert.Equal(providerCalls, fixture.Provider.Requests.Count);
        Assert.Equal(new[] { "BackendBuild" }, fixture.Sandbox.Calls);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval",
            new { approved = true, fingerprint = oldPlan.Fingerprint })).StatusCode);
    }

    [Fact]
    public async Task ManualFrontendRevisionAfterTypeScriptFailurePreservesEvidenceAndRequiresNewApproval()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        fixture.Sandbox.Result = stage => stage == "TypeScript"
            ? new(stage, "Failed", 2, "TypeScript error")
            : new(stage, "Succeeded", 0, "Passed", TotalTests: stage == "BackendTests" ? 2 : null,
                FailedTests: stage == "BackendTests" ? 0 : null);
        var mission = fixture.Store.Submit(new("Failed frontend revision", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var before = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.BuildFailed, before.Status);
        var oldPlan = before.Workflow!.Revisions!.Last().ExecutionPlan!;
        var paths = new[] { "frontend/src/api.ts", "frontend/src/TaskItem.tsx", "frontend/src/App.tsx" };
        var files = before.Workflow.Revisions!.Last().Files.Where(file => paths.Contains(file.Path))
            .Select(file => new ManualFrontendFileRequest(file.Path, file.Content + "\n// reviewed")).ToArray();
        var providerCalls = fixture.Provider.Requests.Count;
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/missions/{mission.Id}/manual-frontend-revision",
            new ManualFrontendRevisionRequest(oldPlan.Fingerprint, files));

        response.EnsureSuccessStatusCode();
        var plan = (await response.Content.ReadFromJsonAsync<ExecutionPlanResponse>())!;
        var after = factory.Services.GetRequiredService<MissionStore>().Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, after.Status);
        Assert.Equal(before.Workflow.Version + 1, after.Workflow!.Version);
        Assert.Equal(before.Events, after.Events.Take(before.Events.Count));
        Assert.Equal(JsonSerializer.Serialize(before.Verification), JsonSerializer.Serialize(after.Verification));
        Assert.Equal(JsonSerializer.Serialize(before.Workflow.Revisions),
            JsonSerializer.Serialize(after.Workflow.Revisions!.Take(before.Workflow.Revisions!.Count)));
        Assert.Equal(before.CorrectionAttempts, after.CorrectionAttempts);
        Assert.False(after.SandboxApproved);
        Assert.Null(after.ApprovalFingerprint);
        Assert.Equal(plan.Fingerprint, plan.Canonical!.ComputeFingerprint());
        Assert.False(plan.Canonical.AutomaticCorrections);
        Assert.Equal(plan.Fingerprint, after.Workflow.Revisions!.Last().Fingerprint);
        Assert.Contains("Frontend", after.Workflow.Revisions!.Last().Reason);
        foreach (var file in after.Workflow.Revisions!.Last().Files)
        {
            var expected = files.SingleOrDefault(item => item.Path == file.Path)?.Content ??
                before.Workflow.Revisions!.Last().Files.Single(item => item.Path == file.Path).Content;
            Assert.Equal(expected, (await fixture.Workspaces.ReadAsync(mission.Id, file.Path, default)).Content);
        }
        Assert.Equal(providerCalls, fixture.Provider.Requests.Count);
        Assert.Equal(new[] { "BackendBuild", "BackendTests", "TypeScript" }, fixture.Sandbox.Calls);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval",
            new { approved = true, fingerprint = oldPlan.Fingerprint })).StatusCode);
        var comparison = await client.GetFromJsonAsync<RevisionComparisonResponse>(
            $"/api/missions/{mission.Id}/revisions/{after.Workflow.Version}");
        Assert.Equal(paths.Order(StringComparer.Ordinal), comparison!.Changes.Select(change => change.Path).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("immutable")]
    [InlineData("stale")]
    [InlineData("changed-workspace")]
    public async Task ManualFrontendRevisionRejectsOtherRolesImmutableFilesAndStaleSources(string failure)
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("Frontend revision guard", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var before = fixture.Store.Get(mission.Id)!;
        var plan = await fixture.Plans.GetAsync(mission.Id, default);
        var path = failure switch
        {
            "backend" => "backend/Api/TaskService.cs",
            "immutable" => "frontend/package.json",
            _ => "frontend/src/App.tsx"
        };
        var file = before.Workflow!.Revisions!.Last().Files.Single(item => item.Path == path);
        if (failure == "changed-workspace")
            await File.AppendAllTextAsync(Path.Combine(fixture.Workspaces.GetDirectory(mission.Id),
                "frontend", "src", "App.tsx"), "\n// external modification");
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/missions/{mission.Id}/manual-frontend-revision",
            new ManualFrontendRevisionRequest(failure == "stale" ? "stale" : plan.Fingerprint,
                [new(file.Path, file.Content + "\n// replacement")]));

        Assert.Equal(failure is "backend" or "immutable" ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict,
            response.StatusCode);
        var after = factory.Services.GetRequiredService<MissionStore>().Get(mission.Id)!;
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Workflow.Version, after.Workflow!.Version);
        Assert.Equal(before.Events, after.Events);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("frontend")]
    [InlineData("changed-workspace")]
    public async Task ManualBackendRevisionRejectsStalePlansUnauthorizedPathsAndChangedWorkspace(string failure)
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("Manual revision guard", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var before = fixture.Store.Get(mission.Id)!;
        var plan = await fixture.Plans.GetAsync(mission.Id, default);
        var file = before.Workflow!.Revisions!.Last().Files.Single(item => item.Path ==
            (failure == "frontend" ? "frontend/src/App.tsx" : "backend/Api/TaskService.cs"));
        if (failure == "changed-workspace")
            await File.AppendAllTextAsync(Path.Combine(fixture.Workspaces.GetDirectory(mission.Id),
                "backend", "Api", "TaskService.cs"), "\n// external modification");
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        var result = await client.PostAsJsonAsync($"/api/missions/{mission.Id}/manual-backend-revision",
            new ManualBackendRevisionRequest(failure == "stale" ? "stale" : plan.Fingerprint,
                [new(file.Path, file.Content + "\n// replacement")]));
        Assert.Equal(failure == "frontend" ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, result.StatusCode);
        var after = factory.Services.GetRequiredService<MissionStore>().Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, after.Status);
        Assert.Equal(before.Workflow.Version, after.Workflow!.Version);
        Assert.Equal(before.Events, after.Events);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task InterruptedManualRevisionKeepsProposalAndDisablesExecution()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("Interrupted manual revision", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var before = fixture.Store.Get(mission.Id)!;
        var plan = await fixture.Plans.GetAsync(mission.Id, default);
        var file = before.Workflow!.Revisions!.Last().Files.Single(item => item.Path == "backend/Api/TaskService.cs");
        var service = new ManualRevisionService(fixture.Store, new InterruptedWorkspace(fixture.Workspaces),
            fixture.Plans, fixture.Sandbox);

        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(mission.Id, plan.Fingerprint, ManualRevisionRole.Backend,
            [file with { Content = file.Content + "\n// replacement" }], default));

        var after = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.Failed, after.Status);
        Assert.False(after.SandboxApproved);
        Assert.Null(after.ApprovalFingerprint);
        Assert.Equal(before.Workflow.Revisions!.Count, after.Workflow!.Revisions!.Count);
        Assert.Equal(after.Workflow.Version, after.Workflow.Proposals!.Last().Version);
        Assert.Equal(WorkflowStatus.Failed, after.Workflow.Steps.Last().Status);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.False(fixture.Store.DecideApproval(mission.Id, true, plan.Fingerprint));
    }

    private sealed class InterruptedWorkspace(IWorkspaceStore inner) : IWorkspaceStore
    {
        public void Create(Guid id) => inner.Create(id);
        public void Delete(Guid id) => inner.Delete(id);
        public void ValidateFiles(IReadOnlyList<GeneratedFile> files, string? templateId = null, bool allowPartialTemplate = false,
            ProjectSizingProfile? sizing = null) => inner.ValidateFiles(files, templateId, allowPartialTemplate, sizing);
        public Task WriteAsync(Guid id, IReadOnlyList<GeneratedFile> files, CancellationToken token, string? templateId = null,
            ProjectSizingProfile? sizing = null) => inner.WriteAsync(id, files, token, templateId, sizing);
        public IReadOnlyList<WorkspaceFile> List(Guid id, ProjectSizingProfile? sizing = null) => inner.List(id, sizing);
        public Task<WorkspaceContent> ReadAsync(Guid id, string path, CancellationToken token) => inner.ReadAsync(id, path, token);
        public Task<IReadOnlyList<GeneratedFile>> ApplyReviewedChangesAsync(
            Guid id, string? templateId, IReadOnlyList<ReviewedWorkspaceChange> changes,
            Func<IReadOnlyList<GeneratedFile>, bool> commit, CancellationToken token) =>
            inner.ApplyReviewedChangesAsync(id, templateId, changes, commit, token);
        public string GetDirectory(Guid id) => inner.GetDirectory(id);
        public async Task ReplaceFullStackFilesAsync(Guid id, ManualRevisionRole role, IReadOnlyList<GeneratedFile> files, CancellationToken token)
        {
            await inner.ReplaceFullStackFilesAsync(id, role, files.Take(1).ToArray(), token);
            throw new IOException("Simulated interruption after a file replacement.");
        }
    }

    [Fact]
    public async Task LegacyFullStackApprovalIsReplacedByNewCanonicalVersionWithoutRunning()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("Repair legacy plan", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var before = fixture.Store.Get(mission.Id)!;
        fixture.Store.Update(mission.Id, item => item with
        {
            Workflow = item.Workflow! with
            {
                Revisions = item.Workflow.Revisions!.Select(revision => revision with { ExecutionPlan = null }).ToArray()
            }
        });
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/missions/{mission.Id}/execution-plan")).StatusCode);
        var response = await client.PostAsync($"/api/missions/{mission.Id}/revalidate", null);
        response.EnsureSuccessStatusCode();
        var plan = (await response.Content.ReadFromJsonAsync<ExecutionPlanResponse>())!;
        var current = factory.Services.GetRequiredService<MissionStore>().Get(mission.Id)!;
        Assert.Equal(before.Workflow!.Version + 1, current.Workflow!.Version);
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal(plan.Fingerprint, current.Workflow.Revisions!.Last().Fingerprint);
        Assert.Equal(plan.Fingerprint, plan.Canonical!.ComputeFingerprint());
        Assert.False(plan.Canonical.AutomaticCorrections);
        Assert.Equal(6, plan.Canonical.Stages.Count);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval",
            new { approved = true, fingerprint = before.Workflow.Revisions!.Last().Fingerprint })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval",
            new { approved = true, plan.Fingerprint })).StatusCode);
    }

    [Fact]
    public async Task FullStackPolicyChangeInvalidatesCanonicalApproval()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("Reject changed policy", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var plan = await fixture.Plans.GetAsync(mission.Id, default);
        var changed = plan.Canonical! with { AutomaticCorrections = true };
        Assert.NotEqual(plan.Fingerprint, changed.ComputeFingerprint());
        fixture.Store.Update(mission.Id, item => item with
        {
            Workflow = item.Workflow! with
            {
                Revisions = item.Workflow.Revisions!.Select(revision => revision with
                { ExecutionPlan = revision.ExecutionPlan! with { Canonical = changed } }).ToArray()
            }
        });
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval",
            new { approved = true, plan.Fingerprint })).StatusCode);
        Assert.False(factory.Services.GetRequiredService<MissionStore>().Get(mission.Id)!.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task ConfiguresAgentsValidatesPermissionsAndLocksEditingWhileWorkflowActive()
    {
        using var fixture = new WorkflowFixture();
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        var config = await client.GetFromJsonAsync<AgentConfigurationResponse>("/api/agents/configuration", JsonOptions);
        var changed = new UpdateAgentConfigurationRequest(config!.Agents.Select(agent =>
            agent.Role == "Review" ? agent with { Name = "Read-only reviewer" } : agent).ToArray(), config.Models);
        (await client.PutAsJsonAsync("/api/agents/configuration", changed)).EnsureSuccessStatusCode();
        var invalid = changed with
        {
            Agents = changed.Agents.Select(agent =>
            agent.Role == "Review" ? agent with { AllowedTools = ["DockerTests"] } : agent).ToArray()
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/agents/configuration", invalid)).StatusCode);
        var mission = await Create(client);
        await Wait(client, mission.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/agents/configuration", changed)).StatusCode);
        var workflow = await client.GetFromJsonAsync<WorkflowResponse>($"/api/missions/{mission.Id}/workflow", JsonOptions);
        Assert.NotNull(workflow!.Plan);
        Assert.Equal("Calculator", workflow.Plan.Name);
        var json = await client.GetStringAsync($"/api/missions/{mission.Id}/workflow");
        Assert.DoesNotContain("systemPrompt", json);
        Assert.DoesNotContain("\"draft\"", json);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/workspaces/{mission.Id}")).StatusCode);
    }

    [Fact]
    public async Task RefusalRenameResumeAndDeletePreserveMissionAudit()
    {
        using var fixture = new WorkflowFixture();
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        var mission = await Create(client);
        await Wait(client, mission.Id);
        var plan = await client.GetFromJsonAsync<ExecutionPlanResponse>($"/api/missions/{mission.Id}/execution-plan");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval",
            new { approved = true, fingerprint = "obsolete" })).StatusCode);
        (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval", new { approved = false, plan!.Fingerprint })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/workspaces/{mission.Id}", new { name = "Arithmetic", description = "Small library" })).EnsureSuccessStatusCode();
        Assert.Equal("Arithmetic", (await client.GetFromJsonAsync<MissionResponse>($"/api/missions/{mission.Id}", JsonOptions))!.ProjectName);
        (await client.PostAsync($"/api/missions/{mission.Id}/resume", null)).EnsureSuccessStatusCode();
        var resumed = await Wait(client, mission.Id);
        Assert.Equal(MissionStatus.AwaitingApproval, resumed.Status);
        Assert.False(resumed.SandboxApproved);
        Assert.Equal(resumed.Workflow!.Version, resumed.Workflow.Revisions.Last().Version);
        Assert.Empty(fixture.Sandbox.Calls);
        (await client.PostAsync($"/api/missions/{mission.Id}/cancel", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/workspaces/{mission.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/workspaces/{mission.Id}/files")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/missions/{mission.Id}/resume", null)).StatusCode);
        Assert.True((await client.GetFromJsonAsync<MissionResponse>($"/api/missions/{mission.Id}", JsonOptions))!.Deleted);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/missions/{Guid.NewGuid()}/workflow")).StatusCode);
    }

    [Fact]
    public async Task ComparisonIsAvailableAndChangedUnreviewedWorkspaceCannotBeApproved()
    {
        using var fixture = new WorkflowFixture();
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        var mission = await Create(client);
        await Wait(client, mission.Id);
        var comparison = await client.GetFromJsonAsync<RevisionComparisonResponse>($"/api/missions/{mission.Id}/revisions/1");
        Assert.NotNull(comparison);
        Assert.Null(comparison.PreviousVersion);
        Assert.Contains(comparison.Changes, change => change.Path == "src/App/Program.cs" && change.Before is null);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/missions/{mission.Id}/revisions/99")).StatusCode);
        await File.AppendAllTextAsync(Path.Combine(fixture.Workspaces.GetDirectory(mission.Id), "src", "App", "Program.cs"), "\n// not reviewed");
        var plan = await client.GetFromJsonAsync<ExecutionPlanResponse>($"/api/missions/{mission.Id}/execution-plan");
        var response = await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval", new { approved = true, plan!.Fingerprint });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task InfrastructureRevalidationPreservesSourcesHistoryAndCorrectionsAndWaitsForConsent()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var submitted = fixture.Store.Submit(new("Full-stack infrastructure recovery", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(submitted.Id, default);
        Assert.True(fixture.Store.DecideApproval(submitted.Id, false,
            (await fixture.Plans.GetAsync(submitted.Id, default)).Fingerprint));
        fixture.Store.Update(submitted.Id, item => item with
        {
            Workflow = item.Workflow! with
            {
                Revisions = item.Workflow.Revisions!.Select(revision =>
                    revision with { Fingerprint = new string('b', 64) }).ToArray()
            }
        });
        var before = fixture.Store.Get(submitted.Id)!;
        var requests = fixture.Provider.Requests.Count;
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/missions/{submitted.Id}/revalidate", null);

        response.EnsureSuccessStatusCode();
        var plan = (await response.Content.ReadFromJsonAsync<ExecutionPlanResponse>())!;
        var current = factory.Services.GetRequiredService<MissionStore>().Get(submitted.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.False(current.SandboxApproved);
        Assert.Null(current.ApprovalFingerprint);
        Assert.Equal(before.CorrectionAttempts, current.CorrectionAttempts);
        Assert.Equal(before.Workflow!.Version + 1, current.Workflow!.Version);
        Assert.Equal(JsonSerializer.Serialize(before.Workflow.Revisions),
            JsonSerializer.Serialize(current.Workflow.Revisions!.Take(before.Workflow.Revisions!.Count)));
        Assert.Equal(before.Events, current.Events.Take(before.Events.Count));
        Assert.Equal(before.Verification, current.Verification);
        Assert.Equal(JsonSerializer.Serialize(before.Workflow.Steps),
            JsonSerializer.Serialize(current.Workflow.Steps.Take(before.Workflow.Steps.Count)));
        Assert.Equal(JsonSerializer.Serialize(before.Workflow.Review), JsonSerializer.Serialize(current.Workflow.Review));
        Assert.Equal(before.Workflow.Revisions!.Last().Files, current.Workflow.Revisions!.Last().Files);
        Assert.Equal(plan.Fingerprint, current.Workflow.Revisions!.Last().Fingerprint);
        var steps = WorkflowResponse.From(current.Workflow).Steps
            .Where(step => step.Version == current.Workflow.Version).ToArray();
        Assert.Equal(new[] { "Approval", "BackendBuild", "BackendTests", "TypeScript", "FrontendBuild", "FrontendTests", "Integration" },
            steps.Select(step => step.Kind));
        Assert.Equal(WorkflowStatus.WaitingForApproval, steps[0].Status);
        Assert.All(steps.Skip(1), step => Assert.Equal(WorkflowStatus.Pending, step.Status));
        Assert.Equal(requests, fixture.Provider.Requests.Count);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/missions/{submitted.Id}/approval",
            new { approved = true, fingerprint = before.Workflow.Revisions.Last().Fingerprint })).StatusCode);
        foreach (var file in before.Workflow.Revisions.Last().Files)
            Assert.Equal(file.Content, (await fixture.Workspaces.ReadAsync(submitted.Id, file.Path, default)).Content);
        using var restarted = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(MissionStatus.AwaitingApproval, restarted.Get(submitted.Id)!.Status);
        Assert.Equal(plan.Fingerprint, restarted.Get(submitted.Id)!.Workflow!.Revisions!.Last().Fingerprint);
        Assert.False(restarted.Get(submitted.Id)!.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("unreviewed")]
    [InlineData("unavailable")]
    public async Task InfrastructureRevalidationRejectsChangedSourcesFailedReviewOrMissingImage(string failure)
    {
        using var fixture = new WorkflowFixture(fullStack: true, available: failure != "unavailable");
        var submitted = fixture.Store.Submit(new("Full-stack recovery guard", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(submitted.Id, default);
        if (fixture.Store.Get(submitted.Id)!.Status == MissionStatus.AwaitingApproval)
            Assert.True(fixture.Store.DecideApproval(submitted.Id, false,
                (await fixture.Plans.GetAsync(submitted.Id, default)).Fingerprint));
        if (failure == "changed")
            await File.AppendAllTextAsync(Path.Combine(fixture.Workspaces.GetDirectory(submitted.Id),
                "backend", "Api", "TaskService.cs"), "\n// unreviewed change");
        if (failure == "unreviewed")
            fixture.Store.Update(submitted.Id, item => item with
            {
                Workflow = item.Workflow! with
                {
                    Review = new("Review failed.", [new("backend/Api/TaskService.cs", "Error", "Blocking defect.")])
                }
            });
        var snapshotPath = Path.Combine(fixture.Options.DataDirectory, "missions", submitted.Id.ToString("N") + ".json");
        var before = await File.ReadAllTextAsync(snapshotPath);
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/missions/{submitted.Id}/revalidate", null)).StatusCode);

        Assert.Equal(before, await File.ReadAllTextAsync(snapshotPath));
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task CommittedVersionComparesToPreviousCommitNotIdenticalRejectedProposal()
    {
        using var fixture = new WorkflowFixture();
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        var mission = await Create(client);
        await Wait(client, mission.Id);
        var store = factory.Services.GetRequiredService<MissionStore>();
        store.Update(mission.Id, item =>
        {
            var flow = item.Workflow!;
            var initial = flow.Revisions!.Single();
            var corrected = initial.Files.Select(file => file.Path == "src/App/Program.cs"
                ? file with { Content = file.Content + "\n// fix" } : file).ToArray();
            return item with
            {
                Workflow = flow with
                {
                    Proposals = [new(2, DateTimeOffset.UtcNow, corrected)],
                    Revisions = [initial, new(3, DateTimeOffset.UtcNow, "Reviewed", "fingerprint", corrected)]
                }
            };
        });
        var proposed = await client.GetFromJsonAsync<RevisionComparisonResponse>($"/api/missions/{mission.Id}/revisions/2");
        var committed = await client.GetFromJsonAsync<RevisionComparisonResponse>($"/api/missions/{mission.Id}/revisions/3");
        Assert.True(proposed!.Proposed);
        Assert.False(committed!.Proposed);
        Assert.Equal(1, committed.PreviousVersion);
        Assert.Contains(committed.Changes, change => change.Path == "src/App/Program.cs" &&
            change.After!.EndsWith("// fix", StringComparison.Ordinal));
    }

    private static async Task<MissionResponse> Create(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/missions", new { task = "Create Calculator", kind = "Project" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MissionResponse>(JsonOptions))!;
    }
    private static async Task<MissionResponse> Wait(HttpClient client, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var mission = (await client.GetFromJsonAsync<MissionResponse>($"/api/missions/{id}", JsonOptions, timeout.Token))!;
            if (mission.Status is not (MissionStatus.Queued or MissionStatus.Running)) return mission;
            await Task.Delay(20, timeout.Token);
        }
    }
    private static WebApplicationFactory<Program> Factory(WorkflowFixture fixture) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                var registry = new ConfigurableRegistry(fixture.Platform, fixture.Options);
                services.RemoveAll<StudioOptions>();
                services.AddSingleton(fixture.Options);
                services.RemoveAll<SandboxOptions>();
                services.AddSingleton(fixture.SandboxOptions);
                services.RemoveAll<IAgentRegistry>();
                services.AddSingleton<IAgentRegistry>(registry);
                services.RemoveAll<IAgentConfigurationService>();
                services.AddSingleton<IAgentConfigurationService>(registry);
                services.RemoveAll<ILLMProvider>();
                services.AddSingleton<ILLMProvider>(fixture.Provider);
                services.RemoveAll<ISandboxRunner>();
                services.AddSingleton<ISandboxRunner>(fixture.Sandbox);
                services.RemoveAll<IContainerProcess>();
                services.AddSingleton<IContainerProcess>(new WorkflowImageProcess());
            });
        });
}
