import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, it, vi } from 'vitest'
import { ConfigurationEditor } from './ConfigurationEditor'
import { MissionDetails } from './MissionDetails'
import { Projects } from './Projects'
import { api } from './api'
import { mission } from './test/fixtures'
import type { AgentConfiguration, Mission, MissionStatus } from './types'

vi.mock('./api', () => ({
  api: { configuration: vi.fn(), saveConfiguration: vi.fn(), resume: vi.fn(), revalidate: vi.fn(), files: vi.fn(), file: vi.fn(),
    renameProject: vi.fn(), deleteProject: vi.fn(), revision: vi.fn(), exportProject: vi.fn(), analysis: vi.fn(),
    executionPlan: vi.fn(), approve: vi.fn() },
  errorMessage: (error: unknown) => error instanceof Error ? error.message : 'Error',
}))
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.files).mockResolvedValue([])
  vi.mocked(api.executionPlan).mockResolvedValue({ fingerprint: 'approved', image: 'offline',
    operations: [], cpuCount: 2, memoryMegabytes: 2048, timeoutSeconds: 120, maxCorrectionAttempts: 3 })
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
  await userEvent.click(screen.getByText(/Revue · ReviewAgent · état enregistré : Failed/))
  expect(screen.getByText('Contexte transmis : Plan and sources only')).toBeVisible()
  expect(screen.getByRole('alert')).toHaveTextContent('Invalid review JSON')
  await userEvent.click(screen.getByRole('button', { name: 'Reprendre · nouvelle autorisation requise' }))
  await waitFor(() => expect(api.resume).toHaveBeenCalledWith(mission.id))
  expect(refresh).toHaveBeenCalledOnce()
})

it('shows React template and separate TypeScript results without claiming verification', () => {
  render(<MissionDetails mission={{ ...workflowMission, templateId: 'react-typescript-vite',
    verification: [{ stage: 'TypeScript', status: 'Failed', exitCode: 2, totalTests: null, log: 'TS2304' }] }}
    cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
  expect(screen.getByText(/Template : React App v1/)).toBeVisible()
  expect(screen.getByText(/Vérification TypeScript · Failed/)).toBeVisible()
  expect(screen.queryByText('Vérifié')).not.toBeInTheDocument()
})

it('prepares an infrastructure validation without resuming generation or approving Docker', async () => {
  const plan = { fingerprint: 'new-infrastructure-fingerprint', image: 'sha256:approved-offline-image',
    operations: ['Release/xUnit', 'TypeScript/Vite/Vitest', 'Real HTTP integration', 'No automatic LLM correction.'],
    cpuCount: 2, memoryMegabytes: 2048, timeoutSeconds: 120, maxCorrectionAttempts: 3 }
  vi.mocked(api.revalidate).mockResolvedValue(plan)
  vi.mocked(api.executionPlan).mockResolvedValue(plan)
  const refresh = vi.fn().mockResolvedValue(undefined)
  const pending: Mission = { ...workflowMission, status: 'GeneratedUnverified', templateId: 'fullstack-react-dotnet',
    workflow: { ...workflowMission.workflow!, review: { summary: 'Previously reviewed.', findings: [] } } }
  const { rerender } = render(<MissionDetails mission={pending} cancel={vi.fn()} busy={false} refresh={refresh} />)

  await userEvent.click(screen.getByRole('button', { name: 'Préparer une nouvelle validation · sans régénération' }))

  await waitFor(() => expect(refresh).toHaveBeenCalledOnce())
  expect(api.revalidate).toHaveBeenCalledWith(mission.id)
  expect(api.resume).not.toHaveBeenCalled()
  expect(api.approve).not.toHaveBeenCalled()
  rerender(<MissionDetails mission={{ ...pending, status: 'AwaitingApproval' }}
    cancel={vi.fn()} busy={false} refresh={refresh} />)
  expect(await screen.findByText('Real HTTP integration')).toBeVisible()
  expect(screen.getByText(/Aucune correction automatique/)).toBeVisible()
  await userEvent.click(screen.getByText('Image et empreinte approuvées'))
  expect(screen.getByText(/new-infrastructure-fingerprint/)).toBeVisible()
  expect(screen.getByRole('button', { name: 'Autoriser compilation et tests' })).toBeEnabled()
  expect(api.approve).not.toHaveBeenCalled()
})

it('shows the reviewed canonical plan without approving it', async () => {
  vi.mocked(api.executionPlan).mockResolvedValue({
    fingerprint: 'canonical-fingerprint', image: 'sha256:image', cpuCount: 2, memoryMegabytes: 2048,
    timeoutSeconds: 120, maxCorrectionAttempts: 0, operations: ['No automatic LLM correction.'],
    canonical: { workflowVersion: 7, automaticCorrections: false,
      files: [{ path: 'backend/Api/TaskService.cs', sha256: 'file-hash' }],
      stages: [{ name: 'BackendBuild', arguments: ['run', '--network=none'] },
        { name: 'Integration', arguments: ['run', '--network=none'] }] },
  })
  render(<MissionDetails mission={{ ...workflowMission, status: 'AwaitingApproval',
    templateId: 'fullstack-react-dotnet', workflow: { ...workflowMission.workflow!, version: 7 } }}
    cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
  await userEvent.click(await screen.findByText('Plan canonique · version 7'))
  expect(screen.getByText('Étapes obligatoires : BackendBuild → Integration')).toBeVisible()
  await userEvent.click(screen.getByText('Empreintes des fichiers (1)'))
  expect(screen.getByText('backend/Api/TaskService.cs · file-hash')).toBeVisible()
  expect(api.approve).not.toHaveBeenCalled()
})

it('reloads the approval plan when the workflow version changes', async () => {
  const basePlan = { image: 'sha256:image', cpuCount: 2, memoryMegabytes: 2048, timeoutSeconds: 120,
    maxCorrectionAttempts: 0 }
  vi.mocked(api.executionPlan)
    .mockResolvedValueOnce({ ...basePlan, fingerprint: 'old', operations: ['Old policy'] })
    .mockResolvedValueOnce({ ...basePlan, fingerprint: 'new', operations: ['No automatic LLM correction.'] })
  const pending: Mission = { ...workflowMission, status: 'AwaitingApproval',
    templateId: 'fullstack-react-dotnet', workflow: { ...workflowMission.workflow!, version: 6 } }
  const { rerender } = render(<MissionDetails mission={pending} cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
  expect(await screen.findByText('Old policy')).toBeVisible()
  rerender(<MissionDetails mission={{ ...pending, workflow: { ...pending.workflow!, version: 7 } }}
    cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
  expect(screen.queryByText('Old policy')).not.toBeInTheDocument()
  expect(await screen.findByText('No automatic LLM correction.')).toBeVisible()
  expect(api.executionPlan).toHaveBeenCalledTimes(2)
  expect(api.approve).not.toHaveBeenCalled()
})

it('shows Twitch dependencies and offline limits without claiming live validation', () => {
  render(<MissionDetails mission={{ ...workflowMission, templateId: 'twitch-bot-dotnet' }}
    cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
  expect(screen.getByText(/Template : Twitch Bot v1/)).toHaveTextContent('Release sans réseau ni compte Twitch')
  expect(screen.getByText(/Template : Twitch Bot v1/)).toHaveTextContent('Connexion réelle non validée')
  expect(screen.queryByText('Vérifié')).not.toBeInTheDocument()
})

it.each([
  { templateId: 'twitch-bot-dotnet', version: 6, failedKind: 'Tests' },
  { templateId: 'react-typescript-vite', version: 5, failedKind: 'Build' },
])('separates $templateId current validation from historical approvals without altering evidence', async ({ templateId, version, failedKind }) => {
  const events: Mission['events'] = [
    { sequence: 1, timestamp: mission.createdAt, step: 'Queue', status: 'Running', message: 'Initial queue' },
    { sequence: 2, timestamp: mission.createdAt, step: 'Approval', status: 'Running', message: 'Approval v1 started' },
    { sequence: 3, timestamp: mission.createdAt, step: 'Authorization', status: 'Succeeded', message: 'Approved v1' },
    { sequence: 4, timestamp: mission.createdAt, step: failedKind, status: 'Failed', message: 'Earlier validation failed' },
    { sequence: 5, timestamp: mission.createdAt, step: 'Correct', status: 'Succeeded', message: 'Correction proposed' },
    { sequence: 6, timestamp: mission.createdAt, step: 'Approval', status: 'Running', message: 'Approval v3 started' },
    { sequence: 7, timestamp: mission.createdAt, step: 'Authorization', status: 'Cancelled', message: 'Cancelled v3' },
    { sequence: 8, timestamp: mission.createdAt, step: 'Queue', status: 'Running', message: 'Explicit resumption' },
    { sequence: 9, timestamp: mission.createdAt, step: 'Review', status: 'Succeeded', message: 'Resumed review' },
    { sequence: 10, timestamp: mission.createdAt, step: 'Approval', status: 'Running', message: 'Current approval started' },
    { sequence: 11, timestamp: mission.createdAt, step: 'Authorization', status: 'Succeeded', message: 'Current approval granted' },
    { sequence: 12, timestamp: mission.updatedAt, step: 'Mission', status: 'Succeeded', message: 'Verified by real validation' },
  ]
  const steps: NonNullable<Mission['workflow']>['steps'] = [
    { id: 'approval-1', kind: 'Approval', version: 1, status: 'Completed', agentId: 'OrchestratorAgent',
      dependsOn: [], attempts: 1, input: null, output: 'Human approved fingerprint v1', error: null, durationMilliseconds: 1000 },
    { id: `${failedKind.toLowerCase()}-1`, kind: failedKind, version: 1, status: 'Failed', agentId: 'TestAgent',
      dependsOn: ['approval-1'], attempts: 1, input: null, output: 'Old failure', error: null, durationMilliseconds: 2000 },
    { id: 'approval-3', kind: 'Approval', version: 3, status: 'Cancelled', agentId: 'OrchestratorAgent',
      dependsOn: [], attempts: 1, input: null, output: null, error: null, durationMilliseconds: 3000 },
    { id: `approval-${version}`, kind: 'Approval', version, status: 'Completed', agentId: 'OrchestratorAgent',
      dependsOn: [], attempts: 1, input: null, output: 'Human approved current fingerprint', error: null, durationMilliseconds: 4000 },
    { id: `tests-${version}`, kind: 'Tests', version, status: 'Completed', agentId: 'TestAgent',
      dependsOn: [`approval-${version}`], attempts: 1, input: null, output: 'Passed', error: null, durationMilliseconds: 5000 },
  ]
  const verified: Mission = { ...workflowMission, status: 'Verified', templateId, sandboxApproved: true, events,
    correctionAttempts: 3, verification: [{ stage: 'Tests', status: 'Succeeded', exitCode: 0, totalTests: 31, log: 'Original TRX' }],
    workflow: { ...workflowMission.workflow!, version, steps, proposals: [2, 3, version - 1].map(number =>
      ({ version: number, createdAt: mission.createdAt, review: null })),
      revisions: [1, 3, version].map(number => ({ version: number, createdAt: mission.createdAt,
        reason: 'Preserved revision', fingerprint: `original-${number}`, files: ['src/App/Program.cs'] })) } }
  const before = JSON.stringify(verified)
  render(<MissionDetails mission={verified} cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
  const chronology = screen.getByRole('region', { name: 'Chronologie' })
  const current = within(chronology).getByRole('list', { name: `Version actuelle ${version}` })
  expect(within(current).getByText(`Autorisation · v${version}`).closest('li')).toHaveTextContent('Autorisation accordée')
  expect(within(current).getByText(`Tests · v${version}`).closest('li')).toHaveTextContent('Réussi')
  await userEvent.click(within(chronology).getByText('Anciennes étapes et attentes historiques'))
  const historical = within(chronology).getByRole('list', { name: 'Étapes historiques' })
  expect(within(historical).getByText('Autorisation · v1').closest('li')).toHaveTextContent('Autorisation accordée')
  expect(within(historical).getByText('Autorisation · v3').closest('li')).toHaveTextContent('Annulé')
  expect(within(historical).getByText(`${failedKind === 'Tests' ? 'Tests' : 'Compilation'} · v1`).closest('li')).toHaveTextContent('Échec')
  expect(within(historical).getAllByText('Attente passée · activité enregistrée ensuite')).toHaveLength(2)
  expect(chronology.querySelectorAll('.step-Running')).toHaveLength(0)
  await userEvent.click(screen.getByText(`Journal technique · ${events.length} événements`))
  for (const event of events) expect(screen.getByText(event.message, { exact: false })).toBeVisible()
  expect(JSON.stringify(verified)).toBe(before)
  expect(api.approve).not.toHaveBeenCalled()
  expect(api.resume).not.toHaveBeenCalled()
})

it.each(['Verified', 'Failed', 'Cancelled'] satisfies MissionStatus[])('archives unclosed Calculator events for %s without inventing a successful result', status => {
  render(<MissionDetails mission={{ ...mission, status, events: [
    { sequence: 1, timestamp: mission.createdAt, step: 'Generation', status: 'Running', message: 'Started generation' },
    { sequence: 2, timestamp: mission.updatedAt, step: 'Mission', status: 'Failed', message: 'Recorded failure' },
  ] }} cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
  const chronology = screen.getByRole('region', { name: 'Chronologie' })
  expect(within(chronology).getByText('Génération').closest('li')).toHaveTextContent('Historique · clôture non enregistrée')
  expect(within(chronology).getByText('Mission').closest('li')).toHaveTextContent('Échec')
  expect(chronology.querySelectorAll('.step-Running')).toHaveLength(0)
})

it.each([
  { status: 'Running', stepStatus: 'Running', label: 'En cours' },
  { status: 'AwaitingApproval', stepStatus: 'WaitingForApproval', label: 'Autorisation requise' },
] as const)('keeps only the actual current operation active for $status', async ({ status, stepStatus, label }) => {
  const base = workflowMission.workflow!.steps[0]
  render(<MissionDetails mission={{ ...workflowMission, status, workflow: { ...workflowMission.workflow!, version: 6,
    steps: [
      { ...base, id: 'approval-1', kind: 'Approval', status: 'Running', version: 1 },
      { ...base, id: 'approval-6', kind: 'Approval', status: stepStatus, version: 6 },
    ] } }} cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
  const chronology = screen.getByRole('region', { name: 'Chronologie' })
  await userEvent.click(within(chronology).getByText('Anciennes étapes et attentes historiques'))
  expect(chronology.querySelectorAll('.step-Running')).toHaveLength(1)
  expect(within(chronology).getByRole('list', { name: 'Version actuelle 6' })).toHaveTextContent(label)
  expect(within(chronology).getByRole('list', { name: 'Étapes historiques' })).not.toHaveTextContent('En cours')
})

it('does not present even a stale current workflow Running flag as active on Verified', () => {
  render(<MissionDetails mission={{ ...workflowMission, status: 'Verified', workflow: { ...workflowMission.workflow!,
    steps: [{ ...workflowMission.workflow!.steps[0], status: 'Running', version: 1 }] } }}
    cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
  const chronology = screen.getByRole('region', { name: 'Chronologie' })
  expect(chronology.querySelectorAll('.step-Running')).toHaveLength(0)
  expect(within(chronology).getByRole('list', { name: 'Version actuelle 1' })).toHaveTextContent('Historique · état enregistré : Running')
})

it('shows persisted diagnostics and before/after changes on demand', async () => {
  vi.mocked(api.revision).mockResolvedValue({ version: 2, previousVersion: 1, proposed: true,
    review: { summary: 'Défaut restant', findings: [], recommendation: 'À corriger' },
    changes: [{ path: 'src/App/Program.cs', before: 'return a + b;', after: 'return a - b;' }] })
  const withRevision: Mission = { ...workflowMission, correctionAttempts: 1,
    workflow: { ...workflowMission.workflow!, correctionLimit: 3,
      proposals: [{ version: 2, createdAt: '', review: null }] },
    verification: [{ stage: 'Tests', status: 'Failed', exitCode: 1, log: 'test failed', totalTests: 2,
      diagnostics: { stage: 'Tests', command: 'dotnet test', exitCode: 1, excerpt: 'failed',
        items: [{ type: 'Test', project: 'tests/App.Tests/App.Tests.csproj', file: 'tests/App.Tests/UnitTests.cs',
          line: 8, column: null, code: null, message: 'Expected: 2' }] } }] }
  render(<MissionDetails mission={withRevision} cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
  expect(screen.getByText(/restantes : 2/)).toBeInTheDocument()
  await userEvent.click(screen.getByRole('button', { name: 'Comparer la version 2' }))
  expect(await screen.findByText(/proposition non appliquée/)).toBeVisible()
  expect(screen.getByText(/Défaut restant/)).toBeVisible()
  await userEvent.click(await screen.findByText('src/App/Program.cs'))
  expect(screen.getByText('return a + b;')).toBeVisible()
  expect(screen.getByText('return a - b;')).toBeVisible()
  await userEvent.click(screen.getByText(/Tests · Failed · tentative/))
  expect(screen.getByText(/Expected: 2/)).toBeVisible()
})

it('requires explicit project deletion confirmation and keeps errors visible', async () => {
  vi.mocked(api.deleteProject).mockRejectedValue(new Error('Suppression refusée'))
  render(<Projects missions={[workflowMission]} selectedId={mission.id} onSelect={vi.fn()} refresh={vi.fn()} />)
  await userEvent.click(screen.getByRole('button', { name: 'Supprimer le projet' }))
  expect(api.deleteProject).not.toHaveBeenCalled()
  await userEvent.click(screen.getByRole('button', { name: 'Confirmer la suppression' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Suppression refusée')
})

it('surfaces export refusal rather than downloading a partial archive', async () => {
  vi.mocked(api.exportProject).mockRejectedValue(new Error('Secret détecté'))
  render(<Projects missions={[workflowMission]} selectedId={mission.id} onSelect={vi.fn()} refresh={vi.fn()} />)
  await userEvent.click(screen.getByRole('button', { name: 'Exporter en ZIP' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Secret détecté')
  expect(api.exportProject).toHaveBeenCalledWith(mission.id)
})

it('shows static analysis without claiming Git was inspected', async () => {
  vi.mocked(api.analysis).mockResolvedValue({ gitRepositoryDetected: null, languages: ['C#'], frameworks: ['net10.0'],
    solutions: [], projects: ['src/App/App.csproj'], packageManifests: [], testProjects: [], entryPoints: [],
    directories: [], dependencies: [{ ecosystem: 'NuGet', name: 'xunit', version: '2.9.3', manifest: 'tests/App.Tests/App.Tests.csproj' }],
    scripts: [], dockerFiles: [], issues: [] })
  render(<Projects missions={[workflowMission]} selectedId={mission.id} onSelect={vi.fn()} refresh={vi.fn()} />)
  await userEvent.click(screen.getByRole('button', { name: 'Analyser le projet' }))
  expect(await screen.findByText('Git : non vérifié')).toBeVisible()
  expect(screen.getByText('Frameworks : net10.0')).toBeVisible()
  expect(screen.getByText(/xunit \(NuGet, 2.9.3\)/)).toBeVisible()
  expect(api.analysis).toHaveBeenCalledWith(mission.id, expect.any(AbortSignal))
})

it('downloads a successful archive through the authenticated API transport', async () => {
  const blob = new Blob(['zip'], { type: 'application/zip' })
  vi.mocked(api.exportProject).mockResolvedValue(blob)
  const create = vi.fn().mockReturnValue('blob:project')
  const revoke = vi.fn()
  vi.stubGlobal('URL', { createObjectURL: create, revokeObjectURL: revoke })
  let clicked: HTMLAnchorElement | undefined
  const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { clicked = this })
  try {
    render(<Projects missions={[workflowMission]} selectedId={mission.id} onSelect={vi.fn()} refresh={vi.fn()} />)
    await userEvent.click(screen.getByRole('button', { name: 'Exporter en ZIP' }))
    await waitFor(() => expect(click).toHaveBeenCalledOnce())
    expect(api.exportProject).toHaveBeenCalledWith(mission.id)
    expect(create).toHaveBeenCalledWith(blob)
    expect(clicked?.download).toBe(`aiplatform-${mission.id}.zip`)
    await waitFor(() => expect(revoke).toHaveBeenCalledWith('blob:project'), { timeout: 2000 })
  } finally {
    click.mockRestore()
    vi.unstubAllGlobals()
  }
})
