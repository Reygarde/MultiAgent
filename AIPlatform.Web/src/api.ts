import type { Agent, AgentConfiguration, CreateMission, CustomAgent, CustomAgentBrowserScenario, CustomAgentBrowserTestReport, CustomAgentCollaborationRun, CustomAgentConversation, CustomAgentConversationSummary, CustomAgentDevelopmentPlan, CustomAgentDraft, CustomAgentProjectType, CustomAgentTeam, CustomAgentTeamMember, CustomAgentWorkspace, CustomAgentWorkspaceFile, ExecutionPlan, FileContent, LocalApplication, LocalModels, Mission, MissionCapacity, Model, ProjectAnalysis, ReviewerCorrectionResumeEligibility, RevisionComparison, Sandbox, TwitchBot, TwitchCommand, TwitchCommandProposal, TwitchCommandTestResult, TwitchConnectResult, TwitchDeviceAuthorization, TwitchSettings, TwitchSimulationEvent, TwitchSimulationResult, WorkspaceFile } from './types'
import { fetchApi } from './desktop/transport'
import type { PlannerAttemptDiagnostic } from './types'

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetchApi(path, init)
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
  decidePlanApproval: async (id: string, approved: boolean, fingerprint: string) => {
    const response = await fetchApi(`/missions/${encodeURIComponent(id)}/plan-approval`, {
      method: 'POST', body: JSON.stringify({ approved, fingerprint }),
    })
    if (!response.ok) throw new Error(`Décision sur le plan refusée (HTTP ${response.status}). Actualisez le plan avant de réessayer.`)
  },
  plannerDiagnostics: (id: string) =>
    request<PlannerAttemptDiagnostic[]>(`/missions/${encodeURIComponent(id)}/planner-diagnostics`, { cache: 'no-store' }),
  plannerRawResponse: (id: string, attempt: string) =>
    request<{ responseBody: string }>(
      `/missions/${encodeURIComponent(id)}/planner-diagnostics/${encodeURIComponent(attempt)}/raw`, { cache: 'no-store' }),
  deletePlannerDiagnostics: async (id: string) => {
    const response = await fetchApi(`/missions/${encodeURIComponent(id)}/planner-diagnostics`, { method: 'DELETE' })
    if (!response.ok) throw new Error(`Suppression des diagnostics refusée (HTTP ${response.status}).`)
  },
  configuration: () => request<AgentConfiguration>('/agents/configuration'),
  saveConfiguration: (configuration: AgentConfiguration) => request<AgentConfiguration>('/agents/configuration',
    { method: 'PUT', body: JSON.stringify(configuration) }),
  resume: async (id: string) => {
    const response = await fetchApi(`/missions/${encodeURIComponent(id)}/resume`, { method: 'POST' })
    if (!response.ok) throw new Error(`Reprise refusée (HTTP ${response.status}).`)
  },
  reviewerCorrectionResumeEligibility: (id: string) =>
    request<ReviewerCorrectionResumeEligibility>(
      `/missions/${encodeURIComponent(id)}/reviewer-correction-resume`, { cache: 'no-store' }),
  resumeReviewerCorrection: async (id: string) => {
    const response = await fetchApi(
      `/missions/${encodeURIComponent(id)}/reviewer-correction-resume`, { method: 'POST' })
    if (!response.ok) {
      let detail = `Reprise Reviewer refusée (HTTP ${response.status}).`
      try {
        const problem: { detail?: string; title?: string } = await response.json()
        detail = problem.detail || problem.title || detail
      } catch {
        // Keep the HTTP failure when a proxy does not return Problem Details.
      }
      throw new Error(detail)
    }
  },
  revalidate: (id: string) => request<ExecutionPlan>(`/missions/${encodeURIComponent(id)}/revalidate`,
    { method: 'POST' }),
  renameProject: (id: string, name: string, description: string) => request<Mission>(`/workspaces/${encodeURIComponent(id)}`,
    { method: 'PUT', body: JSON.stringify({ name, description }) }),
  deleteProject: async (id: string) => {
    const response = await fetchApi(`/workspaces/${encodeURIComponent(id)}`, { method: 'DELETE' })
    if (!response.ok) throw new Error(`Suppression refusée (HTTP ${response.status}). Le projet doit être inactif.`)
  },
  agents: (signal?: AbortSignal) => request<Agent[]>('/agents', { signal }),
  models: (signal?: AbortSignal) => request<Model[]>('/models', { signal }),
  customAgents: (signal?: AbortSignal) => request<CustomAgent[]>('/custom-agents', { signal }),
  customAgentModels: (signal?: AbortSignal) => request<string[]>('/custom-agents/models', { signal }),
  createCustomAgent: (draft: CustomAgentDraft) => request<CustomAgent>('/custom-agents',
    { method: 'POST', body: JSON.stringify(draft) }),
  updateCustomAgent: (id: string, draft: CustomAgentDraft) => request<CustomAgent>(
    `/custom-agents/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(draft) }),
  duplicateCustomAgent: (id: string) => request<CustomAgent>(
    `/custom-agents/${encodeURIComponent(id)}/duplicate`, { method: 'POST' }),
  deleteCustomAgent: async (id: string) => {
    const response = await fetchApi(`/custom-agents/${encodeURIComponent(id)}`, { method: 'DELETE' })
    if (!response.ok) throw new Error(`Suppression refusée (HTTP ${response.status}).`)
  },
  customAgentConversations: (id: string) =>
    request<CustomAgentConversationSummary[]>(`/custom-agents/${encodeURIComponent(id)}/conversations`),
  createCustomAgentConversation: (id: string) =>
    request<CustomAgentConversation>(`/custom-agents/${encodeURIComponent(id)}/conversations`, { method: 'POST' }),
  customAgentConversation: (agentId: string, conversationId: string) =>
    request<CustomAgentConversation>(
      `/custom-agents/${encodeURIComponent(agentId)}/conversations/${encodeURIComponent(conversationId)}`),
  setCustomAgentWorkspace: (
    agentId: string, conversationId: string, workspace: CustomAgentWorkspace | null,
  ) => request<CustomAgentConversation>(
    `/custom-agents/${encodeURIComponent(agentId)}/conversations/${encodeURIComponent(conversationId)}/workspace`,
    { method: 'POST', body: JSON.stringify({ workspace }) }),
  sendCustomAgentMessage: (
    agentId: string, conversationId: string, message: string, signal?: AbortSignal,
    externalFiles?: { path: string; content: string }[],
  ) =>
    request<CustomAgentConversation>(
      `/custom-agents/${encodeURIComponent(agentId)}/conversations/${encodeURIComponent(conversationId)}/messages`,
      { method: 'POST', body: JSON.stringify({ message, externalFiles }), signal }),
  decideCustomAgentProposal: (
    agentId: string, conversationId: string, proposalId: string, approved: boolean,
  ) => request<CustomAgentConversation>(
    `/custom-agents/${encodeURIComponent(agentId)}/conversations/${encodeURIComponent(conversationId)}/proposals/${encodeURIComponent(proposalId)}/decision`,
    { method: 'POST', body: JSON.stringify({ approved }) }),
  recordExternalProposalOutcome: (
    agentId: string, conversationId: string, proposalId: string, backupId: string, restored: boolean,
  ) => request<CustomAgentConversation>(
    `/custom-agents/${encodeURIComponent(agentId)}/conversations/${encodeURIComponent(conversationId)}/proposals/${encodeURIComponent(proposalId)}/native-outcome`,
    { method: 'POST', body: JSON.stringify({ backupId, restored }) }),
  deleteCustomAgentConversation: async (agentId: string, conversationId: string) => {
    const response = await fetchApi(
      `/custom-agents/${encodeURIComponent(agentId)}/conversations/${encodeURIComponent(conversationId)}`,
      { method: 'DELETE' })
    if (!response.ok) throw new Error(`Suppression de la conversation refusée (HTTP ${response.status}).`)
  },
  customAgentTeams: () => request<CustomAgentTeam[]>('/custom-agent-teams'),
  saveCustomAgentTeam: (name: string, members: CustomAgentTeamMember[], id?: string, modelEscalationEnabled = false, automaticModelSelection = false) =>
    request<CustomAgentTeam>(id ? `/custom-agent-teams/${encodeURIComponent(id)}` : '/custom-agent-teams',
      { method: id ? 'PUT' : 'POST', body: JSON.stringify({ name, members, modelEscalationEnabled, automaticModelSelection }) }),
  deleteCustomAgentTeam: async (id: string) => {
    const response = await fetchApi(`/custom-agent-teams/${encodeURIComponent(id)}`, { method: 'DELETE' })
    if (!response.ok) throw new Error(`Suppression de l’équipe refusée (HTTP ${response.status}).`)
  },
  createCustomAgentProject: (teamId: string, name: string, projectType: CustomAgentProjectType) =>
    request<Mission>(`/custom-agent-teams/${encodeURIComponent(teamId)}/projects`,
      { method: 'POST', body: JSON.stringify({ name, projectType }) }),
  localApplication: (workspaceId: string, signal?: AbortSignal) =>
    request<LocalApplication>(`/studio/projects/${encodeURIComponent(workspaceId)}/local-application`, { signal }),
  startLocalApplication: (workspaceId: string) =>
    request<LocalApplication>(`/studio/projects/${encodeURIComponent(workspaceId)}/local-application/start`,
      { method: 'POST', body: JSON.stringify({ executionAuthorized: true }) }),
  stopLocalApplication: (workspaceId: string) =>
    request<LocalApplication>(`/studio/projects/${encodeURIComponent(workspaceId)}/local-application/stop`,
      { method: 'POST' }),
  runStudioBrowserTests: (
    workspaceId: string, input: {
      executionAuthorized: boolean
      teamId?: string
      collaborationRunId?: string
      scenarios?: CustomAgentBrowserScenario[]
    }, signal?: AbortSignal,
  ) => request<CustomAgentBrowserTestReport>(
    `/studio/projects/${encodeURIComponent(workspaceId)}/browser-tests`,
    { method: 'POST', body: JSON.stringify(input), signal }),
  studioBrowserScreenshot: async (
    workspaceId: string, testId: string, scenarioId: string, signal?: AbortSignal,
  ) => {
    const response = await fetchApi(
      `/studio/projects/${encodeURIComponent(workspaceId)}/browser-tests/${encodeURIComponent(testId)}/scenarios/${encodeURIComponent(scenarioId)}/screenshot`,
      { signal })
    if (!response.ok) throw new Error(`Capture d’écran indisponible (HTTP ${response.status}).`)
    return URL.createObjectURL(await response.blob())
  },
  customAgentTeamRuns: (teamId: string, signal?: AbortSignal) =>
    request<CustomAgentCollaborationRun[]>(`/custom-agent-teams/${encodeURIComponent(teamId)}/runs`, { signal }),
  customAgentDevelopmentPlans: (teamId: string, signal?: AbortSignal) =>
    request<CustomAgentDevelopmentPlan[]>(
      `/custom-agent-teams/${encodeURIComponent(teamId)}/development-plans`, { signal }),
  createCustomAgentDevelopmentPlan: (
    teamId: string, goal: string, workspace: CustomAgentWorkspace,
  ) => request<CustomAgentDevelopmentPlan>(
    `/custom-agent-teams/${encodeURIComponent(teamId)}/development-plans`,
    { method: 'POST', body: JSON.stringify({ goal, workspace }) }),
  extendCustomAgentDevelopmentPlan: (teamId: string, planId: string, requestText: string) =>
    request<CustomAgentDevelopmentPlan>(
      `/custom-agent-teams/${encodeURIComponent(teamId)}/development-plans/${encodeURIComponent(planId)}/extend`,
      { method: 'POST', body: JSON.stringify({ request: requestText }) }),
  reviewDevelopmentCriterion: (
    teamId: string, planId: string, taskId: string, index: number,
    category: string, status: string, evidence: string | null,
    runId: string | null, reportId: string | null, scenarioId: string | null,
  ) => request<CustomAgentDevelopmentPlan>(
    `/custom-agent-teams/${encodeURIComponent(teamId)}/development-plans/${encodeURIComponent(planId)}/tasks/${encodeURIComponent(taskId)}/criteria/${index}`,
    { method: 'PUT', body: JSON.stringify({ category, status, evidence, runId, reportId, scenarioId }) }),
  startCustomAgentDevelopmentPlan: (teamId: string, planId: string) =>
    request<CustomAgentDevelopmentPlan>(
      `/custom-agent-teams/${encodeURIComponent(teamId)}/development-plans/${encodeURIComponent(planId)}/start`,
      { method: 'POST', body: JSON.stringify({ workspaceAuthorized: true, executionAuthorized: true }) }),
  pauseCustomAgentDevelopmentPlan: (teamId: string, planId: string) =>
    request<CustomAgentDevelopmentPlan>(
      `/custom-agent-teams/${encodeURIComponent(teamId)}/development-plans/${encodeURIComponent(planId)}/pause`,
      { method: 'POST' }),
  resumeCustomAgentDevelopmentPlan: (teamId: string, planId: string) =>
    request<CustomAgentDevelopmentPlan>(
      `/custom-agent-teams/${encodeURIComponent(teamId)}/development-plans/${encodeURIComponent(planId)}/resume`,
      { method: 'POST', body: JSON.stringify({ workspaceAuthorized: true, executionAuthorized: true }) }),
  cancelCustomAgentDevelopmentPlan: (teamId: string, planId: string) =>
    request<CustomAgentDevelopmentPlan>(
      `/custom-agent-teams/${encodeURIComponent(teamId)}/development-plans/${encodeURIComponent(planId)}/cancel`,
      { method: 'POST' }),
  retryCustomAgentDevelopmentTask: (teamId: string, planId: string, taskId: string) =>
    request<CustomAgentDevelopmentPlan>(
      `/custom-agent-teams/${encodeURIComponent(teamId)}/development-plans/${encodeURIComponent(planId)}/retry`,
      { method: 'POST', body: JSON.stringify({
        taskId, workspaceAuthorized: true, executionAuthorized: true,
      }) }),
  customAgentTeamRun: (teamId: string, runId: string, signal?: AbortSignal) =>
    request<CustomAgentCollaborationRun>(
      `/custom-agent-teams/${encodeURIComponent(teamId)}/runs/${encodeURIComponent(runId)}`, { signal }),
  startCustomAgentCollaboration: (
    teamId: string, task: string, workspace: CustomAgentWorkspace | null,
    externalFiles?: CustomAgentWorkspaceFile[], mode?: 'Supervised' | 'Autonomous',
    workspaceAuthorized?: boolean, executionAuthorized?: boolean, signal?: AbortSignal,
  ) => request<CustomAgentCollaborationRun>(`/custom-agent-teams/${encodeURIComponent(teamId)}/runs`,
    { method: 'POST', body: JSON.stringify({
      task, workspace, externalFiles, mode, workspaceAuthorized, executionAuthorized,
    }), signal }),
  cancelCustomAgentCollaboration: async (teamId: string, runId: string) => {
    const response = await fetchApi(
      `/custom-agent-teams/${encodeURIComponent(teamId)}/runs/${encodeURIComponent(runId)}/cancel`,
      { method: 'POST' })
    if (!response.ok) throw new Error(`Annulation refusée (HTTP ${response.status}).`)
  },
  decideCustomAgentCollaboration: (
    teamId: string, runId: string, approved: boolean,
  ) => request<CustomAgentCollaborationRun>(
    `/custom-agent-teams/${encodeURIComponent(teamId)}/runs/${encodeURIComponent(runId)}/decision`,
    { method: 'POST', body: JSON.stringify({ approved }) }),
  restoreAutonomousCustomAgentRun: (teamId: string, runId: string) =>
    request<CustomAgentCollaborationRun>(
      `/custom-agent-teams/${encodeURIComponent(teamId)}/runs/${encodeURIComponent(runId)}/restore`,
      { method: 'POST' }),
  recordNativeCollaborationOutcome: (
    teamId: string, runId: string, backupId: string, restored: boolean,
  ) => request<CustomAgentCollaborationRun>(
    `/custom-agent-teams/${encodeURIComponent(teamId)}/runs/${encodeURIComponent(runId)}/native-outcome`,
    { method: 'POST', body: JSON.stringify({ backupId, restored }) }),
  missions: (signal?: AbortSignal) => request<Mission[]>('/missions', { signal }),
  missionCapacity: (signal?: AbortSignal) => request<MissionCapacity>('/missions/capacity', { signal }),
  archiveMission: (id: string) => request<Mission>(`/missions/${encodeURIComponent(id)}/archive`, { method: 'POST' }),
  restoreMission: (id: string) => request<Mission>(`/missions/${encodeURIComponent(id)}/restore`, { method: 'POST' }),
  twitchSettings: () => request<TwitchSettings>('/twitch/settings'),
  saveTwitchSettings: (clientId: string) => request<TwitchSettings>('/twitch/settings',
    { method: 'PUT', body: JSON.stringify({ clientId }) }),
  twitchBots: () => request<TwitchBot[]>('/twitch/bots'),
  createTwitchBot: (name: string, channel: string) => request<TwitchBot>('/twitch/bots',
    { method: 'POST', body: JSON.stringify({ name, channel }) }),
  updateTwitchBot: (id: string, name: string, channel: string, commands: TwitchCommand[]) =>
    request<TwitchBot>(`/twitch/bots/${encodeURIComponent(id)}`,
      { method: 'PUT', body: JSON.stringify({ name, channel, commands }) }),
  deleteTwitchBot: async (id: string) => {
    const response = await fetchApi(`/twitch/bots/${encodeURIComponent(id)}`, { method: 'DELETE' })
    if (!response.ok) throw new Error(`Suppression refusée (HTTP ${response.status}). Arrêtez d’abord le bot.`)
  },
  beginTwitchConnect: (id: string) => request<TwitchDeviceAuthorization>(
    `/twitch/bots/${encodeURIComponent(id)}/connect`, { method: 'POST' }),
  pollTwitchConnect: (id: string) => request<TwitchConnectResult>(
    `/twitch/bots/${encodeURIComponent(id)}/connect/poll`, { method: 'POST' }),
  startTwitchBot: (id: string) => request<TwitchBot>(`/twitch/bots/${encodeURIComponent(id)}/start`, { method: 'POST' }),
  stopTwitchBot: (id: string) => request<TwitchBot>(`/twitch/bots/${encodeURIComponent(id)}/stop`, { method: 'POST' }),
  testTwitchCommand: (id: string, input: string) => request<TwitchCommandTestResult>(
    `/twitch/bots/${encodeURIComponent(id)}/commands/test`, { method: 'POST', body: JSON.stringify({ input }) }),
  simulateTwitch: (id: string, events: TwitchSimulationEvent[]) => request<TwitchSimulationResult>(
    `/twitch/bots/${encodeURIComponent(id)}/simulation`, { method: 'POST', body: JSON.stringify({ events }) }),
  twitchCommandProposals: (id: string) => request<TwitchCommandProposal[]>(
    `/twitch/bots/${encodeURIComponent(id)}/command-proposals`),
  generateTwitchCommandProposal: (id: string, teamId: string, requestText: string) => request<TwitchCommandProposal>(
    `/twitch/bots/${encodeURIComponent(id)}/command-proposals`,
    { method: 'POST', body: JSON.stringify({ teamId, request: requestText }) }),
  applyTwitchCommandProposal: (id: string, proposalId: string, deleteApproved: boolean) => request<TwitchCommandProposal>(
    `/twitch/bots/${encodeURIComponent(id)}/command-proposals/${encodeURIComponent(proposalId)}/apply`,
    { method: 'POST', body: JSON.stringify({ approved: true, deleteApproved }) }),
  restoreTwitchCommandProposal: (id: string, proposalId: string) => request<TwitchCommandProposal>(
    `/twitch/bots/${encodeURIComponent(id)}/command-proposals/${encodeURIComponent(proposalId)}/restore`,
    { method: 'POST', body: JSON.stringify({ approved: true }) }),
  localModels: (signal?: AbortSignal) => request<LocalModels>('/models/local', { signal }),
  sandbox: (signal?: AbortSignal) => request<Sandbox>('/sandbox', { signal }),
  create: (input: CreateMission) => request<Mission>('/missions', { method: 'POST', body: JSON.stringify(input) }),
  executionPlan: (id: string, signal?: AbortSignal) =>
    request<ExecutionPlan>(`/missions/${encodeURIComponent(id)}/execution-plan`, { signal }),
  revision: (id: string, version: number) =>
    request<RevisionComparison>(`/missions/${encodeURIComponent(id)}/revisions/${version}`),
  approve: async (id: string, approved: boolean, fingerprint: string) => {
    const response = await fetchApi(`/missions/${encodeURIComponent(id)}/approval`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ approved, fingerprint }),
    })
    if (!response.ok) throw new Error(`Décision refusée (HTTP ${response.status}). Rechargez le plan et l’état de la mission.`)
  },
  cancel: async (id: string) => {
    const response = await fetchApi(`/missions/${encodeURIComponent(id)}/cancel`, { method: 'POST' })
    if (!response.ok) throw new Error(`Annulation refusée (HTTP ${response.status}). Rafraîchissez l’état de la mission.`)
  },
  files: (id: string, signal?: AbortSignal) => request<WorkspaceFile[]>(`/workspaces/${encodeURIComponent(id)}/files`, { signal }),
  analysis: (id: string, signal?: AbortSignal) => request<ProjectAnalysis>(`/workspaces/${encodeURIComponent(id)}/analysis`, { signal }),
  exportProject: async (id: string) => {
    const response = await fetchApi(`/workspaces/${encodeURIComponent(id)}/export`)
    if (!response.ok) {
      const problem: { detail?: string; title?: string } = response.headers.get('content-type')?.includes('json')
        ? await response.json() : {}
      throw new Error(problem.detail || problem.title || `Export refusé (HTTP ${response.status}).`)
    }
    return response.blob()
  },
  file: (id: string, path: string, signal?: AbortSignal) =>
    request<FileContent>(`/workspaces/${encodeURIComponent(id)}/file?path=${encodeURIComponent(path)}`, { signal }),
}

export const errorMessage = (error: unknown) => error instanceof Error ? error.message : 'Une erreur inattendue est survenue.'
