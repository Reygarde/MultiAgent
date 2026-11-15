using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class WindowsCredentialTokenVault : ITwitchTokenVault
{
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;
    private const int MaximumBlobSize = 5 * 512;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public TwitchStoredCredentials? Read(Guid botId)
    {
        EnsureWindows();
        if (!CredRead(Target(botId), Generic, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == NotFound)
                return null;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the Twitch credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlobSize > MaximumBlobSize)
                throw new InvalidDataException("The saved Twitch credential has an invalid size.");
            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return JsonSerializer.Deserialize<TwitchStoredCredentials>(bytes)
                    ?? throw new InvalidDataException("The saved Twitch credential is invalid.");
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Save(Guid botId, TwitchStoredCredentials credentials)
    {
        EnsureWindows();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
        if (bytes.Length > MaximumBlobSize)
            throw new InvalidDataException("The Twitch credential exceeds the Windows Credential Manager limit.");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic,
                TargetName = Target(botId),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = credentials.Login
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the Twitch credential.");
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Delete(Guid botId)
    {
        EnsureWindows();
        if (!CredDelete(Target(botId), Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not delete the Twitch credential.");
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Twitch credentials require Windows Credential Manager.");
    }

    private static string Target(Guid botId) => "AIPlatformStudio.Twitch." + botId.ToString("N");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public NativeFileTime LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
