using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

internal sealed class StudioFixture : IDisposable
{
    public StudioOptions Options { get; } = new()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "AIPlatform-tests-" + Guid.NewGuid().ToString("N"))
    };
    public PlatformConfiguration Platform { get; } = TestSupport.Configuration();
    public AgentRegistry Registry { get; }
    public WorkspaceStore Workspaces { get; }
    public MissionStore Missions { get; }

    public StudioFixture()
    {
        Registry = new(Platform);
        Workspaces = new(Options);
        Missions = new(Options, Platform, Registry);
    }

    public static ProjectManifest Manifest(string summary = "Project generated.") => new(summary,
    [
        new("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
        new("src/App/Program.cs", """var app = WebApplication.CreateBuilder(args).Build(); app.MapGet("/", () => "OK"); app.Run();"""),
        new("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../../src/App/App.csproj" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
              </ItemGroup>
            </Project>
            """),
        new("tests/App.Tests/UnitTests.cs", "public class UnitTests { }")
    ]);

    public MissionProcessor Processor(IProjectGenerator generator, ISandboxRunner sandbox, StudioOptions? options = null)
    {
        var provider = new FakeProvider((request, _) => Task.FromResult(request.IsRouting()
            ? """{"agentId":"DeveloperAgent","reason":"Development"}""" : """{"response":"Answer"}"""));
        var resolver = new LlmProviderResolver([provider]);
        return new(Missions, Registry, new LlmAgentRouter(Registry, resolver), resolver,
            generator, Workspaces, sandbox, options ?? Options,
            new ExecutionPlanService(Workspaces, new(), options ?? Options));
    }

    public void Dispose()
    {
        Missions.Dispose();
        if (Directory.Exists(Options.DataDirectory))
            Directory.Delete(Options.DataDirectory, recursive: true);
    }
}

internal sealed class FakeProjectGenerator(
    Func<int, ProjectManifest?, string?, CancellationToken, Task<ProjectManifest>>? generate = null) : IProjectGenerator
{
    public int Calls { get; private set; }

    public Task<ProjectManifest> GenerateAsync(
        AgentDefinition agent, string task, ProjectManifest? previous, string? diagnostics,
        CancellationToken cancellationToken)
    {
        Calls++;
        return generate?.Invoke(Calls, previous, diagnostics, cancellationToken)
            ?? Task.FromResult(StudioFixture.Manifest());
    }
}

internal sealed class FakeSandbox(bool available = false) : ISandboxRunner
{
    public List<string> Calls { get; } = [];
    public Func<string, VerificationResult>? Result { get; init; }

    public Task<SandboxAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SandboxAvailability(available, available ? "Ready" : "Docker unavailable"));

    public Task<VerificationResult> RunAsync(Guid workspaceId, string stage, CancellationToken cancellationToken)
    {
        Calls.Add(stage);
        return Task.FromResult(Result?.Invoke(stage) ?? new(stage, "Succeeded", 0, "Container output",
            stage == "Tests" ? 1 : null, FailedTests: stage == "Tests" ? 0 : null));
    }
}
