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

export interface AgentSettings {
  id: string; name: string; role: string; enabled: boolean; description: string
  modelId: string; systemPrompt: string; capabilities: string[]; fallbackKeywords: string[]
  allowedTools: string[]; taskTypes: string[]; timeoutSeconds: number
}
export interface AgentConfiguration { agents: AgentSettings[]; models: Omit<Model, 'assignedAgents'>[] }
export interface Workflow {
  workflowId: string; missionId: string; version: number
  steps: {
    id: string; kind: string; agentId: string; dependsOn: string[]; status: string
    attempts: number; durationMilliseconds: number | null; modelName?: string | null
    input: string | null; output: string | null; error: string | null
    version?: number | null; startedAt?: string | null
  }[]
  plan: { name: string; description: string; decisions: string[]; files: { path: string; purpose: string; dependsOn: string[] }[] } | null
  review: { summary: string; findings: { path: string; severity: string; message: string }[]
    problem?: string | null; modifiedFiles?: string[] | null; justification?: string | null
    risks?: string[] | null; planCompliance?: string | null; recommendation?: string | null } | null
  revisions: { version: number; createdAt: string; reason: string; fingerprint: string; files: string[] }[]
  finalReport: string | null
  correctionLimit?: number | null
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
  | 'AwaitingApproval' | 'BuildFailed' | 'TestsFailed' | 'Verified'
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
}

export interface LocalModels {
  available: boolean
  error: string | null
  models: { name: string; installed: boolean; loaded: boolean; inUse: boolean }[]
}

export interface Sandbox { available: boolean; reason: string; engineAvailable?: boolean }
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
  task: string
  kind: MissionKind
  agentId: string | null
  approveSandboxExecution: boolean
  templateId?: string | null
}

export const isActive = (mission: Mission) =>
  mission.status === 'Queued' || mission.status === 'Running' || mission.status === 'AwaitingApproval'

export const statusLabels: Record<MissionStatus, string> = {
  Queued: 'En attente',
  Running: 'En cours',
  Completed: 'Terminée',
  GeneratedUnverified: 'Généré · non vérifié',
  Failed: 'Échec',
  Cancelled: 'Annulée',
  AwaitingApproval: 'Autorisation requise',
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
