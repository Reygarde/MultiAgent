using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace AIPlatform.Tests;

public sealed class PrepareCorrectionScenarioFactAttribute : FactAttribute
{
    public PrepareCorrectionScenarioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AIPLATFORM_PREPARE_CORRECTION") != "1")
            Skip = "Manual scenario preparation is opt-in; it never approves or runs Docker.";
    }
}

public sealed class ManualCorrectionScenarioTests(ITestOutputHelper output)
{
    [PrepareCorrectionScenarioFact]
    public async Task PrepareCompilerErrorWithoutRunningOrApprovingDocker()
    {
        var directory = Environment.GetEnvironmentVariable("AIPLATFORM_SCENARIO_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory) || Directory.Exists(directory))
            throw new InvalidOperationException("Set AIPLATFORM_SCENARIO_DIRECTORY to a new, dedicated directory.");
        var settings = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "AIPlatform.API", "appsettings.json"));
        var config = new ConfigurationBuilder().AddJsonFile(settings).Build();
        var platform = config.GetSection("Platform").Get<PlatformConfiguration>()!;
        var options = config.GetSection("Studio").Get<StudioOptions>()! with { DataDirectory = directory };
        var sandboxOptions = config.GetSection("Sandbox").Get<SandboxOptions>()!;
        var registry = new AgentRegistry(platform);
        var workspaces = new WorkspaceStore(options);
        using var store = new MissionStore(options, platform, registry, workspaces);
        var sandbox = new WorkflowSandbox(true);
        var plans = new ExecutionPlanService(workspaces, sandboxOptions, options);
        var files = DockerIntegrationTests.Calculator(broken: true).Files.Select(file =>
            file.Path == "tests/App.Tests/UnitTests.cs" ? file with { Content = """
                using Xunit;
                using App;
                public class CalculatorTests
                {
                    [Fact] public void Addition() { Assert.Equal(4, Calculator.Add(2, 2)); }
                    [Fact] public void Subtraction() { Assert.Equal(3, Calculator.Subtract(5, 2)); }
                }
                """ } : file).ToArray();
        var provider = new FakeProvider((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = request.ResponseSchema!.Value.GetProperty("properties");
            object answer;
            if (properties.TryGetProperty("decisions", out _))
                answer = WorkflowFixture.Plan;
            else if (properties.TryGetProperty("content", out _))
            {
                using var input = JsonDocument.Parse(request.Messages.Last().Content);
                var path = input.RootElement.GetProperty("file").GetProperty("path").GetString();
                answer = files.Single(file => file.Path == path);
            }
            else
                answer = new ReviewReport("Prepared deterministic fixture; no real initial model review or validation was performed.",
                    [], "Deliberate compiler error for manual validation.", [], "Controlled scenario.", [],
                    "Calculator fixture.", "Run only after explicit human authorization.");
            return Task.FromResult(JsonSerializer.Serialize(answer, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        });
        var processor = new CollaborativeProcessor(store,
            new WorkflowAgents(new LlmProviderResolver([provider]), options), workspaces, plans, sandbox, options);
        var mission = store.Submit(new("Créer une bibliothèque .NET 10 Calculator avec tests d'addition et de soustraction.",
            MissionKind.Project));
        await processor.ProcessAsync(mission.Id, CancellationToken.None);
        var prepared = store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, prepared.Status);
        Assert.False(prepared.SandboxApproved);
        Assert.Empty(sandbox.Calls);
        Assert.Empty(prepared.Verification);
        var plan = await plans.GetAsync(mission.Id, CancellationToken.None);
        output.WriteLine($"Mission: {mission.Id}");
        output.WriteLine($"Workspace: {workspaces.GetDirectory(mission.Id)}");
        output.WriteLine($"Fingerprint: {plan.Fingerprint}");
        output.WriteLine("Initial plan, files and review are deterministic fixtures, not real LLM outputs.");
        output.WriteLine("No Docker container has been run or authorized.");
    }
}
