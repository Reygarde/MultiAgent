import { useEffect, useMemo, useState } from 'react'
import { Activity, Bot, Check, CircleDot, LoaderCircle, Plus, Save, ShieldCheck, Square, Trash2 } from 'lucide-react'
import { api, errorMessage } from './api'
import type { CustomAgentTeam, TwitchBot, TwitchCommand, TwitchCommandProposal, TwitchCommandTestResult, TwitchDeviceAuthorization, TwitchSettings, TwitchSimulationEvent, TwitchSimulationResult } from './types'

export function TwitchBots() {
  const [settings, setSettings] = useState<TwitchSettings | null>(null)
  const [clientId, setClientId] = useState('')
  const [bots, setBots] = useState<TwitchBot[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [channel, setChannel] = useState('')
  const [commands, setCommands] = useState<TwitchCommand[]>([])
  const [newName, setNewName] = useState('')
  const [newChannel, setNewChannel] = useState('')
  const [authorization, setAuthorization] = useState<TwitchDeviceAuthorization | null>(null)
  const [testInput, setTestInput] = useState('!hello')
  const [testResult, setTestResult] = useState<TwitchCommandTestResult | null>(null)
  const [simulationMessage, setSimulationMessage] = useState('!hello')
  const [simulationRole, setSimulationRole] = useState<'Viewer' | 'Moderator'>('Viewer')
  const [repeatSimulation, setRepeatSimulation] = useState(false)
  const [simulationDelay, setSimulationDelay] = useState(10)
  const [simulationResult, setSimulationResult] = useState<TwitchSimulationResult | null>(null)
  const [teams, setTeams] = useState<CustomAgentTeam[]>([])
  const [teamId, setTeamId] = useState('')
  const [commandRequest, setCommandRequest] = useState('')
  const [proposals, setProposals] = useState<TwitchCommandProposal[]>([])
  const [deleteApproved, setDeleteApproved] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const selected = bots.find(bot => bot.id === selectedId) ?? null
  const isRunning = !!selected && ['Starting', 'Connected', 'Reconnecting', 'Stopping'].includes(selected.state)
  const commandsChanged = useMemo(() => selected && (
    selected.name !== name || selected.channel !== channel ||
    JSON.stringify(selected.commands) !== JSON.stringify(commands)
  ), [selected, name, channel, commands])

  async function load() {
    const [nextSettings, nextBots] = await Promise.all([api.twitchSettings(), api.twitchBots()])
    setSettings(nextSettings)
    setClientId(nextSettings.clientId)
    setBots(nextBots)
    const current = nextBots.find(bot => bot.id === selectedId) ?? nextBots[0] ?? null
    setSelectedId(current?.id ?? null)
    if (current) setDraft(current)
  }

  function setDraft(bot: TwitchBot) {
    setName(bot.name)
    setChannel(bot.channel)
    setCommands(bot.commands.map(command => ({ ...command })))
  }

  useEffect(() => {
    void load().catch(reason => setError(errorMessage(reason)))
    void api.customAgentTeams().then(items => { setTeams(items); setTeamId(items[0]?.id ?? '') })
      .catch(reason => setError(errorMessage(reason)))
  }, [])

  useEffect(() => {
    let active = true
    setProposals([]); setDeleteApproved(false)
    if (selectedId) void api.twitchCommandProposals(selectedId)
      .then(items => { if (active) setProposals(items) })
      .catch(reason => { if (active) setError(errorMessage(reason)) })
    return () => { active = false }
  }, [selectedId])

  useEffect(() => {
    if (!authorization || !selectedId) return
    let timer: ReturnType<typeof setTimeout>
    let stopped = false
    const interval = Math.max(authorization.interval, 1) * 1000
    async function poll() {
      try {
        const result = await api.pollTwitchConnect(selectedId!)
        if (stopped) return
        if (result.pending) {
          timer = setTimeout(() => void poll(), interval)
          return
        }
        setAuthorization(null)
        if (result.authorized) {
          setNotice(`Compte Twitch ${result.accountLogin} autorisé. Le bot n’a pas été démarré.`)
          await load()
        } else {
          setError(result.message || 'L’autorisation Twitch a échoué.')
        }
      } catch (reason) {
        if (!stopped) {
          setError(errorMessage(reason))
          timer = setTimeout(() => void poll(), interval)
        }
      }
    }
    timer = setTimeout(() => void poll(), interval)
    return () => { stopped = true; clearTimeout(timer) }
  }, [authorization, selectedId])

  useEffect(() => {
    if (!isRunning || !selectedId) return
    let timer: ReturnType<typeof setTimeout>
    let stopped = false
    async function refreshEvents() {
      try {
        const nextBots = await api.twitchBots()
        if (stopped) return
        setBots(nextBots)
        timer = setTimeout(() => void refreshEvents(), 2000)
      } catch (reason) {
        if (!stopped) setError(errorMessage(reason))
      }
    }
    timer = setTimeout(() => void refreshEvents(), 1000)
    return () => { stopped = true; clearTimeout(timer) }
  }, [selectedId, isRunning])

  async function saveSettings() {
    setBusy(true); setError(null); setNotice(null)
    try {
      const saved = await api.saveTwitchSettings(clientId.trim())
      setSettings(saved)
      setClientId(saved.clientId)
      setNotice('Client ID Twitch enregistré.')
    } catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(false) }
  }

  async function createBot() {
    setBusy(true); setError(null); setNotice(null)
    try {
      const bot = await api.createTwitchBot(newName.trim(), newChannel.trim())
      setBots(previous => [bot, ...previous])
      setSelectedId(bot.id)
      setDraft(bot)
      setSimulationResult(null)
      setNewName(''); setNewChannel('')
      setNotice('Bot créé avec la commande de démonstration !hello.')
    } catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(false) }
  }

  async function saveBot() {
    if (!selected) return
    setBusy(true); setError(null); setNotice(null)
    try {
      const bot = await api.updateTwitchBot(selected.id, name.trim(), channel.trim(), commands)
      setBots(previous => previous.map(item => item.id === bot.id ? bot : item))
      setDraft(bot)
      setSimulationResult(null)
      setProposals(await api.twitchCommandProposals(bot.id))
      setNotice('Configuration du bot enregistrée.')
    } catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(false) }
  }

  async function connect() {
    if (!selected) return
    setBusy(true); setError(null); setNotice(null)
    try {
      setAuthorization(await api.beginTwitchConnect(selected.id))
      setNotice('Autorisez l’application Twitch dans le navigateur, puis laissez cette fenêtre ouverte.')
    } catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(false) }
  }

  async function runAction(action: 'start' | 'stop') {
    if (!selected) return
    setBusy(true); setError(null); setNotice(null)
    try {
      const bot = action === 'start'
        ? await api.startTwitchBot(selected.id)
        : await api.stopTwitchBot(selected.id)
      setBots(previous => previous.map(item => item.id === bot.id ? bot : item))
      setDraft(bot)
      setNotice(action === 'start' ? 'Bot connecté au chat Twitch.' : 'Bot arrêté.')
    } catch (reason) {
      setError(errorMessage(reason))
      await load().catch(loadError => setError(errorMessage(loadError)))
    } finally { setBusy(false) }
  }

  async function testCommand() {
    if (!selected) return
    setError(null); setTestResult(null)
    try { setTestResult(await api.testTwitchCommand(selected.id, testInput)) }
    catch (reason) { setError(errorMessage(reason)) }
  }

  async function simulate() {
    if (!selected) return
    setBusy(true); setError(null); setSimulationResult(null)
    const first: TwitchSimulationEvent = { userId: 'simulated-viewer', role: simulationRole, text: simulationMessage, atSeconds: 0 }
    const events = repeatSimulation ? [first, { ...first }, { ...first, atSeconds: simulationDelay }] : [first]
    try { setSimulationResult(await api.simulateTwitch(selected.id, events)) }
    catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(false) }
  }

  async function generateProposal() {
    if (!selected) return
    setBusy(true); setError(null); setDeleteApproved(false)
    try {
      const proposal = await api.generateTwitchCommandProposal(selected.id, teamId, commandRequest)
      setProposals(previous => [proposal, ...previous])
    } catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(false) }
  }

  async function applyProposal(proposal: TwitchCommandProposal) {
    if (!selected) return
    setBusy(true); setError(null)
    try {
      await api.applyTwitchCommandProposal(selected.id, proposal.id, deleteApproved)
      const [nextBots, history] = await Promise.all([api.twitchBots(), api.twitchCommandProposals(selected.id)])
      setBots(nextBots); setProposals(history); setDeleteApproved(false)
      const updated = nextBots.find(bot => bot.id === selected.id)
      if (updated) setDraft(updated)
      setNotice('Configuration appliquée et scénarios locaux vérifiés.')
    } catch (reason) {
      setError(errorMessage(reason))
      try { setProposals(await api.twitchCommandProposals(selected.id)) }
      catch (historyError) { setError(`${errorMessage(reason)} — Historique : ${errorMessage(historyError)}`) }
    } finally { setBusy(false) }
  }

  async function restoreProposal(proposal: TwitchCommandProposal) {
    if (!selected) return
    setBusy(true); setError(null)
    try {
      await api.restoreTwitchCommandProposal(selected.id, proposal.id)
      const [nextBots, history] = await Promise.all([api.twitchBots(), api.twitchCommandProposals(selected.id)])
      setBots(nextBots); setProposals(history)
      const updated = nextBots.find(bot => bot.id === selected.id)
      if (updated) setDraft(updated)
      setNotice('Configuration précédente restaurée.')
    } catch (reason) { setError(errorMessage(reason)) }
    finally { setBusy(false) }
  }

  const updateCommand = (index: number, patch: Partial<TwitchCommand>) =>
    setCommands(previous => previous.map((command, i) => i === index ? { ...command, ...patch } : command))
  const stateLabel = (state: string) => ({
    Starting: 'Démarrage', Connected: 'Connecté', Reconnecting: 'Reconnexion',
    Stopping: 'Arrêt', Stopped: 'Déconnecté', Disconnected: 'Déconnecté', Error: 'Erreur',
  }[state] ?? state)

  return <div className="twitch-layout">
    {error && <div className="notice danger" role="alert">{error}</div>}
    {notice && <div className="notice success" role="status">{notice}</div>}
    <section className="panel twitch-settings">
      <div className="panel-heading"><h2><ShieldCheck size={16} /> Connexion OAuth Twitch</h2>
        <span className={`badge ${settings?.configured ? 'success' : 'warning'}`}>{settings?.configured ? 'Client ID configuré' : 'Configuration requise'}</span></div>
      <p>Dans <a href="https://dev.twitch.tv/console/apps" target="_blank" rel="noreferrer">Twitch Developer Console</a>, créez une application publique pour Device Code OAuth. Si le formulaire exige une URL de redirection, saisissez <code>http://localhost:3000</code> : ce flux ne l’utilise pas. Le Client ID est requis; ne générez ni ne saisissez aucun Client Secret. L’autorisation demande uniquement <code>user:read:chat</code> et <code>user:write:chat</code>.</p>
      <label>Client ID Twitch <input aria-label="Client ID Twitch" value={clientId}
        onChange={event => setClientId(event.target.value)} autoComplete="off" /></label>
      <button className="button primary" disabled={busy || !clientId.trim()} onClick={() => void saveSettings()}>
        {busy ? <LoaderCircle size={14} className="spin" /> : <Save size={14} />} Enregistrer le Client ID
      </button>
      <p className="muted">Les jetons OAuth sont conservés dans le Gestionnaire d’identifiants Windows; ils ne sont pas inclus dans les fichiers de configuration ni les journaux. Aucun bot ne démarre automatiquement.</p>
    </section>

    <div className="twitch-columns">
      <section className="panel twitch-bot-list">
        <div className="panel-heading"><h2><Bot size={16} /> Bots Twitch</h2><span className="count">{bots.length}</span></div>
        {bots.map(bot => <button key={bot.id} className={`twitch-bot-item ${bot.id === selectedId ? 'selected' : ''}`} disabled={busy}
          onClick={() => { setSelectedId(bot.id); setDraft(bot); setAuthorization(null); setTestResult(null); setSimulationResult(null) }}>
          <strong>{bot.name}</strong><small>#{bot.channel}</small>
          <span className={`badge ${bot.state === 'Connected' ? 'success' : bot.state === 'Error' ? 'warning' : 'quiet'}`}>
            <CircleDot size={11} /> {stateLabel(bot.state)}
          </span>
        </button>)}
        {!bots.length && <div className="empty compact"><Bot size={25} /><p>Aucun bot configuré.</p></div>}
        <form className="twitch-create" onSubmit={event => { event.preventDefault(); void createBot() }}>
          <h3>Créer un bot</h3>
          <label>Nom <input aria-label="Nom du nouveau bot" value={newName} onChange={event => setNewName(event.target.value)} maxLength={64} /></label>
          <label>Chaîne Twitch <input aria-label="Chaîne du nouveau bot" value={newChannel} onChange={event => setNewChannel(event.target.value)} placeholder="nomdechaine" /></label>
          <button className="button secondary full" disabled={busy || !newName.trim() || !newChannel.trim()} type="submit">
            <Plus size={14} /> Créer le bot
          </button>
        </form>
      </section>

      <section className="panel twitch-editor">
        {!selected ? <div className="empty compact"><Bot size={25} /><p>Sélectionnez ou créez un bot.</p></div> : <>
          <div className="panel-heading"><h2>{selected.name}</h2><span className={`badge ${selected.state === 'Connected' ? 'success' : selected.state === 'Error' ? 'warning' : 'quiet'}`}>
          <CircleDot size={11} /> {stateLabel(selected.state)}{selected.authorized && selected.accountLogin ? ` · @${selected.accountLogin}` : ''}
          </span></div>
          {selected.error && <div className="notice danger" role="alert">{selected.error}</div>}
          <div className="twitch-form">
            <label>Nom du bot <input aria-label="Nom du bot" value={name} onChange={event => setName(event.target.value)} maxLength={64} disabled={isRunning} /></label>
            <label>Chaîne Twitch <input aria-label="Chaîne Twitch" value={channel} onChange={event => setChannel(event.target.value)} disabled={isRunning} /></label>
          </div>
          <div className="panel-heading command-heading"><h3>Commandes de chat</h3>
            <button className="text-button" disabled={isRunning} onClick={() => setCommands(previous => [
              ...previous, { name: '!newcommand', response: 'Réponse du bot', cooldownSeconds: 5, enabled: true },
            ])}><Plus size={13} /> Ajouter</button>
          </div>
          {commands.map((command, index) => <div className="twitch-command" key={`${index}-${command.name}`}>
            <label>Commande<input aria-label={`Nom commande ${index + 1}`} value={command.name}
              onChange={event => updateCommand(index, { name: event.target.value })} disabled={isRunning} /></label>
            <label>Réponse<input aria-label={`Réponse commande ${index + 1}`} value={command.response}
              onChange={event => updateCommand(index, { response: event.target.value })} disabled={isRunning} /></label>
            <label className="cooldown">Cooldown (s)<input aria-label={`Cooldown commande ${index + 1}`} type="number" min={0} max={3600}
              value={command.cooldownSeconds} onChange={event => updateCommand(index, { cooldownSeconds: Number(event.target.value) })} disabled={isRunning} /></label>
            <label className="enabled"><input aria-label={`Activer commande ${index + 1}`} type="checkbox" checked={command.enabled}
              onChange={event => updateCommand(index, { enabled: event.target.checked })} disabled={isRunning} /> Active</label>
            <label className="enabled"><input aria-label={`Modérateurs uniquement commande ${index + 1}`} type="checkbox"
              checked={!!command.moderatorOnly} onChange={event => updateCommand(index, { moderatorOnly: event.target.checked })}
              disabled={isRunning} /> Modérateurs</label>
            <button className="icon-button" aria-label={`Supprimer commande ${index + 1}`}
              disabled={isRunning} onClick={() => setCommands(previous => previous.filter((_, i) => i !== index))}><Trash2 size={14} /></button>
          </div>)}
          <div className="twitch-actions">
            <button className="button secondary" onClick={() => void saveBot()} disabled={busy || isRunning || !commandsChanged}>
              <Save size={14} /> Enregistrer
            </button>
            {!selected.authorized
              ? <button className="button primary" disabled={busy || !settings?.configured || !!authorization} onClick={() => void connect()}>
                {busy ? <LoaderCircle size={14} className="spin" /> : <ShieldCheck size={14} />} Connecter Twitch
              </button>
                : isRunning
                  ? <button className="button secondary" disabled={busy || selected.state === 'Stopping'} onClick={() => void runAction('stop')}><Square size={14} /> Arrêter</button>
                  : <button className="button primary" disabled={busy || !!commandsChanged} onClick={() => void runAction('start')}>
                  {busy ? <LoaderCircle size={14} className="spin" /> : <Activity size={14} />} Démarrer
                </button>}
          </div>
          {authorization && <div className="notice warning twitch-auth" role="status">
            Ouvrez <a href={authorization.verificationUri} target="_blank" rel="noreferrer">la page d’autorisation Twitch</a>,
            saisissez le code <strong>{authorization.userCode}</strong> et gardez Studio ouvert pendant la vérification.
            Expiration dans {Math.ceil(authorization.expiresIn / 60)} min.
          </div>}
          <section className="twitch-local-test">
            <h3>Tester une commande localement</h3>
            <form onSubmit={event => { event.preventDefault(); void testCommand() }}>
              <input aria-label="Message de test" value={testInput} onChange={event => setTestInput(event.target.value)} />
              <button className="button secondary" type="submit"><Check size={14} /> Tester</button>
            </form>
            {testResult && <p role="status">{testResult.matched
              ? `Réponse : ${testResult.response}` : 'Aucune commande ne correspond.'}</p>}
          </section>
          <section className="twitch-local-test">
            <h3>Simulation locale — aucune connexion Twitch</h3>
            <p>Utilise la configuration enregistrée, sans toucher aux cooldowns du bot connecté.</p>
            <form onSubmit={event => { event.preventDefault(); void simulate() }}>
              <input aria-label="Message simulé" value={simulationMessage} maxLength={5000}
                onChange={event => setSimulationMessage(event.target.value)} />
              <select aria-label="Rôle simulé" value={simulationRole}
                onChange={event => setSimulationRole(event.target.value === 'Moderator' ? 'Moderator' : 'Viewer')}>
                <option value="Viewer">Spectateur</option><option value="Moderator">Modérateur</option>
              </select>
              <button className="button secondary" type="submit" disabled={busy || !simulationMessage.trim() || !!commandsChanged}>
                Simuler
              </button>
            </form>
            <label><input type="checkbox" checked={repeatSimulation} onChange={event => setRepeatSimulation(event.target.checked)} />
              Tester le cooldown : répéter immédiatement puis après</label>
            {repeatSimulation && <label> Secondes <input aria-label="Délai simulé en secondes" type="number" min={0} max={86400}
              value={simulationDelay} onChange={event => setSimulationDelay(Number(event.target.value))} /></label>}
            {commandsChanged && <p>Enregistrez les modifications avant la simulation.</p>}
            {simulationResult?.botId === selected.id && !commandsChanged && <div role="status">{simulationResult.steps.map((step, index) =>
              <p key={index}>À {step.event.atSeconds} s — {step.event.text} : {step.outcome === 'Responded'
                ? `Réponse : ${step.response}` : step.outcome === 'PermissionDenied' ? 'Refus : modérateur requis'
                  : step.outcome === 'Cooldown' ? `Cooldown : ${step.cooldownRemainingSeconds} s restantes`
                    : 'Aucune commande ne correspond.'}</p>)}</div>}
          </section>
          <section className="twitch-local-test">
            <h3>Proposer des commandes avec une équipe</h3>
            <p>Les réponses sont du texte fixe. Aucun code généré n’est exécuté ; les modifications exigent votre accord.
              Les simulations vérifient la configuration proposée, pas toutes les intentions de la demande.</p>
            <label>Équipe <select aria-label="Équipe pour les commandes Twitch" value={teamId}
              onChange={event => setTeamId(event.target.value)} disabled={busy}>
              {!teams.length && <option value="">Aucune équipe disponible</option>}
              {teams.map(team => <option key={team.id} value={team.id}>{team.name}</option>)}
            </select></label>
            <form onSubmit={event => { event.preventDefault(); void generateProposal() }}>
              <input aria-label="Demande de commandes" value={commandRequest}
                onChange={event => setCommandRequest(event.target.value)} maxLength={4000} />
              <button className="button secondary" type="submit"
                disabled={busy || !teamId || !commandRequest.trim() || !!commandsChanged}>Générer</button>
            </form>
            {proposals.map(proposal => <div key={proposal.id}>
              <p><strong>{proposal.request}</strong> — {proposal.status}</p>
              {proposal.operations.map(operation => <p key={operation.name}>
                {operation.action} {operation.name} : {JSON.stringify(operation)}
              </p>)}
              {proposal.operations.map(operation => <p key={`diff-${operation.name}`}>
                Avant : {JSON.stringify(proposal.before.find(command => command.name.toLowerCase() === operation.name.toLowerCase()) ?? null)}
                {' → Après : '}{JSON.stringify(proposal.after.find(command => command.name.toLowerCase() === operation.name.toLowerCase()) ?? null)}
              </p>)}
              {proposal.unsupported.map((item, index) => <p key={index}>Non pris en charge : {item}</p>)}
              {proposal.error && <p>Erreur : {proposal.error}</p>}
              {proposal.status === 'Outdated' && <p>Preuves périmées : la configuration a changé.</p>}
              {proposal.status === 'Interrupted' && <button className="button secondary"
                disabled={busy || isRunning || !!commandsChanged}
                onClick={() => void restoreProposal(proposal)}>Restaurer la configuration précédente</button>}
              {proposal.evidence.map((item, index) => <p key={index}>
                {proposal.status === 'Outdated' ? 'Périmé' : item.passed ? 'Vérifié' : 'Échoué'} — {item.command} : attendu {item.expected}, obtenu {item.actual}
              </p>)}
              {proposal.status === 'Proposed' && <>
                {proposal.operations.some(operation => operation.action === 'delete') &&
                  <label><input type="checkbox" checked={deleteApproved}
                    onChange={event => setDeleteApproved(event.target.checked)} /> Autoriser explicitement les suppressions</label>}
                <button className="button secondary" disabled={busy || isRunning || !!commandsChanged ||
                  (proposal.operations.some(operation => operation.action === 'delete') && !deleteApproved)}
                  onClick={() => void applyProposal(proposal)}>Approuver et appliquer</button>
              </>}
            </div>)}
          </section>
          <section className="twitch-events">
            <h3>Événements récents</h3>
            {!selected.events.length && <p className="muted">Aucun événement pour le moment.</p>}
            {[...selected.events].reverse().map((item, index) => <p key={`${item.timestamp}-${index}`}>
              <time>{new Date(item.timestamp).toLocaleTimeString('fr-FR')}</time> · {item.message}
            </p>)}
          </section>
        </>}
      </section>
    </div>
  </div>
}
