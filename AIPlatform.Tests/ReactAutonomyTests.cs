using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIPlatform.API;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class ReactAutonomyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task ReactGenerationAndStaticReviewDoNotInspectDockerBeforeExecutionPrerequisites(bool available, int inspections)
    {
        var process = new UnavailableImageProcess();
        using var fixture = new WorkflowFixture(available: available, responseOverride: request =>
        {
            Assert.Empty(process.Calls);
            return MissionReactContractTests.Respond(request);
        }, sourceCheck: new Checker());
        var plans = new ExecutionPlanService(fixture.Workspaces, new() { Enabled = true }, fixture.Options, fixture.Store, process);
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
            fixture.Workspaces, plans, fixture.Sandbox, fixture.Options, reactSourceCheck: new Checker());
        var mission = Submit(fixture);

        await processor.ProcessAsync(mission.Id, default);

        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.GeneratedUnverified, current.Status);
        Assert.Equal(5, current.GenerationReservations!.Count);
        Assert.Equal(WorkflowStatus.Completed, current.Workflow!.Steps.Single(step => step.Kind == "Review").Status);
        Assert.NotNull(current.Workflow.Review);
        Assert.Equal(inspections, process.Calls.Count);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.False(current.SandboxApproved);
        var revision = Assert.Single(current.Workflow.Revisions!);
        Assert.Null(revision.PreparedExecutionPlan);
        Assert.Null(revision.ExecutionPlan!.ReactDescriptor!.ImageDigest);
        Assert.False(fixture.Store.DecideApproval(mission.Id, true, revision.Fingerprint));
        await Assert.ThrowsAsync<SandboxPrerequisiteException>(() => plans.GetAsync(mission.Id, default));
        Assert.Equal(inspections + 1, process.Calls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ExecutionApprovalRejectsMissingOrAlteredPreparedIdentity(int damage)
    {
        using var fixture = new WorkflowFixture(responseOverride: MissionReactContractTests.Respond, sourceCheck: new Checker());
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var before = fixture.Store.Get(mission.Id)!;
        var revision = before.Workflow!.Revisions!.Last();
        var prepared = revision.PreparedExecutionPlan!;
        fixture.Store.Update(mission.Id, item => item with
        {
            Workflow = item.Workflow! with
            {
                RequirePlanApproval = damage == 4 || item.Workflow.RequirePlanApproval,
                Revisions = [revision with
                {
                    PreparedExecutionPlan = damage switch
                    {
                        0 => null,
                        1 => prepared with { Image = "mutable:tag" },
                        2 => prepared with { ReactDescriptor = prepared.ReactDescriptor! with { ImageDigest = null } },
                        3 => prepared with { Fingerprint = "changed" },
                        5 => prepared with
                        {
                            ReactDescriptor = prepared.ReactDescriptor! with { MemoryMegabytes = prepared.MemoryMegabytes + 1 },
                            Fingerprint = (prepared.ReactDescriptor! with { MemoryMegabytes = prepared.MemoryMegabytes + 1 })
                                .ComputeFingerprint(revision.Files)
                        },
                        _ => prepared
                    }
                }]
            }
        });
        var requested = fixture.Store.Get(mission.Id)!.Workflow!.Revisions!.Last().PreparedExecutionPlan?.Fingerprint ?? prepared.Fingerprint;
        Assert.False(fixture.Store.DecideApproval(mission.Id, true, requested));
        Assert.False(fixture.Store.Get(mission.Id)!.SandboxApproved);
        Assert.Equal(before.GenerationReservations, fixture.Store.Get(mission.Id)!.GenerationReservations);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public async Task ReadyDeveloperCandidateRequiresItsExistingReservationAndResponseEvidence(int damage, bool eligible)
    {
        var checker = new Checker();
        using var fixture = new WorkflowFixture(sourceCheck: checker, responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _)) return Correction(request, 1);
            var reply = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (reply["path"]?.GetValue<string>() == "src/App.tsx") reply["content"] = LargeApp();
            return reply.ToJsonString();
        });
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
            fixture.Workspaces, new InterruptedStaticPlan(fixture.Plans), fixture.Sandbox, fixture.Options,
            reactSourceCheck: checker);
        var mission = Submit(fixture);
        await Assert.ThrowsAsync<StudioValidationException>(() => processor.ProcessAsync(mission.Id, default));
        var before = fixture.Store.Get(mission.Id)!;
        var state = Assert.Single(before.Workflow!.SoftwareCorrections);
        Assert.True(state.Ready);
        Assert.NotNull(state.ResponseReceipt);
        fixture.Store.Update(mission.Id, item => item with
        {
            Status = MissionStatus.Failed,
            Workflow = item.Workflow! with
            {
                SoftwareCorrections = [damage switch
                {
                    1 => state with { ResponseReceipt = null },
                    2 => state with { ActiveAttempt = state.ActiveAttempt! with { PlanFingerprint = "changed" } },
                    3 => state with { ResponseReceipt = state.ResponseReceipt! with { ResponseFingerprint = null } },
                    _ => state
                }]
            }
        });
        Assert.Equal(eligible, fixture.Store.Resume(mission.Id));
        if (eligible)
        {
            await fixture.Processor.ProcessAsync(mission.Id, default);
            var after = fixture.Store.Get(mission.Id)!;
            Assert.Equal(MissionStatus.AwaitingApproval, after.Status);
            Assert.Equal(before.GenerationReservations!.Count + 1, after.GenerationReservations!.Count);
            Assert.Equal(before.GenerationReservations, after.GenerationReservations.Take(before.GenerationReservations.Count));
        }
        else Assert.Equal(before.GenerationReservations, fixture.Store.Get(mission.Id)!.GenerationReservations);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData("safe")]
    [InlineData("ambiguous")]
    [InlineData("changed-revision")]
    [InlineData("changed-receipt")]
    public async Task InterruptedRereviewUsesPublishedCorrectionEvidenceWithoutReplayingAmbiguousCall(string scenario)
    {
        var checker = new Checker();
        using var fixture = new WorkflowFixture(available: false, sourceCheck: checker, responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _)) return Correction(request, 1);
            var reply = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (reply["path"]?.GetValue<string>() == "src/App.tsx") reply["content"] = LargeApp();
            return reply.ToJsonString();
        });
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var published = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.GeneratedUnverified, published.Status);
        Assert.True(published.Workflow!.SoftwareCorrections.Last().Ready);
        Assert.True(fixture.Store.Resume(mission.Id));
        var queued = fixture.Store.Get(mission.Id)!;
        var review = queued.Workflow!.Steps.Last(step => step.Kind == "Review");
        var revision = Assert.Single(queued.Workflow.Revisions!);
        fixture.Store.Update(mission.Id, item => item with
        {
            Status = MissionStatus.Running,
            Workflow = item.Workflow! with
            {
                Draft = revision.Files,
                PendingSoftwareAgentCall = review.Id,
                PendingSoftwareReservationCount = item.GenerationReservations!.Count,
                Steps = item.Workflow.Steps.Select(step => step.Id == review.Id
                    ? step with { Status = WorkflowStatus.Running } : step).ToArray(),
                SoftwareCorrections = scenario == "changed-receipt"
                    ? [.. item.Workflow.SoftwareCorrections.SkipLast(1),
                        item.Workflow.SoftwareCorrections.Last() with { ResponseReceipt = null }]
                    : item.Workflow.SoftwareCorrections,
                Revisions = scenario == "changed-revision" ? [revision with { Fingerprint = "changed" }] : [revision]
            }
        });
        using (fixture.Store.EnterGenerationScope(mission.Id, "Reviewer"))
        {
            var reservation = LlmGenerationScope.Reserve(TestSupport.Request(), CancellationToken.None);
            if (scenario == "ambiguous") LlmGenerationScope.MarkRequestMayHaveBeenSent(reservation);
            else LlmGenerationScope.MarkRequestDefinitelyNotSent(reservation);
        }
        var calls = fixture.Provider.Requests.Count;
        using var restarted = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        var interrupted = restarted.Get(mission.Id)!;
        Assert.Equal(MissionStatus.Cancelled, interrupted.Status);
        var savedReservations = interrupted.GenerationReservations!.ToArray();
        var savedCorrections = JsonSerializer.Serialize(interrupted.Workflow!.SoftwareCorrections, Json);
        var eligibility = restarted.GetResumeEligibility(mission.Id)!;
        Assert.Equal(scenario == "safe", eligibility.Available);
        Assert.Equal(scenario == "safe", restarted.Resume(mission.Id));
        var after = restarted.Get(mission.Id)!;
        Assert.Equal(savedReservations, after.GenerationReservations);
        Assert.Equal(savedCorrections, JsonSerializer.Serialize(after.Workflow!.SoftwareCorrections, Json));
        Assert.Equal(calls, fixture.Provider.Requests.Count);
        Assert.Empty(fixture.Sandbox.Calls);
        if (scenario == "safe")
        {
            Assert.Equal(MissionStatus.Queued, after.Status);
            Assert.Null(after.Workflow.PendingSoftwareAgentCall);
            Assert.False(after.SandboxApproved);
            Assert.Null(after.ApprovalFingerprint);
            var plans = new ExecutionPlanService(fixture.Workspaces, fixture.SandboxOptions,
                fixture.Options, restarted, new WorkflowImageProcess());
            var processor = new CollaborativeProcessor(restarted,
                new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
                fixture.Workspaces, plans, fixture.Sandbox, fixture.Options, reactSourceCheck: checker);
            await processor.ProcessAsync(mission.Id, default);
            var completed = restarted.Get(mission.Id)!;
            Assert.Equal(MissionStatus.GeneratedUnverified, completed.Status);
            Assert.Equal(calls + 1, fixture.Provider.Requests.Count);
            Assert.Equal(savedReservations.Length + 1, completed.GenerationReservations!.Count);
            Assert.Equal(savedReservations, completed.GenerationReservations.Take(savedReservations.Length));
            Assert.False(completed.SandboxApproved);
            Assert.Empty(fixture.Sandbox.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyAstCandidateResumesWithoutRepeatingNormalizationOrDeveloperReservations(bool currentPolicyRejectsCandidate)
    {
        var checker = new Checker { Deterministic = true };
        using var fixture = new WorkflowFixture(sourceCheck: checker, responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _)) return Correction(request, 1);
            var reply = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (reply["path"]?.GetValue<string>() == "src/App.tsx") reply["content"] = ReadyApp();
            return reply.ToJsonString();
        }, maxOutputTokens: 3072);
        var plans = new InterruptedStaticPlan(fixture.Plans);
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
            fixture.Workspaces, plans, fixture.Sandbox, fixture.Options, reactSourceCheck: checker);
        var mission = Submit(fixture);
        await Assert.ThrowsAsync<StudioValidationException>(() => processor.ProcessAsync(mission.Id, default));
        fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Failed });
        var before = fixture.Store.Get(mission.Id)!;
        var reservations = before.GenerationReservations!.ToArray();
        Assert.True(Assert.Single(before.Workflow!.SoftwareCorrections).Ready);
        Assert.Null(before.Workflow.Revisions);
        Assert.True(fixture.Store.Resume(mission.Id));

        if (currentPolicyRejectsCandidate)
        {
            checker.AdditionalIssues = HistoricalRoleDiagnostics();
            await processor.ProcessAsync(mission.Id, default);
            var corrected = fixture.Store.Get(mission.Id)!;
            var authorization = Assert.Single(corrected.Workflow!.ReadyCandidateResumptions);
            Assert.Equal(14, authorization.Diagnostics.Count);
            Assert.NotNull(authorization.RevalidatedAt);
            Assert.True(ReactCorrectionPatches.HasReadyResumption(
                corrected with { Workflow = corrected.Workflow with { Revisions = null } }, authorization, requireRejection: true));
            var child = Assert.Single(corrected.Workflow.SoftwareCorrections,
                item => item.RevalidatedFromId == authorization.ReadyCorrectionId);
            Assert.Equal(authorization.DeveloperCorrectionId, child.Id);
            Assert.NotNull(child.ResponseReceipt);
            Assert.Null(child.RetriedFromId);
            Assert.Equal(reservations.Length + 2, corrected.GenerationReservations!.Count);
            Assert.Equal(reservations, corrected.GenerationReservations.Take(reservations.Length));
            Assert.Equal(1, corrected.CorrectionAttempts);
            Assert.Equal(1, checker.RepairCalls);
            Assert.Equal(JsonSerializer.Serialize(before.Workflow.SoftwareCorrections, Json),
                JsonSerializer.Serialize(corrected.Workflow.SoftwareCorrections.Where(item => item.Id == authorization.ReadyCorrectionId), Json));
            Assert.Equal(MissionStatus.AwaitingApproval, corrected.Status);
            Assert.False(corrected.SandboxApproved);
            Assert.Empty(fixture.Sandbox.Calls);
            return;
        }

        await processor.ProcessAsync(mission.Id, default);

        var resumed = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, resumed.Status);
        Assert.Equal(1, checker.RepairCalls);
        Assert.Equal(reservations, resumed.GenerationReservations!.Take(reservations.Length));
        Assert.Equal(reservations.Length + 1, resumed.GenerationReservations!.Count);
        Assert.Equal("Reviewer", resumed.GenerationReservations!.Last().Role);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadyAstCandidateRefusesDivergenceOrIncompleteEvidence(int damage)
    {
        using var fixture = new WorkflowFixture(sourceCheck: new Checker { Deterministic = true },
            responseOverride: request =>
            {
                var reply = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
                if (reply["path"]?.GetValue<string>() == "src/App.tsx") reply["content"] = LargeApp();
                return reply.ToJsonString();
            });
        var mission = Submit(fixture);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var before = fixture.Store.Get(mission.Id)!;
        fixture.Store.Update(mission.Id, item => item with
        {
            Status = MissionStatus.Failed,
            Workflow = item.Workflow! with
            {
                Draft = damage == 0 ? item.Workflow.Draft!.Select(file =>
                    file.Path == "src/App.tsx" ? file with { Content = file.Content + "\n// divergent" } : file).ToArray() : item.Workflow.Draft,
                SoftwareCorrections = item.Workflow.SoftwareCorrections.Select(state => damage switch
                {
                    1 => state with { Original = [] },
                    2 => state with { CompletedGroups = 1 },
                    _ => state
                }).ToArray()
            }
        });
        Assert.False(fixture.Store.Resume(mission.Id));
        Assert.Equal(before.GenerationReservations, fixture.Store.Get(mission.Id)!.GenerationReservations);
    }

    [Theory]
    [InlineData(5, false, false)]
    [InlineData(6, true, false)]
    [InlineData(6, true, true)]
    public async Task ApplicationRoleDiagnosticsUseOnlyTheExistingBoundedDeveloperCorrection(int budget, bool sufficient, bool historicalDiagnostics)
    {
        var correctionCalls = 0;
        var diagnostics = historicalDiagnostics ? HistoricalRoleDiagnostics() : [];
        using var fixture = new WorkflowFixture(sourceCheck: new Checker { RoleIssues = diagnostics }, responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
            {
                correctionCalls++;
                Assert.Contains("REACT_APP_TEST_IMPORT", request.Messages.Last().Content);
                if (historicalDiagnostics)
                {
                    var forwarded = input.RootElement.GetProperty("diagnostics").EnumerateArray()
                        .Where(item => item.TryGetProperty("code", out _))
                        .Select(item => Assert.IsType<ReactSourceIssue>(item.Deserialize<ReactSourceIssue>(Json))).ToArray();
                    Assert.Equal(14, forwarded.Length);
                    Assert.Equal(diagnostics, forwarded);
                }
                return Correction(request, correctionCalls);
            }
            var reply = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (reply["path"]?.GetValue<string>() == "src/App.tsx")
                reply["content"] = "export default function App() { return <main>Mission</main> }\n// ROLE_BROKEN";
            return reply.ToJsonString();
        });
        var mission = Submit(fixture, budget);
        if (sufficient) await fixture.Processor.ProcessAsync(mission.Id, default);
        else await Assert.ThrowsAsync<StudioValidationException>(() => fixture.Processor.ProcessAsync(mission.Id, default));
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(sufficient ? 1 : 0, correctionCalls);
        Assert.Equal(sufficient ? 6 : 4, current.GenerationReservations!.Count);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    private static IReadOnlyList<ReactSourceIssue> HistoricalRoleDiagnostics() =>
    [
        new("src/App.tsx", 2, 43, "REACT_APP_TEST_IMPORT", "Application imports test runtime."),
        new("src/App.tsx", 26, 30, "REACT_APP_TEST_IMPORT", "Application imports Testing Library."),
        .. new[] { 30, 32, 35, 38, 41, 44, 47, 51 }.Select(line =>
            new ReactSourceIssue("src/App.tsx", line, line is 32 or 38 or 44 or 51 ? 3 : 1,
                "REACT_APP_TEST_DECLARATION", "Application embeds tests.")),
        new("src/App.test.tsx", 1, 1, "REACT_DUPLICATE_APPLICATION", "Test file duplicates application."),
        new("src/App.test.tsx", 1, 1, "REACT_TEST_APP_IMPORT", "Test file does not import App."),
        new("src/App.test.tsx", 28, 25, "REACT_TEST_APP_TARGET", "Test renders a local copy."),
        new("src/App.test.tsx", 1, 1, "REACT_TEST_APP_TARGET", "No render of imported App.")
    ];

    [Fact]
    public async Task InvalidReadyCandidateCannotCallDeveloperWithoutExplicitResumption()
    {
        var checker = new Checker { Deterministic = true };
        using var fixture = ReadyRevalidationFixture(checker);
        var before = await PersistReadyCandidate(fixture, checker);
        var requests = fixture.Provider.Requests.Count;

        await fixture.Processor.ProcessAsync(before.Id, default);
        fixture.Store.Update(before.Id, mission => mission with
        {
            Status = MissionStatus.Queued,
            Workflow = mission.Workflow! with
            {
                Steps = mission.Workflow.Steps.Select(step => step.Status is WorkflowStatus.Running or WorkflowStatus.Failed or WorkflowStatus.Cancelled
                    ? step with { Status = WorkflowStatus.Pending } : step).ToArray()
            }
        });
        var error = await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Processor.ProcessAsync(before.Id, default));
        Assert.Contains("explicit source-bound resumption", error.Message);
        var after = fixture.Store.Get(before.Id)!;
        Assert.Empty(after.Workflow!.ReadyCandidateResumptions);
        Assert.Equal(before.GenerationReservations, after.GenerationReservations);
        Assert.Equal(requests, fixture.Provider.Requests.Count);
        Assert.Equal(JsonSerializer.Serialize(before.Workflow!.SoftwareCorrections, Json),
            JsonSerializer.Serialize(after.Workflow.SoftwareCorrections, Json));
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyRevalidationCannotExceedBudgetOrCorrectionLimit(bool exhaustedAttempts)
    {
        var checker = new Checker { Deterministic = true };
        using var fixture = ReadyRevalidationFixture(checker);
        var before = await PersistReadyCandidate(fixture, checker, exhaustedAttempts ? 20 : 5);
        fixture.Store.Update(before.Id, mission => mission with
        {
            CorrectionAttempts = exhaustedAttempts ? fixture.Options.CorrectionLimitFor(mission) : mission.CorrectionAttempts
        });
        Assert.True(fixture.Store.Resume(before.Id));
        var requests = fixture.Provider.Requests.Count;
        var error = await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Processor.ProcessAsync(before.Id, default));
        Assert.Contains(exhaustedAttempts ? "cycle limit" : "budget", error.Message, StringComparison.OrdinalIgnoreCase);
        var after = fixture.Store.Get(before.Id)!;
        Assert.Single(after.Workflow!.SoftwareCorrections);
        Assert.Equal(14, Assert.Single(after.Workflow.ReadyCandidateResumptions).Diagnostics.Count);
        Assert.Equal(before.GenerationReservations, after.GenerationReservations);
        Assert.Equal(requests, fixture.Provider.Requests.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task ReadyRevalidationRefusesChangedOrMissingAuthorizationEvidence(int damage)
    {
        var checker = new Checker { Deterministic = true };
        using var fixture = ReadyRevalidationFixture(checker);
        var before = await PersistReadyCandidate(fixture, checker);
        Assert.True(fixture.Store.Resume(before.Id));
        var authorization = Assert.Single(fixture.Store.Get(before.Id)!.Workflow!.ReadyCandidateResumptions);
        fixture.Store.Update(before.Id, mission => mission with
        {
            Status = MissionStatus.Queued,
            Workflow = mission.Workflow! with
            {
                Version = damage == 4 ? mission.Workflow.Version + 1 : mission.Workflow.Version,
                Revisions = damage == 5 ? [new(mission.Workflow.Version, DateTimeOffset.UtcNow,
                    "Divergent revision", "changed", mission.Workflow.Draft!)] : mission.Workflow.Revisions,
                Agents = damage == 6 ? mission.Workflow.Agents.Select(agent => agent.Role == "Developer"
                    ? agent with { AllowedTools = [] } : agent).ToArray() : mission.Workflow.Agents,
                RequirePlanApproval = damage == 7 || mission.Workflow.RequirePlanApproval,
                Draft = damage == 0 ? mission.Workflow.Draft!.Select(file => file.Path == "src/App.tsx"
                    ? file with { Content = file.Content + "\n// divergent" } : file).ToArray() : mission.Workflow.Draft,
                ReadyCandidateResumptions = damage == 1 ? [] : [authorization with
                {
                    ReadyFingerprint = damage == 2 ? "changed" : authorization.ReadyFingerprint,
                    RevalidatedAt = damage == 3 ? DateTimeOffset.UtcNow : authorization.RevalidatedAt
                }]
            }
        });
        var requests = fixture.Provider.Requests.Count;
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Processor.ProcessAsync(before.Id, default));
        var after = fixture.Store.Get(before.Id)!;
        Assert.Equal(before.GenerationReservations, after.GenerationReservations);
        Assert.Equal(requests, fixture.Provider.Requests.Count);
        fixture.Store.Update(before.Id, mission => mission with { Status = MissionStatus.Failed });
        if (damage != 1) Assert.False(fixture.Store.Resume(before.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ReadyRevalidationSurvivesInterruptionWithoutDuplicatingConsumedCalls(int interruption)
    {
        using var cancellation = new CancellationTokenSource();
        var checker = new Checker { Deterministic = true };
        var corrections = 0;
        using var fixture = ReadyRevalidationFixture(checker, request =>
        {
            corrections++;
            if (interruption == 2)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            var answer = Correction(request, corrections);
            if (interruption == 3) cancellation.Cancel();
            return answer;
        });
        var before = await PersistReadyCandidate(fixture, checker);
        Assert.True(fixture.Store.Resume(before.Id));
        var authorization = Assert.Single(fixture.Store.Get(before.Id)!.Workflow!.ReadyCandidateResumptions);
        Assert.Null(authorization.RevalidatedAt);
        Assert.Empty(authorization.Diagnostics);
        Assert.Equal(before.GenerationReservations, fixture.Store.Get(before.Id)!.GenerationReservations);
        if (interruption is 0 or 1 or 4)
            checker.AfterCheck = () =>
            {
                var latest = fixture.Store.Get(before.Id)!.Workflow!.SoftwareCorrections.Last();
                if (interruption == 0 ||
                    interruption == 1 && latest.RevalidatedFromId is not null ||
                    interruption == 4 && latest.CompletedGroups > 0)
                {
                    checker.AfterCheck = null;
                    cancellation.Cancel();
                }
            };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Processor.ProcessAsync(before.Id, cancellation.Token));
        fixture.Store.Update(before.Id, mission => mission with { Status = MissionStatus.Failed });
        var interrupted = fixture.Store.Get(before.Id)!;
        var rejection = Assert.Single(interrupted.Workflow!.ReadyCandidateResumptions);
        Assert.Equal(authorization.Id, rejection.Id);
        Assert.Equal(14, rejection.Diagnostics.Count);
        Assert.NotNull(rejection.RevalidationFingerprint);
        Assert.False(ReactCorrectionPatches.HasReadyResumption(interrupted, authorization));
        Assert.Equal(JsonSerializer.Serialize(before.Workflow!.SoftwareCorrections, Json),
            JsonSerializer.Serialize(interrupted.Workflow.SoftwareCorrections.Where(item => item.Id == authorization.ReadyCorrectionId), Json));
        if (interruption is 0 or 1)
        {
            Assert.Equal(0, corrections);
            Assert.Equal(before.GenerationReservations, interrupted.GenerationReservations);
        }
        else
        {
            Assert.Equal(1, corrections);
            Assert.Equal(before.GenerationReservations!.Count + 1, interrupted.GenerationReservations!.Count);
        }
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        var eligible = interruption is 0 or 1 or 4;
        Assert.Equal(eligible, reopened.Resume(before.Id));
        if (eligible)
        {
            var processor = new CollaborativeProcessor(reopened,
                new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
                fixture.Workspaces, fixture.Plans, fixture.Sandbox, fixture.Options, reactSourceCheck: checker);
            await processor.ProcessAsync(before.Id, default);
            var completed = reopened.Get(before.Id)!;
            Assert.Equal(MissionStatus.AwaitingApproval, completed.Status);
            Assert.Equal(1, corrections);
            Assert.Equal(1, completed.CorrectionAttempts);
            Assert.Equal(authorization.Id, Assert.Single(completed.Workflow!.ReadyCandidateResumptions).Id);
            Assert.Single(completed.GenerationReservations!, item => item.CapacityId == authorization.DeveloperCorrectionId);
            Assert.Equal(1, checker.RepairCalls);
        }
        else Assert.Equal(1, corrections);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    private static WorkflowFixture ReadyRevalidationFixture(Checker checker, Func<LlmRequest, string>? correction = null) =>
        new(sourceCheck: checker, maxOutputTokens: 3072, responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
                return correction is null ? Correction(request, 1) : correction(request);
            var reply = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (reply["path"]?.GetValue<string>() == "src/App.tsx") reply["content"] = ReadyApp();
            return reply.ToJsonString();
        });

    private static async Task<MissionSnapshot> PersistReadyCandidate(WorkflowFixture fixture, Checker checker, int budget = 20)
    {
        var mission = Submit(fixture, budget);
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options), fixture.Workspaces,
            new InterruptedStaticPlan(fixture.Plans), fixture.Sandbox, fixture.Options, reactSourceCheck: checker);
        await Assert.ThrowsAsync<StudioValidationException>(() => processor.ProcessAsync(mission.Id, default));
        var failed = fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Failed });
        checker.AdditionalIssues = HistoricalRoleDiagnostics();
        return failed;
    }

    private sealed class UnavailableImageProcess : IContainerProcess
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls.Add(arguments);
            return Task.FromResult(new ContainerProcessResult(1, "Simulated Docker Engine unavailable."));
        }
    }

    private sealed class InterruptedStaticPlan(IExecutionPlanService inner) : IExecutionPlanService
    {
        private bool interrupted;
        public Task<ExecutionPlan> GetAsync(Guid id, CancellationToken token, int? version = null) =>
            inner.GetAsync(id, token, version);
        public Task<ExecutionPlan> GetStaticAsync(Guid id, CancellationToken token, int? version = null)
        {
            if (!interrupted)
            {
                interrupted = true;
                throw new StudioValidationException("Simulated interruption after AST candidate persistence.");
            }
            return inner.GetStaticAsync(id, token, version);
        }
    }

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
        if (!deterministic)
        {
            var decision = Assert.Single(repair.Decisions, item => item.StartsWith("Contract:", StringComparison.Ordinal));
            Assert.Contains("exact-patches selected: unique anchors fit", decision);
            Assert.DoesNotContain("compact exact-patches selected", decision);
            var prompt = Regex.Match(decision, @"prompt estimate=(\d+)/(\d+) chars");
            Assert.True(prompt.Success, decision);
            Assert.True(int.Parse(prompt.Groups[1].Value) <= int.Parse(prompt.Groups[2].Value), decision);
        }
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal(deterministic ? 5 : 6, current.GenerationReservations!.Count);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task OversizedCorrectionRefusalIsDiagnosedAndResumesWithUniqueSyntaxPatches()
    {
        var checker = new PreflightChecker();
        var correctionCalls = 0;
        using var fixture = new WorkflowFixture(sourceCheck: checker, maxOutputTokens: 3072,
            developerPromptPadding: 4500,
            responseOverride: request =>
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                if (input.RootElement.TryGetProperty("allowedPaths", out _))
                {
                    correctionCalls++;
                    return Correction(request, correctionCalls);
                }
                var response = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
                if (response["path"]?.GetValue<string>() == "src/App.test.tsx")
                    response["content"] = LargeRepeatedTest(60);
                return response.ToJsonString();
            });
        var mission = Submit(fixture);

        var refused = await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Processor.ProcessAsync(mission.Id, default));
        var failed = fixture.Store.Get(mission.Id)!;
        var diagnostic = Assert.Single(failed.Events, item => item.Step == "SoftwareCorrectionPreflight");
        Assert.Contains("contract=complete-files", diagnostic.Message);
        Assert.Contains("selection=", diagnostic.Message);
        Assert.Contains("prompt estimate=", diagnostic.Message);
        Assert.Contains("output estimate=", diagnostic.Message);
        Assert.Contains("20% reserve", diagnostic.Message);
        Assert.Contains("No model was called.", diagnostic.Message);
        var output = Regex.Match(diagnostic.Message, @"output estimate=(\d+)/(\d+) tokens");
        Assert.True(output.Success, diagnostic.Message);
        Assert.True(int.Parse(output.Groups[1].Value) > int.Parse(output.Groups[2].Value), diagnostic.Message);
        Assert.Contains("preflight refused", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(failed.Workflow!.SoftwareCorrections);
        Assert.Equal(4, failed.GenerationReservations!.Count);
        Assert.Equal(0, correctionCalls);

        var reservationsBeforeResume = failed.GenerationReservations.ToArray();
        checker.UsePrimarySyntaxDiagnostics = true;
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.True(reopened.Resume(mission.Id));
        var processor = new CollaborativeProcessor(reopened,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options), fixture.Workspaces,
            fixture.Plans, fixture.Sandbox, fixture.Options, reactSourceCheck: checker);
        await processor.ProcessAsync(mission.Id, default);

        var resumed = reopened.Get(mission.Id)!;
        var correction = Assert.Single(resumed.Workflow!.SoftwareCorrections);
        var finalReservations = resumed.GenerationReservations!;
        Assert.True(correction.Ready);
        Assert.Contains(correction.Decisions, decision => decision.Contains("Contract: exact-patches", StringComparison.Ordinal));
        Assert.Equal(reservationsBeforeResume, finalReservations.Take(reservationsBeforeResume.Length));
        Assert.Equal(6, finalReservations.Count);
        Assert.Equal(1, correctionCalls);
        Assert.Equal(MissionStatus.AwaitingApproval, resumed.Status);
        var decision = Assert.Single(correction.Decisions, item =>
            item.StartsWith("Contract: exact-patches;", StringComparison.Ordinal));
        Assert.True(decision.Contains("compact exact-patches selected", StringComparison.Ordinal), decision);
        var fullPrompt = Regex.Match(decision, @"full-context prompt estimate (\d+)/(\d+) chars");
        Assert.True(fullPrompt.Success, decision);
        Assert.True(int.Parse(fullPrompt.Groups[1].Value) > int.Parse(fullPrompt.Groups[2].Value), decision);
        var prompt = Regex.Match(decision, @"prompt estimate=(\d+)/(\d+) chars");
        Assert.True(prompt.Success, decision);
        Assert.True(int.Parse(prompt.Groups[1].Value) <= int.Parse(prompt.Groups[2].Value), decision);
        var patchOutput = Regex.Match(decision, @"output estimate=(\d+)/(\d+) tokens");
        Assert.True(patchOutput.Success, decision);
        Assert.True(int.Parse(patchOutput.Groups[1].Value) <= int.Parse(patchOutput.Groups[2].Value), decision);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task RepeatedSyntaxAnchorRefusesCorrectionWithoutCallingDeveloper()
    {
        var checker = new PreflightChecker { UsePrimarySyntaxDiagnostics = true };
        var correctionCalls = 0;
        using var fixture = new WorkflowFixture(sourceCheck: checker, maxOutputTokens: 3072,
            responseOverride: request =>
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                if (input.RootElement.TryGetProperty("allowedPaths", out _))
                {
                    correctionCalls++;
                    return Correction(request, correctionCalls);
                }
                var response = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
                if (response["path"]?.GetValue<string>() == "src/App.test.tsx")
                    response["content"] = LargeRepeatedTest(60) + "\nconst brokenValue = BROKEN_TOKEN;";
                return response.ToJsonString();
            });
        var mission = Submit(fixture);

        var error = await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Processor.ProcessAsync(mission.Id, default));
        var failed = fixture.Store.Get(mission.Id)!;
        var diagnostic = Assert.Single(failed.Events, item => item.Step == "SoftwareCorrectionPreflight");
        Assert.Contains("contract=complete-files", diagnostic.Message);
        Assert.Contains("no safe, unique patch anchors", diagnostic.Message);
        Assert.Contains("No model was called.", diagnostic.Message);
        Assert.Contains("preflight refused", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, failed.GenerationReservations!.Count);
        Assert.Empty(failed.Workflow!.SoftwareCorrections);
        Assert.Equal(0, correctionCalls);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public void SyntaxPatchTargetsRejectRepeatedAnchorsAndIgnoreParserCascades()
    {
        const string repeated = "const value = 'broken;\nconst separator = 1;\nconst value = 'broken;";
        Assert.Null(ReactCorrectionPatches.CreatePrimarySyntaxTargets("src/App.test.tsx", repeated,
        [
            new("src/App.test.tsx", 1, 1, "TS1002", "Primary syntax diagnostic"),
            new("src/App.test.tsx", 3, 1, "TS1002", "Primary syntax diagnostic")
        ]));

        const string unique = "const value = 'broken;\nconst other = 1;";
        var targets = ReactCorrectionPatches.CreatePrimarySyntaxTargets("src/App.test.tsx", unique,
        [
            new("src/App.test.tsx", 1, 1, "TS1002", "Primary syntax diagnostic"),
            new("src/App.test.tsx", 2, 1, "TS1005", "Parser cascade"),
            new("src/App.test.tsx", 2, 1, "REACT_JEST", "Independent runner diagnostic")
        ]);
        Assert.Equal(new[]
        {
            new ReactPatchTarget("src/App.test.tsx", "const value = 'broken;"),
            new ReactPatchTarget("src/App.test.tsx", "const other = 1;")
        }, Assert.IsType<ReactPatchTarget[]>(targets));
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
        using var fixture = new WorkflowFixture(sourceCheck: new Checker());
        var provider = new DeliveryTrackingProvider((request, cancellationToken) =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
                throw new LlmTimeoutException("Ambiguous timeout");
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["path"]?.GetValue<string>() == "src/App.tsx") result["content"] = LargeApp();
            return Task.FromResult(result.ToJsonString());
        });
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([provider]), fixture.Options), fixture.Workspaces,
            fixture.Plans, fixture.Sandbox, fixture.Options, reactSourceCheck: new Checker());
        var mission = Submit(fixture);
        await Assert.ThrowsAsync<LlmTimeoutException>(() => processor.ProcessAsync(mission.Id, default));
        fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Failed });
        var before = provider.Requests.Count;
        var checkpoint = fixture.Store.Get(mission.Id)!.Workflow!.SoftwareCorrections.Last();
        Assert.False(fixture.Store.Resume(mission.Id));
        Assert.True(checkpoint.InFlight);
        Assert.Equal(ReactSoftwareCorrectionCallState.MayHaveBeenSent, checkpoint.CallState);
        Assert.Null(checkpoint.ResponseReceipt);
        var attempt = Assert.IsType<ReactCorrectionCallAttempt>(checkpoint.ActiveAttempt);
        Assert.Equal(mission.Id, attempt.MissionId);
        Assert.Equal(checkpoint.Id, attempt.CorrectionId);
        Assert.Equal(attempt.CallId, Assert.Single(provider.Requests, request =>
            request.CallId == attempt.CallId).CallId);
        Assert.Equal(attempt.CallId, Assert.Single(fixture.Store.Get(mission.Id)!.GenerationReservations!,
            item => item.Id == checkpoint.ActiveReservationId).CallId);
        Assert.False(fixture.Store.Get(mission.Id)!.HasWorkspace);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.False(reopened.Resume(mission.Id));
        Assert.Equal(before, provider.Requests.Count);
        Assert.Equal(before, reopened.Get(mission.Id)!.GenerationReservations!.Count);
    }

    [Fact]
    public async Task RejectedResponseCanBeExplicitlyRetriedWithFreshReservationAndDiagnosticDelta()
    {
        var correctionCalls = 0;
        string? rejectedResponse = null;
        using var fixture = new WorkflowFixture(sourceCheck: new RetryDiagnosticsChecker(), responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
            {
                correctionCalls++;
                if (correctionCalls == 1)
                {
                    rejectedResponse = PatchResponse(request, (_, original) =>
                        original.Replace("BROKEN", "0", StringComparison.Ordinal) + "\nconst newIssue = NEW_ERROR;");
                    return rejectedResponse;
                }
                return PatchResponse(request, (_, original) => original
                    .Replace("BROKEN", "0", StringComparison.Ordinal)
                    .Replace("PRESERVED_ERROR", "0", StringComparison.Ordinal));
            }
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["path"]?.GetValue<string>() == "src/App.tsx")
                result["content"] = LargeRetryApp();
            return result.ToJsonString();
        });
        var mission = Submit(fixture);

        await ProcessAndPersistFailure<StudioValidationException>(fixture, mission.Id);
        var failed = fixture.Store.Get(mission.Id)!;
        var rejected = Assert.Single(failed.Workflow!.SoftwareCorrections);
        Assert.Equal(ReactSoftwareCorrectionCallState.ValidationRejected, rejected.CallState);
        Assert.NotNull(rejected.ResponseReceipt);
        Assert.Equal(rejected.ActiveReservationId, rejected.ResponseReceipt!.ReservationId);
        Assert.Equal("TS2322", Assert.Single(rejected.ValidationRejection!.ExistingDiagnostics).Code);
        Assert.Equal("TS1005", Assert.Single(rejected.ValidationRejection!.NewDiagnostics).Code);
        Assert.Equal("TS2304", Assert.Single(rejected.ValidationRejection.CorrectedDiagnostics).Code);
        var receipt = rejected.ResponseReceipt;
        var attempt = Assert.IsType<ReactCorrectionCallAttempt>(receipt.Attempt);
        Assert.Equal(mission.Id, attempt.MissionId);
        Assert.Equal(failed.Workflow.WorkflowId, attempt.WorkflowId);
        Assert.Equal(failed.Workflow.Version, attempt.WorkflowVersion);
        Assert.Equal(rejected.Id, attempt.CorrectionId);
        Assert.Equal(0, attempt.GroupIndex);
        Assert.Equal(ReactCorrectionPatches.SourceHash(rejected.Candidate), attempt.SourceFingerprint);
        Assert.Equal(64, attempt.PlanFingerprint.Length);
        Assert.Equal(64, attempt.CallFingerprint.Length);
        Assert.Equal(64, receipt.ResponseFingerprint!.Length);
        Assert.Equal(ReactCorrectionPatches.ResponseFingerprint(rejectedResponse!), receipt.ResponseFingerprint);
        Assert.NotEqual(Guid.Empty, attempt.CallId);
        var sentCall = Assert.Single(fixture.Provider.Requests, item => item.CallId == attempt.CallId);
        Assert.Equal(attempt.CallFingerprint, ReactCorrectionPatches.RequestFingerprint(sentCall));
        var receiptReservation = Assert.Single(failed.GenerationReservations!,
            item => item.Id == receipt.ReservationId);
        Assert.Equal(attempt.CallId, receiptReservation.CallId);
        var validation = rejected.ValidationRejection;
        Assert.NotEqual(Guid.Empty, validation.ValidationId);
        Assert.Equal(64, validation.CandidateFingerprint!.Length);
        Assert.Equal(64, validation.ResultFingerprint!.Length);
        Assert.Equal(ReactCorrectionPatches.ValidationFingerprint(validation), validation.ResultFingerprint);
        var applied = ReactCorrectionPatches.Apply(rejectedResponse!, rejected.Calls[0],
            ReactCorrectionPatches.AllowedPaths(rejected.Calls[0]),
            rejected.Candidate.ToDictionary(file => file.Path, StringComparer.Ordinal));
        var evaluatedCandidate = rejected.Candidate.Select(file =>
            applied.Files.SingleOrDefault(updated => updated.Path == file.Path) ?? file).ToArray();
        Assert.Equal(ReactCorrectionPatches.SourceHash(evaluatedCandidate), validation.CandidateFingerprint);
        var validationEvent = Assert.Single(failed.Events, item => item.Step == "SoftwareCorrectionValidation");
        Assert.Contains("Synthetic preserved diagnostic", validationEvent.Message);
        Assert.Contains("NEW_ERROR", validationEvent.Message);
        Assert.Contains("Undefined BROKEN", validationEvent.Message);
        Assert.Contains(attempt.CallId.ToString(), validationEvent.Message);
        Assert.Contains(validation.ValidationId.ToString(), validationEvent.Message);
        var reservationCount = failed.GenerationReservations!.Count;
        var oldReservation = rejected.ActiveReservationId;
        Assert.True(ReactCorrectionPatches.HasRetryableValidationRejection(failed, rejected));
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        var persisted = reopened.Get(mission.Id)!;
        var persistedCorrection = Assert.Single(persisted.Workflow!.SoftwareCorrections);
        Assert.True(ReactCorrectionPatches.HasRetryableValidationRejection(persisted, persistedCorrection));
        var changedRevision = failed with
        {
            Workflow = failed.Workflow with { Version = failed.Workflow.Version + 1 }
        };
        Assert.False(ReactCorrectionPatches.HasRetryableValidationRejection(changedRevision, rejected));
        Assert.False(ReactCorrectionPatches.HasRetryableValidationRejection(
            failed with { Id = Guid.NewGuid() }, rejected));
        var changedReservation = failed with
        {
            GenerationReservations = failed.GenerationReservations.Select(item => item.Id == receipt.ReservationId
                ? item with { CallId = Guid.NewGuid() } : item).ToArray()
        };
        Assert.False(ReactCorrectionPatches.HasRetryableValidationRejection(changedReservation, rejected));
        var changedCandidate = rejected with
        {
            ValidationRejection = validation with { CandidateFingerprint = new string('0', 64) }
        };
        Assert.False(ReactCorrectionPatches.HasRetryableValidationRejection(failed, changedCandidate));
        Assert.True(reopened.Resume(mission.Id));
        var resumedProcessor = new CollaborativeProcessor(reopened,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options), fixture.Workspaces,
            fixture.Plans, fixture.Sandbox, fixture.Options, reactSourceCheck: new RetryDiagnosticsChecker());
        await resumedProcessor.ProcessAsync(mission.Id, default);

        var completed = reopened.Get(mission.Id)!;
        var checkpoints = completed.Workflow!.SoftwareCorrections;
        var reservations = completed.GenerationReservations!;
        Assert.Equal(2, correctionCalls);
        Assert.Equal(2, checkpoints.Count);
        Assert.NotEqual(checkpoints[0].Id, checkpoints[1].Id);
        Assert.Equal(checkpoints[0].Id, checkpoints[1].RetriedFromId);
        Assert.Equal(oldReservation, Assert.Single(reservations,
            item => item.CapacityId == checkpoints[0].Id).Id);
        var retryReservation = Assert.Single(reservations,
            item => item.CapacityId == checkpoints[1].Id);
        Assert.NotEqual(oldReservation, retryReservation.Id);
        var retriedAttempt = Assert.IsType<ReactCorrectionCallAttempt>(checkpoints[1].ActiveAttempt);
        Assert.NotEqual(attempt.CallId, retriedAttempt.CallId);
        Assert.Equal(retriedAttempt.CallId, retryReservation.CallId);
        Assert.Equal(reservationCount + 2, reservations.Count);
        Assert.True(checkpoints[1].Ready);
        Assert.DoesNotContain("BROKEN", checkpoints[1].Candidate.Single(file => file.Path == "src/App.tsx").Content);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task ResponseWithoutAnEvaluatedCandidateCannotAuthorizeRetry()
    {
        var correctionCalls = 0;
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
            {
                correctionCalls++;
                return "{}";
            }
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["path"]?.GetValue<string>() == "src/App.tsx")
                result["content"] = LargeApp();
            return result.ToJsonString();
        });
        var mission = Submit(fixture);

        await ProcessAndPersistFailure<StudioValidationException>(fixture, mission.Id);

        var failed = fixture.Store.Get(mission.Id)!;
        var correction = Assert.Single(failed.Workflow!.SoftwareCorrections);
        Assert.Equal(ReactSoftwareCorrectionCallState.ValidationRejected, correction.CallState);
        Assert.NotNull(correction.ResponseReceipt?.Attempt);
        Assert.NotNull(correction.ValidationRejection);
        Assert.Null(correction.ValidationRejection.CandidateFingerprint);
        Assert.False(ReactCorrectionPatches.HasRetryableValidationRejection(failed, correction));
        Assert.False(fixture.Store.Resume(mission.Id));
        Assert.Equal(1, correctionCalls);
    }

    [Fact]
    public async Task RetryKeepsValidatedGroupsAndRegeneratesOnlyTheRejectedGroup()
    {
        var correctionCalls = 0;
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
            {
                correctionCalls++;
                return correctionCalls == 2 ? UnchangedResponse(request) : Correction(request, correctionCalls);
            }
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["path"]?.GetValue<string>() is "src/App.test.tsx" or "src/model.ts")
                result["content"] = result["content"]!.GetValue<string>() + "\nconst brokenValue = BROKEN;";
            return result.ToJsonString();
        });
        var mission = Submit(fixture);

        await ProcessAndPersistFailure<StudioValidationException>(fixture, mission.Id);
        var failed = fixture.Store.Get(mission.Id)!;
        var rejected = Assert.Single(failed.Workflow!.SoftwareCorrections);
        Assert.Equal(1, rejected.CompletedGroups);
        Assert.DoesNotContain("BROKEN", rejected.Candidate.Single(file => file.Path == "src/App.test.tsx").Content);
        Assert.Contains("BROKEN", rejected.Candidate.Single(file => file.Path == "src/model.ts").Content);
        var reservationCount = failed.GenerationReservations!.Count;
        var originalCorrectionId = rejected.Id;

        Assert.True(fixture.Store.Resume(mission.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);

        var completed = fixture.Store.Get(mission.Id)!;
        var retry = Assert.Single(completed.Workflow!.SoftwareCorrections, item => item.RetriedFromId == originalCorrectionId);
        Assert.Single(retry.Calls);
        using var retryInput = JsonDocument.Parse(retry.Calls[0].Messages.Last().Content);
        Assert.Equal(new[] { "src/model.ts" },
            retryInput.RootElement.GetProperty("allowedPaths").Deserialize<string[]>());
        Assert.DoesNotContain("BROKEN", retry.Candidate.Single(file => file.Path == "src/App.test.tsx").Content);
        Assert.Contains("software correction 1", retry.Candidate.Single(file => file.Path == "src/App.test.tsx").Content);
        Assert.DoesNotContain("BROKEN", retry.Candidate.Single(file => file.Path == "src/model.ts").Content);
        Assert.Equal(reservationCount + 2, completed.GenerationReservations!.Count);
        Assert.Equal(2, completed.GenerationReservations.Count(item => item.CapacityId == originalCorrectionId));
        Assert.Single(completed.GenerationReservations, item => item.CapacityId == retry.Id);
        Assert.Equal(3, correctionCalls);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task RejectedResponseRetriesStopAtCorrectionLimit()
    {
        var correctionCalls = 0;
        using var fixture = new WorkflowFixture(corrections: 2, sourceCheck: new Checker(), responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
            {
                correctionCalls++;
                return PatchResponse(request, (_, original) =>
                    original.Replace("BROKEN", "BROKEN_RETRY", StringComparison.Ordinal));
            }
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["path"]?.GetValue<string>() == "src/App.tsx") result["content"] = LargeApp();
            return result.ToJsonString();
        });
        var mission = Submit(fixture);

        await ProcessAndPersistFailure<StudioValidationException>(fixture, mission.Id);
        Assert.True(fixture.Store.Resume(mission.Id));
        await ProcessAndPersistFailure<StudioValidationException>(fixture, mission.Id);

        var failed = fixture.Store.Get(mission.Id)!;
        Assert.Equal(2, failed.CorrectionAttempts);
        Assert.Equal(2, correctionCalls);
        Assert.Equal(6, failed.GenerationReservations!.Count);
        Assert.False(fixture.Store.Resume(mission.Id));
        Assert.Equal(2, fixture.Provider.Requests.Count(request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            return input.RootElement.TryGetProperty("allowedPaths", out _);
        }));
    }

    [Fact]
    public async Task RejectedCheckpointWithoutPersistedReceiptCannotBeResumed()
    {
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
                return UnchangedResponse(request);
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["path"]?.GetValue<string>() == "src/App.tsx") result["content"] = LargeApp();
            return result.ToJsonString();
        });
        var mission = Submit(fixture);
        await ProcessAndPersistFailure<StudioValidationException>(fixture, mission.Id);
        var reservationCount = fixture.Store.Get(mission.Id)!.GenerationReservations!.Count;
        fixture.Store.Update(mission.Id, current => current with
        {
            Workflow = current.Workflow! with
            {
                SoftwareCorrections = [.. current.Workflow!.SoftwareCorrections.Select(item => item with
                {
                    ResponseReceipt = null,
                    ValidationRejection = null
                })]
            }
        });

        var oldCheckpoint = fixture.Store.Get(mission.Id)!.Workflow!.SoftwareCorrections.Last();
        Assert.False(ReactCorrectionPatches.HasRetryableValidationRejection(fixture.Store.Get(mission.Id)!, oldCheckpoint));
        Assert.False(fixture.Store.Resume(mission.Id));
        Assert.Equal(reservationCount, fixture.Store.Get(mission.Id)!.GenerationReservations!.Count);
    }

    [Fact]
    public async Task ResponseReceiptAloneDoesNotAuthorizeRetryAfterInterruption()
    {
        using var cancellation = new CancellationTokenSource();
        var correctionCalls = 0;
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
            {
                correctionCalls++;
                cancellation.Cancel();
                return UnchangedResponse(request);
            }
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["path"]?.GetValue<string>() == "src/App.tsx") result["content"] = LargeApp();
            return result.ToJsonString();
        });
        var mission = Submit(fixture);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Processor.ProcessAsync(mission.Id, cancellation.Token));
        fixture.Store.Update(mission.Id, item => item with { Status = MissionStatus.Failed });
        var interrupted = fixture.Store.Get(mission.Id)!;
        var checkpoint = Assert.Single(interrupted.Workflow!.SoftwareCorrections);
        Assert.True(checkpoint.InFlight);
        Assert.Equal(ReactSoftwareCorrectionCallState.ResponseReceived, checkpoint.CallState);
        Assert.NotNull(checkpoint.ResponseReceipt);
        Assert.Null(checkpoint.ValidationRejection);
        var attempt = Assert.IsType<ReactCorrectionCallAttempt>(checkpoint.ResponseReceipt.Attempt);
        Assert.Equal(mission.Id, attempt.MissionId);
        Assert.Equal(checkpoint.Id, attempt.CorrectionId);
        Assert.Equal(attempt.CallId, checkpoint.ActiveAttempt!.CallId);
        Assert.Equal(attempt.CallId, Assert.Single(interrupted.GenerationReservations!,
            item => item.Id == checkpoint.ResponseReceipt.ReservationId).CallId);
        var requestCount = fixture.Provider.Requests.Count;
        Assert.False(fixture.Store.Resume(mission.Id));
        Assert.Equal(requestCount, fixture.Provider.Requests.Count);
        Assert.Equal(1, correctionCalls);
    }

    [Fact]
    public async Task RetryCannotSpendBudgetReservedForReviewer()
    {
        var correctionCalls = 0;
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
            {
                correctionCalls++;
                return PatchResponse(request, (_, original) =>
                    original.Replace("BROKEN", "BROKEN_RETRY", StringComparison.Ordinal));
            }
            var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (result["path"]?.GetValue<string>() == "src/App.tsx") result["content"] = LargeApp();
            return result.ToJsonString();
        });
        var mission = Submit(fixture, budget: 6);

        await ProcessAndPersistFailure<StudioValidationException>(fixture, mission.Id);
        var failed = fixture.Store.Get(mission.Id)!;
        Assert.Equal(5, failed.GenerationReservations!.Count);
        Assert.True(fixture.Store.Resume(mission.Id));
        var retryError = await ProcessAndPersistFailure<StudioValidationException>(fixture, mission.Id);

        Assert.Contains("budget", retryError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, fixture.Store.Get(mission.Id)!.GenerationReservations!.Count);
        Assert.Equal(1, correctionCalls);
        Assert.Equal(1, fixture.Provider.Requests.Count(request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            return input.RootElement.TryGetProperty("allowedPaths", out _);
        }));
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

    [Fact]
    public async Task PlannerMemoryPreflightFailureCanResumeOnlyWithPersistedNotSentProof()
    {
        using var fixture = new WorkflowFixture();
        var mission = BeginPendingPlannerCall(fixture);
        var library = new MemoryGuardLibrary(blockMemory: true);
        var calls = 0;
        using var handler = new StubHttpHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(StubHttpHandler.Json("""{"done":true,"message":{"content":"retry"}}"""));
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new(), library: library);

        using (fixture.Store.EnterGenerationScope(mission.Id, "Planner"))
            await Assert.ThrowsAsync<LlmMemoryException>(() =>
                provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));

        var failed = FailPendingPlannerCall(fixture, mission.Id);
        Assert.Equal(SoftwareAgentCallDeliveryState.DefinitelyNotSent,
            failed.Workflow!.PendingSoftwareCallDeliveryState);
        Assert.Single(failed.GenerationReservations!);
        Assert.Equal(0, calls);

        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        var results = new bool[8];
        Parallel.For(0, results.Length, index => results[index] = reopened.Resume(mission.Id));
        Assert.Single(results, resumed => resumed);
        var resumedMission = reopened.Get(mission.Id)!;
        Assert.Single(resumedMission.GenerationReservations!);
        Assert.Null(resumedMission.Workflow!.PendingSoftwareAgentCall);

        library.BlockMemory = false;
        reopened.Update(mission.Id, item => item with
        {
            Status = MissionStatus.Running,
            Workflow = item.Workflow! with
            {
                PendingSoftwareAgentCall = "plan",
                PendingSoftwareReservationCount = item.GenerationReservations!.Count,
                Steps = item.Workflow.Steps.Select(step => step.Kind == "Plan"
                    ? step with { Status = WorkflowStatus.Running }
                    : step).ToArray()
            }
        });
        using (reopened.EnterGenerationScope(mission.Id, "Planner"))
            Assert.Equal("retry", await provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));
        Assert.Equal(2, library.GuardCalls);
        Assert.Equal(1, calls);
        Assert.Collection(reopened.Get(mission.Id)!.GenerationReservations!, _ => { }, _ => { });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OllamaCallThatMayHaveBeenSentCannotResume(bool timeout)
    {
        using var fixture = new WorkflowFixture();
        var mission = BeginPendingPlannerCall(fixture);
        var calls = 0;
        using var handler = new StubHttpHandler(async (_, token) =>
        {
            calls++;
            if (timeout)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            else
                throw new HttpRequestException("Simulated connection failure after dispatch.");
            return StubHttpHandler.Json("""{"done":true,"message":{"content":"unreachable"}}""");
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new() { RequestTimeoutSeconds = 1 });

        using (fixture.Store.EnterGenerationScope(mission.Id, "Planner"))
        {
            if (timeout)
                await Assert.ThrowsAsync<LlmTimeoutException>(() =>
                    provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));
            else
                await Assert.ThrowsAsync<LlmProviderException>(() =>
                    provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));
        }

        var failed = FailPendingPlannerCall(fixture, mission.Id);
        Assert.Equal(SoftwareAgentCallDeliveryState.MayHaveBeenSent,
            failed.Workflow!.PendingSoftwareCallDeliveryState);
        Assert.Equal(1, calls);
        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        var saved = File.ReadAllBytes(Path.Combine(fixture.Options.DataDirectory, "missions", mission.Id.ToString("N") + ".json"));
        var eligibility = reopened.GetResumeEligibility(mission.Id)!;
        Assert.False(eligibility.Available);
        Assert.Contains("peut avoir été envoyé", eligibility.Reason);
        Assert.False(reopened.Resume(mission.Id));
        Assert.Single(reopened.Get(mission.Id)!.GenerationReservations!);
        Assert.Equal(saved, File.ReadAllBytes(Path.Combine(fixture.Options.DataDirectory, "missions", mission.Id.ToString("N") + ".json")));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void InterruptedCallWithUnknownDeliveryStateCannotResume()
    {
        using var fixture = new WorkflowFixture();
        var mission = BeginPendingPlannerCall(fixture);
        using (fixture.Store.EnterGenerationScope(mission.Id, "Planner"))
            LlmGenerationScope.Reserve(TestSupport.Request(), CancellationToken.None);
        FailPendingPlannerCall(fixture, mission.Id);
        Assert.False(fixture.Store.Resume(mission.Id));
        Assert.Single(fixture.Store.Get(mission.Id)!.GenerationReservations!);
    }

    [Fact]
    public void PendingCallWithoutReservationProofCannotResume()
    {
        using var fixture = new WorkflowFixture();
        var mission = BeginPendingPlannerCall(fixture);
        FailPendingPlannerCall(fixture, mission.Id);
        Assert.False(fixture.Store.Resume(mission.Id));
        Assert.Empty(fixture.Store.Get(mission.Id)!.GenerationReservations!);
    }

    [Fact]
    public async Task PreflightFailureDoesNotRefundReservationOrBypassExhaustedBudget()
    {
        using var fixture = new WorkflowFixture();
        var mission = Submit(fixture, budget: 1);
        fixture.Store.Update(mission.Id, item => item with
        {
            Status = MissionStatus.Running,
            Workflow = item.Workflow! with
            {
                PendingSoftwareAgentCall = "plan",
                PendingSoftwareReservationCount = item.GenerationReservations!.Count,
                Steps = item.Workflow.Steps.Select(step => step.Kind == "Plan"
                    ? step with { Status = WorkflowStatus.Running }
                    : step).ToArray()
            }
        });
        var library = new MemoryGuardLibrary(blockMemory: true);
        using var handler = new StubHttpHandler((_, _) =>
            throw new InvalidOperationException("The preflight-blocked request must not reach HTTP."));
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new(), library: library);
        using (fixture.Store.EnterGenerationScope(mission.Id, "Planner"))
            await Assert.ThrowsAsync<LlmMemoryException>(() =>
                provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));

        FailPendingPlannerCall(fixture, mission.Id);
        Assert.False(fixture.Store.Resume(mission.Id));
        Assert.Single(fixture.Store.Get(mission.Id)!.GenerationReservations!);
    }

    [Fact]
    public void LegacyPendingCallWithoutDeliveryProofCannotResume()
    {
        using var fixture = new WorkflowFixture();
        var mission = BeginPendingPlannerCall(fixture);
        using (fixture.Store.EnterGenerationScope(mission.Id, "Planner"))
            LlmGenerationScope.Reserve(TestSupport.Request(), CancellationToken.None);
        FailPendingPlannerCall(fixture, mission.Id);

        var path = Path.Combine(fixture.Options.DataDirectory, "missions", mission.Id.ToString("N") + ".json");
        var snapshot = JsonNode.Parse(File.ReadAllText(path))!;
        var workflow = snapshot["workflow"]!.AsObject();
        workflow.Remove("pendingSoftwareCallReservationId");
        workflow.Remove("pendingSoftwareCallReservationCount");
        workflow.Remove("pendingSoftwareCallDeliveryState");
        File.WriteAllText(path, snapshot.ToJsonString());

        using var reopened = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(SoftwareAgentCallDeliveryState.Unknown,
            reopened.Get(mission.Id)!.Workflow!.PendingSoftwareCallDeliveryState);
        Assert.False(reopened.Resume(mission.Id));
        Assert.Single(reopened.Get(mission.Id)!.GenerationReservations!);
    }

    private static MissionSnapshot BeginPendingPlannerCall(WorkflowFixture fixture)
    {
        var mission = Submit(fixture);
        return fixture.Store.Update(mission.Id, item => item with
        {
            Status = MissionStatus.Running,
            Workflow = item.Workflow! with
            {
                PendingSoftwareAgentCall = "plan",
                PendingSoftwareReservationCount = item.GenerationReservations!.Count,
                Steps = item.Workflow.Steps.Select(step => step.Kind == "Plan"
                    ? step with { Status = WorkflowStatus.Running }
                    : step).ToArray()
            }
        });
    }

    private static MissionSnapshot FailPendingPlannerCall(WorkflowFixture fixture, Guid missionId) =>
        fixture.Store.Update(missionId, item => item with
        {
            Status = MissionStatus.Failed,
            Error = "Simulated Planner failure.",
            Workflow = item.Workflow! with
            {
                Steps = item.Workflow.Steps.Select(step => step.Kind == "Plan"
                    ? step with { Status = WorkflowStatus.Failed, Error = "Simulated Planner failure." }
                    : step).ToArray()
            }
        });

    private static MissionSnapshot Submit(WorkflowFixture fixture, int budget = 20) =>
        fixture.Store.Submit(new("Display initial value 0", MissionKind.Project, TemplateId: MissionReactTemplate.Id,
            MaxTotalGenerations: budget, AutomaticReactCorrections: true));

    private static async Task<TException> ProcessAndPersistFailure<TException>(
        WorkflowFixture fixture, Guid missionId) where TException : Exception
    {
        var exception = await Assert.ThrowsAsync<TException>(() =>
            fixture.Processor.ProcessAsync(missionId, default));
        fixture.Store.Update(missionId, mission => mission with
        {
            Status = MissionStatus.Failed,
            Error = exception.Message,
            SandboxApproved = false,
            ApprovalFingerprint = null
        });
        return exception;
    }

    private static string LargeApp() => string.Join("\n", Enumerable.Range(0, 100).Select(index =>
        index == 50 ? "const broken = BROKEN;" : $"// synthetic unique padding line {index}")) +
        "\nexport default function App() { return <main>Mission</main> }";

    private static string ReadyApp() => string.Join("\n", Enumerable.Range(0, 55).Select(index =>
        index == 20 ? "const broken = BROKEN;" : $"// {index}")) +
        "\nexport default function App() { return <main>Mission</main> }";

    private static string LargeRetryApp() => string.Join("\n", Enumerable.Range(0, 100).Select(index =>
        index == 50 ? "const broken = BROKEN;" :
        index == 51 ? "const existing = PRESERVED_ERROR;" : $"// synthetic unique padding line {index}")) +
        "\nexport default function App() { return <main>Mission</main> }";

    private static string LargeRepeatedTest(int repeatedLines = 35) => string.Join("\n",
        Enumerable.Repeat("test('[C1] simulated assertion', () => expect(1).toBe(1));", repeatedLines)
            .Append("const brokenValue = BROKEN_TOKEN;"));

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

    private static string PatchResponse(LlmRequest request, Func<string, string, string> replace)
    {
        using var input = JsonDocument.Parse(request.Messages.Last().Content);
        return JsonSerializer.Serialize(new
        {
            patches = input.RootElement.GetProperty("patchTargets").EnumerateArray().Select(target =>
            {
                var path = target.GetProperty("path").GetString()!;
                var original = target.GetProperty("original").GetString()!;
                return new { path, original, replacement = replace(path, original) };
            })
        }, Json);
    }

    private static string UnchangedPatches(LlmRequest request) =>
        PatchResponse(request, (_, original) => original);

    private static string UnchangedResponse(LlmRequest request)
    {
        using var input = JsonDocument.Parse(request.Messages.Last().Content);
        if (input.RootElement.TryGetProperty("patchTargets", out _)) return UnchangedPatches(request);
        return JsonSerializer.Serialize(new
        {
            files = input.RootElement.GetProperty("targets").EnumerateArray().Select(file =>
                new GeneratedFile(file.GetProperty("path").GetString()!,
                    file.GetProperty("content").GetString()!))
        }, Json);
    }

    private sealed class DeliveryTrackingProvider(
        Func<LlmRequest, CancellationToken, Task<string>> complete) : IGenerationBudgetedProvider
    {
        public string Name => "Ollama";
        public List<LlmRequest> Requests { get; } = [];

        public Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var reservationId = LlmGenerationScope.Reserve(request, cancellationToken);
            LlmGenerationScope.MarkRequestMayHaveBeenSent(reservationId);
            return complete(request, cancellationToken);
        }
    }

    private sealed class Checker : IReactSourceCheck, IReactSourceRepair
    {
        public bool Deterministic { get; init; }
        public IReadOnlyList<ReactSourceIssue> RoleIssues { get; init; } = [];
        public IReadOnlyList<ReactSourceIssue> AdditionalIssues { get; set; } = [];
        public Action? AfterCheck { get; set; }
        public int RepairCalls { get; private set; }
        public Action? AfterFirstGroup { get; set; }
        public Task<IReadOnlyList<ReactSourceIssue>> CheckAsync(IReadOnlyList<GeneratedFile> files, CancellationToken token)
        {
            IReadOnlyList<ReactSourceIssue> issues = files.SelectMany(file => file.Content.Split('\n')
                .Select((line, index) => (line, index)).Where(item => item.line.Contains("BROKEN", StringComparison.Ordinal))
                .Select(item => new ReactSourceIssue(file.Path, item.index + 1, 1,
                    item.line.Contains("ROLE_BROKEN", StringComparison.Ordinal) ? "REACT_APP_TEST_IMPORT" : "TS2304",
                    "Undefined BROKEN"))).ToArray();
            if (RoleIssues.Count > 0 && files.Any(file => file.Content.Contains("ROLE_BROKEN", StringComparison.Ordinal)))
                issues = issues.Where(issue => issue.Code != "REACT_APP_TEST_IMPORT").Concat(RoleIssues).ToArray();
            if (!files.Any(file => file.Content.Contains("software correction", StringComparison.Ordinal)))
                issues = issues.Concat(AdditionalIssues).ToArray();
            AfterCheck?.Invoke();
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
            RepairCalls++;
            var before = await CheckAsync(files, token);
            var candidate = Deterministic ? files.Select(file => allowedPaths.Contains(file.Path)
                ? file with { Content = file.Content.Replace("BROKEN", "0", StringComparison.Ordinal) } : file).ToArray() : files;
            return new(candidate, before, await CheckAsync(candidate, token), [], []);
        }
    }

    private sealed class RetryDiagnosticsChecker : IReactSourceCheck, IReactSourceRepair
    {
        public Task<IReadOnlyList<ReactSourceIssue>> CheckAsync(
            IReadOnlyList<GeneratedFile> files, CancellationToken token)
        {
            var file = files.SingleOrDefault(item => item.Path == "src/App.tsx");
            if (file is null) return Task.FromResult<IReadOnlyList<ReactSourceIssue>>([]);
            var issues = file.Content.Split('\n').Select((line, index) => (line, index))
                .Where(item => item.line.Contains("BROKEN", StringComparison.Ordinal) ||
                    item.line.Contains("NEW_ERROR", StringComparison.Ordinal) ||
                    item.line.Contains("PRESERVED_ERROR", StringComparison.Ordinal))
                .Select(item => item.line.Contains("NEW_ERROR", StringComparison.Ordinal)
                    ? new ReactSourceIssue(file.Path, item.index + 1, 1, "TS1005", "Synthetic NEW_ERROR diagnostic")
                    : item.line.Contains("PRESERVED_ERROR", StringComparison.Ordinal)
                        ? new ReactSourceIssue(file.Path, item.index + 1, 1, "TS2322", "Synthetic preserved diagnostic")
                        : new ReactSourceIssue(file.Path, item.index + 1, 1, "TS2304", "Undefined BROKEN"))
                .ToArray();
            return Task.FromResult<IReadOnlyList<ReactSourceIssue>>(issues);
        }

        public async Task<ReactSourceRepairResult> RepairAsync(IReadOnlyList<GeneratedFile> files,
            IReadOnlyList<string> allowedPaths, CancellationToken token)
        {
            var issues = await CheckAsync(files, token);
            return new(files, issues, issues, [], []);
        }
    }

    private sealed class PreflightChecker : IReactSourceCheck, IReactSourceRepair
    {
        public bool UsePrimarySyntaxDiagnostics { get; set; }

        public Task<IReadOnlyList<ReactSourceIssue>> CheckAsync(
            IReadOnlyList<GeneratedFile> files, CancellationToken token)
        {
            var file = files.SingleOrDefault(item => item.Path == "src/App.test.tsx");
            if (file is null) return Task.FromResult<IReadOnlyList<ReactSourceIssue>>([]);
            var lines = file.Content.Split('\n');
            if (!UsePrimarySyntaxDiagnostics)
                return Task.FromResult<IReadOnlyList<ReactSourceIssue>>([
                    new(file.Path, 5, 1, "TS2304", "Repeated synthetic source context")
                ]);
            var brokenLine = Array.FindIndex(lines, line => line.Contains("BROKEN_TOKEN", StringComparison.Ordinal));
            if (brokenLine < 0) return Task.FromResult<IReadOnlyList<ReactSourceIssue>>([]);
            return Task.FromResult<IReadOnlyList<ReactSourceIssue>>([
                new(file.Path, brokenLine + 1, 1, "TS1002", "Synthetic primary string diagnostic"),
                new(file.Path, brokenLine + 1, 2, "TS1005", "Synthetic parser follow-on")
            ]);
        }

        public async Task<ReactSourceRepairResult> RepairAsync(IReadOnlyList<GeneratedFile> files,
            IReadOnlyList<string> allowedPaths, CancellationToken token)
        {
            var issues = await CheckAsync(files, token);
            return new(files, issues, issues, [], []);
        }
    }

    private sealed class MemoryGuardLibrary(bool blockMemory) : IModelLibraryService
    {
        public int GuardCalls { get; private set; }
        public bool BlockMemory { get; set; } = blockMemory;
        public Task GuardExecutionAsync(string name, int context, CancellationToken token, bool automatic = false)
        {
            GuardCalls++;
            if (BlockMemory) throw new LlmMemoryException("Simulated insufficient RAM/VRAM; no generation started.");
            return Task.CompletedTask;
        }
        public ModelRoutingProfile SaveSpecializations(string name, string[]? specializations) => throw new NotSupportedException();
        public ModelRoutingProfile ClearQualification(string name) => throw new NotSupportedException();
        public Task<ModelRoutingProfile> QualifyAsync(string name, int context, bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<ModelLibrarySnapshot> SnapshotAsync(int context, CancellationToken token) => throw new NotSupportedException();
        public Task<long> VerifyAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public IReadOnlyList<ModelDownload> Downloads() => throw new NotSupportedException();
        public ModelDownload StartDownload(string name, bool confirmed) => throw new NotSupportedException();
        public bool CancelDownload(Guid id) => throw new NotSupportedException();
        public Task DeleteAsync(string name, bool confirmed, CancellationToken token) => throw new NotSupportedException();
    }
}
