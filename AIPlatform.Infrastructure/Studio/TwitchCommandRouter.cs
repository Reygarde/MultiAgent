using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class TwitchCommandRouter(TimeProvider time)
{
    private readonly object sync = new();
    private readonly Dictionary<(string Command, string Sender), long> lastUsed = [];

    public TwitchCommandTestResult Route(
        IReadOnlyList<TwitchCommand> commands, string input, string senderId, bool consumeCooldown)
    {
        var commandName = input.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (commandName is null)
            return new(false, null);
        var command = commands.FirstOrDefault(item =>
            item.Enabled && string.Equals(item.Name, commandName, StringComparison.OrdinalIgnoreCase));
        if (command is null)
            return new(false, null);

        lock (sync)
        {
            var key = (command.Name.ToLowerInvariant(), senderId);
            if (lastUsed.TryGetValue(key, out var previous) && command.CooldownSeconds > 0)
            {
                var remaining = command.CooldownSeconds - time.GetElapsedTime(previous).TotalSeconds;
                if (remaining > 0)
                    return new(false, null, (int)Math.Ceiling(remaining));
            }
            if (consumeCooldown && command.CooldownSeconds > 0)
                lastUsed[key] = time.GetTimestamp();
            return new(true, command.Response);
        }
    }
}
