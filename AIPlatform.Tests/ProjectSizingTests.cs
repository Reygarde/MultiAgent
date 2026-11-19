using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.Extensions.Configuration;

namespace AIPlatform.Tests;

public sealed class ProjectSizingTests
{
    [Theory]
    [InlineData("small", 12)]
    [InlineData("standard", 32)]
    [InlineData("complex", 64)]
    public async Task SelectedProfileAllowsItsExactPlannerBoundaryButRejectsAnExtraFile(string id, int count)
    {
        using var fixture = new WorkflowFixture(responseOverride: request => Respond(request, count), sourceCheck: new Checker());
        var mission = fixture.Store.Submit(new("Display initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, SizingProfileId: id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal(count + MissionReactTemplate.Files.Count, fixture.Workspaces.List(mission.Id).Count);
        Assert.Equal(count + 2, current.GenerationReservations!.Count);
        Assert.Equal(current.Sizing!.MaxCorrections, (await fixture.Plans.GetAsync(mission.Id, default)).MaxCorrectionAttempts);
        Assert.Empty(fixture.Sandbox.Calls);
        var schema = fixture.Provider.Requests.First().ResponseSchema!.Value.GetProperty("properties");
        Assert.Equal(count, schema.GetProperty("files").GetProperty("maxItems").GetInt32());
        var proposal = current.Workflow!.Plan! with
        {
            Files = [.. current.Workflow.Plan.Files.Where(file => MissionReactTemplate.IsEditable(file.Path)),
                new("src/extra.ts", "Extra file", [])]
        };
        Assert.Throws<StudioValidationException>(() => MissionReactTemplate.Prepare(proposal, current));
    }

    [Theory]
    [InlineData("small")]
    [InlineData("standard")]
    [InlineData("complex")]
    public async Task Utf8FileAndTotalByteLimitsAreExactAndPreservedByReadAndExport(string id)
    {
        using var fixture = new WorkflowFixture(responseOverride: MissionReactContractTests.Respond);
        var mission = fixture.Store.Submit(new("Display initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, SizingProfileId: id));
        var sizing = mission.Sizing!;
        var files = Sources().Append(new GeneratedFile("src/large.ts",
            "//" + new string('x', sizing.MaxFileBytes - 4) + "\u00e9")).ToArray();
        fixture.Workspaces.ValidateFiles(files, MissionReactTemplate.Id, sizing: sizing);
        var tooLarge = files.Select(file => file.Path == "src/large.ts"
            ? file with { Content = file.Content + "x" } : file).ToArray();
        Assert.Throws<StudioValidationException>(() =>
            fixture.Workspaces.ValidateFiles(tooLarge, MissionReactTemplate.Id, sizing: sizing));
        var exact = SizedSources(sizing.MaxProjectBytes, sizing.MaxFileBytes);
        fixture.Workspaces.ValidateFiles(exact, MissionReactTemplate.Id, sizing: sizing);
        var over = exact.Select((file, index) => index == exact.Length - 1
            ? file with { Content = file.Content + "x" } : file).ToArray();
        Assert.Throws<StudioValidationException>(() =>
            fixture.Workspaces.ValidateFiles(over, MissionReactTemplate.Id, sizing: sizing));
        fixture.Workspaces.Create(mission.Id);
        await fixture.Workspaces.WriteAsync(mission.Id, exact, default, MissionReactTemplate.Id);
        Assert.Equal(sizing.MaxProjectBytes, fixture.Workspaces.List(mission.Id).Sum(file => file.Size));
        Assert.NotEmpty(await new ProjectExport(fixture.Workspaces, fixture.Options, fixture.Store).CreateAsync(mission.Id, default));
        var large = exact.Single(file => file.Path == "src/padding0.ts");
        Assert.Equal(large.Content, (await fixture.Workspaces.ReadAsync(mission.Id, large.Path, default)).Content);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void InvalidOrInconsistentProfilesAreRejected(int kind)
    {
        var valid = ProjectSizingProfile.Defaults[2];
        var invalid = kind switch
        {
            0 => valid with { MaxFiles = 101 },
            1 => valid with { MaxProjectBytes = valid.MaxFileBytes - 1 },
            2 => valid with { MaxPlannedFiles = 100 },
            3 => valid with { MaxCorrections = 13 },
            4 => valid with { MaxRepairGroups = 33 },
            _ => valid with { MissionTimeoutSeconds = 7201 }
        };
        Assert.Throws<ArgumentException>(() => (new StudioOptions { SizingProfiles = [invalid] }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (new StudioOptions { SizingProfiles = [valid, valid] }).Validate());
    }

    [Fact]
    public void CriteriaAndWorkspaceCountsEnforceLowAndHighBounds()
    {
        var profile = ProjectSizingProfile.Defaults[2];
        var request = string.Join("\n", Enumerable.Range(1, 128).Select(index => $"- Display item {index}"));
        Assert.Equal(128, MissionReactTemplate.NormalizeRequirements(request, profile).Criteria.Count);
        Assert.Throws<StudioValidationException>(() =>
            MissionReactTemplate.NormalizeRequirements(request + "\n- Display extra item", profile));
        Assert.Throws<StudioValidationException>(() => MissionReactTemplate.NormalizeRequirements(request));
        var low = profile with { MaxCriteria = 1 };
        Assert.Single(MissionReactTemplate.NormalizeRequirements("- Display item", low).Criteria);
        Assert.Throws<StudioValidationException>(() =>
            MissionReactTemplate.NormalizeRequirements("- Display item\n- Add an item", low));
        using var fixture = new WorkflowFixture();
        var exact = Sources().Concat(Enumerable.Range(0, profile.MaxFiles - Sources().Length)
            .Select(index => new GeneratedFile($"src/part{index}.ts", "// valid"))).ToArray();
        fixture.Workspaces.ValidateFiles(exact, MissionReactTemplate.Id, sizing: profile);
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(
            [.. exact, new("src/overflow.ts", "// extra")], MissionReactTemplate.Id, sizing: profile));
        Assert.Throws<StudioValidationException>(() => fixture.Workspaces.ValidateFiles(
            [.. Sources(), new("../escape.ts", "// invalid")], MissionReactTemplate.Id, sizing: profile));
    }

    [Fact]
    public void FrozenProfilesSurviveConfigurationChangesAndCannotBeChangedOnExistingMissions()
    {
        using var fixture = new WorkflowFixture();
        var legacy = fixture.Store.Submit(new("Display initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id));
        var mission = fixture.Store.Submit(new("Display initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, SizingProfileId: "complex", MaxTotalGenerations: 17));
        var options = fixture.Options with { SizingProfiles = [ProjectSizingProfile.Defaults[0]] };
        using var reopened = new MissionStore(options, fixture.Platform, fixture.Registry, fixture.Workspaces);
        Assert.Equal(mission.Sizing, reopened.Get(mission.Id)!.Sizing);
        Assert.Equal(17, reopened.Get(mission.Id)!.MaxTotalGenerations);
        Assert.Null(reopened.Get(legacy.Id)!.Sizing);
        Assert.Throws<StudioValidationException>(() =>
            reopened.Update(mission.Id, item => item with { Sizing = ProjectSizingProfile.Defaults[0] }));
        Assert.Throws<StudioValidationException>(() =>
            reopened.Update(legacy.Id, item => item with { Sizing = ProjectSizingProfile.Defaults[0] }));
        Assert.Throws<StudioValidationException>(() => reopened.Submit(new("Display item", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, SizingProfileId: "complex")));
        Assert.Throws<StudioValidationException>(() =>
            reopened.Submit(new("Display item", MissionKind.Chat, SizingProfileId: "small")));
    }

    [Fact]
    public async Task ComplexProfileAllowsFourBoundedCorrectionCyclesWithoutIncreasingTheBudget()
    {
        var reviews = 0;
        var fixes = 0;
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: request =>
        {
            using var input = JsonDocument.Parse(request.Messages.Last().Content);
            if (input.RootElement.TryGetProperty("allowedPaths", out _))
                return JsonSerializer.Serialize(new { files = input.RootElement.GetProperty("targets").EnumerateArray()
                    .Select(file => new GeneratedFile(file.GetProperty("path").GetString()!,
                        file.GetProperty("content").GetString()! + $"\n// distinct fix {++fixes}")) }, JsonSerializerOptions.Web);
            var answer = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
            if (answer["criterionReviews"] is not null && ++reviews <= 4)
                answer["findings"] = JsonSerializer.SerializeToNode(new[]
                    { new ReviewFinding("src/App.tsx", "Error", $"Distinct issue {reviews}") }, JsonSerializerOptions.Web);
            return answer.ToJsonString();
        });
        var mission = fixture.Store.Submit(new("Display initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, AutomaticReactCorrections: true,
            SizingProfileId: "complex", MaxTotalGenerations: 20));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.Equal(4, current.CorrectionAttempts);
        Assert.Equal(13, current.GenerationReservations!.Count);
        Assert.Equal(20, current.MaxTotalGenerations);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task ReviewerGroupsRespectOutputHeadroomWithoutDroppingAnyCriterion()
    {
        using var fixture = new WorkflowFixture(responseOverride: MissionReactContractTests.Respond,
            sourceCheck: new Checker(), contextSize: 32768);
        var mission = fixture.Store.Submit(new(string.Join("\n",
                Enumerable.Range(1, 33).Select(index => $"- Display item {index}")), MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, SizingProfileId: "standard", MaxTotalGenerations: 100));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        var current = fixture.Store.Get(mission.Id)!;
        var groups = WorkflowAgents.PreviewReactReview(current, default);
        Assert.True(groups.Count > 1);
        Assert.Equal(33, groups.SelectMany(group => group.Criteria).Distinct().Count());
        Assert.All(groups, group => Assert.InRange(group.EstimatedOutputTokens!.Value, 1, (int)(2048 * .8)));
        Assert.Equal(33, current.Workflow!.Review!.CriterionReviews!.Count);
        Assert.Equal(MissionStatus.AwaitingApproval, current.Status);
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public async Task TooManyRepairGroupsAreRefusedBeforeAnyCorrectionCallOrPartialPublication()
    {
        using var fixture = new WorkflowFixture(sourceCheck: new Checker(), responseOverride: request =>
        {
            var answer = JsonNode.Parse(Respond(request, 7))!;
            if (answer["decisions"] is not null)
                answer["files"]![0]!["dependsOn"] = new JsonArray(
                    "src/model.ts", "src/part3.ts", "src/part4.ts", "src/part5.ts", "src/part6.ts");
            if (answer["criterionReviews"] is not null)
                answer["findings"] = JsonSerializer.SerializeToNode(Enumerable.Range(3, 4)
                    .Select(index => new ReviewFinding($"src/part{index}.ts", "Error", $"Distinct issue {index}")),
                    JsonSerializerOptions.Web);
            return answer.ToJsonString();
        });
        var mission = fixture.Store.Submit(new("Display initial value 0", MissionKind.Project,
            TemplateId: MissionReactTemplate.Id, SizingProfileId: "small", AutomaticReactCorrections: true));
        var error = await Assert.ThrowsAsync<StudioValidationException>(() =>
            fixture.Processor.ProcessAsync(mission.Id, default));
        Assert.Contains("repair group limit", error.Message);
        var current = fixture.Store.Get(mission.Id)!;
        Assert.Single(current.Workflow!.Revisions!);
        Assert.Empty(current.Workflow.SoftwareCorrections);
        Assert.Equal(9, current.GenerationReservations!.Count);
        Assert.DoesNotContain(fixture.Provider.Requests,
            request => request.Messages.Last().Content.Contains("\"allowedPaths\"", StringComparison.Ordinal));
        Assert.Empty(fixture.Sandbox.Calls);
    }

    [Fact]
    public void ConfiguredProfilesReplaceDefaultsAndRespectTheSameBounds()
    {
        var selected = ProjectSizingProfile.Defaults[1] with { Id = "configured", MaxCorrections = 4 };
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            { Studio = new { SizingProfiles = new[] { selected } } }));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = configuration.GetSection("Studio").Get<StudioOptions>()!;
        options.Validate();
        Assert.Equal(selected, Assert.Single(options.AvailableSizingProfiles));
    }

    private static GeneratedFile[] Sources() => [.. MissionReactTemplate.Files,
        new("src/App.tsx", "export default function App() { return <main>Mission</main> }"),
        new("src/App.test.tsx", "import { test, expect } from 'vitest'; test('[C1] simulated', () => expect(1).toBe(1))")];

    private static GeneratedFile[] SizedSources(int bytes, int maxFileBytes)
    {
        var files = Sources().ToList();
        var remaining = bytes - files.Sum(file => Encoding.UTF8.GetByteCount(file.Content));
        for (var index = 0; remaining > 0; index++)
        {
            var count = Math.Min(remaining, maxFileBytes);
            files.Add(new($"src/padding{index}.ts", new string('x', count)));
            remaining -= count;
        }
        return files.ToArray();
    }

    private static string Respond(LlmRequest request, int count)
    {
        var result = JsonNode.Parse(MissionReactContractTests.Respond(request))!;
        if (result["decisions"] is not null)
            for (var index = 3; index < count; index++)
                result["files"]!.AsArray().Add(JsonSerializer.SerializeToNode(
                    new PlannedFile($"src/part{index}.ts", "Independent synthetic module", []), JsonSerializerOptions.Web));
        return result.ToJsonString();
    }

    private sealed class Checker : IReactSourceCheck, IReactSourceRepair
    {
        public Task<IReadOnlyList<ReactSourceIssue>> CheckAsync(IReadOnlyList<GeneratedFile> files, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ReactSourceIssue>>([]);
        public Task<ReactSourceRepairResult> RepairAsync(IReadOnlyList<GeneratedFile> files, IReadOnlyList<string> allowedPaths, CancellationToken token) =>
            Task.FromResult(new ReactSourceRepairResult(files, [], [], [], []));
    }
}
