using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIPlatform.API.Services;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AIPlatform.Tests;

public sealed class PreviewApiTests
{
    private static readonly string Token = new('a', 64);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task DesktopApiRequiresAuthenticationAndRunsTheSimulatedLifecycle()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new PreviewSessionManagerTests.FakePreviewRuntime();
        using var factory = Factory(fixture, runtime);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync($"/api/missions/{mission}/previews", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var badOrigin = new HttpRequestMessage(HttpMethod.Post, $"/api/missions/{mission}/previews");
        badOrigin.Headers.Add("Origin", "http://127.0.0.1:49123");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(badOrigin)).StatusCode);

        var response = await client.PostAsync($"/api/missions/{mission}/previews", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<PreviewSession>(JsonOptions))!;
        var plan = await client.GetFromJsonAsync<PreviewSession>($"/api/previews/{session.Id}/plan", JsonOptions);
        Assert.Equal(session.Fingerprint, plan!.Plan.ComputeFingerprint());
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/previews/{session.Id}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/api/previews/{session.Id}/approval",
                new { fingerprint = new string('A', 64) })).StatusCode);
        Assert.Equal(0, runtime.Starts);

        (await client.PostAsJsonAsync($"/api/previews/{session.Id}/approval",
            new { fingerprint = session.Fingerprint })).EnsureSuccessStatusCode();
        var started = await client.PostAsync($"/api/previews/{session.Id}/start", null);
        started.EnsureSuccessStatusCode();
        Assert.Equal(PreviewSessionStatus.Ready,
            (await started.Content.ReadFromJsonAsync<PreviewSession>(JsonOptions))!.Status);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/previews/{session.Id}/start", null)).StatusCode);
        (await client.PostAsync($"/api/previews/{session.Id}/stop", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/previews/{session.Id}/stop", null)).EnsureSuccessStatusCode();
        var events = await client.GetFromJsonAsync<PreviewLogEvent[]>($"/api/previews/{session.Id}/logs", JsonOptions);
        Assert.Equal(new[] { "Prepared", "Authorized", "Starting", "Ready", "Stopping", "Stopped" },
            events!.Select(item => item.Event));
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(1, runtime.Stops);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApiRefusesModifiedSourceOrImageAfterApproval(bool changeSource)
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new PreviewSessionManagerTests.FakePreviewRuntime();
        using var factory = Factory(fixture, runtime);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var prepared = (await (await client.PostAsync($"/api/missions/{mission}/previews", null))
            .Content.ReadFromJsonAsync<PreviewSession>(JsonOptions))!;
        (await client.PostAsJsonAsync($"/api/previews/{prepared.Id}/approval",
            new { fingerprint = prepared.Fingerprint })).EnsureSuccessStatusCode();
        if (changeSource)
            await File.AppendAllTextAsync(Path.Combine(fixture.Workspaces.GetDirectory(mission),
                "frontend", "src", "App.tsx"), "\n// changed");
        else
            runtime.ImageDigit = 'b';

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/previews/{prepared.Id}/start", null)).StatusCode);
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task HostedMaintenanceAutomaticallyExpiresSimulatedSession()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new PreviewSessionManagerTests.FakePreviewRuntime { LifetimeSeconds = 1 };
        using var factory = Factory(fixture, runtime);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var session = (await (await client.PostAsync($"/api/missions/{mission}/previews", null))
            .Content.ReadFromJsonAsync<PreviewSession>(JsonOptions))!;
        (await client.PostAsJsonAsync($"/api/previews/{session.Id}/approval",
            new { fingerprint = session.Fingerprint })).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/previews/{session.Id}/start", null)).EnsureSuccessStatusCode();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        PreviewSession? observed;
        do
        {
            await Task.Delay(100, deadline.Token);
            observed = await client.GetFromJsonAsync<PreviewSession>($"/api/previews/{session.Id}", JsonOptions, deadline.Token);
        } while (observed!.Status != PreviewSessionStatus.Expired);
        Assert.Equal(1, runtime.Stops);
    }

    [Fact]
    public async Task ConcurrentApiStartsConsumeApprovalOnlyOnce()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new PreviewSessionManagerTests.FakePreviewRuntime();
        using var factory = Factory(fixture, runtime);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var session = (await (await client.PostAsync($"/api/missions/{mission}/previews", null))
            .Content.ReadFromJsonAsync<PreviewSession>(JsonOptions))!;
        (await client.PostAsJsonAsync($"/api/previews/{session.Id}/approval",
            new { fingerprint = session.Fingerprint })).EnsureSuccessStatusCode();

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.PostAsync($"/api/previews/{session.Id}/start", null)));
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Equal(4, results.Count(result => result.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, runtime.Starts);
        (await client.PostAsync($"/api/previews/{session.Id}/stop", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task CleanupFailureDoesNotReportStopped()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new PreviewSessionManagerTests.FakePreviewRuntime();
        using var factory = Factory(fixture, runtime);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var session = (await (await client.PostAsync($"/api/missions/{mission}/previews", null))
            .Content.ReadFromJsonAsync<PreviewSession>(JsonOptions))!;
        (await client.PostAsJsonAsync($"/api/previews/{session.Id}/approval",
            new { fingerprint = session.Fingerprint })).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/previews/{session.Id}/start", null)).EnsureSuccessStatusCode();
        runtime.FailStop = true;

        Assert.Equal(HttpStatusCode.InternalServerError,
            (await client.PostAsync($"/api/previews/{session.Id}/stop", null)).StatusCode);
        var state = await client.GetFromJsonAsync<PreviewSession>($"/api/previews/{session.Id}", JsonOptions);
        Assert.Equal(PreviewSessionStatus.Failed, state!.Status);
        var logs = await client.GetFromJsonAsync<PreviewLogEvent[]>($"/api/previews/{session.Id}/logs", JsonOptions);
        Assert.Contains(logs!, item => item.Event == "CleanupFailed");
        runtime.FailStop = false;
        (await client.PostAsync($"/api/previews/{session.Id}/stop", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task RealConfigurationFailsClosedWithoutSecureRuntime()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        using var factory = Factory(fixture, new UnavailablePreviewRuntime());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await client.PostAsync($"/api/missions/{mission}/previews", null)).StatusCode);
    }

    [Fact]
    public async Task DevelopmentWithoutDesktopAuthenticationCannotUsePreviewEndpoints()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new PreviewSessionManagerTests.FakePreviewRuntime();
        using var factory = Factory(fixture, runtime, desktopEnabled: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync($"/api/missions/{mission}/previews", null)).StatusCode);
        Assert.Equal(0, runtime.Starts);
    }

    private static WebApplicationFactory<Program> Factory(
        WorkflowFixture fixture, IPreviewRuntime runtime, bool desktopEnabled = true)
    {
        var manager = new PreviewSessionManager(new PreviewSourceVerifier(fixture.Store, fixture.Workspaces),
            runtime, new PreviewAuthorizationStore());
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DesktopSession>();
                services.AddSingleton(new DesktopSession(desktopEnabled, desktopEnabled ? Token : null));
                services.RemoveAll<IMissionService>();
                services.AddSingleton<IMissionService>(fixture.Store);
                services.RemoveAll<PreviewSessionManager>();
                services.AddSingleton(manager);
                services.RemoveAll<IPreviewRuntime>();
                services.AddSingleton<IPreviewRuntime>(runtime);
                services.RemoveAll<IHostedService>();
                services.AddHostedService<PreviewMaintenance>();
            });
        });
    }
}
