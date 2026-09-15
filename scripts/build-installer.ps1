param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "1.1.0",
    [switch]$SkipObfuscation,
    [switch]$SkipSmokeTest,
    [switch]$KeepIntermediate
)

$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$appProject = Join-Path $root "src\SeaS.App\SeaS.App.csproj"
$installerProject = Join-Path $root "src\SeaS.Installer\SeaS.Installer.csproj"
$uninstallerProject = Join-Path $root "src\SeaS.Uninstaller\SeaS.Uninstaller.csproj"
$dist = Join-Path $root "dist\installer"
$publishDir = Join-Path $dist "publish"
$protectedDir = Join-Path $dist "protected"
$obfuscatedDir = Join-Path $dist "obfuscated"
$uninstallerPublishDir = Join-Path $dist "uninstaller-publish"
$installerPublishDir = Join-Path $dist "setup-publish"
$setupDir = Join-Path $root "dist\setup"
$installerAssets = Join-Path $root "src\SeaS.Installer\Assets"
$payloadPath = Join-Path $installerAssets "payload.zip"
$payloadHashPath = Join-Path $installerAssets "payload.sha256"
$obfuscarConfig = Join-Path $dist "obfuscar.xml"
$packageSucceeded = $false

function Assert-WorkspacePath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path outside SeaS workspace: $fullPath"
    }
    return $fullPath
}

function Remove-DirectoryIfExists([string]$Path) {
    $safePath = Assert-WorkspacePath $Path
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        if (-not (Test-Path -LiteralPath $safePath)) {
            return
        }

        try {
            Remove-Item -LiteralPath $safePath -Recurse -Force
            return
        }
        catch {
            if ($attempt -eq 5) {
                throw
            }

            [System.GC]::Collect()
            [System.GC]::WaitForPendingFinalizers()
            Start-Sleep -Milliseconds 400
        }
    }
}

function Remove-FileIfExists([string]$Path) {
    $safePath = Assert-WorkspacePath $Path
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Force
    }
}

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE"
    }
}

function Find-Obfuscar {
    $command = Get-Command "obfuscar.console" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidate = Join-Path $env:USERPROFILE ".dotnet\tools\obfuscar.console.exe"
    if (Test-Path -LiteralPath $candidate) {
        return $candidate
    }

    throw "Obfuscar was not found. Install it with: dotnet tool install -g Obfuscar.GlobalTool"
}

function Invoke-SmokeTest([string]$Executable, [string]$WorkingDirectory) {
    $process = Start-Process `
        -FilePath $Executable `
        -ArgumentList "--package-smoke-test" `
        -WorkingDirectory $WorkingDirectory `
        -WindowStyle Hidden `
        -PassThru
    try {
        if (-not $process.WaitForExit(15000)) {
            Stop-Process -Id $process.Id -Force
            throw "Smoke test timed out: $Executable"
        }
        if ($process.ExitCode -ne 0) {
            throw "Smoke test failed with exit code $($process.ExitCode): $Executable"
        }
    }
    finally {
        $process.Dispose()
    }
}

Remove-DirectoryIfExists $dist
Remove-FileIfExists $payloadPath
Remove-FileIfExists $payloadHashPath
New-Item -ItemType Directory -Force -Path `
    $publishDir, `
    $protectedDir, `
    $obfuscatedDir, `
    $uninstallerPublishDir, `
    $installerPublishDir, `
    $setupDir, `
    $installerAssets | Out-Null

try {
    Write-Host "[1/7] Publishing SeaS V$Version..."
    Invoke-DotNet @(
        "publish", $appProject,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-o", $publishDir,
        "/p:PublishSingleFile=false",
        "/p:PublishReadyToRun=false",
        "/p:DebugType=None",
        "/p:DebugSymbols=false",
        "/p:Version=$Version"
    )
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $protectedDir -Recurse -Force

    Write-Host "[2/7] Publishing lightweight uninstaller..."
    Invoke-DotNet @(
        "publish", $uninstallerProject,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-o", $uninstallerPublishDir,
        "/p:PublishSingleFile=true",
        "/p:PublishTrimmed=true",
        "/p:DebugType=None",
        "/p:DebugSymbols=false",
        "/p:Version=$Version"
    )
    $uninstallerExe = Join-Path $uninstallerPublishDir "SeaS_Uninstall.exe"
    if (-not (Test-Path -LiteralPath $uninstallerExe)) {
        throw "Uninstaller was not generated: $uninstallerExe"
    }
    Copy-Item -LiteralPath $uninstallerExe -Destination (Join-Path $protectedDir "SeaS_Uninstall.exe") -Force

    Write-Host "[3/7] Protecting SeaS managed code..."
    if (-not $SkipObfuscation) {
        $config = @"
<?xml version="1.0" encoding="utf-8" ?>
<Obfuscator>
  <Var name="InPath" value="$protectedDir" />
  <Var name="OutPath" value="$obfuscatedDir" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="HideStrings" value="true" />
  <Var name="ReuseNames" value="true" />
  <Var name="SuppressIldasm" value="true" />
  <Module file="`$(InPath)\SeaS.dll">
    <SkipType name="SeaS.App.App" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.MainWindow" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.BookDetailsWindow" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.EmbeddedReaderControl" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.GroupManagementWindow" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.GroupNameWindow" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.HelpWindow" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.ImageCropWindow" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.MissingBookActionWindow" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.MissingBooksWindow" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.SeaSMessageBox" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.SettingsWindow" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.TrayMenuWindow" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.CoverImageBrushConverter" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="SeaS.App.HighlightTextBlock" skipMethods="true" skipFields="true" skipProperties="true" />
  </Module>
</Obfuscator>
"@
        Set-Content -LiteralPath $obfuscarConfig -Value $config -Encoding UTF8
        $obfuscar = Find-Obfuscar
        & $obfuscar $obfuscarConfig
        if ($LASTEXITCODE -ne 0) {
            throw "Obfuscar failed with exit code $LASTEXITCODE"
        }

        $obfuscatedDll = Join-Path $obfuscatedDir "SeaS.dll"
        if (-not (Test-Path -LiteralPath $obfuscatedDll)) {
            throw "Protected SeaS.dll was not generated."
        }
        Copy-Item -LiteralPath $obfuscatedDll -Destination (Join-Path $protectedDir "SeaS.dll") -Force
    }

    Write-Host "[4/7] Running protected build smoke tests..."
    if (-not $SkipSmokeTest) {
        Invoke-SmokeTest (Join-Path $protectedDir "SeaS.exe") $protectedDir
        Invoke-SmokeTest (Join-Path $protectedDir "SeaS_Uninstall.exe") $protectedDir
    }

    if (Get-ChildItem -LiteralPath $protectedDir -Recurse -Filter "*.pdb" -File) {
        throw "Release payload unexpectedly contains PDB files."
    }

    Write-Host "[5/7] Creating and hashing installer payload..."
    Compress-Archive `
        -Path (Join-Path $protectedDir "*") `
        -DestinationPath $payloadPath `
        -CompressionLevel Optimal `
        -Force
    $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
    Set-Content -LiteralPath $payloadHashPath -Value $payloadHash -Encoding ASCII

    Write-Host "[6/7] Publishing single-file installer..."
    Invoke-DotNet @(
        "publish", $installerProject,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-o", $installerPublishDir,
        "/p:PublishSingleFile=true",
        "/p:IncludeNativeLibrariesForSelfExtract=true",
        "/p:EnableCompressionInSingleFile=true",
        "/p:DebugType=None",
        "/p:DebugSymbols=false",
        "/p:Version=$Version"
    )

    Write-Host "[7/7] Finalizing package..."
    $builtInstaller = Join-Path $installerPublishDir "SeaS_Setup.exe"
    if (-not (Test-Path -LiteralPath $builtInstaller)) {
        throw "Installer was not generated: $builtInstaller"
    }
    if (-not $SkipSmokeTest) {
        Invoke-SmokeTest $builtInstaller $installerPublishDir
    }

    $finalInstaller = Join-Path $setupDir "SeaS_Setup_v$Version.exe"
    Copy-Item -LiteralPath $builtInstaller -Destination $finalInstaller -Force

    $versionInfo = (Get-Item -LiteralPath $finalInstaller).VersionInfo
    if ($versionInfo.ProductVersion -ne $Version) {
        throw "Installer version mismatch. Expected $Version, got $($versionInfo.ProductVersion)."
    }

    Write-Host "Installer created: $finalInstaller"
    $packageSucceeded = $true
}
finally {
    Remove-FileIfExists $payloadPath
    Remove-FileIfExists $payloadHashPath
    if ($packageSucceeded -and -not $KeepIntermediate) {
        Remove-DirectoryIfExists $dist
    }
}
