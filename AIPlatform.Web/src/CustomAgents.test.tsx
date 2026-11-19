import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { invoke, isTauri } from '@tauri-apps/api/core'
import { api } from './api'
import { CustomAgents } from './CustomAgents'
import type {
  CustomAgent, CustomAgentCollaborationRun, CustomAgentConversation, CustomAgentConversationSummary, Mission,
} from './types'

vi.mock('./api', () => ({
  api: {
    customAgents: vi.fn(),
    customAgentModels: vi.fn(),
    missions: vi.fn(),
    files: vi.fn(),
    file: vi.fn(),
    createCustomAgent: vi.fn(),
    updateCustomAgent: vi.fn(),
    duplicateCustomAgent: vi.fn(),
    deleteCustomAgent: vi.fn(),
    customAgentConversations: vi.fn(),
    createCustomAgentConversation: vi.fn(),
    customAgentConversation: vi.fn(),
    setCustomAgentWorkspace: vi.fn(),
    decideCustomAgentProposal: vi.fn(),
    recordExternalProposalOutcome: vi.fn(),
    sendCustomAgentMessage: vi.fn(),
    deleteCustomAgentConversation: vi.fn(),
    customAgentTeams: vi.fn(),
    saveCustomAgentTeam: vi.fn(),
    deleteCustomAgentTeam: vi.fn(),
    customAgentTeamRuns: vi.fn(),
    customAgentDevelopmentPlans: vi.fn(),
    customAgentTeamRun: vi.fn(),
    createCustomAgentProject: vi.fn(),
    startCustomAgentCollaboration: vi.fn(),
    cancelCustomAgentCollaboration: vi.fn(),
    decideCustomAgentCollaboration: vi.fn(),
    recordNativeCollaborationOutcome: vi.fn(),
  },
  errorMessage: (error: unknown) => error instanceof Error ? error.message : 'Erreur',
}))

vi.mock('@tauri-apps/api/core', () => ({
  invoke: vi.fn(),
  isTauri: vi.fn(() => false),
}))

const agent: CustomAgent = {
  id: 'agent-1', name: 'Assistant Développeur', description: 'Aide au code.',
  systemPrompt: 'Réponds en français.', modelName: 'qwen2.5-coder:7b',
  createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:00:00Z',
}
const summary: CustomAgentConversationSummary = {
  id: 'conversation-1', agentId: agent.id, title: 'Question C#',
  createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:02:00Z', messageCount: 2,
}
const savedConversation: CustomAgentConversation = {
  ...summary, messages: [
    { role: 'user', content: 'Explique une interface.', createdAt: '2026-11-15T10:00:00Z' },
    { role: 'assistant', content: 'Une interface définit un contrat.', createdAt: '2026-11-15T10:02:00Z' },
  ],
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(isTauri).mockReturnValue(false)
  vi.mocked(api.customAgents).mockResolvedValue([])
  vi.mocked(api.customAgentModels).mockResolvedValue(['qwen2.5-coder:7b', 'qwen3:4b'])
  vi.mocked(api.missions).mockResolvedValue([])
  vi.mocked(api.files).mockResolvedValue([])
  vi.mocked(api.file).mockImplementation(async (_id, path) => ({ path, content: 'source' }))
  vi.mocked(api.createCustomAgent).mockResolvedValue(agent)
  vi.mocked(api.updateCustomAgent).mockResolvedValue(agent)
  vi.mocked(api.duplicateCustomAgent).mockResolvedValue({ ...agent, id: 'agent-2', name: 'Assistant Développeur (copie)' })
  vi.mocked(api.deleteCustomAgent).mockResolvedValue(undefined)
  vi.mocked(api.customAgentConversations).mockResolvedValue([])
  vi.mocked(api.customAgentDevelopmentPlans).mockResolvedValue([])
  vi.mocked(api.createCustomAgentConversation).mockResolvedValue({
    id: 'conversation-2', agentId: agent.id, title: 'Nouvelle conversation',
    createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:00:00Z', messages: [],
  })
  vi.mocked(api.customAgentConversation).mockResolvedValue(savedConversation)
  vi.mocked(api.setCustomAgentWorkspace).mockImplementation(async (_agentId, _conversationId, workspace) => ({
    ...savedConversation, workspace,
  }))
  vi.mocked(api.decideCustomAgentProposal).mockResolvedValue(savedConversation)
  vi.mocked(api.recordExternalProposalOutcome).mockResolvedValue(savedConversation)
  vi.mocked(api.sendCustomAgentMessage).mockResolvedValue({
    id: 'conversation-2', agentId: agent.id, title: 'Bonjour',
    createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:02:00Z',
    messages: [
      { role: 'user', content: 'Bonjour', createdAt: '2026-11-15T10:01:00Z' },
      { role: 'assistant', content: 'Réponse réelle du modèle.', createdAt: '2026-11-15T10:02:00Z' },
    ],
  })
  vi.mocked(api.deleteCustomAgentConversation).mockResolvedValue(undefined)
  vi.mocked(api.customAgentTeams).mockResolvedValue([])
  vi.mocked(api.saveCustomAgentTeam).mockImplementation(async (name, members) => ({
    id: 'team-1', name, members, createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:00:00Z',
  }))
  vi.mocked(api.deleteCustomAgentTeam).mockResolvedValue(undefined)
  vi.mocked(api.createCustomAgentProject).mockResolvedValue({
    id: 'project-1', task: 'Agent project: Task manager', kind: 'Project', requestedAgent: null,
    sandboxApproved: false, status: 'Completed', createdAt: '2026-11-15T10:00:00Z',
    updatedAt: '2026-11-15T10:00:00Z', selectedAgent: null, selectedModel: null,
    reason: null, usedFallback: false, response: null, error: null, hasWorkspace: true,
    correctionAttempts: 0, events: [], verification: [], workflow: null, projectName: 'Task manager',
    projectDescription: 'React project created for custom-agent collaboration.', deleted: false,
    templateId: 'custom-agent-react', archived: false, archivedAt: null, completedAt: null,
  } satisfies Mission)
  vi.mocked(api.customAgentTeamRuns).mockResolvedValue([])
  vi.mocked(api.customAgentTeamRun).mockImplementation(async (_teamId, runId) => ({
    id: runId, teamId: 'team-1', task: 'Créer une page.', status: 'Completed', currentStep: null,
    workspace: null, createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:01:00Z',
    contributions: [
      { agentId: 'agent-1', agentName: 'Planner', role: 'Planner', modelName: 'qwen3:4b', content: 'Plan terminé.', createdAt: '2026-11-15T10:00:10Z' },
      { agentId: 'agent-2', agentName: 'Developer', role: 'Developer', modelName: 'qwen2.5-coder:7b', content: 'Code proposé.', createdAt: '2026-11-15T10:00:20Z' },
      { agentId: 'agent-3', agentName: 'Reviewer', role: 'Reviewer', modelName: 'qwen3:4b', content: 'Revue terminée.', createdAt: '2026-11-15T10:00:30Z' },
    ],
    proposal: null, error: null,
  }))
  vi.mocked(api.startCustomAgentCollaboration).mockImplementation(async (teamId, task, workspace) => ({
    id: 'run-1', teamId, task, status: 'Queued', currentStep: null, workspace,
    createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:00:00Z',
    contributions: [], proposal: null, error: null,
  }))
  vi.mocked(api.cancelCustomAgentCollaboration).mockResolvedValue(undefined)
  vi.mocked(api.decideCustomAgentCollaboration).mockImplementation(async (teamId, runId) => ({
    id: runId, teamId, task: 'Créer une page.', status: 'Rejected', currentStep: null,
    workspace: null, createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:01:00Z',
    contributions: [], proposal: null, error: null,
  }))
  vi.mocked(api.recordNativeCollaborationOutcome).mockImplementation(async (teamId, runId) => ({
    id: runId, teamId, task: 'Créer une page.', status: 'Applied', currentStep: null,
    workspace: null, createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:01:00Z',
    contributions: [], proposal: null, error: null,
  }))
})

describe('CustomAgents', () => {
  it('preserves ordered fallbacks and generation settings when explicitly enabling automatic mode', async () => {
    const user = userEvent.setup()
    const configured = { ...agent, fallbackModels: ['qwen3:4b'],
      generation: { contextSize: 8192, maxOutputTokens: 1024, temperature: 0.1 } }
    vi.mocked(api.customAgents).mockResolvedValue([configured])
    render(<CustomAgents />)
    await user.click(await screen.findByRole('button', { name: /Assistant Développeur/ }))
    await user.click(screen.getByRole('button', { name: /Modifier/ }))
    expect(screen.getByLabelText('Mode de sélection')).toHaveValue('manual')
    await user.selectOptions(screen.getByLabelText('Mode de sélection'), 'automatic')
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }))
    await waitFor(() => expect(api.updateCustomAgent).toHaveBeenCalledWith(agent.id, expect.objectContaining({
      automaticModelSelection: true, modelName: agent.modelName, fallbackModels: ['qwen3:4b'],
      generation: configured.generation,
    })))
  })

  it('creates an agent with an installed model and chats in a persisted conversation', async () => {
    const user = userEvent.setup()
    render(<CustomAgents />)
    await user.click(await screen.findByRole('button', { name: /Créer un agent/ }))
    await user.type(screen.getByRole('textbox', { name: 'Nom' }), agent.name)
    await user.type(screen.getByRole('textbox', { name: 'Description facultative' }), agent.description)
    await user.selectOptions(screen.getByRole('combobox', { name: 'Modèle Ollama installé' }), agent.modelName)
    await user.type(screen.getByRole('textbox', { name: 'Instructions système' }), agent.systemPrompt)
    await user.click(screen.getByRole('button', { name: 'Enregistrer' }))

    await screen.findByRole('heading', { name: agent.name })
    expect(api.createCustomAgent).toHaveBeenCalledWith({
      name: agent.name, description: agent.description,
      systemPrompt: agent.systemPrompt, modelName: agent.modelName,
    })
    await user.click(screen.getByRole('button', { name: 'Nouvelle conversation' }))
    await user.type(screen.getByRole('textbox', { name: 'Votre message' }), 'Bonjour')
    await user.click(screen.getByRole('button', { name: 'Envoyer' }))

    expect(await screen.findByText('Réponse réelle du modèle.')).toBeVisible()
    expect(api.sendCustomAgentMessage).toHaveBeenCalledWith(
      agent.id, 'conversation-2', 'Bonjour', expect.any(AbortSignal))
  })

  it('does not send a second message while a response is still generating', async () => {
    const user = userEvent.setup()
    vi.mocked(api.customAgents).mockResolvedValue([agent])
    let resolveResponse!: (conversation: CustomAgentConversation) => void
    const pendingResponse = new Promise<CustomAgentConversation>(resolve => {
      resolveResponse = resolve
    })
    vi.mocked(api.sendCustomAgentMessage).mockReturnValue(pendingResponse)

    render(<CustomAgents />)
    await user.click(await screen.findByRole('button', { name: /Assistant Développeur/ }))
    await user.click(screen.getByRole('button', { name: 'Nouvelle conversation' }))
    await user.type(screen.getByRole('textbox', { name: 'Votre message' }), 'Bonjour')
    const form = screen.getByRole('textbox', { name: 'Votre message' }).closest('form')
    if (!form) throw new Error('Message form was not rendered')

    fireEvent.submit(form)
    fireEvent.submit(form)
    expect(api.sendCustomAgentMessage).toHaveBeenCalledTimes(1)

    resolveResponse({
      ...savedConversation, id: 'conversation-2',
      messages: [{ role: 'assistant', content: 'Réponse.', createdAt: '2026-11-15T10:02:00Z' }],
    })
    expect(await screen.findByText('Réponse.')).toBeVisible()
  })

  it('requires explicit consent before sending a selected external snapshot to Ollama', async () => {
    const user = userEvent.setup()
    vi.mocked(api.customAgents).mockResolvedValue([agent])
    vi.mocked(api.createCustomAgentConversation).mockResolvedValue({
      ...savedConversation, id: 'conversation-2', title: 'Nouvelle conversation', messages: [],
    })
    vi.mocked(api.setCustomAgentWorkspace).mockImplementation(async (_agentId, _conversationId, workspace) => ({
      ...savedConversation, id: 'conversation-2', messages: [], workspace,
    }))
    render(<CustomAgents />)
    await user.click(await screen.findByRole('button', { name: /Assistant Développeur/ }))
    await user.click(screen.getByRole('button', { name: 'Nouvelle conversation' }))

    const source = new File(['public class Example {}'], 'Example.cs', { type: 'text/plain' })
    Object.defineProperty(source, 'webkitRelativePath', { value: 'sample/src/Example.cs' })
    Object.defineProperty(source, 'text', { value: async () => 'public class Example {}' })
    fireEvent.change(screen.getByLabelText('Choisir un dossier externe'), { target: { files: [source] } })
    await screen.findByText('sample · lecture seule')
    expect(screen.getByText(/L’écriture externe n’est disponible que dans AIPlatform Studio Desktop/)).toBeVisible()
    expect(screen.getByRole('textbox', { name: 'Votre message' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Envoyer' })).toBeDisabled()
    expect(api.sendCustomAgentMessage).not.toHaveBeenCalled()

    await user.click(screen.getByRole('checkbox', {
      name: /J’autorise l’envoi de cet instantané à Ollama local/,
    }))
    await user.type(screen.getByRole('textbox', { name: 'Votre message' }), 'Analyse ce fichier')
    await user.click(screen.getByRole('button', { name: 'Envoyer' }))
    await waitFor(() => expect(api.sendCustomAgentMessage).toHaveBeenCalledWith(
      agent.id, 'conversation-2', 'Analyse ce fichier', expect.any(AbortSignal),
      [{ path: 'sample/src/Example.cs', content: 'public class Example {}' }]))
  })

  it('applies and restores an external proposal only through native desktop approval', async () => {
    const user = userEvent.setup()
    vi.mocked(isTauri).mockReturnValue(true)
    vi.mocked(api.customAgents).mockResolvedValue([agent])
    vi.mocked(api.setCustomAgentWorkspace).mockImplementation(async (_agentId, conversationId, selectedWorkspace) => ({
      ...savedConversation, id: conversationId, messages: [], workspace: selectedWorkspace ?? undefined,
    }))
    const workspace: CustomAgentConversation['workspace'] = {
      kind: 'External', workspaceId: null, name: 'sample', authorizationId: 'grant-1',
    }
    const files = [
      { path: 'src/Example.cs', content: 'class Example { }' },
      { path: 'src/Old.cs', content: 'class Old { }' },
    ]
    const change = {
      path: 'src/Example.cs', summary: 'Ajouter une propriété.',
      before: 'class Example { }', after: 'class Example { int Value; }', operation: 'modify' as const,
    }
    const createdChange = {
      path: 'src/Features/Login.tsx', summary: 'Créer le composant de connexion.',
      before: null, after: 'export function Login() { return null }', operation: 'create' as const,
    }
    const deletedChange = {
      path: 'src/Old.cs', summary: 'Supprimer la classe obsolète.',
      before: 'class Old { }', after: null, operation: 'delete' as const,
    }
    const pending: CustomAgentConversation = {
      ...savedConversation, id: 'conversation-2', workspace,
      messages: [{
        role: 'assistant', content: 'Correction proposée.', createdAt: summary.updatedAt,
        proposal: {
          id: 'proposal-1', createdAt: summary.updatedAt, status: 'Pending',
          changes: [change, createdChange, deletedChange],
          canApply: false, applyReason: 'External folders are read-only.', workspace,
        },
      }],
    }
    const applied: CustomAgentConversation = {
      ...pending,
      messages: [{ ...pending.messages[0], proposal: {
        ...pending.messages[0].proposal!, status: 'Applied', externalBackupId: '0123456789abcdef0123456789abcdef',
      } }],
    }
    const restored: CustomAgentConversation = {
      ...applied,
      messages: [{ ...applied.messages[0], proposal: {
        ...applied.messages[0].proposal!, externalRestoredAt: '2026-11-15T10:03:00Z',
      } }],
    }
    vi.mocked(invoke)
      .mockResolvedValueOnce({ grantId: 'grant-1', name: 'sample', files })
      .mockResolvedValueOnce(undefined)
      .mockResolvedValueOnce('0123456789abcdef0123456789abcdef')
      .mockResolvedValueOnce(undefined)
      .mockResolvedValue(undefined)
    vi.mocked(api.sendCustomAgentMessage).mockResolvedValue(pending)
    vi.mocked(api.recordExternalProposalOutcome)
      .mockResolvedValueOnce(applied)
      .mockResolvedValueOnce(restored)
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)

    render(<CustomAgents />)
    await user.click(await screen.findByRole('button', { name: /Assistant Développeur/ }))
    await user.click(screen.getByRole('button', { name: 'Nouvelle conversation' }))
    await user.click(screen.getByRole('button', { name: 'Choisir un dossier Windows' }))
    await screen.findByText('sample · dossier Windows autorisé pour cette session')
    await user.click(screen.getByRole('button', { name: 'Autoriser les modifications pour cette session' }))
    expect(await screen.findByText(/Modifications autorisées pour cette session/)).toBeVisible()

    await user.click(screen.getByRole('checkbox', {
      name: /J’autorise l’envoi de cet instantané à Ollama local/,
    }))
    await user.type(screen.getByRole('textbox', { name: 'Votre message' }), 'Corrige ce fichier')
    await user.click(screen.getByRole('button', { name: 'Envoyer' }))
    await screen.findByText('Proposition · Pending')
    expect(screen.getByText(/3 opération\(s\)/)).toBeVisible()
    expect(screen.getByText(/Création · src\/Features\/Login\.tsx/)).toBeVisible()
    expect(screen.getByText(/Suppression · src\/Old\.cs/)).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Appliquer au dossier autorisé' }))
    await screen.findByText('Proposition · Applied')
    expect(api.recordExternalProposalOutcome).toHaveBeenNthCalledWith(
      1, agent.id, 'conversation-2', 'proposal-1', '0123456789abcdef0123456789abcdef', false)
    await user.click(screen.getByRole('button', { name: 'Restaurer les versions précédentes' }))
    await screen.findByText(/Restauré le /)
    expect(api.recordExternalProposalOutcome).toHaveBeenNthCalledWith(
      2, agent.id, 'conversation-2', 'proposal-1', '0123456789abcdef0123456789abcdef', true)
    expect(confirm).toHaveBeenCalledTimes(3)
    expect(invoke).toHaveBeenCalledWith('apply_external_workspace_proposal', {
      grantId: 'grant-1', proposalId: 'proposal-1',
      changes: [change, createdChange, deletedChange].map(({ path, before, after, operation }) => ({
        path, before, after, operation,
      })),
    })
    confirm.mockRestore()
  })

  it('keeps proposals tied to the native folder selection that created them', async () => {
    const user = userEvent.setup()
    vi.mocked(isTauri).mockReturnValue(true)
    vi.mocked(api.customAgents).mockResolvedValue([agent])
    vi.mocked(api.setCustomAgentWorkspace).mockImplementation(async (_agentId, conversationId, selectedWorkspace) => ({
      ...savedConversation, id: conversationId, messages: [], workspace: selectedWorkspace ?? undefined,
    }))
    const activeWorkspace: NonNullable<CustomAgentConversation['workspace']> = {
      kind: 'External', workspaceId: null, name: 'sample', authorizationId: 'grant-1',
    }
    const staleProposal: CustomAgentConversation = {
      ...savedConversation, id: 'conversation-2', workspace: activeWorkspace,
      messages: [{
        role: 'assistant', content: 'Correction proposée.', createdAt: summary.updatedAt,
        proposal: {
          id: 'proposal-old', createdAt: summary.updatedAt, status: 'Pending',
          changes: [{
            path: 'src/Example.cs', summary: 'Modifier la classe.',
            before: 'class Example { }', after: 'class Example { int Value; }', operation: 'modify',
          }],
          canApply: false, applyReason: 'External folders are read-only.',
          workspace: { ...activeWorkspace, authorizationId: 'previous-grant' },
        },
      }],
    }
    vi.mocked(invoke)
      .mockResolvedValueOnce({
        grantId: 'grant-1', name: 'sample',
        files: [{ path: 'src/Example.cs', content: 'class Example { }' }],
      })
      .mockResolvedValueOnce(undefined)
      .mockResolvedValue(undefined)
    vi.mocked(api.sendCustomAgentMessage).mockResolvedValue(staleProposal)
    vi.spyOn(window, 'confirm').mockReturnValue(true)

    render(<CustomAgents />)
    await user.click(await screen.findByRole('button', { name: /Assistant Développeur/ }))
    await user.click(screen.getByRole('button', { name: 'Nouvelle conversation' }))
    await user.click(screen.getByRole('button', { name: 'Choisir un dossier Windows' }))
    await screen.findByText('sample · dossier Windows autorisé pour cette session')
    await user.click(screen.getByRole('button', { name: 'Autoriser les modifications pour cette session' }))
    await user.click(screen.getByRole('checkbox', {
      name: /J’autorise l’envoi de cet instantané à Ollama local/,
    }))
    await user.type(screen.getByRole('textbox', { name: 'Votre message' }), 'Corrige ce fichier')
    await user.click(screen.getByRole('button', { name: 'Envoyer' }))

    await screen.findByText('Cette proposition appartient à une sélection antérieure; demandez-en une nouvelle.')
    expect(screen.getByRole('button', { name: 'Appliquer au dossier autorisé' })).toBeDisabled()
    expect(invoke).not.toHaveBeenCalledWith('apply_external_workspace_proposal', expect.anything())
  })

  it('loads history and confirms duplication, conversation deletion and agent deletion', async () => {
    const user = userEvent.setup()
    vi.mocked(api.customAgents).mockResolvedValue([agent])
    vi.mocked(api.customAgentConversations).mockResolvedValue([summary])
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<CustomAgents />)
    await user.click(await screen.findByRole('button', { name: /Assistant Développeur/ }))
    await user.click(await screen.findByRole('button', { name: /^Question C#/ }))
    expect(await screen.findByText('Une interface définit un contrat.')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Supprimer Question C#' }))
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('ses messages'))
    await waitFor(() => expect(api.deleteCustomAgentConversation).toHaveBeenCalledWith(agent.id, summary.id))

    await user.click(screen.getByRole('button', { name: /Dupliquer/ }))
    const copied = await screen.findByRole('button', { name: /Assistant Développeur \(copie\)/ })
    await user.click(copied)
    await user.click(screen.getByRole('button', { name: 'Supprimer' }))
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('tout son historique'))
    await waitFor(() => expect(api.deleteCustomAgent).toHaveBeenCalledWith('agent-2'))
    confirm.mockRestore()
  })

  it('creates a saved team and starts a collaboration with visible contributions', async () => {
    const user = userEvent.setup()
    const developer: CustomAgent = {
      ...agent, id: 'agent-2', name: 'Développeur', modelName: 'qwen2.5-coder:7b',
    }
    const reviewer: CustomAgent = {
      ...agent, id: 'agent-3', name: 'Réviseur', modelName: 'qwen3:4b',
    }
    vi.mocked(api.customAgents).mockResolvedValue([agent, developer, reviewer])
    let completedRun: CustomAgentCollaborationRun | null = null
    vi.mocked(api.startCustomAgentCollaboration).mockImplementation(async (teamId, task, workspace) => {
      completedRun = {
        id: 'run-1', teamId, task, status: 'Queued', currentStep: null, workspace,
        createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:00:00Z',
        contributions: [], proposal: null, error: null,
      }
      return completedRun!
    })
    vi.mocked(api.customAgentTeamRun).mockImplementation(async () => ({
      id: completedRun?.id ?? 'run-1', teamId: 'team-1', task: 'Créer une page.', status: 'Completed',
      currentStep: null, workspace: null, createdAt: '2026-11-15T10:00:00Z',
      updatedAt: '2026-11-15T10:01:00Z',
      contributions: [
        { agentId: 'agent-1', agentName: agent.name, role: 'Planner', modelName: 'qwen3:4b', content: 'Plan terminé.', createdAt: '2026-11-15T10:00:10Z' },
        { agentId: 'agent-2', agentName: developer.name, role: 'Developer', modelName: developer.modelName, content: 'Code proposé.', createdAt: '2026-11-15T10:00:20Z' },
        { agentId: 'agent-3', agentName: reviewer.name, role: 'Reviewer', modelName: reviewer.modelName, content: 'Revue terminée.', createdAt: '2026-11-15T10:00:30Z' },
      ],
      proposal: null, error: null,
    }))

    render(<CustomAgents />)
    await user.click(await screen.findByRole('button', { name: 'Équipes' }))
    await user.click(await screen.findByRole('button', { name: 'Nouvelle équipe' }))
    await user.type(screen.getByRole('textbox', { name: 'Nom de l’équipe' }), 'Équipe React')
    await user.selectOptions(screen.getByRole('combobox', { name: '1. Planificateur' }), agent.id)
    await user.selectOptions(screen.getByRole('combobox', { name: '2. Développeur' }), developer.id)
    await user.selectOptions(screen.getByRole('combobox', { name: '3. Réviseur' }), reviewer.id)
    await user.click(screen.getByRole('button', { name: 'Enregistrer l’équipe' }))
    await waitFor(() => expect(api.saveCustomAgentTeam).toHaveBeenCalledWith(
      'Équipe React',
      [
        { agentId: agent.id, role: 'Planner', order: 0, fallbackModels: null },
        { agentId: developer.id, role: 'Developer', order: 1, fallbackModels: null },
        { agentId: reviewer.id, role: 'Reviewer', order: 2, fallbackModels: null },
      ],
      undefined, false, false,
    ))
    await user.type(screen.getByRole('textbox', { name: 'Demande' }), 'Créer une page.')
    await user.click(screen.getByRole('button', { name: 'Lancer la collaboration' }))

    expect(await screen.findByText('Plan terminé.')).toBeVisible()
    expect(screen.getByText('Code proposé.')).toBeVisible()
    expect(screen.getByText('Revue terminée.')).toBeVisible()
    expect(api.startCustomAgentCollaboration).toHaveBeenCalledWith('team-1', 'Créer une page.', null, undefined)
  })

  it('creates a Studio project and starts its team run against that workspace', async () => {
    const user = userEvent.setup()
    const developer: CustomAgent = { ...agent, id: 'agent-2', name: 'Développeur' }
    const reviewer: CustomAgent = { ...agent, id: 'agent-3', name: 'Réviseur' }
    vi.mocked(api.customAgents).mockResolvedValue([agent, developer, reviewer])
    render(<CustomAgents />)
    await user.click(await screen.findByRole('button', { name: 'Équipes' }))
    await user.click(await screen.findByRole('button', { name: 'Nouvelle équipe' }))
    await user.type(screen.getByRole('textbox', { name: 'Nom de l’équipe' }), 'Équipe React')
    await user.selectOptions(screen.getByRole('combobox', { name: '1. Planificateur' }), agent.id)
    await user.selectOptions(screen.getByRole('combobox', { name: '2. Développeur' }), developer.id)
    await user.selectOptions(screen.getByRole('combobox', { name: '3. Réviseur' }), reviewer.id)
    await user.click(screen.getByRole('button', { name: 'Enregistrer l’équipe' }))
    await waitFor(() => expect(api.saveCustomAgentTeam).toHaveBeenCalled())

    await user.selectOptions(screen.getByRole('combobox', { name: 'Projet Studio' }), '__new_project__')
    await user.type(screen.getByRole('textbox', { name: 'Nom du projet' }), 'Task manager')
    await user.click(screen.getByRole('button', { name: 'Créer le projet' }))
    await waitFor(() => expect(api.createCustomAgentProject).toHaveBeenCalledWith(
      'team-1', 'Task manager', 'React'))
    expect(screen.getByRole('combobox', { name: 'Projet Studio' })).toHaveValue('project-1')

    await user.type(screen.getByRole('textbox', { name: 'Demande' }), 'Créer les filtres de tâches.')
    await user.click(screen.getByRole('button', { name: 'Lancer la collaboration' }))
    await waitFor(() => expect(api.startCustomAgentCollaboration).toHaveBeenCalledWith(
      'team-1', 'Créer les filtres de tâches.',
      { kind: 'Studio', workspaceId: 'project-1', name: 'Task manager' }, undefined))
  })
})
