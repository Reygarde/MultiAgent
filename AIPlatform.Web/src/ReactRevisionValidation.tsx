import { useEffect, useState } from 'react'
import { errorMessage, request } from './api'
import type { Mission } from './types'

interface ValidationPlan {
  missionId: string
  revisionVersion: number
  revisionFingerprint: string
  fileCount: number
  imageReference: string
  image: string
  timeoutSeconds: number
  fingerprint: string
  preparationEvidenceId?: string | null
  files?: { path: string; sha256: string }[] | null
  stages: { name: string; arguments: string[] }[]
}

interface Preparation {
  requestId: string
  expiresAt: string
  plan: ValidationPlan
}

interface ValidationReport {
  runId: string
  plan: ValidationPlan
  status: string
  error?: string
  startedAt: string
  results: Mission['verification']
}

const labels: Record<string, string> = { TypeScript: 'TypeScript', Build: 'Build Vite', Tests: 'Tests Vitest' }
const commands: Record<string, string> = { TypeScript: 'tsc --noEmit', Build: 'vite build', Tests: 'vitest run' }
const statuses: Record<string, string> = {
  Running: 'En cours', Succeeded: 'Réussi', Failed: 'Échec', Unavailable: 'Indisponible', Interrupted: 'Interrompu',
}

function ValidationResults({ results }: { results: ValidationReport['results'] }) {
  return <>{results.map(result => <details key={result.stage} className="technical-logs">
    <summary>{labels[result.stage] ?? result.stage} · {statuses[result.status] ?? result.status}
      {' · '}code de sortie : {result.exitCode ?? 'indisponible'}</summary>
    {result.totalTests != null && <p>{result.totalTests} tests · {result.failedTests ?? 0} échecs</p>}
    {result.diagnostics?.items.map((item, index) =>
      <p key={index}>{item.file}{item.line != null && `:${item.line}`} {item.code} : {item.message}</p>)}
    <pre>{result.log}</pre>
  </details>)}</>
}

interface CorrectionPlan {
  fingerprint: string
  model: string
  remainingGenerations: number
  contextLimit: number
  groups: { paths: string[]; contextCharacters: number }[]
}

export function ReactRevisionValidation({ id, canPrepare, currentRevision, onPublished }: {
  id: string; canPrepare: boolean; currentRevision?: string; onPublished?: () => Promise<unknown>
}) {
  const path = `/missions/${encodeURIComponent(id)}/react-revision-validation`
  const [preparation, setPreparation] = useState<Preparation | null>(null)
  const [reports, setReports] = useState<ValidationReport[]>([])
  const [historyRefresh, setHistoryRefresh] = useState(0)
  const [busy, setBusy] = useState<'prepare' | 'authorize' | 'plan' | 'correct' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [correctionText, setCorrectionText] = useState<string | null>(null)
  const [correctionPlan, setCorrectionPlan] = useState<CorrectionPlan | null>(null)
  const [publication, setPublication] = useState<{ version: number; changedPaths: string[] } | null>(null)
  const report = reports[0]
  const running = report?.status === 'Running'

  useEffect(() => {
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout>
    const load = () => {
      void request<{ reports: ValidationReport[] }>(`${path}/reports`, { signal: controller.signal, cache: 'no-store' })
        .then(({ reports: history }) => {
          if (controller.signal.aborted) return
          setReports(history)
          if (history[0]?.status === 'Running') timer = setTimeout(load, 2000)
        }).catch(reason => {
          if (!controller.signal.aborted) setError(errorMessage(reason))
        })
    }
    load()
    return () => { controller.abort(); clearTimeout(timer) }
  }, [path, historyRefresh])

  const prepare = async () => {
    setBusy('prepare'); setError(null); setPreparation(null)
    try { setPreparation(await request<Preparation>(`${path}/prepare`, { method: 'POST' })) }
    catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(null) }
  }
  const authorize = async (value: Preparation) => {
    setBusy('authorize'); setError(null); setPreparation(null)
    try {
      const completed = await request<ValidationReport>(`${path}/authorize`, {
        method: 'POST', body: JSON.stringify({ approved: true, requestId: value.requestId, fingerprint: value.plan.fingerprint }),
      })
      setReports(previous => [completed, ...previous.filter(item => item.runId !== completed.runId)])
    } catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(null); setHistoryRefresh(value => value + 1) }
  }
  const plan = preparation?.plan
  const canCorrect = canPrepare && report?.status === 'Failed' && report.plan.revisionFingerprint === currentRevision &&
    report.results.some(result => result.exitCode != null && result.exitCode !== 0)
  const correct = async () => {
    if (!report || !correctionText?.trim()) return
    const groups = correctionText.trim().split('\n').filter(line => line.trim()).map(line => {
      const separator = line.indexOf('|')
      return { paths: (separator < 0 ? line : line.slice(0, separator)).split(',').map(p => p.trim()),
        instructions: separator < 0 ? '' : line.slice(separator + 1).trim() }
    })
    const scope = { reportId: report.runId, revisionFingerprint: report.plan.revisionFingerprint, groups }
    setError(null); setBusy(correctionPlan ? 'correct' : 'plan')
    try {
      if (!correctionPlan) {
        setCorrectionPlan(await request<CorrectionPlan>(`${path}/correction-plan`, { method: 'POST', body: JSON.stringify(scope) }))
      } else {
        const published = await request<{ version: number; changedPaths: string[] }>(`${path}/correct`, {
          method: 'POST', body: JSON.stringify({ scope, planFingerprint: correctionPlan.fingerprint }),
        })
        setPublication(published); setCorrectionPlan(null); setCorrectionText(null); setPreparation(null)
        await onPublished?.()
      }
    } catch (reason) { setError(errorMessage(reason)); setCorrectionPlan(null) }
    finally { setBusy(null) }
  }
  return <section aria-label="Validation indépendante de la révision React">
    <h3>Validation indépendante de la révision</h3>
    <p>Résultats techniques uniquement : ils ne valident pas les critères fonctionnels et ne changent pas le statut de la mission.</p>
    <p>Pour une révision historique sans image figée, la préparation inspecte Docker et exige le manifeste installé vérifié.
      Aucun agent ni conteneur ne démarre avant une autorisation distincte.</p>
    {canPrepare && !plan && <button className="button secondary" disabled={busy !== null || running} onClick={() => void prepare()}>
      Valider cette révision
    </button>}
    {busy && <p role="status">{busy === 'prepare' || busy === 'plan' ? 'Préparation sans exécution…'
      : busy === 'correct' ? 'Correction Developer en cours, sans Docker…' : 'Validation Docker en cours…'}</p>}
    {preparation && plan && canPrepare && <div className="notice warning">
      <p>Mission <code>{plan.missionId}</code> · révision v{plan.revisionVersion} · {plan.fileCount} fichiers</p>
      <p>Empreinte de la révision : <code>{plan.revisionFingerprint}</code></p>
      <p>Empreinte d’exécution : <code>{plan.fingerprint}</code></p>
      {plan.preparationEvidenceId && <p>Preuve de préparation distincte : <code>{plan.preparationEvidenceId}</code></p>}
      <p>Image locale {plan.imageReference} : <code>{plan.image}</code></p>
      <p>Réseau et téléchargement désactivés ; racine et copie source en lecture seule ; utilisateur non privilégié,
        capacités supprimées, aucun port ni socket Docker. Répertoires temporaires bornés et délai de {plan.timeoutSeconds} s par étape.</p>
      <ul>{plan.stages.map(stage => <li key={stage.name}>
        <code>{commands[stage.name] ?? stage.name}</code>
        <details><summary>Commande et restrictions exactes</summary><pre>{stage.arguments.join('\n')}</pre></details>
      </li>)}</ul>
      <p>Autorisation à usage unique, valable jusqu’à {new Date(preparation.expiresAt).toLocaleTimeString('fr-FR')}.
        Aucun agent ni correction automatique ne sera lancé.</p>
      <button className="button primary" disabled={busy !== null || running}
        onClick={() => void authorize(preparation)}>Autoriser et exécuter dans Docker</button>
      {' '}<button className="button secondary" onClick={() => setPreparation(null)}>Fermer sans exécuter</button>
    </div>}
    {error && <p role="alert" className="notice danger">{error}</p>}
    {publication && <p role="status">Révision v{publication.version} publiée sans compilation. Fichiers modifiés : {publication.changedPaths.join(', ')}.</p>}
    {report && <div>
      <p>Dernière validation technique · révision v{report.plan.revisionVersion} · {statuses[report.status] ?? report.status}
        {' · '}{new Date(report.startedAt).toLocaleString('fr-FR')}</p>
      <p><code>{report.plan.revisionFingerprint}</code></p>
      {report.error && <p role="status">{report.error}</p>}
      {canCorrect && correctionText === null && <button className="button secondary" disabled={busy !== null}
        onClick={() => setCorrectionText('')}>Corriger depuis ce rapport</button>}
      {canCorrect && correctionText !== null && <div>
        <label>Groupes de correction (une ligne : chemin[, chemin] | consigne)
          <textarea value={correctionText} disabled={busy !== null} onChange={event => {
            setCorrectionText(event.target.value); setCorrectionPlan(null)
          }} placeholder="src/App.tsx | Décrire la correction ciblée" />
        </label>
        <p>Laissez vide pour proposer les fichiers éditables identifiés dans les diagnostics.</p>
        {correctionPlan && <div>
          <p>{correctionPlan.model} · {correctionPlan.groups.length} appels prévus / {correctionPlan.remainingGenerations} restants.
            Ni Planner, ni Reviewer, ni Docker.</p>
          <ul>{correctionPlan.groups.map(group => <li key={group.paths.join(',')}>
            {group.paths.join(', ')} · {group.contextCharacters}/{correctionPlan.contextLimit} caractères</li>)}</ul>
        </div>}
        <button className="button primary" disabled={busy !== null}
          onClick={() => void correct()}>{correctionPlan ? 'Lancer la correction Developer' : 'Préparer le plan de correction'}</button>
      </div>}
      <ValidationResults results={report.results} />
    </div>}
    {reports.length > 0 && <details>
      <summary>Historique des validations ({reports.length})</summary>
      {reports.map(item => <article key={item.runId}>
        <p>Révision v{item.plan.revisionVersion} · {statuses[item.status] ?? item.status}
          {' · '}{new Date(item.startedAt).toLocaleString('fr-FR')} · <code>{item.runId}</code></p>
        <p>{item.results.map(result => `${labels[result.stage] ?? result.stage} : ${statuses[result.status] ?? result.status}`).join(' · ')}</p>
        {item.error && <p>{item.error}</p>}
        <ValidationResults results={item.results} />
        <details><summary>Identités et empreintes</summary>
          <p>Révision : <code>{item.plan.revisionFingerprint}</code></p>
          <p>Plan : <code>{item.plan.fingerprint}</code></p>
          <p>Image : <code>{item.plan.image}</code></p>
          {item.plan.files?.length ? <ul>{item.plan.files.map(file =>
            <li key={file.path}>{file.path} · <code>{file.sha256}</code></li>)}</ul>
            : <p>Rapport historique : empreintes individuelles liées à la révision et au plan de préparation.</p>}
        </details>
      </article>)}
    </details>}
  </section>
}
