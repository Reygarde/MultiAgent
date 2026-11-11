using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class ProjectGeneratorTests
{
    [Fact]
    public async Task SendsStructuredManifestSchemaAndConfiguredGenerationBudget()
    {
        using var fixture = new StudioFixture();
        var provider = new FakeProvider((request, cancellationToken) =>
        {
            Assert.Equal(fixture.Options.GenerationContextSize, request.Model.ContextSize);
            Assert.Equal(fixture.Options.GenerationMaxOutputTokens, request.Model.MaxOutputTokens);
            Assert.True(request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("files", out _));
            Assert.Contains("No shell commands", request.Messages[0].Content);
            Assert.Contains("CS1002", request.Messages[1].Content);
            return Task.FromResult(JsonSerializer.Serialize(StudioFixture.Manifest(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        });
        var generator = new ProjectGenerator(fixture.Registry, new LlmProviderResolver([provider]), fixture.Options);
        var result = await generator.GenerateAsync(fixture.Registry.Candidates[0], "Create API",
            StudioFixture.Manifest(), "CS1002", CancellationToken.None);
        Assert.Equal(4, result.Files.Count);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"summary\":\"Hello\",\"files\":null}")]
    [InlineData("{\"summary\":\"Hello\",\"files\":[],\"command\":\"powershell\"}")]
    [InlineData("{\"summary\":\"Hello\",\"files\":[null]}")]
    public async Task RejectsInvalidManifestsAndArbitraryCommands(string json)
    {
        using var fixture = new StudioFixture();
        var provider = new FakeProvider((_, _) => Task.FromResult(json));
        var generator = new ProjectGenerator(fixture.Registry, new LlmProviderResolver([provider]), fixture.Options);
        await Assert.ThrowsAsync<LlmProviderException>(() =>
            generator.GenerateAsync(fixture.Registry.Candidates[0], "Create API", null, null, CancellationToken.None));
    }
}
