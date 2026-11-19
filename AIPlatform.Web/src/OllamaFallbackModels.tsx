const developerDefaults = ['qwen2.5-coder:7b', 'qwen3.5:9b', 'qwen2.5-coder:14b']

export function installedDeveloperFallbacks(models: string[], primary: string) {
  return developerDefaults.filter(model => model !== primary && models.includes(model))
}

export function OllamaFallbackModels({ label, value, inherited = [], models, primary, onChange }: {
  label: string
  value?: string[] | null
  inherited?: string[]
  models: string[]
  primary: string
  onChange: (models: string[] | null) => void
}) {
  const selected = value ?? inherited
  function move(index: number, direction: number) {
    const updated = [...selected]
    const other = index + direction
    ;[updated[index], updated[other]] = [updated[other]!, updated[index]!]
    onChange(updated)
  }
  return <fieldset>
    <legend>{label}</legend>
    <p className="muted small">Premier choix : {primary || 'choisir un modèle'}. Secours dans cet ordre :</p>
    <ol>{selected.map((model, index) => <li key={model}>
      <code>{model}</code>{!models.includes(model) && ' — indisponible, ignoré'}
      <button type="button" className="text-button" aria-label={`Monter ${model}`} disabled={index === 0}
        onClick={() => move(index, -1)}>↑</button>
      <button type="button" className="text-button" aria-label={`Descendre ${model}`} disabled={index === selected.length - 1}
        onClick={() => move(index, 1)}>↓</button>
      <button type="button" className="text-button" aria-label={`Retirer ${model}`}
        onClick={() => onChange(selected.filter(item => item !== model))}>Retirer</button>
    </li>)}</ol>
    <label>Ajouter un modèle de secours<select aria-label={`Ajouter un secours : ${label}`} value=""
      disabled={selected.length >= 8}
      onChange={event => { if (event.target.value) onChange([...selected, event.target.value]) }}>
      <option value="">Choisir parmi les modèles installés</option>
      {models.filter(model => model !== primary && !selected.includes(model))
        .map(model => <option key={model} value={model}>{model}</option>)}
    </select></label>
    <button type="button" className="text-button"
      onClick={() => onChange(installedDeveloperFallbacks(models, primary))}>Proposer les secours Developer installés</button>
    <button type="button" className="text-button" onClick={() => onChange(null)}>Hériter des valeurs par défaut</button>
  </fieldset>
}
