using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class ReviewerCorrectionTests
{
    private const string FiveCriteria = "- Display initial value 0\n- Increment value with +1\n- Decrement value with -1\n- Reset value to 0\n- Persist value in localStorage";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedCorrectionKeepsCompleteCriteriaReportSourcesAndStrictCitations(bool invalidCitation)
    {
        var reviews = 0;
        var correctionInputs = new List<string>();
        string? initialInput = null;
        string? initialSystem = null;
        string? rejected = null;
        using var fixture = new WorkflowFixture(available: false, corrections: 2, maxOutputTokens: 3072,
            responseOverride: request =>
            {
                var response = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
                if (response["decisions"] is JsonArray decisions)
                {
                    for (var index = 0; index < 4; index++) decisions.Add(new string('d', 400));
                    foreach (var coverage in response["criterionCoverage"]!.AsArray())
                        coverage!["explanation"] = new string('p', 450);
                }
                if (response["content"] is not null &&
                    response["path"]!.GetValue<string>() == "src/App.test.tsx")
                    response["content"] = response["content"]!.GetValue<string>() + "\n/*" + new string('t', 2000) + "*/";
                if (IsReview(request))
                {
                    reviews++;
                    if (reviews == 1)
                    {
                        initialInput = request.Messages[1].Content;
                        initialSystem = request.Messages[0].Content;
                        var items = response["criterionReviews"]!.AsArray();
                        while (items.Count > 1) items.RemoveAt(1);
                        items[0]!["explanation"] = new string('e', 900);
                        response["findings"] = new JsonArray(JsonNode.Parse(
                            """{"path":"src/App.tsx","severity":"Warning","message":"Preserve this finding"}"""),
                            JsonNode.Parse("""{"path":"src/App.test.tsx","severity":"Warning","message":""}"""));
                        response["findings"]![1]!["message"] = new string('w', 480);
                        response["summary"] = new string('s', 240);
                        response["problem"] = new string('p', 800);
                        response["justification"] = new string('j', 800);
                        response["recommendation"] = new string('r', 800);
                        response["risks"] = new JsonArray(new string('r', 150));
                        rejected = response.ToJsonString();
                    }
                    else
                    {
                        correctionInputs.Add(request.Messages[1].Content);
                        Assert.True(request.Messages.Sum(message => message.Content.Length) <= 10240);
                        using var input = JsonDocument.Parse(request.Messages[1].Content);
                        Assert.Equal(new[] { "C1", "C2", "C3", "C4", "C5" },
                            input.RootElement.GetProperty("acceptanceCriteria").EnumerateArray()
                                .Select(item => item.GetProperty("id").GetString()));
                        Assert.Equal(5, input.RootElement.GetProperty("plan").GetProperty("criterionCoverage").GetArrayLength());
                        Assert.False(input.RootElement.GetProperty("plan").TryGetProperty("decisions", out _));
                        Assert.Equal("Preserve this finding", input.RootElement.GetProperty("rejectedReport")
                            .GetProperty("findings")[0].GetProperty("message").GetString());
                        Assert.Equal("criterionReviews[0].explanation",
                            input.RootElement.GetProperty("omittedRejectedReportFields")[0].GetString());
                        Assert.False(input.RootElement.GetProperty("rejectedReport")
                            .GetProperty("criterionReviews")[0].TryGetProperty("explanation", out _));
                        Assert.Equal("return <main>Mission</main>", input.RootElement.GetProperty("rejectedReport")
                            .GetProperty("criterionReviews")[0].GetProperty("evidence")[0].GetProperty("quote").GetString());
                        Assert.Equal("MissingCriterion", input.RootElement.GetProperty("validationErrors")[1].GetProperty("rule").GetString());
                        Assert.Contains(input.RootElement.GetProperty("sources").EnumerateArray(), source =>
                            source.GetProperty("path").GetString() == "src/App.tsx" &&
                            source.GetProperty("content").GetString() == "export default function App() { return <main>Mission</main> }");
                        if (reviews == 2) return rejected!;
                        if (invalidCitation) response["criterionReviews"]![1]!["evidence"]![0]!["quote"] = "invented citation";
                    }
                }
                return response.ToJsonString();
            });
        var mission = fixture.Store.Submit(new(FiveCriteria, MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, MaxTotalGenerations: 10));
        if (invalidCitation)
        {
            var error = await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
            Assert.Contains(error.ReviewerDiagnostics!, item => item.Rule == "QuoteMismatch" && item.CriterionId == "C2");
        }
        else await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(3, reviews);
        Assert.Equal(correctionInputs[0], correctionInputs[1]);
        var attempts = new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).ReviewerAttempts(mission.Id);
        var previous = JsonNode.Parse(initialInput!)!;
        previous["rejectedReport"] = JsonNode.Parse(rejected!);
        previous["validationErrors"] = JsonSerializer.SerializeToNode(attempts[0].Errors, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        const string addition = " Correct only your rejected report using the exact validation diagnostics and current sources. " +
            "Preserve all code findings, risks and criterion verdicts unless an identified error requires their correction. " +
            "Never hide a code defect to repair report format. Do not modify application files or invent source citations. " +
            "Copy exact text from the supplied sources, without approximations or line numbers, at most 500 characters per quote. " +
            "Return the complete report in the same schema; absence of runner evidence is not a passed test.";
        Assert.True(initialSystem!.Length + addition.Length +
            previous.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length > 10240);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(7, current.GenerationReservations!.Count);
        Assert.Equal(invalidCitation ? null : 5, current.Workflow!.Review?.CriterionReviews?.Count);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task OversizedEssentialSourcesStopBeforeAnotherReservationOrProviderCall()
    {
        var reviews = 0;
        using var fixture = new WorkflowFixture(available: false, corrections: 2, maxOutputTokens: 3072,
            responseOverride: request =>
            {
                var response = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
                if (response["content"] is not null &&
                    response["path"]!.GetValue<string>() == "src/App.tsx")
                    response["content"] = response["content"]!.GetValue<string>() + "\n/*" + new string('x', 3500) + "*/";
                if (IsReview(request))
                {
                    reviews++;
                    var items = response["criterionReviews"]!.AsArray();
                    while (items.Count > 1) items.RemoveAt(1);
                    items[0]!["explanation"] = new string('e', 900);
                    response["summary"] = new string('s', 1900);
                    response["problem"] = new string('p', 900);
                    response["justification"] = new string('j', 900);
                    response["recommendation"] = new string('r', 900);
                }
                return response.ToJsonString();
            });
        var mission = fixture.Store.Submit(new(FiveCriteria, MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, MaxTotalGenerations: 10));
        var error = await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Contains("Reviewer correction context remains too large", error.Message);
        Assert.Contains("no generation was started", error.Message);
        Assert.Equal(1, reviews);
        Assert.Equal(5, fixture.Provider.Requests.Count);
        Assert.Equal(5, fixture.Store.Get(mission.Id)!.GenerationReservations!.Count);
        Assert.Null(fixture.Store.Get(mission.Id)!.Workflow!.Review);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task DiagnosedCriterionExplanationIsNeverOmittedToFit()
    {
        var reviews = 0;
        using var fixture = new WorkflowFixture(available: false, corrections: 1, maxOutputTokens: 3072,
            responseOverride: request =>
            {
                var response = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
                if (response["decisions"] is JsonArray decisions)
                {
                    for (var index = 0; index < 4; index++) decisions.Add(new string('d', 400));
                    foreach (var coverage in response["criterionCoverage"]!.AsArray())
                        coverage!["explanation"] = new string('p', 450);
                }
                if (response["content"] is not null && response["path"]!.GetValue<string>() == "src/App.test.tsx")
                    response["content"] = response["content"]!.GetValue<string>() + "\n/*" + new string('t', 2600) + "*/";
                if (IsReview(request))
                {
                    reviews++;
                    var items = response["criterionReviews"]!.AsArray();
                    items[0]!["explanation"] = new string('e', 1001);
                    response["summary"] = new string('s', 240);
                    response["problem"] = new string('p', 800);
                    response["justification"] = new string('j', 800);
                    response["recommendation"] = new string('r', 800);
                    response["findings"] = new JsonArray(
                        JsonNode.Parse("""{"path":"src/App.tsx","severity":"Warning","message":""}"""),
                        JsonNode.Parse("""{"path":"src/App.test.tsx","severity":"Warning","message":""}"""));
                    response["findings"]![0]!["message"] = new string('w', 480);
                    response["findings"]![1]!["message"] = new string('w', 480);
                    response["risks"] = new JsonArray(new string('r', 150));
                }
                return response.ToJsonString();
            });
        var mission = fixture.Store.Submit(new(FiveCriteria, MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, MaxTotalGenerations: 10));
        var error = await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Contains("Reviewer correction context remains too large", error.Message);
        Assert.Contains("no generation was started", error.Message);
        var diagnostic = Assert.Single(new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).ReviewerAttempts(mission.Id));
        Assert.Contains(diagnostic.Errors, item => item.Field == "criterionReviews[0].explanation");
        Assert.Equal(1001, diagnostic.Report!.Value.GetProperty("criterionReviews")[0].GetProperty("explanation").GetString()!.Length);
        Assert.Equal(1, reviews);
        Assert.Equal(5, fixture.Store.Get(mission.Id)!.GenerationReservations!.Count);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task MissingPlannedSourceStopsCompactionWithoutAProviderCall()
    {
        using var fixture = new WorkflowFixture(available: false, maxOutputTokens: 3072);
        var saved = fixture.Store.Submit(new(FiveCriteria, MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, MaxTotalGenerations: 10));
        var plan = new ProjectPlan("Test", "Test", [],
            [new("src/App.tsx", "App", []), new("src/missing.test.tsx", "Test", [])])
        {
            CriterionCoverage = saved.Workflow!.AcceptanceCriteria.Select(item =>
                new PlannedCriterionCoverage(item.Id, ["src/App.tsx", "src/missing.test.tsx"], "Coverage")).ToArray()
        };
        var flow = saved.Workflow with
        {
            Plan = plan,
            Draft = [new("src/App.tsx", "export default function App() { return <main>Mission</main> }")],
            Steps = saved.Workflow.Steps.Select(step => step.Kind == "Review"
                ? step with { Status = WorkflowStatus.Running } : step).ToArray()
        };
        var mission = saved with { Workflow = flow };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { flow.Version, mission.Task, flow.AcceptanceCriteria, flow.Plan, flow.Draft })));
        var diagnostics = new OllamaDiagnosticStore(fixture.Options, TimeProvider.System);
        var context = new LlmDiagnosticContext(mission.Id, flow.WorkflowId,
            flow.Steps.Single(step => step.Kind == "Review").Id, Guid.NewGuid());
        diagnostics.BeginReviewer(context, "fake", 1, fingerprint, null);
        using var report = JsonDocument.Parse("{\"summary\":\"" + new string('x', 8000) + "\"}");
        diagnostics.RecordReviewerResult(context, report.RootElement);
        diagnostics.FinishReviewer(context, "Rejected",
            [new("C1", "MissingCriterion", "criterionReviews", null, null, "Criterion missing.")]);
        using var scope = fixture.Store.EnterGenerationScope(mission.Id, "Reviewer");
        var agent = flow.Agents.Single(item => item.Role == "Review");
        var error = await Assert.ThrowsAsync<StudioValidationException>(() =>
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options, diagnosticStore: diagnostics)
                .ReviewAsync(mission, agent, default));
        Assert.Contains("src/missing.test.tsx", error.Message);
        Assert.Contains("no generation was started", error.Message);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.Store.Get(mission.Id)!.GenerationReservations!);
    }

    [Theory]
    [InlineData("empty", "MissingQuote", "criterionReviews[0].evidence[0].quote")]
    [InlineData("missing", "MissingCitation", "criterionReviews[0].evidence")]
    [InlineData("long", "QuoteTooLong", "criterionReviews[0].evidence[0].quote")]
    [InlineData("path", "UnknownPath", "criterionReviews[0].evidence[0].path")]
    [InlineData("null-path", "UnknownPath", "criterionReviews[0].evidence[0].path")]
    [InlineData("mismatch", "QuoteMismatch", "criterionReviews[0].evidence[0].quote")]
    [InlineData("newline", "QuoteMismatch", "criterionReviews[0].evidence[0].quote")]
    [InlineData("numbered", "QuoteMismatch", "criterionReviews[0].evidence[0].quote")]
    [InlineData("verdict", "InvalidVerdict", "criterionReviews[0].verdict")]
    [InlineData("explanation", "InvalidExplanation", "criterionReviews[0].explanation")]
    [InlineData("summary", "InvalidText", "summary")]
    [InlineData("criterion", "MissingCriterion", "criterionReviews")]
    [InlineData("duplicate", "DuplicateCriterion", "criterionReviews")]
    public async Task RejectedReportsKeepExactPrivateDiagnosticsAndAreNeverAccepted(string defect, string rule, string field)
    {
        string? actual = null;
        using var fixture = new WorkflowFixture(corrections: 0, responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (!IsReview(request)) return result.ToJsonString();
            var criterion = result["criterionReviews"]![0]!;
            var evidence = criterion["evidence"]![0]!;
            switch (defect)
            {
                case "empty": evidence["quote"] = " "; break;
                case "missing": criterion["evidence"] = new JsonArray(); break;
                case "long": evidence["quote"] = new string('x', 501); break;
                case "path": evidence["path"] = "src/app.tsx"; break;
                case "null-path": evidence["path"] = null; break;
                case "mismatch": evidence["quote"] = "invented sensitive-looking source text"; break;
                case "newline": evidence["quote"] = "return\n<main>Mission</main>"; break;
                case "numbered": evidence["quote"] = "L1: return <main>Mission</main>"; break;
                case "verdict": criterion["verdict"] = "Passed"; break;
                case "explanation": criterion["explanation"] = ""; break;
                case "summary": result["summary"] = ""; break;
                case "criterion": result["criterionReviews"] = new JsonArray(); break;
                case "duplicate": result["criterionReviews"]!.AsArray().Add(criterion.DeepClone()); break;
            }
            return actual = result.ToJsonString();
        });
        var mission = Submit(fixture);
        var exception = await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        var diagnostic = Assert.Single(exception.ReviewerDiagnostics!, item => item.Rule == rule);
        Assert.Equal(field, diagnostic.Field);
        Assert.Equal(defect == "summary" ? null : "C1", diagnostic.CriterionId);
        if (defect == "mismatch")
        {
            Assert.Equal("src/App.tsx", diagnostic.Path);
            Assert.Equal("invented sensitive-looking source text", diagnostic.Quote);
            Assert.DoesNotContain("invented sensitive-looking source text", exception.Message);
        }
        var artifact = Assert.Single(new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).ReviewerAttempts(mission.Id));
        Assert.Equal("Rejected", artifact.Status);
        Assert.Equal(mission.Workflow!.WorkflowId, artifact.RunId);
        Assert.Equal("review-1", artifact.StepId);
        Assert.Equal(64, artifact.SourceFingerprint.Length);
        Assert.Equal(JsonNode.Parse(actual!)!.ToJsonString(), JsonNode.Parse(artifact.Report!.Value.GetRawText())!.ToJsonString());
        Assert.Equal(exception.ReviewerDiagnostics, artifact.Errors);
        Assert.Equal(fixture.Provider.Requests.Last().Model.Name, artifact.Model);
        Assert.Null(fixture.Store.Get(mission.Id)!.Workflow!.Review);
        Assert.All(fixture.Provider.Requests, request => Assert.Null(request.DiagnosticContext));
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Empty(fixture.Store.Get(mission.Id)!.Workflow!.CriterionExecutions);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    public async Task OnlyTheModelRepairsItsReportWithinTheSharedBudget(int invalid, int budget)
    {
        var reviews = 0;
        using var fixture = new WorkflowFixture(available: false, corrections: budget, responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (!IsReview(request)) return result.ToJsonString();
            reviews++;
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            Assert.Equal("Display requested information", input.RootElement.GetProperty("request").GetString());
            Assert.Single(input.RootElement.GetProperty("acceptanceCriteria").EnumerateArray());
            Assert.Contains(input.RootElement.GetProperty("sources").EnumerateArray(),
                source => source.GetProperty("path").GetString() == "src/App.tsx");
            if (reviews > 1)
            {
                Assert.Equal("QuoteMismatch", input.RootElement.GetProperty("validationErrors")[0].GetProperty("rule").GetString());
                Assert.Equal("not copied from source", input.RootElement.GetProperty("rejectedReport")
                    .GetProperty("criterionReviews")[0].GetProperty("evidence")[0].GetProperty("quote").GetString());
                Assert.Equal("Preserve this warning", input.RootElement.GetProperty("rejectedReport")
                    .GetProperty("findings")[0].GetProperty("message").GetString());
                Assert.Contains("Preserve all code findings", request.Messages[0].Content);
            }
            Assert.Contains("case, spaces and newlines count", request.Messages[0].Content);
            Assert.Contains("Do not add line numbers", request.Messages[0].Content);
            Assert.Contains("at most 500 characters", request.Messages[0].Content);
            result["findings"] = new JsonArray(JsonNode.Parse(
                """{"path":"src/App.tsx","severity":"Warning","message":"Preserve this warning"}"""));
            if (reviews <= invalid) result["criterionReviews"]![0]!["evidence"]![0]!["quote"] = "not copied from source";
            return result.ToJsonString();
        });
        var mission = Submit(fixture);
        var exhausted = invalid > budget;
        if (exhausted)
            await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        else await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(Math.Min(invalid + 1, budget + 1), reviews);
        Assert.Equal(reviews - 1, current.CorrectionAttempts);
        Assert.Equal(1, current.Workflow!.Version);
        Assert.DoesNotContain(current.Workflow.Steps, step => step.Kind == "Correct");
        Assert.Equal(3, fixture.Provider.Requests.Count(request =>
            request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("content", out _)));
        var attempts = new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).ReviewerAttempts(mission.Id);
        Assert.Equal(Enumerable.Range(1, reviews), attempts.Select(item => item.AttemptNumber));
        Assert.Equal(Math.Min(reviews, invalid), current.Events.Count(item => item.Step == "ReviewerValidation"));
        Assert.Equal(reviews - 1, current.Events.Count(item => item.Step == "ReviewerCorrection"));
        if (exhausted) Assert.Null(current.Workflow.Review);
        else
        {
            Assert.Equal(MissionStatus.GeneratedUnverified, current.Status);
            Assert.Equal("Accepted", attempts.Last().Status);
            Assert.Equal("Preserve this warning", Assert.Single(current.Workflow.Review!.Findings).Message);
        }
        Assert.Empty(current.Workflow.CriterionExecutions);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Empty(current.Verification);
    }

    [Fact]
    public async Task AcceptedReportSupersedesEarlierRejectedFeedbackForTheSameSources()
    {
        var reviews = 0;
        using var fixture = new WorkflowFixture(available: false, corrections: 1, responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (IsReview(request))
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                Assert.Equal(++reviews == 2, input.RootElement.TryGetProperty("rejectedReport", out _));
                if (reviews == 1) result["criterionReviews"]![0]!["evidence"]![0]!["quote"] = "not source";
            }
            return result.ToJsonString();
        });
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        await new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options)
            .ReviewAsync(current, current.Workflow!.Agents.Single(agent => agent.Role == "Review"), default);
        Assert.Equal(3, reviews);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task ValidCodeFindingWaitsForIndependentValidationWithoutDeveloperOrReportRepair()
    {
        var reviews = 0;
        var corrected = false;
        using var fixture = new WorkflowFixture(available: false, corrections: 1, responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (IsReview(request))
            {
                Assert.False(input.RootElement.TryGetProperty("rejectedReport", out _));
                if (++reviews == 1) result["findings"] = new JsonArray(JsonNode.Parse(
                    """{"path":"src/App.tsx","severity":"Error","message":"Observed code defect needs repair"}"""));
            }
            else if (result["content"] is not null &&
                input.RootElement.GetProperty("diagnostics").ValueKind != JsonValueKind.Null)
            {
                Assert.Equal("src/App.tsx", input.RootElement.GetProperty("file").GetProperty("path").GetString());
                Assert.Contains("Observed code defect", input.RootElement.GetProperty("diagnostics").GetString());
                result["content"] = result["content"]!.GetValue<string>() + "\n// model repair";
                corrected = true;
            }
            return result.ToJsonString();
        });
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.False(corrected);
        Assert.Equal(1, reviews);
        Assert.Equal(0, current.CorrectionAttempts);
        Assert.Equal(MissionStatus.GeneratedUnverified, current.Status);
        Assert.Contains(current.Workflow!.Review!.Findings, finding => finding.Severity == "Error" &&
            finding.Message == "Observed code defect needs repair");
        Assert.DoesNotContain(current.Workflow.Steps, step => step.Kind == "Correct");
        Assert.DoesNotContain(current.Events, item => item.Step == "ReviewerCorrection");
        Assert.All(new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).ReviewerAttempts(mission.Id),
            item => Assert.Equal("Accepted", item.Status));
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task CancellationDuringReportCorrectionKeepsAttemptsAndDiagnostics()
    {
        using var cancellation = new CancellationTokenSource();
        var reviews = 0;
        using var fixture = new WorkflowFixture(corrections: 2, responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (IsReview(request))
            {
                if (++reviews == 2) cancellation.Cancel();
                result["criterionReviews"]![0]!["evidence"]![0]!["quote"] = "not source";
            }
            return result.ToJsonString();
        });
        var mission = Submit(fixture);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Processor.ProcessAsync(mission.Id, cancellation.Token));
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(2, reviews);
        Assert.Equal(1, current.CorrectionAttempts);
        Assert.DoesNotContain(current.Workflow!.Steps, step => step.Kind == "Correct");
        var diagnostics = new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).ReviewerAttempts(mission.Id);
        Assert.Equal(new[] { "Rejected", "Cancelled" }, diagnostics.Select(item => item.Status));
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(current.CorrectionAttempts, reopened.Get(mission.Id)!.CorrectionAttempts);
        Assert.Equal(MissionStatus.Cancelled, reopened.Get(mission.Id)!.Status);
        Assert.Equal(WorkflowStatus.Cancelled, reopened.Get(mission.Id)!.Workflow!.Steps.Single(step => step.Kind == "Review").Status);
        Assert.Null(reopened.Get(mission.Id)!.Workflow!.Review);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task HistoricalWorkflowsDoNotAcquireTheNewPolicyOnReload()
    {
        using var fixture = new WorkflowFixture(responseOverride: request =>
        {
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (IsReview(request)) result["criterionReviews"]![0]!["evidence"]![0]!["quote"] = "not source";
            return result.ToJsonString();
        });
        var mission = Submit(fixture);
        fixture.Store.Update(mission.Id, current => current with
        { Status = MissionStatus.Failed, Workflow = current.Workflow! with { ReviewerCorrectionEnabled = false } });
        var path = Path.Combine(fixture.Options.DataDirectory, "missions", mission.Id.ToString("N") + ".json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["workflow"]!.AsObject().Remove("reviewerCorrectionEnabled");
        File.WriteAllText(path, json.ToJsonString());
        var bytes = File.ReadAllBytes(path);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(reopened.Get(mission.Id)!.Workflow!.ReviewerCorrectionEnabled);
        Assert.True(reopened.Resume(mission.Id));
        var processor = new CollaborativeProcessor(reopened,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
            fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options);
        await Assert.ThrowsAsync<StudioValidationException>(() => processor.ProcessAsync(mission.Id, default));
        Assert.Single(fixture.Provider.Requests, IsReview);
        Assert.Empty(new OllamaDiagnosticStore(fixture.Options, TimeProvider.System).ReviewerAttempts(mission.Id));
    }

    [Fact]
    public void ReviewerDiagnosticsShareRetentionBoundsDeletionAndRawCaptureDenial()
    {
        using var fixture = new WorkflowFixture();
        var store = new OllamaDiagnosticStore(fixture.Options with { OllamaTraceMaxBytes = 2048 }, TimeProvider.System);
        var mission = Submit(fixture);
        var context = new LlmDiagnosticContext(mission.Id, mission.Workflow!.WorkflowId, "review-1", Guid.NewGuid());
        store.BeginReviewer(context, "fake", 1, "source-hash", null);
        Assert.Throws<StudioValidationException>(() => store.DeleteReviewerAttempts(mission.Id));
        Assert.Throws<LlmDiagnosticException>(() => store.RecordRequest(context, "fake", "{}"u8.ToArray()));
        using var huge = JsonDocument.Parse("{\"summary\":\"" + new string('x', 4096) + "\"}");
        Assert.Throws<LlmDiagnosticException>(() => store.RecordReviewerResult(context, huge.RootElement));
        store.FinishReviewer(context, "Failed", []);
        Assert.Single(store.ReviewerAttempts(mission.Id));
        store.DeletePlannerAttempts(mission.Id);
        Assert.Single(store.ReviewerAttempts(mission.Id));
        store.DeleteReviewerAttempts(mission.Id);
        Assert.Empty(store.ReviewerAttempts(mission.Id));
        store.BeginReviewer(context with { AttemptId = Guid.NewGuid() }, "fake", 2, "source-hash", null);
        var entry = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Options.DataDirectory, "ollama-traces")));
        File.SetLastWriteTimeUtc(entry, DateTime.UtcNow.AddDays(-8));
        var reopened = new OllamaDiagnosticStore(fixture.Options, TimeProvider.System);
        Assert.Empty(reopened.ReviewerAttempts(mission.Id));
    }

    private static MissionSnapshot Submit(WorkflowFixture fixture) =>
        fixture.Store.Submit(new("Display requested information", MissionKind.Project, TemplateId: MissionReactTemplate.Id));

    private static bool IsReview(LlmRequest request) =>
        request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("criterionReviews", out _);
}
