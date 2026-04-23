#!/usr/bin/env pwsh
# Packs, uninstalls, and reinstalls roslyn-mcp and roslyn-cli from the local build.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$nupkgDir = Join-Path $root 'nupkg'
$version = '0.4.0-local'

$tools = @(
    @{ PackageId = 'RoslynMcp.Server'; Project = Join-Path $root 'src\RoslynMcp.Server\RoslynMcp.Server.csproj' }
    @{ PackageId = 'RoslynMcp.Cli';    Project = Join-Path $root 'src\RoslynMcp.Cli\RoslynMcp.Cli.csproj' }
)

foreach ($tool in $tools) {
    $packageId = $tool.PackageId
    $project = $tool.Project

    Write-Host "Packing $packageId $version..." -ForegroundColor Cyan
    dotnet pack $project -c Release -o $nupkgDir /p:Version=$version
    if ($LASTEXITCODE -ne 0) { Write-Error "Pack failed for $packageId."; exit 1 }

    Write-Host "Uninstalling existing global tool $packageId..." -ForegroundColor Cyan
    dotnet tool uninstall -g $packageId 2>$null
    # Ignore exit code — tool may not be installed

    Write-Host "Installing $packageId $version from local package..." -ForegroundColor Cyan
    dotnet tool install -g $packageId --version $version --add-source $nupkgDir
    if ($LASTEXITCODE -ne 0) { Write-Error "Install failed for $packageId."; exit 1 }
}

Write-Host "Done. 'roslyn-mcp' and 'roslyn-cli' are now running your local build ($version)." -ForegroundColor Green
