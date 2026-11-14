using System.Collections.Concurrent;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public enum PreviewSessionStatus { AwaitingApproval, Starting, Ready, Stopping, Stopped, Failed, Expired }

public sealed record PreviewSession(
    Guid Id, PreviewPlan Plan, string Fingerprint, PreviewSessionStatus Status,
    bool Authorized, DateTimeOffset? ExpiresAt = null, string? Url = null);

public sealed record PreviewReadiness(bool BackendReady, bool FrontendReady, string? Url = null);
public sealed record PreviewLogEvent(DateTimeOffset Timestamp, string Event);

public sealed class PreviewUnavailableException(string message) : Exception(message);

public interface IPreviewRuntime
{
    bool IsAvailable { get; }
    Task<PreviewPlan> CreatePlanAsync(PreviewSourceIdentity sources, CancellationToken cancellationToken);
    Task<PreviewReadiness> StartAsync(Guid sessionId, PreviewPlan plan,
        PreviewSourceSnapshot snapshot, CancellationToken cancellationToken);
    Task StopAsync(Guid sessionId, CancellationToken cancellationToken);
    Task CleanupOrphansAsync(CancellationToken cancellationToken);
    Task<bool> IsSessionHealthyAsync(Guid sessionId, CancellationToken cancellationToken) => Task.FromResult(true);
}

public sealed class PreviewSessionManager(
    PreviewSourceVerifier sources, IPreviewRuntime runtime, PreviewAuthorizationStore authorizations,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, PreviewSession> sessions = new();
    private readonly Dictionary<Guid, Queue<PreviewLogEvent>> logs = [];
    private bool recoveryConfirmed;

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (sessions.Values.Any(session => session.Status is PreviewSessionStatus.Starting or
                PreviewSessionStatus.Ready or PreviewSessionStatus.Stopping))
                throw new InvalidOperationException("Active previews must stop before orphan recovery.");
            recoveryConfirmed = false;
            await runtime.CleanupOrphansAsync(cancellationToken);
            recoveryConfirmed = true;
        }
        finally { gate.Release(); }
    }

    public async Task<PreviewSession> PrepareAsync(Guid missionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!runtime.IsAvailable)
                throw new PreviewUnavailableException("The secure preview runtime is unavailable.");
            if (sessions.Values.Any(session => session.Status is PreviewSessionStatus.AwaitingApproval or
                PreviewSessionStatus.Starting or PreviewSessionStatus.Ready or PreviewSessionStatus.Stopping))
                throw new StudioValidationException("Only one preview session may be active at a time.");
            var identity = await sources.ReadAsync(missionId, cancellationToken);
            var plan = await CreatePlanAsync(identity, cancellationToken);
            var authorization = authorizations.Prepare(plan);
            var session = new PreviewSession(authorization.SessionId, plan, authorization.Fingerprint,
                PreviewSessionStatus.AwaitingApproval, false);
            if (!sessions.TryAdd(session.Id, session))
                throw new InvalidOperationException("The preview session identifier already exists.");
            logs.Add(session.Id, new Queue<PreviewLogEvent>());
            Record(session.Id, "Prepared");
            return session;
        }
        finally { gate.Release(); }
    }

    public Task<PreviewSession?> GetAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(sessions.GetValueOrDefault(sessionId));
    }

    public async Task<IReadOnlyList<PreviewLogEvent>?> GetLogsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return logs.TryGetValue(sessionId, out var events) ? events.ToArray() : null; }
        finally { gate.Release(); }
    }

    public async Task<bool> AuthorizeAsync(Guid sessionId, string fingerprint, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!sessions.TryGetValue(sessionId, out var session) ||
                session.Status != PreviewSessionStatus.AwaitingApproval || session.Authorized)
                return false;
            var checkedPlan = false;
            try
            {
                var identity = await sources.ReadAsync(session.Plan.MissionId, cancellationToken);
                var current = await CreatePlanAsync(identity, cancellationToken);
                checkedPlan = true;
                var approved = authorizations.Authorize(sessionId, fingerprint, current);
                if (authorizations.Get(sessionId)?.Status == PreviewAuthorizationStatus.Invalidated)
                {
                    sessions[sessionId] = session with { Status = PreviewSessionStatus.Failed };
                    Record(sessionId, "PlanChanged");
                }
                else if (approved)
                {
                    sessions[sessionId] = session with { Authorized = true };
                    Record(sessionId, "Authorized");
                }
                return approved;
            }
            finally
            {
                if (!checkedPlan)
                {
                    authorizations.Invalidate(sessionId);
                    sessions[sessionId] = session with { Status = PreviewSessionStatus.Failed };
                    Record(sessionId, "PlanCheckFailed");
                }
            }
        }
        finally { gate.Release(); }
    }

    public async Task<bool> StartAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!sessions.TryGetValue(sessionId, out var session) ||
                session.Status != PreviewSessionStatus.AwaitingApproval || !session.Authorized)
                return false;
            if (!recoveryConfirmed)
                throw new PreviewUnavailableException("Preview cleanup has not been confirmed.");
            var checkedPlan = false;
            PreviewSourceSnapshot snapshot;
            try
            {
                snapshot = await sources.CaptureAsync(session.Plan.MissionId, cancellationToken);
                var current = await CreatePlanAsync(snapshot.Identity, cancellationToken);
                var latest = await sources.ReadAsync(session.Plan.MissionId, cancellationToken);
                if (latest.WorkflowVersion != snapshot.Identity.WorkflowVersion ||
                    latest.ValidationFingerprint != snapshot.Identity.ValidationFingerprint ||
                    !latest.Files.SequenceEqual(snapshot.Identity.Files))
                    throw new StudioValidationException("Verified sources changed before preview startup.");
                checkedPlan = true;
                if (!authorizations.Consume(sessionId, current))
                {
                    sessions[sessionId] = session with { Status = PreviewSessionStatus.Failed, Authorized = false };
                    Record(sessionId, "PlanChanged");
                    return false;
                }
            }
            finally
            {
                if (!checkedPlan)
                {
                    authorizations.Invalidate(sessionId);
                    sessions[sessionId] = session with { Status = PreviewSessionStatus.Failed, Authorized = false };
                    Record(sessionId, "PlanCheckFailed");
                }
            }

            var starting = session with
            {
                Status = PreviewSessionStatus.Starting, Authorized = false,
                ExpiresAt = clock.GetUtcNow().AddSeconds(session.Plan.MaximumSessionSeconds)
            };
            sessions[sessionId] = starting;
            Record(sessionId, "Starting");
            var ready = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(session.Plan.StartupTimeoutSeconds));
                var health = await runtime.StartAsync(sessionId, session.Plan, snapshot, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (!health.BackendReady || !health.FrontendReady || clock.GetUtcNow() >= starting.ExpiresAt)
                    throw new InvalidOperationException("Both preview services must pass readiness checks before the session expires.");
                sessions[sessionId] = starting with { Status = PreviewSessionStatus.Ready, Url = health.Url };
                Record(sessionId, "Ready");
                ready = true;
                return true;
            }
            finally
            {
                if (!ready)
                {
                    sessions[sessionId] = starting with { Status = PreviewSessionStatus.Failed };
                    Record(sessionId, "StartFailed");
                    await CleanupAsync(sessionId);
                }
            }
        }
        finally { gate.Release(); }
    }

    public async Task<bool> StopAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!sessions.TryGetValue(sessionId, out var session)) return false;
            if (session.Status is PreviewSessionStatus.Stopped or PreviewSessionStatus.Expired) return true;
            if (session.Status == PreviewSessionStatus.AwaitingApproval)
            {
                authorizations.Invalidate(sessionId);
                sessions[sessionId] = session with { Status = PreviewSessionStatus.Stopped, Authorized = false };
                Record(sessionId, "Stopped");
                return true;
            }
            await StopCoreAsync(session, PreviewSessionStatus.Stopped);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task ExpireAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var session in sessions.Values.Where(session => session.Status == PreviewSessionStatus.Ready).ToArray())
            {
                if (session.ExpiresAt <= clock.GetUtcNow())
                    await StopCoreAsync(session, PreviewSessionStatus.Expired);
                else if (!await runtime.IsSessionHealthyAsync(session.Id, cancellationToken))
                {
                    Record(session.Id, "BrowserClosed");
                    await StopCoreAsync(session, PreviewSessionStatus.Failed);
                }
            }
        }
        finally { gate.Release(); }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        foreach (var id in sessions.Values.Where(session => session.Status is
                     PreviewSessionStatus.AwaitingApproval or PreviewSessionStatus.Starting or
                     PreviewSessionStatus.Ready or PreviewSessionStatus.Stopping or
                     PreviewSessionStatus.Failed).Select(session => session.Id).ToArray())
            await StopAsync(id, cancellationToken);
    }

    private async Task StopCoreAsync(PreviewSession session, PreviewSessionStatus finalStatus)
    {
        sessions[session.Id] = session with { Status = PreviewSessionStatus.Stopping, Url = null };
        Record(session.Id, "Stopping");
        try
        {
            await CleanupAsync(session.Id);
            sessions[session.Id] = session with { Status = finalStatus, Authorized = false, Url = null };
            Record(session.Id, finalStatus.ToString());
        }
        finally
        {
            if (sessions[session.Id].Status == PreviewSessionStatus.Stopping)
                sessions[session.Id] = session with { Status = PreviewSessionStatus.Failed, Authorized = false, Url = null };
        }
    }

    private async Task CleanupAsync(Guid sessionId)
    {
        var completed = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await runtime.StopAsync(sessionId, timeout.Token);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                recoveryConfirmed = false;
                Record(sessionId, "CleanupFailed");
            }
        }
    }

    private async Task<PreviewPlan> CreatePlanAsync(PreviewSourceIdentity identity, CancellationToken cancellationToken)
    {
        var plan = await runtime.CreatePlanAsync(identity, cancellationToken);
        if (plan.MissionId != identity.MissionId || plan.WorkflowVersion != identity.WorkflowVersion ||
            plan.ValidationFingerprint != identity.ValidationFingerprint || !plan.SourceFiles.SequenceEqual(identity.Files))
            throw new StudioValidationException("The preview plan does not match the verified sources.");
        _ = plan.ComputeFingerprint();
        return plan;
    }

    private void Record(Guid sessionId, string eventName)
    {
        var events = logs[sessionId];
        if (events.Count == 64) events.Dequeue();
        events.Enqueue(new(clock.GetUtcNow(), eventName));
    }
}
