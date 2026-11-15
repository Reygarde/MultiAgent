using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class SupervisorNamedPipeLeaseAuthorizer : IHyperVPreviewManagedLeaseAuthorizer
{
    private const int ProtocolVersion = 2;
    private const int MaximumFrameBytes = 8192;
    private const int MaximumCommandSequence = 256;
    private const int MaximumSequenceRetries = 4;
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _pipeName;

    public SupervisorNamedPipeLeaseAuthorizer(string pipeName)
    {
        ValidatePipeName(pipeName);
        _pipeName = pipeName;
    }

    public async Task<bool> TryAuthorizeAndReserveAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewChannelRole role,
        CancellationToken cancellationToken)
    {
        ValidateLease(lease);
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AuthorizationTimeout);
        try
        {
            for (var attempt = 0; attempt < MaximumSequenceRetries; attempt++)
            {
                var status = await SendAsync(new WireRequest(
                    ProtocolVersion,
                    Guid.NewGuid(),
                    WireCommand.GetStatus,
                    lease.SessionId,
                    0,
                    null,
                    null), timeout.Token);

                if (!status.Accepted ||
                    status.Session is not { } session ||
                    session.SessionId != lease.SessionId ||
                    session.VmId != lease.VmId ||
                    session.State != WireSessionState.Running ||
                    session.LastCommandSequence >= MaximumCommandSequence)
                    return false;

                var reservation = await SendAsync(new WireRequest(
                    ProtocolVersion,
                    Guid.NewGuid(),
                    WireCommand.ReserveSocketChannel,
                    lease.SessionId,
                    checked(session.LastCommandSequence + 1),
                    lease,
                    role), timeout.Token);

                if (reservation.Accepted)
                {
                    if (reservation.Session is not { } reservedSession ||
                        reservedSession.SessionId != lease.SessionId ||
                        reservedSession.VmId != lease.VmId ||
                        reservedSession.State != WireSessionState.Running ||
                        !reservedSession.ReservedChannels.Contains(role))
                        throw new InvalidDataException(
                            "Supervisor did not confirm the requested channel reservation.");
                    return true;
                }

                if (reservation.ErrorCode != "OutOfSequence")
                    return false;
            }

            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Supervisor lease authorization exceeded its deadline.");
        }
    }

    private async Task<WireResponse> SendAsync(
        WireRequest request,
        CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using (client)
        {
            await client.ConnectAsync(3000, cancellationToken);
            var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            await WriteFrameAsync(client, payload, cancellationToken);
            var responsePayload = await ReadFrameAsync(client, cancellationToken)
                ?? throw new EndOfStreamException("Supervisor closed IPC before replying.");
            var response = DecodeResponse(responsePayload);
            if (response.RequestId != request.RequestId)
                throw new InvalidDataException("Supervisor IPC response correlation mismatch.");
            return response;
        }
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("Supervisor IPC request exceeds its size limit.");

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]?> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        var firstByte = new byte[1];
        if (await stream.ReadAsync(firstByte, cancellationToken) == 0)
            return null;
        prefix[0] = firstByte[0];
        await ReadExactlyAsync(stream, prefix.AsMemory(1), cancellationToken);

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("Supervisor IPC response length is outside its bounds.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
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
                throw new EndOfStreamException("Supervisor IPC frame was truncated.");
            total += read;
        }
    }

    private static WireResponse DecodeResponse(ReadOnlySpan<byte> payload)
    {
        RejectDuplicateProperties(payload);
        var response = JsonSerializer.Deserialize<WireResponse>(payload, JsonOptions)
            ?? throw new InvalidDataException("Supervisor IPC response is empty.");
        if (response.Version != ProtocolVersion ||
            response.RequestId == Guid.Empty ||
            response.ErrorCode is { Length: > 64 } ||
            response.Accepted && response.ErrorCode is not null ||
            !response.Accepted && string.IsNullOrWhiteSpace(response.ErrorCode) ||
            response.Session is { } session && !IsValidSnapshot(session))
            throw new InvalidDataException("Supervisor IPC response failed validation.");
        return response;
    }

    private static bool IsValidSnapshot(WireSessionSnapshot session) =>
        session.SessionId != Guid.Empty &&
        Enum.IsDefined(session.State) &&
        (session.VmId is null || session.VmId != Guid.Empty) &&
        session.CreatedAtUtc.Offset == TimeSpan.Zero &&
        session.ExpiresAtUtc.Offset == TimeSpan.Zero &&
        session.ExpiresAtUtc > session.CreatedAtUtc &&
        session.LastCommandSequence <= MaximumCommandSequence &&
        (session.CleanupFailure is null || Enum.IsDefined(session.CleanupFailure.Value)) &&
        session.ReservedChannels is not null &&
        session.ReservedChannels.Count <= 2 &&
        session.ReservedChannels.All(role => Enum.IsDefined(role)) &&
        session.ReservedChannels.Distinct().Count() == session.ReservedChannels.Count;

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
                        throw new InvalidDataException(
                            "Supervisor IPC response contains duplicate properties.");
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

    private static void ValidateLease(HyperVPreviewVmLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.SessionId == Guid.Empty || lease.VmId == Guid.Empty ||
            string.IsNullOrWhiteSpace(lease.ResourceId) ||
            lease.ResourceId.Length > 256 ||
            lease.ResourceId.Any(char.IsControl))
            throw new ArgumentException("A complete managed VM lease is required.", nameof(lease));
    }

    private static void ValidatePipeName(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 128 ||
            pipeName.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                                      character is not ('.' or '_' or '-')))
            throw new ArgumentException("Invalid local supervisor pipe name.", nameof(pipeName));
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

    private sealed record WireRequest(
        int Version,
        Guid RequestId,
        WireCommand Command,
        Guid? SessionId,
        ulong Sequence,
        HyperVPreviewVmLease? Lease,
        HyperVPreviewChannelRole? ChannelRole);

    private sealed record WireResponse(
        int Version,
        Guid RequestId,
        bool Accepted,
        string? ErrorCode,
        WireSessionSnapshot? Session);

    private sealed record WireSessionSnapshot(
        Guid SessionId,
        WireSessionState State,
        Guid? VmId,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        ulong LastCommandSequence,
        SupervisorCleanupFailure? CleanupFailure,
        List<HyperVPreviewChannelRole> ReservedChannels);

    private enum WireCommand
    {
        GetStatus,
        ReserveSocketChannel,
    }

    private enum WireSessionState
    {
        Provisioning,
        Provisioned,
        Running,
        Stopping,
        CleanupFailed,
        Stopped,
        Expired,
    }

    private enum SupervisorCleanupFailure
    {
        ResourceLookupFailed,
        LeaseMismatch,
        StateQueryFailed,
        StopUnconfirmed,
        DestroyUnconfirmed,
        JournalFailure,
    }
}
