using System.Text.Json.Serialization;

namespace AIPlatform.Core.Studio;

public sealed record TwitchCommand(string Name, string Response, int CooldownSeconds = 5, bool Enabled = true,
    bool ModeratorOnly = false);
public sealed record TwitchBotConfiguration(Guid Id, string Name, string Channel, IReadOnlyList<TwitchCommand> Commands);
public sealed record TwitchBotEvent(DateTimeOffset Timestamp, string Kind, string Message);
public sealed record TwitchBotView(TwitchBotConfiguration Configuration, string State, bool Authorized,
    string? AccountLogin, string? Error, IReadOnlyList<TwitchBotEvent> Events);
public sealed record TwitchSettingsView(string ClientId, bool Configured);
public sealed record TwitchDeviceAuthorization(string VerificationUri, string UserCode, int ExpiresIn, int Interval);
public sealed record TwitchConnectResult(bool Pending, bool Authorized, string? AccountLogin, string? Message = null);
public sealed record TwitchChatMessage(string Id, string SenderId, string Text, bool IsModerator = false);
public sealed record TwitchCommandTestResult(bool Matched, string? Response, int CooldownRemainingSeconds = 0,
    bool PermissionDenied = false);

public sealed record TwitchSimulationEvent(string UserId, string Role, string Text, int AtSeconds);
public sealed record TwitchSimulationStep(TwitchSimulationEvent Event, string Outcome, string? Response,
    int CooldownRemainingSeconds);
public sealed record TwitchSimulationResult(Guid BotId, IReadOnlyList<TwitchSimulationStep> Steps);

public interface ITwitchSimulationService
{
    TwitchSimulationResult Simulate(Guid botId, IReadOnlyList<TwitchSimulationEvent> events,
        CancellationToken cancellationToken);
}

public sealed record TwitchCommandOperation(
    [property: JsonRequired] string Action, [property: JsonRequired] string Name,
    [property: JsonRequired] string? Response, [property: JsonRequired] int? CooldownSeconds,
    [property: JsonRequired] bool? Enabled, [property: JsonRequired] bool? ModeratorOnly);

public sealed record TwitchCommandProposal(
    Guid Id, Guid BotId, Guid TeamId, DateTimeOffset CreatedAt, string Request, string Status,
    IReadOnlyList<TwitchCommandOperation> Operations, IReadOnlyList<string> Unsupported,
    TwitchBotConfiguration Before, TwitchBotConfiguration After,
    IReadOnlyList<TwitchCommandEvidence> Evidence, string? Error = null);

public sealed record TwitchCommandEvidence(
    string Command, string Criterion, string Expected, string Actual, bool Passed);

public interface ITwitchCommandProposalService
{
    Task<TwitchCommandProposal> GenerateAsync(Guid botId, Guid teamId, string request,
        CancellationToken cancellationToken);
    TwitchCommandProposal Apply(Guid botId, Guid proposalId, bool approved, bool deleteApproved,
        CancellationToken cancellationToken);
    TwitchCommandProposal RestoreInterrupted(Guid botId, Guid proposalId, bool approved,
        CancellationToken cancellationToken);
    IReadOnlyList<TwitchCommandProposal> List(Guid botId);
}

public sealed record TwitchValidatedIdentity(
    string ClientId, string UserId, string Login, IReadOnlyList<string> Scopes, int ExpiresIn);

public sealed class TwitchTokenGrant(string accessToken, string refreshToken, int expiresIn)
{
    public string AccessToken { get; } = accessToken;
    public string RefreshToken { get; } = refreshToken;
    public int ExpiresIn { get; } = expiresIn;
    public override string ToString() => "TwitchTokenGrant { redacted }";
}

public sealed class TwitchStoredCredentials(
    string clientId, string userId, string login, string accessToken, string refreshToken)
{
    public string ClientId { get; } = clientId;
    public string UserId { get; } = userId;
    public string Login { get; } = login;
    public string AccessToken { get; } = accessToken;
    public string RefreshToken { get; } = refreshToken;
    public override string ToString() => "TwitchStoredCredentials { redacted }";
}

public sealed class TwitchIntegrationException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public interface ITwitchBotService
{
    TwitchSettingsView GetSettings();
    TwitchSettingsView SaveSettings(string clientId);
    IReadOnlyList<TwitchBotView> List();
    TwitchBotView? Get(Guid id);
    TwitchBotView Create(string name, string channel);
    TwitchBotView Update(Guid id, string name, string channel, IReadOnlyList<TwitchCommand> commands);
    bool Delete(Guid id);
    Task<TwitchDeviceAuthorization> BeginConnectAsync(Guid id, CancellationToken cancellationToken);
    Task<TwitchConnectResult> PollConnectAsync(Guid id, CancellationToken cancellationToken);
    Task<TwitchBotView> StartAsync(Guid id, CancellationToken cancellationToken);
    Task<TwitchBotView> StopAsync(Guid id, CancellationToken cancellationToken);
    TwitchCommandTestResult TestCommand(Guid id, string input);
}

public interface ITwitchBotStore
{
    string GetClientId();
    void SetClientId(string clientId);
    IReadOnlyList<TwitchBotConfiguration> List();
    TwitchBotConfiguration? Get(Guid id);
    void Save(TwitchBotConfiguration bot);
    bool Delete(Guid id);
}

public interface ITwitchTokenVault
{
    bool IsAvailable { get; }
    TwitchStoredCredentials? Read(Guid botId);
    void Save(Guid botId, TwitchStoredCredentials credentials);
    void Delete(Guid botId);
}

public sealed record TwitchDeviceCode(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval);
public enum TwitchDevicePollStatus { Pending, Authorized, Expired, Denied }
public sealed record TwitchDevicePoll(TwitchDevicePollStatus Status, TwitchTokenGrant? Tokens = null,
    int? RetryIntervalSeconds = null);

public interface ITwitchOAuthClient
{
    Task<TwitchDeviceCode> BeginDeviceCodeAsync(string clientId, CancellationToken cancellationToken);
    Task<TwitchDevicePoll> PollDeviceCodeAsync(string clientId, string deviceCode, CancellationToken cancellationToken);
    Task<TwitchTokenGrant> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken);
    Task<TwitchValidatedIdentity> ValidateAsync(string accessToken, CancellationToken cancellationToken);
}

public sealed record TwitchChannel(string Id, string Login);

public interface ITwitchChatClient
{
    Task<TwitchChannel> FindChannelAsync(string clientId, string accessToken, string login, CancellationToken cancellationToken);
    Task RunEventSubAsync(string clientId, string accessToken, string broadcasterId, string userId,
        Func<TwitchChatMessage, CancellationToken, Task> onMessage, Action onConnected,
        CancellationToken cancellationToken);
    Task SendMessageAsync(string clientId, string accessToken, string broadcasterId, string senderId,
        string replyToMessageId, string message, CancellationToken cancellationToken);
}
