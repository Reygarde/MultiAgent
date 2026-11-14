using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed partial class ProjectExport(IWorkspaceStore workspaces, StudioOptions options) : IProjectExport
{
    public async Task<byte[]> CreateAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var files = new List<WorkspaceContent>();
        var size = 0;
        foreach (var file in workspaces.List(workspaceId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProjectFilePolicy.IsExcluded(file.Path)) continue;
            if (file.Path != "Generated.slnx") SafePaths.ValidateRelative(file.Path);
            var content = await workspaces.ReadAsync(workspaceId, file.Path, cancellationToken);
            if (!ReactTemplate.IsTrustedLockfile(content.Path, content.Content) && ContainsPotentialSecret(content.Content))
                throw new StudioValidationException("Export blocked: a possible credential was found in project sources. Remove it before exporting.");
            size += Encoding.UTF8.GetByteCount(content.Content);
            if (size > options.MaxProjectBytes)
                throw new StudioValidationException("Export exceeds the configured project size limit.");
            files.Add(content);
        }
        if (files.Count == 0)
            throw new StudioValidationException("No eligible project source files are available for export.");
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(file.Path, CompressionLevel.Optimal);
                await using var target = entry.Open();
                await using var writer = new StreamWriter(target, new UTF8Encoding(false));
                await writer.WriteAsync(file.Content.AsMemory(), cancellationToken);
            }
        }
        return buffer.ToArray();
    }

    internal static bool ContainsPotentialSecret(string content) =>
        SecretAssignment().IsMatch(content) || SecretObject().IsMatch(content) || KnownCredential().IsMatch(content);

    [GeneratedRegex("""(?i)\b(?:[a-z0-9_]*(?:token|secret|password|api[_-]?key|connectionstring)[a-z0-9_]*)["']?\s*[:=]\s*["'][^"'\r\n]+["']""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex("""(?i)["']?(?:connectionstrings|credentials)["']?\s*:\s*\{""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretObject();

    [GeneratedRegex(@"\b(?:ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|AKIA[A-Z0-9]{16}|[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{25,}|Bearer\s+[A-Za-z0-9._~+/-]{8,}|(?i:oauth:[a-z0-9]{20,}))\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex KnownCredential();
}
