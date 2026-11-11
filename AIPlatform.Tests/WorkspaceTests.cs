using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class WorkspaceTests
{
    [Theory]
    [InlineData("../secret.cs")]
    [InlineData("src/App/../../secret.cs")]
    [InlineData("C:/secret.cs")]
    [InlineData("/tmp/secret.cs")]
    [InlineData("src\\App\\Program.cs")]
    [InlineData("src/App/Program.cs:secret")]
    [InlineData("src/App/CON.cs")]
    [InlineData("src/App/NUL.json")]
    [InlineData("src/App/COM1.cs")]
    [InlineData("src/App/../Program.cs")]
    [InlineData("src/App/bin/Program.cs")]
    [InlineData("src/App/obj/Project.cs")]
    [InlineData("src/App/file..")]
    [InlineData("src/App/space .cs")]
    [InlineData("src/App/file.ps1")]
    [InlineData("src/App/Other.csproj")]
    [InlineData("README.md/Other.cs")]
    [InlineData("src/App/.hidden.cs")]
    public void RejectsUnsafePaths(string path) =>
        Assert.Throws<StudioValidationException>(() => SafePaths.ValidateRelative(path));

    [Fact]
    public void RefusesStorageUnderAHostProjectSourceTree()
    {
        using var fixture = new StudioFixture();
        var project = Path.Combine(fixture.Options.DataDirectory, "Host");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "Host.csproj"), "<Project />");
        Assert.Throws<StudioValidationException>(() => new WorkspaceStore(
            fixture.Options with { DataDirectory = Path.Combine(project, "data") }));
        Assert.Throws<StudioValidationException>(() => new MissionStore(
            fixture.Options with { DataDirectory = Path.Combine(project, "data") }, fixture.Platform, fixture.Registry));
    }

    [Fact]
    public async Task WritesAndReadsOnlyTheMissionWorkspace()
    {
        using var fixture = new StudioFixture();
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        await fixture.Workspaces.WriteAsync(id, StudioFixture.Manifest().Files, CancellationToken.None);
        Assert.Equal(5, fixture.Workspaces.List(id).Count);
        Assert.Contains("WebApplication", (await fixture.Workspaces.ReadAsync(id, "src/App/Program.cs", CancellationToken.None)).Content);
        Assert.Contains("Solution", (await fixture.Workspaces.ReadAsync(id, "Generated.slnx", CancellationToken.None)).Content);
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Workspaces.ReadAsync(id, "../../appsettings.json", CancellationToken.None));
        Assert.Empty(fixture.Workspaces.List(Guid.NewGuid()));
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.Create(id));
    }

    [Fact]
    public async Task ValidatesTheEntireManifestBeforeWritingAnyFile()
    {
        using var fixture = new StudioFixture();
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        var files = StudioFixture.Manifest().Files.Append(new GeneratedFile("../bad.cs", "Bad")).ToArray();
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Workspaces.WriteAsync(id, files, CancellationToken.None));
        Assert.Empty(fixture.Workspaces.List(id));
    }

    [Fact]
    public void RejectsDuplicateFilesInvalidXmlAndWrongFramework()
    {
        using var fixture = new StudioFixture();
        var files = StudioFixture.Manifest().Files;
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles([.. files, files[0]]));
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(
            files.Select(file => file.Path.EndsWith(".csproj") ? file with { Content = "invalid XML" } : file).ToArray()));
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(
            files.Select(file => file with { Content = file.Content.Replace("net10.0", "net9.0") }).ToArray()));
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(
            files.Select(file => file.Path.EndsWith("Program.cs") ? file with { Content = new string('a', fixture.Options.MaxFileBytes + 1) } : file).ToArray()));
    }

    [Fact]
    public void RejectsBuildCommandsUncachedDependenciesAndMissingTestPackages()
    {
        using var fixture = new StudioFixture();
        var files = StudioFixture.Manifest().Files;
        foreach (var modification in new[]
                 {
                     "<Target Name=\"BeforeBuild\"><Exec Command=\"whoami\" /></Target>",
                     "<Import Project=\"outside.targets\" />",
                     "<ItemGroup><PackageReference Include=\"Uncached.Package\" Version=\"1.0\" /></ItemGroup>",
                     "<PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>"
                 })
        {
            var changed = files.Select(file => file.Path == "src/App/App.csproj"
                ? file with { Content = file.Content.Replace("</Project>", modification + "</Project>") } : file).ToArray();
            Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(changed));
        }
        var missingPackages = files.Select(file => file.Path == "tests/App.Tests/App.Tests.csproj"
            ? file with { Content = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""" }
            : file).ToArray();
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(missingPackages));
    }

    [Fact]
    public async Task RefusesDirectoryJunctionsForReadsAndWrites()
    {
        using var fixture = new StudioFixture();
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        var workspace = fixture.Workspaces.GetDirectory(id);
        var outside = Path.Combine(fixture.Options.DataDirectory, "outside");
        Directory.CreateDirectory(outside);
        var junction = Path.Combine(workspace, "src");
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/c mklink /J \"{junction}\" \"{outside}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else
            Directory.CreateSymbolicLink(junction, outside);
        try
        {
            Assert.Throws<StudioValidationException>(() => fixture.Workspaces.List(id));
            await Assert.ThrowsAsync<StudioValidationException>(() =>
                fixture.Workspaces.ReadAsync(id, "src/App/Program.cs", CancellationToken.None));
            await Assert.ThrowsAsync<StudioValidationException>(() =>
                fixture.Workspaces.WriteAsync(id, StudioFixture.Manifest().Files, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(outside, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }
}
