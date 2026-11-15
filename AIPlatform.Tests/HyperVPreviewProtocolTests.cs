using System.Buffers.Binary;
using AIPlatform.Core.Studio;

namespace AIPlatform.Tests;

public sealed class HyperVPreviewProtocolTests
{
    [Fact]
    public void EncodesAndDecodesVersionedMessagesWithFixedHeaderAndSessionBinding()
    {
        var sessionId = Guid.NewGuid();
        var message = new HyperVPreviewMessage(
            sessionId,
            1,
            HyperVPreviewMessageType.Ready,
            HyperVPreviewGuestControlPayloadCodec.EncodeReady(HyperVPreviewGuestState.Authenticated, new byte[32]),
            CorrelationId: 7);

        var encoded = HyperVPreviewMessageCodec.Encode(message, HyperVPreviewChannelRole.Control);
        var decoded = HyperVPreviewMessageCodec.Decode(
            encoded,
            HyperVPreviewChannelRole.Control,
            sessionId);

        Assert.Equal(
            HyperVPreviewPolicy.HeaderBytes + HyperVPreviewGuestControlPayloadCodec.ReadyBytes,
            encoded.Length);
        Assert.Equal("AP65"u8.ToArray(), encoded[..4]);
        Assert.Equal(HyperVPreviewMessageCodec.Version,
            BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(4, 2)));
        Assert.Equal((ushort)HyperVPreviewMessageType.Ready,
            BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(6, 2)));
        Assert.Equal(sessionId, new Guid(encoded.AsSpan(8, 16), bigEndian: true));
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64BigEndian(encoded.AsSpan(24, 8)));
        Assert.Equal((uint)HyperVPreviewGuestControlPayloadCodec.ReadyBytes,
            BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(32, 4)));
        Assert.All(encoded.AsSpan(36, 4).ToArray(), value => Assert.Equal((byte)0, value));
        Assert.Equal(7UL, BinaryPrimitives.ReadUInt64BigEndian(encoded.AsSpan(40, 8)));
        AssertMessageEqual(message, decoded);
        Assert.Throws<InvalidDataException>(() =>
            HyperVPreviewMessageCodec.Decode(encoded, HyperVPreviewChannelRole.Control, Guid.NewGuid()));
    }

    [Fact]
    public void AppliesChannelTypeAndTypeSpecificPayloadPolicies()
    {
        var sessionId = Guid.NewGuid();
        var challenge = new HyperVPreviewMessage(
            sessionId,
            1,
            HyperVPreviewMessageType.Challenge,
            new byte[32]);
        var input = new HyperVPreviewMessage(
            sessionId,
            2,
            HyperVPreviewMessageType.Input,
            [1]);
        var frame = new HyperVPreviewMessage(
            sessionId,
            1,
            HyperVPreviewMessageType.Frame,
            new byte[16 + HyperVPreviewPolicy.FrameBytes]);

        AssertMessageEqual(challenge, HyperVPreviewMessageCodec.Decode(
            HyperVPreviewMessageCodec.Encode(challenge, HyperVPreviewChannelRole.Frames),
            HyperVPreviewChannelRole.Frames,
            sessionId));
        AssertMessageEqual(input, HyperVPreviewMessageCodec.Decode(
            HyperVPreviewMessageCodec.Encode(input, HyperVPreviewChannelRole.Control),
            HyperVPreviewChannelRole.Control,
            sessionId));
        AssertMessageEqual(frame, HyperVPreviewMessageCodec.Decode(
            HyperVPreviewMessageCodec.Encode(frame, HyperVPreviewChannelRole.Frames),
            HyperVPreviewChannelRole.Frames,
            sessionId));

        Assert.Throws<InvalidDataException>(() =>
            HyperVPreviewMessageCodec.Encode(input, HyperVPreviewChannelRole.Frames));
        Assert.Throws<InvalidDataException>(() =>
            HyperVPreviewMessageCodec.Encode(
                new HyperVPreviewMessage(sessionId, 3, HyperVPreviewMessageType.Challenge, new byte[31]),
                HyperVPreviewChannelRole.Control));
        Assert.Throws<InvalidDataException>(() =>
            HyperVPreviewMessageCodec.Encode(
                new HyperVPreviewMessage(sessionId, 4, HyperVPreviewMessageType.Stop, [0]),
                HyperVPreviewChannelRole.Control));
    }

    [Fact]
    public void RejectsUnknownVersionsTypesFlagsReservedBytesAndMalformedLengths()
    {
        var message = new HyperVPreviewMessage(
            Guid.NewGuid(),
            1,
            HyperVPreviewMessageType.Ready,
            HyperVPreviewGuestControlPayloadCodec.EncodeReady(
                HyperVPreviewGuestState.Authenticated,
                new byte[32]));
        var encoded = HyperVPreviewMessageCodec.Encode(message, HyperVPreviewChannelRole.Control);

        AssertMalformed(encoded, bytes => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 99));
        AssertMalformed(encoded, bytes => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6, 2), ushort.MaxValue));
        AssertMalformed(encoded, bytes => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32, 4), uint.MaxValue));
        AssertMalformed(encoded, bytes => bytes[36] = 1);
        AssertMalformed(encoded, bytes => bytes[39] = 1);
        Assert.Throws<InvalidDataException>(() => HyperVPreviewMessageCodec.Decode(
            encoded.AsSpan(0, HyperVPreviewPolicy.HeaderBytes - 1),
            HyperVPreviewChannelRole.Control,
            message.SessionId));
    }

    [Fact]
    public void RequiresDistinctChallengesBoundToTheExactVmSessionAndChannel()
    {
        var lease = new HyperVPreviewVmLease(Guid.NewGuid(), Guid.NewGuid(), "owned-resource");
        var challenges = HyperVPreviewConnectionChallenges.Create();
        var handshake = new HyperVPreviewHandshake(
            lease.SessionId,
            lease.VmId,
            HyperVPreviewChannelRole.Control,
            challenges.Control);

        handshake.Validate(lease, HyperVPreviewChannelRole.Control, challenges.Control);
        Assert.Throws<InvalidDataException>(() =>
            handshake.Validate(lease, HyperVPreviewChannelRole.Frames, challenges.Control));
        Assert.Throws<InvalidDataException>(() =>
            handshake.Validate(
                lease with { VmId = Guid.NewGuid() },
                HyperVPreviewChannelRole.Control,
                challenges.Control));
        Assert.Throws<InvalidDataException>(() =>
            handshake.Validate(lease, HyperVPreviewChannelRole.Control, challenges.Frames));
    }

    [Fact]
    public void RejectsRepeatedSequencesAndDeterministicMalformedInputFuzz()
    {
        var sessionId = Guid.NewGuid();
        var guard = new HyperVPreviewSequenceGuard();
        var readyPayload = HyperVPreviewGuestControlPayloadCodec.EncodeReady(
            HyperVPreviewGuestState.Authenticated, new byte[32]);
        guard.Accept(new HyperVPreviewMessage(sessionId, 2, HyperVPreviewMessageType.Ready, readyPayload));
        guard.Accept(new HyperVPreviewMessage(sessionId, 5, HyperVPreviewMessageType.Ready, readyPayload));
        Assert.Throws<InvalidDataException>(() =>
            guard.Accept(new HyperVPreviewMessage(sessionId, 5, HyperVPreviewMessageType.Ready, readyPayload)));
        Assert.Throws<InvalidDataException>(() =>
            guard.Accept(new HyperVPreviewMessage(sessionId, 4, HyperVPreviewMessageType.Ready, readyPayload)));

        var random = new Random(6505);
        for (var index = 0; index < 512; index++)
        {
            var bytes = new byte[random.Next(0, 512)];
            random.NextBytes(bytes);
            Assert.Throws<InvalidDataException>(() =>
                HyperVPreviewMessageCodec.Decode(bytes, HyperVPreviewChannelRole.Control, sessionId));
        }
    }

    private static void AssertMalformed(byte[] source, Action<byte[]> mutate)
    {
        var mutated = source.ToArray();
        mutate(mutated);
        Assert.Throws<InvalidDataException>(() =>
            HyperVPreviewMessageCodec.Decode(
                mutated,
                HyperVPreviewChannelRole.Control,
                new Guid(source.AsSpan(8, 16), bigEndian: true)));
    }

    private static void AssertMessageEqual(HyperVPreviewMessage expected, HyperVPreviewMessage actual)
    {
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Payload, actual.Payload);
        Assert.Equal(expected.CorrelationId, actual.CorrelationId);
    }
}
