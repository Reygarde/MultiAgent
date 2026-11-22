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
    string Fingerprint)
{
    public Guid? PreparationEvidenceId { get; init; }
    public IReadOnlyList<ExecutionPlanFile>? Files { get; init; }
}

/// <summary>A short-lived, single-use challenge. Historical preparation inspects Docker but never executes it.</summary>
public sealed record ReactRevisionValidationPreparation(Guid RequestId, DateTimeOffset ExpiresAt,
    ReactRevisionValidationPlan Plan);

public sealed record ReactHistoricalExecutionPreparation(Guid Id, Guid MissionId, int RevisionVersion,
    string RevisionFingerprint, string MissionFingerprint, string Fingerprint,
    ExecutionPlan PreparedExecutionPlan, DateTimeOffset PreparedAt);

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
    Task<IReadOnlyList<ReactRevisionValidationReport>> GetReportsAsync(Guid id, CancellationToken token);
}

public sealed class ReactRevisionValidationConflictException(string message) : Exception(message);

public sealed class ReactRevisionValidation(
    IMissionService missions, IWorkspaceStore workspaces, DockerSandboxRunner sandbox, SandboxOptions options,
    StudioOptions studio, MissionExecutionGate gate, TimeProvider clock, ILogger<ReactRevisionValidation> logger,
    IExecutionPlanService? executionPlans = null, ISandboxImageTrust? imageTrust = null)
    : IReactRevisionValidation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Stages = ["TypeScript", "Build", "Tests"];
    private readonly ConcurrentDictionary<Guid, ReactRevisionValidationPreparation> pending = new();
    private readonly ConcurrentDictionary<Guid, Guid> active = new();
    private readonly string root = Path.Combine(SafePaths.ValidateStorageDirectory(studio.DataDirectory), "react-revision-validation");

    public async Task<ReactRevisionValidationPreparation> PrepareAsync(Guid id, CancellationToken token)
    {
        await gate.EnterAsync(token);
        try
        {
            var (plan, _, _) = await CaptureAsync(id, token);
            var now = clock.GetUtcNow();
            foreach (var item in pending)
                if (item.Value.ExpiresAt <= now) pending.TryRemove(item);
            if (pending.TryGetValue(id, out var existing) && existing.Plan.Fingerprint == plan.Fingerprint &&
                existing.Plan.PreparationEvidenceId == plan.PreparationEvidenceId)
                return existing;
            var request = new ReactRevisionValidationPreparation(Guid.NewGuid(), now.AddMinutes(15), plan);
            pending[id] = request;
            return request;
        }
        finally { gate.Exit(); }
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
            var (plan, files, manifest) = await CaptureAsync(id, token, requireEvidence: true);
            if (plan.Fingerprint != prepared.Plan.Fingerprint ||
                plan.PreparationEvidenceId != prepared.Plan.PreparationEvidenceId)
                throw new ReactRevisionValidationConflictException("The published revision or its sandbox configuration changed. Prepare a new request.");
            return await ExecuteAsync(plan, files, manifest, token);
        }
        finally { gate.Exit(); }
    }

    public async Task<ReactRevisionValidationReport?> GetReportAsync(Guid id, CancellationToken token) =>
        (await GetReportsAsync(id, token)).FirstOrDefault();

    public async Task<IReadOnlyList<ReactRevisionValidationReport>> GetReportsAsync(Guid id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (missions.Get(id) is not { TemplateId: MissionReactTemplate.Id, Deleted: false })
            throw new KeyNotFoundException("React v2 mission not found.");
        var reports = new List<ReactRevisionValidationReport>();
        var legacy = ReportPath(id);
        if (File.Exists(legacy))
            reports.Add(await ReadReportAsync(legacy, id, null, token));
        var directory = ReportDirectory(id);
        if (Directory.Exists(directory))
        {
            var snapshots = new Dictionary<Guid, string>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var name = Path.GetFileName(path);
                var match = Regex.Match(name, @"\A([a-f0-9]{32})-(000|001|002|003|999)\.json\z",
                    RegexOptions.CultureInvariant);
                if (!match.Success || !Guid.TryParseExact(match.Groups[1].Value, "N", out var run))
                    throw new IOException("Independent React validation snapshot has an invalid name.");
                if (!snapshots.TryGetValue(run, out var previous) ||
                    string.CompareOrdinal(name, Path.GetFileName(previous)) > 0)
                    snapshots[run] = path;
            }
            foreach (var (run, path) in snapshots)
            {
                if (reports.Any(report => report.RunId == run))
                    throw new IOException("Independent React validation run is duplicated.");
                reports.Add(await ReadReportAsync(path, id, run, token));
            }
        }
        return reports.OrderByDescending(report => report.StartedAt).ThenByDescending(report => report.RunId).ToArray();
    }

    private async Task<ReactRevisionValidationReport> ReadReportAsync(string path, Guid missionId, Guid? runId,
        CancellationToken token)
    {
        SafePaths.RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var report = await JsonSerializer.DeserializeAsync<ReactRevisionValidationReport>(stream, Json, token)
            ?? throw new IOException("Independent React validation report is unreadable.");
        if (report.RunId == Guid.Empty || report.Plan.MissionId != missionId || runId.HasValue && report.RunId != runId)
            throw new IOException("Independent React validation report identity does not match its storage path.");
        return report.Status == "Running" && (!active.TryGetValue(missionId, out var run) || run != report.RunId)
            ? report with { Status = "Interrupted", Error = "Execution did not record a completion. No automatic retry." }
            : report;
    }

    internal async Task<(ReactRevisionValidationPlan, GeneratedFile[], ExecutionPlanFile[])> CaptureAsync(
        Guid id, CancellationToken token, bool requireEvidence = false)
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
        if (!options.Enabled)
            throw new ReactRevisionValidationConflictException("An enabled sandbox is required.");
        var missionFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(mission, Json)));

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
        var execution = publication;
        var historical = saved.ImageDigest is null;
        if (historical)
        {
            RequireHistoricalRevision(mission, revision);
            await RequirePersistedMissionAsync(mission, missionFingerprint, token);
            if (executionPlans is null || imageTrust?.GetExpectedImageId(ReactTemplate.Image) is not { } expectedImage ||
                !Regex.IsMatch(expectedImage, @"\Asha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant))
                throw new ReactRevisionValidationConflictException("The installed provisioning manifest must be verified before historical preparation.");
            var currentStatic = await executionPlans.GetStaticAsync(id, token, revision.Version);
            if (ExecutionPlanRevisionGuard.Compare(revision, currentStatic, MissionReactTemplate.Id, staticReview: true).Count > 0)
                throw new ReactRevisionValidationConflictException("The reviewed sources or sandbox configuration changed.");
            var availability = await sandbox.GetAvailabilityAsync(id, token);
            if (!availability.Available)
                throw new ReactRevisionValidationConflictException(availability.Reason);
            try { execution = await executionPlans.GetAsync(id, token, revision.Version); }
            catch (SandboxPrerequisiteException error) { throw new ReactRevisionValidationConflictException(error.Message); }
            if (execution.ReactDescriptor is not { } descriptor ||
                saved.CompareTo(descriptor).Any(difference => difference.Field is not ("effectiveImage" or "imageDigest")) ||
                descriptor.CompareManifest(files).Count > 0 || descriptor.ComputeFingerprint(files) != execution.Fingerprint ||
                !imageTrust.IsTrustedImage(ReactTemplate.Image, execution.Image))
                throw new ReactRevisionValidationConflictException("The resolved execution plan does not match the reviewed revision and trusted image.");
        }
        if (execution.ReactDescriptor?.ImageDigest is not { } image || image != execution.ReactDescriptor.EffectiveImage ||
            image != execution.Image || !Regex.IsMatch(image, @"\Asha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant))
            throw new ReactRevisionValidationConflictException("An immutable React Docker image ID is required.");
        if (missions.Get(id) is not { } current ||
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(current, Json))) != missionFingerprint)
            throw new ReactRevisionValidationConflictException("The mission changed while reading the published revision.");

        var stages = Stages.Select(stage => new ExecutionPlanStage(stage, sandbox.CreateArguments(
            "<container>", "<revision snapshot>", stage, image, MissionReactTemplate.Id, manifest))).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1, MissionId = id, revision.Version, revision.Fingerprint, Manifest = manifest,
            saved.ImageReference, Image = image, options.Enabled, options.TimeoutSeconds, Stages = stages,
            HistoricalMissionFingerprint = historical ? missionFingerprint : null
        })));
        var plan = new ReactRevisionValidationPlan(id, revision.Version, revision.Fingerprint, manifest.Length,
            saved.ImageReference, image, options.TimeoutSeconds, stages, fingerprint) { Files = manifest };
        if (historical)
        {
            var evidence = await HistoricalEvidenceAsync(new(Guid.NewGuid(), id, revision.Version,
                revision.Fingerprint, missionFingerprint, fingerprint, execution, clock.GetUtcNow()), token, requireEvidence);
            await RequirePersistedMissionAsync(mission, missionFingerprint, token);
            plan = plan with { PreparationEvidenceId = evidence.Id };
        }
        return (plan, files, manifest);
    }

    private async Task RequirePersistedMissionAsync(MissionSnapshot mission, string fingerprint, CancellationToken token)
    {
        var path = Path.Combine(SafePaths.ValidateStorageDirectory(studio.DataDirectory), "missions", $"{mission.Id:N}.json");
        SafePaths.RejectLinks(path);
        if (!File.Exists(path))
            throw new ReactRevisionValidationConflictException("The persisted historical mission is missing.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var json = new JsonSerializerOptions(Json);
        json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        MissionSnapshot? persisted;
        try { persisted = await JsonSerializer.DeserializeAsync<MissionSnapshot>(stream, json, token); }
        catch (JsonException) { throw new ReactRevisionValidationConflictException("The persisted historical mission is invalid."); }
        if (persisted is null ||
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(persisted, Json))) != fingerprint)
            throw new ReactRevisionValidationConflictException("The persisted mission differs from the loaded historical revision.");
    }

    private static void RequireHistoricalRevision(MissionSnapshot mission, ProjectRevision revision)
    {
        var flow = mission.Workflow!;
        if (mission.Status != MissionStatus.GeneratedUnverified || mission.Archived ||
            flow.MissionId != mission.Id || flow.WorkflowId == Guid.Empty || flow.Version != revision.Version ||
            flow.Plan is null || flow.PendingSoftwareAgentCall is not null ||
            flow.PendingSoftwareCallReservationId is not null || flow.PendingSoftwareCallReservationCount is not null ||
            flow.PlanApproval is { } approval && (approval.Version < 1 || approval.Version > revision.Version) ||
            flow.SoftwareCorrections.Any(correction => correction.InFlight ||
                correction.CallState is ReactSoftwareCorrectionCallState.InFlight or
                    ReactSoftwareCorrectionCallState.MayHaveBeenSent or ReactSoftwareCorrectionCallState.ResponseReceived ||
                !correction.Ready && correction.ActiveReservationId is not null))
            throw new ReactRevisionValidationConflictException("An intact GeneratedUnverified revision without ambiguous agent calls is required.");
        try { MissionStore.RequireApprovedPlan(mission); }
        catch (StudioValidationException error) { throw new ReactRevisionValidationConflictException(error.Message); }
        var ready = flow.SoftwareCorrections.LastOrDefault();
        if (ready is { Ready: true } && (ready.ActiveAttempt is null || ready.ActiveAttempt.WorkflowVersion == flow.Version) &&
            !ReactCorrectionPatches.HasReadyCandidate(mission, ready, revision.Files))
            throw new ReactRevisionValidationConflictException("The published sources do not match their validated correction checkpoint.");
    }

    private async Task<ReactHistoricalExecutionPreparation> HistoricalEvidenceAsync(
        ReactHistoricalExecutionPreparation expected, CancellationToken token, bool required)
    {
        var directory = SafePaths.ValidateStorageDirectory(Path.Combine(root, "preparations"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{expected.MissionId:N}-{expected.Fingerprint}.json");
        SafePaths.RejectLinks(path);
        if (!File.Exists(path))
        {
            if (required)
                throw new ReactRevisionValidationConflictException("Historical preparation evidence is missing. Prepare a new request.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await JsonSerializer.SerializeAsync(stream, expected, Json, token);
                File.Move(temporary, path);
            }
            finally { File.Delete(temporary); }
        }
        if (new FileInfo(path).Length is 0 or > 1048576)
            throw new ReactRevisionValidationConflictException("Historical preparation evidence has an invalid size.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var strict = new JsonSerializerOptions(Json)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true
        };
        ReactHistoricalExecutionPreparation? evidence;
        try { evidence = await JsonSerializer.DeserializeAsync<ReactHistoricalExecutionPreparation>(input, strict, token); }
        catch (JsonException) { throw new ReactRevisionValidationConflictException("Historical preparation evidence is invalid."); }
        if (evidence is null || evidence.Id == Guid.Empty || evidence.PreparedAt == default ||
            JsonSerializer.Serialize(evidence with { Id = expected.Id, PreparedAt = expected.PreparedAt }, Json) !=
                JsonSerializer.Serialize(expected, Json))
            throw new ReactRevisionValidationConflictException("Historical preparation evidence does not match the current revision.");
        return evidence;
    }

    private async Task<ReactRevisionValidationReport> ExecuteAsync(ReactRevisionValidationPlan plan,
        GeneratedFile[] files, ExecutionPlanFile[] manifest, CancellationToken token)
    {
        var id = plan.MissionId;
        var report = new ReactRevisionValidationReport(Guid.NewGuid(), plan, "Running", clock.GetUtcNow(), null, []);
        active[id] = report.RunId;
        try
        {
            await SaveAsync(report, 0);
            try
            {
                var availability = await sandbox.GetAvailabilityAsync(plan.ImageReference, token);
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
                        await SaveAsync(report, report.Results.Count);
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
            await SaveAsync(report, 999);
            return report;
        }
    }

    private string ReportPath(Guid id)
    {
        var path = Path.Combine(root, $"{id:N}.json");
        SafePaths.RejectLinks(path);
        return path;
    }

    private string ReportDirectory(Guid id) =>
        SafePaths.ValidateStorageDirectory(Path.Combine(root, "reports", $"{id:N}"));

    private async Task SaveAsync(ReactRevisionValidationReport report, int sequence)
    {
        var directory = ReportDirectory(report.Plan.MissionId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{report.RunId:N}-{sequence:D3}.json");
        SafePaths.RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, report, Json);
            File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }
}
