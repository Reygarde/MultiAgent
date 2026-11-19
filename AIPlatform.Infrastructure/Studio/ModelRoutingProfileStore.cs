using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

internal sealed class ModelRoutingProfileStore(StudioOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string path = Path.Combine(SafePaths.ValidateStorageDirectory(options.DataDirectory), "model-routing-profiles.json");

    internal static string? NormalizeDigest(string? digest) =>
        digest is not null && Regex.IsMatch(digest, @"\A(?:sha256:)?[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant)
            ? digest.Replace("sha256:", "", StringComparison.Ordinal).ToLowerInvariant() : null;

    public IReadOnlyDictionary<string, ModelRoutingProfile> Read()
    {
        SafePaths.RejectLinks(path);
        if (!File.Exists(path)) return new Dictionary<string, ModelRoutingProfile>(StringComparer.Ordinal);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 1048576)
            throw new InvalidDataException("Model routing profiles exceed their storage limit.");
        var profiles = JsonSerializer.Deserialize<ModelRoutingProfile[]>(stream, Json)
            ?? throw new InvalidDataException("Invalid model routing profiles.");
        if (profiles.Length > 1000 || profiles.Any(profile => profile is null ||
                profile.Name != ModelLibraryService.ValidateName(profile.Name)) ||
            profiles.Select(profile => profile.Name).Distinct(StringComparer.Ordinal).Count() != profiles.Length)
            throw new InvalidDataException("Invalid model routing profile identifiers.");
        foreach (var profile in profiles)
        {
            ValidateSpecializations(profile.Specializations);
            if (profile.Qualification is { } qualification &&
                (string.IsNullOrEmpty(qualification.Digest) || NormalizeDigest(qualification.Digest) != qualification.Digest ||
                 qualification.ContextTokens is < 512 or > ModelContextLimits.Maximum ||
                 qualification.SizeBytes <= 0 || qualification.SizeVramBytes < 0 ||
                 qualification.SizeVramBytes > qualification.SizeBytes || qualification.ObservedAt == default))
                throw new InvalidDataException("Invalid observed model memory qualification.");
        }
        return profiles.ToDictionary(profile => profile.Name, StringComparer.Ordinal);
    }

    public static void ValidateSpecializations(string[]? values)
    {
        if (values is not null && (values.Length > ModelSpecializations.All.Count ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length ||
            values.Any(value => !ModelSpecializations.All.Contains(value, StringComparer.Ordinal))))
            throw new StudioValidationException("Use distinct supported model specializations.");
    }

    public ModelRoutingProfile Update(string name, Func<ModelRoutingProfile, ModelRoutingProfile> update)
    {
        name = ModelLibraryService.ValidateName(name);
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        using var mutex = new Mutex(false, "AIPlatform-ModelProfiles-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        try
        {
            var profiles = Read().ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            var next = update(profiles.GetValueOrDefault(name) ?? new(name));
            ValidateSpecializations(next.Specializations);
            profiles[name] = next;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(profiles.Values.OrderBy(profile => profile.Name, StringComparer.Ordinal), Json);
            if (profiles.Count > 1000 || bytes.Length > 1048576)
                throw new StudioValidationException("Model routing profiles exceed their storage limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SafePaths.RejectLinks(path);
            SafePaths.RejectLinks(path + ".tmp");
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(path + ".tmp", path, true);
            return next;
        }
        finally { mutex.ReleaseMutex(); }
    }
}
