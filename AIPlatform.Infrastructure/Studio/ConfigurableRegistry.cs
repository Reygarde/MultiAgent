using System.Text.Json;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed record AgentConfiguration(IReadOnlyList<AgentDefinition> Agents, IReadOnlyList<ModelDefinition> Models);
public interface IAgentConfigurationService
{
    AgentConfiguration GetConfiguration();
    void Save(AgentConfiguration configuration);
}

public sealed class ConfigurableRegistry : IAgentRegistry, IAgentConfigurationService
{
    private readonly object sync = new();
    private readonly PlatformConfiguration defaults;
    private readonly string path;
    private AgentRegistry snapshot;
    public ConfigurableRegistry(PlatformConfiguration defaults, StudioOptions studio)
    {
        this.defaults = defaults;
        var directory = SafePaths.ValidateStorageDirectory(studio.DataDirectory);
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "agents.json");
        SafePaths.RejectLinks(path);
        snapshot = File.Exists(path)
            ? Validate(JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Saved agent configuration is empty."))
            : new(defaults);
    }

    public IAgentRegistry Snapshot { get { lock (sync) return snapshot; } }
    public IReadOnlyList<AgentDefinition> Agents => Snapshot.Agents;
    public IReadOnlyList<AgentDefinition> Candidates => Snapshot.Candidates;
    public IReadOnlyList<ModelDefinition> Models => Snapshot.Models;
    public AgentDefinition Router => Snapshot.Router;
    public AgentDefinition Fallback => Snapshot.Fallback;
    public ModelDefinition GetModel(string id) => Snapshot.GetModel(id);
    public AgentConfiguration GetConfiguration() => new(Snapshot.Agents, Snapshot.Models);

    public void Save(AgentConfiguration configuration)
    {
        var next = Validate(configuration);
        lock (sync)
        {
            var temporary = path + ".tmp";
            SafePaths.RejectLinks(path);
            SafePaths.RejectLinks(temporary);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new AgentConfiguration(next.Agents, next.Models),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            File.Move(temporary, path, overwrite: true);
            snapshot = next;
        }
    }

    private AgentRegistry Validate(AgentConfiguration configuration)
    {
        if (configuration.Agents is null || configuration.Models is null ||
            configuration.Agents.Count is < 2 or > 40 || configuration.Models.Count is < 1 or > 20 ||
            configuration.Agents.Any(agent => agent is null || agent.Id is null || agent.Id.Length is < 1 or > 80 ||
                !agent.Id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ||
                agent.Name is null || agent.Name.Length > 80 ||
                agent.Description is null || agent.Description.Length > 1000 ||
                agent.SystemPrompt is null || agent.SystemPrompt.Length > 4000 ||
                agent.Capabilities is null || agent.Capabilities.Count > 30 || agent.Capabilities.Any(value => value is null || value.Length > 100) ||
                agent.FallbackKeywords is null || agent.FallbackKeywords.Count > 40 || agent.FallbackKeywords.Any(value => value is null || value.Length > 100) ||
                agent.AllowedTools is null || agent.AllowedTools.Count > 5 || agent.TaskTypes is null || agent.TaskTypes.Count > 2) ||
            configuration.Models.Any(model => model is null || model.Id is null || model.Id.Length > 80 ||
                model.Provider != "Ollama" || string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 120 ||
                model.ContextSize is < 2048 or > 32768 || model.MaxOutputTokens < 256 ||
                !model.Name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.' or '/')))
            throw new StudioValidationException("Invalid bounded local agent/model configuration.");
        if (configuration.Agents.Where(agent => agent.Enabled &&
                agent.Role is not ("Chat" or "Router")).GroupBy(agent => agent.Role).Any(group => group.Count() > 1))
            throw new StudioValidationException("Only one enabled agent per workflow role is supported in this MVP.");
        try
        {
            return new(defaults with { Agents = configuration.Agents.ToList(), Models = configuration.Models.ToList() });
        }
        catch (ArgumentException exception)
        {
            throw new StudioValidationException(exception.Message);
        }
    }
}
