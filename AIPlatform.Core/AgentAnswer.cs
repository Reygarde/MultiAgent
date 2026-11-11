using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIPlatform.Core;

public static class AgentAnswer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { response = new { type = "string", minLength = 1 } },
        required = new[] { "response" },
        additionalProperties = false
    });

    public static async Task<string> GenerateAsync(
        ILLMProvider provider, ModelDefinition model, AgentDefinition agent,
        string task, CancellationToken cancellationToken)
    {
        var completion = await provider.CompleteAsync(new(model,
        [
            new("system", agent.SystemPrompt +
                "\nReturn a JSON object with exactly one property, response. " +
                "Its string value must contain only the final answer for the user, " +
                "including any requested code. Do not include internal analysis or reasoning."),
            new("user", task)
        ], Schema), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Answer? answer;
        try
        {
            answer = JsonSerializer.Deserialize<Answer>(completion, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new LlmProviderException("The execution agent returned invalid answer JSON.", exception);
        }
        if (string.IsNullOrWhiteSpace(answer?.Response))
            throw new LlmProviderException("The execution agent returned an empty answer.");
        return answer.Response;
    }

    private sealed record Answer
    {
        public required string Response { get; init; }
    }
}
