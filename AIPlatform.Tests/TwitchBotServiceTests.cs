using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class TwitchBotServiceTests
{
    private const string ClientId = "abcdefghijklmnopqrstuvwxyz1234";

    [Fact]
    public async Task BotConfigurationAndDeviceAuthorizationPersistWithoutExposingTokens()
    {
        using var fixture = new TwitchFixture();
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        Assert.Equal(new TwitchCommand("!hello", "Salut le chat !", 5), bot.Configuration.Commands.Single());
        var device = await fixture.Service.BeginConnectAsync(bot.Configuration.Id, default);
        Assert.Equal("https://www.twitch.tv/activate", device.VerificationUri);
        Assert.Equal("CODE-123", device.UserCode);

        var authorized = await fixture.Service.PollConnectAsync(bot.Configuration.Id, default);
        Assert.True(authorized.Authorized);
        Assert.Equal("sampleuser", authorized.AccountLogin);
        var persisted = new TwitchBotStore(fixture.Options).Get(bot.Configuration.Id)!;
        Assert.Equal(bot.Configuration.Commands, persisted.Commands);
        var configText = File.ReadAllText(Path.Combine(fixture.Options.DataDirectory, "twitch", "bots.json"));
        Assert.DoesNotContain("access-secret", configText);
        Assert.DoesNotContain("refresh-secret", configText);
        Assert.DoesNotContain("access-secret", fixture.Vault.Read(bot.Configuration.Id)!.ToString());
    }

    [Fact]
    public async Task StartsReceivesHelloAppliesCooldownAndStopsTheSingleInstance()
    {
        using var fixture = new TwitchFixture();
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        fixture.Vault.Save(bot.Configuration.Id, fixture.Credentials);

        using var requestCancellation = new CancellationTokenSource();
        var started = await fixture.Service.StartAsync(bot.Configuration.Id, requestCancellation.Token);
        Assert.Equal("Connected", started.State);
        requestCancellation.Cancel();
        Assert.Equal("Connected", fixture.Service.Get(bot.Configuration.Id)!.State);
        var duplicate = await Assert.ThrowsAsync<TwitchIntegrationException>(
            () => fixture.Service.StartAsync(bot.Configuration.Id, default));
        Assert.Equal("bot_running", duplicate.Code);
        Assert.True(fixture.Service.TestCommand(bot.Configuration.Id, "!hello").Matched);

        await fixture.Chat.DeliverAsync(new("message-1", "viewer-1", "!hello"));
        await fixture.Chat.DeliverAsync(new("message-2", "viewer-1", "!hello"));
        Assert.Equal("Salut le chat !", Assert.Single(fixture.Chat.SentMessages).Message);
        Assert.Contains(fixture.Service.Get(bot.Configuration.Id)!.Events, item => item.Kind == "cooldown");

        var stopped = await fixture.Service.StopAsync(bot.Configuration.Id, default);
        Assert.Equal("Stopped", stopped.State);
        Assert.Equal(1, fixture.Chat.CompletedSessions);
    }

    [Fact]
    public void CommandConfigurationUpdatesPersistAndCanBeTestedWithoutAConnection()
    {
        using var fixture = new TwitchFixture();
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        var changed = fixture.Service.Update(bot.Configuration.Id, "Renamed Bot", "OtherChannel",
        [
            new TwitchCommand("!hello", "Bonjour !", 10),
            new TwitchCommand("!off", "Disabled", Enabled: false)
        ]);
        var persisted = new TwitchBotStore(fixture.Options).Get(bot.Configuration.Id)!;
        Assert.Equal("Renamed Bot", persisted.Name);
        Assert.Equal("otherchannel", persisted.Channel);
        Assert.Equal(changed.Configuration.Commands, persisted.Commands);

        var result = fixture.Service.TestCommand(bot.Configuration.Id, "!hello arguments");
        Assert.True(result.Matched);
        Assert.Equal("Bonjour !", result.Response);
        Assert.False(fixture.Service.TestCommand(bot.Configuration.Id, "!off").Matched);
        Assert.Equal(0, fixture.Chat.Connections);
    }

    [Fact]
    public async Task TokenValidationFailureIsReportedWithoutStartingAChatSession()
    {
        using var fixture = new TwitchFixture();
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        fixture.Vault.Save(bot.Configuration.Id, fixture.Credentials);
        fixture.OAuth.ValidateFailure = new("invalid_token", "Twitch authorization is no longer valid.", 401);
        fixture.OAuth.RefreshFailure = new("oauth_refresh_failed", "Twitch authorization expired.", 400);

        var failure = await Assert.ThrowsAsync<TwitchIntegrationException>(
            () => fixture.Service.StartAsync(bot.Configuration.Id, default));
        Assert.Equal("authorization_required", failure.Code);
        Assert.Equal(0, fixture.Chat.Connections);
        Assert.Equal("Error", fixture.Service.Get(bot.Configuration.Id)!.State);
        Assert.False(fixture.Service.Get(bot.Configuration.Id)!.Authorized);
    }

    [Fact]
    public async Task RefreshesAndPersistsTheRotatedRefreshTokenBeforeConnecting()
    {
        using var fixture = new TwitchFixture();
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        fixture.Vault.Save(bot.Configuration.Id, fixture.Credentials);
        fixture.OAuth.ValidateFailureOnce = true;

        var started = await fixture.Service.StartAsync(bot.Configuration.Id, default);
        Assert.Equal("Connected", started.State);
        Assert.Equal("new-access-secret", fixture.Vault.Read(bot.Configuration.Id)!.AccessToken);
        Assert.Equal("new-refresh-secret", fixture.Vault.Read(bot.Configuration.Id)!.RefreshToken);
        await fixture.Service.StopAsync(bot.Configuration.Id, default);
    }

    [Fact]
    public async Task UnexpectedEventSubDisconnectReconnectsUntilExplicitStop()
    {
        using var fixture = new TwitchFixture();
        fixture.Chat.DropFirstConnection = true;
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        fixture.Vault.Save(bot.Configuration.Id, fixture.Credentials);

        await fixture.Service.StartAsync(bot.Configuration.Id, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (fixture.Chat.Connections < 2 || fixture.Service.Get(bot.Configuration.Id)!.State != "Connected")
            await Task.Delay(20, timeout.Token);

        Assert.Equal(2, fixture.Chat.Connections);
        await fixture.Service.StopAsync(bot.Configuration.Id, default);
    }

    [Fact]
    public async Task OAuthClientUsesDeviceFlowAndRejectsUnexpectedVerificationUrls()
    {
        Uri? capturedUri = null;
        string? capturedBody = null;
        using var client = new HttpClient(new FakeHandler((request, _) =>
        {
            capturedUri = request.RequestUri;
            return CaptureBodyAsync(request, body =>
            {
                capturedBody = body;
                return Task.FromResult(Json(HttpStatusCode.OK, """
                {"device_code":"device-secret","user_code":"ABCD-1234","verification_uri":"https://www.twitch.tv/activate","expires_in":600,"interval":5}
                """));
            });
        }));
        var oauth = new TwitchOAuthClient(client);
        var authorization = await oauth.BeginDeviceCodeAsync(ClientId, default);
        Assert.Equal("ABCD-1234", authorization.UserCode);
        Assert.Equal("https://id.twitch.tv/oauth2/device", capturedUri!.AbsoluteUri);
        Assert.Contains("user%3Aread%3Achat", capturedBody);
        Assert.Contains("user%3Awrite%3Achat", capturedBody);

        using var invalidClient = new HttpClient(new FakeHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """
                {"device_code":"device","user_code":"ABCD","verification_uri":"https://example.com/","expires_in":600,"interval":5}
                """))));
        var invalid = await Assert.ThrowsAsync<TwitchIntegrationException>(
            () => new TwitchOAuthClient(invalidClient).BeginDeviceCodeAsync(ClientId, default));
        Assert.Equal("oauth_device_invalid", invalid.Code);
    }

    [Fact]
    public async Task OAuthNetworkFailureReturnsASafeRetryableError()
    {
        using var client = new HttpClient(new FakeHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("private transport detail"))));
        var failure = await Assert.ThrowsAsync<TwitchIntegrationException>(
            () => new TwitchOAuthClient(client).BeginDeviceCodeAsync(ClientId, default));
        Assert.Equal(503, failure.StatusCode);
        Assert.DoesNotContain("private transport detail", failure.Message);
    }

    [Fact]
    public async Task DevicePollingRecognizesPendingAndNeverReturnsTokenValuesInItsStatus()
    {
        string? body = null;
        using var client = new HttpClient(new FakeHandler(async (request, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.BadRequest, """{"message":"authorization_pending"}""");
        }));
        var result = await new TwitchOAuthClient(client).PollDeviceCodeAsync(ClientId, "device-secret", default);
        Assert.Equal(TwitchDevicePollStatus.Pending, result.Status);
        Assert.Null(result.Tokens);
        Assert.Contains("device-secret", body);
        Assert.Contains("user%3Aread%3Achat", body);
        Assert.Contains("user%3Awrite%3Achat", body);
    }

    [Fact]
    public async Task ChatRepliesUseHelixAndRateLimitErrorsRemainExplicit()
    {
        string? body = null;
        AuthenticationHeaderValue? authorization = null;
        using var client = new HttpClient(new FakeHandler(async (request, _) =>
        {
            authorization = request.Headers.Authorization;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"data":[{"is_sent":true}]}""");
        }));
        using var chat = new TwitchChatClient(client, TimeProvider.System);
        await chat.SendMessageAsync(ClientId, "access-secret", "broadcaster-1", "sender-1",
            "parent-1", "Salut le chat !", default);
        Assert.Equal("Bearer", authorization!.Scheme);
        Assert.Equal("access-secret", authorization.Parameter);
        using (var document = JsonDocument.Parse(body!))
        {
            Assert.Equal("broadcaster-1", document.RootElement.GetProperty("broadcaster_id").GetString());
            Assert.Equal("parent-1", document.RootElement.GetProperty("reply_parent_message_id").GetString());
            Assert.Equal("Salut le chat !", document.RootElement.GetProperty("message").GetString());
        }

        using var limitedClient = new HttpClient(new FakeHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.TooManyRequests, """{"message":"rate limited"}"""))));
        using var limitedChat = new TwitchChatClient(limitedClient, TimeProvider.System);
        var failure = await Assert.ThrowsAsync<TwitchIntegrationException>(() =>
            limitedChat.SendMessageAsync(ClientId, "access-secret", "broadcaster-1", "sender-1",
                "parent-1", "Salut le chat !", default));
        Assert.Equal(429, failure.StatusCode);
        Assert.DoesNotContain("access-secret", failure.ToString());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static async Task<HttpResponseMessage> CaptureBodyAsync(
        HttpRequestMessage request, Func<string, Task<HttpResponseMessage>> complete) =>
        await complete(await request.Content!.ReadAsStringAsync());

    private sealed class FakeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class TwitchFixture : IDisposable
    {
        public StudioOptions Options { get; } = new()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "AIPlatform-twitch-tests-" + Guid.NewGuid().ToString("N"))
        };
        public TwitchBotStore Store { get; }
        public FakeVault Vault { get; } = new();
        public FakeOAuth OAuth { get; } = new();
        public FakeChat Chat { get; } = new();
        public TwitchBotService Service { get; }
        public TwitchStoredCredentials Credentials { get; } = new(
            ClientId, "user-1", "sampleuser", "access-secret", "refresh-secret");

        public TwitchFixture()
        {
            Store = new(Options);
            Store.SetClientId(ClientId);
            Service = new(Store, Vault, OAuth, Chat, TimeProvider.System);
        }

        public void Dispose()
        {
            if (Directory.Exists(Options.DataDirectory))
                Directory.Delete(Options.DataDirectory, recursive: true);
        }
    }

    private sealed class FakeVault : ITwitchTokenVault
    {
        private readonly Dictionary<Guid, TwitchStoredCredentials> credentials = [];
        public bool IsAvailable => true;
        public TwitchStoredCredentials? Read(Guid botId) => credentials.GetValueOrDefault(botId);
        public void Save(Guid botId, TwitchStoredCredentials value) => credentials[botId] = value;
        public void Delete(Guid botId) => credentials.Remove(botId);
    }

    private sealed class FakeOAuth : ITwitchOAuthClient
    {
        public TwitchIntegrationException? ValidateFailure { get; set; }
        public TwitchIntegrationException? RefreshFailure { get; set; }
        public bool ValidateFailureOnce { get; set; }
        public Task<TwitchDeviceCode> BeginDeviceCodeAsync(string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(new TwitchDeviceCode("device-secret", "CODE-123",
                "https://www.twitch.tv/activate", 600, 1));
        public Task<TwitchDevicePoll> PollDeviceCodeAsync(string clientId, string deviceCode, CancellationToken cancellationToken) =>
            Task.FromResult(new TwitchDevicePoll(TwitchDevicePollStatus.Authorized,
                new TwitchTokenGrant("access-secret", "refresh-secret", 3600)));
        public Task<TwitchTokenGrant> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken) =>
            RefreshFailure is { } failure ? Task.FromException<TwitchTokenGrant>(failure)
                : Task.FromResult(new TwitchTokenGrant("new-access-secret", "new-refresh-secret", 3600));
        public Task<TwitchValidatedIdentity> ValidateAsync(string accessToken, CancellationToken cancellationToken)
        {
            if (ValidateFailureOnce)
            {
                ValidateFailureOnce = false;
                return Task.FromException<TwitchValidatedIdentity>(
                    new TwitchIntegrationException("invalid_token", "Twitch authorization is no longer valid.", 401));
            }
            return ValidateFailure is { } failure ? Task.FromException<TwitchValidatedIdentity>(failure)
                : Task.FromResult(new TwitchValidatedIdentity(ClientId, "user-1", "sampleuser",
                    ["user:read:chat", "user:write:chat"], 3600));
        }
    }

    private sealed class FakeChat : ITwitchChatClient
    {
        private Func<TwitchChatMessage, CancellationToken, Task>? onMessage;
        private CancellationToken sessionToken;
        private int connections;
        public int Connections => Volatile.Read(ref connections);
        public int CompletedSessions { get; private set; }
        public bool DropFirstConnection { get; set; }
        public ConcurrentQueue<(string Message, string ParentId)> SentMessages { get; } = new();

        public Task<TwitchChannel> FindChannelAsync(string clientId, string accessToken, string login, CancellationToken cancellationToken) =>
            Task.FromResult(new TwitchChannel("broadcaster-1", login));

        public async Task RunEventSubAsync(string clientId, string accessToken, string broadcasterId, string userId,
            Func<TwitchChatMessage, CancellationToken, Task> messageHandler, Action connected,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref connections);
            onMessage = messageHandler;
            sessionToken = cancellationToken;
            connected();
            if (DropFirstConnection && Connections == 1)
                return;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { CompletedSessions++; }
        }

        public Task SendMessageAsync(string clientId, string accessToken, string broadcasterId, string senderId,
            string replyToMessageId, string message, CancellationToken cancellationToken)
        {
            SentMessages.Enqueue((message, replyToMessageId));
            return Task.CompletedTask;
        }

        public Task DeliverAsync(TwitchChatMessage message) =>
            onMessage?.Invoke(message, sessionToken) ?? throw new InvalidOperationException("No chat session is active.");
    }
}
