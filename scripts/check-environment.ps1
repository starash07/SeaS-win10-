$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $projectRoot "SeaS.sln"
$pluginPath = Join-Path $projectRoot "src\SeaS.App\Plugins\LittleFish\LittleFish.exe"

Write-Host "SeaS development environment" -ForegroundColor Cyan

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw ".NET SDK was not found in PATH. Install the .NET 8 SDK first."
}

$sdkVersions = @(dotnet --list-sdks)
if (-not ($sdkVersions | Where-Object { $_ -match '^8\.' })) {
    throw ".NET 8 SDK was not found. Installed SDKs: $($sdkVersions -join ', ')"
}

$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) {
    throw "Git was not found in PATH."
}

if (-not (Test-Path -LiteralPath $solutionPath)) {
    throw "Solution file is missing: $solutionPath"
}

if (-not (Test-Path -LiteralPath $pluginPath)) {
    throw "Bundled LittleFish executable is missing: $pluginPath"
}

Write-Host "dotnet: $((dotnet --version))"
Write-Host "git: $((git --version))"
Write-Host "solution: OK"
Write-Host "LittleFish plugin: OK"
Write-Host "Environment check passed." -ForegroundColor Green
