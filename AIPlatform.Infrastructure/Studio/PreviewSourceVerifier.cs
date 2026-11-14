using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed record PreviewSourceIdentity(
    Guid MissionId, int WorkflowVersion, string ValidationFingerprint,
    IReadOnlyList<ExecutionPlanFile> Files);

public sealed record PreviewSnapshotFile(string Path, ImmutableArray<byte> Content);
public sealed record PreviewSourceSnapshot(
    PreviewSourceIdentity Identity, ImmutableArray<PreviewSnapshotFile> Files);

public sealed class PreviewSourceVerifier(IMissionService missions, IWorkspaceStore workspaces)
{
    private static readonly string[] RequiredStages =
        ["BackendBuild", "BackendTests", "TypeScript", "FrontendBuild", "FrontendTests", "Integration"];

    public async Task<PreviewSourceIdentity> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var mission = missions.Get(id);
        if (mission is not { Status: MissionStatus.Verified, HasWorkspace: true, Deleted: false,
                TemplateId: FullStackTemplate.Id, Workflow: { } workflow } ||
            workflow.Revisions?.LastOrDefault() is not { ExecutionPlan: { Canonical: { } canonical } plan } revision ||
            revision.Version != workflow.Version || canonical.MissionId != id ||
            canonical.WorkflowVersion != workflow.Version || canonical.TemplateId != FullStackTemplate.Id ||
            revision.Fingerprint != plan.Fingerprint || plan.Fingerprint != canonical.ComputeFingerprint() ||
            mission.ApprovalFingerprint != plan.Fingerprint ||
            RequiredStages.Any(kind => !workflow.Steps.Any(step => step.Kind == kind &&
                step.Id.EndsWith($"-{workflow.Version}", StringComparison.Ordinal) &&
                step.Status == WorkflowStatus.Completed)))
            throw new StudioValidationException("A verified full-stack revision with its original validation plan is required.");

        var files = canonical.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        if (!files.Any(file => file.Path == "Generated.slnx") ||
            files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != files.Length ||
            files.Any(file => file.Path != "Generated.slnx" && !FullStackTemplate.IsPath(file.Path)))
            throw new StudioValidationException("The verified source manifest is invalid.");

        var revisionFiles = revision.Files;
        if (revisionFiles.Count != files.Length - 1 ||
            revisionFiles.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != revisionFiles.Count ||
            revisionFiles.Any(file => file.Path == "Generated.slnx" ||
                files.All(expected => expected.Path != file.Path ||
                    expected.Sha256 != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Content))))))
            throw new StudioValidationException("The verified revision does not match its validation manifest.");

        var workspaceFiles = workspaces.List(id);
        if (workspaceFiles.Count != files.Length ||
            workspaceFiles.Any(file => files.All(expected => expected.Path != file.Path)))
            throw new StudioValidationException("The workspace no longer matches the verified source manifest.");

        var directory = workspaces.GetDirectory(id);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = file.Path == "Generated.slnx"
                ? Path.Combine(directory, file.Path) : SafePaths.Resolve(directory, file.Path);
            SafePaths.RejectLinks(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (hash != file.Sha256)
                throw new StudioValidationException("The workspace no longer matches the verified source manifest.");
        }

        var current = missions.Get(id);
        if (current?.UpdatedAt != mission.UpdatedAt || current.Status != MissionStatus.Verified)
            throw new StudioValidationException("The mission changed while its verified sources were inspected.");

        return new(id, workflow.Version, plan.Fingerprint, Array.AsReadOnly(files));
    }

    public async Task<PreviewSourceSnapshot> CaptureAsync(Guid id, CancellationToken cancellationToken)
    {
        var identity = await ReadAsync(id, cancellationToken);
        var directory = workspaces.GetDirectory(id);
        var files = ImmutableArray.CreateBuilder<PreviewSnapshotFile>(identity.Files.Count);
        var total = 0;
        foreach (var file in identity.Files)
        {
            var path = file.Path == "Generated.slnx"
                ? Path.Combine(directory, file.Path) : SafePaths.Resolve(directory, file.Path);
            SafePaths.RejectLinks(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            {
                total += count;
                if (total > FullStackTemplate.MaxProjectBytes)
                    throw new StudioValidationException("The verified source snapshot exceeds the full-stack size limit.");
                await content.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            var bytes = content.ToArray();
            if (Convert.ToHexString(SHA256.HashData(bytes)) != file.Sha256)
                throw new StudioValidationException("The workspace changed while its verified sources were captured.");
            files.Add(new(file.Path, ImmutableArray.CreateRange(bytes)));
        }

        var current = await ReadAsync(id, cancellationToken);
        if (current.WorkflowVersion != identity.WorkflowVersion ||
            current.ValidationFingerprint != identity.ValidationFingerprint ||
            !current.Files.SequenceEqual(identity.Files))
            throw new StudioValidationException("The mission changed while its verified sources were captured.");
        return new(identity, files.MoveToImmutable());
    }
}
