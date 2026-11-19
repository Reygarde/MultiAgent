$ErrorActionPreference = 'Stop'
$web = Split-Path -Parent $PSScriptRoot
$api = Join-Path (Split-Path -Parent $web) 'AIPlatform.API'
$vite = Join-Path $web 'node_modules\vite\bin\vite.js'
if (-not (Test-Path -LiteralPath $vite -PathType Leaf)) {
    throw 'Install the frontend dependencies before starting Studio development.'
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Studio__DataDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AIPlatformStudio.Dev\StudioData'
$env:StudioDevDisableOllama = 'true'
$env:Sandbox__Enabled = 'false'

dotnet build (Join-Path $api 'AIPlatform.API.csproj') --configuration Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$backend = Start-Process -FilePath 'dotnet' -ArgumentList '"bin\Debug\net10.0\AIPlatform.API.dll" --urls http://localhost:5167' `
    -WorkingDirectory $api -PassThru -NoNewWindow
try {
    $health = $null
    for ($i = 0; $i -lt 40 -and $null -eq $health; $i++) {
        if ($backend.HasExited) { throw "Studio API failed to start (exit $($backend.ExitCode))." }
        try {
            $health = Invoke-RestMethod 'http://localhost:5167/api/health' -TimeoutSec 1
        } catch [System.Net.WebException] {
            Start-Sleep -Milliseconds 250
        }
    }
    if ($null -eq $health -or $health.processId -ne $backend.Id) {
        throw 'Studio API not ready on port 5167, or the port belongs to another instance.'
    }
    Push-Location $web
    try {
        node $vite --host 127.0.0.1
        if ($LASTEXITCODE -ne 0) { throw "Vite exited with code $LASTEXITCODE." }
    } finally {
        Pop-Location
    }
} finally {
    if (-not $backend.HasExited) { Stop-Process -Id $backend.Id }
    $backend.Dispose()
}
