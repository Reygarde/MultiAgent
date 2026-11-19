using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed partial class MissionStore : IMissionService, IDisposable
{
    public const string InfrastructureRevalidationReason =
        "Infrastructure revalidation; identical reviewed sources, no generation or correction.";
    public const string ReviewerCorrectionRestartFailure =
        "Reviewer correction interrupted by backend restart. Source-bound retry is available after integrity checks.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object sync = new();
    private readonly Dictionary<Guid, MissionSnapshot> missions = [];
    private readonly Dictionary<Guid, CancellationTokenSource> cancellations = [];
    private readonly string directory;
    private readonly string persistenceMutexName;
    private readonly PlatformConfiguration platform;
    private readonly StudioOptions options;
    private readonly IAgentRegistry registry;
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private int pendingProjectCreations;

    public MissionStore(StudioOptions options, PlatformConfiguration platform, IAgentRegistry registry,
        IWorkspaceStore? workspaces = null)
    {
        this.options = options;
        this.platform = platform;
        this.registry = registry;
        this.workspaces = workspaces;
        directory = Path.Combine(SafePaths.ValidateStorageDirectory(options.DataDirectory), "missions");
        persistenceMutexName = "AIPlatform-Missions-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? directory.ToUpperInvariant() : directory)));
        SafePaths.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            SafePaths.RejectLinks(path);
            var snapshot = JsonSerializer.Deserialize<MissionSnapshot>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Invalid saved mission: {Path.GetFileName(path)}.");
            if (Path.GetFileNameWithoutExtension(path) != snapshot.Id.ToString("N"))
                throw new InvalidDataException("A saved mission identifier does not match its filename.");
            snapshot.Sizing?.Validate();
            if (snapshot.Workflow?.ReviewerCorrectionResume is { Status: "Queued" or "Running" } targeted &&
                snapshot.Status is MissionStatus.Queued or MissionStatus.Running or MissionStatus.Failed)
            {
                var now = DateTimeOffset.UtcNow;
                snapshot = snapshot with
                {
                    Status = MissionStatus.Failed,
                    UpdatedAt = now,
                    Error = ReviewerCorrectionRestartFailure,
                    SandboxApproved = false,
                    ApprovalFingerprint = null,
                    Workflow = snapshot.Workflow with
                    {
                        ReviewerCorrectionResume = targeted with { Status = "Interrupted" },
                        SoftwareCheckpointVersion = snapshot.Workflow.SoftwareCheckpointVersion +
                            (snapshot.Workflow.AutomaticReactCorrections ? 1 : 0),
                        Steps = snapshot.Workflow.Steps.Select(step => step.Id == targeted.StepId
                            ? step with { Status = WorkflowStatus.Failed, Error = ReviewerCorrectionRestartFailure }
                            : step.Status == WorkflowStatus.Running
                                ? step with { Status = WorkflowStatus.Cancelled, Error = "Interrupted by backend restart." }
                                : step).ToArray()
                    },
                    Events = snapshot.Events.Append(new(snapshot.Events.Count + 1, now,
                        "ReviewerCorrectionResume", StepStatus.Failed, ReviewerCorrectionRestartFailure)).ToArray()
                };
                snapshot = Save(snapshot);
            }
            else if (!snapshot.IsTerminal && snapshot.Status is not (MissionStatus.AwaitingApproval or MissionStatus.AwaitingPlanApproval))
            {
                snapshot = snapshot with
                {
                    Status = MissionStatus.Cancelled,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = "Mission interrupted by a server restart. Explicit resumption is required.",
                    SandboxApproved = false,
                    ApprovalFingerprint = null,
                    Workflow = snapshot.Workflow is { } interrupted ? interrupted with
                    {
                        SoftwareCheckpointVersion = interrupted.SoftwareCheckpointVersion +
                            (interrupted.AutomaticReactCorrections ? 1 : 0),
                        Steps = interrupted.Steps.Select(step => step.Status == WorkflowStatus.Running
                            ? step with { Status = WorkflowStatus.Cancelled, Error = "Interrupted by backend restart." } : step).ToArray()
                    } : null,
                    Events = snapshot.Events.Append(new(snapshot.Events.Count + 1, DateTimeOffset.UtcNow,
                        "Mission", StepStatus.Cancelled, "Interrupted by server restart; not automatically re-executed.")).ToArray()
                };
                snapshot = Save(snapshot);
            }
            missions.Add(snapshot.Id, snapshot);
            if (snapshot.Status is MissionStatus.AwaitingApproval or MissionStatus.AwaitingPlanApproval)
                cancellations.Add(snapshot.Id, new());
        }
    }

    public MissionSnapshot Submit(MissionSubmission submission)
    {
        ProjectSizingProfile? sizing = null;
        if (submission.SizingProfileId is not null)
        {
            if (submission.Kind != MissionKind.Project || submission.TemplateId != MissionReactTemplate.Id ||
                !options.CollaborativeWorkflows)
                throw new StudioValidationException("Sizing profiles require a new React v2 project workflow.");
            sizing = options.AvailableSizingProfiles.SingleOrDefault(profile => profile.Id == submission.SizingProfileId)
                ?? throw new StudioValidationException("Unknown project sizing profile.");
            sizing.Validate();
            if (sizing.MaxPlannedFiles + MissionReactTemplate.Files.Count > sizing.MaxFiles)
                throw new StudioValidationException("The selected sizing profile cannot contain its immutable scaffold.");
            if (sizing.MaxProjectBytes <= MissionReactTemplate.Files.Sum(file => Encoding.UTF8.GetByteCount(file.Content)))
                throw new StudioValidationException("The selected sizing profile leaves no space beyond its immutable scaffold.");
        }
        if (submission.MaxTotalGenerations is < 1 or > 1000)
            throw new StudioValidationException("MaxTotalGenerations must be between 1 and 1000.");
        if (submission.AutomaticReactCorrections && (submission.Kind != MissionKind.Project ||
            submission.TemplateId != MissionReactTemplate.Id || !options.CollaborativeWorkflows))
            throw new StudioValidationException("Automatic software corrections require a new React v2 project workflow.");
        if (submission.RequirePlanApproval && (submission.Kind != MissionKind.Project || !options.CollaborativeWorkflows))
            throw new StudioValidationException("Plan approval requires a collaborative project workflow.");
        if (submission.AutomaticModelSelection && (submission.Kind != MissionKind.Project || !options.CollaborativeWorkflows))
            throw new StudioValidationException("Automatic mission model selection requires a collaborative project workflow.");
        if (submission.RecordPlannerTrace && (submission.TemplateId != MissionReactTemplate.Id ||
            submission.Kind != MissionKind.Project || !options.CollaborativeWorkflows))
            throw new StudioValidationException("Detailed Planner tracing requires a new React v2 project mission.");
        if (string.IsNullOrWhiteSpace(submission.Task) || submission.Task.Length > platform.MaxTaskLength ||
            !Enum.IsDefined(submission.Kind))
            throw new StudioValidationException($"Provide a task of 1 to {platform.MaxTaskLength} characters and a valid kind.");
        if (submission.TemplateId is not null &&
            ((submission.TemplateId != DiscordTemplate.Id && submission.TemplateId != ReactTemplate.Id &&
              submission.TemplateId != MissionReactTemplate.Id &&
              submission.TemplateId != TwitchTemplate.Id && submission.TemplateId != FullStackTemplate.Id) ||
             submission.Kind != MissionKind.Project || !options.CollaborativeWorkflows))
            throw new StudioValidationException("The selected template requires the collaborative project workflow.");
        if (submission.TemplateId is not null && ProjectExport.ContainsPotentialSecret(submission.Task))
            throw new StudioValidationException("A possible credential was detected in the mission. Do not submit tokens.");
        var reactNormalization = submission.TemplateId == MissionReactTemplate.Id
            ? MissionReactTemplate.NormalizeRequirements(submission.Task, sizing) : null;
        if (reactNormalization is { Criteria.Count: 0 })
            throw new StudioValidationException(
                "The React request contains no clearly functional requirement. Add an observable application behavior; " +
                "the original request and unclassified requirements were not discarded.");
        var candidates = submission.Kind == MissionKind.Chat ? registry.Candidates :
            registry.Agents.Where(agent => agent.Enabled &&
                (options.CollaborativeWorkflows ? agent.Role ==
                    (submission.TemplateId == FullStackTemplate.Id ? "Backend" : "Developer") &&
                    agent.TaskTypes.Contains("Project") :
                    agent.Id == options.DeveloperAgentId)).ToArray();
        if (submission.AgentId is not null && !candidates.Any(agent => agent.Id == submission.AgentId))
            throw new StudioValidationException("The requested execution agent is not registered.");
        if (!options.CollaborativeWorkflows && submission.Kind == MissionKind.Project && submission.AgentId is not null &&
            submission.AgentId != options.DeveloperAgentId)
            throw new StudioValidationException("Project generation requires the configured developer agent.");

        lock (sync)
        {
            if (missions.Count + pendingProjectCreations >= options.MaxMissions)
                throw new StudioCapacityException(
                    $"The persisted mission limit ({options.MaxMissions}) is reached; archived missions count toward it. " +
                    "Archiving preserves data and does not free capacity. Increase Studio:MaxMissions (maximum 10000) in settings and restart Studio.");
            var now = DateTimeOffset.UtcNow;
            var snapshot = new MissionSnapshot(
                Guid.NewGuid(), submission.TemplateId == MissionReactTemplate.Id ? submission.Task : submission.Task.Trim(),
                submission.Kind, submission.AgentId,
                submission.ApproveSandboxExecution, MissionStatus.Queued, now, now,
                null, null, null, false, null, null, false, 0,
                [new(1, now, "Queue", StepStatus.Running, "Waiting for the single mission worker.")], [],
                TemplateId: submission.TemplateId,
                MaxTotalGenerations: submission.MaxTotalGenerations ?? options.DefaultMaxTotalGenerations,
                GenerationReservations: [], Sizing: sizing);
            if (options.CollaborativeWorkflows && submission.Kind == MissionKind.Project)
            {
                var workflow = CollaborativeProcessor.Create(snapshot.Id, registry, sizing?.MaxCorrections ?? options.MaxCorrectionAttempts,
                    submission.TemplateId) with
                {
                    AutomaticModelSelection = submission.AutomaticModelSelection,
                    AutomaticReactCorrections = submission.AutomaticReactCorrections,
                    RecordPlannerTrace = submission.RecordPlannerTrace,
                    RequirePlanApproval = submission.RequirePlanApproval,
                    PlannerGenerationLimit = 3,
                    ReviewerCorrectionEnabled = true,
                    AcceptanceCriteria = submission.TemplateId == MissionReactTemplate.Id
                        ? reactNormalization!.Criteria : [],
                    RequirementTrace = reactNormalization?.Trace ?? []
                };
                var developer = workflow.Agents.Single(agent => agent.Role ==
                    (submission.TemplateId == FullStackTemplate.Id ? "Backend" : "Developer") && agent.Enabled);
                snapshot = snapshot with
                {
                    Workflow = workflow,
                    SandboxApproved = false,
                    SelectedAgent = developer.Id,
                    SelectedModel = submission.AutomaticModelSelection ? null :
                        workflow.Models.Single(model => model.Id == developer.ModelId).Name,
                    Reason = "Deterministic collaborative project workflow; each sensitive file version requires human approval."
                };
            }
            var minimum = submission.Kind == MissionKind.Chat || !options.CollaborativeWorkflows
                ? (submission.AgentId is null ? 2 : 1) :
                submission.TemplateId switch
                {
                    MissionReactTemplate.Id => 4,
                    FullStackTemplate.Id => 1 + 2 * FullStackTemplate.Files.Count(file => FullStackTemplate.IsEditable(file.Path)),
                    ReactTemplate.Id => 2 + ReactTemplate.Files.Count(file => ReactTemplate.IsEditable(file.Path)),
                    DiscordTemplate.Id => 3,
                    TwitchTemplate.Id => 4,
                    _ => 6
                };
            if (snapshot.MaxTotalGenerations < minimum)
                snapshot = snapshot with { GenerationBudgetWarning =
                    $"GenerationBudgetRisk: at least {minimum} initial calls are expected; the chosen limit cannot cover them. Corrections may require more." };
            snapshot = Save(snapshot);
            missions.Add(snapshot.Id, snapshot);
            cancellations.Add(snapshot.Id, new());
            if (!queue.Writer.TryWrite(snapshot.Id))
                throw new InvalidOperationException("The mission worker queue is closed.");
            return snapshot;
        }
    }

    public async Task<MissionSnapshot> CreateCustomAgentProjectAsync(
        string? name, CustomAgentProjectType projectType, CancellationToken cancellationToken, int? maxTotalGenerations = null)
    {
        if (maxTotalGenerations is < 1 or > 1000)
            throw new StudioValidationException("MaxTotalGenerations must be between 1 and 1000.");
        var projectName = name?.Trim() ?? "";
        if (projectName.Length is < 1 or > 80 || projectName.Any(char.IsControl) ||
            !Enum.IsDefined(projectType))
            throw new StudioValidationException("Provide a project name of 1-80 characters and a supported project type.");
        if (workspaces is null)
            throw new InvalidOperationException("Workspace storage is required to create a Studio project.");

        lock (sync)
        {
            if (missions.Count + pendingProjectCreations >= options.MaxMissions)
                throw new StudioCapacityException(
                    $"The persisted mission limit ({options.MaxMissions}) is reached; archived missions count toward it. " +
                    "Archiving preserves data and does not free capacity. Increase Studio:MaxMissions in settings and restart Studio.");
            pendingProjectCreations++;
        }

        var id = Guid.NewGuid();
        var templateId = projectType switch
        {
            CustomAgentProjectType.React => AgentReactTemplate.Id,
            CustomAgentProjectType.DotNet => AgentDotNetTemplate.Id,
            CustomAgentProjectType.FullStack => AgentFullStackTemplate.Id,
            _ => throw new StudioValidationException("Unsupported custom-agent project type.")
        };
        var starterFiles = projectType switch
        {
            CustomAgentProjectType.React => AgentReactTemplate.Files,
            CustomAgentProjectType.DotNet => AgentDotNetTemplate.Files,
            CustomAgentProjectType.FullStack => AgentFullStackTemplate.Files,
            _ => throw new StudioValidationException("Unsupported custom-agent project type.")
        };
        var workspaceCreated = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            workspaces.Create(id);
            workspaceCreated = true;
            await workspaces.WriteAsync(id, starterFiles, cancellationToken, templateId);

            var now = DateTimeOffset.UtcNow;
            var workflowAgent = new AgentDefinition { Id = "CustomAgentTeam", Name = "Custom agent team", Role = "Orchestrator" };
            var workflow = new MissionWorkflow(Guid.NewGuid(), id, [workflowAgent], [],
                [new WorkflowStep("project-scaffold", "ManualRevision", workflowAgent.Id, [],
                    WorkflowStatus.Completed, 1, now, Input: "Create a custom-agent project scaffold.",
                    Output: "The trusted scaffold is ready; generated changes still require user approval.")],
                Draft: starterFiles, Version: 0,
                FinalReport: "Project scaffold created. No agent-generated changes have been applied.");
            var mission = new MissionSnapshot(id, $"Agent project: {projectName}", MissionKind.Project, null,
                false, MissionStatus.Completed, now, now, null, null, null, false,
                "Project scaffold created; generated files remain subject to explicit approval.", null,
                true, 0,
                [new(1, now, "Project", StepStatus.Succeeded,
                    $"{projectType} scaffold created. No agent-generated changes were applied.")], [],
                Workflow: workflow, ProjectName: projectName,
                ProjectDescription: $"{projectType} project created for custom-agent collaboration.",
                TemplateId: templateId, CompletedAt: now,
                MaxTotalGenerations: maxTotalGenerations ?? options.DefaultMaxTotalGenerations, GenerationReservations: []);
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                mission = Save(mission);
                missions.Add(id, mission);
                cancellations.Add(id, new());
            }
            return mission;
        }
        catch
        {
            if (workspaceCreated)
                workspaces.Delete(id);
            throw;
        }
        finally
        {
            lock (sync)
                pendingProjectCreations--;
        }
    }

    public IReadOnlyList<MissionSnapshot> List()
    {
        lock (sync)
            return missions.Values.OrderByDescending(mission => mission.CreatedAt).ToArray();
    }

    public MissionSnapshot? Get(Guid id)
    {
        lock (sync)
            return missions.GetValueOrDefault(id);
    }

    public bool Cancel(Guid id)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var current) || current.IsTerminal)
                return false;
            // Cancel under the same lock used to commit completion: neither can win after the other.
            cancellations[id].Cancel();
            if (current.Status is MissionStatus.Queued or MissionStatus.AwaitingApproval or MissionStatus.AwaitingPlanApproval)
            {
                foreach (var active in current.Events.GroupBy(item => item.Step).Select(group => group.Last())
                             .Where(item => item.Status == StepStatus.Running))
                    AddEvent(id, active.Step, StepStatus.Cancelled, "Cancelled before execution or resumption.");
                Update(id, mission => mission with { Status = MissionStatus.Cancelled });
                AddEvent(id, "Mission", StepStatus.Cancelled, "Mission cancelled.");
            }
            return true;
        }
    }

    public bool DecideApproval(Guid id, bool approved, string fingerprint)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var current) || current.Status != MissionStatus.AwaitingApproval ||
                cancellations[id].IsCancellationRequested)
                return false;
            if (approved && current.Workflow is { } pending &&
                (pending.Revisions?.LastOrDefault() is not { } revision ||
                 revision.Version != pending.Version || revision.Fingerprint != fingerprint))
                return false;
            if (approved && current.TemplateId == FullStackTemplate.Id &&
                (current.Workflow?.Revisions?.LastOrDefault()?.ExecutionPlan is not { Canonical: { } canonical } saved ||
                 saved.Fingerprint != fingerprint || canonical.WorkflowVersion != current.Workflow.Version ||
                 canonical.ComputeFingerprint() != fingerprint || canonical.AutomaticCorrections ||
                 !canonical.Image.StartsWith("sha256:", StringComparison.Ordinal)))
                return false;
            Update(id, mission => mission with
            {
                SandboxApproved = approved,
                ApprovalFingerprint = approved ? fingerprint : null,
                Status = approved ? MissionStatus.Queued : MissionStatus.GeneratedUnverified,
                Workflow = mission.Workflow is { } flow ? flow with
                {
                    FinalReport = approved ? null : "Execution refused. Files retained; build and tests not executed.",
                    Steps = flow.Steps.Select(step => step.Status == WorkflowStatus.WaitingForApproval
                        ? step with
                        {
                            Status = approved ? WorkflowStatus.Completed : WorkflowStatus.Skipped,
                            Output = approved ? $"Human approved fingerprint {fingerprint}." : "Human refused execution.",
                            DurationMilliseconds = step.StartedAt is { } start ? (DateTimeOffset.UtcNow - start).TotalMilliseconds : null
                        }
                        : !approved && step.Status == WorkflowStatus.Pending
                            ? step with { Status = WorkflowStatus.Skipped, Output = "Execution refused." } : step).ToArray()
                } : null
            });
            AddEvent(id, "Authorization", approved ? StepStatus.Succeeded : StepStatus.Skipped,
                approved ? "User approved this workspace and bounded execution plan." :
                    "User refused execution. Generated files retained; build and tests not executed.");
            if (approved && !queue.Writer.TryWrite(id))
                throw new InvalidOperationException("The mission worker queue is closed.");
            if (!approved)
            {
                var skippedStages = current.TemplateId == FullStackTemplate.Id
                    ? new[] { "BackendBuild", "BackendTests", "TypeScript", "FrontendBuild", "FrontendTests", "Integration" }
                    : current.TemplateId is ReactTemplate.Id or MissionReactTemplate.Id
                        ? new[] { "TypeScript", "Build", "Tests" } : ["Build", "Tests"];
                Update(id, mission => mission with
                {
                    Verification = [.. mission.Verification,
                        .. skippedStages.Select(stage => new VerificationResult(stage, "Skipped", null,
                            "Human approval refused; not executed."))]
                });
                AddEvent(id, "Mission", StepStatus.Skipped, "GeneratedUnverified: execution refused by the user.");
            }
            return true;
        }
    }

    public IAsyncEnumerable<Guid> ReadQueue(CancellationToken cancellationToken) =>
        queue.Reader.ReadAllAsync(cancellationToken);

    public void PauseForPlanApproval(Guid id, ProjectPlan plan, IReadOnlyList<GeneratedFile>? draft)
    {
        lock (sync)
        {
            var current = missions[id];
            var flow = current.Workflow!;
            if (current.Status != MissionStatus.Running || !flow.RequirePlanApproval || flow.Plan is not null ||
                flow.Steps.Single(step => step.Kind == "Plan").Status != WorkflowStatus.Running)
                throw new StudioValidationException("The mission is not awaiting its initial validated Planner result.");
            cancellations[id].Token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var output = JsonSerializer.Serialize(plan);
            var planned = current with { Workflow = flow with { Plan = plan, Draft = draft } };
            var approval = new WorkflowPlanApproval(flow.Version, PlanFingerprint(planned, flow.Version));
            Update(id, _ => planned with
            {
                Status = MissionStatus.AwaitingPlanApproval,
                ProjectName = plan.Name, ProjectDescription = plan.Description,
                SandboxApproved = false, ApprovalFingerprint = null,
                Workflow = planned.Workflow! with
                {
                    PlanApproval = approval,
                    Steps = flow.Steps.Select(step => step.Kind == "Plan"
                        ? step with { Status = WorkflowStatus.Completed, Output = output, Error = null,
                            DurationMilliseconds = step.StartedAt is { } start ? (now - start).TotalMilliseconds : null }
                        : step).ToArray()
                },
                Events = [.. current.Events,
                    new(current.Events.Count + 1, now, "Plan", StepStatus.Succeeded, output),
                    new(current.Events.Count + 2, now, "PlanApproval", StepStatus.Running,
                        "Validated plan saved. Human plan approval is required before Developer; code execution remains unauthorized.")]
            });
        }
    }

    public bool DecidePlanApproval(Guid id, bool approved, string fingerprint)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var current) ||
                current.Status != MissionStatus.AwaitingPlanApproval || current.Deleted || current.Archived ||
                current.Workflow is not { RequirePlanApproval: true, Plan: not null,
                    PlanApproval: { Approved: null } pending } flow ||
                cancellations[id].IsCancellationRequested || pending.Version != flow.Version ||
                !string.Equals(pending.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                PlanFingerprint(current, pending.Version) != pending.Fingerprint)
                return false;
            WorkflowAgents.ValidatePlan(flow.Plan, current.TemplateId, current.Sizing);
            var now = DateTimeOffset.UtcNow;
            Update(id, mission => mission with
            {
                Status = approved ? MissionStatus.Queued : MissionStatus.Cancelled,
                SandboxApproved = false, ApprovalFingerprint = null,
                Workflow = flow with
                {
                    PlanApproval = pending with { Approved = approved, DecidedAt = now },
                    FinalReport = approved ? null : "Plan refused. No Developer, Reviewer or generated-code execution was started.",
                    Steps = approved ? flow.Steps : flow.Steps.Select(step => step.Status == WorkflowStatus.Pending
                        ? step with { Status = WorkflowStatus.Skipped, Output = "Human refused the plan." } : step).ToArray()
                },
                Events = [.. mission.Events,
                    new(mission.Events.Count + 1, now, "PlanApproval", approved ? StepStatus.Succeeded : StepStatus.Cancelled,
                        approved ? "User approved the exact saved plan. Generation may continue; execution is not authorized."
                            : "User refused the saved plan. No Developer or Reviewer will run.")]
            });
            if (approved && !queue.Writer.TryWrite(id))
                throw new InvalidOperationException("The mission worker queue is closed.");
            return true;
        }
    }

    public void RequireApprovedPlan(Guid id)
    {
        lock (sync)
            RequireApprovedPlan(missions[id]);
    }

    internal static void RequireApprovedPlan(MissionSnapshot current)
    {
        if (current.Workflow is { RequirePlanApproval: true } flow &&
                (flow.Plan is null || flow.PlanApproval is not { Approved: true } approval ||
                 PlanFingerprint(current, approval.Version) != approval.Fingerprint))
            throw new StudioValidationException("The saved plan has no matching human approval. Generation and review are blocked.");
    }

    private static string PlanFingerprint(MissionSnapshot mission, int planVersion)
    {
        var original = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            mission.Id, mission.TemplateId, mission.Task, mission.Workflow!.WorkflowId,
            PlanVersion = planVersion, mission.Workflow.Plan, mission.Workflow.AcceptanceCriteria
        })));
        var flow = mission.Workflow!;
        if (flow.PlanAmendments.Count == 0) return original;
        var developer = flow.Agents.Single(agent => agent.Role == "Developer" && agent.Enabled);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            original, flow.PlanAmendments.Last().PreparedAt, flow.RequirementTrace, developer,
            Profile = flow.Models.Single(model => model.Id == developer.ModelId),
            Routing = flow.ModelSelections.Where(item => item.AgentId == developer.Id)
        })));
    }

    public MissionSnapshot PrepareReactPlanAmendment(Guid id, DateTimeOffset expectedUpdatedAt, ProjectPlan plan,
        ReactRequirementNormalization normalized, ModelRoutingDecision decision, IReadOnlyList<FailureDiagnostic> diagnostics)
    {
        lock (sync)
        {
            var current = missions[id];
            var flow = current.Workflow!;
            if (current.UpdatedAt != expectedUpdatedAt || current.Status != MissionStatus.Failed ||
                current.TemplateId != MissionReactTemplate.Id || current.HasWorkspace || current.Deleted || current.Archived ||
                flow.Revisions?.Count > 0 || flow.PlanApproval is not { Approved: true } approval ||
                PlanFingerprint(current, approval.Version) != approval.Fingerprint)
                throw new ModelOperationConflictException("The mission changed before the amendment could be saved.");
            var developer = flow.Agents.Single(agent => agent.Role == "Developer" && agent.Enabled);
            var now = DateTimeOffset.UtcNow;
            var amendment = new WorkflowPlanAmendment(flow.Version, now, flow.Plan!, flow.AcceptanceCriteria,
                flow.RequirementTrace, approval, flow.ModelSelections, flow.Draft!,
                decision.Model, decision.Alternatives.Single().Compatibility, diagnostics);
            var version = checked(flow.Version + 1);
            var amended = current with { Workflow = flow with
            {
                Version = version, Plan = plan, AcceptanceCriteria = normalized.Criteria, RequirementTrace = normalized.Trace,
                PlanAmendments = [.. flow.PlanAmendments, amendment],
                ModelSelections = [.. flow.ModelSelections.Where(item => item.AgentId != developer.Id),
                    .. plan.Files.Where(file => MissionReactTemplate.IsEditable(file.Path))
                        .Select(file => new WorkflowModelSequence(developer.Id + ":" + file.Path, developer.Id, decision))],
                Steps = flow.Steps.Select(step => step.Kind == "Generate"
                    ? step with { Status = WorkflowStatus.Pending, Error = null, StartedAt = null, DurationMilliseconds = null }
                    : step).ToArray()
            } };
            cancellations.GetValueOrDefault(id)?.Dispose();
            cancellations[id] = new();
            return Update(id, _ => amended with
            {
                Status = MissionStatus.AwaitingPlanApproval, Error = null, SandboxApproved = false, ApprovalFingerprint = null,
                Workflow = amended.Workflow! with
                { PlanApproval = new(version, PlanFingerprint(amended, version)) },
                Events = [.. current.Events, new(current.Events.Count + 1, now, "PlanAmendment", StepStatus.Succeeded,
                    $"Human-assisted plan v{version} prepared from v{flow.Version}; prior plan, approval, routing and draft archived. " +
                    $"{diagnostics.Count} retained-source diagnostics; no generation or execution started."),
                    new(current.Events.Count + 2, now, "PlanApproval", StepStatus.Running,
                        "A new approval is required for the amended plan and Developer routing; no model has been called.")]
            });
        }
    }

    private readonly IWorkspaceStore? workspaces;

    public ReviewerCorrectionResumeQueueResult QueueReviewerCorrectionResume(
        Guid id, DateTimeOffset expectedUpdatedAt, ReviewerCorrectionResumeState resume)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var inMemory))
                return new(false, false, "Mission introuvable.");

            using var persistence = new PersistenceLock(persistenceMutexName);
            var path = Path.Combine(directory, id.ToString("N") + ".json");
            SafePaths.RejectLinks(path);
            if (!File.Exists(path))
                return new(false, false, "L’instantané persistant de la mission est absent.");
            var persisted = JsonSerializer.Deserialize<MissionSnapshot>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("Invalid saved mission.");

            if (persisted.Workflow?.ReviewerCorrectionResume is { Status: "Queued" or "Running" } active &&
                active.StepId == resume.StepId && active.SourceFingerprint == resume.SourceFingerprint &&
                active.RejectedAttemptId == resume.RejectedAttemptId &&
                persisted.Status is MissionStatus.Queued or MissionStatus.Running)
                return new(true, true, "La reprise ciblée est déjà en file ou en cours.");
            if (persisted.UpdatedAt != expectedUpdatedAt || inMemory.UpdatedAt != expectedUpdatedAt)
            {
                missions[id] = persisted;
                return new(false, false, "La mission a changé pendant la vérification. Actualisez son état avant de réessayer.");
            }

            var flow = persisted.Workflow;
            var step = flow?.Steps.SingleOrDefault(item => item.Id == resume.StepId && item.Kind == "Review");
            var correctionLimit = flow is null ? 0 :
                options.CorrectionLimitFor(persisted);
            if (persisted.Status != MissionStatus.Failed || persisted.Deleted || persisted.Archived ||
                persisted.GenerationBudgetExhausted || persisted.MaxTotalGenerations is not { } maxGenerations ||
                persisted.GenerationReservations is not { } reservations || reservations.Count >= maxGenerations ||
                flow is null || !flow.ReviewerCorrectionEnabled || step?.Status != WorkflowStatus.Failed ||
                step.Error is null || persisted.CorrectionAttempts >= correctionLimit ||
                flow.Steps.Any(item => item.Status == WorkflowStatus.Running) ||
                resume.SourceFingerprint.Length != 64 || !resume.SourceFingerprint.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(resume.OriginalFailure))
                return new(false, false, "Les conditions persistées de reprise Reviewer ne sont plus réunies.");

            if (cancellations.Remove(id, out var previousCancellation))
                previousCancellation.Dispose();
            cancellations[id] = new();

            var now = DateTimeOffset.UtcNow;
            var updated = persisted with
            {
                Status = MissionStatus.Queued,
                UpdatedAt = now,
                CompletedAt = null,
                Error = null,
                SandboxApproved = false,
                ApprovalFingerprint = null,
                Workflow = flow with
                {
                    ReviewerCorrectionResume = resume,
                    Steps = flow.Steps.Select(item => item.Id == resume.StepId
                        ? item with { Status = WorkflowStatus.Pending, Error = resume.OriginalFailure }
                        : item).ToArray()
                },
                Events = persisted.Events.Append(new(persisted.Events.Count + 1, now,
                    "ReviewerCorrectionResume", StepStatus.Running,
                    $"Source-bound Reviewer correction queued for {resume.StepId}; no plan or application file was regenerated.")).ToArray()
            };
            missions[id] = Save(updated);
            if (!queue.Writer.TryWrite(id))
                throw new InvalidOperationException("The mission worker queue is closed.");
            return new(true, false, "Reprise ciblée mise en file sans modifier le plan ni les sources.");
        }
    }

    public bool Resume(Guid id)
    {
        lock (sync)
        {
            using var persistence = new PersistenceLock(persistenceMutexName);
            if (missions.GetValueOrDefault(id)?.Workflow?.AutomaticReactCorrections == true)
                missions[id] = ReadLatestForRepair(id);
            if (ReadRepairCheckpoint(id) is { } repair)
            {
                if (!repair.Ready) return false;
                missions[id] = ReadLatestForRepair(id);
            }
            if (!missions.TryGetValue(id, out var current) || !current.IsTerminal ||
                current.Status is MissionStatus.Verified or MissionStatus.Completed || current.Deleted || current.Workflow is not { } flow ||
                current.GenerationBudgetExhausted || current.MaxTotalGenerations is { } limit &&
                current.GenerationReservations!.Count >= limit)
                return false;
            if (flow.AutomaticReactCorrections)
            {
                if (flow.SoftwareCorrections.LastOrDefault() is { InFlight: true } or { Failure: not null } ||
                    flow.PendingSoftwareAgentCall is not null &&
                    current.GenerationReservations!.Count > flow.PendingSoftwareReservationCount)
                    return false;
                flow = flow with { PendingSoftwareAgentCall = null };
            }
            if (flow.RequirePlanApproval && flow.Plan is not null && flow.PlanApproval?.Approved != true)
                return false;
            if (flow.PlannerGenerationLimit > 0 && flow.Plan is null &&
                flow.CountPlannerAttempts() >= Math.Min(3, flow.PlannerGenerationLimit))
                return false;
            cancellations.GetValueOrDefault(id)?.Dispose();
            cancellations[id] = new();
            var steps = flow.Steps;
            var correctionPrefix = $"correct-{flow.Version}-";
            var correctionStages = steps.Where(step => step.Id.StartsWith(correctionPrefix, StringComparison.Ordinal))
                .ToArray();
            var legacyCorrection = steps.LastOrDefault(step => step.Id == $"correct-{flow.Version}");
            var correctionReview = steps.LastOrDefault(step => step.Id == $"review-{flow.Version}");
            var correctionsCompleted = correctionStages.Length > 0 &&
                correctionStages.All(step => step.Status == WorkflowStatus.Completed);
            if (flow.Proposal is not null && correctionsCompleted &&
                (flow.Proposals ?? []).All(item => item.Version != flow.Version))
                flow = flow with
                {
                    Proposals = [.. flow.Proposals ?? [],
                    new(flow.Version, correctionStages[^1].StartedAt ?? current.UpdatedAt, flow.Proposal, flow.Review)]
                };
            if (correctionStages.Length > 0 && correctionReview is not null && !correctionsCompleted)
            {
                var ids = correctionStages.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
                steps = steps.Select(step => ids.Contains(step.Id)
                    ? step.Status == WorkflowStatus.Completed
                        ? step : step with { Status = WorkflowStatus.Pending, Error = null }
                    : step.Id == correctionReview.Id
                        ? step with { Status = WorkflowStatus.Pending, Error = null }
                        : step.Status is WorkflowStatus.Pending or WorkflowStatus.Running
                            ? step with { Status = WorkflowStatus.Skipped, Output = "Superseded by explicit resumption." }
                            : step).ToArray();
                flow = flow with { Steps = steps };
            }
            else if (legacyCorrection is not null && correctionReview is not null &&
                (legacyCorrection.Status != WorkflowStatus.Completed ||
                 (flow.Proposal is not null && correctionReview.Status != WorkflowStatus.Completed)))
            {
                var resumeReview = legacyCorrection.Status == WorkflowStatus.Completed && flow.Proposal is not null;
                steps = steps.Select(step => step.Id == legacyCorrection.Id
                    ? step with { Status = resumeReview ? WorkflowStatus.Completed : WorkflowStatus.Pending, Error = null }
                    : step.Id == correctionReview.Id
                        ? step with { Status = WorkflowStatus.Pending, Error = null }
                        : step.Status is WorkflowStatus.Pending or WorkflowStatus.Running
                            ? step with { Status = WorkflowStatus.Skipped, Output = "Superseded by explicit resumption." }
                            : step).ToArray();
                flow = flow with { Steps = steps };
            }
            else if (current.HasWorkspace && flow.Revisions?.Count > 0)
            {
                var reviewer = flow.Agents.Single(agent => agent.Enabled && agent.Role == "Review");
                steps = [.. steps.Select(step => step.Status is WorkflowStatus.Pending or WorkflowStatus.Running
                    ? step with { Status = WorkflowStatus.Skipped, Output = "Superseded by explicit resumption." } : step),
                    new($"resume-review-{steps.Count}", "Review", reviewer.Id, [])];
                flow = flow with { Steps = steps, Draft = null, Version = flow.Version + 1 };
            }
            else
            {
                steps = steps.Select(step => step.Status is WorkflowStatus.Running or WorkflowStatus.Failed or WorkflowStatus.Cancelled
                    ? step with { Status = WorkflowStatus.Pending } : step).ToArray();
                flow = flow with { Steps = steps };
            }
            WorkflowGraph.Validate(flow.Steps);
            Update(id, mission => mission with
            {
                Status = MissionStatus.Queued,
                Error = null,
                SandboxApproved = false,
                ApprovalFingerprint = null,
                Workflow = flow with
                {
                    FinalReport = null,
                    ReviewerCorrectionResume = flow.ReviewerCorrectionResume is { Status: "Completed" } completed
                        ? completed
                        : flow.ReviewerCorrectionResume is { } targeted
                            ? targeted with { Status = "Superseded" } : null
                }
            });
            AddEvent(id, "Queue", StepStatus.Running, "Explicit resumption; prior authorization discarded.");
            if (!queue.Writer.TryWrite(id)) throw new InvalidOperationException("Mission queue closed.");
            return true;
        }
    }

    public bool PrepareRevalidation(Guid id, DateTimeOffset expectedUpdatedAt, ExecutionPlan plan)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var current) || current.UpdatedAt != expectedUpdatedAt ||
                current.Status is not (MissionStatus.GeneratedUnverified or MissionStatus.AwaitingApproval) ||
                current.TemplateId != FullStackTemplate.Id ||
                current.Deleted || !current.HasWorkspace || current.Workflow is not { } flow ||
                flow.Proposal is not null || flow.Review is not { } review ||
                review.Findings.Any(finding => finding.Severity == "Error") ||
                flow.Revisions?.LastOrDefault() is not { } revision || revision.Version != flow.Version ||
                flow.Steps.LastOrDefault(step => step.Kind == "Review")?.Status != WorkflowStatus.Completed ||
                (current.Status == MissionStatus.GeneratedUnverified &&
                    flow.Steps.Any(step => step.Status is WorkflowStatus.Pending or WorkflowStatus.Running)) ||
                (current.Status == MissionStatus.AwaitingApproval && revision.ExecutionPlan is not null) ||
                plan.Canonical is not { } canonical || canonical.MissionId != id ||
                canonical.WorkflowVersion != flow.Version + 1 || canonical.AutomaticCorrections ||
                !canonical.Image.StartsWith("sha256:", StringComparison.Ordinal) ||
                canonical.ComputeFingerprint() != plan.Fingerprint ||
                plan.Fingerprint.Length != 64 || !plan.Fingerprint.All(Uri.IsHexDigit))
                return false;

            var cycle = flow.Steps.Where(step => step.Id.EndsWith($"-{flow.Version}", StringComparison.Ordinal) &&
                (step.Kind == "Approval" || FullStackBuildProfile.IsStage(step.Kind))).ToArray();
            if (cycle.Length != 7 || cycle.Select(step => step.Kind).Distinct(StringComparer.Ordinal).Count() != 7 ||
                cycle.Where(step => step.Kind == "Approval").Any(step =>
                    step.DependsOn.Any(dependency => flow.Steps.Single(item => item.Id == dependency).Status != WorkflowStatus.Completed)))
                return false;

            var version = flow.Version + 1;
            var now = DateTimeOffset.UtcNow;
            var identifiers = cycle.ToDictionary(step => step.Id,
                step => step.Id[..(step.Id.LastIndexOf('-') + 1)] + version, StringComparer.Ordinal);
            var steps = cycle.Select(step => step with
            {
                Id = identifiers[step.Id],
                DependsOn = step.DependsOn.Select(dependency => identifiers.GetValueOrDefault(dependency, dependency)).ToArray(),
                Status = step.Kind == "Approval" ? WorkflowStatus.WaitingForApproval : WorkflowStatus.Pending,
                Attempts = 0,
                StartedAt = step.Kind == "Approval" ? now : null,
                DurationMilliseconds = null,
                Input = null,
                Output = null,
                Error = null
            }).ToArray();
            var workflow = flow with
            {
                Version = version,
                FinalReport = null,
                Steps = [.. flow.Steps.Select(step => current.Status == MissionStatus.AwaitingApproval &&
                    step.Status is (WorkflowStatus.Pending or WorkflowStatus.WaitingForApproval)
                        ? step with { Status = WorkflowStatus.Skipped, Output = "Superseded by a new canonical plan." } : step), .. steps],
                Revisions = [.. flow.Revisions!, new(version, now, InfrastructureRevalidationReason, plan.Fingerprint, revision.Files, plan)]
            };
            WorkflowGraph.Validate(workflow.Steps);
            cancellations.GetValueOrDefault(id)?.Dispose();
            cancellations[id] = new();
            Update(id, mission => mission with
            {
                Status = MissionStatus.AwaitingApproval,
                Error = null,
                Response = null,
                SandboxApproved = false,
                ApprovalFingerprint = null,
                Workflow = workflow
            });
            AddEvent(id, "Revalidation", StepStatus.Succeeded,
                $"Infrastructure plan refreshed for version {version}; sources and correction count unchanged. Fingerprint {plan.Fingerprint}.");
            AddEvent(id, "Authorization", StepStatus.Running,
                "New human authorization required. No model or container execution was queued.");
            return true;
        }
    }

    public bool BeginManualRevision(Guid id, DateTimeOffset expectedUpdatedAt, string fingerprint, ManualRevisionRole role,
        IReadOnlyList<GeneratedFile> files)
    {
        lock (sync)
        {
            if (!Enum.IsDefined(role) || !missions.TryGetValue(id, out var current) || current.UpdatedAt != expectedUpdatedAt ||
                current.Status is not (MissionStatus.AwaitingApproval or MissionStatus.BuildFailed) ||
                current.TemplateId != FullStackTemplate.Id ||
                current.Deleted || current.SandboxApproved || current.Workflow is not { } flow ||
                flow.Proposal is not null || flow.Revisions?.LastOrDefault() is not { ExecutionPlan: { } saved } revision ||
                revision.Version != flow.Version || revision.Fingerprint != fingerprint ||
                saved.Fingerprint != fingerprint || saved.Canonical?.ComputeFingerprint() != fingerprint ||
                (current.Status == MissionStatus.AwaitingApproval &&
                    (!cancellations.TryGetValue(id, out var cancellation) || cancellation.IsCancellationRequested)))
                return false;
            if (current.Status == MissionStatus.BuildFailed)
            {
                cancellations.GetValueOrDefault(id)?.Dispose();
                cancellations[id] = new();
            }
            var version = flow.Version + 1;
            var orchestrator = flow.Agents.Single(agent => agent.Enabled && agent.Role == "Orchestrator");
            Update(id, mission => mission with
            {
                Status = MissionStatus.Running,
                SandboxApproved = false,
                ApprovalFingerprint = null,
                Workflow = flow with
                {
                    Version = version,
                    FinalReport = null,
                    Proposals = [.. flow.Proposals ?? [], new(version, DateTimeOffset.UtcNow, files)],
                    Steps = [.. flow.Steps.Select(step => step.Status is WorkflowStatus.Pending or WorkflowStatus.WaitingForApproval
                            ? step with { Status = WorkflowStatus.Skipped, Output = "Superseded by manual revision." } : step),
                            new($"manual-revision-{version}", "ManualRevision", orchestrator.Id, [],
                                WorkflowStatus.Running, 1, DateTimeOffset.UtcNow, Input: role.ToString())]
                }
            });
            AddEvent(id, "ManualRevision", StepStatus.Running,
                $"Human-requested {role} revision {version}; previous plan invalidated. No agent or container was started.");
            return true;
        }
    }

    public bool CompleteManualRevision(Guid id, ExecutionPlan plan, IReadOnlyList<GeneratedFile> files)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var current) || current.Status != MissionStatus.Running ||
                current.Workflow is not { } flow || cancellations[id].IsCancellationRequested ||
                flow.Steps.LastOrDefault() is not { Kind: "ManualRevision", Status: WorkflowStatus.Running } manual ||
                manual.Input is not ("Backend" or "Frontend") ||
                flow.Proposals?.LastOrDefault() is not { } proposal || proposal.Version != flow.Version ||
                plan.Canonical is not { } canonical || canonical.MissionId != id ||
                canonical.WorkflowVersion != flow.Version || canonical.AutomaticCorrections ||
                canonical.ComputeFingerprint() != plan.Fingerprint ||
                !canonical.Image.StartsWith("sha256:", StringComparison.Ordinal))
                return false;
            var previousVersion = flow.Version - 1;
            var cycle = flow.Steps.Where(step => step.Id.EndsWith($"-{previousVersion}", StringComparison.Ordinal) &&
                (step.Kind == "Approval" || FullStackBuildProfile.IsStage(step.Kind))).ToArray();
            if (cycle.Length != 7 || cycle.Select(step => step.Kind).Distinct(StringComparer.Ordinal).Count() != 7)
                return false;
            var identifiers = cycle.ToDictionary(step => step.Id,
                step => step.Id[..(step.Id.LastIndexOf('-') + 1)] + flow.Version, StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            var steps = cycle.Select(step => step with
            {
                Id = identifiers[step.Id],
                DependsOn = step.Kind == "Approval" ? [manual.Id] :
                    step.DependsOn.Select(dependency => identifiers.GetValueOrDefault(dependency, dependency)).ToArray(),
                Status = step.Kind == "Approval" ? WorkflowStatus.WaitingForApproval : WorkflowStatus.Pending,
                Attempts = 0,
                StartedAt = step.Kind == "Approval" ? now : null,
                DurationMilliseconds = null,
                Input = null,
                Output = null,
                Error = null
            }).ToArray();
            var workflow = flow with
            {
                Draft = files,
                Review = null,
                Proposal = null,
                Steps = [.. flow.Steps.Select(step => step.Id == manual.Id
                        ? step with { Status = WorkflowStatus.Completed,
                            Output = $"Human-reviewed {manual.Input} correction applied; no automated review or validation was performed." } : step),
                        .. steps],
                Revisions = [.. flow.Revisions ?? [], new(flow.Version, now,
                        $"Human-reviewed manual {manual.Input} correction; no model or Docker execution.", plan.Fingerprint, files, plan)]
            };
            WorkflowGraph.Validate(workflow.Steps);
            Update(id, mission => mission with
            {
                Status = MissionStatus.AwaitingApproval,
                Error = null,
                Response = null,
                SandboxApproved = false,
                ApprovalFingerprint = null,
                Workflow = workflow
            });
            AddEvent(id, "ManualRevision", StepStatus.Succeeded,
                $"Manual {manual.Input} revision {flow.Version} saved with canonical fingerprint {plan.Fingerprint}.");
            AddEvent(id, "Authorization", StepStatus.Running,
                "New human authorization required. No model or container execution was queued.");
            return true;
        }
    }

    public void FailManualRevision(Guid id, string message)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var current) || current.Status != MissionStatus.Running ||
                current.Workflow?.Steps.LastOrDefault() is not { Kind: "ManualRevision", Status: WorkflowStatus.Running } manual)
                return;
            Update(id, mission => mission with
            {
                Status = MissionStatus.Failed,
                Error = message,
                SandboxApproved = false,
                ApprovalFingerprint = null,
                Workflow = mission.Workflow! with
                {
                    Steps = mission.Workflow.Steps.Select(step => step.Id == manual.Id
                        ? step with { Status = WorkflowStatus.Failed, Error = message } : step).ToArray()
                }
            });
            AddEvent(id, "ManualRevision", StepStatus.Failed, message);
        }
    }
    public bool RenameProject(Guid id, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl) ||
            description is null || description.Length > 1000)
            throw new StudioValidationException("Project name requires 1-80 characters; description at most 1000.");
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var mission) || !mission.HasWorkspace || mission.Deleted)
                return false;
            Update(id, current => current with { ProjectName = name.Trim(), ProjectDescription = description.Trim() });
            AddEvent(id, "Project", StepStatus.Succeeded, $"Project renamed: {name.Trim()}");
            return true;
        }
    }

    public bool DeleteProject(Guid id)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var mission) || !mission.IsTerminal || !mission.HasWorkspace || mission.Deleted)
                return false;
            (workspaces ?? throw new InvalidOperationException("Workspace store required for deletion.")).Delete(id);
            Update(id, current => current with
            {
                HasWorkspace = false,
                Deleted = true,
                Workflow = current.Workflow is { } flow ? flow with { Draft = [], Proposal = null, Revisions = [], Proposals = [] } : null
            });
            AddEvent(id, "Project", StepStatus.Succeeded, "Project sources deleted by explicit user request. Mission audit retained.");
            return true;
        }
    }

    public bool Archive(Guid id)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var mission) || !mission.IsTerminal || mission.Archived || mission.Deleted)
                return false;
            Update(id, current => current with { Archived = true, ArchivedAt = DateTimeOffset.UtcNow });
            AddEvent(id, "Mission", StepStatus.Succeeded, "Mission archived by explicit user request; results and workspace preserved.");
            return true;
        }
    }

    public bool Restore(Guid id)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var mission) || !mission.Archived || mission.Deleted)
                return false;
            Update(id, current => current with { Archived = false, ArchivedAt = null });
            AddEvent(id, "Mission", StepStatus.Succeeded, "Mission restored from archive; results and workspace preserved.");
            return true;
        }
    }

    public CancellationToken Cancellation(Guid id)
    {
        lock (sync)
            return cancellations[id].Token;
    }

    public MissionSnapshot Update(Guid id, Func<MissionSnapshot, MissionSnapshot> update)
    {
        lock (sync)
        {
            var previous = missions[id];
            using var persistence = previous.Workflow?.AutomaticReactCorrections == true
                ? new PersistenceLock(persistenceMutexName) : null;
            if (previous.Workflow is { AutomaticReactCorrections: true } previousFlow &&
                ReadLatestForRepair(id).Workflow?.SoftwareCheckpointVersion != previousFlow.SoftwareCheckpointVersion)
                throw new StudioValidationException("A concurrent React workflow writer changed the software checkpoint.");
            var updatedAt = DateTimeOffset.UtcNow;
            var next = update(previous) with { UpdatedAt = updatedAt };
            if (next.MaxTotalGenerations != previous.MaxTotalGenerations)
                throw new StudioValidationException("A mission's frozen generation limit cannot be changed.");
            next = next with { GenerationReservations = previous.GenerationReservations,
                GenerationBudgetExhausted = previous.GenerationBudgetExhausted };
            next = next with
            {
                CompletedAt = next.IsTerminal
                    ? previous.CompletedAt ?? (!previous.IsTerminal ? updatedAt : null)
                    : null
            };
            if (previous.Workflow is { AutomaticReactCorrections: true } previousSoftware && next.Workflow is { } nextSoftware)
                next = next with { Workflow = nextSoftware with
                    { SoftwareCheckpointVersion = previousSoftware.SoftwareCheckpointVersion + 1 } };
            if ((next.Status is MissionStatus.Completed or MissionStatus.GeneratedUnverified or MissionStatus.Verified) &&
                cancellations.TryGetValue(id, out var cancellation))
                cancellation.Token.ThrowIfCancellationRequested();
            if (next.Workflow is { } flow && (next.Status is MissionStatus.Failed or MissionStatus.Cancelled))
                next = next with
                {
                    Workflow = flow with
                    {
                        Steps = flow.Steps.Select(step => step.Status == WorkflowStatus.Running ||
                            (next.Status == MissionStatus.Cancelled && step.Status is WorkflowStatus.Pending or WorkflowStatus.WaitingForApproval)
                            ? step with
                            {
                                Status = next.Status == MissionStatus.Cancelled ? WorkflowStatus.Cancelled : WorkflowStatus.Failed,
                                Error = next.Error ?? "Mission cancelled.",
                                DurationMilliseconds = step.StartedAt is { } start ? (DateTimeOffset.UtcNow - start).TotalMilliseconds : null
                            }
                            : step).ToArray()
                    }
                };
            next = Save(next);
            missions[id] = next;
            return next;
        }
    }

    public bool RecordWorkspaceEdit(Guid id, DateTimeOffset expectedUpdatedAt, Guid agentId, Guid conversationId,
        Guid proposalId, IReadOnlyList<string> changedPaths, IReadOnlyList<GeneratedFile> files)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var current) || current.UpdatedAt != expectedUpdatedAt ||
                !current.HasWorkspace || current.Deleted || current.Archived || current.SandboxApproved ||
                current.Status is MissionStatus.Queued or MissionStatus.Running ||
                current.Workflow is not { } flow || changedPaths.Count is < 1 or > 8 ||
                changedPaths.Any(string.IsNullOrWhiteSpace) || files.Count == 0)
                return false;

            var version = flow.Version + 1;
            var now = DateTimeOffset.UtcNow;
            var stableFiles = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stableFiles)));
            ExecutionPlan? executionPlan = null;
            if ((current.TemplateId is ReactTemplate.Id or MissionReactTemplate.Id) &&
                flow.Revisions?.LastOrDefault() is { } previous &&
                previous.ExecutionPlan is { ReactDescriptor: { } descriptor } saved &&
                saved.Fingerprint == previous.Fingerprint &&
                descriptor.ComputeFingerprint(previous.Files) == previous.Fingerprint &&
                descriptor.CompareManifest(previous.Files).Count == 0)
            {
                var nextDescriptor = descriptor.WithFiles(stableFiles);
                executionPlan = saved with
                {
                    Fingerprint = nextDescriptor.ComputeFingerprint(stableFiles),
                    ReactDescriptor = nextDescriptor
                };
                fingerprint = executionPlan.Fingerprint;
            }
            var revision = new ProjectRevision(version, now,
                "Human-approved custom-agent change; no commands were executed.", fingerprint, stableFiles, executionPlan);
            var steps = flow.Steps.Select(step => step.Status is WorkflowStatus.Pending or WorkflowStatus.Running or
                WorkflowStatus.WaitingForApproval
                    ? step with { Status = WorkflowStatus.Skipped, Output = "Superseded by an approved workspace edit." }
                    : step).ToList();
            var editor = flow.Agents.FirstOrDefault(agent => agent.Role == "Orchestrator")?.Id ??
                flow.Agents.FirstOrDefault()?.Id;
            if (editor is null)
                return false;
            steps.Add(new($"custom-agent-edit-{version}", "ManualRevision", editor, [], WorkflowStatus.Completed,
                1, now, Input: $"Custom agent {agentId:N}; user approved proposal {proposalId:N}.",
                Output: $"Changed {changedPaths.Count} file(s). No build or test was run."));
            WorkflowGraph.Validate(steps);

            if (cancellations.Remove(id, out var previousCancellation))
                previousCancellation.Dispose();
            cancellations[id] = new();
            var eventMessage = $"User-approved custom-agent proposal {proposalId:N} changed: " +
                string.Join(", ", changedPaths.Order(StringComparer.Ordinal));
            Update(id, mission => mission with
            {
                Status = MissionStatus.GeneratedUnverified,
                SandboxApproved = false,
                ApprovalFingerprint = null,
                Error = null,
                Response = null,
                Events = mission.Events.Append(new(mission.Events.Count + 1, now, "CustomAgentEdit",
                    StepStatus.Succeeded, eventMessage)).ToArray(),
                Workflow = flow with
                {
                    Version = version,
                    Steps = steps,
                    Review = new ReviewReport(
                        "Manual changes were approved by the user; no automated review, build, or tests were run.", []),
                    Draft = stableFiles,
                    Proposal = null,
                    Revisions = [.. flow.Revisions ?? [], revision],
                    ExecutionPlanDifferences = null,
                    FinalReport = "Workspace modified by a user-approved custom-agent proposal. Validation was not run."
                }
            });
            return true;
        }
    }

    public void AddEvent(Guid id, string step, StepStatus status, string message) =>
        Update(id, mission =>
        {
            var now = DateTimeOffset.UtcNow;
            var previous = mission.Events.LastOrDefault(item => item.Step == step);
            double? duration = status != StepStatus.Running && previous?.Status == StepStatus.Running
                ? (now - previous.Timestamp).TotalMilliseconds : null;
            return mission with
            {
                Events = mission.Events.Append(new(mission.Events.Count + 1,
                now, step, status, message, duration)).ToArray()
            };
        });

    public IDisposable EnterGenerationScope(Guid id, string role) => EnterGenerationScope(id, role, null);

    public IDisposable EnterGenerationScope(Guid id, string role, Guid? capacityId) =>
        new LlmGenerationScope((request, actualRole) => ReserveGeneration(id, actualRole, request.Model.Name, capacityId), role);

    private void ReserveGeneration(Guid id, string role, string model, Guid? capacityId)
    {
        lock (sync)
        {
            using var persistence = new PersistenceLock(persistenceMutexName);
            var mission = missions[id];
            var path = Path.Combine(directory, id.ToString("N") + ".json");
            SafePaths.RejectLinks(path);
            var persisted = JsonSerializer.Deserialize<MissionSnapshot>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("Invalid persisted generation budget.");
            if (persisted.Id != id || persisted.MaxTotalGenerations != mission.MaxTotalGenerations)
                throw new InvalidDataException("The persisted mission identity or frozen generation limit changed.");
            if (persisted.MaxTotalGenerations is not { } limit) return;
            var reservations = persisted.GenerationReservations
                ?? throw new InvalidDataException("A budgeted mission is missing its reservation ledger.");
            if (limit is < 1 or > 1000 || reservations.Count > limit ||
                reservations.Select(item => item.Id).Distinct().Count() != reservations.Count)
                throw new InvalidDataException("Invalid persisted generation reservations.");
            mission = persisted;
            if (reservations.Count >= limit || persisted.GenerationBudgetExhausted)
            {
                var error = new GenerationBudgetExhaustedException();
                mission = mission with { GenerationBudgetExhausted = true, Status = MissionStatus.Failed,
                    Error = error.Message, SandboxApproved = false, ApprovalFingerprint = null,
                    Events = [.. mission.Events, new(mission.Events.Count + 1, DateTimeOffset.UtcNow,
                        "GenerationBudget", StepStatus.Failed, error.Message)] };
                missions[id] = Save(mission);
                throw error;
            }
            var repair = ReadRepairCheckpoint(id);
            if (repair is not null)
            {
                RequireApprovedPlan(mission);
                var paid = reservations.Where(item => item.CapacityId == repair.Id).ToArray();
                var allowed = !mission.Deleted && !mission.Archived &&
                    (role == "Developer" && capacityId == repair.Id && !repair.Ready && repair.InFlight &&
                    mission.Status == MissionStatus.Failed &&
                    WorkflowSourceFingerprint.Compute(mission) == repair.SourceFingerprint &&
                    ReactDraftRepair.SourcePlanFingerprint(mission) == repair.Plan.SourcePlanFingerprint &&
                    model == repair.Plan.Model && paid.Count(item => item.Role == "Developer") == repair.CompletedGroups &&
                    repair.CompletedGroups < repair.Calls.Length ||
                    role == "Reviewer" && repair.Ready &&
                    mission.Workflow!.Draft!.SequenceEqual(repair.Candidate) &&
                    ReactDraftRepair.SourcePlanFingerprint(mission) == repair.Plan.SourcePlanFingerprint &&
                    paid.Count(item => item.Role == "Reviewer") < repair.Plan.ReviewerGroups.Count);
                if (!allowed)
                    throw new StudioValidationException("Generation capacity is protected by the durable draft repair; no model was called.");
                if (role == "Developer")
                {
                    var first = paid.Length == 0;
                    if (mission.CorrectionAttempts != repair.CorrectionAttemptsBefore + (first ? 0 : 1) ||
                        first && mission.CorrectionAttempts >= options.CorrectionLimitFor(mission))
                        throw new StudioValidationException("The draft correction attempt limit changed; no model was called.");
                    if (first) mission = mission with { CorrectionAttempts = mission.CorrectionAttempts + 1 };
                }
                capacityId = repair.Id;
            }
            else if (mission.Workflow is { AutomaticReactCorrections: true } software &&
                software.SoftwareCorrections.LastOrDefault() is { Ready: false } correction)
            {
                RequireApprovedPlan(mission);
                var paid = reservations.Count(item => item.CapacityId == correction.Id);
                if (mission.Status != MissionStatus.Running || role != "Developer" || capacityId != correction.Id ||
                    !correction.InFlight || correction.Failure is not null || correction.CompletedGroups != paid ||
                    paid >= correction.Calls.Count || model != correction.Calls[paid].Model.Name ||
                    limit - reservations.Count < correction.Calls.Count - paid + correction.ReviewerCapacity)
                    throw new StudioValidationException("Automatic correction capacity is protected; no model was called.");
            }
            else if (capacityId is not null)
                throw new StudioValidationException("The prepared generation capacity is missing; no model was called.");
            else if (mission.Workflow is { AutomaticReactCorrections: true } activeSoftware)
            {
                if (mission.Status != MissionStatus.Running || mission.Deleted || mission.Archived ||
                    activeSoftware.PendingSoftwareAgentCall is null ||
                    role != "Reviewer" && reservations.Count > activeSoftware.PendingSoftwareReservationCount)
                    throw new StudioValidationException("The software agent operation is missing or already charged; no automatic replay.");
                if (role != "Planner") RequireApprovedPlan(mission);
            }
            var reservation = new MissionGenerationReservation(Guid.NewGuid(), role, model, DateTimeOffset.UtcNow, capacityId);
            mission = mission with
            {
                GenerationReservations = [.. reservations, reservation],
                Events = [.. mission.Events, new(mission.Events.Count + 1, reservation.ReservedAt,
                    "GenerationBudget", StepStatus.Running, $"{role}: reserved generation {reservations.Count + 1}/{limit} ({model}); never automatically refunded.")]
            };
            // Budget metadata does not change the workspace's optimistic concurrency timestamp.
            missions[id] = Save(mission);
        }
    }

    public void WarnGenerationBudget(Guid id, int minimumRemaining)
    {
        lock (sync)
        {
            var mission = missions[id];
            if (mission.MaxTotalGenerations is not { } limit ||
                limit - mission.GenerationReservations!.Count >= minimumRemaining) return;
            var warning = $"GenerationBudgetRisk: {minimumRemaining} initial calls remain in the validated plan, " +
                $"but only {limit - mission.GenerationReservations.Count} units remain. Completion is not guaranteed.";
            missions[id] = Save(mission with { GenerationBudgetWarning = warning,
                Events = [.. mission.Events, new(mission.Events.Count + 1, DateTimeOffset.UtcNow,
                    "GenerationBudgetRisk", StepStatus.Unavailable, warning)] });
        }
    }

    private MissionSnapshot Save(MissionSnapshot snapshot)
    {
        using var persistence = new PersistenceLock(persistenceMutexName);
        var path = Path.Combine(directory, snapshot.Id.ToString("N") + ".json");
        var temporary = path + ".tmp";
        SafePaths.RejectLinks(path);
        SafePaths.RejectLinks(temporary);
        if (File.Exists(path))
        {
            var persisted = JsonSerializer.Deserialize<MissionSnapshot>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("Invalid saved mission.");
            if (persisted.Workflow is { AutomaticReactCorrections: true } software &&
                (snapshot.Workflow is null || snapshot.Workflow.SoftwareCheckpointVersion < software.SoftwareCheckpointVersion))
                throw new StudioValidationException("A stale writer cannot overwrite a durable React software checkpoint.");
            var repair = ReadRepairCheckpoint(snapshot.Id);
            if (!snapshot.Deleted && repair is not null && repair.CompletedGroups == repair.Calls.Length &&
                !repair.Original.SequenceEqual(repair.Candidate) && persisted.Workflow?.Draft?.SequenceEqual(repair.Candidate) == true &&
                (snapshot.Workflow?.Draft?.SequenceEqual(repair.Candidate) != true ||
                 ReactDraftRepair.SourcePlanFingerprint(snapshot) != repair.Plan.SourcePlanFingerprint))
                throw new StudioValidationException("A stale writer cannot overwrite the protected repair candidate or its frozen plan.");
            if (snapshot.MaxTotalGenerations != persisted.MaxTotalGenerations)
                throw new InvalidDataException("The persisted generation limit is immutable.");
            if (snapshot.Sizing != persisted.Sizing)
                throw new StudioValidationException("The persisted project sizing profile is immutable.");
            if (persisted.MaxTotalGenerations is { } limit)
            {
                var existing = persisted.GenerationReservations
                    ?? throw new InvalidDataException("Missing persisted generation reservations.");
                var reservations = existing.Concat(snapshot.GenerationReservations ?? [])
                    .DistinctBy(item => item.Id).ToArray();
                if (reservations.Length > limit) throw new InvalidDataException("Generation reservation limit exceeded.");
                snapshot = snapshot with { GenerationReservations = reservations,
                    GenerationBudgetExhausted = snapshot.GenerationBudgetExhausted || persisted.GenerationBudgetExhausted };
            }
        }
        if (snapshot.GenerationBudgetExhausted)
            snapshot = snapshot with { Status = MissionStatus.Failed, Error = new GenerationBudgetExhaustedException().Message,
                SandboxApproved = false, ApprovalFingerprint = null, CompletedAt = snapshot.CompletedAt ?? DateTimeOffset.UtcNow };
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, snapshot, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                SafePaths.RejectLinks(path);
                SafePaths.RejectLinks(temporary);
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
                break;
            }
            catch (Exception exception) when (attempt < 4 &&
                       exception is IOException or UnauthorizedAccessException)
            {
                // Windows scanners can briefly deny rename/delete sharing on freshly written metadata.
                Thread.Sleep(10 * (1 << attempt));
            }
        }
        if (snapshot.Deleted)
        {
            var checkpoint = RepairPath(snapshot.Id);
            if (File.Exists(checkpoint)) File.Delete(checkpoint);
        }
        return snapshot;
    }

    private sealed class PersistenceLock : IDisposable
    {
        private readonly Mutex mutex;
        public PersistenceLock(string name)
        {
            mutex = new(false, name);
            try { mutex.WaitOne(); }
            // An abandoned mutex transfers ownership; the atomic snapshot, not memory, is authoritative.
            catch (AbandonedMutexException) { }
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var cancellation in cancellations.Values)
            cancellation.Dispose();
    }
}
