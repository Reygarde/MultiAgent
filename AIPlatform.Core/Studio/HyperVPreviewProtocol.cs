using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AIPlatform.Core.Studio;

public enum HyperVPreviewMessageType : ushort
{
    Challenge = 1,
    Ready = 2,
    Frame = 3,
    Input = 4,
    Log = 5,
    Stop = 6,
    Acknowledge = 7,
    Hello = 8,
    HelloAck = 9,
    Status = 10,
    Error = 11,
    CleanupConfirmed = 12,
}

public enum HyperVPreviewGuestState : byte
{
    Authenticating = 1,
    Authenticated = 2,
    Waiting = 3,
    Stopping = 4,
    Stopped = 5,
}

public enum HyperVPreviewGuestError : ushort
{
    ProtocolRejected = 1,
    AuthenticationFailed = 2,
    SessionExpired = 3,
    UnsupportedOperation = 4,
    InternalFailure = 5,
}

public enum HyperVPreviewProofParty : byte
{
    Guest = 1,
    Host = 2,
}

public sealed record HyperVPreviewMessage(
    Guid SessionId,
    ulong Sequence,
    HyperVPreviewMessageType Type,
    byte[] Payload,
    ulong CorrelationId = 0);

public sealed record HyperVPreviewGuestHello(
    HyperVPreviewChannelRole Role,
    byte[] Challenge,
    byte[] HostNonce,
    uint RemainingLifetimeMilliseconds,
    byte[] HostProof);

public sealed record HyperVPreviewGuestHelloAck(
    byte[] Challenge,
    byte[] GuestNonce,
    byte[] AgentDigest,
    byte[] GuestProof);

public sealed record HyperVPreviewHandshake(
    Guid SessionId,
    Guid VmId,
    HyperVPreviewChannelRole Role,
    byte[] Challenge)
{
    public void Validate(
        HyperVPreviewVmLease lease,
        HyperVPreviewChannelRole expectedRole,
        ReadOnlySpan<byte> expectedChallenge)
    {
        if (lease is null || SessionId != lease.SessionId || VmId != lease.VmId ||
            Role != expectedRole || Challenge is null || Challenge.Length != 32 ||
            expectedChallenge.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(Challenge, expectedChallenge))
            throw new InvalidDataException("HvSocket handshake identity or challenge mismatch.");
    }
}

public static class HyperVPreviewMessageCodec
{
    public const ushort Version = 1;
    private static ReadOnlySpan<byte> Magic => "AP65"u8;

    public static byte[] Encode(HyperVPreviewMessage message, HyperVPreviewChannelRole role)
    {
        ValidateMessage(message, role);
        var result = new byte[HyperVPreviewPolicy.HeaderBytes + message.Payload.Length];
        Magic.CopyTo(result);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), Version);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6, 2), (ushort)message.Type);
        if (!message.SessionId.TryWriteBytes(result.AsSpan(8, 16), bigEndian: true, out var written) ||
            written != 16)
            throw new InvalidOperationException("Could not encode the Preview session id.");
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(24, 8), message.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(32, 4), (uint)message.Payload.Length);
        result.AsSpan(36, 4).Clear();
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(40, 8), message.CorrelationId);
        message.Payload.CopyTo(result.AsSpan(HyperVPreviewPolicy.HeaderBytes));
        return result;
    }

    public static HyperVPreviewMessage Decode(
        ReadOnlySpan<byte> encoded,
        HyperVPreviewChannelRole role,
        Guid expectedSessionId)
    {
        if (expectedSessionId == Guid.Empty)
            throw new ArgumentException("A non-empty expected session id is required.", nameof(expectedSessionId));
        if (encoded.Length < HyperVPreviewPolicy.HeaderBytes)
            throw new InvalidDataException("Truncated Preview message header.");
        if (!encoded[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Invalid Preview message magic.");
        if (BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(4, 2)) != Version)
            throw new InvalidDataException("Unsupported Preview message version.");

        var rawType = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(6, 2));
        if (!Enum.IsDefined((HyperVPreviewMessageType)rawType))
            throw new InvalidDataException("Unknown Preview message type.");
        var type = (HyperVPreviewMessageType)rawType;
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(encoded.Slice(32, 4));
        if (payloadLength > HyperVPreviewPolicy.MaximumMessageBytes ||
            payloadLength != encoded.Length - HyperVPreviewPolicy.HeaderBytes)
            throw new InvalidDataException("Invalid Preview message payload length.");

        var sessionId = new Guid(encoded.Slice(8, 16), bigEndian: true);
        if (sessionId == Guid.Empty || sessionId != expectedSessionId)
            throw new InvalidDataException("Preview message session id mismatch.");
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(encoded.Slice(24, 8));
        if (sequence == 0)
            throw new InvalidDataException("Preview message sequence must be positive.");
        if (encoded.Slice(36, 4).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Preview message reserved flags are nonzero.");
        var correlationId = BinaryPrimitives.ReadUInt64BigEndian(encoded.Slice(40, 8));

        var message = new HyperVPreviewMessage(
            sessionId,
            sequence,
            type,
            encoded[HyperVPreviewPolicy.HeaderBytes..].ToArray(),
            correlationId);
        ValidateMessage(message, role);
        return message;
    }

    public static void ValidateMessage(HyperVPreviewMessage message, HyperVPreviewChannelRole role)
    {
        if (message is null || message.SessionId == Guid.Empty || message.Sequence == 0 ||
            message.Payload is null || !Enum.IsDefined(message.Type) || !Enum.IsDefined(role))
            throw new InvalidDataException("Invalid Preview message metadata.");
        if (message.Payload.Length > HyperVPreviewPolicy.MaximumMessageBytes)
            throw new InvalidDataException("Preview message exceeds the payload limit.");

        var roleAllowsType = role switch
        {
            HyperVPreviewChannelRole.Control => message.Type is
                HyperVPreviewMessageType.Challenge or HyperVPreviewMessageType.Ready or
                HyperVPreviewMessageType.Input or HyperVPreviewMessageType.Log or
                HyperVPreviewMessageType.Stop or HyperVPreviewMessageType.Acknowledge or
                HyperVPreviewMessageType.Hello or HyperVPreviewMessageType.HelloAck or
                HyperVPreviewMessageType.Status or
                HyperVPreviewMessageType.Error or HyperVPreviewMessageType.CleanupConfirmed,
            HyperVPreviewChannelRole.Frames => message.Type is
                HyperVPreviewMessageType.Challenge or HyperVPreviewMessageType.Frame or
                HyperVPreviewMessageType.Acknowledge,
            _ => false,
        };
        if (!roleAllowsType)
            throw new InvalidDataException("Preview message type is not permitted on this channel.");

        var validPayload = message.Type switch
        {
            HyperVPreviewMessageType.Challenge => message.Payload.Length == 32,
            HyperVPreviewMessageType.Stop or
                HyperVPreviewMessageType.Acknowledge or
                HyperVPreviewMessageType.CleanupConfirmed => message.Payload.Length == 0,
            HyperVPreviewMessageType.Hello => message.Payload.Length == 1 + 32 + 32 + 4 + 32,
            HyperVPreviewMessageType.HelloAck => message.Payload.Length == 32 + 32 + 32 + 32,
            HyperVPreviewMessageType.Ready => message.Payload.Length == 1 + 32 &&
                Enum.IsDefined((HyperVPreviewGuestState)message.Payload[0]),
            HyperVPreviewMessageType.Status => message.Payload.Length == 1 &&
                Enum.IsDefined((HyperVPreviewGuestState)message.Payload[0]),
            HyperVPreviewMessageType.Error => message.Payload.Length == sizeof(ushort) &&
                Enum.IsDefined((HyperVPreviewGuestError)BinaryPrimitives.ReadUInt16BigEndian(message.Payload)),
            HyperVPreviewMessageType.Frame => message.Payload.Length == 16 + HyperVPreviewPolicy.FrameBytes,
            HyperVPreviewMessageType.Input => message.Payload.Length is > 0 and <= HyperVPreviewPolicy.MaximumInputBytes,
            HyperVPreviewMessageType.Log => message.Payload.Length is > 0 and <= HyperVPreviewPolicy.MaximumLogEventBytes,
            _ => false,
        };
        if (!validPayload)
            throw new InvalidDataException("Invalid Preview message payload for its type.");
    }
}

public static class HyperVPreviewGuestControlPayloadCodec
{
    public const int HelloBytes = 1 + 32 + 32 + sizeof(uint) + 32;
    public const int HelloAckBytes = 32 + 32 + 32 + 32;
    public const int ReadyBytes = 1 + 32;

    public static byte[] EncodeHello(
        HyperVPreviewChannelRole role,
        ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> hostNonce,
        uint remainingLifetimeMilliseconds,
        ReadOnlySpan<byte> hostProof)
    {
        ValidateAuthenticationValues(role, challenge, hostNonce, hostProof);
        if (remainingLifetimeMilliseconds == 0)
            throw new ArgumentOutOfRangeException(nameof(remainingLifetimeMilliseconds));
        var payload = new byte[HelloBytes];
        payload[0] = (byte)role;
        challenge.CopyTo(payload.AsSpan(1, 32));
        hostNonce.CopyTo(payload.AsSpan(33, 32));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(65, sizeof(uint)), remainingLifetimeMilliseconds);
        hostProof.CopyTo(payload.AsSpan(69, 32));
        return payload;
    }

    public static HyperVPreviewGuestHello DecodeHello(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != HelloBytes ||
            !Enum.IsDefined((HyperVPreviewChannelRole)payload[0]))
            throw new InvalidDataException("Invalid Preview guest Hello payload.");
        var lifetime = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(65, sizeof(uint)));
        if (lifetime == 0)
            throw new InvalidDataException("Preview guest Hello has an empty lifetime.");
        return new HyperVPreviewGuestHello(
            (HyperVPreviewChannelRole)payload[0],
            payload.Slice(1, 32).ToArray(),
            payload.Slice(33, 32).ToArray(),
            lifetime,
            payload.Slice(69, 32).ToArray());
    }

    public static byte[] EncodeHelloAck(
        ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> guestNonce,
        ReadOnlySpan<byte> agentDigest,
        ReadOnlySpan<byte> guestProof)
    {
        Validate32(challenge, nameof(challenge));
        Validate32(guestNonce, nameof(guestNonce));
        Validate32(agentDigest, nameof(agentDigest));
        Validate32(guestProof, nameof(guestProof));
        var payload = new byte[HelloAckBytes];
        challenge.CopyTo(payload.AsSpan(0, 32));
        guestNonce.CopyTo(payload.AsSpan(32, 32));
        agentDigest.CopyTo(payload.AsSpan(64, 32));
        guestProof.CopyTo(payload.AsSpan(96, 32));
        return payload;
    }

    public static HyperVPreviewGuestHelloAck DecodeHelloAck(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != HelloAckBytes)
            throw new InvalidDataException("Invalid Preview guest HelloAck payload.");
        return new HyperVPreviewGuestHelloAck(
            payload[..32].ToArray(),
            payload.Slice(32, 32).ToArray(),
            payload.Slice(64, 32).ToArray(),
            payload.Slice(96, 32).ToArray());
    }

    public static byte[] EncodeReady(
        HyperVPreviewGuestState state,
        ReadOnlySpan<byte> agentDigest)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        Validate32(agentDigest, nameof(agentDigest));
        var payload = new byte[ReadyBytes];
        payload[0] = (byte)state;
        agentDigest.CopyTo(payload.AsSpan(1));
        return payload;
    }

    public static bool VerifyReady(
        ReadOnlySpan<byte> payload,
        HyperVPreviewGuestState expectedState,
        ReadOnlySpan<byte> expectedAgentDigest) =>
        payload.Length == ReadyBytes &&
        payload[0] == (byte)expectedState &&
        expectedAgentDigest.Length == 32 &&
        CryptographicOperations.FixedTimeEquals(payload[1..], expectedAgentDigest);

    private static void ValidateAuthenticationValues(
        HyperVPreviewChannelRole role,
        ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> proof)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        Validate32(challenge, nameof(challenge));
        Validate32(nonce, nameof(nonce));
        Validate32(proof, nameof(proof));
    }

    private static void Validate32(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length != 32)
            throw new ArgumentException("Preview authentication values must be exactly 32 bytes.", name);
    }
}

public static class HyperVPreviewGuestAuthenticator
{
    private static ReadOnlySpan<byte> Domain => "AIPlatform-Preview-Guest-Auth"u8;

    public static byte[] CreateProof(
        ReadOnlySpan<byte> channelChallenge,
        Guid sessionId,
        Guid vmId,
        HyperVPreviewChannelRole channelRole,
        ReadOnlySpan<byte> guestNonce,
        ReadOnlySpan<byte> hostNonce,
        uint remainingLifetimeMilliseconds,
        HyperVPreviewProofParty party)
    {
        if (channelChallenge.Length != 32 || sessionId == Guid.Empty || vmId == Guid.Empty ||
            !Enum.IsDefined(channelRole) || guestNonce.Length != 32 || hostNonce.Length != 32 ||
            remainingLifetimeMilliseconds == 0 || !Enum.IsDefined(party))
            throw new ArgumentException("Guest authentication requires fixed-size, session-bound inputs.");

        var transcript = new byte[Domain.Length + sizeof(ushort) + 16 + 16 +
                                  sizeof(ushort) + sizeof(uint) + 1 + 32 + 32];
        var offset = 0;
        Domain.CopyTo(transcript);
        offset += Domain.Length;
        BinaryPrimitives.WriteUInt16BigEndian(
            transcript.AsSpan(offset, sizeof(ushort)),
            HyperVPreviewMessageCodec.Version);
        offset += sizeof(ushort);
        if (!sessionId.TryWriteBytes(transcript.AsSpan(offset, 16), bigEndian: true, out var sessionBytes) ||
            sessionBytes != 16)
            throw new InvalidOperationException("Could not encode the Preview session id.");
        offset += 16;
        if (!vmId.TryWriteBytes(transcript.AsSpan(offset, 16), bigEndian: true, out var vmBytes) ||
            vmBytes != 16)
            throw new InvalidOperationException("Could not encode the Preview VM id.");
        offset += 16;
        BinaryPrimitives.WriteUInt16BigEndian(
            transcript.AsSpan(offset, sizeof(ushort)),
            (ushort)channelRole);
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt32BigEndian(
            transcript.AsSpan(offset, sizeof(uint)),
            remainingLifetimeMilliseconds);
        offset += sizeof(uint);
        transcript[offset++] = (byte)party;
        guestNonce.CopyTo(transcript.AsSpan(offset, 32));
        offset += 32;
        hostNonce.CopyTo(transcript.AsSpan(offset, 32));
        return HMACSHA256.HashData(channelChallenge, transcript);
    }

    public static bool VerifyProof(
        ReadOnlySpan<byte> channelChallenge,
        Guid sessionId,
        Guid vmId,
        HyperVPreviewChannelRole channelRole,
        ReadOnlySpan<byte> guestNonce,
        ReadOnlySpan<byte> hostNonce,
        uint remainingLifetimeMilliseconds,
        HyperVPreviewProofParty party,
        ReadOnlySpan<byte> proof)
    {
        if (proof.Length != 32)
            return false;
        var expected = CreateProof(
            channelChallenge, sessionId, vmId, channelRole, guestNonce, hostNonce,
            remainingLifetimeMilliseconds, party);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, proof);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }
}

public sealed class HyperVPreviewSequenceGuard
{
    private ulong _lastSequence;

    public void Accept(HyperVPreviewMessage message)
    {
        if (message.Sequence == 0 || message.Sequence <= _lastSequence)
            throw new InvalidDataException("Preview message sequence is repeated or out of order.");
        _lastSequence = message.Sequence;
    }
}
