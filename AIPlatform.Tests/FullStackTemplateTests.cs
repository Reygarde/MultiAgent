using System.Text.Json;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class FullStackTemplateTests
{
    [Fact]
    public void TemplatePinsNuGetGraphsAndSeparatesSpecialistWriteScopes()
    {
        var files = FullStackTemplate.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        using var apiLock = JsonDocument.Parse(files["backend/Api/packages.lock.json"].Content);
        using var testsLock = JsonDocument.Parse(files["backend/Api.Tests/packages.lock.json"].Content);

        Assert.Equal("10.0.15", apiLock.RootElement.GetProperty("dependencies").GetProperty("net10.0")
            .GetProperty("Microsoft.Data.Sqlite").GetProperty("resolved").GetString());
        Assert.Equal("17.14.1", testsLock.RootElement.GetProperty("dependencies").GetProperty("net10.0")
            .GetProperty("Microsoft.NET.Test.Sdk").GetProperty("resolved").GetString());
        Assert.All(FullStackTemplate.Files.Where(file => file.Path.EndsWith("packages.lock.json", StringComparison.Ordinal)),
            file => Assert.Contains("contentHash", file.Content, StringComparison.Ordinal));
        Assert.Equal(3, FullStackTemplate.Files.Count(file => FullStackTemplate.IsEditableForRole(file.Path, "Backend")));
        Assert.Equal(3, FullStackTemplate.Files.Count(file => FullStackTemplate.IsEditableForRole(file.Path, "Frontend")));
        Assert.All(FullStackTemplate.Files.Where(file => !FullStackTemplate.IsEditable(file.Path)),
            file => Assert.False(FullStackTemplate.IsEditableForRole(file.Path, "Backend") ||
                                FullStackTemplate.IsEditableForRole(file.Path, "Frontend")));
    }

    [Fact]
    public void WorkspaceAcceptsOnlyTheFixedFullStackFilesAndLockedDependencies()
    {
        using var fixture = new StudioFixture();
        fixture.Workspaces.ValidateFiles(FullStackTemplate.Files, FullStackTemplate.Id);

        var alteredLock = FullStackTemplate.Files.Select(file => file.Path == "backend/Api/packages.lock.json"
            ? file with { Content = file.Content.Replace("10.0.15", "10.0.16", StringComparison.Ordinal) }
            : file).ToArray();
        Assert.Throws<StudioValidationException>(() =>
            fixture.Workspaces.ValidateFiles(alteredLock, FullStackTemplate.Id));
        Assert.Throws<StudioValidationException>(() =>
            fixture.Workspaces.ValidateFiles(FullStackTemplate.Files.Skip(1).ToArray(), FullStackTemplate.Id));
    }

    [Fact]
    public void FullStackCommandsUseLockedOfflineRestoreAndLoopbackIntegration()
    {
        var build = FullStackBuildProfile.Command("BackendBuild");
        var tests = FullStackBuildProfile.Command("BackendTests");
        var integration = FullStackBuildProfile.Command("Integration");

        Assert.Contains("RestoreLockedMode=true", build, StringComparison.Ordinal);
        Assert.Contains("RestoreLockedMode=true", tests, StringComparison.Ordinal);
        Assert.Contains("RestoreSources=/opt/empty-feed", tests, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:5080", integration, StringComparison.Ordinal);
        Assert.Contains("FULLSTACK_API_BASE_URL=http://127.0.0.1:5080", integration, StringComparison.Ordinal);
        Assert.Throws<StudioValidationException>(() => FullStackBuildProfile.Command("Shell"));
    }

    [Fact]
    public void FullStackContainerStagesRemainOfflineNonPrivilegedAndReadOnly()
    {
        using var fixture = new StudioFixture();
        var runner = new DockerSandboxRunner(new SandboxOptions { Enabled = true },
            fixture.Workspaces, new WorkflowImageProcess());
        var arguments = runner.CreateArguments("fullstack-test", fixture.Options.DataDirectory,
            "Integration", FullStackTemplate.Image, FullStackTemplate.Id);

        Assert.Contains("--network=none", arguments);
        Assert.Contains("--read-only", arguments);
        Assert.Contains("--cap-drop=ALL", arguments);
        Assert.Contains("--security-opt=no-new-privileges:true", arguments);
        Assert.Equal("10001:10001", arguments[Array.IndexOf(arguments.ToArray(), "--user") + 1]);
        Assert.Contains(arguments, argument => argument.Contains("target=/source,readonly", StringComparison.Ordinal));
        Assert.Contains(arguments, argument => argument.Contains("RestoreLockedMode=true", StringComparison.Ordinal));
    }
}
