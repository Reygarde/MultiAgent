import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { invoke } from '@tauri-apps/api/core'
import { api } from './api'
import { CustomAgentTeams } from './CustomAgentTeams'
import type { CustomAgent, CustomAgentTeam, LocalApplication, Mission } from './types'

vi.mock('./api', () => ({
  api: {
    customAgentTeams: vi.fn(),
    customAgentTeamRuns: vi.fn(),
    localApplication: vi.fn(),
    startLocalApplication: vi.fn(),
    stopLocalApplication: vi.fn(),
  },
  errorMessage: (error: unknown) => error instanceof Error ? error.message : 'Erreur',
}))

vi.mock('@tauri-apps/api/core', () => ({ invoke: vi.fn() }))

const agents: CustomAgent[] = ['Planner', 'Developer', 'Reviewer'].map((name, index) => ({
  id: `agent-${index}`, name, description: '', systemPrompt: '', modelName: 'qwen3:4b',
  createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:00:00Z',
}))
const team: CustomAgentTeam = {
  id: 'team-1', name: 'Équipe de test',
  members: agents.map((agent, order) => ({
    agentId: agent.id, role: (['Planner', 'Developer', 'Reviewer'] as const)[order]!, order,
  })),
  createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:00:00Z',
}
const project: Mission = {
  id: 'project-1', task: 'Agent project: Task manager', kind: 'Project', requestedAgent: null,
  sandboxApproved: false, status: 'Completed', createdAt: '2026-11-15T10:00:00Z',
  updatedAt: '2026-11-15T10:00:00Z', selectedAgent: null, selectedModel: null,
  reason: null, usedFallback: false, response: null, error: null, hasWorkspace: true,
  correctionAttempts: 0, events: [], verification: [], projectName: 'Task manager',
  deleted: false, templateId: 'custom-agent-react', archived: false,
}
const stopped: LocalApplication = {
  workspaceId: project.id, status: 'Stopped', url: null, command: null, output: [],
  error: null, processId: null, updatedAt: '2026-11-15T10:00:00Z',
}
const running: LocalApplication = {
  ...stopped, status: 'Running', url: 'http://127.0.0.1:4175/', command: 'npm run dev',
  output: ['Local: http://127.0.0.1:4175/'], processId: 1234,
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.customAgentTeams).mockResolvedValue([team])
  vi.mocked(api.customAgentTeamRuns).mockResolvedValue([])
  vi.mocked(api.localApplication).mockResolvedValue(stopped)
  vi.mocked(api.startLocalApplication).mockResolvedValue(running)
  vi.mocked(api.stopLocalApplication).mockResolvedValue(stopped)
})

afterEach(() => vi.restoreAllMocks())

describe('CustomAgentTeams local application panel', () => {
  it('starts only after explicit trust confirmation, opens the detected URL, and stops the server', async () => {
    const user = userEvent.setup()
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    vi.mocked(api.localApplication)
      .mockResolvedValueOnce(stopped)
      .mockResolvedValue(running)
    render(<CustomAgentTeams agents={agents} projects={[project]} desktopApp
      onProjectCreated={vi.fn()} onOpenProject={vi.fn()} />)

    await user.selectOptions(await screen.findByLabelText('Projet Studio'), project.id)
    await screen.findByText('Arrêtée')
    await user.click(screen.getByRole('button', { name: /Démarrer l’application/ }))
    await screen.findByRole('button', { name: /Ouvrir dans le navigateur/ })
    expect(api.startLocalApplication).toHaveBeenCalledWith(project.id)
    expect(confirm).toHaveBeenCalledOnce()

    await user.click(screen.getByRole('button', { name: /Ouvrir dans le navigateur/ }))
    await waitFor(() => expect(invoke).toHaveBeenCalledWith('open_local_application', {
      url: running.url,
    }))
    expect(screen.getByLabelText('Sorties du serveur local')).toHaveTextContent('127.0.0.1:4175')

    await user.click(screen.getByRole('button', { name: /Arrêter l’application/ }))
    await waitFor(() => expect(api.stopLocalApplication).toHaveBeenCalledWith(project.id))
  })

  it('does not request server execution when the trust confirmation is refused', async () => {
    const user = userEvent.setup()
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<CustomAgentTeams agents={agents} projects={[project]} desktopApp
      onProjectCreated={vi.fn()} onOpenProject={vi.fn()} />)

    await user.selectOptions(await screen.findByLabelText('Projet Studio'), project.id)
    await screen.findByText('Arrêtée')
    await user.click(screen.getByRole('button', { name: /Démarrer l’application/ }))

    expect(api.startLocalApplication).not.toHaveBeenCalled()
  })
})
