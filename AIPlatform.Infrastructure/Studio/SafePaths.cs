using System.Text.RegularExpressions;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static partial class SafePaths
{
    public static string ValidateStorageDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        RejectLinks(fullPath);
        var ancestor = fullPath;
        while (!string.IsNullOrEmpty(ancestor))
        {
            if (Directory.Exists(ancestor) &&
                Directory.EnumerateFiles(ancestor, "*.csproj", SearchOption.TopDirectoryOnly).Any())
                throw new StudioValidationException(
                    "Studio data must be outside every .NET project source tree to prevent implicit host compilation.");
            ancestor = Path.GetDirectoryName(ancestor);
        }
        return fullPath;
    }

    public static string ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 180 || path.Contains('\\') ||
            Path.IsPathRooted(path) || path.Contains(':'))
            throw new StudioValidationException("Use a short relative project path with forward slashes.");
        var segments = path.Split('/');
        if (segments.Length > 8 || segments.Any(segment =>
            !SegmentPattern().IsMatch(segment) || segment.EndsWith('.') ||
            segment is "." or ".." || DevicePattern().IsMatch(segment)))
            throw new StudioValidationException("Unsafe or reserved path segment.");
        if (segments.Any(segment => new[] { "bin", "obj", "node_modules" }
                .Contains(segment, StringComparer.OrdinalIgnoreCase)))
            throw new StudioValidationException("Generated output directories are not allowed.");
        if (!new[] { ".cs", ".csproj", ".json", ".md" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new StudioValidationException("Only C#, project, JSON and Markdown source files are allowed.");
        if (!(path.StartsWith("src/App/", StringComparison.Ordinal) ||
              path.StartsWith("tests/App.Tests/", StringComparison.Ordinal) ||
              path == "README.md"))
            throw new StudioValidationException("Files must be inside src/App or tests/App.Tests.");
        if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
            path is not ("src/App/App.csproj" or "tests/App.Tests/App.Tests.csproj"))
            throw new StudioValidationException("Only the two fixed project paths are allowed.");
        return path;
    }

    public static string Resolve(string root, string relative)
    {
        ValidateRelative(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new StudioValidationException("The path escapes the workspace.");
        RejectLinks(path);
        return path;
    }

    public static void RejectLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new StudioValidationException("Symbolic links and reparse points are not allowed.");
            current = Path.GetDirectoryName(current);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevicePattern();
}
