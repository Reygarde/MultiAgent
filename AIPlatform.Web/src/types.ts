export interface Agent {
  id: string
  description: string
  capabilities: string[]
  modelId: string
  isRouter: boolean
  canGenerateProjects: boolean
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
export type StepStatus = 'Running' | 'Succeeded' | 'Failed' | 'Unavailable' | 'Skipped' | 'Cancelled'

export interface MissionEvent {
  sequence: number
  timestamp: string
  step: string
  status: StepStatus
  message: string
}

export interface Verification {
  stage: string
  status: string
  exitCode: number | null
  log: string
  totalTests: number | null
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
}

export interface LocalModels {
  available: boolean
  error: string | null
  models: { name: string; installed: boolean; loaded: boolean; inUse: boolean }[]
}

export interface Sandbox { available: boolean; reason: string }
export interface WorkspaceFile { path: string; size: number }
export interface FileContent { path: string; content: string }
export interface CreateMission {
  task: string
  kind: MissionKind
  agentId: string | null
  approveSandboxExecution: boolean
}

export const isActive = (mission: Mission) => mission.status === 'Queued' || mission.status === 'Running'

export const statusLabels: Record<MissionStatus, string> = {
  Queued: 'En attente',
  Running: 'En cours',
  Completed: 'Terminée',
  GeneratedUnverified: 'Généré · non vérifié',
  Failed: 'Échec',
  Cancelled: 'Annulée',
}

export const stepLabels: Record<string, string> = {
  Queue: 'File d’attente', Analysis: 'Analyse', Routing: 'Routage',
  Generation: 'Génération', Writing: 'Écriture des fichiers', Build: 'Compilation',
  Tests: 'Tests', Corrections: 'Corrections', Mission: 'Mission',
}

export const stepStatusLabels: Record<StepStatus, string> = {
  Running: 'En cours', Succeeded: 'Réussi', Failed: 'Échec',
  Unavailable: 'Indisponible', Skipped: 'Non exécuté', Cancelled: 'Annulé',
}
