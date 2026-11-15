using System.Text.Json;
using System.Text.Json.Serialization;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class CustomAgentTests
{
    [Fact]
    public async Task PersistsAgentAndConversationAcrossStoreInstances()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var store = new CustomAgentStore(options);
            var provider = new FakeProvider((_, _) => Task.FromResult("Réponse Ollama."));
            var service = Service(store, provider, new FakeOllamaModelCatalog());
            var agent = await service.CreateAgentAsync(
                new("Assistant", "Aide.", "Réponds en français.", "qwen2.5-coder:7b"), CancellationToken.None);
            var conversation = service.CreateConversation(agent.Id)!;
            var result = await service.SendMessageAsync(agent.Id, conversation.Id, "Bonjour", CancellationToken.None);

            var restartedStore = new CustomAgentStore(options);
            Assert.Equal(agent, Assert.Single(restartedStore.ListAgents()));
            var savedConversation = restartedStore.GetConversation(agent.Id, conversation.Id)!;
            Assert.Equal(result!.Messages, savedConversation.Messages);
            Assert.Equal(new[] { "Bonjour", "Réponse Ollama." },
                savedConversation.Messages.Select(item => item.Content));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ChatForwardsSystemPromptAndSavedConversationContext()
    {
        var directory = NewDirectory();
        try
        {
            var provider = new FakeProvider((request, _) =>
                Task.FromResult($"Réponse {request.Messages.Count(item => item.Role == "user")}"));
            var service = Service(new CustomAgentStore(new() { DataDirectory = directory }), provider,
                new FakeOllamaModelCatalog());
            var agent = await service.CreateAgentAsync(
                new("Assistant", "", "You are a code assistant.", "qwen2.5-coder:7b"), CancellationToken.None);
            var conversation = service.CreateConversation(agent.Id)!;
            await service.SendMessageAsync(agent.Id, conversation.Id, "Premier message", CancellationToken.None);
            await service.SendMessageAsync(agent.Id, conversation.Id, "Question suivante", CancellationToken.None);

            var requests = provider.Requests.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Equal(new[] { "system", "user", "assistant", "user" },
                requests[1].Messages.Select(item => item.Role));
            Assert.Equal("You are a code assistant.", requests[1].Messages[0].Content);
            Assert.Equal("Premier message", requests[1].Messages[1].Content);
            Assert.Equal("Réponse 1", requests[1].Messages[2].Content);
            Assert.Equal("Question suivante", requests[1].Messages[3].Content);
            Assert.Equal("qwen2.5-coder:7b", requests[1].Model.Name);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsInvalidInputAndModelsRemovedFromOllama()
    {
        var directory = NewDirectory();
        try
        {
            var catalog = new FakeOllamaModelCatalog();
            var provider = new FakeProvider((_, _) => Task.FromResult("Should not be called."));
            var service = Service(new CustomAgentStore(new() { DataDirectory = directory }), provider, catalog);
            await Assert.ThrowsAsync<StudioValidationException>(() => service.CreateAgentAsync(
                new(" ", "", "Instructions", "qwen2.5-coder:7b"), CancellationToken.None));
            var agent = await service.CreateAgentAsync(
                new("Assistant", "", "Instructions", "qwen2.5-coder:7b"), CancellationToken.None);
            var conversation = service.CreateConversation(agent.Id)!;
            catalog.Models = ["qwen3:4b"];
            await Assert.ThrowsAsync<CustomAgentModelUnavailableException>(() => service.SendMessageAsync(
                agent.Id, conversation.Id, "Hello", CancellationToken.None));
            await Assert.ThrowsAsync<StudioValidationException>(() => service.SendMessageAsync(
                agent.Id, conversation.Id, new string('x', CustomAgentLimits.MaxUserMessageCharacters + 1),
                CancellationToken.None));
            Assert.Empty(provider.Requests);
            Assert.Empty(service.GetConversation(agent.Id, conversation.Id)!.Messages);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDoesNotPersistAnIncompleteTurn()
    {
        var directory = NewDirectory();
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new FakeProvider(async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "Unreachable";
            });
            var service = Service(new CustomAgentStore(new() { DataDirectory = directory }), provider,
                new FakeOllamaModelCatalog());
            var agent = await service.CreateAgentAsync(
                new("Assistant", "", "Instructions", "qwen2.5-coder:7b"), CancellationToken.None);
            var conversation = service.CreateConversation(agent.Id)!;
            using var cancellation = new CancellationTokenSource();
            var pending = service.SendMessageAsync(agent.Id, conversation.Id, "Cancel this", cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Empty(service.GetConversation(agent.Id, conversation.Id)!.Messages);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateStartsWithoutHistoryAndDeletingAgentRemovesItsChats()
    {
        var directory = NewDirectory();
        try
        {
            var service = Service(new CustomAgentStore(new() { DataDirectory = directory }),
                new FakeProvider((_, _) => Task.FromResult("Answer")), new FakeOllamaModelCatalog());
            var agent = await service.CreateAgentAsync(
                new("Assistant", "", "Instructions", "qwen2.5-coder:7b"), CancellationToken.None);
            var conversation = service.CreateConversation(agent.Id)!;
            var copy = service.DuplicateAgent(agent.Id)!;
            Assert.NotEqual(agent.Id, copy.Id);
            Assert.Empty(service.ListConversations(copy.Id));
            Assert.True(service.DeleteAgent(agent.Id));
            Assert.Null(service.GetAgent(agent.Id));
            Assert.Null(service.GetConversation(agent.Id, conversation.Id));
            Assert.NotNull(service.GetAgent(copy.Id));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadsARealTemporaryExternalFolderAndKeepsItsProposalReadOnly()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "sample", "src"));
        var sourcePath = Path.Combine(directory, "sample", "src", "Example.cs");
        const string before = "public sealed class Example { }";
        const string after = "public sealed class Example { public int Value => 1; }";
        await File.WriteAllTextAsync(sourcePath, before);
        try
        {
            var provider = new FakeProvider((_, _) => Task.FromResult(JsonSerializer.Serialize(new
            {
                response = "La classe est très simple.",
                changes = new[] { new { path = "sample/src/Example.cs", summary = "Ajouter une propriété.", content = after } }
            })));
            var options = new StudioOptions { DataDirectory = directory };
            var service = Service(new CustomAgentStore(options), provider, new FakeOllamaModelCatalog(),
                projectAnalyzer: new ProjectAnalyzer(options));
            var agent = await service.CreateAgentAsync(
                new("Assistant", "", "Analyse le code.", "qwen2.5-coder:7b"), CancellationToken.None);
            var conversation = service.CreateConversation(agent.Id)!;
            service.SetWorkspace(agent.Id, conversation.Id,
                new(CustomAgentWorkspaceKind.External, null, "sample"));
            var file = new CustomAgentWorkspaceFile("sample/src/Example.cs", await File.ReadAllTextAsync(sourcePath));

            var updated = await service.SendMessageAsync(
                agent.Id, conversation.Id, "Analyse le projet.", CancellationToken.None, [file]);
            var proposal = updated!.Messages.Last().Proposal;
            Assert.NotNull(proposal);
            Assert.Equal("Pending", proposal.Status);
            Assert.False(proposal.CanApply);
            Assert.Contains("read-only", proposal.ApplyReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, Assert.Single(proposal.Changes).Before);
            Assert.Equal(after, Assert.Single(proposal.Changes).After);
            Assert.Contains("public sealed class Example", provider.Requests.Single().Messages[0].Content);
            var restartedStore = new CustomAgentStore(options);
            var persisted = restartedStore.GetConversation(agent.Id, conversation.Id)!;
            Assert.Equal("sample", persisted.Workspace!.Name);
            Assert.Null(persisted.Workspace.WorkspaceId);
            Assert.Equal(proposal.Id, persisted.Messages.Last().Proposal!.Id);
            Assert.Equal("Pending", persisted.Messages.Last().Proposal!.Status);

            await Assert.ThrowsAsync<StudioValidationException>(() => service.DecideProposalAsync(
                agent.Id, conversation.Id, proposal.Id, true, CancellationToken.None));
            Assert.Equal(before, await File.ReadAllTextAsync(sourcePath));
            var rejected = await service.DecideProposalAsync(
                agent.Id, conversation.Id, proposal.Id, false, CancellationToken.None);
            Assert.Equal("Rejected", rejected!.Messages.Last().Proposal!.Status);
            Assert.Equal(before, await File.ReadAllTextAsync(sourcePath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ApprovedStudioProposalUpdatesWorkspaceAndMissionRevisionHistory()
    {
        var directory = NewDirectory();
        var options = new StudioOptions { DataDirectory = directory };
        var workspaceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        const string original = "Console.WriteLine(\"before\");";
        const string replacement = "Console.WriteLine(\"approved\");";
        var files = new[]
        {
            new GeneratedFile("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"),
            new GeneratedFile("src/App/Program.cs", original),
            new GeneratedFile("tests/App.Tests/App.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsTestProject>true</IsTestProject><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /><PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.4\" /><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\" /><ProjectReference Include=\"../../src/App/App.csproj\" /></ItemGroup></Project>"),
            new GeneratedFile("tests/App.Tests/Example.cs", "public sealed class ExampleTests { }"),
        };
        var workflow = new MissionWorkflow(Guid.NewGuid(), workspaceId,
            [new AgentDefinition { Id = "Orchestrator", Role = "Orchestrator" }], [],
            [new WorkflowStep("review", "Review", "Orchestrator", [], WorkflowStatus.Completed)]);
        var mission = new MissionSnapshot(workspaceId, "Temporary project", MissionKind.Project, null,
            false, MissionStatus.GeneratedUnverified, now, now, null, null, null, false, null, null,
            true, 0, [], [], Workflow: workflow);
        Directory.CreateDirectory(Path.Combine(directory, "missions"));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "missions", workspaceId.ToString("N") + ".json"),
            JsonSerializer.Serialize(mission, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() }
            }));

        try
        {
            var workspaceStore = new WorkspaceStore(options);
            workspaceStore.Create(workspaceId);
            await workspaceStore.WriteAsync(workspaceId, files, CancellationToken.None);
            using var missionStore = new MissionStore(options, TestSupport.Configuration(),
                new AgentRegistry(TestSupport.Configuration()), workspaceStore);
            var provider = new FakeProvider((_, _) => Task.FromResult(JsonSerializer.Serialize(new
            {
                response = "Correction proposée.",
                changes = new[]
                {
                    new { path = "src/App/Program.cs", summary = "Mettre à jour la sortie.", content = replacement }
                }
            })));
            var service = Service(new CustomAgentStore(options), provider, new FakeOllamaModelCatalog(),
                workspaceStore, missionStore, new ProjectAnalyzer(options));
            var agent = await service.CreateAgentAsync(
                new("Assistant", "", "Analyse le code.", "qwen2.5-coder:7b"), CancellationToken.None);
            var conversation = service.CreateConversation(agent.Id)!;
            service.SetWorkspace(agent.Id, conversation.Id,
                new(CustomAgentWorkspaceKind.Studio, workspaceId, "Temporary project"));
            var updated = await service.SendMessageAsync(
                agent.Id, conversation.Id, "Corrige Program.cs.", CancellationToken.None);
            var proposal = updated!.Messages.Last().Proposal;
            Assert.NotNull(proposal);
            Assert.True(proposal.CanApply);

            var programPath = Path.Combine(workspaceStore.GetDirectory(workspaceId), "src", "App", "Program.cs");
            await File.WriteAllTextAsync(programPath, "changed after proposal");
            await Assert.ThrowsAsync<StudioValidationException>(() => workspaceStore.ApplyReviewedChangesAsync(
                workspaceId, null,
                [new ReviewedWorkspaceChange("src/App/Program.cs", original, replacement)], static _ => true,
                CancellationToken.None));
            Assert.Equal("changed after proposal", await File.ReadAllTextAsync(programPath));
            await File.WriteAllTextAsync(programPath, original);

            await Assert.ThrowsAsync<StudioValidationException>(() => workspaceStore.ApplyReviewedChangesAsync(
                workspaceId, null,
                [new ReviewedWorkspaceChange("src/App/Program.cs", original, replacement)], static _ => false,
                CancellationToken.None));
            Assert.Equal(original,
                (await workspaceStore.ReadAsync(workspaceId, "src/App/Program.cs", CancellationToken.None)).Content);

            var applied = await service.DecideProposalAsync(
                agent.Id, conversation.Id, proposal.Id, true, CancellationToken.None);
            Assert.Equal("Applied", applied!.Messages.Last().Proposal!.Status);
            Assert.Equal(replacement,
                (await workspaceStore.ReadAsync(workspaceId, "src/App/Program.cs", CancellationToken.None)).Content);
            var savedMission = missionStore.Get(workspaceId)!;
            Assert.Equal(2, savedMission.Workflow!.Version);
            Assert.Equal(MissionStatus.GeneratedUnverified, savedMission.Status);
            Assert.Contains(savedMission.Events, item => item.Step == "CustomAgentEdit");
            Assert.Contains(savedMission.Workflow.Revisions!, item =>
                item.Reason.Contains("Human-approved custom-agent change", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsUnsafeExternalPathBeforeCallingTheModel()
    {
        var directory = NewDirectory();
        try
        {
            var provider = new FakeProvider((_, _) => Task.FromResult("Should not be called."));
            var options = new StudioOptions { DataDirectory = directory };
            var service = Service(new CustomAgentStore(options), provider, new FakeOllamaModelCatalog(),
                projectAnalyzer: new ProjectAnalyzer(options));
            var agent = await service.CreateAgentAsync(
                new("Assistant", "", "Analyse le code.", "qwen2.5-coder:7b"), CancellationToken.None);
            var conversation = service.CreateConversation(agent.Id)!;
            Assert.Throws<StudioValidationException>(() => service.SetWorkspace(agent.Id, conversation.Id,
                new(CustomAgentWorkspaceKind.External, null, "C:\\Users\\someone")));
            service.SetWorkspace(agent.Id, conversation.Id,
                new(CustomAgentWorkspaceKind.External, null, "selected folder"));

            await Assert.ThrowsAsync<StudioValidationException>(() => service.SendMessageAsync(
                agent.Id, conversation.Id, "Analyse.", CancellationToken.None,
                [new("../outside.cs", "public class Outside {}")]));
            Assert.Empty(provider.Requests);
            Assert.Empty(service.GetConversation(agent.Id, conversation.Id)!.Messages);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static CustomAgentService Service(
        ICustomAgentStore store, ILLMProvider provider, IOllamaModelCatalog catalog,
        IWorkspaceStore? workspaces = null, IMissionService? missions = null, IProjectAnalyzer? projectAnalyzer = null) =>
        new(store, catalog, provider, new AgentRegistry(TestSupport.Configuration()), TimeProvider.System,
            workspaces, missions, projectAnalyzer);

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "AIPlatform-custom-agent-tests-" + Guid.NewGuid().ToString("N"));
}
