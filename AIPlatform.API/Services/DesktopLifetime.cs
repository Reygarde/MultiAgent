using System.Text;

namespace AIPlatform.API.Services;

public sealed class DesktopLifetime(IHostApplicationLifetime lifetime, ILogger<DesktopLifetime> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(stoppingToken);
                if (line is null or "shutdown")
                {
                    logger.LogInformation("Desktop owner disconnected or requested shutdown.");
                    lifetime.StopApplication();
                    return;
                }
                logger.LogWarning("Ignored an unsupported desktop control message.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels the private owner pipe read.
        }
    }
}
