using System.Diagnostics;

namespace AIPlatform.PreviewSupervisor;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--simulate-client", StringComparer.Ordinal))
            return await RunSimulatedClientAsync(args);
        if (args.Contains("--simulate-studio", StringComparer.Ordinal))
            return await RunSimulatedStudioAsync(args);

        if (!args.Contains("--simulate", StringComparer.Ordinal))
        {
            Console.Error.WriteLine(
                "No Hyper-V adapter is included. This executable only runs with the explicit --simulate mode.");
            return 78;
        }

        try
        {
            var options = ParseOptions(args);
            using var journal = DurableSupervisorJournal.Open(options.JournalPath);
            var control = new SimulatedPersistentVmControl(
                options.ResourceStorePath,
                options.CrashAfterCreate);
            var coordinator = new SupervisedPreviewCoordinator(
                control,
                journal,
                options: new SupervisorOptions(TimeSpan.FromMilliseconds(options.TestTtlMilliseconds)));
            await using (coordinator.ConfigureAwait(false))
            {
                await coordinator.InitializeAsync();
                using var shutdown = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    shutdown.Cancel();
                };
                Console.WriteLine($"READY {options.PipeName}");
                try
                {
                    await new SupervisorNamedPipeServer(options.PipeName, coordinator)
                        .RunAsync(shutdown.Token);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                }
                await coordinator.StopAllAsync();
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Supervisor failed to start: {error.GetType().Name}");
            return 1;
        }
    }

    private static async Task<int> RunSimulatedClientAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--simulate-client" || args[1] == "--pipe-name")
            return 64;

        try
        {
            var response = await SupervisorNamedPipeClient.SendAsync(
                args[1],
                new SupervisorIpcRequest(
                    SupervisorPolicy.ProtocolVersion,
                    Guid.NewGuid(),
                    SupervisorCommand.CreateSession,
                    null,
                    0));
            if (!response.Accepted || response.Session is null)
                return 1;
            Console.WriteLine(response.Session.SessionId.ToString("N"));
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Simulated client failed: {error.GetType().Name}");
            return 1;
        }
    }

    private static async Task<int> RunSimulatedStudioAsync(string[] args)
    {
        if (args.Length != 6 || args[0] != "--simulate-studio")
            return 64;

        Process? supervisor = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[1]))!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    Path.GetFullPath(args[1]),
                    "--simulate",
                    "--journal",
                    args[2],
                    "--resource-store",
                    args[3],
                    "--pipe-name",
                    args[4],
                    "--test-ttl-ms",
                    args[5],
                },
            };
            supervisor = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the simulated supervisor.");
            var ready = await supervisor.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (ready != $"READY {args[4]}")
                throw new InvalidOperationException("Simulated supervisor did not reach readiness.");
            var response = await SupervisorNamedPipeClient.SendAsync(
                args[4],
                new SupervisorIpcRequest(
                    SupervisorPolicy.ProtocolVersion,
                    Guid.NewGuid(),
                    SupervisorCommand.CreateSession,
                    null,
                    0));
            if (!response.Accepted || response.Session is null)
                throw new InvalidOperationException("Simulated Studio could not create a session.");
            Console.WriteLine($"SUPERVISOR_PID {supervisor.Id}");
            Console.WriteLine($"SESSION {response.Session.SessionId:N}");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (Exception error)
        {
            if (supervisor is { HasExited: false })
            {
                supervisor.Kill(entireProcessTree: true);
                await supervisor.WaitForExitAsync();
            }
            Console.Error.WriteLine($"Simulated Studio failed: {error.GetType().Name}");
            return 1;
        }
    }

    private static LaunchOptions ParseOptions(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var crashAfterCreate = false;
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (key == "--simulate")
                continue;
            if (key == "--simulate-crash-after-create")
            {
                if (crashAfterCreate)
                    throw new ArgumentException("Duplicate simulated crash flag.");
                crashAfterCreate = true;
                continue;
            }
            if (key is not ("--journal" or "--resource-store" or "--pipe-name" or "--test-ttl-ms") ||
                index + 1 >= args.Length || !values.TryAdd(key, args[++index]))
                throw new ArgumentException("Invalid or duplicate supervisor argument.");
        }

        var journal = Required(values, "--journal");
        var resourceStore = Required(values, "--resource-store");
        var pipeName = Required(values, "--pipe-name");
        var ttlMilliseconds = values.TryGetValue("--test-ttl-ms", out var ttlText)
            ? int.Parse(ttlText, System.Globalization.CultureInfo.InvariantCulture)
            : (int)SupervisorPolicy.MaximumLifetime.TotalMilliseconds;
        if (ttlMilliseconds <= 0 ||
            ttlMilliseconds > SupervisorPolicy.MaximumLifetime.TotalMilliseconds)
            throw new ArgumentOutOfRangeException("--test-ttl-ms");

        var tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        journal = EnsureWithinTemp(tempRoot, journal);
        resourceStore = EnsureWithinTemp(tempRoot, resourceStore);
        return new LaunchOptions(journal, resourceStore, pipeName, ttlMilliseconds, crashAfterCreate);
    }

    private static string EnsureWithinTemp(string tempRoot, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Simulation files must be under the current user's temporary directory.");
        return fullPath;
    }

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument {name}.");

    private sealed record LaunchOptions(
        string JournalPath,
        string ResourceStorePath,
        string PipeName,
        int TestTtlMilliseconds,
        bool CrashAfterCreate);
}
