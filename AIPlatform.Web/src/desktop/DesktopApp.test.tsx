import { render, screen, waitFor } from '@testing-library/react'
import { invoke } from '@tauri-apps/api/core'
import { beforeEach, expect, it, vi } from 'vitest'
import DesktopApp from './DesktopApp'
import { configureDesktopTransport } from './transport'

vi.mock('@tauri-apps/api/core', () => ({ invoke: vi.fn() }))
vi.mock('./transport', () => ({ configureDesktopTransport: vi.fn() }))
vi.mock('../App', () => ({ default: () => <div>Application connectée</div> }))
beforeEach(() => vi.clearAllMocks())

it('gates the dashboard until real native readiness supplies its protected connection', async () => {
  vi.mocked(invoke).mockResolvedValueOnce({ phase: 'Starting', url: null, token: null, error: null, logPath: 'backend.log' })
    .mockResolvedValue({ phase: 'Ready', url: 'http://127.0.0.1:45678', token: 'a'.repeat(64), error: null, logPath: 'backend.log' })
  render(<DesktopApp />)
  expect(screen.queryByText('Application connectée')).not.toBeInTheDocument()
  expect(await screen.findByText('Application connectée')).toBeVisible()
  expect(configureDesktopTransport).toHaveBeenCalledWith({ url: 'http://127.0.0.1:45678', token: 'a'.repeat(64) })
})

it('surfaces native startup failures instead of falling back to an unprotected API', async () => {
  vi.mocked(invoke).mockRejectedValue(new Error('Backend configuration unavailable'))
  render(<DesktopApp />)
  expect(await screen.findByRole('alert')).toHaveTextContent('Backend configuration unavailable')
  expect(screen.queryByText('Application connectée')).not.toBeInTheDocument()
  await waitFor(() => expect(configureDesktopTransport).toHaveBeenCalledWith(null))
})
