using System.Security.Cryptography;
using System.Runtime.InteropServices;
using AIPlatform.Core.Studio;
using AIPlatform.PreviewGuestAgent.Testing;

namespace AIPlatform.PreviewGuestAgent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            try
            {
                await RunSelfTestAsync();
                Console.WriteLine("SELF_TEST_OK");
                return 0;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
            {
                Console.Error.WriteLine($"SELF_TEST_FAILED {error.GetType().Name}");
                return 1;
            }
        }

        if (args.Length != 2 || args[0] != "--serve")
        {
            Console.Error.WriteLine("Usage: agent --self-test | --serve <systemd-credential-path>");
            return 64;
        }

        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("SERVE_REQUIRES_LINUX");
            return 78;
        }

        try
        {
            return await RunServeAsync(args[1]);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"AGENT_FAILED {error.GetType().Name}");
            return 1;
        }
    }

    private static async Task<int> RunServeAsync(string credentialPath)
    {
        var path = ValidateCredentialPath(credentialPath);
        using var configuration = await HyperVPreviewGuestAgentConfiguration.LoadAsync(path);
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The agent executable path is unavailable.");
        var digest = await HyperVPreviewGuestAgentBinaryDigest.ComputeExecutableSha256Async(executablePath);
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        try
        {
            Console.CancelKeyPress += cancelHandler;
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });

            var service = new HyperVPreviewGuestAgentService(
                new LinuxVsockNativeListenerFactory());
            Console.WriteLine("AGENT_STARTED");
            var result = await service.RunAsync(
                configuration,
                digest,
                shutdown.Token);
            Console.WriteLine($"AGENT_EXIT {result.ExitReason}");
            return result.ExitReason is HyperVPreviewGuestAgentExitReason.SupervisorStop or
                HyperVPreviewGuestAgentExitReason.SessionExpired
                ? 0
                : 1;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            Console.WriteLine("AGENT_STOPPED");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static string ValidateCredentialPath(string path)
    {
        var credentialDirectory = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        if (string.IsNullOrWhiteSpace(credentialDirectory))
            throw new InvalidDataException("Systemd credential directory is not configured.");
        var expectedPath = Path.GetFullPath(Path.Combine(credentialDirectory, "agent.json"));
        if (!string.Equals(
                Path.GetFullPath(path),
                expectedPath,
                StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Agent configuration must come from the systemd credential.");
        return expectedPath;
    }

    private static async Task RunSelfTestAsync()
    {
        var sessionId = Guid.NewGuid();
        var vmId = Guid.NewGuid();
        var challenges = HyperVPreviewConnectionChallenges.Create();
        var agentDigest = SHA256.HashData("local-self-test-agent"u8);
        var (guestTransport, hostTransport) = InMemoryHyperVPreviewTransport.CreatePair(7);
        var agent = new HyperVPreviewGuestAgent(new HyperVPreviewGuestAgentOptions(
            sessionId,
            vmId,
            challenges,
            agentDigest,
            TimeSpan.FromSeconds(10)));
        var agentTask = agent.RunAsync(guestTransport, default);
        await using var host = new HyperVPreviewMessageChannel(
            hostTransport,
            HyperVPreviewChannelRole.Control,
            sessionId);

        var hostNonce = RandomNumberGenerator.GetBytes(32);
        const uint remainingLifetimeMilliseconds = 5000;
        var emptyGuestNonce = new byte[32];
        var hostProof = HyperVPreviewGuestAuthenticator.CreateProof(
            challenges.Control,
            sessionId,
            vmId,
            HyperVPreviewChannelRole.Control,
            emptyGuestNonce,
            hostNonce,
            remainingLifetimeMilliseconds,
            HyperVPreviewProofParty.Host);
        var helloPayload = HyperVPreviewGuestControlPayloadCodec.EncodeHello(
            HyperVPreviewChannelRole.Control,
            challenges.Control,
            hostNonce,
            remainingLifetimeMilliseconds,
            hostProof);
        await host.SendAsync(new HyperVPreviewMessage(
            sessionId, 1, HyperVPreviewMessageType.Hello, helloPayload), default);
        var helloAckMessage = await RequiredMessageAsync(host, 1);
        if (helloAckMessage.Type != HyperVPreviewMessageType.HelloAck)
            throw new InvalidDataException("Guest did not acknowledge the host Hello.");
        var helloAck = HyperVPreviewGuestControlPayloadCodec.DecodeHelloAck(helloAckMessage.Payload);
        if (!CryptographicOperations.FixedTimeEquals(challenges.Control, helloAck.Challenge) ||
            !CryptographicOperations.FixedTimeEquals(agentDigest, helloAck.AgentDigest) ||
            !HyperVPreviewGuestAuthenticator.VerifyProof(
                challenges.Control,
                sessionId,
                vmId,
                HyperVPreviewChannelRole.Control,
                helloAck.GuestNonce,
                hostNonce,
                remainingLifetimeMilliseconds,
                HyperVPreviewProofParty.Guest,
                helloAck.GuestProof))
            throw new InvalidDataException("Guest HelloAck authentication or digest is invalid.");

        var ready = await RequiredMessageAsync(host, 2);
        var waiting = await RequiredMessageAsync(host, 3);
        if (ready.Type != HyperVPreviewMessageType.Ready ||
            !HyperVPreviewGuestControlPayloadCodec.VerifyReady(
                ready.Payload, HyperVPreviewGuestState.Authenticated, agentDigest) ||
            waiting.Type != HyperVPreviewMessageType.Status ||
            waiting.Payload[0] != (byte)HyperVPreviewGuestState.Waiting)
            throw new InvalidDataException("Guest did not reach the waiting state.");

        await host.SendAsync(new HyperVPreviewMessage(
            sessionId, 2, HyperVPreviewMessageType.Stop, []), default);
        var stopping = await RequiredMessageAsync(host, 4);
        var stopped = await RequiredMessageAsync(host, 5);
        var cleanup = await RequiredMessageAsync(host, 6);
        var result = await agentTask.WaitAsync(TimeSpan.FromSeconds(2));
        if (stopping.Payload[0] != (byte)HyperVPreviewGuestState.Stopping ||
            stopped.Payload[0] != (byte)HyperVPreviewGuestState.Stopped ||
            cleanup.Type != HyperVPreviewMessageType.CleanupConfirmed ||
            result.ExitReason != HyperVPreviewGuestAgentExitReason.SupervisorStop)
            throw new InvalidDataException("Guest did not confirm the simulated stop.");

        CryptographicOperations.ZeroMemory(emptyGuestNonce);
        CryptographicOperations.ZeroMemory(hostNonce);
        CryptographicOperations.ZeroMemory(hostProof);
        CryptographicOperations.ZeroMemory(agentDigest);
    }

    private static async Task<HyperVPreviewMessage> RequiredMessageAsync(
        HyperVPreviewMessageChannel channel,
        ulong expectedSequence)
    {
        var message = await channel.ReceiveAsync(default).WaitAsync(TimeSpan.FromSeconds(2))
            ?? throw new EndOfStreamException("In-memory peer disconnected.");
        if (message.Sequence != expectedSequence)
            throw new InvalidDataException("In-memory peer sequence is invalid.");
        return message;
    }
}
