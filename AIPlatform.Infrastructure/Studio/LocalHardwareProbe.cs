using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using AIPlatform.Core;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed class LocalHardwareProbe(StudioOptions options) : IHardwareProbe
{
    public async Task<HardwareSnapshot> ReadAsync(CancellationToken token)
    {
        long? total = null, available = null, disk = null;
        var warnings = new List<string>();
        var gpus = new List<GpuMemory>();
        if (OperatingSystem.IsWindows())
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory))
            {
                total = checked((long)memory.TotalPhysical);
                available = checked((long)memory.AvailablePhysical);
            }
            else warnings.Add("Mesure de RAM Windows indisponible.");
            var executable = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (File.Exists(executable))
            {
                using var process = new Process
                {
                    StartInfo = new(executable,
                        "--query-gpu=name,memory.total,memory.free --format=csv,noheader,nounits")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }
                };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var started = false;
                try
                {
                    started = process.Start();
                    var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                    var error = process.StandardError.ReadToEndAsync(deadline.Token);
                    await process.WaitForExitAsync(deadline.Token);
                    var text = await output;
                    await error;
                    if (process.ExitCode == 0)
                        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = line.Trim().Split(',');
                            if (parts.Length == 3 &&
                                long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var gpuTotal) &&
                                long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var gpuFree) &&
                                gpuTotal > 0 && gpuFree >= 0 && gpuFree <= gpuTotal)
                                gpus.Add(new(parts[0].Trim(), gpuTotal * 1048576, gpuFree * 1048576));
                        }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
                    IOException or OperationCanceledException && !token.IsCancellationRequested)
                {
                    warnings.Add("Mesure NVIDIA indisponible : " + exception.GetType().Name);
                }
                finally
                {
                    if (started && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
            }
            if (gpus.Count == 0) warnings.Add("GPU/VRAM non mesurables : pilote NVIDIA requis pour cette sonde.");
        }
        else warnings.Add("La sonde RAM/GPU est actuellement disponible sous Windows uniquement.");
        try { disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.DataDirectory))!).AvailableFreeSpace; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { warnings.Add("Espace disque indisponible : " + exception.GetType().Name); }
        token.ThrowIfCancellationRequested();
        return new(total, available, gpus, disk, warnings);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

public static class ModelMemoryEstimator
{
    private const long MiB = 1048576;

    public static ModelCompatibility Estimate(ModelMetadata? model, HardwareSnapshot hardware, int context,
        LoadedOllamaModel? loaded = null, long? downloadBytes = null, ModelMemoryQualification? qualification = null)
    {
        if (context is < 512 or > ModelContextLimits.Maximum)
            throw new StudioValidationException($"Context must be between 512 and {ModelContextLimits.Maximum} tokens.");
        ModelCompatibility Unknown(string reason) => new("Unknown", reason, context);
        if (model is null) return Unknown("Métadonnées d'architecture inconnues avant installation ; téléchargement distinct de l'exécution.");
        if (model.ContextLength is { } limit && context > limit)
            return new("Insufficient", "Le contexte demandé dépasse la limite déclarée par le modèle.", context);
        if (model.Architecture is not ("llama" or "qwen2" or "qwen3" or "gemma2" or "gemma3") ||
            model.Layers is not > 0 || model.KvHeads is not > 0 ||
            model.KeyLength is not > 0 || model.ValueLength is not > 0 ||
            model.InstalledBytes is not > 0 || hardware.AvailableRamBytes is null)
            return EstimateObserved(model, hardware, context, loaded, qualification);
        var weights = model.InstalledBytes.Value;
        // F16 is deliberately conservative; quantized KV cache is not assumed.
        var kv = checked((long)context * model.Layers.Value * model.KvHeads.Value *
            (model.KeyLength.Value + model.ValueLength.Value) * 2);
        var buffers = Math.Max(512 * MiB, weights / 10);
        var reserve = 1536 * MiB;
        var gpu = hardware.Gpus.OrderByDescending(item => item.AvailableBytes).FirstOrDefault();
        var vram = gpu?.AvailableBytes ?? 0;
        var ram = hardware.AvailableRamBytes.Value;
        var notes = "Poids GGUF quantifiés + KV F16 + buffers estimés ; réserve système 1,5 Gio. Pas de garantie de débit.";
        if (loaded is not null)
        {
            if (loaded.ContextLength is null || loaded.ContextLength < context)
                return Unknown("Modèle chargé avec un contexte inconnu ou inférieur ; rechargement nécessaire.");
            return new(ram >= reserve ? "Compatible" : "Borderline",
                "Déjà chargé au contexte demandé ; allocations déjà résidentes, non comptées deux fois. " + notes,
                context, weights, kv, buffers, Math.Max(0, loaded.Size - loaded.SizeVram),
                loaded.SizeVram, "Loaded");
        }
        var total = checked(weights + kv + buffers);
        if (vram >= total && ram >= reserve)
            return new("Compatible", notes, context, weights, kv, buffers, reserve, total, "GPU");
        if (ram >= total + reserve)
            return new("Compatible", "Exécution CPU estimée possible, potentiellement lente. " + notes,
                context, weights, kv, buffers, total + reserve, 0, "CPU");
        var gpuAllocation = Math.Max(0, vram - buffers);
        var offloadedWeights = Math.Min(weights, gpuAllocation);
        var cpuRequired = weights - offloadedWeights + kv + buffers + reserve;
        if (gpu is not null && offloadedWeights > 0 && ram >= cpuRequired)
            return new("Borderline", "Offload CPU/GPU nécessaire ; répartition et vitesse non garanties. " + notes,
                context, weights, kv, buffers, cpuRequired, offloadedWeights + buffers, "Hybrid");
        return new("Insufficient", "RAM requise pour le reliquat CPU/cache et réserve non disponible ; RAM et VRAM ne sont pas interchangeables. " + notes,
            context, weights, kv, buffers, cpuRequired, offloadedWeights + buffers, "Hybrid");
    }

    private static ModelCompatibility EstimateObserved(ModelMetadata model, HardwareSnapshot hardware, int context,
        LoadedOllamaModel? loaded, ModelMemoryQualification? qualification)
    {
        ModelCompatibility Unknown(string reason) => new("Unknown", reason, context);
        const long reserve = 1536 * MiB;
        if (qualification is null)
            return Unknown("Architecture ou cache KV non estimable : qualification explicite d'un chargement observé requise. Aucune estimation fiable inventée.");
        if (model.Digest is null)
            return Unknown("Empreinte des poids indisponible : qualification enregistrée non vérifiable.");
        if (qualification.Digest != model.Digest)
            return Unknown("Empreinte des poids modifiée : qualification périmée, nouveau chargement observé requis.");
        if (context > qualification.ContextTokens)
            return Unknown("Contexte supérieur à celui du chargement qualifié ; nouvelle observation requise.");
        if (hardware.AvailableRamBytes is not { } ram || model.InstalledBytes is not > 0)
            return Unknown("Mesures RAM ou poids installés indisponibles ; qualification non vérifiable.");
        var gpu = hardware.Gpus.OrderByDescending(item => item.AvailableBytes).FirstOrDefault();
        var size = loaded?.Size ?? qualification.SizeBytes;
        var vram = loaded?.SizeVram ?? qualification.SizeVramBytes;
        if (loaded is not null && (loaded.Name != model.Name || loaded.Digest != model.Digest))
            return Unknown("Le modèle résident ne correspond pas à l'empreinte qualifiée ; nouvelle observation requise.");
        if (loaded is not null && (loaded.ContextLength is null ||
                loaded.ContextLength < context))
            return Unknown("Modèle résident chargé à un contexte inconnu ou inférieur ; nouvelle observation requise.");
        if (size < model.InstalledBytes || size > long.MaxValue - reserve || vram < 0 || vram > size)
            return Unknown("Allocations observées incohérentes avec les poids installés ; qualification non vérifiable.");
        if (vram > 0 && gpu is null)
            return Unknown("VRAM non mesurable ; le placement qualifié ne peut pas être vérifié.");
        var requiredRam = checked(size - vram + reserve);
        var insufficient = loaded is null
            ? ram < requiredRam || vram > (gpu?.AvailableBytes ?? 0)
            : ram < reserve || vram > (gpu?.TotalBytes ?? 0);
        var reason = $"Chargement explicitement qualifié au contexte {qualification.ContextTokens}, empreinte identique. " +
            "Allocations observées, pas de calcul fiable du cache KV ni de garantie de débit ou de rechargement. " +
            "Aucune extrapolation à un contexte supérieur ; réserve système 1,5 Gio.";
        return new(insufficient ? "Insufficient" : "Compatible",
            insufficient ? "Ressources insuffisantes pour le placement observé. " + reason : reason,
            context, model.InstalledBytes, RequiredRamBytes: requiredRam, RequiredVramBytes: vram,
            Placement: loaded is null ? "Measured" : "MeasuredLoaded");
    }
}
