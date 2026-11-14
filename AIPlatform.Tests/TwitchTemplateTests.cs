using System.IO.Compression;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class TwitchTemplateTests
{
    [Fact]
    public async Task TemplateRejectsChangedDependenciesProtectedFilesSecretsAndExtraPaths()
    {
        using var fixture = new StudioFixture();
        fixture.Workspaces.ValidateFiles(TwitchTemplate.Files, TwitchTemplate.Id);
        foreach (var path in new[] { "src/App/App.csproj", "src/App/EventSubTransport.cs", "tests/App.Tests/UnitTests.cs" })
        {
            var changed = TwitchTemplate.Files.Select(file => file.Path == path
                ? file with { Content = file.Content + "\n " } : file).ToArray();
            Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(changed, TwitchTemplate.Id));
        }
        var unauthorized = TwitchTemplate.Files.Select(file => file.Path == "tests/App.Tests/App.Tests.csproj"
            ? file with { Content = file.Content.Replace("[2.9.3]", "[2.9.2]") } : file).ToArray();
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(unauthorized, TwitchTemplate.Id));
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(
            [.. TwitchTemplate.Files, new("src/App/Extra.cs", "class Extra {}")], TwitchTemplate.Id));
        var exposed = TwitchTemplate.Files.Select(file => file.Path == "src/App/CommandRouter.cs"
            ? file with { Content = """class X { string token = "synthetic-credential"; }""" } : file).ToArray();
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(exposed, TwitchTemplate.Id));
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(TwitchTemplate.Files, "unknown"));
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        await fixture.Workspaces.WriteAsync(id, TwitchTemplate.Files, default, TwitchTemplate.Id);
        var archive = await new ProjectExport(fixture.Workspaces, fixture.Options).CreateAsync(id, default);
        using var zip = new ZipArchive(new MemoryStream(archive));
        Assert.Equal(TwitchTemplate.Files.Count + 1, zip.Entries.Count);
        Assert.Contains(zip.Entries, entry => entry.FullName == "src/App/TwitchAuthentication.cs");
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.EndsWith(".env"));
    }

    [Theory]
    [InlineData("TWITCH_ACCESS_TOKEN = \"synthetic-value\"")]
    [InlineData("oauth:abcdefghijklmnopqrstuvwxyz1234")]
    public void CredentialsAreRejectedBeforeAnyProviderCall(string task)
    {
        using var fixture = new WorkflowFixture();
        Assert.Throws<StudioValidationException>(() =>
            fixture.Store.Submit(new(task, MissionKind.Project, TemplateId: TwitchTemplate.Id)));
        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.Store.List());
    }

    [Fact]
    public async Task WorkflowGeneratesOnlyApplicationFilesReviewsThemAndPersistsPendingApproval()
    {
        using var fixture = new WorkflowFixture(responseOverride: Respond);
        var mission = fixture.Store.Submit(new("Create Twitch bot", MissionKind.Project,
            ApproveSandboxExecution: true, TemplateId: TwitchTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var pending = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, pending.Status);
        Assert.False(pending.SandboxApproved);
        Assert.Null(pending.ApprovalFingerprint);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(TwitchTemplate.Files.Count + 1, fixture.Workspaces.List(mission.Id).Count);
        var generated = fixture.Provider.Requests.Where(request =>
            request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("content", out _)).ToArray();
        Assert.Equal(2, generated.Length);
        Assert.All(generated, request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            var path = input.RootElement.GetProperty("file").GetProperty("path").GetString()!;
            Assert.True(TwitchTemplate.IsEditable(path));
            Assert.Equal(path, request.ResponseSchema!.Value.GetProperty("properties").GetProperty("path").GetProperty("enum")[0].GetString());
            Assert.True(request.Messages.Sum(message => message.Content.Length) < (8192 - 3072) * 2);
        });
        var review = fixture.Provider.Requests.Last();
        Assert.Contains("CommandRouter.cs", review.Messages.Last().Content);
        Assert.Contains("BotHost.cs", review.Messages.Last().Content);
        Assert.DoesNotContain("TWITCH_ACCESS_TOKEN", review.Messages.Last().Content);
        Assert.True(review.Messages.Sum(message => message.Content.Length) < (8192 - 3072) * 2);
        using var restarted = new MissionStore(fixture.Options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        var restored = restarted.Get(mission.Id)!;
        Assert.Equal(TwitchTemplate.Id, restored.TemplateId);
        Assert.Equal(pending.Status, restored.Status);
        Assert.Equal(pending.Events.Count, restored.Events.Count);
        Assert.Equal(pending.Workflow!.Revisions![0].Fingerprint, restored.Workflow!.Revisions![0].Fingerprint);
        Assert.False(restored.SandboxApproved);
    }

    [Fact]
    public async Task RunnerBindsReleaseCommandsImageAndCurrentFilesToFreshApproval()
    {
        using var fixture = new WorkflowFixture(responseOverride: Respond);
        var process = new ImageProcess();
        var options = new SandboxOptions { Enabled = true };
        var plans = new ExecutionPlanService(fixture.Workspaces, options, fixture.Options, fixture.Store, process);
        var processor = new CollaborativeProcessor(fixture.Store,
            new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options),
            fixture.Workspaces, plans, fixture.Sandbox, fixture.Options);
        var mission = fixture.Store.Submit(new("Twitch", MissionKind.Project, TemplateId: TwitchTemplate.Id));
        await processor.ProcessAsync(mission.Id, default);
        var runner = new DockerSandboxRunner(options, fixture.Workspaces, process, fixture.Store, plans);
        Assert.True((await runner.GetAvailabilityAsync(mission.Id, default)).Available);
        var plan = await plans.GetAsync(mission.Id, default);
        Assert.Contains(plan.Operations, text => text.Contains("Release"));
        await Assert.ThrowsAsync<StudioValidationException>(() => runner.RunAsync(mission.Id, "Build", default));
        Assert.True(fixture.Store.DecideApproval(mission.Id, true, plan.Fingerprint));
        process.ImageId = "sha256:" + new string('b', 64);
        await Assert.ThrowsAsync<StudioValidationException>(() => runner.RunAsync(mission.Id, "Build", default));
        process.ImageId = plan.Image;
        var changed = TwitchTemplate.Files.Select(file => file.Path == "src/App/CommandRouter.cs"
            ? file with { Content = file.Content + "\n" } : file).ToArray();
        await fixture.Workspaces.WriteAsync(mission.Id, changed, default, TwitchTemplate.Id);
        await Assert.ThrowsAsync<StudioValidationException>(() => runner.RunAsync(mission.Id, "Build", default));
        Assert.DoesNotContain(process.Calls, args => args[0] == "run");
        await fixture.Workspaces.WriteAsync(mission.Id, TwitchTemplate.Files, default, TwitchTemplate.Id);
        Assert.Equal("Succeeded", (await runner.RunAsync(mission.Id, "Build", default)).Status);
        var arguments = process.Calls.Single(args => args[0] == "run");
        foreach (var flag in new[] { "--network=none", "--read-only", "--cap-drop=ALL",
                     "--security-opt=no-new-privileges:true", "--pull=never", "10001:10001", plan.Image })
            Assert.Contains(flag, arguments);
        Assert.Contains("dotnet build Generated.slnx --configuration Release", arguments.Last());
        Assert.Contains(process.Calls, args => args[0] == "rm");
        var tests = runner.CreateArguments("test", fixture.Workspaces.GetDirectory(mission.Id), "Tests", plan.Image, TwitchTemplate.Id).Last();
        Assert.Contains("dotnet test tests/App.Tests/App.Tests.csproj --configuration Release", tests);
        Assert.DoesNotContain("--no-build", tests);
        Assert.DoesNotContain("dotnet run", tests);
        Assert.DoesNotContain("--configuration Release",
            runner.CreateArguments("discord", fixture.Workspaces.GetDirectory(mission.Id), "Build", plan.Image, DiscordTemplate.Id).Last());
        process.Missing = true;
        Assert.False((await runner.GetAvailabilityAsync(mission.Id, default)).Available);
    }

    [Fact]
    public async Task CorrectionIsLimitedToEditableSourcesAndDemandsAnotherApproval()
    {
        using var fixture = new WorkflowFixture(responseOverride: Respond);
        var mission = fixture.Store.Submit(new("Twitch", MissionKind.Project, TemplateId: TwitchTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        await fixture.Approve(mission.Id);
        fixture.Sandbox.Result = stage => new(stage, "Failed", 1, "infrastructure or compilation failure");
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var pending = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, pending.Status);
        Assert.Equal(2, pending.Workflow!.Version);
        Assert.Equal(2, pending.Workflow.Revisions!.Count);
        Assert.All(pending.Workflow.CorrectionPaths!, path => Assert.True(TwitchTemplate.IsEditable(path)));
        Assert.False(pending.SandboxApproved);
        Assert.Null(pending.ApprovalFingerprint);
        Assert.Equal(new[] { "Build" }, fixture.Sandbox.Calls);
        Assert.All(fixture.Provider.Requests, request =>
            Assert.True(request.Messages.Sum(message => message.Content.Length) < (8192 - 3072) * 2));
        Assert.All(fixture.Provider.Requests.Where(request =>
            request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("content", out _)), request =>
            Assert.Contains("Reconnection and exponential backoff ALREADY exist", request.Messages[0].Content));
    }

    [Fact]
    public async Task OptimisticLlmReviewCannotApproveForbiddenBotHostDispatch()
    {
        using var fixture = new WorkflowFixture(corrections: 0, responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("content", out _) &&
                input.RootElement.GetProperty("file").GetProperty("path").GetString() == "src/App/BotHost.cs")
                return JsonSerializer.Serialize(new GeneratedFile("src/App/BotHost.cs",
                    "namespace App; public class BotHost { public void Run() { while (true) {} } }"),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Respond(request);
        });
        var mission = fixture.Store.Submit(new("Twitch", MissionKind.Project, TemplateId: TwitchTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(MissionStatus.Failed, fixture.Store.Get(mission.Id)!.Status);
        Assert.Contains(fixture.Store.Get(mission.Id)!.Workflow!.Review!.Findings,
            finding => finding.Severity == "Error" && finding.Message.StartsWith("Server template policy:"));
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public void CancellableSendErrorCooldownIsNotMistakenForReconnection()
    {
        var files = TwitchTemplate.Files.Select(file => file.Path == "src/App/BotHost.cs"
            ? file with { Content = file.Content.Replace("BotLog.Write",
                "await Task.Delay(TimeSpan.FromSeconds(5), cancellation); BotLog.Write") } : file).ToArray();
        Assert.Null(TwitchTemplate.DispatchFinding(files));
    }

    [Fact]
    public async Task DirectDeveloperCallsCannotAlterProtectedTransport()
    {
        using var fixture = new WorkflowFixture(responseOverride: Respond);
        var mission = fixture.Store.Submit(new("Twitch", MissionKind.Project, TemplateId: TwitchTemplate.Id));
        var developer = mission.Workflow!.Agents.Single(agent => agent.Role == "Developer");
        var agents = new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options);
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            agents.GenerateFileAsync(mission with { Workflow = mission.Workflow with { Plan = new("Twitch", "Bot", [], TwitchTemplate.PlannedFiles) } },
                developer, TwitchTemplate.PlannedFiles.Single(file => file.Path == "src/App/Program.cs"), default));
    }

    [Fact]
    public async Task TwitchCorrectionKeepsHistoryInStorageWithoutResendingOldSource()
    {
        using var fixture = new WorkflowFixture(responseOverride: Respond);
        var mission = fixture.Store.Submit(new("Twitch", MissionKind.Project, TemplateId: TwitchTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var pending = fixture.Store.Get(mission.Id)!;
        var old = new string('x', 6000);
        var input = pending with { Workflow = pending.Workflow! with
        {
            Diagnostics = new string('d', 2500),
            Revisions = [new(1, DateTimeOffset.UtcNow, "Prior source", "fingerprint",
                [new("src/App/BotHost.cs", old)])]
        } };
        var agents = new WorkflowAgents(new LlmProviderResolver([fixture.Provider]), fixture.Options);
        await agents.GenerateFileAsync(input, input.Workflow!.Agents.Single(agent => agent.Role == "Developer"),
            TwitchTemplate.PlannedFiles.Single(file => file.Path == "src/App/BotHost.cs"), default);
        Assert.DoesNotContain(old, fixture.Provider.Requests.Last().Messages.Last().Content);
        Assert.True(fixture.Provider.Requests.Last().Messages.Sum(message => message.Content.Length) < (8192 - 3072) * 2);
        Assert.Equal(old, input.Workflow.Revisions![0].Files[0].Content);
    }

    private static string Respond(LlmRequest request)
    {
        var properties = request.ResponseSchema!.Value.GetProperty("properties");
        object response;
        if (properties.TryGetProperty("decisions", out _)) response = WorkflowFixture.Plan;
        else if (properties.TryGetProperty("content", out _))
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            response = TwitchTemplate.Files.Single(file => file.Path == input.RootElement.GetProperty("file").GetProperty("path").GetString());
        }
        else response = new ReviewReport("Simulated source review, no execution.", [], "None", [],
            "Matches contracts.", ["Live Twitch not tested."], "Matches plan.", "Request Docker approval.");
        return JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class ImageProcess : IContainerProcess
    {
        public string ImageId { get; set; } = "sha256:" + new string('a', 64);
        public bool Missing { get; set; }
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            return Task.FromResult(new ContainerProcessResult(Missing ? 1 : 0,
                arguments[0] == "image" ? ImageId : arguments[0] == "info" ? "linux\n" : ""));
        }
    }
}
