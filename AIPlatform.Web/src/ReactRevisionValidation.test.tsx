import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { ReactRevisionValidation } from './ReactRevisionValidation'

afterEach(() => vi.unstubAllGlobals())

it('prepares without executing and requires a separate click for the displayed revision', async () => {
  const plan = {
    missionId: 'mission', revisionVersion: 1, revisionFingerprint: 'revision-hash', fileCount: 21,
    imageReference: 'aiplatform-react-sandbox:1.0.0', image: 'pinned-image', timeoutSeconds: 120,
    fingerprint: 'execution-hash', stages: [{ name: 'TypeScript', arguments: ['--network=none'] }],
  }
  const report = { plan, status: 'Failed', startedAt: '2026-01-01T12:00:00Z',
    results: [{ stage: 'TypeScript', status: 'Failed', exitCode: 2, log: 'Type mismatch' }] }
  let latest: typeof report | null = null
  const fetch = vi.fn(async (path: string) => {
    if (path.endsWith('/report')) return Response.json({ report: latest })
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
  expect(fetch.mock.calls.some(([path]) => path.endsWith('/authorize'))).toBe(false)
  fireEvent.click(authorize)
  expect(await screen.findByText(/code de sortie : 2/)).toBeInTheDocument()
  expect(fetch).toHaveBeenCalledWith('/api/missions/mission/react-revision-validation/authorize', expect.objectContaining({
    method: 'POST', body: JSON.stringify({ approved: true, requestId: 'request', fingerprint: 'execution-hash' }),
  }))
  unmount()
  render(<ReactRevisionValidation id="mission" canPrepare />)
  await waitFor(() => expect(screen.getByText(/code de sortie : 2/)).toBeInTheDocument())
})
