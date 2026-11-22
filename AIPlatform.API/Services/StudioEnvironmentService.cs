using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIPlatform.Core;
using AIPlatform.Core.Studio;
using AIPlatform.Infrastructure.Studio;
using Microsoft.Win32;

namespace AIPlatform.API.Services;

public enum EnvironmentState { Ready, Missing, Incompatible, Unverified }

public sealed record EnvironmentCheck(string Id, string Name, EnvironmentState Status, string Detail,
    string? Version = null);

public sealed record EnvironmentModelCheck(string Name, EnvironmentState Status, bool? Installed);

public sealed record EnvironmentImageCheck(string ImageReference, string Version,
    EnvironmentState Status, string? InstalledImageId, string? ExpectedImageId, string Detail);

public sealed record ProvisioningPackageCheck(EnvironmentState Status, string? Version,
    string? Fingerprint, int ImageCount, long? TotalBytes, string Detail);

public sealed record StudioEnvironmentReport(DateTimeOffset CheckedAt, string OperatingSystem,
    string Architecture, EnvironmentCheck System, EnvironmentCheck Memory, EnvironmentCheck Gpu,
    EnvironmentCheck WebView2, EnvironmentCheck Node,
    long? TotalRamBytes, long? AvailableRamBytes, IReadOnlyList<GpuMemory> Gpus,
    EnvironmentCheck Ollama, IReadOnlyList<EnvironmentModelCheck> Models,
    EnvironmentCheck DockerDesktop, EnvironmentCheck DockerEngine,
    IReadOnlyList<EnvironmentImageCheck> SandboxImages, ProvisioningPackageCheck ProvisioningPackage,
    IReadOnlyList<string> Warnings);

public sealed record EnvironmentHostSnapshot(string OperatingSystem, string Architecture,
    EnvironmentCheck System, EnvironmentCheck WebView2, EnvironmentCheck Node, EnvironmentCheck DockerDesktop);

public interface IEnvironmentHostProbe
{
    Task<EnvironmentHostSnapshot> ReadAsync(CancellationToken token);
}

public interface ISandboxProvisioningService
{
    ProvisioningPackageCheck GetPackageCheck();
    Task<ProvisioningPackageCheck> VerifyAsync(CancellationToken token);
    Task<IReadOnlyList<EnvironmentImageCheck>> ImportAsync(bool confirmed, string fingerprint,
        CancellationToken token);
}

public sealed class StudioEnvironmentService(
    IEnvironmentHostProbe hostProbe, IHardwareProbe hardwareProbe, ILocalModelStatus modelStatus,
    IAgentRegistry registry, ICustomAgentStore customAgents, IContainerProcess docker, ISandboxProvisioningService provisioning,
    ISandboxImageTrust? imageTrust)
{
    public async Task<StudioEnvironmentReport> GetAsync(CancellationToken token)
    {
        var host = await hostProbe.ReadAsync(token);
        var hardware = await hardwareProbe.ReadAsync(token);
        var models = await modelStatus.GetAsync(token);
        var (dockerDesktop, dockerEngine) = await CheckDockerEngineAsync(host.DockerDesktop, token);
        var memoryCheck = hardware.TotalRamBytes is not null && hardware.AvailableRamBytes is not null
            ? new EnvironmentCheck("memory", "Mémoire RAM", EnvironmentState.Ready,
                "La mémoire totale et disponible a été mesurée.")
            : new EnvironmentCheck("memory", "Mémoire RAM", EnvironmentState.Unverified,
                "Les mesures de mémoire sont incomplètes.");
        var gpuCheck = hardware.Gpus.Count > 0
            ? new EnvironmentCheck("gpu", "GPU / VRAM", EnvironmentState.Ready,
                "GPU et mémoire disponible détectés.")
            : new EnvironmentCheck("gpu", "GPU / VRAM", EnvironmentState.Unverified,
                "GPU ou VRAM non détectés par la sonde actuelle.");
        var configuredModelNames = registry.Models.Select(model => model.Name)
            .Concat(customAgents.ListAgents().SelectMany(agent =>
                new[] { agent.ModelName }.Concat(agent.FallbackModels ?? [])))
            .Concat(customAgents.ListTeams().SelectMany(team => team.Members.SelectMany(member =>
                member.FallbackModels ?? [])))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var modelChecks = configuredModelNames.Select(name =>
        {
            if (!models.Available)
                return new EnvironmentModelCheck(name, EnvironmentState.Unverified, null);
            var installed = models.Models.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.Ordinal));
            return new EnvironmentModelCheck(name,
                installed?.Installed == true ? EnvironmentState.Ready : EnvironmentState.Missing,
                installed?.Installed ?? false);
        }).ToArray();
        var imageChecks = await CheckImagesAsync(dockerEngine.Status == EnvironmentState.Ready, token);
        var warnings = new List<string>(hardware.Warnings);
        if (!models.Available)
            warnings.Add(models.Error ?? "La télémétrie Ollama n’a pas pu être vérifiée.");
        if (configuredModelNames.Length == 0)
            warnings.Add("Aucun modèle n’est configuré pour les agents Studio.");
        if (host.WebView2.Status == EnvironmentState.Ready)
            warnings.Add("WebView2 est détecté dans le registre ; son lancement par Tauri n’a pas été testé.");
        if (dockerEngine.Status == EnvironmentState.Ready)
            warnings.Add("Le moteur Linux répond ; aucune exécution de code généré n’a été effectuée.");

        return new(DateTimeOffset.UtcNow, host.OperatingSystem, host.Architecture,
            host.System, memoryCheck, gpuCheck, host.WebView2, host.Node, hardware.TotalRamBytes, hardware.AvailableRamBytes,
            hardware.Gpus, models.Available
                ? new("ollama", "Ollama", EnvironmentState.Ready, "L’API locale et sa liste de modèles répondent.")
                : new("ollama", "Ollama", EnvironmentState.Missing,
                    models.Error ?? "Ollama est inaccessible."),
            modelChecks, dockerDesktop, dockerEngine, imageChecks, provisioning.GetPackageCheck(), warnings);
    }

    private async Task<(EnvironmentCheck Desktop, EnvironmentCheck Engine)> CheckDockerEngineAsync(
        EnvironmentCheck desktop, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var result = await docker.RunAsync(["info", "--format", "{{.OSType}}"], timeout.Token);
            if (result.ExitCode != 0)
                return (desktop, new("docker-engine", "Moteur Docker", EnvironmentState.Missing,
                    "Docker est détecté, mais son moteur ne répond pas."));
            var osType = result.Output.Trim();
            return osType == "linux"
                ? (desktop, new("docker-engine", "Moteur Docker Linux", EnvironmentState.Ready,
                    "Le moteur Linux répond. Aucun conteneur n’a été lancé.", osType))
                : (desktop, new("docker-engine", "Moteur Docker Linux", EnvironmentState.Incompatible,
                    $"Type de moteur détecté : {osType}. Studio exige le moteur Linux.", osType));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (desktop, new("docker-engine", "Moteur Docker Linux", EnvironmentState.Missing,
                "La commande Docker est absente."));
        }
        catch (IOException)
        {
            return (desktop, new("docker-engine", "Moteur Docker Linux", EnvironmentState.Missing,
                "Le moteur Docker ne peut pas être interrogé."));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (desktop, new("docker-engine", "Moteur Docker Linux", EnvironmentState.Unverified,
                "La vérification du moteur Docker a expiré."));
        }
    }

    private async Task<IReadOnlyList<EnvironmentImageCheck>> CheckImagesAsync(bool engineReady,
        CancellationToken token)
    {
        var results = new List<EnvironmentImageCheck>(SandboxImageDefinitions.All.Count);
        foreach (var definition in SandboxImageDefinitions.All)
        {
            token.ThrowIfCancellationRequested();
            var expected = imageTrust?.GetExpectedImageId(definition.ImageReference);
            if (!engineReady)
            {
                results.Add(new(definition.ImageReference, definition.Version, EnvironmentState.Unverified,
                    null, expected, "Le moteur Linux n’est pas disponible pour inspecter cette image."));
                continue;
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var inspected = await docker.RunAsync(
                    ["image", "inspect", "--format", "{{.Id}}", definition.ImageReference], timeout.Token);
                var installed = inspected.ExitCode == 0 ? inspected.Output.Trim() : null;
                var state = installed is null ? EnvironmentState.Missing :
                    expected is null ? EnvironmentState.Unverified :
                    string.Equals(installed, expected, StringComparison.Ordinal) ? EnvironmentState.Ready :
                    EnvironmentState.Incompatible;
                var detail = state switch
                {
                    EnvironmentState.Ready => "Identifiant d’image conforme au manifeste Studio vérifié.",
                    EnvironmentState.Missing => "Image absente. Aucun téléchargement automatique n’a été tenté.",
                    EnvironmentState.Unverified => "Image présente mais aucun manifeste de provisioning n’a encore été vérifié.",
                    _ => "L’identifiant installé diffère de celui du manifeste de provisioning vérifié."
                };
                results.Add(new(definition.ImageReference, definition.Version, state, installed, expected, detail));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                results.Add(new(definition.ImageReference, definition.Version, EnvironmentState.Unverified,
                    null, expected, "La commande Docker a échoué pendant l’inspection."));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                results.Add(new(definition.ImageReference, definition.Version, EnvironmentState.Unverified,
                    null, expected, "L’inspection de l’image a expiré."));
            }
        }
        return results;
    }
}

public sealed class WindowsEnvironmentHostProbe : IEnvironmentHostProbe
{
    public async Task<EnvironmentHostSnapshot> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var windows = OperatingSystem.IsWindows();
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        var osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        EnvironmentCheck system = !windows
            ? new("system", "Windows x64", EnvironmentState.Incompatible, "L’application Desktop cible Windows.")
            : architecture == "X64"
                ? new("system", "Windows x64", EnvironmentState.Ready, osDescription, Environment.OSVersion.Version.ToString())
                : new("system", "Windows x64", EnvironmentState.Incompatible,
                    $"Architecture détectée : {architecture}. L’application Desktop cible x64.");
        var node = await ReadNodeAsync(token);
        return new EnvironmentHostSnapshot(osDescription, architecture, system,
            ReadWebView2(), node, ReadDockerDesktop());
    }

    private static EnvironmentCheck ReadWebView2()
    {
        if (!OperatingSystem.IsWindows())
            return new("webview2", "WebView2", EnvironmentState.Incompatible, "WebView2 est requis sous Windows.");
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var clients = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients");
                    if (clients is null) continue;
                    foreach (var childName in clients.GetSubKeyNames())
                    {
                        using var child = clients.OpenSubKey(childName);
                        if (!string.Equals(child?.GetValue("name") as string,
                            "Microsoft Edge WebView2 Runtime", StringComparison.OrdinalIgnoreCase)) continue;
                        var version = child?.GetValue("pv") as string;
                        if (Version.TryParse(version, out _))
                            return new("webview2", "WebView2 Runtime", EnvironmentState.Ready,
                                "Runtime déclaré installé ; le démarrage effectif dans Tauri n’a pas été testé.", version);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
                catch (IOException) { }
            }
        }
        return new("webview2", "WebView2 Runtime", EnvironmentState.Missing,
            "Runtime non détecté dans les emplacements de registre vérifiés.");
    }

    private static EnvironmentCheck ReadDockerDesktop()
    {
        if (!OperatingSystem.IsWindows())
            return new("docker-desktop", "Docker Desktop", EnvironmentState.Incompatible, "Windows est requis.");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(programFiles, "Docker", "Docker", "Docker Desktop.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Docker", "Docker", "Docker Desktop.exe")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        return path is null
            ? new("docker-desktop", "Docker Desktop", EnvironmentState.Missing,
                "Docker Desktop n’a pas été détecté dans ses emplacements d’installation standard.")
            : new("docker-desktop", "Docker Desktop", EnvironmentState.Ready,
                "Exécutable présent ; le moteur est vérifié séparément.", Path.GetFileName(path));
    }

    private static async Task<EnvironmentCheck> ReadNodeAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            return new("node", "Node.js", EnvironmentState.Incompatible, "Le helper Studio cible Windows.");
        var executable = FindNodeExecutable();
        if (executable is null)
            return new("node", "Node.js", EnvironmentState.Missing,
                "Node.js 20.14 ou ultérieur est requis pour les helpers TypeScript et navigateur.");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("--version");
            if (!process.Start())
                return new("node", "Node.js", EnvironmentState.Unverified, "node.exe n’a pas démarré.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
                var error = ReadBoundedAsync(process.StandardError, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var versionText = (await output).Trim();
                _ = await error;
                if (!Regex.IsMatch(versionText, @"\Av?\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant) ||
                    !Version.TryParse(versionText.TrimStart('v'), out var version))
                    return new("node", "Node.js", EnvironmentState.Unverified, "La version de Node.js n’a pas pu être interprétée.");
                var compatible = version.Major > 20 || version.Major == 20 && version.Minor >= 14;
                return new("node", "Node.js", compatible ? EnvironmentState.Ready : EnvironmentState.Incompatible,
                    compatible ? "Version requise pour les helpers locaux." : "Node.js 20.14 ou ultérieur est requis.",
                    version.ToString());
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new("node", "Node.js", EnvironmentState.Unverified, "node.exe est présent mais ne peut pas être lancé.");
        }
        catch (IOException)
        {
            return new("node", "Node.js", EnvironmentState.Unverified, "La version de Node.js n’a pas pu être lue.");
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested) throw;
            return new("node", "Node.js", EnvironmentState.Unverified, "La vérification de Node.js a expiré.");
        }
    }

    private static string? FindNodeExecutable()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "nodejs", "node.exe");
        return File.Exists(executable) ? executable : null;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder(128);
        var buffer = new char[128];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > 512)
                throw new IOException("Node.js version output exceeded its limit.");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}

public sealed class SandboxProvisioningService(string packageDirectory, IContainerProcess docker,
    SandboxImageCatalog imageTrust) : ISandboxProvisioningService
{
    private const int MaximumManifestBytes = 1048576;
    private const long MaximumImageArchiveBytes = 42949672960;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        MaxDepth = 16
    };

    public ProvisioningPackageCheck GetPackageCheck()
    {
        var path = Path.Combine(packageDirectory, "manifest.json");
        if (!File.Exists(path))
            return new(EnvironmentState.Missing, null, null, 0, null,
                "Le paquet de provisioning versionné n’est pas inclus dans cette installation.");
        try
        {
            SafePaths.RejectLinks(packageDirectory);
            SafePaths.RejectLinks(path);
            if (new FileInfo(path).Length is 0 or > MaximumManifestBytes)
                return new(EnvironmentState.Incompatible, null, null, 0, null, "Taille de manifeste invalide.");
            var bytes = File.ReadAllBytes(path);
            var manifest = ParseManifest(bytes);
            var manifestFingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            var verified = imageTrust.IsVerifiedManifest(manifestFingerprint) &&
                manifest.Images.All(image => imageTrust.IsTrustedImage(image.ImageReference, image.ImageId));
            return new(verified ? EnvironmentState.Ready : EnvironmentState.Unverified, manifest.PackageVersion,
                manifestFingerprint, manifest.Images.Count, manifest.ArchiveSizeBytes,
                verified ? "Les empreintes du paquet ont été vérifiées dans cette session."
                    : "Manifeste détecté ; les empreintes des archives doivent être vérifiées avant import.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or StudioValidationException)
        {
            return new(EnvironmentState.Incompatible, null, null, 0, null,
                "Le manifeste de provisioning est invalide.");
        }
    }

    public async Task<ProvisioningPackageCheck> VerifyAsync(CancellationToken token)
    {
        var (manifest, fingerprint) = await VerifyPackageFilesAsync(token);
        imageTrust.SetVerifiedImages(manifest.Images, fingerprint);
        return new(EnvironmentState.Ready, manifest.PackageVersion, fingerprint, manifest.Images.Count,
            manifest.ArchiveSizeBytes,
            "Les empreintes SHA-256 des archives et les références d’images sont cohérentes.");
    }

    public async Task<IReadOnlyList<EnvironmentImageCheck>> ImportAsync(bool confirmed, string fingerprint,
        CancellationToken token)
    {
        if (!confirmed)
            throw new StudioValidationException("Une confirmation explicite est requise avant l’import des images.");
        var requestedFingerprint = fingerprint ?? "";
        if (!Regex.IsMatch(requestedFingerprint, @"\A[0-9A-Fa-f]{64}\z", RegexOptions.CultureInvariant))
            throw new StudioValidationException("Empreinte de manifeste invalide.");
        var (manifest, actualFingerprint) = await VerifyPackageFilesAsync(token);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(requestedFingerprint),
                Convert.FromHexString(actualFingerprint)))
            throw new StudioValidationException("Le paquet a changé depuis sa vérification. Vérifiez-le à nouveau.");
        using (var engineTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            engineTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            var engine = await docker.RunAsync(["info", "--format", "{{.OSType}}"], engineTimeout.Token);
            if (engine.ExitCode != 0 || engine.Output.Trim() != "linux")
                throw new StudioValidationException("L’import exige que le moteur Docker Linux soit démarré.");
        }

        imageTrust.SetVerifiedImages(manifest.Images, actualFingerprint);
        var archivePath = Path.GetFullPath(Path.Combine(packageDirectory, manifest.ArchiveFile));
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var loaded = await docker.RunAsync(["load", "--input", archivePath], timeout.Token);
            if (loaded.ExitCode != 0)
                throw new StudioValidationException($"Docker n’a pas importé le paquet sandbox : {Bound(loaded.Output)}");
        }
        var results = new List<EnvironmentImageCheck>(manifest.Images.Count);
        foreach (var image in manifest.Images)
        {
            token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var inspected = await docker.RunAsync(
                ["image", "inspect", "--format", "{{.Id}}", image.ImageReference], timeout.Token);
            var installedId = inspected.ExitCode == 0 ? inspected.Output.Trim() : null;
            if (installedId is null || !string.Equals(installedId, image.ImageId, StringComparison.Ordinal))
                throw new StudioValidationException(
                    $"L’identifiant importé pour {image.ImageReference} ne correspond pas au manifeste.");
            results.Add(new(image.ImageReference, image.Version, EnvironmentState.Ready, installedId,
                image.ImageId, "Archive vérifiée, importée après confirmation et identifiant Docker conforme."));
        }
        return results;
    }

    private async Task<(SandboxProvisioningManifest Manifest, string Fingerprint)> VerifyPackageFilesAsync(
        CancellationToken token)
    {
        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
        SafePaths.RejectLinks(packageDirectory);
        SafePaths.RejectLinks(manifestPath);
        byte[] manifestBytes;
        if (!File.Exists(manifestPath))
            throw new StudioValidationException("Le paquet de provisioning n’est pas disponible dans cette installation.");
        if (new FileInfo(manifestPath).Length is 0 or > MaximumManifestBytes)
            throw new StudioValidationException("La taille du manifeste de provisioning est invalide.");
        try { manifestBytes = await File.ReadAllBytesAsync(manifestPath, token); }
        catch (FileNotFoundException)
        {
            throw new StudioValidationException("Le paquet de provisioning n’est pas disponible dans cette installation.");
        }
        if (manifestBytes.Length is 0 or > MaximumManifestBytes)
            throw new StudioValidationException("La taille du manifeste de provisioning est invalide.");
        var manifest = ParseManifest(manifestBytes);
        var archivePath = Path.GetFullPath(Path.Combine(packageDirectory, manifest.ArchiveFile));
        if (!archivePath.StartsWith(Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new StudioValidationException("Le manifeste contient un chemin d’archive hors périmètre.");
        SafePaths.RejectLinks(archivePath);
        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length != manifest.ArchiveSizeBytes)
            throw new StudioValidationException("Taille manquante ou incorrecte pour l’archive sandbox.");
        await using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         1048576, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var hash = await SHA256.HashDataAsync(stream, token);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(manifest.ArchiveSha256 ?? "")))
                throw new StudioValidationException("Empreinte SHA-256 incorrecte pour l’archive sandbox.");
        }
        return (manifest, Convert.ToHexString(SHA256.HashData(manifestBytes)));
    }

    private static SandboxProvisioningManifest ParseManifest(byte[] bytes)
    {
        var manifest = JsonSerializer.Deserialize<SandboxProvisioningManifest>(bytes, JsonOptions)
            ?? throw new StudioValidationException("Le manifeste de provisioning est vide.");
        if (manifest.FormatVersion != SandboxImageDefinitions.Manifest.FormatVersion ||
            manifest.PackageVersion != SandboxImageDefinitions.Manifest.PackageVersion ||
            manifest.Platform != SandboxImageDefinitions.Manifest.Platform || manifest.Images is null ||
            manifest.Images.Any(image => image is null) ||
            manifest.ArchiveFile != SandboxImageDefinitions.Manifest.ArchiveFile ||
            manifest.ArchiveSizeBytes is <= 0 or > MaximumImageArchiveBytes ||
            !Regex.IsMatch(manifest.ArchiveSha256 ?? "", @"\A[0-9A-Fa-f]{64}\z", RegexOptions.CultureInvariant) ||
            manifest.Images.Count != SandboxImageDefinitions.All.Count ||
            manifest.Images.Select(image => image.ImageReference).Distinct(StringComparer.Ordinal).Count() !=
                manifest.Images.Count)
            throw new StudioValidationException("Version de manifeste, plateforme ou inventaire d’images incompatible.");
        foreach (var definition in SandboxImageDefinitions.All)
        {
            var image = manifest.Images.SingleOrDefault(item => item.ImageReference == definition.ImageReference);
            if (image is null || image.Version != definition.Version || image.Dockerfile != definition.Dockerfile ||
                !string.Equals(image.SourceFingerprint, definition.SourceFingerprint, StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(image.ImageId ?? "", @"\Asha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(image.SourceFingerprint ?? "", @"\A[0-9A-Fa-f]{64}\z", RegexOptions.CultureInvariant))
                throw new StudioValidationException($"Entrée de manifeste invalide pour {definition.ImageReference}.");
        }
        return manifest;
    }

    private static string Bound(string output)
    {
        var text = output.Trim();
        return text.Length <= 500 ? text : text[..500];
    }
}

public sealed class SandboxImageCatalog : ISandboxImageTrust
{
    private IReadOnlyDictionary<string, string> expectedImages =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private string? verifiedManifestFingerprint;

    public string? GetExpectedImageId(string imageReference) =>
        Volatile.Read(ref expectedImages).GetValueOrDefault(imageReference);

    public bool IsTrustedImage(string imageReference, string imageId) =>
        string.Equals(GetExpectedImageId(imageReference), imageId, StringComparison.Ordinal);

    public bool IsVerifiedManifest(string fingerprint) =>
        string.Equals(Volatile.Read(ref verifiedManifestFingerprint), fingerprint, StringComparison.OrdinalIgnoreCase);

    public void SetVerifiedImages(IReadOnlyList<SandboxProvisionedImage> images, string fingerprint)
    {
        Volatile.Write(ref expectedImages, images.ToDictionary(image => image.ImageReference,
            image => image.ImageId, StringComparer.Ordinal));
        Volatile.Write(ref verifiedManifestFingerprint, fingerprint);
    }
}

public sealed record SandboxProvisioningManifest(int FormatVersion, string PackageVersion, string Platform,
    DateTimeOffset CreatedAt, string ArchiveFile, long ArchiveSizeBytes, string ArchiveSha256,
    IReadOnlyList<SandboxProvisionedImage> Images);

public sealed record SandboxProvisionedImage(string ImageReference, string Version, string Dockerfile,
    string SourceFingerprint, string ImageId);
