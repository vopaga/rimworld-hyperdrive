# rimworld-hyperdrive

IL-level patcher for RimWorld's startup pipeline. Uses Mono.Cecil to inject parallel mod loading, optimized XML parsing, and content prefetch directly into `Assembly-CSharp.dll`. Mod-agnostic — works with any modlist. Achieves **2.5× cold-start speedup** (261s → 102s) without modifying mods or redistributing game files.

---

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- RimWorld 1.6 (Steam or DRM-free, Windows)

---

## Quick start

```powershell
git clone https://github.com/YOUR_USERNAME/rimworld-hyperdrive
cd rimworld-hyperdrive

# Patch your Steam installation (default path auto-detected)
.\patch.ps1

# Or specify a custom path
.\patch.ps1 -GameDir "D:\Games\RimWorld"
```

Launch RimWorld normally through Steam — the patched DLL is already in place.

---

## After a Steam game update

Steam will overwrite `Assembly-CSharp.dll`. Run with `-Fresh` to start from the new version:

```powershell
.\patch.ps1 -GameDir "..." -Fresh
```

---

## Restore original

```powershell
.\patch.ps1 -GameDir "..." -Restore
```

This reverts `Assembly-CSharp.dll` from the backup and removes `RimWorldStartupHelpers.dll`.

---

## How it works

5 IL patches are injected by [Mono.Cecil](https://github.com/jbevain/cecil) at build time. No Harmony, no mod loader, no game restart required.

| # | Target method | What changes |
|---|--------------|-------------|
| 1 | `DirectXmlLoader.XmlAssetsInModFolder` | Thread count: hardcoded `2` → `Max(3, CPU-1)` |
| 2 | `DirectXmlCrossRefLoader.Register*` | `Monitor.Enter/Exit` for thread-safe parallel registration |
| 3 | `LoadedModManager.LoadModXML` | `Parallel.For` over all mods instead of sequential |
| 4 | `LoadedModManager.ParseAndProcessXML` | ⚠️ **Disabled** — replaces method body, breaks Harmony transpiler patches from mods (e.g. HugsLib). Code preserved, ~1s gain not worth the breakage. Potentially fixable. |
| 5 | `ModContentLoader.LoadTextureViaImageConversion` | Cache-first byte loading (files prefetched in background during XML phase) |
| 6 | `LoadedModManager.ApplyPatches` | Hash-index XPath: O(n×m) full-doc scans → O(1) lookup per query |

`RimWorldStartupHelpers.dll` (net472) is deployed alongside `Assembly-CSharp.dll` in `Managed/` and loaded automatically by Mono at runtime.

---

## Benchmark

Tested on RimWorld 1.6 with **184 community mods** on an Intel core-i 7 12700KF, DDR5, NVME gen5 drive.

| Condition | Time |
|-----------|------|
| Vanilla (unpatched) | 261s |
| Patched — warm OS cache | 92s |
| Patched — cold OS cache | 102s |

To simulate a cold run without rebooting, use [RAMMap](https://learn.microsoft.com/en-us/sysinternals/downloads/rammap) → *Empty → Empty Standby List* before launching.

---

## Compatibility

- Works with **any mod list** — patches target engine loading infrastructure, not individual mods
- Mod authors do **not** need to change anything
- Existing mods load correctly and faster
- Hot-reload path preserved and tested

---

## License

GNU GPL v3 — free forever. See [LICENSE](LICENSE).
