using System.Diagnostics;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class StudioBrowserHelperTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolvesOnlyTheHostOwnedHelper(bool sourceHost)
    {
        var root = Path.Combine(Path.GetTempPath(), "AIPlatform-helper-" + Guid.NewGuid().ToString("N"));
        try
        {
            var host = Path.Combine(root, sourceHost ? "AIPlatform.API" : "backend");
            Directory.CreateDirectory(host);
            if (sourceHost)
                File.WriteAllText(Path.Combine(host, "AIPlatform.API.csproj"), "<Project />");
            var expected = sourceHost
                ? Path.Combine(root, "AIPlatform.Web", "scripts", "studio-browser-tests.mjs")
                : Path.Combine(root, "studio-browser", "studio-browser-tests.mjs");
            Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
            File.WriteAllText(expected, "// trusted");
            File.WriteAllText(Path.Combine(host, "studio-browser-tests.mjs"), "// not the helper");

            Assert.Equal(expected, StudioBrowserTestOptions.ResolveHelperFile(host));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefusesLinkedHelperDirectoriesWithoutFallback(bool sourceHost)
    {
        var root = Path.Combine(Path.GetTempPath(), "AIPlatform-helper-" + Guid.NewGuid().ToString("N"));
        var junction = "";
        try
        {
            var host = Path.Combine(root, sourceHost ? "AIPlatform.API" : "backend");
            var target = Path.Combine(root, "other");
            Directory.CreateDirectory(host);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "studio-browser-tests.mjs"), "// not trusted");
            if (sourceHost)
                File.WriteAllText(Path.Combine(host, "AIPlatform.API.csproj"), "<Project />");
            junction = sourceHost ? Path.Combine(root, "AIPlatform.Web", "scripts") : Path.Combine(root, "studio-browser");
            Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                })!;
                await process.WaitForExitAsync();
                Assert.Equal(0, process.ExitCode);
            }
            else
                Directory.CreateSymbolicLink(junction, target);

            Assert.Throws<StudioValidationException>(() => StudioBrowserTestOptions.ResolveHelperFile(host));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(root, recursive: true);
        }
    }
}
