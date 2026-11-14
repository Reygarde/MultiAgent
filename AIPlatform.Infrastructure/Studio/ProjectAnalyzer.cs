using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed partial class ProjectAnalyzer(StudioOptions options) : IProjectAnalyzer
{
    public ProjectAnalysis Analyze(IReadOnlyList<AnalysisFile> files, bool? gitRepositoryDetected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        cancellationToken.ThrowIfCancellationRequested();
        if (files.Count > options.MaxImportFiles)
            throw new StudioValidationException("Project inventory exceeds the configured file limit.");

        var languages = new HashSet<string>(StringComparer.Ordinal);
        var frameworks = new HashSet<string>(StringComparer.Ordinal);
        var solutions = new List<string>();
        var projects = new List<string>();
        var manifests = new List<string>();
        var tests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryPoints = new List<string>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new List<ProjectDependency>();
        var scripts = new List<ProjectScript>();
        var docker = new List<string>();
        var issues = new List<AnalysisIssue>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null)
                throw new StudioValidationException("Project inventory contains an invalid file.");
            ValidatePath(file.Path);
            if (!paths.Add(file.Path) || file.Content is null || file.Content.Contains('\0'))
                throw new StudioValidationException("Project inventory contains duplicate paths or binary content.");
            var size = Encoding.UTF8.GetByteCount(file.Content);
            totalBytes += size;
            if (size > options.MaxImportFileBytes || totalBytes > options.MaxImportBytes)
                throw new StudioValidationException("Project inventory exceeds the configured size limits.");

            var segments = file.Path.Split('/');
            for (var index = 1; index < segments.Length; index++)
                directories.Add(string.Join('/', segments.Take(index)));
            var name = segments[^1];
            var extension = Path.GetExtension(name).ToLowerInvariant();
            var language = extension switch
            {
                ".cs" => "C#", ".ts" or ".tsx" => "TypeScript", ".js" or ".jsx" => "JavaScript",
                ".html" => "HTML", ".css" => "CSS", _ => null
            };
            if (language is not null) languages.Add(language);
            if (extension is ".sln" or ".slnx") solutions.Add(file.Path);
            if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("docker-compose.yaml", StringComparison.OrdinalIgnoreCase))
                docker.Add(file.Path);
            if (name.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Startup.cs", StringComparison.OrdinalIgnoreCase) ||
                name is "main.tsx" or "main.ts" or "main.jsx" or "main.js")
                entryPoints.Add(file.Path);

            if (extension == ".csproj")
            {
                projects.Add(file.Path);
                AnalyzeDotNet(file, frameworks, tests, dependencies, issues);
            }
            else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
            {
                manifests.Add(file.Path);
                AnalyzePackage(file, frameworks, dependencies, scripts, issues);
            }
        }

        static string[] Sorted(IEnumerable<string> values) => values.Order(StringComparer.Ordinal).ToArray();
        return new(gitRepositoryDetected, Sorted(languages), Sorted(frameworks), Sorted(solutions), Sorted(projects),
            Sorted(manifests), Sorted(tests), Sorted(entryPoints), Sorted(directories),
            dependencies.OrderBy(item => item.Manifest, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            scripts.OrderBy(item => item.Manifest, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            Sorted(docker), issues.ToArray());
    }

    private static void AnalyzeDotNet(AnalysisFile file, HashSet<string> frameworks, HashSet<string> tests,
        List<ProjectDependency> dependencies, List<AnalysisIssue> issues)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(file.Content), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1048576
            });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName != "Project")
            {
                issues.Add(new(file.Path, "Invalid project XML root."));
                return;
            }
            if (document.Root.Attribute("Sdk")?.Value == "Microsoft.NET.Sdk.Web")
                frameworks.Add("ASP.NET Core");
            foreach (var target in document.Descendants().Where(element =>
                         element.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
                foreach (var value in target.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    if (TargetFramework().IsMatch(value)) frameworks.Add(value);
            if (document.Descendants().Any(element => element.Name.LocalName == "IsTestProject" &&
                element.Value.Equals("true", StringComparison.OrdinalIgnoreCase)))
                tests.Add(file.Path);
            foreach (var package in document.Descendants().Where(element => element.Name.LocalName == "PackageReference"))
            {
                var name = package.Attribute("Include")?.Value ?? package.Attribute("Update")?.Value;
                var version = package.Attribute("Version")?.Value ??
                    package.Elements().FirstOrDefault(element => element.Name.LocalName == "Version")?.Value;
                if (!ValidName(name)) { issues.Add(new(file.Path, "A package name could not be reported safely.")); continue; }
                dependencies.Add(new("NuGet", name!, ValidVersion(version), file.Path));
                if (name!.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
                    tests.Add(file.Path);
            }
        }
        catch (XmlException)
        {
            issues.Add(new(file.Path, "Project XML could not be parsed safely."));
        }
    }

    private static void AnalyzePackage(AnalysisFile file, HashSet<string> frameworks,
        List<ProjectDependency> dependencies, List<ProjectScript> scripts, List<AnalysisIssue> issues)
    {
        try
        {
            using var document = JsonDocument.Parse(file.Content, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new(file.Path, "Package manifest must be a JSON object."));
                return;
            }
            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
            {
                if (!document.RootElement.TryGetProperty(section, out var packages)) continue;
                if (packages.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new(file.Path, "A dependency section is not an object."));
                    continue;
                }
                foreach (var package in packages.EnumerateObject())
                {
                    if (!ValidName(package.Name) || package.Value.ValueKind != JsonValueKind.String)
                    {
                        issues.Add(new(file.Path, "A package entry could not be reported safely."));
                        continue;
                    }
                    dependencies.Add(new("npm", package.Name, ValidVersion(package.Value.GetString()), file.Path));
                    if (package.Name == "react") frameworks.Add("React");
                    if (package.Name == "vite") frameworks.Add("Vite");
                }
            }
            if (document.RootElement.TryGetProperty("scripts", out var commands))
            {
                if (commands.ValueKind != JsonValueKind.Object)
                    issues.Add(new(file.Path, "The scripts section is not an object."));
                else
                    foreach (var command in commands.EnumerateObject())
                        if (ValidName(command.Name)) scripts.Add(new(file.Path, command.Name));
                        else issues.Add(new(file.Path, "A script name could not be reported safely."));
            }
        }
        catch (JsonException)
        {
            issues.Add(new(file.Path, "Package manifest JSON could not be parsed safely."));
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Contains('\\') ||
            path.Contains(':') || path.StartsWith('/') || path.Any(char.IsControl))
            throw new StudioValidationException("Project inventory contains an unsafe path.");
        var segments = path.Split('/');
        if (segments.Length > 32 || segments.Any(segment => string.IsNullOrWhiteSpace(segment) ||
            segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || DeviceName().IsMatch(segment)))
            throw new StudioValidationException("Project inventory contains an unsafe path.");
        if (ProjectFilePolicy.IsExcluded(path))
            throw new StudioValidationException("Project inventory contains an excluded path.");
    }

    private static bool ValidName(string? value) => value is { Length: > 0 and <= 200 } && PackageName().IsMatch(value);
    private static string? ValidVersion(string? value) =>
        value is { Length: > 0 and <= 100 } && Version().IsMatch(value) ? value : null;

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();
    [GeneratedRegex(@"^[@A-Za-z0-9_./-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageName();
    [GeneratedRegex(@"^[A-Za-z0-9*^~<>=,.\[\]()+ -]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Version();
    [GeneratedRegex(@"^net(?:standard|coreapp)?[0-9]+(?:\.[0-9]+)?(?:-[A-Za-z0-9.]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetFramework();
}
