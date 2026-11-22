using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class OllamaDiagnosticStore(StudioOptions options, TimeProvider timeProvider)
{
    private readonly object gate = new();
    private readonly HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void BeginPlanner(LlmDiagnosticContext context, string model, int attemptNumber,
        IReadOnlyList<WorkflowCriterion> criteria, bool rawConsent)
    {
        lock (gate)
        {
            Update(context, entry => entry with
            {
                Model = model, PlannerRawConsent = rawConsent,
                Planner = new(context.WorkspaceId, context.RunId, context.AttemptId, attemptNumber,
                    model, entry.CreatedAt, "Pending", null, criteria, [])
            });
            active.Add(FileName(context));
        }
    }

    public void RecordPlannerResult(LlmDiagnosticContext context, JsonElement result) =>
        Update(context, entry => entry with
        { Planner = entry.Planner! with { Plan = result.Clone(), Status = "Unvalidated" } });

    public void RecordPlannerNormalizations(LlmDiagnosticContext context,
        IReadOnlyList<PlannerDependencyNormalization> normalizations) =>
        Update(context, entry => entry with
        { Planner = entry.Planner! with { DependencyNormalizations = normalizations } });

    public void FinishPlanner(LlmDiagnosticContext context, string status, IReadOnlyList<PlannerValidationDiagnostic> errors)
    {
        try
        {
            Update(context, entry => entry with
            { Outcome = status, Planner = entry.Planner! with { Status = status, Errors = errors } });
        }
        finally { lock (gate) active.Remove(FileName(context)); }
    }

    public void BeginReviewer(LlmDiagnosticContext context, string model, int attemptNumber,
        string sourceFingerprint, string? routingPath)
    {
        lock (gate)
        {
            Update(context, entry => entry with
            {
                Model = model,
                Reviewer = new(context.WorkspaceId, context.RunId, context.AttemptId, attemptNumber,
                    context.TaskId, sourceFingerprint, routingPath, model, entry.CreatedAt, "Pending", null, [])
            });
            active.Add(FileName(context));
        }
    }

    public void RecordReviewerResult(LlmDiagnosticContext context, JsonElement result) =>
        Update(context, entry => entry with
        { Reviewer = entry.Reviewer! with { Report = result.Clone(), Status = "Unvalidated" } });

    public void FinishReviewer(LlmDiagnosticContext context, string status, IReadOnlyList<ReviewerValidationDiagnostic> errors)
    {
        try
        {
            Update(context, entry => entry with
            { Outcome = status, Reviewer = entry.Reviewer! with { Status = status, Errors = errors } });
        }
        finally { lock (gate) active.Remove(FileName(context)); }
    }

    public IReadOnlyList<ReviewerAttemptDiagnostic> ReviewerAttempts(Guid missionId)
    {
        lock (gate)
            return ReadEntries(missionId).Where(entry => entry.Reviewer is not null)
                .Select(entry => entry.Reviewer!).OrderBy(item => item.CreatedAt).ToArray();
    }

    public TResult WithReviewerAttempts<TResult>(
        Guid missionId, Func<IReadOnlyList<ReviewerAttemptDiagnostic>, TResult> action)
    {
        lock (gate)
        {
            var attempts = ReadEntries(missionId).Where(entry => entry.Reviewer is not null)
                .Select(entry => entry.Reviewer!).OrderBy(item => item.CreatedAt).ToArray();
            return action(attempts);
        }
    }

    public void DeleteReviewerAttempts(Guid missionId)
    {
        lock (gate)
            DeleteEntries(ReadEntries(missionId).Where(entry => entry.Reviewer is not null).ToArray());
    }

    public bool DeleteReviewerAttempts(Guid missionId, Func<bool> canDelete)
    {
        lock (gate)
        {
            if (!canDelete()) return false;
            DeleteEntries(ReadEntries(missionId).Where(entry => entry.Reviewer is not null).ToArray());
            return true;
        }
    }

    public IReadOnlyList<PlannerAttemptDiagnostic> PlannerAttempts(Guid missionId)
    {
        lock (gate)
            return ReadPlannerEntries(missionId).Select(entry => entry.Planner! with
            { RawTraceAvailable = entry.PlannerRawConsent && entry.ResponseBody is not null })
                .OrderBy(item => item.CreatedAt).ToArray();
    }

    public string? PlannerRawResponse(Guid missionId, Guid attemptId)
    {
        lock (gate)
        {
            var entry = ReadPlannerEntries(missionId).SingleOrDefault(item => item.Context.AttemptId == attemptId);
            return entry?.PlannerRawConsent == true ? entry.ResponseBody : null;
        }
    }

    public void DeletePlannerAttempts(Guid missionId)
    {
        lock (gate)
            DeleteEntries(ReadPlannerEntries(missionId));
    }

    private void DeleteEntries(IReadOnlyList<Entry> entries)
    {
        if (entries.Any(entry => active.Contains(FileName(entry.Context))))
            throw new StudioValidationException("Stop the active agent before deleting its private diagnostics.");
        var root = Root(create: false);
        foreach (var entry in entries)
        {
            var path = Path.Combine(root, FileName(entry.Context));
            SafePaths.RejectLinks(path);
            File.Delete(path);
        }
    }

    private IReadOnlyList<Entry> ReadPlannerEntries(Guid missionId) =>
        ReadEntries(missionId).Where(entry => entry.Planner is not null).ToArray();

    private IReadOnlyList<Entry> ReadEntries(Guid missionId)
    {
        var root = Root(create: false);
        if (!Directory.Exists(root)) return [];
        Prune(root, null);
        var entries = new List<Entry>();
        foreach (var path in Directory.GetFiles(root, $"{missionId:N}-*.json"))
        {
            SafePaths.RejectLinks(path);
            if (new FileInfo(path).Length > options.OllamaTraceMaxBytes)
                throw new LlmDiagnosticException("The private diagnostic exceeds the configured size limit.");
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("The private diagnostic is invalid.");
            if (entry.Context.WorkspaceId != missionId)
                throw new InvalidDataException("Private diagnostic mission identity mismatch.");
            entries.Add(entry);
        }
        return entries;
    }

    public void RecordRequest(LlmDiagnosticContext context, string model, byte[] body)
    {
        lock (gate)
        {
            Update(context, entry =>
            {
                RequireRawConsent(entry);
                return entry with { Model = model, RequestBody = Encoding.UTF8.GetString(body), Outcome = "Requested" };
            });
            active.Add(FileName(context));
        }
    }

    public void RecordResponse(LlmDiagnosticContext context, int status, byte[] body) =>
        Update(context, entry =>
        {
            RequireRawConsent(entry);
            return entry with { HttpStatus = status, ResponseBody = Encoding.UTF8.GetString(body), Outcome = "Received" };
        });

    private static void RequireRawConsent(Entry entry)
    {
        if (entry.Planner is not null && !entry.PlannerRawConsent)
            throw new LlmDiagnosticException("Raw Planner capture was not explicitly authorized.");
        if (entry.Reviewer is not null)
            throw new LlmDiagnosticException("Raw Reviewer capture is not authorized.");
    }

    public void RecordResult(LlmDiagnosticContext context, string outcome,
        IReadOnlyList<CustomAgentFileChange>? operations = null, CustomAgentValidationReport? validation = null)
    {
        try
        {
            Update(context, entry => entry with
            {
                Outcome = outcome, Operations = operations ?? entry.Operations,
                Applied = entry.Applied || outcome == "Applied",
                RolledBack = entry.RolledBack || outcome == "RolledBack",
                Validation = validation ?? entry.Validation
            });
        }
        finally
        {
            if (outcome is "Failed" or "Cancelled" or "Succeeded" or "RunFailed" or "RunCancelled" or "RunSucceeded")
                lock (gate) active.Remove(FileName(context));
        }
    }

    public void PruneExpired()
    {
        lock (gate)
        {
            var root = Root(create: false);
            if (Directory.Exists(root)) Prune(root, null);
        }
    }

    public void DeleteWorkspace(Guid workspaceId)
    {
        lock (gate)
        {
            var root = Root(create: false);
            if (!Directory.Exists(root)) return;
            var files = Directory.GetFiles(root, $"{workspaceId:N}-*.json");
            if (files.Any(file => active.Contains(Path.GetFileName(file))))
                throw new StudioValidationException("Stop the active diagnostic run before deleting its traces.");
            foreach (var file in files)
            {
                SafePaths.RejectLinks(file);
                File.Delete(file);
            }
        }
    }

    private void Update(LlmDiagnosticContext context, Func<Entry, Entry> update)
    {
        if (context.WorkspaceId == Guid.Empty || context.RunId == Guid.Empty ||
            context.AttemptId == Guid.Empty || string.IsNullOrWhiteSpace(context.TaskId))
            throw new StudioValidationException("Diagnostic traces require a workspace, run, task and attempt.");
        lock (gate)
        {
            var root = Root(create: true);
            var path = Path.Combine(root, FileName(context));
            SafePaths.RejectLinks(path);
            var entry = File.Exists(path)
                ? JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path), JsonOptions)
                    ?? throw new InvalidDataException("The diagnostic trace is invalid.")
                : new(context, timeProvider.GetUtcNow());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(update(entry), JsonOptions);
            if (bytes.Length > options.OllamaTraceMaxBytes)
                throw new LlmDiagnosticException("Local diagnostic trace size limit exceeded; stop and increase the authorized limit or delete traces.");
            Prune(root, path);
            var temporary = path + ".tmp";
            SafePaths.RejectLinks(temporary);
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, overwrite: true);
                File.SetLastWriteTimeUtc(path, timeProvider.GetUtcNow().UtcDateTime);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private void Prune(string root, string? current)
    {
        var files = new DirectoryInfo(root).GetFiles("*.json")
            .Where(file => file.FullName != current && !active.Contains(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        var activeCount = active.Count(name => !string.Equals(name, Path.GetFileName(current), StringComparison.OrdinalIgnoreCase));
        var retained = Math.Max(0, options.OllamaTraceMaxFiles - activeCount - (current is null ? 0 : 1));
        foreach (var file in files.Where((file, index) => index >= retained ||
            file.LastWriteTimeUtc < timeProvider.GetUtcNow().UtcDateTime.AddDays(-options.OllamaTraceRetentionDays)))
        {
            SafePaths.RejectLinks(file.FullName);
            file.Delete();
        }
        if (activeCount >= options.OllamaTraceMaxFiles)
            throw new LlmDiagnosticException("Local diagnostic trace capacity is occupied by active attempts.");
    }

    private string Root(bool create)
    {
        var root = Path.Combine(SafePaths.ValidateStorageDirectory(options.DataDirectory), "ollama-traces");
        SafePaths.RejectLinks(root);
        if (!create) return root;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Private Ollama diagnostic capture requires Windows user ACLs.");
        Directory.CreateDirectory(root);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(security);
        return root;
    }

    private static string FileName(LlmDiagnosticContext context) =>
        $"{context.WorkspaceId:N}-{context.RunId:N}-{context.AttemptId:N}.json";

    private sealed record Entry(LlmDiagnosticContext Context, DateTimeOffset CreatedAt,
        string? Model = null, string? RequestBody = null, string? ResponseBody = null,
        int? HttpStatus = null, string Outcome = "NotSent",
        IReadOnlyList<CustomAgentFileChange>? Operations = null, bool Applied = false, bool RolledBack = false,
        CustomAgentValidationReport? Validation = null, PlannerAttemptDiagnostic? Planner = null,
        bool PlannerRawConsent = false, ReviewerAttemptDiagnostic? Reviewer = null);
}
