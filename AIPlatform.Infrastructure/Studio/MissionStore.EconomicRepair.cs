using System.Text.Json;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed partial class MissionStore
{
    private string RepairPath(Guid id)
    {
        var folder = Path.Combine(directory, "economic-repairs");
        SafePaths.RejectLinks(folder);
        var path = Path.Combine(folder, id.ToString("N") + ".json");
        SafePaths.RejectLinks(path);
        return path;
    }

    private MissionSnapshot ReadLatestForRepair(Guid id)
    {
        var path = Path.Combine(directory, id.ToString("N") + ".json");
        SafePaths.RejectLinks(path);
        if (!File.Exists(path)) throw new KeyNotFoundException();
        return JsonSerializer.Deserialize<MissionSnapshot>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException("Invalid persisted mission.");
    }

    private ReactDraftRepairCheckpoint? ReadRepairCheckpoint(Guid id)
    {
        var path = RepairPath(id);
        if (!File.Exists(path)) return null;
        var result = JsonSerializer.Deserialize<ReactDraftRepairCheckpoint>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException("Invalid economic repair checkpoint.");
        if (result.MissionId != id || result.Id == Guid.Empty || result.Calls.Length is < 1 or > 3 ||
            result.Plan.ReviewerGroups.Count < 1 || result.CompletedGroups < 0 ||
            result.CompletedGroups > result.Calls.Length)
            throw new InvalidDataException("Invalid economic repair capacity.");
        return result;
    }

    public ReactDraftRepairCheckpoint? GetDraftRepair(Guid id)
    {
        lock (sync)
        {
            using var persistence = new PersistenceLock(persistenceMutexName);
            return ReadRepairCheckpoint(id);
        }
    }

    public MissionSnapshot GetLatestForRepair(Guid id)
    {
        lock (sync)
        {
            using var persistence = new PersistenceLock(persistenceMutexName);
            return ReadLatestForRepair(id);
        }
    }

    private void WriteRepairCheckpoint(ReactDraftRepairCheckpoint checkpoint)
    {
        var path = RepairPath(checkpoint.MissionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        SafePaths.RejectLinks(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, checkpoint, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        SafePaths.RejectLinks(path);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    internal void BeginDraftRepair(ReactDraftRepairCheckpoint checkpoint)
    {
        lock (sync)
        {
            using var persistence = new PersistenceLock(persistenceMutexName);
            var mission = ReadLatestForRepair(checkpoint.MissionId);
            RequireApprovedPlan(mission);
            if (ReadRepairCheckpoint(mission.Id) is not null ||
                WorkflowSourceFingerprint.Compute(mission) != checkpoint.SourceFingerprint ||
                mission.GenerationReservations!.Count != checkpoint.BaseReservations ||
                mission.CorrectionAttempts != checkpoint.CorrectionAttemptsBefore ||
                mission.CorrectionAttempts >= options.CorrectionLimitFor(mission) ||
                mission.GenerationBudgetExhausted || mission.MaxTotalGenerations is not { } limit ||
                limit - checkpoint.BaseReservations < checkpoint.Calls.Length + checkpoint.Plan.ReviewerGroups.Count)
                throw new StudioValidationException("Draft, attempts or atomic generation capacity changed; prepare again. No model was called.");
            WriteRepairCheckpoint(checkpoint);
        }
    }

    internal ReactDraftRepairCheckpoint SaveDraftRepair(Guid id, Guid owner,
        Func<ReactDraftRepairCheckpoint, ReactDraftRepairCheckpoint> update)
    {
        lock (sync)
        {
            using var persistence = new PersistenceLock(persistenceMutexName);
            var current = ReadRepairCheckpoint(id) ?? throw new StudioValidationException("Missing draft repair checkpoint.");
            if (current.Id != owner) throw new StudioValidationException("Draft repair ownership changed.");
            var next = update(current);
            WriteRepairCheckpoint(next);
            return next;
        }
    }

    internal MissionSnapshot CommitDraftRepair(Guid id, Guid owner)
    {
        lock (sync)
        {
            using var persistence = new PersistenceLock(persistenceMutexName);
            var repair = ReadRepairCheckpoint(id) ?? throw new StudioValidationException("Missing draft repair checkpoint.");
            var mission = ReadLatestForRepair(id);
            var flow = mission.Workflow!;
            var alreadyApplied = flow.Draft!.SequenceEqual(repair.Candidate) &&
                repair.Plan.SourcePlanFingerprint == ReactDraftRepair.SourcePlanFingerprint(mission);
            if (repair.Id != owner || repair.CompletedGroups != repair.Calls.Length || repair.InFlight ||
                mission.CorrectionAttempts != repair.CorrectionAttemptsBefore + 1 ||
                ReactDraftRepair.SourcePlanFingerprint(mission) != repair.Plan.SourcePlanFingerprint ||
                !alreadyApplied && WorkflowSourceFingerprint.Compute(mission) != repair.SourceFingerprint)
                throw new StudioValidationException("The original draft or repair state changed. Candidate not applied.");
            if (!alreadyApplied)
            {
                mission = mission with { Workflow = flow with { Draft = repair.Candidate },
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Events = [.. mission.Events, new(mission.Events.Count + 1, DateTimeOffset.UtcNow,
                        "ReactDraftRepair", StepStatus.Succeeded,
                        "Complete statically checked candidate applied atomically; original retained in repair checkpoint. No execution proof.")] };
                missions[id] = Save(mission);
            }
            WriteRepairCheckpoint(repair with { Ready = true });
            return mission;
        }
    }
}
