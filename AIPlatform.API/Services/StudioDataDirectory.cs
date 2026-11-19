using System.Runtime.InteropServices;
using System.Text;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.Win32.SafeHandles;

namespace AIPlatform.API.Services;

public static class StudioDataDirectory
{
    public static string Resolve(bool desktop, string configuredPath, string contentRoot, string localAppData)
    {
        var installedRoot = Path.Combine(localAppData, "AIPlatformStudio");
        if (desktop)
            return SafePaths.ValidateStorageDirectory(Path.Combine(installedRoot, "StudioData"));

        var path = SafePaths.ValidateStorageDirectory(Path.GetFullPath(configuredPath, contentRoot));
        SafePaths.RejectLinks(Path.Combine(installedRoot, "StudioData"));
        var candidate = ExpandExistingPath(path);
        var installed = ExpandExistingPath(installedRoot);
        if (Contains(installed, candidate) || Contains(candidate, installed))
            throw new StudioValidationException("Development Studio data must not overlap the installed AIPlatformStudio data directory.");
        return path;
    }

    private static bool Contains(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        path = Path.TrimEndingDirectorySeparator(path);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExpandExistingPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        // Use the nearest existing ancestor to resolve Windows aliases, including 8.3 and SUBST.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path[Path.GetPathRoot(path)!.Length..].Split(Path.DirectorySeparatorChar)
                .Any(part => part.EndsWith(' ') || part.EndsWith('.') || part.Contains(':')))
            throw new StudioValidationException("Use an unambiguous local directory for Development Studio data.");
        var existing = Path.TrimEndingDirectorySeparator(path);
        while (!Directory.Exists(existing))
        {
            if (File.Exists(existing))
                throw new StudioValidationException("Studio data must be a directory, not a file.");
            existing = Path.GetDirectoryName(existing)
                ?? throw new StudioValidationException("Could not resolve the Studio data directory.");
        }
        const uint backupSemantics = 0x02000000;
        const uint volumeNameGuid = 1;
        using var handle = CreateFile(existing, 0, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, backupSemantics, IntPtr.Zero);
        var finalPath = new StringBuilder(32768);
        var length = handle.IsInvalid ? 0u : GetFinalPathNameByHandle(handle, finalPath, finalPath.Capacity, volumeNameGuid);
        if (length == 0 || length >= finalPath.Capacity)
            throw new StudioValidationException("Could not resolve the Studio data directory.");
        var remainder = Path.GetRelativePath(existing, path);
        return remainder == "." ? finalPath.ToString() : Path.Combine(finalPath.ToString(), remainder);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int length, uint flags);
}
