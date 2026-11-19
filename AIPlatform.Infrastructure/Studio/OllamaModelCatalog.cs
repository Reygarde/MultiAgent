using System.Net.Http.Json;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class OllamaModelCatalog(HttpClient client, OllamaOptions options) : IOllamaModelCatalog
{
    public async Task<IReadOnlyList<string>> GetCompatibleInstalledModelsAsync(
        CancellationToken cancellationToken, IReadOnlyList<string>? requestedModels = null)
    {
        var installed = (await GetInstalledModelsAsync(cancellationToken))
            .Where(model => requestedModels is null || requestedModels.Contains(model)).ToArray();
        var compatible = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            foreach (var model in installed)
            {
                using var response = await client.PostAsJsonAsync(new Uri(new Uri(options.BaseUrl), "/api/show"),
                    new { model, verbose = false }, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    throw new LlmProviderException("Ollama model capabilities are unavailable.");
                var result = await response.Content.ReadFromJsonAsync<OllamaCapabilities>(timeout.Token);
                if (result?.Capabilities is null)
                    throw new LlmProviderException("Ollama omitted model capabilities; compatibility could not be verified.");
                if (result.Capabilities.Contains("completion", StringComparer.Ordinal))
                    compatible.Add(model);
            }
            return compatible;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmTimeoutException("Ollama model capability discovery timed out.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            throw new LlmProviderException("Ollama model capabilities are unavailable.", exception);
        }
    }

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
    private sealed record OllamaCapabilities(IReadOnlyList<string>? Capabilities);
}
