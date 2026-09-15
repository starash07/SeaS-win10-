param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $projectRoot "src\SeaS.App\SeaS.App.csproj"
$outputPath = Join-Path $projectRoot "artifacts\dev"
$shortcutFile = Get-ChildItem -LiteralPath $projectRoot -Filter "*.lnk" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name.EndsWith("SeaS.lnk", [System.StringComparison]::OrdinalIgnoreCase) } |
    Select-Object -First 1
$shortcutPath = if ($shortcutFile) { $shortcutFile.FullName } else { $null }

if (Test-Path -LiteralPath $outputPath) {
    $resolvedOutput = (Resolve-Path -LiteralPath $outputPath).Path
    $workspacePrefix = $projectRoot.TrimEnd('\') + '\'
    if (-not $resolvedOutput.StartsWith($workspacePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear output outside the SeaS workspace: $resolvedOutput"
    }

    $runningFromOutput = Get-CimInstance Win32_Process -Filter "Name = 'SeaS.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($resolvedOutput, [System.StringComparison]::OrdinalIgnoreCase) }
    if ($runningFromOutput) {
        throw "The SeaS test build is running. Exit SeaS from the tray before rebuilding."
    }

    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

dotnet build $projectPath `
    -c $Configuration `
    -o $outputPath

if ($LASTEXITCODE -ne 0) {
    throw "SeaS test build failed."
}

$executablePath = Join-Path $outputPath "SeaS.exe"
if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "SeaS test executable was not generated: $executablePath"
}

if ($shortcutPath -and (Test-Path -LiteralPath $shortcutPath)) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executablePath
    $shortcut.WorkingDirectory = $outputPath
    $shortcut.Save()
}

Write-Host "SeaS test build: $executablePath" -ForegroundColor Green
if ($shortcutPath -and (Test-Path -LiteralPath $shortcutPath)) {
    Write-Host "Test shortcut updated: $shortcutPath" -ForegroundColor Green
}
