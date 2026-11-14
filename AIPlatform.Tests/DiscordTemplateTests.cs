using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class DiscordTemplateTests
{
    [Fact]
    public async Task TemplateIsScopedToDiscordAndRejectsUnknownPackagesAndCredentials()
    {
        using var fixture = new StudioFixture();
        var files = DiscordTemplate.Files;
        fixture.Workspaces.ValidateFiles(files, DiscordTemplate.Id);
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(files));
        var unknown = files.Select(file => file.Path == "src/App/App.csproj"
            ? file with { Content = file.Content.Replace("Discord.Net.WebSocket", "Unknown.Package") } : file).ToArray();
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(unknown, DiscordTemplate.Id));
        var changedVersion = files.Select(file => file.Path == "src/App/App.csproj"
            ? file with { Content = file.Content.Replace("[3.20.2]", "[3.20.1]") } : file).ToArray();
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(changedVersion, DiscordTemplate.Id));
        var secret = files.Select(file => file.Path == DiscordTemplate.EditableFile
            ? file with { Content = """class X { string token = "secret-value"; }""" } : file).ToArray();
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(secret, DiscordTemplate.Id));
        var id = Guid.NewGuid();
        fixture.Workspaces.Create(id);
        await fixture.Workspaces.WriteAsync(id, files, default, DiscordTemplate.Id);
        Assert.Equal(files.Count + 1, fixture.Workspaces.List(id).Count);
        Assert.Contains("DISCORD_BOT_TOKEN", (await fixture.Workspaces.ReadAsync(id, "src/App/Program.cs", default)).Content);
    }

    [Fact]
    public async Task DiscordWorkflowUsesTemplateAndRequiresApprovalBeforeSandbox()
    {
        using var fixture = new WorkflowFixture(responseOverride: request =>
        {
            var properties = request.ResponseSchema!.Value.GetProperty("properties");
            object response = properties.TryGetProperty("decisions", out _) ? WorkflowFixture.Plan :
                properties.TryGetProperty("content", out _) ? DiscordTemplate.Files.Single(file => file.Path == DiscordTemplate.EditableFile) :
                new ReviewReport("Reviewed.", [], "Review", [], "No blocking defect.", [], "Matches plan.", "Verify in Docker.");
            return JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        var mission = fixture.Store.Submit(new("Create a Discord bot", MissionKind.Project,
            ApproveSandboxExecution: true, TemplateId: DiscordTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var pending = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, pending.Status);
        Assert.False(pending.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
        Assert.Equal(DiscordTemplate.Files.Count, pending.Workflow!.Draft!.Count);
        Assert.Single(fixture.Provider.Requests, request =>
            request.ResponseSchema!.Value.GetProperty("properties").TryGetProperty("content", out _));
        Assert.True(fixture.Store.DecideApproval(mission.Id, false, (await fixture.Plans.GetAsync(mission.Id, default)).Fingerprint));
        Assert.Equal(MissionStatus.GeneratedUnverified, fixture.Store.Get(mission.Id)!.Status);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task ApprovalFingerprintBindsTheActualDiscordImage()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Discord", MissionKind.Project, TemplateId: DiscordTemplate.Id));
        fixture.Workspaces.Create(mission.Id);
        await fixture.Workspaces.WriteAsync(mission.Id, DiscordTemplate.Files, default, DiscordTemplate.Id);
        var process = new ImageProcess();
        var service = new ExecutionPlanService(fixture.Workspaces, new(), fixture.Options, fixture.Store, process);
        var first = await service.GetAsync(mission.Id, default);
        Assert.Equal("sha256:" + new string('a', 64), first.Image);
        Assert.Contains(first.Operations, operation => operation.Contains("Discord.Net.WebSocket 3.20.2"));
        process.ImageId = "sha256:" + new string('b', 64);
        var second = await service.GetAsync(mission.Id, default);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task DiscordRunnerRejectsMissingImageAndUnapprovedExecution()
    {
        using var fixture = new WorkflowFixture();
        var mission = fixture.Store.Submit(new("Discord", MissionKind.Project, TemplateId: DiscordTemplate.Id));
        fixture.Workspaces.Create(mission.Id);
        await fixture.Workspaces.WriteAsync(mission.Id, DiscordTemplate.Files, default, DiscordTemplate.Id);
        var process = new ImageProcess { Missing = true };
        var options = new SandboxOptions { Enabled = true };
        var plans = new ExecutionPlanService(fixture.Workspaces, options, fixture.Options, fixture.Store, process);
        var runner = new DockerSandboxRunner(options, fixture.Workspaces, process, fixture.Store, plans);
        var missing = await runner.GetAvailabilityAsync(mission.Id, default);
        Assert.False(missing.Available);
        Assert.Contains(DiscordTemplate.Image, missing.Reason);
        process.Missing = false;
        Assert.True((await runner.GetAvailabilityAsync(mission.Id, default)).Available);
        await Assert.ThrowsAsync<StudioValidationException>(() => runner.RunAsync(mission.Id, "Build", default));
        Assert.DoesNotContain(process.Calls, arguments => arguments[0] == "run");
    }

    [Fact]
    public void TemplateRequiresCollaborativeProjectAndRejectsCredentials()
    {
        using var legacy = new StudioFixture();
        Assert.Throws<StudioValidationException>(() =>
            legacy.Missions.Submit(new("Create a bot", MissionKind.Project, TemplateId: DiscordTemplate.Id)));
        using var workflow = new WorkflowFixture();
        Assert.Throws<StudioValidationException>(() =>
            workflow.Store.Submit(new("""token = "secret-value" """, MissionKind.Project, TemplateId: DiscordTemplate.Id)));
        Assert.Throws<StudioValidationException>(() =>
            workflow.Store.Submit(new("Unknown template", MissionKind.Project, TemplateId: "unknown")));
    }

    private sealed class ImageProcess : IContainerProcess
    {
        public string ImageId { get; set; } = "sha256:" + new string('a', 64);
        public bool Missing { get; set; }
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            if (arguments[0] == "info")
                return Task.FromResult(new ContainerProcessResult(0, "linux\n"));
            Assert.Equal(DiscordTemplate.Image, arguments.Last());
            return Task.FromResult(Missing ? new ContainerProcessResult(1, "Missing") : new(0, ImageId));
        }
    }
}
