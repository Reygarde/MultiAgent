using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static WindowsConfinementPrototype.NativeWindows;

namespace WindowsConfinementPrototype;

internal sealed record ProcessResult(uint Pid, uint ExitCode, bool TimedOut, bool JobEmpty,
    IReadOnlyList<TokenRecord> Tokens, string Output, string Errors);

internal static class RestrictedProcess
{
    internal static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string directory, string name, nint appContainerSid, TimeSpan lifetime, Action<uint>? onStarted = null)
    {
        var job = CreateJobObjectW(0, 0);
        Check(job != 0);
        nint list = 0, capabilityMemory = 0, handleMemory = 0, environment = 0;
        var handles = new List<nint>();
        var info = new ProcessInformation();
        var initialized = false;
        var created = false;
        try
        {
            var limits = new ExtendedLimits
            {
                Basic = new() { Flags = 0x2000 | 8, ActiveProcesses = 32 }
            };
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()));
            var attributes = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
            foreach (var target in new[] { "NUL", Path.Combine(directory, name + ".stdout"), Path.Combine(directory, name + ".stderr") })
            {
                var handle = CreateFileW(target, 0xC0000000, 1, ref attributes, target == "NUL" ? 3u : 1u, 0, 0);
                Check(handle != -1);
                handles.Add(handle);
            }
            var attributeCount = appContainerSid == 0 ? 1 : 2;
            nuint size = 0;
            if (InitializeProcThreadAttributeList(0, attributeCount, 0, ref size) ||
                Marshal.GetLastWin32Error() != 122 || size == 0)
                throw new IOException("Invalid attribute list size query.");
            list = Marshal.AllocHGlobal(checked((int)size));
            Check(InitializeProcThreadAttributeList(list, attributeCount, 0, ref size));
            initialized = true;
            handleMemory = Marshal.AllocHGlobal(3 * nint.Size);
            Marshal.Copy(handles.ToArray(), 0, handleMemory, 3);
            Check(UpdateProcThreadAttribute(list, 0, 0x20002, handleMemory, (nuint)(3 * nint.Size), 0, 0));
            if (appContainerSid != 0)
            {
                capabilityMemory = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
                Marshal.StructureToPtr(new SecurityCapabilities { Sid = appContainerSid }, capabilityMemory, false);
                Check(UpdateProcThreadAttribute(list, 0, 0x20009, capabilityMemory,
                    (nuint)Marshal.SizeOf<SecurityCapabilities>(), 0, 0));
            }
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "SystemRoot", "SystemDrive", "WINDIR", "ProgramFiles", "ProgramFiles(x86)",
                         "ALLUSERSPROFILE", "LOCALAPPDATA", "APPDATA" })
                if (Environment.GetEnvironmentVariable(key) is { } value) values[key] = value;
            values["USERPROFILE"] = directory;
            values["TEMP"] = directory;
            values["TMP"] = directory;
            values["ComSpec"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            values["PATH"] = Path.GetDirectoryName(executable)! + ";" + Environment.GetFolderPath(Environment.SpecialFolder.System);
            environment = Marshal.StringToHGlobalUni(string.Join('\0', values.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0");
            var startup = new StartupInfoEx
            {
                Startup = new() { Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = handles[0], Output = handles[1], Error = handles[2] },
                Attributes = list
            };
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
            Check(CreateProcessW(executable, command, 0, 0, true, 0x80000 | 0x400 | 4 | 0x08000000,
                environment, directory, ref startup, out info));
            created = true;
            try { Check(AssignProcessToJobObject(job, info.Process)); }
            catch (Win32Exception)
            {
                Check(TerminateProcess(info.Process, 1));
                throw;
            }
            if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            onStarted?.Invoke(info.Pid);
            var tokens = new Dictionary<uint, TokenRecord>();
            var timer = Stopwatch.StartNew();
            while (WaitForSingleObject(info.Process, 0) == 258 && timer.Elapsed < lifetime)
            {
                foreach (var pid in JobPids(job))
                {
                    if (tokens.ContainsKey(pid)) continue;
                    try { tokens[pid] = Token(pid); }
                    catch (Win32Exception exception) { tokens[pid] = new(pid, false, null, exception.NativeErrorCode); }
                }
                await Task.Delay(50);
            }
            var timeout = WaitForSingleObject(info.Process, 0) == 258;
            Check(TerminateJobObject(job, timeout ? 124u : 0u));
            for (var attempt = 0; attempt < 100 && JobPids(job).Length != 0; attempt++) await Task.Delay(50);
            var empty = JobPids(job).Length == 0;
            Check(GetExitCodeProcess(info.Process, out var code));
            foreach (var handle in handles) Check(CloseHandle(handle));
            handles.Clear();
            return new(info.Pid, code, timeout, empty, tokens.Values.ToArray(),
                await ReadBoundedAsync(Path.Combine(directory, name + ".stdout")),
                await ReadBoundedAsync(Path.Combine(directory, name + ".stderr")));
        }
        finally
        {
            if (created)
            {
                Check(TerminateJobObject(job, 125));
                Check(CloseHandle(info.Thread));
                Check(CloseHandle(info.Process));
            }
            Check(CloseHandle(job));
            foreach (var handle in handles) Check(CloseHandle(handle));
            if (initialized) DeleteProcThreadAttributeList(list);
            if (list != 0) Marshal.FreeHGlobal(list);
            if (capabilityMemory != 0) Marshal.FreeHGlobal(capabilityMemory);
            if (handleMemory != 0) Marshal.FreeHGlobal(handleMemory);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }

    private static async Task<string> ReadBoundedAsync(string file)
    {
        if (new FileInfo(file).Length > 262144) throw new IOException("Prototype process output exceeded its limit.");
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            if (result.Length + count > 262144) throw new IOException("Prototype output exceeded its limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }
}
