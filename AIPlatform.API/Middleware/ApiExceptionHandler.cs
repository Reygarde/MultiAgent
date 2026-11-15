using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.AspNetCore.Diagnostics;

namespace AIPlatform.API.Middleware;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            LlmTimeoutException => (StatusCodes.Status504GatewayTimeout, "LLM provider timeout"),
            LlmProviderException => (StatusCodes.Status503ServiceUnavailable, "LLM provider unavailable"),
            CustomAgentModelUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Custom agent model unavailable"),
            PreviewUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Preview unavailable"),
            TwitchIntegrationException twitch => (twitch.StatusCode, "Twitch integration error"),
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Invalid HTTP request"),
            StudioValidationException => (StatusCodes.Status400BadRequest, "Invalid Studio request"),
            StudioCapacityException => (StatusCodes.Status429TooManyRequests, "Studio mission limit reached"),
            _ => (0, "")
        };
        if (status == 0)
            return false;

        if (exception is TwitchIntegrationException twitchException)
            logger.LogWarning("Twitch integration request rejected: {Code}", twitchException.Code);
        else if (status >= 500)
            logger.LogError(exception, "AIPlatform request failed: {Title}", title);
        else
            logger.LogWarning("Request rejected with HTTP {Status}: {Title}", status, title);
        await TypedResults.Problem(
            statusCode: status,
            title: title,
            detail: exception switch
            {
                StudioValidationException or StudioCapacityException or TwitchIntegrationException or
                    CustomAgentModelUnavailableException => exception.Message,
                LlmTimeoutException => "Ollama took too long to respond. Try again.",
                LlmProviderException => "Ollama is unavailable or returned an error. Check that it is running and try again.",
                _ when status < 500 => "Provide a valid request body.",
                PreviewUnavailableException => "The preview runtime or its cleanup is unavailable. Check the backend logs.",
                _ => "The request could not be completed. Check the local provider and server logs."
            },
            instance: context.Request.Path).ExecuteAsync(context);
        return true;
    }
}
