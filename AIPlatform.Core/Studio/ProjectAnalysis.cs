namespace AIPlatform.Core.Studio;

public sealed record AnalysisFile(string Path, string Content);
public sealed record ProjectDependency(string Ecosystem, string Name, string? Version, string Manifest);
public sealed record ProjectScript(string Manifest, string Name);
public sealed record AnalysisIssue(string Path, string Message);
public sealed record ProjectAnalysis(
    bool? GitRepositoryDetected,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<string> Solutions,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> PackageManifests,
    IReadOnlyList<string> TestProjects,
    IReadOnlyList<string> EntryPoints,
    IReadOnlyList<string> Directories,
    IReadOnlyList<ProjectDependency> Dependencies,
    IReadOnlyList<ProjectScript> Scripts,
    IReadOnlyList<string> DockerFiles,
    IReadOnlyList<AnalysisIssue> Issues);

public interface IProjectAnalyzer
{
    ProjectAnalysis Analyze(IReadOnlyList<AnalysisFile> files, bool? gitRepositoryDetected, CancellationToken cancellationToken);
}

public enum WorkspaceAnalysisStatus { Available, NotFound, Conflict }
public sealed record WorkspaceAnalysisResult(WorkspaceAnalysisStatus Status, ProjectAnalysis? Analysis = null);

public interface IWorkspaceAnalysis
{
    Task<WorkspaceAnalysisResult> AnalyzeAsync(Guid workspaceId, CancellationToken cancellationToken);
}
