using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

/// <summary>Exact original text selected during preflight, never a fuzzy search pattern.</summary>
public sealed record ReactPatchTarget(string Path, string Original);
/// <summary>One traceable replacement, with offsets relative to the group's original file.</summary>
public sealed record ReactAppliedPatch(string Path, int Start, string Original, string Replacement,
    string OriginalSha256, string CandidateSha256);

internal static class ReactCorrectionPatches
{
    private static readonly JsonSerializerOptions StrictJson = new(ReactRevisionCorrection.Json)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true
    };
    private sealed record Patch(string Path, string Original, string Replacement);
    private sealed record Reply(Patch[] Patches);
    internal sealed record Applied(GeneratedFile[] Files, ReactAppliedPatch[] Edits);

    public static ReactPatchTarget[]? Targets(LlmRequest call)
    {
        using var input = JsonDocument.Parse(call.Messages[1].Content);
        return input.RootElement.TryGetProperty("patchTargets", out var targets)
            ? targets.Deserialize<ReactPatchTarget[]>(StrictJson)
                ?? throw new StudioValidationException("Missing prepared patch targets.")
            : null;
    }

    public static void ValidateTargets(ReactPatchTarget[] targets, string[] paths,
        IReadOnlyDictionary<string, GeneratedFile> sources)
    {
        if (targets.Length is < 1 or > 8 || targets.Any(target => target is null ||
                target.Path is null || !paths.Contains(target.Path, StringComparer.Ordinal) ||
                !MissionReactTemplate.IsEditable(target.Path) || !sources.ContainsKey(target.Path) ||
                string.IsNullOrWhiteSpace(target.Original)) ||
            targets.Distinct().Count() != targets.Length ||
            !targets.Select(target => target.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .SequenceEqual(paths.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new StudioValidationException("Patch targets must be distinct, cover every exact allowed path and contain nonempty original text.");
        foreach (var group in targets.GroupBy(target => target.Path, StringComparer.Ordinal))
        {
            var end = -1;
            foreach (var span in group.Select(target => (Start: ExactSourceReplacement.FindUnique(sources[group.Key].Content, target.Original),
                Length: target.Original.Length)).OrderBy(span => span.Start))
            {
                if (span.Start < end) throw new StudioValidationException("Overlapping patches are forbidden.");
                end = span.Start + span.Length;
            }
        }
    }

    public static int BaselineBytes(LlmRequest call, string[] paths, IReadOnlyDictionary<string, GeneratedFile> sources)
    {
        var targets = Targets(call);
        if (targets is null)
            return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { files = paths.Select(path => sources[path]) }, ReactRevisionCorrection.Json));
        ValidateTargets(targets, paths, sources);
        // Size probe only: an unchanged replacement is never accepted as a correction.
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new {
            patches = targets.Select(target => new { target.Path, target.Original, Replacement = target.Original })
        }, ReactRevisionCorrection.Json));
    }

    public static Applied Apply(string answer, LlmRequest call, string[] paths,
        IReadOnlyDictionary<string, GeneratedFile> sources)
    {
        var targets = Targets(call);
        if (targets is null) return new(ReactRevisionCorrection.ReadReplacements(answer, paths), []);
        ValidateTargets(targets, paths, sources);
        Reply reply;
        try { reply = JsonSerializer.Deserialize<Reply>(answer, StrictJson) ?? throw new JsonException(); }
        catch (JsonException)
        {
            throw new StudioValidationException("The Developer returned incomplete or invalid patch JSON; no patch was applied.");
        }
        if (reply.Patches is not { } patches || patches.Length != targets.Length ||
            patches.Any(patch => patch is null || patch.Replacement is null || patch.Original == patch.Replacement ||
                !targets.Contains(new(patch.Path, patch.Original))) ||
            patches.Select(patch => new ReactPatchTarget(patch.Path, patch.Original)).Distinct().Count() != targets.Length)
            throw new StudioValidationException("The Developer must return every exact prepared patch once, with a changed replacement and no extra patch.");
        var files = new List<GeneratedFile>();
        var edits = new List<ReactAppliedPatch>();
        foreach (var group in patches.GroupBy(patch => patch.Path, StringComparer.Ordinal))
        {
            var original = sources[group.Key].Content;
            var content = original;
            var spans = group.Select(patch => (Patch: patch, Start: ExactSourceReplacement.FindUnique(original, patch.Original)))
                .OrderByDescending(span => span.Start).ToArray();
            foreach (var span in spans)
                content = content[..span.Start] + span.Patch.Replacement + content[(span.Start + span.Patch.Original.Length)..];
            var before = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)));
            var after = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            foreach (var span in spans)
                edits.Add(new(group.Key, span.Start, span.Patch.Original, span.Patch.Replacement, before, after));
            files.Add(new(group.Key, content));
        }
        return new(files.ToArray(), edits.ToArray());
    }

    public static void ValidateDiagnostics(IReadOnlyList<ReactSourceIssue> before, IReadOnlyList<ReactSourceIssue> after,
        string[] paths)
    {
        var counts = before.GroupBy(issue => (issue.File, issue.Line, issue.Column, issue.Code, issue.Message))
            .ToDictionary(group => group.Key, group => group.Count());
        var regressions = after.GroupBy(issue => (issue.File, issue.Line, issue.Column, issue.Code, issue.Message))
            .Any(group => group.Count() > counts.GetValueOrDefault(group.Key));
        if (regressions || after.Any(issue => paths.Contains(issue.File, StringComparer.Ordinal)))
            throw new StudioValidationException("The correction left target diagnostics or introduced a static regression; candidate rejected.\n" +
                ReactSourceCheck.Describe(after.Take(20)));
    }
}
