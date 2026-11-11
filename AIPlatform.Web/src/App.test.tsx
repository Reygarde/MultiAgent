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
    expect(screen.getByRole('checkbox')).toBeDisabled()
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

  it('shows a real request error rather than inserting a successful answer', async () => {
    vi.mocked(api.create).mockRejectedValue(new Error('Fournisseur indisponible'))
    render(<App />)
    await screen.findByText('Ollama connecté')
    await userEvent.type(screen.getByRole('textbox', { name: 'Votre mission' }), 'Bonjour')
    await userEvent.click(screen.getByRole('button', { name: 'Envoyer la mission' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Fournisseur indisponible')
    expect(screen.getByRole('textbox', { name: 'Votre mission' })).toHaveValue('Bonjour')
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
    expect(await screen.findByText('Console.WriteLine("Hello");')).toBeVisible()
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

  it('opens the workspace belonging to an older conversation message', async () => {
    const older = { ...mission, id: '22222222-2222-2222-2222-222222222222', task: 'Ancien projet' }
    vi.mocked(api.missions).mockResolvedValue([mission, older])
    render(<App />)
    const olderMessage = await screen.findByRole('button', { name: /Ancien projet/ })
    const article = olderMessage.closest('article')!
    await userEvent.click(within(article).getByRole('button', { name: 'Explorer les fichiers' }))
    await waitFor(() => expect(api.files).toHaveBeenCalledWith(older.id, expect.any(AbortSignal)))
  })
})
