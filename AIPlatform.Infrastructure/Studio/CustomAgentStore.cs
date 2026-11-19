using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class CustomAgentStore : ICustomAgentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly string directory;
    private readonly string statePath;

    public CustomAgentStore(StudioOptions options)
    {
        directory = Path.Combine(SafePaths.ValidateStorageDirectory(options.DataDirectory), "custom-agents");
        SafePaths.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        SafePaths.RejectLinks(directory);
        statePath = Path.Combine(directory, "state.json");
    }

    public IReadOnlyList<CustomAgentProfile> ListAgents()
    {
        lock (sync)
            return Read().Agents.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public CustomAgentProfile? GetAgent(Guid id)
    {
        lock (sync)
            return Read().Agents.SingleOrDefault(agent => agent.Id == id);
    }

    public void AddAgent(CustomAgentProfile agent)
    {
        lock (sync)
        {
            var state = Read();
            if (state.Agents.Count >= CustomAgentLimits.MaxAgents)
                throw new StudioValidationException($"Studio supports at most {CustomAgentLimits.MaxAgents} custom agents.");
            if (state.Agents.Any(existing => existing.Id == agent.Id))
                throw new InvalidDataException("Custom agent identifiers must be unique.");
            Write(state with { Agents = [.. state.Agents, agent] });
        }
    }

    public bool UpdateAgent(CustomAgentProfile agent)
    {
        lock (sync)
        {
            var state = Read();
            var index = state.Agents.FindIndex(existing => existing.Id == agent.Id);
            if (index < 0)
                return false;
            var agents = state.Agents.ToList();
            agents[index] = agent;
            Write(state with { Agents = agents });
            return true;
        }
    }

    public bool DeleteAgent(Guid id)
    {
        lock (sync)
        {
            var state = Read();
            if (state.DevelopmentPlans.Any(plan => plan.ConversationAgentId == id))
                throw new StudioValidationException("An agent owning a project conversation cannot be deleted while its plan is retained.");
            if (state.Agents.RemoveAll(agent => agent.Id == id) == 0)
                return false;
            Write(state with
            {
                Conversations = state.Conversations.Where(item => item.AgentId != id).ToList(),
                Teams = state.Teams.Where(team => team.Members.All(member => member.AgentId != id)).ToList()
            });
            return true;
        }
    }

    public IReadOnlyList<CustomAgentConversationSummary> ListConversations(Guid agentId)
    {
        lock (sync)
            return Read().Conversations.Where(item => item.AgentId == agentId)
                .OrderByDescending(item => item.UpdatedAt)
                .Select(item => new CustomAgentConversationSummary(
                    item.Id, item.AgentId, item.Title, item.CreatedAt, item.UpdatedAt, item.Messages.Count))
                .ToArray();
    }

    public CustomAgentConversation? GetConversation(Guid agentId, Guid conversationId)
    {
        lock (sync)
            return Read().Conversations.SingleOrDefault(item =>
                item.AgentId == agentId && item.Id == conversationId);
    }

    public CustomAgentConversation? CreateConversation(CustomAgentConversation conversation)
    {
        lock (sync)
        {
            var state = Read();
            if (!state.Agents.Any(agent => agent.Id == conversation.AgentId))
                return null;
            if (state.Conversations.Count(item => item.AgentId == conversation.AgentId) >=
                CustomAgentLimits.MaxConversationsPerAgent)
                throw new StudioValidationException(
                    $"A custom agent can have at most {CustomAgentLimits.MaxConversationsPerAgent} conversations. Delete an old conversation to continue.");
            Write(state with { Conversations = [.. state.Conversations, conversation] });
            return conversation;
        }
    }

    public bool UpdateConversation(CustomAgentConversation conversation)
    {
        lock (sync)
        {
            var state = Read();
            var index = state.Conversations.FindIndex(item =>
                item.AgentId == conversation.AgentId && item.Id == conversation.Id);
            if (index < 0)
                return false;
            if (state.DevelopmentPlans.Any(plan => plan.ConversationId == conversation.Id &&
                    (conversation.Workspace?.Kind != CustomAgentWorkspaceKind.Studio ||
                     conversation.Workspace.WorkspaceId != plan.Workspace.WorkspaceId)))
                throw new StudioValidationException("A project conversation must remain associated with its development plan workspace.");
            var conversations = state.Conversations.ToList();
            conversations[index] = conversation;
            Write(state with { Conversations = conversations });
            return true;
        }
    }

    public bool DeleteConversation(Guid agentId, Guid conversationId)
    {
        lock (sync)
        {
            var state = Read();
            if (state.DevelopmentPlans.Any(plan => plan.ConversationId == conversationId &&
                    plan.ConversationAgentId == agentId))
                throw new StudioValidationException("A conversation linked to a development plan cannot be deleted.");
            if (state.Conversations.RemoveAll(item =>
                    item.AgentId == agentId && item.Id == conversationId) == 0)
                return false;
            Write(state);
            return true;
        }
    }

    public IReadOnlyList<CustomAgentTeam> ListTeams()
    {
        lock (sync)
            return Read().Teams.OrderBy(team => team.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public CustomAgentTeam? GetTeam(Guid id)
    {
        lock (sync)
            return Read().Teams.SingleOrDefault(team => team.Id == id);
    }

    public void SaveTeam(CustomAgentTeam team)
    {
        lock (sync)
        {
            var state = Read();
            var index = state.Teams.FindIndex(existing => existing.Id == team.Id);
            if (index < 0 && state.Teams.Count >= CustomAgentLimits.MaxTeams)
                throw new StudioValidationException($"Studio supports at most {CustomAgentLimits.MaxTeams} custom-agent teams.");
            var teams = state.Teams.ToList();
            if (index < 0)
                teams.Add(team);
            else
                teams[index] = team;
            Write(state with { Teams = teams });
        }
    }

    public bool DeleteTeam(Guid id)
    {
        lock (sync)
        {
            var state = Read();
            if (state.Teams.RemoveAll(team => team.Id == id) == 0)
                return false;
            Write(state);
            return true;
        }
    }

    public IReadOnlyList<CustomAgentDevelopmentPlan> ListDevelopmentPlans(Guid teamId)
    {
        lock (sync)
            return Read().DevelopmentPlans.Where(plan => plan.TeamId == teamId)
                .OrderByDescending(plan => plan.UpdatedAt).ToArray();
    }

    public CustomAgentDevelopmentPlan? GetDevelopmentPlan(Guid teamId, Guid planId)
    {
        lock (sync)
            return Read().DevelopmentPlans.SingleOrDefault(plan => plan.TeamId == teamId && plan.Id == planId);
    }

    public void AddDevelopmentPlan(CustomAgentDevelopmentPlan plan)
    {
        lock (sync)
        {
            var state = Read();
            if (state.DevelopmentPlans.Any(existing => existing.Id == plan.Id))
                throw new InvalidDataException("Development plan identifiers must be unique.");
            var plans = state.DevelopmentPlans.ToList();
            if (plans.Count >= CustomAgentLimits.MaxDevelopmentPlans)
            {
                var oldestInactive = plans.Where(item => item.Status is not
                        ("Running" or "Pausing" or "Cancelling"))
                    .MinBy(item => item.CreatedAt);
                if (oldestInactive is null)
                    throw new StudioCapacityException(
                        "All development-plan history slots are currently active. Wait for a plan to finish.");
                plans.Remove(oldestInactive);
            }
            plans.Add(plan);
            Write(state with { DevelopmentPlans = plans });
        }
    }

    public bool UpdateDevelopmentPlan(CustomAgentDevelopmentPlan plan)
    {
        lock (sync)
        {
            var state = Read();
            var index = state.DevelopmentPlans.FindIndex(existing =>
                existing.TeamId == plan.TeamId && existing.Id == plan.Id);
            if (index < 0)
                return false;
            var plans = state.DevelopmentPlans.ToList();
            plans[index] = plan;
            Write(state with { DevelopmentPlans = plans });
            return true;
        }
    }

    public void SaveDevelopmentPlanConversation(
        CustomAgentDevelopmentPlan plan, CustomAgentConversation conversation,
        DateTimeOffset? expectedPlanUpdatedAt, DateTimeOffset? expectedConversationUpdatedAt)
    {
        lock (sync)
        {
            var state = Read();
            if (plan.ConversationId != conversation.Id || plan.ConversationAgentId != conversation.AgentId ||
                conversation.Workspace?.Kind != CustomAgentWorkspaceKind.Studio ||
                conversation.Workspace.WorkspaceId != plan.Workspace.WorkspaceId)
                throw new StudioValidationException("The plan and conversation must refer to the same Studio project.");
            var plans = state.DevelopmentPlans.ToList();
            var conversations = state.Conversations.ToList();
            var planIndex = plans.FindIndex(item => item.Id == plan.Id && item.TeamId == plan.TeamId);
            var conversationIndex = conversations.FindIndex(item => item.Id == conversation.Id &&
                item.AgentId == conversation.AgentId);
            if (expectedPlanUpdatedAt is null)
            {
                if (planIndex >= 0)
                    throw new StudioValidationException("The development plan already exists.");
                if (plans.Count >= CustomAgentLimits.MaxDevelopmentPlans)
                    throw new StudioCapacityException("Development-plan history is full.");
                plans.Add(plan);
            }
            else
            {
                if (planIndex < 0 || plans[planIndex].UpdatedAt != expectedPlanUpdatedAt ||
                    plans[planIndex].Status is "Running" or "Pausing" or "Cancelling")
                    throw new StudioValidationException("The development plan changed while planning. Reload and retry.");
                plans[planIndex] = plan;
            }
            if (expectedConversationUpdatedAt is null)
            {
                if (conversationIndex >= 0 || !state.Agents.Any(agent => agent.Id == conversation.AgentId))
                    throw new StudioValidationException("The project conversation could not be created.");
                if (conversations.Count(item => item.AgentId == conversation.AgentId) >=
                    CustomAgentLimits.MaxConversationsPerAgent)
                    throw new StudioCapacityException("The Planner conversation history is full.");
                conversations.Add(conversation);
            }
            else
            {
                if (conversationIndex < 0 ||
                    conversations[conversationIndex].UpdatedAt != expectedConversationUpdatedAt ||
                    conversations[conversationIndex].Workspace?.WorkspaceId != plan.Workspace.WorkspaceId)
                    throw new StudioValidationException("The project conversation changed while planning. Reload and retry.");
                conversations[conversationIndex] = conversation;
            }
            Write(state with { DevelopmentPlans = plans, Conversations = conversations });
        }
    }

    public IReadOnlyList<CustomAgentCollaborationRun> ListCollaborationRuns(Guid teamId)
    {
        lock (sync)
            return Read().CollaborationRuns.Where(run => run.TeamId == teamId)
                .OrderByDescending(run => run.UpdatedAt).ToArray();
    }

    public CustomAgentCollaborationRun? GetCollaborationRun(Guid teamId, Guid runId)
    {
        lock (sync)
            return Read().CollaborationRuns.SingleOrDefault(run => run.TeamId == teamId && run.Id == runId);
    }

    public void AddCollaborationRun(CustomAgentCollaborationRun run)
    {
        lock (sync)
        {
            var state = Read();
            if (state.CollaborationRuns.Any(existing => existing.Id == run.Id))
                throw new InvalidDataException("Collaboration run identifiers must be unique.");
            var runs = state.CollaborationRuns.ToList();
            if (runs.Count >= CustomAgentLimits.MaxCollaborationRuns)
            {
                var oldestCompleted = runs.Where(item => item.Status is not ("Queued" or "Running"))
                    .MinBy(item => item.CreatedAt);
                if (oldestCompleted is null)
                    throw new StudioCapacityException(
                        "All collaboration history slots are currently active. Wait for a run to finish.");
                runs.Remove(oldestCompleted);
            }
            runs.Add(run);
            Write(state with { CollaborationRuns = runs });
        }
    }

    public bool UpdateCollaborationRun(CustomAgentCollaborationRun run)
    {
        lock (sync)
        {
            var state = Read();
            var index = state.CollaborationRuns.FindIndex(existing =>
                existing.TeamId == run.TeamId && existing.Id == run.Id);
            if (index < 0)
                return false;
            var runs = state.CollaborationRuns.ToList();
            runs[index] = run;
            Write(state with { CollaborationRuns = runs });
            return true;
        }
    }

    private StoreState Read()
    {
        if (!File.Exists(statePath))
            return new([], []);
        SafePaths.RejectLinks(statePath);
        if (new FileInfo(statePath).Length > CustomAgentLimits.MaxStoredBytes)
            throw new InvalidDataException("Custom agent storage exceeds its configured size limit.");
        var state = JsonSerializer.Deserialize<StoreState>(File.ReadAllText(statePath), JsonOptions)
            ?? throw new InvalidDataException("Invalid custom agent storage.");
        Validate(state);
        return state;
    }

    private void Write(StoreState state)
    {
        Validate(state);
        var content = JsonSerializer.Serialize(state, JsonOptions);
        if (Encoding.UTF8.GetByteCount(content) > CustomAgentLimits.MaxStoredBytes)
            throw new StudioValidationException(
                "Custom agent storage is full. Delete an old conversation or agent to make room.");
        var temporary = statePath + ".tmp";
        SafePaths.RejectLinks(statePath);
        SafePaths.RejectLinks(temporary);
        File.WriteAllText(temporary, content, Encoding.UTF8);
        SafePaths.RejectLinks(statePath);
        SafePaths.RejectLinks(temporary);
        File.Move(temporary, statePath, overwrite: true);
    }

    private static void Validate(StoreState state)
    {
        var invalidRunField = state.CollaborationRuns?
            .Select(GetInvalidCollaborationRunField)
            .FirstOrDefault(field => field is not null);
        if (invalidRunField is not null)
            throw new InvalidDataException(
                $"A collaboration run failed persisted validation for {invalidRunField}.");

        if (state.Agents is null || state.Conversations is null || state.Teams is null ||
            state.DevelopmentPlans is null ||
            state.CollaborationRuns is null ||
            state.Agents.Count > CustomAgentLimits.MaxAgents ||
            state.Teams.Count > CustomAgentLimits.MaxTeams ||
            state.CollaborationRuns.Count > CustomAgentLimits.MaxCollaborationRuns ||
            state.Agents.Any(agent => agent is null || agent.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(agent.Name) || agent.Name.Length > 80 ||
                agent.Description is null || agent.Description.Length > 1000 ||
                string.IsNullOrWhiteSpace(agent.SystemPrompt) || agent.SystemPrompt.Length > 8000 ||
                string.IsNullOrWhiteSpace(agent.ModelName) || agent.ModelName.Length > 200 ||
                !CustomAgentModelPolicy.ValidFallbacks(agent.FallbackModels) ||
                !CustomAgentService.ValidGeneration(agent.Generation)) ||
            state.Agents.Select(agent => agent.Id).Distinct().Count() != state.Agents.Count ||
            state.Conversations.Any(conversation => conversation is null || conversation.Id == Guid.Empty ||
                !state.Agents.Any(agent => agent.Id == conversation.AgentId) ||
                string.IsNullOrWhiteSpace(conversation.Title) || conversation.Title.Length > 80 ||
                !ValidWorkspace(conversation.Workspace) ||
                conversation.Messages is null ||
                conversation.Messages.Count > CustomAgentLimits.MaxMessagesPerConversation ||
                conversation.Messages.Any(message => message is null ||
                    message.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(message.Content) ||
                    message.Content.Length > CustomAgentLimits.MaxStoredMessageCharacters ||
                    message.DevelopmentPlanId == Guid.Empty ||
                    message.DevelopmentTaskIds is { Count: > CustomAgentLimits.MaxDevelopmentPlanTasks } ||
                    message.DevelopmentTaskIds?.Any(id => id is not { Length: > 0 and <= 40 }) == true ||
                    message.Proposal is not null && !ValidProposal(message.Proposal))) ||
            state.Conversations.Select(item => item.Id).Distinct().Count() != state.Conversations.Count ||
            state.Conversations.GroupBy(item => item.AgentId)
                .Any(group => group.Count() > CustomAgentLimits.MaxConversationsPerAgent) ||
            state.Teams.Any(team => team is null || team.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(team.Name) || team.Name.Length > 80 ||
                team.Members is not { Count: 3 } ||
                team.Members.Any(member => member is null ||
                    !CustomAgentModelPolicy.ValidFallbacks(member.FallbackModels) ||
                    !state.Agents.Any(agent => agent.Id == member.AgentId) ||
                    member.Order is < 0 or > 2) ||
                team.Members.Select(member => member.AgentId).Distinct().Count() != 3 ||
                team.Members.OrderBy(member => member.Order).Select(member => member.Role)
                    .SequenceEqual(["Planner", "Developer", "Reviewer"]) is false) ||
            state.Teams.Select(team => team.Id).Distinct().Count() != state.Teams.Count ||
            state.DevelopmentPlans.Count > CustomAgentLimits.MaxDevelopmentPlans ||
            state.DevelopmentPlans.Any(plan => !ValidDevelopmentPlan(plan)) ||
            state.DevelopmentPlans.Select(plan => plan.Id).Distinct().Count() != state.DevelopmentPlans.Count ||
            state.CollaborationRuns.Any(run => run is null || run.Id == Guid.Empty ||
                run.TeamId == Guid.Empty || string.IsNullOrWhiteSpace(run.Task) ||
                run.Task.Length > CustomAgentLimits.MaxUserMessageCharacters ||
                run.Status is not ("Queued" or "Running" or "AwaitingApproval" or "Completed" or "Applied" or
                    "Rejected" or "Failed" or "Cancelled" or "Restored") ||
                run.CurrentStep is not null &&
                    run.CurrentStep is not ("Planner" or "Developer" or "StaticValidation" or "Reviewer" or
                        "DeveloperCorrection" or "ApplyingChanges" or "LocalExecution" or "BrowserTesting" or "Restoring") ||
                run.Mode is not ("Supervised" or "Autonomous") ||
                !ValidModelAttempts(run.ModelAttempts) ||
                !ValidWorkspace(run.Workspace) || run.Contributions is null ||
                run.Contributions.Count > 20 ||
                run.Contributions.Any(item => item is null || item.AgentId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(item.AgentName) || item.AgentName.Length > 80 ||
                    item.Role is not ("Planner" or "Developer" or "Reviewer") ||
                    string.IsNullOrWhiteSpace(item.ModelName) || item.ModelName.Length > 200 ||
                    string.IsNullOrWhiteSpace(item.Content) ||
                    item.Content.Length > CustomAgentLimits.MaxStoredMessageCharacters) ||
                run.Proposal is not null && !ValidProposal(run.Proposal) ||
                run.Validation is not null && !ValidValidation(run.Validation) ||
                run.AppliedChanges is { Count: > 8 } ||
                run.AppliedChanges?.Any(change => change is null ||
                    change.Path is not { Length: > 0 and <= 512 } || change.Path.Any(char.IsControl) ||
                    change.Summary is not { Length: > 0 and <= 500 } ||
                    change.Operation is not ("create" or "modify" or "delete") ||
                    change.Before is { Length: > 262144 } || change.After is { Length: > 262144 }) == true ||
                run.BrowserScenarios is { Count: > 3 } ||
                run.BrowserScenarios?.Any(scenario => !IsValidBrowserScenario(scenario)) == true ||
                run.BrowserTest is not null && !ValidBrowserTestReport(run.BrowserTest) ||
                run.BrowserTestHistory is { Count: > 10 } ||
                run.BrowserTestHistory?.Any(report => !ValidBrowserTestReport(report)) == true ||
                run.ExecutionResults is { Count: > 20 } ||
                run.ExecutionResults?.Any(item => item is null ||
                    item.Command is not { Length: > 0 and <= 200 } || item.Output is null ||
                    item.Output.Length > 12000 || item.StartedAt > item.CompletedAt) == true ||
                run.Error is { Length: > 2000 }) ||
            state.CollaborationRuns.Select(item => item.Id).Distinct().Count() !=
                state.CollaborationRuns.Count)
            throw new InvalidDataException("Custom agent storage contains invalid or inconsistent data.");
    }

    private static bool ValidDevelopmentPlan(CustomAgentDevelopmentPlan? plan)
    {
        if (plan is null || plan.Id == Guid.Empty || plan.TeamId == Guid.Empty ||
            plan.Goal is not { Length: > 0 and <= CustomAgentLimits.MaxUserMessageCharacters } ||
            plan.Status is not ("Ready" or "Running" or "Pausing" or "Paused" or "Cancelling" or
                "Cancelled" or "Completed" or "AwaitingValidation" or "Failed") ||
            !ValidWorkspace(plan.Workspace) || plan.Workspace?.Kind != CustomAgentWorkspaceKind.Studio ||
            plan.Workspace.WorkspaceId is null ||
            plan.Tasks is not { Count: > 0 and <= CustomAgentLimits.MaxDevelopmentPlanTasks } ||
            plan.Attempts < 0 || plan.MaxAttempts is < 1 or > 200 || plan.Attempts > plan.MaxAttempts ||
            plan.Memory is null ||
            (plan.ConversationId is null) != (plan.ConversationAgentId is null) ||
            plan.ConversationId == Guid.Empty || plan.ConversationAgentId == Guid.Empty ||
            plan.Memory.Architecture is not { Length: <= 500 } ||
            !ValidPlanTextList(plan.Memory.CompletedFeatures, 20, 200) ||
            !ValidPlanTextList(plan.Memory.Decisions, 20, 300) ||
            !ValidPlanTextList(plan.Memory.KeyFiles, 80, 512) ||
            !ValidPlanTextList(plan.Memory.KnownIssues, 20, 1200) ||
            plan.Error is { Length: > 2000 } ||
            plan.CurrentTaskId is not null && !plan.Tasks.Any(task => task.Id == plan.CurrentTaskId))
            return false;

        var ids = plan.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != plan.Tasks.Count || plan.Tasks.Select(task => task.Order).Distinct().Count() != plan.Tasks.Count ||
            plan.Tasks.Any(task => task.Id is not { Length: > 0 and <= 40 } ||
                !ValidModelAttempts(task.ModelAttempts) ||
                task.Id.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')) ||
                task.Title is not { Length: > 0 and <= 120 } ||
                task.Description is not { Length: > 0 and <= 800 } ||
                task.Dependencies is not { Count: <= 20 } ||
                task.Dependencies.Distinct(StringComparer.Ordinal).Count() != task.Dependencies.Count ||
                task.Dependencies.Any(dependency => !ids.Contains(dependency) || dependency == task.Id) ||
                task.AcceptanceCriteria is not { Count: > 0 and <= 5 } ||
                !ValidPlanTextList(task.AcceptanceCriteria, 5, 240) ||
                task.CriterionReviews is not null &&
                    (task.CriterionReviews.Count != task.AcceptanceCriteria.Count ||
                     task.CriterionReviews.Any(review => review is null ||
                         review.Category is not ("Unclassified" or "User" or "Technical" or "Optional") ||
                         review.Status is not ("Unverified" or "Verified" or "Failed") ||
                         review.Evidence is { Length: > 1200 } ||
                         review.WorkspaceFingerprint is not null &&
                             !Regex.IsMatch(review.WorkspaceFingerprint, "^[0-9A-F]{64}$", RegexOptions.CultureInvariant) ||
                         review.Status is "Verified" or "Failed" &&
                             (string.IsNullOrWhiteSpace(review.Evidence) || review.ReviewedAt is null ||
                              review.WorkspaceFingerprint is null))) ||
                task.CriterionReviewHistory is { Count: > 100 } ||
                task.CriterionReviewHistory?.Any(item => item is null ||
                    item.CriterionIndex < 0 || item.CriterionIndex >= task.AcceptanceCriteria.Count ||
                    item.Review is null ||
                    item.Review.Category is not ("Unclassified" or "User" or "Technical" or "Optional") ||
                    item.Review.Status is not ("Unverified" or "Verified" or "Failed") ||
                    item.Review.Evidence is { Length: > 1200 } ||
                    item.Review.WorkspaceFingerprint is not null &&
                        !Regex.IsMatch(item.Review.WorkspaceFingerprint, "^[0-9A-F]{64}$", RegexOptions.CultureInvariant)) == true ||
                task.Status is not ("Pending" or "Running" or "Completed" or "Failed" or "Blocked" or "Cancelled") ||
                task.Order < 0 || task.Order >= plan.Tasks.Count ||
                task.Attempts < 0 || task.Attempts > plan.MaxAttempts ||
                task.Summary is { Length: > 1200 } || task.Error is { Length: > 2000 } ||
                task.ChangedFiles is { Count: > 40 } ||
                task.ChangedFiles?.Any(path => path is not { Length: > 0 and <= 512 } || path.Any(char.IsControl)) == true ||
                task.Validation is not null && !ValidValidation(task.Validation)))
            return false;

        return HasAcyclicDependencies(plan.Tasks);
    }

    private static bool ValidPlanTextList(IReadOnlyList<string>? values, int maxCount, int maxLength) =>
        values is { Count: <= 80 } && values.Count <= maxCount &&
        values.All(value => value is { Length: > 0 } && value.Length <= maxLength &&
            !value.Any(char.IsControl));

    private static bool HasAcyclicDependencies(IReadOnlyList<CustomAgentDevelopmentTask> tasks)
    {
        var indegree = tasks.ToDictionary(task => task.Id, task => task.Dependencies.Count, StringComparer.Ordinal);
        var dependents = tasks.ToDictionary(task => task.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var task in tasks)
            foreach (var dependency in task.Dependencies)
                dependents[dependency].Add(task.Id);
        var ready = new Queue<string>(indegree.Where(item => item.Value == 0).Select(item => item.Key));
        var visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var dependent in dependents[id])
                if (--indegree[dependent] == 0)
                    ready.Enqueue(dependent);
        }
        return visited == tasks.Count;
    }

    private static string? GetInvalidCollaborationRunField(CustomAgentCollaborationRun? run)
    {
        if (run is null) return "run";
        if (run.Id == Guid.Empty || run.TeamId == Guid.Empty ||
            string.IsNullOrWhiteSpace(run.Task) || run.Task.Length > CustomAgentLimits.MaxUserMessageCharacters)
            return "identity or task";
        if (run.Status is not ("Queued" or "Running" or "AwaitingApproval" or "Completed" or "Applied" or
                "Rejected" or "Failed" or "Cancelled" or "Restored"))
            return "status";
        if (run.CurrentStep is not null &&
            run.CurrentStep is not ("Planner" or "Developer" or "StaticValidation" or "Reviewer" or
                "DeveloperCorrection" or "ApplyingChanges" or "LocalExecution" or "BrowserTesting" or "Restoring"))
            return "currentStep";
        if (run.Mode is not ("Supervised" or "Autonomous") || !ValidWorkspace(run.Workspace))
            return "mode or workspace";
        if (run.Contributions is null || run.Contributions.Count > 20 ||
            run.Contributions.Any(item => item is null || item.AgentId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.AgentName) || item.AgentName.Length > 80 ||
                item.Role is not ("Planner" or "Developer" or "Reviewer") ||
                string.IsNullOrWhiteSpace(item.ModelName) || item.ModelName.Length > 200 ||
                string.IsNullOrWhiteSpace(item.Content) ||
                item.Content.Length > CustomAgentLimits.MaxStoredMessageCharacters))
            return "contributions";
        if (run.Proposal is not null && !ValidProposal(run.Proposal))
            return "proposal";
        if (run.Validation is not null && GetInvalidValidationField(run.Validation) is { } validationField)
            return validationField;
        if (run.AppliedChanges is { Count: > 8 } ||
            run.AppliedChanges?.Any(change => change is null ||
                change.Path is not { Length: > 0 and <= 512 } || change.Path.Any(char.IsControl) ||
                change.Summary is not { Length: > 0 and <= 500 } ||
                change.Operation is not ("create" or "modify" or "delete") ||
                change.Before is { Length: > 262144 } || change.After is { Length: > 262144 }) == true)
            return "appliedChanges";
        if (run.ExecutionResults is { Count: > 20 } ||
            run.ExecutionResults?.Any(item => item is null ||
                item.Command is not { Length: > 0 and <= 200 } || item.Output is null ||
                item.Output.Length > 12000 || item.StartedAt > item.CompletedAt) == true)
            return "executionResults";
        if (run.BrowserScenarios is { Count: > 3 } ||
            run.BrowserScenarios?.Any(scenario => !IsValidBrowserScenario(scenario)) == true)
            return "browserScenarios";
        if (run.BrowserTest is not null && !ValidBrowserTestReport(run.BrowserTest) ||
            run.BrowserTestHistory is { Count: > 10 } ||
            run.BrowserTestHistory?.Any(report => !ValidBrowserTestReport(report)) == true)
            return "browserTests";
        return run.Error is { Length: > 2000 } ? "error" : null;
    }

    internal static bool IsValidBrowserScenario(CustomAgentBrowserScenario? scenario) =>
        scenario is not null && Guid.TryParseExact(scenario.Id, "N", out _) &&
        scenario.Name is { Length: > 0 and <= 80 } && !scenario.Name.Any(char.IsControl) &&
        scenario.Path is { Length: > 0 and <= 256 } && scenario.Path.StartsWith('/') &&
        !scenario.Path.StartsWith("//", StringComparison.Ordinal) &&
        !scenario.Path.Contains('\\') && !scenario.Path.Contains('#') &&
        !scenario.Path.Contains('?') && !scenario.Path.Contains(':') &&
        scenario.Steps is { Count: <= 10 } && scenario.Steps.All(step =>
            step is not null && step.Action is "fill" or "select" or "click" or "expectVisible" or "expectHidden" or "reload" &&
            (step.Action == "reload" ||
             step.LocatorType is "role" or "label" or "placeholder" or "text" &&
             step.Locator is { Length: > 0 and <= 120 } && !step.Locator.Any(char.IsControl) &&
             step.Index is null or >= 0 and <= 4 &&
             (step.LocatorType != "role" ||
              step.Role is "button" or "checkbox" or "heading" or "link" or "listitem" or "option" or "radio" or "textbox") &&
             (step.Action is not ("fill" or "select") ||
              step.Value is { Length: > 0 and <= 200 } && !step.Value.Any(char.IsControl))));

    private static bool ValidBrowserTestReport(CustomAgentBrowserTestReport? report) =>
        report is not null && report.Id != Guid.Empty &&
        report.Status is "Passed" or "Failed" or "InvalidScenario" or "BrowserUnavailable" or
            "ServerUnavailable" or "TimedOut" or "Unsupported" or "Cancelled" &&
        report.Url is { Length: <= 512 } &&
        (report.Url.Length == 0 || Uri.TryCreate(report.Url, UriKind.Absolute, out var url) &&
            url.IsLoopback && url.Scheme == Uri.UriSchemeHttp && url.UserInfo.Length == 0 &&
            url.Query.Length == 0 && url.Fragment.Length == 0) &&
        report.Passed is >= 0 and <= 3 && report.Failed is >= 0 and <= 3 &&
        report.Error is null or { Length: <= 1200 } &&
        report.Scenarios is { Count: <= 3 } && report.Scenarios.All(scenario =>
            scenario is not null && Guid.TryParseExact(scenario.Id, "N", out _) &&
            scenario.Name is { Length: > 0 and <= 80 } &&
            scenario.Status is "Passed" or "Failed" &&
            scenario.FailedStep is null or { Length: <= 100 } &&
            scenario.Error is null or { Length: <= 1200 } &&
            scenario.JavaScriptErrors is { Count: <= 5 } &&
            scenario.JavaScriptErrors.All(error => error is { Length: <= 400 }) &&
            (scenario.ScreenshotId is null || Guid.TryParseExact(scenario.ScreenshotId, "N", out _)));

    private static bool ValidWorkspace(CustomAgentWorkspaceSelection? workspace) =>
        workspace is null ||
        workspace.Name is { Length: > 0 and <= 120 } && !workspace.Name.Any(char.IsControl) &&
        (workspace.Kind == CustomAgentWorkspaceKind.Studio && workspace.WorkspaceId is not null ||
         workspace.Kind == CustomAgentWorkspaceKind.External && workspace.WorkspaceId is null &&
         !Path.IsPathRooted(workspace.Name) && !workspace.Name.Contains('/') &&
         !workspace.Name.Contains('\\') && !workspace.Name.Contains(':') &&
         workspace.Name is not ("." or ".."));

    private static bool ValidProposal(CustomAgentChangeProposal proposal) =>
        proposal.Id != Guid.Empty && proposal.Status is "Pending" or "Applied" or "Rejected" &&
        ValidWorkspace(proposal.Workspace) && proposal.Workspace is not null &&
        (!proposal.CanApply || proposal.Workspace.Kind == CustomAgentWorkspaceKind.Studio) &&
        proposal.Changes is { Count: > 0 and <= 8 } &&
        proposal.Changes.All(change => change is not null &&
            change.Path is { Length: > 0 and <= 512 } && !change.Path.Any(char.IsControl) &&
            change.Summary is { Length: > 0 and <= 500 } && !change.Summary.Any(char.IsControl) &&
            change.Operation is "create" or "modify" or "delete" &&
            (change.Before is null || change.Before.Length <= 262144 && !change.Before.Contains('\0')) &&
            (change.After is null || change.After.Length is > 0 and <= 262144 && !change.After.Contains('\0')) &&
            (change.Operation switch
            {
                "create" => change.Before is null && change.After is not null,
                "modify" => change.Before is not null && change.After is not null,
                "delete" => change.Before is not null && change.After is null,
                _ => false
            } || change.Operation == "modify" && change.Before is null && change.After is not null)) &&
        proposal.Changes.Select(change => change.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
            proposal.Changes.Count;

    private static bool ValidModelAttempts(IReadOnlyList<CustomAgentModelAttempt>? attempts) =>
        attempts is null || attempts.Count <= 2048 &&
        attempts.All(attempt => attempt is not null) &&
        attempts.Select(attempt => attempt.Id).Distinct().Count() == attempts.Count &&
        attempts.All(attempt => attempt.Id != Guid.Empty && attempt.RunId != Guid.Empty &&
            attempt.AgentId != Guid.Empty && attempt.Role is "Planner" or "Developer" or "Reviewer" &&
            attempt.ModelName is { Length: > 0 and <= 200 } &&
            attempt.SelectionReason is { Length: > 0 and <= 2000 } &&
            attempt.Outcome is "Running" or "Generated" or "Succeeded" or "Failed" &&
            attempt.FailureKind is null or "Generation" or "Environment" or "Infrastructure" or "Context" or
                "Memory" or "Timeout" or "Cancelled" or "GenerationBudgetExhausted" &&
            attempt.Decision is not { Length: > 2000 } &&
            attempt.Candidates is { Count: <= 9 } &&
            attempt.Candidates.All(model => model is { Length: > 0 and <= 200 }) &&
            (attempt.NextModel is null || attempt.Candidates.Contains(attempt.NextModel)) &&
            attempt.CompilationStatus is "Unavailable" or "Passed" or "Failed" or "TimedOut" &&
            (attempt.Diagnostics is null || ValidDiagnostics(attempt.Diagnostics)) &&
            attempt.ExecutionResults is not { Count: > 20 } &&
            (attempt.ExecutionResults is null || attempt.ExecutionResults.All(result => result is not null &&
                result.Command is { Length: > 0 and <= 200 } && result.Output is { Length: <= 12000 } &&
                result.StartedAt <= result.CompletedAt)));

    private static bool ValidValidation(CustomAgentValidationReport validation) =>
        validation.StaticStatus is "Passed" or "Warnings" or "Errors" or "Unavailable" &&
        validation.CompilationStatus is "Unavailable" or "Passed" or "Failed" or "TimedOut" &&
        validation.CorrectionAttempts is >= 0 and <= CustomAgentLimits.MaxAutonomousCorrectionAttempts &&
        ValidDiagnostics(validation.Diagnostics) &&
        validation.History is { Count: > 0 and <= CustomAgentLimits.MaxAutonomousCorrectionAttempts + 1 } &&
        validation.History.All(attempt => attempt is not null &&
            attempt.Number is >= 0 and <= CustomAgentLimits.MaxAutonomousCorrectionAttempts &&
            attempt.Status is "Passed" or "Warnings" or "Errors" or "Unavailable" &&
            ValidDiagnostics(attempt.Diagnostics)) &&
        validation.History.Select(attempt => attempt.Number).Distinct().Count() == validation.History.Count;

    private static string? GetInvalidValidationField(CustomAgentValidationReport validation)
    {
        if (validation.StaticStatus is not ("Passed" or "Warnings" or "Errors" or "Unavailable"))
            return "validation.staticStatus";
        if (validation.CompilationStatus is not ("Unavailable" or "Passed" or "Failed" or "TimedOut"))
            return "validation.compilationStatus";
        if (validation.CorrectionAttempts is < 0 or > CustomAgentLimits.MaxAutonomousCorrectionAttempts)
            return "validation.correctionAttempts";
        if (validation.Diagnostics is null || validation.Diagnostics.Count > CustomAgentLimits.MaxValidationDiagnostics)
            return "validation.diagnostics";
        for (var index = 0; index < validation.Diagnostics.Count; index++)
        {
            var diagnostic = validation.Diagnostics[index];
            if (diagnostic is null) return $"validation.diagnostics[{index}]";
            if (diagnostic.Path is not { Length: > 0 and <= 512 } || diagnostic.Path.Any(char.IsControl))
                return $"validation.diagnostics[{index}].path";
            if (diagnostic.Code is not { Length: > 0 and <= 80 } || diagnostic.Code.Any(char.IsControl))
                return $"validation.diagnostics[{index}].code";
            if (diagnostic.Message is not { Length: > 0 and <= 1200 } || diagnostic.Message.Any(char.IsControl))
                return $"validation.diagnostics[{index}].message";
            if (diagnostic.Severity is not ("Error" or "Warning"))
                return $"validation.diagnostics[{index}].severity";
            if (diagnostic.RelatedPath is not null &&
                (diagnostic.RelatedPath is not { Length: > 0 and <= 512 } ||
                 diagnostic.RelatedPath.Any(char.IsControl)))
                return $"validation.diagnostics[{index}].relatedPath";
        }
        if (validation.History is not { Count: > 0 and <= CustomAgentLimits.MaxAutonomousCorrectionAttempts + 1 })
            return "validation.history";
        for (var index = 0; index < validation.History.Count; index++)
        {
            var attempt = validation.History[index];
            if (attempt is null) return $"validation.history[{index}]";
            if (attempt.Number is < 0 or > CustomAgentLimits.MaxAutonomousCorrectionAttempts)
                return $"validation.history[{index}].number";
            if (attempt.Status is not ("Passed" or "Warnings" or "Errors" or "Unavailable"))
                return $"validation.history[{index}].status";
            if (!ValidDiagnostics(attempt.Diagnostics))
                return $"validation.history[{index}].diagnostics";
        }
        return validation.History.Select(attempt => attempt.Number).Distinct().Count() ==
            validation.History.Count ? null : "validation.history.number";
    }

    private static bool ValidDiagnostics(IReadOnlyList<CustomAgentValidationDiagnostic>? diagnostics) =>
        diagnostics is { Count: <= CustomAgentLimits.MaxValidationDiagnostics } &&
        diagnostics.All(diagnostic => diagnostic is not null &&
            diagnostic.Path is { Length: > 0 and <= 512 } && !diagnostic.Path.Any(char.IsControl) &&
            diagnostic.Code is { Length: > 0 and <= 80 } && !diagnostic.Code.Any(char.IsControl) &&
            diagnostic.Message is { Length: > 0 and <= 1200 } && !diagnostic.Message.Any(char.IsControl) &&
            diagnostic.Severity is "Error" or "Warning" &&
            (diagnostic.RelatedPath is null ||
             diagnostic.RelatedPath is { Length: > 0 and <= 512 } && !diagnostic.RelatedPath.Any(char.IsControl)));

    private sealed record StoreState(List<CustomAgentProfile> Agents, List<CustomAgentConversation> Conversations)
    {
        public List<CustomAgentTeam> Teams { get; init; } = [];
        public List<CustomAgentCollaborationRun> CollaborationRuns { get; init; } = [];
        public List<CustomAgentDevelopmentPlan> DevelopmentPlans { get; init; } = [];
    }
}
