using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static partial class DiagnosticParser
{
    public static DiagnosticReport Parse(VerificationResult result, string? templateId = null)
    {
        var items = new List<FailureDiagnostic>();
        foreach (Match match in CompilerPattern().Matches(result.Log))
        {
            var file = Relative(match.Groups["file"].Value);
            var project = Relative(match.Groups["project"].Value);
            var frontend = templateId == ReactTemplate.Id ||
                templateId == FullStackTemplate.Id && result.Stage is ("TypeScript" or "FrontendBuild");
            var diagnostic = new FailureDiagnostic(frontend ? "TypeScript" : "Compiler",
                frontend ? "frontend/tsconfig.json" : project, file,
                Number(match.Groups["line"].Value), Number(match.Groups["column"].Value),
                match.Groups["code"].Value, match.Groups["message"].Value.Trim());
            if (!items.Contains(diagnostic)) items.Add(diagnostic);
            if (items.Count == 20) break;
        }
        if (result.Stage is "Tests" or "BackendTests")
            AddTestFailures(result.Log, items, templateId == FullStackTemplate.Id
                ? "backend/Api.Tests/Api.Tests.csproj" : "tests/App.Tests/App.Tests.csproj");
        if (templateId == ReactTemplate.Id && result.Stage == "Tests" ||
            templateId == FullStackTemplate.Id && result.Stage is ("FrontendTests" or "Integration"))
            AddVitestFailures(result.Log, items);
        var log = result.Log;
        var marker = log.IndexOf("AIPLATFORM_TRX_BEGIN", StringComparison.Ordinal);
        if (marker >= 0) log = log[..marker];
        var pertinent = string.Join("\n", log.Split('\n')
            .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("Expected", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("Actual", StringComparison.OrdinalIgnoreCase))
            .Take(40));
        if (pertinent.Length == 0) pertinent = log;
        var command = templateId == ReactTemplate.Id
            ? result.Stage switch { "TypeScript" => "tsc --noEmit (offline)", "Build" => "vite build (offline)", _ => "vitest run (offline)" }
            : templateId == FullStackTemplate.Id ? FullStackBuildProfile.Command(result.Stage)
            : result.Stage == "Build" ? "dotnet build Generated.slnx (offline)" :
                "dotnet test tests/App.Tests/App.Tests.csproj (offline)";
        return new(result.Stage, command, result.ExitCode, items, pertinent[..Math.Min(pertinent.Length, 4000)]);
    }

    private static void AddVitestFailures(string log, List<FailureDiagnostic> items)
    {
        const string begin = "\nAIPLATFORM_VITEST_BEGIN\n";
        const string end = "\nAIPLATFORM_VITEST_END\n";
        var start = log.LastIndexOf(begin, StringComparison.Ordinal);
        var finish = log.LastIndexOf(end, StringComparison.Ordinal);
        if (start < 0 || finish <= start || finish - start > 65536 + begin.Length) return;
        try
        {
            using var document = JsonDocument.Parse(log[(start + begin.Length)..finish]);
            foreach (var suite in document.RootElement.GetProperty("testResults").EnumerateArray())
                foreach (var assertion in suite.GetProperty("assertionResults").EnumerateArray())
                {
                    if (assertion.GetProperty("status").GetString() != "failed") continue;
                    var message = assertion.GetProperty("fullName").GetString() + ": " +
                        string.Join("\n", assertion.GetProperty("failureMessages").EnumerateArray().Select(value => value.GetString()));
                    items.Add(new("Vitest", "package.json", Relative(suite.GetProperty("name").GetString() ?? ""),
                        null, null, null, message[..Math.Min(message.Length, 1000)]));
                    if (items.Count >= 20) return;
                }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // The failed verification retains the original incomplete report and logs.
        }
    }

    private static void AddTestFailures(string log, List<FailureDiagnostic> items, string project)
    {
        log = log.Replace("\r\n", "\n", StringComparison.Ordinal);
        const string start = "AIPLATFORM_TRX_BEGIN\n";
        const string end = "\nAIPLATFORM_TRX_END";
        var begin = log.LastIndexOf(start, StringComparison.Ordinal);
        var finish = log.LastIndexOf(end, StringComparison.Ordinal);
        if (begin < 0 || finish <= begin) return;
        try
        {
            using var reader = XmlReader.Create(new StringReader(log[(begin + start.Length)..finish].TrimStart('\uFEFF')),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
            var document = XDocument.Load(reader);
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            foreach (var test in document.Descendants(ns + "UnitTestResult").Where(item => item.Attribute("outcome")?.Value == "Failed").Take(10))
            {
                var name = test.Attribute("testName")?.Value ?? "Unknown test";
                var message = test.Descendants(ns + "Message").FirstOrDefault()?.Value ?? "Test failed.";
                var trace = test.Descendants(ns + "StackTrace").FirstOrDefault()?.Value ?? "";
                var location = StackPattern().Match(trace);
                items.Add(new("Test", project,
                    location.Success ? Relative(location.Groups["file"].Value) : null,
                    location.Success ? Number(location.Groups["line"].Value) : null, null, null,
                    $"{name}: {message}"[..Math.Min($"{name}: {message}".Length, 1000)]));
            }
        }
        catch (XmlException)
        {
            // The verification result already records an invalid or incomplete TRX.
        }
    }

    private static string? Relative(string path)
    {
        var normalized = path.Replace('\\', '/');
        foreach (var prefix in new[] { "backend/Api/", "backend/Api.Tests/", "frontend/", "src/App/", "tests/App.Tests/", "src/" })
        {
            var index = normalized.IndexOf(prefix, StringComparison.Ordinal);
            if (index >= 0)
            {
                var candidate = normalized[index..].Trim();
                try { return SafePaths.ValidateRelative(candidate); }
                catch (StudioValidationException) { return null; }
            }
        }
        return null;
    }
    private static int? Number(string value) => int.TryParse(value, out var number) ? number : null;

    [GeneratedRegex(@"(?m)^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s*(?:fatal\s+)?error\s+(?<code>[A-Za-z]+\d+):\s*(?<message>.*?)(?:\s+\[(?<project>[^\]]+)\])?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CompilerPattern();
    [GeneratedRegex(@"(?:in|dans)\s+(?<file>.+?):(?:line|ligne)\s+(?<line>\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StackPattern();
}
