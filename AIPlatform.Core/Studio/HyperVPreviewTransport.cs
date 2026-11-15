namespace AIPlatform.Core.Studio;

public interface IHyperVPreviewTransport : IAsyncDisposable
{
    // Reads may be partial; a successful write consumes the entire buffer.
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}

public sealed class HyperVPreviewMessageChannel : IAsyncDisposable
{
    private readonly IHyperVPreviewTransport _transport;
    private readonly HyperVPreviewChannelRole _role;
    private readonly Guid _sessionId;
    private bool _disposed;

    public HyperVPreviewMessageChannel(
        IHyperVPreviewTransport transport,
        HyperVPreviewChannelRole role,
        Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A non-empty session id is required.", nameof(sessionId));
        _transport = transport;
        _role = role;
        _sessionId = sessionId;
    }

    public async Task SendAsync(HyperVPreviewMessage message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (message.SessionId != _sessionId)
            throw new InvalidDataException("Preview message session id does not match the transport.");
        var encoded = HyperVPreviewMessageCodec.Encode(message, _role);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HyperVPreviewPolicy.ControlWriteTimeout);
        try
        {
            await _transport.WriteAsync(encoded, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Preview message write exceeded its deadline.");
        }
    }

    public async Task<HyperVPreviewMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var header = new byte[HyperVPreviewPolicy.HeaderBytes];
        if (!await ReadExactlyAsync(
                header,
                HyperVPreviewPolicy.ControlHeaderTimeout,
                allowCleanEnd: true,
                cancellationToken))
            return null;

        var payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32, 4));
        var maximumPayload = _role == HyperVPreviewChannelRole.Control
            ? HyperVPreviewPolicy.MaximumControlPayloadBytes
            : HyperVPreviewPolicy.MaximumMessageBytes;
        if (payloadLength > maximumPayload)
            throw new InvalidDataException("Preview message exceeds the transport payload limit.");

        var encoded = new byte[header.Length + (int)payloadLength];
        header.CopyTo(encoded, 0);
        if (payloadLength > 0 &&
            !await ReadExactlyAsync(
                encoded.AsMemory(header.Length),
                HyperVPreviewPolicy.ControlPayloadTimeout,
                allowCleanEnd: false,
                cancellationToken))
            throw new InvalidDataException("Preview message payload was truncated.");

        return HyperVPreviewMessageCodec.Decode(encoded, _role, _sessionId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _transport.DisposeAsync();
    }

    private async Task<bool> ReadExactlyAsync(
        Memory<byte> destination,
        TimeSpan timeoutDuration,
        bool allowCleanEnd,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDuration);
        var total = 0;
        try
        {
            while (total < destination.Length)
            {
                var read = await _transport.ReadAsync(destination[total..], timeout.Token);
                if (read < 0 || read > destination.Length - total)
                    throw new IOException("Preview transport returned an invalid read length.");
                if (read == 0)
                {
                    if (allowCleanEnd && total == 0)
                        return false;
                    throw new InvalidDataException("Preview message ended before the frame was complete.");
                }
                total += read;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Preview message read exceeded its deadline.");
        }
        return true;
    }
}
