using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class TwitchChatClient(HttpClient http, TimeProvider time) : ITwitchChatClient, IDisposable
{
    private static readonly Uri InitialEventSubUri =
        new("wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=60");
    private const int MaximumEventBytes = 256 * 1024;
    private readonly SemaphoreSlim sending = new(1, 1);
    private long? lastSent;

    public async Task<TwitchChannel> FindChannelAsync(
        string clientId, string accessToken, string login, CancellationToken cancellationToken)
    {
        using var request = CreateApiRequest(HttpMethod.Get,
            "https://api.twitch.tv/helix/users?login=" + Uri.EscapeDataString(login), clientId, accessToken);
        using var response = await SendApiAsync(request, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var users = document.RootElement.GetProperty("data");
        if (users.ValueKind != JsonValueKind.Array || users.GetArrayLength() == 0)
            throw new TwitchIntegrationException("channel_not_found", "The selected Twitch channel was not found.", 404);
        var user = users[0];
        return new(
            RequiredString(user, "id", "channel_response_invalid"),
            RequiredString(user, "login", "channel_response_invalid"));
    }

    public async Task RunEventSubAsync(
        string clientId, string accessToken, string broadcasterId, string userId,
        Func<TwitchChatMessage, CancellationToken, Task> onMessage, Action onConnected,
        CancellationToken cancellationToken)
    {
        ClientWebSocket? socket = null;
        try
        {
            socket = await ConnectAsync(InitialEventSubUri, cancellationToken);
            var welcome = await ReceiveAsync(socket, TimeSpan.FromSeconds(10), cancellationToken);
            var (sessionId, keepalive) = ReadWelcome(welcome);
            using (var subscribeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                subscribeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                await SubscribeAsync(clientId, accessToken, broadcasterId, userId, sessionId, subscribeTimeout.Token);
            }
            onConnected();

            while (!cancellationToken.IsCancellationRequested)
            {
                using var document = await ReceiveAsync(socket, keepalive, cancellationToken);
                var root = document.RootElement;
                var messageType = root.GetProperty("metadata").GetProperty("message_type").GetString();
                if (messageType == "session_reconnect")
                {
                    var reconnectText = root.GetProperty("payload").GetProperty("session")
                        .GetProperty("reconnect_url").GetString();
                    if (!Uri.TryCreate(reconnectText, UriKind.Absolute, out var reconnectUri) ||
                        reconnectUri.Scheme != "wss" || reconnectUri.Host != "eventsub.wss.twitch.tv" ||
                        reconnectUri.Port != 443 || !string.IsNullOrEmpty(reconnectUri.UserInfo))
                        throw new TwitchIntegrationException("eventsub_reconnect_invalid",
                            "Twitch supplied an invalid reconnect endpoint.", 502);
                    var next = await ConnectAsync(reconnectUri, cancellationToken);
                    try
                    {
                        using var nextWelcome = await ReceiveAsync(next, TimeSpan.FromSeconds(10), cancellationToken);
                        keepalive = ReadWelcome(nextWelcome).Keepalive;
                    }
                    catch
                    {
                        next.Dispose();
                        throw;
                    }
                    var previous = socket;
                    socket = next;
                    previous.Dispose();
                    continue;
                }

                if (messageType == "revocation")
                {
                    var status = root.GetProperty("payload").GetProperty("subscription").GetProperty("status").GetString();
                    throw new TwitchIntegrationException("eventsub_" + SafeCode(status),
                        "Twitch revoked the chat event subscription.", 401);
                }
                if (messageType != "notification" ||
                    root.GetProperty("metadata").GetProperty("subscription_type").GetString() != "channel.chat.message")
                    continue;

                var notification = root.GetProperty("payload").GetProperty("event");
                var sender = RequiredString(notification, "chatter_user_id", "chat_event_invalid");
                var id = RequiredString(notification, "message_id", "chat_event_invalid");
                var text = notification.GetProperty("message").GetProperty("text").GetString();
                if (text is null || text.Length > 5000)
                    throw new TwitchIntegrationException("chat_event_invalid", "Twitch sent an invalid chat message.", 502);
                await onMessage(new(id, sender, text, HasModeratorBadge(notification)), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TwitchIntegrationException)
        {
            throw;
        }
        catch (WebSocketException)
        {
            throw new TwitchIntegrationException("eventsub_disconnected", "Twitch chat disconnected.", 503);
        }
        finally
        {
            if (socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
            {
                try
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "stopped", closeTimeout.Token);
                }
                catch (WebSocketException) { }
                catch (OperationCanceledException) { }
            }
            socket?.Dispose();
        }
    }

    internal static bool HasModeratorBadge(JsonElement notification) =>
        notification.TryGetProperty("badges", out var badges) && badges.ValueKind == JsonValueKind.Array &&
        badges.EnumerateArray().Any(badge => badge.ValueKind == JsonValueKind.Object &&
            badge.TryGetProperty("set_id", out var setId) && setId.ValueKind == JsonValueKind.String &&
            setId.GetString() is "moderator" or "broadcaster");

    public async Task SendMessageAsync(
        string clientId, string accessToken, string broadcasterId, string senderId,
        string replyToMessageId, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500)
            throw new TwitchIntegrationException("chat_message_invalid", "Twitch chat responses must be 1 to 500 characters.", 400);
        await sending.WaitAsync(cancellationToken);
        try
        {
            if (lastSent is { } previous)
            {
                var remaining = TimeSpan.FromMilliseconds(1600) - time.GetElapsedTime(previous);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, time, cancellationToken);
            }
            var request = CreateApiRequest(HttpMethod.Post, "https://api.twitch.tv/helix/chat/messages",
                clientId, accessToken);
            request.Content = JsonContent.Create(new
            {
                broadcaster_id = broadcasterId,
                sender_id = senderId,
                message,
                reply_parent_message_id = replyToMessageId
            });
            using (request)
            using (var response = await SendApiAsync(request, cancellationToken))
            {
                lastSent = time.GetTimestamp();
                using var document = await ReadJsonAsync(response, cancellationToken);
                var data = document.RootElement.GetProperty("data");
                if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 1 ||
                    !data[0].TryGetProperty("is_sent", out var sent) || sent.ValueKind != JsonValueKind.True)
                {
                    var dropCode = data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 1 &&
                        data[0].TryGetProperty("drop_reason", out var drop) && drop.ValueKind == JsonValueKind.Object &&
                        drop.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                            ? SafeCode(code.GetString()) : "unknown";
                    throw new TwitchIntegrationException("chat_message_rejected",
                        $"Twitch did not accept the chat response ({dropCode}).", 502);
                }
            }
        }
        finally
        {
            sending.Release();
        }
    }

    private async Task SubscribeAsync(
        string clientId, string accessToken, string broadcasterId, string userId,
        string sessionId, CancellationToken cancellationToken)
    {
        using var request = CreateApiRequest(HttpMethod.Post,
            "https://api.twitch.tv/helix/eventsub/subscriptions", clientId, accessToken);
        request.Content = JsonContent.Create(new
        {
            type = "channel.chat.message",
            version = "1",
            condition = new { broadcaster_user_id = broadcasterId, user_id = userId },
            transport = new { method = "websocket", session_id = sessionId }
        });
        using var response = await SendApiAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Accepted && response.StatusCode != HttpStatusCode.OK)
            throw ApiFailure(response.StatusCode, "Twitch refused the chat subscription.");
    }

    private async Task<HttpResponseMessage> SendApiAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = ApiFailure(response.StatusCode, "Twitch rejected the chat request.");
                response.Dispose();
                throw error;
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TwitchIntegrationException("twitch_timeout", "Twitch did not respond before the request timed out.", 503);
        }
        catch (HttpRequestException)
        {
            throw new TwitchIntegrationException("twitch_unavailable", "Twitch could not be reached.", 503);
        }
    }

    private static HttpRequestMessage CreateApiRequest(
        HttpMethod method, string uri, string clientId, string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);
        return request;
    }

    private static TwitchIntegrationException ApiFailure(HttpStatusCode status, string message)
    {
        var code = (int)status;
        return new TwitchIntegrationException("twitch_http_" + code, message,
            status == HttpStatusCode.Unauthorized ? 401 :
            status == (HttpStatusCode)429 ? 429 :
            code >= 500 ? 503 : 400);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new TwitchIntegrationException("twitch_invalid_response", "Twitch returned an invalid response.", 502);
        }
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(uri, timeout.Token);
            return socket;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TwitchIntegrationException("eventsub_timeout", "Twitch chat connection timed out.", 503);
        }
        catch (WebSocketException)
        {
            socket.Dispose();
            throw new TwitchIntegrationException("eventsub_unavailable", "Twitch chat could not be reached.", 503);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<JsonDocument> ReceiveAsync(
        ClientWebSocket socket, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var payload = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new TwitchIntegrationException("eventsub_disconnected", "Twitch closed the chat connection.", 503);
                if (result.MessageType != WebSocketMessageType.Text || payload.Length + result.Count > MaximumEventBytes)
                    throw new TwitchIntegrationException("eventsub_message_invalid", "Twitch sent an invalid or oversized chat event.", 502);
                payload.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TwitchIntegrationException("eventsub_keepalive_timeout", "Twitch chat keepalive timed out.", 503);
        }
        try
        {
            return JsonDocument.Parse(payload.ToArray());
        }
        catch (JsonException)
        {
            throw new TwitchIntegrationException("eventsub_message_invalid", "Twitch sent malformed chat event data.", 502);
        }
    }

    private static (string Id, TimeSpan Keepalive) ReadWelcome(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.GetProperty("metadata").GetProperty("message_type").GetString() != "session_welcome")
            throw new TwitchIntegrationException("eventsub_welcome_invalid", "Twitch did not send a valid chat welcome.", 502);
        var session = root.GetProperty("payload").GetProperty("session");
        var id = RequiredString(session, "id", "eventsub_welcome_invalid");
        var seconds = session.GetProperty("keepalive_timeout_seconds").GetInt32();
        return (id, TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 600)));
    }

    private static string RequiredString(JsonElement root, string name, string code) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new TwitchIntegrationException(code, "Twitch returned incomplete chat data.", 502);

    private static string SafeCode(string? value) =>
        value is { Length: > 0 and <= 48 } && value.All(char.IsAsciiLetterOrDigit) ? value : "unknown";

    public void Dispose() => sending.Dispose();
}
