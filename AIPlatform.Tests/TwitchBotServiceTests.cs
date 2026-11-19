using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class TwitchBotServiceTests
{
    private const string ClientId = "abcdefghijklmnopqrstuvwxyz1234";

    [Fact]
    public async Task BotConfigurationAndDeviceAuthorizationPersistWithoutExposingTokens()
    {
        using var fixture = new TwitchFixture();
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        Assert.Equal(new TwitchCommand("!hello", "Salut le chat !", 5), bot.Configuration.Commands.Single());
        var device = await fixture.Service.BeginConnectAsync(bot.Configuration.Id, default);
        Assert.Equal("https://www.twitch.tv/activate", device.VerificationUri);
        Assert.Equal("CODE-123", device.UserCode);

        var authorized = await fixture.Service.PollConnectAsync(bot.Configuration.Id, default);
        Assert.True(authorized.Authorized);
        Assert.Equal("sampleuser", authorized.AccountLogin);
        var persisted = new TwitchBotStore(fixture.Options).Get(bot.Configuration.Id)!;
        Assert.Equal(bot.Configuration.Commands, persisted.Commands);
        var configText = File.ReadAllText(Path.Combine(fixture.Options.DataDirectory, "twitch", "bots.json"));
        Assert.DoesNotContain("access-secret", configText);
        Assert.DoesNotContain("refresh-secret", configText);
        Assert.DoesNotContain("access-secret", fixture.Vault.Read(bot.Configuration.Id)!.ToString());
    }

    [Fact]
    public async Task StartsReceivesHelloAppliesCooldownAndStopsTheSingleInstance()
    {
        using var fixture = new TwitchFixture();
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        fixture.Vault.Save(bot.Configuration.Id, fixture.Credentials);

        using var requestCancellation = new CancellationTokenSource();
        var started = await fixture.Service.StartAsync(bot.Configuration.Id, requestCancellation.Token);
        Assert.Equal("Connected", started.State);
        requestCancellation.Cancel();
        Assert.Equal("Connected", fixture.Service.Get(bot.Configuration.Id)!.State);
        var duplicate = await Assert.ThrowsAsync<TwitchIntegrationException>(
            () => fixture.Service.StartAsync(bot.Configuration.Id, default));
        Assert.Equal("bot_running", duplicate.Code);
        Assert.True(fixture.Service.TestCommand(bot.Configuration.Id, "!hello").Matched);

        await fixture.Chat.DeliverAsync(new("message-1", "viewer-1", "!hello"));
        await fixture.Chat.DeliverAsync(new("message-2", "viewer-1", "!hello"));
        Assert.Equal("Salut le chat !", Assert.Single(fixture.Chat.SentMessages).Message);
        Assert.Contains(fixture.Service.Get(bot.Configuration.Id)!.Events, item => item.Kind == "cooldown");

        var stopped = await fixture.Service.StopAsync(bot.Configuration.Id, default);
        Assert.Equal("Stopped", stopped.State);
        Assert.Equal(1, fixture.Chat.CompletedSessions);
    }

    [Fact]
    public void CommandConfigurationUpdatesPersistAndCanBeTestedWithoutAConnection()
    {
        using var fixture = new TwitchFixture();
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        var changed = fixture.Service.Update(bot.Configuration.Id, "Renamed Bot", "OtherChannel",
        [
            new TwitchCommand("!hello", "Bonjour !", 10),
            new TwitchCommand("!off", "Disabled", Enabled: false)
        ]);
        var persisted = new TwitchBotStore(fixture.Options).Get(bot.Configuration.Id)!;
        Assert.Equal("Renamed Bot", persisted.Name);
        Assert.Equal("otherchannel", persisted.Channel);
        Assert.Equal(changed.Configuration.Commands, persisted.Commands);

        var result = fixture.Service.TestCommand(bot.Configuration.Id, "!hello arguments");
        Assert.True(result.Matched);
        Assert.Equal("Bonjour !", result.Response);
        Assert.False(fixture.Service.TestCommand(bot.Configuration.Id, "!off").Matched);
        Assert.Equal(0, fixture.Chat.Connections);
    }

    [Fact]
    public void OfflineSimulationUsesProductionRouterWithRolesAndVirtualCooldownWithoutCredentialsOrTransport()
    {
        using var fixture = new TwitchFixture();
        var id = Guid.NewGuid();
        fixture.Store.Save(new(id, "Test Bot", "samplechannel",
        [
            new("!hello", "Bonjour", 0),
            new("!secret", "Secret", 10, ModeratorOnly: true),
            new("!off", "Disabled", Enabled: false)
        ]));
        var simulator = new TwitchSimulationService(fixture.Store);
        var result = simulator.Simulate(id,
        [
            new("alice", "Viewer", "!hello", 0),
            new("alice", "Viewer", "!secret", 0),
            new("alice", "Moderator", "!secret", 0),
            new("alice", "Moderator", "!secret", 0),
            new("alice", "Moderator", "!secret", 10),
            new("alice", "Viewer", "!off", 10)
        ], default);

        Assert.Equal(new[] { "Responded", "PermissionDenied", "Responded", "Cooldown", "Responded", "NoMatch" },
            result.Steps.Select(step => step.Outcome));
        Assert.Equal("Bonjour", result.Steps[0].Response);
        Assert.Null(result.Steps[1].Response);
        Assert.Equal("Secret", result.Steps[2].Response);
        Assert.Equal(10, result.Steps[3].CooldownRemainingSeconds);
        Assert.Equal("Secret", result.Steps[4].Response);
        Assert.Equal(0, fixture.Vault.ReadCount);
        Assert.Equal(0, fixture.Chat.Connections);
        Assert.Empty(fixture.Chat.SentMessages);
        Assert.Equal("Responded", simulator.Simulate(id, [new("alice", "Moderator", "!secret", 0)], default)
            .Steps.Single().Outcome);
    }

    [Fact]
    public void OfflineSimulationRejectsInvalidSequencesAndHonorsCancellation()
    {
        using var fixture = new TwitchFixture();
        var id = Guid.NewGuid();
        fixture.Store.Save(new(id, "Test Bot", "samplechannel", [new("!hello", "Bonjour")]));
        var simulator = new TwitchSimulationService(fixture.Store);
        Assert.Throws<StudioValidationException>(() =>
            simulator.Simulate(id, [new("viewer", "Admin", "!hello", 0)], default));
        Assert.Throws<StudioValidationException>(() =>
            simulator.Simulate(id, [new("viewer", "Viewer", "!hello", 2), new("viewer", "Viewer", "!hello", 1)], default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            simulator.Simulate(id, [new("viewer", "Viewer", "!hello", 0)], cancelled.Token));
    }

    [Fact]
    public void PreviouslySavedCommandsRemainPublicWithoutModeratorField()
    {
        var oldCommand = JsonSerializer.Deserialize<TwitchCommand>(
            """{"name":"!hello","response":"Bonjour","cooldownSeconds":5,"enabled":true}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(oldCommand.ModeratorOnly);
        Assert.Equal("Bonjour", new TwitchCommandRouter(TimeProvider.System)
            .Route([oldCommand], "!hello", "viewer", consumeCooldown: true).Response);
    }

    [Fact]
    public async Task StructuredProposalsRequireApprovalAndPreserveEvidenceAcrossChanges()
    {
        using var fixture = new TwitchFixture();
        var id = Guid.NewGuid();
        fixture.Store.Save(new(id, "Test Bot", "samplechannel", [new("!hello", "Old", 0)]));
        var calls = 0;
        var provider = new FakeProvider((request, _) => Task.FromResult(
            request.ResponseSchema?.GetProperty("properties").TryGetProperty("summary", out var ignored) == true
                ? """{"summary":"Change the requested commands.","unsupported":[]}""" : ++calls == 1
                ? """{"unsupported":[],"operations":[{"action":"update","name":"!hello","response":"Bonjour !","cooldownSeconds":null,"enabled":null,"moderatorOnly":null},{"action":"add","name":"!secret","response":"Moderators","cooldownSeconds":null,"enabled":true,"moderatorOnly":true}]}"""
                : """{"unsupported":[],"operations":[{"action":"update","name":"!hello","response":null,"cooldownSeconds":10,"enabled":null,"moderatorOnly":null}]}"""));
        var (service, team) = CreateProposalService(fixture, provider);
        var first = await service.GenerateAsync(id, team, "Change !hello to Bonjour and add !secret", default);
        Assert.Equal("Old", fixture.Store.Get(id)!.Commands[0].Response);
        Assert.Throws<StudioValidationException>(() => service.Apply(id, first.Id, false, false, default));
        var applied = service.Apply(id, first.Id, true, false, default);
        Assert.Equal("Applied", applied.Status);
        Assert.All(applied.Evidence, evidence => Assert.True(evidence.Passed));
        Assert.Contains(applied.Evidence, evidence => evidence.Command == "!secret" && evidence.Actual == "PermissionDenied");
        var second = await service.GenerateAsync(id, team, "Change !hello cooldown to 10", default);
        var secondApplied = service.Apply(id, second.Id, true, false, default);
        Assert.All(secondApplied.Evidence, evidence => Assert.True(evidence.Passed));
        Assert.Contains(secondApplied.Evidence, evidence => evidence.Actual == "Cooldown");
        Assert.Equal("Moderators", fixture.Store.Get(id)!.Commands.Single(command => command.Name == "!secret").Response);
        var (restarted, _) = CreateProposalService(fixture, provider, team);
        var history = restarted.List(id);
        Assert.Equal(2, history.Count);
        Assert.Equal("Outdated", history.Single(item => item.Id == first.Id).Status);
        Assert.Equal("Applied", history.Single(item => item.Id == second.Id).Status);
        Assert.Equal(0, fixture.Vault.ReadCount);
        Assert.Equal(0, fixture.Chat.Connections);
        Assert.Empty(fixture.Chat.SentMessages);
    }

    [Theory]
    [InlineData("""{"unsupported":[],"operations":[{"action":"add","name":"!test","response":"Hello","cooldownSeconds":null,"enabled":true,"moderatorOnly":false,"extra":"x"}]}""")]
    [InlineData("""{"unsupported":[],"operations":[{"action":"add","name":"!test","response":"function() { }","cooldownSeconds":null,"enabled":true,"moderatorOnly":false}]}""")]
    [InlineData("""{"unsupported":[],"operations":[{"action":"add","name":"!test","response":"rm -rf folder","cooldownSeconds":null,"enabled":true,"moderatorOnly":false}]}""")]
    [InlineData("""{"unsupported":[],"operations":[{"action":"add","name":"!test","response":"Hello","cooldownSeconds":60,"enabled":true,"moderatorOnly":false}]}""")]
    [InlineData("""{"unsupported":[],"operations":[{"action":"add","name":"!test","response":"Hello","cooldownSeconds":0,"enabled":true}]}""")]
    [InlineData("""{"unsupported":[],"operations":[{"action":"add","name":"!other","response":"Hello","cooldownSeconds":0,"enabled":true,"moderatorOnly":false}]}""")]
    public async Task StructuredProposalsRejectUnknownFieldsCodeAndUnrequestedTargets(string modelOutput)
    {
        using var fixture = new TwitchFixture();
        var id = Guid.NewGuid();
        fixture.Store.Save(new(id, "Test Bot", "samplechannel", []));
        var provider = new FakeProvider((request, _) => Task.FromResult(
            request.ResponseSchema?.GetProperty("properties").TryGetProperty("summary", out var ignored) == true
                ? """{"summary":"Plan","unsupported":[]}""" : modelOutput));
        var (service, team) = CreateProposalService(fixture, provider);
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            service.GenerateAsync(id, team, "Add !test", default));
        Assert.Empty(fixture.Store.Get(id)!.Commands);
        Assert.Equal("Failed", service.List(id).Single().Status);
    }

    [Fact]
    public async Task ContradictoryUnsupportedCooldownIsSentBackForCorrection()
    {
        using var fixture = new TwitchFixture();
        var id = Guid.NewGuid();
        fixture.Store.Save(new(id, "Test Bot", "samplechannel", [new("!hello", "Bonjour !")]));
        var calls = 0;
        var provider = new FakeProvider((request, _) => Task.FromResult(
            request.ResponseSchema?.GetProperty("properties").TryGetProperty("summary", out var ignored) == true
                ? """{"summary":"Plan","unsupported":[]}""" : ++calls == 1
                ? """{"unsupported":["The router already enforces cooldownSeconds."],"operations":[]}"""
                : """{"unsupported":[],"operations":[{"action":"update","name":"!hello","response":null,"cooldownSeconds":10,"enabled":null,"moderatorOnly":null}]}"""));
        var (service, team) = CreateProposalService(fixture, provider);
        var proposal = await service.GenerateAsync(id, team, "Ajoute un cooldown de 10 secondes à !hello.", default);
        Assert.Equal(2, calls);
        Assert.Equal("Proposed", proposal.Status);
        Assert.Equal(10, Assert.Single(proposal.After.Commands).CooldownSeconds);
    }

    [Fact]
    public async Task FailedSimulationRestoresSnapshotAndConflictsDoNotOverwriteChanges()
    {
        using var fixture = new TwitchFixture();
        var id = Guid.NewGuid();
        var original = new TwitchBotConfiguration(id, "Test Bot", "samplechannel", [new("!hello", "Old")]);
        fixture.Store.Save(original);
        var provider = new FakeProvider((request, _) => Task.FromResult(
            request.ResponseSchema?.GetProperty("properties").TryGetProperty("summary", out var ignored) == true
            ? """{"summary":"Plan","unsupported":[]}"""
            : """{"unsupported":[],"operations":[{"action":"update","name":"!hello","response":"Bonjour","cooldownSeconds":null,"enabled":null,"moderatorOnly":null}]}"""));
        var (service, team) = CreateProposalService(fixture, provider, simulation: new FailingSimulation());
        var proposal = await service.GenerateAsync(id, team, "Change !hello", default);
        Assert.Throws<StudioValidationException>(() => service.Apply(id, proposal.Id, true, false, default));
        Assert.Equal(original.Commands, fixture.Store.Get(id)!.Commands);
        Assert.Equal("Failed", service.List(id).Single().Status);
        var other = await service.GenerateAsync(id, team, "Change !hello", default);
        fixture.Service.Update(id, original.Name, original.Channel, [new("!hello", "Manual")]);
        Assert.Throws<TwitchIntegrationException>(() => service.Apply(id, other.Id, true, false, default));
        Assert.Equal("Manual", fixture.Store.Get(id)!.Commands.Single().Response);
    }

    [Fact]
    public async Task InterruptedApplicationCanOnlyRestoreItsOwnUnchangedSnapshot()
    {
        using var fixture = new TwitchFixture();
        var id = Guid.NewGuid();
        var original = new TwitchBotConfiguration(id, "Test Bot", "samplechannel", [new("!hello", "Old")]);
        fixture.Store.Save(original);
        var provider = new FakeProvider((request, _) => Task.FromResult(
            request.ResponseSchema?.GetProperty("properties").TryGetProperty("summary", out var ignored) == true
                ? """{"summary":"Plan","unsupported":[]}"""
                : """{"unsupported":[],"operations":[{"action":"update","name":"!hello","response":"Bonjour","cooldownSeconds":null,"enabled":null,"moderatorOnly":null}]}"""));
        var (service, team) = CreateProposalService(fixture, provider);
        var proposal = await service.GenerateAsync(id, team, "Change !hello", default);
        fixture.Store.Save(proposal.After);
        var path = Path.Combine(fixture.Options.DataDirectory, "twitch", "command-proposals", proposal.Id.ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(proposal with { Status = "Applying" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("Interrupted", service.List(id).Single().Status);
        Assert.Throws<StudioValidationException>(() => service.RestoreInterrupted(id, proposal.Id, false, default));
        fixture.Store.Save(proposal.After with { Commands = [new("!hello", "Manual")] });
        Assert.Throws<TwitchIntegrationException>(() => service.RestoreInterrupted(id, proposal.Id, true, default));
        Assert.Equal("Manual", fixture.Store.Get(id)!.Commands.Single().Response);
        fixture.Store.Save(proposal.After);
        Assert.Equal("Restored", service.RestoreInterrupted(id, proposal.Id, true, default).Status);
        Assert.Equal("Old", fixture.Store.Get(id)!.Commands.Single().Response);
    }

    [Fact]
    public async Task FailedAssertionIsRecordedAndNeverMarkedVerified()
    {
        using var fixture = new TwitchFixture();
        var id = Guid.NewGuid();
        fixture.Store.Save(new(id, "Test Bot", "samplechannel", []));
        var provider = new FakeProvider((request, _) => Task.FromResult(
            request.ResponseSchema?.GetProperty("properties").TryGetProperty("summary", out var ignored) == true
                ? """{"summary":"Plan","unsupported":[]}"""
                : """{"unsupported":[],"operations":[{"action":"add","name":"!hello","response":"Bonjour","cooldownSeconds":null,"enabled":true,"moderatorOnly":false}]}"""));
        var (service, team) = CreateProposalService(fixture, provider, simulation: new WrongSimulation());
        var proposal = await service.GenerateAsync(id, team, "Add !hello", default);
        Assert.Throws<StudioValidationException>(() => service.Apply(id, proposal.Id, true, false, default));
        Assert.Empty(fixture.Store.Get(id)!.Commands);
        var failed = service.List(id).Single();
        Assert.Equal("Failed", failed.Status);
        var evidence = failed.Evidence[0];
        Assert.False(evidence.Passed);
        Assert.Equal("Responded: Bonjour", evidence.Expected);
        Assert.Equal("NoMatch", evidence.Actual);
        Assert.Contains("!hello", failed.Error);
    }

    [Fact]
    public async Task IncompleteSimulationDoesNotApplyTheProposal()
    {
        using var fixture = new TwitchFixture();
        var id = Guid.NewGuid();
        fixture.Store.Save(new(id, "Test Bot", "samplechannel", []));
        var provider = new FakeProvider((request, _) => Task.FromResult(
            request.ResponseSchema?.GetProperty("properties").TryGetProperty("summary", out var ignored) == true
                ? """{"summary":"Plan","unsupported":[]}"""
                : """{"unsupported":[],"operations":[{"action":"add","name":"!hello","response":"Bonjour","cooldownSeconds":null,"enabled":true,"moderatorOnly":false}]}"""));
        var (service, team) = CreateProposalService(fixture, provider, simulation: new EmptySimulation());
        var proposal = await service.GenerateAsync(id, team, "Add !hello", default);
        Assert.Throws<StudioValidationException>(() => service.Apply(id, proposal.Id, true, false, default));
        Assert.Empty(fixture.Store.Get(id)!.Commands);
        var failed = Assert.Single(service.List(id));
        Assert.Equal("Failed", failed.Status);
        Assert.Empty(failed.Evidence);
        Assert.Contains("incomplete results", failed.Error);
    }

    [Fact]
    public async Task ExplicitRandomRequirementIsNotPresentedAsSupported()
    {
        using var fixture = new TwitchFixture();
        var id = Guid.NewGuid();
        fixture.Store.Save(new(id, "Test Bot", "samplechannel", []));
        var provider = new FakeProvider((request, _) => Task.FromResult(
            request.ResponseSchema?.GetProperty("properties").TryGetProperty("summary", out var ignored) == true
                ? """{"summary":"Plan","unsupported":[]}"""
                : """{"unsupported":[],"operations":[{"action":"add","name":"!dice","response":"Six","cooldownSeconds":null,"enabled":true,"moderatorOnly":false}]}"""));
        var (service, team) = CreateProposalService(fixture, provider);
        var proposal = await service.GenerateAsync(id, team, "Ajoute !dice qui produit un nombre aléatoire", default);
        Assert.Equal("Unsupported", proposal.Status);
        Assert.Contains(proposal.Unsupported, item => item.Contains("Random"));
        Assert.Throws<StudioValidationException>(() => service.Apply(id, proposal.Id, true, false, default));
        Assert.Empty(fixture.Store.Get(id)!.Commands);
    }

    [Fact]
    public async Task DeletionRequiresADeletionRequestForThatCommandAndSeparateApproval()
    {
        using var fixture = new TwitchFixture();
        var id = Guid.NewGuid();
        fixture.Store.Save(new(id, "Test Bot", "samplechannel", [new("!one", "One"), new("!two", "Two")]));
        var provider = new FakeProvider((request, _) => Task.FromResult(
            request.ResponseSchema?.GetProperty("properties").TryGetProperty("summary", out var ignored) == true
                ? """{"summary":"Plan","unsupported":[]}"""
                : """{"unsupported":[],"operations":[{"action":"delete","name":"!two","response":null,"cooldownSeconds":null,"enabled":null,"moderatorOnly":null}]}"""));
        var (service, team) = CreateProposalService(fixture, provider);
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            service.GenerateAsync(id, team, "Supprime !one et modifie !two", default));
        var proposal = await service.GenerateAsync(id, team, "Supprime !two", default);
        Assert.Throws<StudioValidationException>(() => service.Apply(id, proposal.Id, true, false, default));
        Assert.Equal(2, fixture.Store.Get(id)!.Commands.Count);
        var result = service.Apply(id, proposal.Id, true, true, default);
        Assert.All(result.Evidence, item => Assert.True(item.Passed));
        Assert.Equal("!one", Assert.Single(fixture.Store.Get(id)!.Commands).Name);
    }

    [Fact]
    [Trait("Category", "RealOllama")]
    public async Task RealOllamaProposesAndEvolvesTheSameOfflineBot()
    {
        var directory = Environment.GetEnvironmentVariable("AIPLATFORM_TWITCH_REAL_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        using var fixture = new TwitchFixture(directory);
        var resumeBot = Environment.GetEnvironmentVariable("AIPLATFORM_TWITCH_REAL_BOT");
        var resumeTeam = Environment.GetEnvironmentVariable("AIPLATFORM_TWITCH_REAL_TEAM");
        var id = resumeBot is null ? Guid.NewGuid() : Guid.Parse(resumeBot);
        if (resumeBot is null)
            fixture.Store.Save(new(id, "Temporary Test Bot", "samplechannel", []));
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var options = new OllamaOptions { KeepAliveSeconds = 0, RequestTimeoutSeconds = 180 };
        using var provider = new OllamaProvider(client, options);
        var (service, team) = CreateProposalService(fixture, provider,
            existingTeam: resumeTeam is null ? null : Guid.Parse(resumeTeam),
            catalog: new OllamaModelCatalog(client, options));
        var savedFirst = resumeBot is null ? null : service.List(id).FirstOrDefault(item => item.Status == "Applied");
        var first = savedFirst ?? await service.GenerateAsync(id, team,
            "Ajoute une commande !hello qui répond Bonjour ! et une commande !secret réservée aux modérateurs.", default);
        Assert.Empty(first.Unsupported);
        var applied = savedFirst is null ? service.Apply(id, first.Id, true, false, default) : first;
        Assert.All(applied.Evidence, item => Assert.True(item.Passed));
        Assert.Contains(applied.Evidence, item => item.Command == "!hello" && item.Actual == "Responded: Bonjour !");
        Assert.Contains(applied.Evidence, item => item.Command == "!secret" && item.Actual == "PermissionDenied");
        Assert.Contains(applied.Evidence, item => item.Command == "!secret" && item.Actual.StartsWith("Responded: "));
        var beforeSecond = fixture.Store.Get(id)!.Commands.Single(command => command.Name == "!secret");
        var second = await service.GenerateAsync(id, team, "Ajoute un cooldown de 10 secondes à !hello.", default);
        Assert.Equal("Proposed", second.Status);
        var appliedSecond = service.Apply(id, second.Id, true, false, default);
        Assert.All(appliedSecond.Evidence, item => Assert.True(item.Passed));
        Assert.Contains(appliedSecond.Evidence, item => item.Command == "!hello" && item.Actual == "Cooldown");
        Assert.Equal(10, fixture.Store.Get(id)!.Commands.Single(command => command.Name == "!hello").CooldownSeconds);
        Assert.Equal(beforeSecond, fixture.Store.Get(id)!.Commands.Single(command => command.Name == "!secret"));
        var (restarted, _) = CreateProposalService(fixture, provider, existingTeam: team,
            catalog: new OllamaModelCatalog(client, options));
        var history = restarted.List(id);
        Assert.True(history.Count >= 2);
        Assert.Equal("Outdated", history.Single(item => item.Id == first.Id).Status);
        Assert.Equal("Applied", history.Single(item => item.Id == second.Id).Status);
        Assert.Equal(0, fixture.Vault.ReadCount);
        Assert.Equal(0, fixture.Chat.Connections);
        Assert.Empty(fixture.Chat.SentMessages);
        Console.WriteLine(JsonSerializer.Serialize(new { botId = id, first, applied, second, appliedSecond }));
    }

    private static (TwitchCommandProposalService Service, Guid Team) CreateProposalService(
        TwitchFixture fixture, ILLMProvider provider, Guid? existingTeam = null, ITwitchSimulationService? simulation = null,
        IOllamaModelCatalog? catalog = null)
    {
        var agents = new CustomAgentStore(fixture.Options);
        var teamId = existingTeam ?? Guid.NewGuid();
        if (existingTeam is null)
        {
            var now = DateTimeOffset.UtcNow;
            var planner = new CustomAgentProfile(Guid.NewGuid(), "Planner", "", "Plan.", "qwen3:4b", now, now);
            var developer = new CustomAgentProfile(Guid.NewGuid(), "Developer", "", "Implement.", "qwen2.5-coder:7b", now, now);
            var reviewer = new CustomAgentProfile(Guid.NewGuid(), "Reviewer", "", "Review.", "qwen3:4b", now, now);
            agents.AddAgent(planner);
            agents.AddAgent(developer);
            agents.AddAgent(reviewer);
            agents.SaveTeam(new(teamId, "Team", [new(planner.Id, "Planner", 0),
                new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)], now, now));
        }
        return (new TwitchCommandProposalService(fixture.Store, fixture.Service,
            simulation ?? new TwitchSimulationService(fixture.Store), agents,
            new AgentRegistry(TestSupport.Configuration()), catalog ?? new FakeOllamaModelCatalog(), provider,
            fixture.Options, TimeProvider.System), teamId);
    }

    private sealed class FailingSimulation : ITwitchSimulationService
    {
        public TwitchSimulationResult Simulate(Guid botId, IReadOnlyList<TwitchSimulationEvent> events,
            CancellationToken cancellationToken) => throw new StudioValidationException("Simulation failed.");
    }

    private sealed class WrongSimulation : ITwitchSimulationService
    {
        public TwitchSimulationResult Simulate(Guid botId, IReadOnlyList<TwitchSimulationEvent> events,
            CancellationToken cancellationToken) =>
            new(botId, events.Select(item => new TwitchSimulationStep(item, "NoMatch", null, 0)).ToArray());
    }

    private sealed class EmptySimulation : ITwitchSimulationService
    {
        public TwitchSimulationResult Simulate(Guid botId, IReadOnlyList<TwitchSimulationEvent> events,
            CancellationToken cancellationToken) => new(botId, []);
    }

    [Fact]
    public async Task ConfiguredModeratorCommandIsEnforcedByLiveHandler()
    {
        using var fixture = new TwitchFixture();
        var bot = fixture.Service.Create("Studio Bot", "samplechannel");
        fixture.Service.Update(bot.Configuration.Id, "Studio Bot", "samplechannel",
            [new("!secret", "Secret", 10, ModeratorOnly: true)]);
        fixture.Vault.Save(bot.Configuration.Id, fixture.Credentials);
        await fixture.Service.StartAsync(bot.Configuration.Id, default);
        await fixture.Chat.DeliverAsync(new("1", "viewer", "!secret"));
        Assert.Empty(fixture.Chat.SentMessages);
        await fixture.Chat.DeliverAsync(new("2", "viewer", "!secret", IsModerator: true));
        Assert.Equal("Secret", Assert.Single(fixture.Chat.SentMessages).Message);
        await fixture.Service.StopAsync(bot.Configuration.Id, default);
    }

    [Theory]
    [InlineData("""{"badges":[{"set_id":"moderator"}]}""", true)]
    [InlineData("""{"badges":[{"set_id":"broadcaster"}]}""", true)]
    [InlineData("""{"badges":[{"set_id":"subscriber"}]}""", false)]
    [InlineData("""{}""", false)]
    public void EventSubRolesComeFromTwitchBadges(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, TwitchChatClient.HasModeratorBadge(document.RootElement));
    }

    [Fact]
    public async Task TokenValidationFailureIsReportedWithoutStartingAChatSession()
    {
        using var fixture = new TwitchFixture();
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        fixture.Vault.Save(bot.Configuration.Id, fixture.Credentials);
        fixture.OAuth.ValidateFailure = new("invalid_token", "Twitch authorization is no longer valid.", 401);
        fixture.OAuth.RefreshFailure = new("oauth_refresh_failed", "Twitch authorization expired.", 400);

        var failure = await Assert.ThrowsAsync<TwitchIntegrationException>(
            () => fixture.Service.StartAsync(bot.Configuration.Id, default));
        Assert.Equal("authorization_required", failure.Code);
        Assert.Equal(0, fixture.Chat.Connections);
        Assert.Equal("Error", fixture.Service.Get(bot.Configuration.Id)!.State);
        Assert.False(fixture.Service.Get(bot.Configuration.Id)!.Authorized);
    }

    [Fact]
    public async Task RefreshesAndPersistsTheRotatedRefreshTokenBeforeConnecting()
    {
        using var fixture = new TwitchFixture();
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        fixture.Vault.Save(bot.Configuration.Id, fixture.Credentials);
        fixture.OAuth.ValidateFailureOnce = true;

        var started = await fixture.Service.StartAsync(bot.Configuration.Id, default);
        Assert.Equal("Connected", started.State);
        Assert.Equal("new-access-secret", fixture.Vault.Read(bot.Configuration.Id)!.AccessToken);
        Assert.Equal("new-refresh-secret", fixture.Vault.Read(bot.Configuration.Id)!.RefreshToken);
        await fixture.Service.StopAsync(bot.Configuration.Id, default);
    }

    [Fact]
    public async Task UnexpectedEventSubDisconnectReconnectsUntilExplicitStop()
    {
        using var fixture = new TwitchFixture();
        fixture.Chat.DropFirstConnection = true;
        var bot = fixture.Service.Create("Studio Bot", "SampleChannel");
        fixture.Vault.Save(bot.Configuration.Id, fixture.Credentials);

        await fixture.Service.StartAsync(bot.Configuration.Id, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (fixture.Chat.Connections < 2 || fixture.Service.Get(bot.Configuration.Id)!.State != "Connected")
            await Task.Delay(20, timeout.Token);

        Assert.Equal(2, fixture.Chat.Connections);
        await fixture.Service.StopAsync(bot.Configuration.Id, default);
    }

    [Fact]
    public async Task OAuthClientUsesDeviceFlowAndRejectsUnexpectedVerificationUrls()
    {
        Uri? capturedUri = null;
        string? capturedBody = null;
        using var client = new HttpClient(new FakeHandler((request, _) =>
        {
            capturedUri = request.RequestUri;
            return CaptureBodyAsync(request, body =>
            {
                capturedBody = body;
                return Task.FromResult(Json(HttpStatusCode.OK, """
                {"device_code":"device-secret","user_code":"ABCD-1234","verification_uri":"https://www.twitch.tv/activate","expires_in":600,"interval":5}
                """));
            });
        }));
        var oauth = new TwitchOAuthClient(client);
        var authorization = await oauth.BeginDeviceCodeAsync(ClientId, default);
        Assert.Equal("ABCD-1234", authorization.UserCode);
        Assert.Equal("https://id.twitch.tv/oauth2/device", capturedUri!.AbsoluteUri);
        Assert.Contains("user%3Aread%3Achat", capturedBody);
        Assert.Contains("user%3Awrite%3Achat", capturedBody);

        using var invalidClient = new HttpClient(new FakeHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """
                {"device_code":"device","user_code":"ABCD","verification_uri":"https://example.com/","expires_in":600,"interval":5}
                """))));
        var invalid = await Assert.ThrowsAsync<TwitchIntegrationException>(
            () => new TwitchOAuthClient(invalidClient).BeginDeviceCodeAsync(ClientId, default));
        Assert.Equal("oauth_device_invalid", invalid.Code);
    }

    [Fact]
    public async Task OAuthNetworkFailureReturnsASafeRetryableError()
    {
        using var client = new HttpClient(new FakeHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("private transport detail"))));
        var failure = await Assert.ThrowsAsync<TwitchIntegrationException>(
            () => new TwitchOAuthClient(client).BeginDeviceCodeAsync(ClientId, default));
        Assert.Equal(503, failure.StatusCode);
        Assert.DoesNotContain("private transport detail", failure.Message);
    }

    [Fact]
    public async Task DevicePollingRecognizesPendingAndNeverReturnsTokenValuesInItsStatus()
    {
        string? body = null;
        using var client = new HttpClient(new FakeHandler(async (request, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.BadRequest, """{"message":"authorization_pending"}""");
        }));
        var result = await new TwitchOAuthClient(client).PollDeviceCodeAsync(ClientId, "device-secret", default);
        Assert.Equal(TwitchDevicePollStatus.Pending, result.Status);
        Assert.Null(result.Tokens);
        Assert.Contains("device-secret", body);
        Assert.Contains("user%3Aread%3Achat", body);
        Assert.Contains("user%3Awrite%3Achat", body);
    }

    [Fact]
    public async Task ChatRepliesUseHelixAndRateLimitErrorsRemainExplicit()
    {
        string? body = null;
        AuthenticationHeaderValue? authorization = null;
        using var client = new HttpClient(new FakeHandler(async (request, _) =>
        {
            authorization = request.Headers.Authorization;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"data":[{"is_sent":true}]}""");
        }));
        using var chat = new TwitchChatClient(client, TimeProvider.System);
        await chat.SendMessageAsync(ClientId, "access-secret", "broadcaster-1", "sender-1",
            "parent-1", "Salut le chat !", default);
        Assert.Equal("Bearer", authorization!.Scheme);
        Assert.Equal("access-secret", authorization.Parameter);
        using (var document = JsonDocument.Parse(body!))
        {
            Assert.Equal("broadcaster-1", document.RootElement.GetProperty("broadcaster_id").GetString());
            Assert.Equal("parent-1", document.RootElement.GetProperty("reply_parent_message_id").GetString());
            Assert.Equal("Salut le chat !", document.RootElement.GetProperty("message").GetString());
        }

        using var limitedClient = new HttpClient(new FakeHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.TooManyRequests, """{"message":"rate limited"}"""))));
        using var limitedChat = new TwitchChatClient(limitedClient, TimeProvider.System);
        var failure = await Assert.ThrowsAsync<TwitchIntegrationException>(() =>
            limitedChat.SendMessageAsync(ClientId, "access-secret", "broadcaster-1", "sender-1",
                "parent-1", "Salut le chat !", default));
        Assert.Equal(429, failure.StatusCode);
        Assert.DoesNotContain("access-secret", failure.ToString());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static async Task<HttpResponseMessage> CaptureBodyAsync(
        HttpRequestMessage request, Func<string, Task<HttpResponseMessage>> complete) =>
        await complete(await request.Content!.ReadAsStringAsync());

    private sealed class FakeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class TwitchFixture : IDisposable
    {
        public StudioOptions Options { get; }
        private readonly bool preserve;
        public TwitchBotStore Store { get; }
        public FakeVault Vault { get; } = new();
        public FakeOAuth OAuth { get; } = new();
        public FakeChat Chat { get; } = new();
        public TwitchBotService Service { get; }
        public TwitchStoredCredentials Credentials { get; } = new(
            ClientId, "user-1", "sampleuser", "access-secret", "refresh-secret");

        public TwitchFixture(string? directory = null)
        {
            preserve = directory is not null;
            Options = new() { DataDirectory = directory ??
                Path.Combine(Path.GetTempPath(), "AIPlatform-twitch-tests-" + Guid.NewGuid().ToString("N")) };
            Store = new(Options);
            Store.SetClientId(ClientId);
            Service = new(Store, Vault, OAuth, Chat, TimeProvider.System);
        }

        public void Dispose()
        {
            if (!preserve && Directory.Exists(Options.DataDirectory))
                Directory.Delete(Options.DataDirectory, recursive: true);
        }
    }

    private sealed class FakeVault : ITwitchTokenVault
    {
        private readonly Dictionary<Guid, TwitchStoredCredentials> credentials = [];
        public int ReadCount { get; private set; }
        public bool IsAvailable => true;
        public TwitchStoredCredentials? Read(Guid botId)
        {
            ReadCount++;
            return credentials.GetValueOrDefault(botId);
        }
        public void Save(Guid botId, TwitchStoredCredentials value) => credentials[botId] = value;
        public void Delete(Guid botId) => credentials.Remove(botId);
    }

    private sealed class FakeOAuth : ITwitchOAuthClient
    {
        public TwitchIntegrationException? ValidateFailure { get; set; }
        public TwitchIntegrationException? RefreshFailure { get; set; }
        public bool ValidateFailureOnce { get; set; }
        public Task<TwitchDeviceCode> BeginDeviceCodeAsync(string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(new TwitchDeviceCode("device-secret", "CODE-123",
                "https://www.twitch.tv/activate", 600, 1));
        public Task<TwitchDevicePoll> PollDeviceCodeAsync(string clientId, string deviceCode, CancellationToken cancellationToken) =>
            Task.FromResult(new TwitchDevicePoll(TwitchDevicePollStatus.Authorized,
                new TwitchTokenGrant("access-secret", "refresh-secret", 3600)));
        public Task<TwitchTokenGrant> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken) =>
            RefreshFailure is { } failure ? Task.FromException<TwitchTokenGrant>(failure)
                : Task.FromResult(new TwitchTokenGrant("new-access-secret", "new-refresh-secret", 3600));
        public Task<TwitchValidatedIdentity> ValidateAsync(string accessToken, CancellationToken cancellationToken)
        {
            if (ValidateFailureOnce)
            {
                ValidateFailureOnce = false;
                return Task.FromException<TwitchValidatedIdentity>(
                    new TwitchIntegrationException("invalid_token", "Twitch authorization is no longer valid.", 401));
            }
            return ValidateFailure is { } failure ? Task.FromException<TwitchValidatedIdentity>(failure)
                : Task.FromResult(new TwitchValidatedIdentity(ClientId, "user-1", "sampleuser",
                    ["user:read:chat", "user:write:chat"], 3600));
        }
    }

    private sealed class FakeChat : ITwitchChatClient
    {
        private Func<TwitchChatMessage, CancellationToken, Task>? onMessage;
        private CancellationToken sessionToken;
        private int connections;
        public int Connections => Volatile.Read(ref connections);
        public int CompletedSessions { get; private set; }
        public bool DropFirstConnection { get; set; }
        public ConcurrentQueue<(string Message, string ParentId)> SentMessages { get; } = new();

        public Task<TwitchChannel> FindChannelAsync(string clientId, string accessToken, string login, CancellationToken cancellationToken) =>
            Task.FromResult(new TwitchChannel("broadcaster-1", login));

        public async Task RunEventSubAsync(string clientId, string accessToken, string broadcasterId, string userId,
            Func<TwitchChatMessage, CancellationToken, Task> messageHandler, Action connected,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref connections);
            onMessage = messageHandler;
            sessionToken = cancellationToken;
            connected();
            if (DropFirstConnection && Connections == 1)
                return;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { CompletedSessions++; }
        }

        public Task SendMessageAsync(string clientId, string accessToken, string broadcasterId, string senderId,
            string replyToMessageId, string message, CancellationToken cancellationToken)
        {
            SentMessages.Enqueue((message, replyToMessageId));
            return Task.CompletedTask;
        }

        public Task DeliverAsync(TwitchChatMessage message) =>
            onMessage?.Invoke(message, sessionToken) ?? throw new InvalidOperationException("No chat session is active.");
    }
}
