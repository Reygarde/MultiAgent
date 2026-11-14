namespace AIPlatform.Core;

public interface IAgentRegistry
{
    IReadOnlyList<AgentDefinition> Agents { get; }
    IReadOnlyList<AgentDefinition> Candidates { get; }
    IReadOnlyList<ModelDefinition> Models { get; }
    AgentDefinition Router { get; }
    AgentDefinition Fallback { get; }
    ModelDefinition GetModel(string id);
}

public sealed class AgentRegistry : IAgentRegistry
{
    public IReadOnlyList<AgentDefinition> Agents { get; }
    public IReadOnlyList<AgentDefinition> Candidates { get; }
    public IReadOnlyList<ModelDefinition> Models { get; }
    public AgentDefinition Router { get; }
    public AgentDefinition Fallback { get; }

    public AgentRegistry(PlatformConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.MaxConcurrentMissions is < 1 or > 16 ||
            configuration.MaxTaskLength is < 1 or > 1000000)
            throw new ArgumentException("Invalid mission concurrency or task length configuration.");

        Models = Array.AsReadOnly(configuration.Models.ToArray());
        Agents = Array.AsReadOnly(configuration.Agents.Select(agent => agent with
        {
            Capabilities = Array.AsReadOnly(agent.Capabilities.ToArray()),
            FallbackKeywords = Array.AsReadOnly(agent.FallbackKeywords.ToArray()),
            AllowedTools = Array.AsReadOnly(agent.AllowedTools.ToArray()),
            TaskTypes = Array.AsReadOnly(agent.TaskTypes.ToArray())
        }).ToArray());

        if (Models.Count == 0 || Agents.Count < 2 ||
            Models.Select(model => model.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Models.Count ||
            Agents.Select(agent => agent.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Agents.Count)
            throw new ArgumentException("Configure unique model and agent identifiers, including a router and an executor.");

        foreach (var model in Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.Provider) ||
                string.IsNullOrWhiteSpace(model.Name) || model.ContextSize < 256 ||
                model.MaxOutputTokens < 1 || model.MaxOutputTokens >= model.ContextSize ||
                !double.IsFinite(model.Temperature) || model.Temperature is < 0 or > 2)
                throw new ArgumentException("Invalid model configuration.");
        }

        foreach (var agent in Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.Id) || string.IsNullOrWhiteSpace(agent.Description) ||
                string.IsNullOrWhiteSpace(agent.SystemPrompt) ||
                !Models.Any(model => model.Id == agent.ModelId) ||
                agent.Capabilities.Any(string.IsNullOrWhiteSpace) ||
                agent.FallbackKeywords.Any(string.IsNullOrWhiteSpace) ||
                agent.TimeoutSeconds is < 5 or > 900 ||
                agent.Role is not ("Chat" or "Router" or "Planner" or "Developer" or "Backend" or "Frontend" or "Review" or "Test" or "Orchestrator") ||
                agent.AllowedTools.Except(AgentPermissions.ToolsFor(agent.Role), StringComparer.Ordinal).Any() ||
                agent.TaskTypes.Any(type => type is not ("Chat" or "Project")))
                throw new ArgumentException("Invalid agent configuration or model assignment.");
        }

        Router = Agents.SingleOrDefault(agent => agent.Id == configuration.RouterAgentId)
            ?? throw new ArgumentException("The configured routing agent does not exist.");
        if (!Router.Enabled) throw new ArgumentException("The routing agent must remain enabled.");
        Candidates = Array.AsReadOnly(Agents.Where(agent => agent.Enabled && agent.Id != Router.Id &&
            agent.TaskTypes.Contains("Chat")).ToArray());
        Fallback = Candidates.SingleOrDefault(agent => agent.Id == configuration.FallbackAgentId)
            ?? throw new ArgumentException("The fallback must be a configured execution agent.");
    }

    public ModelDefinition GetModel(string id) =>
        Models.Single(model => model.Id == id);
}

public static class AgentPermissions
{
    public static string[] ToolsFor(string role) => role switch
    {
        "Planner" => ["Plan"],
        "Developer" or "Backend" or "Frontend" => ["GenerateFiles"],
        "Review" => ["ReadSources"],
        "Test" => ["DockerBuild", "DockerTests"],
        "Orchestrator" => ["Workflow"],
        _ => []
    };

    public static AgentDefinition Require(IAgentRegistry registry, string role, string tool)
    {
        var matches = registry.Agents.Where(agent => agent.Enabled && agent.Role == role &&
            agent.TaskTypes.Contains("Project") && agent.AllowedTools.Contains(tool)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0]
            : throw new Studio.StudioValidationException($"Exactly one enabled {role} agent with {tool} permission is required.");
    }
}
