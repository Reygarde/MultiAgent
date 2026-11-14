using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public enum PreviewAuthorizationStatus { AwaitingApproval, Authorized, Consumed, Invalidated }

public sealed record PreviewAuthorizationSnapshot(
    Guid SessionId, PreviewPlan Plan, string Fingerprint, PreviewAuthorizationStatus Status);

public sealed class PreviewAuthorizationStore
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, PreviewAuthorizationSnapshot> sessions = [];

    public PreviewAuthorizationSnapshot Prepare(PreviewPlan plan)
    {
        var fingerprint = plan.ComputeFingerprint();
        lock (sync)
        {
            var session = new PreviewAuthorizationSnapshot(
                Guid.NewGuid(), plan, fingerprint, PreviewAuthorizationStatus.AwaitingApproval);
            sessions.Add(session.SessionId, session);
            return session;
        }
    }

    public PreviewAuthorizationSnapshot? Get(Guid sessionId)
    {
        lock (sync)
            return sessions.GetValueOrDefault(sessionId);
    }

    public void Invalidate(Guid sessionId)
    {
        lock (sync)
        {
            if (sessions.TryGetValue(sessionId, out var session) &&
                session.Status is PreviewAuthorizationStatus.AwaitingApproval or PreviewAuthorizationStatus.Authorized)
                sessions[sessionId] = session with { Status = PreviewAuthorizationStatus.Invalidated };
        }
    }

    public bool Authorize(Guid sessionId, string approvedFingerprint, PreviewPlan currentPlan)
    {
        var currentFingerprint = FingerprintOrInvalidate(sessionId, currentPlan);
        lock (sync)
        {
            if (!sessions.TryGetValue(sessionId, out var session) ||
                session.Status != PreviewAuthorizationStatus.AwaitingApproval)
                return false;
            if (!string.Equals(session.Fingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                sessions[sessionId] = session with { Status = PreviewAuthorizationStatus.Invalidated };
                return false;
            }
            if (!string.Equals(session.Fingerprint, approvedFingerprint, StringComparison.Ordinal))
                return false;
            sessions[sessionId] = session with { Status = PreviewAuthorizationStatus.Authorized };
            return true;
        }
    }

    public bool Consume(Guid sessionId, PreviewPlan currentPlan)
    {
        var currentFingerprint = FingerprintOrInvalidate(sessionId, currentPlan);
        lock (sync)
        {
            if (!sessions.TryGetValue(sessionId, out var session) ||
                session.Status != PreviewAuthorizationStatus.Authorized)
                return false;
            if (!string.Equals(session.Fingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                sessions[sessionId] = session with { Status = PreviewAuthorizationStatus.Invalidated };
                return false;
            }
            sessions[sessionId] = session with { Status = PreviewAuthorizationStatus.Consumed };
            return true;
        }
    }

    private string FingerprintOrInvalidate(Guid sessionId, PreviewPlan plan)
    {
        try
        {
            return plan.ComputeFingerprint();
        }
        catch (StudioValidationException)
        {
            Invalidate(sessionId);
            throw;
        }
    }
}
