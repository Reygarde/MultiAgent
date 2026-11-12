using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIPlatform.API;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIPlatform.Tests;

public sealed class WorkflowApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task ConfiguresAgentsValidatesPermissionsAndLocksEditingWhileWorkflowActive()
    {
        using var fixture = new WorkflowFixture();
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        var config = await client.GetFromJsonAsync<AgentConfigurationResponse>("/api/agents/configuration", JsonOptions);
        var changed = new UpdateAgentConfigurationRequest(config!.Agents.Select(agent =>
            agent.Role == "Review" ? agent with { Name = "Read-only reviewer" } : agent).ToArray(), config.Models);
        (await client.PutAsJsonAsync("/api/agents/configuration", changed)).EnsureSuccessStatusCode();
        var invalid = changed with { Agents = changed.Agents.Select(agent =>
            agent.Role == "Review" ? agent with { AllowedTools = ["DockerTests"] } : agent).ToArray() };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/agents/configuration", invalid)).StatusCode);
        var mission = await Create(client);
        await Wait(client, mission.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/agents/configuration", changed)).StatusCode);
        var workflow = await client.GetFromJsonAsync<WorkflowResponse>($"/api/missions/{mission.Id}/workflow", JsonOptions);
        Assert.NotNull(workflow!.Plan);
        Assert.Equal("Calculator", workflow.Plan.Name);
        var json = await client.GetStringAsync($"/api/missions/{mission.Id}/workflow");
        Assert.DoesNotContain("systemPrompt", json);
        Assert.DoesNotContain("\"draft\"", json);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/workspaces/{mission.Id}")).StatusCode);
    }

    [Fact]
    public async Task RefusalRenameResumeAndDeletePreserveMissionAudit()
    {
        using var fixture = new WorkflowFixture();
        using var factory = Factory(fixture);
        using var client = factory.CreateClient();
        var mission = await Create(client);
        await Wait(client, mission.Id);
        var plan = await client.GetFromJsonAsync<ExecutionPlanResponse>($"/api/missions/{mission.Id}/execution-plan");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval",
            new { approved = true, fingerprint = "obsolete" })).StatusCode);
        (await client.PostAsJsonAsync($"/api/missions/{mission.Id}/approval", new { approved = false, plan!.Fingerprint })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/workspaces/{mission.Id}", new { name = "Arithmetic", description = "Small library" })).EnsureSuccessStatusCode();
        Assert.Equal("Arithmetic", (await client.GetFromJsonAsync<MissionResponse>($"/api/missions/{mission.Id}", JsonOptions))!.ProjectName);
        (await client.PostAsync($"/api/missions/{mission.Id}/resume", null)).EnsureSuccessStatusCode();
        var resumed = await Wait(client, mission.Id);
        Assert.Equal(MissionStatus.AwaitingApproval, resumed.Status);
        Assert.False(resumed.SandboxApproved);
        Assert.Empty(fixture.Sandbox.Calls);
        (await client.PostAsync($"/api/missions/{mission.Id}/cancel", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/workspaces/{mission.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/workspaces/{mission.Id}/files")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/missions/{mission.Id}/resume", null)).StatusCode);
        Assert.True((await client.GetFromJsonAsync<MissionResponse>($"/api/missions/{mission.Id}", JsonOptions))!.Deleted);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/missions/{Guid.NewGuid()}/workflow")).StatusCode);
    }

    private static async Task<MissionResponse> Create(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/missions", new { task = "Create Calculator", kind = "Project" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MissionResponse>(JsonOptions))!;
    }
    private static async Task<MissionResponse> Wait(HttpClient client, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var mission = (await client.GetFromJsonAsync<MissionResponse>($"/api/missions/{id}", JsonOptions, timeout.Token))!;
            if (mission.Status is not (MissionStatus.Queued or MissionStatus.Running)) return mission;
            await Task.Delay(20, timeout.Token);
        }
    }
    private static WebApplicationFactory<Program> Factory(WorkflowFixture fixture) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                var registry = new ConfigurableRegistry(fixture.Platform, fixture.Options);
                services.RemoveAll<StudioOptions>();
                services.AddSingleton(fixture.Options);
                services.RemoveAll<IAgentRegistry>();
                services.AddSingleton<IAgentRegistry>(registry);
                services.RemoveAll<IAgentConfigurationService>();
                services.AddSingleton<IAgentConfigurationService>(registry);
                services.RemoveAll<ILLMProvider>();
                services.AddSingleton<ILLMProvider>(fixture.Provider);
                services.RemoveAll<ISandboxRunner>();
                services.AddSingleton<ISandboxRunner>(fixture.Sandbox);
            });
        });
}
