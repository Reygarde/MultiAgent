export const maximumModelContext = 262144

export interface Agent {
  id: string
  description: string
  capabilities: string[]
  modelId: string
  isRouter: boolean
  canGenerateProjects: boolean
  name?: string
  role?: string
  enabled?: boolean
  allowedTools?: string[]
  taskTypes?: string[]
}

export interface CustomAgent {
  id: string
  name: string
  description: string
  systemPrompt: string
  modelName: string
  fallbackModels?: string[] | null
  automaticModelSelection?: boolean
  generation?: { contextSize: number; maxOutputTokens: number; temperature: number } | null
  createdAt: string
  updatedAt: string
}

export interface CustomAgentMessage {
  role: 'user' | 'assistant'
  content: string
  createdAt: string
  developmentPlanId?: string | null
  developmentTaskIds?: string[] | null
  proposal?: CustomAgentProposal | null
  routingDecision?: import('./modelLibraryApi').ModelRoutingDecision | null
}

export type CustomAgentWorkspaceKind = 'None' | 'Studio' | 'External'

export interface CustomAgentWorkspace {
  kind: CustomAgentWorkspaceKind
  workspaceId: string | null
  name: string
  authorizationId?: string | null
}

export interface CustomAgentWorkspaceFile {
  path: string
  content: string
}

export interface CustomAgentFileChange {
  path: string
  summary: string
  before: string | null
  after: string | null
  operation: 'create' | 'modify' | 'delete'
}

export interface CustomAgentProposal {
  id: string
  createdAt: string
  status: 'Pending' | 'Applied' | 'Rejected'
  changes: CustomAgentFileChange[]
  canApply: boolean
  applyReason: string | null
  workspace?: CustomAgentWorkspace | null
  externalBackupId?: string | null
  externalRestoredAt?: string | null
}

export interface NativeExternalFolderSelection {
  grantId: string
  name: string
  files: CustomAgentWorkspaceFile[]
}

export interface CustomAgentConversationSummary {
  id: string
  agentId: string
  title: string
  createdAt: string
  updatedAt: string
  messageCount: number
}

export interface CustomAgentConversation {
  id: string
  agentId: string
  title: string
  createdAt: string
  updatedAt: string
  messages: CustomAgentMessage[]
  workspace?: CustomAgentWorkspace | null
}

export interface CustomAgentDraft {
  name: string
  description: string
  systemPrompt: string
  modelName: string
  fallbackModels?: string[] | null
  automaticModelSelection?: boolean
  generation?: { contextSize: number; maxOutputTokens: number; temperature: number } | null
}

export type CustomAgentTeamRole = 'Planner' | 'Developer' | 'Reviewer'

export interface CustomAgentTeamMember {
  agentId: string
  role: CustomAgentTeamRole
  order: number
  fallbackModels?: string[] | null
}

export interface CustomAgentTeam {
  id: string
  name: string
  members: CustomAgentTeamMember[]
  createdAt: string
  updatedAt: string
  modelEscalationEnabled?: boolean
  automaticModelSelection?: boolean
}

export interface CustomAgentDevelopmentTask {
  id: string
  title: string
  description: string
  dependencies: string[]
  acceptanceCriteria: string[]
  status: string
  order: number
  collaborationRunId: string | null
  attempts: number
  summary: string | null
  changedFiles: string[]
  error: string | null
  validation: CustomAgentValidationReport | null
  criterionReviews: CustomAgentCriterionReview[]
  criterionReviewHistory: CustomAgentCriterionReviewEvent[]
  modelAttempts?: CustomAgentModelAttempt[]
}

export interface CustomAgentCriterionReviewEvent {
  criterionIndex: number
  category: string
  status: string
  evidence: string | null
  recordedAt: string
  runId: string | null
  reportId: string | null
  scenarioId: string | null
}

export interface CustomAgentCriterionReview {
  category: 'Unclassified' | 'User' | 'Technical' | 'Optional'
  status: 'Unverified' | 'Verified' | 'Failed'
  evidence: string | null
  reviewedAt: string | null
  current: boolean
  runId: string | null
  reportId: string | null
  scenarioId: string | null
}

export interface CustomAgentProjectMemory {
  architecture: string
  completedFeatures: string[]
  decisions: string[]
  keyFiles: string[]
  knownIssues: string[]
}

export interface CustomAgentDevelopmentPlan {
  id: string
  teamId: string
  goal: string
  status: string
  workspace: CustomAgentWorkspace
  createdAt: string
  updatedAt: string
  tasks: CustomAgentDevelopmentTask[]
  currentTaskId: string | null
  attempts: number
  maxAttempts: number
  memory: CustomAgentProjectMemory
  error: string | null
  conversationId?: string | null
  conversationAgentId?: string | null
}

export interface CustomAgentContribution {
  agentId: string
  agentName: string
  role: CustomAgentTeamRole
  modelName: string
  content: string
  createdAt: string
}

export interface CustomAgentValidationDiagnostic {
  path: string
  code: string
  message: string
  severity: 'Error' | 'Warning'
  relatedPath: string | null
}

export interface CustomAgentValidationAttempt {
  number: number
  status: 'Passed' | 'Warnings' | 'Errors' | 'Unavailable'
  diagnostics: CustomAgentValidationDiagnostic[]
}

export interface CustomAgentValidationReport {
  staticStatus: 'Passed' | 'Warnings' | 'Errors' | 'Unavailable'
  compilationStatus: 'Unavailable' | 'Passed' | 'Failed' | 'TimedOut'
  correctionAttempts: number
  diagnostics: CustomAgentValidationDiagnostic[]
  history: CustomAgentValidationAttempt[]
}

export interface CustomAgentCollaborationRun {
  id: string
  teamId: string
  task: string
  status: 'Queued' | 'Running' | 'AwaitingApproval' | 'Completed' | 'Applied' | 'Rejected' | 'Failed' | 'Cancelled' | 'Restored'
  currentStep: string | null
  workspace: CustomAgentWorkspace | null
  createdAt: string
  updatedAt: string
  contributions: CustomAgentContribution[]
  proposal: CustomAgentProposal | null
  error: string | null
  validation?: CustomAgentValidationReport | null
  mode?: 'Supervised' | 'Autonomous'
  appliedChanges?: CustomAgentFileChange[]
  executionResults?: CustomAgentExecutionResult[]
  browserScenarios?: CustomAgentBrowserScenario[]
  browserTest?: CustomAgentBrowserTestReport | null
  browserTestHistory?: CustomAgentBrowserTestReport[]
  modelAttempts?: CustomAgentModelAttempt[]
}

export interface CustomAgentModelAttempt {
  id: string
  runId: string
  agentId: string
  role: CustomAgentTeamRole
  modelName: string
  selectionReason: string
  startedAt: string
  outcome: string
  failureKind: string | null
  decision: string | null
  nextModel: string | null
  compilationStatus: string
  taskCategory?: string | null
  completedAt?: string | null
  routingDecision?: import('./modelLibraryApi').ModelRoutingDecision | null
  testsStatus?: string
  browserStatus?: string
  rolledBack?: boolean
}

export interface CustomAgentExecutionResult {
  command: string
  exitCode: number | null
  timedOut: boolean
  output: string
  startedAt: string
  completedAt: string
}

export type CustomAgentBrowserAction = 'fill' | 'select' | 'click' | 'expectVisible' | 'expectHidden' | 'reload'
export type CustomAgentBrowserLocatorType = 'role' | 'label' | 'placeholder' | 'text'

export interface CustomAgentBrowserStep {
  action: CustomAgentBrowserAction
  locatorType: CustomAgentBrowserLocatorType | null
  locator: string | null
  value: string | null
  role: 'button' | 'checkbox' | 'heading' | 'link' | 'listitem' | 'option' | 'radio' | 'textbox' | null
  index: number | null
}

export interface CustomAgentBrowserScenario {
  id: string
  name: string
  path: string
  steps: CustomAgentBrowserStep[]
}

export interface CustomAgentBrowserScenarioResult {
  id: string
  name: string
  status: 'Passed' | 'Failed'
  failedStep: string | null
  error: string | null
  javaScriptErrors: string[]
  screenshotId: string | null
}

export interface CustomAgentBrowserTestReport {
  id: string
  status: 'Passed' | 'Failed' | 'InvalidScenario' | 'BrowserUnavailable' |
    'ServerUnavailable' | 'TimedOut' | 'Unsupported' | 'Cancelled'
  url: string
  testedAt: string
  passed: number
  failed: number
  error: string | null
  scenarios: CustomAgentBrowserScenarioResult[]
}

export type LocalApplicationStatus = 'Stopped' | 'Starting' | 'Running' | 'Stopping' | 'Error'

export interface LocalApplication {
  workspaceId: string
  status: LocalApplicationStatus
  url: string | null
  command: string | null
  output: string[]
  error: string | null
  processId: number | null
  updatedAt: string
}

export interface AgentSettings {
  id: string; name: string; role: string; enabled: boolean; description: string
  modelId: string; systemPrompt: string; capabilities: string[]; fallbackKeywords: string[]
  allowedTools: string[]; taskTypes: string[]; timeoutSeconds: number
}
export interface AgentConfiguration { agents: AgentSettings[]; models: Omit<Model, 'assignedAgents'>[] }
export interface Workflow {
  automaticReactCorrections?: boolean
  pendingSoftwareAgentCall?: string | null
  pendingSoftwareCallDeliveryState?: 'Unknown' | 'DefinitelyNotSent' | 'MayHaveBeenSent'
  softwareCorrections?: {
    id: string; trigger: string; reason: string; completedGroups: number; totalGroups: number
    reviewerCapacity: number; inFlight: boolean; ready: boolean; decisions: string[]; failure?: string | null
    callState?: string
  }[]
  workflowId: string; missionId: string; version: number
  automaticModelSelection?: boolean
  requirePlanApproval?: boolean
  planApproval?: { version: number; fingerprint: string; approved: boolean | null; decidedAt: string | null } | null
  planAmendments?: {
    previousVersion: number; preparedAt: string; previousPlan: NonNullable<Workflow['plan']>
    previousApproval: { fingerprint: string }; developerModel: string
    compatibility: { status: string; reason: string }; retainedPaths: string[]; remainingPaths: string[]
    retainedSourceDiagnostics: { file: string | null; line: number | null; code: string | null; message: string }[]
  }[]
  acceptanceCriteria?: { id: string; requirement: string }[]
  requirementTrace?: { id: string; originalText: string; normalizedText: string; categories: string[]
    criterionIds: string[]; diagnostic?: string | null }[]
  criterionExecutions?: { criterionId: string; testFile: string; testName: string; attempt: number
    version: number; sourceFingerprint: string; provenance: string }[]
  steps: {
    id: string; kind: string; agentId: string; dependsOn: string[]; status: string
    attempts: number; durationMilliseconds: number | null; modelName?: string | null
    input: string | null; output: string | null; error: string | null
    version?: number | null; startedAt?: string | null
    selectionMode?: string
    modelSelections?: { key: string; agentId: string; decision: import('./modelLibraryApi').ModelRoutingDecision }[]
    modelInvocations?: { stepId: string; sequence: string; attempt: CustomAgentModelAttempt }[]
  }[]
  plan: { name: string; description: string; decisions: string[]; files: { path: string; purpose: string; dependsOn: string[] }[]
    originalRequest?: string | null
    acceptanceCriteria?: { id: string; requirement: string }[] | null
    criterionCoverage?: { criterionId: string; paths: string[]; explanation: string }[] | null } | null
  review: { summary: string; findings: { path: string; severity: string; message: string }[]
    problem?: string | null; modifiedFiles?: string[] | null; justification?: string | null
    risks?: string[] | null; planCompliance?: string | null; recommendation?: string | null
    criterionReviews?: { criterionId: string; verdict: 'Satisfied' | 'NotSatisfied' | 'Unverified'
      explanation: string; defect?: string | null; uncertainty?: string | null
      evidence: { path: string; quote: string }[] }[] | null } | null
  revisions: { version: number; createdAt: string; reason: string; fingerprint: string; files: string[] }[]
  finalReport: string | null
  correctionLimit?: number | null
  executionPlanDifferences?: { field: string; expected: string | null; actual: string | null }[] | null
  proposals?: { version: number; createdAt: string; review: Workflow['review'] }[]
}

export interface RevisionComparison {
  version: number; previousVersion: number | null
  changes: { path: string; before: string | null; after: string | null }[]
  proposed?: boolean; review?: Workflow['review']
}

export interface Model {
  id: string
  provider: string
  name: string
  contextSize: number
  maxOutputTokens: number
  temperature: number
  assignedAgents: string[]
}

export type MissionKind = 'Chat' | 'Project'
export type MissionStatus = 'Queued' | 'Running' | 'Completed' | 'GeneratedUnverified' | 'Failed' | 'Cancelled'
  | 'AwaitingApproval' | 'BuildFailed' | 'TestsFailed' | 'Verified' | 'AwaitingPlanApproval'
export type StepStatus = 'Running' | 'Succeeded' | 'Failed' | 'Unavailable' | 'Skipped' | 'Cancelled'

export interface MissionEvent {
  sequence: number
  timestamp: string
  step: string
  status: StepStatus
  message: string
  durationMilliseconds?: number | null
}

export interface Verification {
  stage: string
  status: string
  exitCode: number | null
  log: string
  totalTests: number | null
  durationMilliseconds?: number | null
  attempt?: number
  warningCount?: number | null
  errorCount?: number | null
  failedTests?: number | null
  diagnostics?: { stage: string; command: string; exitCode: number | null; excerpt: string
    items: { type: string; project: string | null; file: string | null; line: number | null
      column: number | null; code: string | null; message: string }[] } | null
}

export interface Mission {
  id: string
  task: string
  kind: MissionKind
  requestedAgent: string | null
  sandboxApproved: boolean
  status: MissionStatus
  createdAt: string
  updatedAt: string
  selectedAgent: string | null
  selectedModel: string | null
  reason: string | null
  usedFallback: boolean
  response: string | null
  error: string | null
  hasWorkspace: boolean
  correctionAttempts: number
  events: MissionEvent[]
  verification: Verification[]
  workflow?: Workflow | null
  projectName?: string | null
  projectDescription?: string | null
  deleted?: boolean
  templateId?: string | null
  archived?: boolean
  archivedAt?: string | null
  completedAt?: string | null
  maxTotalGenerations?: number | null
  totalGenerations?: number
  remainingGenerations?: number | null
  generationReservations?: { id: string; role: string; model: string; reservedAt: string }[] | null
  generationBudgetWarning?: string | null
  sizing?: ProjectSizingProfile | null
}

export interface ReviewerCorrectionResumeEligibility {
  available: boolean
  inProgress: boolean
  reason: string
}

export interface ProjectSizingProfile {
  id: string; name: string; maxFiles: number; maxPlannedFiles: number; maxFileBytes: number; maxProjectBytes: number
  maxCriteria: number; maxCorrections: number; maxRepairGroups: number; maxFilesPerRepairGroup: number; missionTimeoutSeconds: number
}
export interface MissionCapacity {
  count: number; limit: number; remaining: number; defaultMaxTotalGenerations?: number
  sizingProfiles?: ProjectSizingProfile[]
}
export interface TwitchSettings { clientId: string; configured: boolean }
export interface TwitchCommand {
  name: string; response: string; cooldownSeconds: number; enabled: boolean; moderatorOnly?: boolean
}
export interface TwitchEvent { timestamp: string; kind: string; message: string }
export interface TwitchBot {
  id: string; name: string; channel: string; state: string; authorized: boolean
  accountLogin: string | null; error: string | null; commands: TwitchCommand[]; events: TwitchEvent[]
}
export interface TwitchDeviceAuthorization {
  verificationUri: string; userCode: string; expiresIn: number; interval: number
}
export interface TwitchConnectResult {
  pending: boolean; authorized: boolean; accountLogin: string | null; message: string | null
}
export interface TwitchCommandTestResult {
  matched: boolean; response: string | null; cooldownRemainingSeconds: number
}
export interface TwitchSimulationEvent { userId: string; role: 'Viewer' | 'Moderator'; text: string; atSeconds: number }
export interface TwitchSimulationResult {
  botId: string
  steps: { event: TwitchSimulationEvent; outcome: 'Responded' | 'PermissionDenied' | 'Cooldown' | 'NoMatch'
    response: string | null; cooldownRemainingSeconds: number }[]
}
export interface TwitchCommandOperation {
  action: 'add' | 'update' | 'disable' | 'delete'; name: string; response: string | null
  cooldownSeconds: number | null; enabled: boolean | null; moderatorOnly: boolean | null
}
export interface TwitchCommandProposal {
  id: string; botId: string; teamId: string; createdAt: string; request: string
  status: string; operations: TwitchCommandOperation[]; unsupported: string[]
  before: TwitchCommand[]; after: TwitchCommand[]
  evidence: { command: string; criterion: string; expected: string; actual: string; passed: boolean }[]
  error: string | null
}

export interface LocalModels {
  available: boolean
  error: string | null
  models: { name: string; installed: boolean; loaded: boolean; inUse: boolean }[]
}

export interface Sandbox { available: boolean; reason: string; engineAvailable?: boolean }
export type EnvironmentState = 'Ready' | 'Missing' | 'Incompatible' | 'Unverified'
export interface EnvironmentCheck {
  id: string; name: string; status: EnvironmentState; detail: string; version: string | null
}
export interface StudioEnvironmentReport {
  checkedAt: string
  operatingSystem: string
  architecture: string
  system: EnvironmentCheck
  memory: EnvironmentCheck
  gpu: EnvironmentCheck
  webView2: EnvironmentCheck
  node: EnvironmentCheck
  totalRamBytes: number | null
  availableRamBytes: number | null
  gpus: { name: string; totalBytes: number; availableBytes: number }[]
  ollama: EnvironmentCheck
  models: { name: string; status: EnvironmentState; installed: boolean | null }[]
  dockerDesktop: EnvironmentCheck
  dockerEngine: EnvironmentCheck
  sandboxImages: {
    imageReference: string; version: string; status: EnvironmentState
    installedImageId: string | null; expectedImageId: string | null; detail: string
  }[]
  provisioningPackage: {
    status: EnvironmentState; version: string | null; fingerprint: string | null
    imageCount: number; totalBytes: number | null; detail: string
  }
  warnings: string[]
}
export interface ProvisioningPackageCheck {
  status: EnvironmentState; version: string | null; fingerprint: string | null
  imageCount: number; totalBytes: number | null; detail: string
}
export interface EnvironmentImageCheck {
  imageReference: string; version: string; status: EnvironmentState
  installedImageId: string | null; expectedImageId: string | null; detail: string
}
export interface ExecutionPlan {
  fingerprint: string
  image: string
  memoryMegabytes: number
  cpuCount: number
  timeoutSeconds: number
  maxCorrectionAttempts: number
  operations: string[]
  canonical?: {
    workflowVersion: number
    files: { path: string; sha256: string }[]
    automaticCorrections: boolean
    stages: { name: string; arguments: string[] }[]
  } | null
}
export interface WorkspaceFile { path: string; size: number }
export interface FileContent { path: string; content: string }
export interface ProjectAnalysis {
  gitRepositoryDetected: boolean | null
  languages: string[]; frameworks: string[]; solutions: string[]; projects: string[]
  packageManifests: string[]; testProjects: string[]; entryPoints: string[]; directories: string[]
  dependencies: { ecosystem: string; name: string; version: string | null; manifest: string }[]
  scripts: { manifest: string; name: string }[]
  dockerFiles: string[]; issues: { path: string; message: string }[]
}
export interface CreateMission {
  sizingProfileId?: string
  automaticReactCorrections?: boolean
  task: string
  kind: MissionKind
  agentId: string | null
  approveSandboxExecution: boolean
  templateId?: string | null
  automaticModelSelection?: boolean
  recordPlannerTrace?: boolean
  requirePlanApproval?: boolean
  maxTotalGenerations?: number
}

export interface PlannerAttemptDiagnostic {
  missionId: string
  runId: string
  attemptId: string
  attemptNumber: number
  model: string
  createdAt: string
  status: string
  plan: unknown
  criteria: { id: string; requirement: string }[]
  errors: { criterionId: string | null; rule: string; field: string; path: string | null; explanation: string }[]
  rawTraceAvailable: boolean
  dependencyNormalizations?: { field: string; path: string; package: string }[] | null
}
export type CustomAgentProjectType = 'React' | 'DotNet'

export const isActive = (mission: Mission) =>
  mission.status === 'Queued' || mission.status === 'Running' || mission.status === 'AwaitingApproval' ||
  mission.status === 'AwaitingPlanApproval'

export const statusLabels: Record<MissionStatus, string> = {
  Queued: 'En attente',
  Running: 'En cours',
  Completed: 'Terminée',
  GeneratedUnverified: 'Généré · non vérifié',
  Failed: 'Échec',
  Cancelled: 'Annulée',
  AwaitingApproval: 'Autorisation requise',
  AwaitingPlanApproval: 'Approbation du plan requise',
  BuildFailed: 'Compilation échouée',
  TestsFailed: 'Tests échoués',
  Verified: 'Vérifié · compilation et tests réussis',
}

export const stepLabels: Record<string, string> = {
  Queue: 'File d’attente', Analysis: 'Analyse', Routing: 'Routage',
  Generation: 'Génération', Writing: 'Écriture des fichiers', Build: 'Compilation',
  Tests: 'Tests', TypeScript: 'Vérification TypeScript', Corrections: 'Corrections', Mission: 'Mission',
  Authorization: 'Autorisation', ErrorAnalysis: 'Analyse des erreurs', Revalidation: 'Nouvelle validation d’infrastructure',
  Plan: 'Planification', Generate: 'Génération incrémentale', Correct: 'Correction ciblée',
  Contract: 'Contrat API', BackendGenerate: 'Génération backend', FrontendGenerate: 'Génération frontend',
  BackendCorrect: 'Correction backend', FrontendCorrect: 'Correction frontend',
  BackendBuild: 'Compilation backend Release', BackendTests: 'Tests xUnit',
  FrontendBuild: 'Build Vite', FrontendTests: 'Tests Vitest', Integration: 'Intégration HTTP',
  Review: 'Revue', Approval: 'Autorisation', Report: 'Rapport final',
}

export const stepStatusLabels: Record<StepStatus, string> = {
  Running: 'En cours', Succeeded: 'Réussi', Failed: 'Échec',
  Unavailable: 'Indisponible', Skipped: 'Non exécuté', Cancelled: 'Annulé',
}
