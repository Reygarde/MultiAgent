using System.Diagnostics;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class MissionProcessor(
    MissionStore store, IAgentRegistry registry, IAgentRouter router,
    ILLMProviderResolver providers, IProjectGenerator generator,
    IWorkspaceStore workspaces, ISandboxRunner sandbox, StudioOptions options,
    IExecutionPlanService plans, CollaborativeProcessor? collaborative = null)
{
    public async Task ProcessAsync(Guid id, CancellationToken cancellationToken)
    {
        var mission = store.Get(id) ?? throw new InvalidOperationException("Mission not found.");
        if (mission.Workflow is not null)
        {
            await (collaborative ?? throw new InvalidOperationException("Collaborative processor missing."))
                .ProcessAsync(id, cancellationToken);
            return;
        }
        if (mission.IsTerminal || mission.Status == MissionStatus.AwaitingApproval)
            return;
        store.Update(id, current => current with { Status = MissionStatus.Running });
        store.AddEvent(id, "Queue", StepStatus.Succeeded, "Mission admitted to the single worker.");
        var resuming = mission.HasWorkspace && mission.ApprovalFingerprint is not null;
        AgentDefinition agent;
        if (resuming)
        {
            var currentPlan = await plans.GetAsync(id, cancellationToken);
            if (currentPlan.Fingerprint != mission.ApprovalFingerprint)
                throw new StudioValidationException("Files or execution settings changed after approval. Submit a new mission.");
            agent = registry.Agents.Single(candidate => candidate.Enabled && candidate.Id == mission.SelectedAgent);
        }
        else
        {
            store.AddEvent(id, "Analysis", StepStatus.Running, "Validating mission mode and authorized agents.");
            cancellationToken.ThrowIfCancellationRequested();
            store.AddEvent(id, "Analysis", StepStatus.Succeeded, mission.Kind == MissionKind.Project
                ? "Project generation requested; files remain untrusted." : "Text-only conversation requested.");
            store.AddEvent(id, "Routing", StepStatus.Running, "Selecting an authorized execution agent.");
            var decision = mission.RequestedAgent is not null
                ? new RoutingDecision(registry.Candidates.Single(candidate => candidate.Id == mission.RequestedAgent),
                    "Agent explicitly selected by the user.", false)
                : await router.RouteAsync(mission.Task, cancellationToken);
            if (mission.Kind == MissionKind.Project && decision.Agent.Id != options.DeveloperAgentId)
                decision = new(registry.Agents.Single(candidate => candidate.Enabled && candidate.Id == options.DeveloperAgentId),
                    decision.Reason + " Project mode is restricted to the configured developer agent.", decision.UsedFallback);
            agent = decision.Agent;
            var selectedModel = registry.GetModel(agent.ModelId);
            store.Update(id, current => current with
            {
                SelectedAgent = agent.Id, SelectedModel = selectedModel.Name,
                Reason = decision.Reason, UsedFallback = decision.UsedFallback
            });
            store.AddEvent(id, "Routing", StepStatus.Succeeded, $"{agent.Id} / {selectedModel.Name}: {decision.Reason}");
        }

        if (mission.Kind == MissionKind.Chat)
        {
            var model = registry.GetModel(agent.ModelId);
            store.AddEvent(id, "Generation", StepStatus.Running, "Waiting for the selected model response.");
            var response = await AgentAnswer.GenerateAsync(providers.Resolve(model.Provider),
                model, agent, mission.Task, cancellationToken);
            store.AddEvent(id, "Generation", StepStatus.Succeeded, "Final answer received and validated.");
            store.Update(id, current => current with { Response = response, Status = MissionStatus.Completed });
            store.AddEvent(id, "Mission", StepStatus.Succeeded, "Conversation completed.");
            return;
        }

        ProjectManifest? manifest = null;
        if (resuming)
        {
            var files = new List<GeneratedFile>();
            foreach (var file in workspaces.List(id).Where(file => file.Path != "Generated.slnx"))
            {
                var content = await workspaces.ReadAsync(id, file.Path, cancellationToken);
                files.Add(new(content.Path, content.Content));
            }
            manifest = new(mission.Response ?? "Generated project.", files);
            await workspaces.WriteAsync(id, manifest.Files, cancellationToken);
            if ((await plans.GetAsync(id, cancellationToken)).Fingerprint != mission.ApprovalFingerprint)
                throw new StudioValidationException("The approved workspace is not a canonical validated project.");
        }
        else
        {
            workspaces.Create(id);
            store.Update(id, current => current with { HasWorkspace = true });
        }

        string? diagnostics = null;
        MissionStatus? verificationFailure = null;
        for (var attempt = mission.CorrectionAttempts; attempt <= options.MaxCorrectionAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resuming || attempt > mission.CorrectionAttempts)
            {
                if (attempt > 0)
                {
                    store.Update(id, current => current with { CorrectionAttempts = attempt });
                    store.AddEvent(id, "Corrections", StepStatus.Running,
                        $"Correction {attempt}/{options.MaxCorrectionAttempts}: requesting targeted file replacements.");
                }
                store.AddEvent(id, "Generation", StepStatus.Running, manifest is null
                    ? "Generating the complete project manifest." : "Generating targeted corrections from diagnostics.");
                var writing = false;
                try
                {
                    var changes = await generator.GenerateAsync(agent, mission.Task, manifest, diagnostics, cancellationToken);
                    var next = manifest is null ? changes : MergeCorrections(manifest, changes);
                    store.AddEvent(id, "Generation", StepStatus.Succeeded, $"Received {changes.Files.Count} proposed files.");
                    store.AddEvent(id, "Writing", StepStatus.Running, "Validating all paths, project XML and size limits.");
                    writing = true;
                    await workspaces.WriteAsync(id, next.Files, cancellationToken);
                    manifest = next;
                    store.AddEvent(id, "Writing", StepStatus.Succeeded,
                        $"Validated project files written. Proposed paths: {string.Join(", ", changes.Files.Select(file => file.Path))}");
                    store.Update(id, current => current with { Response = next.Summary });
                    if (attempt > 0)
                        store.AddEvent(id, "Corrections", StepStatus.Succeeded, "Corrections validated and written; verification is still pending.");
                }
                catch (Exception exception) when (exception is LlmProviderException or StudioValidationException)
                {
                    diagnostics = exception.Message;
                    store.AddEvent(id, writing ? "Writing" : "Generation", StepStatus.Failed, diagnostics);
                    if (attempt > 0)
                        store.AddEvent(id, "Corrections", StepStatus.Failed, diagnostics);
                    if (attempt == options.MaxCorrectionAttempts)
                    {
                        if (verificationFailure is { } failure)
                        {
                            store.Update(id, current => current with { Status = failure, Error = diagnostics });
                            store.AddEvent(id, "Mission", StepStatus.Failed, "Corrections exhausted without a valid replacement project.");
                            return;
                        }
                        throw new LlmProviderException("Project generation failed after the configured correction limit.", exception);
                    }
                    continue;
                }
            }

            var availability = await sandbox.GetAvailabilityAsync(cancellationToken);
            if (!availability.Available)
            {
                FinishUnverified(availability.Reason);
                return;
            }
            if (!mission.SandboxApproved)
            {
                store.AddEvent(id, "Authorization", StepStatus.Running,
                    "Awaiting human approval. Review generated files and the bounded execution plan; no container has started.");
                store.Update(id, current => current with { Status = MissionStatus.AwaitingApproval });
                return;
            }
            if (!resuming && attempt == 0)
                store.AddEvent(id, "Authorization", StepStatus.Succeeded,
                    "Explicit legacy per-mission approval supplied at submission; only the bounded build/test workflow is authorized.");

            var build = await Verify("Build");
            VerificationResult? tests = null;
            if (build.Status == "Succeeded")
                tests = await Verify("Tests");
            else
                store.AddEvent(id, "Tests", StepStatus.Skipped, "Build failed; tests were not executed.");
            if (build.Status == "Succeeded" && tests is { Status: "Succeeded", ExitCode: 0, TotalTests: > 0 })
            {
                store.Update(id, current => current with { Status = MissionStatus.Verified, Error = null });
                store.AddEvent(id, "Mission", StepStatus.Succeeded, "Project built and non-empty tests passed in isolation.");
                return;
            }

            store.AddEvent(id, "ErrorAnalysis", StepStatus.Running, "Collecting actual diagnostics for the developer agent.");
            diagnostics = build.Status == "Succeeded" ? tests!.Log : build.Log;
            verificationFailure = build.Status == "Succeeded" ? MissionStatus.TestsFailed : MissionStatus.BuildFailed;
            store.AddEvent(id, "ErrorAnalysis", StepStatus.Succeeded,
                $"Failure in {(build.Status == "Succeeded" ? "Tests" : "Build")}; no arbitrary commands will be accepted.");
            if (attempt == options.MaxCorrectionAttempts)
            {
                var status = build.Status == "Succeeded" ? MissionStatus.TestsFailed : MissionStatus.BuildFailed;
                store.Update(id, current => current with { Status = status, Error = $"Correction limit reached: {status}." });
                store.AddEvent(id, "Mission", StepStatus.Failed, $"Verification failed after {attempt} corrections: {status}.");
                return;
            }
        }

        void FinishUnverified(string reason)
        {
            store.AddEvent(id, "Build", StepStatus.Unavailable, reason);
            store.AddEvent(id, "Tests", StepStatus.Unavailable, reason);
            store.Update(id, current => current with
            {
                Status = MissionStatus.GeneratedUnverified,
                Verification =
                [
                    .. current.Verification, new("Build", "Unavailable", null, reason),
                    new("Tests", "Unavailable", null, reason)
                ]
            });
            store.AddEvent(id, "Mission", StepStatus.Unavailable, "Files generated; build and tests are unverified.");
        }

        async Task<VerificationResult> Verify(string stage)
        {
            var verificationAttempt = store.Get(id)!.Verification.Count(item => item.Stage == stage && item.Attempt > 0) + 1;
            store.AddEvent(id, stage, StepStatus.Running, $"Running {stage} in a new isolated offline container (attempt {verificationAttempt}).");
            var watch = Stopwatch.StartNew();
            var result = await sandbox.RunAsync(id, stage, cancellationToken);
            result = result with { Attempt = verificationAttempt, DurationMilliseconds = watch.Elapsed.TotalMilliseconds };
            if (result.Status == "Succeeded" && (result.ExitCode != 0 ||
                (stage == "Tests" && (result.TotalTests is null or <= 0 || result.FailedTests > 0))))
                result = result with { Status = "Failed", Log = result.Log + "\nSuccessful verification was not confirmed by the exit code and required test counters." };
            store.Update(id, current => current with { Verification = [.. current.Verification, result] });
            store.AddEvent(id, stage, result.Status == "Succeeded" ? StepStatus.Succeeded : StepStatus.Failed,
                $"{stage}: {result.Status}; exit code {result.ExitCode?.ToString() ?? "unavailable"}.");
            return result;
        }
    }

    private static ProjectManifest MergeCorrections(ProjectManifest previous, ProjectManifest changes)
    {
        if (changes.Files.Count == 0 ||
            changes.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Files.Count)
            throw new StudioValidationException("A correction must contain unique replacement files.");
        var files = previous.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in changes.Files)
        {
            SafePaths.ValidateRelative(file.Path);
            files[file.Path] = file;
        }
        return new(changes.Summary, files.Values.ToArray());
    }
}
