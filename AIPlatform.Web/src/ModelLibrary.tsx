import { useCallback, useEffect, useRef, useState } from 'react'
import { api, errorMessage } from './api'
import { modelLibraryApi, type LibraryModel, type ModelDownload, type ModelLibrarySnapshot, type ModelRoutingDecision } from './modelLibraryApi'
import type { CustomAgent } from './types'
import { maximumModelContext } from './types'

const compatibilityLabels: Record<string, string> = {
  Compatible: 'Compatible estimé', Borderline: 'Limite', Insufficient: 'Probablement insuffisant', Unknown: 'Informations insuffisantes',
}
const downloadLabels: Record<string, string> = {
  Queued: 'En attente', Running: 'En cours', Completed: 'Installé', Cancelled: 'Annulé', Interrupted: 'Interrompu', Failed: 'Échec',
}
function bytes(value: number | null | undefined) {
  return value == null ? 'Inconnu' : `${(value / 1073741824).toLocaleString('fr-FR', { maximumFractionDigits: 2 })} Gio`
}

export function RoutingExplanation({ decision }: { decision: ModelRoutingDecision }) {
  return <details><summary>Pourquoi ce modèle a été choisi ?</summary>
    <p><strong>{decision.model}</strong> · {decision.category} · confiance qualitative : {decision.confidence}</p>
    <p>{decision.reason}</p>
    <ul>{decision.alternatives.map(item => <li key={item.model}><strong>{item.model}</strong> : {item.reason}
      <p>{item.compatibility.reason}</p></li>)}</ul>
  </details>
}

export function ModelLibrary() {
  const [snapshot, setSnapshot] = useState<ModelLibrarySnapshot | null>(null)
  const [downloads, setDownloads] = useState<ModelDownload[]>([])
  const [agents, setAgents] = useState<CustomAgent[]>([])
  const [tab, setTab] = useState('Catalogue')
  const [query, setQuery] = useState('')
  const [family, setFamily] = useState('')
  const [specialization, setSpecialization] = useState('')
  const [compatibility, setCompatibility] = useState('')
  const [installation, setInstallation] = useState('')
  const [size, setSize] = useState('')
  const [context, setContext] = useState(4096)
  const [manual, setManual] = useState('')
  const [verified, setVerified] = useState<{ name: string; downloadBytes: number } | null>(null)
  const [selectedAgents, setSelectedAgents] = useState<Record<string, string>>({})
  const [specializationDrafts, setSpecializationDrafts] = useState<Record<string, string[]>>({})
  const [task, setTask] = useState('')
  const [role, setRole] = useState('Developer')
  const [decision, setDecision] = useState<ModelRoutingDecision | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const completed = useRef(new Set<string>())

  const refresh = useCallback(async (signal?: AbortSignal) => {
    const [library, users] = await Promise.all([modelLibraryApi.snapshot(context, signal), api.customAgents(signal)])
    if (!signal?.aborted) { setSnapshot(library); setAgents(users) }
  }, [context])
  useEffect(() => {
    const controller = new AbortController()
    setError(null)
    void refresh(controller.signal).catch(error => { if (!controller.signal.aborted) setError(errorMessage(error)) })
    return () => controller.abort()
  }, [refresh])
  useEffect(() => {
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout>
    async function poll() {
      try {
        const jobs = await modelLibraryApi.downloads(controller.signal)
        if (controller.signal.aborted) return
        setDownloads(jobs)
        const newlyCompleted = jobs.filter(job => job.status === 'Completed' && !completed.current.has(job.id))
        jobs.filter(job => job.status === 'Completed').forEach(job => completed.current.add(job.id))
        if (newlyCompleted.length) await refresh(controller.signal)
      } catch (error) { if (!controller.signal.aborted) setError(errorMessage(error)) }
      finally { if (!controller.signal.aborted) timer = setTimeout(() => void poll(), 2000) }
    }
    void poll()
    return () => { controller.abort(); clearTimeout(timer) }
  }, [refresh])

  async function act(action: () => Promise<unknown>) {
    setBusy(true); setError(null)
    try { await action() }
    catch (error) { setError(errorMessage(error)) }
    finally { setBusy(false) }
  }
  async function install(name: string) {
    if (!window.confirm(`Installer ${name} via Ollama ? Le téléchargement utilise Internet et de l’espace disque. La possibilité de télécharger ne garantit ni le chargement ni une exécution acceptable. Consultez la licence et l’estimation mémoire ; les tailles du catalogue sont approximatives.`)) return
    await act(async () => {
      const job = await modelLibraryApi.install(name)
      setDownloads(current => [job, ...current]); setTab('Téléchargements')
    })
  }
  async function remove(model: LibraryModel) {
    if (!window.confirm(`Supprimer définitivement ${model.catalogue.name} d’Ollama ? Aucune mission ne sera effacée.`)) return
    await act(async () => { await modelLibraryApi.remove(model.catalogue.name); await refresh() })
  }
  const visible = (snapshot?.models ?? []).filter(model => {
    const c = model.catalogue
    const n = model.metadata?.installedBytes ?? c.downloadBytes
    return (tab !== 'Modèles installés' || model.installed) &&
      (!query || `${c.name} ${c.family} ${c.publisher ?? ''}`.toLocaleLowerCase().includes(query.toLocaleLowerCase())) &&
      (!family || c.family === family) && (!specialization || c.specializations.includes(specialization)) &&
      (!compatibility || model.compatibility.status === compatibility) &&
      (!installation || (installation === 'installed' ? model.installed === true : model.installed === false)) &&
      (!size || n != null && (size === 'small' ? n < 4e9 : size === 'medium' ? n >= 4e9 && n <= 10e9 : n > 10e9))
  })

  return <section className="model-library">
    <div className="panel"><h2>Bibliothèque de modèles</h2>
      <p>Catalogue documentaire, sans téléchargement automatique ni mesure de performance inventée. Les modèles disponibles et leurs licences peuvent évoluer.</p>
      <p className="muted small">Poids ouverts ≠ open-source. Une licence permissive ne certifie pas à elle seule le caractère open-source d’un modèle IA.</p>
      {error && <div role="alert" className="notice danger">{error}</div>}
      <button className="button secondary" disabled={busy} onClick={() => void act(() => refresh())}>Actualiser la bibliothèque et le matériel</button>
      <label>Contexte d’estimation<select value={context} onChange={event => setContext(Number(event.target.value))}>
        {[2048, 4096, 8192, 16384, 32768, 65536, 131072, maximumModelContext].map(value =>
          <option key={value} value={value}>{value} tokens</option>)}
      </select></label>
      <p className="muted small">Aucun profil existant n’est agrandi. Le contexte déclaré du modèle, la mémoire et la sortie inférieure au contexte restent contrôlés.</p>
      {snapshot && <div aria-label="Matériel local">
        <p>RAM disponible / totale : {bytes(snapshot.hardware.availableRamBytes)} / {bytes(snapshot.hardware.totalRamBytes)}.
          Disque disponible : {bytes(snapshot.hardware.freeDiskBytes)}.</p>
        {snapshot.hardware.gpus.map((gpu, index) => <p key={index}>{gpu.name} — VRAM disponible / totale : {bytes(gpu.availableBytes)} / {bytes(gpu.totalBytes)}</p>)}
        {snapshot.hardware.warnings.map(warning => <p className="notice warning" key={warning}>{warning}</p>)}
        <p>Modèles chargés : {snapshot.loaded.length ? snapshot.loaded.map(model => model.name).join(', ') : 'aucun signalé'}</p>
        {snapshot.ollamaError && <p role="alert" className="notice warning">Ollama indisponible : {snapshot.ollamaError}. État d’installation inconnu.</p>}
      </div>}
      <div role="tablist" aria-label="Bibliothèque de modèles">{['Catalogue', 'Modèles installés', 'Téléchargements'].map(value =>
        <button className="button secondary" key={value} role="tab" aria-selected={tab === value}
          onClick={() => setTab(value)}>{value}</button>)}</div>
    </div>
    {tab !== 'Téléchargements' ? <>
      <div className="panel settings-grid">
        <label>Rechercher un modèle<input type="search" value={query} onChange={event => setQuery(event.target.value)} /></label>
        <label>Famille<select value={family} onChange={event => setFamily(event.target.value)}><option value="">Toutes</option>
          {[...new Set(snapshot?.models.map(model => model.catalogue.family))].sort().map(value => <option key={value}>{value}</option>)}</select></label>
        <label>Spécialisation<select value={specialization} onChange={event => setSpecialization(event.target.value)}><option value="">Toutes</option>
          {[...new Set(snapshot?.models.flatMap(model => model.catalogue.specializations))].sort().map(value => <option key={value}>{value}</option>)}</select></label>
        <label>Compatibilité<select value={compatibility} onChange={event => setCompatibility(event.target.value)}><option value="">Toutes</option>
          {Object.entries(compatibilityLabels).map(([key, label]) => <option key={key} value={key}>{label}</option>)}</select></label>
        <label>Installation<select value={installation} onChange={event => setInstallation(event.target.value)}><option value="">Toutes</option>
          <option value="installed">Installés</option><option value="absent">Non installés</option></select></label>
        <label>Taille<select value={size} onChange={event => setSize(event.target.value)}><option value="">Toutes, y compris inconnues</option>
          <option value="small">Moins de 4 Go</option><option value="medium">4 à 10 Go</option><option value="large">Plus de 10 Go</option></select></label>
      </div>
      <div className="cards-grid">{visible.map(model => <article className="panel model-card" key={model.catalogue.name}>
        <h3>{model.catalogue.name}</h3>
        <p>{model.catalogue.family} · {model.catalogue.publisher ?? 'Éditeur inconnu'}</p>
        <p>{snapshot?.ollamaError ? 'Installation inconnue' : model.installed ? 'Installé' : 'Non installé'}{model.loaded && ' · Chargé'}</p>
        <p>{model.catalogue.specializations.join(' · ') || 'Spécialisation non documentée'}</p>
        <details><summary>Spécialisations et qualification</summary>
          <p>{model.routingProfile?.specializations == null ? 'Valeurs du catalogue (ou aucune pour un modèle inconnu).' : 'Spécialisations déclarées explicitement.'}
            {' '}Déclarer Code ne garantit ni la qualité du code ni la compatibilité mémoire.</p>
          <fieldset disabled={busy}><legend>Spécialisations de {model.catalogue.name}</legend>
            {(snapshot?.availableSpecializations ?? []).map(value => {
              const selected = specializationDrafts[model.catalogue.name] ?? model.catalogue.specializations
              return <label key={value}><input type="checkbox" checked={selected.includes(value)}
                onChange={event => setSpecializationDrafts(current => ({ ...current, [model.catalogue.name]:
                  event.target.checked ? [...selected, value] : selected.filter(item => item !== value) }))} />{value}</label>
            })}
          </fieldset>
          <button className="button secondary" disabled={busy || specializationDrafts[model.catalogue.name] == null}
            onClick={() => void act(async () => {
              await modelLibraryApi.saveSpecializations(model.catalogue.name, specializationDrafts[model.catalogue.name])
              await refresh()
              setSpecializationDrafts(current => { const next = { ...current }; delete next[model.catalogue.name]; return next })
            })}>Enregistrer les spécialisations de {model.catalogue.name}</button>
          <button className="button secondary" disabled={busy || model.routingProfile?.specializations == null}
            onClick={() => void act(async () => {
              await modelLibraryApi.saveSpecializations(model.catalogue.name, null)
              await refresh()
              setSpecializationDrafts(current => { const next = { ...current }; delete next[model.catalogue.name]; return next })
            })}>Restaurer le catalogue pour {model.catalogue.name}</button>
          <p>Architecture inconnue : non admissible automatiquement sans qualification explicite d’un chargement réel.
            La sélection manuelle reste distincte, avec avertissements et refus mémoire. Chargez d’abord le modèle via Ollama au contexte souhaité :
            ce bouton ne charge aucun modèle et ne lance aucune génération.</p>
          {model.routingProfile?.qualification && <p>Observation : {model.routingProfile.qualification.observedAt},
            {' '}{model.routingProfile.qualification.contextTokens} tokens ; RAM hors réserve :
            {' '}{bytes(model.routingProfile.qualification.sizeBytes - model.routingProfile.qualification.sizeVramBytes)} ;
            VRAM : {bytes(model.routingProfile.qualification.sizeVramBytes)}.
            Empreinte : <code>{model.routingProfile.qualification.digest}</code>.
            La preuve ne vaut pas pour des poids remplacés ni un contexte supérieur ; les ressources sont recontrôlées.</p>}
          <button className="button secondary" disabled={busy || !model.installed || !model.loaded}
            onClick={() => {
              if (window.confirm(`Qualifier le chargement déjà observé de ${model.catalogue.name} pour ${context} tokens ? Aucun chargement ni appel de génération. Cette observation ne garantit pas les performances ou un futur rechargement.`))
                void act(async () => { await modelLibraryApi.qualify(model.catalogue.name, context); await refresh() })
            }}>Qualifier le chargement de {model.catalogue.name}</button>
          <button className="button secondary" disabled={busy || !model.routingProfile?.qualification} onClick={() => {
            if (window.confirm(`Retirer la qualification mémoire de ${model.catalogue.name} sans modifier les missions ?`))
              void act(async () => { await modelLibraryApi.clearQualification(model.catalogue.name); await refresh() })
          }}>Retirer la qualification de {model.catalogue.name}</button>
        </details>
        <dl className="model-specs">
          <div><dt>Paramètres / quantification</dt><dd>{model.metadata?.parameters ?? 'Inconnus'} / {model.metadata?.quantization ?? 'Inconnue'}</dd></div>
          <div><dt>Téléchargement catalogue (approx.)</dt><dd>{bytes(model.catalogue.downloadBytes)}</dd></div>
          <div><dt>Taille installée</dt><dd>{bytes(model.metadata?.installedBytes)}</dd></div>
          <div><dt>Contexte maximal déclaré</dt><dd>{model.metadata?.contextLength ?? model.catalogue.documentedContext ?? 'Inconnu'}</dd></div>
          <div><dt>Licence</dt><dd>{model.metadata?.license ?? model.catalogue.license ?? 'Inconnue — consulter la source'}</dd></div>
          <div><dt>Statut open-source</dt><dd>Non certifié</dd></div>
        </dl>
        <a href={model.catalogue.source} target="_blank" rel="noreferrer">Fiche et licence Ollama</a>
        <p><strong>{compatibilityLabels[model.compatibility.status]}</strong> · {model.compatibility.placement}</p>
        <details><summary>Estimation mémoire et contraintes</summary><p>{model.compatibility.reason}</p>
          <p>Poids : {bytes(model.compatibility.weightsBytes)} ; KV : {bytes(model.compatibility.kvBytes)} ;
            buffers : {bytes(model.compatibility.buffersBytes)}.</p>
          <p>RAM requise estimée : {bytes(model.compatibility.requiredRamBytes)} ; VRAM : {bytes(model.compatibility.requiredVramBytes)}.
            Ces mémoires ne sont pas interchangeables.</p></details>
        <p>Utilisations : {model.dependencies.map(item => `${item.kind} ${item.name}${item.active ? ' (actif)' : ''}`).join(' · ') || 'aucune configuration recensée'}</p>
        {model.installed ? <>
          <label>Agent à affecter<select value={selectedAgents[model.catalogue.name] ?? ''} onChange={event =>
            setSelectedAgents(current => ({ ...current, [model.catalogue.name]: event.target.value }))}>
            <option value="">Choisir un agent</option>{agents.map(agent => <option key={agent.id} value={agent.id}>{agent.name}</option>)}</select></label>
          <button className="button secondary" disabled={busy || !selectedAgents[model.catalogue.name]} onClick={() => {
            const agent = agents.find(agent => agent.id === selectedAgents[model.catalogue.name])
            if (agent && window.confirm(`Affecter ${model.catalogue.name} à ${agent.name} en mode manuel ?`))
              void act(async () => { await api.updateCustomAgent(agent.id, { ...agent, modelName: model.catalogue.name, automaticModelSelection: false }); await refresh() })
          }}>Affecter en mode manuel</button>
          <button className="button secondary" disabled={busy || model.dependencies.some(item => item.kind !== 'History') || model.loaded != null}
            onClick={() => void remove(model)}>Supprimer le modèle</button>
          {model.dependencies.some(item => item.kind !== 'History') && <p className="muted small">Réaffectez explicitement les utilisations dans Agents/Teams ou les configurations de missions avant suppression.</p>}
        </> : <button className="button secondary" disabled={busy || !!snapshot?.ollamaError} onClick={() => void install(model.catalogue.name)}>Installer {model.catalogue.name}</button>}
      </article>)}</div>
      {snapshot && visible.length === 0 && <p>Aucun modèle pour ces filtres.</p>}
      <div className="panel">
        <h3>Identifiant Ollama manuel</h3>
        <label>Identifiant exact<input value={manual} maxLength={160} placeholder="namespace/modele:tag"
          onChange={event => { setManual(event.target.value); setVerified(null) }} /></label>
        <button className="button secondary" disabled={busy || !manual.trim()} onClick={() => void act(async () => {
          setVerified(await modelLibraryApi.verify(manual.trim()))
        })}>Vérifier le tag sans télécharger</button>
        {verified && <><p>Tag vérifié : {verified.name} · manifeste {bytes(verified.downloadBytes)}. Compatibilité et licence à vérifier.</p>
          <button className="button secondary" disabled={busy} onClick={() => void install(verified.name)}>Confirmer l’installation du tag vérifié</button></>}
      </div>
    </> : <div className="panel">
      {!downloads.length && <p>Aucun téléchargement.</p>}
      {downloads.map(job => <article key={job.id}><h3>{job.model} · {downloadLabels[job.status] ?? job.status}</h3>
        <p>{job.progress}</p><p>Couche / transfert signalé : {bytes(job.completed)} / {bytes(job.total)}</p>
        {job.total != null && job.total > 0 && <progress max={job.total} value={job.completed ?? 0} aria-label={`Progression ${job.model}`} />}
        {job.error && <p role="alert">{job.error}</p>}
        {['Queued', 'Running'].includes(job.status) ? <button className="button secondary" disabled={busy}
          onClick={() => void act(() => modelLibraryApi.cancel(job.id))}>Annuler {job.model}</button> :
          job.status !== 'Completed' && <button className="button secondary" disabled={busy}
            onClick={() => void install(job.model)}>Réessayer {job.model}</button>}
      </article>)}
    </div>}
    <div className="panel"><h3>Prévisualiser le routage, sans génération</h3>
      <label>Tâche à classifier<textarea value={task} maxLength={8000} onChange={event => setTask(event.target.value)} /></label>
      <label>Rôle<select value={role} onChange={event => setRole(event.target.value)}>
        {['Developer', 'Planner', 'Reviewer', 'Chat'].map(value => <option key={value}>{value}</option>)}</select></label>
      <button className="button secondary" disabled={busy || !task.trim()} onClick={() =>
        void act(async () => { setDecision(await modelLibraryApi.preview(task, role, context)) })}>Expliquer la sélection</button>
      {decision && <RoutingExplanation decision={decision} />}
      <button className="button secondary" disabled={busy} onClick={() => {
        if (window.confirm('Réinitialiser les statistiques de classement sans supprimer les historiques de missions ?'))
          void act(() => modelLibraryApi.resetStatistics())
      }}>Réinitialiser les statistiques de sélection</button>
    </div>
  </section>
}
