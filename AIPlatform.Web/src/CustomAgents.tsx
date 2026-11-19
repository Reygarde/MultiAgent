import { useEffect, useRef, useState, type ChangeEvent, type FormEvent } from 'react'
import { invoke, isTauri } from '@tauri-apps/api/core'
import { Bot, Copy, FileText, FolderOpen, LoaderCircle, MessageSquarePlus, Pencil, Plus, Search, Send, Trash2 } from 'lucide-react'
import { api, errorMessage } from './api'
import { Markdown } from './Markdown'
import { CustomAgentTeams } from './CustomAgentTeams'
import { OllamaFallbackModels } from './OllamaFallbackModels'
import { RoutingExplanation } from './ModelLibrary'
import { maximumModelContext } from './types'
import type {
  CustomAgent, CustomAgentConversation, CustomAgentConversationSummary, CustomAgentDraft,
  CustomAgentFileChange, CustomAgentProposal, CustomAgentWorkspace, CustomAgentWorkspaceFile, Mission,
  NativeExternalFolderSelection, WorkspaceFile,
} from './types'

const emptyDraft: CustomAgentDraft = { name: '', description: '', systemPrompt: '', modelName: '' }
const maxWorkspaceFiles = 80
const maxWorkspaceFileBytes = 16_384
const maxWorkspaceContextBytes = 24_000
const sourceExtensions = new Set([
  '.cs', '.csproj', '.sln', '.slnx', '.json', '.md', '.ts', '.tsx', '.js', '.jsx',
  '.html', '.css', '.py', '.go', '.rs', '.java', '.xml', '.yaml', '.yml',
])
const excludedNames = new Set([
  'bin', 'obj', 'node_modules', '.git', '.vs', '.idea', 'dist', 'coverage', 'target',
  '.aws', '.azure', '.ssh', '.docker', '.kube', '.gnupg',
])

function isSourcePath(path: string) {
  const segments = path.split('/')
  const name = segments[segments.length - 1] ?? ''
  const extension = name.includes('.') ? name.slice(name.lastIndexOf('.')).toLowerCase() : ''
  const lower = name.toLowerCase()
  return !segments.some(segment => excludedNames.has(segment.toLowerCase())) &&
    !lower.startsWith('.env') && !lower.startsWith('appsettings') &&
    !['.npmrc', '.yarnrc', '.yarnrc.yml', '.pypirc'].includes(lower) &&
    !['secret', 'credential', 'password', 'token'].some(term => lower.includes(term)) &&
    !['.pem', '.key', '.p12', '.pfx', '.crt', '.cer'].includes(extension) &&
    (sourceExtensions.has(extension) || lower === 'dockerfile')
}

function getSnippet(content: string, query: string) {
  const index = content.toLocaleLowerCase().indexOf(query.toLocaleLowerCase())
  if (index < 0) return ''
  const start = Math.max(0, content.lastIndexOf('\n', index - 1) - 120)
  const lineEnd = content.indexOf('\n', index + query.length)
  const end = Math.min(content.length, (lineEnd < 0 ? content.length : lineEnd) + 160)
  return content.slice(start, end).trim()
}

function summaryOf(conversation: CustomAgentConversation): CustomAgentConversationSummary {
  return {
    id: conversation.id,
    agentId: conversation.agentId,
    title: conversation.title,
    createdAt: conversation.createdAt,
    updatedAt: conversation.updatedAt,
    messageCount: conversation.messages.length,
  }
}

function formatDate(value: string) {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? 'Date indisponible' : date.toLocaleString('fr-FR')
}

export function CustomAgents({ onOpenProject = () => undefined }: { onOpenProject?: (id: string) => void } = {}) {
  const [activeView, setActiveView] = useState<'agents' | 'teams'>('agents')
  const [agents, setAgents] = useState<CustomAgent[]>([])
  const [models, setModels] = useState<string[]>([])
  const [projects, setProjects] = useState<Mission[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [conversations, setConversations] = useState<CustomAgentConversationSummary[]>([])
  const [conversation, setConversation] = useState<CustomAgentConversation | null>(null)
  const [draft, setDraft] = useState<CustomAgentDraft>(emptyDraft)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [message, setMessage] = useState('')
  const [loading, setLoading] = useState(true)
  const [modelsLoading, setModelsLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [sending, setSending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [modelError, setModelError] = useState<string | null>(null)
  const [projectError, setProjectError] = useState<string | null>(null)
  const [externalFiles, setExternalFiles] = useState<CustomAgentWorkspaceFile[]>([])
  const [externalAuthorized, setExternalAuthorized] = useState(false)
  const [nativeGrantId, setNativeGrantId] = useState<string | null>(null)
  const [externalWriteAuthorized, setExternalWriteAuthorized] = useState(false)
  const [workspaceFileList, setWorkspaceFileList] = useState<WorkspaceFile[]>([])
  const [workspaceSnapshot, setWorkspaceSnapshot] = useState<CustomAgentWorkspaceFile[]>([])
  const [workspaceSearch, setWorkspaceSearch] = useState('')
  const [workspaceResults, setWorkspaceResults] = useState<{ path: string; snippet: string }[]>([])
  const [selectedFile, setSelectedFile] = useState<CustomAgentWorkspaceFile | null>(null)
  const [workspaceBusy, setWorkspaceBusy] = useState(false)
  const [workspaceError, setWorkspaceError] = useState<string | null>(null)
  const [decisionBusy, setDecisionBusy] = useState(false)
  const folderInput = useRef<HTMLInputElement>(null)
  const sendController = useRef<AbortController | null>(null)
  const nativeGrantRef = useRef<string | null>(null)
  const sendingRef = useRef(false)
  const desktopApp = isTauri()
  const selected = agents.find(agent => agent.id === selectedId) ?? null
  const activeWorkspace = conversation?.workspace ?? null
  const unavailableModel = Boolean(selected && !modelError && !models.includes(selected.modelName))

  function canApplyExternalProposal(proposal: CustomAgentProposal) {
    return desktopApp && activeWorkspace?.kind === 'External' && nativeGrantId !== null &&
      proposal.workspace?.authorizationId === nativeGrantId && externalWriteAuthorized
  }

  function proposalApplyReason(proposal: CustomAgentProposal) {
    if (proposal.workspace?.kind !== 'External') return proposal.applyReason
    if (!desktopApp) return 'Écriture disponible uniquement dans l’application Desktop.'
    if (!nativeGrantId) return 'Resélectionnez le dossier Windows.'
    if (!externalWriteAuthorized) return 'Autorisez les modifications pour ce dossier.'
    if (proposal.workspace.authorizationId !== nativeGrantId)
      return 'Cette proposition appartient à une sélection antérieure; demandez-en une nouvelle.'
    return proposal.applyReason
  }

  function releaseNativeGrant() {
    const grantId = nativeGrantRef.current
    nativeGrantRef.current = null
    setNativeGrantId(null)
    setExternalWriteAuthorized(false)
    if (desktopApp && grantId)
      void invoke('release_external_workspace', { grantId })
        .catch(error => setWorkspaceError(errorMessage(error)))
  }

  function resetWorkspaceBrowser() {
    releaseNativeGrant()
    setExternalFiles([])
    setExternalAuthorized(false)
    setWorkspaceFileList([])
    setWorkspaceSnapshot([])
    setWorkspaceSearch('')
    setWorkspaceResults([])
    setSelectedFile(null)
    setWorkspaceError(null)
  }

  useEffect(() => {
    let active = true
    async function load() {
      setLoading(true)
      const [agentResult, modelResult, projectResult] = await Promise.allSettled([
        api.customAgents(), api.customAgentModels(), api.missions(),
      ])
      if (!active) return
      if (agentResult.status === 'fulfilled') setAgents(agentResult.value)
      else setError(errorMessage(agentResult.reason))
      if (modelResult.status === 'fulfilled') setModels(modelResult.value)
      else setModelError(errorMessage(modelResult.reason))
      if (projectResult.status === 'fulfilled')
        setProjects(projectResult.value.filter(mission => mission.hasWorkspace && !mission.deleted))
      else setProjectError(errorMessage(projectResult.reason))
      setLoading(false)
    }
    void load()
    return () => {
      active = false
      sendController.current?.abort()
      const grantId = nativeGrantRef.current
      nativeGrantRef.current = null
      if (grantId && isTauri())
        void invoke('release_external_workspace', { grantId }).catch(() => undefined)
    }
  }, [])

  useEffect(() => {
    let active = true
    const workspace = conversation?.workspace
    if (workspace?.kind === 'External') {
      setWorkspaceSnapshot([])
      setSelectedFile(null)
      return () => { active = false }
    }
    if (workspace?.kind !== 'Studio' || !workspace.workspaceId) {
      setWorkspaceFileList([])
      setWorkspaceSnapshot([])
      setSelectedFile(null)
      return () => { active = false }
    }
    setWorkspaceBusy(true)
    setWorkspaceError(null)
    void api.files(workspace.workspaceId).then(files => {
      if (active) setWorkspaceFileList(files.filter(file => isSourcePath(file.path)))
    }).catch(error => {
      if (active) setWorkspaceError(errorMessage(error))
    }).finally(() => {
      if (active) setWorkspaceBusy(false)
    })
    return () => { active = false }
  }, [conversation?.id, conversation?.workspace?.kind, conversation?.workspace?.workspaceId])

  async function selectAgent(agent: CustomAgent) {
    setSelectedId(agent.id)
    setEditingId(null)
    setConversation(null)
    setConversations([])
    setError(null)
    resetWorkspaceBrowser()
    try { setConversations(await api.customAgentConversations(agent.id)) }
    catch (error) { setError(errorMessage(error)) }
  }

  async function retryModels() {
    setModelsLoading(true)
    setModelError(null)
    try { setModels(await api.customAgentModels()) }
    catch (error) { setModelError(errorMessage(error)) }
    finally { setModelsLoading(false) }
  }

  function startCreate() {
    setSelectedId(null)
    setConversation(null)
    setConversations([])
    setEditingId('')
    setDraft({ ...emptyDraft, modelName: models[0] ?? '' })
    setError(null)
    resetWorkspaceBrowser()
  }

  function startEdit() {
    if (!selected) return
    setEditingId(selected.id)
    setDraft({
      name: selected.name, description: selected.description,
      systemPrompt: selected.systemPrompt, modelName: selected.modelName,
      fallbackModels: selected.fallbackModels,
      automaticModelSelection: selected.automaticModelSelection,
      generation: selected.generation,
    })
    setError(null)
  }

  async function saveAgent(event: FormEvent) {
    event.preventDefault()
    if (saving) return
    setSaving(true)
    setError(null)
    try {
      const saved = editingId
        ? await api.updateCustomAgent(editingId, draft)
        : await api.createCustomAgent(draft)
      setAgents(current => editingId
        ? current.map(agent => agent.id === saved.id ? saved : agent)
        : [...current, saved])
      setSelectedId(saved.id)
      setConversations([])
      setConversation(null)
      setEditingId(null)
    } catch (error) { setError(errorMessage(error)) }
    finally { setSaving(false) }
  }

  async function duplicateAgent(agent: CustomAgent) {
    setError(null)
    try {
      const duplicate = await api.duplicateCustomAgent(agent.id)
      setAgents(current => [...current, duplicate])
      setSelectedId(duplicate.id)
      setConversations([])
      setConversation(null)
    } catch (error) { setError(errorMessage(error)) }
  }

  async function deleteAgent(agent: CustomAgent) {
    if (!window.confirm(`Supprimer « ${agent.name} » et tout son historique de conversations ?`)) return
    setError(null)
    try {
      await api.deleteCustomAgent(agent.id)
      setAgents(current => current.filter(item => item.id !== agent.id))
      if (selectedId === agent.id) {
        setSelectedId(null)
        setConversation(null)
        setConversations([])
        resetWorkspaceBrowser()
      }
    } catch (error) { setError(errorMessage(error)) }
  }

  async function startConversation() {
    if (!selected) return
    setError(null)
    try {
      const created = await api.createCustomAgentConversation(selected.id)
      setConversation(created)
      setConversations(current => [summaryOf(created), ...current])
      setMessage('')
      resetWorkspaceBrowser()
    } catch (error) { setError(errorMessage(error)) }
  }

  async function openConversation(item: CustomAgentConversationSummary) {
    if (!selected) return
    setError(null)
    resetWorkspaceBrowser()
    try { setConversation(await api.customAgentConversation(selected.id, item.id)) }
    catch (error) { setError(errorMessage(error)) }
  }

  async function setConversationWorkspace(workspace: CustomAgentWorkspace | null) {
    if (!selected || !conversation || sending) return
    setWorkspaceError(null)
    setWorkspaceBusy(true)
    try {
      const updated = await api.setCustomAgentWorkspace(selected.id, conversation.id, workspace)
      setConversation(updated)
      releaseNativeGrant()
      setExternalFiles([])
      setExternalAuthorized(false)
      setWorkspaceFileList([])
      setWorkspaceSnapshot([])
      setWorkspaceResults([])
      setSelectedFile(null)
    } catch (error) { setWorkspaceError(errorMessage(error)) }
    finally { setWorkspaceBusy(false) }
  }

  async function chooseExternalFolder(event: ChangeEvent<HTMLInputElement>) {
    const selectedFiles = Array.from(event.currentTarget.files ?? [])
    event.currentTarget.value = ''
    if (!selected || !conversation || selectedFiles.length === 0) return
    releaseNativeGrant()
    setWorkspaceBusy(true)
    setWorkspaceError(null)
    setExternalFiles([])
    setExternalAuthorized(false)
    setWorkspaceFileList([])
    setWorkspaceSnapshot([])
    setWorkspaceResults([])
    setSelectedFile(null)
    try {
      const candidates = selectedFiles.filter(file => isSourcePath(file.webkitRelativePath || file.name))
      const snapshots: CustomAgentWorkspaceFile[] = []
      let totalBytes = 0
      let skipped = selectedFiles.length - candidates.length
      for (const file of candidates) {
        const path = file.webkitRelativePath || file.name
        if (snapshots.length >= maxWorkspaceFiles || file.size > maxWorkspaceFileBytes ||
            totalBytes + file.size > maxWorkspaceContextBytes) {
          skipped++
          continue
        }
        const content = await file.text()
        const bytes = new TextEncoder().encode(content).length
        if (content.includes('\0') || bytes > maxWorkspaceFileBytes ||
            totalBytes + bytes > maxWorkspaceContextBytes) {
          skipped++
          continue
        }
        snapshots.push({ path, content })
        totalBytes += bytes
      }
      if (snapshots.length === 0)
        throw new Error('Aucun fichier texte source admissible n’a été trouvé dans le dossier.')
      const name = (snapshots[0].path.split('/')[0] || 'Dossier sélectionné').slice(0, 120)
      const updated = await api.setCustomAgentWorkspace(selected.id, conversation.id, {
        kind: 'External', workspaceId: null, name,
      })
      setConversation(updated)
      setExternalFiles(snapshots)
      setExternalAuthorized(false)
      setWorkspaceSnapshot(snapshots)
      setWorkspaceFileList(snapshots.map(file => ({ path: file.path, size: new TextEncoder().encode(file.content).length })))
      setSelectedFile(null)
      setWorkspaceError(skipped > 0
        ? `${skipped} fichier(s) ignoré(s) selon les limites et règles de sécurité.`
        : null)
    } catch (error) { setWorkspaceError(errorMessage(error)) }
    finally { setWorkspaceBusy(false) }
  }

  async function chooseNativeFolder() {
    if (!selected || !conversation || workspaceBusy || sending) return
    setWorkspaceBusy(true)
    setWorkspaceError(null)
    try {
      const selection = await invoke<NativeExternalFolderSelection | null>('select_external_workspace')
      if (!selection) return
      try {
        const updated = await api.setCustomAgentWorkspace(selected.id, conversation.id, {
          kind: 'External', workspaceId: null, name: selection.name,
          authorizationId: selection.grantId,
        })
        releaseNativeGrant()
        nativeGrantRef.current = selection.grantId
        setNativeGrantId(selection.grantId)
        setExternalWriteAuthorized(false)
        setConversation(updated)
        setExternalFiles(selection.files)
        setExternalAuthorized(false)
        setWorkspaceSnapshot(selection.files)
        setWorkspaceFileList(selection.files.map(file => ({
          path: file.path, size: new TextEncoder().encode(file.content).length,
        })))
        setSelectedFile(null)
        setWorkspaceResults([])
      } catch (error) {
        await invoke('release_external_workspace', { grantId: selection.grantId })
        throw error
      }
    } catch (error) { setWorkspaceError(errorMessage(error)) }
    finally { setWorkspaceBusy(false) }
  }

  async function enableExternalWrites() {
    if (!nativeGrantId || !desktopApp || workspaceBusy || sending) return
    if (!window.confirm(
      `Autoriser les modifications approuvées dans « ${activeWorkspace?.name} » pour cette session ?\n\nChaque diff demandera encore une confirmation. L’agent ne peut pas écrire directement.`))
      return
    setWorkspaceBusy(true)
    setWorkspaceError(null)
    try {
      await invoke('enable_external_workspace_writes', { grantId: nativeGrantId })
      setExternalWriteAuthorized(true)
    } catch (error) { setWorkspaceError(errorMessage(error)) }
    finally { setWorkspaceBusy(false) }
  }

  async function loadWorkspaceFile(path: string) {
    if (!activeWorkspace) return
    setWorkspaceError(null)
    setWorkspaceBusy(true)
    try {
      const known = workspaceSnapshot.find(file => file.path === path) ??
        externalFiles.find(file => file.path === path)
      const content = known ?? (activeWorkspace.kind === 'Studio' && activeWorkspace.workspaceId
        ? await api.file(activeWorkspace.workspaceId, path)
        : null)
      if (!content) throw new Error('Resélectionnez le dossier externe pour relire ses fichiers.')
      setSelectedFile(content)
    } catch (error) { setWorkspaceError(errorMessage(error)) }
    finally { setWorkspaceBusy(false) }
  }

  async function searchWorkspaceCode() {
    const query = workspaceSearch.trim()
    if (!activeWorkspace || !query) {
      setWorkspaceResults([])
      return
    }
    setWorkspaceBusy(true)
    setWorkspaceError(null)
    try {
      let snapshot = activeWorkspace.kind === 'External' ? externalFiles : workspaceSnapshot
      if (activeWorkspace.kind === 'Studio' && snapshot.length === 0 && activeWorkspace.workspaceId) {
        const files = workspaceFileList.filter(file =>
          isSourcePath(file.path) && file.size <= maxWorkspaceFileBytes).slice(0, maxWorkspaceFiles)
        const loaded: CustomAgentWorkspaceFile[] = []
        let totalBytes = 0
        for (let index = 0; index < files.length; index += 8) {
          const batch = await Promise.all(files.slice(index, index + 8).map(file =>
            api.file(activeWorkspace.workspaceId!, file.path)))
          for (const file of batch) {
            const bytes = new TextEncoder().encode(file.content).length
            if (bytes <= maxWorkspaceFileBytes && totalBytes + bytes <= maxWorkspaceContextBytes &&
                !file.content.includes('\0')) {
              loaded.push(file)
              totalBytes += bytes
            }
          }
        }
        snapshot = loaded
        setWorkspaceSnapshot(loaded)
      }
      const matches = snapshot.flatMap(file => {
        const snippet = getSnippet(file.content, query)
        return snippet ? [{ path: file.path, snippet }] : []
      }).slice(0, 50)
      setWorkspaceResults(matches)
      if (matches.length === 0) setWorkspaceError('Aucune occurrence trouvée dans les fichiers chargés.')
    } catch (error) { setWorkspaceError(errorMessage(error)) }
    finally { setWorkspaceBusy(false) }
  }

  async function decideProposal(
    proposalId: string, changes: CustomAgentFileChange[], approved: boolean, authorizationId?: string | null,
  ) {
    if (!selected || !conversation || decisionBusy) return
    const external = activeWorkspace?.kind === 'External'
    if (approved && external &&
        (!desktopApp || !nativeGrantId || !externalWriteAuthorized)) {
      setWorkspaceError(!desktopApp
        ? 'L’écriture des dossiers externes est disponible uniquement dans AIPlatform Studio Desktop.'
        : !nativeGrantId
          ? 'Resélectionnez le dossier dans le sélecteur natif avant d’appliquer.'
          : 'Autorisez d’abord les modifications pour ce dossier.')
      return
    }
    if (approved && external && authorizationId !== nativeGrantId) {
      setWorkspaceError('Cette proposition appartient à une autre sélection du dossier. Demandez une nouvelle proposition avant de l’appliquer.')
      return
    }
    if (approved && !window.confirm(
      `Appliquer ${changes.length} opération(s) ${external ? `au dossier « ${activeWorkspace.name} »` : 'au projet Studio'} ?\n\nUne sauvegarde sera créée avant l’écriture. Les suppressions seront également restaurables.\n\n${changes.map(change => `${change.operation.toUpperCase()} · ${change.path}`).join('\n')}`))
      return
    setDecisionBusy(true)
    setError(null)
    try {
      let updated: CustomAgentConversation
      if (approved && external && nativeGrantId) {
        const backupId = await invoke<string>('apply_external_workspace_proposal', {
          grantId: nativeGrantId, proposalId,
          changes: changes.map(change => ({
            path: change.path, before: change.before, after: change.after, operation: change.operation,
          })),
        })
        updated = await api.recordExternalProposalOutcome(
          selected.id, conversation.id, proposalId, backupId, false)
        const updatedFiles = externalFiles.filter(file =>
          !changes.some(change => change.path === file.path && change.operation === 'delete'))
        for (const change of changes) {
          if (change.after === null || change.operation === 'delete') continue
          const index = updatedFiles.findIndex(file => file.path === change.path)
          if (index >= 0) updatedFiles[index] = { ...updatedFiles[index], content: change.after }
          else updatedFiles.push({ path: change.path, content: change.after })
        }
        setExternalFiles(updatedFiles)
        setWorkspaceSnapshot(updatedFiles)
        setWorkspaceFileList(updatedFiles.map(file => ({
          path: file.path, size: new TextEncoder().encode(file.content).length,
        })))
      } else {
        updated = await api.decideCustomAgentProposal(selected.id, conversation.id, proposalId, approved)
      }
      setConversation(updated)
      setConversations(current => [summaryOf(updated), ...current.filter(item => item.id !== updated.id)])
    } catch (error) { setError(errorMessage(error)) }
    finally { setDecisionBusy(false) }
  }

  async function restoreExternalProposal(proposalId: string, backupId: string, changes: CustomAgentFileChange[]) {
    if (!selected || !conversation || !nativeGrantId || !externalWriteAuthorized || decisionBusy) return
    if (!window.confirm(
      `Restaurer les versions précédentes de ${changes.length} fichier(s) dans « ${activeWorkspace?.name} » ?\n\nLa restauration est refusée si les fichiers ont changé depuis l’application.`))
      return
    setDecisionBusy(true)
    setError(null)
    try {
      await invoke('restore_external_workspace_proposal', { grantId: nativeGrantId, backupId })
      const updated = await api.recordExternalProposalOutcome(
        selected.id, conversation.id, proposalId, backupId, true)
      setConversation(updated)
      setConversations(current => [summaryOf(updated), ...current.filter(item => item.id !== updated.id)])
      const restoredFiles = externalFiles.map(file => {
        const change = changes.find(item => item.path === file.path)
        return change?.operation !== 'create' && change?.before !== null && change?.before !== undefined
          ? { ...file, content: change.before } : file
      }).filter(file => !changes.some(change => change.path === file.path && change.operation === 'create'))
      for (const change of changes) {
        if (change.operation === 'delete' && change.before !== null &&
            !restoredFiles.some(file => file.path === change.path))
          restoredFiles.push({ path: change.path, content: change.before })
      }
      setExternalFiles(restoredFiles)
      setWorkspaceSnapshot(restoredFiles)
      setWorkspaceFileList(restoredFiles.map(file => ({
        path: file.path, size: new TextEncoder().encode(file.content).length,
      })))
    } catch (error) { setError(errorMessage(error)) }
    finally { setDecisionBusy(false) }
  }
  async function deleteConversation(item: CustomAgentConversationSummary) {
    if (!selected || !window.confirm(`Supprimer « ${item.title} » et ses messages ?`)) return
    setError(null)
    try {
      await api.deleteCustomAgentConversation(selected.id, item.id)
      setConversations(current => current.filter(conversation => conversation.id !== item.id))
      if (conversation?.id === item.id) {
        setConversation(null)
        resetWorkspaceBrowser()
      }
    } catch (error) { setError(errorMessage(error)) }
  }

  async function sendMessage(event: FormEvent) {
    event.preventDefault()
    if (!selected || !conversation || !message.trim() || sendingRef.current) return
    if (activeWorkspace?.kind === 'External' && (!externalFiles.length || !externalAuthorized)) {
      setWorkspaceError('Resélectionnez le dossier et confirmez l’envoi de son instantané texte à Ollama local.')
      return
    }
    sendingRef.current = true
    const controller = new AbortController()
    sendController.current = controller
    setSending(true)
    setError(null)
    try {
      const updated = activeWorkspace?.kind === 'External'
        ? await api.sendCustomAgentMessage(selected.id, conversation.id, message, controller.signal, externalFiles)
        : await api.sendCustomAgentMessage(selected.id, conversation.id, message, controller.signal)
      setConversation(updated)
      setConversations(current => [summaryOf(updated), ...current.filter(item => item.id !== updated.id)])
      setMessage('')
    } catch (error) {
      if (!controller.signal.aborted) setError(errorMessage(error))
    } finally {
      sendingRef.current = false
      if (!controller.signal.aborted) setSending(false)
      if (sendController.current === controller) sendController.current = null
    }
  }

  return <section className="panel custom-agents">
    <header className="panel-heading custom-agents-heading">
      <div><h2><Bot size={16} /> Agents personnalisés</h2>
        <p>Chat isolé par défaut. Un workspace explicitement choisi peut fournir un instantané de fichiers texte; aucune commande n’est exécutée.</p></div>
      <div className="custom-agent-actions">
        <button className={`button ${activeView === 'agents' ? 'primary' : 'secondary'}`}
          onClick={() => setActiveView('agents')}>Agents</button>
        <button className={`button ${activeView === 'teams' ? 'primary' : 'secondary'}`}
          onClick={() => setActiveView('teams')}>Équipes</button>
        {activeView === 'agents' && <button className="button primary" onClick={startCreate} disabled={saving || sending}>
          <Plus size={14} /> Créer un agent</button>}
      </div>
    </header>
    {error && <div role="alert" className="notice danger">{error}</div>}
    {modelError && <div role="alert" className="notice warning">Liste Ollama indisponible : {modelError}
      <button className="text-button" disabled={modelsLoading} onClick={() => void retryModels()}>
        {modelsLoading ? 'Vérification…' : 'Réessayer'}</button>
    </div>}
    {activeView === 'teams'
      ? <CustomAgentTeams agents={agents} projects={projects} desktopApp={desktopApp} models={models}
        onProjectCreated={project => setProjects(current =>
          [project, ...current.filter(item => item.id !== project.id)])}
        onOpenProject={onOpenProject} />
      : loading ? <div className="loading"><LoaderCircle size={16} /> Chargement des agents personnalisés…</div>
      : <div className="custom-agent-layout">
        <aside className="custom-agent-sidebar" aria-label="Agents personnalisés enregistrés">
          {agents.length === 0 && <div className="empty compact"><Bot size={22} />
            <p>Aucun agent personnalisé. Créez-en un pour commencer.</p></div>}
          {agents.map(agent => <button key={agent.id} className={`custom-agent-choice ${selectedId === agent.id ? 'selected' : ''}`}
            onClick={() => void selectAgent(agent)} disabled={saving || sending}>
            <strong>{agent.name}</strong><span>{agent.modelName}</span>
          </button>)}
        </aside>
        <div className="custom-agent-main">
          {editingId !== null ? <form className="custom-agent-form" onSubmit={event => void saveAgent(event)}>
            <h3>{editingId ? 'Modifier l’agent' : 'Créer un agent personnalisé'}</h3>
            <label>Nom<input autoFocus required maxLength={80} value={draft.name}
              onChange={event => setDraft(current => ({ ...current, name: event.target.value }))} /></label>
            <label>Description facultative<textarea rows={2} maxLength={1000} value={draft.description}
              onChange={event => setDraft(current => ({ ...current, description: event.target.value }))} /></label>
            <label>Modèle Ollama installé<select required value={draft.modelName}
              onChange={event => setDraft(current => ({ ...current, modelName: event.target.value }))}>
              <option value="" disabled>Choisir un modèle installé</option>
              {!models.includes(draft.modelName) && draft.modelName &&
                <option value={draft.modelName} disabled>{draft.modelName} — indisponible</option>}
              {models.map(model => <option key={model} value={model}>{model}</option>)}
            </select></label>
            <label>Mode de sélection<select value={draft.automaticModelSelection ? 'automatic' : 'manual'}
              onChange={event => setDraft(current => ({ ...current, automaticModelSelection: event.target.value === 'automatic' }))}>
              <option value="manual">Manuel — modèle principal et secours ordonnés</option>
              <option value="automatic">Automatique — modèles installés admissibles</option>
            </select></label>
            <p className="muted small">En équipe, le mode automatique nécessite aussi l’activation explicite dans l’équipe.
              Un agent manuel garde son modèle principal et ses secours.</p>
            <label><input type="checkbox" checked={draft.generation != null} onChange={event =>
              setDraft(current => ({ ...current, generation: event.target.checked ?
                { contextSize: 4096, maxOutputTokens: 1024, temperature: 0.2 } : null }))} />
              Personnaliser les paramètres de génération</label>
            {draft.generation && <div className="settings-grid">
              <label>Contexte (tokens)<input type="number" min={512} max={maximumModelContext} value={draft.generation.contextSize}
                onChange={event => setDraft(current => ({ ...current, generation: { ...current.generation!,
                  contextSize: Number(event.target.value) } }))} /></label>
              <label>Sortie maximale (tokens)<input type="number" min={64} max={8192} value={draft.generation.maxOutputTokens}
                onChange={event => setDraft(current => ({ ...current, generation: { ...current.generation!,
                  maxOutputTokens: Number(event.target.value) } }))} /></label>
              <label>Température<input type="number" min={0} max={2} step={0.1} value={draft.generation.temperature}
                onChange={event => setDraft(current => ({ ...current, generation: { ...current.generation!,
                  temperature: Number(event.target.value) } }))} /></label>
            </div>}
            <OllamaFallbackModels label="Modèles de secours de l’agent" value={draft.fallbackModels}
              models={models} primary={draft.modelName}
              onChange={fallbackModels => setDraft(current => ({ ...current, fallbackModels }))} />
            <p className="muted small">Les secours sont utilisés par les équipes ayant explicitement activé l’escalade.</p>
            <label>Instructions système<textarea required rows={6} maxLength={8000} value={draft.systemPrompt}
              onChange={event => setDraft(current => ({ ...current, systemPrompt: event.target.value }))} /></label>
            {models.length === 0 && <p className="muted">Aucun modèle installé n’est disponible. Vérifiez Ollama avant d’enregistrer.</p>}
            <div className="custom-agent-actions">
              <button className="button primary" disabled={saving || models.length === 0}>
                {saving ? 'Enregistrement…' : 'Enregistrer'}</button>
              <button type="button" className="button secondary" disabled={saving}
                onClick={() => setEditingId(null)}>Annuler</button>
            </div>
          </form> : selected ? <>
            <header className="custom-agent-detail-heading">
              <div><h3>{selected.name}</h3><p>{selected.description || 'Aucune description.'}</p>
                <span className="badge quiet">{selected.modelName}{unavailableModel ? ' · indisponible' : ''}</span>
                <span className="badge quiet">{selected.automaticModelSelection ? 'Automatique' : 'Manuel'}</span></div>
              <div className="custom-agent-actions">
                <button className="button secondary" onClick={startEdit} disabled={sending}><Pencil size={13} /> Modifier</button>
                <button className="button secondary" onClick={() => void duplicateAgent(selected)} disabled={sending}>
                  <Copy size={13} /> Dupliquer</button>
                <button className="button secondary" onClick={() => void deleteAgent(selected)} disabled={sending}>
                  <Trash2 size={13} /> Supprimer</button>
              </div>
            </header>
            {unavailableModel && <div role="alert" className="notice warning">
              Ce modèle n’est plus installé dans Ollama. Modifiez l’agent pour choisir un modèle disponible.
            </div>}
            {conversation && <section className="custom-workspace">
              <div className="custom-workspace-heading">
                <div><strong>Workspace de la conversation</strong>
                  <span>{activeWorkspace
                    ? `${activeWorkspace.name} · ${activeWorkspace.kind === 'External'
                      ? desktopApp && nativeGrantId ? 'dossier Windows autorisé pour cette session' : 'lecture seule'
                      : 'projet Studio'}`
                    : 'Aucun · chat simple'}</span></div>
                <span className="badge quiet">{activeWorkspace?.kind === 'External'
                  ? externalWriteAuthorized ? 'Écriture avec approbation' : 'Lecture seule'
                  : activeWorkspace?.kind === 'Studio' ? 'Modifications avec approbation' : 'Sans fichiers'}</span>
              </div>
              <div className="custom-workspace-controls">
                <label>Projet Studio
                  <select value={activeWorkspace?.kind === 'Studio' ? activeWorkspace.workspaceId ?? '' : ''}
                    disabled={workspaceBusy || sending}
                    onChange={event => {
                      const project = projects.find(item => item.id === event.target.value)
                      void setConversationWorkspace(project ? {
                        kind: 'Studio', workspaceId: project.id,
                        name: project.projectName || project.task.slice(0, 120),
                      } : null)
                    }}>
                    <option value="">Aucun workspace</option>
                    {projects.map(project => <option key={project.id} value={project.id}>
                      {project.projectName || project.task.slice(0, 100)}{project.archived ? ' · archivé' : ''}
                    </option>)}
                  </select>
                </label>
                <button className="button secondary" type="button" disabled={workspaceBusy || sending}
                  onClick={() => desktopApp ? void chooseNativeFolder() : folderInput.current?.click()}>
                  <FolderOpen size={14} /> {desktopApp ? 'Choisir un dossier Windows' : 'Choisir un dossier externe'}
                </button>
                <input ref={element => {
                  folderInput.current = element
                  element?.setAttribute('webkitdirectory', '')
                }} type="file" multiple hidden aria-label="Choisir un dossier externe"
                  onChange={event => void chooseExternalFolder(event)} />
              </div>
              {projectError && <p role="alert" className="notice warning">
                Liste des projets indisponible : {projectError}</p>}
              {activeWorkspace?.kind === 'External' && <>
                <p className="muted small">{desktopApp
                  ? 'Le dossier a été sélectionné par le sélecteur Windows natif. Seuls les fichiers texte vérifiés (80 fichiers maximum, 24 Ko) sont transmis à Ollama; aucun chemin absolu ne quitte Tauri.'
                  : 'Le navigateur transmet uniquement un instantané de fichiers texte (80 fichiers maximum, 24 Ko). L’écriture externe n’est disponible que dans AIPlatform Studio Desktop.'}</p>
                {desktopApp && !nativeGrantId && <button className="button secondary" type="button"
                  onClick={() => void chooseNativeFolder()} disabled={workspaceBusy || sending}>
                  <FolderOpen size={14} /> Resélectionner le dossier Windows</button>}
                {desktopApp && nativeGrantId && !externalWriteAuthorized && <button className="button secondary" type="button"
                  onClick={() => void enableExternalWrites()} disabled={workspaceBusy || sending}>
                  Autoriser les modifications pour cette session</button>}
                {desktopApp && externalWriteAuthorized && <p className="notice warning">
                  Modifications autorisées pour cette session. Chaque proposition exige encore une confirmation et crée une sauvegarde.</p>}
                <p className="muted small">Vérification locale désactivée : l’exécuteur contrôlé disponible est réservé à Docker. Les commandes de build/test peuvent lancer du code du projet et Studio ne dispose pas ici d’un exécuteur local avec arrêt fiable de tous les processus enfants.</p>
                <label className="workspace-consent">
                  <input type="checkbox" checked={externalAuthorized} disabled={!externalFiles.length || sending}
                    onChange={event => setExternalAuthorized(event.target.checked)} />
                  J’autorise l’envoi de cet instantané à Ollama local pour cette conversation.
                </label>
                {!desktopApp && !externalFiles.length && <button className="button secondary" type="button"
                  onClick={() => folderInput.current?.click()} disabled={workspaceBusy || sending}>
                  <FolderOpen size={14} /> Resélectionner le dossier
                </button>}
              </>}
              {activeWorkspace?.kind === 'Studio' && <>
                <p className="muted small">Contexte limité à 80 fichiers texte et 24 Ko par demande; seuls les fichiers admissibles sont envoyés à Ollama.</p>
                <div className="custom-workspace-browser">
                  <div className="custom-workspace-files" aria-label="Fichiers du projet">
                    <strong><FileText size={13} /> Fichiers source ({workspaceFileList.length})</strong>
                    {workspaceFileList.length === 0 && <span className="muted small">
                      {workspaceBusy ? 'Chargement des fichiers…' : 'Aucun fichier source disponible.'}</span>}
                    {workspaceFileList.map(file => <button key={file.path} type="button"
                      className={selectedFile?.path === file.path ? 'selected' : ''}
                      onClick={() => void loadWorkspaceFile(file.path)} disabled={workspaceBusy}>
                      {file.path}</button>)}
                  </div>
                  <div className="custom-workspace-search">
                    <label htmlFor="custom-agent-workspace-search">Rechercher dans le code</label>
                    <div className="workspace-search-control">
                      <input id="custom-agent-workspace-search" value={workspaceSearch}
                        onChange={event => setWorkspaceSearch(event.target.value)}
                        onKeyDown={event => {
                          if (event.key === 'Enter') { event.preventDefault(); void searchWorkspaceCode() }
                        }} placeholder="Texte à rechercher" />
                      <button className="button secondary" type="button" disabled={workspaceBusy || !workspaceSearch.trim()}
                        onClick={() => void searchWorkspaceCode()}><Search size={13} /> Rechercher</button>
                    </div>
                    <div className="workspace-search-results">
                      {workspaceResults.map(result => <button key={result.path} type="button"
                        onClick={() => void loadWorkspaceFile(result.path)}>
                        <strong>{result.path}</strong><code>{result.snippet}</code></button>)}
                    </div>
                    {selectedFile && <details className="workspace-file-preview" open>
                      <summary>{selectedFile.path}</summary><pre>{selectedFile.content}</pre>
                    </details>}
                  </div>
                </div>
                <p className="muted small">Vérification locale désactivée : le seul exécuteur existant passe par Docker et n’est pas utilisé ici. Studio ne dispose pas encore d’un exécuteur local isolant les processus enfants; aucun build ni script du projet ne sera lancé.</p>
              </>}
              {activeWorkspace?.kind === 'External' && workspaceFileList.length > 0 && <>
                <div className="custom-workspace-browser">
                  <div className="custom-workspace-files" aria-label="Fichiers externes sélectionnés">
                    <strong><FileText size={13} /> Instantané ({externalFiles.length})</strong>
                    {externalFiles.map(file => <button key={file.path} type="button"
                      className={selectedFile?.path === file.path ? 'selected' : ''}
                      onClick={() => void loadWorkspaceFile(file.path)}>{file.path}</button>)}
                  </div>
                  <div className="custom-workspace-search">
                    <label htmlFor="custom-agent-external-search">Rechercher dans le code</label>
                    <div className="workspace-search-control">
                      <input id="custom-agent-external-search" value={workspaceSearch}
                        onChange={event => setWorkspaceSearch(event.target.value)}
                        onKeyDown={event => {
                          if (event.key === 'Enter') { event.preventDefault(); void searchWorkspaceCode() }
                        }} placeholder="Texte à rechercher" />
                      <button className="button secondary" type="button" disabled={workspaceBusy || !workspaceSearch.trim()}
                        onClick={() => void searchWorkspaceCode()}><Search size={13} /> Rechercher</button>
                    </div>
                    <div className="workspace-search-results">
                      {workspaceResults.map(result => <button key={result.path} type="button"
                        onClick={() => void loadWorkspaceFile(result.path)}>
                        <strong>{result.path}</strong><code>{result.snippet}</code></button>)}
                    </div>
                    {selectedFile && <details className="workspace-file-preview" open>
                      <summary>{selectedFile.path}</summary><pre>{selectedFile.content}</pre>
                    </details>}
                  </div>
                </div>
              </>}
              {workspaceError && <p role="alert" className="notice warning">{workspaceError}</p>}
            </section>}
            <div className="custom-agent-chat-layout">
              <aside className="custom-conversation-list" aria-label="Historique des conversations">
                <button className="button secondary full" onClick={() => void startConversation()} disabled={sending}>
                  <MessageSquarePlus size={14} /> Nouvelle conversation
                </button>
                {conversations.length === 0 ? <p className="muted small">Aucune conversation enregistrée.</p>
                  : conversations.map(item => <div key={item.id} className="custom-conversation-item">
                    <button className={`custom-conversation-choice ${conversation?.id === item.id ? 'selected' : ''}`}
                      onClick={() => void openConversation(item)} disabled={sending}>
                      <strong>{item.title}</strong><span>{formatDate(item.updatedAt)}</span>
                    </button>
                    <button className="icon-button" aria-label={`Supprimer ${item.title}`}
                      onClick={() => void deleteConversation(item)} disabled={sending}><Trash2 size={13} /></button>
                  </div>)}
              </aside>
              <section className="custom-chat" aria-label="Conversation avec l’agent">
                {!conversation ? <div className="empty compact"><MessageSquarePlus size={24} />
                  <p>Choisissez une conversation ou démarrez-en une nouvelle.</p></div> : <>
                  <div className="custom-chat-messages" role="log" aria-label="Messages de la conversation">
                    {conversation.messages.map((item, index) => <article key={`${item.createdAt}-${index}`}
                      className={`custom-chat-message ${item.role}`}>
                      <strong>{item.role === 'user' ? 'Vous' : selected.name}</strong>
                      {item.routingDecision && <RoutingExplanation decision={item.routingDecision} />}
                      {item.role === 'assistant' ? <Markdown>{item.content}</Markdown>
                        : <p>{item.content}</p>}
                      {item.proposal && <section className="custom-agent-proposal">
                        <header><strong>Proposition · {item.proposal.status}</strong>
                          <span>{item.proposal.changes.length} opération(s)</span></header>
                        {item.proposal.changes.map(change => <details key={change.path} className="proposal-change">
                          <summary>{change.operation === 'create' ? 'Création' : change.operation === 'delete'
                            ? 'Suppression' : 'Modification'} · {change.path} — {change.summary}</summary>
                          {change.operation !== 'create' && <div><strong>Avant</strong>
                            <pre>{change.before ?? '(absent)'}</pre></div>}
                          {change.operation !== 'delete' && <div><strong>Après</strong>
                            <pre>{change.after ?? '(absent)'}</pre></div>}
                        </details>)}
                        {item.proposal.status === 'Pending' && <div className="custom-agent-actions">
                          <button className="button primary" disabled={
                            !(item.proposal.canApply || canApplyExternalProposal(item.proposal)) ||
                            decisionBusy || sending}
                            onClick={() => void decideProposal(item.proposal!.id, item.proposal!.changes, true,
                              item.proposal!.workspace?.authorizationId)}>
                            {decisionBusy ? 'Application…' : item.proposal.workspace?.kind === 'External'
                              ? 'Appliquer au dossier autorisé' : 'Appliquer au projet Studio'}
                          </button>
                          {!item.proposal.canApply && !canApplyExternalProposal(item.proposal) &&
                            <span className="muted small">{proposalApplyReason(item.proposal)}</span>}
                          <button className="button secondary" disabled={decisionBusy || sending}
                            onClick={() => void decideProposal(item.proposal!.id, item.proposal!.changes, false)}>
                            Refuser</button>
                        </div>}
                        {item.proposal.status === 'Applied' && item.proposal.workspace?.kind === 'External' &&
                          item.proposal.externalBackupId && <div className="custom-agent-actions">
                            {item.proposal.externalRestoredAt
                              ? <span className="muted small">Restauré le {formatDate(item.proposal.externalRestoredAt)}</span>
                              : nativeGrantId && externalWriteAuthorized && activeWorkspace?.kind === 'External'
                                ? <button className="button secondary" disabled={decisionBusy || sending}
                                  onClick={() => void restoreExternalProposal(item.proposal!.id,
                                    item.proposal!.externalBackupId!, item.proposal!.changes)}>
                                  Restaurer les versions précédentes</button>
                                : <span className="muted small">Resélectionnez le dossier autorisé pour restaurer cette sauvegarde.</span>}
                          </div>}
                      </section>}
                    </article>)}
                    {sending && <p className="loading" aria-live="polite"><LoaderCircle size={15} /> Génération de la réponse…</p>}
                  </div>
                  <form className="custom-chat-composer" onSubmit={event => void sendMessage(event)}>
                    <label className="sr-only" htmlFor="custom-agent-message">Votre message</label>
                    <textarea id="custom-agent-message" rows={3} maxLength={4000} value={message}
                      placeholder="Écrivez votre message…" disabled={sending || unavailableModel ||
                        (activeWorkspace?.kind === 'External' && (!externalFiles.length || !externalAuthorized))}
                      onChange={event => setMessage(event.target.value)} />
                    <div className="custom-agent-actions">
                      <span className="muted small">Maximum 4000 caractères</span>
                      <button className="button primary" disabled={sending || unavailableModel || !message.trim()}>
                        {sending ? <LoaderCircle size={14} /> : <Send size={14} />}
                        {sending ? 'Génération…' : 'Envoyer'}
                      </button>
                    </div>
                  </form>
                </>}
              </section>
            </div>
          </> : <div className="empty compact custom-agent-empty"><Bot size={26} />
            <p>Sélectionnez un agent personnalisé ou créez-en un nouveau.</p></div>}
        </div>
      </div>}
  </section>
}
