using AIPlatform.Core.Studio;

namespace AIPlatform.Core;

public sealed record MissionResult(
    string SelectedAgent, string SelectedModel, string Reason, string Response, bool UsedFallback);

public interface IMissionOrchestrator
{
    Task<MissionResult> RunAsync(string task, CancellationToken cancellationToken);
}

public sealed class MissionOrchestrator(
    IAgentRegistry registry,
    IAgentRouter router,
    ILLMProviderResolver providers,
    PlatformConfiguration configuration,
    MissionExecutionGate? executionGate = null) : IMissionOrchestrator, IDisposable
{
    private readonly MissionExecutionGate missions = executionGate ?? new(configuration.MaxConcurrentMissions);

    public async Task<MissionResult> RunAsync(string task, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task) || task.Length > configuration.MaxTaskLength)
            throw new ArgumentException("The mission is empty or exceeds the configured length limit.", nameof(task));

        await missions.EnterAsync(cancellationToken);
        try
        {
            var decision = await router.RouteAsync(task, cancellationToken);
            var model = registry.GetModel(decision.Agent.ModelId);
            var answer = await AgentAnswer.GenerateAsync(
                providers.Resolve(model.Provider), model, decision.Agent, task, cancellationToken);
            return new(decision.Agent.Id, model.Name, decision.Reason, answer, decision.UsedFallback);
        }
        finally
        {
            missions.Exit();
        }
    }

    public void Dispose()
    {
        if (executionGate is null)
            missions.Dispose();
    }
}
