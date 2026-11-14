using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AIPlatform.Core.Studio;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIPlatform.Infrastructure.Studio;

public sealed class PreviewProxy(DockerPreviewHttpTransport transport, ILogger<PreviewProxy> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, Access> sessions = [];

    public static PreviewExposure Exposure { get; } =
        new("preview-proxy", "127.0.0.1:ephemeral", "http", "/");

    internal sealed class Access(WebApplication application, string token, DateTimeOffset expiresAt)
    {
        public WebApplication Application { get; } = application;
        public string Token { get; } = token;
        public string Address { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public CancellationTokenSource Lifetime { get; } = new();
        public SemaphoreSlim Requests { get; } = new(1, 1);
    }

    internal async Task<Access> StartAsync(Guid sessionId, string container, DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (sessionId == Guid.Empty || expiresAt <= DateTimeOffset.UtcNow || sessions.ContainsKey(sessionId))
                throw new StudioValidationException("Invalid or duplicate Preview proxy session.");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.AddServerHeader = false;
                server.Listen(IPAddress.Loopback, 0);
                server.Limits.MaxRequestBodySize = DockerPreviewHttpTransport.MaximumRequestBytes;
                server.Limits.MaxRequestHeadersTotalSize = 8192;
                server.Limits.MaxConcurrentConnections = 16;
                server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            });
            var app = builder.Build();
            var access = new Access(app, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), expiresAt);
            app.Run(context => HandleAsync(context, access, container));
            var registered = false;
            try
            {
                await app.StartAsync(cancellationToken);
                access.Address = app.Urls.Single();
                sessions.Add(sessionId, access);
                registered = true;
                return access;
            }
            finally
            {
                if (!registered) await app.DisposeAsync();
            }
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!sessions.TryGetValue(sessionId, out var access)) return;
            access.Lifetime.Cancel();
            await access.Application.StopAsync(cancellationToken);
            await access.Application.DisposeAsync();
            sessions.Remove(sessionId);
        }
        finally { gate.Release(); }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        Guid[] ids;
        await gate.WaitAsync(cancellationToken);
        try { ids = sessions.Keys.ToArray(); }
        finally { gate.Release(); }
        foreach (var id in ids) await StopAsync(id, cancellationToken);
    }

    private async Task HandleAsync(HttpContext context, Access access, string container)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
            "img-src 'self'; font-src 'self'; connect-src 'self'; object-src 'none'; " +
            "frame-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'; worker-src 'none'";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        if (access.Lifetime.IsCancellationRequested || DateTimeOffset.UtcNow >= access.ExpiresAt)
        {
            response.StatusCode = 410;
            return;
        }
        var origin = access.Address;
        if (origin.Length == 0 || request.Host.Value != new Uri(origin).Authority ||
            request.Headers.ContainsKey("Authorization") || request.Headers.ContainsKey("Upgrade") ||
            request.Headers.Keys.Any(key => key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase)) ||
            request.Headers.ContainsKey("Forwarded") || request.Headers.Keys.Any(key =>
                key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)) ||
            request.Headers.TryGetValue("Origin", out var suppliedOrigin) && suppliedOrigin.ToString() != origin ||
            request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site.ToString() is not ("same-origin" or "none") ||
            request.Method is "POST" or "PUT" or "DELETE" && request.Headers.Origin.ToString() != origin ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Headers["X-Preview-Token"].ToString()),
                Encoding.ASCII.GetBytes(access.Token)))
        {
            response.StatusCode = 403;
            return;
        }
        var rawTarget = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget ?? "";
        if (!DockerPreviewHttpTransport.IsAllowed(request.Method, rawTarget))
        {
            response.StatusCode = 403;
            return;
        }
        if (request.ContentLength > DockerPreviewHttpTransport.MaximumRequestBytes)
        {
            response.StatusCode = 413;
            return;
        }
        if (request.Method is "POST" or "PUT" &&
            request.ContentType?.Split(';')[0].Trim() != "application/json")
        {
            response.StatusCode = 415;
            return;
        }
        if (!await access.Requests.WaitAsync(0, context.RequestAborted))
        {
            response.StatusCode = 429;
            return;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, access.Lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(DockerPreviewHttpTransport.TimeoutSeconds));
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await request.Body.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (body.Length + count > DockerPreviewHttpTransport.MaximumRequestBytes)
                {
                    response.StatusCode = 413;
                    return;
                }
                body.Write(buffer, 0, count);
            }
            var result = await transport.SendAsync(container, request.Method, rawTarget, body.ToArray(), deadline.Token);
            response.StatusCode = result.Status;
            response.ContentType = result.ContentType;
            response.ContentLength = result.Body.Length;
            await response.Body.WriteAsync(result.Body, deadline.Token);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            if (!response.HasStarted) response.StatusCode = access.Lifetime.IsCancellationRequested ? 410 : 504;
        }
        catch (Exception exception) when (exception is IOException or StudioValidationException or
                                           BadHttpRequestException or Win32Exception)
        {
            logger.LogWarning("Preview relay request failed: {FailureType}", exception.GetType().Name);
            if (!response.HasStarted) response.StatusCode = exception is BadHttpRequestException bad ? bad.StatusCode : 502;
        }
        finally { access.Requests.Release(); }
    }
}
