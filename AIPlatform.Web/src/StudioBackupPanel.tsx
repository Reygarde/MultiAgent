import { useRef, useState } from 'react'
import { Archive, Download, RefreshCw, ShieldCheck, Upload } from 'lucide-react'
import { errorMessage } from './api'
import { fetchApi } from './desktop/transport'

interface BackupPreview {
  id: string
  applicationVersion: string
  archiveBytes: number
  dataBytes: number
  files: number
  missions: number
  projects: number
  missionIdConflicts: number
  redactedSettingsValues: number
  externalPathsNotTransferred: number
  executionApprovalsToRevoke: number
  pendingMissionsToStop: number
  externalAuthorizationsToRevoke: number
  requiredFreeBytes: number
  availableFreeBytes: number
  spaceAvailable: boolean
  replacesAllCurrentData: boolean
  settingsIncluded: boolean
  replacedCategories: string[]
  exclusions: string[]
  dependenciesToInstall: string[]
  warnings: string[]
}

interface RestoreResult {
  status: string
  missionsRestored: number
  projectsRestored: number
  executionApprovalsRevoked: number
  pendingMissionsStopped: number
  externalAuthorizationsRevoked: number
  sourceFingerprintsPreserved: number
  restartRequired: boolean
  dependenciesToInstall: string[]
  warnings: string[]
}

async function responseError(response: Response): Promise<Error> {
  try {
    const problem = await response.json() as { title?: string; detail?: string }
    return new Error(problem.detail || problem.title || `Erreur HTTP ${response.status}`)
  } catch {
    return new Error(`Erreur HTTP ${response.status}`)
  }
}

function formatBytes(bytes: number): string {
  if (bytes < 1024 * 1024) return `${Math.ceil(bytes / 1024)} Ko`
  return `${(bytes / (1024 * 1024)).toLocaleString('fr-FR', { maximumFractionDigits: 1 })} Mio`
}

export function StudioBackupPanel({ restartBackend }: { restartBackend: () => Promise<void> }) {
  const input = useRef<HTMLInputElement>(null)
  const [file, setFile] = useState<File | null>(null)
  const [preview, setPreview] = useState<BackupPreview | null>(null)
  const [result, setResult] = useState<RestoreResult | null>(null)
  const [confirmReplace, setConfirmReplace] = useState(false)
  const [confirmConflicts, setConfirmConflicts] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  async function createBackup() {
    setBusy(true); setError(null); setNotice(null)
    try {
      const response = await fetchApi('/studio-backup/create', { method: 'POST' })
      if (!response.ok) throw await responseError(response)
      const blob = await response.blob()
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `AIPlatformStudio-${new Date().toISOString().replace(/[:.]/g, '-')}.aiplatform.zip`
      link.click()
      URL.revokeObjectURL(url)
      setNotice('La sauvegarde vérifiée a été téléchargée. Conservez-la dans un emplacement sûr.')
    } catch (reason) {
      setError(errorMessage(reason))
    } finally { setBusy(false) }
  }

  async function inspectArchive(selected: File | null) {
    setFile(selected)
    setPreview(null); setResult(null); setConfirmReplace(false); setConfirmConflicts(false)
    setError(null); setNotice(null)
    if (!selected) return
    setBusy(true)
    try {
      const response = await fetchApi('/studio-backup/inspect', {
        method: 'POST',
        headers: { 'Content-Type': 'application/zip' },
        body: selected,
      })
      if (!response.ok) throw await responseError(response)
      setPreview(await response.json() as BackupPreview)
    } catch (reason) {
      setError(errorMessage(reason))
    } finally { setBusy(false) }
  }

  async function restore() {
    if (!preview || !confirmReplace || preview.missionIdConflicts > 0 && !confirmConflicts) return
    setBusy(true); setError(null); setNotice(null)
    try {
      const response = await fetchApi(`/studio-backup/restore/${encodeURIComponent(preview.id)}`, {
        method: 'POST',
        body: JSON.stringify({
          confirmReplaceAllCurrentData: confirmReplace,
          acknowledgeMissionIdConflicts: confirmConflicts,
        }),
      })
      if (!response.ok) throw await responseError(response)
      const restored = await response.json() as RestoreResult
      setResult(restored)
      setNotice('Restauration terminée. Redémarrage du backend…')
      await restartBackend()
    } catch (reason) {
      setError(errorMessage(reason))
    } finally { setBusy(false) }
  }

  async function restartAgain() {
    setBusy(true); setError(null)
    try { await restartBackend() }
    catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(false) }
  }

  return <section className="panel studio-backup" aria-labelledby="studio-backup-title">
    <div className="panel-heading">
      <h2 id="studio-backup-title"><Archive size={17} /> Sauvegarder et restaurer Studio</h2>
      <span className="badge quiet">Transfert local</span>
    </div>
    <p>La sauvegarde inclut les données internes de Studio et leurs empreintes. Elle n’inclut ni les modèles Ollama, ni les images Docker, ni les secrets de Windows.</p>
    {error && <div role="alert" className="notice danger">{error}</div>}
    {notice && <div role="status" className="notice warning">{notice}</div>}
    <div className="backup-actions">
      <button className="button primary" disabled={busy} onClick={() => void createBackup()}>
        <Download size={15} /> Sauvegarder Studio
      </button>
      <button className="button secondary" disabled={busy} onClick={() => input.current?.click()}>
        <Upload size={15} /> Sélectionner une sauvegarde
      </button>
      <input ref={input} type="file" accept=".zip,.aiplatform.zip,application/zip" hidden
        aria-label="Sélectionner une archive Studio" onChange={event => void inspectArchive(event.target.files?.[0] ?? null)} />
    </div>

    {busy && <p className="backup-progress" role="status"><RefreshCw size={14} className="spin" /> Vérification ou transfert en cours…</p>}
    {preview && <div className="backup-preview">
      <h3>Aperçu avant remplacement</h3>
      <dl>
        <div><dt>Version</dt><dd>{preview.applicationVersion}</dd></div>
        <div><dt>Archive vérifiée</dt><dd>{file?.name} · {formatBytes(preview.archiveBytes)}</dd></div>
        <div><dt>Données / fichiers</dt><dd>{formatBytes(preview.dataBytes)} · {preview.files}</dd></div>
        <div><dt>Missions / projets</dt><dd>{preview.missions} / {preview.projects}</dd></div>
        <div><dt>IDs de missions en conflit</dt><dd>{preview.missionIdConflicts}</dd></div>
        <div><dt>Espace disponible</dt><dd>{formatBytes(preview.availableFreeBytes)} / {formatBytes(preview.requiredFreeBytes)} requis</dd></div>
        <div><dt>Chemins externes à vérifier</dt><dd>{preview.externalPathsNotTransferred}</dd></div>
        <div><dt>Autorisations à révoquer</dt><dd>{preview.executionApprovalsToRevoke} exécution(s), {preview.externalAuthorizationsToRevoke} locale(s)</dd></div>
        <div><dt>Missions en cours à arrêter</dt><dd>{preview.pendingMissionsToStop}</dd></div>
        <div><dt>Valeurs sensibles omises des paramètres</dt><dd>{preview.redactedSettingsValues}</dd></div>
      </dl>
      <h4>Données remplacées</h4>
      <ul>{preview.replacedCategories.map(category => <li key={category}>{category}</li>)}</ul>
      <h4>Exclusions et dépendances à réinstaller</h4>
      <ul>{preview.exclusions.map(item => <li key={item}>{item}</li>)}
        {preview.dependenciesToInstall.map(item => <li key={item}>{item}</li>)}</ul>
      {preview.warnings.map(warning => <p className="notice warning" key={warning}>{warning}</p>)}
      {!preview.spaceAvailable && <p className="notice danger" role="alert">Espace insuffisant pour une restauration transactionnelle.</p>}
      <label className="backup-confirm">
        <input type="checkbox" checked={confirmReplace} onChange={event => setConfirmReplace(event.target.checked)} />
        Je confirme le remplacement complet de toutes les données internes Studio sur ce PC.
      </label>
      {preview.missionIdConflicts > 0 && <label className="backup-confirm">
        <input type="checkbox" checked={confirmConflicts} onChange={event => setConfirmConflicts(event.target.checked)} />
        Je comprends que les missions ayant un identifiant identique seront remplacées, sans fusion.
      </label>}
      <button className="button danger" disabled={busy || !preview.spaceAvailable || !confirmReplace ||
        preview.missionIdConflicts > 0 && !confirmConflicts} onClick={() => void restore()}>
        <ShieldCheck size={15} /> Confirmer et restaurer
      </button>
    </div>}
    {result && <div className="backup-result" role="status">
      <h3>Restauration appliquée</h3>
      <p>{result.missionsRestored} mission(s), {result.projectsRestored} projet(s), {result.executionApprovalsRevoked} approbation(s) d’exécution révoquée(s), {result.pendingMissionsStopped} mission(s) en attente arrêtée(s).</p>
      <p>{result.sourceFingerprintsPreserved} fichiers de projets conservés sans modification; {result.externalAuthorizationsRevoked} autorisation(s) locale(s) révoquée(s).</p>
      <p>Le backend doit redémarrer avant toute autre action.</p>
      <button className="button primary" disabled={busy} onClick={() => void restartAgain()}>Redémarrer le backend</button>
      {result.warnings.map(warning => <p className="notice warning" key={warning}>{warning}</p>)}
    </div>}
  </section>
}
