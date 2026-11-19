using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static class MissionReactTemplate
{
    public const string Id = "react-typescript-vite-v2";
    private static readonly Regex FunctionalSignals = new(
        @"\b(add|create|manage|display|show|list|search|filter|delete|remove|update|edit|mark|toggle|save|persist|store|restore|load|increment|decrement|reset|initial|task list|task manager|task app|todo list|ajouter|creer|gerer|afficher|lister|liste|rechercher|filtrer|supprimer|modifier|marquer|enregistrer|sauvegarder|restaurer|charger|terminer|conserve(?:r|e|es|ee|ees)|persist(?:ent|ed|ence)|stockage|tache|taches|gestionnaire|application|formulaire)\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex TechnicalSignals = new(
        @"\b(react|typescript|vite|localstorage|asp\.net|\.net|sqlite|frontend|backend|dependenc(?:y|ies)|dependance(?:s)?|package(?:s)?|paquet(?:s)?|service\s+externe|external\s+service)\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex NegatedActionPrefix = new(
        @"(?:\b(?:do\s+not|does\s+not|don't|doesn't|must\s+not|should\s+not|mustn't|shouldn't|never|avoid|exclude)|\bne\s+(?:pas|jamais)|\bpas)\s+(?:[\p{L}\p{N}'-]+\s+){0,1}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex TransversalSignals = new(
        @"\b(vitest|unit tests?|integration tests?|tests? (?:must|should|doivent|doit|pour|of|necessaires|necessaire)|responsive|accessib|accessibilite|gestion des erreurs|error handling|(?:blank|empty)\s+(?:input|field|title|value|text)|(?:input|field|title|value|text)\s+(?:is\s+)?(?:blank|empty)|sais(?:ie|ies)\s+vide(?:s)?|champs?\s+vides?)\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex SectionHeading = new(
        @"^\s*[\p{L}\p{N}][\p{L}\p{N}\s&/()'-]{0,79}:\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex KnownSectionHeading = new(
        @"^\s*(?:context|introduction|overview|objectives?|scope|features?|requirements?|functional requirements?|cross-cutting requirements?|technical constraints?|acceptance criteria|contexte|introduction|objectifs?|portee|fonctionnalites(?:\s+attendues)?|exigences(?:\s+(?:transversales|fonctionnelles|techniques))?|contraintes(?:\s+techniques)?|criteres(?:\s+d['']acceptation)?)\s*:?\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownHeading = new(
        @"^\s*#{1,6}\s+\S.*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ProjectIntroduction = new(
        @"^\s*(?:please\s+|veuillez\s+)?(?:create|build|develop|make|design|implement|deliver|construct|creer|cree|developper|concevoir|realiser|livrer)\b[^.!?\r\n]{0,180}\b(application|app|project|projet|site|website|tool|outil|software|logiciel|system|platform|plateforme|solution)\b",
        RegexOptions.CultureInvariant);
    public const string PlanSchema = """
        {"type":"object","properties":{
        "name":{"type":"string","maxLength":80},"description":{"type":"string","maxLength":1000},
        "decisions":{"type":"array","maxItems":8,"items":{"type":"string","maxLength":500}},
        "files":{"type":"array","minItems":2,"maxItems":12,"items":{"type":"object","properties":{
        "path":{"type":"string"},"purpose":{"type":"string","maxLength":500},
        "dependsOn":{"type":"array","items":{"type":"string"}}},"required":["path","purpose","dependsOn"],"additionalProperties":false}},
        "criterionCoverage":{"type":"array","minItems":1,"maxItems":32,"items":{"type":"object","properties":{
        "criterionId":{"type":"string"},"paths":{"type":"array","minItems":2,"maxItems":12,"items":{"type":"string"}},
        "explanation":{"type":"string","maxLength":500}},"required":["criterionId","paths","explanation"],"additionalProperties":false}}
        },"required":["name","description","decisions","files","criterionCoverage"],"additionalProperties":false}
        """;

    public static string PlanSchemaFor(ProjectSizingProfile? sizing)
    {
        if (sizing is null) return PlanSchema;
        var schema = JsonNode.Parse(PlanSchema)!;
        schema["properties"]!["files"]!["maxItems"] = sizing.MaxPlannedFiles;
        schema["properties"]!["criterionCoverage"]!["maxItems"] = sizing.MaxCriteria;
        return schema.ToJsonString();
    }

    public static string ReviewSchema(string original, ProjectSizingProfile? sizing = null)
    {
        var schema = JsonNode.Parse(original)!.AsObject();
        schema["properties"]!["criterionReviews"] = JsonNode.Parse("""
            {"type":"array","minItems":1,"maxItems":32,"items":{"type":"object","properties":{
            "criterionId":{"type":"string"},"verdict":{"type":"string","enum":["Satisfied","NotSatisfied","Unverified"]},
            "explanation":{"type":"string","minLength":1,"maxLength":1000},
            "defect":{"type":["string","null"],"maxLength":1000},
            "uncertainty":{"type":["string","null"],"maxLength":1000},
            "evidence":{"type":"array","maxItems":8,"items":{"type":"object","properties":{
            "path":{"type":"string"},"quote":{"type":"string","minLength":1,"maxLength":500}},
            "required":["path","quote"],"additionalProperties":false}}},
            "required":["criterionId","verdict","explanation","defect","uncertainty","evidence"],"additionalProperties":false}}
            """);
        schema["required"]!.AsArray().Add("criterionReviews");
        if (sizing is not null)
        {
            schema["properties"]!["criterionReviews"]!["maxItems"] = sizing.MaxCriteria;
            schema["properties"]!["findings"]!["maxItems"] = 12;
            schema["properties"]!["modifiedFiles"]!["maxItems"] = 12;
            schema["properties"]!["risks"]!["maxItems"] = 8;
        }
        return schema.ToJsonString();
    }

    public static IReadOnlyList<GeneratedFile> Files { get; } =
    [
        .. ReactTemplate.Files.Where(file => file.Path is "package.json" or "package-lock.json" or
            "tsconfig.json" or "vite.config.ts" or "eslint.config.js" or "test-setup.ts" or "src/main.tsx"),
        new("index.html", """
            <!doctype html>
            <html lang="fr"><head><meta charset="UTF-8" /><meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>React App</title></head><body><div id="root"></div><script type="module" src="/src/main.tsx"></script></body></html>
            """),
        new("src/styles/app.css", """
            :root { font-family: system-ui, sans-serif; color: #172554; background: #eff6ff; }
            * { box-sizing: border-box; }
            body { margin: 0; padding: 1rem; }
            main { max-width: 48rem; margin: 2rem auto; padding: 1.5rem; background: white; }
            button, input, select { font: inherit; margin: .25rem; padding: .5rem; }
            """),
        new("src/technical.test.tsx", """
            import { render, screen } from '@testing-library/react'
            import { expect, test } from 'vitest'
            test('React runtime renders a DOM element', () => {
              render(<div data-testid="react-runtime" />)
              expect(screen.getByTestId('react-runtime')).toBeInTheDocument()
            })
            """),
        new("README.md", """
            # React technical scaffold v2

            Fixed React, TypeScript, Vite and Vitest dependencies; no application domain is imposed.
            Mission-specific sources and tests are generated from the original requirements.
            Static review and the technical smoke test are not functional execution evidence.
            Generated code is untrusted. Execution requires the existing isolated approval workflow.
            """)
    ];

    public static bool IsEditable(string path) => AgentReactTemplate.IsAdditionalSource(path) &&
        Files.All(file => file.Path != path);

    public static string ReactVersion
    {
        get
        {
            using var manifest = JsonDocument.Parse(Files.Single(file => file.Path == "package.json").Content);
            return manifest.RootElement.GetProperty("dependencies").GetProperty("react").GetString()!;
        }
    }

    public static IReadOnlyList<WorkflowCriterion> Requirements(string request)
        => NormalizeRequirements(request).Criteria;

    public static ReactRequirementNormalization NormalizeRequirements(string request, ProjectSizingProfile? sizing = null)
    {
        if (string.IsNullOrWhiteSpace(request))
            throw new StudioValidationException("React missions require a nonblank original request.");

        var lines = request.Split('\n');
        var hasExplicitList = lines.Any(line => Regex.IsMatch(line, @"^\s*(?:[-*\u2022]|\d+[.)])\s+"));
        IReadOnlyList<(string Original, string Normalized, bool IsExplicitItem)> segments = hasExplicitList
            ? ExplicitListSegments(request)
            : ProseSegments(request);
        var classified = segments.Where(item => item.Normalized.Length > 0)
            .Select((item, index) => (item.Original, item.Normalized, Id: $"R{index + 1}",
                Categories: !item.IsExplicitItem &&
                    (hasExplicitList || Regex.IsMatch(item.Normalized, @"(?:d['’]|(?:de|to))$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) &&
                    IsContextIntroductionOrHeading(item.Normalized)
                    ? (IReadOnlyList<string>)["Context"]
                    : ClassifyRequirements(item.Normalized))).ToArray();

        var functional = classified.Where(item => item.Categories.Contains("Functional", StringComparer.Ordinal)).ToArray();
        var maximumCriteria = sizing?.MaxCriteria ?? 32;
        if (functional.Length > maximumCriteria)
            throw new StudioValidationException($"React missions support at most {maximumCriteria} functional requirements.");

        var criterionIds = functional.Select((item, index) => (item.Id, CriterionId: $"C{index + 1}"))
            .ToDictionary(item => item.Id, item => item.CriterionId, StringComparer.Ordinal);
        var criteria = functional.Select(item =>
            new WorkflowCriterion(criterionIds[item.Id], item.Normalized)).ToArray();
        var functionalIds = criteria.Select(item => item.Id).ToArray();
        var trace = classified.Select(item =>
        {
            IReadOnlyList<string> linked = item.Categories.Contains("Functional", StringComparer.Ordinal)
                ? [criterionIds[item.Id]]
                : item.Categories.Contains("Workflow", StringComparer.Ordinal)
                    ? []
                    : functionalIds;
            var diagnostic = item.Categories.Contains("Context", StringComparer.Ordinal)
                ? "Contexte conservé pour interpréter les critères ; il ne constitue pas un comportement fonctionnel indépendant."
                : item.Categories.Contains("Ambiguous", StringComparer.Ordinal)
                ? "Exigence conservée pour interprétation par le Planner ; sa catégorie ne peut pas être déterminée de manière sûre."
                : item.Categories.Contains("Workflow", StringComparer.Ordinal) &&
                  !item.Categories.Contains("Functional", StringComparer.Ordinal)
                    ? "Pris en charge par le workflow Studio ; ne pas générer de code applicatif pour cette opération."
                    : null;
            return new ReactRequirementTrace(item.Id, item.Original, item.Normalized,
                item.Categories, linked, diagnostic);
        }).ToArray();
        return new(criteria, trace);
    }

    private static IReadOnlyList<(string Original, string Normalized, bool IsExplicitItem)> ProseSegments(string request)
    {
        var segments = new List<(string Original, string Normalized, bool IsExplicitItem)>();
        // Split complete sentences, not paths, decimals, abbreviations or unmarked lines.
        foreach (var sentence in Regex.Split(request, @"(?<=[.!?])\s+(?=\p{Lu})", RegexOptions.CultureInvariant))
        {
            const string action = @"(?:ajouter|modifier|supprimer|rechercher|filtrer|afficher|enregistrer|sauvegarder|restaurer|add|edit|update|delete|remove|search|filter|display|save|restore)";
            var coordinated = Regex.Match(sentence,
                @"^(?<prefix>.*?)(?<actions>" + action + @"(?:\s*,\s*" + action + @")*\s*,?\s+(?:et|and)\s+" + action +
                @")(?<object>\s+(?:des|les|une?|la|le|du|de la|the|an?|for)\s+[^.!?\r\n]+[.!?]?)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var prefix = coordinated.Groups["prefix"].Value;
            // Only expand a positive infinitive enumeration with a proven shared object.
            if (coordinated.Success && Regex.IsMatch(prefix,
                    @"(?:permettant\s+d['’]|permet(?:tant)?\s+de\s+|(?:allows?(?:\s+users)?|enables?(?:\s+users)?)\s+to\s+|^)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                !Regex.IsMatch(sentence, @"\b(ne|pas|jamais|not|never|without|sans)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (!string.IsNullOrWhiteSpace(prefix))
                    segments.Add((prefix, prefix.Trim(), false));
                foreach (Match verb in Regex.Matches(coordinated.Groups["actions"].Value, @"\b" + action + @"\b",
                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    segments.Add((sentence, verb.Value + coordinated.Groups["object"].Value, true));
            }
            else
                segments.Add((sentence, sentence.Trim(), false));
        }
        return segments;
    }

    private static IReadOnlyList<(string Original, string Normalized, bool IsExplicitItem)> ExplicitListSegments(string request)
    {
        var segments = new List<(string Original, string Normalized, bool IsExplicitItem)>();
        var context = new List<string>();
        foreach (var line in request.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var match = Regex.Match(line, @"^\s*(?:[-*\u2022]|\d+[.)])\s+(.*)$");
            if (match.Success)
            {
                if (context.Count > 0)
                {
                    var original = string.Join("\n", context);
                    segments.Add((original, original.Trim(), false));
                    context.Clear();
                }
                segments.Add((line, match.Groups[1].Value.Trim(), true));
            }
            else
                context.Add(line);
        }
        if (context.Count > 0)
        {
            var original = string.Join("\n", context);
            segments.Add((original, original.Trim(), false));
        }
        return segments;
    }

    private static bool IsContextIntroductionOrHeading(string requirement)
    {
        var value = RemoveDiacritics(requirement).ToLowerInvariant();
        return SectionHeading.IsMatch(value) || KnownSectionHeading.IsMatch(value) ||
            MarkdownHeading.IsMatch(value) || ProjectIntroduction.IsMatch(value);
    }

    private static IReadOnlyList<string> ClassifyRequirements(string requirement)
    {
        var value = RemoveDiacritics(requirement).ToLowerInvariant();
        var categories = new List<string>();
        var testOnly = Regex.IsMatch(value,
            @"^\s*(?:add|write|create|include|ajoute[rz]?|ecrire|prevoir|inclure)\s+(?:(?:the|des|les)\s+)?(?:vitest\s+)?(?:(?:unit|integration)\s+)?tests?\b");
        var hasTechnicalTarget = TechnicalSignals.IsMatch(value);
        if (!testOnly && FunctionalSignals.Matches(value).Cast<Match>()
            .Any(match => !hasTechnicalTarget || !NegatedActionPrefix.IsMatch(value[..match.Index])))
            categories.Add("Functional");
        if (testOnly || TransversalSignals.IsMatch(value))
            categories.Add("Transversal");
        if (hasTechnicalTarget)
            categories.Add("Technical");
        if (Regex.IsMatch(value,
            @"\b(build|compile|compilation|run tests|execute tests|lancer les tests|compiler|build du|planner|reviewer|ollama|generation budget|budget de generations|workflow)\b"))
            categories.Add("Workflow");
        if (categories.Count == 0) categories.Add("Ambiguous");
        return categories;
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                result.Append(character);
        return result.ToString().Normalize(NormalizationForm.FormC);
    }

    public static ProjectPlan Prepare(ProjectPlan plan, MissionSnapshot mission)
    {
        var maximum = mission.Sizing?.MaxPlannedFiles ?? 12;
        if (plan.Files is not { Count: >= 2 } || plan.Files.Count > maximum || plan.Files.Any(file => file is null))
            throw Rejected([new(null, "InvalidFiles", "files", null, $"Provide 2 to {maximum} non-null planned files.")]);
        foreach (var file in plan.Files)
        {
            try { SafePaths.ValidateRelative(file.Path); }
            catch (StudioValidationException)
            {
                throw Rejected([new(null, "UnauthorizedPath", "files.path", file.Path, "The planned path is unsafe or unsupported.")]);
            }
            if (!IsEditable(file.Path) && Files.All(baseline => baseline.Path != file.Path))
                throw Rejected([new(null, "UnauthorizedPath", "files.path", file.Path,
                    "Only mission sources under src/ and the immutable technical scaffold are allowed.")]);
        }
        var prepared = plan with
        {
            OriginalRequest = mission.Task,
            AcceptanceCriteria = mission.Workflow!.AcceptanceCriteria,
            Files = [.. plan.Files, .. Files.Where(file => plan.Files.All(item => item.Path != file.Path))
                .Select(file => new PlannedFile(file.Path, "Immutable technical scaffold.", []))]
        };
        ValidatePlan(prepared, mission.Sizing);
        return prepared;
    }

    public static void ValidatePlan(ProjectPlan plan, ProjectSizingProfile? sizing = null)
    {
        ValidateStructure(plan, sizing);
        var errors = new List<PlannerValidationDiagnostic>();
        if (plan.Files.All(file => file.Path != "src/App.tsx"))
            errors.Add(new(null, "MissingApplication", "files", "src/App.tsx", "The React application file is required."));
        if (!plan.Files.Any(file => IsEditable(file.Path) && ReactTemplate.IsTest(file.Path)))
            errors.Add(new(null, "MissingBusinessTest", "files", null, "Plan at least one editable .test.ts or .test.tsx business test."));
        if (string.IsNullOrWhiteSpace(plan.OriginalRequest) || plan.AcceptanceCriteria is not { Count: > 0 })
            errors.Add(new(null, "MissingOriginalCriteria", "acceptanceCriteria", null, "The original request and frozen criteria are required."));
        var criteria = plan.AcceptanceCriteria ?? [];
        if (sizing is not null && criteria.Count > sizing.MaxCriteria)
            errors.Add(new(null, "TooManyCriteria", "acceptanceCriteria", null, "The frozen criterion limit was exceeded."));
        var coverages = plan.CriterionCoverage ?? [];
        if (plan.CriterionCoverage is null || coverages.Count != criteria.Count)
            errors.Add(new(null, "CoverageCountMismatch", "criterionCoverage", null, "Exactly one coverage entry per original criterion is required."));
        foreach (var criterion in criteria.Where(criterion => !coverages.Any(item => item?.CriterionId == criterion.Id)))
            errors.Add(new(criterion.Id, "MissingCoverage", "criterionCoverage", null, "This original criterion has no coverage entry."));
        foreach (var group in coverages.Where(item => item is not null).GroupBy(item => item.CriterionId).Where(group => group.Count() > 1))
            errors.Add(new(group.Key, "DuplicateCriterion", "criterionCoverage.criterionId", null, "Criterion coverage identifiers must be unique."));
        foreach (var coverage in coverages)
        {
            if (coverage is null)
            {
                errors.Add(new(null, "InvalidCoverage", "criterionCoverage", null, "A coverage entry cannot be null."));
                continue;
            }
            if (!criteria.Any(item => item.Id == coverage.CriterionId))
                errors.Add(new(coverage.CriterionId, "UnknownCriterion", "criterionCoverage.criterionId", null, "This identifier is not an original mission criterion."));
            if (string.IsNullOrWhiteSpace(coverage.Explanation) || coverage.Explanation.Length > 500)
                errors.Add(new(coverage.CriterionId, "InvalidExplanation", "criterionCoverage.explanation", null, "Provide a nonblank explanation of at most 500 characters."));
            var paths = coverage.Paths ?? [];
            if (coverage.Paths is null || paths.Count is < 1 or > 12)
                errors.Add(new(coverage.CriterionId, "InvalidPathCount", "criterionCoverage.paths", null, "Provide 1 to 12 distinct implementation and test paths."));
            foreach (var group in paths.GroupBy(path => path).Where(group => group.Count() > 1))
                errors.Add(new(coverage.CriterionId, "DuplicatePath", "criterionCoverage.paths", group.Key, "Coverage paths must not be duplicated."));
            foreach (var path in paths)
            {
                var allowed = path is not null && IsEditable(path);
                if (allowed)
                    try { SafePaths.ValidateRelative(path!); }
                    catch (StudioValidationException) { allowed = false; }
                if (!allowed)
                    errors.Add(new(coverage.CriterionId, "UnauthorizedPath", "criterionCoverage.paths", path, "Coverage must reference editable mission sources, not protected scaffold files."));
                if (plan.Files.All(file => file.Path != path))
                    errors.Add(new(coverage.CriterionId, "PathNotPlanned", "criterionCoverage.paths", path, "This coverage path is absent from the planned files."));
            }
            if (!paths.Any(path => path is not null && ReactTemplate.IsTest(path)))
                errors.Add(new(coverage.CriterionId, "MissingTest", "criterionCoverage.paths", null, "A recognized .test.ts or .test.tsx business-test path is required."));
            if (!paths.Any(path => path is not null && !ReactTemplate.IsTest(path)))
                errors.Add(new(coverage.CriterionId, "MissingImplementation", "criterionCoverage.paths", null, "An implementation path, separate from the test, is required."));
        }
        if (errors.Count > 0) throw Rejected(errors);
    }

    private static void ValidateStructure(ProjectPlan? plan, ProjectSizingProfile? sizing)
    {
        if (plan is null)
            throw Rejected([new(null, "MissingPlan", "plan", null, "A structured plan object is required.")]);
        var errors = new List<PlannerValidationDiagnostic>();
        if (string.IsNullOrWhiteSpace(plan.Name) || plan.Name.Length > 80)
            errors.Add(new(null, "InvalidName", "name", null, "Provide a nonblank name of at most 80 characters."));
        if (string.IsNullOrWhiteSpace(plan.Description) || plan.Description.Length > 1000)
            errors.Add(new(null, "InvalidDescription", "description", null, "Provide a nonblank description of at most 1000 characters."));
        if (plan.Decisions is null || plan.Decisions.Count > 8)
            errors.Add(new(null, "InvalidDecisions", "decisions", null, "Provide at most 8 nonblank decisions of at most 500 characters."));
        else
            for (var index = 0; index < plan.Decisions.Count; index++)
                if (string.IsNullOrWhiteSpace(plan.Decisions[index]) || plan.Decisions[index].Length > 500)
                    errors.Add(new(null, "InvalidDecision", $"decisions[{index}]", null, "Provide a nonblank decision of at most 500 characters."));
        if (plan.Files is not { Count: >= 2 } || plan.Files.Count > (sizing?.MaxFiles ?? 40) ||
            plan.Files.Any(file => file is null) ||
            sizing is not null && plan.Files.Count(file => IsEditable(file.Path)) > sizing.MaxPlannedFiles)
            errors.Add(new(null, "InvalidFiles", "files", null, "Provide a bounded list of non-null planned files."));
        else
        {
            foreach (var group in plan.Files.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                errors.Add(new(null, "DuplicatePlannedPath", "files.path", group.Key, "Planned paths must be unique, ignoring case."));
            for (var index = 0; index < plan.Files.Count; index++)
            {
                var file = plan.Files[index];
                try { SafePaths.ValidateRelative(file.Path); }
                catch (StudioValidationException)
                {
                    errors.Add(new(null, "UnauthorizedPath", $"files[{index}].path", file.Path, "The planned path is unsafe or unsupported."));
                }
                if (file.Path is not null && !IsEditable(file.Path) && Files.All(item => item.Path != file.Path))
                    errors.Add(new(null, "UnauthorizedPath", $"files[{index}].path", file.Path, "This path is outside the React source and technical scaffold contract."));
                if (string.IsNullOrWhiteSpace(file.Purpose) || file.Purpose.Length > 500)
                    errors.Add(new(null, "InvalidPurpose", $"files[{index}].purpose", file.Path, "Provide a nonblank purpose of at most 500 characters."));
                if (file.DependsOn is null || file.DependsOn.Count > 12)
                    errors.Add(new(null, "InvalidDependencies", $"files[{index}].dependsOn", file.Path, "Provide at most 12 declared file dependencies."));
                else
                    foreach (var dependency in file.DependsOn.Where(path => plan.Files.All(item => item.Path != path)))
                        errors.Add(new(null, "UnknownDependency", $"files[{index}].dependsOn", dependency, "The dependency is absent from the planned files."));
            }
            if (errors.Count == 0)
                try { WorkflowGraph.Validate(plan.Files.Select(file => new WorkflowStep(file.Path, "File", "Developer", file.DependsOn)).ToArray()); }
                catch (StudioValidationException)
                {
                    errors.Add(new(null, "CyclicDependencies", "files.dependsOn", null, "Planned file dependencies must be acyclic."));
                }
        }
        if (errors.Count > 0) throw Rejected(errors);
    }

    private static StudioValidationException Rejected(IReadOnlyList<PlannerValidationDiagnostic> errors) =>
        new("React Planner result rejected. Consult private Planner diagnostics for the exact rules and fields.")
        { PlannerDiagnostics = errors };

    public static void Validate(IReadOnlyList<GeneratedFile> files)
    {
        foreach (var baseline in Files)
            if (files.SingleOrDefault(file => file.Path == baseline.Path)?.Content != baseline.Content)
                throw new StudioValidationException("React technical scaffold is immutable: " + baseline.Path);
        foreach (var file in files.Where(file => Files.All(baseline => baseline.Path != file.Path)))
            if (!IsEditable(file.Path) || file.Content.Contains("@ts-ignore", StringComparison.Ordinal) ||
                file.Content.Contains("@ts-nocheck", StringComparison.Ordinal))
                throw new StudioValidationException("Only checked React sources and mission-specific tests are allowed under src/.");
        if (files.All(file => file.Path != "src/App.tsx") ||
            !files.Any(file => IsEditable(file.Path) && ReactTemplate.IsTest(file.Path)))
            throw new StudioValidationException("React application and mission-specific tests are required.");
    }

    public static void ValidateReview(ReviewReport review, MissionSnapshot mission)
    {
        var criteria = mission.Workflow!.AcceptanceCriteria;
        var verdicts = review.CriterionReviews;
        if (verdicts is null || verdicts.Count != criteria.Count || verdicts.Any(item => item is null) ||
            verdicts.Select(item => item.CriterionId).Distinct().Count() != criteria.Count)
        {
            var coverageErrors = new List<ReviewerValidationDiagnostic>
            {
                new(null, "CriterionCoverage", "criterionReviews", null, null,
                    "Provide exactly one non-null verdict for each original criterion, without duplicate IDs.")
            };
            foreach (var criterion in criteria.Where(criterion =>
                verdicts?.Any(item => item is not null && item.CriterionId == criterion.Id) != true))
                coverageErrors.Add(new(criterion.Id, "MissingCriterion", "criterionReviews", null, null,
                    "The original criterion is missing its static verdict."));
            if (verdicts is not null)
                foreach (var duplicate in verdicts.Where(item => item is not null).GroupBy(item => item.CriterionId)
                    .Where(group => group.Count() > 1))
                    coverageErrors.Add(new(duplicate.Key, "DuplicateCriterion", "criterionReviews", null, null,
                        "Provide exactly one verdict for this criterion."));
            throw new StudioValidationException("A static verdict for every original criterion is mandatory; empty findings are not acceptance.")
            { ReviewerDiagnostics = coverageErrors };
        }
        var errors = new List<ReviewerValidationDiagnostic>();
        for (var index = 0; index < verdicts.Count; index++)
        {
            var verdict = verdicts[index];
            var field = $"criterionReviews[{index}]";
            void Error(string rule, string member, string explanation, WorkflowSourceEvidence? evidence = null) =>
                errors.Add(new(verdict.CriterionId, rule, field + "." + member, evidence?.Path, evidence?.Quote, explanation));
            if (!criteria.Any(item => item.Id == verdict.CriterionId))
                Error("UnknownCriterion", "criterionId", "Use an original criterion ID.");
            if (verdict.Verdict is not ("Satisfied" or "NotSatisfied" or "Unverified"))
                Error("InvalidVerdict", "verdict", "Use Satisfied, NotSatisfied or Unverified.");
            if (string.IsNullOrWhiteSpace(verdict.Explanation) || verdict.Explanation.Length > 1000)
                Error("InvalidExplanation", "explanation", "A nonblank explanation of at most 1000 characters is required.");
            if (verdict.Verdict == "NotSatisfied" &&
                (string.IsNullOrWhiteSpace(verdict.Defect) || verdict.Defect.Length > 1000))
                Error("MissingDefect", "defect", "NotSatisfied requires a concrete, criterion-specific code defect.");
            if (verdict.Verdict == "Unverified" &&
                (string.IsNullOrWhiteSpace(verdict.Uncertainty) || verdict.Uncertainty.Length > 1000))
                Error("MissingUncertainty", "uncertainty", "Unverified requires a specific reason the criterion cannot be verified.");
            if (verdict.Evidence is null || verdict.Evidence.Count > 8)
            {
                Error("InvalidEvidence", "evidence", "Provide a non-null array of at most eight source citations.");
                continue;
            }
            var coverage = mission.Workflow.Plan?.CriterionCoverage?
                .SingleOrDefault(item => item.CriterionId == verdict.CriterionId);
            if (coverage is null)
                Error("MissingCriterionCoverage", "evidence", "The criterion must have validated implementation and test paths.");
            if (verdict.Verdict == "Satisfied" && verdict.Evidence.Count == 0)
                Error("MissingCitation", "evidence", "Satisfied requires exact static implementation and criterion-test evidence.");
            for (var citation = 0; citation < verdict.Evidence.Count; citation++)
            {
                var evidence = verdict.Evidence[citation];
                var member = $"evidence[{citation}]";
                if (evidence is null || string.IsNullOrWhiteSpace(evidence.Quote))
                {
                   Error("MissingQuote", member + ".quote", "A nonblank verbatim source quote is required.", evidence);
                   continue;
                }
                if (evidence.Quote.Length > 500)
                   Error("QuoteTooLong", member + ".quote", "A quote must not exceed 500 characters.", evidence);
                var source = mission.Workflow.Draft!.FirstOrDefault(file => file.Path == evidence.Path);
                if (source is null)
                   Error("UnknownPath", member + ".path", "The path must exactly match a supplied source path.", evidence);
                else if (!source.Content.Contains(evidence.Quote, StringComparison.Ordinal))
                   Error("QuoteMismatch", member + ".quote",
                       "Copy an exact substring: case, spaces and newlines count; do not add line numbers.", evidence);
                if (coverage is not null && !coverage.Paths.Contains(evidence.Path, StringComparer.Ordinal))
                    Error("EvidenceNotPlannedForCriterion", member + ".path",
                        "This exact path was not assigned to the criterion by the validated plan.", evidence);
            }
            var implementationEvidence = verdict.Evidence.Where(evidence => evidence is not null &&
                evidence.Path is not null && !ReactTemplate.IsTest(evidence.Path) &&
                coverage?.Paths.Contains(evidence.Path, StringComparer.Ordinal) == true).ToArray();
            var criterionTestEvidence = verdict.Evidence.Where(evidence => evidence is not null &&
                evidence.Path is not null && ReactTemplate.IsTest(evidence.Path) &&
                coverage?.Paths.Contains(evidence.Path, StringComparer.Ordinal) == true &&
                !string.IsNullOrEmpty(evidence.Quote) &&
                evidence.Quote.Contains($"[{verdict.CriterionId}]", StringComparison.Ordinal)).ToArray();
            if (verdict.Verdict == "Satisfied" && implementationEvidence.Length == 0)
                Error("MissingImplementationEvidence", "evidence",
                   "Satisfied requires exact evidence from this criterion's planned implementation.");
            if ((verdict.Verdict is "Satisfied" or "NotSatisfied") && criterionTestEvidence.Length == 0)
                Error("MissingCriterionTestEvidence", "evidence",
                    $"{verdict.Verdict} requires an exact citation from a planned test explicitly tagged for this criterion.");
            if (verdict.Verdict == "NotSatisfied" && implementationEvidence.Length == 0)
                Error("MissingImplementationEvidence", "evidence",
                    "NotSatisfied requires exact evidence from the criterion's planned implementation.");
        }
        if (errors.Count > 0)
            throw new StudioValidationException("Reviewer criterion evidence rejected. Consult private Reviewer diagnostics.")
            { ReviewerDiagnostics = errors };
    }

    public static IReadOnlyList<WorkflowCriterionExecution> Executions(MissionSnapshot mission,
        VerificationResult result, string fingerprint)
    {
        if (result.Stage != "Tests" || result.Status != "Succeeded" || result.ExitCode != 0 ||
            ReactBuildProfile.TestCounts(result.Log) is null)
            return [];
        const string begin = "\nAIPLATFORM_VITEST_BEGIN\n";
        const string end = "\nAIPLATFORM_VITEST_END\n";
        var start = result.Log.LastIndexOf(begin, StringComparison.Ordinal) + begin.Length;
        var finish = result.Log.LastIndexOf(end, StringComparison.Ordinal);
        using var report = JsonDocument.Parse(result.Log[start..finish]);
        var records = new List<WorkflowCriterionExecution>();
        foreach (var suite in report.RootElement.GetProperty("testResults").EnumerateArray())
        {
            if (!suite.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
            var file = name.GetString()!.Replace('\\', '/');
            foreach (var assertion in suite.GetProperty("assertionResults").EnumerateArray())
            {
                if (!assertion.TryGetProperty("fullName", out var fullName) || fullName.ValueKind != JsonValueKind.String) continue;
                var testName = fullName.GetString()!;
                foreach (var coverage in mission.Workflow!.Plan!.CriterionCoverage!)
                {
                    var path = coverage.Paths.FirstOrDefault(path => ReactTemplate.IsTest(path) &&
                        (file == path || file == "/work/" + path));
                    if (path is not null && testName.Contains($"[{coverage.CriterionId}]", StringComparison.Ordinal) &&
                        mission.Workflow.Draft!.Any(source => source.Path == path &&
                            source.Content.Contains($"[{coverage.CriterionId}]", StringComparison.Ordinal)))
                        records.Add(new(coverage.CriterionId, path, testName, result.Attempt,
                            mission.Workflow.Version, fingerprint, "Authorized offline Docker / Vitest JSON runner report"));
                }
            }
        }
        return records;
    }
}
