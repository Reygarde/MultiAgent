using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using AIPlatform.PreviewGuestAgent;

namespace AIPlatform.Tests;

public sealed class HyperVPreviewSocketTransportTests
{
    [Fact]
    public void SerializesOnlyThePlannedWindowsServicesAndConcreteVmIds()
    {
        var lease = NewLease();
        var endpoint = new HyperVPreviewWindowsSocketEndPoint(
            lease, HyperVPreviewChannelRole.Control);
        var address = endpoint.Serialize();

        Assert.Equal(HyperVPreviewWindowsSocketEndPoint.HyperVAddressFamily, (int)address.Family);
        Assert.Equal(0, address[2]);
        Assert.Equal(0, address[3]);
        Assert.Equal(lease.VmId, ReadWindowsGuid(address, 4));
        Assert.Equal(
            Guid.Parse("0000c391-facb-11e6-bd58-64006a7986d3"),
            ReadWindowsGuid(address, 20));
        var decoded = Assert.IsType<HyperVPreviewWindowsSocketEndPoint>(endpoint.Create(address));
        Assert.Equal(lease.VmId, decoded.VmId);
        Assert.Equal(HyperVPreviewChannelRole.Control, decoded.Role);

        var unknownService = endpoint.Serialize();
        WriteWindowsGuid(Guid.NewGuid(), unknownService, 20);
        Assert.Throws<InvalidDataException>(() => endpoint.Create(unknownService));

        var wildcardVm = endpoint.Serialize();
        WriteWindowsGuid(Guid.Parse("90db8b89-0d35-4f79-8ce9-49ea0ac8b7cd"), wildcardVm, 4);
        Assert.Throws<InvalidDataException>(() => endpoint.Create(wildcardVm));
        Assert.Throws<ArgumentException>(() => new HyperVPreviewWindowsSocketEndPoint(
            lease with { VmId = Guid.Empty }, HyperVPreviewChannelRole.Control));
    }

    [Theory]
    [InlineData(HyperVPreviewChannelRole.Control, 50065)]
    [InlineData(HyperVPreviewChannelRole.Frames, 50066)]
    public void SerializesOnlyPlannedLinuxVsockPorts(HyperVPreviewChannelRole role, uint port)
    {
        var endpoint = new HyperVPreviewVsockEndPoint(role);
        var address = endpoint.Serialize();

        Assert.Equal(HyperVPreviewVsockEndPoint.VsockAddressFamily, (int)address.Family);
        Assert.Equal(0, address[2]);
        Assert.Equal(0, address[3]);
        Assert.Equal(port, ReadNativeUInt32(address, 4));
        Assert.Equal(HyperVPreviewVsockEndPoint.AnyCid, ReadNativeUInt32(address, 8));
        Assert.All(Enumerable.Range(12, 4), index => Assert.Equal((byte)0, address[index]));
        var decoded = Assert.IsType<HyperVPreviewVsockPeerEndPoint>(endpoint.Create(address));
        Assert.Equal(port, decoded.Port);

        var unknownPort = endpoint.Serialize();
        WriteNativeUInt32(1234, unknownPort, 4);
        var peer = Assert.IsType<HyperVPreviewVsockPeerEndPoint>(endpoint.Create(unknownPort));
        Assert.Equal(1234u, peer.Port);
        unknownPort[12] = 1;
        Assert.Throws<InvalidDataException>(() => endpoint.Create(unknownPort));
    }

    [Fact]
    public void SessionRegistryAllowsTwoChannelsButRejectsReuseAndVmSubstitution()
    {
        var registry = new HyperVPreviewSocketSessionRegistry();
        var lease = NewLease();
        registry.Reserve(lease.SessionId, lease.VmId, HyperVPreviewChannelRole.Control);
        registry.Reserve(lease.SessionId, lease.VmId, HyperVPreviewChannelRole.Frames);

        Assert.Throws<InvalidOperationException>(() =>
            registry.Reserve(lease.SessionId, lease.VmId, HyperVPreviewChannelRole.Control));
        Assert.Throws<InvalidOperationException>(() =>
            registry.Reserve(lease.SessionId, Guid.NewGuid(), HyperVPreviewChannelRole.Frames));
    }

    [Fact]
    public async Task WindowsConnectorUsesPinnedEndpointAndPreventsSessionReuse()
    {
        var lease = NewLease();
        var sockets = new FakeSocketConnector();
        var connector = new WindowsHvSocketPreviewConnector(
            sockets, new HyperVPreviewSocketSessionRegistry(), new FakeLeaseAuthorizer(lease));

        await using var control = await connector.ConnectAsync(
            lease,
            HyperVPreviewChannelRole.Control,
            TimeSpan.FromSeconds(1),
            default);
        Assert.IsType<HyperVPreviewWindowsSocketEndPoint>(sockets.LastEndpoint);
        var endpoint = Assert.IsType<HyperVPreviewWindowsSocketEndPoint>(sockets.LastEndpoint);
        Assert.Equal(lease.VmId, endpoint.VmId);
        Assert.Equal(HyperVPreviewChannelRole.Control, endpoint.Role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.ConnectAsync(
            lease,
            HyperVPreviewChannelRole.Control,
            TimeSpan.FromSeconds(1),
            default));
        await using var frames = await connector.ConnectAsync(
            lease,
            HyperVPreviewChannelRole.Frames,
            TimeSpan.FromSeconds(1),
            default);
        Assert.Equal(2, sockets.ConnectCount);
    }

    [Fact]
    public async Task FailedSocketConnectionDoesNotReleaseTheChannelReservation()
    {
        var lease = NewLease();
        var sockets = new FakeSocketConnector
        {
            Connect = (_, _) => throw new IOException("Simulated connection failure."),
        };
        var authorizer = new FakeLeaseAuthorizer(lease);
        var connector = new WindowsHvSocketPreviewConnector(
            sockets, new HyperVPreviewSocketSessionRegistry(), authorizer);

        await Assert.ThrowsAsync<IOException>(() => connector.ConnectAsync(
            lease,
            HyperVPreviewChannelRole.Control,
            TimeSpan.FromSeconds(1),
            default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.ConnectAsync(
            lease,
            HyperVPreviewChannelRole.Control,
            TimeSpan.FromSeconds(1),
            default));
        Assert.Equal(1, sockets.ConnectCount);
    }

    [Fact]
    public async Task WindowsConnectorRejectsInvalidLeaseAndBoundsConnectionTimeout()
    {
        var sockets = new FakeSocketConnector();
        var lease = NewLease();
        var connector = new WindowsHvSocketPreviewConnector(
            sockets, new HyperVPreviewSocketSessionRegistry(), new FakeLeaseAuthorizer(lease));

        await Assert.ThrowsAsync<ArgumentException>(() => connector.ConnectAsync(
            lease with { VmId = Guid.Empty },
            HyperVPreviewChannelRole.Control,
            TimeSpan.FromSeconds(1),
            default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.ConnectAsync(
            NewLease(),
            HyperVPreviewChannelRole.Control,
            TimeSpan.FromSeconds(1),
            default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connector.ConnectAsync(
            lease,
            HyperVPreviewChannelRole.Control,
            HyperVPreviewSocketPolicy.SocketConnectTimeout + TimeSpan.FromMilliseconds(1),
            default));
        Assert.Equal(0, sockets.ConnectCount);

        var cancellationObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sockets.Connect = async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The delay unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult(true);
                throw;
            }
        };
        await Assert.ThrowsAsync<TimeoutException>(() => connector.ConnectAsync(
            lease,
            HyperVPreviewChannelRole.Control,
            TimeSpan.FromMilliseconds(50),
            default));
        Assert.True(await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsTimeout()
    {
        var sockets = new FakeSocketConnector
        {
            Connect = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The delay unexpectedly completed.");
            },
        };
        var lease = NewLease();
        var connector = new WindowsHvSocketPreviewConnector(
            sockets, new HyperVPreviewSocketSessionRegistry(), new FakeLeaseAuthorizer(lease));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.ConnectAsync(
            lease,
            HyperVPreviewChannelRole.Control,
            TimeSpan.FromSeconds(1),
            cancellation.Token));
    }

    [Fact]
    public async Task WindowsConnectorRejectsUnexpectedRemoteEndpoint()
    {
        var lease = NewLease();
        var sockets = new FakeSocketConnector
        {
            Connect = (_, _) => Task.FromResult<IHyperVPreviewSocketConnection>(
                new FakeSocketConnection(remoteEndPoint: new HyperVPreviewWindowsSocketEndPoint(
                    lease with { VmId = Guid.NewGuid() },
                    HyperVPreviewChannelRole.Control))),
        };
        var connector = new WindowsHvSocketPreviewConnector(
            sockets,
            new HyperVPreviewSocketSessionRegistry(),
            new FakeLeaseAuthorizer(lease));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.ConnectAsync(
            lease,
            HyperVPreviewChannelRole.Control,
            TimeSpan.FromSeconds(1),
            default));
    }

    [Fact]
    public async Task LinuxListenerUsesRolePortValidatesSessionAndAcceptsOnlyOnce()
    {
        var lease = NewLease();
        var factory = new FakeSocketListenerFactory();
        var registry = new HyperVPreviewSocketSessionRegistry();
        var listener = await LinuxVsockPreviewListener.ListenAsync(
            lease.SessionId,
            lease.VmId,
            HyperVPreviewChannelRole.Frames,
            factory,
            registry,
            TimeSpan.FromSeconds(1),
            default);

        var endpoint = Assert.IsType<HyperVPreviewVsockEndPoint>(factory.LastEndpoint);
        Assert.Equal(HyperVPreviewChannelRole.Frames, endpoint.Role);
        Assert.Equal(HyperVPreviewSocketServiceCatalog.FramesPort, endpoint.Port);
        var transport = await listener.AcceptAsync(TimeSpan.FromSeconds(1), default);
        await using var channel = new HyperVPreviewMessageChannel(
            transport, HyperVPreviewChannelRole.Frames, lease.SessionId);

        var foreignSessionMessage = new HyperVPreviewMessage(
            Guid.NewGuid(), 1, HyperVPreviewMessageType.Acknowledge, []);
        factory.Listener.Connection.Feed(
            HyperVPreviewMessageCodec.Encode(foreignSessionMessage, HyperVPreviewChannelRole.Frames));
        await Assert.ThrowsAsync<InvalidDataException>(() => channel.ReceiveAsync(default));
        Assert.True(factory.Listener.IsDisposed);
        Assert.Throws<InvalidOperationException>(() =>
            registry.Reserve(lease.SessionId, lease.VmId, HyperVPreviewChannelRole.Frames));
    }

    [Fact]
    public async Task LinuxListenerTimesOutAndClosesWhenNoPeerArrives()
    {
        var factory = new FakeSocketListenerFactory { BlockAccept = true };
        var listener = await LinuxVsockPreviewListener.ListenAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            HyperVPreviewChannelRole.Control,
            factory,
            new HyperVPreviewSocketSessionRegistry(),
            TimeSpan.FromSeconds(1),
            default);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            listener.AcceptAsync(TimeSpan.FromMilliseconds(50), default));
        Assert.True(factory.Listener.IsDisposed);
    }

    [Fact]
    public async Task LinuxListenerRejectsUnexpectedPeerCid()
    {
        var factory = new FakeSocketListenerFactory
        {
            RemoteEndPoint = new HyperVPreviewVsockPeerEndPoint(0, 3),
        };
        var listener = await LinuxVsockPreviewListener.ListenAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            HyperVPreviewChannelRole.Control,
            factory,
            new HyperVPreviewSocketSessionRegistry(),
            TimeSpan.FromSeconds(1),
            default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            listener.AcceptAsync(TimeSpan.FromSeconds(1), default));
        Assert.True(factory.Listener.IsDisposed);
        Assert.True(factory.Listener.Connection.IsDisposed);
    }

    [Fact]
    public async Task SocketTransportHandlesPartialIoCancellationMalformedFramesAndCloseDuringRead()
    {
        var socket = new FakeSocketConnection(maximumReadBytes: 3, maximumWriteBytes: 2);
        await using var transport = new HyperVPreviewSocketTransport(socket);
        socket.Feed([1, 2, 3, 4, 5]);

        var firstRead = new byte[8];
        Assert.Equal(3, await transport.ReadAsync(firstRead, default));
        Assert.Equal(2, await transport.ReadAsync(firstRead.AsMemory(3), default));

        await transport.WriteAsync(new byte[] { 9, 8, 7, 6, 5 }, default);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, socket.WrittenBytes);

        using var cancellation = new CancellationTokenSource();
        var cancelledRead = transport.ReadAsync(new byte[8], cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRead);

        var sessionId = Guid.NewGuid();
        var secondSocket = new FakeSocketConnection();
        await using var malformedTransport = new HyperVPreviewSocketTransport(secondSocket);
        var message = new HyperVPreviewMessage(
            sessionId, 1, HyperVPreviewMessageType.Acknowledge, []);
        var malformedFrame = HyperVPreviewMessageCodec.Encode(message, HyperVPreviewChannelRole.Control);
        malformedFrame[36] = 1;
        secondSocket.Feed(malformedFrame);
        await using var channel = new HyperVPreviewMessageChannel(
            malformedTransport, HyperVPreviewChannelRole.Control, sessionId);
        await Assert.ThrowsAsync<InvalidDataException>(() => channel.ReceiveAsync(default));

        var thirdSocket = new FakeSocketConnection();
        var closingTransport = new HyperVPreviewSocketTransport(thirdSocket);
        var pendingRead = closingTransport.ReadAsync(new byte[8], default).AsTask();
        await closingTransport.DisposeAsync();
        Assert.Equal(0, await pendingRead.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    private static HyperVPreviewVmLease NewLease() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "managed-test-resource");

    private static Guid ReadWindowsGuid(SocketAddress address, int offset)
    {
        Span<byte> bytes = stackalloc byte[16];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = address[offset + index];
        return new Guid(bytes, bigEndian: false);
    }

    private static void WriteWindowsGuid(Guid value, SocketAddress address, int offset)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: false, out _);
        for (var index = 0; index < bytes.Length; index++)
            address[offset + index] = bytes[index];
    }

    private static uint ReadNativeUInt32(SocketAddress address, int offset)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = address[offset + index];
        return BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static void WriteNativeUInt32(uint value, SocketAddress address, int offset)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (BitConverter.IsLittleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        for (var index = 0; index < bytes.Length; index++)
            address[offset + index] = bytes[index];
    }

    private sealed class FakeSocketConnector : IHyperVPreviewSocketConnector
    {
        public EndPoint? LastEndpoint { get; private set; }
        public int ConnectCount { get; private set; }
        public Func<EndPoint, CancellationToken, Task<IHyperVPreviewSocketConnection>> Connect { get; set; } =
            (endpoint, _) => Task.FromResult<IHyperVPreviewSocketConnection>(
                new FakeSocketConnection(remoteEndPoint: endpoint));

        public Task<IHyperVPreviewSocketConnection> ConnectAsync(
            EndPoint endpoint,
            CancellationToken cancellationToken)
        {
            LastEndpoint = endpoint;
            ConnectCount++;
            return Connect(endpoint, cancellationToken);
        }

    }

    private sealed class FakeLeaseAuthorizer(params HyperVPreviewVmLease[] leases)
        : IHyperVPreviewManagedLeaseAuthorizer
    {
        private readonly HashSet<HyperVPreviewVmLease> _leases = leases.ToHashSet();
        private readonly HashSet<(Guid SessionId, HyperVPreviewChannelRole Role)> _reservations = [];

        public Task<bool> TryAuthorizeAndReserveAsync(
            HyperVPreviewVmLease lease,
            HyperVPreviewChannelRole role,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _leases.Contains(lease) && _reservations.Add((lease.SessionId, role)));
        }
    }

    private sealed class FakeSocketListenerFactory : IHyperVPreviewSocketListenerFactory
    {
        public EndPoint? LastEndpoint { get; private set; }
        public bool BlockAccept { get; init; }
        public EndPoint? RemoteEndPoint { get; init; } =
            new HyperVPreviewVsockPeerEndPoint(0, HyperVPreviewVsockEndPoint.HypervisorHostCid);
        public FakeSocketListener Listener { get; } = new();

        public Task<IHyperVPreviewSocketListener> ListenAsync(
            EndPoint endpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastEndpoint = endpoint;
            Listener.BlockAccept = BlockAccept;
            Listener.Connection.RemoteEndPoint = RemoteEndPoint;
            return Task.FromResult<IHyperVPreviewSocketListener>(Listener);
        }
    }

    private sealed class FakeSocketListener : IHyperVPreviewSocketListener
    {
        public FakeSocketConnection Connection { get; } = new();
        public bool BlockAccept { get; set; }
        public bool IsDisposed { get; private set; }

        public async Task<IHyperVPreviewSocketConnection> AcceptAsync(CancellationToken cancellationToken)
        {
            if (BlockAccept)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Connection;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSocketConnection(
        int maximumReadBytes = int.MaxValue,
        int maximumWriteBytes = int.MaxValue,
        EndPoint? remoteEndPoint = null) : IHyperVPreviewSocketConnection
    {
        private readonly Channel<byte[]> _reads = Channel.CreateUnbounded<byte[]>();
        private readonly List<byte> _written = [];
        private byte[]? _current;
        private int _offset;
        private int _disposed;

        public EndPoint? RemoteEndPoint { get; set; } = remoteEndPoint;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public byte[] WrittenBytes
        {
            get
            {
                lock (_written)
                    return _written.ToArray();
            }
        }

        public void Feed(byte[] bytes)
        {
            if (!_reads.Writer.TryWrite(bytes.ToArray()))
                throw new InvalidOperationException("Fake socket read channel is closed.");
        }

        public async ValueTask<int> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            while (_current is null || _offset == _current.Length)
            {
                _current = null;
                _offset = 0;
                if (!await _reads.Reader.WaitToReadAsync(cancellationToken))
                    return 0;
                if (!_reads.Reader.TryRead(out _current))
                    continue;
            }

            var count = Math.Min(
                Math.Min(buffer.Length, _current.Length - _offset),
                maximumReadBytes);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public ValueTask<int> SendAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var count = Math.Min(buffer.Length, maximumWriteBytes);
            lock (_written)
                _written.AddRange(buffer.Span[..count].ToArray());
            return ValueTask.FromResult(count);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _reads.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
