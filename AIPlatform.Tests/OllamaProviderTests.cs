using System.Net;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Infrastructure;

namespace AIPlatform.Tests;

public sealed class OllamaProviderTests
{
    [Fact]
    public async Task SendsConfiguredMessagesSchemaAndResourceParameters()
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("/api/chat", message.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            var root = json.RootElement;
            Assert.Equal("configured-general", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.False(root.GetProperty("think").GetBoolean());
            Assert.Equal(120, root.GetProperty("keep_alive").GetInt32());
            Assert.Equal(4096, root.GetProperty("options").GetProperty("num_ctx").GetInt32());
            Assert.Equal(1024, root.GetProperty("options").GetProperty("num_predict").GetInt32());
            Assert.Equal(0.2, root.GetProperty("options").GetProperty("temperature").GetDouble());
            Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal("Hello", root.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.Equal("object", root.GetProperty("format").GetProperty("type").GetString());
            return StubHttpHandler.Json("""{"done":true,"message":{"content":"Answer"}}""");
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        var request = TestSupport.Request() with { ResponseSchema = JsonSerializer.SerializeToElement(new { type = "object" }) };
        Assert.Equal("Answer", await provider.CompleteAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task OnlyUnloadsWhenModelChangesAndBeforeLoadingNextModel()
    {
        var paths = new List<string>();
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            var root = json.RootElement;
            paths.Add(message.RequestUri!.AbsolutePath);
            if (paths.Count == 3)
            {
                Assert.Equal("/api/generate", message.RequestUri.AbsolutePath);
                Assert.Equal("first", root.GetProperty("model").GetString());
                Assert.Equal(0, root.GetProperty("keep_alive").GetInt32());
                return StubHttpHandler.Json("""{"done":true}""");
            }
            return StubHttpHandler.Json("""{"done":true,"message":{"content":"Answer"}}""");
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        await provider.CompleteAsync(TestSupport.Request("first"), CancellationToken.None);
        await provider.CompleteAsync(TestSupport.Request("first"), CancellationToken.None);
        await provider.CompleteAsync(TestSupport.Request("second"), CancellationToken.None);
        Assert.Equal(new[] { "/api/chat", "/api/chat", "/api/generate", "/api/chat" }, paths);
    }

    [Fact]
    public async Task UnloadFailurePreventsLoadingAnotherModel()
    {
        var calls = 0;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(++calls == 1
            ? StubHttpHandler.Json("""{"done":true,"message":{"content":"Answer"}}""")
            : StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        await provider.CompleteAsync(TestSupport.Request("first"), CancellationToken.None);
        await Assert.ThrowsAsync<LlmProviderException>(() =>
            provider.CompleteAsync(TestSupport.Request("second"), CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsAreSurfaced(HttpStatusCode status)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StubHttpHandler.Json("{}", status)));
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        await Assert.ThrowsAsync<LlmProviderException>(() =>
            provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));
    }

    [Fact]
    public async Task NetworkErrorsAreSurfaced()
    {
        using var handler = new StubHttpHandler((_, _) => throw new HttpRequestException("Connection refused"));
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        await Assert.ThrowsAsync<LlmProviderException>(() =>
            provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));
    }

    [Theory]
    [InlineData("invalid JSON")]
    [InlineData("null")]
    [InlineData("{\"done\":false,\"message\":{\"content\":\"partial\"}}")]
    [InlineData("{\"done\":true,\"message\":{\"content\":\" \"}}")]
    [InlineData("{\"done\":true,\"error\":\"failure\"}")]
    [InlineData("{\"done\":true,\"done_reason\":\"length\",\"message\":{\"content\":\"truncated\"}}")]
    public async Task InvalidOrIncompleteResponsesAreRejected(string json)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StubHttpHandler.Json(json)));
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        await Assert.ThrowsAsync<LlmProviderException>(() =>
            provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));
    }

    [Fact]
    public async Task CallsAreSerializedAndQueuedCancellationDoesNotChangeLoadedModel()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return message.RequestUri!.AbsolutePath == "/api/generate"
                ? StubHttpHandler.Json("""{"done":true}""")
                : StubHttpHandler.Json("""{"done":true,"message":{"content":"Answer"}}""");
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = provider.CompleteAsync(TestSupport.Request("first"), deadline.Token);
        await started.Task.WaitAsync(deadline.Token);
        using var cancellation = new CancellationTokenSource();
        var queued = provider.CompleteAsync(TestSupport.Request("second"), cancellation.Token);
        Assert.Equal(1, calls);
        Assert.False(queued.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.SetResult();
        Assert.Equal("Answer", await first);
        Assert.Equal("Answer", await provider.CompleteAsync(TestSupport.Request("second"), deadline.Token));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task TimeoutIsDistinguishedFromUserCancellation()
    {
        using var handler = new StubHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new() { RequestTimeoutSeconds = 1 });
        await Assert.ThrowsAsync<LlmTimeoutException>(() =>
            provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));
    }

    [Fact]
    public async Task UserCancellationPropagatesAndReleasesProviderPermit()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var handler = new StubHttpHandler((_, token) =>
        {
            if (++calls == 1)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(StubHttpHandler.Json("""{"done":true,"message":{"content":"Answer"}}"""));
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.CompleteAsync(TestSupport.Request(), cancellation.Token));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("Answer", await provider.CompleteAsync(TestSupport.Request(), deadline.Token));
    }

    [Theory]
    [InlineData("http://example.com:11434")]
    [InlineData("https://localhost:11434")]
    [InlineData("http://localhost:11434/other")]
    [InlineData("http://localhost:11434?redirect=remote")]
    public void RemoteOrInvalidOllamaOriginsAreRejected(string url)
    {
        Assert.Throws<ArgumentException>(() => new OllamaOptions { BaseUrl = url }.Validate());
    }
}
