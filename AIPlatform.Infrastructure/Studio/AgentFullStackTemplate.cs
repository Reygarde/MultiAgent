using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

// Thin full-stack scaffold: trusted build/test configuration only. Application code is written by the agents.
public static class AgentFullStackTemplate
{
    public const string Id = "custom-agent-fullstack";

    public static bool IsEditable(string path)
    {
        if (path.StartsWith("frontend/src/", StringComparison.Ordinal) && path != "frontend/src/main.tsx" &&
            Path.GetExtension(path) is ".ts" or ".tsx" or ".css")
            return true;
        return (path.StartsWith("backend/src/App/", StringComparison.Ordinal) ||
                path.StartsWith("backend/tests/App.Tests/", StringComparison.Ordinal)) &&
            path.EndsWith(".cs", StringComparison.Ordinal) && path is not ("backend/src/App/Program.cs" or "backend/tests/App.Tests/SmokeTests.cs") &&
            !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal);
    }

    public static bool IsPath(string path) =>
        path.StartsWith("frontend/", StringComparison.Ordinal) || path.StartsWith("backend/", StringComparison.Ordinal);

    public static void Validate(IReadOnlyList<GeneratedFile> files)
    {
        var filesByPath = files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        foreach (var baseline in Files.Where(file => !IsEditable(file.Path)))
        {
            if (!filesByPath.TryGetValue(baseline.Path, out var current))
                throw new StudioValidationException($"Full-stack project is missing required template file: {baseline.Path}");
            if (current.Content != baseline.Content)
                throw new StudioValidationException("Trusted full-stack configuration is immutable: " + baseline.Path);
        }
        foreach (var file in files)
            if (!IsEditable(file.Path) && Files.All(baseline => baseline.Path != file.Path) && file.Path != "README.md")
                throw new StudioValidationException(
                    "Full-stack projects may add only frontend/src TypeScript/CSS and backend C# files: " + file.Path);
        if (files.Where(file => IsEditable(file.Path)).Any(file =>
            file.Content.Contains("@ts-nocheck", StringComparison.Ordinal) ||
            file.Content.Contains("@ts-ignore", StringComparison.Ordinal)))
            throw new StudioValidationException("Generated React sources must not disable type checking.");
    }

    public static IReadOnlyList<GeneratedFile> Files { get; } =
    [
        new("frontend/package.json", ReactTemplate.Files.Single(file => file.Path == "package.json").Content),
        new("frontend/package-lock.json", ReactTemplate.Files.Single(file => file.Path == "package-lock.json").Content),
        new("frontend/index.html", ReactTemplate.Files.Single(file => file.Path == "index.html").Content),
        new("frontend/tsconfig.json", ReactTemplate.Files.Single(file => file.Path == "tsconfig.json").Content),
        new("frontend/test-setup.ts", ReactTemplate.Files.Single(file => file.Path == "test-setup.ts").Content),
        new("frontend/vite.config.ts", """
            import { defineConfig } from 'vitest/config'
            import react from '@vitejs/plugin-react'
            export default defineConfig({
              plugins: [react()],
              server: { proxy: { '/api': { target: process.env.STUDIO_API_URL ?? 'http://127.0.0.1:5080', changeOrigin: true } } },
              test: { environment: 'jsdom', globals: true, setupFiles: ['./test-setup.ts'],
                include: ['src/**/*.test.ts', 'src/**/*.test.tsx'], maxWorkers: 1, fileParallelism: false }
            })
            """),
        new("frontend/src/main.tsx", ReactTemplate.Files.Single(file => file.Path == "src/main.tsx").Content),
        new("frontend/src/styles/app.css", ReactTemplate.Files.Single(file => file.Path == "src/styles/app.css").Content),
        new("frontend/src/App.tsx", """
            export default function App() {
              return <main><h1>Task Studio</h1></main>
            }
            """),
        new("frontend/src/smoke.test.ts", """
            import { expect, test } from 'vitest'
            test('scaffold placeholder', () => { expect(1 + 1).toBe(2) })
            """),
        new("backend/Backend.slnx",
            """<Solution><Project Path="src/App/App.csproj" /><Project Path="tests/App.Tests/App.Tests.csproj" /></Solution>"""),
        new("backend/src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """),
        new("backend/src/App/Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            AppModule.AddServices(builder.Services);
            var app = builder.Build();
            app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
            AppModule.MapEndpoints(app);
            app.Run();

            public partial class Program;
            """),
        new("backend/src/App/AppModule.cs", """
            public static class AppModule
            {
                public static void AddServices(IServiceCollection services)
                {
                }

                public static void MapEndpoints(WebApplication app)
                {
                }
            }
            """),
        new("backend/tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsTestProject>true</IsTestProject>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.15" />
                <ProjectReference Include="../../src/App/App.csproj" />
              </ItemGroup>
            </Project>
            """),
        new("backend/tests/App.Tests/SmokeTests.cs", """
            using Xunit;

            public sealed class SmokeTests
            {
                [Fact]
                public void Placeholder() => Assert.True(true);
            }
            """)
    ];
}
