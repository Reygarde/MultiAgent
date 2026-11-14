using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIPlatform.Core.Studio;

public sealed record PreviewCommand(string Service, ImmutableArray<string> Arguments);
public sealed record PreviewExposure(string Service, string Address, string Protocol, string Path);
public sealed record PreviewTemporaryStorage(string Name, string ContainerPath, string Policy);
public sealed record PreviewTransport(string Protocol, int MaximumRequestBytes, int MaximumResponseBytes,
    int RequestTimeoutSeconds, bool WebSockets, string Authentication, string BrowserPolicy);

public sealed record PreviewIsolation(
    string NetworkPolicy, bool ReadOnlyRoot, bool NonRoot, bool DropCapabilities,
    bool NoNewPrivileges, int CpuCount, int MemoryMegabytes, int PidLimit);

public sealed record PreviewPlan(
    int SchemaVersion,
    Guid MissionId,
    int WorkflowVersion,
    string ValidationFingerprint,
    ImmutableArray<ExecutionPlanFile> SourceFiles,
    string Image,
    string ImageDigest,
    ImmutableArray<PreviewCommand> Commands,
    PreviewIsolation Isolation,
    ImmutableArray<PreviewExposure> Exposures,
    ImmutableArray<PreviewTemporaryStorage> TemporaryStorage,
    int MaximumSessionSeconds,
    string CleanupPolicy,
    int StartupTimeoutSeconds = 30,
    PreviewTransport? Transport = null)
{
    public string SourceFingerprint => Hash(JsonSerializer.SerializeToUtf8Bytes(SourceFiles));

    public string ComputeFingerprint()
    {
        Validate();
        return Hash(JsonSerializer.SerializeToUtf8Bytes(this));
    }

    private void Validate()
    {
        if (SchemaVersion != 1 || MissionId == Guid.Empty || WorkflowVersion < 1 ||
            !IsSha256(ValidationFingerprint) || SourceFiles.IsDefaultOrEmpty ||
            SourceFiles.Any(file => file is null || string.IsNullOrWhiteSpace(file.Path) || !IsSha256(file.Sha256)) ||
            !SourceFiles.Select(file => file.Path).SequenceEqual(
                SourceFiles.Select(file => file.Path).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            SourceFiles.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != SourceFiles.Length ||
            string.IsNullOrWhiteSpace(Image) ||
            !Regex.IsMatch(ImageDigest ?? "", @"^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant) ||
            Commands.IsDefaultOrEmpty || Commands.Any(command => command is null ||
                string.IsNullOrWhiteSpace(command.Service) || command.Arguments.IsDefaultOrEmpty ||
                command.Arguments.Any(string.IsNullOrWhiteSpace)) ||
            Isolation is null || string.IsNullOrWhiteSpace(Isolation.NetworkPolicy) ||
            Isolation.CpuCount < 1 || Isolation.MemoryMegabytes < 1 || Isolation.PidLimit < 1 ||
            Exposures.IsDefault || Exposures.Any(exposure => exposure is null ||
                string.IsNullOrWhiteSpace(exposure.Service) || string.IsNullOrWhiteSpace(exposure.Address) ||
                string.IsNullOrWhiteSpace(exposure.Protocol) || string.IsNullOrWhiteSpace(exposure.Path)) ||
            TemporaryStorage.IsDefault || TemporaryStorage.Any(storage => storage is null ||
                string.IsNullOrWhiteSpace(storage.Name) || string.IsNullOrWhiteSpace(storage.ContainerPath) ||
                string.IsNullOrWhiteSpace(storage.Policy)) ||
            MaximumSessionSeconds < 1 || StartupTimeoutSeconds is < 1 or > 600 ||
            string.IsNullOrWhiteSpace(CleanupPolicy))
            throw new StudioValidationException("The canonical preview plan is incomplete or invalid.");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
