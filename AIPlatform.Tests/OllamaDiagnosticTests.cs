using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class OllamaDiagnosticTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordsExactWireBodiesAndOperationsOnlyWithExplicitConsent(bool enabled)
    {
        var directory = NewDirectory();
        try
        {
            var store = new OllamaDiagnosticStore(new() { DataDirectory = directory }, TimeProvider.System);
            var context = new LlmDiagnosticContext(Guid.NewGuid(), Guid.NewGuid(), "integration", Guid.NewGuid());
            const string response = " { \"done\": true, \"done_reason\":\"stop\", \"eval_count\":27,\"message\":{\"content\":\"Answer\"} }\n";
            byte[]? sent = null;
            using var handler = new StubHttpHandler(async (message, token) =>
            {
                sent = await message.Content!.ReadAsByteArrayAsync(token);
                return StubHttpHandler.Json(response);
            });
            using var client = new HttpClient(handler);
            using var provider = new OllamaProvider(client, new(), store);
            var request = TestSupport.Request() with { DiagnosticContext = enabled ? context : null };
            if (enabled && !OperatingSystem.IsWindows())
            {
                await Assert.ThrowsAsync<PlatformNotSupportedException>(() => provider.CompleteAsync(request, CancellationToken.None));
                return;
            }
            Assert.Equal("Answer", await provider.CompleteAsync(request, CancellationToken.None));
            var root = Path.Combine(directory, "ollama-traces");
            if (!enabled)
            {
                Assert.False(Directory.Exists(root));
                return;
            }
            store.RecordResult(context, "Extracted", [new("src/App.tsx", "Fix", "before", "after")]);
            store.RecordResult(context, "Applied");
            store.RecordResult(context, "RolledBack");
            store.RecordResult(context, "Failed");
            var file = Assert.Single(Directory.GetFiles(root, "*.json"));
            using var json = JsonDocument.Parse(File.ReadAllBytes(file));
            var entry = json.RootElement;
            Assert.Equal(sent, Encoding.UTF8.GetBytes(entry.GetProperty("requestBody").GetString()!));
            Assert.Equal(response, entry.GetProperty("responseBody").GetString());
            Assert.Equal(request.Model.Name, entry.GetProperty("model").GetString());
            Assert.Equal(context.AttemptId, entry.GetProperty("context").GetProperty("attemptId").GetGuid());
            Assert.Equal(context.TaskId, entry.GetProperty("context").GetProperty("taskId").GetString());
            Assert.Equal(200, entry.GetProperty("httpStatus").GetInt32());
            Assert.Equal("after", entry.GetProperty("operations")[0].GetProperty("after").GetString());
            Assert.True(entry.GetProperty("applied").GetBoolean());
            Assert.True(entry.GetProperty("rolledBack").GetBoolean());
            Assert.DoesNotContain("authorization", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
            if (OperatingSystem.IsWindows())
            {
                var acl = new DirectoryInfo(root).GetAccessControl();
                Assert.True(acl.AreAccessRulesProtected);
                var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
                using var identity = WindowsIdentity.GetCurrent();
                foreach (var rule in rules) Assert.Equal(identity.User, rule.IdentityReference);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OversizedCaptureStopsBeforeSendingCode()
    {
        var directory = NewDirectory();
        try
        {
            var store = new OllamaDiagnosticStore(new() { DataDirectory = directory, OllamaTraceMaxBytes = 1024 }, TimeProvider.System);
            var calls = 0;
            using var handler = new StubHttpHandler((_, _) =>
            {
                calls++;
                return Task.FromResult(StubHttpHandler.Json("{}"));
            });
            using var client = new HttpClient(handler);
            using var provider = new OllamaProvider(client, new(), store);
            var request = TestSupport.Request() with
            {
                Messages = [new("user", new string('x', 2048))],
                DiagnosticContext = new(Guid.NewGuid(), Guid.NewGuid(), "integration", Guid.NewGuid())
            };
            if (OperatingSystem.IsWindows())
                await Assert.ThrowsAsync<LlmDiagnosticException>(() => provider.CompleteAsync(request, CancellationToken.None));
            else
                await Assert.ThrowsAsync<PlatformNotSupportedException>(() => provider.CompleteAsync(request, CancellationToken.None));
            Assert.Equal(0, calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void BoundsSizeRetentionAndDeletionWithoutTouchingOtherWorkspaces()
    {
        var directory = NewDirectory();
        try
        {
            var options = new StudioOptions { DataDirectory = directory, OllamaTraceMaxFiles = 2, OllamaTraceMaxBytes = 2048 };
            var store = new OllamaDiagnosticStore(options, TimeProvider.System);
            var first = new LlmDiagnosticContext(Guid.NewGuid(), Guid.NewGuid(), "one", Guid.NewGuid());
            if (!OperatingSystem.IsWindows())
            {
                Assert.Throws<PlatformNotSupportedException>(() => store.RecordRequest(first, "model", "{}"u8.ToArray()));
                return;
            }
            var second = first with { AttemptId = Guid.NewGuid() };
            var third = first with { WorkspaceId = Guid.NewGuid(), AttemptId = Guid.NewGuid() };
            store.RecordRequest(first, "model", "{}"u8.ToArray());
            Assert.Throws<StudioValidationException>(() => store.DeleteWorkspace(first.WorkspaceId));
            store.RecordResult(first, "Failed");
            store.RecordRequest(second, "model", "{}"u8.ToArray());
            store.RecordResult(second, "Failed");
            store.RecordRequest(third, "model", "{}"u8.ToArray());
            store.RecordResult(third, "Failed");
            var root = Path.Combine(directory, "ollama-traces");
            Assert.Equal(2, Directory.GetFiles(root, "*.json").Length);
            Assert.DoesNotContain(Directory.GetFiles(root), file => file.Contains(first.AttemptId.ToString("N")));
            store.DeleteWorkspace(first.WorkspaceId);
            var retained = Assert.Single(Directory.GetFiles(root, "*.json"));
            Assert.Contains(third.WorkspaceId.ToString("N"), retained);
            File.SetLastWriteTimeUtc(retained, DateTime.UtcNow.AddDays(-8));
            store.PruneExpired();
            Assert.Empty(Directory.GetFiles(root, "*.json"));
            Assert.Throws<LlmDiagnosticException>(() => store.RecordRequest(first, "model", new byte[4096]));
            Assert.Empty(Directory.GetFiles(root, "*.json"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "AIPlatformOllamaTrace-" + Guid.NewGuid().ToString("N"));
}
