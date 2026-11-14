using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIPlatform.API;
using AIPlatform.API.Services;
using AIPlatform.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIPlatform.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task ListsAgentsModelsAndOpenApi()
    {
        using var factory = CreateFactory(new((_, _) => throw new InvalidOperationException("Must not call Ollama")));
        using var client = factory.CreateClient();
        var agents = await client.GetFromJsonAsync<AgentResponse[]>("/api/agents");
        var models = await client.GetFromJsonAsync<ModelResponse[]>("/api/models");
        Assert.Equal(9, agents!.Length);
        Assert.Contains(agents, agent => agent.Role == "Backend");
        Assert.Contains(agents, agent => agent.Role == "Frontend");
        Assert.Single(agents, agent => agent.IsRouter);
        Assert.Equal(2, models!.Length);
        Assert.Contains(models, model => model.AssignedAgents.Contains("DeveloperAgent"));
        var health = await client.GetFromJsonAsync<BackendHealthResponse>("/api/health");
        Assert.True(health!.Ready);
        Assert.Equal(Environment.ProcessId, health.ProcessId);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/agents", out _));
        Assert.True(paths.TryGetProperty("/api/models", out _));
        Assert.True(paths.TryGetProperty("/api/agents/run", out _));
    }

    [Fact]
    public async Task ReturnsExpectedMissionContract()
    {
        var provider = new FakeProvider((request, _) => Task.FromResult(request.IsRouting()
            ? """{"agentId":"DeveloperAgent","reason":"C# development"}""" : """{"response":"Generated code"}"""));
        using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/agents/run", new { task = "Write C# code" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<RunAgentResponse>();
        Assert.Equal("DeveloperAgent", result!.SelectedAgent);
        var registry = factory.Services.GetRequiredService<IAgentRegistry>();
        Assert.Equal(registry.GetModel("coder").Name, result.SelectedModel);
        Assert.Equal("C# development", result.Reason);
        Assert.Equal("Generated code", result.Response);
        Assert.False(result.UsedFallback);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"task\":null}")]
    [InlineData("{\"task\":\" \"}")]
    [InlineData("{\"task\":42}")]
    [InlineData("{invalid}")]
    public async Task InvalidPayloadReturns400WithoutCallingProvider(string body)
    {
        var provider = new FakeProvider((_, _) => throw new InvalidOperationException("Must not be called"));
        using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/agents/run", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task OversizedTaskReturns400WithoutCallingProvider()
    {
        var provider = new FakeProvider((_, _) => throw new InvalidOperationException("Must not be called"));
        using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        var limit = factory.Services.GetRequiredService<PlatformConfiguration>().MaxTaskLength;
        using var response = await client.PostAsJsonAsync("/api/agents/run", new { task = new string('a', limit + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(provider.Requests);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, HttpStatusCode.GatewayTimeout)]
    public async Task ProviderFailuresReturnSafeProblemDetails(bool timeout, HttpStatusCode status)
    {
        var provider = new FakeProvider((_, _) => throw (timeout
            ? new LlmTimeoutException("Private diagnostic") : new LlmProviderException("Private diagnostic")));
        using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/agents/run", new { task = "Hello" });
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("Private diagnostic", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task HttpRequestCancellationReachesProvider()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = client.PostAsJsonAsync("/api/agents/run", new { task = "Hello" }, cancellation.Token);
        await started.Task.WaitAsync(deadline.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await cancelled.Task.WaitAsync(deadline.Token);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task InvalidRouterResponseIsExplicitlyReportedAsFallback()
    {
        var provider = new FakeProvider((request, _) =>
            Task.FromResult(request.IsRouting() ? "invalid" : """{"response":"Answer"}"""));
        using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/agents/run", new { task = "Explain the sky" });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RunAgentResponse>();
        Assert.True(result!.UsedFallback);
        Assert.Equal("GeneralAgent", result.SelectedAgent);
        Assert.Contains("invalid routing JSON", result.Reason);
    }

    private static WebApplicationFactory<Program> CreateFactory(FakeProvider provider) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILLMProvider>();
                services.AddSingleton<ILLMProvider>(provider);
            });
        });
}
