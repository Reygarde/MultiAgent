using AIPlatform.API.Services;
using AIPlatform.Core.Studio;

namespace AIPlatform.Tests;

public sealed class StudioDataDirectoryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("AIPlatform-storage-").FullName;

    [Fact]
    public void DevelopmentUsesAbsoluteOrContentRootRelativeOverride()
    {
        var dev = Path.Combine(root, "AIPlatformStudio.Dev", "StudioData");
        Assert.Equal(dev, StudioDataDirectory.Resolve(false, dev, root, root));
        Assert.Equal(dev, StudioDataDirectory.Resolve(false, Path.Combine("AIPlatformStudio.Dev", "StudioData"), root, root));
        Assert.False(Directory.Exists(dev));
    }

    [Fact]
    public void DesktopIgnoresDevelopmentOverride()
    {
        var installed = Path.Combine(root, "AIPlatformStudio", "StudioData");
        Assert.Equal(installed, StudioDataDirectory.Resolve(true, "AIPlatformStudio.Dev", root, root));
        Assert.False(Directory.Exists(installed));
    }

    [Theory]
    [InlineData("AIPlatformStudio", "StudioData", "")]
    [InlineData("aiplatformstudio", "studiodata", "")]
    [InlineData("AIPlatformStudio", "other", "..")]
    [InlineData("AIPlatformStudio", "StudioData", "subfolder")]
    [InlineData(".", "", "")]
    public void DevelopmentRejectsInstallingTreeOrItsAncestors(string first, string second, string third)
    {
        var path = Path.Combine(root, first, second, third);
        var installed = Path.Combine(root, "AIPlatformStudio");
        Assert.Contains("must not overlap", Assert.Throws<StudioValidationException>(() =>
            StudioDataDirectory.Resolve(false, path, root, root)).Message);
        Assert.False(Directory.Exists(installed));
    }

    [Fact]
    public void DevelopmentRejectsAliasThroughDirectoryLink()
    {
        var installed = Directory.CreateDirectory(Path.Combine(root, "AIPlatformStudio")).FullName;
        var alias = Path.Combine(root, "alias");
        if (OperatingSystem.IsWindows())
        {
            // Directory symlinks require privileges on Windows; use an ordinary, unprivileged junction.
            using var process = System.Diagnostics.Process.Start("cmd.exe", $"/d /c mklink /J \"{alias}\" \"{installed}\"")!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(alias, installed);
        }
        try
        {
            Assert.Throws<StudioValidationException>(() =>
                StudioDataDirectory.Resolve(false, Path.Combine(alias, "new-directory"), root, root));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
