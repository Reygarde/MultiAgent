import { beforeEach, describe, expect, it, vi } from 'vitest'
import { isTauri } from '@tauri-apps/api/core'
import { api } from '../api'
import { configureDesktopTransport, fetchApi } from './transport'

vi.mock('@tauri-apps/api/core', () => ({ isTauri: vi.fn() }))
const connection = { url: 'http://127.0.0.1:45678', token: 'a'.repeat(64) }
beforeEach(() => {
  vi.clearAllMocks()
  configureDesktopTransport(null)
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 202 })))
})

describe('desktop transport', () => {
  it('does not contact an API before the native readiness handshake', async () => {
    vi.mocked(isTauri).mockReturnValue(true)
    await expect(fetchApi('/agents')).rejects.toThrow('pas encore prêt')
    expect(fetch).not.toHaveBeenCalled()
  })

  it('authenticates ordinary requests and approval/cancellation actions', async () => {
    vi.mocked(isTauri).mockReturnValue(true)
    configureDesktopTransport(connection)
    await fetchApi('/agents')
    await api.approve('mission-id', true, 'reviewed-fingerprint')
    await api.cancel('mission-id')
    expect(fetch).toHaveBeenCalledTimes(3)
    for (const [url, options] of vi.mocked(fetch).mock.calls) {
      expect(url).toMatch(/^http:\/\/127\.0\.0\.1:45678\/api\//)
      expect(new Headers(options?.headers).get('Authorization')).toBe(`Bearer ${connection.token}`)
    }
    configureDesktopTransport(null)
    await expect(fetchApi('/agents')).rejects.toThrow('pas encore prêt')
    expect(fetch).toHaveBeenCalledTimes(3)
  })

  it('preserves browser development URLs without a bearer token', async () => {
    vi.mocked(isTauri).mockReturnValue(false)
    await fetchApi('/missions', { method: 'POST', body: '{}' })
    const [url, options] = vi.mocked(fetch).mock.calls[0]
    expect(url).toBe('/api/missions')
    expect(new Headers(options?.headers).get('Authorization')).toBeNull()
    expect(new Headers(options?.headers).get('Content-Type')).toBe('application/json')
  })

  it('rejects foreign backend URLs and malformed native credentials', () => {
    expect(() => configureDesktopTransport({ ...connection, url: 'https://example.com' })).toThrow()
    expect(() => configureDesktopTransport({ ...connection, token: 'wrong' })).toThrow()
    expect(() => configureDesktopTransport({ ...connection, url: 'http://127.0.0.1:45678/foreign' })).toThrow()
  })
})
