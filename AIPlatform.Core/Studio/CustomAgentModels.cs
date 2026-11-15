namespace AIPlatform.Core.Studio;

public sealed record CustomAgentDraft(string? Name, string? Description, string? SystemPrompt, string? ModelName);

public sealed record CustomAgentProfile(
    Guid Id, string Name, string Description, string SystemPrompt, string ModelName,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public enum CustomAgentWorkspaceKind { None, Studio, External }

public sealed record CustomAgentWorkspaceSelection(
    CustomAgentWorkspaceKind Kind, Guid? WorkspaceId, string Name, string? AuthorizationId = null);

public sealed record CustomAgentWorkspaceFile(string Path, string Content);

public sealed record CustomAgentFileChange(
    string Path, string Summary, string? Before, string? After, string Operation = "modify");

public sealed record CustomAgentChangeProposal(
    Guid Id, DateTimeOffset CreatedAt, string Status, IReadOnlyList<CustomAgentFileChange> Changes,
    bool CanApply, string? ApplyReason, CustomAgentWorkspaceSelection? Workspace = null,
    DateTimeOffset? ExpectedWorkspaceUpdatedAt = null, string? ExternalBackupId = null,
    DateTimeOffset? ExternalRestoredAt = null);

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

public sealed record CustomAgentTeamMember(Guid AgentId, string Role, int Order);

public sealed record CustomAgentTeamDraft(string? Name, IReadOnlyList<CustomAgentTeamMember>? Members);

public sealed record CustomAgentTeam(
    Guid Id, string Name, IReadOnlyList<CustomAgentTeamMember> Members,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CustomAgentContribution(
    Guid AgentId, string AgentName, string Role, string ModelName, string Content, DateTimeOffset CreatedAt);

public sealed record CustomAgentValidationDiagnostic(
    string Path, string Code, string Message, string Severity = "Error", string? RelatedPath = null);

public sealed record CustomAgentValidationAttempt(
    int Number, string Status, IReadOnlyList<CustomAgentValidationDiagnostic> Diagnostics);

public sealed record CustomAgentValidationReport(
    string StaticStatus, string CompilationStatus, int CorrectionAttempts,
    IReadOnlyList<CustomAgentValidationDiagnostic> Diagnostics,
    IReadOnlyList<CustomAgentValidationAttempt> History);

public sealed record CustomAgentExecutionResult(
    string Command, int? ExitCode, bool TimedOut, string Output,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt);

public sealed record CustomAgentCollaborationRun(
    Guid Id, Guid TeamId, string Task, string Status, string? CurrentStep, CustomAgentWorkspaceSelection? Workspace,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<CustomAgentContribution> Contributions,
    CustomAgentChangeProposal? Proposal = null, string? Error = null,
    CustomAgentValidationReport? Validation = null, string Mode = "Supervised",
    IReadOnlyList<CustomAgentFileChange>? AppliedChanges = null,
    IReadOnlyList<CustomAgentExecutionResult>? ExecutionResults = null);

public static class CustomAgentLimits
{
    public const int MaxAgents = 30;
    public const int MaxConversationsPerAgent = 20;
    public const int MaxMessagesPerConversation = 40;
    public const int MaxTeams = 20;
    public const int MaxCollaborationRuns = 50;
    public const int MaxCorrectionAttempts = 2;
    public const int MaxAutonomousCorrectionAttempts = 2;
    public const int MaxValidationDiagnostics = 40;
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
    IReadOnlyList<CustomAgentTeam> ListTeams();
    CustomAgentTeam? GetTeam(Guid id);
    void SaveTeam(CustomAgentTeam team);
    bool DeleteTeam(Guid id);
    IReadOnlyList<CustomAgentCollaborationRun> ListCollaborationRuns(Guid teamId);
    CustomAgentCollaborationRun? GetCollaborationRun(Guid teamId, Guid runId);
    void AddCollaborationRun(CustomAgentCollaborationRun run);
    bool UpdateCollaborationRun(CustomAgentCollaborationRun run);
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
    Task<CustomAgentConversation?> RecordExternalProposalOutcomeAsync(
        Guid agentId, Guid conversationId, Guid proposalId, string? backupId, bool restored,
        CancellationToken cancellationToken);
    bool DeleteConversation(Guid agentId, Guid conversationId);
    IReadOnlyList<CustomAgentTeam> ListTeams();
    CustomAgentTeam? SaveTeam(Guid? id, CustomAgentTeamDraft? draft);
    bool DeleteTeam(Guid id);
    IReadOnlyList<CustomAgentCollaborationRun> ListCollaborationRuns(Guid teamId);
    CustomAgentCollaborationRun? GetCollaborationRun(Guid teamId, Guid runId);
    CustomAgentCollaborationRun? StartCollaboration(
        Guid teamId, string? task, CustomAgentWorkspaceSelection? workspace,
        IReadOnlyList<CustomAgentWorkspaceFile>? externalFiles, string? mode = null,
        bool workspaceAuthorized = false, bool executionAuthorized = false);
    Task<CustomAgentCollaborationRun?> RestoreAutonomousChangesAsync(
        Guid teamId, Guid runId, CancellationToken cancellationToken);
    bool CancelCollaboration(Guid teamId, Guid runId);
    Task<CustomAgentCollaborationRun?> DecideCollaborationProposalAsync(
        Guid teamId, Guid runId, bool approved, CancellationToken cancellationToken);
    Task<CustomAgentCollaborationRun?> RecordExternalCollaborationOutcomeAsync(
        Guid teamId, Guid runId, string? backupId, bool restored, CancellationToken cancellationToken);
}

public sealed class CustomAgentModelUnavailableException()
    : Exception("The model selected for this agent is no longer installed in Ollama. Install it locally or edit the agent to choose an installed model.");
