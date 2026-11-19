import { useEffect, useRef, useState, type ChangeEvent } from 'react'
import { invoke } from '@tauri-apps/api/core'
import { Check, FolderOpen, Globe, LoaderCircle, Play, Plus, RotateCcw, Save, Square, Trash2, X } from 'lucide-react'
import { api, errorMessage } from './api'
import { Markdown } from './Markdown'
import { installedDeveloperFallbacks, OllamaFallbackModels } from './OllamaFallbackModels'
import { RoutingExplanation } from './ModelLibrary'
import type {
  CustomAgent, CustomAgentBrowserScenarioResult, CustomAgentBrowserTestReport,
  CustomAgentCollaborationRun, CustomAgentConversation, CustomAgentCriterionReview, CustomAgentDevelopmentPlan, CustomAgentFileChange, CustomAgentTeam,
  CustomAgentProjectType, CustomAgentTeamMember, CustomAgentTeamRole, CustomAgentWorkspace,
  CustomAgentValidationDiagnostic, CustomAgentWorkspaceFile, LocalApplication, Mission, NativeExternalFolderSelection,
} from './types'

const roles: CustomAgentTeamRole[] = ['Planner', 'Developer', 'Reviewer']
const roleLabels: Record<CustomAgentTeamRole, string> = {
  Planner: 'Planificateur',
  Developer: 'Développeur',
  Reviewer: 'Réviseur',
}

const activeStatuses = new Set(['Queued', 'Running'])
const newProjectOption = '__new_project__'
const sourceExtensions = new Set([
  '.cs', '.csproj', '.sln', '.slnx', '.json', '.md', '.ts', '.tsx', '.js', '.jsx',
  '.html', '.css', '.py', '.go', '.rs', '.java', '.xml', '.yaml', '.yml',
])

function isSourceFile(path: string) {
  const segments = path.split('/')
  const file = (segments.at(-1) ?? '').toLowerCase()
  const extension = file.includes('.') ? file.slice(file.lastIndexOf('.')) : ''
  return !segments.some(segment => [
    'bin', 'obj', 'node_modules', '.git', '.vs', '.idea', 'dist', 'coverage', 'target',
  ].includes(segment.toLowerCase())) &&
    !file.startsWith('.env') && !['.npmrc', '.yarnrc', '.pypirc'].includes(file) &&
    !['secret', 'credential', 'password', 'token'].some(term => file.includes(term)) &&
    (sourceExtensions.has(extension) || file === 'dockerfile')
}

function formatDate(value: string) {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? 'Date indisponible' : date.toLocaleString('fr-FR')
}

function stageLabel(run: CustomAgentCollaborationRun) {
  if (run.status === 'Queued') return 'En attente'
  if (run.currentStep === 'DeveloperCorrection') return 'Correction du Développeur'
  if (run.currentStep === 'StaticValidation') return 'Validation statique'
  if (run.currentStep === 'ApplyingChanges') return 'Application des modifications'
  if (run.currentStep === 'LocalExecution') return 'Exécution locale autorisée'
  if (run.currentStep === 'BrowserTesting') return 'Tests navigateur'
  if (run.currentStep === 'Restoring') return 'Restauration'
  if (run.currentStep) return roleLabels[run.currentStep as CustomAgentTeamRole] ?? run.currentStep
  return ({
    Running: 'En cours',
    AwaitingApproval: 'En attente de votre approbation',
    Completed: 'Terminée',
    Applied: 'Modifications appliquées',
    Rejected: 'Proposition refusée',
    Failed: 'En erreur',
    Cancelled: 'Annulée',
    Restored: 'Fichiers restaurés',
  } as Record<string, string>)[run.status] ?? run.status
}

function validationStatusLabel(status: string) {
  return ({
    Passed: 'Validation statique réussie',
    Warnings: 'Validation statique avec avertissements',
    Errors: 'Erreurs détectées',
    Unavailable: 'Validation statique non disponible',
  } as Record<string, string>)[status] ?? status
}

function localApplicationStatusLabel(status: string) {
  return ({
    Stopped: 'Arrêtée',
    Starting: 'Démarrage',
    Running: 'En cours d’exécution',
    Stopping: 'Arrêt en cours',
    Error: 'Erreur',
  } as Record<string, string>)[status] ?? status
}

function developmentPlanStatusLabel(status: string) {
  return ({
    Ready: 'Prêt',
    Running: 'En cours',
    Pausing: 'Pause en cours',
    Paused: 'En pause',
    Cancelling: 'Annulation en cours',
    Cancelled: 'Annulé',
    Completed: 'Exécution terminée et critères validés',
    AwaitingValidation: 'Exécution terminée · validations manquantes',
    Failed: 'Échec',
  } as Record<string, string>)[status] ?? status
}

function developmentTaskStatusLabel(status: string) {
  return ({
    Pending: 'En attente',
    Running: 'En cours',
    Completed: 'Exécutée · critères à vérifier',
    Failed: 'Échec',
    Blocked: 'Bloquée par une dépendance',
  } as Record<string, string>)[status] ?? status
}

function CriterionEditor({ criterion, review, taskStatus, disabled, runs, workspaceId, save }: {
  criterion: string, review: CustomAgentCriterionReview, taskStatus: string, disabled: boolean,
  runs: CustomAgentCollaborationRun[], workspaceId: string,
  save: (category: string, status: string, evidence: string | null, source: string) => Promise<void>,
}) {
  const [category, setCategory] = useState(review.category)
  const [status, setStatus] = useState(review.status)
  const [evidence, setEvidence] = useState(review.evidence ?? '')
  const [source, setSource] = useState('')
  useEffect(() => {
    setCategory(review.category)
    setStatus(review.status)
    setEvidence(review.evidence ?? '')
  }, [review.category, review.status, review.evidence])
  const canReview = ['Completed', 'Failed'].includes(taskStatus) &&
    (category === 'User' || category === 'Technical')
  const sources = runs.filter(run => run.workspace?.workspaceId === workspaceId).flatMap(run => {
    const reports = [...(run.browserTestHistory ?? []), ...(run.browserTest ? [run.browserTest] : [])]
    return reports.filter((report, index) => reports.findIndex(item => item.id === report.id) === index)
      .flatMap(report => report.scenarios.filter(scenario =>
        status === 'Verified' ? report.status === 'Passed' && scenario.status === 'Passed' :
          status === 'Failed' && scenario.status === 'Failed')
        .map(scenario => ({ value: `${run.id}|${report.id}|${scenario.id}`, label: `${scenario.name} · ${scenario.status} · ${formatDate(report.testedAt)}` })))
  })
  return <li>
    <strong>{criterion}</strong>
    <p>{review.status === 'Verified' ? 'Vérifié par scénario associé' :
      review.status === 'Failed' ? 'Échoué' : 'Non vérifié'}
      {review.reviewedAt && ` · ${formatDate(review.reviewedAt)}`}</p>
    {review.evidence && <p className="muted small">Preuve déclarée : {review.evidence}</p>}
    <label>Catégorie du critère<select aria-label={`Catégorie : ${criterion}`} value={category}
      disabled={disabled} onChange={event => {
        const next = event.target.value as CustomAgentCriterionReview['category']
        setCategory(next)
        if (next === 'Optional' || next === 'Unclassified') setStatus('Unverified')
      }}>
      <option value="Unclassified">À classer</option>
      <option value="User">Exigence utilisateur</option>
      <option value="Technical">Condition technique nécessaire</option>
      <option value="Optional">Suggestion facultative</option>
    </select></label>
    <label>Résultat<select aria-label={`Résultat : ${criterion}`} value={status}
      disabled={disabled || !canReview}
      onChange={event => setStatus(event.target.value as CustomAgentCriterionReview['status'])}>
      <option value="Unverified">Non vérifié</option>
      <option value="Verified" disabled={taskStatus !== 'Completed'}>Vérifié</option>
      <option value="Failed">Échoué</option>
    </select></label>
    {status !== 'Unverified' && <><label>Scénario navigateur<select aria-label={`Scénario : ${criterion}`}
      value={source} disabled={disabled} onChange={event => setSource(event.target.value)}>
      <option value="">Sélectionner un résultat enregistré</option>
      {sources.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}
    </select></label><label>Correspondance vérifiée<textarea maxLength={1200} value={evidence}
      aria-label={`Preuve : ${criterion}`} disabled={disabled}
      placeholder="Expliquez comment ce scénario vérifie ce critère."
      onChange={event => setEvidence(event.target.value)} /></label></>}
    <button className="button secondary" disabled={disabled || status !== 'Unverified' && (!evidence.trim() || !sources.some(item => item.value === source))}
      onClick={() => void save(category, status, status === 'Unverified' ? null : evidence, source)}>
      Enregistrer ce critère
    </button>
  </li>
}

function ValidationDiagnostics({ diagnostics }: { diagnostics: CustomAgentValidationDiagnostic[] }) {
  const grouped = diagnostics.reduce<Record<string, CustomAgentValidationDiagnostic[]>>((result, item) => {
    const pathDiagnostics = result[item.path] ?? []
    pathDiagnostics.push(item)
    result[item.path] = pathDiagnostics
    return result
  }, {})
  return <div className="custom-validation-diagnostics">
    {Object.entries(grouped).map(([path, items]) => <section key={path}>
      <strong>{path}</strong>
      <ul>{items.map((item, index) => <li key={`${item.code}-${index}`}>
        <code>{item.code}</code> · {item.message}
        {item.relatedPath && <span> ({item.relatedPath})</span>}
      </li>)}</ul>
    </section>)}
  </div>
}

export function CustomAgentTeams({
  agents, projects, desktopApp, onProjectCreated, onOpenProject, models = [],
}: {
  agents: CustomAgent[]
  models?: string[]
  projects: Mission[]
  desktopApp: boolean
  onProjectCreated: (project: Mission) => void
  onOpenProject: (id: string) => void
}) {
  const [teams, setTeams] = useState<CustomAgentTeam[]>([])
  const [teamId, setTeamId] = useState<string | null>(null)
  const [teamName, setTeamName] = useState('')
  const [memberIds, setMemberIds] = useState<string[]>(['', '', ''])
  const [modelEscalationEnabled, setModelEscalationEnabled] = useState(false)
  const [automaticModelSelection, setAutomaticModelSelection] = useState(false)
  const [memberFallbacks, setMemberFallbacks] = useState<(string[] | null)[]>([null, null, null])
  const [runs, setRuns] = useState<CustomAgentCollaborationRun[]>([])
  const [selectedRun, setSelectedRun] = useState<CustomAgentCollaborationRun | null>(null)
  const [developmentPlans, setDevelopmentPlans] = useState<CustomAgentDevelopmentPlan[]>([])
  const [selectedDevelopmentPlan, setSelectedDevelopmentPlan] = useState<CustomAgentDevelopmentPlan | null>(null)
  const [projectRequests, setProjectRequests] = useState<CustomAgentConversation['messages']>([])
  const [developmentGoal, setDevelopmentGoal] = useState('')
  const [developmentExtension, setDevelopmentExtension] = useState('')
  const [developmentPlanBusy, setDevelopmentPlanBusy] = useState(false)
  const [task, setTask] = useState('')
  const [mode, setMode] = useState<'Supervised' | 'Autonomous'>('Supervised')
  const [newProjectName, setNewProjectName] = useState('')
  const [newProjectType, setNewProjectType] = useState<CustomAgentProjectType>('React')
  const [creatingProject, setCreatingProject] = useState(false)
  const [workspace, setWorkspace] = useState<CustomAgentWorkspace | null>(null)
  const [externalFiles, setExternalFiles] = useState<CustomAgentWorkspaceFile[]>([])
  const [externalConsent, setExternalConsent] = useState(false)
  const [externalGrantId, setExternalGrantId] = useState<string | null>(null)
  const [externalWriteAuthorized, setExternalWriteAuthorized] = useState(false)
  const [localApplication, setLocalApplication] = useState<LocalApplication | null>(null)
  const [localApplicationBusy, setLocalApplicationBusy] = useState(false)
  const [browserTest, setBrowserTest] = useState<CustomAgentBrowserTestReport | null>(null)
  const [browserTestBusy, setBrowserTestBusy] = useState(false)
  const [browserScreenshot, setBrowserScreenshot] = useState<{ scenarioId: string; url: string } | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const browserFolderInput = useRef<HTMLInputElement>(null)
  const nativeGrantRef = useRef<string | null>(null)
  const browserTestAbort = useRef<AbortController | null>(null)
  const selectedProject = workspace?.kind === 'Studio'
    ? projects.find(project => project.id === workspace.workspaceId)
    : undefined
  const selectedProjectIdRef = useRef<string | null>(null)
  selectedProjectIdRef.current = selectedProject?.id ?? null
  const supportsLocalApplication = selectedProject?.templateId === 'custom-agent-react' ||
    selectedProject?.templateId === 'custom-agent-dotnet'

  useEffect(() => {
    let active = true
    void api.customAgentTeams().then(result => {
      if (!active) return
      setTeams(result)
      if (result.length) selectTeam(result[0])
    }).catch(reason => {
      if (active) setError(errorMessage(reason))
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => { active = false }
  }, [])

  useEffect(() => {
    if (!teamId) {
      setRuns([])
      setSelectedRun(null)
      return
    }
    let active = true
    void api.customAgentTeamRuns(teamId).then(result => {
      if (!active) return
      setRuns(result)
      setSelectedRun(current => current && result.some(run => run.id === current.id)
        ? result.find(run => run.id === current.id) ?? null
        : result[0] ?? null)
    }).catch(reason => {
      if (active) setError(errorMessage(reason))
    })
    return () => { active = false }
  }, [teamId])

  useEffect(() => {
    if (!teamId) {
      setDevelopmentPlans([])
      setSelectedDevelopmentPlan(null)
      return
    }
    let active = true
    void api.customAgentDevelopmentPlans(teamId).then(result => {
      if (!active) return
      setDevelopmentPlans(result)
      setSelectedDevelopmentPlan(current => {
        const projectPlans = result.filter(plan => plan.workspace.workspaceId === selectedProjectIdRef.current)
        return projectPlans.find(plan => plan.id === current?.id) ?? projectPlans[0] ?? null
      })
    }).catch(reason => {
      if (active) setError(errorMessage(reason))
    })
    return () => { active = false }
  }, [teamId])

  useEffect(() => {
    const activePlan = selectedDevelopmentPlan
    if (!teamId || !activePlan || !['Running', 'Pausing', 'Cancelling'].includes(activePlan.status)) return
    let active = true
    let timer: ReturnType<typeof setTimeout> | undefined
    const poll = async () => {
      try {
        const latest = await api.customAgentDevelopmentPlans(teamId)
        if (!active) return
        setDevelopmentPlans(latest)
        const current = latest.find(plan => plan.id === activePlan.id) ?? null
        setSelectedDevelopmentPlan(current)
        const taskRunId = current?.tasks.find(task => task.id === current.currentTaskId)?.collaborationRunId
        if (taskRunId) {
          const taskRun = await api.customAgentTeamRun(teamId, taskRunId)
          if (!active) return
          setSelectedRun(taskRun)
          setRuns(runs => [taskRun, ...runs.filter(run => run.id !== taskRun.id)])
        }
        if (current && ['Running', 'Pausing', 'Cancelling'].includes(current.status))
          timer = setTimeout(() => void poll(), 900)
        else {
          const completedRuns = await api.customAgentTeamRuns(teamId)
          if (active) setRuns(completedRuns)
        }
      } catch (reason) {
        if (active) setError(errorMessage(reason))
      }
    }
    timer = setTimeout(() => void poll(), 500)
    return () => {
      active = false
      if (timer) clearTimeout(timer)
    }
  }, [teamId, selectedDevelopmentPlan?.id, selectedDevelopmentPlan?.status])

  useEffect(() => {
    let active = true
    const plan = selectedDevelopmentPlan
    setProjectRequests([])
    if (plan?.conversationId && plan.conversationAgentId) {
      void api.customAgentConversation(plan.conversationAgentId, plan.conversationId).then(conversation => {
        if (active) setProjectRequests(conversation.messages.filter(message => message.developmentPlanId === plan.id))
      }).catch(reason => {
        if (active) setError(errorMessage(reason))
      })
    }
    return () => { active = false }
  }, [selectedDevelopmentPlan?.id, selectedDevelopmentPlan?.conversationId, selectedDevelopmentPlan?.updatedAt])

  useEffect(() => {
    const activeRun = selectedRun
    if (!teamId || !activeRun || !activeStatuses.has(activeRun.status)) return
    let active = true
    let timer: ReturnType<typeof setTimeout> | undefined
    const poll = async () => {
      try {
        const latest = await api.customAgentTeamRun(teamId, activeRun.id)
        if (!active) return
        setSelectedRun(latest)
        setRuns(current => current.map(run => run.id === latest.id ? latest : run))
        if (activeStatuses.has(latest.status)) timer = setTimeout(() => void poll(), 900)
      } catch (reason) {
        if (active) setError(errorMessage(reason))
      }
    }
    timer = setTimeout(() => void poll(), 350)
    return () => {
      active = false
      if (timer) clearTimeout(timer)
    }
  }, [teamId, selectedRun?.id, selectedRun?.status])

  useEffect(() => {
    const workspaceId = supportsLocalApplication ? selectedProject?.id : null
    if (!desktopApp || !workspaceId) {
      setLocalApplication(null)
      return
    }
    let active = true
    let timer: ReturnType<typeof setTimeout> | undefined
    setLocalApplication(null)
    const poll = async () => {
      try {
        const latest = await api.localApplication(workspaceId)
        if (!active) return
        setLocalApplication(latest)
        if (['Starting', 'Running', 'Stopping'].includes(latest.status))
          timer = setTimeout(() => void poll(), 900)
      } catch (reason) {
        if (active) setError(errorMessage(reason))
      }
    }
    void poll()
    return () => {
      active = false
      if (timer) clearTimeout(timer)
    }
  }, [desktopApp, supportsLocalApplication, selectedProject?.id, selectedProject?.templateId])

  useEffect(() => {
    setBrowserTest(selectedRun && selectedProject &&
      selectedRun.workspace?.workspaceId === selectedProject.id
      ? selectedRun.browserTest ?? null : null)
    setBrowserScreenshot(null)
  }, [selectedRun?.id, selectedRun?.browserTest?.id, selectedProject?.id])

  useEffect(() => () => {
    if (browserScreenshot) URL.revokeObjectURL(browserScreenshot.url)
  }, [browserScreenshot])

  useEffect(() => () => {
    const grantId = nativeGrantRef.current
    nativeGrantRef.current = null
    if (grantId && desktopApp)
      void invoke('release_external_workspace', { grantId }).catch(() => undefined)
  }, [desktopApp])

  function selectTeam(team: CustomAgentTeam) {
    setTeamId(team.id)
    setTeamName(team.name)
    setMemberIds(roles.map(role => team.members.find(member => member.role === role)?.agentId ?? ''))
    setModelEscalationEnabled(team.modelEscalationEnabled ?? false)
    setAutomaticModelSelection(team.automaticModelSelection ?? false)
    setMemberFallbacks(roles.map(role => team.members.find(member => member.role === role)?.fallbackModels ?? null))
    setSelectedRun(null)
    setDevelopmentPlans([])
    setSelectedDevelopmentPlan(null)
    setError(null)
  }

  function newTeam() {
    const available = agents.slice(0, 3).map(agent => agent.id)
    setTeamId(null)
    setTeamName('')
    setMemberIds([available[0] ?? '', available[1] ?? '', available[2] ?? ''])
    setModelEscalationEnabled(false)
    setAutomaticModelSelection(false)
    setMemberFallbacks([null, null, null])
    setRuns([])
    setSelectedRun(null)
    setDevelopmentPlans([])
    setSelectedDevelopmentPlan(null)
    setError(null)
  }

  async function saveTeam() {
    if (busy) return
    setBusy(true)
    setError(null)
    try {
      const members: CustomAgentTeamMember[] = roles.map((role, order) => ({
        agentId: memberIds[order] ?? '', role, order,
        fallbackModels: memberFallbacks[order] ?? (modelEscalationEnabled
          ? agents.find(agent => agent.id === memberIds[order])?.fallbackModels ??
            (role === 'Developer' ? installedDeveloperFallbacks(models,
              agents.find(agent => agent.id === memberIds[order])?.modelName ?? '') : [])
          : null),
      }))
      const saved = await api.saveCustomAgentTeam(teamName, members, teamId ?? undefined, modelEscalationEnabled, automaticModelSelection)
      setTeams(current => [saved, ...current.filter(team => team.id !== saved.id)])
      selectTeam(saved)
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setBusy(false)
    }
  }

  async function deleteTeam() {
    const team = teams.find(item => item.id === teamId)
    if (!team || !window.confirm(`Supprimer l’équipe « ${team.name} » ?`)) return
    setBusy(true)
    setError(null)
    try {
      await api.deleteCustomAgentTeam(team.id)
      const remaining = teams.filter(item => item.id !== team.id)
      setTeams(remaining)
      if (remaining.length) selectTeam(remaining[0])
      else newTeam()
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setBusy(false)
    }
  }

  function releaseGrant() {
    const grantId = nativeGrantRef.current
    nativeGrantRef.current = null
    if (grantId && desktopApp)
      void invoke('release_external_workspace', { grantId }).catch(reason =>
        setError(errorMessage(reason)))
    setExternalGrantId(null)
    setExternalWriteAuthorized(false)
  }

  async function chooseNativeFolder() {
    setError(null)
    setBusy(true)
    try {
      const selection = await invoke<NativeExternalFolderSelection | null>('select_external_workspace')
      if (!selection) return
      releaseGrant()
      nativeGrantRef.current = selection.grantId
      setExternalGrantId(selection.grantId)
      setExternalFiles(selection.files)
      setExternalConsent(false)
      setMode('Supervised')
      setWorkspace({
        kind: 'External', workspaceId: null, name: selection.name, authorizationId: selection.grantId,
      })
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setBusy(false)
    }
  }

  function chooseBrowserFolder(event: ChangeEvent<HTMLInputElement>) {
    const selected = Array.from(event.target.files ?? [])
    event.target.value = ''
    const files = selected.filter(file => isSourceFile(file.webkitRelativePath || file.name))
    if (!files.length || files.length > 80) {
      setError('Choisissez un dossier contenant de 1 à 80 fichiers source admissibles.')
      return
    }
    void Promise.all(files.map(async file => ({
      path: file.webkitRelativePath.split('/').slice(1).join('/'),
      content: await file.text(),
    }))).then(snapshot => {
      const name = files[0].webkitRelativePath.split('/')[0] || 'Dossier sélectionné'
      releaseGrant()
      setExternalFiles(snapshot)
      setExternalConsent(false)
      setMode('Supervised')
      setWorkspace({ kind: 'External', workspaceId: null, name })
      setError(null)
    }).catch(reason => setError(errorMessage(reason)))
  }

  async function authorizeExternalWrites() {
    if (!externalGrantId) return
    setBusy(true)
    setError(null)
    try {
      await invoke('enable_external_workspace_writes', { grantId: externalGrantId })
      setExternalWriteAuthorized(true)
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setBusy(false)
    }
  }

  async function startCollaboration() {
    if (!teamId || busy || !task.trim()) return
    if (mode === 'Autonomous' && (!desktopApp || workspace?.kind !== 'Studio')) {
      setError('Le mode autonome est limité aux projets Studio dans AIPlatform Studio Desktop.')
      return
    }
    if (workspace?.kind === 'External' && !externalConsent) {
      setError('Confirmez l’envoi de l’instantané de fichiers à Ollama local.')
      return
    }
    if (mode === 'Autonomous' &&
      !window.confirm('Autoriser cette exécution à créer, modifier et supprimer automatiquement des fichiers dans ce projet Studio ?'))
      return
    if (mode === 'Autonomous' &&
      !window.confirm('Autoriser séparément l’exécution locale de npm ou .NET ? Ces outils peuvent exécuter du code arbitraire du projet. Les délais et Job Objects arrêtent les processus mais ne constituent pas une isolation de sécurité.'))
      return
    setBusy(true)
    setError(null)
    try {
      const externalSnapshot = workspace?.kind === 'External' ? externalFiles : undefined
      const run = mode === 'Autonomous'
        ? await api.startCustomAgentCollaboration(
          teamId, task, workspace, externalSnapshot, mode, true, true)
        : await api.startCustomAgentCollaboration(teamId, task, workspace, externalSnapshot)
      setRuns(current => [run, ...current.filter(item => item.id !== run.id)])
      setSelectedRun(run)
      setTask('')
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setBusy(false)
    }
  }

  function updateDevelopmentPlan(plan: CustomAgentDevelopmentPlan) {
    setSelectedDevelopmentPlan(plan)
    setDevelopmentPlans(current => [
      plan, ...current.filter(existing => existing.id !== plan.id),
    ])
  }

  async function createDevelopmentPlan() {
    if (!teamId || !selectedProject || !workspace || workspace.kind !== 'Studio' ||
      !developmentGoal.trim() || developmentPlanBusy) return
    setDevelopmentPlanBusy(true)
    setError(null)
    try {
      const plan = await api.createCustomAgentDevelopmentPlan(teamId, developmentGoal.trim(), workspace)
      updateDevelopmentPlan(plan)
      setDevelopmentGoal('')
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setDevelopmentPlanBusy(false)
    }
  }

  async function extendDevelopmentPlan() {
    if (!teamId || !selectedDevelopmentPlan || !developmentExtension.trim() || developmentPlanBusy) return
    setDevelopmentPlanBusy(true)
    setError(null)
    try {
      const plan = await api.extendCustomAgentDevelopmentPlan(
        teamId, selectedDevelopmentPlan.id, developmentExtension.trim())
      updateDevelopmentPlan(plan)
      setDevelopmentExtension('')
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setDevelopmentPlanBusy(false)
    }
  }

  async function reviewCriterion(taskId: string, index: number, category: string, status: string, evidence: string | null, source: string) {
    if (!teamId || !selectedDevelopmentPlan || developmentPlanBusy) return
    setDevelopmentPlanBusy(true)
    try {
      const [runId, reportId, scenarioId] = source.split('|')
      updateDevelopmentPlan(await api.reviewDevelopmentCriterion(
        teamId, selectedDevelopmentPlan.id, taskId, index, category, status, evidence,
        runId || null, reportId || null, scenarioId || null))
    } catch (error) {
      setError(errorMessage(error))
    } finally {
      setDevelopmentPlanBusy(false)
    }
  }

  async function runDevelopmentPlan(action: 'start' | 'resume' | 'retry') {
    if (!teamId || !selectedDevelopmentPlan || !desktopApp || developmentPlanBusy ||
      selectedDevelopmentPlan.workspace.workspaceId !== selectedProject?.id) return
    if (!window.confirm(
      'Autoriser cette session à modifier automatiquement les fichiers du workspace Studio sélectionné ?'))
      return
    if (!window.confirm(
      'Autoriser séparément l’exécution locale de npm ou .NET ? Ces outils peuvent exécuter le code du projet et Studio ne fournit pas d’isolation de sécurité.'))
      return
    setDevelopmentPlanBusy(true)
    setError(null)
    try {
      const plan = action === 'start'
        ? await api.startCustomAgentDevelopmentPlan(teamId, selectedDevelopmentPlan.id)
        : action === 'resume'
          ? await api.resumeCustomAgentDevelopmentPlan(teamId, selectedDevelopmentPlan.id)
          : await api.retryCustomAgentDevelopmentTask(
            teamId, selectedDevelopmentPlan.id,
            selectedDevelopmentPlan.tasks.find(task => task.status === 'Failed')?.id ?? '')
      updateDevelopmentPlan(plan)
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setDevelopmentPlanBusy(false)
    }
  }

  async function controlDevelopmentPlan(action: 'pause' | 'cancel') {
    if (!teamId || !selectedDevelopmentPlan || developmentPlanBusy) return
    setDevelopmentPlanBusy(true)
    setError(null)
    try {
      const plan = action === 'pause'
        ? await api.pauseCustomAgentDevelopmentPlan(teamId, selectedDevelopmentPlan.id)
        : await api.cancelCustomAgentDevelopmentPlan(teamId, selectedDevelopmentPlan.id)
      updateDevelopmentPlan(plan)
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setDevelopmentPlanBusy(false)
    }
  }

  async function createProject() {
    if (!teamId || busy || !newProjectName.trim()) return
    setBusy(true)
    setError(null)
    try {
      const project = await api.createCustomAgentProject(teamId, newProjectName.trim(), newProjectType)
      onProjectCreated(project)
      releaseGrant()
      setExternalFiles([])
      setExternalConsent(false)
      setWorkspace({
        kind: 'Studio', workspaceId: project.id,
        name: project.projectName || newProjectName.trim(),
      })
      setNewProjectName('')
      setCreatingProject(false)
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setBusy(false)
    }
  }

  async function cancelRun() {
    if (!teamId || !selectedRun || !activeStatuses.has(selectedRun.status)) return
    setBusy(true)
    try {
      await api.cancelCustomAgentCollaboration(teamId, selectedRun.id)
      const latest = await api.customAgentTeamRun(teamId, selectedRun.id)
      setSelectedRun(latest)
      setRuns(current => current.map(run => run.id === latest.id ? latest : run))
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setBusy(false)
    }
  }

  async function startLocalApplication() {
    const workspaceId = selectedProject?.id
    if (!desktopApp || !workspaceId || localApplicationBusy) return
    if (!window.confirm(
      'Ce projet peut exécuter des scripts npm ou du code .NET local. Confirmez-vous que ce projet est fiable et autorisez-vous son exécution ? Studio ne fournit pas d’isolation de sécurité.'))
      return
    setLocalApplicationBusy(true)
    setError(null)
    try {
      setLocalApplication(await api.startLocalApplication(workspaceId))
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setLocalApplicationBusy(false)
    }
  }

  async function stopLocalApplication() {
    const workspaceId = selectedProject?.id
    if (!desktopApp || !workspaceId || localApplicationBusy) return
    setLocalApplicationBusy(true)
    setError(null)
    try {
      setLocalApplication(await api.stopLocalApplication(workspaceId))
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setLocalApplicationBusy(false)
    }
  }

  async function runBrowserTests() {
    const workspaceId = selectedProject?.id
    if (!desktopApp || !workspaceId || browserTestBusy) return
    if (!window.confirm(
      'Autoriser Playwright à exécuter des interactions limitées sur ce projet local fiable ? Les scripts du projet peuvent déjà exécuter du code local; Playwright ne constitue pas une sandbox.'))
      return
    const attachToRun = selectedRun && selectedRun.workspace?.workspaceId === workspaceId &&
      !activeStatuses.has(selectedRun.status) ? selectedRun : null
    const controller = new AbortController()
    browserTestAbort.current = controller
    setBrowserTestBusy(true)
    setError(null)
    try {
      const report = await api.runStudioBrowserTests(workspaceId, {
        executionAuthorized: true,
        teamId: attachToRun?.teamId,
        collaborationRunId: attachToRun?.id,
        scenarios: attachToRun?.browserScenarios,
      }, controller.signal)
      setBrowserTest(report)
      if (attachToRun) {
        const updated = await api.customAgentTeamRun(attachToRun.teamId, attachToRun.id)
        setSelectedRun(updated)
        setRuns(current => current.map(item => item.id === updated.id ? updated : item))
      }
    } catch (reason) {
      if (!(reason instanceof DOMException && reason.name === 'AbortError'))
        setError(errorMessage(reason))
    } finally {
      if (browserTestAbort.current === controller) browserTestAbort.current = null
      setBrowserTestBusy(false)
    }
  }

  function cancelBrowserTests() {
    browserTestAbort.current?.abort()
  }

  async function showBrowserScreenshot(report: CustomAgentBrowserTestReport, scenario: CustomAgentBrowserScenarioResult) {
    const workspaceId = selectedProject?.id
    if (!workspaceId || !scenario.screenshotId) return
    try {
      const url = await api.studioBrowserScreenshot(workspaceId, report.id, scenario.screenshotId)
      setBrowserScreenshot({ scenarioId: scenario.id, url })
    } catch (reason) {
      setError(errorMessage(reason))
    }
  }

  async function openLocalApplication() {
    if (!localApplication?.url || localApplication.status !== 'Running') return
    try {
      await invoke('open_local_application', { url: localApplication.url })
    } catch (reason) {
      setError(errorMessage(reason))
    }
  }

  async function decideProposal(run: CustomAgentCollaborationRun, approved: boolean) {
    if (!teamId || !run.proposal || busy) return
    setBusy(true)
    setError(null)
    try {
      let updated: CustomAgentCollaborationRun
      if (run.proposal.workspace?.kind === 'External' && approved) {
        if (!desktopApp || !externalGrantId || !externalWriteAuthorized ||
          run.proposal.workspace.authorizationId !== externalGrantId)
          throw new Error('Resélectionnez et autorisez le dossier Windows lié à cette proposition.')
        const backupId = await invoke<string>('apply_external_workspace_proposal', {
          grantId: externalGrantId,
          proposalId: run.proposal.id,
          changes: run.proposal.changes,
        })
        try {
          updated = await api.recordNativeCollaborationOutcome(teamId, run.id, backupId, false)
        } catch (reason) {
          await invoke('restore_external_workspace_proposal', { grantId: externalGrantId, backupId })
          throw new Error(`L’enregistrement a échoué; les fichiers ont été restaurés. ${errorMessage(reason)}`)
        }
      } else {
        updated = await api.decideCustomAgentCollaboration(teamId, run.id, approved)
      }
      setSelectedRun(updated)
      setRuns(current => current.map(item => item.id === updated.id ? updated : item))
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setBusy(false)
    }
  }

  async function restoreExternalProposal(run: CustomAgentCollaborationRun) {
    const backupId = run.proposal?.externalBackupId
    if (!teamId || !backupId || !externalGrantId || !externalWriteAuthorized || busy) return
    setBusy(true)
    setError(null)
    try {
      await invoke('restore_external_workspace_proposal', { grantId: externalGrantId, backupId })
      const updated = await api.recordNativeCollaborationOutcome(teamId, run.id, backupId, true)
      setSelectedRun(updated)
      setRuns(current => current.map(item => item.id === updated.id ? updated : item))
    } catch (reason) {
      setError(errorMessage(reason))
    } finally {
      setBusy(false)
    }
  }

  function chooseWorkspace(value: string) {
    if (value === newProjectOption) {
      releaseGrant()
      setExternalFiles([])
      setExternalConsent(false)
      setWorkspace(null)
      setCreatingProject(true)
      return
    }
    setCreatingProject(false)
    if (value === newProjectOption || !projects.some(project => project.id === value))
      setMode('Supervised')
    releaseGrant()
    setExternalFiles([])
    setExternalConsent(false)
    const project = projects.find(item => item.id === value)
    setSelectedDevelopmentPlan(project
      ? developmentPlans.find(plan => plan.workspace.workspaceId === project.id) ?? null
      : null)
    setWorkspace(project ? {
      kind: 'Studio', workspaceId: project.id, name: project.projectName || project.task.slice(0, 120),
    } : null)
  }

  const canSaveTeam = teamName.trim().length > 0 && memberIds.length === roles.length &&
    memberIds.every(Boolean) && new Set(memberIds).size === roles.length
  const workspaceLabel = workspace?.kind === 'External'
    ? `${workspace.name} · ${desktopApp ? 'dossier Windows' : 'lecture seule'}`
    : workspace?.kind === 'Studio' ? workspace.name : 'Aucun workspace (résultat textuel uniquement)'
  const projectDevelopmentPlans = selectedProject
    ? developmentPlans.filter(plan => plan.workspace.workspaceId === selectedProject.id)
    : []
  const activeDevelopmentPlan = projectDevelopmentPlans.find(
    plan => plan.id === selectedDevelopmentPlan?.id) ?? null

  return <section className="custom-team-panel">
    {error && <div role="alert" className="notice danger">{error}</div>}
    {loading ? <div className="loading"><LoaderCircle size={16} /> Chargement des équipes…</div> : <>
      <header className="custom-agent-detail-heading">
        <div><h3>Équipes d’agents</h3>
        <p>Workflow séquentiel : Planificateur → Développeur → Réviseur, avec corrections ciblées et validation réelle en mode autonome.</p></div>
        <button className="button secondary" onClick={newTeam} disabled={busy || agents.length < 3}>
          <Plus size={14} /> Nouvelle équipe</button>
      </header>
      {agents.length < 3 && <p className="notice warning">Créez au moins trois agents personnalisés pour composer une équipe.</p>}
      <div className="custom-agent-layout">
        <aside className="custom-agent-sidebar" aria-label="Équipes enregistrées">
          {teams.map(team => <button key={team.id}
            className={`custom-agent-choice ${teamId === team.id ? 'selected' : ''}`}
            onClick={() => selectTeam(team)} disabled={busy}>
            <strong>{team.name}</strong>
            <span>{team.members.map(member => roleLabels[member.role]).join(' · ')}</span>
          </button>)}
          {!teams.length && <p className="muted small">Aucune équipe enregistrée.</p>}
        </aside>
        <div className="custom-agent-main">
          <section className="custom-agent-form">
            <h3>{teamId ? 'Configurer l’équipe' : 'Créer une équipe'}</h3>
            <label>Nom de l’équipe<input maxLength={80} value={teamName}
              onChange={event => setTeamName(event.target.value)} /></label>
            <label><input type="checkbox" checked={modelEscalationEnabled}
              onChange={event => setModelEscalationEnabled(event.target.checked)} />
              Activer l’escalade automatique des modèles</label>
            <label><input type="checkbox" checked={automaticModelSelection}
              onChange={event => setAutomaticModelSelection(event.target.checked)} />
              Autoriser le routage automatique des agents en mode automatique</label>
            <p className="muted small">Désactivé pour les équipes existantes. Les agents manuels ne changent pas de modèle initial.
              En automatique, le routeur choisit une chaîne d’alternatives installées ; elle reste stable pendant la tâche.</p>
            <p className="muted small">Après deux échecs de génération consécutifs, essayer le secours autorisé suivant,
              sans augmenter les limites de tentatives. Contexte, mémoire, timeout et infrastructure ne déclenchent
              pas l’escalade. Les modèles sont utilisés un à la fois ; un 14B peut dépasser la VRAM disponible.</p>
            {roles.map((role, order) => <div key={role}><label>{order + 1}. {roleLabels[role]}<select
              value={memberIds[order] ?? ''} disabled={busy}
              onChange={event => setMemberIds(current => current.map((id, index) =>
                index === order ? event.target.value : id))}>
              <option value="">Choisir un agent</option>
              {agents.map(agent => <option key={agent.id} value={agent.id}
                disabled={memberIds.some((id, index) => index !== order && id === agent.id)}>
                {agent.name} · {agent.modelName}</option>)}
            </select></label>
              <OllamaFallbackModels label={`Secours ${roleLabels[role]}`} value={memberFallbacks[order]}
                inherited={agents.find(agent => agent.id === memberIds[order])?.fallbackModels ??
                  (role === 'Developer' ? installedDeveloperFallbacks(models,
                    agents.find(agent => agent.id === memberIds[order])?.modelName ?? '') : [])}
                models={models} primary={agents.find(agent => agent.id === memberIds[order])?.modelName ?? ''}
                onChange={value => setMemberFallbacks(current => current.map((item, index) => index === order ? value : item))} />
            </div>)}
            <div className="custom-agent-actions">
              <button className="button primary" disabled={!canSaveTeam || busy} onClick={() => void saveTeam()}>
                <Save size={13} /> {busy ? 'Enregistrement…' : 'Enregistrer l’équipe'}</button>
              {teamId && <button className="button secondary" disabled={busy}
                onClick={() => void deleteTeam()}><Trash2 size={13} /> Supprimer</button>}
            </div>
          </section>

          <section className="custom-workspace">
            <div className="custom-workspace-heading"><div><strong>Workspace commun</strong>
              <span>{workspaceLabel}</span></div>
              <span className="badge quiet">{workspace?.kind === 'External'
                ? externalWriteAuthorized ? 'Écriture avec approbation' : 'Lecture seule'
                : workspace?.kind === 'Studio'
                  ? mode === 'Autonomous' ? 'Application automatique après consentement' : 'Modifications avec approbation'
                  : 'Sans fichiers'}</span>
            </div>
            <div className="custom-workspace-controls">
              <label>Projet Studio<select disabled={busy} value={creatingProject ? newProjectOption :
                  workspace?.kind === 'Studio' ? workspace.workspaceId ?? '' : ''}
                onChange={event => chooseWorkspace(event.target.value)}>
                <option value="">Aucun workspace</option>
                <option value={newProjectOption}>Nouveau projet…</option>
                {projects.map(project => <option key={project.id} value={project.id}>
                  {project.projectName || project.task.slice(0, 100)}</option>)}
              </select></label>
              <button className="button secondary" type="button" disabled={busy}
                onClick={() => desktopApp ? void chooseNativeFolder() : browserFolderInput.current?.click()}>
                <FolderOpen size={14} /> Choisir un dossier externe</button>
              <input ref={element => {
                browserFolderInput.current = element
                element?.setAttribute('webkitdirectory', '')
              }} type="file" multiple hidden
                aria-label="Choisir un dossier externe" onChange={chooseBrowserFolder} />
            </div>
            {creatingProject && <div className="custom-agent-form">
              <h4>Nouveau projet Studio</h4>
              <label>Nom du projet<input maxLength={80} value={newProjectName} disabled={busy}
                onChange={event => setNewProjectName(event.target.value)} placeholder="Ex. Gestion de tâches" /></label>
              <label>Type de projet<select value={newProjectType} disabled={busy}
                onChange={event => setNewProjectType(event.target.value as CustomAgentProjectType)}>
                <option value="React">React / TypeScript</option>
                <option value="DotNet">.NET 10</option>
              </select></label>
              <p className="muted small">Le scaffold sera ajouté à Projets. Aucun code IA ne sera enregistré avant votre approbation.</p>
              <button className="button primary" disabled={!teamId || busy || !newProjectName.trim()}
                onClick={() => void createProject()}>
                {busy ? <><LoaderCircle size={13} /> Création…</> : 'Créer le projet'}</button>
            </div>}
            {supportsLocalApplication && selectedProject && <section className="custom-agent-proposal local-application"
              aria-label="Application locale">
              <header><strong>Application locale</strong>
                <span>{localApplication
                  ? localApplicationStatusLabel(localApplication.status)
                  : desktopApp ? 'Chargement' : 'Desktop requis'}</span></header>
              {!desktopApp
                ? <p className="muted small">Le lancement est disponible uniquement dans AIPlatform Studio Desktop sous Windows.</p>
                : <>
                  <p className="notice warning">
                    Les scripts npm et les projets .NET peuvent exécuter du code local. Réservez le lancement à un projet fiable; Studio ne fournit pas d’isolation de sécurité.
                  </p>
                  {localApplication?.url && <p className="local-application-url">
                    <Globe size={13} /> <code>{localApplication.url}</code>
                  </p>}
                  {localApplication?.command && <p className="muted small">
                    Commande : <code>{localApplication.command}</code>
                  </p>}
                  {localApplication?.error && <div role="alert" className="notice danger">
                    {localApplication.error}
                  </div>}
                  <div className="custom-agent-actions">
                    {(!localApplication || localApplication.status === 'Stopped' ||
                      localApplication.status === 'Error') && <button className="button primary"
                      disabled={localApplicationBusy} onClick={() => void startLocalApplication()}>
                      {localApplicationBusy ? <><LoaderCircle size={13} /> Démarrage…</> :
                        <><Play size={13} /> Démarrer l’application</>}
                    </button>}
                    {localApplication?.status === 'Running' && localApplication.url &&
                      <button className="button primary" disabled={localApplicationBusy}
                        onClick={() => void openLocalApplication()}>
                        <Globe size={13} /> Ouvrir dans le navigateur</button>}
                    {['Starting', 'Running'].includes(localApplication?.status ?? '') &&
                      <button className="button secondary" disabled={localApplicationBusy}
                        onClick={() => void stopLocalApplication()}>
                        {localApplication?.status === 'Starting'
                          ? <><Square size={13} /> Annuler le démarrage</>
                          : <><Square size={13} /> Arrêter l’application</>}
                      </button>}
                    {['Starting', 'Stopping'].includes(localApplication?.status ?? '') &&
                      <span className="muted small"><LoaderCircle size={13} className="spin" /> {localApplicationStatusLabel(localApplication!.status)}</span>}
                  </div>
                  {localApplication && <details className="local-application-output" open>
                    <summary>Sorties récentes · lecture seule</summary>
                    <pre aria-label="Sorties du serveur local">
                      {localApplication.output.join('\n') || '(aucune sortie)'}
                    </pre>
                  </details>}
                </>}
            </section>}
            {selectedProject && workspace?.kind === 'Studio' && <section
            className="custom-agent-proposal development-plan" aria-label="Plan de développement">
            <header><strong>Plan de développement</strong>
              <span>{activeDevelopmentPlan
                ? `${developmentPlanStatusLabel(activeDevelopmentPlan.status)} · ${activeDevelopmentPlan.attempts}/${activeDevelopmentPlan.maxAttempts} tentative(s)`
                : desktopApp ? 'Aucun plan' : 'Desktop requis'}</span>
            </header>
            {!desktopApp && <p className="muted small">
              La planification exécutable est disponible uniquement dans AIPlatform Studio Desktop sous Windows.
            </p>}
            {desktopApp && <>
              <label>Plan enregistré<select aria-label="Plan de développement enregistré"
                value={activeDevelopmentPlan?.id ?? ''} disabled={developmentPlanBusy}
                onChange={event => setSelectedDevelopmentPlan(
                  projectDevelopmentPlans.find(plan => plan.id === event.target.value) ?? null)}>
                <option value="">Aucun plan sélectionné</option>
                {projectDevelopmentPlans.map(plan => <option key={plan.id} value={plan.id}>
                  {plan.goal.slice(0, 100)} · {developmentPlanStatusLabel(plan.status)}
                </option>)}
              </select></label>
              {!activeDevelopmentPlan && <div>
                <label>Objectif<textarea rows={3} maxLength={4000} value={developmentGoal}
                  disabled={!teamId || developmentPlanBusy}
                  onChange={event => setDevelopmentGoal(event.target.value)}
                  placeholder="Ex. Ajouter la persistance locale et tester le parcours de tâches." />
                </label>
                <button className="button primary" disabled={!teamId || developmentPlanBusy ||
                  !developmentGoal.trim() || !selectedProject.hasWorkspace}
                  onClick={() => void createDevelopmentPlan()}>
                  {developmentPlanBusy ? <><LoaderCircle size={13} /> Planification…</> :
                    'Créer un plan avec le Planner'}
                </button>
              </div>}
              {activeDevelopmentPlan && <>
                <p>{activeDevelopmentPlan.goal}</p>
                <p className="muted small">
                  Classez les propositions du Planner et associez explicitement les scénarios qui vérifient chaque critère.
                  Vérifiez leur pertinence : un build seul ne prouve pas une fonctionnalité.
                </p>
                <p>Exécution : {activeDevelopmentPlan.tasks.filter(task => task.status === 'Completed').length}/{activeDevelopmentPlan.tasks.length} tâches.
                  {' '}Critères : {activeDevelopmentPlan.tasks.flatMap(task => task.criterionReviews ?? []).filter(item => item.status === 'Verified').length} vérifié(s),
                  {' '}{activeDevelopmentPlan.tasks.flatMap(task => task.criterionReviews ?? []).filter(item => item.status === 'Failed').length} échoué(s),
                  {' '}{activeDevelopmentPlan.tasks.flatMap(task => task.criterionReviews ?? []).filter(item => item.status === 'Unverified' && item.category !== 'Optional').length} non vérifié(s).
                </p>
                {projectRequests.length > 0 && <details open>
                  <summary>Demandes du projet</summary>
                  <ol>{projectRequests.map((message, index) => <li key={`${message.createdAt}-${index}`}>
                    <time>{new Date(message.createdAt).toLocaleString()}</time> : {message.content}
                    <ul>{message.developmentTaskIds?.map(id => {
                      const item = activeDevelopmentPlan.tasks.find(task => task.id === id)
                      return item && <li key={id}>{item.title} · {developmentTaskStatusLabel(item.status)}
                        {item.summary && ` · ${item.summary}`}</li>
                    })}</ul>
                  </li>)}</ol>
                </details>}
                <ol className="development-plan-tasks">
                  {activeDevelopmentPlan.tasks.map(task => <li key={task.id}>
                    <strong>{task.order + 1}. {task.title}</strong>
                    <span>{developmentTaskStatusLabel(task.status)} · {task.attempts} tentative(s)</span>
                    <p>{task.description}</p>
                    <ul>{task.acceptanceCriteria.map((criterion, index) => <CriterionEditor
                      key={`${task.id}-criterion-${index}`} criterion={criterion}
                      review={task.criterionReviews?.[index] ?? {
                        category: 'Unclassified', status: 'Unverified', evidence: null, reviewedAt: null, current: false,
                        runId: null, reportId: null, scenarioId: null,
                      }}
                      taskStatus={task.status}
                      runs={runs} workspaceId={selectedProject.id}
                      disabled={developmentPlanBusy || ['Running', 'Pausing', 'Cancelling'].includes(activeDevelopmentPlan.status)}
                      save={(category, status, evidence, source) => reviewCriterion(task.id, index, category, status, evidence, source)}
                    />)}</ul>
                    {(task.criterionReviewHistory?.length ?? 0) > 0 && <details>
                      <summary>Historique des vérifications</summary>
                      <ul>{task.criterionReviewHistory.map((item, index) => <li key={index}>
                        {formatDate(item.recordedAt)} · {task.acceptanceCriteria[item.criterionIndex]} · {item.category} · {item.status}
                        {item.evidence && ` · ${item.evidence}`}
                        {item.reportId && ` · rapport ${item.reportId}, scénario ${item.scenarioId}`}
                      </li>)}</ul>
                    </details>}
                    {task.dependencies.length > 0 && <p className="muted small">
                      Dépend de : {task.dependencies.join(', ')}
                    </p>}
                    {task.changedFiles.length > 0 && <p className="muted small">
                      Fichiers : {task.changedFiles.join(', ')}
                    </p>}
                    {task.error && <p role="alert" className="notice danger">{task.error}</p>}
                    {task.validation && <>
                      <p className="muted small">
                        {validationStatusLabel(task.validation.staticStatus)} · compilation : {
                          task.validation.compilationStatus === 'Passed' ? 'réussie' :
                            task.validation.compilationStatus === 'Failed' ? 'échouée' :
                              task.validation.compilationStatus === 'TimedOut' ? 'délai dépassé' :
                                'non disponible'}
                      </p>
                      {task.validation.diagnostics.length > 0 &&
                        <ValidationDiagnostics diagnostics={task.validation.diagnostics} />}
                    </>}
                    {task.collaborationRunId && runs.find(run => run.id === task.collaborationRunId) &&
                      <button className="button secondary"
                        onClick={() => setSelectedRun(
                          runs.find(run => run.id === task.collaborationRunId) ?? null)}>
                        Voir la collaboration et ses journaux
                      </button>}
                  </li>)}
                </ol>
                {activeDevelopmentPlan.error && <div role="alert" className="notice danger">
                  {activeDevelopmentPlan.error}
                </div>}
                <div className="custom-agent-actions">
                  {activeDevelopmentPlan.status === 'Ready' && <button className="button primary"
                    disabled={developmentPlanBusy} onClick={() => void runDevelopmentPlan('start')}>
                    <Play size={13} /> Démarrer le plan
                  </button>}
                  {activeDevelopmentPlan.status === 'Running' && <>
                    <button className="button secondary" disabled={developmentPlanBusy}
                      onClick={() => void controlDevelopmentPlan('pause')}>Mettre en pause</button>
                    <button className="button secondary" disabled={developmentPlanBusy}
                      onClick={() => void controlDevelopmentPlan('cancel')}>Annuler le plan</button>
                  </>}
                  {activeDevelopmentPlan.status === 'Paused' && <button className="button primary"
                    disabled={developmentPlanBusy} onClick={() => void runDevelopmentPlan('resume')}>
                    <Play size={13} /> Reprendre après autorisation
                  </button>}
                  {activeDevelopmentPlan.status === 'Failed' &&
                    activeDevelopmentPlan.attempts < activeDevelopmentPlan.maxAttempts &&
                    <button className="button primary" disabled={developmentPlanBusy ||
                      !activeDevelopmentPlan.tasks.some(task => task.status === 'Failed')}
                      onClick={() => void runDevelopmentPlan('retry')}>
                      <RotateCcw size={13} /> Réessayer la tâche échouée
                    </button>}
                </div>
                {!['Running', 'Pausing', 'Cancelling'].includes(activeDevelopmentPlan.status) &&
                  activeDevelopmentPlan.tasks.length < 20 && <div>
                  <label>Ajouter une demande au plan<textarea rows={2} maxLength={4000}
                    value={developmentExtension} disabled={developmentPlanBusy}
                    onChange={event => setDevelopmentExtension(event.target.value)}
                    placeholder="Décrire les tâches supplémentaires sans répéter celles terminées." />
                  </label>
                  <button className="button secondary" disabled={developmentPlanBusy ||
                    !developmentExtension.trim()} onClick={() => void extendDevelopmentPlan()}>
                    <Plus size={13} /> Étendre le plan
                  </button>
                </div>}
                <details>
                  <summary>Mémoire projet persistée</summary>
                  <p>{activeDevelopmentPlan.memory.architecture}</p>
                  {activeDevelopmentPlan.memory.completedFeatures.length > 0 &&
                    <p>Fonctionnalités : {activeDevelopmentPlan.memory.completedFeatures.join(' · ')}</p>}
                  {activeDevelopmentPlan.memory.decisions.length > 0 &&
                    <p>Décisions : {activeDevelopmentPlan.memory.decisions.join(' · ')}</p>}
                  {activeDevelopmentPlan.memory.keyFiles.length > 0 &&
                    <p>Fichiers clés : {activeDevelopmentPlan.memory.keyFiles.join(', ')}</p>}
                  {activeDevelopmentPlan.memory.knownIssues.length > 0 &&
                    <p>Problèmes connus : {activeDevelopmentPlan.memory.knownIssues.join(' · ')}</p>}
                </details>
              </>}
            </>}
            </section>}
            {supportsLocalApplication && selectedProject && <section className="custom-agent-proposal browser-tests"
                aria-label="Tests navigateur">
                <header><strong>Tests navigateur</strong>
                  <span>{browserTest
                    ? `${browserTest.passed} réussi(s) · ${browserTest.failed} échoué(s)`
                    : desktopApp ? 'Aucun résultat' : 'Desktop requis'}</span></header>
                <p className="notice warning">
                  Playwright utilise Edge/Chromium installé localement, un contexte temporaire et des actions limitées. Il ne s’agit pas d’une sandbox de sécurité; les accès réseau hors de l’origine localhost testée sont bloqués.
                </p>
                {!desktopApp
                  ? <p className="muted small">Les tests navigateur sont disponibles uniquement dans AIPlatform Studio Desktop sous Windows.</p>
                  : <div className="custom-agent-actions">
                    <button className="button primary" disabled={browserTestBusy || busy ||
                      !selectedProject.hasWorkspace || activeStatuses.has(selectedRun?.status ?? '')}
                      onClick={() => void runBrowserTests()}>
                      {browserTestBusy ? <><LoaderCircle size={13} className="spin" /> Tests en cours…</> :
                        <><Globe size={13} /> Tester l’application</>}
                    </button>
                    {browserTestBusy && <button className="button secondary"
                      onClick={() => void cancelBrowserTests()}>Annuler les tests</button>}
                  </div>}
                {browserTest && <div className="browser-test-results" aria-live="polite">
                  <strong>{({
                    Passed: 'Tests navigateur réussis',
                    Failed: 'Échec des tests navigateur',
                    InvalidScenario: 'Scénario de test invalide',
                    BrowserUnavailable: 'Chromium indisponible',
                    ServerUnavailable: 'Serveur local indisponible',
                    TimedOut: 'Délai des tests dépassé',
                    Unsupported: 'Projet non compatible',
                    Cancelled: 'Tests annulés',
                  } as Record<string, string>)[browserTest.status] ?? browserTest.status}</strong>
                  {browserTest.url && <p className="muted small">URL vérifiée : <code>{browserTest.url}</code></p>}
                  {browserTest.error && <div role="alert" className="notice danger">{browserTest.error}</div>}
                  {browserTest.scenarios.map(scenario => <article key={scenario.id}>
                    <strong>{scenario.name} · {scenario.status === 'Passed' ? 'Réussi' : 'Échec'}</strong>
                    {scenario.failedStep && <p className="muted small">Étape : {scenario.failedStep}</p>}
                    {scenario.error && <p role="alert" className="muted small">{scenario.error}</p>}
                    {scenario.javaScriptErrors.map((message, index) =>
                      <p key={`${scenario.id}-${index}`} className="muted small">JavaScript : {message}</p>)}
                    {scenario.screenshotId && <button className="button secondary"
                      onClick={() => void showBrowserScreenshot(browserTest, scenario)}>
                      Voir la capture locale</button>}
                    {browserScreenshot?.scenarioId === scenario.id &&
                      <img className="browser-test-screenshot" src={browserScreenshot.url}
                        alt={`Capture d’écran du scénario ${scenario.name}`} />}
                  </article>)}
                  <p className="muted small">Dernier test : {formatDate(browserTest.testedAt)}</p>
                </div>}
                {!browserTest && selectedRun?.workspace?.workspaceId === selectedProject.id &&
                  (selectedRun.browserScenarios?.length ?? 0) > 0 &&
                  <p className="muted small">
                    {selectedRun!.browserScenarios!.length} scénario(s) fonctionnel(s) proposé(s) par le Planner seront utilisés.
                  </p>}
            </section>}
            {workspace?.kind === 'External' && <>
              {desktopApp && !externalWriteAuthorized && <button className="button secondary"
                disabled={!externalGrantId || busy} onClick={() => void authorizeExternalWrites()}>
                Autoriser les modifications pour cette session</button>}
              {desktopApp && externalWriteAuthorized && <p className="notice warning">
                Chaque proposition attend encore votre confirmation et crée une sauvegarde native.</p>}
              <label className="workspace-consent"><input type="checkbox" checked={externalConsent}
                disabled={!externalFiles.length || busy}
                onChange={event => setExternalConsent(event.target.checked)} />
                J’autorise l’envoi de l’instantané à Ollama local pour cette exécution.</label>
              <p className="muted small">Les fichiers sont transmis temporairement pour cette exécution et ne sont pas conservés dans l’historique.</p>
            </>}
          </section>

          <section className="custom-agent-form">
            <h3>Nouvelle collaboration</h3>
            <p className="muted small">Workspace actif : {workspaceLabel}</p>
            <label>Demande<textarea rows={4} maxLength={4000} value={task} disabled={!teamId || busy}
              onChange={event => setTask(event.target.value)}
              placeholder="Ex. Crée une application React de gestion de tâches…" /></label>
            <div className="custom-agent-actions">
              <button className="button primary" disabled={!teamId || busy || !task.trim() ||
                workspace?.kind === 'External' && (!externalFiles.length || !externalConsent) ||
                mode === 'Autonomous' && (!desktopApp || workspace?.kind !== 'Studio')}
                onClick={() => void startCollaboration()}>
                {busy ? <><LoaderCircle size={13} /> Traitement…</> :
                  mode === 'Autonomous' ? 'Lancer en mode autonome' : 'Lancer la collaboration'}</button>
              {selectedRun && activeStatuses.has(selectedRun.status) && <button className="button secondary"
                disabled={busy} onClick={() => void cancelRun()}>Annuler</button>}
            </div>
            <label>Mode d’exécution<select value={mode} disabled={busy}
              onChange={event => setMode(event.target.value as 'Supervised' | 'Autonomous')}>
              <option value="Supervised">Supervisé · proposition et approbation</option>
              <option value="Autonomous" disabled={!desktopApp || workspace?.kind !== 'Studio'}>
                Autonome · projet Studio uniquement</option>
            </select></label>
            {mode === 'Autonomous' && <p className="notice warning">
              Les modifications sont appliquées automatiquement dans le projet Studio. npm/MSBuild peuvent exécuter du code du projet. Le mode autonome reste indisponible pour les dossiers externes.</p>}
          </section>

          <section className="custom-agent-chat-layout">
            <aside className="custom-conversation-list" aria-label="Historique des collaborations">
              <strong>Historique</strong>
              {runs.map(run => <button key={run.id}
                className={`custom-agent-choice ${selectedRun?.id === run.id ? 'selected' : ''}`}
                onClick={() => setSelectedRun(run)} disabled={busy}>
                <strong>{run.task}</strong><span>{stageLabel(run)} · {formatDate(run.updatedAt)}</span>
              </button>)}
              {!runs.length && <p className="muted small">Aucune exécution enregistrée.</p>}
            </aside>
            <div className="custom-chat">
              {!selectedRun ? <div className="empty compact"><p>Sélectionnez une exécution pour suivre les contributions.</p></div>
                : <>
                  <header className="custom-workspace-heading">
                    <div><strong>{selectedRun.mode === 'Autonomous' ? 'Autonome · ' : 'Supervisé · '}{stageLabel(selectedRun)}</strong><span>{selectedRun.task}</span></div>
                    {activeStatuses.has(selectedRun.status) && <LoaderCircle size={16} className="spin" />}
                  </header>
                  {(selectedRun.modelAttempts?.length ?? 0) > 0 && <section aria-label="Sélection des modèles">
                    <h4>Modèles et décisions</h4>
                    <p>Modèle actif / dernier utilisé : <strong>{selectedRun.modelAttempts?.at(-1)?.modelName}</strong></p>
                    <ol>{selectedRun.modelAttempts?.map(attempt => <li key={attempt.id}>
                      <strong>{roleLabels[attempt.role]} · {attempt.modelName}</strong> · {attempt.outcome}
                      <p>{attempt.selectionReason}</p>
                      {attempt.routingDecision && <RoutingExplanation decision={attempt.routingDecision} />}
                      <p>{attempt.decision}</p>
                      <p>Prochain modèle : {attempt.nextModel ?? 'aucun changement prévu'} · Compilation/tests : {attempt.compilationStatus}
                        {attempt.failureKind && ` · ${attempt.failureKind}`}</p>
                    </li>)}</ol>
                  </section>}
                  {selectedRun.contributions.map((contribution, index) => <article
                    key={`${contribution.agentId}-${index}`} className="custom-chat-message assistant">
                    <strong>{contribution.role === 'Developer' && index > 2
                      ? 'Correction du Développeur'
                      : roleLabels[contribution.role]} · {contribution.agentName}</strong>
                    <Markdown>{contribution.content}</Markdown>
                    <span className="badge quiet">{contribution.modelName} · {formatDate(contribution.createdAt)}</span>
                  </article>)}
                  {selectedRun.currentStep && <article className="custom-chat-message assistant" aria-live="polite">
                    <strong>{stageLabel(selectedRun)} en cours</strong>
                    <p><LoaderCircle size={13} className="spin" /> L’agent prépare sa contribution…</p>
                  </article>}
                  {selectedRun.error && <div role="alert" className="notice danger">{selectedRun.error}</div>}
                  {selectedRun.validation && <section className="custom-agent-proposal custom-agent-validation"
                    aria-label="Résultat de validation">
                    <header><strong>{validationStatusLabel(selectedRun.validation.staticStatus)}</strong>
                      <span>{selectedRun.validation.correctionAttempts} correction(s)</span></header>
                    <p className="muted small">{selectedRun.validation.compilationStatus === 'Passed'
                      ? 'Compilation et tests réussis.'
                      : selectedRun.validation.compilationStatus === 'Failed'
                        ? 'Échec de compilation ou de test.'
                        : selectedRun.validation.compilationStatus === 'TimedOut'
                          ? 'La commande a dépassé son délai.'
                          : 'Compilation non disponible; aucun résultat de compilation n’est revendiqué.'}</p>
                    {selectedRun.validation.diagnostics.length > 0
                      ? <ValidationDiagnostics diagnostics={selectedRun.validation.diagnostics} />
                      : <p className="muted small">Aucun diagnostic statique détecté.</p>}
                    {selectedRun.validation.history.length > 1 && <details>
                      <summary>Historique des validations</summary>
                      {selectedRun.validation.history.map(attempt => <div key={attempt.number}>
                        <strong>Tentative {attempt.number + 1} · {validationStatusLabel(attempt.status)}</strong>
                        {attempt.diagnostics.length > 0 &&
                          <ValidationDiagnostics diagnostics={attempt.diagnostics} />}
                      </div>)}
                    </details>}
                  </section>}
                  {(selectedRun.executionResults?.length ?? 0) > 0 && <section className="custom-agent-proposal"
                    aria-label="Sorties des commandes locales">
                    <header><strong>Journal d’exécution local</strong>
                      <span>Lecture seule · aucune isolation de sécurité</span></header>
                    {(selectedRun.executionResults ?? []).map((item, index) => <details key={`${item.command}-${index}`}>
                      <summary>{item.command} · {item.timedOut ? 'Délai dépassé' :
                        item.exitCode === 0 ? 'Réussi' : `Code ${item.exitCode ?? 'inconnu'}`}</summary>
                      <pre>{item.output || '(aucune sortie)'}</pre>
                    </details>)}
                  </section>}
                  {selectedRun.mode === 'Autonomous' && (selectedRun.appliedChanges?.length ?? 0) > 0 &&
                    <section className="custom-agent-proposal">
                      <header><strong>Fichiers appliqués automatiquement</strong>
                        <span>{(selectedRun.appliedChanges ?? []).length} fichier(s)</span></header>
                      {(selectedRun.appliedChanges ?? []).map(change => <details key={change.path}>
                        <summary>{change.operation} · {change.path} — {change.summary}</summary>
                        {change.before !== null && <div><strong>Avant</strong><pre>{change.before}</pre></div>}
                        {change.after !== null && <div><strong>Après</strong><pre>{change.after}</pre></div>}
                      </details>)}
                      {(selectedRun.status === 'Completed' || selectedRun.status === 'Failed') &&
                        <div className="custom-agent-actions">
                        {selectedRun.workspace?.workspaceId &&
                          <button className="button secondary" onClick={() => onOpenProject(selectedRun.workspace!.workspaceId!)}>
                            Ouvrir le projet</button>}
                        <button className="button secondary" disabled={busy}
                          onClick={() => void (async () => {
                            if (!teamId || !window.confirm('Restaurer les fichiers à leur état antérieur à cette exécution ?')) return
                            setBusy(true)
                            try {
                              const restored = await api.restoreAutonomousCustomAgentRun(teamId, selectedRun.id)
                              setSelectedRun(restored)
                              setRuns(current => current.map(item => item.id === restored.id ? restored : item))
                            } catch (reason) { setError(errorMessage(reason)) }
                            finally { setBusy(false) }
                          })()}>
                          <RotateCcw size={13} /> Restaurer</button>
                      </div>}
                    </section>}
                  {selectedRun.proposal && <section className="custom-agent-proposal">
                    <header><strong>Proposition consolidée · {selectedRun.proposal.status}</strong>
                      <span>{selectedRun.proposal.changes.length} fichier(s)</span></header>
                    {selectedRun.proposal.changes.map((change: CustomAgentFileChange) =>
                      <details key={change.path} className="proposal-change">
                        <summary>{change.operation === 'create' ? 'Création' : change.operation === 'delete'
                          ? 'Suppression' : 'Modification'} · {change.path} — {change.summary}</summary>
                        {change.operation !== 'create' && <div><strong>Avant</strong>
                          <pre>{change.before ?? '(absent)'}</pre></div>}
                        {change.operation !== 'delete' && <div><strong>Après</strong>
                          <pre>{change.after ?? '(absent)'}</pre></div>}
                      </details>)}
                    {selectedRun.proposal.status === 'Pending' && <div className="custom-agent-actions">
                      <button className="button primary" disabled={busy || !(selectedRun.proposal.canApply ||
                        selectedRun.proposal.workspace?.kind === 'External' && desktopApp &&
                        externalGrantId === selectedRun.proposal.workspace.authorizationId && externalWriteAuthorized)}
                        onClick={() => void decideProposal(selectedRun, true)}>
                        <Check size={13} /> Appliquer la proposition</button>
                      <button className="button secondary" disabled={busy}
                        onClick={() => void decideProposal(selectedRun, false)}><X size={13} /> Refuser</button>
                    </div>}
                    {selectedRun.proposal.status === 'Applied' &&
                      selectedRun.proposal.workspace?.kind === 'External' &&
                      selectedRun.proposal.externalBackupId && !selectedRun.proposal.externalRestoredAt &&
                      <button className="button secondary" disabled={busy || !externalGrantId || !externalWriteAuthorized}
                        onClick={() => void restoreExternalProposal(selectedRun)}>
                        <RotateCcw size={13} /> Restaurer les fichiers</button>}
                    {selectedRun.proposal.status === 'Applied' &&
                      selectedRun.proposal.workspace?.kind === 'Studio' &&
                      selectedRun.proposal.workspace.workspaceId && <button className="button secondary"
                        onClick={() => onOpenProject(selectedRun.proposal!.workspace!.workspaceId!)}>
                        Ouvrir le projet</button>}
                    {selectedRun.proposal.status === 'Pending' && !selectedRun.proposal.canApply &&
                      selectedRun.proposal.workspace?.kind !== 'External' &&
                      <p className="muted small">{selectedRun.proposal.applyReason}</p>}
                  </section>}
                </>}
            </div>
          </section>
        </div>
      </div>
    </>}
  </section>
}
