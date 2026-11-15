using System.Buffers.Binary;
using System.Security.Cryptography;
using AIPlatform.Core.Studio;

namespace AIPlatform.PreviewGuestAgent;

public enum HyperVPreviewGuestAgentState
{
    Created,
    Authenticating,
    Waiting,
    Stopping,
    Stopped,
    Failed,
}

public enum HyperVPreviewGuestAgentExitReason
{
    SupervisorStop,
    TransportDisconnected,
    SessionExpired,
    HostError,
    ProtocolRejected,
    TransportTimedOut,
}

public sealed record HyperVPreviewGuestAgentResult(
    HyperVPreviewGuestAgentExitReason ExitReason,
    HyperVPreviewGuestAgentState State,
    HyperVPreviewGuestError? HostError = null);

public sealed record HyperVPreviewGuestAgentOptions(
    Guid SessionId,
    Guid VmId,
    HyperVPreviewConnectionChallenges Challenges,
    byte[] AgentDigest,
    TimeSpan MaximumLifetime)
{
    public void Validate()
    {
        if (SessionId == Guid.Empty || VmId == Guid.Empty)
            throw new ArgumentException("Guest sessions require immutable non-empty identifiers.");
        ArgumentNullException.ThrowIfNull(Challenges);
        Challenges.Validate();
        if (AgentDigest is null || AgentDigest.Length != 32)
            throw new ArgumentException("The locked guest-agent digest must be SHA-256.", nameof(AgentDigest));
        if (MaximumLifetime <= TimeSpan.Zero ||
            MaximumLifetime > HyperVPreviewPolicy.MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(MaximumLifetime));
    }
}

public sealed class HyperVPreviewGuestAgent
{
    private readonly Guid _sessionId;
    private readonly Guid _vmId;
    private readonly byte[] _controlChallenge;
    private readonly byte[] _framesChallenge;
    private readonly byte[] _agentDigest;
    private readonly TimeSpan _maximumLifetime;
    private readonly TimeProvider _clock;
    private int _runStarted;
    private int _state = (int)HyperVPreviewGuestAgentState.Created;
    private ulong _nextGuestSequence = 1;
    private ulong _nextHostSequence = 1;

    public HyperVPreviewGuestAgent(
        HyperVPreviewGuestAgentOptions options,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _sessionId = options.SessionId;
        _vmId = options.VmId;
        _controlChallenge = options.Challenges.Control.ToArray();
        _framesChallenge = options.Challenges.Frames.ToArray();
        _agentDigest = options.AgentDigest.ToArray();
        _maximumLifetime = options.MaximumLifetime;
        _clock = clock ?? TimeProvider.System;
    }

    public HyperVPreviewGuestAgentState State =>
        (HyperVPreviewGuestAgentState)Volatile.Read(ref _state);

    public async Task<HyperVPreviewGuestAgentResult> RunAsync(
        IHyperVPreviewTransport transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
            throw new InvalidOperationException("A guest-agent session can run only once.");

        var startedAt = _clock.GetTimestamp();
        var activeLifetime = _maximumLifetime;
        var channel = new HyperVPreviewMessageChannel(
            transport,
            HyperVPreviewChannelRole.Control,
            _sessionId);
        var guestNonce = RandomNumberGenerator.GetBytes(32);
        byte[]? hostNonce = null;
        try
        {
            SetState(HyperVPreviewGuestAgentState.Authenticating);
            var helloMessage = await ReceiveAsync(channel, startedAt, _maximumLifetime, cancellationToken);
            if (helloMessage is null)
                return StopWithoutHandshake(HyperVPreviewGuestAgentExitReason.TransportDisconnected);
            ValidateHostSequence(helloMessage);
            if (helloMessage.Type == HyperVPreviewMessageType.Error)
                return HostFailure(helloMessage);
            if (helloMessage.Type != HyperVPreviewMessageType.Hello)
                throw new InvalidDataException("Expected the host Hello.");
            var hello = HyperVPreviewGuestControlPayloadCodec.DecodeHello(helloMessage.Payload);
            if (hello.Role != HyperVPreviewChannelRole.Control ||
                !CryptographicOperations.FixedTimeEquals(hello.Challenge, _controlChallenge) ||
                hello.RemainingLifetimeMilliseconds > _maximumLifetime.TotalMilliseconds)
                throw new InvalidDataException("Host Hello role, challenge, or lifetime is invalid.");
            activeLifetime = TimeSpan.FromMilliseconds(hello.RemainingLifetimeMilliseconds);
            hostNonce = hello.HostNonce;
            var emptyGuestNonce = new byte[32];
            var hostProofValid = HyperVPreviewGuestAuthenticator.VerifyProof(
                _controlChallenge,
                _sessionId,
                _vmId,
                hello.Role,
                emptyGuestNonce,
                hostNonce,
                hello.RemainingLifetimeMilliseconds,
                HyperVPreviewProofParty.Host,
                hello.HostProof);
            CryptographicOperations.ZeroMemory(emptyGuestNonce);
            CryptographicOperations.ZeroMemory(hello.Challenge);
            CryptographicOperations.ZeroMemory(hello.HostProof);
            if (!hostProofValid)
                throw new InvalidDataException("Host Hello authentication proof is invalid.");

            var guestProof = HyperVPreviewGuestAuthenticator.CreateProof(
                _controlChallenge,
                _sessionId,
                _vmId,
                hello.Role,
                guestNonce,
                hostNonce,
                hello.RemainingLifetimeMilliseconds,
                HyperVPreviewProofParty.Guest);
            var acknowledgement = HyperVPreviewGuestControlPayloadCodec.EncodeHelloAck(
                _controlChallenge, guestNonce, _agentDigest, guestProof);
            CryptographicOperations.ZeroMemory(guestProof);
            try
            {
                await SendAsync(
                    channel,
                    HyperVPreviewMessageType.HelloAck,
                    acknowledgement,
                    startedAt,
                    activeLifetime,
                    cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(acknowledgement);
            }

            var readyPayload = HyperVPreviewGuestControlPayloadCodec.EncodeReady(
                HyperVPreviewGuestState.Authenticated,
                _agentDigest);
            try
            {
                await SendAsync(
                    channel,
                    HyperVPreviewMessageType.Ready,
                    readyPayload,
                    startedAt,
                    activeLifetime,
                    cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(readyPayload);
            }
            SetState(HyperVPreviewGuestAgentState.Waiting);
            await SendStatusAsync(
                channel, HyperVPreviewGuestState.Waiting, startedAt, activeLifetime, cancellationToken);

            while (true)
            {
                var message = await ReceiveAsync(channel, startedAt, activeLifetime, cancellationToken);
                if (message is null)
                    return StopWithoutHandshake(HyperVPreviewGuestAgentExitReason.TransportDisconnected);
                ValidateHostSequence(message);
                if (message.Type == HyperVPreviewMessageType.Error)
                    return HostFailure(message);
                if (message.Type != HyperVPreviewMessageType.Stop)
                    throw new InvalidDataException("Only a supervisor stop is permitted after authentication.");

                SetState(HyperVPreviewGuestAgentState.Stopping);
                await SendStatusAsync(
                    channel, HyperVPreviewGuestState.Stopping, startedAt, activeLifetime, cancellationToken);
                SetState(HyperVPreviewGuestAgentState.Stopped);
                await SendStatusAsync(
                    channel, HyperVPreviewGuestState.Stopped, startedAt, activeLifetime, cancellationToken);
                await SendAsync(
                    channel,
                    HyperVPreviewMessageType.CleanupConfirmed,
                    [],
                    startedAt,
                    activeLifetime,
                    cancellationToken);
                return Result(HyperVPreviewGuestAgentExitReason.SupervisorStop);
            }
        }
        catch (InvalidDataException)
        {
            SetState(HyperVPreviewGuestAgentState.Failed);
            return Result(HyperVPreviewGuestAgentExitReason.ProtocolRejected);
        }
        catch (SessionDeadlineExceededException)
        {
            SetState(HyperVPreviewGuestAgentState.Stopped);
            return Result(HyperVPreviewGuestAgentExitReason.SessionExpired);
        }
        catch (TimeoutException)
        {
            SetState(HyperVPreviewGuestAgentState.Stopped);
            return Result(HyperVPreviewGuestAgentExitReason.TransportTimedOut);
        }
        catch (IOException)
        {
            SetState(HyperVPreviewGuestAgentState.Stopped);
            return Result(HyperVPreviewGuestAgentExitReason.TransportDisconnected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(HyperVPreviewGuestAgentState.Stopping);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_controlChallenge);
            CryptographicOperations.ZeroMemory(_framesChallenge);
            CryptographicOperations.ZeroMemory(_agentDigest);
            CryptographicOperations.ZeroMemory(guestNonce);
            if (hostNonce is not null)
                CryptographicOperations.ZeroMemory(hostNonce);
            if (State == HyperVPreviewGuestAgentState.Stopping)
                SetState(HyperVPreviewGuestAgentState.Stopped);
            await channel.DisposeAsync();
        }
    }

    private async Task<HyperVPreviewMessage?> ReceiveAsync(
        HyperVPreviewMessageChannel channel,
        long startedAt,
        TimeSpan sessionLifetime,
        CancellationToken cancellationToken)
    {
        var remaining = sessionLifetime - Elapsed(startedAt);
        if (remaining <= TimeSpan.Zero)
            throw new SessionDeadlineExceededException();

        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = channel.ReceiveAsync(receiveCancellation.Token);
        var deadline = Task.Delay(remaining, _clock, cancellationToken);
        var completed = await Task.WhenAny(pending, deadline);
        if (completed != pending)
        {
            await deadline;
            receiveCancellation.Cancel();
            ObserveLateFailure(pending);
            throw new SessionDeadlineExceededException();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (Elapsed(startedAt) >= sessionLifetime)
            throw new SessionDeadlineExceededException();
        return await pending;
    }

    private TimeSpan Elapsed(long startedAt) => _clock.GetElapsedTime(startedAt);

    private async Task SendStatusAsync(
        HyperVPreviewMessageChannel channel,
        HyperVPreviewGuestState state,
        long startedAt,
        TimeSpan sessionLifetime,
        CancellationToken cancellationToken) =>
        await SendAsync(
            channel, HyperVPreviewMessageType.Status, [(byte)state], startedAt, sessionLifetime, cancellationToken);

    private async Task SendAsync(
        HyperVPreviewMessageChannel channel,
        HyperVPreviewMessageType type,
        byte[] payload,
        long startedAt,
        TimeSpan sessionLifetime,
        CancellationToken cancellationToken)
    {
        var remaining = sessionLifetime - Elapsed(startedAt);
        if (remaining <= TimeSpan.Zero)
            throw new SessionDeadlineExceededException();
        var sequence = _nextGuestSequence;
        _nextGuestSequence = checked(sequence + 1);
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = channel.SendAsync(
            new HyperVPreviewMessage(_sessionId, sequence, type, payload),
            sendCancellation.Token);
        var deadline = Task.Delay(remaining, _clock, cancellationToken);
        var completed = await Task.WhenAny(pending, deadline);
        if (completed != pending)
        {
            await deadline;
            sendCancellation.Cancel();
            ObserveLateFailure(pending);
            throw new SessionDeadlineExceededException();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (Elapsed(startedAt) >= sessionLifetime)
            throw new SessionDeadlineExceededException();
        await pending;
    }

    private void ValidateHostSequence(HyperVPreviewMessage message)
    {
        if (message.Sequence != _nextHostSequence)
            throw new InvalidDataException("Host message sequence is repeated or out of order.");
        _nextHostSequence = checked(_nextHostSequence + 1);
    }

    private HyperVPreviewGuestAgentResult HostFailure(HyperVPreviewMessage message)
    {
        if (message.Type != HyperVPreviewMessageType.Error)
            throw new InvalidDataException("Expected a bounded host error message.");
        var code = (HyperVPreviewGuestError)BinaryPrimitives.ReadUInt16BigEndian(message.Payload);
        SetState(HyperVPreviewGuestAgentState.Failed);
        return new HyperVPreviewGuestAgentResult(
            HyperVPreviewGuestAgentExitReason.HostError,
            State,
            code);
    }

    private HyperVPreviewGuestAgentResult StopWithoutHandshake(
        HyperVPreviewGuestAgentExitReason reason)
    {
        SetState(HyperVPreviewGuestAgentState.Stopped);
        return Result(reason);
    }

    private HyperVPreviewGuestAgentResult Result(HyperVPreviewGuestAgentExitReason reason) =>
        new(reason, State);

    private void SetState(HyperVPreviewGuestAgentState next) =>
        Volatile.Write(ref _state, (int)next);

    private static void ObserveLateFailure(Task pending)
    {
        _ = pending.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class SessionDeadlineExceededException()
        : TimeoutException("Guest session lifetime expired.");
}
