import { Check, Circle, Clock3, LoaderCircle, ShieldAlert, Square, X } from 'lucide-react'
import { isActive, statusLabels, stepLabels, stepStatusLabels } from './types'
import type { Mission, StepStatus } from './types'

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

export function MissionDetails({ mission, cancel, busy }: {
  mission: Mission; cancel: (id: string) => void; busy: boolean
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
    <ol className="steps">{[...latest.values()].map(event => <li key={event.step} className={`step-${event.status}`}>
      <span className="step-icon"><StepIcon status={event.status} /></span>
      <div><strong>{stepLabels[event.step] ?? event.step}</strong><span>{stepStatusLabels[event.status]}</span></div>
      <time>{new Date(event.timestamp).toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' })}</time>
    </li>)}</ol>
    {mission.error && <div className="notice danger" role="alert">{mission.error}</div>}
    {isActive(mission) && <button className="button secondary full" disabled={busy} onClick={() => cancel(mission.id)}>
      <Square size={13} /> Demander l’annulation
    </button>}
    <details className="technical-logs"><summary>Journal technique · {mission.events.length} événements</summary>
      <div className="event-log">{mission.events.map(event => <div key={event.sequence}>
        <span>{event.sequence.toString().padStart(2, '0')} · {new Date(event.timestamp).toLocaleTimeString('fr-FR')}</span>
        <p>[{stepLabels[event.step] ?? event.step} / {stepStatusLabels[event.status]}] {event.message}</p>
      </div>)}</div>
    </details>
    {mission.verification.map((item, index) => <details className="technical-logs" key={`${item.stage}-${index}`}>
      <summary>{stepLabels[item.stage] ?? item.stage} · {item.status}{item.totalTests != null && ` · ${item.totalTests} tests`}</summary>
      <p className="muted">Code de sortie : {item.exitCode ?? 'Non exécuté'}</p><pre>{item.log}</pre>
    </details>)}
  </section>
}
