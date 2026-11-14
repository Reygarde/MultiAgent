using AIPlatform.Core.Studio;
using System.Text.RegularExpressions;

namespace AIPlatform.Infrastructure.Studio;

public static partial class TwitchTemplate
{
    public const string Id = "twitch-bot-dotnet";
    public const int Version = 1;
    public const string Image = "aiplatform-sandbox:net10";
    public static bool IsEditable(string path) => path is "src/App/CommandRouter.cs" or "src/App/BotHost.cs";
    public static IEnumerable<GeneratedFile> ForReview(IReadOnlyList<GeneratedFile> files) =>
        files.Where(file => IsEditable(file.Path) || file.Path == "src/App/IChatTransport.cs");
    public static ReviewFinding? DispatchFinding(IReadOnlyList<GeneratedFile> files) =>
        UnsafeDispatch().IsMatch(files.Single(file => file.Path == "src/App/BotHost.cs").Content)
            ? new("src/App/BotHost.cs", "Error",
                "Server template policy: BotHost must use one await foreach and only catch retryable send errors. " +
                "Remove outer while, catch(Exception) and exception.Message logging. " +
                "EventSubTransport already reconnects; fatal errors and cancellation must propagate.")
            : null;

    [GeneratedRegex(@"\bwhile\s*\(|\bcatch\s*\(\s*(?:System\s*\.\s*)?Exception\b|\b[A-Za-z_]\w*\s*\.\s*Message\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UnsafeDispatch();

    public static void Validate(IReadOnlyList<GeneratedFile> files)
    {
        if (!files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal)
            .SetEquals(Files.Select(file => file.Path)))
            throw new StudioValidationException("Twitch projects must retain the exact approved template paths.");
        foreach (var trusted in Files.Where(file => !IsEditable(file.Path)))
            if (files.Single(file => file.Path == trusted.Path).Content != trusted.Content)
                throw new StudioValidationException("A protected Twitch template file was modified: " + trusted.Path);
    }

    public static IReadOnlyList<PlannedFile> PlannedFiles { get; } =
    [
        new("src/App/App.csproj", "Dependency-free .NET 10 console", []),
        new("src/App/IChatTransport.cs", "Chat contracts and safe structured logging", []),
        new("src/App/CommandRouter.cs", "Implement !ping, !help and monotonic bot !uptime", ["src/App/IChatTransport.cs"]),
        new("src/App/BotHost.cs", "Dispatch real commands and handle transport errors", ["src/App/IChatTransport.cs", "src/App/CommandRouter.cs"]),
        new("src/App/TwitchConfiguration.cs", "Environment-only configuration and explicit live opt-in", []),
        new("src/App/TwitchApi.cs", "Helix subscription and rate-limited message sending", ["src/App/TwitchConfiguration.cs", "src/App/IChatTransport.cs"]),
        new("src/App/TwitchAuthentication.cs", "OAuth identity, scope and periodic validation", ["src/App/TwitchApi.cs"]),
        new("src/App/EventSubTransport.cs", "EventSub sockets, keepalives, deduplication and reconnect", ["src/App/TwitchApi.cs"]),
        new("src/App/Program.cs", "Explicit manual live entry point and cancellation", ["src/App/BotHost.cs", "src/App/EventSubTransport.cs", "src/App/TwitchAuthentication.cs"]),
        new("tests/App.Tests/App.Tests.csproj", "Exact approved xUnit versions", ["src/App/App.csproj"]),
        new("tests/App.Tests/UnitTests.cs", "Immutable offline command and transport contracts", ["src/App/BotHost.cs", "src/App/EventSubTransport.cs", "src/App/TwitchAuthentication.cs", "tests/App.Tests/App.Tests.csproj"]),
        new("README.md", "OAuth setup, manual live opt-in and offline limitations", [])
    ];

    public static IReadOnlyList<GeneratedFile> Files { get; } =
    [
        new("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """),
        new("src/App/IChatTransport.cs", """
            using System.Text.Json;
            namespace App;

            public sealed record ChatMessage(string Id, string SenderId, string Text);
            public interface IChatTransport
            {
                IAsyncEnumerable<ChatMessage> ReadAllAsync(CancellationToken cancellation);
                Task SendAsync(ChatMessage original, string response, CancellationToken cancellation);
            }
            public sealed class ChatTransportException(string code, bool retryable) : Exception(code)
            {
                public string Code { get; } = code;
                public bool Retryable { get; } = retryable;
            }
            public static class BotLog
            {
                public static void Write(string eventName, string? code = null) =>
                    Console.Error.WriteLine(JsonSerializer.Serialize(new
                    { timestamp = DateTimeOffset.UtcNow, eventName, code }));
            }
            """),
        new("src/App/CommandRouter.cs", """
            namespace App;

            public sealed class CommandRouter(TimeProvider time)
            {
                private readonly long started = time.GetTimestamp();
                public string? Route(string? message)
                {
                    if (string.IsNullOrWhiteSpace(message) || !message.TrimStart().StartsWith('!')) return null;
                    var parts = message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var command = parts[0].ToLowerInvariant();
                    if (command is not ("!ping" or "!help" or "!uptime")) return "Unknown command. Use !help.";
                    if (parts.Length != 1) return "Usage: " + command;
                    if (command == "!ping") return "Pong!";
                    if (command == "!help") return "Commands: !ping, !help, !uptime.";
                    var elapsed = time.GetElapsedTime(started);
                    return $"Uptime: {elapsed.Days}d {elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
                }
            }
            """),
        new("src/App/BotHost.cs", """
            namespace App;

            public sealed class BotHost(IChatTransport transport, CommandRouter commands)
            {
                public async Task RunAsync(CancellationToken cancellation)
                {
                    await foreach (var message in transport.ReadAllAsync(cancellation).WithCancellation(cancellation))
                    {
                        var response = commands.Route(message.Text);
                        if (response is null) continue;
                        try { await transport.SendAsync(message, response, cancellation); }
                        catch (ChatTransportException error) when (error.Retryable)
                        {
                            BotLog.Write("send_failed", error.Code);
                        }
                    }
                }
            }
            """),
        new("src/App/TwitchConfiguration.cs", """
            namespace App;

            public sealed class TwitchConfiguration
            {
                public string ClientId { get; }
                public string UserId { get; }
                public string BroadcasterId { get; }
                public string AccessValue { get; }
                public TwitchConfiguration(string clientId, string userId, string broadcasterId, string accessValue)
                {
                    if (!Valid(clientId) || !Valid(accessValue) ||
                        !Numeric(userId) || !Numeric(broadcasterId))
                        throw new ChatTransportException("invalid_environment_configuration", false);
                    ClientId = clientId; UserId = userId; BroadcasterId = broadcasterId; AccessValue = accessValue;
                }
                private static bool Valid(string? value) => !string.IsNullOrEmpty(value) &&
                    value.Length <= 2048 && value.All(char.IsAsciiLetterOrDigit);
                private static bool Numeric(string? value) => !string.IsNullOrEmpty(value) &&
                    value.Length <= 32 && value.All(char.IsAsciiDigit);
                public static TwitchConfiguration FromEnvironment() => new(
                    Environment.GetEnvironmentVariable("TWITCH_CLIENT_ID") ?? "",
                    Environment.GetEnvironmentVariable("TWITCH_USER_ID") ?? "",
                    Environment.GetEnvironmentVariable("TWITCH_BROADCASTER_ID") ?? "",
                    Environment.GetEnvironmentVariable("TWITCH_ACCESS_TOKEN") ?? "");
            }
            """),
        new("src/App/TwitchApi.cs", """
            using System.Net.Http.Headers;
            using System.Net.Http.Json;
            using System.Text.Json;
            namespace App;

            public sealed class TwitchApi(HttpClient http, TwitchConfiguration configuration, TimeProvider time) : IDisposable
            {
                private readonly SemaphoreSlim sending = new(1, 1);
                private long? lastAttempt;
                public async Task SubscribeAsync(string sessionId, CancellationToken cancellation)
                {
                    await PostAsync("eventsub/subscriptions", new
                    {
                        type = "channel.chat.message", version = "1",
                        condition = new { broadcaster_user_id = configuration.BroadcasterId, user_id = configuration.UserId },
                        transport = new { method = "websocket", session_id = sessionId }
                    }, cancellation);
                }
                public async Task SendAsync(ChatMessage original, string text, CancellationToken cancellation)
                {
                    if (text.Length is < 1 or > 500) throw new ChatTransportException("invalid_message_length", false);
                    await sending.WaitAsync(cancellation);
                    try
                    {
                        if (lastAttempt is { } previous)
                        {
                            var remaining = TimeSpan.FromMilliseconds(1600) - time.GetElapsedTime(previous);
                            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, time, cancellation);
                        }
                        lastAttempt = time.GetTimestamp();
                        var body = await PostAsync("chat/messages", new
                        {
                            broadcaster_id = configuration.BroadcasterId, sender_id = configuration.UserId,
                            message = text, reply_parent_message_id = original.Id
                        }, cancellation);
                        if (!body.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array ||
                            data.GetArrayLength() != 1 || !data[0].TryGetProperty("is_sent", out var sent) ||
                            sent.ValueKind != JsonValueKind.True)
                            throw new ChatTransportException("message_not_sent", false);
                    }
                    finally { sending.Release(); }
                }
                private async Task<JsonElement> PostAsync(string path, object body, CancellationToken cancellation)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/" + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.AccessValue);
                    request.Headers.Add("Client-Id", configuration.ClientId);
                    request.Content = JsonContent.Create(body);
                    try
                    {
                        using var response = await http.SendAsync(request, cancellation);
                        EnsureSuccess(response);
                        return await ReadJsonAsync(response, cancellation);
                    }
                    catch (HttpRequestException) { throw new ChatTransportException("http_unavailable", true); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    { throw new ChatTransportException("http_timeout", true); }
                }
                public static void EnsureSuccess(HttpResponseMessage response)
                {
                    var status = (int)response.StatusCode;
                    if (!response.IsSuccessStatusCode)
                        throw new ChatTransportException("http_" + status, status == 429 || status >= 500);
                }
                public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                            throw new ChatTransportException("invalid_http_json", false);
                        return document.RootElement.Clone();
                    }
                    catch (JsonException) { throw new ChatTransportException("invalid_http_json", false); }
                }
                public static string Text(JsonElement element, string property) =>
                    element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
                    value.ValueKind == JsonValueKind.String ? value.GetString()! :
                    throw new ChatTransportException("invalid_protocol_field", false);
                public void Dispose() => sending.Dispose();
            }
            """),
        new("src/App/TwitchAuthentication.cs", """
            using System.Net.Http.Headers;
            using System.Text.Json;
            namespace App;

            public sealed class TwitchAuthentication(HttpClient http, TwitchConfiguration configuration, TimeProvider time)
            {
                private TimeSpan validationInterval = TimeSpan.FromMinutes(30);
                public async Task ValidateAsync(CancellationToken cancellation)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
                    request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", configuration.AccessValue);
                    using var response = await http.SendAsync(request, cancellation);
                    TwitchApi.EnsureSuccess(response);
                    var body = await TwitchApi.ReadJsonAsync(response, cancellation);
                    if (TwitchApi.Text(body, "client_id") != configuration.ClientId ||
                        TwitchApi.Text(body, "user_id") != configuration.UserId ||
                        !body.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt32(out var seconds) || seconds <= 0 ||
                        !body.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array ||
                        !new[] { "user:read:chat", "user:write:chat" }.All(required =>
                            scopes.EnumerateArray().Any(scope => scope.ValueKind == JsonValueKind.String && scope.GetString() == required)))
                        throw new ChatTransportException("oauth_identity_scopes_or_expiry", false);
                    validationInterval = TimeSpan.FromSeconds(Math.Min(1800, seconds));
                }
                public async Task MonitorAsync(CancellationToken cancellation)
                {
                    while (true)
                    {
                        await Task.Delay(validationInterval, time, cancellation);
                        await ValidateAsync(cancellation);
                    }
                }
            }
            """),
        new("src/App/EventSubTransport.cs", """
            using System.Net.WebSockets;
            using System.Runtime.CompilerServices;
            using System.Text;
            using System.Text.Json;
            namespace App;

            public interface IEventSocket : IAsyncDisposable
            {
                Task ConnectAsync(Uri address, CancellationToken cancellation);
                Task<string> ReceiveAsync(CancellationToken cancellation);
            }
            public sealed class TwitchEventSocket : IEventSocket
            {
                private readonly ClientWebSocket socket = new();
                public Task ConnectAsync(Uri address, CancellationToken cancellation) => socket.ConnectAsync(address, cancellation);
                public async Task<string> ReceiveAsync(CancellationToken cancellation)
                {
                    var buffer = new byte[4096];
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation);
                        if (result.MessageType != WebSocketMessageType.Text)
                            throw new ChatTransportException("socket_closed_or_nontext", true);
                        if (message.Length + result.Count > 65536)
                            throw new ChatTransportException("event_too_large", false);
                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    return Encoding.UTF8.GetString(message.ToArray());
                }
                public ValueTask DisposeAsync() { socket.Dispose(); return ValueTask.CompletedTask; }
            }
            public sealed class EventSubTransport(TwitchApi api, TwitchConfiguration configuration,
                TimeProvider time, Func<IEventSocket>? sockets = null) : IChatTransport
            {
                private IEventSocket? active;
                private int keepaliveSeconds = 10;
                private readonly HashSet<string> seen = new(StringComparer.Ordinal);
                private readonly Queue<string> recent = new();
                public static TimeSpan RetryDelay(int failures) =>
                    TimeSpan.FromSeconds(Math.Pow(2, Math.Clamp(failures, 0, 6)));
                public static Uri ReconnectAddress(string value)
                {
                    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "wss" ||
                        uri.Host != "eventsub.wss.twitch.tv" || uri.Port != 443 ||
                        uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/ws")
                        throw new ChatTransportException("unsafe_reconnect_address", false);
                    return uri;
                }
                public Task SendAsync(ChatMessage original, string response, CancellationToken cancellation) =>
                    api.SendAsync(original, response, cancellation);

                public async IAsyncEnumerable<ChatMessage> ReadAllAsync(
                    [EnumeratorCancellation] CancellationToken cancellation)
                {
                    var failures = 0;
                    try
                    {
                        while (true)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            ChatMessage? message;
                            try { message = await NextAsync(cancellation); }
                            catch (Exception error) when (error is ChatTransportException { Retryable: true } or
                                WebSocketException or HttpRequestException or IOException)
                            {
                                await DisconnectAsync();
                                BotLog.Write("reconnecting", "transient_transport_failure");
                                await Task.Delay(RetryDelay(failures), time, cancellation);
                                failures = Math.Min(6, failures + 1);
                                continue;
                            }
                            if (message is not null) { failures = 0; yield return message; }
                        }
                    }
                    finally { await DisconnectAsync(); }
                }
                private async Task DisconnectAsync()
                {
                    if (active is { } socket) { active = null; await socket.DisposeAsync(); }
                }
                private async Task ConnectAsync(Uri address, bool transferred, CancellationToken cancellation)
                {
                    var candidate = sockets?.Invoke() ?? new TwitchEventSocket();
                    var adopted = false;
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                        deadline.CancelAfter(TimeSpan.FromSeconds(10));
                        try
                        {
                            await candidate.ConnectAsync(address, deadline.Token);
                            using var welcome = Parse(await candidate.ReceiveAsync(deadline.Token));
                            if (TwitchApi.Text(welcome.RootElement.GetProperty("metadata"), "message_type") != "session_welcome")
                                throw new ChatTransportException("missing_session_welcome", false);
                            var session = welcome.RootElement.GetProperty("payload").GetProperty("session");
                            var sessionId = TwitchApi.Text(session, "id");
                            if (!session.TryGetProperty("keepalive_timeout_seconds", out var timeout) ||
                                !timeout.TryGetInt32(out var seconds) || seconds is < 1 or > 600)
                                throw new ChatTransportException("invalid_keepalive_timeout", false);
                            if (!transferred) await api.SubscribeAsync(sessionId, deadline.Token);
                            var previous = active;
                            active = candidate; keepaliveSeconds = seconds; adopted = true;
                            if (previous is not null) await previous.DisposeAsync();
                            BotLog.Write(transferred ? "session_transferred" : "session_connected");
                        }
                        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                        { throw new ChatTransportException("welcome_timeout", true); }
                    }
                    finally { if (!adopted) await candidate.DisposeAsync(); }
                }
                private async Task<ChatMessage?> NextAsync(CancellationToken cancellation)
                {
                    if (active is null)
                        await ConnectAsync(new Uri("wss://eventsub.wss.twitch.tv/ws"), false, cancellation);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    deadline.CancelAfter(TimeSpan.FromSeconds(keepaliveSeconds + 1));
                    string frame;
                    try { frame = await active!.ReceiveAsync(deadline.Token); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    { throw new ChatTransportException("keepalive_timeout", true); }
                    using var document = Parse(frame);
                    var root = document.RootElement;
                    var kind = TwitchApi.Text(root.GetProperty("metadata"), "message_type");
                    var payload = root.GetProperty("payload");
                    if (kind == "session_keepalive") return null;
                    if (kind == "revocation") throw new ChatTransportException("subscription_revoked", false);
                    if (kind == "session_reconnect")
                    {
                        var address = ReconnectAddress(TwitchApi.Text(payload.GetProperty("session"), "reconnect_url"));
                        await ConnectAsync(address, true, cancellation);
                        return null;
                    }
                    if (kind != "notification") throw new ChatTransportException("unknown_event_type", false);
                    var subscription = payload.GetProperty("subscription");
                    if (TwitchApi.Text(subscription, "type") != "channel.chat.message" ||
                        TwitchApi.Text(subscription, "version") != "1") return null;
                    var data = payload.GetProperty("event");
                    if (TwitchApi.Text(data, "broadcaster_user_id") != configuration.BroadcasterId)
                        throw new ChatTransportException("unexpected_channel", false);
                    var id = TwitchApi.Text(data, "message_id");
                    var sender = TwitchApi.Text(data, "chatter_user_id");
                    if (sender == configuration.UserId || !seen.Add(id)) return null;
                    recent.Enqueue(id);
                    if (recent.Count > 512) seen.Remove(recent.Dequeue());
                    return new(id, sender, TwitchApi.Text(data.GetProperty("message"), "text"));
                }
                private static JsonDocument Parse(string frame)
                {
                    try { return JsonDocument.Parse(frame); }
                    catch (JsonException) { throw new ChatTransportException("invalid_event_json", false); }
                }
            }
            """),
        new("src/App/Program.cs", """
            using App;

            if (Environment.GetEnvironmentVariable("TWITCH_CONNECT_LIVE") != "1")
            {
                BotLog.Write("live_connection_disabled", "set_live_opt_in_only_after_manual_review");
                return 1;
            }
            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
            try
            {
                var configuration = TwitchConfiguration.FromEnvironment();
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                var time = TimeProvider.System;
                var authentication = new TwitchAuthentication(http, configuration, time);
                await authentication.ValidateAsync(shutdown.Token);
                using var api = new TwitchApi(http, configuration, time);
                var transport = new EventSubTransport(api, configuration, time);
                var bot = new BotHost(transport, new CommandRouter(time)).RunAsync(shutdown.Token);
                var monitoring = authentication.MonitorAsync(shutdown.Token);
                await Task.WhenAny(bot, monitoring);
                shutdown.Cancel();
                await Task.WhenAll(bot, monitoring);
                return 0;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            { BotLog.Write("stopped"); return 0; }
            catch (ChatTransportException error)
            { BotLog.Write("bot_failed", error.Code); return 1; }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or
                System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            { BotLog.Write("bot_failed", "network_or_protocol_failure"); return 1; }
            finally { shutdown.Cancel(); }
            """),
        new("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsTestProject>true</IsTestProject>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../../src/App/App.csproj" />
                <PackageReference Include="xunit" Version="[2.9.3]" />
                <PackageReference Include="xunit.runner.visualstudio" Version="[3.1.4]" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="[17.14.1]" />
              </ItemGroup>
            </Project>
            """),
        new("tests/App.Tests/UnitTests.cs", """"
            using App;
            using System.Net;
            using System.Runtime.CompilerServices;
            using System.Text.Json;
            using Xunit;

            public sealed class UnitTests
            {
                [Theory]
                [InlineData(null, null)]
                [InlineData("", null)]
                [InlineData("hello everyone", null)]
                [InlineData("!ping", "Pong!")]
                [InlineData("  !PING  ", "Pong!")]
                [InlineData("!unknown", "Unknown command. Use !help.")]
                [InlineData("!ping extra", "Usage: !ping")]
                [InlineData("!help extra", "Usage: !help")]
                [InlineData("!uptime extra", "Usage: !uptime")]
                public void RoutesMessages(string? input, string? expected) =>
                    Assert.Equal(expected, new CommandRouter(new Clock()).Route(input));

                [Fact]
                public void HelpListsAllCommands()
                {
                    var response = new CommandRouter(new Clock()).Route("!HeLp");
                    foreach (var command in new[] { "!ping", "!help", "!uptime" }) Assert.Contains(command, response);
                }
                [Fact]
                public void UptimeUsesMonotonicBotLifetime()
                {
                    var clock = new Clock();
                    var commands = new CommandRouter(clock);
                    Assert.Equal("Uptime: 0d 00:00:00", commands.Route("!uptime"));
                    clock.Ticks = TimeSpan.FromDays(2).Ticks + TimeSpan.FromHours(3).Ticks +
                        TimeSpan.FromMinutes(4).Ticks + TimeSpan.FromSeconds(5).Ticks;
                    Assert.Equal("Uptime: 2d 03:04:05", commands.Route("!UPTIME"));
                }
                [Fact]
                public async Task DispatchesOnlyCommandsAndContinuesAfterTransientSendFailure()
                {
                    var transport = new ChatStub { Failure = new ChatTransportException("http_429", true) };
                    await new BotHost(transport, new CommandRouter(new Clock())).RunAsync(default);
                    Assert.Equal(new[] { "Pong!", "Commands: !ping, !help, !uptime." }, transport.Attempts);
                }
                [Fact]
                public async Task FatalTransportFailureIsNotHidden()
                {
                    var transport = new ChatStub { Failure = new ChatTransportException("http_401", false) };
                    var error = await Assert.ThrowsAsync<ChatTransportException>(() =>
                        new BotHost(transport, new CommandRouter(new Clock())).RunAsync(default));
                    Assert.Equal("http_401", error.Code);
                }
                [Fact]
                public async Task CancellationIsNotSwallowed()
                {
                    using var cancellation = new CancellationTokenSource();
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        new BotHost(new ChatStub(), new CommandRouter(new Clock())).RunAsync(cancellation.Token));
                }
                [Theory]
                [InlineData(0, 1)]
                [InlineData(1, 2)]
                [InlineData(20, 64)]
                public void BackoffIsBounded(int attempt, int seconds) =>
                    Assert.Equal(TimeSpan.FromSeconds(seconds), EventSubTransport.RetryDelay(attempt));
                [Theory]
                [InlineData("ws://eventsub.wss.twitch.tv/ws")]
                [InlineData("wss://example.org/ws")]
                [InlineData("wss://eventsub.wss.twitch.tv.evil.test/ws")]
                [InlineData("wss://eventsub.wss.twitch.tv:444/ws")]
                public void RejectsUntrustedReconnectDestinations(string value) =>
                    Assert.Throws<ChatTransportException>(() => EventSubTransport.ReconnectAddress(value));

                private static TwitchConfiguration Config => new("client", "1", "2", "offlineTestValue");
                [Theory]
                [InlineData("1", "user:read:chat", "user:write:chat", true)]
                [InlineData("9", "user:read:chat", "user:write:chat", false)]
                [InlineData("1", "user:read:chat", "missing", false)]
                public async Task ValidatesOAuthIdentityAndBothScopes(string user, string read, string write, bool valid)
                {
                    using var http = new HttpClient(new HttpStub(_ => JsonSerializer.Serialize(new
                    { client_id = "client", user_id = user, scopes = new[] { read, write }, expires_in = 3600 })));
                    var operation = new TwitchAuthentication(http, Config, new Clock()).ValidateAsync(default);
                    if (valid) await operation;
                    else await Assert.ThrowsAsync<ChatTransportException>(() => operation);
                }
                [Theory]
                [InlineData(true)]
                [InlineData(false)]
                public async Task SendingChecksActualDeliveryAndUsesHelix(bool delivered)
                {
                    var handler = new HttpStub(_ => JsonSerializer.Serialize(new { data = new[] { new { is_sent = delivered } } }));
                    using var http = new HttpClient(handler);
                    using var api = new TwitchApi(http, Config, new Clock());
                    var operation = api.SendAsync(new("message", "3", "!ping"), "Pong!", default);
                    if (delivered) await operation;
                    else await Assert.ThrowsAsync<ChatTransportException>(() => operation);
                    Assert.Equal("https://api.twitch.tv/helix/chat/messages", Assert.Single(handler.Addresses));
                    using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
                    Assert.Equal("1", body.RootElement.GetProperty("sender_id").GetString());
                    Assert.Equal("2", body.RootElement.GetProperty("broadcaster_id").GetString());
                    Assert.Equal("message", body.RootElement.GetProperty("reply_parent_message_id").GetString());
                }
                [Fact]
                public async Task ConsecutiveSendsRespectTheSingleChannelRateLimit()
                {
                    var handler = new HttpStub(_ => """{"data":[{"is_sent":true}]}""");
                    using var http = new HttpClient(handler);
                    var clock = new Clock();
                    using var api = new TwitchApi(http, Config, clock);
                    await api.SendAsync(new("first", "3", "!ping"), "Pong!", default);
                    var second = api.SendAsync(new("second", "3", "!ping"), "Pong!", default);
                    await clock.WaitForTimerAsync();
                    Assert.Single(handler.Addresses);
                    clock.Advance(TimeSpan.FromMilliseconds(1600));
                    await second.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(2, handler.Addresses.Count);
                }
                [Fact]
                public async Task OAuthIsRevalidatedBeforeExpiryAndStopsOnInvalidation()
                {
                    var requests = 0;
                    using var http = new HttpClient(new HttpStub(_ => JsonSerializer.Serialize(new
                    { client_id = "client", user_id = "1", scopes = new[] { "user:read:chat", "user:write:chat" },
                        expires_in = requests++ == 0 ? 2 : 0 })));
                    var clock = new Clock();
                    var authentication = new TwitchAuthentication(http, Config, clock);
                    await authentication.ValidateAsync(default);
                    var monitoring = authentication.MonitorAsync(default);
                    await clock.WaitForTimerAsync();
                    clock.Advance(TimeSpan.FromSeconds(2));
                    await Assert.ThrowsAsync<ChatTransportException>(() => monitoring.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(2, requests);
                }
                [Fact]
                public async Task NetworkFailuresReconnectWithIncreasingDelaysAndDisposeFailedSockets()
                {
                    using var http = new HttpClient(new HttpStub(_ => "{}"));
                    using var api = new TwitchApi(http, Config, new Clock());
                    var first = new SocketStub { OnConnect = () => throw new ChatTransportException("offline", true) };
                    var second = new SocketStub { OnConnect = () => throw new ChatTransportException("offline", true) };
                    var third = new SocketStub(Welcome("restored"), Notification("received", "3"));
                    var sockets = new Queue<IEventSocket>(new[] { first, second, third });
                    var clock = new Clock();
                    var transport = new EventSubTransport(api, Config, clock, sockets.Dequeue);
                    await using var messages = transport.ReadAllAsync(default).GetAsyncEnumerator();
                    var receiving = messages.MoveNextAsync().AsTask();
                    await clock.WaitForTimerAsync();
                    Assert.True(first.Disposed);
                    clock.Advance(TimeSpan.FromSeconds(1));
                    await clock.WaitForTimerAsync();
                    Assert.True(second.Disposed);
                    clock.Advance(TimeSpan.FromSeconds(1));
                    Assert.False(receiving.IsCompleted);
                    clock.Advance(TimeSpan.FromSeconds(1));
                    Assert.True(await receiving.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal("received", messages.Current.Id);
                }
                [Fact]
                public async Task TransfersSessionWithoutResubscribingAndDeduplicatesMessages()
                {
                    var handler = new HttpStub(_ => "{}");
                    using var http = new HttpClient(handler);
                    using var api = new TwitchApi(http, Config, new Clock());
                    var first = new SocketStub(Welcome("first"), Notification("self", "1"), Notification("once", "3"),
                        """{"metadata":{"message_type":"session_reconnect"},"payload":{"session":{"reconnect_url":"wss://eventsub.wss.twitch.tv/ws?session=transfer"}}}""");
                    var second = new SocketStub(Welcome("second"), Notification("once", "3"), Notification("next", "3"));
                    second.OnConnect = () => Assert.False(first.Disposed);
                    var sockets = new Queue<IEventSocket>(new[] { first, second });
                    var transport = new EventSubTransport(api, Config, new Clock(), sockets.Dequeue);
                    var messages = new List<string>();
                    await foreach (var message in transport.ReadAllAsync(default))
                    {
                        messages.Add(message.Id);
                        if (messages.Count == 2) break;
                    }
                    Assert.Equal(new[] { "once", "next" }, messages);
                    Assert.Single(handler.Addresses);
                    Assert.Contains("eventsub/subscriptions", handler.Addresses[0]);
                    Assert.Contains("\"session_id\":\"first\"", handler.Bodies[0]);
                    Assert.True(first.Disposed);
                    Assert.True(second.Disposed);
                    Assert.Equal("wss://eventsub.wss.twitch.tv/ws?session=transfer", second.Address?.AbsoluteUri);
                }
                [Fact]
                public async Task RevocationStopsInsteadOfReconnectingForever()
                {
                    using var http = new HttpClient(new HttpStub(_ => "{}"));
                    using var api = new TwitchApi(http, Config, new Clock());
                    var socket = new SocketStub(Welcome("first"),
                        """{"metadata":{"message_type":"revocation"},"payload":{}}""");
                    var transport = new EventSubTransport(api, Config, new Clock(), () => socket);
                    var error = await Assert.ThrowsAsync<ChatTransportException>(async () =>
                    { await foreach (var message in transport.ReadAllAsync(default)) Assert.Fail("Unexpected message."); });
                    Assert.Equal("subscription_revoked", error.Code);
                    Assert.True(socket.Disposed);
                }
                private static string Welcome(string id) => JsonSerializer.Serialize(new
                { metadata = new { message_type = "session_welcome" }, payload = new { session = new { id, keepalive_timeout_seconds = 10 } } });
                private static string Notification(string id, string sender) => JsonSerializer.Serialize(new
                {
                    metadata = new { message_type = "notification" },
                    payload = new { subscription = new { type = "channel.chat.message", version = "1" },
                        @event = new { broadcaster_user_id = "2", message_id = id, chatter_user_id = sender, message = new { text = "!ping" } } }
                });
                private sealed class Clock : TimeProvider
                {
                    public long Ticks { get; set; }
                    private readonly List<Timer> timers = [];
                    private readonly SemaphoreSlim scheduled = new(0);
                    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
                    public override long GetTimestamp() => Ticks;
                    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
                    {
                        var timer = new Timer(this, callback, state);
                        timer.Change(dueTime, period);
                        timers.Add(timer);
                        scheduled.Release();
                        return timer;
                    }
                    public Task WaitForTimerAsync() => scheduled.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(task =>
                    { Assert.True(task.GetAwaiter().GetResult(), "Expected an offline virtual timer."); }, TaskScheduler.Default);
                    public void Advance(TimeSpan duration)
                    {
                        Ticks += duration.Ticks;
                        foreach (var timer in timers.ToArray()) timer.Fire();
                    }
                    private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
                    {
                        private long due = long.MaxValue;
                        public bool Change(TimeSpan dueTime, TimeSpan period)
                        { due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.Ticks + dueTime.Ticks; return true; }
                        public void Fire()
                        {
                            if (clock.Ticks < due) return;
                            due = long.MaxValue; callback(state);
                        }
                        public void Dispose() => due = long.MaxValue;
                        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
                    }
                }
                private sealed class ChatStub : IChatTransport
                {
                    public ChatTransportException? Failure { get; init; }
                    public List<string> Attempts { get; } = [];
                    public async IAsyncEnumerable<ChatMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellation)
                    {
                        await Task.CompletedTask;
                        foreach (var text in new[] { "hello", "!ping", "!help" })
                        { cancellation.ThrowIfCancellationRequested(); yield return new("message", "3", text); }
                    }
                    public Task SendAsync(ChatMessage original, string response, CancellationToken cancellation)
                    {
                        Attempts.Add(response);
                        if (Attempts.Count == 1 && Failure is not null) throw Failure;
                        return Task.CompletedTask;
                    }
                }
                private sealed class HttpStub(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
                {
                    public List<string> Addresses { get; } = [];
                    public List<string> Bodies { get; } = [];
                    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
                    {
                        Addresses.Add(request.RequestUri!.AbsoluteUri);
                        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellation));
                        return new(HttpStatusCode.OK) { Content = new StringContent(reply(request)) };
                    }
                }
                private sealed class SocketStub(params string[] frames) : IEventSocket
                {
                    private readonly Queue<string> messages = new(frames);
                    public bool Disposed { get; private set; }
                    public Uri? Address { get; private set; }
                    public Action? OnConnect { get; set; }
                    public Task ConnectAsync(Uri address, CancellationToken cancellation)
                    { OnConnect?.Invoke(); Address = address; return Task.CompletedTask; }
                    public Task<string> ReceiveAsync(CancellationToken cancellation) => Task.FromResult(messages.Dequeue());
                    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
                }
            }
            """"),
        new("README.md", """
            # Twitch bot template v1 (.NET 10)

            Commands: !ping -> Pong!, !help -> command list, !uptime -> bot process
            lifetime (not stream duration), using an injectable monotonic TimeProvider.
            Commands are case insensitive. Ordinary messages are ignored; unknown
            commands and unexpected arguments receive explicit guidance.

            ## Authentication and manual live connection
            This installed chatbot uses EventSub WebSocket channel.chat.message v1 and
            Helix Send Chat Message, not legacy IRC. Only .NET libraries are needed.
            Register an application with Twitch. Obtain a User Access Token yourself
            with user:read:chat and user:write:chat for the bot account. The broadcaster
            account can itself be the bot. Use Twitch's official OAuth tools/flows:
            https://dev.twitch.tv/docs/chat/authenticating/
            https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/

            Supply TWITCH_CLIENT_ID, TWITCH_USER_ID, TWITCH_BROADCASTER_ID and
            TWITCH_ACCESS_TOKEN through the execution environment, NEVER source files,
            Studio prompts, JSON, logs, screenshots or ZIP exports. Do not include an
            oauth: prefix. Tokens are validated at startup and every 30 minutes;
            wrong identity/scopes, expiry or revocation stop the bot. Acquisition and
            renewal are manual: automatic login/refresh and secret storage are not
            implemented. Restart with a newly obtained token when required.

            No live connection starts unless you manually set TWITCH_CONNECT_LIVE=1
            and separately choose to run this untrusted application after review.
            AIPlatform's Docker verification does NOT run the console entry point.
            No real Twitch account/session has been tested by template validation.

            ## Transport and reliability
            IChatTransport separates commands from OAuth, Helix and EventSub.
            Validate OAuth before connecting; subscribe after session_welcome.
            A reconnect handoff keeps the old socket until the new welcome, validates
            Twitch's TLS destination and transfers subscriptions without duplicating
            them. Network loss/keepalive expiry creates a fresh subscription with
            capped exponential backoff (1 to 64 seconds). Cancellation stops retries.
            Revocation is fatal and requires manual intervention.
            Recent message IDs are deduplicated (512 entries); self messages ignored.
            Disconnected messages cannot be replayed. Send attempts are spaced at least
            1.6 seconds apart for this single-channel bot; failed sends are NOT replayed
            to avoid duplicate replies. HTTP 429/transient HTTP errors are logged with
            fixed codes; permanent failures stop. is_sent=false is never a success.
            Structured logs omit credentials, raw messages and HTTP response bodies.

            ## Offline verification after explicit approval
            Build Release and run xUnit in AIPlatform's existing network-disabled
            .NET Docker sandbox. Runtime has no NuGet dependencies. Exact test packages:
            xunit [2.9.3], xunit.runner.visualstudio [3.1.4],
            Microsoft.NET.Test.Sdk [17.14.1], restored from the approved offline cache.
            Tests use fake HTTP handlers/sockets/time/transport, never Twitch or secrets.
            The configuration, transport and contract tests are protected; DeveloperAgent
            implements CommandRouter and BotHost without changing approved dependencies.
            https://dev.twitch.tv/docs/eventsub/handling-websocket-events/
            https://dev.twitch.tv/docs/chat/send-receive-messages/
            https://dev.twitch.tv/docs/authentication/validate-tokens/
            """)
    ];
}
