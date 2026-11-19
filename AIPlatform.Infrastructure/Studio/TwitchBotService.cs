using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Text.Json;
using AIPlatform.Core.Studio;
using Microsoft.Extensions.Hosting;

namespace AIPlatform.Infrastructure.Studio;

public sealed partial class TwitchBotService(
    ITwitchBotStore store,
    ITwitchTokenVault tokenVault,
    ITwitchOAuthClient oauth,
    ITwitchChatClient chat,
    TimeProvider time) : ITwitchBotService, IHostedService
{
    private static readonly string[] RequiredScopes = ["user:read:chat", "user:write:chat"];
    private readonly object sync = new();
    private readonly Dictionary<Guid, PendingDevice> pending = [];
    private readonly Dictionary<Guid, RunningBot> running = [];
    private readonly Dictionary<Guid, string> states = [];
    private readonly Dictionary<Guid, string?> errors = [];
    private readonly Dictionary<Guid, List<TwitchBotEvent>> events = [];
    private readonly Dictionary<Guid, HashSet<string>> seenMessageIds = [];
    private readonly Dictionary<Guid, Queue<string>> messageOrder = [];

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        RunningBot[] active;
        lock (sync)
        {
            active = running.Values.ToArray();
            foreach (var runtime in active)
                runtime.Cancellation.Cancel();
        }
        await Task.WhenAll(active.Select(runtime => runtime.Task.WaitAsync(cancellationToken)));
    }

    public TwitchSettingsView GetSettings()
    {
        var clientId = store.GetClientId();
        return new(clientId, ClientIdPattern().IsMatch(clientId));
    }

    public TwitchSettingsView SaveSettings(string clientId)
    {
        if (clientId is null || clientId.Length > 100 ||
            clientId.Length > 0 && !ClientIdPattern().IsMatch(clientId))
            throw new StudioValidationException("Enter a Twitch Client ID containing 30 lowercase letters or digits.");
        lock (sync)
        {
            if (running.Count > 0)
                throw new TwitchIntegrationException("bot_running", "Stop Twitch bots before changing the Client ID.", 409);
            if (!string.Equals(store.GetClientId(), clientId, StringComparison.Ordinal))
                pending.Clear();
            store.SetClientId(clientId);
            return new(clientId, clientId.Length > 0);
        }
    }

    public IReadOnlyList<TwitchBotView> List() => store.List().Select(BuildView).ToArray();

    public TwitchBotView? Get(Guid id)
    {
        var bot = store.Get(id);
        return bot is null ? null : BuildView(bot);
    }

    public TwitchBotView Create(string name, string channel)
    {
        var bot = new TwitchBotConfiguration(Guid.NewGuid(), name.Trim(), channel.Trim().ToLowerInvariant(),
            [new("!hello", "Salut le chat !", 5)]);
        store.Save(bot);
        AddEvent(bot.Id, "created", "Bot créé avec la commande de démonstration !hello.");
        return BuildView(bot);
    }

    public TwitchBotView Update(Guid id, string name, string channel, IReadOnlyList<TwitchCommand> commands)
    {
        TwitchBotConfiguration updated;
        lock (sync)
        {
            var current = store.Get(id) ??
                throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
            if (running.ContainsKey(id))
                throw new TwitchIntegrationException("bot_running", "Stop the Twitch bot before editing its configuration.", 409);
            updated = current with
            {
                Name = name.Trim(),
                Channel = channel.Trim().ToLowerInvariant(),
                Commands = commands.ToArray()
            };
            store.Save(updated);
        }
        AddEvent(id, "configuration", "Configuration du bot enregistrée.");
        return BuildView(updated);
    }

    internal T ApplyIfUnchanged<T>(TwitchBotConfiguration before, TwitchBotConfiguration after,
        Func<T> validate)
    {
        lock (sync)
        {
            var current = store.Get(before.Id) ??
                throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
            if (running.ContainsKey(before.Id))
                throw new TwitchIntegrationException("bot_running", "Stop the Twitch bot before editing its configuration.", 409);
            if (!SameConfiguration(current, before))
                throw new TwitchIntegrationException("bot_changed",
                    "The bot configuration changed after this proposal was created.", 409);
            store.Save(after);
            var success = false;
            try
            {
                var result = validate();
                success = true;
                return result;
            }
            finally
            {
                if (!success)
                    store.Save(before);
            }
        }
    }

    internal static bool SameConfiguration(TwitchBotConfiguration left, TwitchBotConfiguration right) =>
        left.Id == right.Id && left.Name == right.Name && left.Channel == right.Channel &&
        left.Commands.SequenceEqual(right.Commands);

    public bool Delete(Guid id)
    {
        lock (sync)
        {
            if (running.ContainsKey(id))
                throw new TwitchIntegrationException("bot_running", "Stop the Twitch bot before deleting it.", 409);
            if (store.Get(id) is null)
                return false;
            if (tokenVault.IsAvailable)
                DeleteCredentials(id);
            var deleted = store.Delete(id);
            if (deleted)
            {
                pending.Remove(id);
                states.Remove(id);
                errors.Remove(id);
                events.Remove(id);
                seenMessageIds.Remove(id);
                messageOrder.Remove(id);
            }
            return deleted;
        }
    }

    public async Task<TwitchDeviceAuthorization> BeginConnectAsync(Guid id, CancellationToken cancellationToken)
    {
        var bot = store.Get(id) ?? throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
        var clientId = RequireClientId();
        EnsureVault();
        lock (sync)
        {
            if (running.ContainsKey(id))
                throw new TwitchIntegrationException("bot_running", "Stop the bot before reconnecting its Twitch account.", 409);
            if (pending.TryGetValue(id, out var current) && current.ExpiresAt > time.GetUtcNow())
                throw new TwitchIntegrationException("oauth_pending", "A Twitch authorization is already waiting for this bot.", 409);
        }

        var device = await oauth.BeginDeviceCodeAsync(clientId, cancellationToken);
        lock (sync)
        {
            var now = time.GetUtcNow();
            if (store.Get(id) is null)
                throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
            if (!string.Equals(store.GetClientId(), clientId, StringComparison.Ordinal))
                throw new TwitchIntegrationException("client_id_changed", "The Twitch Client ID changed during authorization.", 409);
            if (pending.TryGetValue(id, out var current) && current.ExpiresAt > now)
                throw new TwitchIntegrationException("oauth_pending", "A Twitch authorization is already waiting for this bot.", 409);
            pending[id] = new(device, clientId, now.AddSeconds(device.ExpiresIn), device.Interval, now);
            states[id] = "Disconnected";
            errors[id] = null;
        }
        AddEvent(id, "oauth", "Connexion Twitch en attente de l’autorisation dans le navigateur.");
        return new(device.VerificationUri, device.UserCode, device.ExpiresIn, device.Interval);
    }

    public async Task<TwitchConnectResult> PollConnectAsync(Guid id, CancellationToken cancellationToken)
    {
        var bot = store.Get(id) ?? throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
        EnsureVault();
        PendingDevice? flow;
        lock (sync)
            pending.TryGetValue(id, out flow);
        if (flow is null)
        {
            var saved = ReadCredentials(id);
            return saved is null ? new(false, false, null, "Aucune autorisation Twitch en attente.")
                : new(false, true, saved.Login);
        }

        await flow.Gate.WaitAsync(cancellationToken);
        try
        {
            lock (sync)
                if (!pending.TryGetValue(id, out var current) || !ReferenceEquals(current, flow))
                {
                    var saved = ReadCredentials(id);
                    return saved is null ? new(false, false, null, "Aucune autorisation Twitch en attente.")
                        : new(false, true, saved.Login);
                }

            var now = time.GetUtcNow();
            if (now >= flow.ExpiresAt)
            {
                RemovePending(id, flow);
                AddEvent(id, "oauth", "Le code d’autorisation Twitch a expiré.");
                return new(false, false, null, "Le code d’autorisation a expiré.");
            }
            if (now < flow.NextPollAt)
                return new(true, false, null);

            var result = await oauth.PollDeviceCodeAsync(flow.ClientId, flow.Device.DeviceCode, cancellationToken);
            lock (sync)
                if (!pending.TryGetValue(id, out var current) || !ReferenceEquals(current, flow))
                    return new(false, false, null, "Cette autorisation a été annulée par un changement de configuration.");
            if (result.RetryIntervalSeconds is { } retryInterval)
                flow.Interval = Math.Clamp(retryInterval, flow.Interval, 60);
            flow.NextPollAt = time.GetUtcNow().AddSeconds(flow.Interval);
            if (result.Status == TwitchDevicePollStatus.Pending)
                return new(true, false, null);
            RemovePending(id, flow);
            if (result.Status is TwitchDevicePollStatus.Expired or TwitchDevicePollStatus.Denied)
            {
                AddEvent(id, "oauth", result.Status == TwitchDevicePollStatus.Expired
                    ? "Le code d’autorisation Twitch a expiré." : "Autorisation Twitch refusée.");
                return new(false, false, null, result.Status == TwitchDevicePollStatus.Expired
                    ? "Le code d’autorisation a expiré." : "Autorisation Twitch refusée.");
            }

            var tokens = result.Tokens ?? throw new TwitchIntegrationException(
                "oauth_token_missing", "Twitch did not return authorization tokens.", 502);
            var identity = await oauth.ValidateAsync(tokens.AccessToken, cancellationToken);
            ValidateIdentity(identity, flow.ClientId);
            tokenVault.Save(id, new(flow.ClientId, identity.UserId, identity.Login, tokens.AccessToken, tokens.RefreshToken));
            lock (sync)
            {
                states[id] = "Stopped";
                errors[id] = null;
            }
            AddEvent(id, "oauth", $"Compte Twitch {identity.Login} autorisé; aucun bot n’a été démarré.");
            return new(false, true, identity.Login);
        }
        catch (TwitchIntegrationException exception)
        {
            RecordError(id, exception);
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException)
        {
            var failure = new TwitchIntegrationException("credential_store_failed",
                "The secure Twitch credential store could not be accessed.", 503);
            RecordError(id, failure);
            throw failure;
        }
        finally
        {
            flow.Gate.Release();
        }
    }

    public async Task<TwitchBotView> StartAsync(Guid id, CancellationToken cancellationToken)
    {
        var bot = store.Get(id) ?? throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
        EnsureVault();
        var clientId = RequireClientId();
        RunningBot runtime;
        lock (sync)
        {
            if (running.ContainsKey(id))
                throw new TwitchIntegrationException("bot_running", "This Twitch bot already has an active instance.", 409);
            runtime = new(id, new(), new(time));
            running.Add(id, runtime);
            states[id] = "Starting";
            errors[id] = null;
        }
        AddEvent(id, "start", "Démarrage demandé par l’utilisateur.");
        runtime.Task = Task.Run(() => RunBotAsync(runtime, bot, clientId));
        try
        {
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            return Get(id)!;
        }
        catch (TimeoutException)
        {
            await StopAsync(id, CancellationToken.None);
            throw new TwitchIntegrationException("eventsub_start_timeout", "Twitch chat did not connect in time.", 503);
        }
        catch (TwitchIntegrationException)
        {
            throw;
        }
    }

    public async Task<TwitchBotView> StopAsync(Guid id, CancellationToken cancellationToken)
    {
        RunningBot? runtime;
        lock (sync)
        {
            running.TryGetValue(id, out runtime);
            if (runtime is not null)
            {
                states[id] = "Stopping";
                runtime.Cancellation.Cancel();
            }
        }
        if (runtime is null)
        {
            if (store.Get(id) is null)
                throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
            return BuildView(store.Get(id)!);
        }
        AddEvent(id, "stop", "Arrêt demandé par l’utilisateur.");
        await runtime.Task.WaitAsync(cancellationToken);
        return Get(id)!;
    }

    public TwitchCommandTestResult TestCommand(Guid id, string input)
    {
        var bot = store.Get(id) ?? throw new TwitchIntegrationException("bot_not_found", "This Twitch bot no longer exists.", 404);
        return new TwitchCommandRouter(time).Route(bot.Commands, input, "local-test", consumeCooldown: false);
    }

    private async Task RunBotAsync(RunningBot runtime, TwitchBotConfiguration bot, string clientId)
    {
        try
        {
            var credentials = await GetValidCredentialsAsync(runtime.Id, clientId, runtime.Cancellation.Token);
            runtime.Credentials = credentials;
            var channel = await chat.FindChannelAsync(clientId, credentials.AccessToken, bot.Channel, runtime.Cancellation.Token);
            AddEvent(runtime.Id, "channel", $"Canal Twitch {channel.Login} résolu.");
            await RunConnectionLoopAsync(runtime, bot, channel.Id, clientId);
        }
        catch (TwitchIntegrationException exception)
        {
            runtime.Failure = exception;
            runtime.Started.TrySetException(exception);
            RecordError(runtime.Id, exception);
        }
        catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
        {
            runtime.Started.TrySetCanceled(runtime.Cancellation.Token);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            var failure = new TwitchIntegrationException("twitch_response_invalid",
                "Twitch returned an unexpected chat or account response.", 502);
            runtime.Failure = failure;
            runtime.Started.TrySetException(failure);
            RecordError(runtime.Id, failure);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException)
        {
            var errorCode = exception is Win32Exception ? "credential_store_failed" : "credential_data_invalid";
            var failure = new TwitchIntegrationException(
                errorCode, "The secure Twitch credential store could not be accessed.", 503);
            runtime.Failure = failure;
            runtime.Started.TrySetException(failure);
            RecordError(runtime.Id, failure);
        }
        finally
        {
            lock (sync)
            {
                running.Remove(runtime.Id);
                if (!errors.TryGetValue(runtime.Id, out var error) || error is null)
                    states[runtime.Id] = "Stopped";
            }
            runtime.Cancellation.Dispose();
        }
    }

    private async Task RunConnectionLoopAsync(RunningBot runtime, TwitchBotConfiguration bot, string broadcasterId, string clientId)
    {
        var retry = 0;
        while (!runtime.Cancellation.IsCancellationRequested)
        {
            var credentials = runtime.Credentials ?? ReadCredentials(runtime.Id)
                ?? throw new TwitchIntegrationException("authorization_required", "Connect a Twitch account before starting the bot.", 401);
            try
            {
                await RunConnectedSessionAsync(runtime, bot, broadcasterId, clientId, credentials);
                if (!runtime.Cancellation.IsCancellationRequested)
                    throw new TwitchIntegrationException("eventsub_disconnected", "Twitch chat connection ended.", 503);
                return;
            }
            catch (TwitchIntegrationException exception) when (!runtime.Cancellation.IsCancellationRequested)
            {
                if (exception.StatusCode == 401)
                {
                    await RefreshCredentialsAsync(runtime.Id, runtime, clientId, credentials, runtime.Cancellation.Token);
                    retry = 0;
                    continue;
                }
                if (exception.StatusCode is 400 or 403 or 404)
                    throw;
                SetState(runtime.Id, "Reconnecting", exception.Message);
                AddEvent(runtime.Id, "reconnect", exception.Message);
                var delay = TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(retry++, 6)));
                await Task.Delay(delay, time, runtime.Cancellation.Token);
            }
        }
    }

    private async Task RunConnectedSessionAsync(
        RunningBot runtime, TwitchBotConfiguration bot, string broadcasterId, string clientId,
        TwitchStoredCredentials credentials)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(runtime.Cancellation.Token);
        runtime.ConnectionCancellation = session;
        var connection = chat.RunEventSubAsync(clientId, credentials.AccessToken, broadcasterId, credentials.UserId,
            (message, token) => HandleMessageAsync(runtime, bot.Id, broadcasterId, clientId, message, token),
            () =>
            {
                SetState(runtime.Id, "Connected", null);
                AddEvent(runtime.Id, "connected", $"Connecté au chat de {bot.Channel}.");
                runtime.Started.TrySetResult();
            }, session.Token);
        try
        {
            var validation = Task.Delay(TimeSpan.FromHours(1), time, session.Token);
            while (true)
            {
                var completed = await Task.WhenAny(connection, validation);
                if (completed == connection)
                {
                    try { await connection; }
                    catch (OperationCanceledException) when (session.IsCancellationRequested &&
                        !runtime.Cancellation.IsCancellationRequested) { }
                    if (session.IsCancellationRequested && !runtime.Cancellation.IsCancellationRequested)
                        return;
                    return;
                }

                var current = runtime.Credentials ?? credentials;
                try
                {
                    var identity = await oauth.ValidateAsync(current.AccessToken, runtime.Cancellation.Token);
                    ValidateIdentity(identity, clientId);
                    if (identity.UserId != current.UserId)
                        throw new TwitchIntegrationException("account_changed", "Twitch authorization changed accounts.", 401);
                }
                catch (TwitchIntegrationException exception) when (exception.StatusCode == 401)
                {
                    await RefreshCredentialsAsync(runtime.Id, runtime, clientId, current, runtime.Cancellation.Token);
                    session.Cancel();
                    try { await connection; }
                    catch (OperationCanceledException) when (session.IsCancellationRequested) { }
                    return;
                }
                validation = Task.Delay(TimeSpan.FromHours(1), time, session.Token);
            }
        }
        finally
        {
            session.Cancel();
            runtime.ConnectionCancellation = null;
        }
    }

    private async Task HandleMessageAsync(
        RunningBot runtime, Guid botId, string broadcasterId, string clientId,
        TwitchChatMessage message, CancellationToken cancellationToken)
    {
        var credentials = runtime.Credentials ?? ReadCredentials(botId)
            ?? throw new TwitchIntegrationException("authorization_required", "Reconnect the Twitch account.", 401);
        if (message.SenderId == credentials.UserId || !RememberMessage(botId, message.Id))
            return;

        var bot = store.Get(botId);
        if (bot is null)
            return;
        var route = runtime.Commands.Route(bot.Commands, message.Text, message.SenderId, consumeCooldown: true,
            isModerator: message.IsModerator);
        if (route.PermissionDenied)
        {
            AddEvent(botId, "permission", "Commande réservée aux modérateurs ignorée.");
            return;
        }
        if (route.CooldownRemainingSeconds > 0)
        {
            AddEvent(botId, "cooldown", "Commande ignorée pendant son délai anti-spam.");
            return;
        }
        if (!route.Matched || route.Response is null)
            return;

        AddEvent(botId, "command", $"Commande {message.Text.Trim().Split(' ')[0]} reçue.");
        try
        {
            await chat.SendMessageAsync(clientId, credentials.AccessToken, broadcasterId, credentials.UserId,
                message.Id, route.Response, cancellationToken);
            AddEvent(botId, "sent", "Réponse envoyée au chat Twitch.");
        }
        catch (TwitchIntegrationException exception) when (exception.StatusCode == 401)
        {
            await RefreshCredentialsAsync(botId, runtime, clientId, credentials, cancellationToken);
            runtime.ConnectionCancellation?.Cancel();
            AddEvent(botId, "token_refresh", "Jeton Twitch renouvelé; reconnexion du chat.");
        }
        catch (TwitchIntegrationException exception)
        {
            AddEvent(botId, "error", exception.Message);
        }
    }

    private async Task<TwitchStoredCredentials> GetValidCredentialsAsync(
        Guid botId, string clientId, CancellationToken cancellationToken)
    {
        var credentials = ReadCredentials(botId)
            ?? throw new TwitchIntegrationException("authorization_required", "Connect a Twitch account before starting the bot.", 401);
        if (credentials.ClientId != clientId)
            throw new TwitchIntegrationException("client_id_changed", "Reconnect the Twitch account after changing the Client ID.", 401);
        try
        {
            var identity = await oauth.ValidateAsync(credentials.AccessToken, cancellationToken);
            ValidateIdentity(identity, clientId);
            if (identity.UserId != credentials.UserId)
                throw new TwitchIntegrationException("account_changed", "Twitch authorization changed accounts.", 401);
            return credentials;
        }
        catch (TwitchIntegrationException exception) when (exception.StatusCode == 401)
        {
            return await RefreshCredentialsAsync(botId, null, clientId, credentials, cancellationToken);
        }
    }

    private async Task<TwitchStoredCredentials> RefreshCredentialsAsync(
        Guid botId, RunningBot? runtime, string clientId, TwitchStoredCredentials current,
        CancellationToken cancellationToken)
    {
        TwitchTokenGrant tokens;
        try { tokens = await oauth.RefreshAsync(clientId, current.RefreshToken, cancellationToken); }
        catch (TwitchIntegrationException exception) when (exception.StatusCode == 400)
        {
            try { tokenVault.Delete(botId); }
            catch (Exception vaultException) when (vaultException is Win32Exception or InvalidDataException)
            {
                throw new TwitchIntegrationException("credential_store_failed",
                    "Twitch authorization expired and Studio could not update the secure credential store.", 503);
            }
            if (runtime is not null)
                runtime.Credentials = null;
            throw new TwitchIntegrationException("authorization_required",
                "Twitch authorization expired. Reconnect the account before starting the bot.", 401);
        }
        var identity = await oauth.ValidateAsync(tokens.AccessToken, cancellationToken);
        ValidateIdentity(identity, clientId);
        if (identity.UserId != current.UserId)
            throw new TwitchIntegrationException("account_changed", "Twitch authorization changed accounts.", 401);
        var updated = new TwitchStoredCredentials(clientId, identity.UserId, identity.Login,
            tokens.AccessToken, tokens.RefreshToken);
        try { tokenVault.Save(botId, updated); }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException)
        {
            throw new TwitchIntegrationException("credential_store_failed",
                "Twitch rotated the token but Studio could not securely save it. Reconnect the account.", 503);
        }
        if (runtime is not null)
            runtime.Credentials = updated;
        return updated;
    }

    private static void ValidateIdentity(TwitchValidatedIdentity identity, string clientId)
    {
        if (!string.Equals(identity.ClientId, clientId, StringComparison.Ordinal) ||
            RequiredScopes.Any(scope => !identity.Scopes.Contains(scope, StringComparer.Ordinal)))
            throw new TwitchIntegrationException("oauth_scopes_missing",
                "Reconnect Twitch and grant the requested chat read/write permissions.", 403);
    }

    private TwitchBotView BuildView(TwitchBotConfiguration bot)
    {
        var credentials = ReadCredentials(bot.Id);
        var authorized = credentials is not null &&
            string.Equals(credentials.ClientId, store.GetClientId(), StringComparison.Ordinal);
        lock (sync)
        {
            return new(bot, states.GetValueOrDefault(bot.Id) ?? "Stopped", authorized,
                authorized ? credentials!.Login : null, errors.GetValueOrDefault(bot.Id), events.GetValueOrDefault(bot.Id)?.ToArray() ?? []);
        }
    }

    private void EnsureVault()
    {
        if (!tokenVault.IsAvailable)
            throw new TwitchIntegrationException("credential_store_unavailable",
                "Secure Twitch credentials require Windows Credential Manager.", 503);
    }

    private TwitchStoredCredentials? ReadCredentials(Guid botId)
    {
        if (!tokenVault.IsAvailable)
            return null;
        try { return tokenVault.Read(botId); }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException)
        {
            throw new TwitchIntegrationException("credential_store_failed",
                "The secure Twitch credential store could not be accessed.", 503);
        }
    }

    private void DeleteCredentials(Guid botId)
    {
        try { tokenVault.Delete(botId); }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException)
        {
            throw new TwitchIntegrationException("credential_store_failed",
                "The secure Twitch credential store could not be updated.", 503);
        }
    }

    private string RequireClientId()
    {
        var clientId = store.GetClientId();
        if (!ClientIdPattern().IsMatch(clientId))
            throw new TwitchIntegrationException("client_id_required",
                "Configure a valid Twitch Client ID before connecting an account.", 400);
        return clientId;
    }

    private bool RememberMessage(Guid botId, string messageId)
    {
        lock (sync)
        {
            if (!seenMessageIds.TryGetValue(botId, out var ids))
            {
                ids = [];
                seenMessageIds.Add(botId, ids);
                messageOrder.Add(botId, new());
            }
            if (!ids.Add(messageId))
                return false;
            var order = messageOrder[botId];
            order.Enqueue(messageId);
            if (order.Count > 512)
                ids.Remove(order.Dequeue());
            return true;
        }
    }

    private void SetState(Guid id, string state, string? error)
    {
        lock (sync)
        {
            states[id] = state;
            errors[id] = error;
        }
    }

    private void RecordError(Guid id, TwitchIntegrationException exception)
    {
        SetState(id, "Error", exception.Message);
        AddEvent(id, "error", exception.Message);
    }

    private void AddEvent(Guid id, string kind, string message)
    {
        lock (sync)
        {
            if (!events.TryGetValue(id, out var log))
            {
                log = [];
                events.Add(id, log);
            }
            log.Add(new(time.GetUtcNow(), kind, message));
            if (log.Count > 100)
                log.RemoveAt(0);
        }
    }

    private void RemovePending(Guid id, PendingDevice flow)
    {
        lock (sync)
            if (pending.TryGetValue(id, out var current) && ReferenceEquals(current, flow))
                pending.Remove(id);
    }

    [GeneratedRegex("^[a-z0-9]{30}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ClientIdPattern();

    private sealed class PendingDevice(
        TwitchDeviceCode device, string clientId, DateTimeOffset expiresAt, int interval, DateTimeOffset nextPollAt)
    {
        public TwitchDeviceCode Device { get; } = device;
        public string ClientId { get; } = clientId;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int Interval { get; set; } = interval;
        public DateTimeOffset NextPollAt { get; set; } = nextPollAt;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class RunningBot(Guid id, CancellationTokenSource cancellation, TwitchCommandRouter commands)
    {
        public Guid Id { get; } = id;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TwitchCommandRouter Commands { get; } = commands;
        public Task Task { get; set; } = Task.CompletedTask;
        public TwitchStoredCredentials? Credentials { get; set; }
        public CancellationTokenSource? ConnectionCancellation { get; set; }
        public TwitchIntegrationException? Failure { get; set; }
    }
}
