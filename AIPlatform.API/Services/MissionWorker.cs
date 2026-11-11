using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.API.Services;

public sealed class MissionWorker(
    MissionStore store, MissionProcessor processor, MissionExecutionGate gate,
    StudioOptions options, ILogger<MissionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in store.ReadQueue(stoppingToken))
        {
            if (store.Get(id)?.IsTerminal != false)
                continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, store.Cancellation(id));
            timeout.CancelAfter(TimeSpan.FromSeconds(options.MissionTimeoutSeconds));
            var entered = false;
            try
            {
                await gate.EnterAsync(timeout.Token);
                entered = true;
                await processor.ProcessAsync(id, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                var userCancelled = store.Cancellation(id).IsCancellationRequested || stoppingToken.IsCancellationRequested;
                var message = userCancelled ? "Mission cancelled; no further steps will execute." :
                    "Mission exceeded its configured total time limit.";
                CloseActiveSteps(id, userCancelled ? StepStatus.Cancelled : StepStatus.Failed, message);
                store.Update(id, current => current with
                {
                    Status = userCancelled ? MissionStatus.Cancelled : MissionStatus.Failed,
                    Error = message
                });
                store.AddEvent(id, "Mission", userCancelled ? StepStatus.Cancelled : StepStatus.Failed, message);
                logger.LogInformation("Mission {MissionId}: {Message}", id, message);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Mission {MissionId} failed", id);
                var message = exception is AIPlatform.Core.LlmProviderException or StudioValidationException
                    ? exception.Message : "Mission processing failed. Consult the server logs.";
                CloseActiveSteps(id, StepStatus.Failed, message);
                store.Update(id, current => current with { Status = MissionStatus.Failed, Error = message });
                store.AddEvent(id, "Mission", StepStatus.Failed, message);
            }
            finally
            {
                if (entered)
                    gate.Exit();
            }
        }

    }

    private void CloseActiveSteps(Guid id, StepStatus status, string message)
    {
        var active = store.Get(id)!.Events.GroupBy(item => item.Step)
            .Select(group => group.Last()).Where(item => item.Status == StepStatus.Running).ToArray();
        foreach (var item in active)
            store.AddEvent(id, item.Step, status, message);
    }
}
