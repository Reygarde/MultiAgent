import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from './api'
import { TwitchBots } from './TwitchBots'
import type { TwitchBot } from './types'

vi.mock('./api', () => ({
  api: {
    twitchSettings: vi.fn(), twitchBots: vi.fn(), saveTwitchSettings: vi.fn(),
    createTwitchBot: vi.fn(), updateTwitchBot: vi.fn(), beginTwitchConnect: vi.fn(),
    pollTwitchConnect: vi.fn(), startTwitchBot: vi.fn(), stopTwitchBot: vi.fn(),
    testTwitchCommand: vi.fn(),
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
})
