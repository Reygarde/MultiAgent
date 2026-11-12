import { useState } from 'react'
import { api, errorMessage } from './api'
import type { AgentConfiguration, AgentSettings } from './types'

const tools: Record<string, string[]> = {
  Chat: [], Router: [], Planner: ['Plan'], Developer: ['GenerateFiles'],
  Review: ['ReadSources'], Test: ['DockerBuild', 'DockerTests'], Orchestrator: ['Workflow'],
}

export function ConfigurationEditor({ mode, saved }: { mode: 'agents' | 'models'; saved: () => Promise<void> }) {
  const [config, setConfig] = useState<AgentConfiguration | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [selected, setSelected] = useState(0)
  async function load() {
    setBusy(true); setError(null)
    try { setConfig(await api.configuration()) }
    catch (error) { setError(errorMessage(error)) }
    finally { setBusy(false) }
  }
  async function save() {
    if (!config) return
    setBusy(true); setError(null)
    try { setConfig(await api.saveConfiguration(config)); await saved(); setConfig(null) }
    catch (error) { setError(errorMessage(error)) }
    finally { setBusy(false) }
  }
  function agentChange(changes: Partial<AgentSettings>) {
    setConfig(current => current && ({ ...current, agents: current.agents.map((agent, index) =>
      index === selected ? { ...agent, ...changes } : agent) }))
  }
  const agent = config?.agents[selected]
  return <section className="panel configuration-editor">
    {error && <div role="alert" className="notice danger">{error}</div>}
    {!config ? <button className="button secondary" disabled={busy} onClick={() => void load()}>
      Configurer les {mode === 'agents' ? 'agents' : 'modèles'}</button> : <>
      <p className="notice warning">Enregistrement possible uniquement sans mission active ou en attente d’autorisation.
        Les workflows existants conservent leur configuration. Aucun modèle n’est téléchargé.</p>
      {mode === 'agents' && <>
        <label>Agent à modifier<select value={selected} onChange={event => setSelected(Number(event.target.value))}>
          {config.agents.map((item, index) => <option key={index} value={index}>{item.name || item.id || 'Nouvel agent'}</option>)}
        </select></label>
        <button className="button secondary" onClick={() => {
          setSelected(config.agents.length)
          setConfig({ ...config, agents: [...config.agents, { id: '', name: '', description: '', role: 'Chat',
            enabled: true, modelId: config.models[0]?.id ?? '', systemPrompt: '', capabilities: [], fallbackKeywords: [],
            allowedTools: [], taskTypes: ['Chat'], timeoutSeconds: 180 }] })
        }}>Créer un agent</button>
        {agent && <div className="settings-grid">
          <label>Identifiant<input value={agent.id} onChange={event => agentChange({ id: event.target.value })} maxLength={80} /></label>
          <label>Nom<input value={agent.name} onChange={event => agentChange({ name: event.target.value })} maxLength={80} /></label>
          <label>Rôle<select value={agent.role} onChange={event => agentChange({ role: event.target.value,
            allowedTools: tools[event.target.value], taskTypes: ['Chat', 'Router'].includes(event.target.value) ? ['Chat'] : ['Project'] })}>
            {Object.keys(tools).map(role => <option key={role}>{role}</option>)}</select></label>
          <label>Modèle<select value={agent.modelId} onChange={event => agentChange({ modelId: event.target.value })}>
            {config.models.map(model => <option key={model.id} value={model.id}>{model.name}</option>)}</select></label>
          <label>Description<textarea value={agent.description} onChange={event => agentChange({ description: event.target.value })} maxLength={1000} /></label>
          <label>Instructions système<textarea value={agent.systemPrompt} onChange={event => agentChange({ systemPrompt: event.target.value })} maxLength={4000} /></label>
          <label>Capacités (séparées par virgules)<input value={agent.capabilities.join(', ')}
            onChange={event => agentChange({ capabilities: event.target.value.split(',').map(value => value.trim()) })} /></label>
          <label>Délai maximal (secondes)<input type="number" min={5} max={900} value={agent.timeoutSeconds}
            onChange={event => agentChange({ timeoutSeconds: Number(event.target.value) })} /></label>
          <label><input type="checkbox" checked={agent.enabled} onChange={event => agentChange({ enabled: event.target.checked })} /> Actif</label>
          <fieldset><legend>Types de missions</legend>{['Chat', 'Project'].map(type => <label key={type}>
            <input type="checkbox" checked={agent.taskTypes.includes(type)} onChange={event => agentChange({
              taskTypes: event.target.checked ? [...agent.taskTypes, type] : agent.taskTypes.filter(item => item !== type),
            })} />{type}</label>)}</fieldset>
          <fieldset><legend>Outils autorisés pour ce rôle</legend>{tools[agent.role]?.map(tool => <label key={tool}>
            <input type="checkbox" checked={agent.allowedTools.includes(tool)} onChange={event => agentChange({
              allowedTools: event.target.checked ? [...agent.allowedTools, tool] : agent.allowedTools.filter(item => item !== tool),
            })} />{tool}</label>)}{!tools[agent.role]?.length && <span>Aucun outil système</span>}</fieldset>
        </div>}
      </>}
      {mode === 'models' && <>
        {config.models.map((model, index) => <fieldset className="settings-grid" key={index}><legend>{model.id}</legend>
          {(['id', 'name'] as const).map(field => <label key={field}>{field === 'name' ? 'Nom Ollama installé' : 'Identifiant'}
            <input value={model[field]} onChange={event => setConfig({ ...config,
              models: config.models.map((item, position) => position === index ? { ...item, [field]: event.target.value } : item) })} /></label>)}
          {(['contextSize', 'maxOutputTokens', 'temperature'] as const).map(field => <label key={field}>{field}
            <input type="number" step={field === 'temperature' ? 0.1 : 1} value={model[field]} onChange={event => setConfig({ ...config,
              models: config.models.map((item, position) => position === index ? { ...item, [field]: Number(event.target.value) } : item) })} /></label>)}
        </fieldset>)}
        <button className="button secondary" onClick={() => setConfig({ ...config, models: [...config.models,
          { id: `model-${config.models.length + 1}`, provider: 'Ollama', name: '', contextSize: 8192, maxOutputTokens: 2048, temperature: 0.2 }] })}>
          Ajouter un modèle déjà installé</button>
      </>}
      <div className="approval-actions"><button className="button primary" disabled={busy} onClick={() => void save()}>Enregistrer la configuration</button>
        <button className="button secondary" disabled={busy} onClick={() => { setConfig(null); setError(null) }}>Annuler les modifications</button></div>
    </>}
  </section>
}
