import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { MissionDetails } from './MissionDetails'
import { mission } from './test/fixtures'

describe('MissionDetails', () => {
  it('shows recorded creation and completion times and all execution events', () => {
    render(<MissionDetails mission={{
      ...mission, status: 'Completed', createdAt: '2026-11-11T16:00:00Z',
      completedAt: '2026-11-11T16:02:00Z',
      events: [
        { sequence: 1, timestamp: '2026-11-11T16:01:00Z', step: 'Generation', status: 'Succeeded', message: 'Première génération.' },
        { sequence: 2, timestamp: '2026-11-11T16:02:00Z', step: 'Generation', status: 'Succeeded', message: 'Correction terminée.' },
        { sequence: 3, timestamp: '2026-11-11T16:02:00Z', step: 'Mission', status: 'Succeeded', message: 'Conversation completed.' },
      ],
    }} cancel={vi.fn()} busy={false} refresh={async () => undefined} />)

    expect(screen.getByRole('heading', { name: /Créer un projet de test/ })).toBeVisible()
    expect(document.querySelector('.status-Completed')).toBeVisible()
    expect(screen.getByText('Durée totale')).toBeVisible()
    expect(screen.getByText('Première génération.')).toBeVisible()
    expect(screen.getByText('Correction terminée.')).toBeVisible()
  })

  it('does not display a completion time or duration when neither was recorded', () => {
    render(<MissionDetails mission={{ ...mission, status: 'Failed', completedAt: null, events: [
      { sequence: 1, timestamp: '2026-11-11T16:00:30Z', step: 'Build', status: 'Failed', message: 'Échec de compilation.' },
    ] }} cancel={vi.fn()} busy={false} refresh={async () => undefined} />)

    expect(screen.queryByText('Terminée')).not.toBeInTheDocument()
    expect(screen.queryByText('Durée totale')).not.toBeInTheDocument()
    expect(screen.getByText('Échec de compilation.')).toBeVisible()
  })
})
