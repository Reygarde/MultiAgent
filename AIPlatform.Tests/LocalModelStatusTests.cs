using System.Net;
using AIPlatform.Core;
using AIPlatform.Infrastructure;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class LocalModelStatusTests
{
    [Fact]
    public async Task DistinguishesInstalledLoadedAndActiveModels()
    {
        var registry = new AgentRegistry(TestSupport.Configuration());
        using var handler = new StubHttpHandler((request, _) => Task.FromResult(StubHttpHandler.Json(
            request.RequestUri!.AbsolutePath == "/api/tags"
                ? """{"models":[{"name":"configured-general"},{"name":"configured-coder"}]}"""
                : """{"models":[{"name":"configured-general"}]}""")));
        using var client = new HttpClient(handler);
        var status = new LocalModelStatus(client, new(), registry, []);
        var snapshot = await status.GetAsync(CancellationToken.None);
        Assert.True(snapshot.Available);
        var general = snapshot.Models.Single(model => model.Name == "configured-general");
        var coder = snapshot.Models.Single(model => model.Name == "configured-coder");
        Assert.True(general.Installed);
        Assert.True(general.Loaded);
        Assert.False(general.InUse);
        Assert.True(coder.Installed);
        Assert.False(coder.Loaded);
    }

    [Fact]
    public async Task UnavailableTelemetryDoesNotClaimModelsAreUninstalled()
    {
        var registry = new AgentRegistry(TestSupport.Configuration());
        using var handler = new StubHttpHandler((_, _) =>
            Task.FromResult(StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler);
        var status = new LocalModelStatus(client, new(), registry, []);
        var snapshot = await status.GetAsync(CancellationToken.None);
        Assert.False(snapshot.Available);
        Assert.NotEmpty(snapshot.Error!);
        Assert.Empty(snapshot.Models);
    }
}
