using AIPlatform.Core.Studio;

namespace AIPlatform.API;

/// <summary>Configuration supplied when creating or updating a user-defined AI agent.</summary>
public sealed record CustomAgentRequest(
    string? Name, string? Description, string? SystemPrompt, string? ModelName);

/// <summary>Persisted user-defined AI agent; separate from workflow agents.</summary>
public sealed record CustomAgentResponse(
    Guid Id, string Name, string Description, string SystemPrompt, string ModelName,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static CustomAgentResponse From(CustomAgentProfile agent) => new(
        agent.Id, agent.Name, agent.Description, agent.SystemPrompt, agent.ModelName,
        agent.CreatedAt, agent.UpdatedAt);
}

/// <summary>Conversation summary that omits message contents until opened.</summary>
public sealed record CustomAgentConversationSummaryResponse(
    Guid Id, Guid AgentId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount)
{
    public static CustomAgentConversationSummaryResponse From(CustomAgentConversationSummary conversation) => new(
        conversation.Id, conversation.AgentId, conversation.Title, conversation.CreatedAt,
        conversation.UpdatedAt, conversation.MessageCount);
}

/// <summary>A bounded, user-selected workspace attached to a conversation.</summary>
public sealed record CustomAgentWorkspaceResponse(
    CustomAgentWorkspaceKind Kind, Guid? WorkspaceId, string Name)
{
    public static CustomAgentWorkspaceResponse? From(CustomAgentWorkspaceSelection? workspace) =>
        workspace is null ? null : new(workspace.Kind, workspace.WorkspaceId, workspace.Name);
}

/// <summary>A proposed text-file change requiring an explicit user decision.</summary>
public sealed record CustomAgentFileChangeResponse(
    string Path, string Summary, string? Before, string After);

/// <summary>Approval status and diff for a custom-agent proposal.</summary>
public sealed record CustomAgentProposalResponse(
    Guid Id, DateTimeOffset CreatedAt, string Status, IReadOnlyList<CustomAgentFileChangeResponse> Changes,
    bool CanApply, string? ApplyReason, CustomAgentWorkspaceResponse? Workspace)
{
    public static CustomAgentProposalResponse From(CustomAgentChangeProposal proposal) => new(
        proposal.Id, proposal.CreatedAt, proposal.Status,
        proposal.Changes.Select(change => new CustomAgentFileChangeResponse(
            change.Path, change.Summary, change.Before, change.After)).ToArray(),
        proposal.CanApply, proposal.ApplyReason, CustomAgentWorkspaceResponse.From(proposal.Workspace));
}

/// <summary>One persisted user or assistant message in a custom-agent conversation.</summary>
public sealed record CustomAgentMessageResponse(
    string Role, string Content, DateTimeOffset CreatedAt, CustomAgentProposalResponse? Proposal);

/// <summary>Conversation history returned after loading or sending a message.</summary>
public sealed record CustomAgentConversationResponse(
    Guid Id, Guid AgentId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<CustomAgentMessageResponse> Messages, CustomAgentWorkspaceResponse? Workspace)
{
    public static CustomAgentConversationResponse From(CustomAgentConversation conversation) => new(
        conversation.Id, conversation.AgentId, conversation.Title, conversation.CreatedAt,
        conversation.UpdatedAt, conversation.Messages.Select(message =>
            new CustomAgentMessageResponse(message.Role, message.Content, message.CreatedAt,
                message.Proposal is null ? null : CustomAgentProposalResponse.From(message.Proposal))).ToArray(),
        CustomAgentWorkspaceResponse.From(conversation.Workspace));
}

/// <summary>A text message to send to a custom agent; no tools or command execution are available.</summary>
public sealed record SendCustomAgentMessageRequest(
    string? Message, IReadOnlyList<CustomAgentWorkspaceFileRequest>? ExternalFiles = null);

/// <summary>One untrusted relative-path text file from a user-selected, read-only folder snapshot.</summary>
public sealed record CustomAgentWorkspaceFileRequest(string Path, string Content);

/// <summary>Changes or clears the workspace attached to a conversation.</summary>
public sealed record SetCustomAgentWorkspaceRequest(CustomAgentWorkspaceSelection? Workspace);

/// <summary>Explicitly applies or refuses a pending workspace change proposal.</summary>
public sealed record DecideCustomAgentProposalRequest(bool Approved);
