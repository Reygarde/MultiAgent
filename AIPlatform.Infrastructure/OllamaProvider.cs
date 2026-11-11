using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIPlatform.Core;

namespace AIPlatform.Infrastructure;

public sealed class OllamaProvider : ILLMProvider, IDisposable
{
    private readonly HttpClient client;
    private readonly OllamaOptions options;
    private readonly SemaphoreSlim requests = new(1, 1);
    private string? loadedModel;
    private string? activeModel;

    public string Name => "Ollama";
    public string? ActiveModel => Volatile.Read(ref activeModel);

    public OllamaProvider(HttpClient client, OllamaOptions options)
    {
        options.Validate();
        this.client = client;
        this.options = options;
    }

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Model.Provider, Name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected model belongs to another provider.", nameof(request));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        var acquired = false;
        try
        {
            await requests.WaitAsync(deadline.Token);
            acquired = true;
            if (options.UnloadPreviousModel && loadedModel is not null && loadedModel != request.Model.Name)
            {
                using var unload = await client.PostAsJsonAsync(new Uri(new Uri(options.BaseUrl), "/api/generate"),
                    new { model = loadedModel, stream = false, keep_alive = 0 }, deadline.Token);
                unload.EnsureSuccessStatusCode();
                var result = await unload.Content.ReadFromJsonAsync<UnloadResponse>(deadline.Token);
                if (result?.Done != true || !string.IsNullOrWhiteSpace(result.Error))
                    throw new LlmProviderException("Ollama did not confirm the previous model was unloaded.");
                loadedModel = null;
            }

            var payload = new
            {
                model = request.Model.Name,
                messages = request.Messages.Select(message => new { role = message.Role, content = message.Content }),
                stream = false,
                think = false,
                format = request.ResponseSchema,
                keep_alive = options.KeepAliveSeconds,
                options = new
                {
                    num_ctx = request.Model.ContextSize,
                    num_predict = request.Model.MaxOutputTokens,
                    temperature = request.Model.Temperature
                }
            };

            // A cancelled or failed request may still have loaded the model in Ollama.
            loadedModel = request.Model.Name;
            Volatile.Write(ref activeModel, request.Model.Name);
            using var response = await client.PostAsJsonAsync(new Uri(new Uri(options.BaseUrl), "/api/chat"),
                payload, deadline.Token);
            response.EnsureSuccessStatusCode();
            var completion = await response.Content.ReadFromJsonAsync<ChatResponse>(deadline.Token);
            if (completion?.Done != true || !string.IsNullOrWhiteSpace(completion.Error) ||
                string.IsNullOrWhiteSpace(completion.Message?.Content))
                throw new LlmProviderException("Ollama returned an incomplete or empty response.");
            if (completion.DoneReason == "length")
                throw new LlmProviderException("Ollama reached the output token limit before completing the response.");

            return completion.Message.Content;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmTimeoutException("The Ollama request exceeded its configured timeout.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new LlmProviderException("Ollama is unavailable or rejected the request.", exception);
        }
        catch (JsonException exception)
        {
            throw new LlmProviderException("Ollama returned an invalid response.", exception);
        }
        catch (IOException exception)
        {
            throw new LlmProviderException("The Ollama response was interrupted.", exception);
        }
        finally
        {
            if (acquired)
            {
                Volatile.Write(ref activeModel, null);
                requests.Release();
            }
        }
    }

    public void Dispose() => requests.Dispose();

    private sealed record UnloadResponse(bool Done, string? Error);
    private sealed record ChatResponse(
        bool Done, ChatMessage? Message, string? Error,
        [property: JsonPropertyName("done_reason")] string? DoneReason);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
}
