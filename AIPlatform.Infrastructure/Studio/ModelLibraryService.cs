using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using Microsoft.Extensions.Logging;

namespace AIPlatform.Infrastructure.Studio;

public sealed class ModelLibraryService(
    HttpClient ollamaClient, HttpClient registryClient, OllamaOptions options, StudioOptions studio,
    IHardwareProbe hardware, ICustomAgentStore agents, IAgentRegistry registry,
    OllamaModelOperations operations, ILogger<ModelLibraryService> logger, IMissionService? missions = null)
    : IModelLibraryService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly List<ModelDownload> downloads = [];
    private readonly Dictionary<Guid, (CancellationTokenSource Cancellation, Task Work)> running = [];
    private readonly ModelRoutingProfileStore profiles = new(studio);
    private bool initialized;
    private string StatePath => Path.Combine(SafePaths.ValidateStorageDirectory(studio.DataDirectory), "model-downloads.json");

    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 ||
            !Regex.IsMatch(name, @"^(?:[a-z0-9][a-z0-9_-]*/)?[a-z0-9][a-z0-9._-]*(?::[A-Za-z0-9][A-Za-z0-9._-]*)?$",
                RegexOptions.CultureInvariant) ||
            name.Contains("..", StringComparison.Ordinal) || name.Contains("cloud", StringComparison.OrdinalIgnoreCase))
            throw new StudioValidationException("Use a local Ollama identifier [namespace/]model[:tag], without URL, host or cloud tag.");
        return name.Contains(':') ? name : name + ":latest";
    }

    public IReadOnlyList<ModelCatalogueEntry> Catalogue()
    {
        using var stream = typeof(ModelLibraryService).Assembly.GetManifestResourceStream("Studio.model-catalog.json")
            ?? throw new InvalidDataException("Model catalogue resource is missing.");
        return JsonSerializer.Deserialize<ModelCatalogueEntry[]>(stream, Json)
            ?? throw new InvalidDataException("Model catalogue is invalid.");
    }

    private Uri Url(string path) => new(new Uri(options.BaseUrl), path);

    private async Task<JsonDocument> ReadAsync(string path, object? body, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = body is null ? await ollamaClient.GetAsync(Url(path), deadline.Token) :
                await ollamaClient.PostAsJsonAsync(Url(path), body, deadline.Token);
            response.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token),
                cancellationToken: deadline.Token);
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        { throw new LlmTimeoutException("Ollama metadata timed out.", exception); }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        { throw new LlmProviderException("Ollama metadata is unavailable.", exception); }
    }

    public async Task<ModelMetadata> MetadataAsync(string name, long? size, CancellationToken token, string? digest = null)
    {
        name = ValidateName(name);
        using var doc = await ReadAsync("/api/show", new { model = name }, token);
        var root = doc.RootElement;
        var details = root.TryGetProperty("details", out var d) ? d : default;
        var info = root.TryGetProperty("model_info", out var i) ? i : default;
        string? Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        long? Number(string key) => info.ValueKind == JsonValueKind.Object && info.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0 ? number : null;
        int? Small(string key) => Number(key) is { } number && number <= int.MaxValue ? (int)number : null;
        var architecture = Text(info, "general.architecture");
        var prefix = architecture + ".";
        var heads = Small(prefix + "attention.head_count");
        var embedding = Small(prefix + "embedding_length");
        var headLength = heads is > 0 && embedding is > 0 ? embedding / heads : null;
        var license = Text(root, "license");
        var licenseName = license is not null && Regex.IsMatch(license, @"Apache License[\s\S]{0,100}Version 2\.0",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "Apache-2.0" :
            license?.Contains("Permission is hereby granted", StringComparison.Ordinal) == true ? "MIT" :
            string.IsNullOrWhiteSpace(license) ? null : "Licence spécifique (consulter la fiche Ollama)";
        return new(name, Text(details, "family"), Text(details, "parameter_size"), Text(details, "quantization_level"),
            size, Number("general.parameter_count"), architecture, Small(prefix + "context_length"),
            Small(prefix + "block_count"), Small(prefix + "attention.head_count_kv"),
            Small(prefix + "attention.key_length") ?? headLength, Small(prefix + "attention.value_length") ?? headLength,
            root.TryGetProperty("capabilities", out var caps) && caps.EnumerateArray().Any(cap => cap.GetString() == "completion"),
            licenseName) { Digest = digest };
    }

    private static string? Digest(JsonElement model) => model.TryGetProperty("digest", out var value) &&
        value.ValueKind == JsonValueKind.String ? ModelRoutingProfileStore.NormalizeDigest(value.GetString()) : null;

    public ModelRoutingProfile SaveSpecializations(string name, string[]? specializations)
    {
        ModelRoutingProfileStore.ValidateSpecializations(specializations);
        return profiles.Update(name, profile => profile with { Specializations = specializations?.ToArray() });
    }

    public ModelRoutingProfile ClearQualification(string name) =>
        profiles.Update(name, profile => profile with { Qualification = null });

    public async Task<ModelRoutingProfile> QualifyAsync(string name, int context, bool confirmed, CancellationToken token)
    {
        name = ValidateName(name);
        if (!confirmed || context is < 512 or > ModelContextLimits.Maximum)
            throw new StudioValidationException("Explicit qualification confirmation and a bounded context are required.");
        await operations.Gate.WaitAsync(token);
        try
        {
            using var tags = await ReadAsync("/api/tags", null, token);
            var installed = tags.RootElement.GetProperty("models").EnumerateArray()
                .FirstOrDefault(item => ValidateName(item.GetProperty("name").GetString()!) == name);
            if (installed.ValueKind != JsonValueKind.Object || Digest(installed) is not { } digest)
                throw new StudioValidationException("An installed model with a verified weight digest is required for qualification.");
            var metadata = await MetadataAsync(name, installed.GetProperty("size").GetInt64(), token, digest);
            var loaded = (await LoadedAsync(token)).SingleOrDefault(item => item.Name == name);
            if (!metadata.Completion || metadata.ContextLength is null || metadata.ContextLength < context ||
                loaded is null || loaded.ContextLength is not { } observedContext || observedContext < context ||
                loaded.Digest != digest ||
                observedContext > ModelContextLimits.Maximum || observedContext > metadata.ContextLength || loaded.Size <= 0 ||
                loaded.Size < metadata.InstalledBytes || loaded.SizeVram < 0 || loaded.SizeVram > loaded.Size)
                throw new StudioValidationException("Qualification requires a real resident completion model with consistent allocations and a known sufficient context. No model was loaded.");
            var qualification = new ModelMemoryQualification(digest, observedContext, loaded.Size, loaded.SizeVram, DateTimeOffset.UtcNow);
            var estimate = ModelMemoryEstimator.Estimate(metadata, await hardware.ReadAsync(token), context, loaded,
                qualification: qualification);
            if (estimate.Status != "Compatible")
                throw new StudioValidationException("Qualification refused: " + estimate.Reason);
            // Recheck identity after observing allocations; a replaced tag cannot inherit this evidence.
            using var latest = await ReadAsync("/api/tags", null, token);
            if (!latest.RootElement.GetProperty("models").EnumerateArray().Any(item =>
                    ValidateName(item.GetProperty("name").GetString()!) == name && Digest(item) == digest))
                throw new StudioValidationException("Model weights changed during qualification. Observe the new model explicitly.");
            return profiles.Update(name, profile => profile with { Qualification = qualification });
        }
        finally { operations.Gate.Release(); }
    }

    public async Task<IReadOnlyList<LoadedOllamaModel>> LoadedAsync(CancellationToken token)
    {
        using var doc = await ReadAsync("/api/ps", null, token);
        return doc.RootElement.GetProperty("models").EnumerateArray().Select(model => new LoadedOllamaModel(
            model.GetProperty("name").GetString()!, model.GetProperty("size").GetInt64(),
            model.GetProperty("size_vram").GetInt64(),
            model.TryGetProperty("context_length", out var context) ? context.GetInt32() : null)
            { Digest = Digest(model) }).ToArray();
    }

    public IReadOnlyList<ModelDependency> Dependencies(string name)
    {
        name = ValidateName(name);
        bool Is(string candidate) => (candidate.Contains(':') ? candidate : candidate + ":latest") == name;
        var result = new List<ModelDependency>();
        foreach (var agent in agents.ListAgents())
            if (Is(agent.ModelName) || (agent.FallbackModels ?? []).Any(Is))
                result.Add(new("Agent", agent.Name, false));
        foreach (var team in agents.ListTeams())
        {
            if (team.Members.Any(member => (member.FallbackModels ?? []).Any(Is)))
                result.Add(new("Team", team.Name, false));
            foreach (var run in agents.ListCollaborationRuns(team.Id).Where(run =>
                         run.Status is "Running" or "Queued" or "AwaitingApproval"))
                if ((run.ModelAttempts ?? []).Any(attempt => Is(attempt.ModelName)) ||
                    run.Contributions.Any(contribution => Is(contribution.ModelName)))
                    result.Add(new("Run", run.Id.ToString(), true));
            foreach (var plan in agents.ListDevelopmentPlans(team.Id).Where(plan =>
                         plan.Status is "Running" or "Pausing" or "Cancelling"))
                if (team.Members.Any(member => agents.GetAgent(member.AgentId) is { } agent && Is(agent.ModelName)) ||
                    plan.Tasks.SelectMany(task => task.ModelAttempts ?? []).Any(attempt => Is(attempt.ModelName)))
                    result.Add(new("Plan", plan.Id.ToString(), true));
        }
        foreach (var model in registry.Models.Where(model => Is(model.Name)))
            result.Add(new("Configuration", model.Id, false));
        foreach (var mission in missions?.List() ?? [])
        {
            var workflowUses = mission.Workflow?.Agents.Any(agent =>
                registry.Models.Any(model => model.Id == agent.ModelId && Is(model.Name))) == true;
            workflowUses |= mission.Workflow?.ModelSelections.Any(sequence =>
                sequence.Decision.Alternatives.Any(choice => Is(choice.Model))) == true;
            if (mission.SelectedModel is { } selected && (selected == name || registry.Models.Any(model =>
                    model.Id == selected && Is(model.Name))) || workflowUses)
                result.Add(new(mission.IsTerminal ? "History" : "Mission", mission.Id.ToString(), !mission.IsTerminal));
        }
        return result;
    }

    public async Task<ModelLibrarySnapshot> SnapshotAsync(int context, CancellationToken token)
    {
        if (context is < 512 or > ModelContextLimits.Maximum)
            throw new StudioValidationException($"Context must be 512–{ModelContextLimits.Maximum} tokens.");
        var hw = await hardware.ReadAsync(token);
        var savedProfiles = profiles.Read();
        ModelCatalogueEntry ApplyProfile(ModelCatalogueEntry entry) =>
            savedProfiles.GetValueOrDefault(entry.Name)?.Specializations is { } specializations
                ? entry with { Specializations = specializations } : entry;
        var catalogue = Catalogue().Select(ApplyProfile).ToDictionary(item => item.Name, StringComparer.Ordinal);
        try
        {
            using var tags = await ReadAsync("/api/tags", null, token);
            var installed = tags.RootElement.GetProperty("models").EnumerateArray()
                .ToDictionary(item => ValidateName(item.GetProperty("name").GetString()!),
                    item => (Size: item.GetProperty("size").GetInt64(), Digest: Digest(item)), StringComparer.Ordinal);
            var loaded = await LoadedAsync(token);
            foreach (var name in installed.Keys)
                catalogue.TryAdd(name, ApplyProfile(new(name, name.Split(':')[0], null, [],
                    "https://ollama.com/" + (name.Contains('/') ? "" : "library/") + name.Split(':')[0])));
            var models = new List<LibraryModel>();
            foreach (var entry in catalogue.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                var meta = installed.TryGetValue(entry.Name, out var identity)
                    ? await MetadataAsync(entry.Name, identity.Size, token, identity.Digest) : null;
                var resident = loaded.FirstOrDefault(item => item.Name == entry.Name);
                var profile = savedProfiles.GetValueOrDefault(entry.Name);
                models.Add(new(entry, meta is not null, meta, resident,
                    ModelMemoryEstimator.Estimate(meta, hw, context, resident, entry.DownloadBytes, profile?.Qualification),
                    Dependencies(entry.Name)) { RoutingProfile = profile });
            }

            return new(hw, models, loaded);
        }
        catch (LlmProviderException exception)
        {
            logger.LogWarning(exception, "Model library inventory could not be refreshed.");
            return new(hw, catalogue.Values.Select(entry => new LibraryModel(entry, null, null, null,
                new("Unknown", "Inventaire Ollama indisponible : installation inconnue.", context), Dependencies(entry.Name))
                { RoutingProfile = savedProfiles.GetValueOrDefault(entry.Name) }).ToArray(),
                [], exception.Message);
        }
    }

    public async Task GuardExecutionAsync(string name, int context, CancellationToken token, bool automatic = false)
    {
        using var tags = await ReadAsync("/api/tags", null, token);
        var installed = tags.RootElement.GetProperty("models").EnumerateArray().Where(item =>
            ValidateName(item.GetProperty("name").GetString()!) == ValidateName(name))
            .ToArray();
        if (installed.Length != 1) throw new LlmProviderException("Selected model is no longer installed or its inventory is ambiguous.");
        var metadata = await MetadataAsync(name, installed[0].GetProperty("size").GetInt64(), token, Digest(installed[0]));
        var loaded = (await LoadedAsync(token)).FirstOrDefault(item => item.Name == ValidateName(name));
        var estimate = ModelMemoryEstimator.Estimate(metadata, await hardware.ReadAsync(token), context, loaded,
            qualification: profiles.Read().GetValueOrDefault(ValidateName(name))?.Qualification);
        if (estimate.Status == "Insufficient") throw new LlmMemoryException(estimate.Reason);
        if (automatic && (!metadata.Completion || metadata.ContextLength is null ||
                metadata.ContextLength < context || estimate.Status == "Unknown"))
            throw new LlmContextException("Automatic model admissibility could not be verified: " + estimate.Reason);
        if (estimate.Status is "Unknown" or "Borderline")
            logger.LogWarning("Model {Model} execution estimate {Status}: {Reason}", name, estimate.Status, estimate.Reason);
    }

    public async Task<long> VerifyAsync(string name, CancellationToken token)
    {
        name = ValidateName(name);
        var parts = name.Split(':');
        var path = parts[0].Contains('/') ? parts[0] : "library/" + parts[0];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://registry.ollama.ai/v2/{path}/manifests/{parts[1]}");
        request.Headers.Accept.ParseAdd("application/vnd.docker.distribution.manifest.v2+json");
        try
        {
            using var response = await registryClient.SendAsync(request, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new StudioValidationException("This exact Ollama tag is not available in the registry.");
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token),
                cancellationToken: deadline.Token);
            return doc.RootElement.GetProperty("layers").EnumerateArray().Sum(layer => layer.GetProperty("size").GetInt64());
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        { throw new LlmTimeoutException("Registry metadata verification timed out.", exception); }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        { throw new LlmProviderException("Cannot verify this Ollama tag with the registry.", exception); }
    }

    public IReadOnlyList<ModelDownload> Downloads()
    {
        lock (sync) { Initialize(); return downloads.ToArray(); }
    }

    private void Initialize()
    {
        if (initialized) return;
        SafePaths.RejectLinks(StatePath);
        if (File.Exists(StatePath))
        {
            if (new FileInfo(StatePath).Length > 262144) throw new InvalidDataException("Download history exceeds its limit.");
            downloads.AddRange(JsonSerializer.Deserialize<ModelDownload[]>(File.ReadAllText(StatePath), Json)
                ?? throw new InvalidDataException("Invalid download history."));
            for (var index = 0; index < downloads.Count; index++)
                if (downloads[index].Status is "Running" or "Queued")
                    downloads[index] = downloads[index] with { Status = "Interrupted",
                        Error = "Studio stopped before completion. Retry explicitly to resume Ollama's cached layers." };
        }
        initialized = true;
    }

    private void Save(ModelDownload download)
    {
        lock (sync)
        {
            Initialize();
            downloads.RemoveAll(item => item.Id == download.Id);
            downloads.Insert(0, download);
            if (downloads.Count > 50) downloads.RemoveRange(50, downloads.Count - 50);
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            SafePaths.RejectLinks(StatePath);
            var temp = StatePath + ".tmp";
            SafePaths.RejectLinks(temp);
            File.WriteAllText(temp, JsonSerializer.Serialize(downloads, Json));
            File.Move(temp, StatePath, true);
        }
    }

    public ModelDownload StartDownload(string name, bool confirmed)
    {
        name = ValidateName(name);
        if (!confirmed) throw new StudioValidationException("Explicit installation confirmation is required.");
        lock (sync)
        {
            Initialize();
            if (running.Count > 0) throw new ModelOperationConflictException("Another model download is active. Cancel or wait.");
            var job = new ModelDownload(Guid.NewGuid(), name, "Queued", "Vérification du tag", null, null, DateTimeOffset.UtcNow);
            var cancel = new CancellationTokenSource();
            Save(job);
            running[job.Id] = (cancel, Task.Run(() => DownloadAsync(job, cancel.Token)));
            return job;
        }
    }

    private async Task DownloadAsync(ModelDownload job, CancellationToken token)
    {
        var acquired = false;
        try
        {
            var size = await VerifyAsync(job.Model, token);
            var hw = await hardware.ReadAsync(token);
            if (hw.FreeDiskBytes is { } free && free < size + 512L * 1048576)
                throw new StudioValidationException("Insufficient free disk for the verified model and download overhead.");
            await operations.Gate.WaitAsync(token);
            acquired = true;
            job = job with { Status = "Running", Total = size, UpdatedAt = DateTimeOffset.UtcNow };
            Save(job);
            using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/pull"))
                { Content = JsonContent.Create(new { model = job.Model, stream = true }) };
            using var response = await ollamaClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
            var successful = false;
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (line.Length > 65536) throw new InvalidDataException("Ollama progress line exceeds its limit.");
                if (line.Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error))
                    throw new LlmProviderException("Ollama pull failed: " + error.GetString());
                var status = root.TryGetProperty("status", out var progress) ? progress.GetString() ?? "" : "";
                job = job with { Progress = status[..Math.Min(status.Length, 200)],
                    Total = root.TryGetProperty("total", out var total) ? total.GetInt64() : job.Total,
                    Completed = root.TryGetProperty("completed", out var completed) ? completed.GetInt64() : job.Completed,
                    UpdatedAt = DateTimeOffset.UtcNow };
                Save(job);
                if (status == "success") successful = true;
            }
            if (!successful) throw new LlmProviderException("Ollama pull stream ended without success.");
            using var inventory = await ReadAsync("/api/tags", null, token);
            if (!inventory.RootElement.GetProperty("models").EnumerateArray().Any(item =>
                    ValidateName(item.GetProperty("name").GetString()!) == job.Model))
                throw new LlmProviderException("Ollama reported success but the model is absent from inventory.");
            job = job with { Status = "Completed", Progress = "Installé" };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { job = job with { Status = "Cancelled", Error = "Download cancelled; partial layers may remain cached by Ollama." }; }
        catch (Exception exception) when (exception is LlmProviderException or HttpRequestException or IOException or
            JsonException or StudioValidationException or UnauthorizedAccessException or KeyNotFoundException or
            InvalidOperationException or OverflowException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Model download {DownloadId} failed.", job.Id);
            job = job with { Status = "Failed", Error = exception.Message[..Math.Min(exception.Message.Length, 500)] };
        }
        finally
        {
            if (acquired) operations.Gate.Release();
            lock (sync)
            {
                try { Save(job with { UpdatedAt = DateTimeOffset.UtcNow }); }
                finally { if (running.Remove(job.Id, out var work)) work.Cancellation.Dispose(); }
            }
        }
    }

    public bool CancelDownload(Guid id)
    {
        lock (sync)
        {
            if (!running.TryGetValue(id, out var work)) return false;
            work.Cancellation.Cancel();
            return true;
        }
    }

    public async Task DeleteAsync(string name, bool confirmed, CancellationToken token)
    {
        name = ValidateName(name);
        if (!confirmed) throw new StudioValidationException("Explicit deletion confirmation is required.");
        if (!await operations.Gate.WaitAsync(0, token))
            throw new ModelOperationConflictException("A download or generation is active. Deletion is blocked.");
        try
        {
            var dependencies = Dependencies(name).Where(item => item.Kind != "History").ToArray();
            if (dependencies.Length > 0) throw new ModelOperationConflictException(
                "Explicitly reassign configured agents/teams/missions before deletion: " +
                string.Join(", ", dependencies.Take(10).Select(item => item.Kind + " " + item.Name)));
            if ((await LoadedAsync(token)).Any(item => item.Name == name))
                throw new ModelOperationConflictException("The model is still loaded in Ollama; wait for its release before deletion.");
            using var request = new HttpRequestMessage(HttpMethod.Delete, Url("/api/delete"))
                { Content = JsonContent.Create(new { model = name }) };
            using var response = await ollamaClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException exception) { throw new LlmProviderException("Ollama model deletion failed.", exception); }
        finally { operations.Gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (sync) { pending = running.Values.Select(item => item.Work).ToArray();
            foreach (var work in running.Values) work.Cancellation.Cancel(); }
        await Task.WhenAll(pending);
    }
}
