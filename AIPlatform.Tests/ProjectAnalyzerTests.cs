using System.Text.Json;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class ProjectAnalyzerTests
{
    [Fact]
    public void IdentifiesDotNetAndReactFromStaticApprovedInventory()
    {
        AnalysisFile[] files =
        [
            new("Example.slnx", "<Solution />"),
            new("src/Api/Api.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="Example.Library" Version="1.2.3" /></ItemGroup>
                </Project>
                """),
            new("src/Api/Program.cs", "var app = WebApplication.CreateBuilder(args).Build();"),
            new("tests/Api.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject>
                <TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
                """),
            new("web/package.json", """
                {"dependencies":{"react":"19.3.1"},"devDependencies":{"vite":"6.4.6"},
                 "scripts":{"build":"dangerous-command-that-must-not-be-executed"}}
                """),
            new("web/src/main.tsx", "export const App = () => null;"),
            new("Dockerfile", "FROM example")
        ];
        var result = new ProjectAnalyzer(new()).Analyze(files, true, CancellationToken.None);
        Assert.True(result.GitRepositoryDetected);
        Assert.Contains("C#", result.Languages);
        Assert.Contains("TypeScript", result.Languages);
        Assert.Contains("ASP.NET Core", result.Frameworks);
        Assert.Contains("net10.0", result.Frameworks);
        Assert.Contains("React", result.Frameworks);
        Assert.Contains("Vite", result.Frameworks);
        Assert.Contains("Example.slnx", result.Solutions);
        Assert.Contains("tests/Api.Tests.csproj", result.TestProjects);
        Assert.Contains("src/Api/Program.cs", result.EntryPoints);
        Assert.Contains("web/src/main.tsx", result.EntryPoints);
        Assert.Contains("web/src", result.Directories);
        Assert.Contains(result.Dependencies, item => item.Ecosystem == "NuGet" &&
            item.Name == "Example.Library" && item.Version == "1.2.3");
        Assert.Contains(result.Dependencies, item => item.Ecosystem == "npm" && item.Name == "react");
        Assert.Contains(result.Scripts, item => item.Name == "build");
        Assert.Contains("Dockerfile", result.DockerFiles);
        Assert.DoesNotContain("dangerous-command", JsonSerializer.Serialize(result));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ReportsMalformedManifestsWithoutLeakingTheirContentOrFollowingXmlEntities()
    {
        var result = new ProjectAnalyzer(new()).Analyze(
        [
            new("App.csproj", """<!DOCTYPE Project [<!ENTITY x SYSTEM "file:///nonexistent">]><Project>&x;</Project>"""),
            new("package.json", """{"dependencies":{"example":"https://example.invalid/private"}}"""),
            new("bad/package.json", """{"scripts":""")
        ], false, CancellationToken.None);
        Assert.Equal(2, result.Issues.Count);
        Assert.Null(Assert.Single(result.Dependencies).Version);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("nonexistent", json);
        Assert.DoesNotContain("example.invalid", json);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../outside.cs")]
    [InlineData("C:/outside.cs")]
    [InlineData("src\\outside.cs")]
    [InlineData("src/NUL.cs")]
    [InlineData(".git/config")]
    [InlineData("src/obj/Generated.cs")]
    [InlineData("web/node_modules/example/index.js")]
    [InlineData(".env")]
    [InlineData(".npmrc")]
    [InlineData(".ssh/config")]
    [InlineData("src/bad?.cs")]
    [InlineData("src/appsettings.json")]
    [InlineData("src/credentials.json")]
    public void RejectsUnsafeOrExcludedPaths(string path) =>
        Assert.Throws<StudioValidationException>(() =>
            new ProjectAnalyzer(new()).Analyze([new(path, "content")], false, CancellationToken.None));

    [Fact]
    public void RejectsDuplicateBinaryAndOversizedInventories()
    {
        var analyzer = new ProjectAnalyzer(new() { MaxImportFiles = 2, MaxImportFileBytes = 1024, MaxImportBytes = 2048 });
        Assert.Throws<StudioValidationException>(() => analyzer.Analyze(
            [new("App.cs", "a"), new("app.cs", "b")], false, CancellationToken.None));
        Assert.Throws<StudioValidationException>(() => analyzer.Analyze(
            [new("App.cs", "a\0b")], false, CancellationToken.None));
        Assert.Throws<StudioValidationException>(() => analyzer.Analyze(
            [new("App.cs", new string('a', 1025))], false, CancellationToken.None));
        Assert.Throws<StudioValidationException>(() => analyzer.Analyze(
            [new("a.cs", "a"), new("b.cs", "b"), new("c.cs", "c")], false, CancellationToken.None));
    }

    [Fact]
    public void HonorsCancellationBeforeAnalyzingAFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new ProjectAnalyzer(new()).Analyze([new("App.cs", "code")], false, cancellation.Token));
    }

    [Fact]
    public async Task IsolatedWorkspaceAnalysisBlocksActiveMissionsAndSkipsExcludedFiles()
    {
        using var fixture = new StudioFixture();
        var mission = fixture.Missions.Submit(new("Create a project", MissionKind.Project));
        fixture.Workspaces.Create(mission.Id);
        await fixture.Workspaces.WriteAsync(mission.Id, StudioFixture.Manifest().Files, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspaces.GetDirectory(mission.Id), ".npmrc"),
            "//example.invalid/:_authToken=should-not-be-analyzed");
        fixture.Missions.Update(mission.Id, current => current with { HasWorkspace = true, Status = MissionStatus.Running });
        var analysis = new WorkspaceAnalysis(fixture.Missions, fixture.Workspaces, new ProjectAnalyzer(fixture.Options));
        Assert.Equal(WorkspaceAnalysisStatus.Conflict, (await analysis.AnalyzeAsync(mission.Id, CancellationToken.None)).Status);
        fixture.Missions.Update(mission.Id, current => current with { Status = MissionStatus.GeneratedUnverified });
        var result = await analysis.AnalyzeAsync(mission.Id, CancellationToken.None);
        Assert.Equal(WorkspaceAnalysisStatus.Available, result.Status);
        Assert.NotNull(result.Analysis);
        Assert.DoesNotContain("should-not-be-analyzed", JsonSerializer.Serialize(result.Analysis));
        Assert.Null(result.Analysis.GitRepositoryDetected);
    }
}
