using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed record CustomAgentStaticValidationResult(
    string Status, IReadOnlyList<CustomAgentValidationDiagnostic> Diagnostics);

public static partial class CustomAgentProposalValidator
{
    private static readonly string[] ModuleExtensions = [".ts", ".tsx", ".js", ".jsx", ".json", ".css"];

    public static CustomAgentStaticValidationResult Validate(
        string? templateId,
        IReadOnlyList<CustomAgentWorkspaceFile> snapshotFiles,
        IReadOnlyList<string> workspacePaths,
        IReadOnlyList<CustomAgentFileChange> changes,
        IProjectAnalyzer? projectAnalyzer,
        CancellationToken cancellationToken)
    {
        var contents = snapshotFiles.ToDictionary(file => file.Path, file => file.Content,
            StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(workspacePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshotFiles)
            paths.Add(file.Path);
        ApplyChanges(contents, paths, changes);

        var diagnostics = new List<CustomAgentValidationDiagnostic>();
        var supported = templateId is AgentReactTemplate.Id or ReactTemplate.Id or AgentDotNetTemplate.Id ||
            paths.Contains("package.json") || paths.Any(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (!supported)
        {
            Add(diagnostics, new(".", "STATIC_PROJECT_UNSUPPORTED",
                "Aucun scaffold React/TypeScript ou .NET reconnaissable; la validation statique n’est pas disponible.",
                "Warning"));
            return new("Unavailable", diagnostics);
        }

        IEnumerable<string> expectedFiles = templateId switch
        {
            AgentReactTemplate.Id => AgentReactTemplate.Files.Select(file => file.Path),
            ReactTemplate.Id => ReactTemplate.Files.Select(file => file.Path),
            AgentDotNetTemplate.Id => AgentDotNetTemplate.Files.Select(file => file.Path),
            _ => Array.Empty<string>()
        };
        foreach (var expected in expectedFiles)
            if (!paths.Contains(expected))
                Add(diagnostics, new(expected, "SCAFFOLD_FILE_MISSING",
                    $"Le fichier requis par le scaffold est absent : {expected}."));

        if (templateId is AgentReactTemplate.Id or ReactTemplate.Id)
        {
            foreach (var path in paths.Where(path => !ReactTemplate.IsPath(path) &&
                         templateId == AgentReactTemplate.Id && !AgentReactTemplate.IsAdditionalSource(path)))
                Add(diagnostics, new(path, "REACT_PATH_NOT_ALLOWED",
                    "Le scaffold React n’autorise que les fichiers supplémentaires .ts, .tsx et .css sous src/."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (paths.Any(path => IsSourceFile(path) && !contents.ContainsKey(path)))
            Add(diagnostics, new(".", "STATIC_SOURCE_OMITTED",
                "Un ou plusieurs fichiers source ont été omis de l’inventaire par les limites de taille; leurs imports n’ont pas été vérifiés.",
                "Warning"));

        if (projectAnalyzer is not null && contents.Count > 0)
        {
            var analysis = projectAnalyzer.Analyze(
                contents.Select(item => new AnalysisFile(item.Key, item.Value)).ToArray(), null, cancellationToken);
            foreach (var issue in analysis.Issues)
                Add(diagnostics, new(issue.Path, "PROJECT_MANIFEST_INVALID", issue.Message));
        }

        ValidateLocalImports(contents, paths, diagnostics, cancellationToken);
        ValidateProjectReferences(contents, paths, diagnostics, cancellationToken);

        var bounded = diagnostics.Take(CustomAgentLimits.MaxValidationDiagnostics).ToArray();
        return new(bounded.Any(item => item.Severity == "Error") ? "Errors" :
            bounded.Any(item => item.Severity == "Warning") ? "Warnings" : "Passed", bounded);
    }

    private static void ApplyChanges(
        Dictionary<string, string> contents, HashSet<string> paths,
        IReadOnlyList<CustomAgentFileChange> changes)
    {
        foreach (var change in changes)
        {
            var existing = paths.FirstOrDefault(path =>
                string.Equals(path, change.Path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                paths.Remove(existing);
                contents.Remove(existing);
            }
            if (change.Operation == "delete")
                continue;
            paths.Add(change.Path);
            if (change.After is not null)
                contents[change.Path] = change.After;
        }
    }

    private static void ValidateLocalImports(
        IReadOnlyDictionary<string, string> contents, HashSet<string> paths,
        List<CustomAgentValidationDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        foreach (var file in contents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsImportSource(file.Key)) continue;
            foreach (Match match in LocalImportPattern().Matches(file.Value))
            {
                var specifier = match.Groups["path"].Value;
                var target = ResolveRelativePath(file.Key, specifier);
                if (target is null)
                {
                    Add(diagnostics, new(file.Key, "LOCAL_IMPORT_OUTSIDE_WORKSPACE",
                        "Un import relatif sort du workspace ou contient un chemin non pris en charge.", RelatedPath: specifier));
                    continue;
                }
                if (!ResolvesModule(target, paths))
                    Add(diagnostics, new(file.Key, "LOCAL_IMPORT_MISSING",
                        $"L’import local « {specifier} » ne correspond à aucun fichier du workspace.",
                        RelatedPath: target));
            }
        }
    }

    private static void ValidateProjectReferences(
        IReadOnlyDictionary<string, string> contents, HashSet<string> paths,
        List<CustomAgentValidationDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        foreach (var project in contents.Where(item =>
                     item.Key.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var reader = XmlReader.Create(new StringReader(project.Value), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 1_048_576
                });
                var document = XDocument.Load(reader);
                foreach (var reference in document.Descendants()
                             .Where(element => element.Name.LocalName == "ProjectReference"))
                {
                    var include = reference.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Include")?.Value;
                    if (string.IsNullOrWhiteSpace(include)) continue;
                    var target = ResolveProjectReference(project.Key, include);
                    if (target is null || !paths.Contains(target))
                        Add(diagnostics, new(project.Key, "DOTNET_PROJECT_REFERENCE_MISSING",
                            "La référence ProjectReference ne correspond à aucun projet du workspace.",
                            RelatedPath: target ?? include));
                }
            }
            catch (XmlException)
            {
                // ProjectAnalyzer reports malformed project XML as a static manifest issue.
            }
        }
    }

    private static string? ResolveRelativePath(string sourcePath, string specifier)
    {
        if (specifier.Contains('\\') || specifier.Contains(':')) return null;
        var baseSegments = sourcePath.Split('/').SkipLast(1).ToList();
        foreach (var segment in specifier.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (baseSegments.Count == 0) return null;
                baseSegments.RemoveAt(baseSegments.Count - 1);
                continue;
            }
            baseSegments.Add(segment);
        }
        return baseSegments.Count == 0 ? null : string.Join('/', baseSegments);
    }

    private static string? ResolveProjectReference(string projectPath, string include)
    {
        var normalized = include.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':')) return null;
        return ResolveRelativePath(projectPath, normalized);
    }

    private static bool ResolvesModule(string target, HashSet<string> paths)
    {
        if (paths.Contains(target)) return true;
        if (Path.HasExtension(target))
        {
            var extension = Path.GetExtension(target);
            if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase))
            {
                var stem = target[..^extension.Length];
                return paths.Contains(stem + ".ts") || paths.Contains(stem + ".tsx");
            }
            return false;
        }
        return ModuleExtensions.Any(extension => paths.Contains(target + extension)) ||
            ModuleExtensions.Any(extension => paths.Contains($"{target}/index{extension}"));
    }

    private static bool IsSourceFile(string path) =>
        IsImportSource(path) || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static bool IsImportSource(string path) =>
        Path.GetExtension(path) is ".ts" or ".tsx" or ".js" or ".jsx";

    private static void Add(
        List<CustomAgentValidationDiagnostic> diagnostics, CustomAgentValidationDiagnostic diagnostic)
    {
        if (diagnostics.Count < CustomAgentLimits.MaxValidationDiagnostics && !diagnostics.Contains(diagnostic))
            diagnostics.Add(diagnostic);
    }

    [GeneratedRegex(
        @"\b(?:import|export)\s+(?:type\s+)?[^'"";]*?\bfrom\s*['""](?<path>\.{1,2}/[^'""]+)['""]|\bimport\s*['""](?<path>\.{1,2}/[^'""]+)['""]|\bimport\s*\(\s*['""](?<path>\.{1,2}/[^'""]+)['""]",
        RegexOptions.CultureInvariant)]
    private static partial Regex LocalImportPattern();
}
