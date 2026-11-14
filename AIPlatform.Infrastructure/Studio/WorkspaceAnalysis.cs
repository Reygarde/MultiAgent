using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class WorkspaceAnalysis(
    IMissionService missions, IWorkspaceStore workspaces, IProjectAnalyzer analyzer) : IWorkspaceAnalysis
{
    public async Task<WorkspaceAnalysisResult> AnalyzeAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var mission = missions.Get(workspaceId);
        if (mission is null || !mission.HasWorkspace || mission.Deleted)
            return new(WorkspaceAnalysisStatus.NotFound);
        if (mission.Status is MissionStatus.Queued or MissionStatus.Running)
            return new(WorkspaceAnalysisStatus.Conflict);

        var files = new List<AnalysisFile>();
        foreach (var file in workspaces.List(workspaceId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProjectFilePolicy.IsExcluded(file.Path)) continue;
            var source = await workspaces.ReadAsync(workspaceId, file.Path, cancellationToken);
            files.Add(new(source.Path, source.Content));
        }
        var analysis = analyzer.Analyze(files, null, cancellationToken);
        var current = missions.Get(workspaceId);
        if (current is null || current.Deleted || current.UpdatedAt != mission.UpdatedAt)
            return new(WorkspaceAnalysisStatus.Conflict);
        return new(WorkspaceAnalysisStatus.Available, analysis);
    }
}
