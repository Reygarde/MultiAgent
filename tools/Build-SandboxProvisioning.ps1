param(
    [switch]$ConfirmDockerBuild,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\AIPlatform.Web\src-tauri\resources\sandbox-provisioning")
)

$ErrorActionPreference = "Stop"
if (-not $ConfirmDockerBuild) {
    throw "Pass -ConfirmDockerBuild to explicitly build and export the trusted sandbox images."
}

$repository = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$sandbox = Join-Path $repository "sandbox"
$recipePath = Join-Path $sandbox "provisioning-recipes.json"
$recipeManifest = Get-Content -LiteralPath $recipePath -Raw | ConvertFrom-Json
if ($recipeManifest.formatVersion -ne 1 -or $recipeManifest.platform -ne "windows-x64" -or
    $recipeManifest.packageVersion -ne "1.0.0" -or
    $recipeManifest.archiveFile -ne "aiplatform-sandbox-images-1.0.0.tar" -or
    $recipeManifest.images.Count -ne 4) {
    throw "Unsupported sandbox recipe manifest."
}

function Resolve-PathWithin([string]$Root, [string]$RelativePath, [string]$Description) {
    $rootPath = [System.IO.Path]::GetFullPath($Root)
    $path = [System.IO.Path]::GetFullPath((Join-Path $rootPath $RelativePath))
    $rootPrefix = $rootPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $path.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -and
        -not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description escapes its allowed directory: $RelativePath"
    }
    return $path
}

function Get-RecipeFingerprint([string[]]$Files) {
    $stream = [System.IO.MemoryStream]::new()
    try {
        $sortedFiles = [string[]]@($Files)
        [Array]::Sort($sortedFiles, [StringComparer]::Ordinal)
        $uniqueFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in $sortedFiles) {
            if (-not $uniqueFiles.Add($file)) {
                throw "Recipe input list contains duplicate paths."
            }
        }
        foreach ($file in $sortedFiles) {
            $relative = $file.Replace("/", "\")
            $absolute = Resolve-PathWithin $repository $relative "Recipe input"
            if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
                throw "Recipe input is missing: $file"
            }
            $name = [System.Text.Encoding]::UTF8.GetBytes($file.Replace("\", "/") + "`n")
            $content = [System.IO.File]::ReadAllBytes($absolute)
            $stream.Write($name, 0, $name.Length)
            $stream.Write($content, 0, $content.Length)
        }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $digest = $sha.ComputeHash($stream.ToArray()) }
        finally { $sha.Dispose() }
        return [BitConverter]::ToString($digest).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
    }
}

$builds = @($recipeManifest.images)
foreach ($build in $builds) {
    $fingerprint = Get-RecipeFingerprint $build.sourceFiles
    if ($fingerprint -ne $build.sourceFingerprint) {
        throw "Sandbox recipe inputs changed for $($build.imageReference); review and version the recipe before packaging."
    }
    $context = Resolve-PathWithin $sandbox $build.buildContext "Build context"
    $dockerfile = Resolve-PathWithin $sandbox $build.dockerfile "Dockerfile"
    $recipeDockerfile = (Join-Path "sandbox" $build.dockerfile).Replace("\", "/")
    if (-not (Test-Path -LiteralPath $context -PathType Container) -or
        -not (Test-Path -LiteralPath $dockerfile -PathType Leaf) -or
        -not ($build.sourceFiles | Where-Object { $_ -ceq $recipeDockerfile })) {
        throw "Recipe paths are invalid for $($build.imageReference)."
    }
}

$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path $output)) {
    New-Item -ItemType Directory -Path $output | Out-Null
}
if (Get-ChildItem -LiteralPath $output -File | Where-Object { $_.Name -match '\.tar$|^manifest\.json$' }) {
    throw "The output directory already contains a provisioning package. Choose a new directory or archive the previous package."
}

$engine = & docker info --format "{{.OSType}}"
if ($LASTEXITCODE -ne 0 -or $engine.Trim() -ne "linux") {
    throw "A running Docker Linux engine is required to build the provisioning package."
}

function Invoke-DockerBuild([string[]]$Arguments) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker build failed with exit code $LASTEXITCODE."
    }
}

foreach ($build in $builds) {
    $context = Resolve-PathWithin $sandbox $build.buildContext "Build context"
    $dockerfile = Resolve-PathWithin $sandbox $build.dockerfile "Dockerfile"
    $arguments = @("build")
    if ($build.pullPinnedBase) { $arguments += "--pull" }
    $arguments += @("--tag", $build.imageReference, "--file", $dockerfile, $context)
    Invoke-DockerBuild $arguments
}

$imageIds = @()
foreach ($build in $builds) {
    $id = (& docker image inspect --format "{{.Id}}" $build.imageReference).Trim()
    if ($LASTEXITCODE -ne 0 -or $id -notmatch '^sha256:[a-f0-9]{64}$') {
        throw "Could not verify the built image ID for $($build.imageReference)."
    }
    $imageIds += $id
}

$archivePath = Join-Path $output $recipeManifest.archiveFile
$imageReferences = @($builds | ForEach-Object { $_.imageReference })
& docker save --output $archivePath @imageReferences
if ($LASTEXITCODE -ne 0) {
    throw "Docker image export failed with exit code $LASTEXITCODE."
}
$archiveInfo = Get-Item -LiteralPath $archivePath
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$images = @()
for ($index = 0; $index -lt $builds.Count; $index++) {
    $build = $builds[$index]
    $images += [ordered]@{
        imageReference = $build.imageReference
        version = $build.version
        dockerfile = $build.dockerfile
        sourceFingerprint = $build.sourceFingerprint
        imageId = $imageIds[$index]
    }
}
$manifest = [ordered]@{
    formatVersion = $recipeManifest.formatVersion
    packageVersion = $recipeManifest.packageVersion
    platform = $recipeManifest.platform
    createdAt = [DateTimeOffset]::UtcNow.ToString("O")
    archiveFile = [System.IO.Path]::GetFileName($archivePath)
    archiveSizeBytes = $archiveInfo.Length
    archiveSha256 = $archiveHash
    images = $images
}
$json = ConvertTo-Json -InputObject $manifest -Depth 12
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $output "manifest.json"), $json, $utf8)

Write-Host "Provisioning package created at $output"
Write-Host "Archive SHA-256: $archiveHash"
Write-Host "Manifest SHA-256: $((Get-FileHash -LiteralPath (Join-Path $output 'manifest.json') -Algorithm SHA256).Hash)"
