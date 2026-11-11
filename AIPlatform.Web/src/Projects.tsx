import { useEffect, useState } from 'react'
import { FileCode2, FolderOpen, LoaderCircle } from 'lucide-react'
import { api, errorMessage } from './api'
import type { FileContent, Mission, WorkspaceFile } from './types'
import { CodeBlock } from './Markdown'
import { StatusBadge } from './MissionDetails'

export function Projects({ missions, selectedId, onSelect }: {
  missions: Mission[]; selectedId: string | null; onSelect: (id: string) => void
}) {
  const projects = missions.filter(mission => mission.hasWorkspace)
  const selected = projects.find(project => project.id === selectedId) ?? projects[0]
  const [files, setFiles] = useState<WorkspaceFile[]>([])
  const [content, setContent] = useState<FileContent | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [path, setPath] = useState<string | null>(null)
  useEffect(() => {
    setFiles([]); setContent(null); setPath(null); setError(null)
    if (!selected) return
    const controller = new AbortController()
    void api.files(selected.id, controller.signal).then(result => {
      setFiles(result)
      setPath(result.find(file => file.path.endsWith('Program.cs'))?.path ?? result[0]?.path ?? null)
    }).catch(error => { if (!controller.signal.aborted) setError(errorMessage(error)) })
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
    <aside className="panel project-list"><h2>Workspaces <span className="count">{projects.length}</span></h2>
      {projects.map(project => <button key={project.id} className={`project-entry ${selected?.id === project.id ? 'selected' : ''}`}
        onClick={() => onSelect(project.id)}>
        <FolderOpen size={17} /><span><strong>{project.task}</strong><small>{new Date(project.createdAt).toLocaleDateString('fr-FR')}</small><StatusBadge mission={project} /></span>
      </button>)}
    </aside>
    <section className="panel explorer">
      <div className="panel-heading"><h2><FileCode2 size={17} /> Fichiers du projet</h2><span className="muted">{files.length} fichiers</span></div>
      {error && <div className="notice danger" role="alert">{error}</div>}
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
