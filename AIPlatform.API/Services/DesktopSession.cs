using System.Security.Cryptography;
using System.Text;

namespace AIPlatform.API.Services;

public sealed class DesktopSession(bool enabled, string? token)
{
    private readonly byte[] expected = enabled && token is { Length: 64 } && token.All(Uri.IsHexDigit)
        ? Encoding.ASCII.GetBytes("Bearer " + token)
        : !enabled ? [] : throw new InvalidOperationException("Desktop requires a launcher-generated session token.");

    public bool Enabled => enabled;

    public bool Authorize(string? authorization) => enabled && authorization is not null &&
        CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(authorization));

    public static bool IsAllowedOrigin(string origin) => origin is
        "http://tauri.localhost" or "tauri://localhost" or "http://127.0.0.1:5173";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Headers.TryGetValue("Origin", out var origin) && !IsAllowedOrigin(origin.ToString()))
        {
            await TypedResults.Problem(statusCode: 403, title: "This origin is not allowed by the desktop application.")
                .ExecuteAsync(context);
            return;
        }
        if (!Authorize(context.Request.Headers.Authorization))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await TypedResults.Problem(statusCode: 401, title: "The desktop session token is required.")
                .ExecuteAsync(context);
            return;
        }
        await next(context);
    }
}

/// <summary>Readiness of this API process; authentication is required in Desktop mode.</summary>
public sealed record BackendHealthResponse(bool Ready, int ProcessId);
