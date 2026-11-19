import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { invoke } from '@tauri-apps/api/core'
import { api } from './api'
import { CustomAgentTeams } from './CustomAgentTeams'
import type {
  CustomAgent, CustomAgentCollaborationRun, CustomAgentDevelopmentPlan, CustomAgentTeam, LocalApplication, Mission,
} from './types'

vi.mock('./api', () => ({
  api: {
    customAgentTeams: vi.fn(),
    saveCustomAgentTeam: vi.fn(),
    customAgentTeamRuns: vi.fn(),
    customAgentConversation: vi.fn(),
    customAgentDevelopmentPlans: vi.fn(),
    createCustomAgentDevelopmentPlan: vi.fn(),
    extendCustomAgentDevelopmentPlan: vi.fn(),
    reviewDevelopmentCriterion: vi.fn(),
    startCustomAgentDevelopmentPlan: vi.fn(),
    pauseCustomAgentDevelopmentPlan: vi.fn(),
    resumeCustomAgentDevelopmentPlan: vi.fn(),
    cancelCustomAgentDevelopmentPlan: vi.fn(),
    retryCustomAgentDevelopmentTask: vi.fn(),
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
const developmentPlan: CustomAgentDevelopmentPlan = {
  id: 'plan-1', teamId: team.id, goal: 'Add task persistence', status: 'Ready',
  workspace: { kind: 'Studio', workspaceId: project.id, name: project.projectName! },
  createdAt: '2026-11-15T10:00:00Z', updatedAt: '2026-11-15T10:00:00Z',
  tasks: [{
    id: 'persist-tasks', title: 'Persist tasks', description: 'Save the task list in localStorage.',
    dependencies: [], acceptanceCriteria: ['Tasks survive a reload.'], status: 'Pending', order: 0,
    criterionReviews: [{ category: 'Unclassified', status: 'Unverified', evidence: null, reviewedAt: null,
      current: false, runId: null, reportId: null, scenarioId: null }],
    criterionReviewHistory: [],
    collaborationRunId: null, attempts: 0, summary: null, changedFiles: [], error: null, validation: null,
  }],
  currentTaskId: null, attempts: 0, maxAttempts: 6,
  memory: {
    architecture: 'React, TypeScript, Vite', completedFeatures: [], decisions: [],
    keyFiles: ['src/App.tsx'], knownIssues: [],
  },
  error: null,
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.customAgentTeams).mockResolvedValue([team])
  vi.mocked(api.customAgentTeamRuns).mockResolvedValue([])
  vi.mocked(api.customAgentDevelopmentPlans).mockResolvedValue([])
  vi.mocked(api.createCustomAgentDevelopmentPlan).mockResolvedValue(developmentPlan)
  vi.mocked(api.localApplication).mockResolvedValue(stopped)
  vi.mocked(api.startLocalApplication).mockResolvedValue(running)
  vi.mocked(api.stopLocalApplication).mockResolvedValue(stopped)
})

afterEach(() => vi.restoreAllMocks())

describe('CustomAgentTeams local application panel', () => {
  it('requires explicit team opt-in to route automatic agents and preserves escalation settings', async () => {
    const user = userEvent.setup()
    vi.mocked(api.saveCustomAgentTeam).mockResolvedValue({ ...team, automaticModelSelection: true })
    render(<CustomAgentTeams agents={agents} projects={[]} desktopApp
      onProjectCreated={vi.fn()} onOpenProject={vi.fn()} />)
    const toggle = await screen.findByLabelText('Autoriser le routage automatique des agents en mode automatique')
    expect(toggle).not.toBeChecked()
    await user.click(toggle)
    await user.click(screen.getByRole('button', { name: 'Enregistrer l’équipe' }))
    await waitFor(() => expect(api.saveCustomAgentTeam).toHaveBeenCalledWith(
      team.name, team.members.map(member => ({ ...member, fallbackModels: null })), team.id, false, true))
  })

  it('displays the actual model, next fallback and persisted escalation reason', async () => {
    const run: CustomAgentCollaborationRun = {
      id: 'run-models', teamId: team.id, task: 'Fix PUT integration', status: 'Failed',
      currentStep: null, workspace: null, contributions: [], proposal: null, error: 'TS18004',
      createdAt: team.createdAt, updatedAt: team.updatedAt,
      modelAttempts: [{
        id: 'attempt-2', runId: 'run-models', agentId: agents[1]!.id, role: 'Developer',
        modelName: 'qwen2.5-coder:7b', startedAt: team.createdAt, outcome: 'Failed',
        failureKind: 'Generation', compilationStatus: 'Failed',
        selectionReason: 'Configured model is the first choice.',
        decision: 'Two consecutive generation failures; select the next authorized model.',
        nextModel: 'qwen3.5:9b',
      }],
    }
    vi.mocked(api.customAgentTeamRuns).mockResolvedValue([run])
    render(<CustomAgentTeams agents={agents} projects={[]} desktopApp
      onProjectCreated={vi.fn()} onOpenProject={vi.fn()} />)
    const audit = await screen.findByRole('region', { name: 'Sélection des modèles' })
    expect(audit).toHaveTextContent('Modèle actif / dernier utilisé : qwen2.5-coder:7b')
    expect(audit).toHaveTextContent('Prochain modèle : qwen3.5:9b')
    expect(audit).toHaveTextContent('Two consecutive generation failures')
    expect(audit).toHaveTextContent('Compilation/tests : Failed')
  })

  it('keeps old teams opted out and saves only installed Developer defaults after explicit opt-in', async () => {
    const user = userEvent.setup()
    vi.mocked(api.saveCustomAgentTeam).mockImplementation(async (name, members, id, enabled) => ({
      ...team, id: id!, name, members, modelEscalationEnabled: enabled,
    }))
    const installed = ['qwen2.5-coder:7b', 'qwen3.5:9b']
    render(<CustomAgentTeams agents={agents.map((agent, index) => index === 1
      ? { ...agent, modelName: installed[0]! } : agent)} projects={[]} models={installed}
      desktopApp onProjectCreated={vi.fn()} onOpenProject={vi.fn()} />)
    const toggle = await screen.findByLabelText('Activer l’escalade automatique des modèles')
    expect(toggle).not.toBeChecked()
    await user.click(toggle)
    await user.click(screen.getByRole('button', { name: 'Enregistrer l’équipe' }))
    await waitFor(() => expect(api.saveCustomAgentTeam).toHaveBeenCalledWith(
      team.name, [
        { ...team.members[0], fallbackModels: [] },
        { ...team.members[1], fallbackModels: ['qwen3.5:9b'] },
        { ...team.members[2], fallbackModels: [] },
      ], team.id, true, false))
  })

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

  it('creates and displays a persisted development plan for the selected Studio project', async () => {
    const user = userEvent.setup()
    render(<CustomAgentTeams agents={agents} projects={[project]} desktopApp
      onProjectCreated={vi.fn()} onOpenProject={vi.fn()} />)

    await user.selectOptions(await screen.findByLabelText('Projet Studio'), project.id)
    await user.type(await screen.findByPlaceholderText(
      'Ex. Ajouter la persistance locale et tester le parcours de tâches.'), 'Add task persistence')
    await user.click(screen.getByRole('button', { name: 'Créer un plan avec le Planner' }))

    expect(api.createCustomAgentDevelopmentPlan).toHaveBeenCalled()
    expect(await screen.findByText(/Persist tasks/)).toBeInTheDocument()
    expect(screen.getByText(/Tasks survive a reload/)).toBeInTheDocument()
    expect(api.createCustomAgentDevelopmentPlan).toHaveBeenCalledWith(
      team.id, 'Add task persistence',
      { kind: 'Studio', workspaceId: project.id, name: project.projectName },
    )
  })

  it('loads project requests and extends the existing completed plan', async () => {
    const user = userEvent.setup()
    const completed = {
      ...developmentPlan, status: 'Completed', conversationId: 'conversation-1',
      conversationAgentId: agents[0]!.id,
      tasks: [{ ...developmentPlan.tasks[0]!, status: 'Completed', attempts: 1 }],
    }
    vi.mocked(api.customAgentDevelopmentPlans).mockResolvedValue([completed])
    vi.mocked(api.customAgentConversation).mockResolvedValue({
      id: 'conversation-1', agentId: agents[0]!.id, title: 'Initial request',
      createdAt: completed.createdAt, updatedAt: completed.updatedAt,
      messages: [{
        role: 'user', content: 'Initial request', createdAt: completed.createdAt,
        developmentPlanId: completed.id, developmentTaskIds: ['persist-tasks'],
      }],
    })
    vi.mocked(api.extendCustomAgentDevelopmentPlan).mockResolvedValue({ ...completed, status: 'Ready' })
    render(<CustomAgentTeams agents={agents} projects={[project]} desktopApp
      onProjectCreated={vi.fn()} onOpenProject={vi.fn()} />)

    await screen.findByText('Équipe de test')
    await user.selectOptions(await screen.findByLabelText('Projet Studio'), project.id)
    expect(await screen.findByText('Initial request', { exact: false })).toBeInTheDocument()
    await user.type(screen.getByLabelText('Ajouter une demande au plan'), 'Add search')
    await user.click(screen.getByRole('button', { name: /Étendre le plan/ }))
    expect(api.extendCustomAgentDevelopmentPlan).toHaveBeenCalledWith(team.id, completed.id, 'Add search')
    expect(screen.getByRole('button', { name: /Démarrer le plan/ })).toBeInTheDocument()
  })

  it('requires an explicit manual result and evidence for a completed criterion', async () => {
    const user = userEvent.setup()
    const completed = {
      ...developmentPlan, status: 'AwaitingValidation',
      tasks: [{ ...developmentPlan.tasks[0]!, status: 'Completed', attempts: 1 }],
    }
    vi.mocked(api.customAgentDevelopmentPlans).mockResolvedValue([completed])
    vi.mocked(api.reviewDevelopmentCriterion).mockResolvedValue(completed)
    vi.mocked(api.customAgentTeamRuns).mockResolvedValue([{
      id: 'run-1', teamId: team.id, task: 'Search', status: 'Completed', currentStep: null,
      workspace: completed.workspace, createdAt: completed.createdAt, updatedAt: completed.updatedAt,
      contributions: [], proposal: null, error: null,
      browserTest: { id: 'report-1', status: 'Passed', url: 'http://127.0.0.1:5173/',
        testedAt: completed.updatedAt, passed: 1, failed: 0, error: null,
        scenarios: [{ id: 'scenario-1', name: 'Search', status: 'Passed',
          failedStep: null, error: null, javaScriptErrors: [], screenshotId: null }] },
    }])
    render(<CustomAgentTeams agents={agents} projects={[project]} desktopApp
      onProjectCreated={vi.fn()} onOpenProject={vi.fn()} />)
    await screen.findByText('Équipe de test')
    await user.selectOptions(await screen.findByLabelText('Projet Studio'), project.id)
    await user.selectOptions(await screen.findByLabelText('Catégorie : Tasks survive a reload.'), 'User')
    await user.selectOptions(screen.getByLabelText('Résultat : Tasks survive a reload.'), 'Verified')
    const save = screen.getByRole('button', { name: 'Enregistrer ce critère' })
    expect(save).toBeDisabled()
    await user.type(screen.getByLabelText('Preuve : Tasks survive a reload.'), 'Test navigateur réussi')
    expect(save).toBeDisabled()
    await user.selectOptions(screen.getByLabelText('Scénario : Tasks survive a reload.'), 'run-1|report-1|scenario-1')
    await user.click(save)
    expect(api.reviewDevelopmentCriterion).toHaveBeenCalledWith(
      team.id, completed.id, 'persist-tasks', 0, 'User', 'Verified', 'Test navigateur réussi',
      'run-1', 'report-1', 'scenario-1')
  })
})
