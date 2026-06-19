#!/usr/bin/env pwsh
# Packs, uninstalls, and reinstalls roslyn-mcp and roslyn-cli from the local build.
#
# The base version lives in src/Directory.Build.props (<VersionPrefix>). This script
# appends a '-local' suffix so locally-installed dev builds are clearly distinguishable
# from released packages — e.g. base 0.5.0 -> installed 0.5.0-local. A normal
# `dotnet pack` (no suffix) still produces the clean release version.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$nupkgDir = Join-Path $root 'nupkg'

$propsPath = Join-Path $root 'src/Directory.Build.props'
$prefix = (Select-Xml -Path $propsPath -XPath '/Project/PropertyGroup/VersionPrefix').Node.InnerText.Trim()
if ([string]::IsNullOrWhiteSpace($prefix)) {
    Write-Error "Could not read <VersionPrefix> from $propsPath."
    exit 1
}
$suffix = 'local'
$version = "$prefix-$suffix"

$tools = @(
    @{ PackageId = 'RoslynMcp.Server'; Project = Join-Path $root 'src\RoslynMcp.Server\RoslynMcp.Server.csproj' }
    @{ PackageId = 'RoslynMcp.Cli';    Project = Join-Path $root 'src\RoslynMcp.Cli\RoslynMcp.Cli.csproj' }
)

foreach ($tool in $tools) {
    $packageId = $tool.PackageId
    $project = $tool.Project

    # Base version comes from Directory.Build.props; we only append the '-local' suffix.
    Write-Host "Packing $packageId $version..." -ForegroundColor Cyan
    dotnet pack $project -c Release -o $nupkgDir --version-suffix $suffix
    if ($LASTEXITCODE -ne 0) { Write-Error "Pack failed for $packageId."; exit 1 }

    Write-Host "Uninstalling existing global tool $packageId..." -ForegroundColor Cyan
    dotnet tool uninstall -g $packageId 2>$null
    # Ignore exit code — tool may not be installed

    Write-Host "Installing $packageId $version from local package..." -ForegroundColor Cyan
    dotnet tool install -g $packageId --version $version --add-source $nupkgDir
    if ($LASTEXITCODE -ne 0) { Write-Error "Install failed for $packageId."; exit 1 }
}

Write-Host "Done. 'roslyn-mcp' and 'roslyn-cli' are now running your local build ($version)." -ForegroundColor Green
