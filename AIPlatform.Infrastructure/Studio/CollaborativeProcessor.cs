using System.Text.Json;
using System.Diagnostics;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed partial class CollaborativeProcessor(MissionStore store, IWorkflowAgents agents,
    IWorkspaceStore workspaces, IExecutionPlanService plans, ISandboxRunner sandbox, StudioOptions options,
    WorkflowModelRouting? modelRouting = null, IReactSourceCheck? reactSourceCheck = null,
    IReactSourceRepair? reactSourceRepair = null)
{
    public static MissionWorkflow Create(Guid missionId, IAgentRegistry registry, int maxCorrectionAttempts = 3,
        string? templateId = null)
    {
        registry = registry is ConfigurableRegistry configurable ? configurable.Snapshot : registry;
        var planner = AgentPermissions.Require(registry, "Planner", "Plan");
        var developer = templateId == FullStackTemplate.Id ? null :
            AgentPermissions.Require(registry, "Developer", "GenerateFiles");
        if (templateId == FullStackTemplate.Id)
        {
            AgentPermissions.Require(registry, "Backend", "GenerateFiles");
            AgentPermissions.Require(registry, "Frontend", "GenerateFiles");
        }
        var reviewer = AgentPermissions.Require(registry, "Review", "ReadSources");
        AgentPermissions.Require(registry, "Test", "DockerBuild");
        AgentPermissions.Require(registry, "Test", "DockerTests");
        var orchestrator = AgentPermissions.Require(registry, "Orchestrator", "Workflow");
        var initialSteps = templateId == FullStackTemplate.Id
            ? new[]
            {
                new WorkflowStep("plan", "Plan", planner.Id, []),
                new WorkflowStep("contract", "Contract", orchestrator.Id, ["plan"]),
                new WorkflowStep("backend-generate", "BackendGenerate",
                    AgentPermissions.Require(registry, "Backend", "GenerateFiles").Id, ["contract"]),
                new WorkflowStep("frontend-generate", "FrontendGenerate",
                    AgentPermissions.Require(registry, "Frontend", "GenerateFiles").Id, ["backend-generate"]),
                new WorkflowStep("review-1", "Review", reviewer.Id, ["frontend-generate"])
            }
            :
            [
                new("plan", "Plan", planner.Id, []),
                new("generate-1", "Generate", developer!.Id, ["plan"]),
                new("review-1", "Review", reviewer.Id, ["generate-1"])
            ];
        return new(Guid.NewGuid(), missionId, registry.Agents.ToArray(), registry.Models.ToArray(),
            initialSteps, CorrectionLimit: maxCorrectionAttempts);
    }

    public async Task ProcessAsync(Guid id, CancellationToken token)
    {
        using var generationScope = store.EnterGenerationScope(id, "Workflow");
        if (Current().IsTerminal || Current().Status is MissionStatus.AwaitingApproval or MissionStatus.AwaitingPlanApproval) return;
        store.Update(id, mission => mission with { Status = MissionStatus.Running, Error = null });
        while (!Current().IsTerminal && Current().Status is not (MissionStatus.AwaitingApproval or MissionStatus.AwaitingPlanApproval))
        {
            token.ThrowIfCancellationRequested();
            var workflow = Current().Workflow!;
            if (workflow.AutomaticReactCorrections && reactSourceCheck is null)
                throw new StudioValidationException("Trusted React static checks are unavailable; autonomous workflow blocked.");
            var step = WorkflowGraph.Next(workflow);
            var agent = workflow.Agents.Single(item => item.Id == step.AgentId && item.Enabled);
            var boundedPlanner = step.Kind == "Plan" && workflow.PlannerGenerationLimit > 0;
            var correctingReviewer = step.Kind == "Review" && workflow.ReviewerCorrectionEnabled;
            var retryingReviewer = correctingReviewer && step.Attempts > 0 && step.Error is not null;
            var correctionLimit = options.CorrectionLimitFor(Current());
            if (boundedPlanner && (workflow.CountPlannerAttempts() >= Math.Min(3, workflow.PlannerGenerationLimit) ||
                !workflow.AutomaticModelSelection && step.Attempts > 0 && Current().CorrectionAttempts >= correctionLimit))
            {
                const string exhausted = "Planner generation/correction budget exhausted; retained diagnostics require explicit inspection.";
                SetStep(step.Id, item => item with { Status = WorkflowStatus.Failed, Error = exhausted });
                throw new StudioValidationException(exhausted);
            }
            if (retryingReviewer && !workflow.AutomaticModelSelection &&
                Current().CorrectionAttempts >= correctionLimit)
                throw new StudioValidationException("Reviewer correction budget exhausted; retained diagnostics require explicit inspection.");
            if (step.Kind != "Plan") store.RequireApprovedPlan(id);
            if (step.Kind == "Review" && workflow.ReviewerCorrectionResume is
                { Status: "Queued", StepId: var resumeStepId } && resumeStepId == step.Id)
                Update(flow => flow with
                {
                    ReviewerCorrectionResume = flow.ReviewerCorrectionResume! with { Status = "Running" }
                });
            SetStep(step.Id, item => item with { Status = WorkflowStatus.Running,
                Attempts = item.Attempts + (workflow.AutomaticModelSelection &&
                    step.Kind is "Plan" or "Generate" or "BackendGenerate" or "FrontendGenerate" or
                        "Correct" or "BackendCorrect" or "FrontendCorrect" or "Review" ? 0 : 1),
                StartedAt = DateTimeOffset.UtcNow, Error = null,
                Input = DescribeInput(step.Kind, workflow) });
            store.AddEvent(id, step.Kind, StepStatus.Running, $"{agent.Id}: {DescribeInput(step.Kind, workflow)}");
            try
            {
                switch (step.Kind)
                {
                    case "Plan":
                        if (boundedPlanner && !workflow.AutomaticModelSelection && step.Attempts > 0)
                            store.Update(id, mission => mission with { CorrectionAttempts = mission.CorrectionAttempts + 1 });
                        BeginSoftwareAgentCall(id, step.Id);
                        var plan = await agents.PlanAsync(Current(), agent, token);
                        WorkflowAgents.ValidatePlan(plan, Current().TemplateId, Current().Sizing);
                        var plannedDraft = Current().TemplateId switch
                        {
                            MissionReactTemplate.Id => MissionReactTemplate.Files,
                            ReactTemplate.Id => ReactTemplate.Files.Where(file => !ReactTemplate.IsEditable(file.Path)).ToArray(),
                            DiscordTemplate.Id => DiscordTemplate.Files.Where(file => file.Path != DiscordTemplate.EditableFile).ToArray(),
                            TwitchTemplate.Id => TwitchTemplate.Files.Where(file => !TwitchTemplate.IsEditable(file.Path)).ToArray(),
                            FullStackTemplate.Id => FullStackTemplate.Files.Where(file => !FullStackTemplate.IsEditable(file.Path)).ToArray(),
                            _ => workflow.Draft
                        };
                        var editableCount = plan.Files.Count(file => plannedDraft?.All(source => source.Path != file.Path) != false);
                        store.WarnGenerationBudget(id, editableCount +
                            (Current().TemplateId == FullStackTemplate.Id ? editableCount : 1));
                        if (workflow.RequirePlanApproval)
                        {
                            if (workflow.AutomaticModelSelection) modelRouting!.CompleteStep(id, step.Id);
                            store.PauseForPlanApproval(id, plan, plannedDraft);
                            Update(flow => flow with { PendingSoftwareAgentCall = null });
                            return;
                        }
                        Update(flow => flow with { Plan = plan, Draft = plannedDraft, PendingSoftwareAgentCall = null });
                        store.Update(id, mission => mission with { ProjectName = plan.Name, ProjectDescription = plan.Description });
                        Complete(JsonSerializer.Serialize(plan));
                        break;
                    case "Contract":
                        Demand(agent, "Workflow");
                        if (!FullStackTemplate.Files.Any(file => file.Path == "contracts/openapi.json") ||
                            FullStackTemplate.Files.Single(file => file.Path == "contracts/openapi.json").Content.Length == 0)
                            throw new StudioValidationException("The fixed full-stack API contract is unavailable.");
                        Complete("The versioned OpenAPI contract is fixed and shared with both specialist agents.");
                        break;
                    case "Generate":
                    case "BackendGenerate":
                    case "FrontendGenerate":
                    case "Correct":
                    case "BackendCorrect":
                    case "FrontendCorrect":
                    {
                        var fullStack = Current().TemplateId == FullStackTemplate.Id;
                        var correction = step.Kind is "Correct" or "BackendCorrect" or "FrontendCorrect";
                        var role = fullStack ? step.Kind switch
                        {
                            "BackendGenerate" or "BackendCorrect" => "Backend",
                            "FrontendGenerate" or "FrontendCorrect" => "Frontend",
                            _ => agent.Role == "Frontend" ? "Frontend" : "Backend"
                        } : agent.Role;
                        Demand(agent, "GenerateFiles");
                        var currentPlan = workflow.Plan ?? throw new StudioValidationException("Validated plan missing.");
                        var draft = (correction ? workflow.Proposal ?? workflow.Draft ?? [] :
                            workflow.Draft ?? []).ToDictionary(file => file.Path, StringComparer.Ordinal);
                        var remaining = currentPlan.Files.Where(file => !draft.ContainsKey(file.Path) &&
                            (!fullStack || FullStackTemplate.IsEditableForRole(file.Path, role))).ToList();
                        if (correction)
                        {
                            var lastRevision = workflow.Revisions?.LastOrDefault()
                                ?? throw new StudioValidationException("A committed revision is required before correction.");
                            var currentExecutionPlan = await plans.GetAsync(id, token, lastRevision.Version);
                            var planDifferences = ExecutionPlanRevisionGuard.Compare(
                                lastRevision, currentExecutionPlan, Current().TemplateId);
                            Update(flow => flow with
                            {
                                ExecutionPlanDifferences = planDifferences.Count == 0 ? null : planDifferences
                            });
                            if (planDifferences.Count > 0)
                                throw new StudioValidationException("Workspace or sandbox settings changed since review; resume to review the current files.");
                            remaining = currentPlan.Files.Where(file => (workflow.CorrectionPaths ?? []).Contains(file.Path)).ToList();
                            if (fullStack)
                                remaining = remaining.Where(file => FullStackTemplate.IsEditableForRole(file.Path, role)).ToList();
                            if (remaining.Count == 0)
                                throw new StudioValidationException("No authorized application source can be corrected.");
                        }
                        var generatedFileCount = remaining.Count;
                        if (workflow.AutomaticReactCorrections && correction)
                        {
                            var repaired = await RepairReactCandidateAsync(id, step.Id, draft.Values.ToArray(),
                                workflow.CorrectionPaths ?? [], workflow.Diagnostics ?? "Reviewer correction", token);
                            draft = repaired.ToDictionary(file => file.Path, StringComparer.Ordinal);
                            remaining.Clear();
                        }
                        var processed = new HashSet<string>(currentPlan.Files.Select(file => file.Path).Except(remaining.Select(file => file.Path)));
                        while (remaining.Count > 0)
                        {
                            if (workflow.AutomaticReactCorrections && Remaining(id) < remaining.Count + 1)
                                throw new StudioValidationException("Generation budget cannot cover the remaining files and at least one Reviewer call.");
                            var file = remaining.FirstOrDefault(file => file.DependsOn.All(processed.Contains))
                                ?? throw new StudioValidationException("File prerequisites cannot be satisfied.");
                            var input = Current();
                            var fileAgent = fullStack
                                ? workflow.Agents.Single(item => item.Enabled && item.Role ==
                                    (FullStackTemplate.IsEditableForRole(file.Path, "Backend") ? "Backend" : "Frontend"))
                                : agent;
                            Demand(fileAgent, "GenerateFiles");
                            if (correction)
                                input = input with { Workflow = input.Workflow! with { Draft = draft.Values.ToArray() } };
                            BeginSoftwareAgentCall(id, step.Id + ":" + file.Path);
                            var generated = await agents.GenerateFileAsync(input, fileAgent, file, token);
                            if (generated.Path != file.Path)
                                throw new StudioValidationException("An agent attempted to modify an unauthorized file.");
                            draft[file.Path] = generated;
                            Update(flow => correction
                                ? flow with { Proposal = draft.Values.ToArray(), PendingSoftwareAgentCall = null }
                                : flow with { Draft = draft.Values.ToArray(), PendingSoftwareAgentCall = null });
                            store.AddEvent(id, step.Kind, StepStatus.Running, $"Generated authorized file: {file.Path}");
                            remaining.Remove(file);
                            processed.Add(file.Path);
                        }
                        workspaces.ValidateFiles(draft.Values.ToArray(), Current().TemplateId,
                            allowPartialTemplate: fullStack && step.Kind == "BackendGenerate", sizing: Current().Sizing);
                        if (Current().TemplateId == MissionReactTemplate.Id && reactSourceCheck is not null)
                        {
                            if (workflow.AutomaticReactCorrections && !correction)
                            {
                                var repaired = await RepairReactCandidateAsync(id, step.Id + ":static",
                                    draft.Values.ToArray(), [], "Static diagnostics", token);
                                draft = repaired.ToDictionary(file => file.Path, StringComparer.Ordinal);
                                Update(flow => flow with { Draft = repaired });
                            }
                            var expected = currentPlan.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
                            if (!expected.SetEquals(draft.Keys))
                                throw new StudioValidationException("The React draft does not match the approved file manifest.");
                            var issues = await reactSourceCheck.CheckAsync(draft.Values.ToArray(), token);
                            if (issues.Count > 0 && !correction && !workflow.AutomaticReactCorrections)
                            {
                                var affected = issues.Select(issue => issue.File).Distinct(StringComparer.Ordinal).ToArray();
                                var mission = Current();
                                if (affected.All(path => expected.Contains(path) && MissionReactTemplate.IsEditable(path)) &&
                                    mission.CorrectionAttempts < options.CorrectionLimitFor(mission) &&
                                    (mission.MaxTotalGenerations is not { } limit ||
                                        limit - (mission.GenerationReservations?.Count ?? 0) >= affected.Length))
                                {
                                    store.Update(id, item => item with { CorrectionAttempts = item.CorrectionAttempts + 1 });
                                    foreach (var target in currentPlan.Files.Where(file => affected.Contains(file.Path, StringComparer.Ordinal)))
                                    {
                                        var input = Current();
                                        var relevant = ReactSourceCheck.Describe(issues.Where(issue => issue.File == target.Path).Take(12));
                                        input = input with { Workflow = input.Workflow! with
                                        { Draft = draft.Values.ToArray(), Diagnostics = relevant } };
                                        var repaired = await agents.GenerateFileAsync(input, agent, target, token);
                                        if (repaired.Path != target.Path)
                                            throw new StudioValidationException("The React repair attempted to modify an unauthorized file.");
                                        draft[target.Path] = repaired;
                                    }
                                    workspaces.ValidateFiles(draft.Values.ToArray(), MissionReactTemplate.Id, sizing: Current().Sizing);
                                    Update(flow => flow with { Draft = draft.Values.ToArray() });
                                    issues = await reactSourceCheck.CheckAsync(draft.Values.ToArray(), token);
                                }
                            }
                            if (issues.Count > 0)
                                throw new StudioValidationException("React source checks blocked publication:\n" +
                                    ReactSourceCheck.Describe(issues.Take(20)));
                        }
                        if (correction)
                        {
                            Update(flow => flow with { Proposal = draft.Values.ToArray() });
                            var otherCorrectionsPending = Current().Workflow!.Steps.Any(item =>
                                item.Id.StartsWith($"correct-{workflow.Version}-", StringComparison.Ordinal) &&
                                item.Id != step.Id && item.Status == WorkflowStatus.Pending);
                            if (otherCorrectionsPending)
                            {
                                Complete($"Prepared {role} correction for version {workflow.Version}.");
                                break;
                            }
                            Update(flow => flow with { Proposals = [.. (flow.Proposals ?? []).Where(item => item.Version != flow.Version),
                                new(flow.Version, DateTimeOffset.UtcNow, draft.Values.ToArray())] });
                            Complete($"Proposed version {workflow.Version}; files: {string.Join(", ", workflow.CorrectionPaths ?? [])}. Review required before application.");
                            break;
                        }
                        if (fullStack && step.Kind == "BackendGenerate")
                        {
                            Complete($"BackendAgent generated {generatedFileCount} backend files; frontend generation follows.");
                            break;
                        }
                        if (!Current().HasWorkspace)
                        {
                            workspaces.Create(id);
                            store.Update(id, mission => mission with { HasWorkspace = true });
                        }
                        await workspaces.WriteAsync(id, draft.Values.ToArray(), token, Current().TemplateId, Current().Sizing);
                        var generatedPlan = await plans.GetAsync(id, token);
                        var version = workflow.Version;
                        var revisionFiles = draft.Values.ToArray();
                        Update(flow => flow with { Revisions = [.. flow.Revisions ?? [],
                            new(version, DateTimeOffset.UtcNow, flow.Diagnostics ?? "Initial generation",
                                generatedPlan.Fingerprint, revisionFiles, PersistExecutionPlan(generatedPlan, revisionFiles))],
                            ExecutionPlanDifferences = null });
                        store.Update(id, mission => mission with { SandboxApproved = false, ApprovalFingerprint = null });
                        Complete($"Version {version}; files: {string.Join(", ", draft.Keys)}");
                        break;
                    }
                    case "Review":
                        if (retryingReviewer && !workflow.AutomaticModelSelection)
                            store.Update(id, mission => mission with { CorrectionAttempts = mission.CorrectionAttempts + 1 });
                        Demand(agent, "ReadSources");
                        if (Current().Workflow!.Draft is null)
                        {
                            var sources = new List<GeneratedFile>();
                            foreach (var file in workspaces.List(id).Where(file => file.Path != "Generated.slnx"))
                            {
                                var source = await workspaces.ReadAsync(id, file.Path, token);
                                sources.Add(new(source.Path, source.Content));
                            }
                            var allowed = Current().TemplateId == FullStackTemplate.Id
                                ? FullStackTemplate.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal)
                                : workflow.Plan!.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
                            if (sources.Count != allowed.Count || sources.Any(file => !allowed.Contains(file.Path)))
                                throw new StudioValidationException("Resumed files do not match the validated plan.");
                            if (Current().TemplateId == MissionReactTemplate.Id && reactSourceCheck is not null)
                                await RequireCleanReactSources(sources);
                            await workspaces.WriteAsync(id, sources, token, Current().TemplateId, Current().Sizing);
                            Update(flow => flow with { Draft = sources });
                        }
                        var reviewMission = Current();
                        var proposal = reviewMission.Workflow!.Proposal;
                        if (proposal is not null)
                            reviewMission = reviewMission with { Workflow = reviewMission.Workflow! with { Draft = proposal } };
                        var reviewedPlan = await plans.GetAsync(id, token);
                        var reviewedFingerprint = reviewedPlan.Fingerprint;
                        if (workflow.AutomaticReactCorrections &&
                            Remaining(id) < WorkflowAgents.PreviewReactReview(reviewMission, token).Count)
                            throw new StudioValidationException("The budget does not cover the complete Reviewer coverage; criteria were not reduced.");
                        BeginSoftwareAgentCall(id, step.Id);
                        var review = await agents.ReviewAsync(reviewMission, agent, token);
                        if (Current().TemplateId == MissionReactTemplate.Id)
                        {
                            MissionReactTemplate.ValidateReview(review, reviewMission);
                            var unmet = review.CriterionReviews!.Where(item => item.Verdict == "NotSatisfied").ToArray();
                            if (unmet.Length > 0)
                            {
                                var affected = Current().Workflow!.Plan!.CriterionCoverage!
                                    .Where(coverage => unmet.Any(item => item.CriterionId == coverage.CriterionId))
                                    .SelectMany(coverage => coverage.Paths).Where(path => !ReactTemplate.IsTest(path))
                                    .Distinct(StringComparer.Ordinal);
                                review = review with { Findings = [.. review.Findings, .. affected.Select(path =>
                                    new ReviewFinding(path, "Error", "Original acceptance criteria not satisfied: " +
                                        string.Join(", ", unmet.Select(item => item.CriterionId))))] };
                            }
                        }
                        if (proposal is not null)
                        {
                            var previous = Current().Workflow!.Revisions?.LastOrDefault()?.Files
                                ?? throw new StudioValidationException("Committed revision missing.");
                            review = review with { ModifiedFiles = proposal.Where(file =>
                                previous.FirstOrDefault(old => old.Path == file.Path)?.Content != file.Content)
                                .Select(file => file.Path).ToArray() };
                        }
                        Update(flow => flow with { Review = review, PendingSoftwareAgentCall = null,
                            Proposals = flow.Proposals?.Select(item => item.Version == flow.Version
                                ? item with { Review = review } : item).ToArray() });
                        if (workflow.AutomaticModelSelection) modelRouting!.RecordReview(id, review);
                        if (review.Findings.Any(finding => finding.Severity == "Error"))
                        {
                            Complete(JsonSerializer.Serialize(review));
                            var findings = string.Join("\n", review.Findings.Where(finding => finding.Severity == "Error")
                                .Select(finding => $"{finding.Path}: {finding.Message}"));
                            if (Current().TemplateId == FullStackTemplate.Id)
                                Finish(MissionStatus.GeneratedUnverified, "Review failed; human review is required. No automatic correction was started.\n" + findings);
                            else if (Current().TemplateId == MissionReactTemplate.Id && !workflow.AutomaticReactCorrections)
                                Finish(MissionStatus.GeneratedUnverified,
                                    "Reviewer findings require independent verification. No Developer correction or generated-code execution was started.\n" +
                                    findings);
                            else
                                Correct(findings, step.Id);
                        }
                        else
                        {
                            if ((await plans.GetAsync(id, token)).Fingerprint != reviewedFingerprint)
                                throw new StudioValidationException("Workspace changed during review; review must be repeated.");
                            if (proposal is not null)
                            {
                                var lastRevision = Current().Workflow!.Revisions?.LastOrDefault()
                                    ?? throw new StudioValidationException("Committed revision missing.");
                                if ((await plans.GetAsync(id, token, lastRevision.Version)).Fingerprint != lastRevision.Fingerprint &&
                                    !await IsInterruptedProposalAsync(id, lastRevision.Files, proposal, token))
                                    throw new StudioValidationException("Workspace changed since the proposal was prepared.");
                                workspaces.ValidateFiles(proposal, Current().TemplateId, sizing: Current().Sizing);
                                if (Current().TemplateId == MissionReactTemplate.Id && reactSourceCheck is not null)
                                    await RequireCleanReactSources(proposal);
                                await workspaces.WriteAsync(id, proposal, token, Current().TemplateId, Current().Sizing);
                                var newPlan = await plans.GetAsync(id, token);
                                Update(flow => flow with { Draft = proposal, Proposal = null,
                                    Revisions = [.. flow.Revisions ?? [], new(flow.Version, DateTimeOffset.UtcNow,
                                        flow.Diagnostics ?? "Correction", newPlan.Fingerprint, proposal,
                                        PersistExecutionPlan(newPlan, proposal))],
                                    ExecutionPlanDifferences = null });
                            }
                            else if (Current().Workflow!.Revisions?.LastOrDefault() is not { } latest ||
                                     latest.Fingerprint != reviewedFingerprint || latest.Version != Current().Workflow!.Version)
                            {
                                var revisionFiles = Current().Workflow!.Draft!;
                                if (Current().TemplateId == MissionReactTemplate.Id && reactSourceCheck is not null)
                                    await RequireCleanReactSources(revisionFiles);
                                Update(flow => flow with { Revisions = [.. flow.Revisions ?? [],
                                    new(flow.Version, DateTimeOffset.UtcNow, "Explicitly resumed review", reviewedFingerprint, revisionFiles,
                                        PersistExecutionPlan(reviewedPlan, revisionFiles))],
                                    ExecutionPlanDifferences = null });
                            }
                            Complete(JsonSerializer.Serialize(review));
                            AddVerification(step.Id);
                        }
                        break;
                    case "Approval":
                        Demand(agent, "Workflow");
                        var availability = await sandbox.GetAvailabilityAsync(id, token);
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
                    case "TypeScript":
                    case "Build":
                    case "Tests":
                    case "BackendBuild":
                    case "BackendTests":
                    case "FrontendBuild":
                    case "FrontendTests":
                    case "Integration":
                    {
                        var isTestStage = step.Kind is "Tests" or "BackendTests" or "FrontendTests" or "Integration";
                        Demand(agent, isTestStage ? "DockerTests" : "DockerBuild");
                        var approved = Current();
                        var actualPlan = await plans.GetAsync(id, token);
                        if (!approved.SandboxApproved || approved.ApprovalFingerprint != actualPlan.Fingerprint ||
                            (approved.TemplateId == FullStackTemplate.Id &&
                             (approved.Workflow?.Revisions?.LastOrDefault() is not { ExecutionPlan: { } saved } ||
                              saved.Fingerprint != actualPlan.Fingerprint ||
                              saved.Canonical?.ComputeFingerprint() != actualPlan.Fingerprint)))
                        {
                            store.Update(id, item => item with { SandboxApproved = false, ApprovalFingerprint = null });
                            throw new StudioValidationException("Files or sandbox settings changed. Resume to review sources and grant a new approval.");
                        }
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
                            (isTestStage && (result.TotalTests is null or <= 0 || result.FailedTests != 0))))
                            result = result with { Status = "Failed", Log = result.Log + "\nSuccessful exit and nonempty passed tests were not confirmed." };
                        if (result.Status == "Failed")
                            result = result with { Diagnostics = DiagnosticParser.Parse(result, Current().TemplateId) };
                        store.Update(id, mission => mission with { Verification = [.. mission.Verification, result] });
                        if (workflow.AutomaticModelSelection) modelRouting!.RecordVerification(id, result);
                        Complete(JsonSerializer.Serialize(result), result.Status == "Succeeded" ? WorkflowStatus.Completed : WorkflowStatus.Failed);
                        if (result.Status != "Succeeded")
                        {
                            SkipPending("Verification halted.");
                            if (workflow.AutomaticReactCorrections && (result.Diagnostics is null || !result.Diagnostics.Items.Any(issue =>
                                issue.File is { } file && workflow.Plan!.Files.Any(planned =>
                                    MissionReactTemplate.IsEditable(planned.Path) &&
                                    (file.Replace('\\', '/') == planned.Path ||
                                     file.Replace('\\', '/').EndsWith("/" + planned.Path, StringComparison.Ordinal))))))
                            {
                                store.Update(id, item => item with { SandboxApproved = false, ApprovalFingerprint = null });
                                Finish(isTestStage ? MissionStatus.TestsFailed : MissionStatus.BuildFailed,
                                    "Validation infrastructure or unscoped diagnostics require inspection; no speculative Developer correction was started.");
                                break;
                            }
                            if (Current().TemplateId == FullStackTemplate.Id)
                            {
                                store.Update(id, item => item with { SandboxApproved = false, ApprovalFingerprint = null });
                                Finish(isTestStage ? MissionStatus.TestsFailed : MissionStatus.BuildFailed,
                                    "Validation failed. Inspect the retained diagnostics and distinguish infrastructure from generated code before proposing a correction; no automatic correction was started.");
                            }
                            else
                                Correct(JsonSerializer.Serialize(result.Diagnostics), step.Id);
                        }
                        else if (step.Kind == (Current().TemplateId == FullStackTemplate.Id ? "Integration" : "Tests"))
                        {
                            var required = Current().TemplateId == FullStackTemplate.Id
                                ? new[] { "BackendBuild", "BackendTests", "TypeScript", "FrontendBuild", "FrontendTests", "Integration" }
                                : Current().TemplateId is ReactTemplate.Id or MissionReactTemplate.Id
                                    ? new[] { "TypeScript", "Build", "Tests" } : ["Build", "Tests"];
                            if (required.Any(kind => !Current().Workflow!.Steps.Any(item =>
                                item.Kind == kind && item.Id.EndsWith($"-{Current().Workflow!.Version}", StringComparison.Ordinal) &&
                                item.Status == WorkflowStatus.Completed)))
                                throw new StudioValidationException("All required validation stages must pass for this version.");
                            if (Current().TemplateId == MissionReactTemplate.Id)
                            {
                                var executions = MissionReactTemplate.Executions(Current(), result, actualPlan.Fingerprint);
                                Update(flow => flow with { CriterionExecutions = [.. flow.CriterionExecutions, .. executions] });
                                var covered = Current().Workflow!.AcceptanceCriteria.All(criterion =>
                                    Current().Workflow!.Review?.CriterionReviews?.Any(item =>
                                        item.CriterionId == criterion.Id && item.Verdict == "Satisfied") == true &&
                                    executions.Any(item => item.CriterionId == criterion.Id));
                                if (!covered)
                                {
                                    Finish(MissionStatus.GeneratedUnverified,
                                        "Compilation/tests completed, but not every original criterion has a favorable static review and matching executed business test. Functional validation is incomplete.");
                                    break;
                                }
                            }
                            Finish(MissionStatus.Verified, Current().TemplateId == FullStackTemplate.Id
                                ? "Backend Release build/xUnit, TypeScript, Vite, Vitest and real local API integration succeeded in offline Docker."
                                : "Required compilation stages and non-empty tests succeeded in offline Docker.");
                        }
                        break;
                    }
                    default:
                        throw new StudioValidationException("Unknown workflow operation.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (exception is GenerationBudgetExhaustedException)
                {
                    Update(flow => flow with { Steps = flow.Steps.Select(item => item.Id == step.Id
                            ? item with { Status = WorkflowStatus.Failed, Error = exception.Message } : item).ToArray(),
                        ReviewerCorrectionResume = flow.ReviewerCorrectionResume is
                            { Status: "Running", StepId: var resumeStepId } && resumeStepId == step.Id
                                ? flow.ReviewerCorrectionResume with { Status = "Failed" }
                                : flow.ReviewerCorrectionResume,
                        ModelInvocations = flow.ModelInvocations.Select(item => item.StepId == step.Id && item.Attempt.Outcome == "Running"
                            ? item with { Attempt = item.Attempt with { Outcome = "Blocked",
                                FailureKind = "GenerationBudgetExhausted", CompletedAt = DateTimeOffset.UtcNow } } : item).ToArray() });
                    throw;
                }
                var routedRetry = workflow.AutomaticModelSelection &&
                    modelRouting!.FailStep(id, step.Id, exception, workflow.ModelInvocations.Count);
                if (boundedPlanner && exception is StudioValidationException { PlannerDiagnostics: not null })
                    store.AddEvent(id, "PlanValidation", StepStatus.Failed,
                        $"Planner attempt {Current().Workflow!.CountPlannerAttempts()}/" +
                        $"{Math.Min(3, workflow.PlannerGenerationLimit)} rejected; exact diagnostics retained privately.");
                if (correctingReviewer && exception is StudioValidationException { ReviewerDiagnostics: not null })
                    store.AddEvent(id, "ReviewerValidation", StepStatus.Failed,
                        "Reviewer report rejected; exact fields and citations retained privately. This is not a finding against application code.");
                var retry = routedRetry || boundedPlanner && !workflow.AutomaticModelSelection &&
                    exception is StudioValidationException { PlannerDiagnostics.Count: > 0 } &&
                    Current().CorrectionAttempts < correctionLimit ||
                    correctingReviewer && !workflow.AutomaticModelSelection &&
                    exception is StudioValidationException { ReviewerDiagnostics.Count: > 0 } &&
                    Current().CorrectionAttempts < correctionLimit;
                if (workflow.AutomaticReactCorrections && Current().Workflow!.PendingSoftwareAgentCall is not null)
                {
                    if (exception is StudioValidationException { PlannerDiagnostics.Count: > 0 } or
                        StudioValidationException { ReviewerDiagnostics.Count: > 0 })
                        Update(flow => flow with { PendingSoftwareAgentCall = null });
                    else retry = false;
                }
                if (boundedPlanner)
                    retry &= Current().Workflow!.CountPlannerAttempts() <
                        Math.Min(3, workflow.PlannerGenerationLimit);
                if (retry)
                {
                    SetStep(step.Id, item => item with { Status = WorkflowStatus.Pending, Error = exception.Message });
                    store.AddEvent(id, boundedPlanner ? "PlanCorrection" : correctingReviewer ? "ReviewerCorrection" : "ModelSelection", StepStatus.Failed,
                        boundedPlanner ? "Retrying the Planner within retained generation and correction budgets; no application code is generated." :
                        correctingReviewer ? "Retrying the Reviewer within the shared budget; only its report is corrected, not application files." :
                        "Generation rejected; bounded retry uses the retained model policy and counters.");
                    continue;
                }
                Update(flow => flow with
                {
                    Steps = flow.Steps.Select(item => item.Id == step.Id
                        ? item with { Status = WorkflowStatus.Failed, Error = exception.Message,
                            DurationMilliseconds = Elapsed(item) } : item).ToArray(),
                    ReviewerCorrectionResume = flow.ReviewerCorrectionResume is
                        { Status: "Running", StepId: var resumeStepId } && resumeStepId == step.Id
                            ? flow.ReviewerCorrectionResume with { Status = "Failed" }
                            : flow.ReviewerCorrectionResume
                });
                throw;
            }

            void Complete(string output, WorkflowStatus status = WorkflowStatus.Completed)
            {
                if (workflow.AutomaticModelSelection) modelRouting!.CompleteStep(id, step.Id);
                Update(flow => flow with
                {
                    Steps = flow.Steps.Select(item => item.Id == step.Id
                        ? item with { Status = status, Output = output,
                            DurationMilliseconds = Elapsed(item), Error = status == WorkflowStatus.Failed ? output : null }
                        : item).ToArray(),
                    ReviewerCorrectionResume = status == WorkflowStatus.Completed &&
                        flow.ReviewerCorrectionResume is { Status: "Running", StepId: var resumeStepId } &&
                        resumeStepId == step.Id
                            ? flow.ReviewerCorrectionResume with { Status = "Completed" }
                            : flow.ReviewerCorrectionResume
                });
                store.AddEvent(id, step.Kind, status == WorkflowStatus.Completed ? StepStatus.Succeeded :
                    status == WorkflowStatus.Skipped ? StepStatus.Skipped : StepStatus.Failed, output);
            }
        }

        MissionSnapshot Current() => store.Get(id) ?? throw new InvalidOperationException("Mission missing.");
        async Task RequireCleanReactSources(IReadOnlyList<GeneratedFile> files)
        {
            var expected = Current().Workflow!.Plan!.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
            if (!expected.SetEquals(files.Select(file => file.Path)))
                throw new StudioValidationException("The React draft does not match the approved file manifest.");
            var issues = await reactSourceCheck!.CheckAsync(files, token);
            if (issues.Count > 0)
                throw new StudioValidationException("React source checks blocked publication:\n" +
                    ReactSourceCheck.Describe(issues.Take(20)));
        }
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
            var react = Current().TemplateId is ReactTemplate.Id or MissionReactTemplate.Id;
            if (Current().TemplateId == FullStackTemplate.Id)
            {
                Update(flow => flow with { Steps = [.. flow.Steps,
                    new($"approval-{suffix}", "Approval", Role("Orchestrator", "Workflow"), [dependency]),
                    new($"backend-build-{suffix}", "BackendBuild", Role("Test", "DockerBuild"), [$"approval-{suffix}"]),
                    new($"backend-tests-{suffix}", "BackendTests", Role("Test", "DockerTests"), [$"backend-build-{suffix}"]),
                    new($"typescript-{suffix}", "TypeScript", Role("Test", "DockerBuild"), [$"backend-tests-{suffix}"]),
                    new($"frontend-build-{suffix}", "FrontendBuild", Role("Test", "DockerBuild"), [$"typescript-{suffix}"]),
                    new($"frontend-tests-{suffix}", "FrontendTests", Role("Test", "DockerTests"), [$"frontend-build-{suffix}"]),
                    new($"integration-{suffix}", "Integration", Role("Test", "DockerTests"), [$"frontend-tests-{suffix}"])] });
                return;
            }
            Update(flow => flow with { Steps = [.. flow.Steps,
                new($"approval-{suffix}", "Approval", Role("Orchestrator", "Workflow"), [dependency]),
                .. (react ? new[] { new WorkflowStep($"typescript-{suffix}", "TypeScript", Role("Test", "DockerBuild"), [$"approval-{suffix}"]) } : []),
                new($"build-{suffix}", "Build", Role("Test", "DockerBuild"), [react ? $"typescript-{suffix}" : $"approval-{suffix}"]),
                new($"tests-{suffix}", "Tests", Role("Test", "DockerTests"), [$"build-{suffix}"])] });
        }
        void Correct(string diagnostics, string dependency)
        {
            var mission = Current();
            if (mission.CorrectionAttempts >= options.CorrectionLimitFor(mission))
            {
                Finish(mission.Verification.LastOrDefault()?.Stage is "Tests" or "BackendTests" or "FrontendTests" or "Integration"
                    ? MissionStatus.TestsFailed :
                    mission.Verification.Count > 0 ? MissionStatus.BuildFailed : MissionStatus.Failed,
                    "Correction limit reached. Files, reviews and diagnostics retained.");
                return;
            }
            var suffix = mission.Workflow!.Version + 1;
            var applicationFiles = mission.Workflow.Plan!.Files
                .Where(file => mission.TemplateId == MissionReactTemplate.Id
                    ? MissionReactTemplate.IsEditable(file.Path) &&
                        (mission.Workflow.AutomaticReactCorrections || !ReactTemplate.IsTest(file.Path))
                    : mission.TemplateId == ReactTemplate.Id
                    ? ReactTemplate.IsEditable(file.Path) && !ReactTemplate.IsTest(file.Path)
                    : mission.TemplateId == FullStackTemplate.Id
                        ? FullStackTemplate.IsEditable(file.Path)
                    : mission.TemplateId == TwitchTemplate.Id ? TwitchTemplate.IsEditable(file.Path)
                    : file.Path.StartsWith("src/App/", StringComparison.Ordinal) &&
                      file.Path.EndsWith(".cs", StringComparison.Ordinal)).Select(file => file.Path).ToArray();
            var identified = applicationFiles.Where(path => diagnostics.Contains(path, StringComparison.Ordinal)).ToArray();
            var targets = identified.Length > 0 ? identified : applicationFiles;
            // Record analysis separately: correction prerequisites refer to completed analysis, not a failed build.
            var analysis = $"analysis-{suffix}";
            var orchestrator = Role("Orchestrator", "Workflow");
            var reviewer = Role("Review", "ReadSources");
            var fullStackCorrections = mission.TemplateId == FullStackTemplate.Id
                ? new[] { "Backend", "Frontend" }.Where(role =>
                    targets.Any(path => FullStackTemplate.IsEditableForRole(path, role))).ToArray()
                : [];
            var correctionIds = fullStackCorrections.Select(role =>
                $"correct-{suffix}-{role.ToLowerInvariant()}").ToArray();
            store.Update(id, item => item with
            {
                CorrectionAttempts = item.CorrectionAttempts +
                    (item.Workflow!.AutomaticModelSelection || item.Workflow.AutomaticReactCorrections ? 0 : 1),
                SandboxApproved = false, ApprovalFingerprint = null,
                Workflow = item.Workflow! with
                {
                    Version = suffix, Diagnostics = diagnostics[..Math.Min(diagnostics.Length, 12000)],
                    CorrectionPaths = targets,
                    Steps = [.. item.Workflow!.Steps,
                        new(analysis, "ErrorAnalysis", orchestrator, [], WorkflowStatus.Completed, 1,
                            DateTimeOffset.UtcNow, 0, dependency, diagnostics),
                        .. (mission.TemplateId == FullStackTemplate.Id
                            ? fullStackCorrections.Select((role, index) => new WorkflowStep(
                                correctionIds[index], role + "Correct", Role(role, "GenerateFiles"),
                                [index == 0 ? analysis : correctionIds[index - 1]]))
                            : new[] { new WorkflowStep($"correct-{suffix}", "Correct",
                                Role("Developer", "GenerateFiles"), [analysis]) }),
                        new($"review-{suffix}", "Review", reviewer,
                            mission.TemplateId == FullStackTemplate.Id ? correctionIds : [$"correct-{suffix}"])]
                }
            });
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

    private static ExecutionPlan? PersistExecutionPlan(ExecutionPlan plan, IReadOnlyList<GeneratedFile> files)
    {
        if (plan.ReactDescriptor is { } react &&
            (react.CompareManifest(files).Count > 0 || react.ComputeFingerprint(files) != plan.Fingerprint))
            throw new InvalidDataException("React execution plan descriptor does not match the persisted revision.");
        if (plan.Canonical is { } canonical && canonical.ComputeFingerprint() != plan.Fingerprint)
            throw new InvalidDataException("Canonical execution plan does not match the persisted revision fingerprint.");
        return plan.Canonical is not null || plan.ReactDescriptor is not null ? plan : null;
    }

    private async Task<bool> IsInterruptedProposalAsync(Guid id, IReadOnlyList<GeneratedFile> previous,
        IReadOnlyList<GeneratedFile> proposal, CancellationToken token)
    {
        var oldFiles = previous.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal);
        var newFiles = proposal.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal);
        if (!oldFiles.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(newFiles.Keys)) return false;
        var existing = workspaces.List(id);
        var react = store.Get(id)?.TemplateId is ReactTemplate.Id or MissionReactTemplate.Id;
        var fullStack = store.Get(id)?.TemplateId == FullStackTemplate.Id;
        if (existing.Count != oldFiles.Count + (react ? 0 : 1) ||
            (!react && !existing.Any(file => file.Path == "Generated.slnx"))) return false;
        foreach (var file in existing.Where(file => file.Path != "Generated.slnx"))
        {
            if (!oldFiles.TryGetValue(file.Path, out var oldContent)) return false;
            var actual = (await workspaces.ReadAsync(id, file.Path, token)).Content;
            if (actual != oldContent && actual != newFiles[file.Path]) return false;
        }
        if (react) return true;
        var solution = (await workspaces.ReadAsync(id, "Generated.slnx", token)).Content;
        return solution == (fullStack
            ? """<Solution><Project Path="backend/Api/Api.csproj" /><Project Path="backend/Api.Tests/Api.Tests.csproj" /></Solution>"""
            : """<Solution><Project Path="src/App/App.csproj" /><Project Path="tests/App.Tests/App.Tests.csproj" /></Solution>""");
    }
    private static void Demand(AgentDefinition agent, string tool)
    {
        if (!agent.AllowedTools.Contains(tool) || !AgentPermissions.ToolsFor(agent.Role).Contains(tool))
            throw new StudioValidationException("Agent operation is forbidden by server policy.");
    }
    private static string DescribeInput(string kind, MissionWorkflow flow) => kind switch
    {
        "Plan" => "Original request; bounded planning schema for the selected project template.",
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
