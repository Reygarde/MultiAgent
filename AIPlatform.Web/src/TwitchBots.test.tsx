import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from './api'
import { TwitchBots } from './TwitchBots'
import type { TwitchBot, TwitchCommandProposal } from './types'

vi.mock('./api', () => ({
  api: {
    twitchSettings: vi.fn(), twitchBots: vi.fn(), saveTwitchSettings: vi.fn(),
    createTwitchBot: vi.fn(), updateTwitchBot: vi.fn(), beginTwitchConnect: vi.fn(),
    pollTwitchConnect: vi.fn(), startTwitchBot: vi.fn(), stopTwitchBot: vi.fn(),
    testTwitchCommand: vi.fn(), simulateTwitch: vi.fn(), customAgentTeams: vi.fn(),
    twitchCommandProposals: vi.fn(), generateTwitchCommandProposal: vi.fn(), applyTwitchCommandProposal: vi.fn(),
    restoreTwitchCommandProposal: vi.fn(),
  },
  errorMessage: (error: unknown) => error instanceof Error ? error.message : 'Error',
}))

const bot: TwitchBot = {
  id: '11111111-1111-1111-1111-111111111111',
  name: 'Chat Bot',
  channel: 'samplechannel',
  state: 'Stopped',
  authorized: false,
  accountLogin: null,
  error: null,
  commands: [{ name: '!hello', response: 'Salut le chat !', cooldownSeconds: 5, enabled: true }],
  events: [],
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.twitchSettings).mockResolvedValue({ clientId: '', configured: false })
  vi.mocked(api.twitchBots).mockResolvedValue([])
  vi.mocked(api.customAgentTeams).mockResolvedValue([])
  vi.mocked(api.twitchCommandProposals).mockResolvedValue([])
})

describe('Twitch bot controls', () => {
  it('creates a persistent bot with !hello and tests its command locally', async () => {
    vi.mocked(api.createTwitchBot).mockResolvedValue(bot)
    vi.mocked(api.updateTwitchBot).mockResolvedValue(bot)
    vi.mocked(api.testTwitchCommand).mockResolvedValue({
      matched: true, response: 'Salut le chat !', cooldownRemainingSeconds: 0,
    })
    render(<TwitchBots />)
    await userEvent.type(await screen.findByRole('textbox', { name: 'Nom du nouveau bot' }), 'Chat Bot')
    await userEvent.type(screen.getByRole('textbox', { name: 'Chaîne du nouveau bot' }), 'samplechannel')
    await userEvent.click(screen.getByRole('button', { name: 'Créer le bot' }))

    expect(await screen.findByRole('textbox', { name: 'Nom commande 1' })).toHaveValue('!hello')
    await userEvent.click(screen.getByRole('button', { name: 'Tester' }))
    await waitFor(() => expect(api.testTwitchCommand).toHaveBeenCalledWith(bot.id, '!hello'))
    expect(await screen.findByText('Réponse : Salut le chat !')).toBeVisible()
    const response = screen.getByRole('textbox', { name: 'Réponse commande 1' })
    await userEvent.clear(response)
    await userEvent.type(response, 'Bonjour !')
    await userEvent.click(screen.getByRole('button', { name: 'Enregistrer' }))
    await waitFor(() => expect(api.updateTwitchBot).toHaveBeenCalledWith(bot.id, 'Chat Bot',
      'samplechannel', [{ name: '!hello', response: 'Bonjour !', cooldownSeconds: 5, enabled: true }]))
    expect(screen.getByRole('button', { name: 'Connecter Twitch' })).toBeDisabled()
  })

  it('uses the explicit Device Code flow and never starts the bot automatically', async () => {
    vi.mocked(api.twitchSettings).mockResolvedValue({ clientId: 'abcdefghijklmnopqrstuvwxyz1234', configured: true })
    vi.mocked(api.twitchBots).mockResolvedValue([bot])
    vi.mocked(api.beginTwitchConnect).mockResolvedValue({
      verificationUri: 'https://www.twitch.tv/activate', userCode: 'ABCD-1234', expiresIn: 600, interval: 30,
    })
    render(<TwitchBots />)
    await userEvent.click(await screen.findByRole('button', { name: 'Connecter Twitch' }))
    expect(await screen.findByText('ABCD-1234')).toBeVisible()
    expect(screen.getByRole('link', { name: 'la page d’autorisation Twitch' })).toHaveAttribute(
      'href', 'https://www.twitch.tv/activate')
    expect(api.startTwitchBot).not.toHaveBeenCalled()
    expect(screen.queryByLabelText(/token|secret/i)).not.toBeInTheDocument()
  })

  it('simulates a moderator and a cooldown offline using saved configuration', async () => {
    vi.mocked(api.twitchBots).mockResolvedValue([bot])
    const event = { userId: 'simulated-viewer', role: 'Moderator' as const, text: '!hello', atSeconds: 0 }
    vi.mocked(api.simulateTwitch).mockResolvedValue({
      botId: bot.id, steps: [
        { event, outcome: 'Responded', response: 'Salut le chat !', cooldownRemainingSeconds: 0 },
        { event, outcome: 'Cooldown', response: null, cooldownRemainingSeconds: 5 },
        { event: { ...event, atSeconds: 10 }, outcome: 'Responded', response: 'Salut le chat !', cooldownRemainingSeconds: 0 },
      ],
    })
    render(<TwitchBots />)
    expect(await screen.findByText('Simulation locale — aucune connexion Twitch')).toBeVisible()
    await userEvent.selectOptions(screen.getByLabelText('Rôle simulé'), 'Moderator')
    await userEvent.click(screen.getByLabelText(/Tester le cooldown/))
    await userEvent.click(screen.getByRole('button', { name: 'Simuler' }))
    await waitFor(() => expect(api.simulateTwitch).toHaveBeenCalledWith(bot.id,
      [event, event, { ...event, atSeconds: 10 }]))
    expect(await screen.findByText(/Cooldown : 5 s restantes/)).toBeVisible()
    expect(api.startTwitchBot).not.toHaveBeenCalled()
    expect(api.beginTwitchConnect).not.toHaveBeenCalled()
  })

  it('shows a proposal for review before explicitly applying it', async () => {
    vi.mocked(api.twitchBots).mockResolvedValue([bot])
    vi.mocked(api.customAgentTeams).mockResolvedValue([{
      id: 'team', name: 'Équipe locale', members: [], createdAt: '', updatedAt: '',
    }])
    const changed = { ...bot.commands[0], response: 'Bonjour !' }
    const proposal: TwitchCommandProposal = {
      id: 'proposal', botId: bot.id, teamId: 'team', createdAt: '', request: 'Change !hello',
      status: 'Proposed', unsupported: [], before: bot.commands, after: [changed], evidence: [], error: null,
      operations: [{ action: 'update', name: '!hello', response: 'Bonjour !', cooldownSeconds: null,
        enabled: null, moderatorOnly: null }],
    }
    const applied = { ...proposal, status: 'Applied',
      evidence: [{ command: '!hello', criterion: 'Viewer t=0', expected: 'Responded: Bonjour !',
        actual: 'Responded: Bonjour !', passed: true }] }
    vi.mocked(api.generateTwitchCommandProposal).mockResolvedValue(proposal)
    vi.mocked(api.applyTwitchCommandProposal).mockResolvedValue(applied)
    render(<TwitchBots />)
    await userEvent.type(await screen.findByLabelText('Demande de commandes'), 'Change !hello')
    await userEvent.click(screen.getByRole('button', { name: 'Générer' }))
    expect(await screen.findByText(/update !hello/)).toBeVisible()
    expect(api.applyTwitchCommandProposal).not.toHaveBeenCalled()
    vi.mocked(api.twitchCommandProposals).mockResolvedValue([applied])
    vi.mocked(api.twitchBots).mockResolvedValue([{ ...bot, commands: [changed] }])
    await userEvent.click(screen.getByRole('button', { name: 'Approuver et appliquer' }))
    await waitFor(() => expect(api.applyTwitchCommandProposal).toHaveBeenCalledWith(bot.id, 'proposal', false))
    expect(await screen.findByText(/Vérifié — !hello/)).toBeVisible()
  })
})
