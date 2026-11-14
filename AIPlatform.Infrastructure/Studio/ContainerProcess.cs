using System.Diagnostics;
using System.Text;

namespace AIPlatform.Infrastructure.Studio;

public sealed record ContainerProcessResult(int ExitCode, string Output);

public interface IContainerProcess
{
    Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
    Task<ContainerProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        int maximumOutputCharacters) => RunAsync(arguments, cancellationToken);
}

public sealed class ContainerProcess : IContainerProcess
{
    public Task<ContainerProcessResult> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(arguments, cancellationToken, 65536);

    public async Task<ContainerProcessResult> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken, int maximumOutputCharacters)
    {
        if (maximumOutputCharacters is < 1 or > 2097152)
            throw new ArgumentOutOfRangeException(nameof(maximumOutputCharacters));
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new()
            {
                FileName = ResolveDockerExecutable(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            throw new IOException("Unable to start the Docker CLI.");
        var output = new StringBuilder();
        var sync = new object();
        var truncated = false;
        var stdout = Drain(process.StandardOutput);
        var stderr = Drain(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        if (truncated)
            output.Append($"\n[Log truncated at {maximumOutputCharacters} characters.]");
        return new(process.ExitCode, output.ToString());

        async Task Drain(StreamReader reader)
        {
            var buffer = new char[2048];
            int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
            {
                lock (sync)
                {
                    var remaining = maximumOutputCharacters - output.Length;
                    if (remaining > 0)
                        output.Append(buffer, 0, Math.Min(remaining, count));
                    if (count > remaining)
                        truncated = true;
                }
            }
        }
    }

    private static string ResolveDockerExecutable()
    {
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Docker", "Docker", "resources", "bin", "docker.exe");
        return OperatingSystem.IsWindows() && File.Exists(desktop) ? desktop : "docker";
    }
}
