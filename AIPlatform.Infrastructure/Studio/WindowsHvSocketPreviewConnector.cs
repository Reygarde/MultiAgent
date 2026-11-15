using System.Net;
using System.Net.Sockets;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class WindowsHvSocketPreviewConnector
{
    private readonly IHyperVPreviewSocketConnector _socketConnector;
    private readonly HyperVPreviewSocketSessionRegistry _sessionRegistry;
    private readonly IHyperVPreviewManagedLeaseAuthorizer _leaseAuthorizer;

    public WindowsHvSocketPreviewConnector(
        IHyperVPreviewSocketConnector socketConnector,
        HyperVPreviewSocketSessionRegistry sessionRegistry,
        IHyperVPreviewManagedLeaseAuthorizer leaseAuthorizer)
    {
        _socketConnector = socketConnector ?? throw new ArgumentNullException(nameof(socketConnector));
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _leaseAuthorizer = leaseAuthorizer ?? throw new ArgumentNullException(nameof(leaseAuthorizer));
    }

    public async Task<IHyperVPreviewTransport> ConnectAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewChannelRole role,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (timeout <= TimeSpan.Zero || timeout > HyperVPreviewSocketPolicy.SocketConnectTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var endpoint = new HyperVPreviewWindowsSocketEndPoint(lease, role);
        var authorized = await HyperVPreviewSocketTimeout.RunAsync(
            token => _leaseAuthorizer.TryAuthorizeAndReserveAsync(lease, role, token),
            timeout,
            cancellationToken);
        if (!authorized)
            throw new UnauthorizedAccessException("HvSocket requires the exact active AIPlatform VM lease.");

        _sessionRegistry.Reserve(lease.SessionId, lease.VmId, role);
        var connection = await HyperVPreviewSocketTimeout.RunAsync(
            token => _socketConnector.ConnectAsync(endpoint, token),
            timeout,
            cancellationToken);
        if (connection.RemoteEndPoint is not HyperVPreviewWindowsSocketEndPoint remoteEndpoint ||
            remoteEndpoint.VmId != endpoint.VmId ||
            remoteEndpoint.ServiceId != endpoint.ServiceId ||
            remoteEndpoint.Role != endpoint.Role)
        {
            await connection.DisposeAsync();
            throw new UnauthorizedAccessException("HvSocket connected to an unexpected VM or service endpoint.");
        }
        return new HyperVPreviewSocketTransport(connection);
    }
}

public sealed class WindowsHvSocketNativeConnector : IHyperVPreviewSocketConnector
{
    public async Task<IHyperVPreviewSocketConnection> ConnectAsync(
        EndPoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("AF_HYPERV connections are available only on Windows.");
        if (endpoint is not HyperVPreviewWindowsSocketEndPoint hyperVEndpoint ||
            !HyperVPreviewSocketServiceCatalog.TryGetRole(
                hyperVEndpoint.ServiceId,
                out var allowedRole) ||
            allowedRole != hyperVEndpoint.Role)
            throw new InvalidDataException("Windows HvSocket endpoint is not allowlisted.");

        var socket = new Socket(
            (AddressFamily)HyperVPreviewWindowsSocketEndPoint.HyperVAddressFamily,
            SocketType.Stream,
            (ProtocolType)HyperVPreviewWindowsSocketEndPoint.HyperVRawProtocol);
        try
        {
            await socket.ConnectAsync(hyperVEndpoint, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new SystemHyperVPreviewSocketConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
