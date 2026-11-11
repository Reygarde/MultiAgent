using AIPlatform.Core;

namespace AIPlatform.Tests;

public sealed class OrchestrationTests
{
    [Fact]
    public async Task ProviderFailureDuringExecutionIsNotHidden()
    {
        var provider = new FakeProvider((request, _) => request.IsRouting()
            ? Task.FromResult("""{"agentId":"DeveloperAgent","reason":"Code"}""")
            : throw new LlmProviderException("Offline"));
        using var orchestrator = Create(provider);
        await Assert.ThrowsAsync<LlmProviderException>(() => orchestrator.RunAsync("Write code", CancellationToken.None));
    }

    [Fact]
    public async Task MissionsAreSerializedAndQueuedCancellationDoesNotLeakPermit()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var provider = new FakeProvider(async (request, token) =>
        {
            if (request.IsRouting())
                return """{"agentId":"DeveloperAgent","reason":"Code"}""";
            if (Interlocked.Increment(ref executions) == 1)
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }
            return """{"response":"Answer"}""";
        });
        using var orchestrator = Create(provider);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = orchestrator.RunAsync("First code task", deadline.Token);
        await started.Task.WaitAsync(deadline.Token);
        using var queuedCancellation = new CancellationTokenSource();
        var queued = orchestrator.RunAsync("Second code task", queuedCancellation.Token);
        Assert.Equal(2, provider.Requests.Count);
        Assert.False(queued.IsCompleted);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.SetResult();
        await first;
        var third = await orchestrator.RunAsync("Third code task", deadline.Token);
        Assert.Equal("Answer", third.Response);
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task ExecutionCancellationReleasesMissionPermit()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider((request, token) =>
        {
            if (request.IsRouting())
                return Task.FromResult("""{"agentId":"GeneralAgent","reason":"General"}""");
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult("""{"response":"Answer"}""");
        });
        using var orchestrator = Create(provider);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.RunAsync("Hello", cancellation.Token));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await orchestrator.RunAsync("Hello again", deadline.Token);
        Assert.Equal("Answer", result.Response);
    }

    [Theory]
    [InlineData("invalid JSON")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"response\":null}")]
    [InlineData("{\"response\":\" \"}")]
    [InlineData("{\"response\":\"Answer\",\"extra\":\"invalid\"}")]
    [InlineData("{\"response\":\"First\",\"response\":\"Second\"}")]
    public async Task InvalidExecutionAnswersAreNotReportedAsSuccess(string completion)
    {
        var provider = new FakeProvider((request, _) => Task.FromResult(request.IsRouting()
            ? """{"agentId":"GeneralAgent","reason":"General"}""" : completion));
        using var orchestrator = Create(provider);
        await Assert.ThrowsAsync<LlmProviderException>(() => orchestrator.RunAsync("Hello", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidTaskDoesNotInvokeProvider(string task)
    {
        var provider = new FakeProvider((_, _) => throw new InvalidOperationException("Must not be called"));
        using var orchestrator = Create(provider);
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.RunAsync(task, CancellationToken.None));
        Assert.Empty(provider.Requests);
    }

    private static MissionOrchestrator Create(ILLMProvider provider)
    {
        var configuration = TestSupport.Configuration();
        var registry = new AgentRegistry(configuration);
        var resolver = new LlmProviderResolver([provider]);
        return new(registry, new LlmAgentRouter(registry, resolver), resolver, configuration);
    }
}
