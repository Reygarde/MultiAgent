namespace AIPlatform.API;

/// <summary>Mission text submitted for automatic routing and execution.</summary>
public sealed record RunAgentRequest(string? Task);

/// <summary>Selected agent, authorized model, routing reason and generated text.</summary>
public sealed record RunAgentResponse(
    string SelectedAgent, string SelectedModel, string Reason, string Response, bool UsedFallback);

/// <summary>Configured agent and its capabilities and model assignment.</summary>
public sealed record AgentResponse(
    string Id, string Description, IReadOnlyList<string> Capabilities, string ModelId, bool IsRouter,
    bool CanGenerateProjects = false);

/// <summary>Configured model, generation settings and assigned agents; not an installation check.</summary>
public sealed record ModelResponse(
    string Id, string Provider, string Name, int ContextSize, int MaxOutputTokens,
    double Temperature, IReadOnlyList<string> AssignedAgents);
