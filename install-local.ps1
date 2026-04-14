#!/usr/bin/env pwsh
# Packs, uninstalls, and reinstalls roslyn-mcp from the local build.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$nupkgDir = Join-Path $root 'nupkg'
$project = Join-Path $root 'src\RoslynMcp.Server\RoslynMcp.Server.csproj'
$packageId = 'RoslynMcp.Server'
$version = '0.4.0-local'

Write-Host "Packing $packageId $version..." -ForegroundColor Cyan
dotnet pack $project -c Release -o $nupkgDir /p:Version=$version
if ($LASTEXITCODE -ne 0) { Write-Error 'Pack failed.'; exit 1 }

Write-Host "Uninstalling existing global tool..." -ForegroundColor Cyan
dotnet tool uninstall -g $packageId 2>$null
# Ignore exit code — tool may not be installed

Write-Host "Installing $packageId $version from local package..." -ForegroundColor Cyan
dotnet tool install -g $packageId --version $version --add-source $nupkgDir
if ($LASTEXITCODE -ne 0) { Write-Error 'Install failed.'; exit 1 }

Write-Host "Done. 'roslyn-mcp' is now running your local build ($version)." -ForegroundColor Green
