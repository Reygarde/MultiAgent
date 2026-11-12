namespace AIPlatform.Core;

public sealed record PlatformConfiguration
{
    public string RouterAgentId { get; init; } = "";
    public string FallbackAgentId { get; init; } = "";
    public int MaxConcurrentMissions { get; init; } = 1;
    public int MaxTaskLength { get; init; } = 16000;
    public List<AgentDefinition> Agents { get; init; } = [];
    public List<ModelDefinition> Models { get; init; } = [];
}

public sealed record AgentDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Role { get; init; } = "Chat";
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public IReadOnlyList<string> TaskTypes { get; init; } = ["Chat"];
    public int TimeoutSeconds { get; init; } = 180;
    public string Description { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string SystemPrompt { get; init; } = "";
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> FallbackKeywords { get; init; } = [];
}

public sealed record ModelDefinition
{
    public string Id { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Name { get; init; } = "";
    public int ContextSize { get; init; } = 4096;
    public int MaxOutputTokens { get; init; } = 1024;
    public double Temperature { get; init; } = 0.2;
}
