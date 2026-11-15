using System.Net.Http.Json;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class OllamaModelCatalog(HttpClient client, OllamaOptions options) : IOllamaModelCatalog
{
    public async Task<IReadOnlyList<string>> GetInstalledModelsAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await client.GetAsync(
                new Uri(new Uri(options.BaseUrl), "/api/tags"), timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new LlmProviderException("Ollama model inventory is unavailable.");
            var result = await response.Content.ReadFromJsonAsync<OllamaTags>(timeout.Token);
            if (result?.Models is null)
                throw new LlmProviderException("Ollama returned incomplete model inventory.");
            return result.Models.Select(model => model.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmTimeoutException("Ollama model inventory request timed out.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            throw new LlmProviderException("Ollama model inventory is unavailable.", exception);
        }
    }

    private sealed record OllamaTags(IReadOnlyList<OllamaModel>? Models);
    private sealed record OllamaModel(string Name);
}
