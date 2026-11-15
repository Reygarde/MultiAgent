using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

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
    CustomAgentWorkspaceKind Kind, Guid? WorkspaceId, string Name, string? AuthorizationId)
{
    public static CustomAgentWorkspaceResponse? From(CustomAgentWorkspaceSelection? workspace) =>
        workspace is null ? null : new(workspace.Kind, workspace.WorkspaceId, workspace.Name, workspace.AuthorizationId);
}

/// <summary>A proposed text-file creation, replacement, or deletion requiring explicit approval.</summary>
public sealed record CustomAgentFileChangeResponse(
    string Path, string Summary, string? Before, string? After, string Operation);

/// <summary>Approval status and diff for a custom-agent proposal.</summary>
public sealed record CustomAgentProposalResponse(
    Guid Id, DateTimeOffset CreatedAt, string Status, IReadOnlyList<CustomAgentFileChangeResponse> Changes,
    bool CanApply, string? ApplyReason, CustomAgentWorkspaceResponse? Workspace,
    string? ExternalBackupId, DateTimeOffset? ExternalRestoredAt)
{
    public static CustomAgentProposalResponse From(CustomAgentChangeProposal proposal) => new(
        proposal.Id, proposal.CreatedAt, proposal.Status,
        proposal.Changes.Select(change => new CustomAgentFileChangeResponse(
            change.Path, change.Summary, change.Before, change.After,
            change.Operation == "modify" && change.Before is null ? "create" : change.Operation)).ToArray(),
        proposal.CanApply, proposal.ApplyReason, CustomAgentWorkspaceResponse.From(proposal.Workspace),
        proposal.ExternalBackupId, proposal.ExternalRestoredAt);
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

/// <summary>Persists the result of a successful native external-folder apply or restore.</summary>
public sealed record RecordExternalProposalOutcomeRequest(string? BackupId, bool Restored);

/// <summary>One selected user-created agent and its fixed workflow role.</summary>
public sealed record CustomAgentTeamMemberResponse(Guid AgentId, string Role, int Order);

/// <summary>A saved Planner, Developer, and Reviewer group.</summary>
public sealed record CustomAgentTeamResponse(
    Guid Id, string Name, IReadOnlyList<CustomAgentTeamMemberResponse> Members,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static CustomAgentTeamResponse From(CustomAgentTeam team) => new(
        team.Id, team.Name, team.Members.Select(member =>
            new CustomAgentTeamMemberResponse(member.AgentId, member.Role, member.Order)).ToArray(),
        team.CreatedAt, team.UpdatedAt);
}

/// <summary>Payload for creating or updating a custom-agent team.</summary>
public sealed record CustomAgentTeamRequest(
    string? Name, IReadOnlyList<CustomAgentTeamMember>? Members);

/// <summary>Creates a named React/TypeScript or .NET Studio project for a saved custom-agent team.</summary>
public sealed record CreateCustomAgentProjectRequest(string? Name, string? ProjectType);

/// <summary>One-time confirmation that the selected trusted Studio project may execute locally.</summary>
public sealed record StartLocalApplicationRequest(bool ExecutionAuthorized);

/// <summary>In-memory status of a local development server owned by the Desktop backend.</summary>
public sealed record LocalApplicationResponse(
    Guid WorkspaceId, string Status, string? Url, string? Command,
    IReadOnlyList<string> Output, string? Error, int? ProcessId, DateTimeOffset UpdatedAt)
{
    public static LocalApplicationResponse From(LocalApplicationSnapshot application) => new(
        application.WorkspaceId, application.Status, application.Url, application.Command,
        application.Output, application.Error, application.ProcessId, application.UpdatedAt);
}

/// <summary>One persisted contribution from a member of a collaborating team.</summary>
public sealed record CustomAgentContributionResponse(
    Guid AgentId, string AgentName, string Role, string ModelName, string Content, DateTimeOffset CreatedAt);

/// <summary>One verified static issue in the proposed workspace.</summary>
public sealed record CustomAgentValidationDiagnosticResponse(
    string Path, string Code, string Message, string Severity, string? RelatedPath);

/// <summary>Diagnostics and correction history for one static validation pass.</summary>
public sealed record CustomAgentValidationAttemptResponse(
    int Number, string Status, IReadOnlyList<CustomAgentValidationDiagnosticResponse> Diagnostics);

/// <summary>Static validation state; compilation is reported separately and is never inferred.</summary>
public sealed record CustomAgentValidationResponse(
    string StaticStatus, string CompilationStatus, int CorrectionAttempts,
    IReadOnlyList<CustomAgentValidationDiagnosticResponse> Diagnostics,
    IReadOnlyList<CustomAgentValidationAttemptResponse> History)
{
    public static CustomAgentValidationResponse From(CustomAgentValidationReport validation) => new(
        validation.StaticStatus, validation.CompilationStatus, validation.CorrectionAttempts,
        validation.Diagnostics.Select(CustomAgentValidationDiagnosticResponseFrom).ToArray(),
        validation.History.Select(attempt => new CustomAgentValidationAttemptResponse(
            attempt.Number, attempt.Status,
            attempt.Diagnostics.Select(CustomAgentValidationDiagnosticResponseFrom).ToArray())).ToArray());

    private static CustomAgentValidationDiagnosticResponse CustomAgentValidationDiagnosticResponseFrom(
        CustomAgentValidationDiagnostic diagnostic) =>
        new(diagnostic.Path, diagnostic.Code, diagnostic.Message, diagnostic.Severity, diagnostic.RelatedPath);
}

/// <summary>Persisted status, contributions, and single final proposal for a team run.</summary>
public sealed record CustomAgentCollaborationRunResponse(
    Guid Id, Guid TeamId, string Task, string Status, string? CurrentStep, CustomAgentWorkspaceResponse? Workspace,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<CustomAgentContributionResponse> Contributions,
    CustomAgentProposalResponse? Proposal, string? Error, CustomAgentValidationResponse? Validation = null,
    string Mode = "Supervised", IReadOnlyList<CustomAgentFileChange>? AppliedChanges = null,
    IReadOnlyList<CustomAgentExecutionResult>? ExecutionResults = null)
{
    public static CustomAgentCollaborationRunResponse From(CustomAgentCollaborationRun run) => new(
        run.Id, run.TeamId, run.Task, run.Status, run.CurrentStep,
        CustomAgentWorkspaceResponse.From(run.Workspace), run.CreatedAt, run.UpdatedAt,
        run.Contributions.Select(item => new CustomAgentContributionResponse(
            item.AgentId, item.AgentName, item.Role, item.ModelName, item.Content, item.CreatedAt)).ToArray(),
        run.Proposal is null ? null : CustomAgentProposalResponse.From(run.Proposal), run.Error,
        run.Validation is null ? null : CustomAgentValidationResponse.From(run.Validation),
        run.Mode, run.AppliedChanges ?? [], run.ExecutionResults ?? []);
}

/// <summary>A user request and selected shared workspace for one team collaboration.</summary>
public sealed record StartCustomAgentCollaborationRequest(
    string? Task, CustomAgentWorkspaceSelection? Workspace,
    IReadOnlyList<CustomAgentWorkspaceFileRequest>? ExternalFiles = null,
    string? Mode = null, bool WorkspaceAuthorized = false, bool ExecutionAuthorized = false);

/// <summary>Explicit approval or refusal of a completed collaboration proposal.</summary>
public sealed record DecideCustomAgentCollaborationRequest(bool Approved);
