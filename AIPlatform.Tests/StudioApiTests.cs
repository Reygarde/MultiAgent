using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIPlatform.API;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIPlatform.Tests;

public sealed class StudioApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task QueuesProjectAndExposesEventsFilesAndExplicitUnverifiedResults()
    {
        using var fixture = new StudioFixture();
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/missions",
            new { task = "Create .NET API", kind = "Project", approveSandboxExecution = true });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var initial = await created.Content.ReadFromJsonAsync<MissionResponse>(JsonOptions);
        Assert.Equal($"/api/missions/{initial!.Id}", created.Headers.Location!.ToString());
        var terminal = await WaitForTerminal(client, initial.Id);
        Assert.Equal(MissionStatus.GeneratedUnverified, terminal.Status);
        Assert.Equal("DeveloperAgent", terminal.SelectedAgent);
        var events = await client.GetFromJsonAsync<MissionEventResponse[]>($"/api/missions/{initial.Id}/events?after=2", JsonOptions);
        Assert.NotEmpty(events!);
        Assert.All(events!, item => Assert.True(item.Sequence > 2));
        var verification = await client.GetFromJsonAsync<VerificationResponse[]>($"/api/missions/{initial.Id}/verification", JsonOptions);
        Assert.Equal(2, verification!.Length);
        Assert.All(verification, item => { Assert.Null(item.ExitCode); Assert.Equal("Unavailable", item.Status); });
        var projects = await client.GetFromJsonAsync<MissionResponse[]>("/api/workspaces", JsonOptions);
        Assert.Single(projects!);
        var files = await client.GetFromJsonAsync<WorkspaceFileResponse[]>($"/api/workspaces/{initial.Id}/files");
        Assert.Equal(5, files!.Length);
        var content = await client.GetFromJsonAsync<WorkspaceContentResponse>(
            $"/api/workspaces/{initial.Id}/file?path=src%2FApp%2FProgram.cs");
        Assert.Contains("WebApplication", content!.Content);
        using var traversal = await client.GetAsync($"/api/workspaces/{initial.Id}/file?path=..%2F..%2Fappsettings.json");
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
        using var missing = await client.GetAsync($"/api/workspaces/{initial.Id}/file?path=src%2FApp%2FMissing.cs");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var alreadyFinished = await client.PostAsync($"/api/missions/{initial.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, alreadyFinished.StatusCode);
    }

    [Fact]
    public async Task ExportsExistingProjectAndRejectsPossibleCredentials()
    {
        using var fixture = new StudioFixture();
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        var created = await (await client.PostAsJsonAsync("/api/missions",
            new { task = "Create .NET API", kind = "Project" })).Content.ReadFromJsonAsync<MissionResponse>(JsonOptions);
        await WaitForTerminal(client, created!.Id);
        using var response = await client.GetAsync($"/api/workspaces/{created.Id}/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("src/App/Program.cs"));

        var source = Path.Combine(fixture.Workspaces.GetDirectory(created.Id), "src", "App", "Extra.cs");
        await File.WriteAllTextAsync(source, """class C { string password = "dummy"; }""");
        using var blocked = await client.GetAsync($"/api/workspaces/{created.Id}/export");
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.DoesNotContain("dummy", await blocked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnalyzesGeneratedWorkspaceWithoutExecutingIt()
    {
        using var fixture = new StudioFixture();
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        var created = await (await client.PostAsJsonAsync("/api/missions",
            new { task = "Create .NET API", kind = "Project" })).Content.ReadFromJsonAsync<MissionResponse>(JsonOptions);
        await WaitForTerminal(client, created!.Id);
        var result = await client.GetFromJsonAsync<ProjectAnalysisResponse>($"/api/workspaces/{created.Id}/analysis");
        Assert.NotNull(result);
        Assert.Null(result.GitRepositoryDetected);
        Assert.Contains("C#", result.Languages);
        Assert.Contains("ASP.NET Core", result.Frameworks);
        Assert.Contains("net10.0", result.Frameworks);
        Assert.Contains("tests/App.Tests/App.Tests.csproj", result.TestProjects);
        Assert.Contains(result.Dependencies, item => item.Name == "xunit" && item.Version == "2.9.3");
        Assert.Empty(result.Scripts);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task InvalidMissionsAreRejectedWithoutCallingProvider(string task)
    {
        using var fixture = new StudioFixture();
        var provider = new FakeProvider((_, _) => throw new InvalidOperationException("Must not call"));
        using var factory = Factory(fixture, provider);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/missions", new { task });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(provider.Requests);
        using var badAgent = await client.PostAsJsonAsync("/api/missions", new { task = "hello", agentId = "Invented" });
        Assert.Equal(HttpStatusCode.BadRequest, badAgent.StatusCode);
    }

    [Fact]
    public async Task ManualSelectionBypassesRouterAndProviderErrorBecomesFailedMission()
    {
        using var fixture = new StudioFixture();
        var provider = new FakeProvider((_, _) => throw new LlmProviderException("Local provider unavailable."));
        using var factory = Factory(fixture, provider);
        using var client = factory.CreateClient();
        var initial = await (await client.PostAsJsonAsync("/api/missions",
            new { task = "Write code", kind = "Chat", agentId = "DeveloperAgent" })).Content.ReadFromJsonAsync<MissionResponse>(JsonOptions);
        var terminal = await WaitForTerminal(client, initial!.Id);
        Assert.Equal(MissionStatus.Failed, terminal.Status);
        Assert.Equal("DeveloperAgent", terminal.SelectedAgent);
        Assert.Contains("unavailable", terminal.Error);
        Assert.Single(provider.Requests);
        Assert.False(provider.Requests.Single().IsRouting());
    }

    [Fact]
    public async Task LongMissionReturnsImmediatelyCanBeCancelledAndDoesNotOverlapLegacyEndpoint()
    {
        using var fixture = new StudioFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async (request, token) =>
        {
            if (request.Messages.Any(message => message.Content == "Long mission"))
            {
                entered.TrySetResult();
                using var registration = token.Register(() => cancelled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return request.IsRouting() ? """{"agentId":"GeneralAgent","reason":"General"}""" : """{"response":"Answer"}""";
        });
        using var factory = Factory(fixture, provider);
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = await (await client.PostAsJsonAsync("/api/missions",
            new { task = "Long mission", agentId = "GeneralAgent" }, deadline.Token)).Content.ReadFromJsonAsync<MissionResponse>(JsonOptions, deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        var legacy = client.PostAsJsonAsync("/api/agents/run", new { task = "Hello" }, deadline.Token);
        await Task.Delay(50, deadline.Token);
        Assert.False(legacy.IsCompleted);
        Assert.Single(provider.Requests);
        using var cancellation = await client.PostAsync($"/api/missions/{initial!.Id}/cancel", null, deadline.Token);
        Assert.Equal(HttpStatusCode.Accepted, cancellation.StatusCode);
        await cancelled.Task.WaitAsync(deadline.Token);
        var terminal = await WaitForTerminal(client, initial.Id);
        Assert.Equal(MissionStatus.Cancelled, terminal.Status);
        Assert.DoesNotContain(terminal.Events.GroupBy(item => item.Step).Select(group => group.Last()),
            item => item.Status == StepStatus.Running);
        (await legacy).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ReviewsPlanRejectsStaleConsentAndResumesWithoutBlockingOtherMissions()
    {
        using var fixture = new StudioFixture();
        using var factory = Factory(fixture, sandbox: new FakeSandbox(true));
        using var client = factory.CreateClient();
        var initial = await (await client.PostAsJsonAsync("/api/missions",
            new { task = "Create library", kind = "Project" })).Content.ReadFromJsonAsync<MissionResponse>(JsonOptions);
        Assert.Equal(MissionStatus.AwaitingApproval, (await WaitForTerminal(client, initial!.Id)).Status);
        var plan = await client.GetFromJsonAsync<ExecutionPlanResponse>($"/api/missions/{initial.Id}/execution-plan");
        Assert.Equal(3, plan!.MaxCorrectionAttempts);
        Assert.Contains(plan.Operations, operation => operation.Contains("no network", StringComparison.OrdinalIgnoreCase));
        using var stale = await client.PostAsJsonAsync($"/api/missions/{initial.Id}/approval",
            new { approved = true, fingerprint = "stale" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var chat = await (await client.PostAsJsonAsync("/api/missions",
            new { task = "A chat while authorization is pending", agentId = "GeneralAgent" }))
            .Content.ReadFromJsonAsync<MissionResponse>(JsonOptions);
        Assert.Equal(MissionStatus.Completed, (await WaitForTerminal(client, chat!.Id)).Status);
        using var approval = await client.PostAsJsonAsync($"/api/missions/{initial.Id}/approval",
            new { approved = true, plan.Fingerprint });
        Assert.Equal(HttpStatusCode.Accepted, approval.StatusCode);
        var result = await WaitForTerminal(client, initial.Id);
        Assert.Equal(MissionStatus.Verified, result.Status);
        Assert.Equal(2, result.Verification.Count);
        Assert.All(result.Verification, item => Assert.True(item.DurationMilliseconds >= 0));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/missions/{initial.Id}/approval",
            new { approved = true, plan.Fingerprint })).StatusCode);
    }

    [Fact]
    public async Task RefusingExecutionRetainsFilesWithExplicitSkippedResults()
    {
        using var fixture = new StudioFixture();
        var sandbox = new FakeSandbox(true);
        using var factory = Factory(fixture, sandbox: sandbox);
        using var client = factory.CreateClient();
        var initial = await (await client.PostAsJsonAsync("/api/missions",
            new { task = "Create library", kind = "Project" })).Content.ReadFromJsonAsync<MissionResponse>(JsonOptions);
        await WaitForTerminal(client, initial!.Id);
        var plan = await client.GetFromJsonAsync<ExecutionPlanResponse>($"/api/missions/{initial.Id}/execution-plan");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/missions/{initial.Id}/approval",
            new { approved = false, plan!.Fingerprint })).StatusCode);
        var result = await WaitForTerminal(client, initial.Id);
        Assert.Equal(MissionStatus.GeneratedUnverified, result.Status);
        Assert.True(result.HasWorkspace);
        Assert.All(result.Verification, item => Assert.Equal("Skipped", item.Status));
        Assert.Empty(sandbox.Calls);
    }

    [Fact]
    public async Task UnknownIdentifiersReturn404AndExternalOriginsAreForbidden()
    {
        using var fixture = new StudioFixture();
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        foreach (var path in new[] { $"/api/missions/{id}", $"/api/missions/{id}/events",
                     $"/api/missions/{id}/verification", $"/api/workspaces/{id}/files",
                     $"/api/workspaces/{id}/file?path=README.md", $"/api/workspaces/{id}/export",
                     $"/api/workspaces/{id}/analysis", $"/api/missions/{id}/execution-plan" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/missions/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/missions/{id}/approval",
            new { approved = true, fingerprint = "missing" })).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/missions");
        request.Headers.Add("Origin", "https://untrusted.example");
        request.Content = JsonContent.Create(new { task = "hello" });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task OpenApiIncludesStudioEndpoints()
    {
        using var fixture = new StudioFixture();
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/api/missions", "/api/missions/{id}", "/api/missions/{id}/events",
                     "/api/missions/{id}/cancel", "/api/workspaces", "/api/workspaces/{id}/file",
                     "/api/models/local", "/api/sandbox", "/api/missions/{id}/approval", "/api/missions/{id}/execution-plan",
                     "/api/workspaces/{id}/export", "/api/workspaces/{id}/analysis" })
            Assert.True(paths.TryGetProperty(path, out _), path);
    }

    private static WebApplicationFactory<Program> Factory(
        StudioFixture fixture, ILLMProvider? provider = null, ISandboxRunner? sandbox = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<StudioOptions>();
                services.AddSingleton(fixture.Options);
                services.RemoveAll<ILLMProvider>();
                services.AddSingleton(provider ?? new FakeProvider((request, _) => Task.FromResult(request.IsRouting()
                    ? """{"agentId":"DeveloperAgent","reason":"Code"}""" : """{"response":"Answer"}""")));
                services.RemoveAll<IProjectGenerator>();
                services.AddSingleton<IProjectGenerator>(new FakeProjectGenerator());
                services.RemoveAll<ISandboxRunner>();
                services.AddSingleton(sandbox ?? new FakeSandbox());
            });
        });

    private static async Task<MissionResponse> WaitForTerminal(HttpClient client, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var mission = await client.GetFromJsonAsync<MissionResponse>($"/api/missions/{id}", JsonOptions, timeout.Token);
            if (mission!.Status is not (MissionStatus.Queued or MissionStatus.Running))
                return mission;
            await Task.Delay(20, timeout.Token);
        }
    }
}
