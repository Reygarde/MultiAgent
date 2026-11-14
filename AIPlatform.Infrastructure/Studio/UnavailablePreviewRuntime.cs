using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class UnavailablePreviewRuntime : IPreviewRuntime
{
    public bool IsAvailable => false;

    public Task<PreviewPlan> CreatePlanAsync(PreviewSourceIdentity sources, CancellationToken cancellationToken) =>
        throw Unavailable();

    public Task<PreviewReadiness> StartAsync(Guid sessionId, PreviewPlan plan,
        PreviewSourceSnapshot snapshot, CancellationToken cancellationToken) => throw Unavailable();

    public Task StopAsync(Guid sessionId, CancellationToken cancellationToken) => throw Unavailable();

    public Task CleanupOrphansAsync(CancellationToken cancellationToken) => throw Unavailable();

    private static PreviewUnavailableException Unavailable() =>
        new("The isolated preview runtime and proxy are not implemented.");
}
