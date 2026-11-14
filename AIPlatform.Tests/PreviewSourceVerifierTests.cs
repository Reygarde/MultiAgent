using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using System.Security.Cryptography;

namespace AIPlatform.Tests;

public sealed class PreviewSourceVerifierTests
{
    [Fact]
    public async Task ReadsExactVerifiedSourcesWithoutGrantingPreviewOrRunningAnything()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await VerifiedAsync(fixture);
        var before = fixture.Store.Get(mission)!;
        var sandboxCalls = fixture.Sandbox.Calls.Count;
        var providerCalls = fixture.Provider.Requests.Count;
        var verifier = new PreviewSourceVerifier(fixture.Store, fixture.Workspaces);

        var identity = await verifier.ReadAsync(mission, default);

        Assert.Equal(mission, identity.MissionId);
        Assert.Equal(before.Workflow!.Version, identity.WorkflowVersion);
        Assert.Equal(before.ApprovalFingerprint, identity.ValidationFingerprint);
        Assert.Equal(before.Workflow.Revisions!.Last().ExecutionPlan!.Canonical!.Files.Count, identity.Files.Count);
        Assert.Equal(sandboxCalls, fixture.Sandbox.Calls.Count);
        Assert.Equal(providerCalls, fixture.Provider.Requests.Count);
        Assert.Same(before, fixture.Store.Get(mission));
    }

    [Fact]
    public async Task RejectsVerifiedMissionAfterSourceChange()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await VerifiedAsync(fixture);
        var path = Path.Combine(fixture.Workspaces.GetDirectory(mission), "frontend", "src", "App.tsx");
        await File.AppendAllTextAsync(path, "\n// changed after validation");

        await Assert.ThrowsAsync<StudioValidationException>(
            () => new PreviewSourceVerifier(fixture.Store, fixture.Workspaces).ReadAsync(mission, default));
    }

    [Fact]
    public async Task CapturesApprovedBytesIndependentlyOfLaterWorkspaceChanges()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await VerifiedAsync(fixture);
        var verifier = new PreviewSourceVerifier(fixture.Store, fixture.Workspaces);
        var snapshot = await verifier.CaptureAsync(mission, default);
        var file = snapshot.Files.Single(item => item.Path == "frontend/src/App.tsx");
        var expected = snapshot.Identity.Files.Single(item => item.Path == file.Path);
        var path = Path.Combine(fixture.Workspaces.GetDirectory(mission), "frontend", "src", "App.tsx");
        await File.AppendAllTextAsync(path, "\n// changed after capture");

        Assert.Equal(expected.Sha256, Convert.ToHexString(SHA256.HashData(file.Content.AsSpan())));
        await Assert.ThrowsAsync<StudioValidationException>(() => verifier.CaptureAsync(mission, default));
    }

    [Fact]
    public async Task RejectsUnexpectedWorkspaceFile()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await VerifiedAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspaces.GetDirectory(mission), "unexpected.txt"), "extra");

        await Assert.ThrowsAsync<StudioValidationException>(
            () => new PreviewSourceVerifier(fixture.Store, fixture.Workspaces).ReadAsync(mission, default));
    }

    [Fact]
    public async Task RejectsAlteredRevisionEvenWhenWorkspaceStillMatchesPlan()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await VerifiedAsync(fixture);
        fixture.Store.Update(mission, current =>
        {
            var workflow = current.Workflow!;
            var revisions = workflow.Revisions!;
            var revision = revisions.Last();
            var files = revision.Files.Select((file, index) => index == 0
                ? file with { Content = file.Content + "\nchanged" } : file).ToArray();
            return current with
            {
                Workflow = workflow with { Revisions = [.. revisions.SkipLast(1), revision with { Files = files }] }
            };
        });

        await Assert.ThrowsAsync<StudioValidationException>(
            () => new PreviewSourceVerifier(fixture.Store, fixture.Workspaces).ReadAsync(mission, default));
    }

    [Fact]
    public async Task RejectsAlteredCanonicalPlan()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await VerifiedAsync(fixture);
        fixture.Store.Update(mission, current =>
        {
            var workflow = current.Workflow!;
            var revisions = workflow.Revisions!;
            var revision = revisions.Last();
            var plan = revision.ExecutionPlan!;
            var altered = plan with { Canonical = plan.Canonical! with { CpuCount = plan.Canonical.CpuCount + 1 } };
            return current with
            {
                Workflow = workflow with { Revisions = [.. revisions.SkipLast(1), revision with { ExecutionPlan = altered }] }
            };
        });

        await Assert.ThrowsAsync<StudioValidationException>(
            () => new PreviewSourceVerifier(fixture.Store, fixture.Workspaces).ReadAsync(mission, default));
    }

    [Fact]
    public async Task HonorsCancellation()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await VerifiedAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new PreviewSourceVerifier(fixture.Store, fixture.Workspaces).ReadAsync(mission, cancellation.Token));
    }

    [Fact]
    public async Task RejectsUnverifiedMissionEvenIfItsWorkspaceExists()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = fixture.Store.Submit(new("Unverified preview source", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);

        await Assert.ThrowsAsync<StudioValidationException>(
            () => new PreviewSourceVerifier(fixture.Store, fixture.Workspaces).ReadAsync(mission.Id, default));
        Assert.Empty(fixture.Sandbox.Calls);
    }

    internal static async Task<Guid> VerifiedAsync(WorkflowFixture fixture)
    {
        fixture.Sandbox.Result = stage => new(stage, "Succeeded", 0, "Mock validation",
            TotalTests: stage is "BackendTests" or "FrontendTests" or "Integration" ? 1 : null,
            FailedTests: stage is "BackendTests" or "FrontendTests" or "Integration" ? 0 : null);
        var mission = fixture.Store.Submit(new("Verified preview source", MissionKind.Project,
            TemplateId: FullStackTemplate.Id));
        await fixture.Processor.ProcessAsync(mission.Id, default);
        await fixture.Approve(mission.Id);
        await fixture.Processor.ProcessAsync(mission.Id, default);
        Assert.Equal(MissionStatus.Verified, fixture.Store.Get(mission.Id)!.Status);
        return mission.Id;
    }
}
