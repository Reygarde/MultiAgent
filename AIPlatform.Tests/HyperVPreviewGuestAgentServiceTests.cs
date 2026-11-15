using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIPlatform.Core.Studio;
using AIPlatform.PreviewGuestAgent;
using AIPlatform.PreviewGuestAgent.Testing;

namespace AIPlatform.Tests;

public sealed class HyperVPreviewGuestAgentServiceTests
{
    [Fact]
    public async Task LoadsOnlyCompleteBoundedSessionCredentials()
    {
        var challenges = HyperVPreviewConnectionChallenges.Create();
        using var configuration = HyperVPreviewGuestAgentConfiguration.Parse(
            CreateConfigurationBytes(Guid.NewGuid(), Guid.NewGuid(), challenges, DateTimeOffset.UtcNow.AddSeconds(90)));
        Assert.Equal(32, configuration.Challenges.Control.Length);
        Assert.False(configuration.Challenges.Control.AsSpan()
            .SequenceEqual(configuration.Challenges.Frames));

        var missing = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HyperVPreviewGuestAgentConfiguration.LoadAsync(missing));
        Assert.Throws<InvalidDataException>(() =>
            HyperVPreviewGuestAgentConfiguration.Parse("{}"u8.ToArray()));

        var duplicate = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"schemaVersion\":1}");
        Assert.Throws<InvalidDataException>(() =>
            HyperVPreviewGuestAgentConfiguration.Parse(duplicate));
        Assert.Throws<InvalidDataException>(() =>
            HyperVPreviewGuestAgentConfiguration.Parse(CreateConfigurationBytes(
                Guid.NewGuid(),
                Guid.NewGuid(),
                new HyperVPreviewConnectionChallenges(new byte[32], RandomNumberGenerator.GetBytes(32)),
                DateTimeOffset.UtcNow.AddSeconds(30))));
        Assert.Throws<InvalidDataException>(() =>
            HyperVPreviewGuestAgentConfiguration.Parse(CreateConfigurationBytes(
                Guid.NewGuid(),
                Guid.NewGuid(),
                HyperVPreviewConnectionChallenges.Create(),
                DateTimeOffset.UtcNow.AddSeconds(171))));
    }

    [Fact]
    public async Task LoadsOwnerOnlyCredentialFileAndRejectsSharedPermissionsOnLinux()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        var challenges = HyperVPreviewConnectionChallenges.Create();
        try
        {
            await File.WriteAllBytesAsync(path, CreateConfigurationBytes(
                Guid.NewGuid(), Guid.NewGuid(), challenges, DateTimeOffset.UtcNow.AddSeconds(90)));
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, UnixFileMode.UserRead);
            using var loaded = await HyperVPreviewGuestAgentConfiguration.LoadAsync(path);
            Assert.NotEqual(Guid.Empty, loaded.SessionId);

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    HyperVPreviewGuestAgentConfiguration.LoadAsync(path));
            }
        }
        finally
        {
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(path);
            CryptographicOperations.ZeroMemory(challenges.Control);
            CryptographicOperations.ZeroMemory(challenges.Frames);
        }
    }

    [Fact]
    public async Task RunsACompleteAuthenticatedControlSessionThroughTheServiceAndFakeVsock()
    {
        using var configuration = CreateConfiguration(TimeSpan.FromSeconds(120));
        var digest = SHA256.HashData("test-agent-image"u8);
        var (factory, hostTransport, connection) = FakeSocketListenerFactory.Create();
        var service = new HyperVPreviewGuestAgentService(factory);
        await using var host = new HyperVPreviewMessageChannel(
            hostTransport, HyperVPreviewChannelRole.Control, configuration.SessionId);

        var running = service.RunAsync(configuration, digest, default);
        var endpoint = Assert.IsType<HyperVPreviewVsockEndPoint>(factory.Endpoint);
        Assert.Equal(HyperVPreviewChannelRole.Control, endpoint.Role);
        Assert.Equal(HyperVPreviewSocketServiceCatalog.ControlPort, endpoint.Port);

        var hostNonce = RandomNumberGenerator.GetBytes(32);
        var emptyGuestNonce = new byte[32];
        const uint hostLifetimeMilliseconds = 30_000;
        var hostProof = HyperVPreviewGuestAuthenticator.CreateProof(
            configuration.Challenges.Control,
            configuration.SessionId,
            configuration.VmId,
            HyperVPreviewChannelRole.Control,
            emptyGuestNonce,
            hostNonce,
            hostLifetimeMilliseconds,
            HyperVPreviewProofParty.Host);
        var hello = HyperVPreviewGuestControlPayloadCodec.EncodeHello(
            HyperVPreviewChannelRole.Control,
            configuration.Challenges.Control,
            hostNonce,
            hostLifetimeMilliseconds,
            hostProof);
        await host.SendAsync(new HyperVPreviewMessage(
            configuration.SessionId, 1, HyperVPreviewMessageType.Hello, hello), default);

        var helloAckMessage = await RequiredMessageAsync(host, 1);
        Assert.Equal(HyperVPreviewMessageType.HelloAck, helloAckMessage.Type);
        var helloAck = HyperVPreviewGuestControlPayloadCodec.DecodeHelloAck(helloAckMessage.Payload);
        Assert.True(HyperVPreviewGuestAuthenticator.VerifyProof(
            configuration.Challenges.Control,
            configuration.SessionId,
            configuration.VmId,
            HyperVPreviewChannelRole.Control,
            helloAck.GuestNonce,
            hostNonce,
            hostLifetimeMilliseconds,
            HyperVPreviewProofParty.Guest,
            helloAck.GuestProof));
        Assert.True(CryptographicOperations.FixedTimeEquals(digest, helloAck.AgentDigest));
        Assert.Equal(HyperVPreviewMessageType.Ready, (await RequiredMessageAsync(host, 2)).Type);
        Assert.Equal(HyperVPreviewMessageType.Status, (await RequiredMessageAsync(host, 3)).Type);

        await host.SendAsync(new HyperVPreviewMessage(
            configuration.SessionId, 2, HyperVPreviewMessageType.Stop, []), default);
        Assert.Equal((byte)HyperVPreviewGuestState.Stopping,
            (await RequiredMessageAsync(host, 4)).Payload[0]);
        Assert.Equal((byte)HyperVPreviewGuestState.Stopped,
            (await RequiredMessageAsync(host, 5)).Payload[0]);
        Assert.Equal(HyperVPreviewMessageType.CleanupConfirmed,
            (await RequiredMessageAsync(host, 6)).Type);

        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HyperVPreviewGuestAgentExitReason.SupervisorStop, result.ExitReason);
        Assert.Equal(1, factory.Listener.AcceptCount);
        Assert.True(factory.Listener.IsDisposed);
        Assert.True(connection.IsDisposed);
        CryptographicOperations.ZeroMemory(digest);
        CryptographicOperations.ZeroMemory(hostNonce);
        CryptographicOperations.ZeroMemory(emptyGuestNonce);
        CryptographicOperations.ZeroMemory(hostProof);
        CryptographicOperations.ZeroMemory(hello);
    }

    [Fact]
    public async Task RefusesExpiredConfigurationBeforeOpeningTheListener()
    {
        using var configuration = CreateConfiguration(TimeSpan.FromMilliseconds(100));
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        var (factory, hostTransport, _) = FakeSocketListenerFactory.Create();
        await using var host = hostTransport;
        var service = new HyperVPreviewGuestAgentService(factory);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RunAsync(configuration, new byte[32], default));
        Assert.Null(factory.Endpoint);
        Assert.Equal(0, factory.Listener.AcceptCount);
    }

    [Fact]
    public async Task ExpiresAnAcceptedSessionAtItsAbsoluteDeadlineWithoutAHeartbeat()
    {
        using var configuration = CreateConfiguration(TimeSpan.FromMilliseconds(150));
        var (factory, hostTransport, connection) = FakeSocketListenerFactory.Create();
        await using var host = hostTransport;
        var service = new HyperVPreviewGuestAgentService(factory);

        var result = await service.RunAsync(configuration, new byte[32], default)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HyperVPreviewGuestAgentExitReason.SessionExpired, result.ExitReason);
        Assert.True(factory.Listener.IsDisposed);
        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task EnforcesAbsoluteSessionExpiryWhileWaitingForTheHost()
    {
        using var configuration = CreateConfiguration(TimeSpan.FromMilliseconds(150));
        var factory = FakeSocketListenerFactory.Create(blockAccept: true).Factory;
        var service = new HyperVPreviewGuestAgentService(factory);

        var result = await service.RunAsync(
                configuration,
                new byte[32],
                default,
                TimeSpan.FromSeconds(1))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HyperVPreviewGuestAgentExitReason.SessionExpired, result.ExitReason);
        Assert.NotNull(factory.Endpoint);
        Assert.True(factory.Listener.IsDisposed);
    }

    [Fact]
    public async Task RejectsInvalidHandshakeAndNeverExecutesInput()
    {
        using var configuration = CreateConfiguration(TimeSpan.FromSeconds(60));
        var (factory, hostTransport, connection) = FakeSocketListenerFactory.Create();
        var service = new HyperVPreviewGuestAgentService(factory);
        await using var host = new HyperVPreviewMessageChannel(
            hostTransport, HyperVPreviewChannelRole.Control, configuration.SessionId);
        var running = service.RunAsync(configuration, new byte[32], default);

        var badChallenge = RandomNumberGenerator.GetBytes(32);
        var hostNonce = RandomNumberGenerator.GetBytes(32);
        var hostProof = HyperVPreviewGuestAuthenticator.CreateProof(
            badChallenge,
            configuration.SessionId,
            configuration.VmId,
            HyperVPreviewChannelRole.Control,
            new byte[32],
            hostNonce,
            20_000,
            HyperVPreviewProofParty.Host);
        var hello = HyperVPreviewGuestControlPayloadCodec.EncodeHello(
            HyperVPreviewChannelRole.Control,
            badChallenge,
            hostNonce,
            20_000,
            hostProof);
        await host.SendAsync(new HyperVPreviewMessage(
            configuration.SessionId, 1, HyperVPreviewMessageType.Hello, hello), default);
        var invalidHandshake = await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HyperVPreviewGuestAgentExitReason.ProtocolRejected, invalidHandshake.ExitReason);
        Assert.True(connection.IsDisposed);

        CryptographicOperations.ZeroMemory(badChallenge);
        CryptographicOperations.ZeroMemory(hostNonce);
        CryptographicOperations.ZeroMemory(hostProof);
        CryptographicOperations.ZeroMemory(hello);
    }

    [Fact]
    public async Task ListenerInitializationFailureAndCancellationAreExplicitAndCleanedUp()
    {
        using var configuration = CreateConfiguration(TimeSpan.FromSeconds(60));
        var failedFactory = FakeSocketListenerFactory.Create(failListen: true).Factory;
        var failedService = new HyperVPreviewGuestAgentService(failedFactory);
        await Assert.ThrowsAsync<IOException>(() =>
            failedService.RunAsync(configuration, new byte[32], default));
        Assert.Equal(0, failedFactory.Listener.AcceptCount);

        var blockedFactory = FakeSocketListenerFactory.Create(blockAccept: true).Factory;
        var blockedService = new HyperVPreviewGuestAgentService(blockedFactory);
        using var cancellation = new CancellationTokenSource();
        var pending = blockedService.RunAsync(
            configuration,
            new byte[32],
            cancellation.Token,
            TimeSpan.FromSeconds(2));
        await blockedFactory.Listener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(blockedFactory.Listener.IsDisposed);
    }

    [Fact]
    public async Task InitializationTimeoutAndConcurrentAcceptFailClosed()
    {
        using var configuration = CreateConfiguration(TimeSpan.FromSeconds(60));
        var blockedFactory = FakeSocketListenerFactory.Create(blockListen: true).Factory;
        var service = new HyperVPreviewGuestAgentService(blockedFactory);
        await Assert.ThrowsAsync<TimeoutException>(() => service.RunAsync(
            configuration,
            new byte[32],
            default,
            TimeSpan.FromMilliseconds(40)));
        Assert.True(blockedFactory.ListenCancellationObserved.Task.IsCompleted);

        var listenerFactory = FakeSocketListenerFactory.Create(blockAccept: true).Factory;
        var listener = await LinuxVsockPreviewListener.ListenAsync(
            configuration.SessionId,
            configuration.VmId,
            HyperVPreviewChannelRole.Control,
            listenerFactory,
            new HyperVPreviewSocketSessionRegistry(),
            TimeSpan.FromSeconds(1),
            default);
        using var acceptCancellation = new CancellationTokenSource();
        var firstAccept = listener.AcceptAsync(TimeSpan.FromSeconds(1), acceptCancellation.Token);
        await listenerFactory.Listener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            listener.AcceptAsync(TimeSpan.FromSeconds(1), default));
        acceptCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstAccept);
        Assert.True(listenerFactory.Listener.IsDisposed);
    }

    [Fact]
    public async Task DisconnectDuringServiceSessionClosesTheAcceptedSocket()
    {
        using var configuration = CreateConfiguration(TimeSpan.FromSeconds(60));
        var (factory, hostTransport, connection) = FakeSocketListenerFactory.Create();
        var service = new HyperVPreviewGuestAgentService(factory);
        var running = service.RunAsync(configuration, new byte[32], default);
        await hostTransport.DisposeAsync();

        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HyperVPreviewGuestAgentExitReason.TransportDisconnected, result.ExitReason);
        Assert.True(connection.IsDisposed);
        Assert.True(factory.Listener.IsDisposed);
    }

    [Fact]
    public async Task MeasuresExecutableDigestWithoutLoggingOrChangingIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        var contents = "guest-agent-test-image"u8.ToArray();
        await File.WriteAllBytesAsync(path, contents);
        try
        {
            var actual = await HyperVPreviewGuestAgentBinaryDigest.ComputeExecutableSha256Async(path);
            Assert.Equal(SHA256.HashData(contents), actual);
            CryptographicOperations.ZeroMemory(actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HyperVPreviewGuestAgentConfiguration CreateConfiguration(TimeSpan lifetime)
    {
        var challenges = HyperVPreviewConnectionChallenges.Create();
        return HyperVPreviewGuestAgentConfiguration.Parse(CreateConfigurationBytes(
            Guid.NewGuid(),
            Guid.NewGuid(),
            challenges,
            DateTimeOffset.UtcNow.Add(lifetime)));
    }

    private static byte[] CreateConfigurationBytes(
        Guid sessionId,
        Guid vmId,
        HyperVPreviewConnectionChallenges challenges,
        DateTimeOffset expiresAt) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            sessionId,
            vmId,
            controlChallenge = Convert.ToBase64String(challenges.Control),
            framesChallenge = Convert.ToBase64String(challenges.Frames),
            expiresAtUnixTimeMilliseconds = expiresAt.ToUnixTimeMilliseconds(),
        });

    private static async Task<HyperVPreviewMessage> RequiredMessageAsync(
        HyperVPreviewMessageChannel channel,
        ulong expectedSequence)
    {
        var message = await channel.ReceiveAsync(default).WaitAsync(TimeSpan.FromSeconds(2))
            ?? throw new EndOfStreamException("In-memory host peer disconnected.");
        Assert.Equal(expectedSequence, message.Sequence);
        return message;
    }

    private sealed class FakeSocketListenerFactory : IHyperVPreviewSocketListenerFactory
    {
        private FakeSocketListenerFactory(
            IHyperVPreviewSocketConnection connection,
            bool blockAccept)
        {
            Listener = new FakeSocketListener(connection, blockAccept);
        }

        public EndPoint? Endpoint { get; private set; }
        public bool FailListen { get; init; }
        public bool BlockListen { get; init; }
        public FakeSocketListener Listener { get; }
        public TaskCompletionSource<bool> ListenCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static (FakeSocketListenerFactory Factory, InMemoryHyperVPreviewTransport HostTransport,
            FakeSocketConnection Connection) Create(
                bool blockAccept = false,
                bool blockListen = false,
                bool failListen = false)
        {
            var (guest, host) = InMemoryHyperVPreviewTransport.CreatePair(7);
            var connection = new FakeSocketConnection(
                guest,
                new HyperVPreviewVsockPeerEndPoint(
                    HyperVPreviewSocketServiceCatalog.ControlPort,
                    HyperVPreviewVsockEndPoint.HypervisorHostCid));
            var factory = new FakeSocketListenerFactory(connection, blockAccept)
            {
                BlockListen = blockListen,
                FailListen = failListen,
            };
            return (factory, host, connection);
        }

        public async Task<IHyperVPreviewSocketListener> ListenAsync(
            EndPoint endpoint,
            CancellationToken cancellationToken)
        {
            Endpoint = endpoint;
            if (FailListen)
                throw new IOException("Simulated listener initialization failure.");
            if (BlockListen)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    ListenCancellationObserved.TrySetResult(true);
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Listener;
        }
    }

    private sealed class FakeSocketListener(
        IHyperVPreviewSocketConnection connection,
        bool blockAccept)
        : IHyperVPreviewSocketListener
    {
        private int _acceptCount;
        public int AcceptCount => Volatile.Read(ref _acceptCount);
        public bool IsDisposed { get; private set; }
        public TaskCompletionSource<bool> AcceptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IHyperVPreviewSocketConnection> AcceptAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _acceptCount) != 1)
                throw new InvalidOperationException("The listener permits one connection only.");
            AcceptStarted.TrySetResult(true);
            if (blockAccept)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return connection;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSocketConnection(
        InMemoryHyperVPreviewTransport transport,
        EndPoint remoteEndPoint) : IHyperVPreviewSocketConnection
    {
        private int _disposed;
        public EndPoint? RemoteEndPoint { get; } = remoteEndPoint;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            transport.ReadAsync(buffer, cancellationToken);

        public async ValueTask<int> SendAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            await transport.WriteAsync(buffer, cancellationToken);
            return buffer.Length;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                return transport.DisposeAsync();
            return ValueTask.CompletedTask;
        }
    }
}
