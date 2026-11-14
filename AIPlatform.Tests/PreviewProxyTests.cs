using System.Net;
using System.Text;
using System.Text.Json;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIPlatform.Tests;

public sealed class PreviewProxyTests
{
    private const string Container = "aiplatform-preview-aaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData("http://localhost:5167/api/missions")]
    [InlineData("http://127.0.0.1:56622/api/missions")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://example.com/")]
    [InlineData("//127.0.0.1:5167/")]
    [InlineData("/assets/../api/missions")]
    [InlineData("/%2e%2e/api/missions")]
    [InlineData("/api/tasks?url=http://localhost")]
    public async Task TransportRejectsArbitraryDestinationsBeforeDocker(string path)
    {
        var process = new RelayDouble();
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            new DockerPreviewHttpTransport(process).SendAsync(Container, "GET", path, [], default));
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task RelayUsesFixedProgramAndEncodedPayloadRatherThanShell()
    {
        var process = new RelayDouble();
        var body = Encoding.UTF8.GetBytes("""{"title":"$(touch /tmp/escape)"}""");
        var result = await new DockerPreviewHttpTransport(process).SendAsync(Container, "POST", "/api/tasks", body, default);
        Assert.Equal(200, result.Status);
        var args = Assert.Single(process.Calls);
        Assert.Equal(new[] { "exec", Container, "node", "--input-type=module", "-e" }, args.Take(5));
        using var payload = JsonDocument.Parse(Convert.FromBase64String(args[^1]));
        Assert.Equal("/api/tasks", payload.RootElement.GetProperty("path").GetString());
        Assert.Equal(body, Convert.FromBase64String(payload.RootElement.GetProperty("body").GetString()!));
        Assert.DoesNotContain("$(touch", args[5]);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    public async Task RelayRejectsRedirects(int status)
    {
        var process = new RelayDouble { Status = status };
        await Assert.ThrowsAsync<IOException>(() =>
            new DockerPreviewHttpTransport(process).SendAsync(Container, "GET", "/", [], default));
    }

    [Fact]
    public async Task LargeBodyAndResponseAreRejected()
    {
        var process = new RelayDouble();
        var transport = new DockerPreviewHttpTransport(process);
        await Assert.ThrowsAsync<StudioValidationException>(() => transport.SendAsync(Container, "POST", "/api/tasks",
            new byte[DockerPreviewHttpTransport.MaximumRequestBytes + 1], default));
        Assert.Empty(process.Calls);
        process.Body = new byte[DockerPreviewHttpTransport.MaximumResponseBytes + 1];
        await Assert.ThrowsAsync<IOException>(() => transport.SendAsync(Container, "GET", "/", [], default));
    }

    [Fact]
    public async Task ProxyFiltersHeadersAndRequiresSessionTokenAndExactOrigin()
    {
        var process = new RelayDouble();
        var proxy = Proxy(process);
        var id = Guid.NewGuid();
        var access = await proxy.StartAsync(id, Container, DateTimeOffset.UtcNow.AddMinutes(1), default);
        try
        {
            using var client = Client(access.Address);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/")).StatusCode);
            client.DefaultRequestHeaders.Add("X-Preview-Token", access.Token);
            client.DefaultRequestHeaders.Add("Cookie", "StudioSecret=not-forwarded");
            var response = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.DoesNotContain("StudioSecret", string.Join(" ", process.Calls.Single()));
            Assert.DoesNotContain(access.Token, string.Join(" ", process.Calls.Single()));
            foreach (var header in new[] { "Authorization", "Origin", "Upgrade", "X-Forwarded-Host", "Sec-WebSocket-Key" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/");
                request.Headers.TryAddWithoutValidation(header, "http://127.0.0.1:5167");
                Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
            }
            using var badHost = new HttpRequestMessage(HttpMethod.Get, "/");
            badHost.Headers.Host = "localhost:5167";
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(badHost)).StatusCode);
            Assert.Single(process.Calls);
        }
        finally { await proxy.StopAsync(id, default); }
    }

    [Fact]
    public async Task SessionsCannotReuseTokensAndClosedProxyCannotBeReused()
    {
        var proxy = Proxy(new RelayDouble());
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var first = await proxy.StartAsync(a, Container, DateTimeOffset.UtcNow.AddMinutes(1), default);
        var second = await proxy.StartAsync(b, Container, DateTimeOffset.UtcNow.AddMinutes(1), default);
        try
        {
            using var client = Client(second.Address);
            client.DefaultRequestHeaders.Add("X-Preview-Token", first.Token);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/")).StatusCode);
            await proxy.StopAsync(a, default);
            using var stopped = Client(first.Address);
            stopped.DefaultRequestHeaders.Add("X-Preview-Token", first.Token);
            await Assert.ThrowsAsync<HttpRequestException>(() => stopped.GetAsync("/"));
        }
        finally { await proxy.StopAllAsync(default); }
    }

    [Fact]
    public async Task ExpiredProxyRefusesRequestsWithoutInvokingDocker()
    {
        var process = new RelayDouble();
        var proxy = Proxy(process);
        var id = Guid.NewGuid();
        var access = await proxy.StartAsync(id, Container, DateTimeOffset.UtcNow.AddMilliseconds(300), default);
        try
        {
            await Task.Delay(400);
            using var client = Client(access.Address);
            client.DefaultRequestHeaders.Add("X-Preview-Token", access.Token);
            Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync("/")).StatusCode);
            Assert.Empty(process.Calls);
        }
        finally { await proxy.StopAsync(id, default); }
    }

    [Fact]
    public async Task AdapterStartsProxyOnlyAfterContainerHealthAndStopsBoth()
    {
        var process = new DockerPreviewAdapterTests.DockerDouble();
        var proxy = Proxy(new RelayDouble());
        var adapter = new DockerPreviewAdapter(process, new StudioOptions
        { DataDirectory = Path.Combine(Path.GetTempPath(), "PreviewProxyTests-" + Guid.NewGuid().ToString("N")) }, proxy);
        var (plan, snapshot) = DockerPreviewAdapterTests.Sources();
        plan = plan with { Transport = DockerPreviewHttpTransport.Policy, Exposures = [PreviewProxy.Exposure] };
        var id = Guid.NewGuid();
        var health = await adapter.StartProxySessionAsync(id, plan, snapshot, default);
        Assert.NotNull(health.Url);
        Assert.False(adapter.IsAvailable);
        await adapter.StopAsync(id, default);
        Assert.Empty(process.Containers);
        using var client = Client(health.Url!);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/"));
    }

    [Fact]
    public async Task TransportCancellationReachesDockerExecutor()
    {
        var process = new RelayDouble { Hang = true };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DockerPreviewHttpTransport(process).SendAsync(Container, "GET", "/", [], cancellation.Token));
        Assert.Single(process.Calls);
    }

    [Fact]
    public async Task ProxyBoundsRelayDurationAndReportsTimeout()
    {
        var proxy = Proxy(new RelayDouble { Hang = true });
        var id = Guid.NewGuid();
        var access = await proxy.StartAsync(id, Container, DateTimeOffset.UtcNow.AddMinutes(1), default);
        try
        {
            using var client = Client(access.Address);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("X-Preview-Token", access.Token);
            Assert.Equal(HttpStatusCode.GatewayTimeout, (await client.GetAsync("/")).StatusCode);
        }
        finally { await proxy.StopAsync(id, default); }
    }

    [Fact]
    public async Task ProxyRejectsLargeBodyBeforeRelayAndRejectsUpstreamRedirect()
    {
        var process = new RelayDouble { Status = 302 };
        var proxy = Proxy(process);
        var id = Guid.NewGuid();
        var access = await proxy.StartAsync(id, Container, DateTimeOffset.UtcNow.AddMinutes(1), default);
        try
        {
            using var client = Client(access.Address);
            client.DefaultRequestHeaders.Add("X-Preview-Token", access.Token);
            client.DefaultRequestHeaders.Add("Origin", access.Address);
            using var body = new ByteArrayContent(new byte[DockerPreviewHttpTransport.MaximumRequestBytes + 1]);
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/tasks", body)).StatusCode);
            Assert.Empty(process.Calls);
            Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync("/")).StatusCode);
        }
        finally { await proxy.StopAsync(id, default); }
    }

    [Fact]
    public void ProxyPolicyIsBoundToCanonicalFingerprint()
    {
        var (plan, _) = DockerPreviewAdapterTests.Sources();
        var transport = plan with { Transport = DockerPreviewHttpTransport.Policy, Exposures = [PreviewProxy.Exposure] };
        Assert.NotEqual(plan.ComputeFingerprint(), transport.ComputeFingerprint());
        Assert.NotEqual(transport.ComputeFingerprint(),
            (transport with { Transport = transport.Transport! with { WebSockets = true } }).ComputeFingerprint());
    }

    private static PreviewProxy Proxy(IContainerProcess process) =>
        new(new DockerPreviewHttpTransport(process), NullLogger<PreviewProxy>.Instance);

    private static HttpClient Client(string address) => new(new HttpClientHandler
    { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(3) };

    private sealed class RelayDouble : IContainerProcess
    {
        public List<string[]> Calls { get; } = [];
        public int Status { get; set; } = 200;
        public byte[] Body { get; set; } = Encoding.UTF8.GetBytes("response");
        public bool Hang { get; set; }
        public async Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments.ToArray());
            if (Hang) await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            return new ContainerProcessResult(0, JsonSerializer.Serialize(new
            { status = Status, contentType = "text/plain", body = Convert.ToBase64String(Body) }));
        }
    }
}
