using System.Text.Json;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static class ReactBuildProfile
{
    // Vite writes .vite-temp beside packages; only the individual packages may link to the read-only image.
    public static string Command(string stage) => "mkdir /work/node_modules && " +
        "ln -s /opt/aiplatform/node_modules/* /opt/aiplatform/node_modules/.bin /work/node_modules/ && " + (stage switch
    {
        "TypeScript" => "/opt/aiplatform/node_modules/.bin/tsc --noEmit --project /work/tsconfig.json",
        "Build" => "/opt/aiplatform/node_modules/.bin/vite build",
        "Tests" => "/opt/aiplatform/node_modules/.bin/vitest run --reporter=json --outputFile=/work/vitest.json " +
            "> /work/test.log 2>&1; result=$?; head -c 24000 /work/test.log; " +
            "printf '\\nAIPLATFORM_VITEST_BEGIN\\n'; " +
            "if [ -f /work/vitest.json ]; then head -c 65536 /work/vitest.json; fi; " +
            "printf '\\nAIPLATFORM_VITEST_END\\n'; exit \"$result\"",
        _ => throw new StudioValidationException("Unauthorized React validation stage.")
    });

    public static (int Total, int Failed)? TestCounts(string output)
    {
        const string begin = "\nAIPLATFORM_VITEST_BEGIN\n";
        const string end = "\nAIPLATFORM_VITEST_END\n";
        var start = output.LastIndexOf(begin, StringComparison.Ordinal);
        var finish = output.LastIndexOf(end, StringComparison.Ordinal);
        if (start < 0 || finish <= start || finish - start > 65536 + begin.Length) return null;
        try
        {
            using var document = JsonDocument.Parse(output[(start + begin.Length)..finish]);
            var root = document.RootElement;
            if (root.GetProperty("success").ValueKind != JsonValueKind.True ||
                !root.GetProperty("numTotalTests").TryGetInt32(out var total) || total <= 0 ||
                !root.GetProperty("numPassedTests").TryGetInt32(out var passed) || passed != total ||
                root.GetProperty("numFailedTests").GetInt32() != 0 ||
                root.GetProperty("numPendingTests").GetInt32() != 0 ||
                root.GetProperty("numFailedTestSuites").GetInt32() != 0)
                return null;
            var suites = root.GetProperty("testResults").EnumerateArray().ToArray();
            if (suites.Length == 0 || suites.Any(suite => suite.GetProperty("status").GetString() != "passed"))
                return null;
            var assertions = suites.SelectMany(suite => suite.GetProperty("assertionResults").EnumerateArray()).ToArray();
            if (assertions.Length != total || assertions.Any(item => item.GetProperty("status").GetString() != "passed"))
                return null;
            return (total, 0);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException or OverflowException)
        {
            // Invalid or incomplete reports must never validate generated tests.
            return null;
        }
    }
}
