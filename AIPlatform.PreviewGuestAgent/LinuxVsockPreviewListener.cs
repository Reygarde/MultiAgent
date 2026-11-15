using System.Net;
using System.Net.Sockets;
using AIPlatform.Core.Studio;

namespace AIPlatform.PreviewGuestAgent;

public sealed class LinuxVsockPreviewListener : IAsyncDisposable
{
    private readonly IHyperVPreviewSocketListener _listener;
    private int _acceptStarted;
    private int _disposed;

    private LinuxVsockPreviewListener(IHyperVPreviewSocketListener listener) =>
        _listener = listener;

    public static async Task<LinuxVsockPreviewListener> ListenAsync(
        Guid sessionId,
        Guid vmId,
        HyperVPreviewChannelRole role,
        IHyperVPreviewSocketListenerFactory listenerFactory,
        HyperVPreviewSocketSessionRegistry sessionRegistry,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listenerFactory);
        ArgumentNullException.ThrowIfNull(sessionRegistry);
        if (timeout <= TimeSpan.Zero || timeout > HyperVPreviewSocketPolicy.SocketAcceptTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var endpoint = new HyperVPreviewVsockEndPoint(role);
        sessionRegistry.Reserve(sessionId, vmId, role);
        var listener = await HyperVPreviewSocketTimeout.RunAsync(
            token => listenerFactory.ListenAsync(endpoint, token),
            timeout,
            cancellationToken);
        return new LinuxVsockPreviewListener(listener);
    }

    public async Task<IHyperVPreviewTransport> AcceptAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (timeout <= TimeSpan.Zero || timeout > HyperVPreviewSocketPolicy.SocketAcceptTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (Interlocked.Exchange(ref _acceptStarted, 1) != 0)
            throw new InvalidOperationException("Each HvSocket listener accepts one session only.");

        try
        {
            var connection = await HyperVPreviewSocketTimeout.RunAsync(
                token => _listener.AcceptAsync(token),
                timeout,
                cancellationToken);
            if (connection.RemoteEndPoint is not HyperVPreviewVsockPeerEndPoint peerEndpoint ||
                peerEndpoint.Cid != HyperVPreviewVsockEndPoint.HypervisorHostCid)
            {
                await connection.DisposeAsync();
                throw new UnauthorizedAccessException("AF_VSOCK peer is not the Hyper-V host CID.");
            }
            return new HyperVPreviewSocketTransport(connection);
        }
        finally
        {
            await DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await _listener.DisposeAsync();
    }
}

public sealed class LinuxVsockNativeListenerFactory : IHyperVPreviewSocketListenerFactory
{
    public Task<IHyperVPreviewSocketListener> ListenAsync(
        EndPoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("AF_VSOCK listeners are available only on Linux.");
        if (endpoint is not HyperVPreviewVsockEndPoint vsockEndpoint ||
            vsockEndpoint.Cid != HyperVPreviewVsockEndPoint.AnyCid ||
            !HyperVPreviewSocketServiceCatalog.TryGetRole(vsockEndpoint.Port, out var allowedRole) ||
            allowedRole != vsockEndpoint.Role)
            throw new InvalidDataException("AF_VSOCK endpoint is not allowlisted.");

        var socket = new Socket(
            (AddressFamily)HyperVPreviewVsockEndPoint.VsockAddressFamily,
            SocketType.Stream,
            ProtocolType.Unspecified);
        try
        {
            socket.Bind(vsockEndpoint);
            socket.Listen(1);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IHyperVPreviewSocketListener>(
                new LinuxVsockNativeListener(socket));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class LinuxVsockNativeListener(Socket socket) : IHyperVPreviewSocketListener
{
    private readonly Socket _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    private int _disposed;

    public async Task<IHyperVPreviewSocketConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var accepted = await _socket.AcceptAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SystemHyperVPreviewSocketConnection(accepted);
        }
        catch
        {
            accepted.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
