import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { StudioBackupPanel } from './StudioBackupPanel'
import { fetchApi } from './desktop/transport'

vi.mock('./desktop/transport', () => ({ fetchApi: vi.fn() }))

const preview = {
  id: 'preview-id', applicationVersion: '0.6.6', archiveBytes: 1200, dataBytes: 5000,
  files: 4, missions: 2, projects: 1, missionIdConflicts: 1, redactedSettingsValues: 1,
  externalPathsNotTransferred: 1, executionApprovalsToRevoke: 1, pendingMissionsToStop: 0,
  externalAuthorizationsToRevoke: 1, requiredFreeBytes: 1000, availableFreeBytes: 5000,
  spaceAvailable: true, replacesAllCurrentData: true, settingsIncluded: true,
  replacedCategories: ['Missions and workspaces'], exclusions: ['Ollama model weights'],
  dependenciesToInstall: ['Ollama'], warnings: ['All current data will be replaced.'],
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

describe('Studio backup panel', () => {
  beforeEach(() => vi.clearAllMocks())

  it('requires explicit replacement and conflict confirmation before restoring', async () => {
    const user = userEvent.setup()
    const restart = vi.fn().mockResolvedValue(undefined)
    vi.mocked(fetchApi).mockResolvedValueOnce(jsonResponse(preview)).mockResolvedValueOnce(jsonResponse({
      status: 'Restored', missionsRestored: 2, projectsRestored: 1, executionApprovalsRevoked: 1,
      pendingMissionsStopped: 0, externalAuthorizationsRevoked: 1, sourceFingerprintsPreserved: 2,
      restartRequired: true, dependenciesToInstall: ['Ollama'], warnings: [],
    }))
    render(<StudioBackupPanel restartBackend={restart} />)

    const archive = new File(['synthetic archive'], 'studio.zip', { type: 'application/zip' })
    fireEvent.change(screen.getByLabelText('Sélectionner une archive Studio'), { target: { files: [archive] } })
    expect(await screen.findByText('Aperçu avant remplacement')).toBeInTheDocument()
    const restoreButton = screen.getByRole('button', { name: 'Confirmer et restaurer' })
    expect(restoreButton).toBeDisabled()

    await user.click(screen.getByRole('checkbox', {
      name: /confirme le remplacement complet de toutes les données internes Studio/,
    }))
    expect(restoreButton).toBeDisabled()
    await user.click(screen.getByRole('checkbox', {
      name: /missions ayant un identifiant identique seront remplacées/,
    }))
    expect(restoreButton).toBeEnabled()
    await user.click(restoreButton)

    await waitFor(() => expect(restart).toHaveBeenCalledOnce())
    expect(fetchApi).toHaveBeenLastCalledWith('/studio-backup/restore/preview-id', expect.objectContaining({
      method: 'POST',
      body: JSON.stringify({ confirmReplaceAllCurrentData: true, acknowledgeMissionIdConflicts: true }),
    }))
  })

  it('does not request restoration when the archive cannot be inspected', async () => {
    const response = new Response(JSON.stringify({ title: 'Archive corrompue' }), {
      status: 400, headers: { 'Content-Type': 'application/problem+json' },
    })
    vi.mocked(fetchApi).mockResolvedValueOnce(response)
    render(<StudioBackupPanel restartBackend={vi.fn()} />)
    const archive = new File(['bad'], 'broken.zip', { type: 'application/zip' })
    fireEvent.change(screen.getByLabelText('Sélectionner une archive Studio'), { target: { files: [archive] } })
    expect(await screen.findByRole('alert')).toHaveTextContent('Archive corrompue')
    expect(fetchApi).toHaveBeenCalledTimes(1)
  })
})
