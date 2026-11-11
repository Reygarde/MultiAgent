import type { Agent, Mission, Model } from '../types'

export const agents: Agent[] = [
  { id: 'Dispatch', description: 'Route les tâches', capabilities: ['routing'], modelId: 'local', isRouter: true, canGenerateProjects: false },
  { id: 'CustomCoder', description: 'Développe des projets', capabilities: ['C#'], modelId: 'local', isRouter: false, canGenerateProjects: true },
  { id: 'CustomGeneral', description: 'Explique des concepts', capabilities: ['explication'], modelId: 'local', isRouter: false, canGenerateProjects: false },
]
export const models: Model[] = [
  { id: 'local', name: 'custom:4b', provider: 'Ollama', contextSize: 4096, maxOutputTokens: 1024, temperature: 0.2, assignedAgents: ['Dispatch', 'CustomCoder'] },
]
export const mission: Mission = {
  id: '11111111-1111-1111-1111-111111111111', task: 'Créer un projet de test', kind: 'Project',
  requestedAgent: null, sandboxApproved: false, status: 'GeneratedUnverified',
  createdAt: '2026-11-11T16:00:00Z', updatedAt: '2026-11-11T16:01:00Z',
  selectedAgent: 'CustomCoder', selectedModel: 'custom:4b', reason: 'Demande de développement.',
  usedFallback: false, response: 'Projet généré.', error: null, hasWorkspace: true, correctionAttempts: 0,
  events: [
    { sequence: 1, timestamp: '2026-11-11T16:00:00Z', step: 'Generation', status: 'Succeeded', message: 'Fichiers validés.' },
    { sequence: 2, timestamp: '2026-11-11T16:01:00Z', step: 'Build', status: 'Unavailable', message: 'Docker indisponible.' },
  ],
  verification: [{ stage: 'Build', status: 'Unavailable', exitCode: null, log: 'Docker indisponible.', totalTests: null }],
}
