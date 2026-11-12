using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class MissionStore : IMissionService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object sync = new();
    private readonly Dictionary<Guid, MissionSnapshot> missions = [];
    private readonly Dictionary<Guid, CancellationTokenSource> cancellations = [];
    private readonly string directory;
    private readonly PlatformConfiguration platform;
    private readonly StudioOptions options;
    private readonly IAgentRegistry registry;
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public MissionStore(StudioOptions options, PlatformConfiguration platform, IAgentRegistry registry,
        IWorkspaceStore? workspaces = null)
    {
        this.options = options;
        this.platform = platform;
        this.registry = registry;
        this.workspaces = workspaces;
        directory = Path.Combine(SafePaths.ValidateStorageDirectory(options.DataDirectory), "missions");
        SafePaths.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            SafePaths.RejectLinks(path);
            var snapshot = JsonSerializer.Deserialize<MissionSnapshot>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Invalid saved mission: {Path.GetFileName(path)}.");
            if (Path.GetFileNameWithoutExtension(path) != snapshot.Id.ToString("N"))
                throw new InvalidDataException("A saved mission identifier does not match its filename.");
            if (!snapshot.IsTerminal && snapshot.Status != MissionStatus.AwaitingApproval)
            {
                snapshot = snapshot with
                {
                    Status = MissionStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow,
                    Error = "Mission interrupted by a server restart. Explicit resumption is required.",
                    SandboxApproved = false, ApprovalFingerprint = null,
                    Workflow = snapshot.Workflow is { } interrupted ? interrupted with
                    {
                        Steps = interrupted.Steps.Select(step => step.Status == WorkflowStatus.Running
                            ? step with { Status = WorkflowStatus.Cancelled, Error = "Interrupted by backend restart." } : step).ToArray()
                    } : null,
                    Events = snapshot.Events.Append(new(snapshot.Events.Count + 1, DateTimeOffset.UtcNow,
                        "Mission", StepStatus.Cancelled, "Interrupted by server restart; not automatically re-executed.")).ToArray()
                };
                Save(snapshot);
            }
            missions.Add(snapshot.Id, snapshot);
            if (snapshot.Status == MissionStatus.AwaitingApproval)
                cancellations.Add(snapshot.Id, new());
        }
    }

    public MissionSnapshot Submit(MissionSubmission submission)
    {
        if (string.IsNullOrWhiteSpace(submission.Task) || submission.Task.Length > platform.MaxTaskLength ||
            !Enum.IsDefined(submission.Kind))
            throw new StudioValidationException($"Provide a task of 1 to {platform.MaxTaskLength} characters and a valid kind.");
        var candidates = submission.Kind == MissionKind.Chat ? registry.Candidates :
            registry.Agents.Where(agent => agent.Enabled &&
                (options.CollaborativeWorkflows ? agent.Role == "Developer" && agent.TaskTypes.Contains("Project") :
                    agent.Id == options.DeveloperAgentId)).ToArray();
        if (submission.AgentId is not null && !candidates.Any(agent => agent.Id == submission.AgentId))
            throw new StudioValidationException("The requested execution agent is not registered.");
        if (!options.CollaborativeWorkflows && submission.Kind == MissionKind.Project && submission.AgentId is not null &&
            submission.AgentId != options.DeveloperAgentId)
            throw new StudioValidationException("Project generation requires the configured developer agent.");

        lock (sync)
        {
            if (missions.Count >= options.MaxMissions)
                throw new StudioCapacityException("The persisted mission limit has been reached. Archive data while the server is stopped.");
            var now = DateTimeOffset.UtcNow;
            var snapshot = new MissionSnapshot(
                Guid.NewGuid(), submission.Task.Trim(), submission.Kind, submission.AgentId,
                submission.ApproveSandboxExecution, MissionStatus.Queued, now, now,
                null, null, null, false, null, null, false, 0,
                [new(1, now, "Queue", StepStatus.Running, "Waiting for the single mission worker.")], []);
            if (options.CollaborativeWorkflows && submission.Kind == MissionKind.Project)
            {
                var workflow = CollaborativeProcessor.Create(snapshot.Id, registry);
                var developer = workflow.Agents.Single(agent => agent.Role == "Developer" && agent.Enabled);
                snapshot = snapshot with { Workflow = workflow, SandboxApproved = false,
                    SelectedAgent = developer.Id,
                    SelectedModel = workflow.Models.Single(model => model.Id == developer.ModelId).Name,
                    Reason = "Deterministic collaborative .NET workflow; each sensitive file version requires human approval." };
            }
            Save(snapshot);
            missions.Add(snapshot.Id, snapshot);
            cancellations.Add(snapshot.Id, new());
            if (!queue.Writer.TryWrite(snapshot.Id))
                throw new InvalidOperationException("The mission worker queue is closed.");
            return snapshot;
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
            if (current.Status is MissionStatus.Queued or MissionStatus.AwaitingApproval)
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
            Update(id, mission => mission with
            {
                SandboxApproved = approved,
                ApprovalFingerprint = approved ? fingerprint : null,
                Status = approved ? MissionStatus.Queued : MissionStatus.GeneratedUnverified,
                Workflow = mission.Workflow is { } flow ? flow with
                {
                    FinalReport = approved ? null : "Execution refused. Files retained; build and tests not executed.",
                    Steps = flow.Steps.Select(step => step.Status == WorkflowStatus.WaitingForApproval
                        ? step with { Status = approved ? WorkflowStatus.Completed : WorkflowStatus.Skipped,
                            Output = approved ? $"Human approved fingerprint {fingerprint}." : "Human refused execution.",
                            DurationMilliseconds = step.StartedAt is { } start ? (DateTimeOffset.UtcNow - start).TotalMilliseconds : null }
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
                Update(id, mission => mission with
                {
                    Verification =
                    [
                        .. mission.Verification, new("Build", "Skipped", null, "Human approval refused; not executed."),
                        new("Tests", "Skipped", null, "Human approval refused; not executed.")
                    ]
                });
                AddEvent(id, "Mission", StepStatus.Skipped, "GeneratedUnverified: execution refused by the user.");
            }
            return true;
        }
    }

    public IAsyncEnumerable<Guid> ReadQueue(CancellationToken cancellationToken) =>
        queue.Reader.ReadAllAsync(cancellationToken);

    private readonly IWorkspaceStore? workspaces;

    public bool Resume(Guid id)
    {
        lock (sync)
        {
            if (!missions.TryGetValue(id, out var current) || !current.IsTerminal ||
                current.Status is MissionStatus.Verified or MissionStatus.Completed || current.Deleted || current.Workflow is not { } flow)
                return false;
            cancellations.GetValueOrDefault(id)?.Dispose();
            cancellations[id] = new();
            var steps = flow.Steps;
            if (current.HasWorkspace && flow.Revisions?.Count > 0)
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
            Update(id, mission => mission with { Status = MissionStatus.Queued, Error = null,
                SandboxApproved = false, ApprovalFingerprint = null, Workflow = flow with { FinalReport = null } });
            AddEvent(id, "Queue", StepStatus.Running, "Explicit resumption; prior authorization discarded.");
            if (!queue.Writer.TryWrite(id)) throw new InvalidOperationException("Mission queue closed.");
            return true;
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
            Update(id, current => current with { HasWorkspace = false, Deleted = true,
                Workflow = current.Workflow is { } flow ? flow with { Draft = [], Revisions = [] } : null });
            AddEvent(id, "Project", StepStatus.Succeeded, "Project sources deleted by explicit user request. Mission audit retained.");
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
            var next = update(previous) with { UpdatedAt = DateTimeOffset.UtcNow };
            if ((next.Status is MissionStatus.Completed or MissionStatus.GeneratedUnverified or MissionStatus.Verified) &&
                cancellations.TryGetValue(id, out var cancellation))
                cancellation.Token.ThrowIfCancellationRequested();
            if (next.Workflow is { } flow && (next.Status is MissionStatus.Failed or MissionStatus.Cancelled))
                next = next with { Workflow = flow with
                {
                    Steps = flow.Steps.Select(step => step.Status == WorkflowStatus.Running ||
                        (next.Status == MissionStatus.Cancelled && step.Status is WorkflowStatus.Pending or WorkflowStatus.WaitingForApproval)
                        ? step with { Status = next.Status == MissionStatus.Cancelled ? WorkflowStatus.Cancelled : WorkflowStatus.Failed,
                            Error = next.Error ?? "Mission cancelled.",
                            DurationMilliseconds = step.StartedAt is { } start ? (DateTimeOffset.UtcNow - start).TotalMilliseconds : null }
                        : step).ToArray()
                } };
            Save(next);
            missions[id] = next;
            return next;
        }
    }

    public void AddEvent(Guid id, string step, StepStatus status, string message) =>
        Update(id, mission =>
        {
            var now = DateTimeOffset.UtcNow;
            var previous = mission.Events.LastOrDefault(item => item.Step == step);
            double? duration = status != StepStatus.Running && previous?.Status == StepStatus.Running
                ? (now - previous.Timestamp).TotalMilliseconds : null;
            return mission with { Events = mission.Events.Append(new(mission.Events.Count + 1,
                now, step, status, message, duration)).ToArray() };
        });

    private void Save(MissionSnapshot snapshot)
    {
        var path = Path.Combine(directory, snapshot.Id.ToString("N") + ".json");
        var temporary = path + ".tmp";
        SafePaths.RejectLinks(path);
        SafePaths.RejectLinks(temporary);
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
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
    }

    public void Dispose()
    {
        foreach (var cancellation in cancellations.Values)
            cancellation.Dispose();
    }
}
