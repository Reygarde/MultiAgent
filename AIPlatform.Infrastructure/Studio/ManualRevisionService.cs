using System.Security.Cryptography;
using System.Text;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class ManualRevisionService(
    MissionStore missions, IWorkspaceStore workspaces, IExecutionPlanService plans,
    ISandboxRunner sandbox) : IManualRevisionService
{
    public async Task<ExecutionPlan?> ApplyAsync(Guid id, string expectedFingerprint, ManualRevisionRole role,
        IReadOnlyList<GeneratedFile> replacements, CancellationToken cancellationToken)
    {
        var mission = missions.Get(id);
        if (mission?.Status is not (MissionStatus.AwaitingApproval or MissionStatus.BuildFailed) ||
            mission.TemplateId != FullStackTemplate.Id ||
            mission.Workflow?.Revisions?.LastOrDefault() is not { ExecutionPlan: { } saved } revision ||
            revision.Version != mission.Workflow.Version || revision.Fingerprint != expectedFingerprint ||
            saved.Fingerprint != expectedFingerprint || mission.SandboxApproved)
            return null;
        if (!Enum.IsDefined(role) || replacements.Count is < 1 or > 3 ||
            replacements.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != replacements.Count ||
            replacements.Any(file => !FullStackTemplate.IsEditableForRole(file.Path, role.ToString())))
            throw new StudioValidationException("Only distinct editable files for the requested full-stack role may be revised.");

        var replacementMap = replacements.ToDictionary(file => file.Path, StringComparer.Ordinal);
        if (replacements.Any(file => revision.Files.All(old => old.Path != file.Path) ||
            revision.Files.Single(old => old.Path == file.Path).Content == file.Content))
            throw new StudioValidationException("Each replacement must change a reviewed file.");
        var files = revision.Files.Select(file => replacementMap.GetValueOrDefault(file.Path, file)).ToArray();
        workspaces.ValidateFiles(files, FullStackTemplate.Id);
        if (!(await sandbox.GetAvailabilityAsync(id, cancellationToken)).Available)
            return null;
        var before = await plans.GetAsync(id, cancellationToken);
        if (before.Fingerprint != expectedFingerprint || saved.Canonical?.ComputeFingerprint() != expectedFingerprint ||
            !await WorkspaceMatchesAsync(id, revision.Files, cancellationToken))
            return null;
        if (!missions.BeginManualRevision(id, mission.UpdatedAt, expectedFingerprint, role, files))
            return null;

        try
        {
            var token = missions.Cancellation(id);
            if (!await WorkspaceMatchesAsync(id, revision.Files, token))
                throw new StudioValidationException("Workspace changed before the manual revision was applied.");
            await workspaces.ReplaceFullStackFilesAsync(id, role, replacements, token);
            if (!await WorkspaceMatchesAsync(id, files, token))
                throw new StudioValidationException("Workspace differs from the requested manual revision.");
            var plan = await plans.GetAsync(id, token);
            var expectedHashes = before.Canonical!.Files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);
            foreach (var file in replacements)
                expectedHashes[file.Path] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Content)));
            if (plan.Canonical is null || plan.Canonical.Files.Count != expectedHashes.Count ||
                plan.Canonical.Files.Any(file => expectedHashes.GetValueOrDefault(file.Path) != file.Sha256) ||
                plan.Fingerprint != (await plans.GetAsync(id, token)).Fingerprint)
                throw new StudioValidationException("The new plan does not match the requested files and current configuration.");
            if (!missions.CompleteManualRevision(id, plan, files))
                throw new StudioValidationException("Manual revision could not be committed; no execution was authorized.");
            return plan;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           StudioValidationException or OperationCanceledException or InvalidOperationException)
        {
            missions.FailManualRevision(id, "Manual revision interrupted; inspect the retained proposal and workspace before resuming.");
            throw;
        }
    }

    private async Task<bool> WorkspaceMatchesAsync(Guid id, IReadOnlyList<GeneratedFile> files, CancellationToken token)
    {
        var paths = workspaces.List(id).Where(file => file.Path != "Generated.slnx").ToArray();
        if (paths.Length != files.Count || paths.Any(path => files.All(file => file.Path != path.Path)))
            return false;
        foreach (var file in files)
            if ((await workspaces.ReadAsync(id, file.Path, token)).Content != file.Content)
                return false;
        return true;
    }
}
