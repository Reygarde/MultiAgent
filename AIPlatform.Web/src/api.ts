import type { Agent, CreateMission, FileContent, LocalModels, Mission, Model, Sandbox, WorkspaceFile } from './types'

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: { ...(init?.body ? { 'Content-Type': 'application/json' } : {}), ...init?.headers },
  })
  if (!response.ok) {
    let detail = `Erreur HTTP ${response.status}`
    try {
      const problem: { detail?: string; title?: string } = await response.json()
      detail = problem.detail || problem.title || detail
    } catch {
      // Some proxies return an HTML error; preserve the real HTTP failure.
    }
    throw new Error(detail)
  }
  const result: T = await response.json()
  return result
}

export const api = {
  agents: (signal?: AbortSignal) => request<Agent[]>('/agents', { signal }),
  models: (signal?: AbortSignal) => request<Model[]>('/models', { signal }),
  missions: (signal?: AbortSignal) => request<Mission[]>('/missions', { signal }),
  localModels: (signal?: AbortSignal) => request<LocalModels>('/models/local', { signal }),
  sandbox: (signal?: AbortSignal) => request<Sandbox>('/sandbox', { signal }),
  create: (input: CreateMission) => request<Mission>('/missions', { method: 'POST', body: JSON.stringify(input) }),
  cancel: async (id: string) => {
    const response = await fetch(`/api/missions/${encodeURIComponent(id)}/cancel`, { method: 'POST' })
    if (!response.ok) throw new Error(`Annulation refusée (HTTP ${response.status}). Rafraîchissez l’état de la mission.`)
  },
  files: (id: string, signal?: AbortSignal) => request<WorkspaceFile[]>(`/workspaces/${encodeURIComponent(id)}/files`, { signal }),
  file: (id: string, path: string, signal?: AbortSignal) =>
    request<FileContent>(`/workspaces/${encodeURIComponent(id)}/file?path=${encodeURIComponent(path)}`, { signal }),
}

export const errorMessage = (error: unknown) => error instanceof Error ? error.message : 'Une erreur inattendue est survenue.'
