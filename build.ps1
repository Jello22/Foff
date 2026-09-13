param(
    [Parameter(Mandatory=$true)]
    [string]$ValheimDir,

    [Parameter(Mandatory=$true)]
    [string]$LibDir,

    [string]$InstallDir = "",
    [switch]$Install
)

$ErrorActionPreference = "Stop"

# Normalize user-supplied paths. Directory.Exists is intentionally used here
# instead of Test-Path so spaces/parentheses in "Program Files (x86)" cannot
# get tangled up with PowerShell provider parsing.
$ValheimDir = $ValheimDir.Trim().Trim('"').Trim("'")
$LibDir     = $LibDir.Trim().Trim('"').Trim("'")

if (-not [System.IO.Directory]::Exists($ValheimDir)) {
    throw "Valheim folder not found: [$ValheimDir]"
}
if (-not [System.IO.Directory]::Exists($LibDir)) {
    throw "Library folder not found: [$LibDir]"
}

$ValheimDir = [System.IO.Path]::GetFullPath($ValheimDir)
$LibDir     = [System.IO.Path]::GetFullPath($LibDir)

# LibDir may be a BepInEx root or the core folder itself.
$coreCandidate = [System.IO.Path]::Combine($LibDir, 'core')
if ([System.IO.File]::Exists([System.IO.Path]::Combine($coreCandidate, 'BepInEx.dll')) -and
    -not [System.IO.File]::Exists([System.IO.Path]::Combine($LibDir, 'BepInEx.dll'))) {
    $LibDir = $coreCandidate
}

$managed = [System.IO.Path]::Combine($ValheimDir, 'valheim_Data', 'Managed')
$required = @(
    [System.IO.Path]::Combine($LibDir, 'BepInEx.dll'),
    [System.IO.Path]::Combine($LibDir, '0Harmony.dll'),
    [System.IO.Path]::Combine($managed, 'UnityEngine.CoreModule.dll'),
    [System.IO.Path]::Combine($managed, 'UnityEngine.InputLegacyModule.dll')
)

foreach ($file in $required) {
    if (-not [System.IO.File]::Exists($file)) {
        throw "Missing required reference: $file"
    }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw ".NET SDK was not found. Install a .NET SDK or build FOff.csproj in Visual Studio."
}

Write-Host "Building F Off" -ForegroundColor Cyan
Write-Host "  Valheim:      $ValheimDir"
Write-Host "  Managed:      $managed"
Write-Host "  BepInEx libs: $LibDir"
Write-Host "  dotnet:       $($dotnet.Source)"

dotnet build "$PSScriptRoot\FOff\FOff.csproj" -c Release `
    -p:ValheimDir="$ValheimDir" `
    -p:BepInExLibDir="$LibDir"

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = "$PSScriptRoot\FOff\bin\Release\netstandard2.0\FOff.dll"
if (-not [System.IO.File]::Exists($dll)) {
    throw "Build reported success but FOff.dll was not found at: $dll"
}

Write-Host "Built: $dll" -ForegroundColor Green

if ($Install) {
    if (-not $InstallDir) {
        throw "-Install was specified without -InstallDir. For Gale, point -InstallDir at the active profile's BepInEx\plugins\FOff folder."
    }

    $InstallDir = $InstallDir.Trim().Trim('"').Trim("'")
    [System.IO.Directory]::CreateDirectory($InstallDir) | Out-Null
    $destination = [System.IO.Path]::Combine($InstallDir, 'FOff.dll')
    Copy-Item -LiteralPath $dll -Destination $destination -Force
    Write-Host "Installed: $destination" -ForegroundColor Green
}
