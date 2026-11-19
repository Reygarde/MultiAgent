using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIPlatform.Core.Studio;
using Microsoft.Extensions.Logging;

namespace AIPlatform.Infrastructure.Studio;

/// <summary>Exact source and execution scope for independent React validation.</summary>
public sealed record ReactRevisionValidationPlan(Guid MissionId, int RevisionVersion, string RevisionFingerprint,
    int FileCount, string ImageReference, string Image, int TimeoutSeconds, IReadOnlyList<ExecutionPlanStage> Stages,
    string Fingerprint);

/// <summary>A short-lived, single-use challenge. Preparation does not execute or inspect Docker.</summary>
public sealed record ReactRevisionValidationPreparation(Guid RequestId, DateTimeOffset ExpiresAt,
    ReactRevisionValidationPlan Plan);

/// <summary>Explicit authorization for the exact prepared scope.</summary>
public sealed record AuthorizeReactRevisionValidationRequest(bool Approved, Guid RequestId, string Fingerprint);

/// <summary>Technical evidence only; never a mission or functional acceptance verdict.</summary>
public sealed record ReactRevisionValidationReport(Guid RunId, ReactRevisionValidationPlan Plan, string Status,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, IReadOnlyList<VerificationResult> Results, string? Error = null);

public interface IReactRevisionValidation
{
    Task<ReactRevisionValidationPreparation> PrepareAsync(Guid id, CancellationToken token);
    Task<ReactRevisionValidationReport> AuthorizeAsync(Guid id, AuthorizeReactRevisionValidationRequest request,
        CancellationToken token);
    Task<ReactRevisionValidationReport?> GetReportAsync(Guid id, CancellationToken token);
}

public sealed class ReactRevisionValidationConflictException(string message) : Exception(message);

public sealed class ReactRevisionValidation(
    IMissionService missions, IWorkspaceStore workspaces, DockerSandboxRunner sandbox, SandboxOptions options,
    StudioOptions studio, MissionExecutionGate gate, TimeProvider clock, ILogger<ReactRevisionValidation> logger)
    : IReactRevisionValidation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Stages = ["TypeScript", "Build", "Tests"];
    private readonly ConcurrentDictionary<Guid, ReactRevisionValidationPreparation> pending = new();
    private readonly ConcurrentDictionary<Guid, Guid> active = new();
    private readonly string root = Path.Combine(SafePaths.ValidateStorageDirectory(studio.DataDirectory), "react-revision-validation");

    public async Task<ReactRevisionValidationPreparation> PrepareAsync(Guid id, CancellationToken token)
    {
        var (plan, _, _) = await CaptureAsync(id, token);
        var now = clock.GetUtcNow();
        foreach (var item in pending)
            if (item.Value.ExpiresAt <= now) pending.TryRemove(item);
        var request = new ReactRevisionValidationPreparation(Guid.NewGuid(), now.AddMinutes(15), plan);
        pending[id] = request;
        return request;
    }

    public async Task<ReactRevisionValidationReport> AuthorizeAsync(Guid id, AuthorizeReactRevisionValidationRequest request,
        CancellationToken token)
    {
        if (!request.Approved || !pending.TryGetValue(id, out var prepared) ||
            prepared.RequestId != request.RequestId || prepared.Plan.Fingerprint != request.Fingerprint ||
            prepared.ExpiresAt <= clock.GetUtcNow() || !pending.TryRemove(new(id, prepared)))
            throw new ReactRevisionValidationConflictException("Explicit approval of a current, unused validation request is required.");

        await gate.EnterAsync(token);
        try
        {
            if (prepared.ExpiresAt <= clock.GetUtcNow())
                throw new ReactRevisionValidationConflictException("The validation request expired while waiting for the sandbox.");
            var (plan, files, manifest) = await CaptureAsync(id, token);
            if (plan.Fingerprint != prepared.Plan.Fingerprint)
                throw new ReactRevisionValidationConflictException("The published revision or its sandbox configuration changed. Prepare a new request.");
            return await ExecuteAsync(plan, files, manifest, token);
        }
        finally { gate.Exit(); }
    }

    public async Task<ReactRevisionValidationReport?> GetReportAsync(Guid id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (missions.Get(id) is not { TemplateId: MissionReactTemplate.Id, Deleted: false })
            throw new KeyNotFoundException("React v2 mission not found.");
        var path = ReportPath(id);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var report = await JsonSerializer.DeserializeAsync<ReactRevisionValidationReport>(stream, Json, token)
            ?? throw new IOException("Independent React validation report is unreadable.");
        return report.Status == "Running" && (!active.TryGetValue(id, out var run) || run != report.RunId)
            ? report with { Status = "Interrupted", Error = "Execution did not record a completion. No automatic retry." }
            : report;
    }

    internal async Task<(ReactRevisionValidationPlan, GeneratedFile[], ExecutionPlanFile[])> CaptureAsync(
        Guid id, CancellationToken token)
    {
        if (missions.Get(id) is not { TemplateId: MissionReactTemplate.Id, Deleted: false } mission)
            throw new KeyNotFoundException("React v2 mission not found.");
        if (!mission.IsTerminal || !mission.HasWorkspace || mission.Workflow?.Revisions?.LastOrDefault() is not { } revision)
            throw new ReactRevisionValidationConflictException("An inactive mission with a published React v2 revision is required.");

        var files = revision.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        workspaces.ValidateFiles(files, MissionReactTemplate.Id, sizing: mission.Sizing);
        var manifest = ReactExecutionPlanDescriptor.Manifest(files).ToArray();
        if (revision.ExecutionPlan is not { ReactDescriptor: { } saved } publication ||
            saved.TemplateId != MissionReactTemplate.Id || saved.ImageReference != ReactTemplate.Image ||
            publication.Fingerprint != revision.Fingerprint || !saved.Files.SequenceEqual(manifest) ||
            saved.ComputeFingerprint(files) != revision.Fingerprint)
            throw new ReactRevisionValidationConflictException("The published React revision does not match its saved manifest.");
        if (!options.Enabled || saved.ImageDigest is not { } image || image != saved.EffectiveImage ||
            image != publication.Image || !Regex.IsMatch(image, @"\Asha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant))
            throw new ReactRevisionValidationConflictException("An enabled sandbox and an immutable React Docker image ID are required.");

        var directory = workspaces.GetDirectory(id);
        if (!workspaces.List(id).Select(file => file.Path).Order(StringComparer.Ordinal)
                .SequenceEqual(manifest.Select(file => file.Path), StringComparer.Ordinal))
            throw new ReactRevisionValidationConflictException("The workspace no longer matches the published file list.");
        for (var i = 0; i < files.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            await using var stream = new FileStream(SafePaths.Resolve(directory, files[i].Path), FileMode.Open,
                FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != Encoding.UTF8.GetByteCount(files[i].Content) ||
                manifest[i].Sha256 != Convert.ToHexString(await SHA256.HashDataAsync(stream, token)))
                throw new ReactRevisionValidationConflictException("The workspace no longer matches the published file hashes.");
        }
        if (missions.Get(id)?.UpdatedAt != mission.UpdatedAt)
            throw new ReactRevisionValidationConflictException("The mission changed while reading the published revision.");

        var stages = Stages.Select(stage => new ExecutionPlanStage(stage, sandbox.CreateArguments(
            "<container>", "<revision snapshot>", stage, image, MissionReactTemplate.Id, manifest))).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1, MissionId = id, revision.Version, revision.Fingerprint, Manifest = manifest,
            saved.ImageReference, Image = image, options.Enabled, options.TimeoutSeconds, Stages = stages
        })));
        return (new(id, revision.Version, revision.Fingerprint, manifest.Length, saved.ImageReference,
            image, options.TimeoutSeconds, stages, fingerprint), files, manifest);
    }

    private async Task<ReactRevisionValidationReport> ExecuteAsync(ReactRevisionValidationPlan plan,
        GeneratedFile[] files, ExecutionPlanFile[] manifest, CancellationToken token)
    {
        var id = plan.MissionId;
        var report = new ReactRevisionValidationReport(Guid.NewGuid(), plan, "Running", clock.GetUtcNow(), null, []);
        active[id] = report.RunId;
        try
        {
            await SaveAsync(report);
            try
            {
                var availability = await sandbox.GetAvailabilityAsync(plan.Image, token);
                if (!availability.Available)
                    return await FinishAsync("Unavailable", availability.Reason);

                var snapshots = new WorkspaceStore(studio with { DataDirectory = SafePaths.ValidateStorageDirectory(root) });
                snapshots.Create(report.RunId);
                try
                {
                    await snapshots.WriteAsync(report.RunId, files, token, MissionReactTemplate.Id, missions.Get(id)!.Sizing);
                    var directory = snapshots.GetDirectory(report.RunId);
                    foreach (var stage in Stages)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var result = await sandbox.RunIndependentReactAsync(directory, stage, plan.Image, manifest, token);
                        result = result with { Diagnostics = DiagnosticParser.Parse(result, MissionReactTemplate.Id),
                            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
                        report = report with { Results = [.. report.Results, result] };
                        await SaveAsync(report);
                    }
                }
                finally { snapshots.Delete(report.RunId); }
                return await FinishAsync(report.Results.All(result => result.Status == "Succeeded") ? "Succeeded" : "Failed");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return await FinishAsync("Interrupted", "Validation was cancelled. No automatic retry.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                StudioValidationException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning(exception, "Independent React validation {RunId} was stopped", report.RunId);
                return await FinishAsync("Failed", "The sandbox, its cleanup, or its storage failed. Check the Studio backend logs.");
            }
        }
        finally { active.TryRemove(id, out _); }

        async Task<ReactRevisionValidationReport> FinishAsync(string status, string? error = null)
        {
            report = report with { Status = status, Error = error, FinishedAt = clock.GetUtcNow() };
            await SaveAsync(report);
            return report;
        }
    }

    private string ReportPath(Guid id)
    {
        var path = Path.Combine(root, $"{id:N}.json");
        SafePaths.RejectLinks(path);
        return path;
    }

    private async Task SaveAsync(ReactRevisionValidationReport report)
    {
        var path = ReportPath(report.Plan.MissionId);
        Directory.CreateDirectory(root);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, report, Json);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
