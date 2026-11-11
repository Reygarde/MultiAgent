using System.Text.Json.Serialization;
using AIPlatform.API;
using AIPlatform.API.Middleware;
using AIPlatform.Core;
using AIPlatform.Infrastructure;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using AIPlatform.API.Services;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5167");

var configuration = builder.Configuration.GetSection("Platform").Get<PlatformConfiguration>()
    ?? throw new InvalidOperationException("The Platform configuration is required.");
var registry = new AgentRegistry(configuration);
if (configuration.MaxConcurrentMissions != 1)
    throw new InvalidOperationException("Studio MVP requires exactly one active mission.");
var studio = builder.Configuration.GetSection("Studio").Get<StudioOptions>() ?? new();
studio.Validate();
studio = studio with { DataDirectory = Path.GetFullPath(studio.DataDirectory, builder.Environment.ContentRootPath) };
SafePaths.ValidateStorageDirectory(studio.DataDirectory);
if (!registry.Candidates.Any(agent => agent.Id == studio.DeveloperAgentId))
    throw new InvalidOperationException("The Studio developer agent must be a registered execution agent.");
var sandboxOptions = builder.Configuration.GetSection("Sandbox").Get<SandboxOptions>() ?? new();
sandboxOptions.Validate();
var ollama = builder.Configuration.GetSection("Ollama").Get<OllamaOptions>()
    ?? throw new InvalidOperationException("The Ollama configuration is required.");
ollama.Validate();
if (registry.Models.Any(model => !string.Equals(model.Provider, "Ollama", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Only the Ollama provider is registered in this MVP.");

builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton<IAgentRegistry>(registry);
builder.Services.AddSingleton(ollama);
builder.Services.AddHttpClient("Ollama", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    });
builder.Services.AddSingleton<ILLMProvider>(services => new OllamaProvider(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama"), ollama));
builder.Services.AddSingleton<ILLMProviderResolver, LlmProviderResolver>();
builder.Services.AddSingleton<IAgentRouter, LlmAgentRouter>();
builder.Services.AddSingleton<IMissionOrchestrator, MissionOrchestrator>();
builder.Services.AddSingleton<MissionExecutionGate>();
builder.Services.AddSingleton(studio);
builder.Services.AddSingleton(sandboxOptions);
builder.Services.AddSingleton<MissionStore>();
builder.Services.AddSingleton<IMissionService>(services => services.GetRequiredService<MissionStore>());
builder.Services.AddSingleton<IWorkspaceStore, WorkspaceStore>();
builder.Services.AddSingleton<IProjectGenerator, ProjectGenerator>();
builder.Services.AddSingleton<IContainerProcess, ContainerProcess>();
builder.Services.AddSingleton<ISandboxRunner, DockerSandboxRunner>();
builder.Services.AddSingleton<MissionProcessor>();
builder.Services.AddHostedService<MissionWorker>();
builder.Services.AddSingleton<ILocalModelStatus>(services => new LocalModelStatus(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama"),
    ollama, registry, services.GetServices<ILLMProvider>()));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    var address = context.Connection.RemoteIpAddress;
    if (address is not null && !System.Net.IPAddress.IsLoopback(
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address))
    {
        await TypedResults.Problem(statusCode: 403, title: "Studio MVP accepts loopback connections only.").ExecuteAsync(context);
        return;
    }
    if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") &&
        context.Request.Headers.TryGetValue("Origin", out var origin) &&
        (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !uri.IsLoopback ||
         uri.Scheme is not ("http" or "https")))
    {
        await TypedResults.Problem(statusCode: 403, title: "Only local Studio origins are permitted.").ExecuteAsync(context);
        return;
    }
    await next(context);
});
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.MapAgents();
app.MapModels();
app.MapStudio();
app.Run();

public partial class Program;
