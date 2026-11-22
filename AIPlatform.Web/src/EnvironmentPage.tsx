import { useState } from 'react'
import { AlertTriangle, CheckCircle2, CircleHelp, Download, HardDrive, LoaderCircle, RefreshCw, ShieldCheck } from 'lucide-react'
import { api, errorMessage } from './api'
import type { EnvironmentCheck, EnvironmentState, StudioEnvironmentReport } from './types'

function stateLabel(state: EnvironmentState): string {
  switch (state) {
    case 'Ready': return 'Prêt'
    case 'Missing': return 'Manquant'
    case 'Incompatible': return 'Incompatible'
    case 'Unverified': return 'Non vérifié'
  }
}

function stateIcon(state: EnvironmentState) {
  if (state === 'Ready') return <CheckCircle2 size={16} />
  if (state === 'Unverified') return <CircleHelp size={16} />
  return <AlertTriangle size={16} />
}

function formatBytes(bytes: number | null): string {
  if (bytes == null || !Number.isFinite(bytes)) return 'Non mesuré'
  const gib = bytes / 1073741824
  return `${gib.toLocaleString('fr-FR', { maximumFractionDigits: 1 })} Gio`
}

function CheckRow({ check }: { check: EnvironmentCheck }) {
  return <div className="environment-row">
    <span className={`environment-state ${check.status.toLowerCase()}`}>
      {stateIcon(check.status)} {stateLabel(check.status)}
    </span>
    <div><strong>{check.name}{check.version && <small> · {check.version}</small>}</strong><p>{check.detail}</p></div>
  </div>
}

export function EnvironmentPage() {
  const [report, setReport] = useState<StudioEnvironmentReport | null>(null)
  const [busy, setBusy] = useState(false)
  const [confirmed, setConfirmed] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  async function refresh() {
    setBusy(true); setError(null); setNotice(null); setConfirmed(false)
    try { setReport(await api.environment()) }
    catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(false) }
  }

  async function verifyPackage() {
    setBusy(true); setError(null); setNotice(null); setConfirmed(false)
    try {
      const verified = await api.verifySandboxProvisioning()
      setNotice(verified.detail)
      setReport(await api.environment())
    } catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(false) }
  }

  async function importPackage() {
    const fingerprint = report?.provisioningPackage.fingerprint
    if (!fingerprint || !confirmed || busy) return
    setBusy(true); setError(null); setNotice(null)
    try {
      const images = await api.importSandboxProvisioning(fingerprint)
      setNotice(`${images.length} images sandbox importées et vérifiées. Aucun projet généré n’a été exécuté.`)
      setReport(await api.environment())
      setConfirmed(false)
    } catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(false) }
  }

  const missingModels = report?.models.filter(model => model.status === 'Missing') ?? []
  const missingImages = report?.sandboxImages.filter(image => image.status !== 'Ready') ?? []
  const diagnostics = report ? [report.memory, report.gpu, report.webView2, report.node, report.ollama,
    report.dockerDesktop, report.dockerEngine] : []

  return <section className="environment-page" aria-labelledby="environment-title">
    <div className="panel environment-intro">
      <div className="panel-heading"><h2 id="environment-title"><HardDrive size={18} /> Préparer ce PC</h2>
        <button className="button secondary" disabled={busy} onClick={() => void refresh()}>
          {busy ? <LoaderCircle className="spin" size={15} /> : <RefreshCw size={15} />}
          {report ? 'Revérifier' : 'Analyser l’environnement'}
        </button>
      </div>
      <p>Le diagnostic observe les composants présents sans les installer. Il ne lance aucun projet généré.
        Les modèles Ollama et les images Docker ne sont jamais téléchargés automatiquement.</p>
      <p className="environment-guidance">Après restauration d’une sauvegarde, réinstallez les modèles et images manquants,
        re-sélectionnez les projets externes non transférés et redonnez explicitement toute autorisation d’exécution.
        Les autorisations de l’ancien PC ne sont jamais réutilisées.</p>
    </div>
    {error && <div role="alert" className="notice danger">{error}</div>}
    {notice && <div role="status" className="notice warning">{notice}</div>}
    {busy && <p className="environment-progress" role="status"><RefreshCw size={14} className="spin" />
      Vérification en cours… Le calcul des empreintes peut prendre plusieurs minutes.</p>}
    {!report && !busy && <div className="panel empty compact"><HardDrive size={26} />
      <p>Lancez le diagnostic pour vérifier Windows, les outils locaux, Ollama et Docker.</p>
    </div>}

    {report && <>
      <div className="panel environment-summary">
        <strong>{report.operatingSystem} · {report.architecture}</strong>
        <span>Diagnostic du {new Date(report.checkedAt).toLocaleString('fr-FR')}</span>
        <CheckRow check={report.system} />
        <div className="environment-hardware">
          <div><span>RAM totale</span><strong>{formatBytes(report.totalRamBytes)}</strong></div>
          <div><span>RAM disponible</span><strong>{formatBytes(report.availableRamBytes)}</strong></div>
          <div><span>GPU / VRAM</span><strong>{report.gpus.length
            ? report.gpus.map(gpu => `${gpu.name} · ${formatBytes(gpu.availableBytes)} libres / ${formatBytes(gpu.totalBytes)}`).join(' · ')
            : 'Non détecté ou non mesurable'}</strong></div>
        </div>
      </div>

      <div className="panel environment-section">
        <h3>Dépendances locales</h3>
        <div className="environment-grid">{diagnostics.slice(1).map(check => <CheckRow key={check.id} check={check} />)}</div>
        {report.node.status !== 'Ready' && <p className="environment-guidance">
          Node.js 20.14 ou ultérieur est requis par les helpers TypeScript et navigateur. Installez-le depuis sa distribution officielle,
          puis redémarrez Studio et relancez le diagnostic.
        </p>}
        {report.webView2.status !== 'Ready' && <p className="environment-guidance">
          WebView2 Runtime est requis pour l’interface Windows. Utilisez l’installateur officiel Evergreen ou relancez l’installateur Studio;
          sa présence seule ne valide pas son démarrage.
        </p>}
        {report.ollama.status !== 'Ready' && <p className="environment-guidance">
          Installez et démarrez Ollama, puis revérifiez. Studio n’installe ni Ollama ni les modèles à votre place.
        </p>}
        {missingModels.length > 0 && <div className="notice warning">
          Modèles absents : {missingModels.map(model => model.name).join(', ')}. Installez manuellement les modèles configurés,
          puis relancez le diagnostic.
        </div>}
        {(report.dockerDesktop.status !== 'Ready' || report.dockerEngine.status !== 'Ready') &&
          <p className="environment-guidance">
            Installez Docker Desktop, démarrez-le et sélectionnez le moteur Linux. Studio ne démarrera pas Docker à votre place.
          </p>}
      </div>

      <div className="panel environment-section">
        <div className="panel-heading"><h3>Images sandbox versionnées</h3>
          <span className={`environment-state ${report.provisioningPackage.status.toLowerCase()}`}>
            {stateIcon(report.provisioningPackage.status)} {stateLabel(report.provisioningPackage.status)}
          </span>
        </div>
        <p>{report.provisioningPackage.detail}</p>
        {report.provisioningPackage.version && <p className="muted small">
          Paquet {report.provisioningPackage.version} · {report.provisioningPackage.imageCount} images ·
          {formatBytes(report.provisioningPackage.totalBytes)} · SHA-256 manifeste {report.provisioningPackage.fingerprint}
        </p>}
        <div className="environment-image-list">{report.sandboxImages.map(image => <article key={image.imageReference}>
          <div className="environment-image-heading"><strong>{image.imageReference}</strong>
            <span className={`environment-state ${image.status.toLowerCase()}`}>
              {stateIcon(image.status)} {stateLabel(image.status)}
            </span></div>
          <p>{image.version} · {image.detail}</p>
          <small>Image installée : {image.installedImageId ?? '—'} · attendue : {image.expectedImageId ?? 'manifeste non vérifié'}</small>
        </article>)}</div>
        {missingImages.length > 0 && <p className="environment-guidance">
          Pour préparer Docker, vérifiez d’abord les empreintes du paquet de provisioning inclus à l’installation.
          Aucun <code>docker pull</code> ni build local n’est lancé depuis cet écran.
        </p>}
        {report.provisioningPackage.fingerprint && report.provisioningPackage.status !== 'Ready' &&
          <button className="button secondary" disabled={busy} onClick={() => void verifyPackage()}>
            <ShieldCheck size={15} /> Vérifier le paquet
          </button>}
        {report.provisioningPackage.fingerprint && report.provisioningPackage.status === 'Ready' &&
          missingImages.length > 0 && <div className="environment-provision">
            <label className="backup-confirm">
              <input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} />
              Je confirme l’import du paquet vérifié dans Docker. Cela modifie les images locales, sans exécuter de code généré.
            </label>
            <button className="button primary" disabled={busy || !confirmed ||
              report.dockerEngine.status !== 'Ready'} onClick={() => void importPackage()}>
              <Download size={15} /> Importer les images sandbox
            </button>
          </div>}
      </div>

      {report.warnings.length > 0 && <div className="panel environment-section">
        <h3>Limites du diagnostic</h3>
        <ul>{report.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul>
      </div>}
    </>}
  </section>
}
