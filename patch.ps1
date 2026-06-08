<#
.SYNOPSIS
    RimWorld Hyperdrive - one-click build + patch script.

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
    # After a Steam update - force fresh re-detection
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
if (-not $ScriptDir) {
    Write-Host "[Hyperdrive] ERROR: Cannot determine script location. Run patch.ps1 from its extracted folder, not via a pipe." -ForegroundColor Red
    exit 1
}

function Write-Step { param($msg) Write-Host "`n[Hyperdrive] $msg" -ForegroundColor Cyan }
function Write-OK { param($msg) Write-Host "[Hyperdrive] $msg" -ForegroundColor Green }
function Write-Fail { param($msg) Write-Host "`n[Hyperdrive] ERROR: $msg" -ForegroundColor Red }
function Write-Warn { param($msg) Write-Host "[Hyperdrive] WARNING: $msg" -ForegroundColor Yellow }

# ------------------------------------------------------------------------------- OS check -------------------------------------------------------------------------------
# $IsWindows is $null in Windows PowerShell 5.1 (only defined in PS Core 6+),
# so check for explicit $false to avoid false positives on Windows.
if ($IsWindows -eq $false) {
    Write-Fail "RimWorld Hyperdrive only supports Windows."
    exit 1
}

# ------------------------------------------------------------------------------- .NET SDK check -------------------------------------------------------------------------------
Write-Step "Checking prerequisites..."
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Fail ".NET SDK not found.`n  Install it from: https://dotnet.microsoft.com/download`n  Requires .NET 8 or newer."
    exit 1
}
# Take the last line: stderr noise (telemetry notice, global.json roll-forward
# warning) can prepend extra lines and break naive parsing.
$sdkVersion = (dotnet --version 2>$null | Select-Object -Last 1)
$majorStr = ($sdkVersion -split '\.')[0]
if ($majorStr -notmatch '^\d+$') {
    Write-Fail "Could not parse .NET SDK version: $sdkVersion"
    exit 1
}
$major = [int]$majorStr
if ($major -lt 8) {
    Write-Fail ".NET SDK $sdkVersion is too old. Requires .NET 8+.`n  Install from: https://dotnet.microsoft.com/download"
    exit 1
}
Write-OK "Found .NET SDK $sdkVersion"

# ------------------------------------------------------------------------------- Source files check -------------------------------------------------------------------------------
$HelpersProj = Join-Path $ScriptDir "src\Helpers\Helpers.csproj"
$PatcherProj = Join-Path $ScriptDir "src\PatcherTool\PatcherTool.csproj"
if (-not (Test-Path $HelpersProj) -or -not (Test-Path $PatcherProj)) {
    Write-Fail "Source files not found. Make sure you extracted the full ZIP and are running patch.ps1 from its folder."
    exit 1
}

# ------------------------------------------------------------------------------- Game dir check -------------------------------------------------------------------------------
$ManagedDir = Join-Path $GameDir "RimWorldWin64_Data\Managed"
$TargetDll = Join-Path $ManagedDir "Assembly-CSharp.dll"
$BackupDll = Join-Path $ManagedDir "Assembly-CSharp.dll.original"
$HelperDeployed = Join-Path $ManagedDir "RimWorldStartupHelpers.dll"

if (-not (Test-Path $GameDir)) {
    Write-Fail "Game directory not found: $GameDir`n  Use -GameDir to specify the correct path, e.g.:`n    .\patch.ps1 -GameDir `"D:\Games\RimWorld`""
    exit 1
}
if (-not (Test-Path $TargetDll)) {
    Write-Fail "Assembly-CSharp.dll not found in: $ManagedDir`n  This doesn't look like a RimWorld installation folder.`n  Use -GameDir to specify the correct path."
    exit 1
}
Write-OK "Found RimWorld at: $GameDir"

# ------------------------------------------------------------------------------- Game-running check -------------------------------------------------------------------------------
# A running game locks Assembly-CSharp.dll / RimWorldStartupHelpers.dll, which makes
# both patch (File.Move) and restore (Remove-Item) fail with a cryptic IO error.
if (Get-Process -Name "RimWorldWin64" -ErrorAction SilentlyContinue) {
    Write-Fail "RimWorld is running. Close the game completely, then re-run this script."
    exit 1
}

# ------------------------------------------------------------------------------- Expose game dir to MSBuild (HintPaths in Helpers.csproj) -------------------------------------------------------------------------------
$env:RimWorldDir = $GameDir
$HelpersBin = Join-Path $ScriptDir "src\Helpers\bin\Release\net472"

# ------------------------------------------------------------------------------- Restore mode -------------------------------------------------------------------------------
if ($Restore) {
    if (-not (Test-Path $BackupDll)) {
        Write-Warn "No backup found at: $BackupDll`n  The game may already be unpatched, or was never patched with Hyperdrive."
        exit 0
    }
    Write-Step "Restoring original DLL..."
    dotnet run --project $PatcherProj -c Release -v quiet -- $ManagedDir $HelpersBin --restore
    if ($LASTEXITCODE -ne 0) { Write-Fail "Restore failed."; exit 1 }

    if (Test-Path $HelperDeployed) {
        try {
            Remove-Item $HelperDeployed -ErrorAction Stop
            Write-OK "Removed RimWorldStartupHelpers.dll"
        }
        catch {
            Write-Warn "Could not remove RimWorldStartupHelpers.dll: $_`n  Close RimWorld and try again."
        }
    }
    Write-OK "Game restored to original state."
    exit 0
}

# ------------------------------------------------------------------------------- Fresh-backup safety guard -------------------------------------------------------------------------------
# -Fresh discards the existing backup and captures the CURRENT DLL as the new clean
# original. If the current DLL is still patched (no Steam update happened), this
# permanently destroys the clean backup. Refuse unless the user confirms.
if ($Fresh -and (Test-Path $HelperDeployed)) {
    Write-Warn "RimWorldStartupHelpers.dll is present - the current Assembly-CSharp.dll may already be patched."
    Write-Warn "-Fresh will capture the CURRENT DLL as the new clean backup, overwriting the existing one."
    Write-Warn "Only continue if Steam has just re-downloaded a fresh, unpatched DLL."
    $answer = Read-Host "Continue with -Fresh? (y/N)"
    if ($answer -notmatch '^[Yy]') {
        Write-Fail "Aborted. Run without -Fresh to re-patch from the existing clean backup."
        exit 1
    }
}

# ------------------------------------------------------------------------------- Already patched warning -------------------------------------------------------------------------------
if ((Test-Path $BackupDll) -and (Test-Path $HelperDeployed) -and -not $Fresh) {
    Write-Warn "Game appears to already be patched. Re-patching from backup (idempotent).`n  Use -Fresh if you updated RimWorld via Steam."
}

# ------------------------------------------------------------------------------- Build -------------------------------------------------------------------------------
Write-Step "Building RimWorldStartupHelpers..."
dotnet build $HelpersProj -c Release -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Fail "Helpers build failed. Check that RimWorld is installed at:`n  $GameDir"
    exit 1
}

Write-Step "Building PatcherTool..."
dotnet build $PatcherProj -c Release -v quiet
if ($LASTEXITCODE -ne 0) { Write-Fail "PatcherTool build failed."; exit 1 }

# ------------------------------------------------------------------------------- Run patcher -------------------------------------------------------------------------------
Write-Step "Patching $ManagedDir ..."

$ExtraArgs = @()
if ($Fresh) { $ExtraArgs += "--fresh" }
if ($Skip -ne "") {
    if ($Skip -notmatch '^\d+(,\d+)*$') {
        Write-Fail "Invalid -Skip value: '$Skip'. Use comma-separated patch numbers, e.g. -Skip `"3,4`"."
        exit 1
    }
    $ExtraArgs += "--skip=$Skip"
}

dotnet run --project $PatcherProj -c Release --no-build -- $ManagedDir $HelpersBin @ExtraArgs
if ($LASTEXITCODE -ne 0) { Write-Fail "Patching failed."; exit 1 }

Write-OK "Done! Launch RimWorld and enjoy faster loading."
