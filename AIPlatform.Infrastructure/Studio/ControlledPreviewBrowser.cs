using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed record PreviewBrowserOptions(string NodeExecutable, string HelperFile);

public interface IControlledPreviewBrowser
{
    bool IsolationVerified { get; }
    Task OpenAsync(Guid sessionId, string origin, string token, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task CloseAsync(Guid sessionId, CancellationToken cancellationToken);
    Task CloseAllAsync(CancellationToken cancellationToken);
    bool IsRunning(Guid sessionId);
}

public sealed class ControlledPreviewBrowser : IControlledPreviewBrowser
{
    private sealed record Session(Process Process, string Profile, Task Errors);
    private readonly PreviewBrowserOptions options;
    private readonly ILogger<ControlledPreviewBrowser> logger;
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Session> sessions = new();

    public ControlledPreviewBrowser(PreviewBrowserOptions options, StudioOptions studio, ILogger<ControlledPreviewBrowser> logger)
    {
        this.options = options;
        this.logger = logger;
        var store = SafePaths.ValidateStorageDirectory(studio.DataDirectory);
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(store.ToUpperInvariant())))[..16];
        root = SafePaths.ValidateStorageDirectory(Path.Combine(Path.GetTempPath(), "AIPlatform-preview-browser-" + scope));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var storePrefix = store.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (rootPrefix.StartsWith(storePrefix, StringComparison.OrdinalIgnoreCase) ||
            storePrefix.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new StudioValidationException("Preview profiles must be outside Studio storage.");
    }

    // Request interception is tested, but OS-level network containment and packaging are not certified.
    public bool IsolationVerified => false;

    public bool IsRunning(Guid sessionId) =>
        sessions.TryGetValue(sessionId, out var session) && !session.Process.HasExited;

    public async Task OpenAsync(Guid sessionId, string origin, string token, DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!OperatingSystem.IsWindows() || sessionId == Guid.Empty || sessions.ContainsKey(sessionId) ||
                !Uri.TryCreate(origin, UriKind.Absolute, out var address) || address.Scheme != "http" ||
                address.Host != "127.0.0.1" || address.IsDefaultPort || address.AbsolutePath != "/" ||
                address.Query.Length != 0 || address.Fragment.Length != 0 || address.UserInfo.Length != 0 ||
                token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)) ||
                expiresAt <= DateTimeOffset.UtcNow || expiresAt > DateTimeOffset.UtcNow.AddMinutes(30))
                throw new StudioValidationException("Invalid controlled Preview browser session.");
            if (!Path.IsPathFullyQualified(options.NodeExecutable) || !Path.IsPathFullyQualified(options.HelperFile) ||
                !File.Exists(options.NodeExecutable) || !File.Exists(options.HelperFile))
                throw new PreviewUnavailableException("The trusted Node executable or Preview browser helper is missing.");
            SafePaths.RejectLinks(options.NodeExecutable);
            SafePaths.RejectLinks(options.HelperFile);
            SafePaths.RejectLinks(root);
            Directory.CreateDirectory(root);
            var profile = Path.Combine(root, sessionId.ToString("N"));
            if (Directory.Exists(profile)) throw new PreviewUnavailableException("A previous browser profile requires cleanup.");
            var process = new Process
            {
                StartInfo = new()
                {
                    FileName = options.NodeExecutable, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(options.HelperFile)!,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                }
            };
            process.StartInfo.ArgumentList.Add(options.HelperFile);
            process.StartInfo.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "ProgramFiles", "ProgramFiles(x86)" })
                if (Environment.GetEnvironmentVariable(name) is { } value) process.StartInfo.Environment[name] = value;
            process.StartInfo.Environment["TEMP"] = root;
            process.StartInfo.Environment["TMP"] = root;
            process.StartInfo.Environment["USERPROFILE"] = root;
            var started = false;
            try
            {
                if (!process.Start()) throw new IOException("Controlled Preview browser helper did not start.");
                started = true;
                var errors = DrainErrorsAsync(process);
                sessions[sessionId] = new(process, profile, errors);
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    origin = address.GetLeftPart(UriPartial.Authority), token = token.ToUpperInvariant(),
                    profile, expiresAt = expiresAt.ToUnixTimeMilliseconds()
                }).AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var ready = await ReadReadyAsync(process, timeout.Token);
                if (ready != "AIPLATFORM_PREVIEW_BROWSER_READY" || process.HasExited)
                    throw new IOException("Controlled Preview browser did not become ready.");
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or
                                               System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                if (started)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await CloseCoreAsync(sessionId, cleanup.Token);
                }
                else process.Dispose();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task CloseAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await CloseCoreAsync(sessionId, cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task CloseAllAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var id in sessions.Keys) await CloseCoreAsync(id, cancellationToken);
            SafePaths.RejectLinks(root);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new PreviewUnavailableException("Orphan browser profiles remain; inspect them before enabling Preview.");
        }
        finally { gate.Release(); }
    }

    private async Task CloseCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(id, out var session)) return;
        var process = session.Process;
        if (!process.HasExited)
        {
            try
            {
                await process.StandardInput.WriteLineAsync("shutdown".AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                logger.LogWarning("Controlled Preview browser required forced termination.");
            }
        }
        await session.Errors;
        if (Directory.Exists(session.Profile))
            throw new PreviewUnavailableException("Browser profile cleanup was not confirmed; the session is not cleanly stopped.");
        sessions.TryRemove(id, out _);
        process.Dispose();
    }

    private async Task DrainErrorsAsync(Process process)
    {
        var buffer = new char[1024];
        var reported = false;
        while (await process.StandardError.ReadAsync(buffer) != 0)
        {
            if (reported) continue;
            logger.LogWarning("The controlled Preview browser helper reported an error; raw output is withheld.");
            reported = true;
        }
    }

    private static async Task<string> ReadReadyAsync(Process process, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var character = new char[1];
        while (result.Length < 64 && await process.StandardOutput.ReadAsync(character.AsMemory(), cancellationToken) != 0)
        {
            if (character[0] == '\n') return result.ToString().TrimEnd('\r');
            result.Append(character[0]);
        }
        throw new IOException("Invalid or oversized browser readiness message.");
    }
}
