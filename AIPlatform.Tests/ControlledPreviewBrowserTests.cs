using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIPlatform.Tests;

public sealed class ControlledPreviewBrowserTests
{
    [Theory]
    [InlineData("file:///C:/test", "A")]
    [InlineData("http://127.0.0.2:12345", "A")]
    [InlineData("http://127.0.0.1:12345/path", "A")]
    [InlineData("http://127.0.0.1:12345", "not-a-token")]
    public async Task InvalidSessionsCannotLaunchProcess(string origin, string token)
    {
        var browser = Browser();
        Assert.False(browser.IsolationVerified);
        await Assert.ThrowsAsync<StudioValidationException>(() => browser.OpenAsync(Guid.NewGuid(),
            origin, token == "A" ? new string('A', 64) : token, DateTimeOffset.UtcNow.AddMinutes(1), default));
        await browser.CloseAllAsync(default);
    }

    [Fact]
    public async Task MissingTrustedHelperCannotLaunchAndCancellationIsRespected()
    {
        var browser = Browser();
        await Assert.ThrowsAsync<PreviewUnavailableException>(() => browser.OpenAsync(Guid.NewGuid(),
            "http://127.0.0.1:12345", new string('A', 64), DateTimeOffset.UtcNow.AddMinutes(1), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.OpenAsync(Guid.NewGuid(),
            "http://127.0.0.1:12345", new string('A', 64), DateTimeOffset.UtcNow.AddMinutes(1), cancelled.Token));
        await browser.CloseAllAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserStartAndCleanupFailureStillCloseProxyAndRemoveDocker(bool cleanupFails)
    {
        var process = new DockerPreviewAdapterTests.DockerDouble();
        var browser = new BrowserDouble { FailOpen = !cleanupFails, FailClose = cleanupFails };
        var proxy = new PreviewProxy(new DockerPreviewHttpTransport(process), NullLogger<PreviewProxy>.Instance);
        var adapter = new DockerPreviewAdapter(process, Options(), proxy, browser);
        var (plan, snapshot) = DockerPreviewAdapterTests.Sources();
        plan = plan with { Transport = DockerPreviewHttpTransport.Policy, Exposures = [PreviewProxy.Exposure] };
        var id = Guid.NewGuid();
        if (!cleanupFails)
            await Assert.ThrowsAsync<IOException>(() => adapter.StartProxySessionAsync(id, plan, snapshot, default));
        else
        {
            await adapter.StartProxySessionAsync(id, plan, snapshot, default);
            Assert.True(await adapter.IsSessionHealthyAsync(id, default));
            browser.Running = false;
            Assert.False(await adapter.IsSessionHealthyAsync(id, default));
            await Assert.ThrowsAsync<IOException>(() => adapter.StopAsync(id, default));
            await Assert.ThrowsAsync<PreviewUnavailableException>(() =>
                adapter.StartContainerAsync(Guid.NewGuid(), plan, snapshot, default));
        }
        Assert.Empty(process.Containers);
        Assert.True(browser.Closed);
        using var port = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { ExclusiveAddressUse = true };
        port.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, new Uri(browser.Origin).Port));
        browser.FailClose = false;
        await adapter.CleanupOrphansAsync(default);
    }

    [Fact]
    public async Task BrowserOrphanFailureDoesNotPreventDockerRecoveryButRemainsExplicit()
    {
        var process = new DockerPreviewAdapterTests.DockerDouble();
        var browser = new BrowserDouble { FailClose = true };
        var adapter = new DockerPreviewAdapter(process, Options(), browser: browser);
        var (plan, snapshot) = DockerPreviewAdapterTests.Sources();
        await adapter.StartContainerAsync(Guid.NewGuid(), plan, snapshot, default);
        await Assert.ThrowsAsync<IOException>(() => adapter.CleanupOrphansAsync(default));
        Assert.Empty(process.Containers);
        browser.FailClose = false;
        await adapter.CleanupOrphansAsync(default);
    }

    private static ControlledPreviewBrowser Browser() => new(
        new(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mjs")),
        Options(), NullLogger<ControlledPreviewBrowser>.Instance);

    private static StudioOptions Options() => new()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "BrowserTests-" + Guid.NewGuid().ToString("N"))
    };

    private sealed class BrowserDouble : IControlledPreviewBrowser
    {
        public bool IsolationVerified => false;
        public bool FailOpen { get; set; }
        public bool FailClose { get; set; }
        public bool Closed { get; private set; }
        public bool Running { get; set; }
        public string Origin { get; private set; } = "";
        public Task OpenAsync(Guid sessionId, string origin, string token, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            Origin = origin;
            if (FailOpen) throw new IOException("Simulated browser startup failure.");
            Running = true;
            return Task.CompletedTask;
        }
        public Task CloseAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            Closed = true;
            Running = false;
            if (FailClose) throw new IOException("Simulated profile cleanup failure.");
            return Task.CompletedTask;
        }
        public Task CloseAllAsync(CancellationToken cancellationToken) => CloseAsync(Guid.Empty, cancellationToken);
        public bool IsRunning(Guid sessionId) => Running;
    }
}
