import { useEffect, useRef, useState, type ChangeEvent } from 'react'
import { invoke } from '@tauri-apps/api/core'
import { Check, FolderOpen, Globe, LoaderCircle, Play, Plus, RotateCcw, Save, Square, Trash2, X } from 'lucide-react'
import { api, errorMessage } from './api'
import { Markdown } from './Markdown'
import type {
  CustomAgent, CustomAgentCollaborationRun, CustomAgentFileChange, CustomAgentTeam,
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
  agents, projects, desktopApp, onProjectCreated, onOpenProject,
}: {
  agents: CustomAgent[]
  projects: Mission[]
  desktopApp: boolean
  onProjectCreated: (project: Mission) => void
  onOpenProject: (id: string) => void
}) {
  const [teams, setTeams] = useState<CustomAgentTeam[]>([])
  const [teamId, setTeamId] = useState<string | null>(null)
  const [teamName, setTeamName] = useState('')
  const [memberIds, setMemberIds] = useState<string[]>(['', '', ''])
  const [runs, setRuns] = useState<CustomAgentCollaborationRun[]>([])
  const [selectedRun, setSelectedRun] = useState<CustomAgentCollaborationRun | null>(null)
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
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const browserFolderInput = useRef<HTMLInputElement>(null)
  const nativeGrantRef = useRef<string | null>(null)
  const selectedProject = workspace?.kind === 'Studio'
    ? projects.find(project => project.id === workspace.workspaceId)
    : undefined
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
    setSelectedRun(null)
    setError(null)
  }

  function newTeam() {
    const available = agents.slice(0, 3).map(agent => agent.id)
    setTeamId(null)
    setTeamName('')
    setMemberIds([available[0] ?? '', available[1] ?? '', available[2] ?? ''])
    setRuns([])
    setSelectedRun(null)
    setError(null)
  }

  async function saveTeam() {
    if (busy) return
    setBusy(true)
    setError(null)
    try {
      const members: CustomAgentTeamMember[] = roles.map((role, order) => ({
        agentId: memberIds[order] ?? '', role, order,
      }))
      const saved = await api.saveCustomAgentTeam(teamName, members, teamId ?? undefined)
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
    setWorkspace(project ? {
      kind: 'Studio', workspaceId: project.id, name: project.projectName || project.task.slice(0, 120),
    } : null)
  }

  const canSaveTeam = teamName.trim().length > 0 && memberIds.length === roles.length &&
    memberIds.every(Boolean) && new Set(memberIds).size === roles.length
  const workspaceLabel = workspace?.kind === 'External'
    ? `${workspace.name} · ${desktopApp ? 'dossier Windows' : 'lecture seule'}`
    : workspace?.kind === 'Studio' ? workspace.name : 'Aucun workspace (résultat textuel uniquement)'

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
            {roles.map((role, order) => <label key={role}>{order + 1}. {roleLabels[role]}<select
              value={memberIds[order] ?? ''} disabled={busy}
              onChange={event => setMemberIds(current => current.map((id, index) =>
                index === order ? event.target.value : id))}>
              <option value="">Choisir un agent</option>
              {agents.map(agent => <option key={agent.id} value={agent.id}
                disabled={memberIds.some((id, index) => index !== order && id === agent.id)}>
                {agent.name} · {agent.modelName}</option>)}
            </select></label>)}
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
