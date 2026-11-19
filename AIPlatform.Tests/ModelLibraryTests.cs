using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIPlatform.Tests;

public sealed class ModelLibraryTests
{
    private const long GiB = 1073741824;
    private static readonly ModelMetadata Metadata = new("coder:7b", "qwen2", "7B", "Q4_K_M",
        4 * GiB, 7_000_000_000, "qwen2", 32768, 32, 8, 128, 128, true, "Apache-2.0");
    private static HardwareSnapshot Hardware(long ram = 8 * GiB, long vram = 8 * GiB) =>
        new(16 * GiB, ram, [new("Simulated GPU", 8 * GiB, vram)], 100 * GiB, []);

    [Fact]
    public void EstimatesSeparateRamVramKvAndUnknownArchitectures()
    {
        var estimate = ModelMemoryEstimator.Estimate(Metadata, Hardware(), 4096);
        Assert.Equal("Compatible", estimate.Status);
        Assert.Equal(512 * 1048576L, estimate.KvBytes);
        Assert.Equal("GPU", estimate.Placement);
        Assert.Equal("Compatible", ModelMemoryEstimator.Estimate(
            Metadata with { Architecture = "qwen3" }, Hardware(), 4096).Status);
        Assert.Equal("Insufficient", ModelMemoryEstimator.Estimate(
            Metadata with { InstalledBytes = 6 * GiB }, Hardware(4 * GiB, 4 * GiB), 4096).Status);
        Assert.Equal("Unknown", ModelMemoryEstimator.Estimate(
            Metadata with { Architecture = "qwen35" }, Hardware(), 4096).Status);
        Assert.Equal("Unknown", ModelMemoryEstimator.Estimate(
            Metadata, Hardware() with { AvailableRamBytes = null }, 4096).Status);
        Assert.Equal("Insufficient", ModelMemoryEstimator.Estimate(Metadata, Hardware(), 65536).Status);
        var larger = ModelMemoryEstimator.Estimate(Metadata, Hardware(), 16384);
        Assert.Equal(4 * estimate.KvBytes, larger.KvBytes);
    }

    [Fact]
    public void LoadedAllocationsAreNotDoubleCountedButContextChangesRequireReassessment()
    {
        var loaded = new LoadedOllamaModel(Metadata.Name, 5 * GiB, 5 * GiB, 4096);
        var result = ModelMemoryEstimator.Estimate(Metadata, Hardware(3 * GiB, GiB), 4096, loaded);
        Assert.Equal("Compatible", result.Status);
        Assert.Equal("Loaded", result.Placement);
        Assert.Equal("Unknown", ModelMemoryEstimator.Estimate(Metadata, Hardware(), 8192, loaded).Status);
    }

    [Theory]
    [InlineData("https://evil/model")]
    [InlineData("../model:7b")]
    [InlineData("model:cloud")]
    [InlineData("host:1234/model")]
    [InlineData("model;command")]
    public void RejectsUrlsCloudTagsAndUnsafeIdentifiers(string name) =>
        Assert.Throws<StudioValidationException>(() => ModelLibraryService.ValidateName(name));

    [Fact]
    public async Task DiscoversMetadataCatalogueAndLoadedModelsWithoutDownloading()
    {
        await using var fixture = new Fixture();
        var snapshot = await fixture.Service.SnapshotAsync(4096, CancellationToken.None);
        Assert.Null(snapshot.OllamaError);
        var model = Assert.Single(snapshot.Models, item => item.Installed == true);
        Assert.Equal("coder:7b", model.Catalogue.Name);
        Assert.Equal("Q4_K_M", model.Metadata!.Quantization);
        Assert.Equal(32768, model.Metadata.ContextLength);
        Assert.Equal("Compatible", model.Compatibility.Status);
        Assert.Contains(snapshot.Models, item => item.Catalogue.Family == "Qwen3-Coder-Next");
        Assert.DoesNotContain(fixture.Paths, path => path.Contains("/api/pull") || path.Contains("/api/chat"));
        Assert.Empty(fixture.Service.Downloads());
    }

    [Fact]
    public async Task UnavailableOllamaIsExplicitAndDoesNotInventInstallation()
    {
        await using var fixture = new Fixture(unavailable: true);
        var snapshot = await fixture.Service.SnapshotAsync(4096, CancellationToken.None);
        Assert.NotNull(snapshot.OllamaError);
        Assert.All(snapshot.Models, model => Assert.Equal("Unknown", model.Compatibility.Status));
        Assert.All(snapshot.Models, model => Assert.Null(model.Metadata));
        Assert.All(snapshot.Models, model => Assert.Null(model.Installed));
    }

    [Fact]
    public async Task VerifiesExactRegistryTagsWithoutPullAndRejectsAbsentTags()
    {
        await using var fixture = new Fixture();
        Assert.Equal(4096, await fixture.Service.VerifyAsync("manual:latest", CancellationToken.None));
        Assert.Contains("/v2/library/manual/manifests/latest", fixture.Paths);
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Service.VerifyAsync("missing:latest", CancellationToken.None));
        Assert.DoesNotContain("/api/pull", fixture.Paths);
        Assert.Throws<StudioValidationException>(() => fixture.Service.StartDownload("manual:latest", false));
    }

    [Fact]
    public async Task PullTracksActualProgressRefreshesInventoryAndPersistsSuccess()
    {
        var pipe = new Pipe();
        await using var fixture = new Fixture(pull: _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(pipe.Reader.AsStream()) }));
        var job = fixture.Service.StartDownload("coder:7b", true);
        await WaitUntil(() => fixture.Service.Downloads().Single().Status == "Running");
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"pulling layer\",\"total\":100,\"completed\":42}\n"));
        await WaitUntil(() => fixture.Service.Downloads().Single().Completed == 42);
        Assert.Equal(100, fixture.Service.Downloads().Single().Total);
        Assert.Throws<ModelOperationConflictException>(() => fixture.Service.StartDownload("other:7b", true));
        await Assert.ThrowsAsync<ModelOperationConflictException>(() =>
            fixture.Service.DeleteAsync("other:7b", true, CancellationToken.None));
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"success\"}\n"));
        await pipe.Writer.CompleteAsync();
        await WaitUntil(() => fixture.Service.Downloads().Single().Status == "Completed");
        Assert.Contains("/api/tags", fixture.Paths);
        using var persisted = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Directory, "model-downloads.json")));
        Assert.Equal(job.Id, persisted.RootElement[0].GetProperty("id").GetGuid());
        Assert.False(fixture.Service.CancelDownload(job.Id));
    }

    [Fact]
    public async Task PullCanBeCancelledAndInterruptedHistoryRequiresExplicitRetry()
    {
        await using var fixture = new Fixture(pull: async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        });
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(fixture.Directory, "model-downloads.json"), JsonSerializer.Serialize(
            new[] { new ModelDownload(id, "old:7b", "Running", "pulling", 100, 25, DateTimeOffset.UtcNow) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("Interrupted", Assert.Single(fixture.Service.Downloads()).Status);
        Assert.DoesNotContain("/api/pull", fixture.Paths);
        var job = fixture.Service.StartDownload("coder:7b", true);
        await WaitUntil(() => fixture.Paths.Contains("/api/pull"));
        Assert.True(fixture.Service.CancelDownload(job.Id));
        await WaitUntil(() => fixture.Service.Downloads().First().Status == "Cancelled");
    }

    [Fact]
    public async Task PullErrorsCannotBecomeSuccessAndDoNotLoadWeights()
    {
        await using var fixture = new Fixture(pull: _ => Task.FromResult(StubHttpHandler.Json(
            "{\"error\":\"disk failure\"}\n")));
        fixture.Service.StartDownload("coder:7b", true);
        await WaitUntil(() => fixture.Service.Downloads().First().Status == "Failed");
        Assert.Contains("disk failure", fixture.Service.Downloads().First().Error);
        Assert.DoesNotContain("/api/chat", fixture.Paths);
    }

    [Fact]
    public async Task DeletionRequiresConfirmationReassignmentAndInactiveOperations()
    {
        await using var fixture = new Fixture();
        var agent = new CustomAgentProfile(Guid.NewGuid(), "Configured", "", "Work", "coder:7b",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        fixture.Store.AddAgent(agent);
        Assert.Single(fixture.Service.Dependencies("coder:7b"));
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Service.DeleteAsync("coder:7b", false, CancellationToken.None));
        await Assert.ThrowsAsync<ModelOperationConflictException>(() =>
            fixture.Service.DeleteAsync("coder:7b", true, CancellationToken.None));
        Assert.DoesNotContain("/api/delete", fixture.Paths);
        fixture.Store.DeleteAgent(agent.Id);
        await fixture.Service.DeleteAsync("coder:7b", true, CancellationToken.None);
        Assert.Contains("/api/delete", fixture.Paths);
    }

    [Theory]
    [InlineData("src/App.tsx TS18004", "Developer", "Debugging")]
    [InlineData("Construire React TypeScript", "Developer", "ReactTypeScript")]
    [InlineData("ASP.NET Core C#", "Developer", "DotNet")]
    [InlineData("Vitest tests failed", "Developer", "Tests")]
    [InlineData("Écrire un plan", "Planner", "Planning")]
    [InlineData("Review", "Reviewer", "Review")]
    [InlineData("README documentation", "Developer", "Documentation")]
    public void ClassifiesWithoutLlm(string task, string role, string expected) =>
        Assert.Equal(expected, LocalModelRouter.Classify(task, role));

    [Fact]
    public void RankingIsDeterministicSpecializedAndUsesOnlyEnoughQualityObservations()
    {
        LibraryModel Model(string name, string[] specializations) => new(
            new(name, "Test", null, specializations, "https://ollama.com/library/test"), true, Metadata with { Name = name },
            null, ModelMemoryEstimator.Estimate(Metadata, Hardware(), 4096), []);
        var models = new[] { Model("general:7b", ["General"]), Model("coder:7b", ["Code"]) };
        var result = LocalModelRouter.Rank("DotNet", 4096, models, []);
        Assert.Equal("coder:7b", result.Model);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(LocalModelRouter.Rank("DotNet", 4096, models, [])));
        var failure = new CustomAgentModelAttempt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Developer",
            "coder:7b", "test", DateTimeOffset.UtcNow, true, ["coder:7b"], "Failed", "Generation") { TaskCategory = "DotNet" };
        Assert.Equal(result.Alternatives[0].Score, LocalModelRouter.Rank("DotNet", 4096, models, [failure]).Alternatives[0].Score);
        var qualityFailures = Enumerable.Range(0, 4).Select(_ => failure with { Id = Guid.NewGuid() }).ToArray();
        Assert.True(LocalModelRouter.Rank("DotNet", 4096, models, qualityFailures).Alternatives[0].Score < result.Alternatives[0].Score);
        var infrastructure = qualityFailures.Select(item => item with { FailureKind = "Infrastructure" }).ToArray();
        Assert.Equal(result.Alternatives[0].Score, LocalModelRouter.Rank("DotNet", 4096, models, infrastructure).Alternatives[0].Score);
        var compileOnly = qualityFailures.Select(item => item with
            { FailureKind = null, Outcome = "Succeeded", CompilationStatus = "Passed" }).ToArray();
        Assert.Equal(result.Alternatives[0].Score, LocalModelRouter.Rank("DotNet", 4096, models, compileOnly).Alternatives[0].Score);
        Assert.True(LocalModelRouter.Rank("DotNet", 4096, models,
            compileOnly.Select(item => item with { TestsStatus = "Passed" }).ToArray()).Alternatives[0].Score >
            result.Alternatives[0].Score);
        Assert.Throws<StudioValidationException>(() => LocalModelRouter.Rank("DotNet", 4096,
            [models[0] with { Compatibility = models[0].Compatibility with { Status = "Insufficient" } }], []));
    }

    [Theory]
    [InlineData("ReactTypeScript")]
    [InlineData("DotNet")]
    [InlineData("Project")]
    public void CodingRouterPrefersInstalledCodeModelOrStopsRatherThanReusingResidentPlanner(string category)
    {
        var planner = new LibraryModel(new("planner:4b", "Planner", null, ["Planning"], ""),
            true, Metadata with { Name = "planner:4b" }, new("planner:4b", 4 * GiB, 4 * GiB, 8192),
            new("Compatible", "Already loaded", 8192), []);
        var coder = new LibraryModel(new("coder:7b", "Coder", null, ["Code"], ""),
            true, Metadata, null, new("Borderline", "Hybrid possible", 8192), []);
        Assert.Equal("coder:7b", LocalModelRouter.Rank(category, 8192, [planner, coder], []).Model);
        var error = Assert.Throws<StudioValidationException>(() => LocalModelRouter.Rank(category, 8192,
            [planner, coder with { Compatibility = new("Insufficient", "Occupied GPU", 8192) }], []));
        Assert.Contains("ne pas remplacer silencieusement", error.Message);
        Assert.Equal("planner:4b", LocalModelRouter.Rank("Planning", 8192, [planner, coder], []).Model);
        Assert.Equal("planner:4b", LocalModelRouter.Rank(category, 8192,
            [planner, coder with { Installed = false }], []).Model);
    }

    [Fact]
    public async Task StatisticsResetLeavesAgentsTeamsAndHistoryUntouched()
    {
        await using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var agent = new CustomAgentProfile(Guid.NewGuid(), "Developer", "", "Work", "coder:7b", now, now);
        var planner = agent with { Id = Guid.NewGuid(), Name = "Planner" };
        var reviewer = agent with { Id = Guid.NewGuid(), Name = "Reviewer" };
        var team = new CustomAgentTeam(Guid.NewGuid(), "Preserved team",
            [new(planner.Id, "Planner", 0), new(agent.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)], now, now);
        fixture.Store.AddAgent(agent);
        fixture.Store.AddAgent(planner);
        fixture.Store.AddAgent(reviewer);
        fixture.Store.SaveTeam(team);
        var runId = Guid.NewGuid();
        var attempt = new CustomAgentModelAttempt(Guid.NewGuid(), runId, agent.Id, "Developer",
            "coder:7b", "Manual", now, false, ["coder:7b"], "Failed", "Generation") { TaskCategory = "Debugging" };
        var run = new CustomAgentCollaborationRun(runId, team.Id, "TS18004", "Failed", null, null, now, now, [])
            { ModelAttempts = [attempt] };
        fixture.Store.AddCollaborationRun(run);
        var router = new LocalModelRouter(fixture.Service, fixture.Store, new() { DataDirectory = fixture.Directory });
        Assert.Single(router.Observations());
        var statePath = Path.Combine(fixture.Directory, "custom-agents", "state.json");
        var before = File.ReadAllBytes(statePath);
        router.ResetStatistics();
        Assert.Empty(router.Observations());
        Assert.Equal(before, File.ReadAllBytes(statePath));
        Assert.Equal(JsonSerializer.Serialize(run),
            JsonSerializer.Serialize(fixture.Store.GetCollaborationRun(team.Id, runId)));
        Assert.True(File.Exists(Path.Combine(fixture.Directory, "model-ranking-reset.json")));
    }

    [Fact]
    public async Task ExplicitSpecializationsPersistAcrossInstancesAndRestoreCatalogueDefaults()
    {
        await using var fixture = new Fixture();
        var original = await fixture.Service.SnapshotAsync(4096, default);
        Assert.Empty(original.Models.Single(model => model.Catalogue.Name == "coder:7b").Catalogue.Specializations);
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "model-routing-profiles.json")));
        var known = original.Models.First(model => model.Catalogue.Specializations.Contains("Code"));
        fixture.Service.SaveSpecializations("coder:7b", ["Code", "Tests"]);
        fixture.Service.SaveSpecializations(known.Catalogue.Name, ["General"]);
        await using var restarted = fixture.CreateService();
        var saved = await restarted.SnapshotAsync(4096, default);
        Assert.Equal(new[] { "Code", "Tests" }, saved.Models.Single(model => model.Catalogue.Name == "coder:7b").Catalogue.Specializations);
        Assert.Equal(new[] { "General" }, saved.Models.Single(model => model.Catalogue.Name == known.Catalogue.Name).Catalogue.Specializations);
        Assert.Equal("coder:7b", LocalModelRouter.Rank("DotNet", 4096, saved.Models, []).Model);
        restarted.SaveSpecializations("coder:7b", ["General"]);
        var general = (await restarted.SnapshotAsync(4096, default)).Models.Single(model => model.Installed == true);
        Assert.DoesNotContain("Code", general.Catalogue.Specializations);
        restarted.SaveSpecializations(known.Catalogue.Name, null);
        restarted.SaveSpecializations("coder:7b", null);
        var restored = await fixture.Service.SnapshotAsync(4096, default);
        Assert.Equal(known.Catalogue.Specializations, restored.Models.Single(model => model.Catalogue.Name == known.Catalogue.Name).Catalogue.Specializations);
        Assert.Empty(restored.Models.Single(model => model.Installed == true).Catalogue.Specializations);
        var before = File.ReadAllBytes(Path.Combine(fixture.Directory, "model-routing-profiles.json"));
        Assert.Throws<StudioValidationException>(() => fixture.Service.SaveSpecializations("coder:7b", ["Code", "Code"]));
        Assert.Throws<StudioValidationException>(() => fixture.Service.SaveSpecializations("coder:7b", ["Invented"]));
        Assert.Throws<StudioValidationException>(() => fixture.Service.SaveSpecializations("../outside", ["Code"]));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Directory, "model-routing-profiles.json")));
    }

    [Fact]
    public async Task UnknownArchitectureRequiresExplicitObservedQualificationAndRechecksAvailableResources()
    {
        await using var fixture = new Fixture { Architecture = "future-architecture", DeclaredContext = 262144, ResidentContext = 65536 };
        fixture.Service.SaveSpecializations("coder:7b", ["Code"]);
        var before = await fixture.Service.SnapshotAsync(65536, default);
        Assert.Equal("Unknown", before.Models.Single(model => model.Installed == true).Compatibility.Status);
        Assert.Throws<StudioValidationException>(() => LocalModelRouter.Rank("DotNet", 65536, before.Models, []));
        await fixture.Service.GuardExecutionAsync("coder:7b", 65536, default);
        await Assert.ThrowsAsync<LlmContextException>(() => fixture.Service.GuardExecutionAsync("coder:7b", 65536, default, automatic: true));
        var profile = await fixture.Service.QualifyAsync("coder:7b", 65536, true, default);
        Assert.Equal(new[] { "Code" }, profile.Specializations);
        Assert.Equal(65536, profile.Qualification!.ContextTokens);
        fixture.ResidentContext = null;
        await using var restarted = fixture.CreateService();
        var qualified = await restarted.SnapshotAsync(65536, default);
        var model = qualified.Models.Single(item => item.Installed == true);
        Assert.Equal(profile.Qualification, model.RoutingProfile!.Qualification);
        Assert.Equal("Compatible", model.Compatibility.Status);
        Assert.Null(model.Compatibility.KvBytes);
        Assert.Equal("coder:7b", LocalModelRouter.Rank("DotNet", 65536, qualified.Models, []).Model);
        await restarted.GuardExecutionAsync("coder:7b", 65536, default, automatic: true);
        Assert.Equal("Unknown", (await restarted.SnapshotAsync(131072, default)).Models.Single(item => item.Installed == true).Compatibility.Status);
        fixture.Resources = Hardware(vram: GiB);
        await Assert.ThrowsAsync<LlmMemoryException>(() => restarted.GuardExecutionAsync("coder:7b", 65536, default));
        fixture.Resources = Hardware();
        fixture.Digest = new string('b', 64);
        await Assert.ThrowsAsync<LlmContextException>(() => restarted.GuardExecutionAsync("coder:7b", 65536, default, automatic: true));
        fixture.Digest = new string('a', 64);
        restarted.ClearQualification("coder:7b");
        Assert.Null((await fixture.Service.SnapshotAsync(4096, default)).Models.Single(item => item.Installed == true).RoutingProfile!.Qualification);
        Assert.DoesNotContain(fixture.Paths, path => path is "/api/chat" or "/api/generate" or "/api/pull" or "/api/delete");
    }

    [Theory]
    [InlineData("confirmation")]
    [InlineData("unloaded")]
    [InlineData("context")]
    [InlineData("digest")]
    [InlineData("resident-digest")]
    [InlineData("ram")]
    [InlineData("allocations")]
    public async Task RefusedQualificationDoesNotModifySavedProfile(string reason)
    {
        await using var fixture = new Fixture { Architecture = "unknown", ResidentContext = 8192 };
        fixture.Service.SaveSpecializations("coder:7b", ["Code"]);
        var path = Path.Combine(fixture.Directory, "model-routing-profiles.json");
        var before = File.ReadAllBytes(path);
        if (reason == "unloaded") fixture.ResidentContext = null;
        if (reason == "context") fixture.ResidentContext = 4096;
        if (reason == "digest") fixture.Digest = null;
        if (reason == "resident-digest") fixture.ResidentDigest = new string('b', 64);
        if (reason == "ram") fixture.Resources = Hardware(ram: 1073741824);
        if (reason == "allocations") fixture.ResidentSize = GiB;
        await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Service.QualifyAsync("coder:7b", 8192, reason != "confirmation", default));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.DoesNotContain(fixture.Paths, endpoint => endpoint is "/api/chat" or "/api/generate" or "/api/pull");
    }

    [Fact]
    public async Task ConcurrentProfileEditsMergeIndependentFieldsAcrossServiceInstances()
    {
        await using var fixture = new Fixture { Architecture = "future", ResidentContext = 8192 };
        await fixture.Service.QualifyAsync("coder:7b", 8192, true, default);
        await using var other = fixture.CreateService();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
        {
            if (index % 2 == 0) fixture.Service.SaveSpecializations("coder:7b", ["Code", "Tests"]);
            else other.ClearQualification("coder:7b");
        })));
        var model = (await fixture.Service.SnapshotAsync(8192, default)).Models.Single(item => item.Installed == true);
        Assert.Equal(new[] { "Code", "Tests" }, model.RoutingProfile!.Specializations);
        Assert.Null(model.RoutingProfile.Qualification);
    }

    [Fact]
    public void MeasuredQualificationCannotOverrideKnownInsufficiencyOrExtrapolateUnknownPlacements()
    {
        var metadata = Metadata with { Digest = new string('a', 64) };
        var proof = new ModelMemoryQualification(metadata.Digest, 65536, 5 * GiB, 4 * GiB, DateTimeOffset.UtcNow);
        Assert.Equal("Insufficient", ModelMemoryEstimator.Estimate(metadata with { InstalledBytes = 6 * GiB },
            Hardware(4 * GiB, 4 * GiB), 4096, qualification: proof).Status);
        metadata = metadata with { Architecture = "unknown", ContextLength = 131072 };
        Assert.Equal("Insufficient", ModelMemoryEstimator.Estimate(metadata, Hardware(ram: GiB),
            4096, qualification: proof).Status);
        Assert.Equal("Unknown", ModelMemoryEstimator.Estimate(metadata, Hardware() with { Gpus = [] },
            4096, qualification: proof).Status);
        var cpuProof = proof with { SizeVramBytes = 0 };
        Assert.Equal("Compatible", ModelMemoryEstimator.Estimate(metadata, Hardware() with { Gpus = [] },
            4096, qualification: cpuProof).Status);
        var loaded = new LoadedOllamaModel(metadata.Name, 5 * GiB, 4 * GiB, 4096) { Digest = metadata.Digest };
        Assert.Equal("Insufficient", ModelMemoryEstimator.Estimate(metadata, Hardware(ram: GiB),
            4096, loaded, qualification: proof).Status);
    }

    [Theory]
    [InlineData(65536)]
    [InlineData(262144)]
    public async Task LargeContextProfilesPersistWithoutEnlargingOtherProfiles(int context)
    {
        await using var fixture = new Fixture();
        var defaults = TestSupport.Configuration();
        var options = new StudioOptions { DataDirectory = fixture.Directory };
        var registry = new ConfigurableRegistry(defaults, options);
        var configuration = registry.GetConfiguration();
        var first = configuration.Models[0];
        registry.Save(configuration with { Models = configuration.Models.Select(model =>
            model.Id == first.Id ? model with { ContextSize = context } : model).ToArray() });
        var restored = new ConfigurableRegistry(defaults, options);
        Assert.Equal(context, restored.GetModel(first.Id).ContextSize);
        Assert.Equal(first.MaxOutputTokens, restored.GetModel(first.Id).MaxOutputTokens);
        Assert.Equal(configuration.Models.Skip(1), restored.Models.Skip(1));
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Directory, "agents.json"));
        Assert.Throws<StudioValidationException>(() => registry.Save(configuration with
        { Models = [first with { ContextSize = ModelContextLimits.Maximum + 1 }] }));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Directory, "agents.json")));
    }

    [Theory]
    [InlineData(65536)]
    [InlineData(262144)]
    public void LargerHardwareCanAdmitLargeDeclaredContextWithoutChangingDefaults(int context)
    {
        var resources = new HardwareSnapshot(64 * GiB, 60 * GiB,
            [new("Future simulated GPU", 24 * GiB, 24 * GiB)], 100 * GiB, []);
        var result = ModelMemoryEstimator.Estimate(Metadata with { ContextLength = 262144 }, resources, context);
        Assert.Equal("Compatible", result.Status);
        Assert.Equal(context, result.ContextTokens);
        Assert.Equal(context * 131072L, result.KvBytes);
    }

    [Fact]
    public async Task ProfileHttpEndpointsValidateConfirmationAndBoundsWithoutAnyGeneration()
    {
        await using var fixture = new Fixture { Architecture = "future", ResidentContext = 8192 };
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Studio:DataDirectory"] = fixture.Directory,
                    ["StudioDevDisableOllama"] = "true",
                    ["StudioDevDisableDocker"] = "true"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IModelLibraryService>();
                services.AddSingleton<IModelLibraryService>(fixture.Service);
            });
        });
        using var client = factory.CreateClient();
        using var saved = await client.PutAsJsonAsync("/api/model-library/specializations",
            new { name = "coder:7b", specializations = new[] { "Code", "Tests" } });
        saved.EnsureSuccessStatusCode();
        using var refused = await client.PostAsJsonAsync("/api/model-library/qualification",
            new { name = "coder:7b", contextTokens = 8192, confirmed = false });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var qualified = await client.PostAsJsonAsync("/api/model-library/qualification",
            new { name = "coder:7b", contextTokens = 8192, confirmed = true });
        qualified.EnsureSuccessStatusCode();
        using var snapshot = await client.GetAsync("/api/model-library/?context=65536");
        snapshot.EnsureSuccessStatusCode();
        using var preview = await client.PostAsJsonAsync("/api/model-library/routing/preview",
            new { task = "React TypeScript", role = "Developer", contextTokens = 8192 });
        preview.EnsureSuccessStatusCode();
        using var invalid = await client.GetAsync("/api/model-library/?context=262145");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var clearRefused = await client.PostAsJsonAsync("/api/model-library/qualification/clear",
            new { name = "coder:7b" });
        Assert.Equal(HttpStatusCode.BadRequest, clearRefused.StatusCode);
        using var cleared = await client.PostAsJsonAsync("/api/model-library/qualification/clear",
            new { name = "coder:7b", confirmed = true });
        cleared.EnsureSuccessStatusCode();
        using var restored = await client.PutAsJsonAsync("/api/model-library/specializations",
            new { name = "coder:7b", specializations = (string[]?)null });
        restored.EnsureSuccessStatusCode();
        Assert.DoesNotContain(fixture.Paths, endpoint => endpoint is "/api/chat" or "/api/generate" or "/api/pull" or "/api/delete");
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeHardware(Func<HardwareSnapshot> read) : IHardwareProbe
    {
        public Task<HardwareSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(read());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "AIPlatformModelLibrary-" + Guid.NewGuid().ToString("N"));
        public System.Collections.Concurrent.ConcurrentQueue<string> Paths { get; } = new();
        public CustomAgentStore Store { get; }
        public ModelLibraryService Service { get; }
        public string Architecture { get; set; } = "qwen2";
        public int DeclaredContext { get; set; } = 32768;
        public string? Digest { get; set; } = new string('a', 64);
        public string? ResidentDigest { get; set; }
        public int? ResidentContext { get; set; }
        public long ResidentSize { get; set; } = 5 * GiB;
        public HardwareSnapshot Resources { get; set; } = Hardware();
        private readonly HttpClient client;
        public Fixture(Func<CancellationToken, Task<HttpResponseMessage>>? pull = null, bool unavailable = false)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var studio = new StudioOptions { DataDirectory = Directory };
            Store = new(studio);
            client = new(new StubHttpHandler(async (message, token) =>
            {
                var path = message.RequestUri!.AbsolutePath;
                Paths.Enqueue(path);
                if (path.StartsWith("/v2/", StringComparison.Ordinal))
                    return path.Contains("/missing/") ? new(HttpStatusCode.NotFound) :
                        StubHttpHandler.Json("""{"layers":[{"size":4096}]}""");
                if (unavailable) return new(HttpStatusCode.ServiceUnavailable);
                if (path == "/api/pull") return pull is null ? throw new InvalidOperationException("Unexpected pull") : await pull(token);
                return path switch
                {
                    "/api/tags" => StubHttpHandler.Json(JsonSerializer.Serialize(new
                        { models = new[] { new { name = "coder:7b", size = 4 * GiB, digest = Digest } } })),
                    "/api/ps" => StubHttpHandler.Json(ResidentContext is null ? """{"models":[]}""" :
                        JsonSerializer.Serialize(new { models = new[] { new { name = "coder:7b", size = ResidentSize,
                            size_vram = 4 * GiB, context_length = ResidentContext, digest = ResidentDigest ?? Digest } } })),
                    "/api/show" => StubHttpHandler.Json("""
                        {"details":{"family":"qwen2","parameter_size":"7B","quantization_level":"Q4_K_M"},
                        "capabilities":["completion"],"license":"Apache License",
                        "model_info":{"general.architecture":"qwen2","general.parameter_count":7000000000,
                        "qwen2.context_length":262144,"qwen2.block_count":32,"qwen2.embedding_length":4096,
                        "qwen2.attention.head_count":32,"qwen2.attention.head_count_kv":8}}
                        """.Replace("qwen2", Architecture, StringComparison.Ordinal)
                            .Replace("262144", DeclaredContext.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)),
                    "/api/delete" => new(HttpStatusCode.OK),
                    _ => throw new InvalidOperationException("Unexpected endpoint " + path)
                };
            }));
            Service = CreateService();
        }
        public ModelLibraryService CreateService() => new(client, client, new(), new() { DataDirectory = Directory },
            new FakeHardware(() => Resources), Store, new AgentRegistry(TestSupport.Configuration()), new(),
            NullLogger<ModelLibraryService>.Instance);
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            client.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
