using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AIPlatform.Core;

namespace AIPlatform.Tests;

internal sealed class FakeProvider(
    Func<LlmRequest, CancellationToken, Task<string>> complete) : ILLMProvider
{
    public string Name => "Ollama";
    public ConcurrentQueue<LlmRequest> Requests { get; } = new();

    public Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return complete(request, cancellationToken);
    }
}

internal sealed class StubHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal static class TestSupport
{
    public static bool IsRouting(this LlmRequest request) =>
        request.ResponseSchema is { } schema &&
        schema.TryGetProperty("properties", out var properties) &&
        properties.TryGetProperty("agentId", out _);

    public static PlatformConfiguration Configuration() => new()
    {
        RouterAgentId = "RouterAgent",
        FallbackAgentId = "GeneralAgent",
        Models =
        [
            new() { Id = "general", Provider = "Ollama", Name = "configured-general" },
            new() { Id = "developer", Provider = "Ollama", Name = "configured-coder" }
        ],
        Agents =
        [
            new() { Id = "RouterAgent", ModelId = "general", Description = "Routing", SystemPrompt = "Route tasks." },
            new()
            {
                Id = "DeveloperAgent", ModelId = "developer", Description = "Software development",
                SystemPrompt = "Write safe code.", Capabilities = ["C#", "TypeScript"],
                FallbackKeywords = ["C#", "TypeScript", "code", "API", "Discord", "Twitch"]
            },
            new() { Id = "GeneralAgent", ModelId = "general", Description = "General help", SystemPrompt = "Explain clearly." }
        ]
    };

    public static LlmAgentRouter Router(ILLMProvider provider, PlatformConfiguration? configuration = null) =>
        new(new AgentRegistry(configuration ?? Configuration()), new LlmProviderResolver([provider]));

    public static LlmRequest Request(string model = "configured-general") =>
        new(new() { Id = model, Provider = "Ollama", Name = model },
            [new("system", "Help."), new("user", "Hello")]);
}
