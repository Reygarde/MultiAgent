using AIPlatform.Core.Studio;

namespace AIPlatform.PreviewSupervisor;

public static class SupervisorPolicy
{
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(170);
    public static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ForcedStopTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan StateQueryTimeout = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DestroyTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan CleanupMargin = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinimumHeartbeatInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan BootEpochTolerance = TimeSpan.FromSeconds(2);
    public const int ProtocolVersion = 2;
    public const int MaximumIpcFrameBytes = 8192;
    public const int MaximumJournalBytes = 16 * 1024 * 1024;
    public const int MaximumJournalRecordBytes = 16 * 1024;
    public const int MaximumRecentCommands = 256;
}

public enum SupervisedSessionState
{
    Provisioning,
    Provisioned,
    Running,
    Stopping,
    CleanupFailed,
    Stopped,
    Expired,
}

public enum SupervisorTerminalState
{
    Stopped,
    Expired,
}

public enum SupervisorCleanupFailure
{
    ResourceLookupFailed,
    LeaseMismatch,
    StateQueryFailed,
    StopUnconfirmed,
    DestroyUnconfirmed,
    JournalFailure,
}

public enum SupervisorCommand
{
    CreateSession,
    GetStatus,
    Heartbeat,
    Stop,
    RetryCleanup,
    ReserveSocketChannel,
}

public sealed record SupervisorIpcRequest(
    int Version,
    Guid RequestId,
    SupervisorCommand Command,
    Guid? SessionId,
    ulong Sequence,
    HyperVPreviewVmLease? Lease = null,
    HyperVPreviewChannelRole? ChannelRole = null);

public sealed record SupervisedSessionSnapshot(
    Guid SessionId,
    SupervisedSessionState State,
    Guid? VmId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    ulong LastCommandSequence,
    SupervisorCleanupFailure? CleanupFailure)
{
    public List<HyperVPreviewChannelRole> ReservedChannels { get; init; } = [];
}

public sealed record SupervisorIpcResponse(
    int Version,
    Guid RequestId,
    bool Accepted,
    string? ErrorCode,
    SupervisedSessionSnapshot? Session);

public sealed record SupervisorSessionRecord(
    long JournalSequence,
    int Revision,
    Guid SessionId,
    Guid CreationRequestId,
    HyperVPreviewVmLease? Lease,
    SupervisedSessionState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset BootEpochUtc,
    long CreatedUptimeMilliseconds,
    long ExpiresAtUptimeMilliseconds,
    ulong LastCommandSequence,
    List<Guid> RecentCommandIds,
    long LastHeartbeatUptimeMilliseconds,
    SupervisorTerminalState? RequestedTerminalState,
    SupervisorCleanupFailure? CleanupFailure)
{
    public List<HyperVPreviewChannelRole> ReservedChannels { get; init; } = [];
}

public sealed record SupervisorOptions(TimeSpan MaximumLifetime)
{
    public static SupervisorOptions Default { get; } = new(SupervisorPolicy.MaximumLifetime);

    public void Validate()
    {
        if (MaximumLifetime <= TimeSpan.Zero ||
            MaximumLifetime > SupervisorPolicy.MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(MaximumLifetime));
    }
}

public interface ISupervisorClock
{
    DateTimeOffset UtcNow { get; }
    long UptimeMilliseconds { get; }
    DateTimeOffset BootEpochUtc { get; }
    ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period);
}

public sealed class SystemSupervisorClock : ISupervisorClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long UptimeMilliseconds => Environment.TickCount64;
    public DateTimeOffset BootEpochUtc =>
        UtcNow - TimeSpan.FromMilliseconds(UptimeMilliseconds);

    public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        TimeProvider.System.CreateTimer(callback, state, dueTime, period);
}

public interface IRecoverableHyperVVmControl : IHyperVVmControl
{
    // A null result must authoritatively confirm that this session owns no VM.
    Task<HyperVPreviewVmLease?> FindBySessionIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken);
}

public sealed class SupervisorProtocolException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}
