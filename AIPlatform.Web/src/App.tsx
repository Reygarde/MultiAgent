import { useCallback, useEffect, useState } from 'react'
import {
  Activity, ArrowRight, Bot, ChevronRight, CircleDot, Cpu, FolderOpen, Layers3,
  LayoutDashboard, LoaderCircle, MessageSquare, Plus, Send, ShieldCheck, Sparkles, Workflow,
} from 'lucide-react'
import { api, errorMessage } from './api'
import { Markdown } from './Markdown'
import { MissionDetails, StatusBadge } from './MissionDetails'
import { Projects } from './Projects'
import { ConfigurationEditor } from './ConfigurationEditor'
import type { Agent, LocalModels, Mission, MissionKind, Model, Sandbox } from './types'
import { isActive } from './types'

const navigation = [
  { id: 'Workspace', icon: MessageSquare, subtitle: 'Votre espace de création' },
  { id: 'Agents', icon: Bot, subtitle: 'Votre équipe spécialisée' },
  { id: 'Models', icon: Cpu, subtitle: 'Intelligence locale' },
  { id: 'Projects', icon: FolderOpen, subtitle: 'Du prompt aux fichiers' },
  { id: 'Missions', icon: Workflow, subtitle: 'Chaque étape, en transparence' },
] as const
type Page = typeof navigation[number]['id']

export default function App({ restartBackend }: { restartBackend?: () => Promise<void> } = {}) {
  const [page, setPage] = useState<Page>('Workspace')
  const [agents, setAgents] = useState<Agent[]>([])
  const [models, setModels] = useState<Model[]>([])
  const [missions, setMissions] = useState<Mission[]>([])
  const [local, setLocal] = useState<LocalModels | null>(null)
  const [sandbox, setSandbox] = useState<Sandbox | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [pollError, setPollError] = useState<string | null>(null)
  const [telemetryError, setTelemetryError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [task, setTask] = useState('')
  const [kind, setKind] = useState<MissionKind>('Chat')
  const [agentId, setAgentId] = useState('')
  const [busy, setBusy] = useState(false)
  const [confirmRestart, setConfirmRestart] = useState(false)
  const [restartError, setRestartError] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const selected = missions.find(mission => mission.id === selectedId) ?? missions[0]
  const activeCount = missions.filter(isActive).length

  const refresh = useCallback(async () => {
    const result = await api.missions()
    setMissions(result)
    setPollError(null)
    return result
  }, [])
  async function refreshCatalog() {
    const [nextAgents, nextModels] = await Promise.all([api.agents(), api.models()])
    setAgents(nextAgents); setModels(nextModels)
  }

  useEffect(() => {
    const controller = new AbortController()
    void Promise.all([api.agents(controller.signal), api.models(controller.signal)])
      .then(([agents, models]) => { setAgents(agents); setModels(models) })
      .catch(error => { if (!controller.signal.aborted) setError(errorMessage(error)) })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout>
    async function poll() {
      try {
        const result = await api.missions(controller.signal)
        if (!controller.signal.aborted) { setMissions(result); setPollError(null) }
      } catch (error) {
        if (!controller.signal.aborted) setPollError(`Suivi des missions interrompu : ${errorMessage(error)}`)
      } finally {
        if (!controller.signal.aborted) timer = setTimeout(() => void poll(), 2000)
      }
    }
    void poll()
    return () => { controller.abort(); clearTimeout(timer) }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout>
    async function poll() {
      try {
        const [localModels, isolation] = await Promise.all([api.localModels(controller.signal), api.sandbox(controller.signal)])
        if (!controller.signal.aborted) { setLocal(localModels); setSandbox(isolation); setTelemetryError(null) }
      } catch (error) {
        if (!controller.signal.aborted) {
          setLocal(null); setSandbox(null); setTelemetryError(`État local indisponible : ${errorMessage(error)}`)
        }
      } finally {
        if (!controller.signal.aborted) timer = setTimeout(() => void poll(), 5000)
      }
    }
    void poll()
    return () => { controller.abort(); clearTimeout(timer) }
  }, [])

  async function submit() {
    if (!task.trim() || busy) return
    setBusy(true); setError(null)
    try {
      const mission = await api.create({ task, kind, agentId: agentId || null, approveSandboxExecution: false })
      setMissions(previous => [mission, ...previous.filter(item => item.id !== mission.id)])
      setSelectedId(mission.id); setTask('')
    } catch (error) { setError(errorMessage(error)) }
    finally { setBusy(false) }
  }

  async function cancel(id: string) {
    setBusy(true); setError(null)
    try { await api.cancel(id); await refresh() }
    catch (error) { setError(errorMessage(error)) }
    finally { setBusy(false) }
  }

  const pageInfo = navigation.find(item => item.id === page)!
  const eligible = agents.filter(agent => agent.enabled !== false && !agent.isRouter &&
    (kind === 'Chat' ? !agent.taskTypes || agent.taskTypes.includes('Chat') : agent.canGenerateProjects))

  return <div className="studio">
    <aside className="sidebar">
      <a className="brand" href="#" onClick={event => { event.preventDefault(); setPage('Workspace') }}>
        <span className="brand-mark"><Layers3 size={23} /></span><span>AIPlatform <strong>Studio</strong><small>LOCAL AGENT WORKSPACE</small></span>
      </a>
      <button className="new-mission" onClick={() => { setPage('Workspace'); document.getElementById('mission-task')?.focus() }}>
        <Plus size={17} /> Nouvelle mission <span>↗</span>
      </button>
      <div className="nav-label">PLATEFORME</div>
      <nav aria-label="Navigation principale">{navigation.map(item => <button key={item.id} aria-label={item.id} title={item.id} aria-current={page === item.id ? 'page' : undefined}
        className={`nav-item ${page === item.id ? 'active' : ''}`} onClick={() => setPage(item.id)}>
        <item.icon size={18} /><span>{item.id}</span>{item.id === 'Missions' && activeCount > 0 && <small>{activeCount}</small>}
      </button>)}</nav>
      <div className="sidebar-bottom">
        <div className="local-card"><span className={`status-dot ${local?.available ? 'green' : 'amber'}`} /><strong>{local?.available ? 'Ollama connecté' : 'État Ollama non disponible'}</strong>
          <p>Exécution locale · aucun cloud</p><div className="local-card-line"><Cpu size={13} /> Une mission IA active maximum</div></div>
        <div className="profile"><div className="avatar">AI</div><span>Environnement local<small>AIPlatform Studio · MVP 5</small></span><ShieldCheck size={16} /></div>
      </div>
    </aside>
    <main>
      <header className="topbar"><div><LayoutDashboard size={15} /><span>Studio</span><ChevronRight size={13} /><strong>{page}</strong></div>
        <span className="local-pill"><CircleDot size={13} /> LOCAL FIRST</span>
      </header>
      <div className="page-content">
        <div className="page-title"><div><p className="eyebrow">{pageInfo.subtitle}</p><h1>{page === 'Workspace' ? 'Construisons la suite.' : page}</h1></div>
          <span className="badge quiet"><Activity size={13} /> {activeCount} mission{activeCount > 1 ? 's' : ''} en attente / active{activeCount > 1 ? 's' : ''}</span></div>
        {error && <div className="notice danger" role="alert">{error}<button onClick={() => setError(null)} aria-label="Fermer l’erreur">×</button></div>}
        {pollError && <div className="notice danger" role="alert">{pollError} Les états affichés peuvent être périmés.</div>}
        {telemetryError && <div className="notice warning" role="alert">{telemetryError}</div>}
        <section className="service-status panel" aria-label="État des services">
          <span><span className={`status-dot ${pollError ? 'amber' : 'green'}`} />
            API : {pollError ? 'indisponible' : loading ? 'vérification' : 'prête'}{restartBackend ? ' · session protégée' : ' · développement'}</span>
          <span><span className={`status-dot ${local?.available ? 'green' : 'amber'}`} />
            Ollama : {local ? local.available ? 'disponible' : 'indisponible' : 'vérification'}</span>
          <span><span className={`status-dot ${sandbox?.engineAvailable || sandbox?.available ? 'green' : 'amber'}`} />
            Docker : {sandbox ? sandbox.engineAvailable || sandbox.available ? 'moteur prêt' : 'indisponible' : 'vérification'}</span>
          {restartBackend && <button className="button secondary" onClick={() => setConfirmRestart(true)}>Relancer le backend</button>}
          {local && !local.available && <p>Démarrez Ollama ou installez-le depuis ollama.com/download/windows, puis laissez Studio revérifier sa disponibilité.</p>}
          {sandbox && !(sandbox.engineAvailable || sandbox.available) && <p>Démarrez Docker Desktop en mode Linux. Installation : docker.com/products/docker-desktop. La génération reste possible sans compilation.</p>}
          {sandbox?.engineAvailable && !sandbox.available && <p>Docker fonctionne mais la vérification est indisponible : {sandbox.reason}</p>}
          {confirmRestart && restartBackend && <div className="restart-confirm">
            <p>Relancer uniquement l’API ? Les missions actives seront annulées et les brouillons non envoyés réinitialisés. Les fichiers sont conservés. Ollama et Docker restent en service.</p>
            <button className="button primary" onClick={() => {
              setConfirmRestart(false); setRestartError(null)
              void restartBackend().catch(error => setRestartError(errorMessage(error)))
            }}>Confirmer le redémarrage</button>
            <button className="button secondary" onClick={() => setConfirmRestart(false)}>Conserver la session</button>
          </div>}
          {restartError && <div role="alert" className="notice danger">{restartError}</div>}
        </section>
        {loading && <div className="loading"><LoaderCircle className="spin" size={18} /> Connexion à l’API locale…</div>}

        {page === 'Workspace' && <div className="workspace-grid">
          <div className="conversation-column">
            {!missions.length ? <section className="welcome panel">
              <span className="hero-icon"><Sparkles size={28} /></span><span className="badge violet">VOTRE ÉQUIPE IA, EN LOCAL</span>
              <h2>Une idée. Les bons agents.<br /><span>Un espace pour créer.</span></h2>
              <p>Décrivez votre mission. Studio choisit l’agent adapté et vous montre chaque étape, sans boîte noire.</p>
              <div className="suggestions">
                <button onClick={() => { setKind('Project'); setAgentId(''); setTask('Crée une API REST .NET 10 avec CRUD utilisateurs et tests unitaires.') }}>
                  <FolderOpen size={18} /><strong>Créer un projet .NET</strong><span>API REST, fichiers et tests <ArrowRight size={14} /></span>
                </button>
                <button onClick={() => { setKind('Chat'); setTask('Explique simplement comment fonctionne un système multi-agents IA.') }}>
                  <Bot size={18} /><strong>Explorer une idée</strong><span>Comprendre, analyser, expliquer <ArrowRight size={14} /></span>
                </button>
              </div>
            </section> : <section className="conversation panel" aria-label="Historique des missions">
              <div className="panel-heading"><h2><MessageSquare size={16} /> Historique</h2><span className="muted">Missions indépendantes</span></div>
              {[...missions].reverse().map(mission => <article key={mission.id} data-mission-id={mission.id} className={`conversation-item ${selected?.id === mission.id ? 'focused' : ''}`}>
                <button className="user-message" onClick={() => setSelectedId(mission.id)}><span className="avatar user">U</span><span>{mission.task}</span></button>
                <div className="agent-message"><span className="avatar agent"><Bot size={17} /></span><div className="answer">
                  <div className="answer-header"><strong>{mission.selectedAgent ?? 'Studio'}</strong><StatusBadge mission={mission} /></div>
                  {mission.selectedModel && <p className="model-caption">{mission.selectedModel}</p>}
                  {mission.workflow?.plan && <details className="technical-logs"><summary>Plan · {mission.workflow.plan.name}</summary>
                    <p>{mission.workflow.plan.description}</p><ul>{mission.workflow.plan.decisions.map((decision, index) => <li key={index}>{decision}</li>)}</ul>
                    <p>Agents : {[...new Set(mission.workflow.steps.map(step => step.agentId))].join(' → ')}</p>
                    {mission.workflow.review && <p>Revue : {mission.workflow.review.summary}</p>}
                  </details>}
                  {mission.response ? <Markdown>{mission.response}</Markdown> : mission.error ? <p className="error-inline">{mission.error}</p>
                    : isActive(mission) ? <p className="muted loading"><LoaderCircle size={15} className="spin" /> {mission.events.at(-1)?.message ?? 'En attente…'}</p>
                    : <p className="muted">Aucune réponse générée.</p>}
                  {mission.status === 'GeneratedUnverified' && <div className="notice warning">
                    {mission.verification.some(result => (result.attempt ?? 0) > 0 || result.exitCode !== null)
                      ? 'Fichiers disponibles. Vérification non aboutie : consultez les résultats des étapes exécutées.'
                      : 'Fichiers disponibles. Compilation et tests NON exécutés.'}
                  </div>}
                  {mission.hasWorkspace && <button className="text-button" onClick={() => { setSelectedId(mission.id); setPage('Projects') }}>Explorer les fichiers <ArrowRight size={14} /></button>}
                </div></div>
              </article>)}
            </section>}
            <form className="composer panel" onSubmit={event => { event.preventDefault(); void submit() }}>
              <div className="composer-options"><label>Mode <select aria-label="Mode de mission" value={kind}
                onChange={event => { setKind(event.target.value as MissionKind); setAgentId('') }}>
                <option value="Chat">Conversation</option><option value="Project">Projet .NET</option>
              </select></label>
                <label>Agent <select aria-label="Sélection de l’agent" value={agentId} onChange={event => setAgentId(event.target.value)}>
                  <option value="">Automatique</option>{eligible.map(agent => <option key={agent.id} value={agent.id}>{agent.id}</option>)}
                </select></label>
              </div>
              <textarea id="mission-task" aria-label="Votre mission" placeholder="Décrivez ce que vous voulez créer, comprendre ou améliorer…"
                value={task} onChange={event => setTask(event.target.value)} rows={3} />
              {kind === 'Project' && <div className="project-consent">
                <p><ShieldCheck size={14} /> Écriture autorisée uniquement dans un workspace dédié.</p>
                <p>Après génération, consultez les fichiers et autorisez ou refusez le plan de compilation et de tests.</p>
                {!sandbox?.available && <span className="warning-text">Isolation indisponible : fichiers uniquement, aucune exécution sur l’hôte.</span>}
              </div>}
              <div className="composer-footer"><span><ShieldCheck size={13} /> Local · aucun code exécuté sur l’hôte</span>
                <button className="button primary" disabled={!task.trim() || busy || !agents.length || !!pollError} type="submit">
                  {busy ? <LoaderCircle size={15} className="spin" /> : <Send size={15} />} Envoyer la mission
                </button></div>
            </form>
          </div>
          <aside className="context-column">{selected ? <MissionDetails mission={selected} cancel={id => void cancel(id)} busy={busy} refresh={refresh} />
            : <section className="panel mission-details"><div className="panel-heading"><h2><Workflow size={16} /> Suivi de mission</h2></div>
              <div className="empty compact"><Workflow size={28} /><p>Les étapes apparaîtront ici dès votre première mission.</p><span>Aucune progression simulée.</span></div></section>}
            <section className="panel safety-card"><ShieldCheck size={20} /><h3>Vous gardez le contrôle.</h3>
              <p>Les fichiers générés restent non fiables. Sans conteneur disponible et sans votre accord, aucune compilation ni aucun test n’est lancé.</p>
              <span className={`badge ${sandbox?.available ? 'success' : 'warning'}`}>{sandbox?.available ? 'Isolation prête' : 'Isolation indisponible'}</span>
              <p className="sandbox-detail">{sandbox?.reason ?? 'Vérification de la disponibilité…'}</p></section>
          </aside>
        </div>}

        {page === 'Agents' && <><ConfigurationEditor mode="agents" saved={refreshCatalog} /><div className="cards-grid">{agents.map(agent => <article className="panel agent-card" key={agent.id}>
          <div className="card-top"><span className="card-icon"><Bot size={24} /></span><span className="badge quiet">
            {agent.isRouter ? 'Routeur' : agent.canGenerateProjects ? 'Développeur · outils' : 'Exécutant'}</span></div>
          <h2>{agent.name || agent.id}</h2><p>{agent.role ?? 'Chat'} · {agent.enabled === false ? 'Inactif' : 'Actif'}</p>
          <p>{agent.description}</p><div className="tags">{agent.capabilities.map(capability => <span key={capability}>{capability}</span>)}</div>
          <p className="muted">Outils : {agent.allowedTools?.join(', ') || 'aucun'}</p>
          <div className="card-bottom"><Cpu size={14} /><span>{models.find(model => model.id === agent.modelId)?.name ?? agent.modelId}</span></div>
        </article>)}</div></>}

        {page === 'Models' && <><ConfigurationEditor mode="models" saved={refreshCatalog} />{!local?.available && <div className="notice warning">État local inconnu : {local?.error ?? 'télémétrie non disponible'}. Les modèles ci-dessous sont configurés, pas nécessairement installés.</div>}
          <div className="cards-grid">{models.map(model => {
            const state = local?.available ? local.models.find(item => item.name === model.name) : undefined
            return <article className="panel model-card" key={model.id}><div className="card-top"><span className="card-icon"><Cpu size={24} /></span><span className="badge quiet">{model.provider}</span></div>
              <h2>{model.name}</h2><p>{model.assignedAgents.join(' · ')}</p>
              <div className="model-states"><span className={`badge ${state?.installed ? 'success' : 'quiet'}`}>Installé : {state ? state.installed ? 'oui' : 'non' : 'inconnu'}</span>
                <span className={`badge ${state?.loaded ? 'violet' : 'quiet'}`}>Chargé : {state ? state.loaded ? 'oui' : 'non' : 'inconnu'}</span>
                <span className={`badge ${state?.inUse ? 'success' : 'quiet'}`}>Utilisé : {state ? state.inUse ? 'oui' : 'non' : 'inconnu'}</span></div>
              <dl className="model-specs"><div><dt>Contexte conversation</dt><dd>{model.contextSize.toLocaleString('fr-FR')}</dd></div>
                <div><dt>Sortie max. conversation</dt><dd>{model.maxOutputTokens}</dd></div><div><dt>Température</dt><dd>{model.temperature}</dd></div></dl>
              <p className="muted small">Ces budgets sont utilisés par les étapes collaboratives. Test et orchestration sont déterministes, sans appel LLM.</p>
            </article>
          })}</div></>}

        {page === 'Projects' && <Projects missions={missions} selectedId={selectedId} onSelect={setSelectedId} refresh={refresh} />}
        {page === 'Missions' && <div className="missions-layout">
          <section className="panel mission-list"><div className="panel-heading"><h2><Workflow size={17} /> Toutes les missions</h2><span className="count">{missions.length}</span></div>
            {!missions.length && <div className="empty compact"><Workflow size={28} /><p>Aucune mission pour le moment.</p></div>}
            {missions.map(mission => <button key={mission.id} className={`mission-row ${selected?.id === mission.id ? 'selected' : ''}`} onClick={() => setSelectedId(mission.id)}>
              <span className="mission-row-icon">{mission.kind === 'Project' ? <FolderOpen size={18} /> : <MessageSquare size={18} />}</span>
              <span><strong>{mission.task}</strong><small>{mission.selectedAgent ?? 'Routage en attente'} · {new Date(mission.createdAt).toLocaleString('fr-FR')}</small></span>
              <StatusBadge mission={mission} /><ChevronRight size={16} />
            </button>)}
          </section>{selected && <MissionDetails mission={selected} cancel={id => void cancel(id)} busy={busy} refresh={refresh} />}
        </div>}
        <footer className="page-footer"><span>AIPlatform Studio</span><span>Les modèles peuvent se tromper. Vérifiez le code avant toute utilisation.</span></footer>
      </div>
    </main>
  </div>
}
