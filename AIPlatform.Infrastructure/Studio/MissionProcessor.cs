using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class MissionProcessor(
    MissionStore store, IAgentRegistry registry, IAgentRouter router,
    ILLMProviderResolver providers, IProjectGenerator generator,
    IWorkspaceStore workspaces, ISandboxRunner sandbox, StudioOptions options)
{
    public async Task ProcessAsync(Guid id, CancellationToken cancellationToken)
    {
        var mission = store.Get(id) ?? throw new InvalidOperationException("Mission not found.");
        if (mission.IsTerminal)
            return;
        store.Update(id, current => current with { Status = MissionStatus.Running });
        store.AddEvent(id, "Queue", StepStatus.Succeeded, "Mission admitted to the single worker.");
        store.AddEvent(id, "Analysis", StepStatus.Running, "Validating mission mode and authorized agents.");
        cancellationToken.ThrowIfCancellationRequested();
        store.AddEvent(id, "Analysis", StepStatus.Succeeded, mission.Kind == MissionKind.Project
            ? "Project generation requested; files remain untrusted." : "Text-only conversation requested.");
        store.AddEvent(id, "Routing", StepStatus.Running, "Selecting an authorized execution agent.");
        var decision = mission.RequestedAgent is not null
            ? new RoutingDecision(registry.Candidates.Single(agent => agent.Id == mission.RequestedAgent),
                "Agent explicitly selected by the user.", false)
            : await router.RouteAsync(mission.Task, cancellationToken);
        if (mission.Kind == MissionKind.Project && decision.Agent.Id != options.DeveloperAgentId)
            decision = new(registry.Candidates.Single(agent => agent.Id == options.DeveloperAgentId),
                decision.Reason + " Project mode is restricted to the configured developer agent.", decision.UsedFallback);
        var model = registry.GetModel(decision.Agent.ModelId);
        store.Update(id, current => current with
        {
            SelectedAgent = decision.Agent.Id, SelectedModel = model.Name,
            Reason = decision.Reason, UsedFallback = decision.UsedFallback
        });
        store.AddEvent(id, "Routing", StepStatus.Succeeded, $"{decision.Agent.Id} / {model.Name}: {decision.Reason}");

        if (mission.Kind == MissionKind.Chat)
        {
            store.AddEvent(id, "Generation", StepStatus.Running, "Waiting for the selected model response.");
            var response = await AgentAnswer.GenerateAsync(providers.Resolve(model.Provider),
                model, decision.Agent, mission.Task, cancellationToken);
            store.AddEvent(id, "Generation", StepStatus.Succeeded, "Final answer received and validated.");
            store.Update(id, current => current with { Response = response, Status = MissionStatus.Completed });
            store.AddEvent(id, "Mission", StepStatus.Succeeded, "Conversation completed.");
            return;
        }

        workspaces.Create(id);
        store.Update(id, current => current with { HasWorkspace = true });
        ProjectManifest? manifest = null;
        string? diagnostics = null;
        for (var attempt = 0; attempt <= options.MaxCorrectionAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0)
            {
                store.Update(id, current => current with { CorrectionAttempts = attempt });
                store.AddEvent(id, "Corrections", StepStatus.Running,
                    $"Correction {attempt}/{options.MaxCorrectionAttempts}: requesting a revised manifest.");
            }
            store.AddEvent(id, "Generation", StepStatus.Running, "Generating the complete project manifest.");
            var writing = false;
            try
            {
                var next = await generator.GenerateAsync(decision.Agent, mission.Task, manifest, diagnostics, cancellationToken);
                manifest = next;
                store.AddEvent(id, "Generation", StepStatus.Succeeded, $"Received {next.Files.Count} proposed files.");
                store.AddEvent(id, "Writing", StepStatus.Running, "Validating paths, project XML and size limits before writing.");
                writing = true;
                await workspaces.WriteAsync(id, next.Files, cancellationToken);
                store.AddEvent(id, "Writing", StepStatus.Succeeded, "Validated project files written to the dedicated workspace.");
                store.Update(id, current => current with { Response = next.Summary });
                if (attempt > 0)
                    store.AddEvent(id, "Corrections", StepStatus.Succeeded, "Revised manifest passed file validation; execution results are still pending.");
            }
            catch (Exception exception) when (exception is LlmProviderException or StudioValidationException)
            {
                diagnostics = exception.Message;
                store.AddEvent(id, writing ? "Writing" : "Generation", StepStatus.Failed, diagnostics);
                if (attempt == options.MaxCorrectionAttempts)
                    throw new LlmProviderException("Project generation failed after the configured correction limit.", exception);
                continue;
            }

            var availability = await sandbox.GetAvailabilityAsync(cancellationToken);
            if (!mission.SandboxApproved || !availability.Available)
            {
                var reason = !availability.Available ? availability.Reason :
                    "Human approval was not granted; build and tests were not executed.";
                var status = !availability.Available ? StepStatus.Unavailable : StepStatus.Skipped;
                store.AddEvent(id, "Build", status, reason);
                store.AddEvent(id, "Tests", status, reason);
                store.Update(id, current => current with
                {
                    Status = MissionStatus.GeneratedUnverified,
                    Verification =
                    [
                        .. current.Verification,
                        new("Build", status.ToString(), null, reason),
                        new("Tests", status.ToString(), null, reason)
                    ],
                    Response = current.Response + "\n\nProjet genere mais NON VERIFIE : compilation et tests non executes. " + reason
                });
                store.AddEvent(id, "Mission", StepStatus.Unavailable, "Files generated; build and tests are unverified.");
                return;
            }

            var build = await Verify("Build");
            VerificationResult? tests = null;
            if (build.Status == "Succeeded")
                tests = await Verify("Tests");
            else
                store.AddEvent(id, "Tests", StepStatus.Skipped, "Build failed; tests were not executed.");
            if (build.Status == "Succeeded" && tests?.Status == "Succeeded")
            {
                if (attempt > 0)
                    store.AddEvent(id, "Corrections", StepStatus.Succeeded, "Corrected project passed isolated verification.");
                store.Update(id, current => current with { Status = MissionStatus.Completed });
                store.AddEvent(id, "Mission", StepStatus.Succeeded, "Project built and tests passed in isolation.");
                return;
            }
            diagnostics = build.Status == "Succeeded" ? tests!.Log : build.Log;
            if (attempt == options.MaxCorrectionAttempts)
                throw new LlmProviderException("Build or tests failed after the configured correction limit.");
        }

        async Task<VerificationResult> Verify(string stage)
        {
            store.AddEvent(id, stage, StepStatus.Running, $"Running {stage} in the isolated offline container.");
            var result = await sandbox.RunAsync(id, stage, cancellationToken);
            store.Update(id, current => current with { Verification = [.. current.Verification, result] });
            store.AddEvent(id, stage, result.Status == "Succeeded" ? StepStatus.Succeeded : StepStatus.Failed,
                $"{stage}: {result.Status}; exit code {result.ExitCode?.ToString() ?? "unavailable"}.");
            return result;
        }
    }
}
