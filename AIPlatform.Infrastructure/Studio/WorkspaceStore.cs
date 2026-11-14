using System.Text;
using System.Xml;
using System.Xml.Linq;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class WorkspaceStore(StudioOptions options) : IWorkspaceStore
{
    private readonly string root = Path.Combine(SafePaths.ValidateStorageDirectory(options.DataDirectory), "workspaces");

    public void Create(Guid id)
    {
        var path = GetDirectory(id);
        if (Directory.Exists(path))
            throw new StudioValidationException("This workspace already exists.");
        Directory.CreateDirectory(path);
        SafePaths.RejectLinks(path);
    }

    public string GetDirectory(Guid id)
    {
        var path = Path.Combine(root, id.ToString("N"));
        SafePaths.RejectLinks(path);
        return path;
    }

    public void Delete(Guid id)
    {
        var path = GetDirectory(id);
        var files = List(id);
        foreach (var file in files)
        {
            var fullPath = Path.Combine(path, file.Path.Replace('/', Path.DirectorySeparatorChar));
            SafePaths.RejectLinks(fullPath);
            File.Delete(fullPath);
        }
        RemoveEmpty(path);
        static void RemoveEmpty(string directory)
        {
            SafePaths.RejectLinks(directory);
            foreach (var child in Directory.EnumerateDirectories(directory))
                RemoveEmpty(child);
            Directory.Delete(directory, recursive: false);
        }
    }

    public void ValidateFiles(
        IReadOnlyList<GeneratedFile> files, string? templateId = null, bool allowPartialTemplate = false)
    {
        if (templateId is not null && templateId != DiscordTemplate.Id && templateId != ReactTemplate.Id &&
            templateId != TwitchTemplate.Id && templateId != FullStackTemplate.Id)
            throw new StudioValidationException("Unknown project template.");
        if (files.Count is < 3 || files.Count > options.MaxFiles)
            throw new StudioValidationException("The project file count exceeds configured limits.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var file in files)
        {
            SafePaths.ValidateRelative(file.Path);
            if (!names.Add(file.Path) || string.IsNullOrWhiteSpace(file.Content) || file.Content.Contains('\0'))
                throw new StudioValidationException("Duplicate paths or empty/binary contents are not allowed.");
            var size = Encoding.UTF8.GetByteCount(file.Content);
            total += size;
            var trustedLockfile = templateId == ReactTemplate.Id && file.Path == "package-lock.json" ||
                templateId == FullStackTemplate.Id && file.Path == "frontend/package-lock.json";
            if (size > (trustedLockfile ? ReactTemplate.MaxLockfileBytes : options.MaxFileBytes) ||
                total > Math.Min(options.MaxProjectBytes, templateId == FullStackTemplate.Id
                    ? FullStackTemplate.MaxProjectBytes : options.MaxProjectBytes))
                throw new StudioValidationException("Generated project size exceeds configured limits.");
            if (templateId is not null && !ReactTemplate.IsTrustedLockfile(file.Path, file.Content) &&
                !FullStackTemplate.IsTrustedLockfile(file.Path, file.Content) &&
                ProjectExport.ContainsPotentialSecret(file.Content))
                throw new StudioValidationException("A possible credential was detected in project file: " + file.Path);
            if (file.Path.EndsWith(".csproj", StringComparison.Ordinal))
                ValidateProject(file, templateId);
        }
        if (templateId == ReactTemplate.Id)
        {
            ReactTemplate.Validate(files);
            if (files.Where(file => ReactTemplate.IsEditable(file.Path)).Any(file =>
                file.Content.Contains("@ts-nocheck", StringComparison.Ordinal) ||
                file.Content.Contains("@ts-ignore", StringComparison.Ordinal)))
                throw new StudioValidationException("Generated React sources must not disable type checking.");
            return;
        }
        if (templateId == FullStackTemplate.Id)
        {
            if (allowPartialTemplate)
                FullStackTemplate.ValidatePartial(files);
            else
                FullStackTemplate.Validate(files);
            return;
        }
        if (allowPartialTemplate)
            throw new StudioValidationException("Partial template validation is available only during full-stack generation.");
        if (files.Any(file => ReactTemplate.IsPath(file.Path) && file.Path != "README.md"))
            throw new StudioValidationException("React files require the React template.");
        if (templateId == TwitchTemplate.Id) TwitchTemplate.Validate(files);
        foreach (var required in new[] { "src/App/App.csproj", "src/App/Program.cs", "tests/App.Tests/App.Tests.csproj" })
            if (!names.Contains(required))
                throw new StudioValidationException($"Required project file is missing: {required}.");
        if (!files.Any(file => file.Path.StartsWith("tests/App.Tests/", StringComparison.Ordinal) &&
                               file.Path.EndsWith(".cs", StringComparison.Ordinal)))
            throw new StudioValidationException("At least one test source file is required.");
    }

    private static void ValidateProject(GeneratedFile file, string? templateId)
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
            if (document.Root?.Name != "Project" ||
                !document.Descendants("TargetFramework").Any(element => element.Value == "net10.0"))
                throw new StudioValidationException("Every generated project must target net10.0.");
            if (document.Root.Attributes().Any(attribute => attribute.Name != "Sdk") ||
                document.Root.Attribute("Sdk")?.Value is not ("Microsoft.NET.Sdk" or "Microsoft.NET.Sdk.Web"))
                throw new StudioValidationException("Only the standard .NET or ASP.NET SDK is allowed.");
            var properties = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["TargetFramework"] = ["net10.0"],
                ["ImplicitUsings"] = ["enable"],
                ["Nullable"] = ["enable"],
                ["IsTestProject"] = ["true"],
                ["IsPackable"] = ["false"],
                ["OutputType"] = ["Exe", "Library"],
                ["RestorePackagesWithLockFile"] = ["true"]
            };
            foreach (var group in document.Root.Elements())
            {
                if (group.HasAttributes || (group.Name != "PropertyGroup" && group.Name != "ItemGroup"))
                    throw new StudioValidationException("Custom MSBuild targets, imports and conditions are not allowed.");
                foreach (var element in group.Elements())
                {
                    if (group.Name == "PropertyGroup")
                    {
                        if (element.HasAttributes || element.HasElements ||
                            !properties.TryGetValue(element.Name.ToString(), out var values) ||
                            !values.Contains(element.Value, StringComparer.Ordinal))
                            throw new StudioValidationException("A generated project contains an unauthorized MSBuild property.");
                    }
                    else if (element.Name == "PackageReference")
                    {
                        var allowed = new Dictionary<string, string>
                        {
                            ["xunit"] = "2.9.3",
                            ["xunit.runner.visualstudio"] = "3.1.4",
                            ["Microsoft.NET.Test.Sdk"] = "17.14.1"
                        };
                        if (templateId == FullStackTemplate.Id && file.Path == "backend/Api/Api.csproj")
                        {
                            allowed.Clear();
                            allowed["Microsoft.Data.Sqlite"] = "10.0.15";
                        }
                        if (templateId == DiscordTemplate.Id && file.Path == "src/App/App.csproj")
                            allowed["Discord.Net.WebSocket"] = "3.20.2";
                        var requestedVersion = element.Attribute("Version")?.Value;
                        if (!allowed.TryGetValue(element.Attribute("Include")?.Value ?? "", out var version) ||
                            requestedVersion != (templateId is DiscordTemplate.Id or TwitchTemplate.Id ||
                                templateId == FullStackTemplate.Id && file.Path == "backend/Api/Api.csproj"
                                    ? $"[{version}]" : version) ||
                            element.Attributes().Any(attribute => attribute.Name.LocalName is not ("Include" or "Version" or "PrivateAssets")) ||
                            (element.Attribute("PrivateAssets") is { Value: not "all" }) ||
                            element.Elements().Any(child => child.Name != "PrivateAssets" || child.HasAttributes || child.HasElements ||
                                child.Value != "all"))
                            throw new StudioValidationException("The project contains an unauthorized package or version.");
                    }
                    else if (element.Name == "ProjectReference")
                    {
                        var isAllowedReference = templateId == FullStackTemplate.Id
                            ? file.Path == "backend/Api.Tests/Api.Tests.csproj" &&
                              element.Attribute("Include")?.Value == "../Api/Api.csproj"
                            : file.Path == "tests/App.Tests/App.Tests.csproj" &&
                              element.Attribute("Include")?.Value == "../../src/App/App.csproj";
                        if (!isAllowedReference ||
                            element.Attributes().Any(attribute => attribute.Name != "Include") || element.HasElements)
                            throw new StudioValidationException("Only the test-to-application project reference is authorized.");
                    }
                    else if (element.Name == "FrameworkReference")
                    {
                        if (templateId != FullStackTemplate.Id || file.Path != "backend/Api.Tests/Api.Tests.csproj" ||
                            element.Attribute("Include")?.Value != "Microsoft.AspNetCore.App" ||
                            element.Attributes().Any(attribute => attribute.Name != "Include") || element.HasElements)
                            throw new StudioValidationException("Only the approved ASP.NET test framework reference is allowed.");
                    }
                    else
                        throw new StudioValidationException("Custom MSBuild items are not allowed.");
                }
            }
            var isTestProject = templateId == FullStackTemplate.Id
                ? file.Path == "backend/Api.Tests/Api.Tests.csproj"
                : file.Path == "tests/App.Tests/App.Tests.csproj";
            if (isTestProject)
            {
                foreach (var name in new[] { "xunit", "xunit.runner.visualstudio", "Microsoft.NET.Test.Sdk" })
                    if (!document.Descendants("PackageReference").Any(element => element.Attribute("Include")?.Value == name))
                        throw new StudioValidationException($"The test project must include PackageReference for {name} with the pinned version.");
                if (!document.Descendants("IsTestProject").Any(element => element.Value == "true") ||
                    !document.Descendants("ProjectReference").Any() ||
                    (templateId == FullStackTemplate.Id && !document.Descendants("FrameworkReference")
                        .Any(element => element.Attribute("Include")?.Value == "Microsoft.AspNetCore.App")))
                    throw new StudioValidationException("The test project must enable IsTestProject and include its approved references.");
            }
            if (templateId == FullStackTemplate.Id &&
                ((file.Path == "backend/Api/Api.csproj" &&
                  !document.Descendants("PackageReference").Any(element =>
                      element.Attribute("Include")?.Value == "Microsoft.Data.Sqlite" &&
                      element.Attribute("Version")?.Value == "[10.0.15]")) ||
                 (file.Path == "backend/Api.Tests/Api.Tests.csproj" &&
                  document.Descendants("PackageReference").Any(element =>
                      element.Attribute("Include")?.Value == "Microsoft.Data.Sqlite"))))
                throw new StudioValidationException("Full-stack projects must use only the pinned SQLite runtime package.");
            if (templateId == DiscordTemplate.Id && file.Path == "src/App/App.csproj" &&
                !document.Descendants("PackageReference").Any(element =>
                    element.Attribute("Include")?.Value == "Discord.Net.WebSocket"))
                throw new StudioValidationException("The Discord template requires its pinned Discord.Net.WebSocket package.");
        }
        catch (XmlException)
        {
            throw new StudioValidationException("A generated project is not valid XML.");
        }
    }

    public async Task WriteAsync(Guid id, IReadOnlyList<GeneratedFile> files, CancellationToken cancellationToken, string? templateId = null)
    {
        ValidateFiles(files, templateId);
        var directory = GetDirectory(id);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Workspace not found.");
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var file in files)
            SafePaths.Resolve(directory, file.Path);

        foreach (var old in List(id).Where(file => file.Path != "Generated.slnx" &&
                     !files.Any(next => string.Equals(next.Path, file.Path, StringComparison.OrdinalIgnoreCase))))
            File.Delete(SafePaths.Resolve(directory, old.Path));

        foreach (var file in files)
        {
            var path = SafePaths.Resolve(directory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SafePaths.RejectLinks(path);
            await File.WriteAllTextAsync(path, file.Content, new UTF8Encoding(false), cancellationToken);
        }
        if (templateId == ReactTemplate.Id)
        {
            var staleSolution = Path.Combine(directory, "Generated.slnx");
            SafePaths.RejectLinks(staleSolution);
            if (File.Exists(staleSolution)) File.Delete(staleSolution);
            return;
        }
        if (templateId == FullStackTemplate.Id)
        {
            var fullStackSolution = Path.Combine(directory, "Generated.slnx");
            SafePaths.RejectLinks(fullStackSolution);
            await File.WriteAllTextAsync(fullStackSolution,
                """<Solution><Project Path="backend/Api/Api.csproj" /><Project Path="backend/Api.Tests/Api.Tests.csproj" /></Solution>""",
                cancellationToken);
            return;
        }
        var solution = Path.Combine(directory, "Generated.slnx");
        SafePaths.RejectLinks(solution);
        await File.WriteAllTextAsync(solution,
            """<Solution><Project Path="src/App/App.csproj" /><Project Path="tests/App.Tests/App.Tests.csproj" /></Solution>""",
            cancellationToken);
    }

    public IReadOnlyList<WorkspaceFile> List(Guid id)
    {
        var directory = GetDirectory(id);
        if (!Directory.Exists(directory))
            return [];
        var result = new List<WorkspaceFile>();
        Scan(directory);
        return result.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();

        void Scan(string current)
        {
            SafePaths.RejectLinks(current);
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                SafePaths.RejectLinks(path);
                if (Directory.Exists(path))
                    Scan(path);
                else
                {
                    if (result.Count > options.MaxFiles)
                        throw new StudioValidationException("Workspace contains unexpected files.");
                    result.Add(new(Path.GetRelativePath(directory, path).Replace('\\', '/'), new FileInfo(path).Length));
                }
            }
        }
    }

    public async Task<WorkspaceContent> ReadAsync(Guid id, string path, CancellationToken cancellationToken)
    {
        var directory = GetDirectory(id);
        var fullPath = path == "Generated.slnx"
            ? Path.Combine(directory, path) : SafePaths.Resolve(directory, path);
        SafePaths.RejectLinks(fullPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Workspace file not found.");
        if (new FileInfo(fullPath).Length > (path == "package-lock.json" ||
            path == "frontend/package-lock.json" ? ReactTemplate.MaxLockfileBytes : options.MaxFileBytes))
            throw new StudioValidationException("This file exceeds the preview limit.");
        return new(path, await File.ReadAllTextAsync(fullPath, cancellationToken));
    }

    public async Task ReplaceFullStackFilesAsync(Guid id, ManualRevisionRole role, IReadOnlyList<GeneratedFile> files,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(role) || files.Count == 0 ||
            files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != files.Count ||
            files.Any(file => !FullStackTemplate.IsEditableForRole(file.Path, role.ToString())))
            throw new StudioValidationException("Only distinct editable files for the requested full-stack role may be replaced.");
        var directory = GetDirectory(id);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = SafePaths.Resolve(directory, file.Path);
            if (!File.Exists(path)) throw new FileNotFoundException("The reviewed file is missing.", file.Path);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, useAsync: true))
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    await writer.WriteAsync(file.Content.AsMemory(), cancellationToken);
                SafePaths.RejectLinks(path);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
