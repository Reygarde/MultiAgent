import { invoke } from '@tauri-apps/api/core'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Layers3, LoaderCircle, RefreshCw, ShieldCheck } from 'lucide-react'
import App from '../App'
import { errorMessage } from '../api'
import { configureDesktopTransport } from './transport'

interface BackendStatus {
  phase: 'Starting' | 'Ready' | 'Failed' | 'Stopped'
  url: string | null
  token: string | null
  error: string | null
  logPath: string
}

export default function DesktopApp() {
  const [status, setStatus] = useState<BackendStatus>({
    phase: 'Starting', url: null, token: null, error: null, logPath: '',
  })
  const restarting = useRef(false)
  useEffect(() => {
    let disposed = false
    let timer: ReturnType<typeof setTimeout>
    async function poll() {
      try {
        const next = await invoke<BackendStatus>('backend_status')
        if (!disposed && !restarting.current) {
          if (next.phase === 'Ready') {
            if (!next.url || !next.token) throw new Error('Le backend a renvoyé un état prêt incomplet.')
            configureDesktopTransport({ url: next.url, token: next.token })
          } else configureDesktopTransport(null)
          setStatus(previous => previous.phase === next.phase && previous.url === next.url &&
            previous.token === next.token && previous.error === next.error ? previous : next)
        }
      } catch (error) {
        if (!disposed && !restarting.current) {
          configureDesktopTransport(null)
          setStatus(previous => ({ ...previous, phase: 'Failed', token: null, url: null, error: errorMessage(error) }))
        }
      } finally {
        if (!disposed) timer = setTimeout(() => void poll(), 500)
      }
    }
    void poll()
    return () => { disposed = true; clearTimeout(timer); configureDesktopTransport(null) }
  }, [])

  const restart = useCallback(async () => {
    if (restarting.current) return
    restarting.current = true
    configureDesktopTransport(null)
    setStatus(previous => ({ ...previous, phase: 'Starting', token: null, url: null, error: null }))
    try { await invoke('restart_backend') }
    catch (error) {
      setStatus(previous => ({ ...previous, phase: 'Failed', error: errorMessage(error) }))
    } finally { restarting.current = false }
  }, [])

  if (status.phase === 'Ready') return <App restartBackend={restart} />
  return <main className="desktop-startup"><section className="panel">
    <span className="hero-icon"><Layers3 size={32} /></span>
    <h1>AIPlatform Studio</h1>
    <p>{status.phase === 'Starting'
      ? 'Démarrage du backend local. Attente du signal de disponibilité réel…'
      : 'Le backend local est indisponible.'}</p>
    {status.phase === 'Starting' ? <LoaderCircle className="spin" size={24} aria-label="Démarrage" />
      : <button className="button primary" onClick={() => void restart()}><RefreshCw size={16} /> Relancer le backend</button>}
    {status.error && <div role="alert" className="notice danger">{status.error}</div>}
    {status.logPath && <p className="muted">Journal technique : <code>{status.logPath}</code></p>}
    <p className="muted"><ShieldCheck size={14} /> Connexion locale authentifiée. Aucun terminal requis.<br />
      Ollama et Docker sont vérifiés une fois le backend prêt ; ils ne sont jamais arrêtés par Studio.</p>
  </section></main>
}
