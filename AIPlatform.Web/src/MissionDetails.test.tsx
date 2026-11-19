import { render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import userEvent from '@testing-library/user-event'
import { api } from './api'
import { MissionDetails } from './MissionDetails'
import { mission } from './test/fixtures'
import type { ModelCompatibility } from './modelLibraryApi'
import type { Mission } from './types'

const waitingForPlan: Mission = {
  ...mission, status: 'AwaitingPlanApproval', hasWorkspace: false, sandboxApproved: false, verification: [],
  workflow: {
    workflowId: 'workflow', missionId: mission.id, version: 1, requirePlanApproval: true,
    planApproval: { version: 1, fingerprint: 'saved-plan-fingerprint', approved: null, decidedAt: null },
    plan: { name: 'Requested interface', description: 'Requested behavior', decisions: ['Keep it simple'],
      originalRequest: 'Display the requested information',
      acceptanceCriteria: [{ id: 'C1', requirement: 'Display the information' }],
      criterionCoverage: [{ criterionId: 'C1', paths: ['src/App.tsx', 'src/App.test.tsx'], explanation: 'Render and check visible information' }],
      files: [{ path: 'src/App.tsx', purpose: 'Interface', dependsOn: [] },
        { path: 'src/App.test.tsx', purpose: 'Business tests', dependsOn: ['src/App.tsx'] }] },
    review: null, revisions: [], finalReport: null,
    steps: [{ id: 'plan', kind: 'Plan', agentId: 'PlannerAgent', dependsOn: [], status: 'Completed',
      attempts: 1, modelName: 'simulated-planner', durationMilliseconds: 10, input: null, output: null, error: null }],
  },
}

describe('MissionDetails', () => {
  it('shows frozen sizing limits separately from the generation budget', () => {
    render(<MissionDetails mission={{ ...waitingForPlan,
      sizing: { id: 'saved', name: 'Profil enregistré', maxFiles: 64, maxPlannedFiles: 32,
        maxFileBytes: 131072, maxProjectBytes: 1048576, maxCriteria: 64, maxCorrections: 6,
        maxRepairGroups: 12, maxFilesPerRepairGroup: 4, missionTimeoutSeconds: 3600 },
    }} cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
    const panel = screen.getByRole('region', { name: 'Dimensionnement figé' })
    expect(panel).toHaveTextContent('32 fichiers proposés / 64 au total')
    expect(panel).toHaveTextContent('128 Kio/fichier')
    expect(panel).toHaveTextContent('3600 s par passage du worker')
    expect(panel).toHaveTextContent('budget et autorisations indépendants')
  })

  it('shows autonomous progress and explicit blockers without claiming execution', () => {
    render(<MissionDetails mission={{ ...waitingForPlan, workflow: {
      ...waitingForPlan.workflow!, automaticReactCorrections: true, correctionLimit: 3,
      pendingSoftwareAgentCall: 'review-2',
      softwareCorrections: [{ id: 'repair', trigger: 'correct-2', reason: 'Missing behavior',
        completedGroups: 1, totalGroups: 2, reviewerCapacity: 1, inFlight: false, ready: false,
        decisions: ['Patches: src/App.tsx; measured bounded context'] }],
    } }} cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
    const panel = screen.getByRole('region', { name: 'Corrections logicielles autonomes' })
    expect(panel).toHaveTextContent('1/2 groupes Developer contrôlés')
    expect(panel).toHaveTextContent('1 appels Reviewer prévus')
    expect(panel).toHaveTextContent('approuver le plan avant Developer')
    expect(panel).toHaveTextContent('aucune compilation ni aucun test du projet exécuté')
    expect(panel).toHaveTextContent('Un appel ambigu ne sera pas rejoué automatiquement')
  })

  it('displays the assisted amendment, retained errors and new approval without starting work', () => {
    const decide = vi.spyOn(api, 'decidePlanApproval').mockResolvedValue()
    render(<MissionDetails mission={{ ...waitingForPlan, workflow: {
      ...waitingForPlan.workflow!,
      planAmendments: [{
        previousVersion: 1, preparedAt: '2026-01-01T12:00:00Z',
        previousPlan: waitingForPlan.workflow!.plan!, previousApproval: { fingerprint: 'old-plan' },
        developerModel: 'coder:7b', compatibility: { status: 'Compatible', reason: 'Measured resources' },
        retainedPaths: ['src/model.ts'], remainingPaths: ['src/App.tsx', 'src/App.test.tsx'],
        retainedSourceDiagnostics: [{ file: 'src/model.ts', line: 22, code: 'TS1005', message: 'Expected comma' }],
      }],
    } }} cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
    const panel = screen.getByRole('region', { name: 'Approbation du plan' })
    expect(panel).toHaveTextContent('Amendement assisté · pas de nouveau Planner')
    expect(panel).toHaveTextContent('old-plan')
    expect(panel).toHaveTextContent('coder:7b')
    expect(panel).toHaveTextContent('TS1005')
    expect(panel).toHaveTextContent('Conservation ne signifie pas compatibilité')
    expect(decide).not.toHaveBeenCalled()
  })

  it('shows the preserved requirement trace and normalization diagnostics', () => {
    const tracedMission: Mission = {
      ...waitingForPlan,
      templateId: 'react-typescript-vite-v2',
      workflow: {
        ...waitingForPlan.workflow!,
        requirementTrace: [{
          id: 'R1', originalText: '- Use a particular visual style', normalizedText: 'Use a particular visual style',
          categories: ['Ambiguous'], criterionIds: ['C1'],
          diagnostic: 'Exigence conservée pour interprétation par le Planner.',
        }],
      },
    }

    render(<MissionDetails mission={tracedMission} cancel={vi.fn()} busy={false} refresh={vi.fn()} />)

    const trace = screen.getByRole('region', { name: 'Exigences originales conservées' })
    expect(trace).toHaveTextContent('- Use a particular visual style')
    expect(trace).toHaveTextContent('Ambiguous')
    expect(trace).toHaveTextContent('C1')
    expect(within(trace).getByRole('status')).toHaveTextContent('interprétation par le Planner')
  })

  it('shows frozen usage, remaining units and role history without pretending reservations are passed tests', () => {
    render(<MissionDetails mission={{ ...mission, maxTotalGenerations: 10, totalGenerations: 2, remainingGenerations: 8,
      generationBudgetWarning: 'GenerationBudgetRisk: insufficient remaining budget',
      generationReservations: [
        { id: 'one', role: 'Planner', model: 'planner:local', reservedAt: mission.createdAt },
        { id: 'two', role: 'Developer', model: 'coder:local', reservedAt: mission.createdAt },
      ] }} cancel={vi.fn()} busy={false} refresh={vi.fn()} />)
    const budget = screen.getByRole('region', { name: 'Budget des générations' })
    expect(budget).toHaveTextContent('Générations réservées : 2 / 10 · restantes : 8')
    expect(budget).toHaveTextContent('Planner · planner:local')
    expect(budget).toHaveTextContent('Developer · coder:local')
    expect(budget).toHaveTextContent('jamais remboursée automatiquement')
    expect(within(budget).getByRole('status')).toHaveTextContent('GenerationBudgetRisk')
  })

  afterEach(() => vi.restoreAllMocks())

  it.each([true, false])('requires an explicit fingerprint-bound plan decision, approve: %s', async approved => {
    const decide = vi.spyOn(api, 'decidePlanApproval').mockResolvedValue()
    const resume = vi.spyOn(api, 'resume').mockResolvedValue()
    const execute = vi.spyOn(api, 'approve').mockResolvedValue()
    const refresh = vi.fn().mockResolvedValue(undefined)
    render(<MissionDetails mission={waitingForPlan} cancel={vi.fn()} busy={false} refresh={refresh} />)
    const panel = screen.getByRole('region', { name: 'Approbation du plan' })
    expect(within(panel).getByText(/Modèle Planner : simulated-planner/)).toHaveTextContent('Générations consommées : 1')
    expect(within(panel).getByText(/Implémentation prévue/)).toHaveTextContent('src/App.tsx')
    expect(within(panel).getByText(/Tests prévus/)).toHaveTextContent('src/App.test.tsx')
    expect(within(panel).getByText(/Cette décision autorise uniquement la génération/)).toBeVisible()
    await userEvent.click(within(panel).getByText('Plan complet'))
    expect(within(panel).getByText(/"criterionCoverage"/)).toBeVisible()
    expect(decide).not.toHaveBeenCalled()
    expect(resume).not.toHaveBeenCalled()
    expect(execute).not.toHaveBeenCalled()
    await userEvent.click(within(panel).getByRole('button', { name: approved ? 'Approuver et continuer' : 'Refuser le plan' }))
    expect(await within(panel).findByText(/Décision enregistrée/)).toBeVisible()
    expect(decide).toHaveBeenCalledExactlyOnceWith(mission.id, approved, 'saved-plan-fingerprint')
    expect(refresh).toHaveBeenCalledOnce()
    expect(within(panel).getByRole('button', { name: 'Approuver et continuer' })).toBeDisabled()
    expect(within(panel).getByRole('button', { name: 'Refuser le plan' })).toBeDisabled()
    expect(execute).not.toHaveBeenCalled()
  })

  it('reports a stale-plan refusal without silently continuing or authorizing execution', async () => {
    vi.spyOn(api, 'decidePlanApproval').mockRejectedValue(new Error('Plan obsolète (HTTP 409)'))
    const refresh = vi.fn()
    render(<MissionDetails mission={waitingForPlan} cancel={vi.fn()} busy={false} refresh={refresh} />)
    await userEvent.click(screen.getByRole('button', { name: 'Approuver et continuer' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Plan obsolète (HTTP 409)')
    expect(refresh).not.toHaveBeenCalled()
  })

  it('offers the distinct Reviewer correction retry only when the server confirms eligibility', async () => {
    const eligibility = vi.spyOn(api, 'reviewerCorrectionResumeEligibility').mockResolvedValue({
      available: true, inProgress: false, reason: 'Sources et rapport rejeté identiques.',
    })
    const resumeReviewer = vi.spyOn(api, 'resumeReviewerCorrection').mockResolvedValue()
    const generalResume = vi.spyOn(api, 'resume').mockResolvedValue()
    const refresh = vi.fn().mockResolvedValue(undefined)
    const failed: Mission = {
      ...mission, status: 'Failed', templateId: 'react-typescript-vite-v2', error: 'Reviewer context budget exceeded',
      workflow: {
        workflowId: 'workflow', missionId: mission.id, version: 1, plan: null, review: null,
        revisions: [], finalReport: null, steps: [{
          id: 'review-1', kind: 'Review', agentId: 'ReviewAgent', dependsOn: [], status: 'Failed',
          attempts: 1, durationMilliseconds: null, input: null, output: null, error: 'Reviewer context budget exceeded',
        }],
      },
    }
    render(<MissionDetails mission={failed} cancel={vi.fn()} busy={false} refresh={refresh} />)

    const panel = screen.getByRole('region', { name: 'Reprise ciblée de correction Reviewer' })
    expect(await within(panel).findByText('Sources et rapport rejeté identiques.')).toBeVisible()
    expect(eligibility).toHaveBeenCalledWith(mission.id)
    expect(screen.queryByRole('button', { name: 'Reprendre · nouvelle autorisation requise' })).not.toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('button', { name: 'Reprendre la correction Reviewer' }))
    expect(resumeReviewer).toHaveBeenCalledWith(mission.id)
    expect(generalResume).not.toHaveBeenCalled()
    expect(refresh).toHaveBeenCalledOnce()
  })

  it('shows the ineligibility reason and keeps general resume separate', async () => {
    vi.spyOn(api, 'reviewerCorrectionResumeEligibility').mockResolvedValue({
      available: false, inProgress: false, reason: 'Le rapport privé est absent.',
    })
    const failed: Mission = {
      ...mission, status: 'Failed', templateId: 'react-typescript-vite-v2',
      workflow: {
        workflowId: 'workflow', missionId: mission.id, version: 1, plan: null, review: null,
        revisions: [], finalReport: null, steps: [{
          id: 'review-1', kind: 'Review', agentId: 'ReviewAgent', dependsOn: [], status: 'Failed',
          attempts: 1, durationMilliseconds: null, input: null, output: null, error: 'Reviewer context budget exceeded',
        }],
      },
    }
    render(<MissionDetails mission={failed} cancel={vi.fn()} busy={false} refresh={async () => undefined} />)

    const panel = screen.getByRole('region', { name: 'Reprise ciblée de correction Reviewer' })
    expect(await within(panel).findByText('Le rapport privé est absent.')).toBeVisible()
    expect(within(panel).queryByRole('button', { name: 'Reprendre la correction Reviewer' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reprendre · nouvelle autorisation requise' })).toBeVisible()
  })

  it('loads rejected plans explicitly and keeps raw response inspection and deletion separate', async () => {
    const inspect = vi.spyOn(api, 'plannerDiagnostics').mockResolvedValue([{
      missionId: mission.id, runId: 'private-run', attemptId: 'attempt', attemptNumber: 1,
      model: 'simulated-planner', createdAt: mission.createdAt, status: 'Rejected',
      plan: { name: 'Rejected counter', files: [] }, criteria: [{ id: 'C1', requirement: 'Initial value 0' }],
      errors: [{ criterionId: 'C1', rule: 'MissingTest', field: 'criterionCoverage.paths',
        path: null, explanation: 'A business test is required.' }], rawTraceAvailable: true,
    }])
    const raw = vi.spyOn(api, 'plannerRawResponse').mockResolvedValue({ responseBody: 'private raw response' })
    const remove = vi.spyOn(api, 'deletePlannerDiagnostics').mockResolvedValue()
    render(<MissionDetails mission={{ ...mission, status: 'Failed', templateId: 'react-typescript-vite-v2', workflow: {
      workflowId: 'workflow', missionId: mission.id, version: 1, steps: [], plan: null, review: null,
      revisions: [], finalReport: null,
    } }} cancel={vi.fn()} busy={false} refresh={async () => undefined} />)
    expect(inspect).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Consulter les diagnostics Planner' }))
    const summary = await screen.findByText('Planner · simulated-planner · tentative 1 · Rejected')
    await userEvent.click(summary)
    expect(screen.getByText(/C1 · MissingTest · champ criterionCoverage.paths/)).toBeInTheDocument()
    expect(screen.getByText(/"name": "Rejected counter"/)).toBeInTheDocument()
    expect(raw).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Consulter la réponse brute privée' }))
    expect(await screen.findByText('private raw response')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Supprimer les diagnostics privés' }))
    expect(remove).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Confirmer la suppression des diagnostics uniquement' }))
    expect(await screen.findByText(/Aucun diagnostic conservé/)).toBeInTheDocument()
    expect(remove).toHaveBeenCalledWith(mission.id)
    expect(screen.queryByText('private raw response')).not.toBeInTheDocument()
  })

  it('distinguishes static criterion declarations from current-version executed test records', () => {
    render(<MissionDetails mission={{ ...mission, templateId: 'react-typescript-vite-v2', status: 'GeneratedUnverified',
      workflow: {
        workflowId: 'workflow', missionId: mission.id, version: 2, steps: [], plan: null, revisions: [], finalReport: null,
        acceptanceCriteria: [{ id: 'C1', requirement: 'Initial value 0' }, { id: 'C2', requirement: 'Increment' }],
        executionPlanDifferences: [{ field: 'sandbox.memoryMegabytes', expected: '2048', actual: '1024' }],
        review: { summary: 'Static only', findings: [], criterionReviews: [
          { criterionId: 'C1', verdict: 'Satisfied', explanation: 'State initializer found.',
            evidence: [{ path: 'src/App.tsx', quote: 'useState(0)' }] },
          { criterionId: 'C2', verdict: 'Unverified', explanation: 'Button behavior not verified.',
            uncertainty: 'No browser evidence.', evidence: [] },
        ] },
        criterionExecutions: [
          { criterionId: 'C1', testFile: 'src/App.test.tsx', testName: 'stale-assertion', attempt: 1,
            version: 1, sourceFingerprint: 'previous-source', provenance: 'runner report' },
          { criterionId: 'C2', testFile: 'src/App.test.tsx', testName: '[C2] increment', attempt: 1,
            version: 2, sourceFingerprint: 'approved-source', provenance: 'Authorized Vitest runner report' },
        ],
      },
    }} cancel={vi.fn()} busy={false} refresh={async () => undefined} />)
    expect(screen.getByText('Revue statique : Satisfait avec citation de source')).toBeInTheDocument()
    expect(screen.getByText('Revue statique : Non vérifié')).toBeInTheDocument()
    expect(screen.getByText('Incertitude : No browser evidence.')).toBeInTheDocument()
    expect(screen.getByText('sandbox.memoryMegabytes')).toBeInTheDocument()
    expect(screen.getByText(/attendu : 2048 · actuel : 1024/)).toBeInTheDocument()
    expect(screen.getByText(/Exécution fonctionnelle : non vérifiée/)).toBeInTheDocument()
    expect(screen.getByText(/Test exécuté : \[C2\] increment/)).toHaveTextContent('approved-source')
    expect(screen.queryByText(/stale-assertion/)).not.toBeInTheDocument()
    expect(screen.getByText(/Les citations du Reviewer sont une analyse statique/)).toBeInTheDocument()
  })

  it('shows the initial decision, actual escalated model and observed validation without promising functionality', () => {
    const compatibility: ModelCompatibility = { status: 'Compatible', reason: 'RAM/VRAM estimées séparément', contextTokens: 4096,
      weightsBytes: null, kvBytes: null, buffersBytes: null, requiredRamBytes: null,
      requiredVramBytes: null, placement: 'GPU' }
    render(<MissionDetails mission={{ ...mission, status: 'Running', workflow: {
      workflowId: 'workflow', missionId: mission.id, version: 1, automaticModelSelection: true,
      plan: null, review: null, revisions: [], finalReport: null, steps: [{
        id: 'generate-1', kind: 'Generate', agentId: 'DeveloperAgent', dependsOn: [], status: 'Running',
        attempts: 3, input: 'src/App.tsx', output: null, error: null, durationMilliseconds: null,
        modelName: 'reserve:7b', selectionMode: 'Automatic',
        modelSelections: [{ key: 'file', agentId: 'DeveloperAgent', decision: {
          model: 'coder:7b', category: 'ReactTypeScript', confidence: 'Low', reason: 'Spécialisation documentée, non garantie',
          alternatives: [{ model: 'reserve:7b', score: 5, reason: 'Secours admissible', compatibility }],
        } }],
        modelInvocations: [{ stepId: 'generate-1', sequence: 'file', attempt: {
          id: 'attempt', runId: 'workflow', agentId: 'agent', role: 'Developer', modelName: 'reserve:7b',
          selectionReason: 'Two consecutive generation failures; authorized fallback.', startedAt: mission.createdAt,
          outcome: 'Generated', failureKind: null, decision: null, nextModel: null,
          compilationStatus: 'Passed', testsStatus: 'Unavailable',
        } }],
      }],
    } }} cancel={vi.fn()} busy={false} refresh={async () => undefined} />)
    expect(screen.getByText('Sélection des modèles : automatique (heuristique)')).toBeInTheDocument()
    expect(screen.getByText('Modèle appelé : reserve:7b · Generated')).toBeInTheDocument()
    expect(screen.getByText(/Two consecutive generation failures/)).toBeInTheDocument()
    expect(screen.getByText(/Critères fonctionnels : non déduits d’un build/)).toBeInTheDocument()
    expect(screen.getByText('RAM/VRAM estimées séparément')).toBeInTheDocument()
  })

  it('shows recorded creation and completion times and all execution events', () => {
    render(<MissionDetails mission={{
      ...mission, status: 'Completed', createdAt: '2026-11-11T16:00:00Z',
      completedAt: '2026-11-11T16:02:00Z',
      events: [
        { sequence: 1, timestamp: '2026-11-11T16:01:00Z', step: 'Generation', status: 'Succeeded', message: 'Première génération.' },
        { sequence: 2, timestamp: '2026-11-11T16:02:00Z', step: 'Generation', status: 'Succeeded', message: 'Correction terminée.' },
        { sequence: 3, timestamp: '2026-11-11T16:02:00Z', step: 'Mission', status: 'Succeeded', message: 'Conversation completed.' },
      ],
    }} cancel={vi.fn()} busy={false} refresh={async () => undefined} />)

    expect(screen.getByRole('heading', { name: /Créer un projet de test/ })).toBeVisible()
    expect(document.querySelector('.status-Completed')).toBeVisible()
    expect(screen.getByText('Durée totale')).toBeVisible()
    expect(screen.getByText('Première génération.')).toBeVisible()
    expect(screen.getByText('Correction terminée.')).toBeVisible()
  })

  it('does not display a completion time or duration when neither was recorded', () => {
    render(<MissionDetails mission={{ ...mission, status: 'Failed', completedAt: null, events: [
      { sequence: 1, timestamp: '2026-11-11T16:00:30Z', step: 'Build', status: 'Failed', message: 'Échec de compilation.' },
    ] }} cancel={vi.fn()} busy={false} refresh={async () => undefined} />)

    expect(screen.queryByText('Terminée')).not.toBeInTheDocument()
    expect(screen.queryByText('Durée totale')).not.toBeInTheDocument()
    expect(screen.getByText('Échec de compilation.')).toBeVisible()
  })
})
