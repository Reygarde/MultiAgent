using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class WorkflowModelRouting(MissionStore store, IModelRouter router,
    IModelLibraryService library, StudioOptions options)
{
    public async Task<T> ExecuteAsync<T>(MissionSnapshot input, AgentDefinition agent, string tool,
        string? path, string context, ModelDefinition configured, Func<ModelDefinition, Guid, Task<T>> generate,
        CancellationToken token)
    {
        var mission = store.Get(input.Id)!;
        var flow = mission.Workflow!;
        var step = flow.Steps.Single(item => item.Status == WorkflowStatus.Running);
        var key = agent.Id + ":" + (path ?? tool);
        var role = agent.Role == "Review" ? "Reviewer" : agent.Role;
        if (tool == "GenerateFiles" && agent.Role == "Developer")
            role = path?.EndsWith(".cs", StringComparison.Ordinal) == true ? "Backend" :
                input.TemplateId is ReactTemplate.Id or MissionReactTemplate.Id ? "Frontend" : role;
        var sequence = flow.ModelSelections.FirstOrDefault(item => item.Key == key);
        if (sequence is null)
        {
            var decision = await router.SelectAsync(
                $"Template: {mission.TemplateId ?? ".NET"}; operation: {step.Kind}; file: {path}; " +
                $"complexity heuristic: {(context.Length > 8000 || flow.Plan?.Files.Count > 8 ? "high" : "bounded")}; " +
                $"task: {mission.Task}\nDiagnostics: {flow.Diagnostics}\nActual bounded context:\n{context}",
                role, configured.ContextSize, token);
            sequence = new(key, agent.Id, decision);
            store.Update(mission.Id, current => current with
            {
                Workflow = current.Workflow! with { ModelSelections = [.. current.Workflow.ModelSelections, sequence] }
            });
        }
        var prior = flow.ModelInvocations.Where(item => item.Sequence == key && item.Attempt.Outcome != "Blocked")
            .Select(item => item.Attempt).ToArray();
        var candidates = sequence.Decision.Alternatives.Select(item => item.Model).Distinct(StringComparer.Ordinal).ToArray();
        var agentKey = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(flow.WorkflowId + ":" + key)).AsSpan(0, 16));
        var selection = CustomAgentModelPolicy.Next(agentKey, candidates, prior);
        var retry = flow.ModelInvocations.LastOrDefault(item => item.Sequence == key) is
            { StepId: var previousStep, Attempt.Outcome: "Failed" } && previousStep == step.Id;
        var correction = step.Kind is "Correct" or "BackendCorrect" or "FrontendCorrect";
        var charge = retry || correction && step.Attempts == 0;
        if (charge && mission.CorrectionAttempts >= options.CorrectionLimitFor(mission))
            throw new StudioValidationException("Correction/generation retry limit reached; model history was retained.");

        var model = configured with { Name = selection.Model };
        // Recheck the stable choice, never silently reorder a correction chain after a resource change.
        await library.GuardExecutionAsync(model.Name, model.ContextSize, token, automatic: true);
        var attempt = new CustomAgentModelAttempt(Guid.NewGuid(), flow.WorkflowId, agentKey,
            agent.Role is "Backend" or "Frontend" ? "Developer" : agent.Role == "Review" ? "Reviewer" : agent.Role,
            model.Name, selection.Reason, DateTimeOffset.UtcNow, true, candidates)
            {
                TaskCategory = flow.Diagnostics is null ? sequence.Decision.Category :
                    LocalModelRouter.Classify($"{path}\n{flow.Diagnostics}", role),
                RoutingDecision = sequence.Decision
            };
        store.Update(mission.Id, current => current with
        {
            SelectedAgent = agent.Id, SelectedModel = model.Name, Reason = sequence.Decision.Reason,
            UsedFallback = model.Name != candidates[0],
            CorrectionAttempts = current.CorrectionAttempts + (charge ? 1 : 0),
            Workflow = current.Workflow! with
            {
                ModelInvocations = [.. current.Workflow.ModelInvocations, new(step.Id, key, attempt)],
                Steps = current.Workflow.Steps.Select(item => item.Id == step.Id
                    ? item with { Attempts = item.Attempts + 1 } : item).ToArray()
            }
        });
        store.AddEvent(mission.Id, "ModelSelection", StepStatus.Running, $"{agent.Id}: {model.Name}. {selection.Reason}");
        try
        {
            var result = await generate(model, attempt.Id);
            Change(mission.Id, attempt.Id, item => item with { Outcome = "Generated", CompletedAt = DateTimeOffset.UtcNow });
            return result;
        }
        catch (Exception exception) when (exception is LlmProviderException or StudioValidationException or
            OperationCanceledException or HttpRequestException)
        {
            var kind = CustomAgentModelPolicy.Classify(exception);
            if (exception is StudioValidationException && kind != "Context" || exception.InnerException is JsonException)
                kind = "Generation";
            var blocked = kind is "Memory" or "Context";
            Change(mission.Id, attempt.Id, item => item with
            {
                Outcome = blocked ? "Blocked" : "Failed", FailureKind = kind, CompletedAt = DateTimeOffset.UtcNow
            });
            if (blocked)
                store.Update(mission.Id, current => current with
                {
                    CorrectionAttempts = current.CorrectionAttempts - (charge ? 1 : 0),
                    Workflow = current.Workflow! with { Steps = current.Workflow.Steps.Select(item => item.Id == step.Id
                        ? item with { Attempts = item.Attempts - 1 } : item).ToArray() }
                });
            throw;
        }
    }

    public void CompleteStep(Guid missionId, string stepId)
    {
        UpdateStep(missionId, stepId, attempt => attempt.Outcome == "Generated" && attempt.Role != "Developer"
            ? attempt with { Outcome = "Succeeded" } : attempt);
    }

    public bool FailStep(Guid missionId, string stepId, Exception exception, int previousInvocationCount)
    {
        var flow = store.Get(missionId)!.Workflow!;
        if (flow.ModelInvocations.Count <= previousInvocationCount) return false;
        var latest = flow.ModelInvocations.LastOrDefault(item => item.StepId == stepId);
        if (latest?.Attempt.Outcome == "Generated" && exception is StudioValidationException &&
            !exception.Message.Contains("context budget", StringComparison.Ordinal))
            Change(missionId, latest.Attempt.Id, attempt => attempt with { Outcome = "Failed", FailureKind = "Generation" });
        var current = store.Get(missionId)!;
        return current.Workflow!.ModelInvocations.LastOrDefault(item => item.StepId == stepId)?.Attempt is
            { Outcome: "Failed", FailureKind: "Generation" } &&
            current.CorrectionAttempts < options.CorrectionLimitFor(current);
    }

    public void RecordReview(Guid missionId, ReviewReport review)
    {
        var flow = store.Get(missionId)!.Workflow!;
        foreach (var invocation in flow.ModelInvocations.Where(item => item.Attempt.Role == "Developer")
                     .GroupBy(item => item.Sequence).Select(group => group.Last()))
            if (review.Findings.Any(finding => finding.Severity == "Error" &&
                invocation.Sequence.EndsWith(":" + finding.Path, StringComparison.Ordinal)))
                Change(missionId, invocation.Attempt.Id, attempt => attempt with
                    { Outcome = "Failed", FailureKind = "Generation" });
    }

    public void RecordVerification(Guid missionId, VerificationResult result)
    {
        var mission = store.Get(missionId)!;
        var flow = mission.Workflow!;
        var tests = result.Stage.Contains("Tests", StringComparison.Ordinal) || result.Stage == "Integration";
        var qualityFailure = result.Diagnostics?.Items.Any(item => item.Code is { } code &&
            (code.StartsWith("TS", StringComparison.Ordinal) || code.StartsWith("CS", StringComparison.Ordinal))) == true ||
            tests && result.FailedTests > 0;
        var participants = flow.ModelInvocations.Where(item => item.Attempt.Role == "Developer")
                     .GroupBy(item => item.Sequence).Select(group => group.Last())
                     .Where(item => item.Attempt.Outcome is "Generated" or "Succeeded")
                     .Where(item => mission.TemplateId != FullStackTemplate.Id ||
                         (result.Stage.StartsWith("Backend", StringComparison.Ordinal)
                             ? item.Sequence.Contains(":backend/", StringComparison.Ordinal)
                             : result.Stage.StartsWith("Frontend", StringComparison.Ordinal) || result.Stage == "TypeScript"
                                 ? item.Sequence.Contains(":frontend/", StringComparison.Ordinal) : true)).ToArray();
        var implicated = participants.Where(item => result.Diagnostics?.Items.Any(diagnostic =>
        {
            var path = item.Sequence[(item.Sequence.IndexOf(':') + 1)..];
            var file = diagnostic.File?.Replace('\\', '/');
            return file == path || file?.EndsWith("/" + path, StringComparison.Ordinal) == true;
        }) == true).ToArray();
        foreach (var invocation in result.Status != "Succeeded" && qualityFailure && implicated.Length > 0
                     ? implicated : participants)
        {
            var failed = result.Status != "Succeeded";
            Change(missionId, invocation.Attempt.Id, attempt => attempt with
            {
                Outcome = failed ? "Failed" : tests ? "Succeeded" : attempt.Outcome,
                FailureKind = failed ? qualityFailure ? "Generation" : "Environment" : null,
                CompilationStatus = !tests ? failed ? "Failed" : "Passed" : attempt.CompilationStatus,
                TestsStatus = tests ? failed ? "Failed" : "Passed" : attempt.TestsStatus
            });
        }
    }

    private void UpdateStep(Guid id, string stepId, Func<CustomAgentModelAttempt, CustomAgentModelAttempt> update) =>
        store.Update(id, mission => mission with { Workflow = mission.Workflow! with
        {
            ModelInvocations = mission.Workflow.ModelInvocations.Select(item => item.StepId == stepId
                ? item with { Attempt = update(item.Attempt) } : item).ToArray()
        } });

    private void Change(Guid id, Guid attemptId, Func<CustomAgentModelAttempt, CustomAgentModelAttempt> update) =>
        store.Update(id, mission => mission with { Workflow = mission.Workflow! with
        {
            ModelInvocations = mission.Workflow.ModelInvocations.Select(item => item.Attempt.Id == attemptId
                ? item with { Attempt = update(item.Attempt) } : item).ToArray()
        } });
}
