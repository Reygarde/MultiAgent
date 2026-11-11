import { Check, Circle, Clock3, LoaderCircle, ShieldAlert, Square, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { api, errorMessage } from './api'
import { isActive, statusLabels, stepLabels, stepStatusLabels } from './types'
import type { ExecutionPlan, Mission, StepStatus } from './types'

export function StatusBadge({ mission }: { mission: Mission }) {
  return <span className={`badge status-${mission.status}`}>{isActive(mission) && <span className="status-dot" />}{statusLabels[mission.status]}</span>
}

function StepIcon({ status }: { status: StepStatus }) {
  if (status === 'Running') return <LoaderCircle size={15} className="spin" />
  if (status === 'Succeeded') return <Check size={15} />
  if (status === 'Failed') return <X size={15} />
  if (status === 'Unavailable') return <ShieldAlert size={15} />
  return <Circle size={13} />
}

export function MissionDetails({ mission, cancel, busy, refresh }: {
  mission: Mission; cancel: (id: string) => void; busy: boolean; refresh: () => Promise<unknown>
}) {
  const latest = new Map(mission.events.map(event => [event.step, event]))
  return <section className="panel mission-details">
    <div className="panel-heading"><h2><Clock3 size={16} /> Exécution réelle</h2><StatusBadge mission={mission} /></div>
    <p className="mission-id">{mission.id}</p>
    <div className="assignment">
      <span>Agent <strong>{mission.selectedAgent ?? 'En attente du routage'}</strong></span>
      <span>Modèle <strong>{mission.selectedModel ?? 'Non sélectionné'}</strong></span>
    </div>
    {mission.reason && <p className="routing-reason">{mission.reason}</p>}
    {mission.usedFallback && <div className="notice warning">Routage de secours déterministe utilisé.</div>}
    <p className="muted">Corrections effectuées : {mission.correctionAttempts}</p>
    {mission.status === 'AwaitingApproval' && <Approval key={mission.id} id={mission.id} refresh={refresh} />}
    <ol className="steps">{[...latest.values()].map(event => <li key={event.step} className={`step-${event.status}`}>
      <span className="step-icon"><StepIcon status={event.status} /></span>
      <div><strong>{stepLabels[event.step] ?? event.step}</strong><span>{stepStatusLabels[event.status]}</span></div>
      <time>{event.durationMilliseconds != null ? `${(event.durationMilliseconds / 1000).toFixed(1)} s`
        : new Date(event.timestamp).toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' })}</time>
    </li>)}</ol>
    {mission.error && <div className="notice danger" role="alert">{mission.error}</div>}
    {isActive(mission) && <button className="button secondary full" disabled={busy} onClick={() => cancel(mission.id)}>
      <Square size={13} /> Demander l’annulation
    </button>}
    <details className="technical-logs"><summary>Journal technique · {mission.events.length} événements</summary>
      <div className="event-log">{mission.events.map(event => <div key={event.sequence}>
        <span>{event.sequence.toString().padStart(2, '0')} · {new Date(event.timestamp).toLocaleTimeString('fr-FR')}</span>
        <p>[{stepLabels[event.step] ?? event.step} / {stepStatusLabels[event.status]}] {event.message}
          {event.durationMilliseconds != null && ` · ${(event.durationMilliseconds / 1000).toFixed(2)} s`}</p>
      </div>)}</div>
    </details>
    {mission.verification.map((item, index) => <details className="technical-logs" key={`${item.stage}-${index}`}>
      <summary>{stepLabels[item.stage] ?? item.stage} · {item.status} · tentative {item.attempt ?? '—'}
        {item.totalTests != null && ` · ${item.totalTests} tests`}</summary>
      <p className="muted">Code de sortie : {item.exitCode ?? 'Non exécuté'}
        {item.durationMilliseconds != null && ` · ${(item.durationMilliseconds / 1000).toFixed(2)} s`}<br />
        Avertissements MSBuild : {item.warningCount ?? 'non comptés'} · Erreurs MSBuild : {item.errorCount ?? 'non comptées'}
        {item.failedTests != null && ` · Tests échoués : ${item.failedTests}`}</p><pre>{item.log}</pre>
    </details>)}
  </section>
}

function Approval({ id, refresh }: { id: string; refresh: () => Promise<unknown> }) {
  const [plan, setPlan] = useState<ExecutionPlan | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [revision, setRevision] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    setPlan(null); setError(null)
    void api.executionPlan(id, controller.signal).then(result => {
      if (!controller.signal.aborted) setPlan(result)
    }).catch(error => { if (!controller.signal.aborted) setError(errorMessage(error)) })
    return () => controller.abort()
  }, [id, revision])
  async function decide(approved: boolean) {
    if (!plan) return
    setBusy(true); setError(null)
    try { await api.approve(id, approved, plan.fingerprint); await refresh() }
    catch (error) { setError(errorMessage(error)); setPlan(null) }
    finally { setBusy(false) }
  }
  return <section className="approval" aria-label="Autorisation de compilation">
    <h3>Votre autorisation est requise</h3>
    <p>Consultez les fichiers dans Projects avant de décider. Docker réduit les risques sans constituer une frontière de sécurité absolue.</p>
    {error && <div role="alert" className="notice danger">{error}</div>}
    {plan ? <>
      <ul>{plan.operations.map(operation => <li key={operation}>{operation}</li>)}</ul>
      <p>{plan.cpuCount} CPU · {plan.memoryMegabytes} Mio · {plan.timeoutSeconds} s par conteneur<br />
        {plan.maxCorrectionAttempts} corrections maximum · réseau désactivé</p>
      <details><summary>Image et empreinte approuvées</summary><code>{plan.image}<br />{plan.fingerprint}</code></details>
      <div className="approval-actions">
        <button className="button primary" disabled={busy} onClick={() => void decide(true)}>Autoriser compilation et tests</button>
        <button className="button secondary" disabled={busy} onClick={() => void decide(false)}>Refuser l’exécution</button>
      </div>
    </> : error ? <button className="button secondary" onClick={() => setRevision(value => value + 1)}>Recharger le plan</button>
      : <p className="loading"><LoaderCircle className="spin" size={16} /> Chargement du plan réel…</p>}
  </section>
}
