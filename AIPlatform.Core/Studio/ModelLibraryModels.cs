namespace AIPlatform.Core.Studio;

public sealed record ModelCatalogueEntry(
    string Name, string Family, string? Publisher, string[] Specializations,
    string Source, long? DownloadBytes = null, int? DocumentedContext = null,
    string? License = null, string Openness = "Unverified");

public sealed record GpuMemory(string Name, long TotalBytes, long AvailableBytes);
public sealed record HardwareSnapshot(
    long? TotalRamBytes, long? AvailableRamBytes, IReadOnlyList<GpuMemory> Gpus,
    long? FreeDiskBytes, IReadOnlyList<string> Warnings);
public interface IHardwareProbe
{
    Task<HardwareSnapshot> ReadAsync(CancellationToken token);
}

public sealed record LoadedOllamaModel(string Name, long Size, long SizeVram, int? ContextLength)
{
    public string? Digest { get; init; }
}
public sealed record ModelMetadata(
    string Name, string? Family, string? Parameters, string? Quantization, long? InstalledBytes,
    long? ParameterCount, string? Architecture, int? ContextLength, int? Layers,
    int? KvHeads, int? KeyLength, int? ValueLength, bool Completion, string? License)
{
    public string? Digest { get; init; }
}
public static class ModelSpecializations
{
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
        { "Code", "General", "Planning", "Review", "Debugging", "Tests", "Architecture", "Documentation", "Reasoning" });
}
public sealed record ModelMemoryQualification(string Digest, int ContextTokens, long SizeBytes,
    long SizeVramBytes, DateTimeOffset ObservedAt);
public sealed record ModelRoutingProfile(string Name, string[]? Specializations = null,
    ModelMemoryQualification? Qualification = null);
public sealed record ModelCompatibility(
    string Status, string Reason, int ContextTokens, long? WeightsBytes = null,
    long? KvBytes = null, long? BuffersBytes = null, long? RequiredRamBytes = null,
    long? RequiredVramBytes = null, string Placement = "Unknown");
public sealed record ModelDependency(string Kind, string Name, bool Active);
public sealed record LibraryModel(
    ModelCatalogueEntry Catalogue, bool? Installed, ModelMetadata? Metadata,
    LoadedOllamaModel? Loaded, ModelCompatibility Compatibility, IReadOnlyList<ModelDependency> Dependencies)
{
    public ModelRoutingProfile? RoutingProfile { get; init; }
}
public sealed record ModelLibrarySnapshot(
    HardwareSnapshot Hardware, IReadOnlyList<LibraryModel> Models,
    IReadOnlyList<LoadedOllamaModel> Loaded, string? OllamaError = null)
{
    public IReadOnlyList<string> AvailableSpecializations => ModelSpecializations.All;
    public int MaximumContextTokens => AIPlatform.Core.ModelContextLimits.Maximum;
}
public sealed record ModelDownload(
    Guid Id, string Model, string Status, string Progress, long? Total, long? Completed,
    DateTimeOffset UpdatedAt, string? Error = null);
public sealed record ModelRoutingChoice(
    string Model, int Score, string Reason, ModelCompatibility Compatibility);
public sealed record ModelRoutingDecision(
    string Model, string Category, string Reason, string Confidence, IReadOnlyList<ModelRoutingChoice> Alternatives);
public interface IModelRouter
{
    Task<ModelRoutingDecision> SelectAsync(string task, string role, int contextTokens, CancellationToken token);
    void ResetStatistics();
}

public sealed class ModelOperationConflictException(string message) : Exception(message);

public interface IModelLibraryService
{
    Task<ModelLibrarySnapshot> SnapshotAsync(int context, CancellationToken token);
    Task<long> VerifyAsync(string name, CancellationToken token);
    IReadOnlyList<ModelDownload> Downloads();
    ModelDownload StartDownload(string name, bool confirmed);
    bool CancelDownload(Guid id);
    Task DeleteAsync(string name, bool confirmed, CancellationToken token);
    Task GuardExecutionAsync(string name, int context, CancellationToken token, bool automatic = false);
    ModelRoutingProfile SaveSpecializations(string name, string[]? specializations);
    ModelRoutingProfile ClearQualification(string name);
    Task<ModelRoutingProfile> QualifyAsync(string name, int context, bool confirmed, CancellationToken token);
}
