using System.Text.Json;
using System.Text.RegularExpressions;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class LocalModelRouter(IModelLibraryService library, ICustomAgentStore store, StudioOptions options,
    IMissionService? missions = null) : IModelRouter
{
    private readonly object sync = new();
    private string ResetFile => Path.Combine(SafePaths.ValidateStorageDirectory(options.DataDirectory), "model-ranking-reset.json");

    public static string Classify(string task, string role)
    {
        var text = task.ToLowerInvariant();
        if (role is "Planner" or "Plan") return "Planning";
        if (role is "Reviewer" or "Review") return "Review";
        if (Regex.IsMatch(text, @"\b(ts|cs)\d{4,5}\b|compil|debug|débog|rollback")) return "Debugging";
        if (Regex.IsMatch(text, @"tests?\s+(fail|échou)|correction.*test")) return "Tests";
        if (role == "Backend") return "DotNet";
        if (role == "Frontend") return "ReactTypeScript";
        if (Regex.IsMatch(text, @"vitest|xunit|assert|tests?\s+(fail|échou)|correction.*test")) return "Tests";
        if (text.Contains("architect")) return "Architecture";
        if (Regex.IsMatch(text, @"readme|documentation|documenter")) return "Documentation";
        if (Regex.IsMatch(text, @"react|typescript|\.tsx?\b")) return "ReactTypeScript";
        if (Regex.IsMatch(text, @"c#|\.net|asp\.net|\.cs\b")) return "DotNet";
        if (Regex.IsMatch(text, @"génér|generat|projet|project")) return "Project";
        return "General";
    }

    public IReadOnlyList<CustomAgentModelAttempt> Observations()
    {
        lock (sync)
        {
            SafePaths.RejectLinks(ResetFile);
            var since = File.Exists(ResetFile) ? JsonSerializer.Deserialize<DateTimeOffset>(File.ReadAllText(ResetFile)) :
                DateTimeOffset.MinValue;
            return store.ListTeams().SelectMany(team =>
                    store.ListCollaborationRuns(team.Id).SelectMany(run => run.ModelAttempts ?? []).Concat(
                        store.ListDevelopmentPlans(team.Id).SelectMany(plan => plan.Tasks)
                            .SelectMany(task => task.ModelAttempts ?? [])))
                .Concat((missions?.List() ?? []).SelectMany(mission =>
                    mission.Workflow?.ModelInvocations.Select(item => item.Attempt) ?? []))
                .GroupBy(attempt => attempt.Id).Select(group => group.Last())
                .Where(attempt => attempt.StartedAt >= since).ToArray();
        }
    }

    public void ResetStatistics()
    {
        lock (sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResetFile)!);
            SafePaths.RejectLinks(ResetFile);
            SafePaths.RejectLinks(ResetFile + ".tmp");
            File.WriteAllText(ResetFile + ".tmp", JsonSerializer.Serialize(DateTimeOffset.UtcNow));
            File.Move(ResetFile + ".tmp", ResetFile, true);
        }
    }

    public async Task<ModelRoutingDecision> SelectAsync(string task, string role, int contextTokens, CancellationToken token)
    {
        var snapshot = await library.SnapshotAsync(contextTokens, token);
        if (snapshot.OllamaError is not null)
            throw new AIPlatform.Core.LlmProviderException("Automatic routing requires a verified Ollama inventory.");
        var category = Classify(task, role);
        var observations = Observations();
        return Rank(category, contextTokens, snapshot.Models, observations);
    }

    public static ModelRoutingDecision Rank(string category, int context, IReadOnlyList<LibraryModel> models,
        IReadOnlyList<CustomAgentModelAttempt> observations)
    {
        var coding = category is "DotNet" or "ReactTypeScript" or "Project";
        var hasCodeModel = coding && models.Any(model => model.Installed == true &&
            model.Metadata?.Completion == true && model.Metadata.ContextLength >= context &&
            model.Catalogue.Specializations.Contains("Code"));
        var choices = models.Where(model => model.Installed == true && model.Metadata?.Completion == true &&
            (!hasCodeModel || model.Catalogue.Specializations.Contains("Code")) &&
            model.Metadata.ContextLength >= context && model.Compatibility.Status is "Compatible" or "Borderline")
            .Select(model =>
            {
                var specializations = model.Catalogue.Specializations;
                var desired = category is "DotNet" or "ReactTypeScript" or "Project" ? "Code" : category;
                var specialized = specializations.Contains(desired);
                var codeAlternative = desired is "Debugging" or "Tests" or "Review" or "Architecture" &&
                    specializations.Contains("Code");
                var history = observations.Where(attempt => attempt.ModelName == model.Catalogue.Name &&
                    attempt.TaskCategory == category && (attempt.FailureKind == "Generation" ||
                        attempt.Outcome == "Succeeded" && (attempt.Role != "Developer" ||
                            attempt.TestsStatus == "Passed" || attempt.BrowserStatus == "Passed"))).ToArray();
                var performance = history.Length < 3 ? 0 : (int)Math.Round(
                    8.0 * (history.Count(attempt => attempt.Outcome == "Succeeded") -
                        history.Count(attempt => attempt.FailureKind == "Generation")) / (history.Length + 4));
                var durations = history.Where(attempt => attempt.CompletedAt > attempt.StartedAt)
                    .Select(attempt => (attempt.CompletedAt!.Value - attempt.StartedAt).TotalSeconds).ToArray();
                var latencyPenalty = durations.Length >= 3 && durations.Average() > 120 ? 2 : 0;
                var score = (specialized ? 30 : codeAlternative ? 20 : 0) +
                    (model.Compatibility.Status == "Compatible" ? 12 : 0) +
                    (model.Loaded is not null ? 3 : 0) + performance - latencyPenalty;
                return new ModelRoutingChoice(model.Catalogue.Name, score,
                    $"{(specialized ? "Spécialisation déclarée" : codeAlternative ? "Spécialisation code" : "Spécialisation non documentée")} ; " +
                    $"{model.Compatibility.Status} ; {history.Length} observations comparables" +
                    (history.Length < 3 ? " (classement non influencé)." : " (influence limitée ; compilation ≠ validation fonctionnelle).") +
                    (model.Loaded is not null ? " Déjà chargé." : " Coût de chargement à prévoir.") +
                    (durations.Length >= 3 ? $" Durée moyenne observée : {durations.Average():F0} s." : ""),
                    model.Compatibility);
            }).OrderByDescending(choice => choice.Score)
            .ThenBy(choice => choice.Model, StringComparer.Ordinal).Take(9).ToArray();
        if (choices.Length == 0)
            throw new StudioValidationException(
                (hasCodeModel ? "Aucun modèle spécialisé code admissible ; ne pas remplacer silencieusement le Developer par un modèle généraliste. " : "") +
                "Aucun modèle installé admissible : vérifier capacités, contexte et mémoire. Les informations inconnues ne prouvent pas la compatibilité.");
        return new(choices[0].Model, category,
            $"Classification heuristique {category}, non garantie. {choices[0].Reason}",
            choices[0].Reason.StartsWith("Spécialisation déclarée", StringComparison.Ordinal) &&
                choices[0].Compatibility.Status == "Compatible" ? "Moderate" : "Low", choices);
    }
}
