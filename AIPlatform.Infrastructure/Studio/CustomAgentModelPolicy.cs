using System.Text.RegularExpressions;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static class CustomAgentModelPolicy
{
    public static readonly string[] DeveloperDefaults = ["qwen2.5-coder:7b", "qwen3.5:9b", "qwen2.5-coder:14b"];

    public static bool ValidFallbacks(IReadOnlyList<string>? models) => models is null ||
        models.Count <= 8 && models.Distinct(StringComparer.Ordinal).Count() == models.Count &&
        models.All(model => model is { Length: > 0 and <= 200 } &&
            Regex.IsMatch(model, @"^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant));

    public static (string Model, string Reason, string[] Candidates) Select(
        CustomAgentProfile agent, CustomAgentTeamMember member, bool enabled,
        IReadOnlyList<string> installed, IReadOnlyList<CustomAgentModelAttempt> history)
    {
        var fallback = member.FallbackModels ?? agent.FallbackModels ??
            (member.Role == "Developer" ? DeveloperDefaults : []);
        var allowed = new[] { agent.ModelName }.Concat(fallback).Distinct(StringComparer.Ordinal).ToArray();
        var candidates = allowed.Where(installed.Contains).ToArray();
        var absent = allowed.Where(model => !installed.Contains(model)).ToArray();
        var availability = absent.Length == 0 ? "" : $" Unavailable/incompatible models skipped: {string.Join(", ", absent)}.";
        if (!enabled || !installed.Contains(agent.ModelName))
            return (agent.ModelName, (!enabled ? "Configured model; escalation disabled." :
                "Configured model unavailable; fix installation before generation.") + availability, candidates);
        var next = Next(agent.Id, candidates, history);
        return (next.Model, next.Reason + availability, candidates);
    }

    public static (string Model, string Reason) Next(
        Guid agentId, IReadOnlyList<string> candidates, IReadOnlyList<CustomAgentModelAttempt> history)
    {
        if (candidates.Count == 0)
            throw new StudioValidationException("No authorized installed model is available.");
        var attempts = history.Where(attempt => attempt.AgentId == agentId &&
            (attempt.Candidates.Count == 0 || attempt.Candidates[0] == candidates[0])).ToArray();
        var last = attempts.LastOrDefault();
        var current = last is not null && candidates.Contains(last.ModelName) ? last.ModelName : candidates[0];
        var failures = attempts.Reverse().TakeWhile(attempt =>
            attempt.ModelName == current && attempt.FailureKind == "Generation").Count();
        if (failures < 2)
            return (current, last is null ? "Configured model is the first choice." :
                $"Continue {current}: {failures}/2 consecutive generation failures; infrastructure errors do not trigger escalation.");
        var index = candidates.ToList().IndexOf(current);
        return index + 1 < candidates.Count
            ? (candidates[index + 1], $"Two consecutive generation failures on {current}; select the next authorized installed model.")
            : (current, "Fallbacks exhausted; no additional attempts are granted.");
    }

    public static string Classify(Exception exception) => exception switch
    {
        LlmMemoryException => "Memory",
        LlmTimeoutException => "Timeout",
        LlmContextException => "Context",
        LlmOutputLimitException => "OutputLimit",
        OperationCanceledException => "Cancelled",
        StudioValidationException when exception.Message.Contains("input tokens", StringComparison.Ordinal) ||
            exception.Message.Contains("context limit", StringComparison.Ordinal) => "Context",
        _ => "Infrastructure"
    };

    public static string? Classify(CustomAgentValidationReport validation)
    {
        if (validation.CompilationStatus == "TimedOut")
            return "Environment";
        var errors = validation.Diagnostics.Where(diagnostic => diagnostic.Severity == "Error" &&
            diagnostic.Code != "INITIAL_HTTP_FAILURE").ToArray();
        if (errors.Length == 0)
            return null;
        if (errors.Any(diagnostic => diagnostic.Code == "ROLLBACK_FAILED"))
            return "Infrastructure";
        if (errors.Any(diagnostic => diagnostic.Code == "LOCAL_BUILD_OR_TEST_FAILED"))
            return errors.Any(diagnostic => Regex.IsMatch(diagnostic.Message,
                @"\berror (TS|CS)\d+|AssertionError|TestingLibraryElementError|\bFAIL\s|\[FAIL\]|(?m:^\s*Failed\s+\S+\s+\[)|[Ff]ailed:\s*[1-9]|Failed!",
                RegexOptions.CultureInvariant)) ? "Generation" : "Environment";
        return errors.Any(diagnostic => diagnostic.Code is
            "NO_FILE_CHANGES" or "NO_EFFECTIVE_CHANGES" or "REVIEWER_FINDING" or "BROWSER_FUNCTIONAL_TEST_FAILED" or
            "REACT_PATH_NOT_ALLOWED" or "PROJECT_MANIFEST_INVALID" or "LOCAL_IMPORT_OUTSIDE_WORKSPACE" or
            "LOCAL_IMPORT_MISSING" or "DOTNET_PROJECT_REFERENCE_MISSING")
            ? "Generation" : "Environment";
    }
}
