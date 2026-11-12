import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, it, vi } from 'vitest'
import { ConfigurationEditor } from './ConfigurationEditor'
import { MissionDetails } from './MissionDetails'
import { Projects } from './Projects'
import { api } from './api'
import { mission } from './test/fixtures'
import type { AgentConfiguration, Mission } from './types'

vi.mock('./api', () => ({
  api: { configuration: vi.fn(), saveConfiguration: vi.fn(), resume: vi.fn(), files: vi.fn(), file: vi.fn(),
    renameProject: vi.fn(), deleteProject: vi.fn() },
  errorMessage: (error: unknown) => error instanceof Error ? error.message : 'Error',
}))
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.files).mockResolvedValue([])
})

const config: AgentConfiguration = {
  agents: [{ id: 'ReviewAgent', name: 'Reviewer', role: 'Review', enabled: true, description: 'Read only',
    modelId: 'coder', systemPrompt: 'Review observable defects', capabilities: ['review'], fallbackKeywords: [],
    allowedTools: ['ReadSources'], taskTypes: ['Project'], timeoutSeconds: 180 }],
  models: [{ id: 'coder', name: 'existing:7b', provider: 'Ollama', contextSize: 8192, maxOutputTokens: 3072, temperature: 0.2 }],
}

it('edits an agent without offering shell permissions and surfaces server validation', async () => {
  vi.mocked(api.configuration).mockResolvedValue(structuredClone(config))
  vi.mocked(api.saveConfiguration).mockRejectedValue(new Error('Mission active: configuration verrouillée'))
  render(<ConfigurationEditor mode="agents" saved={vi.fn()} />)
  await userEvent.click(screen.getByRole('button', { name: 'Configurer les agents' }))
  const name = await screen.findByRole('textbox', { name: 'Nom' })
  await userEvent.clear(name)
  await userEvent.type(name, 'Code reviewer')
  expect(screen.queryByRole('checkbox', { name: 'Shell' })).not.toBeInTheDocument()
  expect(screen.queryByRole('checkbox', { name: 'GenerateFiles' })).not.toBeInTheDocument()
  await userEvent.click(screen.getByRole('button', { name: 'Enregistrer la configuration' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('verrouillée')
  expect(api.saveConfiguration).toHaveBeenCalledWith(expect.objectContaining({
    agents: [expect.objectContaining({ name: 'Code reviewer', allowedTools: ['ReadSources'] })],
  }))
})

it('edits model budgets locally without downloading models', async () => {
  vi.mocked(api.configuration).mockResolvedValue(structuredClone(config))
  vi.mocked(api.saveConfiguration).mockResolvedValue(config)
  const saved = vi.fn().mockResolvedValue(undefined)
  render(<ConfigurationEditor mode="models" saved={saved} />)
  await userEvent.click(screen.getByRole('button', { name: 'Configurer les modèles' }))
  const budget = await screen.findByRole('spinbutton', { name: 'maxOutputTokens' })
  await userEvent.clear(budget); await userEvent.type(budget, '2048')
  await userEvent.click(screen.getByRole('button', { name: 'Enregistrer la configuration' }))
  await waitFor(() => expect(saved).toHaveBeenCalledOnce())
  expect(api.saveConfiguration).toHaveBeenCalledWith(expect.objectContaining({
    models: [expect.objectContaining({ maxOutputTokens: 2048 })],
  }))
})

const workflowMission: Mission = { ...mission, status: 'Failed', projectName: 'Calculator', workflow: {
  workflowId: 'workflow-id', missionId: mission.id, version: 1, plan: null, review: null,
  steps: [{ id: 'review', kind: 'Review', agentId: 'ReviewAgent', dependsOn: ['generate'], status: 'Failed',
    attempts: 1, durationMilliseconds: 1200, input: 'Plan and sources only', output: null, error: 'Invalid review JSON' }],
  revisions: [], finalReport: null,
} }

it('shows actual workflow context and permits controlled resumption', async () => {
  vi.mocked(api.resume).mockResolvedValue(undefined)
  const refresh = vi.fn().mockResolvedValue(undefined)
  render(<MissionDetails mission={workflowMission} cancel={vi.fn()} busy={false} refresh={refresh} />)
  await userEvent.click(screen.getByText(/Revue · ReviewAgent · Failed/))
  expect(screen.getByText('Contexte transmis : Plan and sources only')).toBeVisible()
  expect(screen.getByRole('alert')).toHaveTextContent('Invalid review JSON')
  await userEvent.click(screen.getByRole('button', { name: 'Reprendre · nouvelle autorisation requise' }))
  await waitFor(() => expect(api.resume).toHaveBeenCalledWith(mission.id))
  expect(refresh).toHaveBeenCalledOnce()
})

it('requires explicit project deletion confirmation and keeps errors visible', async () => {
  vi.mocked(api.deleteProject).mockRejectedValue(new Error('Suppression refusée'))
  render(<Projects missions={[workflowMission]} selectedId={mission.id} onSelect={vi.fn()} refresh={vi.fn()} />)
  await userEvent.click(screen.getByRole('button', { name: 'Supprimer le projet' }))
  expect(api.deleteProject).not.toHaveBeenCalled()
  await userEvent.click(screen.getByRole('button', { name: 'Confirmer la suppression' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Suppression refusée')
})
