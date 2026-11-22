import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { ReactRevisionValidation } from './ReactRevisionValidation'

afterEach(() => vi.unstubAllGlobals())

it('prepares without executing and requires a separate click for the displayed revision', async () => {
  const plan = {
    missionId: 'mission', revisionVersion: 1, revisionFingerprint: 'revision-hash', fileCount: 21,
    imageReference: 'aiplatform-react-sandbox:1.0.0', image: 'pinned-image', timeoutSeconds: 120,
    fingerprint: 'execution-hash', stages: [{ name: 'TypeScript', arguments: ['--network=none'] }],
    preparationEvidenceId: 'historical-preparation-proof',
  }
  const report = { runId: 'run', plan, status: 'Failed', startedAt: '2026-01-01T12:00:00Z',
    results: [{ stage: 'TypeScript', status: 'Failed', exitCode: 2, log: 'Type mismatch' }] }
  let latest: typeof report | null = null
  const fetch = vi.fn(async (path: string) => {
    if (path.endsWith('/reports')) return Response.json({ reports: latest ? [latest] : [] })
    if (path.endsWith('/prepare')) return Response.json({
      requestId: 'request', expiresAt: '2026-01-01T12:15:00Z', plan,
    })
    if (path.endsWith('/authorize')) {
      latest = report
      return Response.json(latest)
    }
    throw new Error(`Unexpected request: ${path}`)
  })
  vi.stubGlobal('fetch', fetch)
  const { unmount } = render(<ReactRevisionValidation id="mission" canPrepare />)
  fireEvent.click(screen.getByRole('button', { name: 'Valider cette révision' }))
  const authorize = await screen.findByRole('button', { name: 'Autoriser et exécuter dans Docker' })
  expect(screen.getByText('revision-hash')).toBeInTheDocument()
  expect(screen.getByText('execution-hash')).toBeInTheDocument()
  expect(screen.getByText('historical-preparation-proof')).toBeInTheDocument()
  expect(fetch.mock.calls.some(([path]) => path.endsWith('/authorize'))).toBe(false)
  fireEvent.click(authorize)
  expect((await screen.findAllByText(/code de sortie : 2/))[0]).toBeInTheDocument()
  expect(fetch).toHaveBeenCalledWith('/api/missions/mission/react-revision-validation/authorize', expect.objectContaining({
    method: 'POST', body: JSON.stringify({ approved: true, requestId: 'request', fingerprint: 'execution-hash' }),
  }))
  unmount()
  render(<ReactRevisionValidation id="mission" canPrepare />)
  await waitFor(() => expect(screen.getAllByText(/code de sortie : 2/)[0]).toBeInTheDocument())
})

it('shows validation history separately, including the individual source fingerprints', async () => {
  const plan = {
    missionId: 'mission', revisionVersion: 1, revisionFingerprint: 'revision-hash', fileCount: 1,
    imageReference: 'react', image: 'sha256:image', timeoutSeconds: 120, fingerprint: 'plan-hash',
    files: [{ path: 'src/App.tsx', sha256: 'source-hash' }], stages: [],
  }
  const reports = [
    { runId: 'newer', plan, status: 'Interrupted', startedAt: '2026-01-02T12:00:00Z', results: [] },
    { runId: 'older', plan, status: 'Succeeded', startedAt: '2026-01-01T12:00:00Z',
      results: [{ stage: 'Tests', status: 'Succeeded', exitCode: 0, totalTests: 5, failedTests: 0, log: '' }] },
  ]
  const fetch = vi.fn(async () => Response.json({ reports }))
  vi.stubGlobal('fetch', fetch)
  render(<ReactRevisionValidation id="mission" canPrepare={false} />)
  expect(await screen.findByText(/Dernière validation technique · révision v1 · Interrompu/)).toBeInTheDocument()
  fireEvent.click(screen.getByText('Historique des validations (2)'))
  expect(screen.getByText(/Tests Vitest : Réussi/)).toBeVisible()
  expect(screen.getByText('older')).toBeVisible()
  const identities = screen.getAllByText('Identités et empreintes')
  fireEvent.click(identities[1])
  expect(screen.getAllByText('source-hash')[1]).toBeVisible()
  expect(fetch).toHaveBeenCalledTimes(1)
  expect(screen.queryByRole('button', { name: 'Valider cette révision' })).not.toBeInTheDocument()
})
