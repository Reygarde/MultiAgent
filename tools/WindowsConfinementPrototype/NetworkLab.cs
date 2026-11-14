using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WindowsConfinementPrototype;

internal sealed class NetworkLab : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly List<TcpListener> listeners = [];
    private readonly List<UdpClient> sockets = [];
    private readonly List<Task> services = [];
    private readonly ConcurrentBag<Task> clients = [];
    private readonly ConcurrentDictionary<string, long> counters = new();
    private string phase = "unassigned";
    internal string Phase
    {
        set
        {
            foreach (var metric in new[] { "http-connections", "other-connections", "ipv6-connections",
                         "stun-packets", "dns-packets", "websocket-requests", "redirect-requests" })
                counters.TryAdd(value + "/" + metric, 0);
            Volatile.Write(ref phase, value);
        }
    }
    internal int Http { get; }
    internal int Other { get; }
    internal int IPv6 { get; }
    internal int Stun { get; }
    internal int Dns { get; }

    internal NetworkLab()
    {
        try
        {
            Other = Listen("other", IPAddress.Loopback);
            Http = Listen("http", IPAddress.Loopback);
            IPv6 = Listen("ipv6", IPAddress.IPv6Loopback);
            Stun = ListenUdp("stun", false);
            Dns = ListenUdp("dns", true);
        }
        catch (SocketException)
        {
            stop.Cancel();
            foreach (var listener in listeners) listener.Stop();
            foreach (var socket in sockets) socket.Dispose();
            throw;
        }
    }

    internal Dictionary<string, long> Snapshot(string name) =>
        counters.Where(pair => pair.Key.StartsWith(name + "/", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key[(name.Length + 1)..], pair => pair.Value);

    private void Count(string current, string name) =>
        counters.AddOrUpdate(current + "/" + name, 1, (_, count) => count + 1);

    private int Listen(string name, IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start(32);
        listeners.Add(listener);
        services.Add(AcceptAsync(listener, name));
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task AcceptAsync(TcpListener listener, string name)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                var current = Volatile.Read(ref phase);
                Count(current, name + "-connections");
                clients.Add(ReplyAsync(client, name, current));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task ReplyAsync(TcpClient client, string name, string current)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var count = await stream.ReadAsync(buffer, timeout.Token);
                if (count == 0) return;
                var text = Encoding.ASCII.GetString(buffer, 0, count);
                var redirected = name == "http" && text.Split("\r\n")[0].Contains("/redirect ", StringComparison.Ordinal);
                if (redirected) Count(current, "redirect-requests");
                if (text.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase)) Count(current, "websocket-requests");
                var response = redirected
                    ? $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{Other}/redirect-target\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Count(current, "client-timeouts"); }
            catch (IOException) { Count(current, "client-disconnects"); }
        }
    }

    private int ListenUdp(string name, bool dns)
    {
        var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        sockets.Add(socket);
        services.Add(ReceiveAsync(socket, name, dns));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private async Task ReceiveAsync(UdpClient socket, string name, bool dns)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await socket.ReceiveAsync(stop.Token);
                var current = Volatile.Read(ref phase);
                Count(current, name + "-packets");
                if (dns && packet.Buffer.Length >= 12)
                {
                    var response = packet.Buffer;
                    response[2] = 0x81; response[3] = 0x83;
                    await socket.SendAsync(response, packet.RemoteEndPoint, stop.Token);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await Task.WhenAll(services);
        await Task.WhenAll(clients);
        foreach (var listener in listeners) listener.Stop();
        foreach (var socket in sockets) socket.Dispose();
        stop.Dispose();
    }
}
