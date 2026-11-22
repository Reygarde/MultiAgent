using System.Net.Http.Json;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class LocalModelStatus(
    HttpClient client, OllamaOptions options, IAgentRegistry registry, IEnumerable<ILLMProvider> providers) : ILocalModelStatus
{
    public async Task<LocalModelsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var tags = await client.GetFromJsonAsync<OllamaModels>(
                new Uri(new Uri(options.BaseUrl), "/api/tags"), timeout.Token);
            var running = await client.GetFromJsonAsync<OllamaModels>(
                new Uri(new Uri(options.BaseUrl), "/api/ps"), timeout.Token);
            if (tags?.Models is null || running?.Models is null)
                return new(false, "Ollama returned incomplete model telemetry.", []);
            var active = providers.OfType<OllamaProvider>().Select(provider => provider.ActiveModel).ToHashSet();
            var models = registry.Models.Select(model => model.Name)
                .Concat(tags.Models.Select(local => local.Name))
                .Concat(running.Models.Select(local => local.Name))
                .Distinct(StringComparer.Ordinal)
                .Select(name => new LocalModelState(
                    name,
                    tags.Models.Any(local => local.Name == name),
                    running.Models.Any(local => local.Name == name),
                    active.Contains(name))).ToArray();
            return new(true, null, models);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            return new(false, "Ollama model telemetry is unavailable.", []);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "Ollama model telemetry timed out.", []);
        }
    }

    private sealed record OllamaModels(IReadOnlyList<OllamaModel>? Models);
    private sealed record OllamaModel(string Name);
}
