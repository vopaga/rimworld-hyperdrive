<#
.SYNOPSIS
    RimWorld Hyperdrive — one-click build + patch script.

.PARAMETER GameDir
    Path to the root of your RimWorld installation.
    Defaults to the standard Steam path on Windows.

.PARAMETER Fresh
    Delete the existing Assembly-CSharp.dll.original backup before patching.
    Use this after a Steam game update to re-detect the new clean DLL.

.PARAMETER Restore
    Restore Assembly-CSharp.dll from backup and remove our helper DLL.
    Reverts the game to its original unmodified state.

.PARAMETER Skip
    Comma-separated patch numbers to skip (for debugging).
    Example: -Skip "3,4"

.EXAMPLE
    # Standard patch (Steam default path)
    .\patch.ps1

.EXAMPLE
    # Custom game directory
    .\patch.ps1 -GameDir "D:\Games\RimWorld"

.EXAMPLE
    # After a Steam update — force fresh re-detection
    .\patch.ps1 -GameDir "D:\Games\RimWorld" -Fresh

.EXAMPLE
    # Restore original unpatched DLL
    .\patch.ps1 -GameDir "D:\Games\RimWorld" -Restore
#>

param(
    [string] $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\RimWorld",
    [switch] $Fresh,
    [switch] $Restore,
    [string] $Skip = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot

function Write-Step  { param($msg) Write-Host "`n[Hyperdrive] $msg" -ForegroundColor Cyan }
function Write-OK    { param($msg) Write-Host "[Hyperdrive] $msg" -ForegroundColor Green }
function Write-Fail  { param($msg) Write-Host "`n[Hyperdrive] ERROR: $msg" -ForegroundColor Red }
function Write-Warn  { param($msg) Write-Host "[Hyperdrive] WARNING: $msg" -ForegroundColor Yellow }

# ── OS check ──────────────────────────────────────────────────────────────────
if (-not $IsWindows) {
    Write-Fail "RimWorld Hyperdrive only supports Windows."
    exit 1
}

# ── .NET SDK check ────────────────────────────────────────────────────────────
Write-Step "Checking prerequisites..."
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Fail ".NET SDK not found.`n  Install it from: https://dotnet.microsoft.com/download`n  Requires .NET 8 or newer."
    exit 1
}
$sdkVersion = (dotnet --version 2>&1)
$major = [int]($sdkVersion -split '\.')[0]
if ($major -lt 8) {
    Write-Fail ".NET SDK $sdkVersion is too old. Requires .NET 8+.`n  Install from: https://dotnet.microsoft.com/download"
    exit 1
}
Write-OK "Found .NET SDK $sdkVersion"

# ── Source files check ────────────────────────────────────────────────────────
$HelpersProj = Join-Path $ScriptDir "src\Helpers\Helpers.csproj"
$PatcherProj = Join-Path $ScriptDir "src\PatcherTool\PatcherTool.csproj"
if (-not (Test-Path $HelpersProj) -or -not (Test-Path $PatcherProj)) {
    Write-Fail "Source files not found. Make sure you extracted the full ZIP and are running patch.ps1 from its folder."
    exit 1
}

# ── Game dir check ────────────────────────────────────────────────────────────
$ManagedDir  = Join-Path $GameDir "RimWorldWin64_Data\Managed"
$TargetDll   = Join-Path $ManagedDir "Assembly-CSharp.dll"
$BackupDll   = Join-Path $ManagedDir "Assembly-CSharp.dll.original"

if (-not (Test-Path $GameDir)) {
    Write-Fail "Game directory not found: $GameDir`n  Use -GameDir to specify the correct path, e.g.:`n    .\patch.ps1 -GameDir `"D:\Games\RimWorld`""
    exit 1
}
if (-not (Test-Path $TargetDll)) {
    Write-Fail "Assembly-CSharp.dll not found in: $ManagedDir`n  This doesn't look like a RimWorld installation folder.`n  Use -GameDir to specify the correct path."
    exit 1
}
Write-OK "Found RimWorld at: $GameDir"

# ── Expose game dir to MSBuild (HintPaths in Helpers.csproj) ─────────────────
$env:RimWorldDir = $GameDir
$HelpersBin = Join-Path $ScriptDir "src\Helpers\bin\Release\net472"

# ── Restore mode ──────────────────────────────────────────────────────────────
if ($Restore) {
    if (-not (Test-Path $BackupDll)) {
        Write-Warn "No backup found at: $BackupDll`n  The game may already be unpatched, or was never patched with Hyperdrive."
        exit 0
    }
    Write-Step "Restoring original DLL..."
    dotnet run --project $PatcherProj -c Release -- $ManagedDir $HelpersBin --restore
    if ($LASTEXITCODE -ne 0) { Write-Fail "Restore failed."; exit 1 }

    $HelperDeployed = Join-Path $ManagedDir "RimWorldStartupHelpers.dll"
    if (Test-Path $HelperDeployed) {
        Remove-Item $HelperDeployed
        Write-OK "Removed RimWorldStartupHelpers.dll"
    }
    Write-OK "Game restored to original state."
    exit 0
}

# ── Already patched warning ───────────────────────────────────────────────────
$HelperDeployed = Join-Path $ManagedDir "RimWorldStartupHelpers.dll"
if ((Test-Path $BackupDll) -and (Test-Path $HelperDeployed) -and -not $Fresh) {
    Write-Warn "Game appears to already be patched. Re-patching from backup (idempotent).`n  Use -Fresh if you updated RimWorld via Steam."
}

# ── Build ─────────────────────────────────────────────────────────────────────
Write-Step "Building RimWorldStartupHelpers..."
dotnet build $HelpersProj -c Release -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Fail "Helpers build failed. Check that RimWorld is installed at:`n  $GameDir"
    exit 1
}

Write-Step "Building PatcherTool..."
dotnet build $PatcherProj -c Release -v quiet
if ($LASTEXITCODE -ne 0) { Write-Fail "PatcherTool build failed."; exit 1 }

# ── Run patcher ───────────────────────────────────────────────────────────────
Write-Step "Patching $ManagedDir ..."

$ExtraArgs = @()
if ($Fresh)       { $ExtraArgs += "--fresh" }
if ($Skip -ne "") { $ExtraArgs += "--skip=$Skip" }

dotnet run --project $PatcherProj -c Release -- $ManagedDir $HelpersBin @ExtraArgs
if ($LASTEXITCODE -ne 0) { Write-Fail "Patching failed."; exit 1 }

Write-OK "Done! Launch RimWorld and enjoy faster loading."
