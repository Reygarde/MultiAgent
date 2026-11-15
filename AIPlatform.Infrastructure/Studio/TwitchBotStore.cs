using System.Text.Json;
using System.Text.RegularExpressions;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed partial class TwitchBotStore : ITwitchBotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly string directory;
    private readonly string botsPath;
    private readonly string settingsPath;

    public TwitchBotStore(StudioOptions options)
    {
        directory = Path.Combine(SafePaths.ValidateStorageDirectory(options.DataDirectory), "twitch");
        SafePaths.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        SafePaths.RejectLinks(directory);
        botsPath = Path.Combine(directory, "bots.json");
        settingsPath = Path.Combine(directory, "settings.json");
    }

    public string GetClientId()
    {
        lock (sync)
        {
            if (!File.Exists(settingsPath))
                return "";
            SafePaths.RejectLinks(settingsPath);
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath), JsonOptions)
                ?? throw new InvalidDataException("Invalid Twitch settings.");
            return settings.ClientId;
        }
    }

    public void SetClientId(string clientId)
    {
        lock (sync)
            Write(settingsPath, JsonSerializer.Serialize(new Settings(clientId), JsonOptions));
    }

    public IReadOnlyList<TwitchBotConfiguration> List()
    {
        lock (sync)
            return ReadBots().OrderBy(bot => bot.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public TwitchBotConfiguration? Get(Guid id)
    {
        lock (sync)
            return ReadBots().SingleOrDefault(bot => bot.Id == id);
    }

    public void Save(TwitchBotConfiguration bot)
    {
        Validate(bot);
        lock (sync)
        {
            var bots = ReadBots();
            var index = bots.FindIndex(item => item.Id == bot.Id);
            if (index < 0)
                bots.Add(bot);
            else
                bots[index] = bot;
            Write(botsPath, JsonSerializer.Serialize(bots, JsonOptions));
        }
    }

    public bool Delete(Guid id)
    {
        lock (sync)
        {
            var bots = ReadBots();
            if (bots.RemoveAll(bot => bot.Id == id) == 0)
                return false;
            Write(botsPath, JsonSerializer.Serialize(bots, JsonOptions));
            return true;
        }
    }

    private List<TwitchBotConfiguration> ReadBots()
    {
        if (!File.Exists(botsPath))
            return [];
        SafePaths.RejectLinks(botsPath);
        var bots = JsonSerializer.Deserialize<List<TwitchBotConfiguration>>(File.ReadAllText(botsPath), JsonOptions)
            ?? throw new InvalidDataException("Invalid Twitch bot configuration.");
        if (bots.Select(bot => bot.Id).Distinct().Count() != bots.Count)
            throw new InvalidDataException("Twitch bot identifiers must be unique.");
        foreach (var bot in bots)
            Validate(bot);
        return bots;
    }

    private void Write(string path, string content)
    {
        var temporary = path + ".tmp";
        SafePaths.RejectLinks(path);
        SafePaths.RejectLinks(temporary);
        File.WriteAllText(temporary, content);
        SafePaths.RejectLinks(path);
        SafePaths.RejectLinks(temporary);
        File.Move(temporary, path, overwrite: true);
    }

    private static void Validate(TwitchBotConfiguration bot)
    {
        if (bot.Id == Guid.Empty || string.IsNullOrWhiteSpace(bot.Name) || bot.Name.Length > 64 ||
            bot.Name.Any(char.IsControl) || !ChannelLogin().IsMatch(bot.Channel) ||
            bot.Commands is null || bot.Commands.Count > 100)
            throw new StudioValidationException("A Twitch bot needs a name, a valid channel login and at most 100 commands.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in bot.Commands)
        {
            if (command is null || !CommandName().IsMatch(command.Name) || !names.Add(command.Name) ||
                string.IsNullOrWhiteSpace(command.Response) || command.Response.Length > 500 ||
                command.Response.Any(char.IsControl) || command.CooldownSeconds is < 0 or > 3600)
                throw new StudioValidationException("Commands need unique !names, one-line responses up to 500 characters and cooldowns from 0 to 3600 seconds.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,25}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ChannelLogin();

    [GeneratedRegex("^![A-Za-z0-9_]{1,32}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CommandName();

    private sealed record Settings(string ClientId);
}
