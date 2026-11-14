using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class PreviewAuthorizationTests
{
    [Fact]
    public void CanonicalFingerprintIsStableAndBindsSourcesAndConfiguration()
    {
        var plan = Plan();
        var same = plan with { SourceFiles = [.. plan.SourceFiles] };
        var changedSources = plan with { SourceFiles = [new("backend/Api.cs", Hash('B'))] };
        var changedCommand = plan with { Commands = [new("api", ["dotnet", "different.dll"])] };
        var changedImage = plan with { ImageDigest = "sha256:" + new string('b', 64) };
        var changedLimits = plan with { Isolation = plan.Isolation with { MemoryMegabytes = 2048 } };
        var changedNetwork = plan with { Isolation = plan.Isolation with { NetworkPolicy = "changed" } };
        var changedExposure = plan with { Exposures = [new("proxy", "127.0.0.1:12346", "http", "/")] };
        var changedStorage = plan with { TemporaryStorage = [new("sqlite", "/tmp/other", "temporary")] };
        var changedLifetime = plan with { MaximumSessionSeconds = 601 };
        var changedCleanup = plan with { CleanupPolicy = "different cleanup" };

        Assert.Equal(plan.ComputeFingerprint(), same.ComputeFingerprint());
        Assert.Equal(plan.SourceFingerprint, same.SourceFingerprint);
        Assert.NotEqual(plan.SourceFingerprint, changedSources.SourceFingerprint);
        foreach (var changed in new[] { changedSources, changedCommand, changedImage, changedLimits,
                     changedNetwork, changedExposure, changedStorage, changedLifetime, changedCleanup })
            Assert.NotEqual(plan.ComputeFingerprint(), changed.ComputeFingerprint());
    }

    [Fact]
    public void RequiresExplicitApprovalAndNeverUsesValidationFingerprintAsConsent()
    {
        var store = new PreviewAuthorizationStore();
        var plan = Plan();
        var session = store.Prepare(plan);

        Assert.False(store.Consume(session.SessionId, plan));
        Assert.False(store.Authorize(session.SessionId, plan.ValidationFingerprint, plan));
        Assert.Equal(PreviewAuthorizationStatus.AwaitingApproval, store.Get(session.SessionId)!.Status);
        Assert.True(store.Authorize(session.SessionId, session.Fingerprint, plan));
        Assert.True(store.Consume(session.SessionId, plan));
        Assert.False(store.Consume(session.SessionId, plan));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedSourceOrConfigurationInvalidatesApproval(bool sourceChange)
    {
        var store = new PreviewAuthorizationStore();
        var plan = Plan();
        var session = store.Prepare(plan);
        Assert.True(store.Authorize(session.SessionId, session.Fingerprint, plan));
        var changed = sourceChange
            ? plan with { SourceFiles = [new("backend/Api.cs", Hash('B'))] }
            : plan with { Isolation = plan.Isolation with { CpuCount = 2 } };

        Assert.False(store.Consume(session.SessionId, changed));
        Assert.False(store.Consume(session.SessionId, plan));
        Assert.Equal(PreviewAuthorizationStatus.Invalidated, store.Get(session.SessionId)!.Status);
    }

    [Fact]
    public void StalePlanCannotBeApprovedEvenIfOldFingerprintIsSupplied()
    {
        var store = new PreviewAuthorizationStore();
        var plan = Plan();
        var session = store.Prepare(plan);
        var changed = plan with { ImageDigest = "sha256:" + new string('b', 64) };

        Assert.False(store.Authorize(session.SessionId, session.Fingerprint, changed));
        Assert.False(store.Authorize(session.SessionId, session.Fingerprint, plan));
        Assert.Equal(PreviewAuthorizationStatus.Invalidated, store.Get(session.SessionId)!.Status);
    }

    [Fact]
    public async Task ConcurrentConsumptionSucceedsOnlyOnce()
    {
        var store = new PreviewAuthorizationStore();
        var plan = Plan();
        var session = store.Prepare(plan);
        Assert.True(store.Authorize(session.SessionId, session.Fingerprint, plan));

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => store.Consume(session.SessionId, plan))));

        Assert.Single(results, result => result);
        Assert.Equal(PreviewAuthorizationStatus.Consumed, store.Get(session.SessionId)!.Status);
    }

    [Fact]
    public void InvalidationIsPermanentAndDoesNotAffectOtherSessions()
    {
        var store = new PreviewAuthorizationStore();
        var plan = Plan();
        var first = store.Prepare(plan);
        var second = store.Prepare(plan);
        Assert.True(store.Authorize(first.SessionId, first.Fingerprint, plan));
        store.Invalidate(first.SessionId);
        store.Invalidate(first.SessionId);

        Assert.False(store.Consume(first.SessionId, plan));
        Assert.Equal(PreviewAuthorizationStatus.AwaitingApproval, store.Get(second.SessionId)!.Status);
    }

    [Fact]
    public void ApprovalCannotBeReusedForAnotherSessionOrAfterRestart()
    {
        var store = new PreviewAuthorizationStore();
        var plan = Plan();
        var first = store.Prepare(plan);
        var second = store.Prepare(plan);
        Assert.True(store.Authorize(first.SessionId, first.Fingerprint, plan));

        Assert.False(store.Consume(second.SessionId, plan));
        Assert.False(new PreviewAuthorizationStore().Consume(first.SessionId, plan));
        Assert.True(store.Consume(first.SessionId, plan));
    }

    [Fact]
    public void RejectsUnresolvedImageAndNonCanonicalFileOrder()
    {
        var plan = Plan();
        Assert.Throws<StudioValidationException>(() =>
            (plan with { ImageDigest = "latest" }).ComputeFingerprint());
        Assert.Throws<StudioValidationException>(() =>
            (plan with { SourceFiles = [new("z", Hash('A')), new("a", Hash('B'))] }).ComputeFingerprint());
    }

    [Fact]
    public void InvalidCurrentPlanRevokesExistingApproval()
    {
        var store = new PreviewAuthorizationStore();
        var plan = Plan();
        var session = store.Prepare(plan);
        Assert.True(store.Authorize(session.SessionId, session.Fingerprint, plan));

        Assert.Throws<StudioValidationException>(() =>
            store.Consume(session.SessionId, plan with { ImageDigest = "unavailable" }));
        Assert.False(store.Consume(session.SessionId, plan));
    }

    private static PreviewPlan Plan() => new(
        1, Guid.Parse("11111111-2222-4333-8444-555555555555"), 9, Hash('C'),
        [new("backend/Api.cs", Hash('A'))], "example:local", "sha256:" + new string('a', 64),
        [new("api", ["dotnet", "Api.dll"])],
        new("isolated", true, true, true, true, 1, 1024, 128),
        [new("proxy", "127.0.0.1:12345", "http", "/")],
        [new("sqlite", "/tmp/data", "temporary")],
        600, "remove temporary resources");

    private static string Hash(char digit) => new(digit, 64);
}
