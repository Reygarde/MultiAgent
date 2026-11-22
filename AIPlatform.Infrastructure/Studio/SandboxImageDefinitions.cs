using System.Text.Json;

namespace AIPlatform.Infrastructure.Studio;

public sealed record SandboxImageDefinition(
    string ImageReference,
    string Version,
    string Dockerfile,
    string BuildContext,
    string SourceFingerprint,
    IReadOnlyList<string> Templates,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> SourceFiles,
    bool PullPinnedBase);

public sealed record SandboxImageRecipeManifest(int FormatVersion, string PackageVersion, string Platform,
    string ArchiveFile, IReadOnlyList<SandboxImageDefinition> Images);

public static class SandboxImageDefinitions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        MaxDepth = 16
    };

    public static SandboxImageRecipeManifest Manifest { get; } = LoadManifest();
    public static IReadOnlyList<SandboxImageDefinition> All => Manifest.Images;

    private static SandboxImageRecipeManifest LoadManifest()
    {
        using var stream = typeof(SandboxImageDefinitions).Assembly.GetManifestResourceStream(
            "AIPlatform.Infrastructure.Studio.sandbox-provisioning-recipes.json")
            ?? throw new InvalidOperationException("The trusted sandbox recipe manifest is missing.");
        var manifest = JsonSerializer.Deserialize<SandboxImageRecipeManifest>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The trusted sandbox recipe manifest is invalid.");
        if (manifest.FormatVersion != 1 || manifest.PackageVersion != "1.0.0" ||
            manifest.Platform != "windows-x64" || manifest.Images is null || manifest.Images.Count != 4 ||
            manifest.ArchiveFile != "aiplatform-sandbox-images-1.0.0.tar" ||
            manifest.Images.Any(image => image is null) ||
            manifest.Images.Select(image => image.ImageReference).Distinct(StringComparer.Ordinal).Count() != 4 ||
            manifest.Images.Any(image => image.SourceFingerprint is null ||
                image.SourceFingerprint.Length != 64 || !image.SourceFingerprint.All(Uri.IsHexDigit) ||
                image.SourceFiles is null || image.SourceFiles.Count == 0 ||
                image.SourceFiles.Distinct(StringComparer.Ordinal).Count() != image.SourceFiles.Count))
            throw new InvalidOperationException("The trusted sandbox recipe manifest has an unsupported shape.");
        return manifest;
    }
}
