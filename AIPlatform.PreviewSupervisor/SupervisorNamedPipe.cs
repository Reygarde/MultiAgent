using System.IO.Pipes;
using System.Text.Json;

namespace AIPlatform.PreviewSupervisor;

public sealed class SupervisorNamedPipeServer
{
    private readonly string _pipeName;
    private readonly SupervisedPreviewCoordinator _coordinator;

    public SupervisorNamedPipeServer(string pipeName, SupervisedPreviewCoordinator coordinator)
    {
        ValidatePipeName(pipeName);
        _pipeName = pipeName;
        _coordinator = coordinator;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Hyper-V supervisor IPC host is Windows-only.");

        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                inBufferSize: SupervisorPolicy.MaximumIpcFrameBytes,
                outBufferSize: SupervisorPolicy.MaximumIpcFrameBytes);
            await server.WaitForConnectionAsync(cancellationToken);

            SupervisorIpcRequest? request = null;
            try
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var payload = await SupervisorIpcCodec.ReadFrameAsync(server, readTimeout.Token);
                if (payload is null)
                    continue;

                request = SupervisorIpcCodec.DecodeRequest(payload);
                var response = await _coordinator.HandleAsync(request, cancellationToken);
                await SupervisorIpcCodec.WriteFrameAsync(
                    server,
                    SupervisorIpcCodec.EncodeResponse(response),
                    cancellationToken);
            }
            catch (SupervisorProtocolException error)
            {
                Console.Error.WriteLine($"Supervisor IPC rejected a request: {error.ErrorCode}");
                try
                {
                    var response = new SupervisorIpcResponse(
                        SupervisorPolicy.ProtocolVersion,
                        request?.RequestId ?? Guid.NewGuid(),
                        false,
                        error.ErrorCode,
                        null);
                    await SupervisorIpcCodec.WriteFrameAsync(
                        server,
                        SupervisorIpcCodec.EncodeResponse(response),
                        cancellationToken);
                }
                catch (Exception writeError) when (writeError is IOException or OperationCanceledException)
                {
                    Console.Error.WriteLine($"Supervisor IPC rejection response failed: {writeError.GetType().Name}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or
                                               JsonException or TimeoutException or OperationCanceledException)
            {
                Console.Error.WriteLine($"Supervisor IPC connection rejected: {error.GetType().Name}");
            }
        }
    }

    private static void ValidatePipeName(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 128 ||
            pipeName.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                                      character is not ('.' or '_' or '-')))
            throw new ArgumentException("Invalid local supervisor pipe name.", nameof(pipeName));
    }
}

public static class SupervisorNamedPipeClient
{
    public static async Task<SupervisorIpcResponse> SendAsync(
        string pipeName,
        SupervisorIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await using (client.ConfigureAwait(false))
        {
            await client.ConnectAsync(3000, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await SupervisorIpcCodec.WriteFrameAsync(
                client,
                SupervisorIpcCodec.EncodeRequest(request),
                timeout.Token);
            var responsePayload = await SupervisorIpcCodec.ReadFrameAsync(client, timeout.Token)
                ?? throw new EndOfStreamException("Supervisor closed IPC before replying.");
            var response = SupervisorIpcCodec.DecodeResponse(responsePayload);
            if (response.RequestId != request.RequestId)
                throw new InvalidDataException("Supervisor IPC response correlation mismatch.");
            return response;
        }
    }
}
