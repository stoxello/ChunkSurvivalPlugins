[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [string] $Output = "artifacts/plugins",
    [string] $ExpectedVersion,
    [switch] $NoBuild
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packager = Join-Path $PSScriptRoot "package-plugin.ps1"
$projects = Get-ChildItem -LiteralPath $repositoryRoot -Directory |
    Where-Object { $_.Name -like "BlockGame.Plugins.*" } |
    ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Filter "*.csproj" -File
    } |
    Sort-Object FullName

if (-not $projects) {
    throw "No official plugin projects were found in $repositoryRoot"
}

if ([System.IO.Path]::IsPathRooted($Output)) {
    $outputRoot = [System.IO.Path]::GetFullPath($Output)
} else {
    $outputRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot $Output)
    )
}

$packages = foreach ($project in $projects) {
    $manifestPath = Join-Path $project.DirectoryName "plugin.yml"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "plugin.yml was not found beside $($project.FullName)"
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
        $versionLine = Get-Content -LiteralPath $manifestPath |
            Where-Object { $_ -match '^\s*version\s*:' } |
            Select-Object -First 1
        $manifestVersion = ($versionLine -replace '^\s*version\s*:\s*', '').Trim().Trim('"').Trim("'")
        if ($manifestVersion -ne $ExpectedVersion) {
            throw "$manifestPath has version '$manifestVersion'; expected '$ExpectedVersion'."
        }
    }

    $packageArguments = @{
        Project = $project.FullName
        Configuration = $Configuration
        Output = $outputRoot
    }
    if ($NoBuild) { $packageArguments["NoBuild"] = $true }
    & $packager @packageArguments
}

Write-Host "Packaged $($packages.Count) plugins in $outputRoot"
$packages
