import { ArrowRight, Check, Circle, Clock3, FolderOpen, LoaderCircle, ShieldAlert, Square, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { api, errorMessage } from './api'
import { isActive, statusLabels, stepLabels, stepStatusLabels } from './types'
import type { ExecutionPlan, Mission, MissionEvent, PlannerAttemptDiagnostic, ReviewerCorrectionResumeEligibility, RevisionComparison, StepStatus } from './types'
import { formatDateTime, formatMissionDuration, formatTime, missionFinishedAt, missionTitle } from './missionPresentation'
import { RoutingExplanation } from './ModelLibrary'
import { ReactRevisionValidation } from './ReactRevisionValidation'

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

export function MissionDetails({ mission, cancel, busy, refresh, openProject }: {
  mission: Mission; cancel: (id: string) => void; busy: boolean; refresh: () => Promise<unknown>
  openProject?: (id: string) => void
}) {
  const [resumeError, setResumeError] = useState<string | null>(null)
  const [resuming, setResuming] = useState(false)
  const [reviewerResumeEligibility, setReviewerResumeEligibility] =
    useState<ReviewerCorrectionResumeEligibility | null>(null)
  const [reviewerResumeError, setReviewerResumeError] = useState<string | null>(null)
  const [resumingReviewer, setResumingReviewer] = useState(false)
  const canCheckReviewerResume = mission.status === 'Failed' && mission.templateId === 'react-typescript-vite-v2' &&
    mission.workflow?.steps.some(step => step.kind === 'Review' && step.status === 'Failed') === true
  const canRevalidate = mission.status === 'GeneratedUnverified' && mission.templateId === 'fullstack-react-dotnet' &&
    mission.workflow?.review != null && !mission.workflow.review.findings.some(finding => finding.severity === 'Error')
  const finishedAt = missionFinishedAt(mission)
  const duration = formatMissionDuration(mission.createdAt, finishedAt)
  useEffect(() => {
    let active = true
    setReviewerResumeEligibility(null)
    setReviewerResumeError(null)
    if (!canCheckReviewerResume) return () => { active = false }
    void api.reviewerCorrectionResumeEligibility(mission.id).then(result => {
      if (active) setReviewerResumeEligibility(result)
    }).catch(error => {
      if (active) setReviewerResumeError(errorMessage(error))
    })
    return () => { active = false }
  }, [canCheckReviewerResume, mission.id, mission.status, mission.updatedAt])
  return <section className="panel mission-details">
    <div className="panel-heading mission-detail-heading"><h2><Clock3 size={16} /> {missionTitle(mission)}</h2><StatusBadge mission={mission} /></div>
    <div className="mission-dates">
      <span>Créée <strong>{formatDateTime(mission.createdAt) ?? 'date inconnue'}</strong></span>
      {finishedAt && <span>Terminée <strong>{formatDateTime(finishedAt) ?? 'date inconnue'}</strong></span>}
      {duration && <span>Durée totale <strong>{duration}</strong></span>}
      {mission.hasWorkspace && openProject && <button className="text-button" onClick={() => openProject(mission.id)}>
        <FolderOpen size={14} /> Ouvrir le projet associé <ArrowRight size={14} />
      </button>}
    </div>
    <details className="technical-logs"><summary>Identifiant technique de la mission</summary><code>{mission.id}</code></details>
    {mission.templateId === 'discord-bot-dotnet' && <p>Template : Discord Bot v1 · Discord.Net.WebSocket 3.20.2 · xUnit 2.9.3</p>}
    {mission.templateId === 'twitch-bot-dotnet' && <p>Template : Twitch Bot v1 · .NET 10 · EventSub + Helix.
      Aucune dépendance applicative ; xUnit 2.9.3, runner 3.1.4, Test SDK 17.14.1.
      Validation Docker Release sans réseau ni compte Twitch. Connexion réelle non validée ; OAuth par environnement.</p>}
    {mission.templateId === 'react-typescript-vite' && <p>Template : React App v1 · React 19.3.1 · TypeScript 5.9.3 · Vite 6.4.6 · Vitest 4.1.12.
      Dépendances approuvées dans package.json ; versions transitives dans package-lock.json.
      Validation Docker : TypeScript → build Vite → tests Vitest, sans réseau.</p>}
    {mission.templateId === 'react-typescript-vite-v2' && <p>Template : React App v2 · scaffold technique sans contrat métier imposé.
      La revue des sources et les preuves de tests exécutés sont distinctes. Exécution isolée après autorisation uniquement.</p>}
    {mission.templateId === 'fullstack-react-dotnet' && <p>Template : Full-Stack App v1 · ASP.NET Core .NET 10 · SQLite 10.0.15 · React/TypeScript.
      BackendAgent puis FrontendAgent utilisent le même contrat OpenAPI. Validation après approbation : Release/xUnit,
      TypeScript, Vite, Vitest et appels HTTP réels sur loopback Docker ; réseau externe désactivé.</p>}
    <div className="assignment">
      <span>Agent <strong>{mission.selectedAgent ?? 'En attente du routage'}</strong></span>
      <span>Modèle <strong>{mission.selectedModel ?? 'Non sélectionné'}</strong></span>
    </div>
    {mission.sizing && <section aria-label="Dimensionnement figé">
      <h4>{mission.sizing.name}</h4>
      <p>{mission.sizing.maxPlannedFiles} fichiers proposés / {mission.sizing.maxFiles} au total ;
        {' '}{mission.sizing.maxFileBytes / 1024} Kio/fichier, {mission.sizing.maxProjectBytes / 1024} Kio/projet ;
        {' '}{mission.sizing.maxCriteria} critères, {mission.sizing.maxCorrections} corrections ;
        {' '}{mission.sizing.maxRepairGroups} groupes de {mission.sizing.maxFilesPerRepairGroup} fichiers ;
        {' '}{mission.sizing.missionTimeoutSeconds} s par passage du worker.</p>
      <p>Limites enregistrées à la création ; budget et autorisations indépendants.</p>
    </section>}
    {mission.reason && <p className="routing-reason">{mission.reason}</p>}
    {mission.usedFallback && <div className="notice warning">Routage de secours déterministe utilisé.</div>}
    <p className="muted">Corrections effectuées : {mission.correctionAttempts}
      {mission.templateId === 'fullstack-react-dotnet'
        ? ' · correction automatique désactivée'
        : mission.workflow?.correctionLimit != null && ` · restantes : ${Math.max(0, mission.workflow.correctionLimit - mission.correctionAttempts)}`}</p>
    <section aria-label="Budget des générations">
      {mission.maxTotalGenerations != null ? <>
        <p>Générations réservées : {mission.totalGenerations ?? 0} / {mission.maxTotalGenerations}
          {' · '}restantes : {mission.remainingGenerations ?? 0}</p>
        <p className="muted">Une réservation n’est jamais remboursée automatiquement, même après échec ou annulation.</p>
        {!!mission.generationReservations?.length && <details><summary>Historique des générations par rôle</summary>
          <ul>{mission.generationReservations.map((reservation, index) => <li key={reservation.id}>
            {index + 1}. {reservation.role} · {reservation.model} · {formatDateTime(reservation.reservedAt)}
          </li>)}</ul>
        </details>}
      </> : <p className="muted">Mission historique : aucun plafond global enregistré ; compteurs antérieurs inchangés.</p>}
      {mission.generationBudgetWarning && <p className="notice warning" role="status">{mission.generationBudgetWarning}</p>}
    </section>
    {mission.status === 'AwaitingApproval' &&
      <Approval key={`${mission.id}:${mission.workflow?.version ?? 'legacy'}`} id={mission.id} refresh={refresh} />}
    {mission.status === 'AwaitingPlanApproval' &&
      <PlanApproval key={`${mission.id}:${mission.workflow?.planApproval?.fingerprint}`} mission={mission} refresh={refresh} />}
    {mission.templateId === 'react-typescript-vite-v2' && !mission.deleted && mission.hasWorkspace &&
      !!mission.workflow?.revisions.length &&
      <ReactRevisionValidation key={mission.id} id={mission.id} canPrepare={!isActive(mission)}
        currentRevision={mission.workflow?.revisions.at(-1)?.fingerprint} onPublished={refresh} />}
    {mission.workflow && <section aria-label="Workflow collaboratif">
      <h3>Workflow · version {mission.workflow.version}</h3>
      {mission.workflow.automaticReactCorrections && <section aria-label="Corrections logicielles autonomes">
        <h4>Corrections logicielles autonomes</h4>
        <p>AST puis Developer et réévaluation Reviewer ; limite partagée : {mission.workflow.correctionLimit ?? 'non renseignée'}.
          Aucune exécution du projet sur l’hôte.</p>
        {mission.workflow.pendingSoftwareAgentCall && <p role="status">Opération en cours ou résultat à clarifier :
          {' '}{mission.workflow.pendingSoftwareAgentCall}. Un appel ambigu ne sera pas rejoué automatiquement.</p>}
        {mission.workflow.softwareCorrections?.map(correction => <article key={correction.id}>
          <p>{correction.trigger} · {correction.completedGroups}/{correction.totalGroups} groupes Developer contrôlés
            {' · '}{correction.reviewerCapacity} appels Reviewer prévus.</p>
          <p>{correction.inFlight ? 'Correction en cours ou interrompue sans résultat confirmé.'
            : correction.ready ? 'Candidat statiquement contrôlé ; ce n’est pas une preuve d’exécution.'
              : 'Préparation ou reprise du candidat isolé.'}</p>
          <p>{correction.reason}</p>
          {correction.failure && <p role="alert">Correction refusée : {correction.failure}</p>}
          <ul>{correction.decisions.map((decision, index) => <li key={index}>{decision}</li>)}</ul>
        </article>)}
        <p>{mission.status === 'AwaitingApproval' ? 'Décision requise : autoriser Docker pour cette révision et son empreinte.'
          : mission.status === 'AwaitingPlanApproval' ? 'Décision requise : approuver le plan avant Developer.'
            : mission.error ?? mission.workflow.finalReport ?? 'Les étapes logicielles autorisées s’enchaînent automatiquement.'}</p>
        <p>Validations réellement exécutées : {mission.verification.length
          ? mission.verification.map(result => `${result.stage} : ${result.status}`).join(' · ')
          : 'aucune compilation ni aucun test du projet exécuté.'}</p>
      </section>}
      <p>Sélection des modèles : {mission.workflow.automaticModelSelection ? 'automatique (heuristique)' : 'manuelle'}</p>
      {!!mission.workflow.executionPlanDifferences?.length && <section aria-label="Différences du plan d’exécution">
        <h4>Plan d’exécution différent du plan persisté</h4>
        {mission.workflow.executionPlanDifferences.map((difference, index) =>
          <p key={`${difference.field}-${index}`}>
            <code>{difference.field}</code> · attendu : {difference.expected ?? 'absent'}
            {' · '}actuel : {difference.actual ?? 'absent'}
          </p>)}
      </section>}
      {mission.workflow.planApproval?.approved != null && <p>
        {mission.workflow.planApproval.approved
          ? 'Plan de génération approuvé ; autorisation d’exécution distincte.'
          : 'Plan refusé ; aucun Developer ni Reviewer lancé.'}
      </p>}
      <details className="technical-logs"><summary>Identifiant du workflow</summary><code>{mission.workflow.workflowId}</code></details>
      {mission.templateId === 'react-typescript-vite-v2' &&
        <PlannerDiagnostics key={mission.id} mission={mission} />}
      {!!mission.workflow.requirementTrace?.length && <section aria-label="Exigences originales conservées">
        <h3>Exigences originales conservées</h3>
        <p>La classification ne supprime pas le texte d’origine ; les catégories indiquent comment il est traité par le Planner.</p>
        {mission.workflow.requirementTrace.map(item =>
          <div key={item.id}>
            <p>{item.originalText}</p>
            <p>Catégories : {item.categories.join(', ')}</p>
            {item.criterionIds.length > 0 && <p>Critères associés : {item.criterionIds.join(', ')}</p>}
            {item.diagnostic && <p role="status">{item.diagnostic}</p>}
          </div>)}
      </section>}
      {!!mission.workflow.acceptanceCriteria?.length && <section aria-label="Critères originaux de la mission">
        <h3>Critères originaux · revue statique et exécution</h3>
        <p>Les citations du Reviewer sont une analyse statique, pas une preuve de test exécuté.</p>
        {mission.workflow.acceptanceCriteria.map(criterion => {
          const review = mission.workflow!.review?.criterionReviews?.find(item => item.criterionId === criterion.id)
          const executions = mission.workflow!.criterionExecutions?.filter(item =>
            item.criterionId === criterion.id && item.version === mission.workflow!.version) ?? []
          return <div key={criterion.id}>
            <h4>{criterion.id} · {criterion.requirement}</h4>
            <p>Revue statique : {review?.verdict === 'Satisfied' ? 'Satisfait avec citation de source'
              : review?.verdict === 'NotSatisfied' ? 'Non satisfait' : 'Non vérifié'}</p>
            {review && <p>{review.explanation}</p>}
            {review?.defect && <p>Défaut déclaré : {review.defect}</p>}
            {review?.uncertainty && <p>Incertitude : {review.uncertainty}</p>}
            {review?.evidence.map((item, index) => <p key={index}>Source : {item.path} · <code>{item.quote}</code></p>)}
            {executions.length === 0 ? <p>Exécution fonctionnelle : non vérifiée · aucun résultat de test correspondant.</p>
              : executions.map((item, index) => <p key={index}>Test exécuté : {item.testName} · {item.testFile}
                {' · '}{item.provenance} · tentative {item.attempt} · source {item.sourceFingerprint}</p>)}
          </div>
        })}
      </section>}
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
        <p>Modèle : {step.modelName ?? (step.selectionMode === 'Automatic'
          ? 'Aucune génération exécutée' : 'C# déterministe (ou ancienne étape)')}</p>
        <p>Mode : {step.selectionMode === 'Automatic' ? 'automatique' : 'manuel / déterministe'}</p>
        {step.modelSelections?.map(selection => <RoutingExplanation key={selection.key} decision={selection.decision} />)}
        {step.modelInvocations?.map(({ attempt }) => <div key={attempt.id}>
          <p>Modèle {attempt.outcome === 'Blocked' ? 'bloqué avant génération' : 'appelé'} : {attempt.modelName} · {attempt.outcome}</p>
          <p>{attempt.selectionReason}</p>
          <p>Compilation : {attempt.compilationStatus} · Tests : {attempt.testsStatus ?? 'Unavailable'}.
            Critères fonctionnels : non déduits d’un build.</p>
          {attempt.failureKind && <p>Motif : {attempt.failureKind}</p>}
        </div>)}
        <p>Contexte transmis : {step.input ?? 'Pas encore exécuté'}</p>
        {step.output && <pre>{step.output}</pre>}
        {step.error && <div role="alert" className="notice danger">{step.error}</div>}
      </details>)}
      {canCheckReviewerResume && <section aria-label="Reprise ciblée de correction Reviewer">
        <h3>Correction du rapport Reviewer</h3>
        <p>Cette action conserve l’étape, le plan, les sources et les budgets ; elle ne relance ni Planner ni Developer.</p>
        {reviewerResumeEligibility
          ? <p role="status">{reviewerResumeEligibility.reason}</p>
          : reviewerResumeError
            ? <p role="alert" className="notice danger">{reviewerResumeError}</p>
            : <p role="status">Vérification de l’éligibilité et de l’intégrité des sources…</p>}
        {reviewerResumeEligibility?.available && <button className="button primary full"
          disabled={resumingReviewer || busy} onClick={() => {
            setResumingReviewer(true); setReviewerResumeError(null)
            void api.resumeReviewerCorrection(mission.id).then(refresh).catch(error =>
              setReviewerResumeError(errorMessage(error))).finally(() => setResumingReviewer(false))
          }}>{resumingReviewer ? 'Mise en file…' : 'Reprendre la correction Reviewer'}</button>}
      </section>}
      {!isActive(mission) && !mission.deleted && !['Verified', 'Completed'].includes(mission.status) &&
        (!mission.workflow.requirePlanApproval || !mission.workflow.plan || mission.workflow.planApproval?.approved === true) &&
        !reviewerResumeEligibility?.available && !reviewerResumeEligibility?.inProgress &&
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

function PlanApproval({ mission, refresh }: { mission: Mission; refresh: () => Promise<unknown> }) {
  const [deciding, setDeciding] = useState(false)
  const [submitted, setSubmitted] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const flow = mission.workflow
  const plan = flow?.plan
  const approval = flow?.planApproval
  const planner = flow?.steps.find(step => step.kind === 'Plan')
  async function decide(approved: boolean) {
    if (!approval || deciding || submitted) return
    setDeciding(true); setError(null)
    try {
      await api.decidePlanApproval(mission.id, approved, approval.fingerprint)
      setSubmitted(true)
      await refresh()
    } catch (failure) { setError(errorMessage(failure)) }
    finally { setDeciding(false) }
  }
  if (!plan || !approval) return <p role="alert">Plan ou empreinte indisponible : approbation impossible. Actualisez la mission.</p>
  const criteria = plan.acceptanceCriteria ?? flow?.acceptanceCriteria ?? []
  return <section aria-label="Approbation du plan" className="notice warning">
    <h3>Valider le plan avant Developer</h3>
    <p>Aucun Developer ou Reviewer ne démarre avant votre approbation. Consulter ce plan ne reprend pas la mission.</p>
    <p>Cette décision autorise uniquement la génération. Compilation, tests et exécution restent soumis à une autorisation distincte.</p>
    {flow?.planAmendments?.map(amendment => <div key={amendment.preparedAt}>
      <h4>Amendement assisté · pas de nouveau Planner</h4>
      <p>Plan v{amendment.previousVersion} conservé · ancienne empreinte : <code>{amendment.previousApproval.fingerprint}</code></p>
      <p>Developer préparé : {amendment.developerModel} · {amendment.compatibility.status}.
        {' '}{amendment.compatibility.reason}</p>
      <p>Fichiers conservés sans modification : {amendment.retainedPaths.join(', ')}.
        Conservation ne signifie pas compatibilité ou compilation réussie.</p>
      <p>Fichiers restants : {amendment.remainingPaths.join(', ')}.</p>
      {amendment.retainedSourceDiagnostics.map((issue, index) =>
        <p role="status" key={index}>{issue.file}:{issue.line} · {issue.code} · {issue.message}</p>)}
      <details><summary>Plan précédent conservé</summary><pre>{JSON.stringify(amendment.previousPlan, null, 2)}</pre></details>
    </div>)}
    <p>Modèle Planner : {planner?.modelName ?? 'Non renseigné'} · Générations consommées : {
      planner?.modelInvocations?.length || planner?.attempts || 0}</p>
    <p>Version du plan : {approval.version} · Empreinte : <code>{approval.fingerprint}</code></p>
    <p>{plan.originalRequest ?? mission.task}</p>
    <h4>Fichiers prévus</h4>
    <ul>{plan.files.map(file => <li key={file.path}>{file.path} · {file.purpose}
      {file.dependsOn.length > 0 && ` · Prérequis : ${file.dependsOn.join(', ')}`}</li>)}</ul>
    <h4>Critères et couverture prévue · aucune preuve d’exécution</h4>
    {criteria.length === 0 && <p>Ce contrat ne déclare pas de critères structurés ; consultez la demande et le plan complets.</p>}
    {criteria.map(criterion => {
      const coverage = plan.criterionCoverage?.find(item => item.criterionId === criterion.id)
      const isTest = (path: string) => path.endsWith('.test.ts') || path.endsWith('.test.tsx')
      return <div key={criterion.id}>
        <p>{criterion.id} · {criterion.requirement}</p>
        <p>Implémentation prévue : {coverage?.paths.filter(path => !isTest(path)).join(', ') || 'Non déclarée'}</p>
        <p>Tests prévus : {coverage?.paths.filter(isTest).join(', ') || 'Non déclarés'}</p>
        {coverage && <p>{coverage.explanation}</p>}
      </div>
    })}
    <details className="technical-logs"><summary>Plan complet</summary><pre>{JSON.stringify(plan, null, 2)}</pre></details>
    <button className="button primary" disabled={deciding || submitted} onClick={() => void decide(true)}>Approuver et continuer</button>
    <button className="button secondary" disabled={deciding || submitted} onClick={() => void decide(false)}>Refuser le plan</button>
    {submitted && <p>Décision enregistrée ; actualisation de la mission en cours.</p>}
    {error && <p role="alert">{error}</p>}
  </section>
}

function PlannerDiagnostics({ mission }: { mission: Mission }) {
  const [attempts, setAttempts] = useState<PlannerAttemptDiagnostic[] | null>(null)
  const [raw, setRaw] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  async function load() {
    setLoading(true); setError(null)
    try { setAttempts(await api.plannerDiagnostics(mission.id)) }
    catch (failure) { setError(errorMessage(failure)) }
    finally { setLoading(false) }
  }
  return <section aria-label="Diagnostics privés du Planner">
    <h3>Historique privé du Planner</h3>
    <p>Copies de diagnostic uniquement : ni plans approuvés ni autorisations d’exécution.
      Conservation limitée par la taille, la rétention et la suppression des traces privées.</p>
    <button className="button secondary" disabled={loading || isActive(mission)} onClick={() => void load()}>
      Consulter les diagnostics Planner
    </button>
    {attempts?.length === 0 && <p>Aucun diagnostic conservé : mission antérieure, rétention expirée ou suppression.</p>}
    {attempts?.map(attempt => <details className="technical-logs" key={attempt.attemptId}>
      <summary>Planner · {attempt.model} · tentative {attempt.attemptNumber} · {attempt.status}</summary>
      <p>Mission {attempt.missionId} · run {attempt.runId} · tentative {attempt.attemptId}</p>
      <p>Résultat structuré non exécutable :</p><pre>{JSON.stringify(attempt.plan, null, 2)}</pre>
      <ul>{attempt.criteria.map(criterion => <li key={criterion.id}>{criterion.id} : {criterion.requirement}</li>)}</ul>
      {attempt.errors.map((item, index) => <p key={index}>
        {item.criterionId ?? 'Plan'} · {item.rule} · champ {item.field}{item.path && ` · ${item.path}`} : {item.explanation}
      </p>)}
      {attempt.rawTraceAvailable ? <button className="text-button" onClick={() => {
        setError(null)
        void api.plannerRawResponse(mission.id, attempt.attemptId)
          .then(result => setRaw(previous => ({ ...previous, [attempt.attemptId]: result.responseBody })))
          .catch(failure => setError(errorMessage(failure)))
      }}>Consulter la réponse brute privée</button> : <p>Aucune réponse brute disponible ou autorisée.</p>}
      {raw[attempt.attemptId] && <pre>{raw[attempt.attemptId]}</pre>}
    </details>)}
    {!!attempts?.length && <button className="text-button" onClick={() => setConfirmDelete(true)}>
      Supprimer les diagnostics privés
    </button>}
    {confirmDelete && <button className="button danger" onClick={() => {
      setError(null)
      void api.deletePlannerDiagnostics(mission.id).then(() => {
        setAttempts([]); setRaw({}); setConfirmDelete(false)
      }).catch(failure => setError(errorMessage(failure)))
    }}>Confirmer la suppression des diagnostics uniquement</button>}
    {error && <div role="alert">{error}</div>}
  </section>
}

interface TimelineItem {
  id: string; kind: string; status: StepStatus | 'Archived'; label: string
  message?: string | null; version?: number | null; timestamp?: string | null; duration?: number | null
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
      message: event.message.length > 180 ? `${event.message.slice(0, 177)}…` : event.message,
      timestamp: event.timestamp, duration: event.durationMilliseconds }
  }
  function list(items: TimelineItem[], name: string) {
    return <ol className="steps" aria-label={name}>{items.map(item => <li key={item.id} className={`step-${item.status}`}>
      <span className="step-icon"><StepIcon status={item.status} /></span>
      <div><strong>{stepLabels[item.kind] ?? item.kind}
        {item.version != null && ` · v${item.version}`}</strong><span>{item.label}</span>
        {item.message && <span className="timeline-result">{item.message}</span>}</div>
      <time>{item.duration != null ? `${(item.duration / 1000).toFixed(1)} s`
        : formatTime(item.timestamp) ?? ''}</time>
    </li>)}</ol>
  }
  if (!mission.workflow) {
    return <section aria-label="Chronologie"><h3>Chronologie</h3>
      {mission.events.length ? list(mission.events.map(eventItem), 'Étapes de la mission')
        : <p className="muted">Aucun événement enregistré.</p>}</section>
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
    const result = step.error ?? step.output ?? null
    return { id: step.id, kind: step.kind, version: step.version, status, label,
      message: result ? result.length > 180 ? `${result.slice(0, 177)}…` : result : null,
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
