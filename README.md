<p align="center">
  <img src="assets/hyperdrive-icon.png" width="150" alt="Hyperdrive">
</p>

<h1 align="center">Hyperdrive</h1>

<p align="center">
  <a href="https://steamcommunity.com/sharedfiles/filedetails/?id=3741577108"><img alt="Steam Workshop" src="https://img.shields.io/badge/Steam-Workshop-1b2838?style=flat-square&logo=steam&logoColor=white"></a>
  <a href="https://ko-fi.com/vopaga"><img alt="Support on Ko-fi" src="https://img.shields.io/badge/support-Ko--fi-FF5E5B?style=flat-square&logo=ko-fi&logoColor=white"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-GPL--3.0-165d45?style=flat-square"></a>
</p>

Faster RimWorld startup: parallel mod loading, optimized XML parsing, and background content prefetch, without touching your mods. About 2.5x faster cold start on a heavy modlist (261s to 102s on the rig below).

It comes three ways: a Steam Workshop mod, a drop-in standalone mod, or a build-time patcher that edits `Assembly-CSharp.dll` directly. All work on RimWorld 1.6 (Steam or DRM-free; Windows, Linux, or macOS).

---

## Install

**Steam Workshop (easiest).** [Subscribe here](https://steamcommunity.com/sharedfiles/filedetails/?id=3741577108) and enable it in your mod list. It auto-updates, and Harmony comes in as a dependency.

**Standalone mod (no Steam).** Grab `hyperdrive-<version>.zip` from [Releases](../../releases/latest), unzip it into your RimWorld `Mods` folder, and enable it below [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) in the mod list. This is the same in-memory mod as the Workshop build; it just needs the Harmony mod installed.

**Build-time patcher (advanced).** Instead of loading as a mod, this edits `Assembly-CSharp.dll` directly, so it needs no Harmony and no mod entry. Requires the [.NET 8+ SDK](https://dotnet.microsoft.com/download) (8, 9, and 10 all work). Grab `hyperdrive-<version>-direct_game_engine_patch.zip` and run the script for your OS below.

---

## Build-time patcher

### Windows

```powershell
# Steam default path, auto-detected
.\patch.ps1

# Or specify a custom path
.\patch.ps1 -GameDir "D:\Games\RimWorld"
```

Launch RimWorld normally through Steam. The patched DLL is already in place.

<details>
<summary>Example output</summary>

```
[Hyperdrive] Building RimWorldStartupHelpers...
[Hyperdrive] Building PatcherTool...
[Hyperdrive] Patching ...\RimWorldWin64_Data\Managed ...
Backup created: ...\Assembly-CSharp.dll.original
Merged helper into Assembly-CSharp (9230 types).
[Patch1] thread count 2 raised to Max(3, ProcessorCount-1)
[Patch2] cross-ref registration locked for parallel use
[Patch3] LoadModXML now runs in parallel
[Patch4] ParseAndProcessXML skipped (permanently disabled)
[Patch5] LoadTextureViaImageConversion reads from the prefetch cache
[Patch6] XPath redirected to hash index: 11 calls across 11 methods
Written: ...\Assembly-CSharp.dll
ALL PATCHES APPLIED. To restore: patch.ps1 -Restore
[Hyperdrive] Done! Launch RimWorld and enjoy faster loading.
```

</details>

### Linux

```bash
# Auto-detects Steam default path
./patch.sh

# Or specify a custom path
./patch.sh --game-dir ~/.steam/steam/steamapps/common/RimWorld
```

<details>
<summary>Proton / Wine users</summary>

If you run RimWorld through Proton or Wine, point `--game-dir` at the game directory inside the Wine prefix. `patch.sh` probes which data folder exists (`RimWorldWin64_Data` vs `RimWorldLinux_Data`), so it handles both layouts automatically.

```bash
./patch.sh --game-dir ~/.steam/steam/steamapps/compatdata/294100/pfx/drive_c/Program\ Files\ \(x86\)/Steam/steamapps/common/RimWorld
```

The exact path depends on your Steam library location.

</details>

### macOS

```bash
# Auto-detects Steam default path
./patch.sh

# Or specify the .app bundle path
./patch.sh --game-dir ~/Library/Application\ Support/Steam/steamapps/common/RimWorld/RimWorldMac.app
```

---

## After a Steam game update (patcher only)

Steam will overwrite `Assembly-CSharp.dll`. Run with `-Fresh` to start from the new version:

```powershell
.\patch.ps1 -GameDir "..." -Fresh
```

```bash
# Linux / macOS
./patch.sh --fresh
```

---

## Restore original (patcher only)

```powershell
.\patch.ps1 -GameDir "..." -Restore
```

```bash
# Linux / macOS
./patch.sh --restore
```

This reverts `Assembly-CSharp.dll` from the backup and removes `RimWorldStartupHelpers.dll`.

---

## How it works

6 IL patches (5 active) are applied to the startup code. The build-time patcher injects them with [Mono.Cecil](https://github.com/jbevain/cecil); the mod applies the same ones in memory with Harmony.

| # | Target method | What changes |
|---|--------------|-------------|
| 1 | `DirectXmlLoader.XmlAssetsInModFolder` | Thread count: hardcoded `2` raised to `Max(3, CPU-1)` |
| 2 | `DirectXmlCrossRefLoader.Register*` | `Monitor.Enter/Exit` for thread-safe parallel registration |
| 3 | `LoadedModManager.LoadModXML` | `Parallel.For` over all mods instead of sequential |
| 4 | `LoadedModManager.ParseAndProcessXML` | Disabled. Replaces the method body, which breaks Harmony transpiler patches from mods (e.g. HugsLib). Code preserved, ~1s gain not worth the breakage. Potentially fixable. |
| 5 | `ModContentLoader.LoadTextureViaImageConversion` | Cache-first byte loading (files prefetched in the background during the XML phase) |
| 6 | `LoadedModManager.ApplyPatches` | Hash-index XPath: O(n*m) full-document scans replaced by an O(1) lookup per query |

For the patcher, the helper code (`Verse.StartupOptimizer.*`) is merged directly into `Assembly-CSharp.dll` by [MonoMod](https://github.com/MonoMod/MonoMod), so there's no separate DLL in `Managed/`. Earlier versions shipped a side-car `RimWorldStartupHelpers.dll`, which tripped up Prepatcher; merging fixed that.

---

## Benchmark

Tested on RimWorld 1.6 with 184 community mods on an Intel Core i7 12700KF, DDR5, NVMe gen5 drive.

| Condition | Time |
|-----------|------|
| Vanilla (unpatched) | 261s |
| Patched, warm OS cache | 92s |
| Patched, cold OS cache | 102s |

To simulate a cold run without rebooting, use [RAMMap](https://learn.microsoft.com/en-us/sysinternals/downloads/rammap) and pick Empty, then Empty Standby List, before launching.

---

## Compatibility

- Works with any mod list; patches target engine loading infrastructure, not individual mods
- Mod authors do not need to change anything
- Existing mods load correctly and faster
- Hot-reload path preserved and tested
- Compatible with [Prepatcher](https://steamcommunity.com/workshop/filedetails/?id=2934420800): the helper is merged into `Assembly-CSharp.dll`, so there's no separate DLL for Prepatcher to choke on

### Known incompatibilities

- **[Yet Another Optimizer](https://steamcommunity.com/sharedfiles/filedetails/?id=3718308218)** — both mods rewrite the same vanilla `PatchOperation*.ApplyWorker` methods to speed up the XML patch phase, and the rewrites collide, causing a hard crash to desktop during loading (the patching phase, after the def tree). Run only one of the two. If you want Hyperdrive's other optimizations alongside it, disable Yet Another Optimizer's "fast patch operation" option.

### Overlapping mods (safe, but redundant)

- **[Faster Game Loading (Continued)](https://github.com/mushroomTW/FasterGameLoading---Continued)** — no crash; both are startup optimizers and step on each other's work. They parallelize mod XML loading at different layers (Hyperdrive across mods, FGL across files within a mod), so the two `Parallel.For` loops nest and oversubscribe the CPU instead of adding up, and both run their own background texture prefetch. Pick one as your primary loader. If you want to run both, disable FGL's multithreading so it sticks to texture downscaling / atlas caching while Hyperdrive handles parallel loading.

---

## License

GNU GPL v3, free forever. See [LICENSE](LICENSE).
