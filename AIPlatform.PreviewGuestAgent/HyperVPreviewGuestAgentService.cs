using System.Security.Cryptography;
using AIPlatform.Core.Studio;

namespace AIPlatform.PreviewGuestAgent;

public sealed class HyperVPreviewGuestAgentService
{
    public static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(5);

    private readonly IHyperVPreviewSocketListenerFactory _listenerFactory;
    private readonly HyperVPreviewSocketSessionRegistry _sessionRegistry;
    private readonly TimeProvider _clock;

    public HyperVPreviewGuestAgentService(
        IHyperVPreviewSocketListenerFactory listenerFactory,
        HyperVPreviewSocketSessionRegistry? sessionRegistry = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(listenerFactory);
        _listenerFactory = listenerFactory;
        _sessionRegistry = sessionRegistry ?? new HyperVPreviewSocketSessionRegistry();
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<HyperVPreviewGuestAgentResult> RunAsync(
        HyperVPreviewGuestAgentConfiguration configuration,
        ReadOnlyMemory<byte> measuredAgentDigest,
        CancellationToken cancellationToken,
        TimeSpan? initializationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (measuredAgentDigest.Length != 32)
            throw new ArgumentException("Measured agent digest must be SHA-256.", nameof(measuredAgentDigest));
        configuration.Validate(_clock);

        var timeout = initializationTimeout ?? InitializationTimeout;
        if (timeout <= TimeSpan.Zero || timeout > InitializationTimeout ||
            timeout > HyperVPreviewSocketPolicy.SocketAcceptTimeout)
            throw new ArgumentOutOfRangeException(nameof(initializationTimeout));

        var sessionLifetime = configuration.GetRemainingLifetime(_clock);
        var startedAt = _clock.GetTimestamp();
        using var sessionExpiry = new CancellationTokenSource(sessionLifetime, _clock);
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            sessionExpiry.Token);
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            sessionCancellation.Token);
        startupCancellation.CancelAfter(timeout);
        try
        {
            await using var listener = await LinuxVsockPreviewListener.ListenAsync(
                configuration.SessionId,
                configuration.VmId,
                HyperVPreviewChannelRole.Control,
                _listenerFactory,
                _sessionRegistry,
                RemainingInitializationTimeout(),
                startupCancellation.Token);
            var transport = await listener.AcceptAsync(
                RemainingInitializationTimeout(),
                startupCancellation.Token);

            var ownsTransport = false;
            try
            {
                var options = configuration.CreateAgentOptions(measuredAgentDigest.Span, _clock);
                try
                {
                    var agent = new HyperVPreviewGuestAgent(options, _clock);
                    ownsTransport = true;
                    return await agent.RunAsync(transport, sessionCancellation.Token);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(options.AgentDigest);
                    CryptographicOperations.ZeroMemory(options.Challenges.Control);
                    CryptographicOperations.ZeroMemory(options.Challenges.Frames);
                }
            }
            finally
            {
                if (!ownsTransport)
                    await transport.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && sessionExpiry.IsCancellationRequested)
        {
            return new HyperVPreviewGuestAgentResult(
                HyperVPreviewGuestAgentExitReason.SessionExpired,
                HyperVPreviewGuestAgentState.Stopped);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && startupCancellation.IsCancellationRequested)
        {
            throw new TimeoutException("Guest-agent listener initialization exceeded its deadline.");
        }

        TimeSpan RemainingInitializationTimeout()
        {
            var remaining = timeout - _clock.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Guest-agent listener initialization exceeded its deadline.");
            return remaining;
        }
    }
}

public static class HyperVPreviewGuestAgentBinaryDigest
{
    public static async Task<byte[]> ComputeExecutableSha256Async(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var file = new FileInfo(executablePath);
        if (!file.Exists || file.LinkTarget is not null ||
            (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Agent executable must be a regular file.");

        await using var stream = new FileStream(
            executablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }
}
