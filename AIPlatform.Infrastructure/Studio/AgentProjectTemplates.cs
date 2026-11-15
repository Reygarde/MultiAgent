using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static class AgentReactTemplate
{
    public const string Id = "custom-agent-react";

    public static IReadOnlyList<GeneratedFile> Files => ReactTemplate.Files;

    public static bool IsEditable(string path)
    {
        if (ReactTemplate.IsEditable(path))
            return true;
        if (ReactTemplate.IsPath(path))
            return false;
        return IsAdditionalSource(path);
    }

    public static bool IsAdditionalSource(string path) =>
        path.StartsWith("src/", StringComparison.Ordinal) &&
        Path.GetExtension(path) is ".ts" or ".tsx" or ".css";

    public static void Validate(IReadOnlyList<GeneratedFile> files)
    {
        var filesByPath = files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        foreach (var baseline in ReactTemplate.Files)
        {
            if (!filesByPath.TryGetValue(baseline.Path, out var current))
                throw new StudioValidationException($"React project is missing required template file: {baseline.Path}");
            if (!ReactTemplate.IsEditable(baseline.Path) && current.Content != baseline.Content)
                throw new StudioValidationException("Trusted React configuration and tests are immutable: " + baseline.Path);
        }

        foreach (var file in files.Where(file => !ReactTemplate.IsPath(file.Path)))
            if (!IsAdditionalSource(file.Path))
                throw new StudioValidationException("Custom React projects may add only TypeScript, TSX, or CSS files under src/.");

        if (files.Where(file => IsEditable(file.Path)).Any(file =>
            file.Content.Contains("@ts-nocheck", StringComparison.Ordinal) ||
            file.Content.Contains("@ts-ignore", StringComparison.Ordinal)))
            throw new StudioValidationException("Generated React sources must not disable type checking.");
    }
}

public static class AgentDotNetTemplate
{
    public const string Id = "custom-agent-dotnet";

    public static IReadOnlyList<GeneratedFile> Files { get; } =
    [
        new("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """),
        new("src/App/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/health", () => Results.Ok(new { status = HealthStatus.Get() }));
            app.Run();

            public static class HealthStatus
            {
                public static string Get() => "ok";
            }
            """),
        new("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsTestProject>true</IsTestProject>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <ProjectReference Include="../../src/App/App.csproj" />
              </ItemGroup>
            </Project>
            """),
        new("tests/App.Tests/HealthStatusTests.cs", """
            using Xunit;

            public sealed class HealthStatusTests
            {
                [Fact]
                public void ReturnsOk()
                {
                    Assert.Equal("ok", HealthStatus.Get());
                }
            }
            """),
        new("README.md", """
            # .NET 10 API scaffold

            This trusted starter exposes a simple health endpoint. Replace or extend the application
            only through an explicitly approved Studio proposal. Studio does not execute generated code.
            """)
    ];
}
