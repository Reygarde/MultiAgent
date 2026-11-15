using System.Threading.Channels;
using AIPlatform.Core.Studio;

namespace AIPlatform.PreviewGuestAgent.Testing;

public sealed class InMemoryHyperVPreviewTransport : IHyperVPreviewTransport
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private readonly int _maximumChunkBytes;
    private byte[]? _currentChunk;
    private int _currentChunkOffset;
    private int _disposed;

    private InMemoryHyperVPreviewTransport(
        Channel<byte[]> incoming,
        Channel<byte[]> outgoing,
        int maximumChunkBytes)
    {
        _incoming = incoming;
        _outgoing = outgoing;
        _maximumChunkBytes = maximumChunkBytes;
    }

    public static (InMemoryHyperVPreviewTransport First, InMemoryHyperVPreviewTransport Second)
        CreatePair(int maximumChunkBytes = 13)
    {
        if (maximumChunkBytes <= 0 || maximumChunkBytes > 64 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumChunkBytes));
        var firstToSecond = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var secondToFirst = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        return (
            new InMemoryHyperVPreviewTransport(secondToFirst, firstToSecond, maximumChunkBytes),
            new InMemoryHyperVPreviewTransport(firstToSecond, secondToFirst, maximumChunkBytes));
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty)
            return 0;
        while (_currentChunk is null || _currentChunkOffset == _currentChunk.Length)
        {
            _currentChunk = null;
            _currentChunkOffset = 0;
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
                return 0;
            if (!_incoming.Reader.TryRead(out _currentChunk))
                continue;
        }

        var count = Math.Min(buffer.Length, _currentChunk.Length - _currentChunkOffset);
        _currentChunk.AsMemory(_currentChunkOffset, count).CopyTo(buffer);
        _currentChunkOffset += count;
        return count;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = Math.Min(_maximumChunkBytes, buffer.Length - offset);
            var chunk = buffer.Slice(offset, count).ToArray();
            try
            {
                await _outgoing.Writer.WriteAsync(chunk, cancellationToken);
            }
            catch (ChannelClosedException error)
            {
                throw new IOException("In-memory peer has disconnected.", error);
            }
            offset += count;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _outgoing.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
