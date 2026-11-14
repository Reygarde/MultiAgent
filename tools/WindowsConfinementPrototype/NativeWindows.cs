using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace WindowsConfinementPrototype;

internal static class NativeWindows
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityCapabilities { public nint Sid, Capabilities; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort Show, ReservedSize; public nint ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation { public nint Process, Thread; public uint Pid, Tid; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes { public uint Length; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint Minimum, Maximum;
        public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayData { public string Name, Description; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WfpSession
    {
        public Guid Key; public DisplayData Display; public uint Flags, Timeout, Pid;
        public nint Sid; public string? UserName; public int Kernel;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Blob { public uint Size; public nint Data; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SubLayer
    {
        public Guid Key; public DisplayData Display; public uint Flags; public nint Provider;
        public Blob Data; public ushort Weight;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int CreateAppContainerProfile(string name, string display, string description, nint capabilities, uint count, out nint sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int DeleteAppContainerProfile(string name);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetAppContainerFolderPath(string sid, out nint path);
    [DllImport("advapi32.dll")] internal static extern nint FreeSid(nint sid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint CreateJobObjectW(nint attributes, nint name);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetInformationJobObject(nint job, int info, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool QueryInformationJobObject(nint job, int info, nint buffer, uint size, out uint needed);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes,
        nint threadAttributes, bool inherit, uint flags, nint environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateFileW(string path, uint access, uint sharing, ref SecurityAttributes attributes,
        uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(nint token, int info, nint buffer, uint length, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint version, out nint descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorSacl(nint descriptor, out bool present, out nint sacl, out bool defaulted);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SetNamedSecurityInfoW(string path, uint type, uint information, nint owner, nint group, nint dacl, nint sacl);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    internal static extern uint FwpmEngineOpen0(string? server, uint authentication, nint identity, ref WfpSession session, out nint engine);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmEngineClose0(nint engine);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmTransactionBegin0(nint engine, uint flags);
    [DllImport("fwpuclnt.dll")] internal static extern uint FwpmTransactionAbort0(nint engine);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    internal static extern uint FwpmSubLayerAdd0(nint engine, ref SubLayer layer, nint descriptor);

    internal static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal sealed record TokenRecord(uint Pid, bool AppContainer, string? Sid, int? Error = null,
        string? Integrity = null, string[]? Capabilities = null);

    internal static void LowerOwnedDirectoryIntegrity(string path)
    {
        Check(ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;OICI;NW;;;LW)", 1, out var descriptor, out _));
        try
        {
            Check(GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _));
            if (!present) throw new IOException("Missing mandatory label.");
            var result = SetNamedSecurityInfoW(path, 1, 0x10, 0, 0, 0, sacl);
            if (result != 0) throw new Win32Exception((int)result);
        }
        finally { LocalFree(descriptor); }
    }

    internal static TokenRecord Token(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        Check(process != 0);
        try
        {
            Check(OpenProcessToken(process, 8, out var token));
            try
            {
                var buffer = Marshal.AllocHGlobal(1024);
                try
                {
                    Check(GetTokenInformation(token, 29, buffer, 1024, out _));
                    var contained = Marshal.ReadInt32(buffer) != 0;
                    string? sid = null;
                    if (contained)
                    {
                        Check(GetTokenInformation(token, 31, buffer, 1024, out _));
                        sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                    }
                    Check(GetTokenInformation(token, 25, buffer, 1024, out _));
                    var integrity = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                    Check(GetTokenInformation(token, 30, buffer, 1024, out _));
                    var capabilityCount = Marshal.ReadInt32(buffer);
                    if (capabilityCount is < 0 or > 60) throw new IOException("Unexpected token capability inventory.");
                    var capabilities = Enumerable.Range(0, capabilityCount).Select(index =>
                        new SecurityIdentifier(Marshal.ReadIntPtr(buffer, nint.Size + index * (2 * nint.Size))).Value).ToArray();
                    return new(pid, contained, sid, Integrity: integrity, Capabilities: capabilities);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    internal static uint[] JobPids(nint job)
    {
        const int size = 4096;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Check(QueryInformationJobObject(job, 3, buffer, size, out _));
            var count = Marshal.ReadInt32(buffer, 4);
            if (count is < 0 or > 128) throw new IOException("Unexpected Job Object inventory.");
            return Enumerable.Range(0, count).Select(index => checked((uint)Marshal.ReadIntPtr(buffer, 8 + index * nint.Size))).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static object ProbeWfp()
    {
        var session = new WfpSession
        {
            Key = Guid.NewGuid(), Flags = 1,
            Display = new() { Name = "AIPlatform temporary ACL diagnostic", Description = "Dynamic; no network filters" }
        };
        var opened = FwpmEngineOpen0(null, 10, 0, ref session, out var engine);
        if (opened != 0) return new { engineOpen = opened, systemFiltersCreated = 0, elevationRequested = false };
        uint? begin = null, add = null, abort = null;
        uint closed;
        try
        {
            begin = FwpmTransactionBegin0(engine, 0);
            if (begin == 0)
            {
                var layer = new SubLayer { Key = Guid.NewGuid(), Display = session.Display };
                try { add = FwpmSubLayerAdd0(engine, ref layer, 0); }
                finally { abort = FwpmTransactionAbort0(engine); }
            }
        }
        finally { closed = FwpmEngineClose0(engine); }
        return new { engineOpen = opened, transactionBegin = begin, subLayerAdd = add,
            transactionAbort = abort, engineClose = closed, systemFiltersCreated = 0, elevationRequested = false };
    }
}
