namespace AIPlatform.Core.Studio;

public sealed record CustomAgentDraft(string? Name, string? Description, string? SystemPrompt, string? ModelName);

public sealed record CustomAgentProfile(
    Guid Id, string Name, string Description, string SystemPrompt, string ModelName,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public enum CustomAgentWorkspaceKind { None, Studio, External }

public sealed record CustomAgentWorkspaceSelection(
    CustomAgentWorkspaceKind Kind, Guid? WorkspaceId, string Name);

public sealed record CustomAgentWorkspaceFile(string Path, string Content);

public sealed record CustomAgentFileChange(string Path, string Summary, string? Before, string After);

public sealed record CustomAgentChangeProposal(
    Guid Id, DateTimeOffset CreatedAt, string Status, IReadOnlyList<CustomAgentFileChange> Changes,
    bool CanApply, string? ApplyReason, CustomAgentWorkspaceSelection? Workspace = null,
    DateTimeOffset? ExpectedWorkspaceUpdatedAt = null);

public sealed record CustomAgentMessage(string Role, string Content, DateTimeOffset CreatedAt)
{
    public CustomAgentChangeProposal? Proposal { get; init; }
}

public sealed record CustomAgentConversation(
    Guid Id, Guid AgentId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<CustomAgentMessage> Messages)
{
    public CustomAgentWorkspaceSelection? Workspace { get; init; }
}

public sealed record CustomAgentConversationSummary(
    Guid Id, Guid AgentId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount);

public static class CustomAgentLimits
{
    public const int MaxAgents = 30;
    public const int MaxConversationsPerAgent = 20;
    public const int MaxMessagesPerConversation = 40;
    public const int MaxUserMessageCharacters = 4000;
    public const int MaxStoredMessageCharacters = 8000;
    public const int MaxStoredBytes = 16_777_216;
}

public interface ICustomAgentStore
{
    IReadOnlyList<CustomAgentProfile> ListAgents();
    CustomAgentProfile? GetAgent(Guid id);
    void AddAgent(CustomAgentProfile agent);
    bool UpdateAgent(CustomAgentProfile agent);
    bool DeleteAgent(Guid id);
    IReadOnlyList<CustomAgentConversationSummary> ListConversations(Guid agentId);
    CustomAgentConversation? GetConversation(Guid agentId, Guid conversationId);
    CustomAgentConversation? CreateConversation(CustomAgentConversation conversation);
    bool UpdateConversation(CustomAgentConversation conversation);
    bool DeleteConversation(Guid agentId, Guid conversationId);
}

public interface IOllamaModelCatalog
{
    Task<IReadOnlyList<string>> GetInstalledModelsAsync(CancellationToken cancellationToken);
}

public interface ICustomAgentService
{
    IReadOnlyList<CustomAgentProfile> ListAgents();
    CustomAgentProfile? GetAgent(Guid id);
    Task<IReadOnlyList<string>> ListInstalledModelsAsync(CancellationToken cancellationToken);
    Task<CustomAgentProfile> CreateAgentAsync(CustomAgentDraft draft, CancellationToken cancellationToken);
    Task<CustomAgentProfile?> UpdateAgentAsync(Guid id, CustomAgentDraft draft, CancellationToken cancellationToken);
    CustomAgentProfile? DuplicateAgent(Guid id);
    bool DeleteAgent(Guid id);
    IReadOnlyList<CustomAgentConversationSummary> ListConversations(Guid agentId);
    CustomAgentConversation? CreateConversation(Guid agentId);
    CustomAgentConversation? GetConversation(Guid agentId, Guid conversationId);
    CustomAgentConversation? SetWorkspace(
        Guid agentId, Guid conversationId, CustomAgentWorkspaceSelection? selection);
    Task<CustomAgentConversation?> SendMessageAsync(
        Guid agentId, Guid conversationId, string? message, CancellationToken cancellationToken,
        IReadOnlyList<CustomAgentWorkspaceFile>? externalFiles = null);
    Task<CustomAgentConversation?> DecideProposalAsync(
        Guid agentId, Guid conversationId, Guid proposalId, bool approved, CancellationToken cancellationToken);
    bool DeleteConversation(Guid agentId, Guid conversationId);
}

public sealed class CustomAgentModelUnavailableException()
    : Exception("The model selected for this agent is no longer installed in Ollama. Install it locally or edit the agent to choose an installed model.");
