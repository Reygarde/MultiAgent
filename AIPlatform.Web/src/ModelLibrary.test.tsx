import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from './api'
import { ModelLibrary } from './ModelLibrary'
import { modelLibraryApi, type ModelLibrarySnapshot } from './modelLibraryApi'

vi.mock('./api', () => ({ api: { customAgents: vi.fn(), updateCustomAgent: vi.fn() },
  errorMessage: (error: unknown) => error instanceof Error ? error.message : 'Erreur' }))
vi.mock('./modelLibraryApi', () => ({ modelLibraryApi: {
  snapshot: vi.fn(), downloads: vi.fn(), verify: vi.fn(), install: vi.fn(), cancel: vi.fn(),
  remove: vi.fn(), preview: vi.fn(), resetStatistics: vi.fn(),
  saveSpecializations: vi.fn(), qualify: vi.fn(), clearQualification: vi.fn(),
} }))

const compatibility = { status: 'Compatible', reason: 'Mémoire simulée', contextTokens: 4096,
  weightsBytes: 4e9, kvBytes: 512e6, buffersBytes: 512e6, requiredRamBytes: 1.5e9, requiredVramBytes: 5e9, placement: 'GPU' } as const
const snapshot: ModelLibrarySnapshot = {
  availableSpecializations: ['Code', 'General', 'Planning', 'Review', 'Debugging', 'Tests', 'Architecture', 'Documentation', 'Reasoning'],
  maximumContextTokens: 262144,
  hardware: { totalRamBytes: 16e9, availableRamBytes: 8e9, freeDiskBytes: 100e9,
    gpus: [{ name: 'GPU simulé', totalBytes: 8e9, availableBytes: 7e9 }], warnings: [] },
  loaded: [], ollamaError: null, models: [
    { catalogue: { name: 'coder:7b', family: 'Coder', publisher: 'Éditeur', specializations: ['Code'],
      source: 'https://ollama.com/library/coder', downloadBytes: 4e9, documentedContext: 32768, license: null, openness: 'Unverified' },
    installed: true, metadata: { family: 'qwen2', parameters: '7B', quantization: 'Q4_K_M', installedBytes: 4e9,
      contextLength: 32768, architecture: 'qwen2', license: null, completion: true }, loaded: null, compatibility, dependencies: [] },
    { catalogue: { name: 'general:8b', family: 'General', publisher: null, specializations: ['General'],
      source: 'https://ollama.com/library/general', downloadBytes: 5e9, documentedContext: null, license: null, openness: 'Unverified' },
    installed: false, metadata: null, loaded: null,
    compatibility: { ...compatibility, status: 'Unknown', reason: 'Métadonnées inconnues' }, dependencies: [] },
  ],
}
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(modelLibraryApi.snapshot).mockResolvedValue(snapshot)
  vi.mocked(modelLibraryApi.downloads).mockResolvedValue([])
  vi.mocked(api.customAgents).mockResolvedValue([])
  vi.spyOn(window, 'confirm').mockReturnValue(true)
})
afterEach(() => { vi.restoreAllMocks(); vi.useRealTimers() })

describe('Bibliothèque locale', () => {
  it('enregistre les spécialisations explicitement, sans qualification automatique', async () => {
    const user = userEvent.setup()
    vi.mocked(modelLibraryApi.saveSpecializations).mockResolvedValue({ name: 'coder:7b', specializations: ['Code', 'Tests'], qualification: null })
    render(<ModelLibrary />)
    await screen.findByText('coder:7b')
    await user.click(screen.getAllByText('Spécialisations et qualification')[0])
    const fields = screen.getByRole('group', { name: 'Spécialisations de coder:7b' })
    await user.click(within(fields).getByLabelText('Tests'))
    expect(modelLibraryApi.saveSpecializations).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Enregistrer les spécialisations de coder:7b' }))
    await waitFor(() => expect(modelLibraryApi.saveSpecializations).toHaveBeenCalledWith('coder:7b', ['Code', 'Tests']))
    expect(modelLibraryApi.qualify).not.toHaveBeenCalled()
    expect(modelLibraryApi.install).not.toHaveBeenCalled()
  })

  it('qualifie seulement après confirmation et affiche un refus sans masquer l’erreur', async () => {
    const user = userEvent.setup()
    vi.mocked(modelLibraryApi.snapshot).mockResolvedValue({ ...snapshot,
      models: [{ ...snapshot.models[0], loaded: { name: 'coder:7b', size: 5e9, sizeVram: 4e9, contextLength: 65536 } }] })
    vi.mocked(modelLibraryApi.qualify).mockRejectedValue(new Error('Ressources insuffisantes'))
    render(<ModelLibrary />)
    await screen.findByText('coder:7b')
    await user.click(screen.getByText('Spécialisations et qualification'))
    await user.selectOptions(screen.getByLabelText('Contexte d’estimation'), '65536')
    const button = screen.getByRole('button', { name: 'Qualifier le chargement de coder:7b' })
    vi.mocked(window.confirm).mockReturnValueOnce(false)
    await user.click(button)
    expect(modelLibraryApi.qualify).not.toHaveBeenCalled()
    await user.click(button)
    expect(modelLibraryApi.qualify).toHaveBeenCalledWith('coder:7b', 65536)
    expect(await screen.findByRole('alert')).toHaveTextContent('Ressources insuffisantes')
    expect(modelLibraryApi.install).not.toHaveBeenCalled()
  })

  it('permet un contexte explicite de 256k sans modifier les profils', async () => {
    const user = userEvent.setup()
    render(<ModelLibrary />)
    await screen.findByText('coder:7b')
    await user.selectOptions(screen.getByLabelText('Contexte d’estimation'), '262144')
    await waitFor(() => expect(modelLibraryApi.snapshot).toHaveBeenLastCalledWith(262144, expect.any(AbortSignal)))
    expect(modelLibraryApi.saveSpecializations).not.toHaveBeenCalled()
    expect(modelLibraryApi.qualify).not.toHaveBeenCalled()
    expect(api.updateCustomAgent).not.toHaveBeenCalled()
  })

  it('affiche matériel et métadonnées puis filtre recherche et installation sans téléchargement', async () => {
    const user = userEvent.setup()
    render(<ModelLibrary />)
    expect(await screen.findByText('coder:7b')).toBeInTheDocument()
    expect(screen.getByText(/GPU simulé/)).toBeInTheDocument()
    expect(screen.getByText('7B / Q4_K_M')).toBeInTheDocument()
    await user.type(screen.getByRole('searchbox'), 'general')
    expect(screen.queryByText('coder:7b')).not.toBeInTheDocument()
    expect(screen.getByText('general:8b')).toBeInTheDocument()
    await user.clear(screen.getByRole('searchbox'))
    await user.click(screen.getByRole('tab', { name: 'Modèles installés' }))
    expect(screen.queryByText('general:8b')).not.toBeInTheDocument()
    expect(modelLibraryApi.install).not.toHaveBeenCalled()
  })

  it('exige confirmation puis suit et annule une installation', async () => {
    const user = userEvent.setup()
    const job = { id: 'download-1', model: 'general:8b', status: 'Queued', progress: 'Vérification',
      total: 100, completed: 0, updatedAt: '2026-11-17T00:00:00Z', error: null }
    vi.mocked(modelLibraryApi.install).mockResolvedValue(job)
    render(<ModelLibrary />)
    const button = await screen.findByRole('button', { name: 'Installer general:8b' })
    vi.mocked(window.confirm).mockReturnValueOnce(false)
    await user.click(button)
    expect(modelLibraryApi.install).not.toHaveBeenCalled()
    await user.click(button)
    await waitFor(() => expect(modelLibraryApi.install).toHaveBeenCalledWith('general:8b'))
    await user.click(await screen.findByRole('button', { name: 'Annuler general:8b' }))
    expect(modelLibraryApi.cancel).toHaveBeenCalledWith('download-1')
  })

  it('bloque la suppression des dépendances et affiche les erreurs Ollama', async () => {
    vi.mocked(modelLibraryApi.snapshot).mockResolvedValue({ ...snapshot,
      models: [{ ...snapshot.models[0], dependencies: [{ kind: 'Agent', name: 'Developer', active: true }] }] })
    render(<ModelLibrary />)
    expect(await screen.findByRole('button', { name: 'Supprimer le modèle' })).toBeDisabled()
    expect(screen.getByText(/Developer \(actif\)/)).toBeInTheDocument()
    vi.mocked(modelLibraryApi.snapshot).mockRejectedValueOnce(new Error('Ollama indisponible'))
    await userEvent.click(screen.getByRole('button', { name: /Actualiser la bibliothèque/ }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Ollama indisponible')
    expect(modelLibraryApi.remove).not.toHaveBeenCalled()
  })

  it('vérifie un identifiant manuel sans installer et explique une décision sans génération', async () => {
    const user = userEvent.setup()
    vi.mocked(modelLibraryApi.verify).mockResolvedValue({ name: 'manual:7b', downloadBytes: 1000 })
    vi.mocked(modelLibraryApi.preview).mockResolvedValue({ model: 'coder:7b', category: 'Debugging',
      reason: 'Classification heuristique', confidence: 'Low',
      alternatives: [{ model: 'coder:7b', score: 1, reason: 'Spécialisation code', compatibility }] })
    render(<ModelLibrary />)
    await user.type(screen.getByLabelText('Identifiant exact'), 'manual:7b')
    await user.click(screen.getByRole('button', { name: /Vérifier le tag/ }))
    expect(await screen.findByText(/Tag vérifié/)).toHaveTextContent('manual:7b')
    expect(modelLibraryApi.install).not.toHaveBeenCalled()
    await user.type(screen.getByLabelText('Tâche à classifier'), 'TS18004')
    await user.click(screen.getByRole('button', { name: 'Expliquer la sélection' }))
    expect(await screen.findByText('Classification heuristique')).toBeInTheDocument()
    expect(modelLibraryApi.preview).toHaveBeenCalledWith('TS18004', 'Developer', 4096)
  })

  it('actualise automatiquement l’inventaire après succès du téléchargement', async () => {
    vi.useFakeTimers()
    const job = { id: 'progress', model: 'coder:7b', status: 'Running', progress: 'pulling',
      total: 100, completed: 50, updatedAt: '2026-11-17T00:00:00Z', error: null }
    vi.mocked(modelLibraryApi.downloads).mockResolvedValueOnce([job]).mockResolvedValue([{ ...job, status: 'Completed' }])
    render(<ModelLibrary />)
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    fireEvent.click(screen.getByRole('tab', { name: 'Téléchargements' }))
    expect(screen.getByRole('progressbar')).toHaveAttribute('value', '50')
    await act(async () => { await vi.advanceTimersByTimeAsync(2000) })
    expect(modelLibraryApi.snapshot).toHaveBeenCalledTimes(2)
    expect(screen.getByRole('heading', { name: 'coder:7b · Installé' })).toBeInTheDocument()
  })
})
