using System.Text.Json;
using System.Text.RegularExpressions;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed record StudioBrowserTestOptions(string NodeExecutable, string HelperFile)
{
    public static string ResolveHelperFile(string contentRoot)
    {
        var sourceHost = File.Exists(Path.Combine(contentRoot, "AIPlatform.API.csproj"));
        var helper = sourceHost
            ? Path.GetFullPath(Path.Combine(contentRoot, "..", "AIPlatform.Web", "scripts", "studio-browser-tests.mjs"))
            : Path.GetFullPath(Path.Combine(contentRoot, "..", "studio-browser", "studio-browser-tests.mjs"));
        SafePaths.RejectLinks(helper);
        return helper;
    }
}

public interface IStudioBrowserTestRunner
{
    bool Available { get; }
    Task<CustomAgentBrowserTestReport> RunAsync(
        Guid workspaceId, IReadOnlyList<CustomAgentBrowserScenario>? scenarios,
        bool executionAuthorized, CancellationToken cancellationToken);
    Task<CustomAgentBrowserTestReport> RunAuthorizedAsync(
        Guid workspaceId, Guid authorizationId, IReadOnlyList<CustomAgentBrowserScenario>? scenarios,
        CancellationToken cancellationToken);
    Task<byte[]?> ReadScreenshotAsync(
        Guid workspaceId, Guid testId, Guid scenarioId, CancellationToken cancellationToken);
}

public sealed class StudioBrowserTestRunner(
    IMissionService missions, ILocalCommandExecutor commands, StudioOptions options,
    StudioBrowserTestOptions browserOptions, bool desktopEnabled) : IStudioBrowserTestRunner
{
    private const int MaxScenarios = 3;
    private const int MaxSteps = 10;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex ResultLine = new(
        @"(?m)^AIPLATFORM_BROWSER_TEST_RESULT (?<json>\{[^\r\n]*\})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> Actions = new(
        ["fill", "select", "click", "expectVisible", "expectHidden", "reload"], StringComparer.Ordinal);
    private static readonly HashSet<string> LocatorTypes = new(
        ["role", "label", "placeholder", "text"], StringComparer.Ordinal);
    private static readonly HashSet<string> Roles = new(
        ["button", "checkbox", "heading", "link", "listitem", "option", "radio", "textbox"],
        StringComparer.Ordinal);

    public bool Available => desktopEnabled && commands.Available && OperatingSystem.IsWindows();

    public async Task<CustomAgentBrowserTestReport> RunAsync(
        Guid workspaceId, IReadOnlyList<CustomAgentBrowserScenario>? scenarios,
        bool executionAuthorized, CancellationToken cancellationToken)
    {
        if (!executionAuthorized)
            throw new StudioValidationException("Explicit local execution authorization is required for browser tests.");
        var authorizationId = commands.Authorize(workspaceId);
        try
        {
            return await RunAuthorizedAsync(workspaceId, authorizationId, scenarios, cancellationToken);
        }
        finally
        {
            commands.Revoke(authorizationId);
        }
    }

    public async Task<CustomAgentBrowserTestReport> RunAuthorizedAsync(
        Guid workspaceId, Guid authorizationId, IReadOnlyList<CustomAgentBrowserScenario>? scenarios,
        CancellationToken cancellationToken)
    {
        var reportId = Guid.NewGuid();
        if (!desktopEnabled || !commands.Available || !OperatingSystem.IsWindows())
            return Report(reportId, "BrowserUnavailable", "", "Browser tests are available only in Studio Desktop on Windows.", []);
        if (!Path.IsPathFullyQualified(browserOptions.NodeExecutable) ||
            !Path.IsPathFullyQualified(browserOptions.HelperFile) ||
            !File.Exists(browserOptions.NodeExecutable) || !File.Exists(browserOptions.HelperFile))
            return Report(reportId, "BrowserUnavailable", "",
                "The trusted Playwright runtime is unavailable. Reinstall AIPlatform Studio or its browser-test dependencies.", []);

        var mission = missions.Get(workspaceId);
        if (mission is not { HasWorkspace: true, Deleted: false, Archived: false } ||
            mission.TemplateId is not (AgentReactTemplate.Id or AgentDotNetTemplate.Id or AgentFullStackTemplate.Id))
            return Report(reportId, "Unsupported", "", "Browser tests require an active Studio React or .NET Web project.", []);

        IReadOnlyList<CustomAgentBrowserScenario> normalized;
        if (scenarios is { Count: > 0 })
            normalized = scenarios;
        else if (mission.TemplateId is AgentReactTemplate.Id or AgentFullStackTemplate.Id)
        {
            var value = "{{testValue}}";
            normalized =
            [
                new(
                    Guid.NewGuid().ToString("N"),
                    "Ajout, persistance et suppression d’une tâche",
                    "/",
                    [
                        new("fill", "label", "Nouvelle tâche", value, null, null),
                        new("click", "role", "Ajouter", null, "button", null),
                        new("expectVisible", "text", value, null, null, null),
                        new("reload", null, null, null, null, null),
                        new("expectVisible", "text", value, null, null, null),
                        new("click", "role", $"Supprimer {value}", null, "button", null),
                        new("expectHidden", "text", value, null, null, null)
                    ])
            ];
        }
        else
            normalized =
            [
                new(Guid.NewGuid().ToString("N"), "Chargement de l’endpoint de santé", "/health", [])
            ];
        var invalid = ValidateScenarios(normalized);
        if (invalid is not null)
            return Report(reportId, "InvalidScenario", "", invalid, []);

        var projectType = mission.TemplateId == AgentReactTemplate.Id ? CustomAgentProjectType.React
            : mission.TemplateId == AgentFullStackTemplate.Id ? CustomAgentProjectType.FullStack
            : CustomAgentProjectType.DotNet;
        var existing = commands.GetApplication(workspaceId, projectType);
        if (existing.Status == "Stopping")
            return Report(reportId, "ServerUnavailable", existing.Url ?? "",
                "The local application is already stopping; retry after it has stopped.", []);
        var startedForTest = existing.Status is "Stopped" or "Error";
        var result = Report(reportId, "Failed", "", "The browser test did not complete.", []);
        try
        {
            if (startedForTest)
                await commands.StartApplicationAsync(workspaceId, projectType, true, cancellationToken);

            var application = await WaitForApplicationAsync(workspaceId, projectType, cancellationToken);
            if (application.Status != "Running" || application.Url is null)
                result = Report(reportId, "ServerUnavailable", application.Url ?? "",
                    application.Error ?? "The Studio application did not become ready.", []);
            else if (!Uri.TryCreate(application.Url, UriKind.Absolute, out var address) ||
                !address.IsLoopback || address.Scheme != Uri.UriSchemeHttp ||
                address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
                result = Report(reportId, "ServerUnavailable", "",
                    "The local application did not provide a verified loopback HTTP URL.", []);
            else
            {
                var screenshotDirectory = GetScreenshotDirectory(workspaceId, reportId);
                Directory.CreateDirectory(screenshotDirectory);
                SafePaths.RejectLinks(screenshotDirectory);
                var payload = JsonSerializer.Serialize(new
                {
                    url = address.ToString(),
                    runId = reportId.ToString("N"),
                    scenarios = normalized,
                    screenshotDirectory
                }, JsonOptions);
                var execution = await commands.RunBrowserHelperAsync(
                    authorizationId, workspaceId, payload, cancellationToken);
                result = ParseResult(reportId, address.ToString(), execution);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StudioValidationException exception)
        {
            result = Report(reportId, "ServerUnavailable", "", exception.Message, []);
        }
        finally
        {
            if (startedForTest)
            {
                using var cleanup = new CancellationTokenSource(CleanupTimeout);
                try
                {
                    var stopped = await commands.StopApplicationAsync(workspaceId, cleanup.Token)
                        ?? commands.GetApplication(workspaceId, projectType);
                    if (stopped.Status != "Stopped")
                        result = result with
                        {
                            Status = result.Status == "Passed" ? "ServerUnavailable" : result.Status,
                            Error = "The test-started application did not confirm shutdown."
                        };
                }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                    result = result with
                    {
                        Status = result.Status == "Passed" ? "ServerUnavailable" : result.Status,
                        Error = "The test-started application did not confirm shutdown before the cleanup timeout."
                    };
                }
            }
        }

        return result;
    }

    public async Task<byte[]?> ReadScreenshotAsync(
        Guid workspaceId, Guid testId, Guid scenarioId, CancellationToken cancellationToken)
    {
        if (!desktopEnabled || !commands.Available ||
            missions.Get(workspaceId) is not { HasWorkspace: true, Deleted: false, Archived: false })
            return null;
        var path = Path.Combine(GetScreenshotDirectory(workspaceId, testId), scenarioId.ToString("N") + ".png");
        SafePaths.RejectLinks(path);
        if (!File.Exists(path))
            return null;
        var info = new FileInfo(path);
        if (info.Length is < 8 or > 4_194_304)
            throw new StudioValidationException("The saved browser screenshot has an invalid size.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new StudioValidationException("The saved browser screenshot is not a PNG image.");
        return bytes;
    }

    private async Task<LocalApplicationSnapshot> WaitForApplicationAsync(
        Guid workspaceId, CustomAgentProjectType projectType, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + StartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var application = commands.GetApplication(workspaceId, projectType);
            if (application.Status is "Running" or "Error" or "Stopped")
                return application;
            await Task.Delay(200, cancellationToken);
        }
        return commands.GetApplication(workspaceId, projectType) with
        {
            Status = "Error",
            Error = "The local application startup timed out before browser testing."
        };
    }

    private string GetScreenshotDirectory(Guid workspaceId, Guid testId)
    {
        var root = SafePaths.ValidateStorageDirectory(Path.Combine(
            options.DataDirectory, "browser-tests", workspaceId.ToString("N")));
        var directory = Path.Combine(root, testId.ToString("N"));
        SafePaths.RejectLinks(directory);
        return directory;
    }

    private static CustomAgentBrowserTestReport ParseResult(
        Guid reportId, string url, LocalCommandResult execution)
    {
        if (execution.TimedOut)
            return Report(reportId, "TimedOut", url, "The browser test exceeded its time limit.", []);
        var match = ResultLine.Matches(execution.Output).LastOrDefault();
        if (match is null)
            return Report(reportId, "BrowserUnavailable", url,
                "The trusted Playwright runner did not return a result. Verify that Edge and the packaged Playwright runtime are available.", []);
        try
        {
            using var document = JsonDocument.Parse(match.Groups["json"].Value,
                new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            var status = root.GetProperty("status").GetString() ?? "Failed";
            var error = root.TryGetProperty("error", out var errorElement) &&
                        errorElement.ValueKind == JsonValueKind.String
                ? SafeDiagnostic(errorElement.GetString()!, 1200) : null;
            var scenarios = root.GetProperty("scenarios").EnumerateArray().Select(item =>
            {
                var errors = item.GetProperty("javaScriptErrors").EnumerateArray()
                    .Select(value => SafeDiagnostic(value.GetString() ?? "", 400)).Take(5).ToArray();
                return new CustomAgentBrowserScenarioResult(
                    item.GetProperty("id").GetString() ?? "",
                    SafeDiagnostic(item.GetProperty("name").GetString() ?? "", 80),
                    item.GetProperty("status").GetString() ?? "Failed",
                    item.TryGetProperty("failedStep", out var failedStep) &&
                    failedStep.ValueKind == JsonValueKind.String ? SafeDiagnostic(failedStep.GetString()!, 100) : null,
                    item.TryGetProperty("error", out var scenarioError) &&
                    scenarioError.ValueKind == JsonValueKind.String ? SafeDiagnostic(scenarioError.GetString()!, 1200) : null,
                    errors,
                    item.TryGetProperty("screenshotId", out var screenshot) &&
                    screenshot.ValueKind == JsonValueKind.String &&
                    Guid.TryParseExact(screenshot.GetString(), "N", out _) ? screenshot.GetString() : null);
            }).Take(MaxScenarios).ToArray();
            if (status is not ("Passed" or "Failed" or "InvalidScenario" or "BrowserUnavailable"))
                status = "Failed";
            return new(reportId, status, url, DateTimeOffset.UtcNow,
                scenarios.Count(item => item.Status == "Passed"),
                scenarios.Count(item => item.Status != "Passed"), error, scenarios);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Report(reportId, "BrowserUnavailable", url,
                "The Playwright runner returned an invalid result.", []);
        }
    }

    private static string? ValidateScenarios(IReadOnlyList<CustomAgentBrowserScenario> scenarios)
    {
        if (scenarios.Count is < 1 or > MaxScenarios)
            return $"Provide between 1 and {MaxScenarios} browser scenarios.";
        var totalSteps = 0;
        foreach (var scenario in scenarios)
        {
            if (!Guid.TryParseExact(scenario.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(scenario.Name) || scenario.Name.Length > 80 ||
                scenario.Name.Any(char.IsControl) || scenario.Path is null ||
                scenario.Path.Length is < 1 or > 256 || !scenario.Path.StartsWith('/') ||
                scenario.Path.StartsWith("//", StringComparison.Ordinal) ||
                scenario.Path.Contains('\\') || scenario.Path.Contains('#') ||
                scenario.Path.Contains('?') || scenario.Path.Contains(':'))
                return "A browser scenario has an invalid identifier, name, or local path.";
            if (scenario.Steps.Count > MaxSteps)
                return $"Each browser scenario is limited to {MaxSteps} actions.";
            totalSteps += scenario.Steps.Count;
            foreach (var step in scenario.Steps)
            {
                if (!Actions.Contains(step.Action))
                    return "A browser scenario contains an unsupported action.";
                if (step.Action == "reload")
                    continue;
                if (step.LocatorType is null || !LocatorTypes.Contains(step.LocatorType) ||
                    string.IsNullOrWhiteSpace(step.Locator) || step.Locator.Length > 120 ||
                    step.Locator.Any(char.IsControl) || step.Index is < 0 or > 4)
                    return "A browser scenario contains an invalid accessible locator.";
                if (step.LocatorType == "role" &&
                    (step.Role is null || !Roles.Contains(step.Role)))
                    return "A browser scenario contains an unsupported accessible role.";
                if (step.Action is "fill" or "select" &&
                    (string.IsNullOrWhiteSpace(step.Value) || step.Value.Length > 200 ||
                     step.Value.Any(char.IsControl)))
                    return "A fill or select action needs a short, plain-text value.";
            }
        }
        return totalSteps > MaxScenarios * MaxSteps
            ? "The browser test plan exceeds the total action limit."
            : null;
    }

    private static CustomAgentBrowserTestReport Report(
        Guid id, string status, string url, string error,
        IReadOnlyList<CustomAgentBrowserScenarioResult> scenarios) =>
        new(id, status, url, DateTimeOffset.UtcNow, 0, scenarios.Count, SafeDiagnostic(error, 1200), scenarios);

    private static string SafeDiagnostic(string value, int limit)
    {
        var sanitized = Regex.Replace(value, @"[\u0000-\u001f\u007f]", " ");
        sanitized = Regex.Replace(sanitized, @"Bearer\s+\S+", "Bearer [redacted]", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"https?://\S+", "[URL]", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"[A-Za-z]:\\\S+", "[local path]");
        sanitized = sanitized.Trim();
        return sanitized.Length <= limit ? sanitized : sanitized[..(limit - 3)] + "...";
    }
}
