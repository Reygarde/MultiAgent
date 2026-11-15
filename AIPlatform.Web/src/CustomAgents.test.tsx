import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from './api'
import { CustomAgents } from './CustomAgents'
import type { CustomAgent, CustomAgentConversation, CustomAgentConversationSummary } from './types'

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
    sendCustomAgentMessage: vi.fn(),
    deleteCustomAgentConversation: vi.fn(),
  },
  errorMessage: (error: unknown) => error instanceof Error ? error.message : 'Erreur',
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
  vi.mocked(api.createCustomAgentConversation).mockResolvedValue({
    id: 'conversation-2', agentId: agent.id, title: 'Nouvelle conversation',
    createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:00:00Z', messages: [],
  })
  vi.mocked(api.customAgentConversation).mockResolvedValue(savedConversation)
  vi.mocked(api.setCustomAgentWorkspace).mockImplementation(async (_agentId, _conversationId, workspace) => ({
    ...savedConversation, workspace,
  }))
  vi.mocked(api.decideCustomAgentProposal).mockResolvedValue(savedConversation)
  vi.mocked(api.sendCustomAgentMessage).mockResolvedValue({
    id: 'conversation-2', agentId: agent.id, title: 'Bonjour',
    createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:02:00Z',
    messages: [
      { role: 'user', content: 'Bonjour', createdAt: '2026-11-15T10:01:00Z' },
      { role: 'assistant', content: 'Réponse réelle du modèle.', createdAt: '2026-11-15T10:02:00Z' },
    ],
  })
  vi.mocked(api.deleteCustomAgentConversation).mockResolvedValue(undefined)
})

describe('CustomAgents', () => {
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
    expect(screen.getByText(/Le sélecteur WebView ne fournit pas de handle Windows/)).toBeVisible()
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
})
