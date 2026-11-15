using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AIPlatform.Core.Studio;

public static class HyperVPreviewSocketServiceCatalog
{
    public const uint ControlPort = 50065;
    public const uint FramesPort = 50066;

    private static readonly Guid ControlServiceId = Guid.Parse("0000c391-facb-11e6-bd58-64006a7986d3");
    private static readonly Guid FramesServiceId = Guid.Parse("0000c392-facb-11e6-bd58-64006a7986d3");

    public static Guid GetServiceId(HyperVPreviewChannelRole role) => role switch
    {
        HyperVPreviewChannelRole.Control => ControlServiceId,
        HyperVPreviewChannelRole.Frames => FramesServiceId,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static uint GetPort(HyperVPreviewChannelRole role) => role switch
    {
        HyperVPreviewChannelRole.Control => ControlPort,
        HyperVPreviewChannelRole.Frames => FramesPort,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static bool TryGetRole(Guid serviceId, out HyperVPreviewChannelRole role)
    {
        if (serviceId == ControlServiceId)
        {
            role = HyperVPreviewChannelRole.Control;
            return true;
        }

        if (serviceId == FramesServiceId)
        {
            role = HyperVPreviewChannelRole.Frames;
            return true;
        }

        role = default;
        return false;
    }

    public static bool TryGetRole(uint port, out HyperVPreviewChannelRole role)
    {
        if (port == ControlPort)
        {
            role = HyperVPreviewChannelRole.Control;
            return true;
        }

        if (port == FramesPort)
        {
            role = HyperVPreviewChannelRole.Frames;
            return true;
        }

        role = default;
        return false;
    }
}

public sealed class HyperVPreviewWindowsSocketEndPoint : EndPoint
{
    public const int HyperVAddressFamily = 34;
    public const int HyperVRawProtocol = 1;
    public const int SerializedSize = 36;

    public HyperVPreviewWindowsSocketEndPoint(
        HyperVPreviewVmLease lease,
        HyperVPreviewChannelRole role)
        : this(ValidateVmId(lease), HyperVPreviewSocketServiceCatalog.GetServiceId(role), role)
    {
    }

    private HyperVPreviewWindowsSocketEndPoint(
        Guid vmId,
        Guid serviceId,
        HyperVPreviewChannelRole role)
    {
        if (!IsManagedVmId(vmId))
            throw new ArgumentException("HvSocket requires a concrete, managed VM id.", nameof(vmId));
        if (!HyperVPreviewSocketServiceCatalog.TryGetRole(serviceId, out var serviceRole) ||
            serviceRole != role)
            throw new ArgumentException("HvSocket service id is not authorized for this channel.", nameof(serviceId));

        VmId = vmId;
        ServiceId = serviceId;
        Role = role;
    }

    public Guid VmId { get; }
    public Guid ServiceId { get; }
    public HyperVPreviewChannelRole Role { get; }
    public override AddressFamily AddressFamily => (AddressFamily)HyperVAddressFamily;

    public override SocketAddress Serialize()
    {
        var address = new SocketAddress(AddressFamily, SerializedSize);
        address[2] = 0;
        address[3] = 0;
        WriteGuid(VmId, address, 4);
        WriteGuid(ServiceId, address, 20);
        return address;
    }

    public override EndPoint Create(SocketAddress socketAddress)
    {
        ArgumentNullException.ThrowIfNull(socketAddress);
        if (socketAddress.Family != AddressFamily ||
            socketAddress.Size != SerializedSize ||
            socketAddress[2] != 0 ||
            socketAddress[3] != 0)
            throw new InvalidDataException("Invalid Hyper-V socket address.");

        var vmId = ReadGuid(socketAddress, 4);
        var serviceId = ReadGuid(socketAddress, 20);
        if (!IsManagedVmId(vmId) ||
            !HyperVPreviewSocketServiceCatalog.TryGetRole(serviceId, out var role))
            throw new InvalidDataException("Hyper-V socket VM or service id is not allowlisted.");
        return new HyperVPreviewWindowsSocketEndPoint(vmId, serviceId, role);
    }

    internal static bool IsManagedVmId(Guid vmId) =>
        vmId != Guid.Empty &&
        vmId != Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") &&
        vmId != Guid.Parse("90db8b89-0d35-4f79-8ce9-49ea0ac8b7cd") &&
        vmId != Guid.Parse("e0e16197-dd56-4a10-9195-5ee7a155a838") &&
        vmId != Guid.Parse("a42e7cda-d03f-480c-9cc2-a4de20abb878");

    private static Guid ValidateVmId(HyperVPreviewVmLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.SessionId == Guid.Empty || !IsManagedVmId(lease.VmId) ||
            string.IsNullOrWhiteSpace(lease.ResourceId))
            throw new ArgumentException("A managed VM lease with non-empty immutable ids is required.", nameof(lease));
        return lease.VmId;
    }

    private static void WriteGuid(Guid value, SocketAddress address, int offset)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: false, out var written) || written != bytes.Length)
            throw new InvalidOperationException("Could not encode the native Windows GUID.");
        for (var index = 0; index < bytes.Length; index++)
            address[offset + index] = bytes[index];
    }

    private static Guid ReadGuid(SocketAddress address, int offset)
    {
        Span<byte> bytes = stackalloc byte[16];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = address[offset + index];
        return new Guid(bytes, bigEndian: false);
    }
}

public sealed class HyperVPreviewVsockEndPoint : EndPoint
{
    public const int VsockAddressFamily = 40;
    public const uint AnyCid = uint.MaxValue;
    public const uint HypervisorHostCid = 2;
    public const int SerializedSize = 16;

    public HyperVPreviewVsockEndPoint(HyperVPreviewChannelRole role)
        : this(role, AnyCid)
    {
    }

    private HyperVPreviewVsockEndPoint(HyperVPreviewChannelRole role, uint cid)
    {
        _ = HyperVPreviewSocketServiceCatalog.GetPort(role);
        Role = role;
        Cid = cid;
    }

    public HyperVPreviewChannelRole Role { get; }
    public uint Port => HyperVPreviewSocketServiceCatalog.GetPort(Role);
    public uint Cid { get; }
    public override AddressFamily AddressFamily => (AddressFamily)VsockAddressFamily;

    public override SocketAddress Serialize()
    {
        var address = new SocketAddress(AddressFamily, SerializedSize);
        address[2] = 0;
        address[3] = 0;
        WriteNativeUInt32(Port, address, 4);
        WriteNativeUInt32(Cid, address, 8);
        address[12] = 0;
        address[13] = 0;
        address[14] = 0;
        address[15] = 0;
        return address;
    }

    public override EndPoint Create(SocketAddress socketAddress)
    {
        ArgumentNullException.ThrowIfNull(socketAddress);
        if (socketAddress.Family != AddressFamily ||
            socketAddress.Size != SerializedSize ||
            socketAddress[2] != 0 ||
            socketAddress[3] != 0 ||
            socketAddress[12] != 0 ||
            socketAddress[13] != 0 ||
            socketAddress[14] != 0 ||
            socketAddress[15] != 0)
            throw new InvalidDataException("Invalid AF_VSOCK address.");

        return new HyperVPreviewVsockPeerEndPoint(
            ReadNativeUInt32(socketAddress, 4),
            ReadNativeUInt32(socketAddress, 8));
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

    private static uint ReadNativeUInt32(SocketAddress address, int offset)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = address[offset + index];
        return BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}

public sealed class HyperVPreviewVsockPeerEndPoint(uint port, uint cid) : EndPoint
{
    public uint Port { get; } = port;
    public uint Cid { get; } = cid;
    public override AddressFamily AddressFamily => (AddressFamily)HyperVPreviewVsockEndPoint.VsockAddressFamily;

    public override SocketAddress Serialize()
    {
        var address = new SocketAddress(AddressFamily, HyperVPreviewVsockEndPoint.SerializedSize);
        address[2] = 0;
        address[3] = 0;
        WriteNativeUInt32(Port, address, 4);
        WriteNativeUInt32(Cid, address, 8);
        address[12] = 0;
        address[13] = 0;
        address[14] = 0;
        address[15] = 0;
        return address;
    }

    public override EndPoint Create(SocketAddress socketAddress)
    {
        ArgumentNullException.ThrowIfNull(socketAddress);
        if (socketAddress.Family != AddressFamily ||
            socketAddress.Size != HyperVPreviewVsockEndPoint.SerializedSize ||
            socketAddress[2] != 0 ||
            socketAddress[3] != 0 ||
            socketAddress[12] != 0 ||
            socketAddress[13] != 0 ||
            socketAddress[14] != 0 ||
            socketAddress[15] != 0)
            throw new InvalidDataException("Invalid AF_VSOCK peer address.");

        return new HyperVPreviewVsockPeerEndPoint(
            ReadNativeUInt32(socketAddress, 4),
            ReadNativeUInt32(socketAddress, 8));
    }

    private static void WriteNativeUInt32(uint value, SocketAddress address, int offset)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        WriteNativeUInt32(value, bytes);
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

    private static void WriteNativeUInt32(uint value, Span<byte> bytes)
    {
        if (BitConverter.IsLittleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    }
}

public sealed class HyperVPreviewSocketSessionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, SessionEntry> _sessions = [];

    public void Reserve(Guid sessionId, Guid vmId, HyperVPreviewChannelRole role)
    {
        if (sessionId == Guid.Empty ||
            !HyperVPreviewWindowsSocketEndPoint.IsManagedVmId(vmId) ||
            !Enum.IsDefined(role))
            throw new ArgumentException("A unique session, managed VM id, and known channel are required.");

        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry))
            {
                _sessions.Add(sessionId, new SessionEntry(vmId, [role]));
                return;
            }

            if (entry.VmId != vmId || !entry.Roles.Add(role))
                throw new InvalidOperationException("Session id or channel has already been used.");
        }
    }

    private sealed record SessionEntry(Guid VmId, HashSet<HyperVPreviewChannelRole> Roles);
}

public interface IHyperVPreviewManagedLeaseAuthorizer
{
    Task<bool> TryAuthorizeAndReserveAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewChannelRole role,
        CancellationToken cancellationToken);
}

public interface IHyperVPreviewSocketConnection : IAsyncDisposable
{
    EndPoint? RemoteEndPoint { get; }
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}

public sealed class SystemHyperVPreviewSocketConnection(Socket socket) : IHyperVPreviewSocketConnection
{
    private readonly Socket _socket = socket ?? throw new ArgumentNullException(nameof(socket));

    public EndPoint? RemoteEndPoint => _socket.RemoteEndPoint;

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        _socket.SendAsync(buffer, SocketFlags.None, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

public interface IHyperVPreviewSocketConnector
{
    Task<IHyperVPreviewSocketConnection> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken);
}

public interface IHyperVPreviewSocketListener : IAsyncDisposable
{
    Task<IHyperVPreviewSocketConnection> AcceptAsync(CancellationToken cancellationToken);
}

public interface IHyperVPreviewSocketListenerFactory
{
    Task<IHyperVPreviewSocketListener> ListenAsync(EndPoint endpoint, CancellationToken cancellationToken);
}

public sealed class HyperVPreviewSocketTransport : IHyperVPreviewTransport
{
    private readonly IHyperVPreviewSocketConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    public HyperVPreviewSocketTransport(IHyperVPreviewSocketConnection connection) =>
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty)
            return 0;
        var received = await _connection.ReceiveAsync(buffer, cancellationToken);
        if (received < 0 || received > buffer.Length)
            throw new IOException("Hyper-V socket returned an invalid read length.");
        return received;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var sent = await _connection.SendAsync(buffer[offset..], cancellationToken);
                if (sent <= 0 || sent > buffer.Length - offset)
                    throw new IOException("Hyper-V socket closed or returned an invalid write length.");
                offset += sent;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await _connection.DisposeAsync();
    }
}

public static class HyperVPreviewSocketTimeout
{
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var result = await operation(timeoutSource.Token).WaitAsync(timeout, cancellationToken);
            if (timeoutSource.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                if (result is IAsyncDisposable disposable)
                    await disposable.DisposeAsync();
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Hyper-V socket operation exceeded its deadline.");
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("Hyper-V socket operation exceeded its deadline.");
            throw;
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            timeoutSource.Cancel();
            throw new TimeoutException("Hyper-V socket operation exceeded its deadline.");
        }
    }
}

public static class HyperVPreviewSocketPolicy
{
    public static readonly TimeSpan SocketConnectTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SocketAcceptTimeout = TimeSpan.FromSeconds(5);
}
