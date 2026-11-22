using System.Text.Json.Serialization;
using AIPlatform.API;
using AIPlatform.API.Middleware;
using AIPlatform.Core;
using AIPlatform.Infrastructure;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using AIPlatform.API.Services;

var builder = WebApplication.CreateBuilder(args);
var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var desktop = new DesktopSession(builder.Environment.IsEnvironment("Desktop"),
    Environment.GetEnvironmentVariable("AIPLATFORM_DESKTOP_TOKEN"));
if (desktop.Enabled)
{
    var settings = Path.Combine(localAppData, "AIPlatformStudio", "settings.json");
    builder.Configuration.AddJsonFile(settings, optional: true, reloadOnChange: false);
    builder.WebHost.UseUrls("http://127.0.0.1:0").PreferHostingUrls(false);
    builder.WebHost.ConfigureKestrel(server =>
    {
        server.Configure(new ConfigurationBuilder().Build());
        server.Listen(System.Net.IPAddress.Loopback, 0);
    });
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
    builder.Services.AddHostedService<DesktopLifetime>();
    builder.Services.AddCors(options => options.AddPolicy("Desktop", policy => policy
        .WithOrigins("http://tauri.localhost", "tauri://localhost", "http://127.0.0.1:5173")
        .AllowAnyMethod().AllowAnyHeader()));
}
else if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Start the published backend through AIPlatform Studio, or use the Development launch profile.");
else if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5167");

var configuration = builder.Configuration.GetSection("Platform").Get<PlatformConfiguration>()
    ?? throw new InvalidOperationException("The Platform configuration is required.");
if (configuration.MaxConcurrentMissions != 1)
    throw new InvalidOperationException("Studio MVP requires exactly one active mission.");
var studio = builder.Configuration.GetSection("Studio").Get<StudioOptions>() ?? new();
studio.Validate();
studio = studio with
{
    DataDirectory = StudioDataDirectory.Resolve(desktop.Enabled, studio.DataDirectory,
        builder.Environment.ContentRootPath, localAppData)
};
var registry = new ConfigurableRegistry(configuration, studio);
if (!studio.CollaborativeWorkflows && !registry.Agents.Any(agent => agent.Id == studio.DeveloperAgentId))
    throw new InvalidOperationException("The Studio developer agent must be a registered execution agent.");
var sandboxOptions = builder.Configuration.GetSection("Sandbox").Get<SandboxOptions>() ?? new();
sandboxOptions.Validate();
var sandboxProvisioningDirectory = builder.Configuration["SandboxProvisioning:Directory"] ??
    (desktop.Enabled
        ? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "sandbox-provisioning"))
        : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "AIPlatform.Web",
            "src-tauri", "resources", "sandbox-provisioning")));
var ollama = builder.Configuration.GetSection("Ollama").Get<OllamaOptions>()
    ?? throw new InvalidOperationException("The Ollama configuration is required.");
ollama.Validate();
var disableOllama = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("StudioDevDisableOllama");
if (registry.Models.Any(model => !string.Equals(model.Provider, "Ollama", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Only the Ollama provider is registered in this MVP.");

builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton(desktop);
builder.Services.AddSingleton<IAgentRegistry>(registry);
builder.Services.AddSingleton<IAgentConfigurationService>(registry);
builder.Services.AddSingleton(ollama);
builder.Services.AddSingleton<OllamaDiagnosticStore>();
builder.Services.AddSingleton<OllamaModelOperations>();
builder.Services.AddSingleton<IHardwareProbe, LocalHardwareProbe>();
builder.Services.AddHttpClient("OllamaRegistry", client => client.Timeout = TimeSpan.FromSeconds(25))
    .ConfigurePrimaryHttpMessageHandler(() => CreateOllamaHandler(disableOllama));
builder.Services.AddSingleton(services => new ModelLibraryService(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama"),
    services.GetRequiredService<IHttpClientFactory>().CreateClient("OllamaRegistry"), ollama, studio,
    services.GetRequiredService<IHardwareProbe>(), services.GetRequiredService<ICustomAgentStore>(),
    services.GetRequiredService<IAgentRegistry>(), services.GetRequiredService<OllamaModelOperations>(),
    services.GetRequiredService<ILogger<ModelLibraryService>>(), services.GetRequiredService<IMissionService>()));
builder.Services.AddSingleton<LocalModelRouter>();
builder.Services.AddSingleton<IModelLibraryService>(services => services.GetRequiredService<ModelLibraryService>());
builder.Services.AddSingleton<IModelRouter>(services => services.GetRequiredService<LocalModelRouter>());
builder.Services.AddHttpClient("Ollama", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => CreateOllamaHandler(disableOllama));
builder.Services.AddSingleton<ILLMProvider>(services => new OllamaProvider(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama"), ollama,
    services.GetRequiredService<OllamaDiagnosticStore>(), services.GetRequiredService<OllamaModelOperations>(),
    services.GetRequiredService<IModelLibraryService>()));
builder.Services.AddSingleton<ILLMProviderResolver, LlmProviderResolver>();
builder.Services.AddSingleton<IAgentRouter, LlmAgentRouter>();
builder.Services.AddSingleton<IMissionOrchestrator, MissionOrchestrator>();
builder.Services.AddSingleton<MissionExecutionGate>();
builder.Services.AddSingleton(studio);
builder.Services.AddSingleton<SandboxImageCatalog>();
builder.Services.AddSingleton<ISandboxProvisioningService>(services => new SandboxProvisioningService(
    sandboxProvisioningDirectory, services.GetRequiredService<IContainerProcess>(),
    services.GetRequiredService<SandboxImageCatalog>()));
if (desktop.Enabled)
{
    builder.Services.AddSingleton<StudioMaintenanceGate>();
    builder.Services.AddSingleton<ISandboxImageTrust>(services => services.GetRequiredService<SandboxImageCatalog>());
    builder.Services.AddSingleton<IEnvironmentHostProbe, WindowsEnvironmentHostProbe>();
    builder.Services.AddSingleton<StudioEnvironmentService>();
    builder.Services.AddSingleton(services => new StudioBackupService(
        studio,
        configuration,
        services.GetRequiredService<StudioMaintenanceGate>(),
        services.GetRequiredService<MissionExecutionGate>(),
        services.GetRequiredService<OllamaModelOperations>(),
        services.GetRequiredService<IMissionService>(),
        services.GetRequiredService<ICustomAgentStore>(),
        services.GetRequiredService<ITwitchBotService>(),
        services.GetRequiredService<ILocalCommandExecutor>(),
        localAppData));
}
builder.Services.AddSingleton(sandboxOptions);
builder.Services.AddSingleton<MissionStore>();
builder.Services.AddSingleton<IMissionService>(services => services.GetRequiredService<MissionStore>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITwitchBotStore, TwitchBotStore>();
builder.Services.AddSingleton<ITwitchTokenVault, WindowsCredentialTokenVault>();
builder.Services.AddHttpClient<ITwitchOAuthClient, TwitchOAuthClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<ITwitchChatClient, TwitchChatClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<TwitchBotService>();
builder.Services.AddSingleton<ITwitchBotService>(services => services.GetRequiredService<TwitchBotService>());
builder.Services.AddSingleton<ITwitchSimulationService, TwitchSimulationService>();
builder.Services.AddSingleton<ITwitchCommandProposalService, TwitchCommandProposalService>();
builder.Services.AddHostedService(services => services.GetRequiredService<TwitchBotService>());
builder.Services.AddSingleton<ICustomAgentStore, CustomAgentStore>();
builder.Services.AddSingleton<IOllamaModelCatalog>(services => new OllamaModelCatalog(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama"),
    services.GetRequiredService<OllamaOptions>()));
var previewBrowserOptions = new PreviewBrowserOptions(
    Path.Combine(
        new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"))
            .ResolveLinkTarget(returnFinalTarget: true)?.FullName ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
        "node.exe"),
    builder.Environment.IsDevelopment()
        ? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "AIPlatform.Web", "scripts", "preview-browser.mjs"))
        : Path.Combine(AppContext.BaseDirectory, "preview-browser", "preview-browser.mjs"));
var browserTestHelper = StudioBrowserTestOptions.ResolveHelperFile(builder.Environment.ContentRootPath);
var studioBrowserTestOptions = new StudioBrowserTestOptions(
    previewBrowserOptions.NodeExecutable, browserTestHelper);
builder.Services.AddSingleton(new ReactSourceCheck(
    previewBrowserOptions.NodeExecutable,
    Path.Combine(Path.GetDirectoryName(browserTestHelper)!, "react-source-check.mjs")));
builder.Services.AddSingleton<IReactSourceCheck>(services => services.GetRequiredService<ReactSourceCheck>());
builder.Services.AddSingleton<IReactSourceRepair>(services => services.GetRequiredService<ReactSourceCheck>());
builder.Services.AddSingleton<IReactDraftRepair, ReactDraftRepair>();
builder.Services.AddSingleton(previewBrowserOptions);
builder.Services.AddSingleton(studioBrowserTestOptions);
builder.Services.AddSingleton(services => new LocalCommandExecutor(
    services.GetRequiredService<IWorkspaceStore>(), studio, desktop.Enabled, studioBrowserTestOptions));
builder.Services.AddSingleton<ILocalCommandExecutor>(services =>
    services.GetRequiredService<LocalCommandExecutor>());
builder.Services.AddHostedService(services => services.GetRequiredService<LocalCommandExecutor>());
builder.Services.AddSingleton<IStudioBrowserTestRunner>(services => new StudioBrowserTestRunner(
    services.GetRequiredService<IMissionService>(),
    services.GetRequiredService<ILocalCommandExecutor>(),
    studio,
    studioBrowserTestOptions,
    desktop.Enabled));
builder.Services.AddSingleton<ICustomAgentService, CustomAgentService>();
builder.Services.AddSingleton<IWorkspaceStore>(services => new WorkspaceStore(services.GetRequiredService<StudioOptions>(),
    id => services.GetRequiredService<MissionStore>().Get(id)?.Workflow?.Revisions?
        .LastOrDefault(revision => revision.WorkspaceId.HasValue)?.WorkspaceId,
    id => services.GetRequiredService<MissionStore>().Get(id)?.Sizing));
builder.Services.AddSingleton<IProjectExport, ProjectExport>();
builder.Services.AddSingleton<IProjectAnalyzer, ProjectAnalyzer>();
builder.Services.AddSingleton<IWorkspaceAnalysis, WorkspaceAnalysis>();
builder.Services.AddSingleton<IProjectGenerator, ProjectGenerator>();
builder.Services.AddSingleton<IContainerProcess, ContainerProcess>();
builder.Services.AddSingleton<DockerSandboxRunner>();
builder.Services.AddSingleton<ISandboxRunner>(services => services.GetRequiredService<DockerSandboxRunner>());
builder.Services.AddSingleton<ReactRevisionValidation>();
builder.Services.AddSingleton<IReactRevisionValidation>(services => services.GetRequiredService<ReactRevisionValidation>());
builder.Services.AddSingleton<IReactRevisionCorrection, ReactRevisionCorrection>();
builder.Services.AddSingleton<IReactPlanAmendment, ReactPlanAmendment>();
builder.Services.AddSingleton<IExecutionPlanService, ExecutionPlanService>();
builder.Services.AddSingleton<ReviewerCorrectionResumeService>();
builder.Services.AddSingleton<IManualRevisionService, ManualRevisionService>();
builder.Services.AddSingleton<PreviewSourceVerifier>();
builder.Services.AddSingleton<PreviewAuthorizationStore>();
builder.Services.AddSingleton<DockerPreviewHttpTransport>();
builder.Services.AddSingleton<PreviewProxy>();
builder.Services.AddSingleton<IControlledPreviewBrowser, ControlledPreviewBrowser>();
builder.Services.AddSingleton<IPreviewRuntime, DockerPreviewAdapter>();
builder.Services.AddSingleton<PreviewSessionManager>();
builder.Services.AddHostedService<PreviewMaintenance>();
builder.Services.AddSingleton<MissionProcessor>();
builder.Services.AddSingleton<IWorkflowAgents, WorkflowAgents>();
builder.Services.AddSingleton<WorkflowModelRouting>();
builder.Services.AddSingleton<CollaborativeProcessor>();
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
if (app.Environment.IsDevelopment())
    app.Logger.LogInformation("Studio development data directory: {Directory}", studio.DataDirectory);
app.Services.GetRequiredService<OllamaDiagnosticStore>().PruneExpired();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (desktop.Enabled)
{
    app.UseCors("Desktop");
    app.Use(desktop.InvokeAsync);
}
app.Use(async (context, next) =>
{
    var address = context.Connection.RemoteIpAddress;
    if (address is not null && !System.Net.IPAddress.IsLoopback(
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address))
    {
        await TypedResults.Problem(statusCode: 403, title: "Studio MVP accepts loopback connections only.").ExecuteAsync(context);
        return;
    }
    if (!desktop.Enabled && context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") &&
        context.Request.Headers.TryGetValue("Origin", out var origin) &&
        (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !uri.IsLoopback ||
         uri.Scheme is not ("http" or "https")))
    {
        await TypedResults.Problem(statusCode: 403, title: "Only local Studio origins are permitted.").ExecuteAsync(context);
        return;
    }
    await next(context);
});
if (desktop.Enabled)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/studio-backup"))
        {
            await next(context);
            return;
        }
        using var requestLease = await context.RequestServices.GetRequiredService<StudioMaintenanceGate>()
            .EnterRequestAsync(context.RequestAborted);
        await next(context);
    });
}
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.MapAgents();
app.MapCustomAgents();
app.MapModels();
app.MapModelLibrary();
app.MapStudio();
app.MapReactRevisionValidation();
app.MapTwitch();
app.MapPreview();
if (desktop.Enabled)
{
    app.MapStudioBackup();
    app.MapStudioEnvironment();
}
app.MapGet("/api/health", (CancellationToken token) =>
{
    token.ThrowIfCancellationRequested();
    return TypedResults.Ok(new BackendHealthResponse(true, Environment.ProcessId));
}).WithName("GetBackendHealth").WithSummary("Check this API process is ready")
    .WithDescription("In Desktop mode every request requires the per-launch bearer token.");
if (desktop.Enabled)
{
    await app.StartAsync();
    var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
        .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
    Console.WriteLine("AIPLATFORM_READY " + System.Text.Json.JsonSerializer.Serialize(new { url = address, processId = Environment.ProcessId }));
    await Console.Out.FlushAsync();
    await app.WaitForShutdownAsync();
}
else
    app.Run();

static HttpMessageHandler CreateOllamaHandler(bool disabled) => disabled
    ? new DisabledOllamaHandler()
    : new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };

public partial class Program;

file sealed class DisabledOllamaHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("Ollama is disabled for this development launch."));
}
