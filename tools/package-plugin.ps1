[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Project,
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [string] $Output = "artifacts/plugins",
    [switch] $NoBuild
)

$ErrorActionPreference = "Stop"

$projectPath = (Resolve-Path -LiteralPath $Project).Path
$projectDirectory = Split-Path -Parent $projectPath
$manifestPath = Join-Path $projectDirectory "plugin.yml"
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "plugin.yml was not found beside $projectPath"
}

$manifest = @{}
foreach ($source in Get-Content -LiteralPath $manifestPath) {
    $line = $source.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith("#")) { continue }

    $colon = $line.IndexOf(":")
    if ($colon -le 0) { continue }

    $key = $line.Substring(0, $colon).Trim().ToLowerInvariant()
    $value = $line.Substring($colon + 1).Trim().Trim('"').Trim("'")
    $manifest[$key] = $value
}

foreach ($required in @("name", "version", "main")) {
    if (-not $manifest.ContainsKey($required) -or
        [string]::IsNullOrWhiteSpace($manifest[$required])) {
        throw "'$required' is required in $manifestPath"
    }
}

if (-not $NoBuild) {
    & dotnet build $projectPath -c $Configuration --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Plugin build failed: $projectPath" }
}

[xml] $projectXml = Get-Content -LiteralPath $projectPath
$targetFramework = [string](
    $projectXml.Project.PropertyGroup.TargetFramework | Select-Object -First 1
)
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "The project must declare a TargetFramework: $projectPath"
}

$buildDirectory = [System.IO.Path]::Combine(
    $projectDirectory,
    "bin",
    $Configuration,
    $targetFramework
)
$mainAssembly = Join-Path $buildDirectory $manifest["main"]
if (-not (Test-Path -LiteralPath $mainAssembly)) {
    throw "The manifest main assembly was not built: $mainAssembly"
}

if ([System.IO.Path]::IsPathRooted($Output)) {
    $outputRoot = [System.IO.Path]::GetFullPath($Output)
} else {
    $outputRoot = [System.IO.Path]::GetFullPath(
        (Join-Path (Get-Location) $Output)
    )
}

$safeName = $manifest["name"] -replace '[^A-Za-z0-9._-]', '_'
$packageBase = "$safeName-$($manifest['version'])"
$stage = Join-Path $outputRoot $safeName
$archive = Join-Path $outputRoot "$packageBase.zip"

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stage "plugin.yml")
foreach ($file in Get-ChildItem -LiteralPath $buildDirectory -File) {
    $excluded = $file.Name -eq "plugin.yml" -or
        $file.Name -like "*.pdb" -or
        $file.Name -like "*.zip" -or
        $file.Name -eq "BlockGame.PluginApi.dll" -or
        $file.Name -eq "BlockGame.PluginApi.xml"
    if ($excluded) { continue }

    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $stage $file.Name)
}

Compress-Archive -LiteralPath $stage -DestinationPath $archive
Write-Host "Plugin directory: $stage"
Write-Host "Plugin archive:   $archive"

[pscustomobject]@{
    Name = $manifest["name"]
    Version = $manifest["version"]
    Directory = $stage
    Archive = $archive
}
