using System.Text.Json;
using AIPlatform.Core;

namespace AIPlatform.Tests;

public sealed class RoutingTests
{
    [Theory]
    [InlineData("DeveloperAgent", "Write a C# API", "configured-coder")]
    [InlineData("GeneralAgent", "Explain the sky", "configured-general")]
    public async Task RoutesAndExecutesWithTheConfiguredAgent(string agentId, string task, string model)
    {
        var provider = new FakeProvider((request, _) => Task.FromResult(request.IsRouting()
            ? JsonSerializer.Serialize(new { agentId, reason = "Capabilities match." })
            : """{"response":"Generated answer"}"""));
        var configuration = TestSupport.Configuration();
        var registry = new AgentRegistry(configuration);
        var resolver = new LlmProviderResolver([provider]);
        using var orchestrator = new MissionOrchestrator(
            registry, new LlmAgentRouter(registry, resolver), resolver, configuration);

        var result = await orchestrator.RunAsync(task, CancellationToken.None);

        Assert.Equal(agentId, result.SelectedAgent);
        Assert.Equal(model, result.SelectedModel);
        Assert.Equal("Capabilities match.", result.Reason);
        Assert.Equal("Generated answer", result.Response);
        Assert.False(result.UsedFallback);
        var requests = provider.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(task, requests[1].Messages.Single(message => message.Role == "user").Content);
        Assert.StartsWith(registry.Agents.Single(agent => agent.Id == agentId).SystemPrompt,
            requests[1].Messages.Single(message => message.Role == "system").Content);
        Assert.True(requests[1].ResponseSchema!.Value.GetProperty("properties").TryGetProperty("response", out _));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("```json\n{\"agentId\":\"DeveloperAgent\",\"reason\":\"code\"}\n```")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"agentId\":\"InventedAgent\",\"reason\":\"code\"}")]
    [InlineData("{\"agentId\":\"RouterAgent\",\"reason\":\"code\"}")]
    [InlineData("{\"agentId\":\"DeveloperAgent\",\"reason\":\" \"}")]
    [InlineData("{\"agentId\":\"DeveloperAgent\",\"reason\":null}")]
    [InlineData("{\"agentId\":\"DeveloperAgent\",\"reason\":\"code\",\"model\":\"unauthorized\"}")]
    [InlineData("{\"agentId\":\"DeveloperAgent\",\"agentId\":\"GeneralAgent\",\"reason\":\"code\"}")]
    public async Task InvalidRoutingUsesDeterministicFallback(string response)
    {
        var provider = new FakeProvider((_, _) => Task.FromResult(response));
        var result = await TestSupport.Router(provider).RouteAsync("Write a C# API", CancellationToken.None);

        Assert.Equal("DeveloperAgent", result.Agent.Id);
        Assert.True(result.UsedFallback);
        Assert.Contains("fallback", result.Reason);
    }

    [Fact]
    public async Task FallbackUsesGeneralAgentWhenNoKeywordsMatch()
    {
        var provider = new FakeProvider((_, _) => Task.FromResult("invalid"));
        var result = await TestSupport.Router(provider).RouteAsync("Explain the sky", CancellationToken.None);
        Assert.Equal("GeneralAgent", result.Agent.Id);
        Assert.True(result.UsedFallback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureDuringRoutingFallsBack(bool timeout)
    {
        var provider = new FakeProvider((_, _) => throw (timeout
            ? new LlmTimeoutException("Timeout") : new LlmProviderException("Offline")));
        var result = await TestSupport.Router(provider).RouteAsync("Write code", CancellationToken.None);
        Assert.True(result.UsedFallback);
        Assert.Contains(timeout ? "timeout" : "unavailable", result.Reason);
    }

    [Fact]
    public async Task NewAgentsAreIncludedWithoutChangingRouterCode()
    {
        var configuration = TestSupport.Configuration();
        configuration.Agents.Add(new()
        {
            Id = "ResearchAgent", Description = "Research", ModelId = "general",
            SystemPrompt = "Research.", Capabilities = ["research"], FallbackKeywords = ["research"]
        });
        var provider = new FakeProvider((request, _) =>
        {
            Assert.Contains("ResearchAgent", request.Messages[0].Content);
            Assert.Contains("ResearchAgent", request.ResponseSchema!.Value.GetRawText());
            Assert.DoesNotContain("RouterAgent", request.ResponseSchema.Value.GetRawText());
            return Task.FromResult("""{"agentId":"ResearchAgent","reason":"Research task"}""");
        });
        var result = await TestSupport.Router(provider, configuration).RouteAsync("Research something", CancellationToken.None);
        Assert.Equal("ResearchAgent", result.Agent.Id);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public async Task CancellationIsNotConvertedToFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult("");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TestSupport.Router(provider).RouteAsync("Write code", cancellation.Token));
    }

    [Fact]
    public void RegistryRejectsInvalidAssignmentsAndRouterFallback()
    {
        var configuration = TestSupport.Configuration();
        Assert.Throws<ArgumentException>(() => new AgentRegistry(configuration with { FallbackAgentId = "RouterAgent" }));
        configuration.Agents.Add(new() { Id = "BadAgent", ModelId = "missing", Description = "Bad", SystemPrompt = "Bad" });
        Assert.Throws<ArgumentException>(() => new AgentRegistry(configuration));
    }
}
