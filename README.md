# rimworld-hyperdrive

<p>
  <a href="https://ko-fi.com/vopaga"><img alt="Support on Ko-fi" src="https://img.shields.io/badge/support-Ko--fi-FF5E5B?style=flat-square&logo=ko-fi&logoColor=white"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-GPL--3.0-165d45?style=flat-square"></a>
</p>

IL-level patcher for RimWorld's startup pipeline. Uses Mono.Cecil to inject parallel mod loading, optimized XML parsing, and content prefetch directly into `Assembly-CSharp.dll`. Mod-agnostic — works with any modlist. Achieves **2.5× cold-start speedup** (261s → 102s) without modifying mods or redistributing game files.

---

## Requirements

- RimWorld 1.6 (Steam or DRM-free, Windows)
- [.NET 8 SDK](https://dotnet.microsoft.com/download) — only if building from source

---

## Quick start

**Option A — Download release (no build tools needed):**

1. Download the latest ZIP from [Releases](../../releases/latest) and unzip it anywhere
2. Run:
```powershell
.\patch.ps1
```

**Option B — Build from source (requires [.NET 8 SDK](https://dotnet.microsoft.com/download)):**

```powershell
git clone https://github.com/YOUR_USERNAME/rimworld-hyperdrive
cd rimworld-hyperdrive
.\patch.ps1
```

Launch RimWorld normally through Steam — the patched DLL is already in place.

<details>
<summary>Example output</summary>

```
[Hyperdrive] Building RimWorldStartupHelpers...
[Hyperdrive] Building PatcherTool...
[Hyperdrive] Patching C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed ...
══════════════════════════════════════════════
RimWorld Hyperdrive — DLL Patcher
══════════════════════════════════════════════
Target : ...\Managed\Assembly-CSharp.dll
Helpers: ...\RimWorldStartupHelpers.dll
Backup created: ...\Assembly-CSharp.dll.original

Loading assemblies...
Target  : Assembly-CSharp, Version=1.6.9438.37837 (9230 types)
Helpers : RimWorldStartupHelpers, Version=0.0.0.0 (3 types)

── Applying patches ──────────────────────────────
[Patch1] OK — Thread count changed from 2 to Max(3, ProcessorCount-1)
[Patch2] Patched 3 RegisterObjectWantsCrossRef overload(s)
[Patch2] Patched RegisterListWantsCrossRef
[Patch2] Patched RegisterDictionaryWantsCrossRef
[Patch3] LoadModXML → LoadModXML_Parallel
[Patch4] ParseAndProcessXML SKIPPED (permanently disabled)
[Patch5] LoadTextureViaImageConversion → cache-first byte loading
[Patch6] XPath calls redirected: 11 across 11 method(s).

── Writing patched assembly ──────────────────────
Written: ...\Assembly-CSharp.dll

── Deploying helpers DLL ──────────────────────────────
Deployed: ...\RimWorldStartupHelpers.dll

══════════════════════════════════════════════
ALL PATCHES APPLIED SUCCESSFULLY
Startup time: expect ~60% of vanilla with a heavy modlist.
To restore original: run patch.ps1 -Restore
══════════════════════════════════════════════

[Hyperdrive] Done! Launch RimWorld and enjoy faster loading.
```

</details>

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
