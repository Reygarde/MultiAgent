using System.ComponentModel;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.API.Services;

public sealed class PreviewMaintenance(
    PreviewSessionManager sessions, IPreviewRuntime runtime, ILogger<PreviewMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!runtime.IsAvailable)
        {
            logger.LogInformation("Preview runtime unavailable; preview startup remains disabled.");
            return;
        }

        try
        {
            await sessions.RecoverAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                           PreviewUnavailableException or Win32Exception ||
                                           exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Preview orphan recovery failed; preview startup remains disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await sessions.ExpireAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                               PreviewUnavailableException or Win32Exception ||
                                               exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Preview expiration cleanup failed; preview startup remains disabled.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (runtime.IsAvailable)
            await sessions.StopAllAsync(cancellationToken);
    }
}
