import { isTauri } from '@tauri-apps/api/core'

interface Connection { url: string; token: string }
let connection: Connection | null = null

export function configureDesktopTransport(value: Connection | null) {
  if (value) {
    const url = new URL(value.url)
    if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port ||
        url.pathname !== '/' || url.search || url.hash || url.username || url.password ||
        !/^[a-f0-9]{64}$/.test(value.token)) {
      throw new Error('Connexion au backend local invalide.')
    }
  }
  connection = value
}

export async function fetchApi(path: string, init?: RequestInit): Promise<Response> {
  const native = isTauri()
  let base = ''
  const headers = new Headers(init?.headers)
  if (init?.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (native) {
    if (!connection) throw new Error('Le backend natif n’est pas encore prêt.')
    base = connection.url
    headers.set('Authorization', `Bearer ${connection.token}`)
  }
  return fetch(`${base}/api${path}`, { ...init, headers })
}
