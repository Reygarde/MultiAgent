using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;

namespace AIPlatform.Tests;

public sealed class CustomAgentProposalValidatorTests
{
    [Fact]
    public void DetectsMissingLocalReactImport()
    {
        var files = new[]
        {
            new CustomAgentWorkspaceFile("package.json", """{"dependencies":{"react":"^19.0.0"}}"""),
            new CustomAgentWorkspaceFile("src/App.tsx", "import Panel from './Panel'; export default Panel;")
        };

        var result = CustomAgentProposalValidator.Validate(null, files,
            files.Select(file => file.Path).ToArray(), [], null, CancellationToken.None);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "LOCAL_IMPORT_MISSING" &&
            diagnostic.Path == "src/App.tsx" && diagnostic.RelatedPath == "src/Panel");
    }

    [Fact]
    public void ResolvesImportsAcrossMultifileProposal()
    {
        var files = new[]
        {
            new CustomAgentWorkspaceFile("package.json", """{"dependencies":{"react":"^19.0.0"}}"""),
            new CustomAgentWorkspaceFile("src/App.tsx", "export default function App() { return null }")
        };
        var changes = new[]
        {
            new CustomAgentFileChange("src/App.tsx", "Use the panel.", files[1].Content,
                "import Panel from './Panel'; export default Panel;", "modify"),
            new CustomAgentFileChange("src/Panel.tsx", "Add the panel.", null,
                "export default function Panel() { return null }", "create")
        };

        var result = CustomAgentProposalValidator.Validate(null, files,
            files.Select(file => file.Path).ToArray(), changes, null, CancellationToken.None);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "LOCAL_IMPORT_MISSING");
    }

    [Fact]
    public void ReportsMissingReactScaffoldFile()
    {
        var files = AgentReactTemplate.Files.Where(file => file.Path != "src/main.tsx")
            .Select(file => new CustomAgentWorkspaceFile(file.Path, file.Content)).ToArray();

        var result = CustomAgentProposalValidator.Validate(AgentReactTemplate.Id, files,
            files.Select(file => file.Path).ToArray(), [], null, CancellationToken.None);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "SCAFFOLD_FILE_MISSING" && diagnostic.Path == "src/main.tsx");
    }

    [Fact]
    public void ReportsMissingDotNetProjectReference()
    {
        var files = AgentDotNetTemplate.Files
            .Select(file => new CustomAgentWorkspaceFile(file.Path,
                file.Path == "tests/App.Tests/App.Tests.csproj"
                    ? file.Content.Replace("../../src/App/App.csproj", "../../src/Missing/Missing.csproj",
                        StringComparison.Ordinal)
                    : file.Content))
            .ToArray();

        var result = CustomAgentProposalValidator.Validate(AgentDotNetTemplate.Id, files,
            files.Select(file => file.Path).ToArray(), [], null, CancellationToken.None);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "DOTNET_PROJECT_REFERENCE_MISSING" &&
            diagnostic.Path == "tests/App.Tests/App.Tests.csproj");
    }
}
