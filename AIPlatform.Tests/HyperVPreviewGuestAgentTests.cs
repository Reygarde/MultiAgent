using System.Buffers.Binary;
using System.Security.Cryptography;
using AIPlatform.Core.Studio;
using AIPlatform.PreviewGuestAgent;
using AIPlatform.PreviewGuestAgent.Testing;

namespace AIPlatform.Tests;

public sealed class HyperVPreviewGuestAgentTests
{
    [Fact]
    public async Task AuthenticatesDistinctSessionBoundControlChallengeAndStopsCleanly()
    {
        await using var session = await AgentSession.StartAsync();
        Assert.Equal(HyperVPreviewGuestAgentState.Authenticating, session.Agent.State);

        await session.HandshakeAsync();
        Assert.Equal(HyperVPreviewGuestAgentState.Waiting, session.Agent.State);

        await session.SendHostAsync(2, HyperVPreviewMessageType.Stop, []);
        var stopping = await session.ReadGuestAsync(4);
        var stopped = await session.ReadGuestAsync(5);
        var cleanup = await session.ReadGuestAsync(6);
        var result = await session.Result.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal((byte)HyperVPreviewGuestState.Stopping, stopping.Payload[0]);
        Assert.Equal((byte)HyperVPreviewGuestState.Stopped, stopped.Payload[0]);
        Assert.Equal(HyperVPreviewMessageType.CleanupConfirmed, cleanup.Type);
        Assert.Equal(HyperVPreviewGuestAgentExitReason.SupervisorStop, result.ExitReason);
        Assert.Equal(HyperVPreviewGuestAgentState.Stopped, result.State);
    }

    [Fact]
    public async Task RejectsWrongChannelAuthenticationChallenge()
    {
        await using var session = await AgentSession.StartAsync();

        await session.HandshakeAsync(helloUsesFramesChallenge: true);
        var result = await session.Result.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HyperVPreviewGuestAgentExitReason.ProtocolRejected, result.ExitReason);
        Assert.Equal(HyperVPreviewGuestAgentState.Failed, result.State);
    }

    [Fact]
    public async Task RejectsInvalidHostProofForTheConfiguredControlChallenge()
    {
        await using var session = await AgentSession.StartAsync();
        await session.HandshakeAsync(corruptHostProof: true);

        var result = await session.Result.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HyperVPreviewGuestAgentExitReason.ProtocolRejected, result.ExitReason);
        Assert.Equal(HyperVPreviewGuestAgentState.Failed, result.State);
    }

    [Fact]
    public void RejectsReusedChallengesAndInvalidSessionConfiguration()
    {
        var challenge = new byte[32];
        var challenges = new HyperVPreviewConnectionChallenges(challenge, challenge);

        Assert.Throws<ArgumentException>(() => new HyperVPreviewGuestAgentOptions(
            Guid.NewGuid(), Guid.NewGuid(), challenges, new byte[32], TimeSpan.FromSeconds(10)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HyperVPreviewGuestAgentOptions(
            Guid.NewGuid(),
            Guid.NewGuid(),
            HyperVPreviewConnectionChallenges.Create(),
            new byte[32],
            HyperVPreviewPolicy.MaximumLifetime + TimeSpan.FromTicks(1)).Validate());
    }

    [Fact]
    public async Task RejectsIncompatibleVersionAndOversizedControlFrame()
    {
        var sessionId = Guid.NewGuid();
        var (sender, receiver) = InMemoryHyperVPreviewTransport.CreatePair(5);
        await using var senderScope = sender;
        await using var channel = new HyperVPreviewMessageChannel(
            receiver, HyperVPreviewChannelRole.Control, sessionId);

        await sender.WriteAsync(CreateHeader(sessionId, HyperVPreviewMessageType.Ready, 0, version: 99), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => channel.ReceiveAsync(default));

        var (largeSender, largeReceiver) = InMemoryHyperVPreviewTransport.CreatePair(11);
        await using var largeSenderScope = largeSender;
        await using var largeChannel = new HyperVPreviewMessageChannel(
            largeReceiver, HyperVPreviewChannelRole.Control, sessionId);
        await largeSender.WriteAsync(
            CreateHeader(
                sessionId,
                HyperVPreviewMessageType.Status,
                HyperVPreviewPolicy.MaximumControlPayloadBytes + 1),
            default);

        await Assert.ThrowsAsync<InvalidDataException>(() => largeChannel.ReceiveAsync(default));
    }

    [Fact]
    public async Task DetectsTruncatedHeaderAndPayloadAfterPartialReads()
    {
        var sessionId = Guid.NewGuid();
        var (sender, receiver) = InMemoryHyperVPreviewTransport.CreatePair(3);
        await using var channel = new HyperVPreviewMessageChannel(
            receiver, HyperVPreviewChannelRole.Control, sessionId);
        await sender.WriteAsync(CreateHeader(sessionId, HyperVPreviewMessageType.Ready, 0)[..17], default);
        await sender.DisposeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => channel.ReceiveAsync(default));

        var (payloadSender, payloadReceiver) = InMemoryHyperVPreviewTransport.CreatePair(5);
        await using var payloadChannel = new HyperVPreviewMessageChannel(
            payloadReceiver, HyperVPreviewChannelRole.Control, sessionId);
        await payloadSender.WriteAsync(
            CreateHeader(sessionId, HyperVPreviewMessageType.Challenge, 32),
            default);
        await payloadSender.WriteAsync(new byte[] { 1, 2, 3 }, default);
        await payloadSender.DisposeAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => payloadChannel.ReceiveAsync(default));
    }

    [Fact]
    public async Task DisconnectDuringHandshakeStopsAgentWithoutClaimingCleanupConfirmation()
    {
        await using var session = await AgentSession.StartAsync();
        await session.DisposeHostAsync();
        var result = await session.Result.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HyperVPreviewGuestAgentExitReason.TransportDisconnected, result.ExitReason);
        Assert.Equal(HyperVPreviewGuestAgentState.Stopped, result.State);
    }

    [Fact]
    public async Task StopsAtNonRenewableSessionDeadline()
    {
        await using var session = await AgentSession.StartAsync(TimeSpan.FromMilliseconds(150));
        var result = await session.Result.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HyperVPreviewGuestAgentExitReason.SessionExpired, result.ExitReason);
        Assert.Equal(HyperVPreviewGuestAgentState.Stopped, result.State);
    }

    [Fact]
    public async Task RejectsReplayAndUnexpectedInputInsteadOfExecutingIt()
    {
        await using var replaySession = await AgentSession.StartAsync();
        await replaySession.HandshakeAsync();
        await replaySession.SendHostAsync(1, HyperVPreviewMessageType.Stop, []);
        var replay = await replaySession.Result.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HyperVPreviewGuestAgentExitReason.ProtocolRejected, replay.ExitReason);

        await using var inputSession = await AgentSession.StartAsync();
        await inputSession.HandshakeAsync();
        await inputSession.SendHostAsync(2, HyperVPreviewMessageType.Input, [1]);
        var unexpectedInput = await inputSession.Result.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HyperVPreviewGuestAgentExitReason.ProtocolRejected, unexpectedInput.ExitReason);
        Assert.Equal(HyperVPreviewGuestAgentState.Failed, unexpectedInput.State);
    }

    [Fact]
    public async Task HandlesBoundedHostErrorWithoutTreatingItAsACommand()
    {
        await using var session = await AgentSession.StartAsync();
        await session.HandshakeAsync();
        await session.SendHostAsync(
            2,
            HyperVPreviewMessageType.Error,
            [0, (byte)HyperVPreviewGuestError.UnsupportedOperation]);

        var result = await session.Result.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HyperVPreviewGuestAgentExitReason.HostError, result.ExitReason);
        Assert.Equal(HyperVPreviewGuestError.UnsupportedOperation, result.HostError);
        Assert.Equal(HyperVPreviewGuestAgentState.Failed, result.State);
    }

    [Fact]
    public async Task CleansUpAndStopsAfterHostDisconnectsWhileWaiting()
    {
        await using var session = await AgentSession.StartAsync();
        await session.HandshakeAsync();

        await session.DisposeHostAsync();
        var result = await session.Result.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HyperVPreviewGuestAgentExitReason.TransportDisconnected, result.ExitReason);
        Assert.Equal(HyperVPreviewGuestAgentState.Stopped, result.State);
    }

    private static byte[] CreateHeader(
        Guid sessionId,
        HyperVPreviewMessageType type,
        int payloadLength,
        ushort version = HyperVPreviewMessageCodec.Version)
    {
        var header = new byte[HyperVPreviewPolicy.HeaderBytes];
        "AP65"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), version);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)type);
        if (!sessionId.TryWriteBytes(header.AsSpan(8, 16), bigEndian: true, out var written) ||
            written != 16)
            throw new InvalidOperationException("Could not encode the Preview session id.");
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(24, 8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(32, 4), (uint)payloadLength);
        return header;
    }

    private sealed class AgentSession : IAsyncDisposable
    {
        private byte[]? _hostNonce;
        private readonly HyperVPreviewGuestAgentOptions _options;
        private bool _hostDisposed;

        private AgentSession(
            HyperVPreviewGuestAgent agent,
            HyperVPreviewGuestAgentOptions options,
            HyperVPreviewMessageChannel host,
            Task<HyperVPreviewGuestAgentResult> result)
        {
            Agent = agent;
            _options = options;
            Host = host;
            Result = result;
        }

        public HyperVPreviewGuestAgent Agent { get; }
        public HyperVPreviewMessageChannel Host { get; }
        public Task<HyperVPreviewGuestAgentResult> Result { get; }

        public static Task<AgentSession> StartAsync(TimeSpan? lifetime = null)
        {
            var sessionId = Guid.NewGuid();
            var vmId = Guid.NewGuid();
            var options = new HyperVPreviewGuestAgentOptions(
                sessionId,
                vmId,
                HyperVPreviewConnectionChallenges.Create(),
                SHA256.HashData("hyperv-guest-test-digest"u8),
                lifetime ?? TimeSpan.FromSeconds(10));
            var agent = new HyperVPreviewGuestAgent(options);
            var (guestTransport, hostTransport) = InMemoryHyperVPreviewTransport.CreatePair(7);
            var result = agent.RunAsync(guestTransport, default);
            var host = new HyperVPreviewMessageChannel(
                hostTransport, HyperVPreviewChannelRole.Control, sessionId);
            return Task.FromResult(new AgentSession(agent, options, host, result));
        }

        public async Task HandshakeAsync(
            bool helloUsesFramesChallenge = false,
            bool corruptHostProof = false)
        {
            _hostNonce = RandomNumberGenerator.GetBytes(32);
            const uint lifetimeMilliseconds = 5000;
            var challenge = helloUsesFramesChallenge
                ? _options.Challenges.Frames
                : _options.Challenges.Control;
            var proofKey = helloUsesFramesChallenge
                ? _options.Challenges.Frames
                : _options.Challenges.Control;
            var hostProof = HyperVPreviewGuestAuthenticator.CreateProof(
                proofKey,
                _options.SessionId,
                _options.VmId,
                HyperVPreviewChannelRole.Control,
                new byte[32],
                _hostNonce,
                lifetimeMilliseconds,
                HyperVPreviewProofParty.Host);
            if (corruptHostProof)
                hostProof[0] ^= 0x80;
            var helloPayload = HyperVPreviewGuestControlPayloadCodec.EncodeHello(
                HyperVPreviewChannelRole.Control,
                challenge,
                _hostNonce,
                lifetimeMilliseconds,
                hostProof);
            await SendHostAsync(1, HyperVPreviewMessageType.Hello, helloPayload);
            CryptographicOperations.ZeroMemory(hostProof);

            var acknowledgement = await Host.ReceiveAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
            if (acknowledgement is null)
                return;
            Assert.Equal(1UL, acknowledgement.Sequence);
            Assert.Equal(HyperVPreviewMessageType.HelloAck, acknowledgement.Type);
            var helloAck = HyperVPreviewGuestControlPayloadCodec.DecodeHelloAck(acknowledgement.Payload);
            Assert.True(CryptographicOperations.FixedTimeEquals(challenge, helloAck.Challenge));
            Assert.Equal(_options.AgentDigest, helloAck.AgentDigest);
            Assert.True(HyperVPreviewGuestAuthenticator.VerifyProof(
                _options.Challenges.Control,
                _options.SessionId,
                _options.VmId,
                HyperVPreviewChannelRole.Control,
                helloAck.GuestNonce,
                _hostNonce,
                lifetimeMilliseconds,
                HyperVPreviewProofParty.Guest,
                helloAck.GuestProof));
            var ready = await ReadGuestAsync(2);
            Assert.Equal(HyperVPreviewMessageType.Ready, ready.Type);
            Assert.True(HyperVPreviewGuestControlPayloadCodec.VerifyReady(
                ready.Payload,
                HyperVPreviewGuestState.Authenticated,
                _options.AgentDigest));
            var status = await ReadGuestAsync(3);
            Assert.Equal(HyperVPreviewMessageType.Status, status.Type);
            Assert.Equal((byte)HyperVPreviewGuestState.Waiting, status.Payload[0]);
        }

        public async Task<HyperVPreviewMessage> ReadGuestAsync(ulong expectedSequence)
        {
            var message = await Host.ReceiveAsync(default).WaitAsync(TimeSpan.FromSeconds(2))
                ?? throw new EndOfStreamException("Guest disconnected.");
            Assert.Equal(expectedSequence, message.Sequence);
            return message;
        }

        public Task SendHostAsync(
            ulong sequence,
            HyperVPreviewMessageType type,
            byte[] payload) =>
            Host.SendAsync(new HyperVPreviewMessage(_options.SessionId, sequence, type, payload), default);

        public async Task DisposeHostAsync()
        {
            if (_hostDisposed)
                return;
            _hostDisposed = true;
            await Host.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeHostAsync();
            if (!Result.IsCompleted)
            {
                try
                {
                    await Result.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                }
            }
            if (_hostNonce is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(_hostNonce);
        }
    }
}
