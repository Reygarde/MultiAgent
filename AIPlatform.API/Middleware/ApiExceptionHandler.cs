using AIPlatform.Core;
using AIPlatform.Core.Studio;
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
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Invalid HTTP request"),
            StudioValidationException => (StatusCodes.Status400BadRequest, "Invalid Studio request"),
            StudioCapacityException => (StatusCodes.Status429TooManyRequests, "Studio mission limit reached"),
            _ => (0, "")
        };
        if (status == 0)
            return false;

        if (status >= 500)
            logger.LogError(exception, "Mission failed: {Title}", title);
        else
            logger.LogWarning("Request rejected with HTTP {Status}: {Title}", status, title);
        await TypedResults.Problem(
            statusCode: status,
            title: title,
            detail: exception is StudioValidationException or StudioCapacityException
                ? exception.Message : status < 500
                ? "Provide a valid JSON request body with a task string."
                : "The mission could not be completed. Check the local provider and server logs.",
            instance: context.Request.Path).ExecuteAsync(context);
        return true;
    }
}
