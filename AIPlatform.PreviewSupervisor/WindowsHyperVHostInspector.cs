using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIPlatform.PreviewSupervisor;

public enum HyperVHostInspectionStatus
{
    Available,
    AccessDenied,
    HyperVUnavailable,
    UnsupportedPlatform,
    TimedOut,
    QueryFailed,
}

public enum HyperVObservedVmState
{
    Running,
    Off,
    Paused,
    Saved,
    Starting,
    Stopping,
    Unknown,
}

public sealed record HyperVObservedVm(
    Guid VmId,
    HyperVObservedVmState State,
    Guid? OwnerSessionId);

public sealed record HyperVHostSnapshot(
    int LogicalProcessorCount,
    long FreePhysicalMemoryKiB,
    long FreeSystemDriveBytes,
    IReadOnlyList<HyperVObservedVm> VirtualMachines);

public sealed record HyperVHostInspection(
    HyperVHostInspectionStatus Status,
    HyperVHostSnapshot? Snapshot = null,
    string? ErrorCode = null);

public sealed record HyperVHostQueryExecution(
    bool TimedOut,
    int ExitCode,
    string StandardOutput);

public interface IHyperVHostInspector
{
    Task<HyperVHostInspection> InspectAsync(CancellationToken cancellationToken);
}

public interface IHyperVHostQueryRunner
{
    Task<HyperVHostQueryExecution> ExecuteAsync(CancellationToken cancellationToken);
}

public sealed class WindowsHyperVHostInspector : IHyperVHostInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IHyperVHostQueryRunner _queryRunner;
    private readonly TimeProvider _clock;
    private readonly bool _requiresWindows;

    public WindowsHyperVHostInspector(
        IHyperVHostQueryRunner? queryRunner = null,
        TimeProvider? clock = null)
    {
        _queryRunner = queryRunner ?? new WindowsPowerShellHyperVHostQueryRunner();
        _clock = clock ?? TimeProvider.System;
        _requiresWindows = queryRunner is null;
    }

    public async Task<HyperVHostInspection> InspectAsync(CancellationToken cancellationToken)
    {
        if (_requiresWindows && !OperatingSystem.IsWindows())
            return new HyperVHostInspection(HyperVHostInspectionStatus.UnsupportedPlatform, ErrorCode: "WindowsOnly");

        try
        {
            var execution = await _queryRunner.ExecuteAsync(cancellationToken);
            if (execution.TimedOut)
                return new HyperVHostInspection(HyperVHostInspectionStatus.TimedOut, ErrorCode: "QueryTimeout");
            if (execution.StandardOutput.Length is 0 or > WindowsPowerShellHyperVHostQueryRunner.MaximumOutputCharacters)
                return new HyperVHostInspection(HyperVHostInspectionStatus.QueryFailed, ErrorCode: "InvalidQueryOutput");

            RejectDuplicateProperties(execution.StandardOutput);
            var response = JsonSerializer.Deserialize<HostQueryResponse>(execution.StandardOutput, JsonOptions)
                ?? throw new InvalidDataException("Hyper-V host query returned an empty result.");
            if (response.SchemaVersion != 1)
                throw new InvalidDataException("Unsupported Hyper-V host query schema.");

            if (!response.Success)
            {
                if (execution.ExitCode == 0 || !IsKnownErrorCode(response.ErrorKind))
                    throw new InvalidDataException("Hyper-V host query error result is inconsistent.");
                return new HyperVHostInspection(MapErrorStatus(response.ErrorKind!), ErrorCode: response.ErrorKind);
            }

            if (execution.ExitCode != 0 ||
                response.ErrorKind is not null ||
                response.LogicalProcessorCount is not > 0 ||
                response.FreePhysicalMemoryKiB is not >= 0 ||
                response.FreeSystemDriveBytes is not >= 0 ||
                response.VirtualMachines is null ||
                response.VirtualMachines.Length > WindowsPowerShellHyperVHostQueryRunner.MaximumVirtualMachines)
                throw new InvalidDataException("Hyper-V host query result failed validation.");

            var seenVmIds = new HashSet<Guid>();
            var machines = new List<HyperVObservedVm>(response.VirtualMachines.Length);
            foreach (var vm in response.VirtualMachines)
            {
                if (!Guid.TryParseExact(vm.VmId, "D", out var vmId) || vmId == Guid.Empty ||
                    !seenVmIds.Add(vmId))
                    throw new InvalidDataException("Hyper-V VM inventory contains an invalid entry.");
                if (!Enum.TryParse<HyperVObservedVmState>(vm.State, ignoreCase: false, out var state) ||
                    !Enum.IsDefined(state))
                    state = HyperVObservedVmState.Unknown;

                Guid? ownerSessionId = null;
                if (vm.OwnerSessionId is not null)
                {
                    if (!Guid.TryParseExact(vm.OwnerSessionId, "D", out var parsedOwner) ||
                        parsedOwner == Guid.Empty)
                        throw new InvalidDataException("Hyper-V VM ownership marker is malformed.");
                    ownerSessionId = parsedOwner;
                }

                machines.Add(new HyperVObservedVm(vmId, state, ownerSessionId));
            }

            return new HyperVHostInspection(
                HyperVHostInspectionStatus.Available,
                new HyperVHostSnapshot(
                    response.LogicalProcessorCount.Value,
                    response.FreePhysicalMemoryKiB.Value,
                    response.FreeSystemDriveBytes.Value,
                    machines),
                ErrorCode: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return new HyperVHostInspection(HyperVHostInspectionStatus.TimedOut, ErrorCode: "QueryTimeout");
        }
        catch (FileNotFoundException)
        {
            return new HyperVHostInspection(HyperVHostInspectionStatus.HyperVUnavailable, ErrorCode: "PowerShellNotFound");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            return new HyperVHostInspection(HyperVHostInspectionStatus.QueryFailed, ErrorCode: error.GetType().Name);
        }
        catch (Win32Exception error)
        {
            return new HyperVHostInspection(
                HyperVHostInspectionStatus.HyperVUnavailable,
                ErrorCode: error.NativeErrorCode == 2 ? "PowerShellNotFound" : "PowerShellStartFailed");
        }
        catch (UnauthorizedAccessException)
        {
            return new HyperVHostInspection(HyperVHostInspectionStatus.AccessDenied, ErrorCode: "AccessDenied");
        }
        catch (SecurityException)
        {
            return new HyperVHostInspection(HyperVHostInspectionStatus.AccessDenied, ErrorCode: "AccessDenied");
        }
    }

    private static bool IsKnownErrorCode(string? errorCode) =>
        errorCode is "accessDenied" or "hyperVUnavailable" or "inventoryLimitExceeded" or "queryFailed";

    private static HyperVHostInspectionStatus MapErrorStatus(string errorCode) =>
        errorCode switch
        {
            "accessDenied" => HyperVHostInspectionStatus.AccessDenied,
            "hyperVUnavailable" => HyperVHostInspectionStatus.HyperVUnavailable,
            "inventoryLimitExceeded" or "queryFailed" => HyperVHostInspectionStatus.QueryFailed,
            _ => throw new InvalidDataException("Unknown Hyper-V host query error code."),
        };

    private static void RejectDuplicateProperties(string json)
    {
        using var document = JsonDocument.Parse(json);
        Visit(document.RootElement);

        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException("Hyper-V host query contains duplicate JSON properties.");
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Visit(item);
            }
        }
    }

    private sealed record HostQueryResponse(
        int SchemaVersion,
        bool Success,
        string? ErrorKind,
        int? LogicalProcessorCount,
        long? FreePhysicalMemoryKiB,
        long? FreeSystemDriveBytes,
        HostVmResponse[]? VirtualMachines);

    private sealed record HostVmResponse(string VmId, string State, string? OwnerSessionId);
}

public sealed class WindowsPowerShellHyperVHostQueryRunner : IHyperVHostQueryRunner
{
    public const int MaximumVirtualMachines = 4096;
    public const int MaximumOutputCharacters = 1024 * 1024;
    private const int MaximumErrorCharacters = 64 * 1024;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private const string ReadOnlyQuery = """
        $ErrorActionPreference = 'Stop'
        try {
            if (-not (Get-Module -ListAvailable -Name Hyper-V)) {
                throw [System.Management.Automation.CommandNotFoundException]::new('Hyper-V module unavailable.')
            }
            Import-Module Hyper-V -ErrorAction Stop
            $vmHost = Get-VMHost -ErrorAction Stop
            $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
            $drive = [System.IO.DriveInfo]::new($env:SystemDrive)
            $vms = @(Get-VM -ErrorAction Stop | Select-Object -First 4097 | ForEach-Object {
                $vmId = [guid]$_.Id
                $ownerSessionId = $null
                $tag = [regex]::Match([string]$_.Notes,
                    '^AIPlatformPreview/65J/(?<session>[0-9a-fA-F]{32})/(?<vm>[0-9a-fA-F-]{36})$')
                if ($tag.Success) {
                    $tagSessionId = [guid]::Empty
                    $tagVmId = [guid]::Empty
                    if ([guid]::TryParseExact($tag.Groups['session'].Value, 'N', [ref]$tagSessionId) -and
                        [guid]::TryParseExact($tag.Groups['vm'].Value, 'D', [ref]$tagVmId) -and
                        $tagVmId -eq $vmId) {
                        $ownerSessionId = $tagSessionId.ToString('D')
                    }
                }
                [pscustomobject]@{
                    vmId = $vmId.ToString('D')
                    state = [string]$_.State
                    ownerSessionId = $ownerSessionId
                }
            })
            if ($vms.Count -gt 4096) {
                $result = [pscustomobject]@{
                    schemaVersion = 1; success = $false; errorKind = 'inventoryLimitExceeded'
                    logicalProcessorCount = $null; freePhysicalMemoryKiB = $null
                    freeSystemDriveBytes = $null; virtualMachines = $null
                }
                [Console]::Out.WriteLine((ConvertTo-Json -InputObject $result -Compress -Depth 4))
                exit 13
            }
            $result = [pscustomobject]@{
                schemaVersion = 1; success = $true; errorKind = $null
                logicalProcessorCount = [int]$vmHost.LogicalProcessorCount
                freePhysicalMemoryKiB = [long]$os.FreePhysicalMemory
                freeSystemDriveBytes = [long]$drive.AvailableFreeSpace
                virtualMachines = @($vms)
            }
            [Console]::Out.WriteLine((ConvertTo-Json -InputObject $result -Compress -Depth 4))
            exit 0
        }
        catch {
            $errorKind = 'queryFailed'
            $exception = $_.Exception
            while ($null -ne $exception) {
                # CIM status code 2 is AccessDenied.
                if ($exception.GetType().FullName -eq 'Microsoft.Management.Infrastructure.CimException' -and
                    [int]$exception.StatusCode -eq 2) {
                    $errorKind = 'accessDenied'
                    break
                }
                $exception = $exception.InnerException
            }
            if ($_.CategoryInfo.Category -eq 'PermissionDenied' -or
                $_.Exception -is [System.UnauthorizedAccessException] -or
                $_.Exception -is [System.Security.SecurityException]) {
                $errorKind = 'accessDenied'
            }
            elseif ($_.Exception -is [System.Management.Automation.CommandNotFoundException]) {
                $errorKind = 'hyperVUnavailable'
            }
            $result = [pscustomobject]@{
                schemaVersion = 1; success = $false; errorKind = $errorKind
                logicalProcessorCount = $null; freePhysicalMemoryKiB = $null
                freeSystemDriveBytes = $null; virtualMachines = $null
            }
            [Console]::Out.WriteLine((ConvertTo-Json -InputObject $result -Compress -Depth 4))
            exit 13
        }
        """;

    public async Task<HyperVHostQueryExecution> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Hyper-V host query is Windows-only.");

        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Windows PowerShell is unavailable.", executable);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", ReadOnlyQuery },
            },
        };

        if (!process.Start())
            throw new IOException("Windows PowerShell did not start.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QueryTimeout);
        var stdout = ReadBoundedAsync(process.StandardOutput, MaximumOutputCharacters, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, MaximumErrorCharacters, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            _ = await stderr;
            return new HyperVHostQueryExecution(false, process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateProcessAsync(process);
            await ObserveReaderAsync(stdout);
            await ObserveReaderAsync(stderr);
            return new HyperVHostQueryExecution(true, -1, string.Empty);
        }
        catch
        {
            await TerminateProcessAsync(process);
            await ObserveReaderAsync(stdout);
            await ObserveReaderAsync(stderr);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
                return output.ToString();
            if (output.Length + count > maximumCharacters)
                throw new InvalidDataException("Hyper-V PowerShell query exceeded its output limit.");
            output.Append(buffer, 0, count);
        }
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
        catch (TimeoutException error)
        {
            throw new IOException("Could not stop the bounded Hyper-V query process.", error);
        }
    }

    private static async Task ObserveReaderAsync(Task<string> reader)
    {
        try
        {
            _ = await reader;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }
}
