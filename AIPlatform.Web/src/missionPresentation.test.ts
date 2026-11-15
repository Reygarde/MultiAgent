import { describe, expect, it } from 'vitest'
import type { Mission } from './types'
import { formatMissionDuration, groupMissionsByDay, missionFinishedAt, missionTitle, projectVerificationLabel } from './missionPresentation'
import { mission } from './test/fixtures'

describe('mission presentation', () => {
  it('uses the project name when available and otherwise summarizes the original request', () => {
    expect(missionTitle({ ...mission, projectName: '  Ma bibliothèque  ' })).toBe('Ma bibliothèque')
    expect(missionTitle(mission)).toBe(mission.task)
  })

  it('groups missions by their local creation day', () => {
    const now = new Date(2026, 10, 15, 12)
    const create = (id: string, date: Date): Mission => ({ ...mission, id, createdAt: date.toISOString() })
    const groups = groupMissionsByDay([
      create('today', new Date(2026, 10, 15, 9)),
      create('yesterday', new Date(2026, 10, 14, 23)),
      create('older', new Date(2026, 10, 10, 9)),
    ], now)
    expect(groups.map(group => group.label)).toEqual([
      'Aujourd’hui', 'Hier', new Intl.DateTimeFormat('fr-FR', { dateStyle: 'long' }).format(new Date(2026, 10, 10)),
    ])
  })

  it('shows only a recorded finish time and excludes later archive events', () => {
    expect(missionFinishedAt({
      ...mission, status: 'Completed', completedAt: '2026-11-11T16:00:45Z', events: [],
    })).toBe('2026-11-11T16:00:45Z')
    const missionWithArchive = {
      ...mission, status: 'Completed' as const, completedAt: null, archived: true,
      events: [
        { sequence: 1, timestamp: '2026-11-11T16:01:00Z', step: 'Mission', status: 'Succeeded' as const, message: 'Conversation completed.' },
        { sequence: 2, timestamp: '2026-11-11T16:05:00Z', step: 'Mission', status: 'Succeeded' as const, message: 'Mission archived by explicit user request.' },
      ],
    }
    expect(missionFinishedAt(missionWithArchive)).toBe('2026-11-11T16:01:00Z')
    expect(formatMissionDuration(missionWithArchive.createdAt, missionFinishedAt(missionWithArchive))).toBe('1 min')
    expect(missionFinishedAt({ ...mission, status: 'Cancelled', events: [] })).toBeNull()
    expect(formatMissionDuration(mission.createdAt, null)).toBeNull()
  })

  it('distinguishes actual project verification results', () => {
    expect(projectVerificationLabel(mission)).toBe('Non vérifié')
    expect(projectVerificationLabel({ ...mission, status: 'Verified' })).toBe('Compilation et tests réussis')
    expect(projectVerificationLabel({ ...mission, status: 'BuildFailed' })).toBe('Échec de vérification')
  })
})
