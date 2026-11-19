namespace AIPlatform.Core.Studio;

/// <summary>Bounded project limits frozen only when explicitly selected for a new React v2 mission.</summary>
public sealed record ProjectSizingProfile(
    string Id, string Name, int MaxFiles, int MaxPlannedFiles, int MaxFileBytes, int MaxProjectBytes,
    int MaxCriteria, int MaxCorrections, int MaxRepairGroups, int MaxFilesPerRepairGroup,
    int MissionTimeoutSeconds)
{
    public static ProjectSizingProfile[] Defaults =>
    [
        new("small", "Petit projet", 40, 12, 65536, 262144, 32, 3, 3, 4, 900),
        new("standard", "Projet standard", 64, 32, 131072, 1048576, 64, 6, 12, 4, 3600),
        new("complex", "Projet complexe", 100, 64, 262144, 4194304, 128, 12, 32, 4, 7200)
    ];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 32 ||
            Id.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-') ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 80 ||
            MaxFiles is < 13 or > 100 || MaxPlannedFiles is < 2 or > 89 || MaxPlannedFiles + 11 > MaxFiles ||
            MaxFileBytes is < 1024 or > 1048576 || MaxProjectBytes < MaxFileBytes || MaxProjectBytes > 4194304 ||
            MaxCriteria is < 1 or > 128 || MaxCorrections is < 0 or > 12 ||
            MaxRepairGroups is < 1 or > 32 || MaxRepairGroups > MaxPlannedFiles || MaxFilesPerRepairGroup is < 1 or > 8 ||
            MaxFilesPerRepairGroup > MaxPlannedFiles || MissionTimeoutSeconds is < 10 or > 7200)
            throw new ArgumentException("Invalid bounded project sizing profile.");
    }

    public StudioOptions Apply(StudioOptions options) => options with
    {
        MaxFiles = MaxFiles, MaxFileBytes = MaxFileBytes, MaxProjectBytes = MaxProjectBytes,
        MaxCorrectionAttempts = MaxCorrections, MissionTimeoutSeconds = MissionTimeoutSeconds
    };
}
