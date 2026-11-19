namespace AIPlatform.Core.Studio;

public sealed record CustomAgentDraft(string? Name, string? Description, string? SystemPrompt, string? ModelName,
    IReadOnlyList<string>? FallbackModels = null, bool AutomaticModelSelection = false,
    CustomAgentGenerationOptions? Generation = null);

public sealed record CustomAgentGenerationOptions(int ContextSize = 4096, int MaxOutputTokens = 1024, double Temperature = 0.2);

public sealed record CustomAgentProfile(
    Guid Id, string Name, string Description, string SystemPrompt, string ModelName,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<string>? FallbackModels = null,
    bool AutomaticModelSelection = false, CustomAgentGenerationOptions? Generation = null);

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
    public Guid? DevelopmentPlanId { get; init; }
    public IReadOnlyList<string>? DevelopmentTaskIds { get; init; }
    public ModelRoutingDecision? RoutingDecision { get; init; }
}

public sealed record CustomAgentConversation(
    Guid Id, Guid AgentId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<CustomAgentMessage> Messages)
{
    public CustomAgentWorkspaceSelection? Workspace { get; init; }
}

public sealed record CustomAgentConversationSummary(
    Guid Id, Guid AgentId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount);

public sealed record CustomAgentTeamMember(Guid AgentId, string Role, int Order,
    IReadOnlyList<string>? FallbackModels = null);

public sealed record CustomAgentTeamDraft(string? Name, IReadOnlyList<CustomAgentTeamMember>? Members,
    bool ModelEscalationEnabled = false, bool AutomaticModelSelection = false);

public sealed record CustomAgentTeam(
    Guid Id, string Name, IReadOnlyList<CustomAgentTeamMember> Members,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool ModelEscalationEnabled = false,
    bool AutomaticModelSelection = false);

public sealed record CustomAgentModelAttempt(
    Guid Id, Guid RunId, Guid AgentId, string Role, string ModelName, string SelectionReason,
    DateTimeOffset StartedAt, bool EscalationEnabled, IReadOnlyList<string> Candidates,
    string Outcome = "Running", string? FailureKind = null, string? Decision = null,
    string? NextModel = null, string CompilationStatus = "Unavailable",
    IReadOnlyList<CustomAgentExecutionResult>? ExecutionResults = null,
    IReadOnlyList<CustomAgentValidationDiagnostic>? Diagnostics = null)
{
    public string? TaskCategory { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public ModelRoutingDecision? RoutingDecision { get; init; }
    public string TestsStatus { get; init; } = "Unavailable";
    public string BrowserStatus { get; init; } = "Unavailable";
    public bool RolledBack { get; init; }
}

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

public sealed record CustomAgentBrowserStep(
    string Action, string? LocatorType = null, string? Locator = null, string? Value = null,
    string? Role = null, int? Index = null);

public sealed record CustomAgentBrowserScenario(
    string Id, string Name, string Path, IReadOnlyList<CustomAgentBrowserStep> Steps);

public sealed record CustomAgentBrowserScenarioResult(
    string Id, string Name, string Status, string? FailedStep, string? Error,
    IReadOnlyList<string> JavaScriptErrors, string? ScreenshotId);

public sealed record CustomAgentBrowserTestReport(
    Guid Id, string Status, string Url, DateTimeOffset TestedAt, int Passed, int Failed,
    string? Error, IReadOnlyList<CustomAgentBrowserScenarioResult> Scenarios);

public sealed record CustomAgentCriterionReview(
    string Category, string Status, string? Evidence, DateTimeOffset? ReviewedAt, string? WorkspaceFingerprint,
    Guid? RunId = null, Guid? ReportId = null, string? ScenarioId = null);

public sealed record CustomAgentCriterionReviewEvent(
    int CriterionIndex, CustomAgentCriterionReview Review, DateTimeOffset RecordedAt);

public sealed record CustomAgentTargetedCorrection(
    string Path, string SourceSnippet, string ApiSignature, string HttpDiagnostic, string ExpectedFormat,
    string? CompilationDiagnostic = null, bool CompilationOnly = false,
    Guid? PreviousCandidateRunId = null, string? PreviousCandidateSnippet = null);

public sealed record CustomAgentDevelopmentTask(
    string Id, string Title, string Description, IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> AcceptanceCriteria, string Status, int Order,
    Guid? CollaborationRunId = null, int Attempts = 0, string? Summary = null,
    IReadOnlyList<string>? ChangedFiles = null, string? Error = null,
    CustomAgentValidationReport? Validation = null)
{
    public IReadOnlyList<CustomAgentCriterionReview>? CriterionReviews { get; init; }
    public IReadOnlyList<CustomAgentCriterionReviewEvent>? CriterionReviewHistory { get; init; }
    public CustomAgentTargetedCorrection? TargetedCorrection { get; init; }
    public IReadOnlyList<CustomAgentModelAttempt>? ModelAttempts { get; init; }
}

public sealed record CustomAgentProjectMemory(
    string Architecture, IReadOnlyList<string> CompletedFeatures,
    IReadOnlyList<string> Decisions, IReadOnlyList<string> KeyFiles,
    IReadOnlyList<string> KnownIssues);

public sealed record CustomAgentDevelopmentPlan(
    Guid Id, Guid TeamId, string Goal, string Status, CustomAgentWorkspaceSelection Workspace,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<CustomAgentDevelopmentTask> Tasks,
    string? CurrentTaskId, int Attempts, int MaxAttempts,
    CustomAgentProjectMemory Memory, string? Error = null,
    Guid? ConversationId = null, Guid? ConversationAgentId = null);

public sealed record CustomAgentCollaborationRun(
    Guid Id, Guid TeamId, string Task, string Status, string? CurrentStep, CustomAgentWorkspaceSelection? Workspace,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<CustomAgentContribution> Contributions,
    CustomAgentChangeProposal? Proposal = null, string? Error = null,
    CustomAgentValidationReport? Validation = null, string Mode = "Supervised",
    IReadOnlyList<CustomAgentFileChange>? AppliedChanges = null,
    IReadOnlyList<CustomAgentExecutionResult>? ExecutionResults = null,
    IReadOnlyList<CustomAgentBrowserScenario>? BrowserScenarios = null,
    CustomAgentBrowserTestReport? BrowserTest = null,
    IReadOnlyList<CustomAgentBrowserTestReport>? BrowserTestHistory = null)
{
    public DateTimeOffset? ChangesRolledBackAt { get; init; }
    public IReadOnlyList<CustomAgentModelAttempt>? ModelAttempts { get; init; }
}

public static class CustomAgentLimits
{
    public const int MaxAgents = 30;
    public const int MaxConversationsPerAgent = 20;
    public const int MaxMessagesPerConversation = 40;
    public const int MaxTeams = 20;
    public const int MaxCollaborationRuns = 50;
    public const int MaxDevelopmentPlans = 30;
    public const int MaxDevelopmentPlanTasks = 20;
    public const int MaxInitialDevelopmentPlanTasks = 6;
    public const int MaxCorrectionAttempts = 2;
    public const int MaxAutonomousCorrectionAttempts = 3;
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
    IReadOnlyList<CustomAgentDevelopmentPlan> ListDevelopmentPlans(Guid teamId);
    CustomAgentDevelopmentPlan? GetDevelopmentPlan(Guid teamId, Guid planId);
    void AddDevelopmentPlan(CustomAgentDevelopmentPlan plan);
    bool UpdateDevelopmentPlan(CustomAgentDevelopmentPlan plan);
    void SaveDevelopmentPlanConversation(
        CustomAgentDevelopmentPlan plan, CustomAgentConversation conversation,
        DateTimeOffset? expectedPlanUpdatedAt, DateTimeOffset? expectedConversationUpdatedAt);
    IReadOnlyList<CustomAgentCollaborationRun> ListCollaborationRuns(Guid teamId);
    CustomAgentCollaborationRun? GetCollaborationRun(Guid teamId, Guid runId);
    void AddCollaborationRun(CustomAgentCollaborationRun run);
    bool UpdateCollaborationRun(CustomAgentCollaborationRun run);
}

public interface IOllamaModelCatalog
{
    Task<IReadOnlyList<string>> GetInstalledModelsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetCompatibleInstalledModelsAsync(
        CancellationToken cancellationToken, IReadOnlyList<string>? requestedModels = null) =>
        GetInstalledModelsAsync(cancellationToken);
}

public interface ICustomAgentService
{
    CustomAgentDevelopmentPlan? StartTargetedCorrection(
        Guid teamId, Guid planId, CustomAgentTargetedCorrection correction,
        bool workspaceAuthorized, bool executionAuthorized);
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
    Task<CustomAgentDevelopmentPlan?> CreateDevelopmentPlanAsync(
        Guid teamId, string? goal, CustomAgentWorkspaceSelection? workspace,
        CancellationToken cancellationToken);
    IReadOnlyList<CustomAgentDevelopmentPlan> ListDevelopmentPlans(Guid teamId);
    CustomAgentDevelopmentPlan? GetDevelopmentPlan(Guid teamId, Guid planId);
    Task<CustomAgentDevelopmentPlan?> ExtendDevelopmentPlanAsync(
        Guid teamId, Guid planId, string? request, CancellationToken cancellationToken);
    CustomAgentDevelopmentPlan? StartDevelopmentPlan(
        Guid teamId, Guid planId, bool workspaceAuthorized, bool executionAuthorized);
    CustomAgentDevelopmentPlan? PauseDevelopmentPlan(Guid teamId, Guid planId);
    CustomAgentDevelopmentPlan? ResumeDevelopmentPlan(
        Guid teamId, Guid planId, bool workspaceAuthorized, bool executionAuthorized,
        bool taskOnly = false, string? taskInstructions = null);
    CustomAgentDevelopmentPlan? CancelDevelopmentPlan(Guid teamId, Guid planId);
    CustomAgentDevelopmentPlan? RetryDevelopmentTask(
        Guid teamId, Guid planId, string? taskId, bool workspaceAuthorized, bool executionAuthorized,
        bool taskOnly = false, bool compilationOnly = false, bool recordOllamaTrace = false);
    CustomAgentDevelopmentPlan? ReviewDevelopmentCriterion(
        Guid teamId, Guid planId, string taskId, int criterionIndex,
        string? category, string? status, string? evidence,
        Guid? runId = null, Guid? reportId = null, string? scenarioId = null);
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
    CustomAgentCollaborationRun? RecordBrowserTestResult(
        Guid teamId, Guid runId, Guid workspaceId, CustomAgentBrowserTestReport report);
}

public sealed class CustomAgentModelUnavailableException()
    : Exception("The model selected for this agent is no longer installed in Ollama. Install it locally or edit the agent to choose an installed model.");
