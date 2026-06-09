#!/usr/bin/env bash
# RimWorld Hyperdrive — one-click build + patch script for Linux (native & proton).

# colors for logging
red()  { printf '\n\033[0;31m[Hyperdrive] ERROR: %s\033[0m\n' "$*" >&2; }
cyan() { printf '\n\033[0;36m[Hyperdrive] %s\033[0m\n' "$*"; }
grn()  { printf '\033[0;32m[Hyperdrive] %s\033[0m\n' "$*"; }
warn() { printf '\033[0;33m[Hyperdrive] WARNING: %s\033[0m\n' "$*"; }

# Usage docs
usage() {
    cat <<'EOF'
Usage: ./patch.sh [OPTIONS]

Options:
  -g, --game-dir DIR   Path to RimWorld root (auto-detected from Steam default)
  -f, --fresh          Discard existing backup, capture current DLL as clean original
  -r, --restore        Restore Assembly-CSharp.dll from backup
  -s, --skip LIST      Comma-separated patch numbers to skip (e.g. "3,4")
  -h, --help           Show this help

Examples:
  ./patch.sh
  ./patch.sh --game-dir ~/.steam/steam/steamapps/common/RimWorld
  ./patch.sh --fresh
  ./patch.sh --restore
EOF
    exit 0
}

# arg parsing
GAME_DIR=""
FRESH=false
RESTORE=false
SKIP=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        -g|--game-dir)
            [[ -z "${2:-}" ]] && { red "--game-dir requires a path argument."; exit 1; }
            GAME_DIR="$2"; shift 2 ;;
        -f|--fresh)    FRESH=true; shift ;;
        -r|--restore)  RESTORE=true; shift ;;
        -s|--skip)
            [[ -z "${2:-}" ]] && { red "--skip requires a comma-separated list of patch numbers."; exit 1; }
            SKIP="$2"; shift 2 ;;
        -h|--help)     usage ;;
        *) red "Unknown option: $1"; usage ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# .NET SDK check
cyan "Checking prerequisites..."

if ! command -v dotnet &>/dev/null; then
    red ".NET SDK not found."
    echo "  Install it from: https://dotnet.microsoft.com/download"
    echo "  Requires .NET 8 or newer."
    exit 1
fi

SDK_VERSION="$(dotnet --version 2>/dev/null | tail -1)"
MAJOR="${SDK_VERSION%%.*}"
if ! [[ "$MAJOR" =~ ^[0-9]+$ ]]; then
    red "Could not parse .NET SDK version: $SDK_VERSION"
    exit 1
fi
if (( MAJOR < 8 )); then
    red ".NET SDK $SDK_VERSION is too old. Requires .NET 8+."
    echo "  Install from: https://dotnet.microsoft.com/download"
    exit 1
fi
grn "Found .NET SDK $SDK_VERSION"

# Source files check
HELPERS_PROJ="$SCRIPT_DIR/src/Helpers/Helpers.csproj"
PATCHER_PROJ="$SCRIPT_DIR/src/PatcherTool/PatcherTool.csproj"

if [[ ! -f "$HELPERS_PROJ" ]] || [[ ! -f "$PATCHER_PROJ" ]]; then
    red "Source files not found. Make sure you extracted the full archive and are running patch.sh from its folder."
    exit 1
fi

# Auto-detect game dir from common steam paths
if [[ -z "$GAME_DIR" ]]; then
    CANDIDATES=(
        "$HOME/.steam/steam/steamapps/common/RimWorld"
        "$HOME/.local/share/Steam/steamapps/common/RimWorld"
        "$HOME/.var/app/com.valvesoftware.Steam/.steam/steam/steamapps/common/RimWorld"
    )
    for candidate in "${CANDIDATES[@]}"; do
        if [[ -d "$candidate" ]]; then
            GAME_DIR="$candidate"
            break
        fi
    done
    if [[ -z "$GAME_DIR" ]]; then
        red "Could not auto-detect RimWorld installation."
        echo "  Use --game-dir to specify the path, e.g.:"
        echo "    ./patch.sh --game-dir ~/.steam/steam/steamapps/common/RimWorld"
        exit 1
    fi
fi

if [[ ! -d "$GAME_DIR" ]]; then
    red "Game directory not found: $GAME_DIR"
    echo "  Use --game-dir to specify the correct path."
    exit 1
fi

# Probe the filesystem so this works for both native Linux (RimWorldLinux_Data) and Proton/Wine installs (RimWorldWin64_Data).
if [[ -d "$GAME_DIR/RimWorldLinux_Data/Managed" ]]; then
    DATA_FOLDER="RimWorldLinux_Data"
elif [[ -d "$GAME_DIR/RimWorldWin64_Data/Managed" ]]; then
    DATA_FOLDER="RimWorldWin64_Data"
else
    red "Could not find RimWorldLinux_Data/Managed or RimWorldWin64_Data/Managed in: $GAME_DIR"
    echo "  This doesn't look like a RimWorld installation folder."
    echo "  Use --game-dir to specify the correct path."
    exit 1
fi

MANAGED_DIR="$GAME_DIR/$DATA_FOLDER/Managed"
TARGET_DLL="$MANAGED_DIR/Assembly-CSharp.dll"
BACKUP_DLL="$MANAGED_DIR/Assembly-CSharp.dll.original"

if [[ ! -f "$TARGET_DLL" ]]; then
    red "Assembly-CSharp.dll not found in: $MANAGED_DIR"
    echo "  This doesn't look like a RimWorld installation folder."
    echo "  Use --game-dir to specify the correct path."
    exit 1
fi
grn "Found RimWorld at: $GAME_DIR ($DATA_FOLDER)"

# check if the game is running
if pgrep -x "RimWorldLinux" &>/dev/null; then
    red "RimWorld is running. Close the game completely, then re-run this script."
    exit 1
fi

# expose game dir to MSBuild
export RIMWORLDDIR="$GAME_DIR"
HELPERS_BIN="$SCRIPT_DIR/src/Helpers/bin/Release/net472"

# Restore mode
if $RESTORE; then
    if [[ ! -f "$BACKUP_DLL" ]]; then
        warn "No backup found at: $BACKUP_DLL"
        echo "  The game may already be unpatched, or was never patched with Hyperdrive."
        exit 0
    fi
    cyan "Restoring original DLL..."
    dotnet run --project "$PATCHER_PROJ" -c Release -v quiet -- "$MANAGED_DIR" "$HELPERS_BIN" --restore
    if [[ $? -ne 0 ]]; then red "Restore failed."; exit 1; fi
    STALE_HELPER="$MANAGED_DIR/RimWorldStartupHelpers.dll"
    if [[ -f "$STALE_HELPER" ]]; then
        rm -f "$STALE_HELPER" && grn "Removed RimWorldStartupHelpers.dll" \
            || warn "Could not remove RimWorldStartupHelpers.dll — close RimWorld and try again."
    fi
    grn "Game restored to original state."
    exit 0
fi

# If --fresh is used but a backup already exists, the user may be about to destroy their only clean backup. Confirm before proceeding.
if $FRESH && [[ -f "$BACKUP_DLL" ]]; then
    warn "A backup already exists. --fresh will discard it and capture the CURRENT DLL as the new clean backup."
    warn "Only continue if Steam has just re-downloaded a fresh, unpatched DLL."
    read -rp "[Hyperdrive] Continue with --fresh? (y/N) " answer
    if [[ ! "$answer" =~ ^[Yy]$ ]]; then
        red "Aborted. Run without --fresh to re-patch from the existing clean backup."
        exit 1
    fi
fi

# 
if [[ -f "$BACKUP_DLL" ]] && ! $FRESH; then
    warn "Game appears to already be patched. Re-patching from backup (idempotent)."
    warn "Use --fresh if you updated RimWorld via Steam."
fi

# do the actual build
cyan "Building RimWorldStartupHelpers..."
dotnet build "$HELPERS_PROJ" -c Release -v quiet
if [[ $? -ne 0 ]]; then
    red "Helpers build failed. Check that RimWorld is installed at: $GAME_DIR"
    exit 1
fi

cyan "Building PatcherTool..."
dotnet build "$PATCHER_PROJ" -c Release -v quiet
if [[ $? -ne 0 ]]; then
    red "PatcherTool build failed."
    exit 1
fi

# do the actual patching
cyan "Patching $MANAGED_DIR ..."

EXTRA_ARGS=()
if $FRESH; then EXTRA_ARGS+=("--fresh"); fi
if [[ -n "$SKIP" ]]; then
    if ! [[ "$SKIP" =~ ^[0-9]+(,[0-9]+)*$ ]]; then
        red "Invalid --skip value: '$SKIP'. Use comma-separated patch numbers, e.g. --skip '3,4'."
        exit 1
    fi
    EXTRA_ARGS+=("--skip=$SKIP")
fi

dotnet run --project "$PATCHER_PROJ" -c Release --no-build -- "$MANAGED_DIR" "$HELPERS_BIN" "${EXTRA_ARGS[@]}"
if [[ $? -ne 0 ]]; then
    red "Patching failed."
    exit 1
fi

grn "Done! Launch RimWorld and enjoy faster loading!"
