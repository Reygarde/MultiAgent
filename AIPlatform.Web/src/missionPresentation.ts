import { isActive, type Mission, type MissionStatus } from './types'

export function missionTitle(mission: Mission): string {
  const projectName = mission.kind === 'Project' ? mission.projectName?.trim() : ''
  const title = projectName || mission.task.trim().replace(/\s+/g, ' ')
  if (!title) return 'Mission sans titre'
  return title.length > 110 ? `${title.slice(0, 107).trimEnd()}…` : title
}

export function formatDateTime(value?: string | null): string | null {
  if (!value) return null
  const date = new Date(value)
  return Number.isNaN(date.getTime())
    ? null
    : new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium', timeStyle: 'short' }).format(date)
}

export function formatTime(value?: string | null): string | null {
  if (!value) return null
  const date = new Date(value)
  return Number.isNaN(date.getTime())
    ? null
    : new Intl.DateTimeFormat('fr-FR', { hour: '2-digit', minute: '2-digit' }).format(date)
}

const terminalEventStatus: Partial<Record<MissionStatus, string[]>> = {
  Completed: ['Succeeded'],
  Verified: ['Succeeded'],
  GeneratedUnverified: ['Unavailable', 'Skipped'],
  Failed: ['Failed'],
  BuildFailed: ['Failed'],
  TestsFailed: ['Failed'],
  Cancelled: ['Cancelled'],
}

export function missionFinishedAt(mission: Mission): string | null {
  if (isActive(mission)) return null
  if (mission.completedAt && !Number.isNaN(new Date(mission.completedAt).getTime()))
    return mission.completedAt
  const acceptableStatuses = terminalEventStatus[mission.status] ?? []
  const event = [...mission.events].reverse().find(item =>
    item.step === 'Mission' && acceptableStatuses.includes(item.status) &&
    !/\b(?:archived|restored)\b/i.test(item.message))
  return event?.timestamp ?? null
}

export function formatMissionDuration(startedAt: string, finishedAt: string | null): string | null {
  if (!finishedAt) return null
  const milliseconds = new Date(finishedAt).getTime() - new Date(startedAt).getTime()
  if (!Number.isFinite(milliseconds) || milliseconds < 0) return null
  const seconds = Math.round(milliseconds / 1000)
  if (seconds < 1) return 'moins d’une seconde'
  if (seconds < 60) return `${seconds} s`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes} min${seconds % 60 ? ` ${seconds % 60} s` : ''}`
  const hours = Math.floor(minutes / 60)
  return `${hours} h${minutes % 60 ? ` ${minutes % 60} min` : ''}`
}

function localDayKey(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

export function groupMissionsByDay(missions: Mission[], now = new Date()) {
  const yesterday = new Date(now)
  yesterday.setDate(yesterday.getDate() - 1)
  const todayKey = localDayKey(now)
  const yesterdayKey = localDayKey(yesterday)
  const groups = new Map<string, { key: string; label: string; missions: Mission[] }>()
  for (const mission of missions) {
    const date = new Date(mission.createdAt)
    const key = Number.isNaN(date.getTime()) ? 'unknown' : localDayKey(date)
    let group = groups.get(key)
    if (!group) {
      const label = key === todayKey ? 'Aujourd’hui'
        : key === yesterdayKey ? 'Hier'
        : key === 'unknown' ? 'Date inconnue'
        : new Intl.DateTimeFormat('fr-FR', { dateStyle: 'long' }).format(date)
      group = { key, label, missions: [] }
      groups.set(key, group)
    }
    group.missions.push(mission)
  }
  return [...groups.values()]
}

export function missionSearchText(mission: Mission): string {
  return [
    missionTitle(mission), mission.task, mission.projectDescription, mission.response, mission.error,
    mission.workflow?.finalReport, mission.workflow?.review?.summary,
    ...mission.events.map(event => event.message),
  ].filter(Boolean).join(' ').toLocaleLowerCase('fr-FR')
}

export function projectVerificationLabel(mission: Mission): string {
  if (mission.status === 'Verified') return 'Compilation et tests réussis'
  if (mission.status === 'BuildFailed' || mission.status === 'TestsFailed' ||
      mission.verification.some(item => item.status === 'Failed')) return 'Échec de vérification'
  if (mission.status === 'GeneratedUnverified' ||
      mission.verification.some(item => item.status === 'Unavailable')) return 'Non vérifié'
  if (mission.status === 'AwaitingApproval') return 'Autorisation requise'
  if (isActive(mission)) return 'En cours'
  return mission.verification.length ? 'Vérification enregistrée' : 'Aucune vérification exécutée'
}
