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
    .\patch.ps1 -GameDir "..." -Fresh

.EXAMPLE
    # Restore original unpatched DLL
    .\patch.ps1 -GameDir "..." -Restore
#>

param(
    [string] $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\RimWorld",
    [switch] $Fresh,
    [switch] $Restore,
    [string] $Skip = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot

# ── Validate game dir ─────────────────────────────────────────────────────────
$ManagedDir = Join-Path $GameDir "RimWorldWin64_Data\Managed"
if (-not (Test-Path (Join-Path $ManagedDir "Assembly-CSharp.dll"))) {
    Write-Error "Assembly-CSharp.dll not found in: $ManagedDir`nCheck -GameDir path."
}

# ── Detect release (pre-built) vs dev (build from source) ────────────────────
$ReleasePatcher = Join-Path $ScriptDir "PatcherTool.exe"
$ReleaseHelpers = Join-Path $ScriptDir "RimWorldStartupHelpers.dll"
$IsRelease      = (Test-Path $ReleasePatcher) -and (Test-Path $ReleaseHelpers)

if ($IsRelease) {
    $HelpersBin = $ScriptDir
} else {
    $env:RimWorldDir = $GameDir
    $HelpersProj = Join-Path $ScriptDir "src\Helpers\Helpers.csproj"
    $PatcherProj = Join-Path $ScriptDir "src\PatcherTool\PatcherTool.csproj"
    $HelpersBin  = Join-Path $ScriptDir "src\Helpers\bin\Release\net472"
}

# ── Restore mode ──────────────────────────────────────────────────────────────
if ($Restore) {
    Write-Host "`n[Hyperdrive] Restoring original DLL..." -ForegroundColor Cyan
    $ExtraArgs = @($ManagedDir, $HelpersBin, "--restore")
    if ($IsRelease) { & $ReleasePatcher @ExtraArgs }
    else            { dotnet run --project $PatcherProj -c Release -- @ExtraArgs }
    if ($LASTEXITCODE -ne 0) { exit 1 }

    $HelperDeployed = Join-Path $ManagedDir "RimWorldStartupHelpers.dll"
    if (Test-Path $HelperDeployed) {
        Remove-Item $HelperDeployed
        Write-Host "[Hyperdrive] Removed RimWorldStartupHelpers.dll"
    }
    Write-Host "[Hyperdrive] Game restored to original state." -ForegroundColor Green
    exit 0
}

# ── Build (dev mode only) ─────────────────────────────────────────────────────
if (-not $IsRelease) {
    Write-Host "`n[Hyperdrive] Building RimWorldStartupHelpers..." -ForegroundColor Cyan
    dotnet build $HelpersProj -c Release -v quiet
    if ($LASTEXITCODE -ne 0) { Write-Error "Helpers build failed." }

    Write-Host "[Hyperdrive] Building PatcherTool..." -ForegroundColor Cyan
    dotnet build $PatcherProj -c Release -v quiet
    if ($LASTEXITCODE -ne 0) { Write-Error "PatcherTool build failed." }
}

# ── Run patcher ───────────────────────────────────────────────────────────────
Write-Host "[Hyperdrive] Patching $ManagedDir ..." -ForegroundColor Cyan

$ExtraArgs = @($ManagedDir, $HelpersBin)
if ($Fresh)       { $ExtraArgs += "--fresh" }
if ($Skip -ne "") { $ExtraArgs += "--skip=$Skip" }

if ($IsRelease) { & $ReleasePatcher @ExtraArgs }
else            { dotnet run --project $PatcherProj -c Release -- @ExtraArgs }
if ($LASTEXITCODE -ne 0) { Write-Error "Patching failed." }

Write-Host "`n[Hyperdrive] Done! Launch RimWorld and enjoy faster loading." -ForegroundColor Green
