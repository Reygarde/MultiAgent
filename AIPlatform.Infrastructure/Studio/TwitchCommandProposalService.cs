using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed partial class TwitchCommandProposalService(
    ITwitchBotStore bots,
    TwitchBotService botService,
    ITwitchSimulationService simulation,
    ICustomAgentStore agents,
    IAgentRegistry registry,
    IOllamaModelCatalog models,
    ILLMProvider provider,
    StudioOptions options,
    TimeProvider time) : ITwitchCommandProposalService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["unsupported","operations"],
        "properties":{"unsupported":{"type":"array","items":{"type":"string"}},
        "operations":{"type":"array","items":{"type":"object","additionalProperties":false,
        "required":["action","name","response","cooldownSeconds","enabled","moderatorOnly"],
        "properties":{"action":{"type":"string","enum":["add","update","disable","delete"]},
        "name":{"type":"string"},"response":{"type":["string","null"]},
        "cooldownSeconds":{"type":["integer","null"]},"enabled":{"type":["boolean","null"]},
        "moderatorOnly":{"type":["boolean","null"]}}}}}}
        """).RootElement.Clone();
    private static readonly JsonElement PlanSchema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["summary","unsupported"],
        "properties":{"summary":{"type":"string","maxLength":300},"unsupported":{"type":"array","maxItems":3,
        "items":{"type":"string","maxLength":160}}}}
        """).RootElement.Clone();
    private readonly object sync = new();
    private readonly string directory = Path.Combine(SafePaths.ValidateStorageDirectory(options.DataDirectory),
        "twitch", "command-proposals");

    public async Task<TwitchCommandProposal> GenerateAsync(Guid botId, Guid teamId, string request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request) || request.Length > CustomAgentLimits.MaxUserMessageCharacters ||
            ProjectExport.ContainsPotentialSecret(request))
            throw new StudioValidationException("The request is empty, too long, or may contain credentials.");
        var before = bots.Get(botId) ??
            throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
        if (ProjectExport.ContainsPotentialSecret(JsonSerializer.Serialize(before.Commands, Json)))
            throw new StudioValidationException("The bot configuration may contain credentials and cannot be sent to a model.");
        var team = agents.GetTeam(teamId) ?? throw new StudioValidationException("Select an existing agent team.");
        var planner = Agent(team, "Planner");
        var developer = Agent(team, "Developer");
        var installed = await models.GetInstalledModelsAsync(cancellationToken);
        if (!installed.Contains(planner.ModelName, StringComparer.Ordinal) ||
            !installed.Contains(developer.ModelName, StringComparer.Ordinal))
            throw new CustomAgentModelUnavailableException();
        var id = Guid.NewGuid();
        var createdAt = time.GetUtcNow();
        try
        {
            var input = JsonSerializer.Serialize(new { request, commands = before.Commands }, Json);
            var plan = await Complete(planner,
                "Return a short JSON summary of only the requested changes to static Twitch chat commands. " +
                "The existing router already enforces moderatorOnly (viewers denied, moderators allowed) " +
                "and cooldownSeconds; both are supported configuration fields and require no new code. " +
                "The bot cannot execute code, evaluate expressions, or produce random numbers. " +
                "List only genuinely unsupported requirements. Do not invent additional requirements.",
                input, PlanSchema, cancellationToken);
            if (plan.Length > 4000 || ProjectExport.ContainsPotentialSecret(plan))
                throw new StudioValidationException("The planner returned an unsafe or oversized response.");

            string? diagnostic = null;
            for (var attempt = 0; attempt <= options.MaxCorrectionAttempts; attempt++)
            {
                var raw = await Complete(developer,
                    "Output only JSON matching the schema. Modify only command names explicitly mentioned in the request. " +
                    "Use update for existing commands and add for new commands. Null means leave unchanged for update. " +
                    "Do not invent cooldowns: use null unless the request explicitly specifies a duration. " +
                    "New commands default to a 5-second cooldown. " +
                    "Commands are static text only: never generate code, scripts, expressions or random behavior. " +
                    "The existing router already enforces moderatorOnly and cooldownSeconds; these are fully supported. " +
                    "Cooldowns, permissions and static responses are supported. Set unsupported to [] when all " +
                    "requirements are supported; never list a supported requirement as unsupported. " +
                    "Do not delete unless explicitly requested.",
                    JsonSerializer.Serialize(new { request, commands = before.Commands, plan, diagnostic }, Json),
                    Schema, cancellationToken);
                try
                {
                    var parsed = Parse(raw);
                    var after = ApplyOperations(before, request, parsed.Operations);
                    var unsupported = parsed.Unsupported.ToList();
                    if (RandomRequirement().IsMatch(request))
                        unsupported.Add("Random responses are not supported by configured Twitch commands.");
                    var proposal = new TwitchCommandProposal(id, botId, teamId, createdAt, request,
                        unsupported.Count == 0 ? "Proposed" : "Unsupported", parsed.Operations,
                        unsupported, before, after, []);
                    lock (sync) Write(proposal);
                    return proposal;
                }
                catch (StudioValidationException error)
                {
                    diagnostic = error.Message;
                }
            }
            throw new StudioValidationException("The model could not produce a valid proposal: " + diagnostic);
        }
        catch (Exception error) when (error is StudioValidationException or LlmProviderException or OperationCanceledException)
        {
            var message = error switch
            {
                StudioValidationException => error.Message,
                OperationCanceledException => "Generation cancelled.",
                _ => "The local model did not complete the proposal."
            };
            lock (sync) Write(new(id, botId, teamId, createdAt, request, "Failed", [], [], before, before, [], message));
            throw;
        }
    }

    public TwitchCommandProposal Apply(Guid botId, Guid proposalId, bool approved, bool deleteApproved,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!approved)
            throw new StudioValidationException("Explicit approval of the bot configuration changes is required.");
        lock (sync)
        {
            var proposal = Read(proposalId);
            if (proposal.Id != proposalId || proposal.BotId != botId || proposal.Before.Id != botId ||
                proposal.After.Id != botId || proposal.Status != "Proposed" || proposal.Unsupported.Count != 0 ||
                RandomRequirement().IsMatch(proposal.Request))
                throw new StudioValidationException("This proposal is not available for application.");
            var validated = ApplyOperations(proposal.Before, proposal.Request, proposal.Operations);
            if (!TwitchBotService.SameConfiguration(validated, proposal.After))
                throw new StudioValidationException("The saved proposal no longer matches its operations.");
            if (proposal.Operations.Any(operation => operation.Action == "delete") && !deleteApproved)
                throw new StudioValidationException("Deleting commands requires separate explicit approval.");
            var applying = proposal with { Status = "Applying" };
            Write(applying);
            try
            {
                return botService.ApplyIfUnchanged(proposal.Before, proposal.After, () =>
                {
                    var evidence = ValidateSimulation(proposal, cancellationToken);
                    var result = proposal with { Status = "Applied", Evidence = evidence };
                    Write(result);
                    return result;
                });
            }
            catch (SimulationAssertionException error)
            {
                Write(proposal with { Status = "Failed", Error = error.Message, Evidence = error.Evidence });
                throw new StudioValidationException(error.Message);
            }
            catch (Exception error) when (error is StudioValidationException or TwitchIntegrationException or OperationCanceledException)
            {
                Write(proposal with { Status = "Failed", Error = error is OperationCanceledException
                    ? "The operation was cancelled." : error.Message });
                throw;
            }
        }
    }

    public IReadOnlyList<TwitchCommandProposal> List(Guid botId)
    {
        lock (sync)
        {
            if (bots.Get(botId) is not { } current)
                throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
            EnsureDirectory();
            return Directory.EnumerateFiles(directory, "*.json").Select(ReadPath)
                .Where(proposal => proposal.BotId == botId)
                .OrderByDescending(proposal => proposal.CreatedAt)
                .Select(proposal => proposal.Status == "Applied" && !TwitchBotService.SameConfiguration(current, proposal.After)
                    ? proposal with { Status = "Outdated" }
                    : proposal.Status == "Applying" ? proposal with { Status = "Interrupted" } : proposal)
                .ToArray();
        }
    }

    public TwitchCommandProposal RestoreInterrupted(Guid botId, Guid proposalId, bool approved,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!approved)
            throw new StudioValidationException("Explicit approval is required to restore the bot configuration.");
        lock (sync)
        {
            var proposal = Read(proposalId);
            if (proposal.Id != proposalId || proposal.BotId != botId || proposal.Before.Id != botId ||
                proposal.After.Id != botId || proposal.Status != "Applying")
                throw new StudioValidationException("Only an interrupted application can be restored.");
            var validated = ApplyOperations(proposal.Before, proposal.Request, proposal.Operations);
            if (!TwitchBotService.SameConfiguration(validated, proposal.After))
                throw new StudioValidationException("The saved proposal no longer matches its operations.");
            var current = bots.Get(botId) ??
                throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
            var restored = proposal with { Status = "Restored", Error = "Interrupted application restored." };
            if (TwitchBotService.SameConfiguration(current, proposal.Before))
            {
                Write(restored);
                return restored;
            }
            return botService.ApplyIfUnchanged(proposal.After, proposal.Before, () =>
            {
                Write(restored);
                return restored;
            });
        }
    }

    private CustomAgentProfile Agent(CustomAgentTeam team, string role)
    {
        var members = team.Members.Where(member => member.Role == role).ToArray();
        if (members.Length != 1 || agents.GetAgent(members[0].AgentId) is not { } profile)
            throw new StudioValidationException($"The team requires one available {role}.");
        if (ProjectExport.ContainsPotentialSecret(profile.SystemPrompt))
            throw new StudioValidationException("An agent prompt may contain credentials.");
        return profile;
    }

    private Task<string> Complete(CustomAgentProfile agent, string instruction, string input, JsonElement? schema,
        CancellationToken cancellationToken)
    {
        var configured = registry.Models.FirstOrDefault(model => model.Provider == "Ollama" && model.Name == agent.ModelName);
        var model = configured is null
            ? new ModelDefinition { Id = $"custom-{agent.Id:N}", Provider = "Ollama", Name = agent.ModelName,
                ContextSize = 4096, MaxOutputTokens = 1024, Temperature = 0.2 }
            : configured with { MaxOutputTokens = Math.Min(configured.MaxOutputTokens, 1024) };
        var system = agent.SystemPrompt + "\n\n" + instruction;
        if (System.Text.Encoding.UTF8.GetByteCount(system + input) > (model.ContextSize - model.MaxOutputTokens - 128) * 3)
            throw new StudioValidationException("The request and bot commands exceed the model context budget.");
        return provider.CompleteAsync(new(model, [new("system", system), new("user", input)], schema), cancellationToken);
    }

    private static ProposalOutput Parse(string raw)
    {
        if (raw.Length > 30000 || ProjectExport.ContainsPotentialSecret(raw))
            throw new StudioValidationException("The model output is oversized or may contain credentials.");
        try
        {
            var result = JsonSerializer.Deserialize<ProposalOutput>(raw, Json);
            if (result?.Operations is null || result.Unsupported is null ||
                result.Operations.Count > 20 || result.Unsupported.Count > 20 ||
                result.Unsupported.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 500))
                throw new StudioValidationException("The model output has invalid operations or unsupported requirements.");
            if (result.Unsupported.Any(item => SupportedClaim().IsMatch(item)))
                throw new StudioValidationException(
                    "The unsupported list contains a requirement described as supported. Remove supported requirements from unsupported.");
            if (result.Operations.Count == 0 && result.Unsupported.Count == 0)
                throw new StudioValidationException("The model returned no operations or unsupported requirements.");
            return result;
        }
        catch (JsonException)
        {
            throw new StudioValidationException("The model output is not valid strict JSON or contains unknown fields.");
        }
    }

    private static TwitchBotConfiguration ApplyOperations(TwitchBotConfiguration before, string request,
        IReadOnlyList<TwitchCommandOperation> operations)
    {
        var commands = before.Commands.ToList();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            if (operation is null || operation.Name is null || !CommandName().IsMatch(operation.Name) ||
                !Regex.IsMatch(request, Regex.Escape(operation.Name) + @"(?![A-Za-z0-9_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
                !targets.Add(operation.Name))
                throw new StudioValidationException("Each operation needs one unique command name explicitly present in the request.");
            var index = commands.FindIndex(command => string.Equals(command.Name, operation.Name, StringComparison.OrdinalIgnoreCase));
            if (operation.Action == "add" && index >= 0 || operation.Action != "add" && index < 0)
                throw new StudioValidationException("The operation conflicts with the existing command configuration.");
            var oldCooldown = index >= 0 ? commands[index].CooldownSeconds : 5;
            if (operation.CooldownSeconds is { } cooldown && cooldown != oldCooldown &&
                (!CooldownIntent().IsMatch(request) ||
                 !Regex.IsMatch(request, $@"(?<![\p{{L}}\p{{N}}_]){cooldown}(?![\p{{L}}\p{{N}}_])",
                     RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))
                throw new StudioValidationException(
                    "A changed cooldown requires that exact duration to be explicitly requested.");
            if (operation.CooldownSeconds is < 0 or > 3600 ||
                operation.Response is { } text && (string.IsNullOrWhiteSpace(text) || text.Length > 500 ||
                    text.Any(char.IsControl) || UnsafeResponse().IsMatch(text)))
                throw new StudioValidationException("The response or cooldown is invalid or contains executable-looking content.");
            switch (operation.Action)
            {
                case "add":
                    if (operation.Response is null)
                        throw new StudioValidationException("Adding a command requires a static response.");
                    commands.Add(new(operation.Name, operation.Response, operation.CooldownSeconds ?? 5,
                        operation.Enabled ?? true, operation.ModeratorOnly ?? false));
                    break;
                case "update":
                    var existing = commands[index];
                    commands[index] = existing with { Response = operation.Response ?? existing.Response,
                        CooldownSeconds = operation.CooldownSeconds ?? existing.CooldownSeconds,
                        Enabled = operation.Enabled ?? existing.Enabled,
                        ModeratorOnly = operation.ModeratorOnly ?? existing.ModeratorOnly };
                    break;
                case "disable":
                    if (operation.Response is not null || operation.CooldownSeconds is not null ||
                        operation.Enabled is not null || operation.ModeratorOnly is not null)
                        throw new StudioValidationException("Disable cannot modify other command properties.");
                    commands[index] = commands[index] with { Enabled = false };
                    break;
                case "delete":
                    if (!HasDeleteIntent(request, operation.Name) || operation.Response is not null ||
                        operation.CooldownSeconds is not null || operation.Enabled is not null ||
                        operation.ModeratorOnly is not null)
                        throw new StudioValidationException("Delete requires an explicit deletion request and no additional fields.");
                    commands.RemoveAt(index);
                    break;
                default:
                    throw new StudioValidationException("Unsupported command operation.");
            }
        }
        if (commands.Count > 100)
            throw new StudioValidationException("A bot cannot contain more than 100 commands.");
        return before with { Commands = commands.ToArray() };
    }

    private IReadOnlyList<TwitchCommandEvidence> ValidateSimulation(TwitchCommandProposal proposal,
        CancellationToken cancellationToken)
    {
        var evidence = new List<TwitchCommandEvidence>();
        foreach (var operation in proposal.Operations)
        {
            var command = proposal.After.Commands.SingleOrDefault(item =>
                string.Equals(item.Name, operation.Name, StringComparison.OrdinalIgnoreCase));
            var steps = new List<TwitchSimulationEvent> { new("viewer", "Viewer", operation.Name, 0) };
            if (command is { Enabled: true, ModeratorOnly: true })
                steps.Add(new("moderator", "Moderator", operation.Name, 0));
            var successful = steps.Count - 1;
            if (command is { Enabled: true, CooldownSeconds: > 0 })
            {
                steps.Add(steps[successful]);
                steps.Add(steps[successful] with { AtSeconds = command.CooldownSeconds });
            }
            var result = simulation.Simulate(proposal.BotId, steps, cancellationToken);
            if (result.BotId != proposal.BotId || result.Steps.Count != steps.Count)
                throw new SimulationAssertionException(evidence,
                    $"Simulation returned incomplete results for {operation.Name}. The configuration was restored.");
            for (var i = 0; i < result.Steps.Count; i++)
            {
                var expected = command is null || !command.Enabled ? "NoMatch" :
                    i == 0 && command.ModeratorOnly ? "PermissionDenied" :
                    i == successful + 1 && command.CooldownSeconds > 0 ? "Cooldown" : "Responded";
                var actual = result.Steps[i];
                var passed = actual.Event == steps[i] && actual.Outcome == expected &&
                    (expected != "Responded" || actual.Response == command!.Response);
                evidence.Add(new(operation.Name,
                    $"{actual.Event.Role} {actual.Event.UserId}, {actual.Event.Text}, t={actual.Event.AtSeconds}s",
                    expected == "Responded"
                    ? expected + ": " + command!.Response : expected,
                    actual.Outcome == "Responded" ? actual.Outcome + ": " + actual.Response : actual.Outcome, passed));
            }
        }
        if (evidence.FirstOrDefault(item => !item.Passed) is { } failure)
            throw new SimulationAssertionException(evidence,
                $"Simulation failed for {failure.Criterion}: expected {failure.Expected}; got {failure.Actual}. The configuration was restored.");
        return evidence;
    }

    private void EnsureDirectory()
    {
        SafePaths.RejectLinks(directory);
        Directory.CreateDirectory(directory);
        SafePaths.RejectLinks(directory);
    }

    private void Write(TwitchCommandProposal proposal)
    {
        EnsureDirectory();
        var path = Path.Combine(directory, proposal.Id.ToString("N") + ".json");
        var temporary = path + ".tmp";
        SafePaths.RejectLinks(path);
        SafePaths.RejectLinks(temporary);
        File.WriteAllText(temporary, JsonSerializer.Serialize(proposal, Json));
        SafePaths.RejectLinks(path);
        SafePaths.RejectLinks(temporary);
        File.Move(temporary, path, overwrite: true);
    }

    private TwitchCommandProposal Read(Guid id) => ReadPath(Path.Combine(directory, id.ToString("N") + ".json"));

    private static TwitchCommandProposal ReadPath(string path)
    {
        SafePaths.RejectLinks(path);
        if (!File.Exists(path))
            throw new StudioValidationException("The command proposal does not exist.");
        return JsonSerializer.Deserialize<TwitchCommandProposal>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Invalid command proposal history.");
    }

    private sealed record ProposalOutput(
        [property: JsonRequired] IReadOnlyList<string> Unsupported,
        [property: JsonRequired] IReadOnlyList<TwitchCommandOperation> Operations);

    private sealed class SimulationAssertionException(IReadOnlyList<TwitchCommandEvidence> evidence, string message)
        : Exception(message)
    {
        public IReadOnlyList<TwitchCommandEvidence> Evidence { get; } = evidence;
    }

    private static bool HasDeleteIntent(string request, string name) =>
        Regex.IsMatch(request,
            @"(?i:\b(?:supprim\w*|effac\w*|enlev\w*|retir\w*|delet\w*|remov\w*)\s+(?:(?:la|the)\s+)?(?:commande\s+|command\s+)?)" +
            Regex.Escape(name) + @"(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [GeneratedRegex("^![A-Za-z0-9_]{1,32}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CommandName();

    [GeneratedRegex(@"[{};`$\\<>|&=()/*+]|(?i:\b(?:powershell|cmd|bash|sh|python|javascript|script|using|namespace|class|function|eval|exec|curl|wget|sudo|shutdown|rmdir|del|rm|echo|npm|dotnet)\b)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UnsafeResponse();

    [GeneratedRegex(@"(?i:\b(?:(?:is|est)\s+(?:a\s+|une?\s+)?supported|already\s+enforces)\b)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SupportedClaim();

    [GeneratedRegex(@"(?i:\b(?:cooldown|d[eé]lai|temporisation|intervalle)\b)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CooldownIntent();

    [GeneratedRegex(@"(?i:\b(?:aléatoire\w*|aleatoire\w*|hasard|random\w*|tirage|lanc(?:e|er|ez)\s+(?:un\s+)?d[eé]|roll\s+(?:a\s+)?(?:die|dice)|nombre\s+entre)\b)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RandomRequirement();
}
