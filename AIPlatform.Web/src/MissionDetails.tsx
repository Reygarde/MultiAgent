import { Check, Circle, Clock3, LoaderCircle, ShieldAlert, Square, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { api, errorMessage } from './api'
import { isActive, statusLabels, stepLabels, stepStatusLabels } from './types'
import type { ExecutionPlan, Mission, MissionEvent, RevisionComparison, StepStatus } from './types'

export function StatusBadge({ mission }: { mission: Mission }) {
  return <span className={`badge status-${mission.status}`}>{isActive(mission) && <span className="status-dot" />}{statusLabels[mission.status]}</span>
}

function StepIcon({ status }: { status: StepStatus | 'Archived' }) {
  if (status === 'Running') return <LoaderCircle size={15} className="spin" />
  if (status === 'Succeeded') return <Check size={15} />
  if (status === 'Failed') return <X size={15} />
  if (status === 'Unavailable') return <ShieldAlert size={15} />
  return <Circle size={13} />
}

export function MissionDetails({ mission, cancel, busy, refresh }: {
  mission: Mission; cancel: (id: string) => void; busy: boolean; refresh: () => Promise<unknown>
}) {
  const [resumeError, setResumeError] = useState<string | null>(null)
  const [resuming, setResuming] = useState(false)
  const canRevalidate = mission.status === 'GeneratedUnverified' && mission.templateId === 'fullstack-react-dotnet' &&
    mission.workflow?.review != null && !mission.workflow.review.findings.some(finding => finding.severity === 'Error')
  return <section className="panel mission-details">
    <div className="panel-heading"><h2><Clock3 size={16} /> Exécution réelle</h2><StatusBadge mission={mission} /></div>
    <p className="mission-id">{mission.id}</p>
    {mission.templateId === 'discord-bot-dotnet' && <p>Template : Discord Bot v1 · Discord.Net.WebSocket 3.20.2 · xUnit 2.9.3</p>}
    {mission.templateId === 'twitch-bot-dotnet' && <p>Template : Twitch Bot v1 · .NET 10 · EventSub + Helix.
      Aucune dépendance applicative ; xUnit 2.9.3, runner 3.1.4, Test SDK 17.14.1.
      Validation Docker Release sans réseau ni compte Twitch. Connexion réelle non validée ; OAuth par environnement.</p>}
    {mission.templateId === 'react-typescript-vite' && <p>Template : React App v1 · React 19.3.1 · TypeScript 5.9.3 · Vite 6.4.6 · Vitest 4.1.12.
      Dépendances approuvées dans package.json ; versions transitives dans package-lock.json.
      Validation Docker : TypeScript → build Vite → tests Vitest, sans réseau.</p>}
    {mission.templateId === 'fullstack-react-dotnet' && <p>Template : Full-Stack App v1 · ASP.NET Core .NET 10 · SQLite 10.0.15 · React/TypeScript.
      BackendAgent puis FrontendAgent utilisent le même contrat OpenAPI. Validation après approbation : Release/xUnit,
      TypeScript, Vite, Vitest et appels HTTP réels sur loopback Docker ; réseau externe désactivé.</p>}
    <div className="assignment">
      <span>Agent <strong>{mission.selectedAgent ?? 'En attente du routage'}</strong></span>
      <span>Modèle <strong>{mission.selectedModel ?? 'Non sélectionné'}</strong></span>
    </div>
    {mission.reason && <p className="routing-reason">{mission.reason}</p>}
    {mission.usedFallback && <div className="notice warning">Routage de secours déterministe utilisé.</div>}
    <p className="muted">Corrections effectuées : {mission.correctionAttempts}
      {mission.templateId === 'fullstack-react-dotnet'
        ? ' · correction automatique désactivée'
        : mission.workflow?.correctionLimit != null && ` · restantes : ${Math.max(0, mission.workflow.correctionLimit - mission.correctionAttempts)}`}</p>
    {mission.status === 'AwaitingApproval' &&
      <Approval key={`${mission.id}:${mission.workflow?.version ?? 'legacy'}`} id={mission.id} refresh={refresh} />}
    {mission.workflow && <section aria-label="Workflow collaboratif">
      <h3>Workflow · version {mission.workflow.version}</h3>
      <p className="mission-id">{mission.workflow.workflowId}</p>
      {mission.workflow.review && <details className="technical-logs">
        <summary>Rapport du ReviewAgent</summary>
        <p>{mission.workflow.review.summary}</p>
        {mission.workflow.review.problem && <p>Problème : {mission.workflow.review.problem}</p>}
        {mission.workflow.review.modifiedFiles && <p>Fichiers signalés : {mission.workflow.review.modifiedFiles.join(', ') || 'aucun'}</p>}
        {mission.workflow.review.justification && <p>Justification : {mission.workflow.review.justification}</p>}
        {mission.workflow.review.risks && <p>Risques : {mission.workflow.review.risks.join('; ') || 'aucun signalé'}</p>}
        {mission.workflow.review.planCompliance && <p>Conformité au plan : {mission.workflow.review.planCompliance}</p>}
        {mission.workflow.review.recommendation && <p>Recommandation : {mission.workflow.review.recommendation}</p>}
        {mission.workflow.review.findings.map((finding, i) => <p key={i}>{finding.severity} · {finding.path} : {finding.message}</p>)}
      </details>}
      {[...new Set([...mission.workflow.revisions.map(revision => revision.version),
        ...(mission.workflow.proposals ?? []).map(proposal => proposal.version)])].sort((a, b) => a - b)
        .map(version => <Revision key={version} id={mission.id} version={version} />)}
      {mission.workflow.steps.map(step => <details className="technical-logs" key={step.id}>
        <summary>{stepLabels[step.kind] ?? step.kind}
          {step.version != null && ` · v${step.version}`} · {step.agentId} · état enregistré : {step.status}
          {step.durationMilliseconds != null && ` · ${(step.durationMilliseconds / 1000).toFixed(1)} s`}</summary>
        <p>Identifiant : {step.id} · Tentatives : {step.attempts}<br />Prérequis : {step.dependsOn.join(', ') || 'aucun'}</p>
        <p>Modèle : {step.modelName ?? 'C# déterministe (ou ancienne étape)'}</p>
        <p>Contexte transmis : {step.input ?? 'Pas encore exécuté'}</p>
        {step.output && <pre>{step.output}</pre>}
        {step.error && <div role="alert" className="notice danger">{step.error}</div>}
      </details>)}
      {!isActive(mission) && !mission.deleted && !['Verified', 'Completed'].includes(mission.status) &&
        <button className="button secondary full" disabled={resuming} onClick={() => {
          setResuming(true); setResumeError(null)
          const prepare = canRevalidate ? api.revalidate(mission.id) : api.resume(mission.id)
          void prepare.then(refresh).catch(error => setResumeError(errorMessage(error))).finally(() => setResuming(false))
        }}>{canRevalidate ? 'Préparer une nouvelle validation · sans régénération' : 'Reprendre · nouvelle autorisation requise'}</button>}
      {resumeError && <p role="alert" className="notice danger">{resumeError}</p>}
    </section>}
    <Chronology mission={mission} />
    {mission.error && <div className="notice danger" role="alert">{mission.error}</div>}
    {isActive(mission) && <button className="button secondary full" disabled={busy} onClick={() => cancel(mission.id)}>
      <Square size={13} /> Demander l’annulation
    </button>}
    <details className="technical-logs"><summary>Journal technique · {mission.events.length} événements</summary>
      <div className="event-log">{mission.events.map(event => <div key={event.sequence}>
        <span>{event.sequence.toString().padStart(2, '0')} · {new Date(event.timestamp).toLocaleTimeString('fr-FR')}</span>
        <p>[{stepLabels[event.step] ?? event.step} / {event.status === 'Running'
          ? 'Début / attente enregistrée (historique)' : stepStatusLabels[event.status]}] {event.message}
          {event.durationMilliseconds != null && ` · ${(event.durationMilliseconds / 1000).toFixed(2)} s`}</p>
      </div>)}</div>
    </details>
    {mission.verification.map((item, index) => <details className="technical-logs" key={`${item.stage}-${index}`}>
      <summary>{stepLabels[item.stage] ?? item.stage} · {item.status} · tentative {item.attempt ?? '—'}
        {item.totalTests != null && ` · ${item.totalTests} tests`}</summary>
      <p className="muted">Code de sortie : {item.exitCode ?? 'Non exécuté'}
        {item.durationMilliseconds != null && ` · ${(item.durationMilliseconds / 1000).toFixed(2)} s`}<br />
        Avertissements : {item.warningCount ?? 'non comptés'} · Erreurs : {item.errorCount ?? 'non comptées'}
        {item.failedTests != null && ` · Tests échoués : ${item.failedTests}`}</p>
      {item.diagnostics && <div>
        <p>Commande : {item.diagnostics.command} · code : {item.diagnostics.exitCode ?? 'indisponible'}</p>
        {item.diagnostics.items.map((diagnostic, i) => <p key={i}>
          {diagnostic.type} · {diagnostic.project ?? 'projet inconnu'} · {diagnostic.file ?? 'fichier inconnu'}
          {diagnostic.line != null && `:${diagnostic.line}${diagnostic.column != null ? `:${diagnostic.column}` : ''}`}
          {diagnostic.code && ` · ${diagnostic.code}`} : {diagnostic.message}
        </p>)}
        <pre>{item.diagnostics.excerpt}</pre>
      </div>}<pre>{item.log}</pre>
    </details>)}
  </section>
}

interface TimelineItem {
  id: string; kind: string; status: StepStatus | 'Archived'; label: string
  version?: number | null; timestamp?: string | null; duration?: number | null
}

function Chronology({ mission }: { mission: Mission }) {
  function eventItem(event: MissionEvent): TimelineItem {
    const active = event.status === 'Running' && (mission.status === 'Queued' && event.step === 'Queue' ||
      mission.status === 'Running' && event.step !== 'Queue' && event.sequence === mission.events.at(-1)?.sequence ||
      mission.status === 'AwaitingApproval' && ['Approval', 'Authorization'].includes(event.step) &&
        event.sequence === mission.events.at(-1)?.sequence)
    const archived = event.status === 'Running' && !active
    const activityAfterQueue = event.step === 'Queue' && mission.events.some(item => item.sequence > event.sequence &&
      ['Analysis', 'Routing', 'Generation', 'Writing', 'Plan', 'Generate', 'Review', 'Correct',
        'TypeScript', 'Build', 'Tests'].includes(item.step))
    return { id: `event-${event.sequence}`, kind: event.step, status: archived ? 'Archived' : event.status,
      label: archived ? activityAfterQueue ? 'Attente passée · activité enregistrée ensuite'
        : 'Historique · clôture non enregistrée' : stepStatusLabels[event.status],
      timestamp: event.timestamp, duration: event.durationMilliseconds }
  }
  function list(items: TimelineItem[], name: string) {
    return <ol className="steps" aria-label={name}>{items.map(item => <li key={item.id} className={`step-${item.status}`}>
      <span className="step-icon"><StepIcon status={item.status} /></span>
      <div><strong>{stepLabels[item.kind] ?? item.kind}
        {item.version != null && ` · v${item.version}`}</strong><span>{item.label}</span>
        <span className="muted">{item.id}{mission.workflow && item.version == null &&
          (item.kind === 'Queue' ? ' · attente/reprise hors étapes versionnées' : ' · version non enregistrée')}</span></div>
      <time>{item.duration != null ? `${(item.duration / 1000).toFixed(1)} s`
        : item.timestamp ? new Date(item.timestamp).toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' }) : '—'}</time>
    </li>)}</ol>
  }
  if (!mission.workflow) {
    const latest = new Map(mission.events.map(event => [event.step, event]))
    return <section aria-label="Chronologie"><h3>Chronologie</h3>
      {list([...latest.values()].map(eventItem), 'Étapes de la mission')}</section>
  }
  const workflow = mission.workflow
  const items: TimelineItem[] = workflow.steps.map(step => {
    const active = step.version === workflow.version &&
      (mission.status === 'Running' && step.status === 'Running' ||
       mission.status === 'AwaitingApproval' && step.status === 'WaitingForApproval')
    const status: StepStatus | 'Archived' = active ? 'Running' : step.status === 'Completed' ? 'Succeeded'
      : step.status === 'Failed' ? 'Failed' : step.status === 'Cancelled' ? 'Cancelled'
      : step.status === 'Skipped' || step.status === 'Pending' ? 'Skipped' : 'Archived'
    const label = active && step.status === 'WaitingForApproval' ? 'Autorisation requise'
      : status === 'Archived' ? `Historique · état enregistré : ${step.status}`
      : step.status === 'Pending' ? 'Non exécuté · en attente'
      : step.kind === 'Approval' && step.status === 'Completed' ? 'Autorisation accordée'
      : stepStatusLabels[status]
    return { id: step.id, kind: step.kind, version: step.version, status, label,
      timestamp: step.startedAt, duration: step.durationMilliseconds }
  })
  const queueEvents = mission.events.filter(event => event.step === 'Queue').map(eventItem)
    .map(item => item.status === 'Running' ? { ...item, status: 'Archived' as const,
      label: 'Attente historique · état de la file indiqué ci-dessus' } : item)
  return <section aria-label="Chronologie">
    <h3>Chronologie · version actuelle {workflow.version}</h3>
    {mission.status === 'Queued' && list([{ id: 'queue-current', kind: 'Queue', status: 'Running',
      label: 'En attente du worker' }], 'File actuelle')}
    {list(items.filter(item => item.version === workflow.version), `Version actuelle ${workflow.version}`)}
    <details className="technical-logs"><summary>Anciennes étapes et attentes historiques</summary>
      {list([...queueEvents, ...items.filter(item => item.version !== workflow.version)], 'Étapes historiques')}
    </details>
  </section>
}

function Revision({ id, version }: { id: string; version: number }) {
  const [comparison, setComparison] = useState<RevisionComparison | null>(null)
  const [error, setError] = useState<string | null>(null)
  async function load() {
    try { setError(null); setComparison(await api.revision(id, version)) }
    catch (failure) { setError(errorMessage(failure)) }
  }
  return <section className="technical-logs">
    <button className="button secondary" onClick={() => void load()}>Comparer la version {version}</button>
    {error && <p role="alert" className="notice danger">{error}</p>}
    {comparison && <div>
      <p>Version précédente : {comparison.previousVersion ?? 'aucune'} · {comparison.changes.length} fichier(s) modifié(s)
        {comparison.proposed && ' · proposition non appliquée'}</p>
      {comparison.review && <p>Revue de cette proposition : {comparison.review.summary}
        {comparison.review.recommendation && ` · ${comparison.review.recommendation}`}</p>}
      {comparison.changes.map(change => <details key={change.path}>
        <summary>{change.path}</summary>
        <p>Avant</p><pre>{change.before ?? '(absent)'}</pre>
        <p>Après</p><pre>{change.after ?? '(supprimé)'}</pre>
      </details>)}
    </div>}
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
        {plan.operations.some(operation => operation.includes('No automatic LLM correction.'))
          ? 'Aucune correction automatique'
          : `${plan.maxCorrectionAttempts} corrections maximum`} · réseau désactivé</p>
      <details><summary>Image et empreinte approuvées</summary><code>{plan.image}<br />{plan.fingerprint}</code></details>
      {plan.canonical && <details><summary>Plan canonique · version {plan.canonical.workflowVersion}</summary>
        <p>Étapes obligatoires : {plan.canonical.stages.map(stage => stage.name).join(' → ')}</p>
        {plan.canonical.stages.map(stage => <details key={stage.name}>
          <summary>{stage.name} · commande et isolation</summary><code>{JSON.stringify(stage.arguments)}</code>
        </details>)}
        <details><summary>Empreintes des fichiers ({plan.canonical.files.length})</summary>
          {plan.canonical.files.map(file => <p key={file.path}><code>{file.path} · {file.sha256}</code></p>)}
        </details>
      </details>}
      <div className="approval-actions">
        <button className="button primary" disabled={busy} onClick={() => void decide(true)}>Autoriser compilation et tests</button>
        <button className="button secondary" disabled={busy} onClick={() => void decide(false)}>Refuser l’exécution</button>
      </div>
    </> : error ? <button className="button secondary" onClick={() => setRevision(value => value + 1)}>Recharger le plan</button>
      : <p className="loading"><LoaderCircle className="spin" size={16} /> Chargement du plan réel…</p>}
  </section>
}
