using System.Security.Cryptography;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class PreviewSessionManagerTests
{
    [Fact]
    public async Task VerifiedSourcesFlowThroughConsentReadinessAndCleanup()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new FakePreviewRuntime();
        var manager = Create(fixture, runtime);
        await manager.RecoverAsync(default);

        var prepared = await manager.PrepareAsync(mission, default);
        Assert.Equal(PreviewSessionStatus.AwaitingApproval, prepared.Status);
        Assert.Equal(prepared.Fingerprint, prepared.Plan.ComputeFingerprint());
        Assert.Equal(prepared, await manager.GetAsync(prepared.Id, default));
        Assert.False(await manager.StartAsync(prepared.Id, default));
        Assert.Equal(0, runtime.Starts);
        Assert.True(await manager.AuthorizeAsync(prepared.Id, prepared.Fingerprint, default));
        Assert.True(await manager.StartAsync(prepared.Id, default));
        Assert.Equal(PreviewSessionStatus.Ready, (await manager.GetAsync(prepared.Id, default))!.Status);
        Assert.Equal(1, runtime.Starts);
        Assert.False(await manager.StartAsync(prepared.Id, default));
        Assert.True(await manager.StopAsync(prepared.Id, default));
        Assert.True(await manager.StopAsync(prepared.Id, default));
        Assert.Equal(1, runtime.Stops);
        Assert.Equal(PreviewSessionStatus.Stopped, (await manager.GetAsync(prepared.Id, default))!.Status);
    }

    [Fact]
    public async Task ChangedImageRejectsApprovalAndCannotLaterReuseIt()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new FakePreviewRuntime();
        var manager = Create(fixture, runtime);
        await manager.RecoverAsync(default);
        var session = await manager.PrepareAsync(mission, default);
        runtime.ImageDigit = 'b';

        Assert.False(await manager.AuthorizeAsync(session.Id, session.Fingerprint, default));
        runtime.ImageDigit = 'a';
        Assert.False(await manager.AuthorizeAsync(session.Id, session.Fingerprint, default));
        Assert.False(await manager.StartAsync(session.Id, default));
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task StaleFingerprintDoesNotAuthorizeSession()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new FakePreviewRuntime();
        var manager = Create(fixture, runtime);
        await manager.RecoverAsync(default);
        var session = await manager.PrepareAsync(mission, default);

        Assert.False(await manager.AuthorizeAsync(session.Id, new string('A', 64), default));
        Assert.False(await manager.StartAsync(session.Id, default));
        Assert.Equal(0, runtime.Starts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModifiedSourceOrImageAfterApprovalPreventsStart(bool sourceChange)
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new FakePreviewRuntime();
        var manager = Create(fixture, runtime);
        await manager.RecoverAsync(default);
        var session = await manager.PrepareAsync(mission, default);
        Assert.True(await manager.AuthorizeAsync(session.Id, session.Fingerprint, default));
        if (sourceChange)
            await File.AppendAllTextAsync(Path.Combine(fixture.Workspaces.GetDirectory(mission),
                "frontend", "src", "App.tsx"), "\n// changed");
        else
            runtime.ImageDigit = 'b';

        if (sourceChange)
            await Assert.ThrowsAsync<StudioValidationException>(() => manager.StartAsync(session.Id, default));
        else
            Assert.False(await manager.StartAsync(session.Id, default));
        Assert.False(await manager.StartAsync(session.Id, default));
        Assert.Equal(PreviewSessionStatus.Failed, (await manager.GetAsync(session.Id, default))!.Status);
        Assert.Equal(0, runtime.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedHealthOrTimeoutCleansUpAndNeverBecomesReady(bool timeout)
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new FakePreviewRuntime { FrontendReady = false, Timeout = timeout };
        var manager = Create(fixture, runtime);
        await manager.RecoverAsync(default);
        var session = await manager.PrepareAsync(mission, default);
        Assert.True(await manager.AuthorizeAsync(session.Id, session.Fingerprint, default));

        if (timeout)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartAsync(session.Id, default));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(session.Id, default));
        Assert.Equal(1, runtime.Stops);
        Assert.Equal(PreviewSessionStatus.Failed, (await manager.GetAsync(session.Id, default))!.Status);
        Assert.False(await manager.StartAsync(session.Id, default));
    }

    [Fact]
    public async Task IncompleteCleanupBlocksStartsUntilRecovery()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new FakePreviewRuntime { FrontendReady = false, FailStop = true };
        var manager = Create(fixture, runtime);
        await manager.RecoverAsync(default);
        var session = await manager.PrepareAsync(mission, default);
        Assert.True(await manager.AuthorizeAsync(session.Id, session.Fingerprint, default));
        await Assert.ThrowsAsync<IOException>(() => manager.StartAsync(session.Id, default));
        var next = await manager.PrepareAsync(mission, default);
        Assert.True(await manager.AuthorizeAsync(next.Id, next.Fingerprint, default));

        await Assert.ThrowsAsync<PreviewUnavailableException>(() => manager.StartAsync(next.Id, default));
        runtime.FailStop = false;
        runtime.FrontendReady = true;
        await manager.RecoverAsync(default);
        Assert.True(await manager.StartAsync(next.Id, default));
        Assert.True(await manager.StopAsync(next.Id, default));
    }

    [Fact]
    public async Task ExpirationStopsAndCleansUpSession()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new FakePreviewRuntime();
        var clock = new ManualClock();
        var manager = Create(fixture, runtime, clock);
        await manager.RecoverAsync(default);
        var session = await manager.PrepareAsync(mission, default);
        Assert.True(await manager.AuthorizeAsync(session.Id, session.Fingerprint, default));
        Assert.True(await manager.StartAsync(session.Id, default));
        clock.Advance(TimeSpan.FromSeconds(session.Plan.MaximumSessionSeconds));

        await manager.ExpireAsync(default);
        Assert.Equal(1, runtime.Stops);
        Assert.Equal(PreviewSessionStatus.Expired, (await manager.GetAsync(session.Id, default))!.Status);
        Assert.True(await manager.StopAsync(session.Id, default));
        Assert.Equal(1, runtime.Stops);
    }

    [Fact]
    public async Task BrowserExitClosesSessionAndInvalidatesItsUrlDuringMaintenance()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new FakePreviewRuntime();
        var manager = Create(fixture, runtime);
        await manager.RecoverAsync(default);
        var session = await manager.PrepareAsync(mission, default);
        Assert.True(await manager.AuthorizeAsync(session.Id, session.Fingerprint, default));
        Assert.True(await manager.StartAsync(session.Id, default));
        runtime.Healthy = false;
        await manager.ExpireAsync(default);
        var stopped = (await manager.GetAsync(session.Id, default))!;
        Assert.Equal(PreviewSessionStatus.Failed, stopped.Status);
        Assert.Null(stopped.Url);
        Assert.False(stopped.Authorized);
        Assert.Equal(1, runtime.Stops);
        Assert.Contains((await manager.GetLogsAsync(session.Id, default))!, entry => entry.Event == "BrowserClosed");
    }

    [Fact]
    public async Task RestartDropsApprovalAndRequiresOrphanRecovery()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new FakePreviewRuntime();
        var manager = Create(fixture, runtime);
        await manager.RecoverAsync(default);
        var session = await manager.PrepareAsync(mission, default);
        Assert.True(await manager.AuthorizeAsync(session.Id, session.Fingerprint, default));
        var restarted = Create(fixture, runtime);

        Assert.Null(await restarted.GetAsync(session.Id, default));
        Assert.False(await restarted.StartAsync(session.Id, default));
        await restarted.RecoverAsync(default);
        Assert.False(await restarted.StartAsync(session.Id, default));
        Assert.Equal(2, runtime.Recoveries);
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task FailedOrphanRecoveryDoesNotPermitStartup()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new FakePreviewRuntime { FailRecovery = true };
        var manager = Create(fixture, runtime);
        await Assert.ThrowsAsync<IOException>(() => manager.RecoverAsync(default));
        var session = await manager.PrepareAsync(mission, default);
        Assert.True(await manager.AuthorizeAsync(session.Id, session.Fingerprint, default));

        await Assert.ThrowsAsync<PreviewUnavailableException>(() => manager.StartAsync(session.Id, default));
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task OneSessionPerMissionVersionAndConcurrentStartsConsumeOnce()
    {
        using var fixture = new WorkflowFixture(fullStack: true);
        var mission = await PreviewSourceVerifierTests.VerifiedAsync(fixture);
        var runtime = new FakePreviewRuntime();
        var manager = Create(fixture, runtime);
        await manager.RecoverAsync(default);
        var session = await manager.PrepareAsync(mission, default);
        await Assert.ThrowsAsync<StudioValidationException>(() => manager.PrepareAsync(mission, default));
        Assert.True(await manager.AuthorizeAsync(session.Id, session.Fingerprint, default));

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => manager.StartAsync(session.Id, default)));
        Assert.Single(results, result => result);
        Assert.Equal(1, runtime.Starts);
        await manager.StopAsync(session.Id, default);
    }

    private static PreviewSessionManager Create(WorkflowFixture fixture, FakePreviewRuntime runtime, TimeProvider? clock = null) =>
        new(new PreviewSourceVerifier(fixture.Store, fixture.Workspaces), runtime, new PreviewAuthorizationStore(), clock);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    internal sealed class FakePreviewRuntime : IPreviewRuntime
    {
        public bool IsAvailable => true;
        public char ImageDigit { get; set; } = 'a';
        public bool FrontendReady { get; set; } = true;
        public bool Timeout { get; set; }
        public bool FailStop { get; set; }
        public bool FailRecovery { get; set; }
        public bool Healthy { get; set; } = true;
        public int LifetimeSeconds { get; set; } = 600;
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Recoveries { get; private set; }

        public Task<PreviewPlan> CreatePlanAsync(PreviewSourceIdentity sources, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PreviewPlan(1, sources.MissionId, sources.WorkflowVersion,
                sources.ValidationFingerprint, [.. sources.Files], "fake:local", "sha256:" + new string(ImageDigit, 64),
                [new("fake", ["fake-command"])], new("fake", true, true, true, true, 1, 1024, 128),
                [], [new("sqlite", "/tmp/sqlite", "temporary")], LifetimeSeconds, "fake cleanup", 1));
        }

        public async Task<PreviewReadiness> StartAsync(Guid sessionId, PreviewPlan plan,
            PreviewSourceSnapshot snapshot, CancellationToken cancellationToken)
        {
            Starts++;
            Assert.Equal(plan.SourceFiles.Length, snapshot.Files.Length);
            foreach (var file in snapshot.Files)
                Assert.Equal(plan.SourceFiles.Single(item => item.Path == file.Path).Sha256,
                    Convert.ToHexString(SHA256.HashData(file.Content.AsSpan())));
            if (Timeout) await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new(true, FrontendReady);
        }

        public Task StopAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            Stops++;
            if (FailStop) throw new IOException("Simulated cleanup failure.");
            return Task.CompletedTask;
        }

        public Task CleanupOrphansAsync(CancellationToken cancellationToken)
        {
            Recoveries++;
            if (FailRecovery) throw new IOException("Simulated orphan recovery failure.");
            return Task.CompletedTask;
        }

        public Task<bool> IsSessionHealthyAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(Healthy);
    }
}
