using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class TwitchOAuthClient(HttpClient http) : ITwitchOAuthClient
{
    private static readonly string[] RequiredScopes = ["user:read:chat", "user:write:chat"];
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string TokenEndpoint = "https://id.twitch.tv/oauth2/token";

    public async Task<TwitchDeviceCode> BeginDeviceCodeAsync(string clientId, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scopes"] = string.Join(' ', RequiredScopes)
        });
        using var response = await SendAsync(() => http.PostAsync(
            "https://id.twitch.tv/oauth2/device", content, cancellationToken), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw Failure("oauth_device_failed", "Twitch refused to start device authorization.", response.StatusCode);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        var code = RequiredString(root, "device_code", "oauth_device_invalid");
        var userCode = RequiredString(root, "user_code", "oauth_device_invalid");
        var verificationUri = RequiredString(root, "verification_uri", "oauth_device_invalid");
        if (!Uri.TryCreate(verificationUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "www.twitch.tv")
            throw new TwitchIntegrationException("oauth_device_invalid", "Twitch returned an unexpected authorization URL.", 502);
        return new(code, userCode, verificationUri,
            Math.Clamp(RequiredInt(root, "expires_in", "oauth_device_invalid"), 30, 1800),
            Math.Clamp(RequiredInt(root, "interval", "oauth_device_invalid"), 1, 30));
    }

    public async Task<TwitchDevicePoll> PollDeviceCodeAsync(
        string clientId, string deviceCode, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["device_code"] = deviceCode,
            ["grant_type"] = DeviceGrant,
            ["scopes"] = string.Join(' ', RequiredScopes)
        });
        using var response = await SendAsync(() => http.PostAsync(TokenEndpoint, content, cancellationToken), cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        if (response.IsSuccessStatusCode)
            return new(TwitchDevicePollStatus.Authorized, ReadGrant(document.RootElement));

        var error = ReadError(document.RootElement);
        return error switch
        {
            "authorization_pending" => new(TwitchDevicePollStatus.Pending),
            "slow_down" => new(TwitchDevicePollStatus.Pending, RetryIntervalSeconds: 10),
            "expired_token" or "invalid device code" => new(TwitchDevicePollStatus.Expired),
            "access_denied" => new(TwitchDevicePollStatus.Denied),
            _ => throw Failure("oauth_poll_failed", "Twitch could not complete device authorization.", response.StatusCode)
        };
    }

    public async Task<TwitchTokenGrant> RefreshAsync(
        string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });
        using var response = await SendAsync(() => http.PostAsync(TokenEndpoint, content, cancellationToken), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw Failure("oauth_refresh_failed", "Twitch authorization expired; reconnect the account.", response.StatusCode);
        using var document = await ReadJsonAsync(response, cancellationToken);
        return ReadGrant(document.RootElement);
    }

    public async Task<TwitchValidatedIdentity> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + accessToken);
        using var response = await SendAsync(() => http.SendAsync(request, cancellationToken), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new TwitchIntegrationException("invalid_token", "Twitch authorization is no longer valid.", 401);
        if (!response.IsSuccessStatusCode)
            throw Failure("oauth_validation_failed", "Twitch could not validate the account.", response.StatusCode);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        return new(
            RequiredString(root, "client_id", "oauth_validation_invalid"),
            RequiredString(root, "user_id", "oauth_validation_invalid"),
            RequiredString(root, "login", "oauth_validation_invalid"),
            root.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
                ? scopes.EnumerateArray().Select(scope => scope.GetString() ?? "").ToArray() : [],
            RequiredInt(root, "expires_in", "oauth_validation_invalid"));
    }

    private static TwitchTokenGrant ReadGrant(JsonElement root)
    {
        var accessToken = RequiredString(root, "access_token", "oauth_token_invalid");
        var refreshToken = RequiredString(root, "refresh_token", "oauth_token_invalid");
        var expiresIn = RequiredInt(root, "expires_in", "oauth_token_invalid");
        if (expiresIn < 1)
            throw new TwitchIntegrationException("oauth_token_invalid", "Twitch returned an invalid token lifetime.", 502);
        return new(accessToken, refreshToken, expiresIn);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try { return await send(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TwitchIntegrationException("twitch_timeout", "Twitch did not respond before the request timed out.", 503); }
        catch (HttpRequestException)
        { throw new TwitchIntegrationException("twitch_unavailable", "Twitch could not be reached.", 503); }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return document;
        }
        catch (JsonException)
        {
            throw new TwitchIntegrationException("twitch_invalid_response", "Twitch returned an invalid response.", 502);
        }
    }

    private static string RequiredString(JsonElement root, string name, string errorCode) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new TwitchIntegrationException(errorCode, "Twitch returned an incomplete authorization response.", 502);

    private static int RequiredInt(JsonElement root, string name, string errorCode) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) && result > 0
            ? result
            : throw new TwitchIntegrationException(errorCode, "Twitch returned an incomplete authorization response.", 502);

    private static string ReadError(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString() ?? "";
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            return error.GetString() ?? "";
        return "";
    }

    private static TwitchIntegrationException Failure(string code, string message, HttpStatusCode status) =>
        new(code, message, (int)status >= 500 ? 503 : 400);
}
