import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from './api'
import { EnvironmentPage } from './EnvironmentPage'

vi.mock('./api', () => ({
  api: {
    environment: vi.fn(),
    verifySandboxProvisioning: vi.fn(),
    importSandboxProvisioning: vi.fn(),
  },
  errorMessage: (error: unknown) => error instanceof Error ? error.message : 'Erreur',
}))

const report = {
  checkedAt: '2026-11-19T12:00:00Z',
  operatingSystem: 'Windows 11',
  architecture: 'X64',
  system: { id: 'system', name: 'Windows x64', status: 'Ready', detail: 'Système détecté.', version: '10.0' },
  memory: { id: 'memory', name: 'RAM', status: 'Ready', detail: 'Mesurée.', version: null },
  gpu: { id: 'gpu', name: 'GPU / VRAM', status: 'Unverified', detail: 'Non mesurée.', version: null },
  webView2: { id: 'webview2', name: 'WebView2', status: 'Ready', detail: 'Détecté.', version: '130.0' },
  node: { id: 'node', name: 'Node.js', status: 'Missing', detail: 'Node requis.', version: null },
  totalRamBytes: 32e9, availableRamBytes: 20e9, gpus: [],
  ollama: { id: 'ollama', name: 'Ollama', status: 'Ready', detail: 'API locale disponible.', version: null },
  models: [{ name: 'coder:7b', status: 'Missing', installed: false }],
  dockerDesktop: { id: 'docker-desktop', name: 'Docker Desktop', status: 'Ready', detail: 'Détecté.', version: null },
  dockerEngine: { id: 'docker-engine', name: 'Moteur Docker Linux', status: 'Ready', detail: 'Linux.', version: null },
  sandboxImages: [{
    imageReference: 'aiplatform-react-sandbox:1.0.0', version: 'react-v1', status: 'Missing',
    installedImageId: null, expectedImageId: null, detail: 'Absente.',
  }],
  provisioningPackage: {
    status: 'Unverified', version: '1.0.0', fingerprint: 'a'.repeat(64),
    imageCount: 4, totalBytes: 1200, detail: 'À vérifier.',
  },
  warnings: ['Le lancement de WebView2 n’a pas été testé.'],
} as const

const verifiedReport = {
  ...report,
  node: { ...report.node, status: 'Ready' },
  models: [{ name: 'coder:7b', status: 'Ready', installed: true }],
  sandboxImages: [{ ...report.sandboxImages[0], expectedImageId: 'sha256:' + 'b'.repeat(64) }],
  provisioningPackage: { ...report.provisioningPackage, status: 'Ready', detail: 'Empreintes vérifiées.' },
} as const
const importedReport = {
  ...verifiedReport,
  sandboxImages: [{ ...verifiedReport.sandboxImages[0], status: 'Ready' }],
} as const

describe('Environment page', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(api.environment).mockResolvedValue(report as never)
  })

  it('only imports sandbox images after verification and explicit confirmation', async () => {
    const user = userEvent.setup()
    render(<EnvironmentPage />)

    expect(api.environment).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Analyser l’environnement' }))
    expect(await screen.findByText('Windows 11 · X64')).toBeInTheDocument()
    expect(screen.getByText(/Node.js 20.14 ou ultérieur est requis/)).toBeInTheDocument()
    expect(api.importSandboxProvisioning).not.toHaveBeenCalled()

    vi.mocked(api.verifySandboxProvisioning).mockResolvedValue(verifiedReport.provisioningPackage as never)
    vi.mocked(api.environment).mockResolvedValueOnce(verifiedReport as never)
    await user.click(screen.getByRole('button', { name: 'Vérifier le paquet' }))
    expect(await screen.findAllByText('Empreintes vérifiées.')).not.toHaveLength(0)
    const importButton = screen.getByRole('button', { name: 'Importer les images sandbox' })
    expect(importButton).toBeDisabled()
    await user.click(screen.getByRole('checkbox', {
      name: /confirme l’import du paquet vérifié dans Docker/,
    }))
    expect(importButton).toBeEnabled()

    vi.mocked(api.importSandboxProvisioning).mockResolvedValue([{
      ...verifiedReport.sandboxImages[0], status: 'Ready',
    }] as never)
    vi.mocked(api.environment).mockResolvedValueOnce(importedReport as never)
    await user.click(importButton)

    await waitFor(() => expect(api.importSandboxProvisioning).toHaveBeenCalledWith('a'.repeat(64)))
    expect(await screen.findByText(/images sandbox importées et vérifiées/)).toBeInTheDocument()
  })

  it('surfaces diagnostic failures without attempting installation or import', async () => {
    const user = userEvent.setup()
    vi.mocked(api.environment).mockRejectedValueOnce(new Error('Diagnostic inaccessible'))
    render(<EnvironmentPage />)

    await user.click(screen.getByRole('button', { name: 'Analyser l’environnement' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Diagnostic inaccessible')
    expect(api.verifySandboxProvisioning).not.toHaveBeenCalled()
    expect(api.importSandboxProvisioning).not.toHaveBeenCalled()
  })
})
