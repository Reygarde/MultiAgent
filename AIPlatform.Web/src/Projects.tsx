import { useEffect, useRef, useState } from 'react'
import { FileCode2, FolderOpen, LoaderCircle } from 'lucide-react'
import { api, errorMessage } from './api'
import type { FileContent, Mission, ProjectAnalysis, WorkspaceFile } from './types'
import { CodeBlock } from './Markdown'
import { StatusBadge } from './MissionDetails'
import { isActive } from './types'
import { formatDateTime, missionSearchText, missionTitle, projectVerificationLabel } from './missionPresentation'

export function Projects({ missions, selectedId, onSelect, refresh, onOpenMission }: {
  missions: Mission[]; selectedId: string | null; onSelect: (id: string) => void; refresh: () => Promise<unknown>
  onOpenMission?: (id: string) => void
}) {
  const projects = missions.filter(mission => mission.hasWorkspace)
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState<'newest' | 'oldest'>('newest')
  const visibleProjects = projects.filter(project => !search.trim() ||
    missionSearchText(project).includes(search.trim().toLocaleLowerCase('fr-FR')))
    .sort((left, right) => {
      const leftDate = Date.parse(left.createdAt)
      const rightDate = Date.parse(right.createdAt)
      const leftValue = Number.isNaN(leftDate) ? Number.NEGATIVE_INFINITY : leftDate
      const rightValue = Number.isNaN(rightDate) ? Number.NEGATIVE_INFINITY : rightDate
      return sort === 'newest' ? rightValue - leftValue : leftValue - rightValue
    })
  const selected = visibleProjects.find(project => project.id === selectedId) ?? visibleProjects[0]
  const [files, setFiles] = useState<WorkspaceFile[]>([])
  const [content, setContent] = useState<FileContent | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [filesLoading, setFilesLoading] = useState(false)
  const [loading, setLoading] = useState(false)
  const [path, setPath] = useState<string | null>(null)
  const [editing, setEditing] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [busy, setBusy] = useState(false)
  const createdAt = selected ? formatDateTime(selected.createdAt) : null
  const updatedAt = selected && Date.parse(selected.updatedAt) > Date.parse(selected.createdAt)
    ? formatDateTime(selected.updatedAt) : null
  useEffect(() => { setEditing(false); setConfirmDelete(false) }, [selected?.id])
  async function changeProject(deleting: boolean) {
    if (!selected) return
    setBusy(true); setError(null)
    try {
      if (deleting) await api.deleteProject(selected.id)
      else await api.renameProject(selected.id, name, description)
      setEditing(false); setConfirmDelete(false); await refresh()
    } catch (error) { setError(errorMessage(error)) }
    finally { setBusy(false) }
  }
  async function downloadProject() {
    if (!selected) return
    setBusy(true); setError(null)
    try {
      const blob = await api.exportProject(selected.id)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url; link.download = `aiplatform-${selected.id}.zip`
      link.click()
      setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (failure) { setError(errorMessage(failure)) }
    finally { setBusy(false) }
  }
  useEffect(() => {
    setFiles([]); setContent(null); setPath(null); setError(null)
    setFilesLoading(!!selected)
    if (!selected) return
    const controller = new AbortController()
    void api.files(selected.id, controller.signal).then(result => {
      setFiles(result)
      setPath(result.find(file => file.path.endsWith('Program.cs'))?.path ?? result[0]?.path ?? null)
    }).catch(error => { if (!controller.signal.aborted) setError(errorMessage(error)) })
      .finally(() => { if (!controller.signal.aborted) setFilesLoading(false) })
    return () => controller.abort()
  }, [selected?.id, selected?.updatedAt])
  useEffect(() => {
    if (!selected || !path) return
    const controller = new AbortController()
    setLoading(true); setContent(null)
    void api.file(selected.id, path, controller.signal).then(setContent)
      .catch(error => { if (!controller.signal.aborted) setError(errorMessage(error)) })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [selected?.id, path])

  if (!projects.length) return <div className="empty panel"><FolderOpen size={36} /><h2>Aucun projet généré</h2>
    <p>Lancez une mission « Projet .NET » depuis le Workspace. Les fichiers apparaîtront ici après leur validation.</p></div>
  return <div className="projects-layout">
    <aside className="panel project-list"><h2>Projets <span className="count">{visibleProjects.length} / {projects.length}</span></h2>
      <div className="project-toolbar">
        <label>Rechercher<input aria-label="Rechercher un projet" type="search" value={search}
          onChange={event => setSearch(event.target.value)} placeholder="Nom, demande ou description" /></label>
        <label>Trier par<select aria-label="Trier les projets par date" value={sort}
          onChange={event => setSort(event.target.value as typeof sort)}>
          <option value="newest">Plus récents</option><option value="oldest">Plus anciens</option>
        </select></label>
      </div>
      {!visibleProjects.length && <div className="empty compact"><FolderOpen size={24} /><p>Aucun projet ne correspond à la recherche.</p></div>}
      {visibleProjects.map(project => <button key={project.id} className={`project-entry ${selected?.id === project.id ? 'selected' : ''}`}
        onClick={() => onSelect(project.id)}>
        <FolderOpen size={17} /><span><strong>{missionTitle(project)}</strong>
          <small>{formatDateTime(project.createdAt) ?? 'Date inconnue'}</small>
          <StatusBadge mission={project} />
          <small className="project-entry-verification">{projectVerificationLabel(project)}</small></span>
      </button>)}
    </aside>
    <section className="panel explorer">
      <div className="panel-heading"><h2><FileCode2 size={17} /> Fichiers du projet</h2><span className="muted">{files.length} fichiers</span></div>
      {error && <div className="notice danger" role="alert">{error}</div>}
      {filesLoading && <div className="loading"><LoaderCircle className="spin" size={16} /> Chargement des fichiers…</div>}
      {selected && <div className="project-verification">
        <h3>{missionTitle(selected)}</h3>
        <p>{selected.projectDescription || 'Projet .NET généré dans un workspace isolé.'}</p>
        <div className="project-dates">
          <span>Créé le <strong>{createdAt ?? 'date inconnue'}</strong></span>
          {updatedAt && <span>Dernière mise à jour <strong>{updatedAt}</strong></span>}
        </div>
        <div className="project-status">
          <span>Génération <StatusBadge mission={selected} /></span>
          <span>Vérification <strong>{projectVerificationLabel(selected)}</strong></span>
        </div>
        <button className="button secondary" disabled={!onOpenMission} onClick={() => onOpenMission?.(selected.id)}>
          Ouvrir la mission associée
        </button>
        <details className="technical-logs"><summary>Identifiant technique</summary><code>{selected.id}</code></details>
        <button className="button secondary" onClick={() => {
          setName(selected.projectName || missionTitle(selected)); setDescription(selected.projectDescription || ''); setEditing(true)
        }}>Renommer</button>
        <button className="button secondary" disabled={isActive(selected)} onClick={() => setConfirmDelete(true)}>Supprimer le projet</button>
        <button className="button secondary" disabled={busy || selected.status === 'Running' || selected.status === 'Queued'}
          onClick={() => void downloadProject()}>Exporter en ZIP</button>
        <p className="muted small">Inspectez l’archive avant partage : la détection des secrets n’est pas exhaustive.</p>
        <AnalysisPanel key={`${selected.id}-${selected.updatedAt}`} mission={selected} />
        {editing && <form className="settings-grid" onSubmit={event => { event.preventDefault(); void changeProject(false) }}>
          <label>Nom du projet<input value={name} onChange={event => setName(event.target.value)} maxLength={80} required /></label>
          <label>Description<textarea value={description} onChange={event => setDescription(event.target.value)} maxLength={1000} /></label>
          <button className="button primary" disabled={busy}>Enregistrer le nom</button>
          <button className="button secondary" type="button" onClick={() => setEditing(false)}>Annuler</button>
        </form>}
        {confirmDelete && <div className="notice warning"><p>Supprimer définitivement les sources et versions de ce projet uniquement ?
          La mission et ses journaux seront conservés.</p>
          <button className="button secondary" disabled={busy} onClick={() => void changeProject(true)}>Confirmer la suppression</button>
          <button className="button secondary" onClick={() => setConfirmDelete(false)}>Conserver le projet</button></div>}
        {!!selected.workflow?.revisions.length && <details className="technical-logs"><summary>Historique des versions</summary>
          {selected.workflow.revisions.map(revision => <section key={revision.version}>
            <h4>Version {revision.version} · {new Date(revision.createdAt).toLocaleString('fr-FR')}</h4>
            <p>{revision.reason}</p><p>{revision.files.join(', ')}</p><code>{revision.fingerprint}</code>
          </section>)}</details>}
      </div>}
      {!filesLoading && !error && selected && !files.length && <p className="empty compact">Aucun fichier n’est associé à ce workspace.</p>}
      <div className="file-browser">
        <nav className="file-tree" aria-label="Fichiers générés">{files.map(file => <button key={file.path}
          className={path === file.path ? 'selected' : ''} onClick={() => { setError(null); setPath(file.path) }}>
          <FileCode2 size={14} /><span>{file.path}</span>
        </button>)}</nav>
        <div className="file-preview">{loading ? <div className="loading"><LoaderCircle className="spin" /> Lecture du fichier…</div>
          : content ? <CodeBlock code={content.content} language={content.path} /> : <p className="muted">Aucun fichier disponible pour le moment.</p>}</div>
      </div>
      <div className="project-verification"><h3>Compilation & tests</h3>
        {!selected?.verification.length && <p className="muted">Aucune vérification exécutée pour le moment.</p>}
        {selected?.verification.map((result, index) => <details key={index} className="technical-logs" open={result.status === 'Failed'}>
          <summary>{result.stage} · {result.status} · sortie {result.exitCode ?? 'non exécuté'}</summary>
          <pre>{result.log}</pre>
        </details>)}
      </div>
    </section>
  </div>
}

function AnalysisPanel({ mission }: { mission: Mission }) {
  const [analysis, setAnalysis] = useState<ProjectAnalysis | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const controller = useRef<AbortController | null>(null)
  useEffect(() => () => controller.current?.abort(), [])
  async function analyze() {
    const request = new AbortController()
    controller.current = request
    setLoading(true); setError(null); setAnalysis(null)
    try { setAnalysis(await api.analysis(mission.id, request.signal)) }
    catch (failure) { if (!request.signal.aborted) setError(errorMessage(failure)) }
    finally { if (!request.signal.aborted) setLoading(false) }
  }
  return <section className="technical-logs">
    <h3>Analyse statique</h3>
    <p className="muted small">Workspace isolé uniquement ; fichiers sensibles et artefacts exclus.
      Aucun script ni appel Ollama. Rapport non persisté.</p>
    <button className="button secondary" disabled={loading || mission.status === 'Running' || mission.status === 'Queued'}
      onClick={() => void analyze()}>{loading ? 'Analyse…' : 'Analyser le projet'}</button>
    {error && <p role="alert" className="error-inline">{error}</p>}
    {analysis && <div>
      <p>Langages : {analysis.languages.join(', ') || 'non détectés'}</p>
      <p>Frameworks : {analysis.frameworks.join(', ') || 'non détectés'}</p>
      <p>Git : {analysis.gitRepositoryDetected === null ? 'non vérifié' : analysis.gitRepositoryDetected ? 'détecté' : 'non détecté'}</p>
      <p>Solutions : {analysis.solutions.join(', ') || 'aucune détectée'}</p>
      <p>Projets : {analysis.projects.join(', ') || 'aucun détecté'}</p>
      <p>Tests : {analysis.testProjects.join(', ') || 'aucun projet détecté'}</p>
      <p>Points d’entrée : {analysis.entryPoints.join(', ') || 'aucun détecté'}</p>
      <p>Docker : {analysis.dockerFiles.join(', ') || 'aucun fichier détecté'}</p>
      <p>Scripts déclarés (non exécutés) : {analysis.scripts.map(script => `${script.manifest}: ${script.name}`).join(', ') || 'aucun'}</p>
      <p>Dépendances déclarées : {analysis.dependencies.map(dependency =>
        `${dependency.name} (${dependency.ecosystem}, ${dependency.version ?? 'version non résolue'})`).join(', ') || 'aucune'}</p>
      {analysis.issues.map((issue, index) => <p role="alert" key={`${issue.path}-${index}`}>{issue.path} : {issue.message}</p>)}
    </div>}
  </section>
}
