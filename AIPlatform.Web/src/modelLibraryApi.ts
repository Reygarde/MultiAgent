import { request } from './api'
import { fetchApi } from './desktop/transport'

export interface ModelCompatibility {
  status: 'Compatible' | 'Borderline' | 'Insufficient' | 'Unknown'
  reason: string
  contextTokens: number
  weightsBytes: number | null
  kvBytes: number | null
  buffersBytes: number | null
  requiredRamBytes: number | null
  requiredVramBytes: number | null
  placement: string
}
export interface ModelRoutingDecision {
  model: string
  category: string
  reason: string
  confidence: string
  alternatives: { model: string; score: number; reason: string; compatibility: ModelCompatibility }[]
}
export interface ModelLibrarySnapshot {
  availableSpecializations: string[]
  maximumContextTokens: number
  hardware: {
    totalRamBytes: number | null
    availableRamBytes: number | null
    freeDiskBytes: number | null
    gpus: { name: string; totalBytes: number; availableBytes: number }[]
    warnings: string[]
  }
  models: LibraryModel[]
  loaded: { name: string; size: number; sizeVram: number; contextLength: number | null }[]
  ollamaError: string | null
}
export interface LibraryModel {
  routingProfile?: ModelRoutingProfile | null
  catalogue: {
    name: string; family: string; publisher: string | null; specializations: string[]
    source: string; downloadBytes: number | null; documentedContext: number | null
    license: string | null; openness: string
  }
  installed: boolean | null
  metadata: {
    family: string | null; parameters: string | null; quantization: string | null; installedBytes: number | null
    contextLength: number | null; architecture: string | null; license: string | null; completion: boolean
  } | null
  loaded: { name: string; size: number; sizeVram: number; contextLength?: number | null } | null
  compatibility: ModelCompatibility
  dependencies: { kind: string; name: string; active: boolean }[]
}
export interface ModelRoutingProfile {
  name: string
  specializations: string[] | null
  qualification: {
    digest: string; contextTokens: number; sizeBytes: number; sizeVramBytes: number; observedAt: string
  } | null
}
export interface ModelDownload {
  id: string; model: string; status: string; progress: string
  total: number | null; completed: number | null; updatedAt: string; error: string | null
}

async function command(path: string, body?: unknown) {
  const response = await fetchApi(`/model-library/${path}`, { method: 'POST',
    ...(body === undefined ? {} : { body: JSON.stringify(body) }) })
  if (!response.ok) {
    const text = await response.text()
    let detail = `Opération refusée (HTTP ${response.status}).`
    try { const problem: { detail?: string } = JSON.parse(text); detail = problem.detail || detail }
    catch { /* Preserve HTTP failure when the response is not a problem document. */ }
    throw new Error(detail)
  }
}
export const modelLibraryApi = {
  snapshot: (context: number, signal?: AbortSignal) =>
    request<ModelLibrarySnapshot>(`/model-library/?context=${context}`, { signal }),
  downloads: (signal?: AbortSignal) => request<ModelDownload[]>('/model-library/downloads', { signal }),
  verify: (name: string) => request<{ name: string; downloadBytes: number }>('/model-library/verify',
    { method: 'POST', body: JSON.stringify({ name }) }),
  install: (name: string) => request<ModelDownload>('/model-library/downloads',
    { method: 'POST', body: JSON.stringify({ name, confirmed: true }) }),
  cancel: (id: string) => command(`downloads/${encodeURIComponent(id)}/cancel`),
  remove: (name: string) => command('delete', { name, confirmed: true }),
  preview: (task: string, role: string, contextTokens: number) => request<ModelRoutingDecision>('/model-library/routing/preview',
    { method: 'POST', body: JSON.stringify({ task, role, contextTokens }) }),
  resetStatistics: () => command('routing/statistics/reset', { confirmed: true }),
  saveSpecializations: (name: string, specializations: string[] | null) =>
    request<ModelRoutingProfile>('/model-library/specializations',
      { method: 'PUT', body: JSON.stringify({ name, specializations }) }),
  qualify: (name: string, contextTokens: number) => request<ModelRoutingProfile>('/model-library/qualification',
    { method: 'POST', body: JSON.stringify({ name, contextTokens, confirmed: true }) }),
  clearQualification: (name: string) => command('qualification/clear', { name, confirmed: true }),
}

