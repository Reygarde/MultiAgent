import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import { api } from './api'
import { agents, mission, models } from './test/fixtures'

vi.mock('./api', () => ({
  api: {
    agents: vi.fn(), models: vi.fn(), missions: vi.fn(), localModels: vi.fn(),
    sandbox: vi.fn(), create: vi.fn(), cancel: vi.fn(), files: vi.fn(), file: vi.fn(),
    executionPlan: vi.fn(), approve: vi.fn(),
  },
  errorMessage: (error: unknown) => error instanceof Error ? error.message : 'Error',
}))

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.agents).mockResolvedValue(agents)
  vi.mocked(api.models).mockResolvedValue(models)
  vi.mocked(api.missions).mockResolvedValue([])
  vi.mocked(api.localModels).mockResolvedValue({ available: true, error: null,
    models: [{ name: 'custom:4b', installed: true, loaded: false, inUse: false }] })
  vi.mocked(api.sandbox).mockResolvedValue({ available: false, reason: 'Docker absent.' })
  vi.mocked(api.files).mockResolvedValue([{ path: 'src/App/Program.cs', size: 12 }])
  vi.mocked(api.file).mockResolvedValue({ path: 'src/App/Program.cs', content: 'Console.WriteLine("Hello");' })
})

describe('Studio dashboard', () => {
  it('distinguishes a running Docker engine from unavailable sandbox preparation and requires restart confirmation', async () => {
    vi.mocked(api.sandbox).mockResolvedValue({ available: false, engineAvailable: true, reason: 'Image manquante.' })
    const restart = vi.fn().mockResolvedValue(undefined)
    render(<App restartBackend={restart} />)
    const services = screen.getByRole('region', { name: 'État des services' })
    await waitFor(() => expect(services).toHaveTextContent('Docker : moteur prêt'))
    expect(services).toHaveTextContent('Image manquante.')
    await userEvent.click(screen.getByRole('button', { name: 'Relancer le backend' }))
    expect(restart).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Confirmer le redémarrage' }))
    expect(restart).toHaveBeenCalledOnce()
  })

  it('loads API agents dynamically and excludes the router from manual selection', async () => {
    render(<App />)
    const select = await screen.findByRole('combobox', { name: 'Sélection de l’agent' })
    await waitFor(() => expect(within(select).getByRole('option', { name: 'CustomCoder' })).toBeVisible())
    expect(within(select).queryByRole('option', { name: 'Dispatch' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Agents' }))
    expect(screen.getByRole('heading', { name: 'Dispatch' })).toBeVisible()
    expect(screen.getByRole('heading', { name: 'CustomCoder' })).toBeVisible()
    expect(screen.queryByText('DeveloperAgent')).not.toBeInTheDocument()
  })

  it('creates a project mission without allowing unavailable sandbox execution', async () => {
    vi.mocked(api.create).mockResolvedValue({ ...mission, status: 'Queued', response: null, hasWorkspace: false })
    render(<App />)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Envoyer la mission' })).toBeDisabled())
    await screen.findByText('Ollama connecté')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Mode de mission' }), 'Project')
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
    expect(screen.getByText(/Après génération, consultez les fichiers/)).toBeVisible()
    const selector = screen.getByRole('combobox', { name: 'Sélection de l’agent' })
    expect(within(selector).queryByRole('option', { name: 'CustomGeneral' })).not.toBeInTheDocument()
    await userEvent.selectOptions(selector, 'CustomCoder')
    await userEvent.type(screen.getByRole('textbox', { name: 'Votre mission' }), 'Créer une API')
    await userEvent.click(screen.getByRole('button', { name: 'Envoyer la mission' }))
    await waitFor(() => expect(api.create).toHaveBeenCalledWith({
      task: 'Créer une API', kind: 'Project', agentId: 'CustomCoder', approveSandboxExecution: false,
    }))
    expect(screen.getByRole('button', { name: 'Demander l’annulation' })).toBeVisible()
  })

  it('submits the Discord template without preapproving Docker', async () => {
    vi.mocked(api.create).mockResolvedValue({ ...mission, status: 'Queued', response: null, hasWorkspace: false })
    render(<App />)
    await screen.findByText('Ollama connecté')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Mode de mission' }), 'Project')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Template du projet' }), 'discord-bot-dotnet')
    expect(screen.getByText(/Discord.Net.WebSocket 3.20.2/)).toBeVisible()
    await userEvent.type(screen.getByRole('textbox', { name: 'Votre mission' }), 'Créer un bot Discord')
    await userEvent.click(screen.getByRole('button', { name: 'Envoyer la mission' }))
    await waitFor(() => expect(api.create).toHaveBeenCalledWith({
      task: 'Créer un bot Discord', kind: 'Project', agentId: null,
      approveSandboxExecution: false, templateId: 'discord-bot-dotnet',
    }))
  })

  it('shows a real request error rather than inserting a successful answer', async () => {
    vi.mocked(api.create).mockRejectedValue(new Error('Fournisseur indisponible'))
    render(<App />)
    await screen.findByText('Ollama connecté')
    await userEvent.type(screen.getByRole('textbox', { name: 'Votre mission' }), 'Bonjour')
    await userEvent.click(screen.getByRole('button', { name: 'Envoyer la mission' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Fournisseur indisponible')
    expect(screen.getByRole('textbox', { name: 'Votre mission' })).toHaveValue('Bonjour')
  })

  it('submits Twitch Bot without credentials or preapproved Docker execution', async () => {
    vi.mocked(api.create).mockResolvedValue({ ...mission, status: 'Queued', response: null, hasWorkspace: false })
    render(<App />)
    await screen.findByText('Ollama connecté')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Mode de mission' }), 'Project')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Template du projet' }), 'twitch-bot-dotnet')
    expect(screen.getByText(/Twitch Bot v1/)).toHaveTextContent('Release hors ligne après autorisation')
    expect(screen.queryByLabelText(/token|secret/i)).not.toBeInTheDocument()
    await userEvent.type(screen.getByRole('textbox', { name: 'Votre mission' }), 'Créer un bot Twitch')
    await userEvent.click(screen.getByRole('button', { name: 'Envoyer la mission' }))
    await waitFor(() => expect(api.create).toHaveBeenCalledWith({
      task: 'Créer un bot Twitch', kind: 'Project', agentId: null,
      approveSandboxExecution: false, templateId: 'twitch-bot-dotnet',
    }))
  })

  it('submits React App with approved dependencies but without Docker consent', async () => {
    vi.mocked(api.create).mockResolvedValue({ ...mission, status: 'Queued', response: null, hasWorkspace: false })
    render(<App />)
    await screen.findByText('Ollama connecté')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Mode de mission' }), 'Project')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Template du projet' }), 'react-typescript-vite')
    expect(screen.getByText(/React App v1/)).toHaveTextContent('lockfile figés')
    await userEvent.type(screen.getByRole('textbox', { name: 'Votre mission' }), 'Créer un gestionnaire de tâches')
    await userEvent.click(screen.getByRole('button', { name: 'Envoyer la mission' }))
    await waitFor(() => expect(api.create).toHaveBeenCalledWith({
      task: 'Créer un gestionnaire de tâches', kind: 'Project', agentId: null,
      approveSandboxExecution: false, templateId: 'react-typescript-vite',
    }))
  })

  it('submits the full-stack template with its backend specialist and no Docker consent', async () => {
    vi.mocked(api.create).mockResolvedValue({ ...mission, status: 'Queued', response: null, hasWorkspace: false })
    render(<App />)
    await screen.findByText('Ollama connecté')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Mode de mission' }), 'Project')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Template du projet' }), 'fullstack-react-dotnet')
    expect(screen.getByText(/Contrat OpenAPI partagé/)).toBeVisible()
    const agent = screen.getByRole('combobox', { name: 'Sélection de l’agent' })
    expect(within(agent).getByRole('option', { name: 'BackendAgent' })).toBeVisible()
    expect(within(agent).queryByRole('option', { name: 'FrontendAgent' })).not.toBeInTheDocument()
    await userEvent.type(screen.getByRole('textbox', { name: 'Votre mission' }), 'Créer une app full-stack')
    await userEvent.click(screen.getByRole('button', { name: 'Envoyer la mission' }))
    await waitFor(() => expect(api.create).toHaveBeenCalledWith({
      task: 'Créer une app full-stack', kind: 'Project', agentId: null,
      approveSandboxExecution: false, templateId: 'fullstack-react-dotnet',
    }))
  })

  it('distinguishes installed loaded and in-use model states', async () => {
    render(<App />)
    await screen.findByText('Ollama connecté')
    await userEvent.click(screen.getByRole('button', { name: 'Models' }))
    expect(screen.getByText('Installé : oui')).toBeVisible()
    expect(screen.getByText('Chargé : non')).toBeVisible()
    expect(screen.getByText('Utilisé : non')).toBeVisible()
  })

  it('explores generated files and never claims unexecuted builds succeeded', async () => {
    vi.mocked(api.missions).mockResolvedValue([mission])
    render(<App />)
    expect(await screen.findByText('Fichiers disponibles. Compilation et tests NON exécutés.')).toBeVisible()
    await userEvent.click(screen.getByRole('button', { name: 'Projects' }))
    await waitFor(() => expect(document.querySelector('.file-preview code')).toHaveTextContent('Console.WriteLine("Hello");'))
    expect(screen.getByText('Build · Unavailable · sortie non exécuté')).toBeVisible()
    expect(api.file).toHaveBeenCalledWith(mission.id, 'src/App/Program.cs', expect.any(AbortSignal))
  })

  it('requests cancellation for an active mission', async () => {
    vi.mocked(api.missions).mockResolvedValue([{ ...mission, status: 'Running' }])
    vi.mocked(api.cancel).mockResolvedValue(undefined)
    render(<App />)
    await userEvent.click(await screen.findByRole('button', { name: 'Demander l’annulation' }))
    await waitFor(() => expect(api.cancel).toHaveBeenCalledWith(mission.id))
  })

  it('does not erase actual earlier verification attempts when isolation becomes unavailable', async () => {
    vi.mocked(api.missions).mockResolvedValue([{ ...mission, verification: [
      { stage: 'Build', status: 'Failed', exitCode: 1, totalTests: null, attempt: 1, log: 'CS1002' },
      { stage: 'Build', status: 'Unavailable', exitCode: null, totalTests: null, log: 'Docker unavailable' },
    ] }])
    render(<App />)
    expect(await screen.findByText(/Vérification non aboutie/)).toBeVisible()
    expect(screen.queryByText('Fichiers disponibles. Compilation et tests NON exécutés.')).not.toBeInTheDocument()
  })

  it('opens the workspace belonging to an older conversation message', async () => {
    const older = { ...mission, id: '22222222-2222-2222-2222-222222222222', task: 'Ancien projet' }
    vi.mocked(api.missions).mockResolvedValue([mission, older])
    render(<App />)
    const olderMessage = await screen.findByRole('button', { name: /Ancien projet/ })
    const article = olderMessage.closest('article')!
    await userEvent.click(within(article).getByRole('button', { name: 'Explorer les fichiers' }))
    await waitFor(() => expect(api.files).toHaveBeenCalledWith(older.id, expect.any(AbortSignal)))
  })

  it.each([true, false])('reviews the scoped plan and submits human decision %s', async approved => {
    vi.mocked(api.missions).mockResolvedValue([{ ...mission, status: 'AwaitingApproval' }])
    vi.mocked(api.executionPlan).mockResolvedValue({
      fingerprint: 'reviewed-files-hash', image: 'sha256:pinned', memoryMegabytes: 2048,
      cpuCount: 2, timeoutSeconds: 120, maxCorrectionAttempts: 3,
      operations: ['Offline build and tests only'],
    })
    vi.mocked(api.approve).mockResolvedValue(undefined)
    render(<App />)
    expect(await screen.findByText('Offline build and tests only')).toBeVisible()
    expect(screen.getByText(/3 corrections maximum/)).toBeVisible()
    expect(api.approve).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', {
      name: approved ? 'Autoriser compilation et tests' : 'Refuser l’exécution',
    }))
    await waitFor(() => expect(api.approve).toHaveBeenCalledWith(mission.id, approved, 'reviewed-files-hash'))
  })

  it('surfaces stale approval and requires reviewing a fresh plan', async () => {
    vi.mocked(api.missions).mockResolvedValue([{ ...mission, status: 'AwaitingApproval' }])
    vi.mocked(api.executionPlan).mockResolvedValue({
      fingerprint: 'old', image: 'fixed', memoryMegabytes: 2048, cpuCount: 2,
      timeoutSeconds: 120, maxCorrectionAttempts: 3, operations: ['Offline build'],
    })
    vi.mocked(api.approve).mockRejectedValue(new Error('Plan modifié HTTP 409'))
    render(<App />)
    await userEvent.click(await screen.findByRole('button', { name: 'Autoriser compilation et tests' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Plan modifié HTTP 409')
    expect(screen.queryByRole('button', { name: 'Autoriser compilation et tests' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Recharger le plan' })).toBeVisible()
  })
})
