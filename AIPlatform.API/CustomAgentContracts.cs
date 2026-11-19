using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.API;

/// <summary>Configuration supplied when creating or updating a user-defined AI agent.</summary>
public sealed record CustomAgentRequest(
    string? Name, string? Description, string? SystemPrompt, string? ModelName,
    IReadOnlyList<string>? FallbackModels = null, bool AutomaticModelSelection = false,
    CustomAgentGenerationOptions? Generation = null);

/// <summary>Persisted user-defined AI agent; separate from workflow agents.</summary>
public sealed record CustomAgentResponse(
    Guid Id, string Name, string Description, string SystemPrompt, string ModelName,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<string>? FallbackModels = null,
    bool AutomaticModelSelection = false, CustomAgentGenerationOptions? Generation = null)
{
    public static CustomAgentResponse From(CustomAgentProfile agent) => new(
        agent.Id, agent.Name, agent.Description, agent.SystemPrompt, agent.ModelName,
        agent.CreatedAt, agent.UpdatedAt, agent.FallbackModels, agent.AutomaticModelSelection, agent.Generation);
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
    string Role, string Content, DateTimeOffset CreatedAt, CustomAgentProposalResponse? Proposal,
    Guid? DevelopmentPlanId, IReadOnlyList<string>? DevelopmentTaskIds, ModelRoutingDecision? RoutingDecision = null);

/// <summary>Conversation history returned after loading or sending a message.</summary>
public sealed record CustomAgentConversationResponse(
    Guid Id, Guid AgentId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<CustomAgentMessageResponse> Messages, CustomAgentWorkspaceResponse? Workspace)
{
    public static CustomAgentConversationResponse From(CustomAgentConversation conversation) => new(
        conversation.Id, conversation.AgentId, conversation.Title, conversation.CreatedAt,
        conversation.UpdatedAt, conversation.Messages.Select(message =>
            new CustomAgentMessageResponse(message.Role, message.Content, message.CreatedAt,
                message.Proposal is null ? null : CustomAgentProposalResponse.From(message.Proposal),
                message.DevelopmentPlanId, message.DevelopmentTaskIds, message.RoutingDecision)).ToArray(),
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
public sealed record CustomAgentTeamMemberResponse(Guid AgentId, string Role, int Order,
    IReadOnlyList<string>? FallbackModels = null);

/// <summary>A saved Planner, Developer, and Reviewer group.</summary>
public sealed record CustomAgentTeamResponse(
    Guid Id, string Name, IReadOnlyList<CustomAgentTeamMemberResponse> Members,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool ModelEscalationEnabled = false,
    bool AutomaticModelSelection = false)
{
    public static CustomAgentTeamResponse From(CustomAgentTeam team) => new(
        team.Id, team.Name, team.Members.Select(member =>
            new CustomAgentTeamMemberResponse(member.AgentId, member.Role, member.Order, member.FallbackModels)).ToArray(),
        team.CreatedAt, team.UpdatedAt, team.ModelEscalationEnabled, team.AutomaticModelSelection);
}

/// <summary>Payload for creating or updating a custom-agent team.</summary>
public sealed record CustomAgentTeamRequest(
    string? Name, IReadOnlyList<CustomAgentTeamMember>? Members, bool ModelEscalationEnabled = false,
    bool AutomaticModelSelection = false);

/// <summary>Creates a named React/TypeScript or .NET Studio project for a saved custom-agent team.</summary>
public sealed record CreateCustomAgentProjectRequest(string? Name, string? ProjectType, int? MaxTotalGenerations = null);

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

/// <summary>One bounded browser scenario represented only by allowlisted actions and accessible locators.</summary>
public sealed record CustomAgentBrowserScenarioResponse(
    string Id, string Name, string Path, IReadOnlyList<CustomAgentBrowserStep> Steps);

/// <summary>Result of one browser interaction scenario, with bounded local diagnostics.</summary>
public sealed record CustomAgentBrowserScenarioResultResponse(
    string Id, string Name, string Status, string? FailedStep, string? Error,
    IReadOnlyList<string> JavaScriptErrors, string? ScreenshotId);

/// <summary>Persisted browser-test result for a Studio project.</summary>
public sealed record CustomAgentBrowserTestResponse(
    Guid Id, string Status, string Url, DateTimeOffset TestedAt, int Passed, int Failed,
    string? Error, IReadOnlyList<CustomAgentBrowserScenarioResultResponse> Scenarios)
{
    public static CustomAgentBrowserTestResponse From(CustomAgentBrowserTestReport report) => new(
        report.Id, report.Status, report.Url, report.TestedAt, report.Passed, report.Failed, report.Error,
        report.Scenarios.Select(item => new CustomAgentBrowserScenarioResultResponse(
            item.Id, item.Name, item.Status, item.FailedStep, item.Error, item.JavaScriptErrors, item.ScreenshotId))
            .ToArray());
}

/// <summary>Explicitly authorizes local execution and optionally associates the result with a team run.</summary>
public sealed record RunStudioBrowserTestsRequest(
    bool ExecutionAuthorized, Guid? TeamId, Guid? CollaborationRunId,
    IReadOnlyList<CustomAgentBrowserScenario>? Scenarios);

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
    IReadOnlyList<CustomAgentExecutionResult>? ExecutionResults = null,
    IReadOnlyList<CustomAgentBrowserScenario>? BrowserScenarios = null,
    CustomAgentBrowserTestResponse? BrowserTest = null,
    IReadOnlyList<CustomAgentBrowserTestResponse>? BrowserTestHistory = null,
    IReadOnlyList<CustomAgentModelAttempt>? ModelAttempts = null)
{
    public static CustomAgentCollaborationRunResponse From(CustomAgentCollaborationRun run) => new(
        run.Id, run.TeamId, run.Task, run.Status, run.CurrentStep,
        CustomAgentWorkspaceResponse.From(run.Workspace), run.CreatedAt, run.UpdatedAt,
        run.Contributions.Select(item => new CustomAgentContributionResponse(
            item.AgentId, item.AgentName, item.Role, item.ModelName, item.Content, item.CreatedAt)).ToArray(),
        run.Proposal is null ? null : CustomAgentProposalResponse.From(run.Proposal), run.Error,
        run.Validation is null ? null : CustomAgentValidationResponse.From(run.Validation),
        run.Mode, run.AppliedChanges ?? [], run.ExecutionResults ?? [], run.BrowserScenarios ?? [],
        run.BrowserTest is null ? null : CustomAgentBrowserTestResponse.From(run.BrowserTest),
        run.BrowserTestHistory?.Select(CustomAgentBrowserTestResponse.From).ToArray() ?? [], run.ModelAttempts ?? []);
}

/// <summary>A user request and selected shared workspace for one team collaboration.</summary>
public sealed record StartCustomAgentCollaborationRequest(
    string? Task, CustomAgentWorkspaceSelection? Workspace,
    IReadOnlyList<CustomAgentWorkspaceFileRequest>? ExternalFiles = null,
    string? Mode = null, bool WorkspaceAuthorized = false, bool ExecutionAuthorized = false);

/// <summary>Provides the goal and selected Studio project for a new incremental development plan.</summary>
public sealed record CreateCustomAgentDevelopmentPlanRequest(
    string? Goal, CustomAgentWorkspaceSelection? Workspace);

/// <summary>Provides a user request to append tasks to an existing development plan.</summary>
public sealed record ExtendCustomAgentDevelopmentPlanRequest(string? Request);

/// <summary>Confirms both workspace modification and local-code execution for this plan session.</summary>
public sealed record DevelopmentPlanAuthorizationRequest(
    bool WorkspaceAuthorized, bool ExecutionAuthorized);

public sealed record ResumeDevelopmentPlanRequest(
    bool WorkspaceAuthorized, bool ExecutionAuthorized, bool TaskOnly = false, string? TaskInstructions = null);

public sealed record TargetedDevelopmentCorrectionRequest(
    CustomAgentTargetedCorrection Correction, bool WorkspaceAuthorized, bool ExecutionAuthorized);

/// <summary>Selects a failed task and confirms both required authorizations for its retry.</summary>
public sealed record RetryCustomAgentDevelopmentTaskRequest(
    string? TaskId, bool WorkspaceAuthorized, bool ExecutionAuthorized, bool TaskOnly = false,
    bool CompilationOnly = false, bool RecordOllamaTrace = false);

/// <summary>Associates a criterion with a saved browser scenario selected by the user.</summary>
public sealed record ReviewDevelopmentCriterionRequest(
    string? Category, string? Status, string? Evidence,
    Guid? RunId = null, Guid? ReportId = null, string? ScenarioId = null);

/// <summary>Describes a criterion classification and its user-provided verification.</summary>
public sealed record CustomAgentCriterionReviewResponse(
    string Category, string Status, string? Evidence, DateTimeOffset? ReviewedAt, bool Current,
    Guid? RunId, Guid? ReportId, string? ScenarioId);

/// <summary>Records a previous user classification or verification.</summary>
public sealed record CustomAgentCriterionReviewEventResponse(
    int CriterionIndex, string Category, string Status, string? Evidence, DateTimeOffset RecordedAt,
    Guid? RunId, Guid? ReportId, string? ScenarioId);

/// <summary>Represents one persisted implementation task and its latest validation outcome.</summary>
public sealed record CustomAgentDevelopmentTaskResponse(
    string Id, string Title, string Description, IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> AcceptanceCriteria, string Status, int Order,
    Guid? CollaborationRunId, int Attempts, string? Summary,
    IReadOnlyList<string> ChangedFiles, string? Error,
    CustomAgentValidationResponse? Validation,
    IReadOnlyList<CustomAgentCriterionReviewResponse> CriterionReviews,
    IReadOnlyList<CustomAgentCriterionReviewEventResponse> CriterionReviewHistory,
    IReadOnlyList<CustomAgentModelAttempt>? ModelAttempts = null)
{
    public static CustomAgentDevelopmentTaskResponse From(CustomAgentDevelopmentTask task) => new(
        task.Id, task.Title, task.Description, task.Dependencies, task.AcceptanceCriteria,
        task.Status, task.Order, task.CollaborationRunId, task.Attempts, task.Summary,
        task.ChangedFiles ?? [], task.Error,
        task.Validation is null ? null : CustomAgentValidationResponse.From(task.Validation),
        (task.CriterionReviews ?? task.AcceptanceCriteria.Select(_ =>
            new CustomAgentCriterionReview("Unclassified", "Unverified", null, null, null)).ToArray())
        .Select(review => new CustomAgentCriterionReviewResponse(
            review.Category, review.Status, review.Evidence, review.ReviewedAt,
            review.Status != "Unverified", review.RunId, review.ReportId, review.ScenarioId)).ToArray(),
        (task.CriterionReviewHistory ?? []).Select(item => new CustomAgentCriterionReviewEventResponse(
            item.CriterionIndex, item.Review.Category, item.Review.Status, item.Review.Evidence,
            item.RecordedAt, item.Review.RunId, item.Review.ReportId, item.Review.ScenarioId)).ToArray(), task.ModelAttempts ?? []);
}

/// <summary>Represents compact project facts retained across tasks in a development plan.</summary>
public sealed record CustomAgentProjectMemoryResponse(
    string Architecture, IReadOnlyList<string> CompletedFeatures,
    IReadOnlyList<string> Decisions, IReadOnlyList<string> KeyFiles,
    IReadOnlyList<string> KnownIssues)
{
    public static CustomAgentProjectMemoryResponse From(CustomAgentProjectMemory memory) => new(
        memory.Architecture, memory.CompletedFeatures, memory.Decisions,
        memory.KeyFiles, memory.KnownIssues);
}

/// <summary>Represents a persistent incremental plan and its task-by-task execution history.</summary>
public sealed record CustomAgentDevelopmentPlanResponse(
    Guid Id, Guid TeamId, string Goal, string Status, CustomAgentWorkspaceResponse Workspace,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<CustomAgentDevelopmentTaskResponse> Tasks,
    string? CurrentTaskId, int Attempts, int MaxAttempts,
    CustomAgentProjectMemoryResponse Memory, string? Error, Guid? ConversationId, Guid? ConversationAgentId)
{
    public static CustomAgentDevelopmentPlanResponse From(CustomAgentDevelopmentPlan plan) => new(
        plan.Id, plan.TeamId, plan.Goal, plan.Status,
        CustomAgentWorkspaceResponse.From(plan.Workspace)!,
        plan.CreatedAt, plan.UpdatedAt,
        plan.Tasks.Select(CustomAgentDevelopmentTaskResponse.From).ToArray(),
        plan.CurrentTaskId, plan.Attempts, plan.MaxAttempts,
        CustomAgentProjectMemoryResponse.From(plan.Memory), plan.Error, plan.ConversationId, plan.ConversationAgentId);
}

/// <summary>Explicit approval or refusal of a completed collaboration proposal.</summary>
public sealed record DecideCustomAgentCollaborationRequest(bool Approved);
