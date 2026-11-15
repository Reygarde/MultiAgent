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
            Write(state with { Conversations = state.Conversations.Where(item => item.AgentId != id).ToList() });
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
        if (state.Agents is null || state.Conversations is null ||
            state.Agents.Count > CustomAgentLimits.MaxAgents ||
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
                .Any(group => group.Count() > CustomAgentLimits.MaxConversationsPerAgent))
            throw new InvalidDataException("Custom agent storage contains invalid or inconsistent data.");
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
            change.After is { Length: > 0 and <= 262144 } && !change.After.Contains('\0') &&
            (change.Before is null || change.Before.Length <= 262144 && !change.Before.Contains('\0'))) &&
        proposal.Changes.Select(change => change.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
            proposal.Changes.Count;

    private sealed record StoreState(List<CustomAgentProfile> Agents, List<CustomAgentConversation> Conversations);
}
