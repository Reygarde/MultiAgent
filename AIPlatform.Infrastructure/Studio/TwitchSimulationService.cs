using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class TwitchSimulationService(ITwitchBotStore store) : ITwitchSimulationService
{
    public TwitchSimulationResult Simulate(Guid botId, IReadOnlyList<TwitchSimulationEvent> events,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bot = store.Get(botId) ??
            throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
        if (events is null || events.Count is < 1 or > 100)
            throw new StudioValidationException("A simulation requires between 1 and 100 chat messages.");

        var clock = new SimulationClock();
        var router = new TwitchCommandRouter(clock);
        var steps = new List<TwitchSimulationStep>(events.Count);
        var previousSeconds = 0;
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null || string.IsNullOrWhiteSpace(item.UserId) || item.UserId.Length > 64 ||
                item.UserId.Any(char.IsControl) || item.Role is not ("Viewer" or "Moderator") ||
                string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > 5000 ||
                item.AtSeconds < previousSeconds || item.AtSeconds > 86400)
                throw new StudioValidationException(
                    "Simulation messages require a user, Viewer or Moderator role, text and nondecreasing times from 0 to 86400 seconds.");
            previousSeconds = item.AtSeconds;
            clock.Seconds = item.AtSeconds;
            var result = router.Route(bot.Commands, item.Text, item.UserId, consumeCooldown: true,
                isModerator: item.Role == "Moderator");
            var outcome = result.PermissionDenied ? "PermissionDenied" :
                result.CooldownRemainingSeconds > 0 ? "Cooldown" : result.Matched ? "Responded" : "NoMatch";
            steps.Add(new(item, outcome, result.Response, result.CooldownRemainingSeconds));
        }
        return new(botId, steps);
    }

    private sealed class SimulationClock : TimeProvider
    {
        public int Seconds { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => (long)Seconds * TimestampFrequency;
    }
}
