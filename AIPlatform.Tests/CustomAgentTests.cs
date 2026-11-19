using System.Text.Json;
using System.Text.Json.Serialization;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class CustomAgentTests
{
    [Theory]
    [InlineData(65536)]
    [InlineData(262144)]
    public async Task PersistsExplicitLargeContextWithoutChangingLegacyAgentDefaults(int context)
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var store = new CustomAgentStore(options);
            var provider = new FakeProvider((_, _) => throw new InvalidOperationException("No generation permitted."));
            var service = Service(store, provider, new FakeOllamaModelCatalog());
            var legacy = await service.CreateAgentAsync(
                new("Legacy", "", "Work.", "qwen2.5-coder:7b"), default);
            var draft = new CustomAgentDraft("Future", "", "Work.", "qwen2.5-coder:7b",
                Generation: new(context, 3072));
            var future = await service.CreateAgentAsync(draft, default);
            var restarted = new CustomAgentStore(options);
            Assert.Null(restarted.GetAgent(legacy.Id)!.Generation);
            Assert.Equal(new(context, 3072), restarted.GetAgent(future.Id)!.Generation);
            await Assert.ThrowsAsync<StudioValidationException>(() =>
                service.CreateAgentAsync(draft with { Generation = new(ModelContextLimits.Maximum + 1, 3072) }, default));
            Assert.Empty(provider.Requests);
            Assert.Equal(2, restarted.ListAgents().Count);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

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
            var provider = new FakeProvider((request, _) =>
            {
                Assert.True(request.ResponseSchema!.Value.GetProperty("properties")
                    .TryGetProperty("files", out var filesSchema));
                Assert.Equal("array", filesSchema.GetProperty("type").GetString());
                var response = JsonSerializer.Serialize(new
                {
                    files = new[]
                    {
                        new { operation = "update", path = "sample/src/Example.cs", content = after }
                    }
                });
                return Task.FromResult($"```json\n{response}\n```");
            });
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
    public async Task ReportsTruncatedWorkspaceJsonWithoutOfferingChanges()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "sample", "src"));
        var sourcePath = Path.Combine(directory, "sample", "src", "Example.cs");
        const string source = "public sealed class Example { }";
        await File.WriteAllTextAsync(sourcePath, source);
        try
        {
            var provider = new FakeProvider((_, _) => Task.FromResult("{\"files\":["));
            var options = new StudioOptions { DataDirectory = directory };
            var service = Service(new CustomAgentStore(options), provider, new FakeOllamaModelCatalog(),
                projectAnalyzer: new ProjectAnalyzer(options));
            var agent = await service.CreateAgentAsync(
                new("Assistant", "", "Implement a code change.", "qwen2.5-coder:7b"), CancellationToken.None);
            var conversation = service.CreateConversation(agent.Id)!;
            service.SetWorkspace(agent.Id, conversation.Id,
                new(CustomAgentWorkspaceKind.External, null, "sample"));
            var file = new CustomAgentWorkspaceFile("sample/src/Example.cs", source);

            var exception = await Assert.ThrowsAsync<LlmProviderException>(() => service.SendMessageAsync(
                agent.Id, conversation.Id, "Add a value property.", CancellationToken.None, [file]));

            Assert.Contains("malformed or truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(service.GetConversation(agent.Id, conversation.Id)!.Messages);
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
            new GeneratedFile("tests/App.Tests/Retained.cs", "public sealed class RetainedTests { }"),
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
                    new
                    {
                        operation = "modify", path = "src/App/Program.cs",
                        summary = "Mettre à jour la sortie.", content = (string?)replacement
                    },
                    new
                    {
                        operation = "create", path = "src/App/Helpers.cs",
                        summary = "Ajouter le helper.", content = (string?)"public static class Helpers { }"
                    },
                    new
                    {
                        operation = "delete", path = "tests/App.Tests/Example.cs",
                        summary = "Supprimer le test obsolète.", content = (string?)null
                    }
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
            Assert.Equal(new[] { "modify", "create", "delete" },
                proposal.Changes.Select(change => change.Operation));

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
            await Assert.ThrowsAsync<StudioValidationException>(() => workspaceStore.ApplyReviewedChangesAsync(
                workspaceId, null,
                [
                    new ReviewedWorkspaceChange("src/App/Program.cs", original, replacement),
                    new ReviewedWorkspaceChange("src/App/Helpers.cs", null, "public static class Helpers { }"),
                    new ReviewedWorkspaceChange("tests/App.Tests/Example.cs", "public sealed class ExampleTests { }", null)
                ], static _ => false, CancellationToken.None));
            Assert.Equal(original,
                (await workspaceStore.ReadAsync(workspaceId, "src/App/Program.cs", CancellationToken.None)).Content);
            Assert.DoesNotContain(workspaceStore.List(workspaceId), file => file.Path == "src/App/Helpers.cs");
            Assert.Equal("public sealed class ExampleTests { }",
                (await workspaceStore.ReadAsync(workspaceId, "tests/App.Tests/Example.cs", CancellationToken.None)).Content);

            var applied = await service.DecideProposalAsync(
                agent.Id, conversation.Id, proposal.Id, true, CancellationToken.None);
            Assert.Equal("Applied", applied!.Messages.Last().Proposal!.Status);
            Assert.Equal(replacement,
                (await workspaceStore.ReadAsync(workspaceId, "src/App/Program.cs", CancellationToken.None)).Content);
            Assert.Equal("public static class Helpers { }",
                (await workspaceStore.ReadAsync(workspaceId, "src/App/Helpers.cs", CancellationToken.None)).Content);
            Assert.DoesNotContain(workspaceStore.List(workspaceId),
                file => file.Path == "tests/App.Tests/Example.cs");
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
    public async Task PersistsNativeExternalApplyAndRestoreOutcomes()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory };
            var provider = new FakeProvider((_, _) => Task.FromResult(JsonSerializer.Serialize(new
            {
                response = "La correction est prête.",
                changes = new[] { new
                {
                    path = "src/Example.cs", summary = "Ajouter une propriété.",
                    content = "public sealed class Example { public int Value => 1; }"
                } }
            })));
            var store = new CustomAgentStore(options);
            var service = Service(store, provider, new FakeOllamaModelCatalog(),
                projectAnalyzer: new ProjectAnalyzer(options));
            var agent = await service.CreateAgentAsync(
                new("Assistant", "", "Analyse le code.", "qwen2.5-coder:7b"), CancellationToken.None);
            var conversation = service.CreateConversation(agent.Id)!;
            var authorizationId = Guid.NewGuid().ToString("N");
            service.SetWorkspace(agent.Id, conversation.Id,
                new(CustomAgentWorkspaceKind.External, null, "temporary", authorizationId));
            var result = await service.SendMessageAsync(agent.Id, conversation.Id, "Corrige le fichier.",
                CancellationToken.None,
                [new("src/Example.cs", "public sealed class Example { }")]);
            var proposal = result!.Messages.Last().Proposal!;
            Assert.Equal(authorizationId, proposal.Workspace!.AuthorizationId);
            var backupId = Guid.NewGuid().ToString("N");

            var applied = await service.RecordExternalProposalOutcomeAsync(
                agent.Id, conversation.Id, proposal.Id, backupId, false, CancellationToken.None);
            Assert.Equal("Applied", applied!.Messages.Last().Proposal!.Status);
            Assert.Equal(backupId, applied.Messages.Last().Proposal!.ExternalBackupId);
            Assert.Null(applied.Messages.Last().Proposal!.ExternalRestoredAt);

            var restored = await service.RecordExternalProposalOutcomeAsync(
                agent.Id, conversation.Id, proposal.Id, backupId, true, CancellationToken.None);
            Assert.NotNull(restored!.Messages.Last().Proposal!.ExternalRestoredAt);
            var restarted = new CustomAgentStore(options).GetConversation(agent.Id, conversation.Id)!;
            Assert.Equal(backupId, restarted.Messages.Last().Proposal!.ExternalBackupId);
            Assert.NotNull(restarted.Messages.Last().Proposal!.ExternalRestoredAt);
            Assert.Equal(authorizationId, restarted.Messages.Last().Proposal!.Workspace!.AuthorizationId);
            await Assert.ThrowsAsync<StudioValidationException>(() => service.RecordExternalProposalOutcomeAsync(
                agent.Id, conversation.Id, proposal.Id, backupId, true, CancellationToken.None));
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
            Assert.Throws<StudioValidationException>(() => service.SetWorkspace(agent.Id, conversation.Id,
                new(CustomAgentWorkspaceKind.External, null, "selected folder", "not-a-native-grant")));

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

    [Fact]
    public async Task CollaboratesInOrderConsolidatesCorrectionAndPersistsOnePendingProposal()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "external", "src"));
        var originalPath = Path.Combine(directory, "external", "src", "Example.tsx");
        const string original = "export const Example = () => <div />;";
        await File.WriteAllTextAsync(originalPath, original);
        try
        {
            var correctionCall = 0;
            var reviewerCall = 0;
            var provider = new FakeProvider((request, _) =>
            {
                var system = request.Messages[0].Content;
                if (system.Contains("You are the Planner", StringComparison.Ordinal))
                    return Task.FromResult("""{"response":"1. Update Example. 2. Add a helper.","changes":[]}""");
                if (system.Contains("read-only Reviewer", StringComparison.Ordinal))
                {
                    var requiresCorrection = Interlocked.Increment(ref reviewerCall) == 1;
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        response = "Use an explicit accessible label on the control.",
                        requiresCorrection
                    }));
                }
                if (system.Contains("Address the Reviewer findings", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref correctionCall);
                    return Task.FromResult("""{"response":"Added the accessible label.","changes":[{"operation":"modify","path":"src/Example.tsx","summary":"Address reviewer accessibility finding.","content":"export const Example = () => <button aria-label=\"Open\">Open</button>;"}]}""");
                }
                return Task.FromResult("""{"response":"Implementation prepared.","changes":[{"operation":"modify","path":"src/Example.tsx","summary":"Implement the component.","content":"export const Example = () => <button>Open</button>;"},{"operation":"create","path":"src/Helper.ts","summary":"Add a helper.","content":"export const helper = () => true;"}]}""");
            });
            var options = new StudioOptions { DataDirectory = directory };
            var store = new CustomAgentStore(options);
            var service = Service(store, provider, new FakeOllamaModelCatalog(),
                projectAnalyzer: new ProjectAnalyzer(options));
            var planner = await service.CreateAgentAsync(
                new("Planner", "", "Plan clearly.", "qwen3:4b"), CancellationToken.None);
            var developer = await service.CreateAgentAsync(
                new("Developer", "", "Implement carefully.", "qwen2.5-coder:7b"), CancellationToken.None);
            var reviewer = await service.CreateAgentAsync(
                new("Reviewer", "", "Review critically.", "qwen3:4b"), CancellationToken.None);
            var team = service.SaveTeam(null, new("Frontend team",
                [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)]))!;
            var authorizationId = Guid.NewGuid().ToString("N");
            var workspace = new CustomAgentWorkspaceSelection(
                CustomAgentWorkspaceKind.External, null, "external", authorizationId);
            var run = service.StartCollaboration(team.Id, "Create an accessible task button.", workspace,
                [new("src/Example.tsx", original)])!;

            var completed = await WaitForCollaborationAsync(service, team.Id, run.Id);
            Assert.Equal("AwaitingApproval", completed.Status);
            Assert.Equal(new[] { "Planner", "Developer", "Reviewer", "Developer", "Reviewer" },
                completed.Contributions.Select(item => item.Role));
            Assert.Equal(new[] { "qwen3:4b", "qwen2.5-coder:7b", "qwen3:4b", "qwen2.5-coder:7b", "qwen3:4b" },
                provider.Requests.Select(item => item.Model.Name));
            Assert.Equal(1, correctionCall);
            Assert.Equal(2, completed.Proposal!.Changes.Count);
            Assert.Equal("export const Example = () => <button aria-label=\"Open\">Open</button>;",
                completed.Proposal.Changes.Single(change => change.Path == "src/Example.tsx").After);
            Assert.Equal("create", completed.Proposal.Changes.Single(change => change.Path == "src/Helper.ts").Operation);
            Assert.Equal(authorizationId, completed.Proposal.Workspace!.AuthorizationId);
            Assert.Equal(original, await File.ReadAllTextAsync(originalPath));

            var reloaded = new CustomAgentStore(options);
            Assert.Equal(team.Id, Assert.Single(reloaded.ListTeams()).Id);
            var persisted = Assert.Single(reloaded.ListCollaborationRuns(team.Id));
            Assert.Equal(completed.Proposal.Id, persisted.Proposal!.Id);
            Assert.Equal(completed.Contributions, persisted.Contributions);
            Assert.Equal("Pending", persisted.Proposal.Status);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelsCollaborativeRunWithoutProposalAndReportsModelErrors()
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
            var store = new CustomAgentStore(new() { DataDirectory = directory });
            var service = Service(store, provider, new FakeOllamaModelCatalog());
            var team = await CreateTeamAsync(service);
            var run = service.StartCollaboration(team.Id, "Explain the task.", null, null)!;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(service.CancelCollaboration(team.Id, run.Id));
            var cancelled = await WaitForCollaborationAsync(service, team.Id, run.Id);
            Assert.Equal("Cancelled", cancelled.Status);
            Assert.Null(cancelled.Proposal);
            Assert.Empty(cancelled.Contributions);

            var failingDirectory = Path.Combine(directory, "failing");
            var failingService = Service(new CustomAgentStore(new() { DataDirectory = failingDirectory }),
                new FakeProvider((_, _) => throw new LlmProviderException("Sensitive Ollama detail")),
                new FakeOllamaModelCatalog());
            var failingTeam = await CreateTeamAsync(failingService);
            var failedRun = failingService.StartCollaboration(failingTeam.Id, "Explain the task.", null, null)!;
            var failed = await WaitForCollaborationAsync(failingService, failingTeam.Id, failedRun.Id);
            Assert.Equal("Failed", failed.Status);
            Assert.DoesNotContain("Sensitive Ollama detail", failed.Error);
            Assert.Null(failed.Proposal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidatesTeamOrderAndSupportsUpdates()
    {
        var directory = NewDirectory();
        try
        {
            var service = Service(new CustomAgentStore(new() { DataDirectory = directory }),
                new FakeProvider((_, _) => Task.FromResult("unused")), new FakeOllamaModelCatalog());
            var planner = await service.CreateAgentAsync(new("Planner", "", "Plan.", "qwen3:4b"), CancellationToken.None);
            var developer = await service.CreateAgentAsync(new("Developer", "", "Code.", "qwen3:4b"), CancellationToken.None);
            var reviewer = await service.CreateAgentAsync(new("Reviewer", "", "Review.", "qwen3:4b"), CancellationToken.None);
            Assert.Throws<StudioValidationException>(() => service.SaveTeam(null, new("Invalid", [
                new(planner.Id, "Developer", 0), new(developer.Id, "Planner", 1),
                new(reviewer.Id, "Reviewer", 2)
            ])));
            var members = new[]
            {
                new CustomAgentTeamMember(planner.Id, "Planner", 0),
                new CustomAgentTeamMember(developer.Id, "Developer", 1),
                new CustomAgentTeamMember(reviewer.Id, "Reviewer", 2)
            };
            var team = service.SaveTeam(null, new("Original", members))!;
            var updated = service.SaveTeam(team.Id, new("Renamed", members))!;
            Assert.Equal(team.Id, updated.Id);
            Assert.Equal(team.CreatedAt, updated.CreatedAt);
            Assert.Equal("Renamed", updated.Name);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedRunIsMarkedCancelledInsteadOfResumed()
    {
        var directory = NewDirectory();
        try
        {
            var store = new CustomAgentStore(new() { DataDirectory = directory });
            var service = Service(store, new FakeProvider((_, _) => Task.FromResult("unused")),
                new FakeOllamaModelCatalog());
            var team = await CreateTeamAsync(service);
            var run = new CustomAgentCollaborationRun(Guid.NewGuid(), team.Id, "An interrupted task.",
                "Running", "Developer", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
            var previous = new CustomAgentModelAttempt(Guid.NewGuid(), run.Id,
                team.Members.Single(member => member.Role == "Developer").AgentId, "Developer",
                "qwen2.5-coder:7b", "Configured model.", DateTimeOffset.UtcNow, true,
                ["qwen2.5-coder:7b", "qwen3.5:9b"], "Failed", "Generation");
            run = run with { ModelAttempts = [previous, previous with
                { Id = Guid.NewGuid(), Outcome = "Running", FailureKind = null }] };
            store.AddCollaborationRun(run);

            var recovered = service.GetCollaborationRun(team.Id, run.Id)!;
            Assert.Equal("Cancelled", recovered.Status);
            Assert.Null(recovered.Proposal);
            Assert.Contains("n’a pas été relancée", recovered.Error);
            Assert.Null(recovered.CurrentStep);
            Assert.Equal(JsonSerializer.Serialize(previous), JsonSerializer.Serialize(recovered.ModelAttempts![0]));
            Assert.Equal("Failed", recovered.ModelAttempts![1].Outcome);
            Assert.Equal("Infrastructure", recovered.ModelAttempts![1].FailureKind);
            Assert.Null(recovered.ModelAttempts![1].NextModel);
            Assert.Empty(new CustomAgentStore(new() { DataDirectory = directory })
                .GetCollaborationRun(team.Id, run.Id)!.Contributions);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<CustomAgentTeam> CreateTeamAsync(CustomAgentService service)
    {
        var planner = await service.CreateAgentAsync(new("Planner", "", "Plan.", "qwen3:4b"), CancellationToken.None);
        var developer = await service.CreateAgentAsync(new("Developer", "", "Code.", "qwen2.5-coder:7b"), CancellationToken.None);
        var reviewer = await service.CreateAgentAsync(new("Reviewer", "", "Review.", "qwen3:4b"), CancellationToken.None);
        return service.SaveTeam(null, new("Test team",
            [new(planner.Id, "Planner", 0), new(developer.Id, "Developer", 1), new(reviewer.Id, "Reviewer", 2)]))!;
    }

    private static async Task<CustomAgentCollaborationRun> WaitForCollaborationAsync(
        CustomAgentService service, Guid teamId, Guid runId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var run = service.GetCollaborationRun(teamId, runId)!;
            if (run.Status is not ("Queued" or "Running"))
                return run;
            await Task.Delay(20, timeout.Token);
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
