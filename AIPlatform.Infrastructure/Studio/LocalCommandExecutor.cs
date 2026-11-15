using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Hosting;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public enum LocalCommandKind
{
    DotNetRestore,
    DotNetBuild,
    DotNetTest,
    NpmInstall,
    NpmBuild,
    NpmTest,
    DotNetRun,
    NpmRunDev
}

public sealed record LocalCommandResult(
    string Command, int? ExitCode, bool TimedOut, string Output, DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public sealed record LocalApplicationSnapshot(
    Guid WorkspaceId, string Status, string? Url, string? Command,
    IReadOnlyList<string> Output, string? Error, int? ProcessId, DateTimeOffset UpdatedAt);

public interface ILocalCommandExecutor
{
    bool Available { get; }
    Guid Authorize(Guid workspaceId);
    void Revoke(Guid authorizationId);
    Task<LocalCommandResult> RunAsync(
        Guid authorizationId, Guid workspaceId, CustomAgentProjectType projectType,
        LocalCommandKind command, CancellationToken cancellationToken);
    LocalApplicationSnapshot GetApplication(Guid workspaceId, CustomAgentProjectType projectType);
    Task<LocalApplicationSnapshot> StartApplicationAsync(
        Guid workspaceId, CustomAgentProjectType projectType, bool executionAuthorized,
        CancellationToken cancellationToken);
    Task<LocalApplicationSnapshot?> StopApplicationAsync(Guid workspaceId, CancellationToken cancellationToken);
}

public sealed class LocalCommandExecutor(
    IWorkspaceStore workspaces, StudioOptions options, bool desktopEnabled) : ILocalCommandExecutor, IHostedService
{
    private const int MaxOutputCharacters = 12000;
    private const int MaxServerOutputLines = 100;
    private static readonly TimeSpan ServerStartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly Regex AnsiEscapeSequences = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly HttpClient LoopbackClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(2)
    }) { Timeout = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<Guid, Guid> authorizations = [];
    private readonly object sync = new();
    private readonly object applicationSync = new();
    private readonly ConcurrentDictionary<Guid, LocalApplicationSession> applications = new();
    private int stopping;

    public bool Available => desktopEnabled && OperatingSystem.IsWindows();

    public Guid Authorize(Guid workspaceId)
    {
        if (!Available)
            throw new StudioValidationException("Local command execution is available only in AIPlatform Studio Desktop on Windows.");
        if (!Directory.Exists(workspaces.GetDirectory(workspaceId)))
            throw new DirectoryNotFoundException("The selected Studio workspace does not exist.");
        var id = Guid.NewGuid();
        lock (sync)
            authorizations[id] = workspaceId;
        return id;
    }

    public void Revoke(Guid authorizationId)
    {
        lock (sync)
            authorizations.Remove(authorizationId);
    }

    public async Task<LocalCommandResult> RunAsync(
        Guid authorizationId, Guid workspaceId, CustomAgentProjectType projectType,
        LocalCommandKind command, CancellationToken cancellationToken)
    {
        if (!Available)
            throw new StudioValidationException("Local command execution is available only in AIPlatform Studio Desktop on Windows.");
        lock (sync)
        {
            if (!authorizations.TryGetValue(authorizationId, out var authorizedWorkspace) ||
                authorizedWorkspace != workspaceId)
                throw new StudioValidationException("Local execution was not authorized for this Studio workspace.");
        }

        var (executable, arguments, display) = GetCommand(projectType, command);
        var directory = workspaces.GetDirectory(workspaceId);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("The selected Studio workspace does not exist.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["CI"] = "true";
        return await RunProcessAsync(startInfo, display, cancellationToken);
    }

    public LocalApplicationSnapshot GetApplication(Guid workspaceId, CustomAgentProjectType projectType)
    {
        if (applications.TryGetValue(workspaceId, out var existing))
            return existing.Snapshot;
        var (_, _, command) = GetCommand(
            projectType, projectType == CustomAgentProjectType.React ? LocalCommandKind.NpmRunDev : LocalCommandKind.DotNetRun);
        return new(workspaceId, "Stopped", null, command, [], null, null, DateTimeOffset.UtcNow);
    }

    public async Task<LocalApplicationSnapshot> StartApplicationAsync(
        Guid workspaceId, CustomAgentProjectType projectType, bool executionAuthorized,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Available)
            throw new StudioValidationException("Local applications can be started only in AIPlatform Studio Desktop on Windows.");
        if (!executionAuthorized)
            throw new StudioValidationException("Explicit local code-execution authorization is required to start this application.");
        if (Volatile.Read(ref stopping) != 0)
            throw new StudioValidationException("Studio is shutting down and cannot start another local application.");

        var directory = workspaces.GetDirectory(workspaceId);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("The selected Studio workspace does not exist.");
        var files = workspaces.List(workspaceId);
        var requiredFile = projectType switch
        {
            CustomAgentProjectType.React => "package.json",
            CustomAgentProjectType.DotNet => "src/App/App.csproj",
            _ => throw new StudioValidationException("The selected Studio project type is not supported.")
        };
        if (!files.Any(file => string.Equals(file.Path, requiredFile, StringComparison.OrdinalIgnoreCase)))
            throw new StudioValidationException($"The Studio project is missing its trusted launch file: {requiredFile}");

        var (display, startInfo) = CreateApplicationStartInfo(projectType, directory);
        var authorizationId = Authorize(workspaceId);
        LocalApplicationSession session;
        lock (applicationSync)
        {
            if (Volatile.Read(ref stopping) != 0)
            {
                Revoke(authorizationId);
                throw new StudioValidationException("Studio is shutting down and cannot start another local application.");
            }
            while (applications.TryGetValue(workspaceId, out var previous))
            {
                if (!previous.IsCompleted)
                {
                    Revoke(authorizationId);
                    return previous.Snapshot;
                }
                applications.TryRemove(new KeyValuePair<Guid, LocalApplicationSession>(workspaceId, previous));
            }
            session = new LocalApplicationSession(
                workspaceId, authorizationId, display, startInfo, Revoke);
            applications[workspaceId] = session;
        }

        try
        {
            session.Start();
            return session.Snapshot;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            session.MarkStartFailure(error.Message);
            await session.FailStartAsync();
            throw new StudioValidationException($"The local application could not be started: {error.Message}");
        }
    }

    public async Task<LocalApplicationSnapshot?> StopApplicationAsync(
        Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!applications.TryGetValue(workspaceId, out var session))
            return null;
        await session.StopAsync(cancellationToken);
        return session.Snapshot;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        LocalApplicationSession[] sessions;
        lock (applicationSync)
        {
            Interlocked.Exchange(ref stopping, 1);
            sessions = applications.Values.ToArray();
        }
        await Task.WhenAll(sessions.Select(session => session.StopAsync(cancellationToken)));
    }

    internal async Task<LocalCommandResult> RunProcessAsync(
        ProcessStartInfo startInfo, string display, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var started = DateTimeOffset.UtcNow;
        using var job = WindowsJob.Create();
        if (!process.Start())
            throw new InvalidOperationException("The local command process could not be started.");
        try
        {
            job.Assign(process);
        }
        catch
        {
            job.Terminate();
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }

        var output = new StringBuilder();
        var outputLock = new object();
        var stdout = PumpAsync(process.StandardOutput, output, outputLock);
        var stderr = PumpAsync(process.StandardError, output, outputLock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.MissionTimeoutSeconds));
        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            job.Terminate();
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (OperationCanceledException)
        {
            job.Terminate();
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            cancelled = true;
        }
        await Task.WhenAll(stdout, stderr);
        if (cancelled)
            cancellationToken.ThrowIfCancellationRequested();
        return new(display, timedOut ? null : process.ExitCode, timedOut, output.ToString(),
            started, DateTimeOffset.UtcNow);
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder output, object sync)
    {
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0)
                return;
            lock (sync)
            {
                var remaining = MaxOutputCharacters - output.Length;
                if (remaining > 0)
                    output.Append(buffer, 0, Math.Min(count, remaining));
            }
        }
    }

    private static (string Executable, string Arguments, string Display) GetCommand(
        CustomAgentProjectType projectType, LocalCommandKind command)
    {
        if (projectType == CustomAgentProjectType.React)
            return command switch
            {
                LocalCommandKind.NpmInstall => ("cmd.exe", "/d /s /c \"npm install --no-audit --no-fund\"",
                    "npm install --no-audit --no-fund"),
                LocalCommandKind.NpmBuild => ("cmd.exe", "/d /s /c \"npm run build\"",
                    "npm run build"),
                LocalCommandKind.NpmTest => ("cmd.exe", "/d /s /c \"npm test\"",
                    "npm test"),
                LocalCommandKind.NpmRunDev => ("cmd.exe", "/d /s /c \"npm run dev\"",
                    "npm run dev"),
                _ => throw new StudioValidationException("That command is not supported for a React project.")
            };
        if (projectType == CustomAgentProjectType.DotNet)
            return command switch
            {
                LocalCommandKind.DotNetRestore => ("dotnet.exe", "restore --disable-parallel", "dotnet restore --disable-parallel"),
                LocalCommandKind.DotNetBuild => ("dotnet.exe", "build --no-restore", "dotnet build --no-restore"),
                LocalCommandKind.DotNetTest => ("dotnet.exe", "test --no-restore", "dotnet test --no-restore"),
                LocalCommandKind.DotNetRun => ("dotnet.exe",
                    "run --project src/App/App.csproj --no-launch-profile -- --urls http://127.0.0.1:0",
                    "dotnet run --project src/App/App.csproj --no-launch-profile -- --urls http://127.0.0.1:0"),
                _ => throw new StudioValidationException("That command is not supported for a .NET project.")
            };
        throw new StudioValidationException("The selected Studio project type is not supported for local execution.");
    }

    private static (string Display, ProcessStartInfo StartInfo) CreateApplicationStartInfo(
        CustomAgentProjectType projectType, string directory)
    {
        var (executable, arguments, display) = GetCommand(
            projectType, projectType == CustomAgentProjectType.React ? LocalCommandKind.NpmRunDev : LocalCommandKind.DotNetRun);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        return (display, startInfo);
    }

    private static bool TryGetAnnouncedLocalUrl(string line, out Uri? uri)
    {
        var plainLine = AnsiEscapeSequences.Replace(line, string.Empty);
        var httpIndex = plainLine.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        var httpsIndex = plainLine.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        var start = httpIndex < 0 ? httpsIndex : httpsIndex < 0 ? httpIndex : Math.Min(httpIndex, httpsIndex);
        if (start >= 0)
        {
            var candidate = plainLine[start..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.TrimEnd('.', ',', ';', ')', ']');
            if (candidate is not null && Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
                parsed.IsLoopback &&
                (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
                parsed.Port is > 0 and <= 65535)
            {
                uri = parsed;
                return true;
            }
        }
        uri = null;
        return false;
    }

    private static async Task<bool> IsHttpRespondingAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!url.IsLoopback || url.Scheme is not ("http" or "https"))
            return false;
        try
        {
            using var response = await LoopbackClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)response.StatusCode < 500;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed class LocalApplicationSession
    {
        private readonly Guid workspaceId;
        private readonly Guid authorizationId;
        private readonly string command;
        private readonly Action<Guid> revoke;
        private readonly ProcessStartInfo startInfo;
        private readonly Process process;
        private readonly object processSync = new();
        private readonly object stateSync = new();
        private readonly Queue<string> output = new();
        private readonly TaskCompletionSource<Uri> announcedUrl =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource stopping = new();
        private WindowsJob? job;
        private Task? stdoutPump;
        private Task? stderrPump;
        private Task? monitor;
        private string status = "Starting";
        private string? url;
        private string? error;
        private int? processId;
        private DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
        private int outputCharacters;
        private int stopRequested;
        private int cleanupStarted;
        private bool processStarted;

        public LocalApplicationSession(
            Guid workspaceId, Guid authorizationId, string command, ProcessStartInfo startInfo,
            Action<Guid> revoke)
        {
            this.workspaceId = workspaceId;
            this.authorizationId = authorizationId;
            this.command = command;
            this.startInfo = startInfo;
            this.revoke = revoke;
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        }

        public LocalApplicationSnapshot Snapshot
        {
            get
            {
                lock (stateSync)
                    return new(workspaceId, status, url, command, output.ToArray(), error, processId, updatedAt);
            }
        }

        public bool IsCompleted => completed.Task.IsCompleted;

        public void Start()
        {
            lock (processSync)
            {
                if (Volatile.Read(ref stopRequested) != 0 || IsCompleted)
                    return;
                job = WindowsJob.Create();
                if (!process.Start())
                    throw new InvalidOperationException("The local application process could not be started.");
                processStarted = true;
                lock (stateSync)
                    processId = process.Id;
                try
                {
                    job.Assign(process);
                }
                catch
                {
                    job.Terminate();
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    throw;
                }
                stdoutPump = PumpOutputAsync(process.StandardOutput);
                stderrPump = PumpOutputAsync(process.StandardError);
                monitor = MonitorAsync();
            }
        }

        public void MarkStartFailure(string message) => SetStatus("Error", null, message);

        public async Task FailStartAsync()
        {
            TerminateProcess();
            await WaitForExitAndDrainAsync();
            Cleanup();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            RequestStop();
            Task? monitorTask;
            lock (processSync)
                monitorTask = monitor;
            if (monitorTask is null)
            {
                TerminateProcess();
                await WaitForExitAndDrainAsync();
                SetStatus("Stopped", null, null);
                Cleanup();
            }
            await completed.Task.WaitAsync(cancellationToken);
        }

        private async Task PumpOutputAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                line = AnsiEscapeSequences.Replace(line, string.Empty);
                AppendOutput(line);
                if (TryGetAnnouncedLocalUrl(line, out var announced))
                    this.announcedUrl.TrySetResult(announced!);
            }
        }

        private async Task MonitorAsync()
        {
            var exitTask = process.WaitForExitAsync();
            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                var startupDelay = Task.Delay(ServerStartupTimeout, stopping.Token);
                var first = await Task.WhenAny(announcedUrl.Task, exitTask, startupDelay);
                if (Volatile.Read(ref stopRequested) != 0)
                    return;
                if (first == exitTask)
                {
                    await exitTask;
                    SetStatus("Error", null, $"The local application exited before announcing a localhost URL (exit code {process.ExitCode}).");
                    return;
                }
                if (first != announcedUrl.Task)
                {
                    SetStatus("Error", null, "The local application did not announce a localhost URL within 60 seconds.");
                    TerminateProcess();
                    return;
                }

                var announced = await announcedUrl.Task;
                SetStatus("Starting", announced.ToString(), null);
                while (DateTimeOffset.UtcNow - startedAt < ServerStartupTimeout &&
                       Volatile.Read(ref stopRequested) == 0)
                {
                    if (process.HasExited)
                    {
                        await exitTask;
                        SetStatus("Error", announced.ToString(),
                            $"The local application exited before its HTTP endpoint responded (exit code {process.ExitCode}).");
                        return;
                    }
                    if (await IsHttpRespondingAsync(announced, stopping.Token))
                    {
                        SetStatus("Running", announced.ToString(), null);
                        await exitTask;
                        if (Volatile.Read(ref stopRequested) == 0)
                            SetStatus("Error", announced.ToString(),
                                $"The local application stopped unexpectedly (exit code {process.ExitCode}).");
                        return;
                    }
                    await Task.Delay(250, stopping.Token);
                }
                if (Volatile.Read(ref stopRequested) == 0)
                {
                    SetStatus("Error", announced.ToString(),
                        "The announced localhost URL did not respond over HTTP within 60 seconds.");
                    TerminateProcess();
                }
            }
            catch (OperationCanceledException) when (Volatile.Read(ref stopRequested) != 0)
            {
            }
            catch (HttpRequestException exception)
            {
                SetStatus("Error", null, $"Could not verify the local application over HTTP: {exception.Message}");
                TerminateProcess();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                SetStatus("Error", Snapshot.Url, $"The local application stopped unexpectedly: {exception.Message}");
                TerminateProcess();
            }
            finally
            {
                if (Volatile.Read(ref stopRequested) != 0)
                    SetStatus("Stopped", Snapshot.Url, null);
                try
                {
                    await WaitForExitAndDrainAsync();
                }
                finally
                {
                    lock (processSync)
                        Cleanup();
                }
            }
        }

        private void RequestStop()
        {
            lock (processSync)
            {
                if (IsCompleted || Interlocked.Exchange(ref stopRequested, 1) != 0)
                    return;
                SetStatus("Stopping", Snapshot.Url, null);
                stopping.Cancel();
                TerminateProcessLocked();
            }
        }

        private void TerminateProcess()
        {
            lock (processSync)
                TerminateProcessLocked();
        }

        private void TerminateProcessLocked()
        {
            if (processStarted && !IsProcessExited())
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (IsProcessExited())
                {
                }
            }
            job?.Terminate();
        }

        private bool IsProcessExited()
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private async Task WaitForExitAndDrainAsync()
        {
            if (processStarted && !IsProcessExited())
                await process.WaitForExitAsync();
            var pumps = new[] { stdoutPump, stderrPump }.Where(task => task is not null).Cast<Task>().ToArray();
            if (pumps.Length > 0)
                await Task.WhenAll(pumps);
        }

        private void AppendOutput(string line)
        {
            if (line.Length > MaxOutputCharacters)
                line = line[^MaxOutputCharacters..];
            lock (stateSync)
            {
                output.Enqueue(line);
                outputCharacters += line.Length;
                while (output.Count > MaxServerOutputLines || outputCharacters > MaxOutputCharacters)
                    outputCharacters -= output.Dequeue().Length;
                updatedAt = DateTimeOffset.UtcNow;
            }
        }

        private void SetStatus(string nextStatus, string? nextUrl, string? nextError)
        {
            lock (stateSync)
            {
                status = nextStatus;
                if (nextUrl is not null)
                    url = nextUrl;
                error = nextError;
                updatedAt = DateTimeOffset.UtcNow;
            }
        }

        private void Cleanup()
        {
            if (Interlocked.Exchange(ref cleanupStarted, 1) != 0)
                return;
            job?.Dispose();
            process.Dispose();
            stopping.Dispose();
            revoke(authorizationId);
            completed.TrySetResult();
        }
    }

    private sealed class WindowsJob : IDisposable
    {
        private readonly SafeFileHandle handle;

        private WindowsJob(SafeFileHandle handle) => this.handle = handle;

        public static WindowsJob Create()
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimitInformation
            {
                BasicLimitInformation = new BasicLimitInformation { LimitFlags = 0x00002000 }
            };
            var size = Marshal.SizeOf<ExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, pointer, false);
                if (!SetInformationJobObject(handle, 9, pointer, (uint)size))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            catch
            {
                handle.Dispose();
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
            return new(handle);
        }

        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.SafeHandle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Terminate()
        {
            if (!handle.IsClosed && !handle.IsInvalid)
                TerminateJobObject(handle, 1);
        }

        public void Dispose()
        {
            Terminate();
            handle.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle job, int informationClass, IntPtr information, uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    }
}
