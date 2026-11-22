using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIPlatform.Tests;

public sealed class ReactRevisionValidationTests
{
    [Fact]
    public async Task MultipleRunsKeepEverySnapshotAndExposeHistoryWithoutChangingTheMission()
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        fixture.Process.TypeScriptSucceeds = true;
        var clock = new AdjustableClock();
        var service = fixture.Service(clock: clock);
        var missionBefore = File.ReadAllBytes(fixture.MissionPath);
        var filesBefore = fixture.Files.Select(file => (file.Path,
            Content: File.ReadAllBytes(SafePaths.Resolve(fixture.Workspaces.GetDirectory(fixture.Id), file.Path)))).ToArray();

        var firstPlan = await service.PrepareAsync(fixture.Id, default);
        var first = await service.AuthorizeAsync(fixture.Id, new(true, firstPlan.RequestId, firstPlan.Plan.Fingerprint), default);
        Assert.Equal("Succeeded", first.Status);
        Assert.Equal(ReactExecutionPlanDescriptor.Manifest(fixture.Files), first.Plan.Files);
        var before = Directory.GetFiles(fixture.Reports).ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal(5, before.Count);

        clock.Now = clock.Now.AddMinutes(1);
        var secondPlan = await service.PrepareAsync(fixture.Id, default);
        var second = await service.AuthorizeAsync(fixture.Id, new(true, secondPlan.RequestId, secondPlan.Plan.Fingerprint), default);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(10, Directory.GetFiles(fixture.Reports).Length);
        foreach (var (path, content) in before) Assert.Equal(content, File.ReadAllBytes(path));
        var history = await fixture.Service().GetReportsAsync(fixture.Id, default);
        Assert.Equal(new[] { second.RunId, first.RunId }, history.Select(report => report.RunId));
        Assert.Equal(second.RunId, (await service.GetReportAsync(fixture.Id, default))!.RunId);
        Assert.Equal(missionBefore, File.ReadAllBytes(fixture.MissionPath));
        foreach (var file in filesBefore)
            Assert.Equal(file.Content, File.ReadAllBytes(SafePaths.Resolve(fixture.Workspaces.GetDirectory(fixture.Id), file.Path)));
        Assert.Equal(13, fixture.Store.Get(fixture.Id)!.GenerationReservations!.Count);
    }

    [Fact]
    public async Task LegacySuccessfulReportRemainsReadableForCancelledWorkflowWithAmbiguousReviewer()
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        var service = fixture.Service();
        var preparation = await service.PrepareAsync(fixture.Id, default);
        var legacy = new ReactRevisionValidationReport(Guid.NewGuid(), preparation.Plan with { Files = null },
            "Succeeded", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new("TypeScript", "Succeeded", 0, ""), new("Build", "Succeeded", 0, ""),
             new("Tests", "Succeeded", 0, "", 5, FailedTests: 0)]);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.LegacyReport)!);
        var oldFormat = JsonNode.Parse(JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        oldFormat["plan"]!.AsObject().Remove("files");
        await File.WriteAllTextAsync(fixture.LegacyReport, oldFormat.ToJsonString());
        var beforeReport = File.ReadAllBytes(fixture.LegacyReport);
        fixture.Process.TypeScriptSucceeds = true;
        var newer = await service.AuthorizeAsync(fixture.Id,
            new(true, preparation.RequestId, preparation.Plan.Fingerprint), default);
        fixture.Store.Update(fixture.Id, mission => mission with
        {
            Status = MissionStatus.Cancelled, Workflow = mission.Workflow! with
            {
                PendingSoftwareAgentCall = "resume-review-8",
                PendingSoftwareCallDeliveryState = SoftwareAgentCallDeliveryState.MayHaveBeenSent
            }
        });
        var beforeMission = File.ReadAllBytes(fixture.MissionPath);
        var calls = fixture.Process.Calls.Count;
        var history = await fixture.Service().GetReportsAsync(fixture.Id, default);
        Assert.Equal(2, history.Count);
        Assert.Contains(history, item => item.RunId == newer.RunId);
        var actual = Assert.Single(history, item => item.RunId == legacy.RunId);
        Assert.Equal(legacy.RunId, actual.RunId);
        Assert.Equal("Succeeded", actual.Status);
        Assert.Equal(1, actual.Plan.RevisionVersion);
        Assert.Equal(5, actual.Results[2].TotalTests);
        Assert.Null(actual.Plan.Files);
        Assert.Equal(beforeMission, File.ReadAllBytes(fixture.MissionPath));
        Assert.Equal(beforeReport, File.ReadAllBytes(fixture.LegacyReport));
        Assert.Equal(calls, fixture.Process.Calls.Count);
    }

    [Fact]
    public async Task InterruptedRunKeepsCompletedStagesAndDoesNotRetryAfterRestart()
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        fixture.Process.TypeScriptSucceeds = true;
        fixture.Process.BlockBuild = true;
        var service = fixture.Service();
        var before = File.ReadAllBytes(fixture.MissionPath);
        var prepared = await service.PrepareAsync(fixture.Id, default);
        using var cancel = new CancellationTokenSource();
        var execution = service.AuthorizeAsync(fixture.Id, new(true, prepared.RequestId, prepared.Plan.Fingerprint), cancel.Token);
        await fixture.Process.BuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var snapshots = Directory.GetFiles(fixture.Reports).ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal(2, snapshots.Count);
        var running = Assert.Single(await service.GetReportsAsync(fixture.Id, default));
        Assert.Equal("Running", running.Status);
        Assert.Equal("TypeScript", Assert.Single(running.Results).Stage);

        var interruptedAfterRestart = Assert.Single(await fixture.Service().GetReportsAsync(fixture.Id, default));
        Assert.Equal("Interrupted", interruptedAfterRestart.Status);
        Assert.Equal("TypeScript", Assert.Single(interruptedAfterRestart.Results).Stage);
        Assert.Equal(2, fixture.Process.Calls.Count(call => call[0] == "run"));
        foreach (var (path, content) in snapshots) Assert.Equal(content, File.ReadAllBytes(path));
        cancel.Cancel();
        var finished = await execution;
        Assert.Equal("Interrupted", finished.Status);
        Assert.Equal("Interrupted", Assert.Single(await fixture.Service().GetReportsAsync(fixture.Id, default)).Status);
        foreach (var (path, content) in snapshots) Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal(before, File.ReadAllBytes(fixture.MissionPath));
        Assert.Equal(2, fixture.Process.Calls.Count(call => call[0] == "run"));
    }

    [Fact]
    public async Task HistoricalPreparationPersistsSeparateProofWithoutGenerationAndReusesItAfterRestart()
    {
        using var fixture = new Fixture();
        await fixture.PublishHistoricalAsync();
        var before = File.ReadAllBytes(fixture.MissionPath);
        var service = fixture.Service();
        var prepared = await service.PrepareAsync(fixture.Id, default);
        Assert.Equal(Fixture.Image, prepared.Plan.Image);
        Assert.NotNull(prepared.Plan.PreparationEvidenceId);
        Assert.Equal(prepared.RequestId, (await service.PrepareAsync(fixture.Id, default)).RequestId);
        var restarted = await fixture.Service().PrepareAsync(fixture.Id, default);
        Assert.Equal(prepared.Plan.PreparationEvidenceId, restarted.Plan.PreparationEvidenceId);
        var evidencePath = Assert.Single(Directory.GetFiles(fixture.Preparations));
        var evidence = JsonSerializer.Deserialize<ReactHistoricalExecutionPreparation>(
            File.ReadAllText(evidencePath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(prepared.Plan.PreparationEvidenceId, evidence.Id);
        Assert.Equal(prepared.Plan.RevisionFingerprint, evidence.RevisionFingerprint);
        Assert.Equal(Fixture.Image, evidence.PreparedExecutionPlan.ReactDescriptor!.ImageDigest);
        Assert.All(fixture.Process.Calls, call => Assert.Contains(call[0], new[] { "info", "image" }));
        Assert.Equal(before, File.ReadAllBytes(fixture.MissionPath));
        Assert.Equal(6, fixture.Store.Get(fixture.Id)!.GenerationReservations!.Count);
        Assert.Null(await service.GetReportAsync(fixture.Id, default));

        var report = await service.AuthorizeAsync(fixture.Id,
            new(true, prepared.RequestId, prepared.Plan.Fingerprint), default);
        Assert.Equal(3, report.Results.Count);
        Assert.Equal(before, File.ReadAllBytes(fixture.MissionPath));
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => service.AuthorizeAsync(fixture.Id,
            new(true, prepared.RequestId, prepared.Plan.Fingerprint), default));
        Assert.Equal(3, fixture.Process.Calls.Count(call => call[0] == "run"));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("untrusted")]
    [InlineData("offline")]
    [InlineData("absent")]
    [InlineData("wrong-image")]
    [InlineData("tag")]
    public async Task HistoricalPreparationRequiresAnAvailableTrustedImmutableImage(string defect)
    {
        using var fixture = new Fixture();
        await fixture.PublishHistoricalAsync();
        if (defect == "untrusted") fixture.Trust.ExpectedImage = null;
        if (defect == "offline") fixture.Process.LinuxAvailable = false;
        if (defect == "absent") fixture.Process.ImagePresent = false;
        if (defect == "wrong-image") fixture.Process.ImageId = "sha256:" + new string('b', 64);
        if (defect == "tag") fixture.Process.ImageId = ReactTemplate.Image;
        var service = fixture.Service(defect == "disabled" ? fixture.Sandbox with { Enabled = false } : null);
        var before = File.ReadAllBytes(fixture.MissionPath);
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => service.PrepareAsync(fixture.Id, default));
        Assert.False(Directory.Exists(fixture.Preparations));
        Assert.DoesNotContain(fixture.Process.Calls, call => call[0] is "run" or "rm");
        Assert.Equal(before, File.ReadAllBytes(fixture.MissionPath));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("plan")]
    [InlineData("revision")]
    [InlineData("mission")]
    [InlineData("ambiguous")]
    [InlineData("configuration")]
    [InlineData("image")]
    [InlineData("evidence")]
    [InlineData("missing-evidence")]
    [InlineData("persisted-revision")]
    public async Task HistoricalAuthorizationRefusesChangedBindingsWithoutExecution(string defect)
    {
        using var fixture = new Fixture();
        await fixture.PublishHistoricalAsync();
        var service = fixture.Service();
        var prepared = await service.PrepareAsync(fixture.Id, default);
        if (defect == "source")
            await File.AppendAllTextAsync(SafePaths.Resolve(fixture.Workspaces.GetDirectory(fixture.Id), "src/App.tsx"), "\nchanged");
        if (defect is "plan" or "revision" or "mission" or "ambiguous")
            fixture.Store.Update(fixture.Id, mission => defect switch
            {
                "plan" => mission with { Workflow = mission.Workflow! with { Plan = mission.Workflow.Plan! with { Name = "Changed" } } },
                "revision" => mission with { Workflow = mission.Workflow! with {
                    Revisions = [mission.Workflow.Revisions![0] with { Reason = "Changed publication" }] } },
                "mission" => mission with { Status = MissionStatus.Failed },
                _ => mission with { Workflow = mission.Workflow! with { PendingSoftwareAgentCall = "MayHaveBeenSent" } }
            });
        if (defect == "configuration")
            service = fixture.Service(fixture.Sandbox with { MemoryMegabytes = 1024 });
        if (defect == "image")
            fixture.Trust.ExpectedImage = fixture.Process.ImageId = "sha256:" + new string('b', 64);
        if (defect == "evidence")
            await File.WriteAllTextAsync(Assert.Single(Directory.GetFiles(fixture.Preparations)), "{}");
        if (defect == "missing-evidence")
            File.Delete(Assert.Single(Directory.GetFiles(fixture.Preparations)));
        if (defect == "persisted-revision")
        {
            var mission = fixture.Store.Get(fixture.Id)!;
            var changed = mission with { Workflow = mission.Workflow! with
            {
                Revisions = [mission.Workflow.Revisions![0] with { Reason = "Changed on disk only" }]
            } };
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            json.Converters.Add(new JsonStringEnumConverter());
            await File.WriteAllTextAsync(fixture.MissionPath, JsonSerializer.Serialize(changed, json));
        }
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => service.AuthorizeAsync(fixture.Id,
            new(true, prepared.RequestId, prepared.Plan.Fingerprint), default));
        Assert.DoesNotContain(fixture.Process.Calls, call => call[0] is "run" or "rm");
        Assert.Equal(6, fixture.Store.Get(fixture.Id)!.GenerationReservations!.Count);
    }

    [Fact]
    public async Task HistoricalPreparationRejectsChangedSourcesConfigurationAndAmbiguousCheckpointBeforeInspection()
    {
        using var fixture = new Fixture();
        await fixture.PublishHistoricalAsync();
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() =>
            fixture.Service(fixture.Sandbox with { CpuCount = 1 }).PrepareAsync(fixture.Id, default));
        fixture.Store.Update(fixture.Id, mission => mission with { Workflow = mission.Workflow! with
        {
            SoftwareCorrections = [new(Guid.NewGuid(), "generate", "interrupted", fixture.Files, fixture.Files,
                [], 0, [], InFlight: true)]
        } });
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => fixture.Service().PrepareAsync(fixture.Id, default));
        fixture.Store.Update(fixture.Id, mission => mission with { Workflow = mission.Workflow! with { SoftwareCorrections = [] } });
        await File.AppendAllTextAsync(SafePaths.Resolve(fixture.Workspaces.GetDirectory(fixture.Id), "src/App.tsx"), "\nchanged");
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => fixture.Service().PrepareAsync(fixture.Id, default));
        Assert.Empty(fixture.Process.Calls);
    }

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

    [Fact]
    public async Task ExpiredValidationApprovalCannotStartAContainer()
    {
        using var fixture = new Fixture();
        await fixture.PublishAsync();
        var clock = new AdjustableClock();
        var service = fixture.Service(clock: clock);
        var before = File.ReadAllBytes(fixture.MissionPath);
        var prepared = await service.PrepareAsync(fixture.Id, default);
        clock.Now = prepared.ExpiresAt;
        await Assert.ThrowsAsync<ReactRevisionValidationConflictException>(() => service.AuthorizeAsync(fixture.Id,
            new(true, prepared.RequestId, prepared.Plan.Fingerprint), default));
        Assert.Empty(fixture.Process.Calls);
        Assert.Equal(before, File.ReadAllBytes(fixture.MissionPath));
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

    private sealed class AdjustableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        public const string Image = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public Guid Id { get; } = Guid.NewGuid();
        public StudioOptions Options { get; } = new() { DataDirectory = Path.Combine(Path.GetTempPath(), $"react-validation-{Guid.NewGuid():N}") };
        public SandboxOptions Sandbox { get; } = new() { Enabled = true };
        public SimulatedDocker Process { get; } = new();
        public SimulatedTrust Trust { get; } = new();
        public WorkspaceStore BaseWorkspaces => new(Options);
        public WorkspaceStore Workspaces => new(Options, id =>
            Store?.Get(id)?.Workflow?.Revisions?.LastOrDefault(revision => revision.WorkspaceId.HasValue)?.WorkspaceId);
        public MissionStore Store { get; private set; } = null!;
        public MissionExecutionGate Gate { get; } = new();
        public string MissionPath => Path.Combine(Options.DataDirectory, "missions", $"{Id:N}.json");
        public string Preparations => Path.Combine(Options.DataDirectory, "react-revision-validation", "preparations");
        public string Reports => Path.Combine(Options.DataDirectory, "react-revision-validation", "reports", $"{Id:N}");
        public string LegacyReport => Path.Combine(Options.DataDirectory, "react-revision-validation", $"{Id:N}.json");
        public GeneratedFile[] Files { get; } = [.. MissionReactTemplate.Files, new("src/App.tsx",
            "export default function App() { return <main>Project</main> }"),
            new("src/App.test.tsx", "import { test } from 'vitest'; test('test', () => {})")];

        public async Task PublishAsync(bool hasRevision = true, int generationLimit = 20, int generationCount = 13)
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
                GenerationReservations: Enumerable.Range(1, generationCount).Select(_ => new MissionGenerationReservation(
                    Guid.NewGuid(), "Developer", "model", now)).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(MissionPath)!);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            json.Converters.Add(new JsonStringEnumConverter());
            await File.WriteAllTextAsync(MissionPath, JsonSerializer.Serialize(mission, json));
            var platform = TestSupport.Configuration();
            Store = new(Options, platform, new AgentRegistry(platform), Workspaces);
        }

        public ReactRevisionValidation Service(SandboxOptions? options = null, TimeProvider? clock = null)
        {
            var current = options ?? Sandbox;
            var plans = new ExecutionPlanService(Workspaces, current, Options, Store, Process, Trust);
            return new(Store, Workspaces, new(current, Workspaces, Process, Store, imageTrust: Trust), current, Options, Gate,
                clock ?? TimeProvider.System, NullLogger<ReactRevisionValidation>.Instance, plans, Trust);
        }

        public async Task PublishHistoricalAsync()
        {
            await PublishAsync(generationLimit: 12, generationCount: 6);
            Store.Update(Id, mission => mission with
            {
                Status = MissionStatus.GeneratedUnverified,
                Workflow = mission.Workflow! with
                {
                    Version = 1, RequirePlanApproval = true,
                    Plan = new("Counter", "Reviewed historical project", [],
                        Files.Select(file => new PlannedFile(file.Path, "Project file", [])).ToArray())
                }
            });
            var plan = await new ExecutionPlanService(Workspaces, Sandbox, Options, Store, Process, Trust).GetStaticAsync(Id, default);
            Store.Update(Id, mission => mission with { Workflow = mission.Workflow! with
            {
                PlanApproval = new(1, MissionStore.PlanFingerprint(mission, 1), true),
                Revisions = [mission.Workflow.Revisions![0] with { Fingerprint = plan.Fingerprint, ExecutionPlan = plan }]
            } });
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
        public bool LinuxAvailable { get; set; } = true;
        public bool ImagePresent { get; set; } = true;
        public string ImageId { get; set; } = Fixture.Image;
        public bool TypeScriptSucceeds { get; set; }
        public bool BlockBuild { get; set; }
        public TaskCompletionSource<bool> BuildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            if (BlockBuild && arguments[0] == "run" && arguments[^1].Contains("vite build"))
            {
                BuildStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            ContainerProcessResult result = arguments[0] switch
            {
                "info" => new(LinuxAvailable ? 0 : 1, LinuxAvailable ? "linux" : "Engine unavailable"),
                "image" => new(ImagePresent ? 0 : 1, ImagePresent ? ImageId : "Image missing"),
                "rm" => new(0, ""),
                "run" when arguments[^1].Contains("tsc --noEmit") => TypeScriptSucceeds
                    ? new(0, "") : new(2, "src/App.tsx(1,2): error TS2322: Type mismatch."),
                "run" when arguments[^1].Contains("vite build") => new(0, "Vite built the project."),
                "run" when arguments[^1].Contains("vitest run") => new(0, "\nAIPLATFORM_VITEST_BEGIN\n" +
                    """{"success":true,"numTotalTests":1,"numPassedTests":1,"numFailedTests":0,"numPendingTests":0,"numFailedTestSuites":0,"testResults":[{"status":"passed","assertionResults":[{"status":"passed"}]}]}""" +
                    "\nAIPLATFORM_VITEST_END\n"),
                _ => throw new InvalidOperationException("Unexpected simulated command.")
            };
            return result;
        }

    }

    private sealed class SimulatedTrust : ISandboxImageTrust
    {
        public string? ExpectedImage { get; set; } = Fixture.Image;
        public string? GetExpectedImageId(string imageReference) => imageReference == ReactTemplate.Image ? ExpectedImage : null;
        public bool IsTrustedImage(string imageReference, string imageId) => GetExpectedImageId(imageReference) == imageId;
    }
}
