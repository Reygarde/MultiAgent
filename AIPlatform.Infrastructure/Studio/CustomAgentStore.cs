using System.Text;
using System.Text.Json;
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
            state.CollaborationRuns is null ||
            state.Agents.Count > CustomAgentLimits.MaxAgents ||
            state.Teams.Count > CustomAgentLimits.MaxTeams ||
            state.CollaborationRuns.Count > CustomAgentLimits.MaxCollaborationRuns ||
            state.Agents.Any(agent => agent is null || agent.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(agent.Name) || agent.Name.Length > 80 ||
                agent.Description is null || agent.Description.Length > 1000 ||
                string.IsNullOrWhiteSpace(agent.SystemPrompt) || agent.SystemPrompt.Length > 8000 ||
                string.IsNullOrWhiteSpace(agent.ModelName) || agent.ModelName.Length > 200) ||
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
                    message.Proposal is not null && !ValidProposal(message.Proposal))) ||
            state.Conversations.Select(item => item.Id).Distinct().Count() != state.Conversations.Count ||
            state.Conversations.GroupBy(item => item.AgentId)
                .Any(group => group.Count() > CustomAgentLimits.MaxConversationsPerAgent) ||
            state.Teams.Any(team => team is null || team.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(team.Name) || team.Name.Length > 80 ||
                team.Members is not { Count: 3 } ||
                team.Members.Any(member => member is null ||
                    !state.Agents.Any(agent => agent.Id == member.AgentId) ||
                    member.Order is < 0 or > 2) ||
                team.Members.Select(member => member.AgentId).Distinct().Count() != 3 ||
                team.Members.OrderBy(member => member.Order).Select(member => member.Role)
                    .SequenceEqual(["Planner", "Developer", "Reviewer"]) is false) ||
            state.Teams.Select(team => team.Id).Distinct().Count() != state.Teams.Count ||
            state.CollaborationRuns.Any(run => run is null || run.Id == Guid.Empty ||
                run.TeamId == Guid.Empty || string.IsNullOrWhiteSpace(run.Task) ||
                run.Task.Length > CustomAgentLimits.MaxUserMessageCharacters ||
                run.Status is not ("Queued" or "Running" or "AwaitingApproval" or "Completed" or "Applied" or
                    "Rejected" or "Failed" or "Cancelled" or "Restored") ||
                run.CurrentStep is not null &&
                    run.CurrentStep is not ("Planner" or "Developer" or "StaticValidation" or "Reviewer" or
                        "DeveloperCorrection" or "ApplyingChanges" or "LocalExecution" or "Restoring") ||
                run.Mode is not ("Supervised" or "Autonomous") ||
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
                run.ExecutionResults is { Count: > 20 } ||
                run.ExecutionResults?.Any(item => item is null ||
                    item.Command is not { Length: > 0 and <= 200 } || item.Output is null ||
                    item.Output.Length > 12000 || item.StartedAt > item.CompletedAt) == true ||
                run.Error is { Length: > 2000 }) ||
            state.CollaborationRuns.Select(item => item.Id).Distinct().Count() !=
                state.CollaborationRuns.Count)
            throw new InvalidDataException("Custom agent storage contains invalid or inconsistent data.");
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
                "DeveloperCorrection" or "ApplyingChanges" or "LocalExecution" or "Restoring"))
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
        return run.Error is { Length: > 2000 } ? "error" : null;
    }

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
    }
}
