using System.Text.Json;
using AIPlatform.Core.Studio;

namespace AIPlatform.PreviewSupervisor;

public sealed class SimulatedPersistentVmControl : IRecoverableHyperVVmControl
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _resourceStorePath;
    private readonly bool _crashAfterCreate;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SimulatedPersistentVmControl(string resourceStorePath, bool crashAfterCreate = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceStorePath);
        _resourceStorePath = Path.GetFullPath(resourceStorePath);
        _crashAfterCreate = crashAfterCreate;
        Directory.CreateDirectory(Path.GetDirectoryName(_resourceStorePath)!);
    }

    public async Task<HyperVPreviewVmLease> CreateAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var resources = Load();
            if (resources.TryGetValue(sessionId, out var existing))
                return existing.Lease;

            var lease = new HyperVPreviewVmLease(
                sessionId,
                Guid.NewGuid(),
                $"simulated:{sessionId:N}");
            resources.Add(sessionId, new SimulatedResource(lease, HyperVPreviewVmState.Created));
            Save(resources);
            if (_crashAfterCreate)
            {
                Console.Error.WriteLine("Simulated supervisor crash after resource allocation.");
                Environment.Exit(86);
            }
            return lease;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
    {
        await SetStateAsync(lease, HyperVPreviewVmState.Running, cancellationToken);
    }

    public async Task StopAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewStopMode mode,
        CancellationToken cancellationToken)
    {
        await SetStateAsync(lease, HyperVPreviewVmState.Stopped, cancellationToken);
    }

    public async Task DestroyAsync(HyperVPreviewVmLease lease, CancellationToken cancellationToken)
    {
        await SetStateAsync(lease, HyperVPreviewVmState.Destroyed, cancellationToken);
    }

    public async Task<HyperVPreviewVmState> GetStateAsync(
        HyperVPreviewVmLease lease,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var resource = Require(Load(), lease);
            return resource.State;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HyperVPreviewVmLease?> FindBySessionIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return Load().TryGetValue(sessionId, out var resource) ? resource.Lease : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SetStateAsync(
        HyperVPreviewVmLease lease,
        HyperVPreviewVmState state,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var resources = Load();
            var resource = Require(resources, lease);
            resources[lease.SessionId] = resource with { State = state };
            Save(resources);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<Guid, SimulatedResource> Load()
    {
        if (!File.Exists(_resourceStorePath))
            return [];
        var resources = JsonSerializer.Deserialize<Dictionary<Guid, SimulatedResource>>(
            File.ReadAllBytes(_resourceStorePath), JsonOptions);
        if (resources is null)
            throw new InvalidDataException("Simulated VM resource store is invalid.");
        foreach (var (sessionId, resource) in resources)
        {
            if (sessionId == Guid.Empty || resource.Lease.SessionId != sessionId ||
                resource.Lease.VmId == Guid.Empty || !Enum.IsDefined(resource.State))
                throw new InvalidDataException("Simulated VM resource store failed validation.");
        }
        return resources;
    }

    private void Save(Dictionary<Guid, SimulatedResource> resources)
    {
        var temporaryPath = _resourceStorePath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(resources, JsonOptions);
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, _resourceStorePath, overwrite: true);
    }

    private static SimulatedResource Require(
        Dictionary<Guid, SimulatedResource> resources,
        HyperVPreviewVmLease lease)
    {
        if (!resources.TryGetValue(lease.SessionId, out var resource) ||
            resource.Lease != lease)
            throw new InvalidOperationException("Simulated VM lease is unknown or mismatched.");
        return resource;
    }

    private sealed record SimulatedResource(HyperVPreviewVmLease Lease, HyperVPreviewVmState State);
}
