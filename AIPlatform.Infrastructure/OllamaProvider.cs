using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using AIPlatform.Core;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Infrastructure;

public sealed class OllamaProvider : IGenerationBudgetedProvider, IDisposable
{
    private readonly HttpClient client;
    private readonly OllamaOptions options;
    private readonly SemaphoreSlim requests = new(1, 1);
    private string? loadedModel;
    private string? activeModel;
    private readonly OllamaDiagnosticStore? diagnostics;
    private readonly OllamaModelOperations? modelOperations;
    private readonly AIPlatform.Core.Studio.IModelLibraryService? library;

    public string Name => "Ollama";
    public string? ActiveModel => Volatile.Read(ref activeModel);

    public OllamaProvider(HttpClient client, OllamaOptions options, OllamaDiagnosticStore? diagnostics = null,
        OllamaModelOperations? modelOperations = null, AIPlatform.Core.Studio.IModelLibraryService? library = null)
    {
        options.Validate();
        this.client = client;
        this.options = options;
        this.diagnostics = diagnostics;
        this.modelOperations = modelOperations;
        this.library = library;
    }

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Model.Provider, Name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected model belongs to another provider.", nameof(request));
        if (request.Model.ContextSize is < 256 or > ModelContextLimits.Maximum ||
            request.Model.MaxOutputTokens < 1 || request.Model.MaxOutputTokens >= request.Model.ContextSize)
            throw new LlmContextException("Invalid bounded context/output profile; no generation was requested.");
        var reservationId = LlmGenerationScope.Reserve(request, cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        var acquired = false;
        var operationAcquired = false;
        var mayHaveBeenSent = false;
        try
        {
            await requests.WaitAsync(deadline.Token);
            acquired = true;
            if (modelOperations is not null)
            {
                await modelOperations.Gate.WaitAsync(deadline.Token);
                operationAcquired = true;
            }
            if ((options.UnloadPreviousModel || request.ExclusiveModel) && loadedModel is not null && loadedModel != request.Model.Name)
            {
                using var unload = await client.PostAsJsonAsync(new Uri(new Uri(options.BaseUrl), "/api/generate"),
                    new { model = loadedModel, stream = false, keep_alive = 0 }, deadline.Token);
                unload.EnsureSuccessStatusCode();
                var result = await unload.Content.ReadFromJsonAsync<UnloadResponse>(deadline.Token);
                if (result?.Done != true || !string.IsNullOrWhiteSpace(result.Error))
                    throw new LlmProviderException("Ollama did not confirm the previous model was unloaded.");
                loadedModel = null;
            }

            if (library is not null)
                await library.GuardExecutionAsync(request.Model.Name, request.Model.ContextSize, deadline.Token,
                    request.RequireAutomaticAdmissibility);
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
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonSerializerOptions.Web);
            if (request.DiagnosticContext is { } trace)
            {
                if (diagnostics is null)
                    throw new LlmDiagnosticException("Local diagnostic capture is unavailable; the request was not sent.");
                diagnostics.RecordRequest(trace, request.Model.Name, bytes);
            }
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            LlmGenerationScope.MarkRequestMayHaveBeenSent(reservationId);
            mayHaveBeenSent = true;
            using var response = await client.PostAsync(new Uri(new Uri(options.BaseUrl), "/api/chat"), content, deadline.Token);
            ChatResponse? completion;
            if (request.DiagnosticContext is { } diagnosticContext)
            {
                var responseBytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                diagnostics!.RecordResponse(diagnosticContext, (int)response.StatusCode, responseBytes);
                if (!response.IsSuccessStatusCode)
                    ThrowResourceError(System.Text.Encoding.UTF8.GetString(responseBytes));
                response.EnsureSuccessStatusCode();
                completion = JsonSerializer.Deserialize<ChatResponse>(responseBytes, JsonSerializerOptions.Web);
            }
            else
            {
                if (!response.IsSuccessStatusCode)
                    ThrowResourceError(await response.Content.ReadAsStringAsync(deadline.Token));
                response.EnsureSuccessStatusCode();
                completion = await response.Content.ReadFromJsonAsync<ChatResponse>(deadline.Token);
            }
            if (completion?.Error is { } error)
                ThrowResourceError(error);
            if (completion?.DoneReason == "length")
                throw new LlmOutputLimitException(request.Model.Name, request.Model.MaxOutputTokens,
                    completion.EvalCount, completion.Message?.Content?.Length ?? 0,
                    completion.Message?.Thinking?.Length ?? 0);
            if (completion?.Done != true || !string.IsNullOrWhiteSpace(completion.Error) ||
                string.IsNullOrWhiteSpace(completion.Message?.Content))
                throw new LlmProviderException("Ollama returned an incomplete or empty response.");
            return completion.Message.Content;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (!mayHaveBeenSent) LlmGenerationScope.MarkRequestDefinitelyNotSent(reservationId);
            throw new LlmTimeoutException("The Ollama request exceeded its configured timeout.", exception);
        }
        catch (HttpRequestException exception)
        {
            if (!mayHaveBeenSent) LlmGenerationScope.MarkRequestDefinitelyNotSent(reservationId);
            throw new LlmProviderException("Ollama is unavailable or rejected the request.", exception);
        }
        catch (JsonException exception)
        {
            if (!mayHaveBeenSent) LlmGenerationScope.MarkRequestDefinitelyNotSent(reservationId);
            throw new LlmProviderException("Ollama returned an invalid response.", exception);
        }
        catch (IOException exception)
        {
            if (!mayHaveBeenSent) LlmGenerationScope.MarkRequestDefinitelyNotSent(reservationId);
            throw new LlmProviderException("The Ollama response was interrupted.", exception);
        }
        catch
        {
            if (!mayHaveBeenSent) LlmGenerationScope.MarkRequestDefinitelyNotSent(reservationId);
            throw;
        }
        finally
        {
            if (acquired)
            {
                Volatile.Write(ref activeModel, null);
                requests.Release();
            }
            if (operationAcquired) modelOperations!.Gate.Release();
        }
    }

    public void Dispose() => requests.Dispose();

    private static void ThrowResourceError(string error)
    {
        if (error.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("requires more", StringComparison.OrdinalIgnoreCase) && error.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("unable to allocate", StringComparison.OrdinalIgnoreCase))
            throw new LlmMemoryException("Ollama returned a memory allocation failure; no larger model will be tried automatically.");
        if (error.Contains("context length", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("context window", StringComparison.OrdinalIgnoreCase))
            throw new LlmContextException("Ollama returned a context capacity failure; reduce the context before retrying.");
    }

    private sealed record UnloadResponse(bool Done, string? Error);
    private sealed record ChatResponse(
        bool Done, ChatMessage? Message, string? Error,
        [property: JsonPropertyName("done_reason")] string? DoneReason,
        [property: JsonPropertyName("eval_count")] int? EvalCount);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("thinking")] string? Thinking);
}
