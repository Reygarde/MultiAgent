using System.Net;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Infrastructure;

namespace AIPlatform.Tests;

public sealed class OllamaProviderTests
{
    [Theory]
    [InlineData(65536)]
    [InlineData(262144)]
    public async Task SendsExplicitLargeContextWithoutChangingOutputOrThinking(int context)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal(context, json.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
            Assert.Equal(1024, json.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
            Assert.False(json.RootElement.GetProperty("think").GetBoolean());
            return StubHttpHandler.Json("""{"done":true,"message":{"content":"Answer"}}""");
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        var request = TestSupport.Request();
        Assert.Equal("Answer", await provider.CompleteAsync(request with
            { Model = request.Model with { ContextSize = context } }, default));
    }

    [Theory]
    [InlineData(262145, 1024)]
    [InlineData(65536, 65536)]
    [InlineData(65536, 0)]
    public async Task InvalidContextOutputFailsBeforeAnyHttpRequest(int context, int output)
    {
        var calls = 0;
        using var handler = new StubHttpHandler((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("No HTTP call is permitted.");
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        var request = TestSupport.Request();
        await Assert.ThrowsAsync<LlmContextException>(() => provider.CompleteAsync(request with
            { Model = request.Model with { ContextSize = context, MaxOutputTokens = output } }, default));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("model requires more system memory")]
    [InlineData("CUDA out of memory")]
    [InlineData("unable to allocate tensor memory")]
    public async Task MemoryErrorsAreTypedAndNeverStartAnotherGeneration(string error)
    {
        var calls = 0;
        using var handler = new StubHttpHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(StubHttpHandler.Json(JsonSerializer.Serialize(new { error }), HttpStatusCode.InternalServerError));
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        await Assert.ThrowsAsync<LlmMemoryException>(() => provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ContextErrorsDoNotStartAnAlternativeModel()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(
            StubHttpHandler.Json("""{"error":"context length exceeded"}""", HttpStatusCode.BadRequest)));
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        await Assert.ThrowsAsync<LlmContextException>(() => provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));
    }

    [Fact]
    public async Task ExclusiveSelectionUnloadsPreviousModelEvenWhenGlobalUnloadIsOff()
    {
        var paths = new List<string>();
        using var handler = new StubHttpHandler((message, _) =>
        {
            paths.Add(message.RequestUri!.AbsolutePath);
            return Task.FromResult(StubHttpHandler.Json(message.RequestUri.AbsolutePath == "/api/generate"
                ? """{"done":true}""" : """{"done":true,"message":{"content":"Answer"}}"""));
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new() { UnloadPreviousModel = false });
        await provider.CompleteAsync(TestSupport.Request("first") with { ExclusiveModel = true }, CancellationToken.None);
        await provider.CompleteAsync(TestSupport.Request("second") with { ExclusiveModel = true }, CancellationToken.None);
        Assert.Equal(new[] { "/api/chat", "/api/generate", "/api/chat" }, paths);
    }

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
    public async Task InvalidOrIncompleteResponsesAreRejected(string json)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StubHttpHandler.Json(json)));
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        await Assert.ThrowsAsync<LlmProviderException>(() =>
            provider.CompleteAsync(TestSupport.Request(), CancellationToken.None));
    }

    [Theory]
    [InlineData("partial JSON", "")]
    [InlineData("", "reasoning only")]
    public async Task OutputLimitRejectsEvenEmptyContentWithoutRetryAndReportsAvailableCounts(string content, string thinking)
    {
        var calls = 0;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            calls++;
            using var input = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.False(input.RootElement.GetProperty("think").GetBoolean());
            Assert.Equal(3072, input.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
            return StubHttpHandler.Json(JsonSerializer.Serialize(new
            {
                done = true, done_reason = "length", eval_count = 3072,
                message = new { content, thinking }
            }));
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaProvider(client, new());
        var request = TestSupport.Request() with
        { Model = TestSupport.Request().Model with { Name = "selected-code-model", MaxOutputTokens = 3072 } };
        var error = await Assert.ThrowsAsync<LlmOutputLimitException>(() => provider.CompleteAsync(request, default));
        Assert.Equal(3072, error.EvaluatedTokens);
        Assert.Equal(content.Length, error.ContentCharacters);
        Assert.Equal(thinking.Length, error.ThinkingCharacters);
        Assert.Contains("selected-code-model", error.Message);
        Assert.Equal("OutputLimit", AIPlatform.Infrastructure.Studio.CustomAgentModelPolicy.Classify(error));
        Assert.Equal(1, calls);
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
