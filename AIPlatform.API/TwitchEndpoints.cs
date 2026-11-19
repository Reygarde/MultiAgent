using AIPlatform.Core.Studio;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AIPlatform.API;

public static class TwitchEndpoints
{
    public static void MapTwitch(this WebApplication app)
    {
        app.MapGet("/api/twitch/settings", (ITwitchBotService bots, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(TwitchSettingsResponse.From(bots.GetSettings()));
        }).WithName("GetTwitchSettings").WithSummary("Read Twitch application configuration")
            .WithDescription("Returns only the non-secret Twitch Client ID; access and refresh tokens are never exposed.");

        app.MapPut("/api/twitch/settings", (UpdateTwitchSettingsRequest request,
            ITwitchBotService bots, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(TwitchSettingsResponse.From(bots.SaveSettings(request.ClientId ?? "")));
        }).WithName("SaveTwitchSettings").WithSummary("Save the Twitch public Client ID")
            .WithDescription("Requires a Twitch application registered as a public client for Device Code OAuth.")
            .ProducesProblem(400).ProducesProblem(409);

        app.MapGet("/api/twitch/bots", (ITwitchBotService bots, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(bots.List().Select(TwitchBotResponse.From).ToArray());
        }).WithName("ListTwitchBots").WithSummary("List configured Twitch bots")
            .WithDescription("Includes connection state and recent events, but never credentials.");

        app.MapPost("/api/twitch/bots", (CreateTwitchBotRequest request,
            ITwitchBotService bots, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var bot = bots.Create(request.Name ?? "", request.Channel ?? "");
            var response = TwitchBotResponse.From(bot);
            return TypedResults.Created($"/api/twitch/bots/{bot.Configuration.Id}", response);
        }).WithName("CreateTwitchBot").WithSummary("Create a Twitch bot with the !hello demo command")
            .WithDescription("Creation only persists configuration; it does not connect or start the bot.")
            .ProducesProblem(400);

        app.MapGet("/api/twitch/bots/{id:guid}", Results<Ok<TwitchBotResponse>, NotFound> (
            Guid id, ITwitchBotService bots, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            var bot = bots.Get(id);
            return bot is null ? TypedResults.NotFound() : TypedResults.Ok(TwitchBotResponse.From(bot));
        }).WithName("GetTwitchBot").WithSummary("Read one configured Twitch bot")
            .WithDescription("Includes its commands, current state and recent events; credentials are excluded.");

        app.MapPut("/api/twitch/bots/{id:guid}", Results<Ok<TwitchBotResponse>, NotFound> (
            Guid id, UpdateTwitchBotRequest request, ITwitchBotService bots, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            if (bots.Get(id) is null)
                return TypedResults.NotFound();
            var commands = request.Commands?.Select(command => command is null
                ? throw new StudioValidationException("A command entry cannot be null.")
                : new TwitchCommand(command.Name ?? "", command.Response ?? "",
                    command.CooldownSeconds, command.Enabled, command.ModeratorOnly)).ToArray()
                ?? throw new StudioValidationException("A commands list is required.");
            return TypedResults.Ok(TwitchBotResponse.From(
                bots.Update(id, request.Name ?? "", request.Channel ?? "", commands)));
        }).WithName("UpdateTwitchBot").WithSummary("Update bot name, channel and chat commands")
            .WithDescription("Commands are simple text responses with a per-user cooldown; a running bot must be stopped first.")
            .ProducesProblem(400).ProducesProblem(409);

        app.MapDelete("/api/twitch/bots/{id:guid}", Results<NoContent, NotFound> (
            Guid id, ITwitchBotService bots, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return bots.Delete(id) ? TypedResults.NoContent() : TypedResults.NotFound();
        }).WithName("DeleteTwitchBot").WithSummary("Delete a stopped Twitch bot")
            .WithDescription("Removes its saved configuration and securely stored Twitch credentials.")
            .ProducesProblem(409);

        app.MapPost("/api/twitch/bots/{id:guid}/connect",
            async Task<Ok<TwitchDeviceAuthorizationResponse>> (
                Guid id, ITwitchBotService bots, CancellationToken token) =>
                TypedResults.Ok(new TwitchDeviceAuthorizationResponse(await bots.BeginConnectAsync(id, token))))
            .WithName("BeginTwitchConnect").WithSummary("Begin explicit Twitch Device Code authorization")
            .WithDescription("Starts authorization only after the user clicks Connect; no bot is started automatically.")
            .ProducesProblem(400).ProducesProblem(409).ProducesProblem(503);

        app.MapPost("/api/twitch/bots/{id:guid}/connect/poll",
            async Task<Results<Ok<TwitchConnectResponse>, NotFound>> (
                Guid id, ITwitchBotService bots, CancellationToken token) =>
                TypedResults.Ok(TwitchConnectResponse.From(await bots.PollConnectAsync(id, token))))
            .WithName("PollTwitchConnect").WithSummary("Poll an explicitly started Twitch authorization")
            .WithDescription("Returns pending state or the authorized account; token values are never returned.")
            .ProducesProblem(400).ProducesProblem(503);

        app.MapPost("/api/twitch/bots/{id:guid}/start",
            async Task<Results<Ok<TwitchBotResponse>, NotFound>> (
                Guid id, ITwitchBotService bots, CancellationToken token) =>
                TypedResults.Ok(TwitchBotResponse.From(await bots.StartAsync(id, token))))
            .WithName("StartTwitchBot").WithSummary("Start one Twitch bot instance")
            .WithDescription("Connects to EventSub chat only after an explicit user action.")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(409).ProducesProblem(503);

        app.MapPost("/api/twitch/bots/{id:guid}/stop",
            async Task<Results<Ok<TwitchBotResponse>, NotFound>> (
                Guid id, ITwitchBotService bots, CancellationToken token) =>
                TypedResults.Ok(TwitchBotResponse.From(await bots.StopAsync(id, token))))
            .WithName("StopTwitchBot").WithSummary("Stop a running Twitch bot")
            .WithDescription("Cancels EventSub and waits for the connection and background task to close.")
            .ProducesProblem(404);

        app.MapPost("/api/twitch/bots/{id:guid}/commands/test",
            (Guid id, TestTwitchCommandRequest request, ITwitchBotService bots, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(TwitchCommandTestResponse.From(bots.TestCommand(id, request.Input ?? "")));
        }).WithName("TestTwitchCommand").WithSummary("Test a chat command locally")
            .WithDescription("Runs the command matcher without connecting to Twitch or consuming its cooldown.")
            .ProducesProblem(400).ProducesProblem(404);

        app.MapPost("/api/twitch/bots/{id:guid}/simulation",
            (Guid id, TwitchSimulationRequest request, ITwitchSimulationService simulations, CancellationToken token) =>
        {
            if (request.Events is null || request.Events.Count is < 1 or > 100)
                throw new StudioValidationException("A simulation requires between 1 and 100 chat messages.");
            var events = request.Events.Select(item => item is null
                ? throw new StudioValidationException("A simulation event cannot be null.")
                : new TwitchSimulationEvent(item.UserId ?? "", item.Role ?? "", item.Text ?? "", item.AtSeconds))
                .ToArray();
            var result = simulations.Simulate(id, events, token);
            return TypedResults.Ok(new TwitchSimulationResponse(result.BotId,
                result.Steps.Select(step => new TwitchSimulationStepResponse(
                    new TwitchSimulationEventRequest(step.Event.UserId, step.Event.Role, step.Event.Text,
                        step.Event.AtSeconds), step.Outcome, step.Response, step.CooldownRemainingSeconds)).ToArray()));
        }).WithName("SimulateTwitchBot").WithSummary("Simulate configured chat commands offline")
            .WithDescription("Uses an isolated virtual clock and the production command router; does not read OAuth credentials or contact Twitch.")
            .ProducesProblem(400).ProducesProblem(404);

        app.MapGet("/api/twitch/bots/{id:guid}/command-proposals",
            (Guid id, ITwitchCommandProposalService proposals, CancellationToken token) =>
        {
            token.ThrowIfCancellationRequested();
            return TypedResults.Ok(proposals.List(id).Select(TwitchCommandProposalResponse.From).ToArray());
        }).WithName("ListTwitchCommandProposals").WithSummary("List command proposals for one configured bot")
            .WithDescription("Returns saved snapshots and simulation evidence; stale configuration results are marked outdated.")
            .ProducesProblem(404);

        app.MapPost("/api/twitch/bots/{id:guid}/command-proposals",
            async Task<Ok<TwitchCommandProposalResponse>> (Guid id, CreateTwitchCommandProposalRequest request,
                ITwitchCommandProposalService proposals, CancellationToken token) =>
                TypedResults.Ok(TwitchCommandProposalResponse.From(
                    await proposals.GenerateAsync(id, request.TeamId, request.Request ?? "", token))))
            .WithName("GenerateTwitchCommandProposal").WithSummary("Generate a structured command proposal")
            .WithDescription("Calls the selected team's local models; does not change the bot or execute generated code.")
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(503);

        app.MapPost("/api/twitch/bots/{id:guid}/command-proposals/{proposalId:guid}/apply",
            (Guid id, Guid proposalId, ApplyTwitchCommandProposalRequest request,
                ITwitchCommandProposalService proposals, CancellationToken token) =>
                TypedResults.Ok(TwitchCommandProposalResponse.From(
                    proposals.Apply(id, proposalId, request.Approved, request.DeleteApproved, token))))
            .WithName("ApplyTwitchCommandProposal").WithSummary("Apply an explicitly approved command proposal")
            .WithDescription("Requires a stopped and unchanged bot; tests changes offline and restores the snapshot on simulation failure.")
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);

        app.MapPost("/api/twitch/bots/{id:guid}/command-proposals/{proposalId:guid}/restore",
            (Guid id, Guid proposalId, RestoreTwitchCommandProposalRequest request,
                ITwitchCommandProposalService proposals, CancellationToken token) =>
                TypedResults.Ok(TwitchCommandProposalResponse.From(
                    proposals.RestoreInterrupted(id, proposalId, request.Approved, token))))
            .WithName("RestoreInterruptedTwitchCommandProposal").WithSummary("Restore an interrupted bot configuration change")
            .WithDescription("Restores only if the bot still matches the proposal snapshot, and never starts a Twitch connection.")
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
    }
}

/// <summary>Non-secret Twitch application configuration returned to Studio.</summary>
public sealed record TwitchSettingsResponse(string ClientId, bool Configured)
{
    public static TwitchSettingsResponse From(TwitchSettingsView settings) => new(settings.ClientId, settings.Configured);
}

/// <summary>Request to save a Twitch public Client ID.</summary>
public sealed record UpdateTwitchSettingsRequest(string? ClientId);

/// <summary>Request to create a named bot for one Twitch channel.</summary>
public sealed record CreateTwitchBotRequest(string? Name, string? Channel);

/// <summary>Request to update a bot and its configured text commands.</summary>
public sealed record UpdateTwitchBotRequest(string? Name, string? Channel, IReadOnlyList<TwitchCommandRequest>? Commands);

/// <summary>One configured text response and its cooldown.</summary>
public sealed record TwitchCommandRequest(string? Name, string? Response, int CooldownSeconds, bool Enabled,
    bool ModeratorOnly = false);

/// <summary>Request to test a command without contacting Twitch.</summary>
public sealed record TestTwitchCommandRequest(string? Input);

/// <summary>Command returned for a configured Twitch bot.</summary>
public sealed record TwitchCommandResponse(string Name, string Response, int CooldownSeconds, bool Enabled,
    bool ModeratorOnly)
{
    public static TwitchCommandResponse From(TwitchCommand command) =>
        new(command.Name, command.Response, command.CooldownSeconds, command.Enabled, command.ModeratorOnly);
}

/// <summary>One simulated chat message at a virtual time in seconds.</summary>
public sealed record TwitchSimulationEventRequest(string? UserId, string? Role, string? Text, int AtSeconds);

/// <summary>A bounded, independent offline sequence of chat messages.</summary>
public sealed record TwitchSimulationRequest(IReadOnlyList<TwitchSimulationEventRequest>? Events);

/// <summary>Actual command outcome and captured response for one simulated message.</summary>
public sealed record TwitchSimulationStepResponse(TwitchSimulationEventRequest Event, string Outcome,
    string? Response, int CooldownRemainingSeconds);

/// <summary>Offline simulation results for one configured bot.</summary>
public sealed record TwitchSimulationResponse(Guid BotId, IReadOnlyList<TwitchSimulationStepResponse> Steps);

/// <summary>Natural-language request and existing agent team for a command proposal.</summary>
public sealed record CreateTwitchCommandProposalRequest(Guid TeamId, string? Request);

/// <summary>Explicit approval, including separate approval when commands are deleted.</summary>
public sealed record ApplyTwitchCommandProposalRequest(bool Approved, bool DeleteApproved);

/// <summary>Explicit approval to restore an interrupted configuration change.</summary>
public sealed record RestoreTwitchCommandProposalRequest(bool Approved);

/// <summary>One proposed structured command operation.</summary>
public sealed record TwitchCommandOperationResponse(string Action, string Name, string? Response,
    int? CooldownSeconds, bool? Enabled, bool? ModeratorOnly);

/// <summary>Offline assertion associated with a specific command.</summary>
public sealed record TwitchCommandEvidenceResponse(string Command, string Criterion, string Expected,
    string Actual, bool Passed);

/// <summary>Saved proposal, configuration snapshots and offline validation results.</summary>
public sealed record TwitchCommandProposalResponse(Guid Id, Guid BotId, Guid TeamId, DateTimeOffset CreatedAt,
    string Request, string Status, IReadOnlyList<TwitchCommandOperationResponse> Operations,
    IReadOnlyList<string> Unsupported, IReadOnlyList<TwitchCommandResponse> Before,
    IReadOnlyList<TwitchCommandResponse> After, IReadOnlyList<TwitchCommandEvidenceResponse> Evidence, string? Error)
{
    public static TwitchCommandProposalResponse From(TwitchCommandProposal proposal) => new(
        proposal.Id, proposal.BotId, proposal.TeamId, proposal.CreatedAt, proposal.Request, proposal.Status,
        proposal.Operations.Select(operation => new TwitchCommandOperationResponse(operation.Action, operation.Name,
            operation.Response, operation.CooldownSeconds, operation.Enabled, operation.ModeratorOnly)).ToArray(),
        proposal.Unsupported, proposal.Before.Commands.Select(TwitchCommandResponse.From).ToArray(),
        proposal.After.Commands.Select(TwitchCommandResponse.From).ToArray(),
        proposal.Evidence.Select(item => new TwitchCommandEvidenceResponse(
            item.Command, item.Criterion, item.Expected, item.Actual, item.Passed)).ToArray(), proposal.Error);
}

/// <summary>Bot configuration, connection state and bounded event history without credentials.</summary>
public sealed record TwitchBotResponse(
    Guid Id, string Name, string Channel, string State, bool Authorized, string? AccountLogin, string? Error,
    IReadOnlyList<TwitchCommandResponse> Commands, IReadOnlyList<TwitchBotEventResponse> Events)
{
    public static TwitchBotResponse From(TwitchBotView bot) => new(
        bot.Configuration.Id, bot.Configuration.Name, bot.Configuration.Channel, bot.State, bot.Authorized,
        bot.AccountLogin, bot.Error, bot.Configuration.Commands.Select(TwitchCommandResponse.From).ToArray(),
        bot.Events.Select(TwitchBotEventResponse.From).ToArray());
}

/// <summary>Recent bot lifecycle or chat event without sensitive data.</summary>
public sealed record TwitchBotEventResponse(DateTimeOffset Timestamp, string Kind, string Message)
{
    public static TwitchBotEventResponse From(TwitchBotEvent item) => new(item.Timestamp, item.Kind, item.Message);
}

/// <summary>Device Code shown to the user to authorize the Twitch account.</summary>
public sealed record TwitchDeviceAuthorizationResponse(string VerificationUri, string UserCode, int ExpiresIn, int Interval)
{
    public TwitchDeviceAuthorizationResponse(TwitchDeviceAuthorization device)
        : this(device.VerificationUri, device.UserCode, device.ExpiresIn, device.Interval) { }
}

/// <summary>Safe status of a Twitch authorization poll, with no token data.</summary>
public sealed record TwitchConnectResponse(bool Pending, bool Authorized, string? AccountLogin, string? Message)
{
    public static TwitchConnectResponse From(TwitchConnectResult result) =>
        new(result.Pending, result.Authorized, result.AccountLogin, result.Message);
}

/// <summary>Result of local command matching without a Twitch connection.</summary>
public sealed record TwitchCommandTestResponse(bool Matched, string? Response, int CooldownRemainingSeconds)
{
    public static TwitchCommandTestResponse From(TwitchCommandTestResult result) =>
        new(result.Matched, result.Response, result.CooldownRemainingSeconds);
}
