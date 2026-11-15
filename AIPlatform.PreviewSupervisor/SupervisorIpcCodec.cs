using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIPlatform.Core.Studio;

namespace AIPlatform.PreviewSupervisor;

public static class SupervisorIpcCodec
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static byte[] EncodeRequest(SupervisorIpcRequest request)
    {
        ValidateRequest(request);
        return SerializeFrame(request);
    }

    public static SupervisorIpcRequest DecodeRequest(ReadOnlySpan<byte> payload)
    {
        RejectDuplicateProperties(payload);
        var request = JsonSerializer.Deserialize<SupervisorIpcRequest>(payload, JsonOptions)
            ?? throw new InvalidDataException("IPC request is empty.");
        ValidateRequest(request);
        return request;
    }

    public static byte[] EncodeResponse(SupervisorIpcResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Version != SupervisorPolicy.ProtocolVersion ||
            response.RequestId == Guid.Empty ||
            response.ErrorCode is { Length: > 64 } ||
            response.Accepted && response.ErrorCode is not null ||
            !response.Accepted && string.IsNullOrWhiteSpace(response.ErrorCode) ||
            response.Session is { } session && !IsValidSnapshot(session))
            throw new InvalidDataException("IPC response failed validation.");
        return SerializeFrame(response);
    }

    public static SupervisorIpcResponse DecodeResponse(ReadOnlySpan<byte> payload)
    {
        RejectDuplicateProperties(payload);
        var response = JsonSerializer.Deserialize<SupervisorIpcResponse>(payload, JsonOptions)
            ?? throw new InvalidDataException("IPC response is empty.");
        if (response.Version != SupervisorPolicy.ProtocolVersion ||
            response.RequestId == Guid.Empty ||
            response.ErrorCode is { Length: > 64 } ||
            response.Accepted && response.ErrorCode is not null ||
            !response.Accepted && string.IsNullOrWhiteSpace(response.ErrorCode) ||
            response.Session is { } session && !IsValidSnapshot(session))
            throw new InvalidDataException("IPC response failed validation.");
        return response;
    }

    private static bool IsValidSnapshot(SupervisedSessionSnapshot session) =>
        session.SessionId != Guid.Empty &&
        Enum.IsDefined(session.State) &&
        (session.VmId is null || session.VmId != Guid.Empty) &&
        session.CreatedAtUtc.Offset == TimeSpan.Zero &&
        session.ExpiresAtUtc.Offset == TimeSpan.Zero &&
        session.ExpiresAtUtc > session.CreatedAtUtc &&
        session.LastCommandSequence <= (ulong)SupervisorPolicy.MaximumRecentCommands &&
        (session.CleanupFailure is null || Enum.IsDefined(session.CleanupFailure.Value)) &&
        session.ReservedChannels is not null &&
        session.ReservedChannels.Count <= 2 &&
        session.ReservedChannels.All(role => Enum.IsDefined(role)) &&
        session.ReservedChannels.Distinct().Count() == session.ReservedChannels.Count;

    public static async Task WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 or > SupervisorPolicy.MaximumIpcFrameBytes)
            throw new InvalidDataException("IPC frame length is outside the permitted bounds.");

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<byte[]?> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        var firstByte = new byte[1];
        var read = await stream.ReadAsync(firstByte, cancellationToken);
        if (read == 0)
            return null;
        prefix[0] = firstByte[0];
        await ReadExactlyAsync(stream, prefix.AsMemory(1), cancellationToken);

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > SupervisorPolicy.MaximumIpcFrameBytes)
            throw new InvalidDataException("IPC frame length is outside the permitted bounds.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return payload;
    }

    public static void ValidateRequest(SupervisorIpcRequest request)
    {
        if (request is null || request.Version != SupervisorPolicy.ProtocolVersion ||
            request.RequestId == Guid.Empty || !Enum.IsDefined(request.Command))
            throw new SupervisorProtocolException("InvalidRequest");

        var shapeIsValid = request.Command switch
        {
            SupervisorCommand.CreateSession =>
                request.SessionId is null && request.Sequence == 0 &&
                request.Lease is null && request.ChannelRole is null,
            SupervisorCommand.GetStatus => request.SessionId is { } statusId &&
                                           statusId != Guid.Empty && request.Sequence == 0 &&
                                           request.Lease is null && request.ChannelRole is null,
            SupervisorCommand.ReserveSocketChannel =>
                request.SessionId is { } reservationSessionId &&
                reservationSessionId != Guid.Empty && request.Sequence > 0 &&
                request.Lease is { } lease && IsValidLease(lease) &&
                lease.SessionId == reservationSessionId &&
                request.ChannelRole is { } role && Enum.IsDefined(role),
            SupervisorCommand.Heartbeat or SupervisorCommand.Stop or SupervisorCommand.RetryCleanup =>
                request.SessionId is { } sessionId && sessionId != Guid.Empty &&
                request.Sequence > 0 && request.Lease is null && request.ChannelRole is null,
            _ => false,
        };
        if (!shapeIsValid)
            throw new SupervisorProtocolException("InvalidRequestShape");
    }

    private static bool IsValidLease(HyperVPreviewVmLease lease) =>
        lease.SessionId != Guid.Empty && lease.VmId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(lease.ResourceId) &&
        lease.ResourceId.Length <= 256 &&
        !lease.ResourceId.Any(char.IsControl);

    private static byte[] SerializeFrame<T>(T value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length is <= 0 or > SupervisorPolicy.MaximumIpcFrameBytes)
            throw new InvalidDataException("IPC message exceeds its size limit.");
        return payload;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("IPC frame was truncated.");
            total += read;
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> payload)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        Visit(document.RootElement);

        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException("IPC message contains duplicate properties.");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Visit(item);
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
