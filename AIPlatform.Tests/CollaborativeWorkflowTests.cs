using System.Text.Json;
using AIPlatform.API;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class CollaborativeWorkflowTests
{
    [Fact]
    public async Task FullStackFailedBuildAndResumeCreateAConsistentNewPlanWithoutCorrections()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        fixture.Sandbox.Result = stage => new(stage, "Failed", 1, "Compilation error");
        var mission = fixture.Store.Submit(new("Full-stack failed build", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var first = await fixture.Plans.GetAsync(mission.Id, default);
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(MissionStatus.BuildFailed, fixture.Store.Get(mission.Id)!.Status);
        Assert.True(fixture.Store.Resume(mission.Id));

        await fixture.Processor.ProcessAsync(mission.Id, default);

        var resumed = fixture.Store.Get(mission.Id)!;
        var second = await fixture.Plans.GetAsync(mission.Id, default);
        Assert.Equal(MissionStatus.AwaitingApproval, resumed.Status);
        Assert.Equal(2, resumed.Workflow!.Version);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal(second.Fingerprint, resumed.Workflow.Revisions!.Last().Fingerprint);
        Assert.Equal(second.Fingerprint, resumed.Workflow.Revisions!.Last().ExecutionPlan!.Canonical!.ComputeFingerprint());
        Assert.False(second.Canonical!.AutomaticCorrections);
        Assert.Equal(0, resumed.CorrectionAttempts);
        Assert.False(resumed.SandboxApproved);
        Assert.Equal(new[] { "BackendBuild" }, fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task FullStackFileChangedAfterApprovalInvalidatesApprovalBeforeSandbox()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("Full-stack approval", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        await fixture.Approve(mission.Id);
        await File.AppendAllTextAsync(Path.Combine(fixture.Workspaces.GetDirectory(mission.Id),
            "backend", "Api", "TaskService.cs"), "\n// changed after approval");

        await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));

        var current = fixture.Store.Get(mission.Id)!;
        Assert.False(current.SandboxApproved);
        Assert.Null(current.ApprovalFingerprint);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task RevalidationFailureStopsForHumanDiagnosisWithoutCorrections()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("Infrastructure revalidation failure", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var initialPlan = await fixture.Plans.GetAsync(mission.Id, default);
        Assert.True(fixture.Store.DecideApproval(mission.Id, false, initialPlan.Fingerprint));
        var before = fixture.Store.Get(mission.Id)!;
        var newPlan = await fixture.Plans.GetAsync(mission.Id, default, before.Workflow!.Version + 1);
        Assert.True(fixture.Store.PrepareRevalidation(mission.Id, before.UpdatedAt, newPlan));
        Assert.True(fixture.Store.DecideApproval(mission.Id, true, newPlan.Fingerprint));
        var requests = fixture.Provider.Requests.Count;
        fixture.Sandbox.Result = stage => new(stage, "Failed", 1, "Infrastructure failure");

        await fixture.Processor.ProcessAsync(mission.Id, default);

        var result = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.BuildFailed, result.Status);
        Assert.Equal(before.CorrectionAttempts, result.CorrectionAttempts);
        Assert.Equal(requests, fixture.Provider.Requests.Count);
        Assert.Equal(new[] { "BackendBuild" }, fixture.Sandbox.Calls);
        Assert.False(result.SandboxApproved);
        Assert.Contains("no automatic correction", result.Workflow!.FinalReport);
        Assert.Equal("Failed", result.Verification.Last().Status);
    }

    [Fact]
    public async Task FullStackGenerationRunsSpecialistsSequentiallyAndStopsForApproval()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new(
            "Create a full-stack task manager", MissionKind.Project, TemplateId: FullStackTemplate.Id));

        await fixture.Processor.ProcessAsync(mission.Id, default);

        var result = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, result.Status);
        Assert.Equal(new[] { "Plan", "Contract", "BackendGenerate", "FrontendGenerate", "Review" },
            result.Workflow!.Steps.Where(step => step.Kind is
                "Plan" or "Contract" or "BackendGenerate" or "FrontendGenerate" or "Review")
                .Select(step => step.Kind));
        Assert.Contains("3 backend files", result.Workflow.Steps.Single(step =>
            step.Kind == "BackendGenerate").Output);
        Assert.Equal(FullStackTemplate.Files.Count, result.Workflow.Draft!.Count);
        Assert.Equal(FullStackTemplate.Files.Count + 1, fixture.Workspaces.List(mission.Id).Count);
        Assert.False(result.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task FullStackGenerationKeepsRealisticAgentPromptsWithinContextBudget()
    {
        var plan = new ProjectPlan("TaskManager",
            "Full-stack task manager with ASP.NET Core .NET 10 API and React TypeScript frontend",
            [
                "Use SQLite for persistence as it is lightweight and suitable for local development",
                "Implement CRUD operations for tasks with status filtering",
                "Ensure responsive UI using React with Tailwind CSS for styling",
                "Create automated tests using Jest for frontend and xUnit for backend",
                "Share a single OpenAPI contract (Swagger) for both frontend and backend",
                "Include offline testing by simulating network requests with axios in frontend",
                "Use immutable OpenAPI contract to ensure consistent interface between frontend and backend"
            ],
            [
                new("backend/Api/TaskService.cs", "BackendAgent: validate task operations and map repository results.", []),
                new("backend/Api/SqliteTaskRepository.cs", "BackendAgent: implement parameterized SQLite CRUD and status filtering.", []),
                new("backend/Api/TaskEndpoints.cs", "BackendAgent: map the exact HTTP contract with minimal APIs.", ["backend/Api/TaskService.cs"]),
                new("frontend/src/api.ts", "FrontendAgent: implement the typed HTTP client from the OpenAPI contract.", []),
                new("frontend/src/TaskItem.tsx", "FrontendAgent: implement accessible task item actions.", []),
                new("frontend/src/App.tsx", "FrontendAgent: implement responsive CRUD and server-backed status filtering.",
                    ["frontend/src/api.ts", "frontend/src/TaskItem.tsx"])
            ]);
        using var fixture = new WorkflowFixture(fullStack: true, maxOutputTokens: 3072,
            responseOverride: request =>
            {
                var properties = request.ResponseSchema!.Value.GetProperty("properties");
                object response;
                if (properties.TryGetProperty("decisions", out _))
                    response = plan;
                else if (properties.TryGetProperty("content", out _))
                {
                    using var input = JsonDocument.Parse(request.Messages.Last().Content);
                    var path = input.RootElement.GetProperty("file").GetProperty("path").GetString()!;
                    response = FullStackTemplate.Files.Single(file => file.Path == path);
                }
                else
                    response = new ReviewReport("Reviewed current file.", [], "No blocking defect.",
                        [], "Matches contract.", [], "Compliant.", "Continue after validation.");
                return JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            });
        var task = "Create a full-stack task manager with an ASP.NET Core .NET 10 API, React TypeScript frontend, SQLite persistence, CRUD operations, status filtering, responsive UI, and automated tests. The frontend must call the actual API; follow the shared OpenAPI contract and include offline backend and frontend tests.";
        var mission = fixture.Store.Submit(new(task, MissionKind.Project, TemplateId: FullStackTemplate.Id));

        await fixture.Processor.ProcessAsync(mission.Id, default);

        var result = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, result.Status);
        var generationRequests = fixture.Provider.Requests.Where(request =>
            request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("content", out _)).ToArray();
        Assert.Equal(6, generationRequests.Length);
        var contexts = generationRequests.Select(request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            var filePath = input.RootElement.GetProperty("file").GetProperty("path").GetString()!;
            var sources = input.RootElement.GetProperty("sources").EnumerateArray().ToArray();
            var sourcePaths = sources.Select(item => item.GetProperty("path").GetString()!).ToArray();
            var contract = sources.SingleOrDefault(item =>
                item.GetProperty("path").GetString() == "contracts/openapi.json");
            return (filePath, sourcePaths, contract: contract.ValueKind == JsonValueKind.Undefined
                ? null : contract.GetProperty("content").GetString());
        }).ToArray();
        Assert.All(contexts.Where(item => item.filePath is "backend/Api/TaskService.cs" or "backend/Api/SqliteTaskRepository.cs"),
            item => Assert.Contains("backend/Api/TaskContracts.cs", item.sourcePaths));
        var contracts = contexts.Where(item => item.contract is not null).Select(item => item.contract).ToArray();
        Assert.Equal(4, contracts.Length);
        Assert.All(contracts, contract => Assert.Contains("/api/tasks", contract));
        Assert.All(contracts.Skip(1), contract => Assert.Equal(contracts[0], contract));
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task FullStackReviewBoundsVerbosePerFileSummariesExplicitly()
    {
        using var fixture = new WorkflowFixture(fullStack: true, responseOverride: request =>
        {
            var properties = request.ResponseSchema!.Value.GetProperty("properties");
            object response;
            if (properties.TryGetProperty("decisions", out _))
                response = WorkflowFixture.Plan;
            else if (properties.TryGetProperty("content", out _))
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                var path = input.RootElement.GetProperty("file").GetProperty("path").GetString()!;
                response = FullStackTemplate.Files.Single(file => file.Path == path);
            }
            else
                response = new ReviewReport(new string('S', 400), [], new string('P', 400), [],
                    new string('J', 400), [], new string('C', 400), new string('R', 400));
            return JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        var mission = fixture.Store.Submit(new(
            "Create a full-stack task manager", MissionKind.Project, TemplateId: FullStackTemplate.Id));

        await fixture.Processor.ProcessAsync(mission.Id, default);

        var result = fixture.Store.Get(mission.Id)!;
        var review = result.Workflow!.Review!;
        var summary = Assert.IsType<string>(review.Summary);
        var justification = Assert.IsType<string>(review.Justification);
        Assert.Equal(MissionStatus.AwaitingApproval, result.Status);
        Assert.Contains("[truncated]", summary);
        Assert.Contains("[truncated]", justification);
        Assert.InRange(summary.Length, 1, 2000);
        Assert.InRange(justification.Length, 1, 1000);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task FullStackFailedValidationStopsWithoutAutomaticCorrection()
    {
        using var fixture = new WorkflowFixture(fullStack: true, responseOverride: request =>
        {
            var properties = request.ResponseSchema!.Value.GetProperty("properties");
            object answer;
            if (properties.TryGetProperty("decisions", out _))
                answer = WorkflowFixture.Plan;
            else if (properties.TryGetProperty("content", out _))
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                var path = input.RootElement.GetProperty("file").GetProperty("path").GetString()!;
                var baseline = FullStackTemplate.Files.Single(file => file.Path == path);
                answer = path == "backend/Api/TaskService.cs" &&
                    input.RootElement.GetProperty("diagnostics").ValueKind != JsonValueKind.Null
                        ? baseline with { Content = baseline.Content + "\n// targeted correction" }
                        : baseline;
            }
            else
                answer = new ReviewReport("Reviewed current file.", [], "No blocking defect.",
                    [], "Matches contract.", [], "Compliant.", "Continue after validation.");
            return JsonSerializer.Serialize(answer, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        var integrationRuns = 0;
        fixture.Sandbox.Result = stage => stage == "Integration" && ++integrationRuns == 1
            ? new(stage, "Failed", 1,
                "/work/backend/Api/TaskService.cs(1,1): error CS1002: Missing semicolon")
            : new(stage, "Succeeded", 0, "Offline stage passed.",
                stage is "BackendTests" or "FrontendTests" or "Integration" ? 1 : null, FailedTests: 0);

        var mission = fixture.Store.Submit(new(
            "Create a full-stack task manager", MissionKind.Project, TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var initial = fixture.Store.Get(mission.Id)!;
        var unchanged = initial.Workflow!.Draft!.Single(file => file.Path == "frontend/src/App.tsx").Content;
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);

        var corrected = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.TestsFailed, corrected.Status);
        Assert.Equal(1, corrected.Workflow!.Version);
        Assert.Equal(0, corrected.CorrectionAttempts);
        Assert.False(corrected.SandboxApproved);
        Assert.Equal(WorkflowStatus.Completed, corrected.Workflow.Steps.Single(step => step.Id == "approval-1").Status);
        Assert.Equal(unchanged, corrected.Workflow.Draft!.Single(file => file.Path == "frontend/src/App.tsx").Content);
        Assert.DoesNotContain("targeted correction",
            corrected.Workflow.Revisions!.Last().Files.Single(file => file.Path == "backend/Api/TaskService.cs").Content);
        Assert.Equal(new[] { "BackendBuild", "BackendTests", "TypeScript", "FrontendBuild", "FrontendTests", "Integration" },
            fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task RealAgentContractsExchangeBoundedContextAndRequirePostGenerationConsent()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator DATA_MARKER", MissionKind.Project, ApproveSandboxExecution: true));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var pending = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, pending.Status);
        Assert.False(pending.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(6, fixture.Provider.Requests.Count);
        Assert.Equal("Calculator", pending.ProjectName);
        Assert.NotNull(pending.Workflow!.Plan);
        Assert.NotNull(pending.Workflow.Review);
        Assert.Single(pending.Workflow.Revisions!);
        var developerCalls = fixture.Provider.Requests.Where(request => request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("content", out _)).ToArray();
        Assert.Equal(4, developerCalls.Length);
        using var testInput = JsonDocument.Parse(developerCalls.Last().Messages.Last().Content);
        Assert.Contains(testInput.RootElement.GetProperty("sources").EnumerateArray(),
            item => item.GetProperty("path").GetString() == "src/App/Program.cs");
        Assert.All(fixture.Provider.Requests, request => Assert.Contains("untrusted data", request.Messages[0].Content));
        Assert.All(fixture.Provider.Requests, request => Assert.DoesNotContain("DATA_MARKER", request.Messages[0].Content));
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var verified = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.Verified, verified.Status);
        Assert.Equal(new[] { "Build", "Tests" }, fixture.Sandbox.Calls);
        Assert.All(verified.Verification, result => Assert.True(result.DurationMilliseconds >= 0));
        Assert.All(verified.Workflow!.Steps, step => Assert.Equal(WorkflowStatus.Completed, step.Status));
        Assert.Equal(6, fixture.Provider.Requests.Count); // Deterministic Test/Orchestrator do not call a model.
    }

    [Fact]
    public async Task CompilerFailurePersistsDiagnosticsAndRequiresFreshApprovalForEveryCorrection()
    {
        using var fixture = new WorkflowFixture();
        fixture.Sandbox.Result = stage => stage == "Build"
            ? new(stage, "Failed", 1, "CS1002: semicolon expected.")
            : throw new InvalidOperationException("Tests must not run after failed build.");
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        for (var version = 1; version <= 4; version++)
        {
            var before = fixture.Store.Get(mission.Id)!;
            Assert.Equal(MissionStatus.AwaitingApproval, before.Status);
            Assert.False(before.SandboxApproved);
            var testSource = before.Workflow!.Draft!.Single(file => file.Path.EndsWith("UnitTests.cs")).Content;
            await fixture.Approve(mission.Id);
            await fixture.Processor.ProcessAsync(mission.Id, default);
            var after = fixture.Store.Get(mission.Id)!;
            Assert.Equal(version, after.Verification.Count);
            Assert.Equal(testSource, after.Workflow!.Draft!.Single(file => file.Path.EndsWith("UnitTests.cs")).Content);
            Assert.Equal(version == 4 ? MissionStatus.BuildFailed : MissionStatus.AwaitingApproval, after.Status);
        }
        var final = fixture.Store.Get(mission.Id)!;
        Assert.Equal(3, final.CorrectionAttempts);
        Assert.Equal(4, final.Workflow!.Revisions!.Count);
        Assert.Equal(4, fixture.Sandbox.Calls.Count);
        Assert.All(final.Verification, result => Assert.Contains("CS1002", result.Log));
        Assert.Contains(fixture.Provider.Requests, request => request.Messages.Last().Content.Contains("CS1002"));
    }

    [Fact]
    public async Task InterruptedCorrectionRetainsProposalWithoutApplyingItAndRequiresNewApproval()
    {
        using var fixture = new WorkflowFixture();
        fixture.Sandbox.Result = stage => new(stage, "Failed", 1,
            "/work/src/App/Program.cs(3,5): error CS0103: Missing name [/work/src/App/App.csproj]");
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var original = (await fixture.Workspaces.ReadAsync(mission.Id, "src/App/Program.cs", default)).Content;
        var tests = (await fixture.Workspaces.ReadAsync(mission.Id, "tests/App.Tests/UnitTests.cs", default)).Content;
        await fixture.Approve(mission.Id);
        var reviewing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async (request, token) =>
        {
            var properties = request.ResponseSchema!.Value.GetProperty("properties");
            if (properties.TryGetProperty("content", out _))
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                Assert.Equal("src/App/Program.cs", input.RootElement.GetProperty("file").GetProperty("path").GetString());
                return JsonSerializer.Serialize(new GeneratedFile("src/App/Program.cs", original + "\n// proposed fix"),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            reviewing.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "";
        });
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([provider]), fixture.Options),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options);
        using var cancellation = new CancellationTokenSource();
        var running = processor.ProcessAsync(mission.Id, cancellation.Token);
        await reviewing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(original, (await fixture.Workspaces.ReadAsync(mission.Id, "src/App/Program.cs", default)).Content);
        Assert.Contains("proposed fix", fixture.Store.Get(mission.Id)!.Workflow!.Proposal!.Single(file => file.Path == "src/App/Program.cs").Content);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Cancelled });
        // Simulate interruption after source writes but before the revision snapshot is committed.
        await fixture.Workspaces.WriteAsync(mission.Id, fixture.Store.Get(mission.Id)!.Workflow!.Proposal!, default);
        using var restarted = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.True(restarted.Resume(mission.Id));
        var resumed = new CollaborativeProcessor(restarted,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options);
        await resumed.ProcessAsync(mission.Id, default);
        var result = restarted.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, result.Status);
        Assert.False(result.SandboxApproved);
        Assert.Equal(1, result.CorrectionAttempts);
        Assert.Contains("proposed fix", (await fixture.Workspaces.ReadAsync(mission.Id, "src/App/Program.cs", default)).Content);
        Assert.Equal(tests, (await fixture.Workspaces.ReadAsync(mission.Id, "tests/App.Tests/UnitTests.cs", default)).Content);
        Assert.Equal(2, result.Workflow!.Revisions!.Count);
        Assert.Equal(new[] { "src/App/Program.cs" }, result.Workflow.Review!.ModifiedFiles);
        Assert.Single(fixture.Sandbox.Calls);
        Assert.Single(result.Verification[0].Diagnostics!.Items);
    }

    [Fact]
    public async Task FailedTestsRequireFreshApprovalAndPreserveAssertionsBeforeVerification()
    {
        using var fixture = new WorkflowFixture();
        var testRuns = 0;
        fixture.Sandbox.Result = stage => stage == "Tests" && ++testRuns == 1
            ? new(stage, "Failed", 1, "Failed: Subtract expected 2, actual 4", 2, FailedTests: 1)
            : new(stage, "Succeeded", 0, "Simulated success", stage == "Tests" ? 2 : null, FailedTests: 0);
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var source = (await fixture.Workspaces.ReadAsync(mission.Id, "tests/App.Tests/UnitTests.cs", default)).Content;
        var project = (await fixture.Workspaces.ReadAsync(mission.Id, "tests/App.Tests/App.Tests.csproj", default)).Content;
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var pending = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, pending.Status);
        Assert.False(pending.SandboxApproved);
        Assert.Equal("Tests", pending.Verification.Last().Diagnostics!.Stage);
        Assert.Equal(source, (await fixture.Workspaces.ReadAsync(mission.Id, "tests/App.Tests/UnitTests.cs", default)).Content);
        Assert.Equal(project, (await fixture.Workspaces.ReadAsync(mission.Id, "tests/App.Tests/App.Tests.csproj", default)).Content);
        Assert.Equal(new[] { "Build", "Tests" }, fixture.Sandbox.Calls);
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var verified = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.Verified, verified.Status);
        Assert.Equal(new[] { "Build", "Tests", "Build", "Tests" }, fixture.Sandbox.Calls);
        var path = Path.Combine(fixture.Options.DataDirectory, "missions", mission.Id.ToString("N") + ".json");
        var saved = await File.ReadAllBytesAsync(path);
        var response = MissionResponse.From(verified);
        Assert.Equal(new int?[] { 1, 2 }, response.Workflow!.Steps
            .Where(step => step.Kind == "Approval").Select(step => step.Version));
        Assert.All(response.Workflow.Steps.Where(step => step.Kind == "Approval"),
            step => Assert.Equal(WorkflowStatus.Completed, step.Status));
        Assert.Contains(response.Events, item => item.Step == "Approval" && item.Status == StepStatus.Running);
        Assert.Equal(verified.Events.Select(item => (item.Sequence, item.Step, item.Status, item.Message)),
            response.Events.Select(item => (item.Sequence, item.Step, item.Status, item.Message)));
        Assert.Equal(saved, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(3, 6)]
    [InlineData(2, 5)]
    public void TimelineVersionsSeparateOrdinalStepIdsAndPreserveHistoricalOutcomes(int earlierVersion, int currentVersion)
    {
        using var fixture = new WorkflowFixture();
        var submitted = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        var original = submitted.Workflow!;
        WorkflowStep Step(string id, string kind, WorkflowStatus status) =>
            new(id, kind, original.Steps[0].AgentId, [], status, 1, DateTimeOffset.UtcNow, 1000);
        var snapshot = submitted with
        {
            Status = MissionStatus.Verified,
            SandboxApproved = true, ApprovalFingerprint = "unchanged-current",
            Workflow = original with
            {
                Version = currentVersion,
                Steps = [
                    Step("plan", "Plan", WorkflowStatus.Completed),
                    Step("approval-1", "Approval", WorkflowStatus.Completed),
                    Step("tests-1", "Tests", WorkflowStatus.Failed),
                    Step($"correct-{earlierVersion}", "Correct", WorkflowStatus.Completed),
                    Step($"approval-{earlierVersion}", "Approval", WorkflowStatus.Cancelled),
                    Step("resume-review-15", "Review", WorkflowStatus.Completed),
                    Step($"correct-{currentVersion - 1}", "Correct", WorkflowStatus.Completed),
                    Step("report-19", "Report", WorkflowStatus.Completed),
                    Step("resume-review-20", "Review", WorkflowStatus.Completed),
                    Step($"approval-{currentVersion}", "Approval", WorkflowStatus.Completed),
                    Step($"tests-{currentVersion}", "Tests", WorkflowStatus.Completed),
                    Step("report-24", "Report", WorkflowStatus.Completed)
                ],
                Revisions = [new(1, submitted.CreatedAt, "Initial", "unchanged-1", []),
                    new(earlierVersion, submitted.CreatedAt, "Correction", "unchanged-earlier", []),
                    new(currentVersion, submitted.CreatedAt, "Resumed", "unchanged-current", [])],
                Proposals = [new(2, submitted.CreatedAt, []), new(earlierVersion, submitted.CreatedAt, []),
                    new(currentVersion - 1, submitted.CreatedAt, [])]
            },
            Events = [.. submitted.Events,
                new(2, submitted.CreatedAt, "Approval", StepStatus.Running, "Historical start"),
                new(3, submitted.CreatedAt, "Authorization", StepStatus.Succeeded, "Human decision")],
            Verification = [new("Tests", "Failed", 1, "Original failing log", 31, FailedTests: 1),
                new("Tests", "Succeeded", 0, "Original passing TRX", 31, FailedTests: 0)]
        };
        var before = JsonSerializer.Serialize(snapshot);
        var response = MissionResponse.From(snapshot);
        Assert.Equal(earlierVersion + 1, response.Workflow!.Steps.Single(step => step.Id == "resume-review-15").Version);
        Assert.Equal(currentVersion - 1, response.Workflow.Steps.Single(step => step.Id == "report-19").Version);
        Assert.Equal(currentVersion, response.Workflow.Steps.Single(step => step.Id == "resume-review-20").Version);
        Assert.Equal(currentVersion, response.Workflow.Steps.Single(step => step.Id == "report-24").Version);
        Assert.Equal(WorkflowStatus.Cancelled, response.Workflow.Steps.Single(step => step.Id == $"approval-{earlierVersion}").Status);
        Assert.Equal(new[] { 1, earlierVersion, currentVersion }, response.Workflow.Revisions.Select(item => item.Version));
        Assert.Equal(snapshot.Workflow.Proposals.Select(item => item.Version), response.Workflow.Proposals.Select(item => item.Version));
        Assert.Equal(snapshot.Events.Select(item => (item.Sequence, item.Status, item.Message)),
            response.Events.Select(item => (item.Sequence, item.Status, item.Message)));
        Assert.Equal(snapshot.Verification.Select(item => (item.Status, item.ExitCode, item.Log)),
            response.Verification.Select(item => (item.Status, item.ExitCode, item.Log)));
        Assert.Equal(before, JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public async Task RejectedReviewsRetainEachProposalAndNeverApplyOrRunIt()
    {
        var correction = 0;
        using var fixture = new WorkflowFixture(responseOverride: request =>
        {
            var properties = request.ResponseSchema!.Value.GetProperty("properties");
            object answer;
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (properties.TryGetProperty("decisions", out _)) answer = WorkflowFixture.Plan;
            else if (properties.TryGetProperty("content", out _))
            {
                var path = input.RootElement.GetProperty("file").GetProperty("path").GetString();
                var file = StudioFixture.Manifest().Files.Single(item => item.Path == path);
                answer = input.RootElement.GetProperty("diagnostics").ValueKind == JsonValueKind.Null
                    ? file : file with { Content = file.Content + $"\n// candidate {++correction}" };
            }
            else
            {
                Assert.False(input.RootElement.TryGetProperty("diagnostics", out _));
                Assert.False(input.RootElement.TryGetProperty("previousRevision", out _));
                var isCorrection = input.RootElement.GetProperty("correctedPaths").ValueKind != JsonValueKind.Null;
                answer = new ReviewReport("Review", isCorrection
                    ? [new("src/App/Program.cs", "Error", "Still incorrect.")] : [],
                    "Defect", [], "Review current source.", [], "Plan checked.", "Correct.");
            }
            return JsonSerializer.Serialize(answer, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        fixture.Sandbox.Result = stage => new(stage, "Failed", 1,
            "/work/src/App/Program.cs(1,1): error CS1525: Invalid expression [/work/src/App/App.csproj]");
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var original = (await fixture.Workspaces.ReadAsync(mission.Id, "src/App/Program.cs", default)).Content;
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var result = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.BuildFailed, result.Status);
        Assert.Equal(3, result.CorrectionAttempts);
        Assert.Equal(new[] { 2, 3, 4 }, result.Workflow!.Proposals!.Select(item => item.Version));
        Assert.All(result.Workflow.Proposals!, item => Assert.NotNull(item.Review));
        Assert.Contains("candidate 1", result.Workflow.Proposals![0].Files.Single(item => item.Path == "src/App/Program.cs").Content);
        Assert.Contains("candidate 3", result.Workflow.Proposals![2].Files.Single(item => item.Path == "src/App/Program.cs").Content);
        Assert.Equal(original, (await fixture.Workspaces.ReadAsync(mission.Id, "src/App/Program.cs", default)).Content);
        Assert.Single(fixture.Sandbox.Calls);
    }

    [Fact]
    public void StructuredDiagnosticsRetainCompilerLocationAndFailedTestDetails()
    {
        var build = DiagnosticParser.Parse(new("Build", "Failed", 1,
            "/work/src/App/Program.cs(7,12): error CS0103: Unknown variable [/work/src/App/App.csproj]\n"));
        var item = Assert.Single(build.Items);
        Assert.Equal(("Compiler", "src/App/App.csproj", "src/App/Program.cs", 7, 12, "CS0103"),
            (item.Type, item.Project, item.File, item.Line, item.Column, item.Code));
        Assert.Equal(1, build.ExitCode);
        var test = DiagnosticParser.Parse(new("Tests", "Failed", 1, """
            failure
            AIPLATFORM_TRX_BEGIN
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
            <UnitTestResult testName="Calculator.Subtract" outcome="Failed"><Output><ErrorInfo>
            <Message>Expected: 2 Actual: 4</Message><StackTrace>at Test in /work/tests/App.Tests/UnitTests.cs:line 8</StackTrace>
            </ErrorInfo></Output></UnitTestResult></Results></TestRun>
            AIPLATFORM_TRX_END
            """));
        var failed = Assert.Single(test.Items);
        Assert.Equal("Test", failed.Type);
        Assert.Equal("tests/App.Tests/UnitTests.cs", failed.File);
        Assert.Equal(8, failed.Line);
        Assert.Contains("Expected: 2", failed.Message);
        Assert.DoesNotContain("TestRun", test.Excerpt);
    }

    [Fact]
    public async Task StaleFilesCannotExecuteAndExplicitResumeRereviewsThem()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        await fixture.Approve(mission.Id);
        var path = Path.Combine(fixture.Workspaces.GetDirectory(mission.Id), "src", "App", "Program.cs");
        await File.AppendAllTextAsync(path, "\n// changed after approval");
        await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Empty(fixture.Sandbox.Calls);
        fixture.Store.Update(mission.Id, current => current with { Status = MissionStatus.Failed });
        Assert.True(fixture.Store.Resume(mission.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(MissionStatus.AwaitingApproval, fixture.Store.Get(mission.Id)!.Status);
        Assert.Contains("changed after approval", fixture.Provider.Requests.Last().Messages.Last().Content);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task RefusalAndRestartPreserveWorkspaceAndNeverAutomaticallyExecute()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var plan = await fixture.Plans.GetAsync(mission.Id, default);
        Assert.True(fixture.Store.DecideApproval(mission.Id, false, plan.Fingerprint));
        Assert.Equal(MissionStatus.GeneratedUnverified, fixture.Store.Get(mission.Id)!.Status);
        Assert.True(fixture.Store.Resume(mission.Id));
        fixture.Store.Update(mission.Id, current => current with { Status = MissionStatus.Running });
        using var restarted = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(MissionStatus.Cancelled, restarted.Get(mission.Id)!.Status);
        Assert.False(restarted.Get(mission.Id)!.SandboxApproved);
        Assert.NotEmpty(restarted.Get(mission.Id)!.Workflow!.Revisions!);
        Assert.True(restarted.Resume(mission.Id));
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(5, fixture.Workspaces.List(mission.Id).Count);
    }

    [Fact]
    public async Task PendingApprovalSurvivesRestartWithoutRequeuing()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        using var restarted = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry);
        Assert.Equal(MissionStatus.AwaitingApproval, restarted.Get(mission.Id)!.Status);
        Assert.Equal(WorkflowStatus.WaitingForApproval, restarted.Get(mission.Id)!.Workflow!.Steps.Single(step => step.Kind == "Approval").Status);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("""{"name":"bad","description":"bad","decisions":[],"files":[]}""")]
    public async Task InvalidPlanNeverReachesDeveloperOrDocker(string response)
    {
        using var fixture = new WorkflowFixture(responseOverride: _ => response);
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Single(fixture.Provider.Requests);
        Assert.False(fixture.Store.Get(mission.Id)!.HasWorkspace);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 2, 1)]
    [InlineData(true, 2, null)]
    public async Task DockerUnavailableOrInvalidTestCountersNeverVerify(bool available, int total, int? failed)
    {
        using var fixture = new WorkflowFixture(available: available, corrections: 0);
        fixture.Sandbox.Result = stage => new(stage, "Succeeded", 0, "output", stage == "Tests" ? total : null, FailedTests: failed);
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        if (available)
        {
            await fixture.Approve(mission.Id);
            await fixture.Processor.ProcessAsync(mission.Id, default);
        }
        Assert.Equal(available ? MissionStatus.TestsFailed : MissionStatus.GeneratedUnverified, fixture.Store.Get(mission.Id)!.Status);
    }

    [Fact]
    public async Task CancellationReachesModelAndStopsDownstreamSteps()
    {
        using var fixture = new WorkflowFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Processor.ProcessAsync(mission.Id, cancellation.Token));
        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.Sandbox.Calls);
        fixture.Store.Cancel(mission.Id);
        fixture.Store.Update(mission.Id, current => current with { Status = MissionStatus.Cancelled });
        Assert.DoesNotContain(fixture.Store.Get(mission.Id)!.Workflow!.Steps, step => step.Status == WorkflowStatus.Running);
    }

    [Fact]
    public async Task ActivePlanningCancellationReachesProvider()
    {
        using var fixture = new WorkflowFixture();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "";
        });
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([provider]), fixture.Options),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options);
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        var running = processor.ProcessAsync(mission.Id, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Single(provider.Requests);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidFileOrReviewOutputStopsBeforeApprovalAndDocker(bool invalidFile)
    {
        var calls = 0;
        using var fixture = new WorkflowFixture(responseOverride: request =>
        {
            calls++;
            var properties = request.ResponseSchema!.Value.GetProperty("properties");
            if (properties.TryGetProperty("decisions", out _))
                return JsonSerializer.Serialize(WorkflowFixture.Plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (properties.TryGetProperty("content", out _))
            {
                if (invalidFile) return """{"path":"../outside.cs","content":"untrusted"}""";
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                var file = StudioFixture.Manifest().Files.Single(file => file.Path ==
                    input.RootElement.GetProperty("file").GetProperty("path").GetString());
                return JsonSerializer.Serialize(file, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            return """{"summary":"Bad","findings":[{"path":"outside.cs","severity":"Error","message":"invented"}]}""";
        });
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.NotEqual(MissionStatus.AwaitingApproval, fixture.Store.Get(mission.Id)!.Status);
        Assert.Equal(invalidFile ? 2 : 6, calls);
    }

    [Fact]
    public void ProjectOnlyDeveloperCanBeManuallySelectedWithoutEnteringChatRouting()
    {
        using var fixture = new WorkflowFixture();
        var platform = fixture.Platform with { Agents = fixture.Platform.Agents.Select(agent =>
            agent.Role == "Developer" ? agent with { Id = "CustomDeveloper", TaskTypes = ["Project"] } : agent).ToList() };
        var registry = new AgentRegistry(platform);
        using var store = new MissionStore(fixture.Options, platform, registry);
        var mission = store.Submit(new("Create Calculator", MissionKind.Project, "CustomDeveloper"));
        Assert.Equal("CustomDeveloper", mission.SelectedAgent);
        Assert.DoesNotContain(registry.Candidates, agent => agent.Id == "CustomDeveloper");
    }

    [Fact]
    public async Task AgentTimeoutIsExplicitAndCanBeResumed()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        var provider = new FakeProvider(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return ""; });
        var agents = new WorkflowAgents(new LlmProviderResolver([provider]), fixture.Options);
        var planner = mission.Workflow!.Agents.Single(agent => agent.Role == "Planner") with { TimeoutSeconds = 1 };
        await Assert.ThrowsAsync<LlmTimeoutException>(() => agents.PlanAsync(mission, planner, default));
    }

    [Fact]
    public void CyclesUnknownDependenciesAndMissingToolPermissionsAreRejected()
    {
        Assert.Throws<StudioValidationException>(() => WorkflowGraph.Validate(
            [new("a", "Plan", "planner", ["b"]), new("b", "Generate", "dev", ["a"])]));
        Assert.Throws<StudioValidationException>(() => WorkflowGraph.Validate([new("a", "Plan", "planner", ["unknown"])]));
        Assert.Throws<StudioValidationException>(() => WorkflowGraph.Next(new(Guid.NewGuid(), Guid.NewGuid(), [], [],
            [new("a", "Plan", "planner", [], WorkflowStatus.Skipped), new("b", "Generate", "dev", ["a"])])));
        using var fixture = new WorkflowFixture();
        var invalid = fixture.Platform with { Agents = fixture.Platform.Agents.Select(agent =>
            agent.Role == "Review" ? agent with { AllowedTools = [] } : agent).ToList() };
        using var store = new MissionStore(fixture.Options, invalid, new AgentRegistry(invalid));
        Assert.Throws<StudioValidationException>(() => store.Submit(new("Create", MissionKind.Project)));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task ProjectManagementPreservesLegacyAuditAndRejectsActiveDeletion()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Create Calculator", MissionKind.Project));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.False(fixture.Store.DeleteProject(mission.Id));
        var plan = await fixture.Plans.GetAsync(mission.Id, default);
        fixture.Store.DecideApproval(mission.Id, false, plan.Fingerprint);
        Assert.True(fixture.Store.RenameProject(mission.Id, "Arithmetic", "Short description"));
        Assert.Equal("Arithmetic", fixture.Store.Get(mission.Id)!.ProjectName);
        Assert.True(fixture.Store.DeleteProject(mission.Id));
        Assert.True(fixture.Store.Get(mission.Id)!.Deleted);
        Assert.False(Directory.Exists(fixture.Workspaces.GetDirectory(mission.Id)));
        Assert.NotEmpty(fixture.Store.Get(mission.Id)!.Events);
        Assert.False(fixture.Store.Resume(mission.Id));
        var legacy = fixture.Store.Submit(new("Ordinary question"));
        Assert.Null(legacy.Workflow);
        Assert.False(fixture.Store.Resume(legacy.Id));
    }

    [Fact]
    public void ConfigurationPersistsAndCannotGrantShellOrCrossRoleTools()
    {
        using var fixture = new WorkflowFixture();
        var registry = new ConfigurableRegistry(fixture.Platform, fixture.Options);
        var configuration = registry.GetConfiguration();
        var changed = configuration with { Agents = configuration.Agents.Select(agent =>
            agent.Role == "Developer" ? agent with { Name = "Coder" } : agent).ToArray() };
        registry.Save(changed);
        Assert.Equal("Coder", new ConfigurableRegistry(fixture.Platform, fixture.Options).Agents.Single(agent => agent.Role == "Developer").Name);
        Assert.Throws<StudioValidationException>(() => registry.Save(changed with
        {
            Agents = changed.Agents.Select(agent => agent.Role == "Review"
                ? agent with { AllowedTools = ["GenerateFiles", "Shell"] } : agent).ToArray()
        }));
        Assert.Throws<StudioValidationException>(() => registry.Save(changed with
        {
            Models = changed.Models.Select(model => model with { MaxOutputTokens = model.ContextSize }).ToArray()
        }));
    }
}

internal sealed class WorkflowFixture : IDisposable
{
    public StudioOptions Options { get; }
    public PlatformConfiguration Platform { get; }
    public AgentRegistry Registry { get; }
    public WorkspaceStore Workspaces { get; }
    public MissionStore Store { get; }
    public ExecutionPlanService Plans { get; }
    public FakeProvider Provider { get; }
    public WorkflowSandbox Sandbox { get; }
    public CollaborativeProcessor Processor { get; }
    public static ProjectPlan Plan => new("Calculator", "Offline calculator library", ["Small deterministic library."],
    [
        new("src/App/App.csproj", "Application project", []),
        new("src/App/Program.cs", "Public implementation", ["src/App/App.csproj"]),
        new("tests/App.Tests/App.Tests.csproj", "Offline test project", ["src/App/App.csproj"]),
        new("tests/App.Tests/UnitTests.cs", "Actual unit tests", ["src/App/Program.cs", "tests/App.Tests/App.Tests.csproj"])
    ]);

    public WorkflowFixture(bool available = true, int corrections = 3,
        Func<LlmRequest, string>? responseOverride = null, bool fullStack = false, int maxOutputTokens = 2048)
    {
        Options = new() { DataDirectory = Path.Combine(Path.GetTempPath(), "AIPlatform-workflow-" + Guid.NewGuid().ToString("N")),
            CollaborativeWorkflows = true, MaxCorrectionAttempts = corrections };
        var original = TestSupport.Configuration();
        var workflowRoles = new List<string> { "Planner", "Review", "Test", "Orchestrator" };
        if (fullStack) workflowRoles.AddRange(["Backend", "Frontend"]);
        Platform = original with
        {
            Models = original.Models.Select(model => model with { ContextSize = 8192, MaxOutputTokens = maxOutputTokens }).ToList(),
            Agents = [.. original.Agents.Select(agent => agent.Id == "DeveloperAgent"
                ? agent with { Role = "Developer", AllowedTools = ["GenerateFiles"], TaskTypes = ["Chat", "Project"] } : agent),
                .. workflowRoles.Select(role => new AgentDefinition
                {
                    Id = role + "Agent", Role = role, Description = role, SystemPrompt = role,
                    ModelId = role is "Review" or "Test" or "Backend" or "Frontend" ? "developer" : "general",
                    TaskTypes = ["Project"], AllowedTools = AgentPermissions.ToolsFor(role)
                })]
        };
        Registry = new(Platform);
        Workspaces = new(Options);
        Store = new(Options, Platform, Registry, Workspaces);
        Plans = new(Workspaces, new(), Options, Store, new WorkflowImageProcess());
        Sandbox = new(available);
        Provider = new((request, cancellationToken) =>
        {
            if (responseOverride is not null) return Task.FromResult(responseOverride(request));
            var properties = request.ResponseSchema!.Value.GetProperty("properties");
            object response;
            if (properties.TryGetProperty("decisions", out _)) response = Plan;
            else if (properties.TryGetProperty("content", out _))
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                var path = input.RootElement.GetProperty("file").GetProperty("path").GetString();
                response = fullStack
                    ? FullStackTemplate.Files.Single(file => file.Path == path)
                    : StudioFixture.Manifest().Files.Single(file => file.Path == path);
            }
            else response = new ReviewReport("Source review complete; not executed.", [], "Review",
                [], "No blocking defect found.", [], "Matches plan.", "Verify in Docker.");
            return Task.FromResult(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        });
        Processor = new(Store, new WorkflowAgents(new LlmProviderResolver([Provider]), Options), Workspaces, Plans, Sandbox, Options);
    }

    public async Task Approve(Guid id)
    {
        var plan = await Plans.GetAsync(id, default);
        Assert.True(Store.DecideApproval(id, true, plan.Fingerprint));
    }

    public void Dispose()
    {
        Store.Dispose();
        if (Directory.Exists(Options.DataDirectory)) Directory.Delete(Options.DataDirectory, recursive: true);
    }
}

internal sealed class WorkflowImageProcess : IContainerProcess
{
    public Task<ContainerProcessResult> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        Task.FromResult(new ContainerProcessResult(0, "sha256:" + new string('a', 64)));
}

internal sealed class WorkflowSandbox(bool available) : ISandboxRunner
{
    public List<string> Calls { get; } = [];
    public Func<string, VerificationResult>? Result { get; set; }
    public Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SandboxAvailability(available, available ? "Ready" : "Unavailable"));
    public Task<VerificationResult> RunAsync(Guid workspaceId, string stage, CancellationToken cancellationToken)
    {
        Calls.Add(stage);
        return Task.FromResult(Result?.Invoke(stage) ?? new(stage, "Succeeded", 0, "Simulated Docker",
            stage == "Tests" ? 2 : null, FailedTests: stage == "Tests" ? 0 : null));
    }
}
