using System.Text.Json;
using System.Diagnostics;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class CollaborativeProcessor(MissionStore store, IWorkflowAgents agents,
    IWorkspaceStore workspaces, IExecutionPlanService plans, ISandboxRunner sandbox, StudioOptions options)
{
    public static MissionWorkflow Create(Guid missionId, IAgentRegistry registry)
    {
        registry = registry is ConfigurableRegistry configurable ? configurable.Snapshot : registry;
        var planner = AgentPermissions.Require(registry, "Planner", "Plan");
        var developer = AgentPermissions.Require(registry, "Developer", "GenerateFiles");
        var reviewer = AgentPermissions.Require(registry, "Review", "ReadSources");
        AgentPermissions.Require(registry, "Test", "DockerBuild");
        AgentPermissions.Require(registry, "Test", "DockerTests");
        AgentPermissions.Require(registry, "Orchestrator", "Workflow");
        return new(Guid.NewGuid(), missionId, registry.Agents.ToArray(), registry.Models.ToArray(),
        [
            new("plan", "Plan", planner.Id, []),
            new("generate-1", "Generate", developer.Id, ["plan"]),
            new("review-1", "Review", reviewer.Id, ["generate-1"])
        ]);
    }

    public async Task ProcessAsync(Guid id, CancellationToken token)
    {
        if (Current().IsTerminal || Current().Status == MissionStatus.AwaitingApproval) return;
        store.Update(id, mission => mission with { Status = MissionStatus.Running, Error = null });
        while (!Current().IsTerminal && Current().Status != MissionStatus.AwaitingApproval)
        {
            token.ThrowIfCancellationRequested();
            var workflow = Current().Workflow!;
            var step = WorkflowGraph.Next(workflow);
            var agent = workflow.Agents.Single(item => item.Id == step.AgentId && item.Enabled);
            SetStep(step.Id, item => item with { Status = WorkflowStatus.Running,
                Attempts = item.Attempts + 1, StartedAt = DateTimeOffset.UtcNow, Error = null,
                Input = DescribeInput(step.Kind, workflow) });
            store.AddEvent(id, step.Kind, StepStatus.Running, $"{agent.Id}: {DescribeInput(step.Kind, workflow)}");
            try
            {
                switch (step.Kind)
                {
                    case "Plan":
                        var plan = await agents.PlanAsync(Current(), agent, token);
                        WorkflowAgents.ValidatePlan(plan);
                        Update(flow => flow with { Plan = plan });
                        store.Update(id, mission => mission with { ProjectName = plan.Name, ProjectDescription = plan.Description });
                        Complete(JsonSerializer.Serialize(plan));
                        break;
                    case "Generate":
                    case "Correct":
                        Demand(agent, "GenerateFiles");
                        var currentPlan = workflow.Plan ?? throw new StudioValidationException("Validated plan missing.");
                        var draft = (workflow.Draft ?? []).ToDictionary(file => file.Path, StringComparer.Ordinal);
                        var remaining = currentPlan.Files.Where(file => !draft.ContainsKey(file.Path)).ToList();
                        if (step.Kind == "Correct")
                        {
                            // Keep test sources immutable during automatic fixes; failing tests cannot be deleted or weakened.
                            remaining = currentPlan.Files.Where(file => !file.Path.StartsWith("tests/", StringComparison.Ordinal) ||
                                file.Path.EndsWith(".csproj", StringComparison.Ordinal)).ToList();
                        }
                        var processed = new HashSet<string>(currentPlan.Files.Select(file => file.Path).Except(remaining.Select(file => file.Path)));
                        while (remaining.Count > 0)
                        {
                            var file = remaining.FirstOrDefault(file => file.DependsOn.All(processed.Contains))
                                ?? throw new StudioValidationException("File prerequisites cannot be satisfied.");
                            var generated = await agents.GenerateFileAsync(Current(), agent, file, token);
                            if (generated.Path != file.Path)
                                throw new StudioValidationException("An agent attempted to modify an unauthorized file.");
                            draft[file.Path] = generated;
                            Update(flow => flow with { Draft = draft.Values.ToArray() });
                            store.AddEvent(id, step.Kind, StepStatus.Running, $"Generated authorized file: {file.Path}");
                            remaining.Remove(file);
                            processed.Add(file.Path);
                        }
                        if (!Current().HasWorkspace)
                        {
                            workspaces.Create(id);
                            store.Update(id, mission => mission with { HasWorkspace = true });
                        }
                        await workspaces.WriteAsync(id, draft.Values.ToArray(), token);
                        var fingerprint = (await plans.GetAsync(id, token)).Fingerprint;
                        var version = workflow.Version;
                        Update(flow => flow with { Revisions = [.. flow.Revisions ?? [],
                            new(version, DateTimeOffset.UtcNow, flow.Diagnostics ?? "Initial generation", fingerprint, draft.Values.ToArray())] });
                        store.Update(id, mission => mission with { SandboxApproved = false, ApprovalFingerprint = null });
                        Complete($"Version {version}; files: {string.Join(", ", draft.Keys)}");
                        break;
                    case "Review":
                        Demand(agent, "ReadSources");
                        if (Current().Workflow!.Draft is null)
                        {
                            var sources = new List<GeneratedFile>();
                            foreach (var file in workspaces.List(id).Where(file => file.Path != "Generated.slnx"))
                            {
                                var source = await workspaces.ReadAsync(id, file.Path, token);
                                sources.Add(new(source.Path, source.Content));
                            }
                            var allowed = workflow.Plan!.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
                            if (sources.Count != allowed.Count || sources.Any(file => !allowed.Contains(file.Path)))
                                throw new StudioValidationException("Resumed files do not match the validated plan.");
                            await workspaces.WriteAsync(id, sources, token);
                            Update(flow => flow with { Draft = sources });
                        }
                        var review = await agents.ReviewAsync(Current(), agent, token);
                        Update(flow => flow with { Review = review });
                        Complete(JsonSerializer.Serialize(review));
                        if (review.Findings.Any(finding => finding.Severity == "Error"))
                            Correct(string.Join("\n", review.Findings.Where(finding => finding.Severity == "Error")
                                .Select(finding => $"{finding.Path}: {finding.Message}")), step.Id);
                        else
                            AddVerification(step.Id);
                        break;
                    case "Approval":
                        Demand(agent, "Workflow");
                        var availability = await sandbox.GetAvailabilityAsync(token);
                        if (!availability.Available)
                        {
                            Complete(availability.Reason, WorkflowStatus.Skipped);
                            Finish(MissionStatus.GeneratedUnverified, availability.Reason);
                            break;
                        }
                        SetStep(step.Id, item => item with { Status = WorkflowStatus.WaitingForApproval });
                        store.Update(id, mission => mission with { Status = MissionStatus.AwaitingApproval,
                            SandboxApproved = false, ApprovalFingerprint = null });
                        store.AddEvent(id, "Authorization", StepStatus.Running,
                            "Review this exact file version. Approval never authorizes later modifications.");
                        break;
                    case "Build":
                    case "Tests":
                    {
                        Demand(agent, step.Kind == "Build" ? "DockerBuild" : "DockerTests");
                        var approved = Current();
                        if (!approved.SandboxApproved || approved.ApprovalFingerprint != (await plans.GetAsync(id, token)).Fingerprint)
                            throw new StudioValidationException("Files or sandbox settings changed. Resume to review sources and grant a new approval.");
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(agent.TimeoutSeconds));
                        var watch = Stopwatch.StartNew();
                        VerificationResult result;
                        try { result = await sandbox.RunAsync(id, step.Kind, timeout.Token); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
                        {
                            throw new StudioValidationException($"Agent {agent.Id} exceeded its execution timeout; container cleanup was requested.");
                        }
                        result = result with { Attempt = Current().Verification.Count(item => item.Stage == step.Kind && item.Attempt > 0) + 1,
                            DurationMilliseconds = watch.Elapsed.TotalMilliseconds };
                        if (result.Status == "Succeeded" && (result.ExitCode != 0 ||
                            (step.Kind == "Tests" && (result.TotalTests is null or <= 0 || result.FailedTests > 0))))
                            result = result with { Status = "Failed", Log = result.Log + "\nSuccessful exit and nonempty passed tests were not confirmed." };
                        store.Update(id, mission => mission with { Verification = [.. mission.Verification, result] });
                        Complete(JsonSerializer.Serialize(result), result.Status == "Succeeded" ? WorkflowStatus.Completed : WorkflowStatus.Failed);
                        if (result.Status != "Succeeded")
                        {
                            SkipPending("Verification halted.");
                            Correct(result.Log, step.Id);
                        }
                        else if (step.Kind == "Tests")
                            Finish(MissionStatus.Verified, "Compilation and non-empty tests succeeded in offline Docker.");
                        break;
                    }
                    default:
                        throw new StudioValidationException("Unknown workflow operation.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                SetStep(step.Id, item => item with { Status = WorkflowStatus.Failed, Error = exception.Message,
                    DurationMilliseconds = Elapsed(item) });
                throw;
            }

            void Complete(string output, WorkflowStatus status = WorkflowStatus.Completed)
            {
                SetStep(step.Id, item => item with { Status = status, Output = output,
                    DurationMilliseconds = Elapsed(item), Error = status == WorkflowStatus.Failed ? output : null });
                store.AddEvent(id, step.Kind, status == WorkflowStatus.Completed ? StepStatus.Succeeded :
                    status == WorkflowStatus.Skipped ? StepStatus.Skipped : StepStatus.Failed, output);
            }
        }

        MissionSnapshot Current() => store.Get(id) ?? throw new InvalidOperationException("Mission missing.");
        void Update(Func<MissionWorkflow, MissionWorkflow> update) =>
            store.Update(id, mission => mission with { Workflow = update(mission.Workflow!) });
        void SetStep(string key, Func<WorkflowStep, WorkflowStep> update) =>
            Update(flow => flow with { Steps = flow.Steps.Select(item => item.Id == key ? update(item) : item).ToArray() });
        void SkipPending(string reason) => Update(flow => flow with
        {
            Steps = flow.Steps.Select(item => item.Status == WorkflowStatus.Pending
                ? item with { Status = WorkflowStatus.Skipped, Output = reason } : item).ToArray()
        });
        string Role(string role, string tool) => AgentPermissions.Require(
            new FrozenRegistry(Current().Workflow!), role, tool).Id;
        void AddVerification(string dependency)
        {
            var suffix = Current().Workflow!.Version;
            Update(flow => flow with { Steps = [.. flow.Steps,
                new($"approval-{suffix}", "Approval", Role("Orchestrator", "Workflow"), [dependency]),
                new($"build-{suffix}", "Build", Role("Test", "DockerBuild"), [$"approval-{suffix}"]),
                new($"tests-{suffix}", "Tests", Role("Test", "DockerTests"), [$"build-{suffix}"])] });
        }
        void Correct(string diagnostics, string dependency)
        {
            var mission = Current();
            if (mission.CorrectionAttempts >= options.MaxCorrectionAttempts)
            {
                Finish(mission.Verification.LastOrDefault()?.Stage == "Tests" ? MissionStatus.TestsFailed :
                    mission.Verification.Count > 0 ? MissionStatus.BuildFailed : MissionStatus.Failed,
                    "Correction limit reached. Files, reviews and diagnostics retained.");
                return;
            }
            var suffix = mission.Workflow!.Version + 1;
            // Record analysis separately: correction prerequisites refer to completed analysis, not a failed build.
            var analysis = $"analysis-{suffix}";
            Update(flow => flow with { Version = suffix, Diagnostics = diagnostics[..Math.Min(diagnostics.Length, 12000)],
                Steps = [.. flow.Steps,
                    new(analysis, "ErrorAnalysis", Role("Orchestrator", "Workflow"), [],
                        WorkflowStatus.Completed, 1, DateTimeOffset.UtcNow, 0,
                        dependency, diagnostics),
                    new($"correct-{suffix}", "Correct", Role("Developer", "GenerateFiles"), [analysis]),
                    new($"review-{suffix}", "Review", Role("Review", "ReadSources"), [$"correct-{suffix}"])] });
            store.Update(id, item => item with { CorrectionAttempts = item.CorrectionAttempts + 1,
                SandboxApproved = false, ApprovalFingerprint = null });
        }
        void Finish(MissionStatus status, string message)
        {
            SkipPending(message);
            Update(flow => flow with { FinalReport = message, Steps = [.. flow.Steps,
                new($"report-{flow.Steps.Count}", "Report", Role("Orchestrator", "Workflow"), [],
                    WorkflowStatus.Completed, 1, DateTimeOffset.UtcNow, 0, null, message)] });
            store.Update(id, mission => mission with { Status = status, Response = message,
                Error = status is MissionStatus.Failed or MissionStatus.BuildFailed or MissionStatus.TestsFailed ? message : null });
            store.AddEvent(id, "Mission", status == MissionStatus.Verified ? StepStatus.Succeeded :
                status == MissionStatus.GeneratedUnverified ? StepStatus.Unavailable : StepStatus.Failed, message);
        }
    }

    private static double? Elapsed(WorkflowStep step) => step.StartedAt is { } start
        ? (DateTimeOffset.UtcNow - start).TotalMilliseconds : null;
    private static void Demand(AgentDefinition agent, string tool)
    {
        if (!agent.AllowedTools.Contains(tool) || !AgentPermissions.ToolsFor(agent.Role).Contains(tool))
            throw new StudioValidationException("Agent operation is forbidden by server policy.");
    }
    private static string DescribeInput(string kind, MissionWorkflow flow) => kind switch
    {
        "Plan" => "Original request; bounded .NET 10 planning schema.",
        "Generate" or "Correct" => $"Validated plan, explicit paths and direct prerequisites; version {flow.Version}.",
        "Review" => $"Validated plan and generated source; version {flow.Version}. Read only.",
        "Approval" => $"Human decision required for version {flow.Version}.",
        _ => $"Approved workspace fingerprint; version {flow.Version}."
    };

    private sealed class FrozenRegistry(MissionWorkflow workflow) : IAgentRegistry
    {
        public IReadOnlyList<AgentDefinition> Agents => workflow.Agents;
        public IReadOnlyList<AgentDefinition> Candidates => workflow.Agents;
        public IReadOnlyList<ModelDefinition> Models => workflow.Models;
        public AgentDefinition Router => throw new NotSupportedException();
        public AgentDefinition Fallback => throw new NotSupportedException();
        public ModelDefinition GetModel(string id) => Models.Single(model => model.Id == id);
    }
}
