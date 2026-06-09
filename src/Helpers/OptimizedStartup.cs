using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Verse;

namespace Verse.StartupOptimizer
{
    /// <summary>
    /// Optimized replacements for LoadedModManager methods.
    /// Deployed as RimWorldStartupHelpers.dll in Managed/ and called via Cecil patches.
    /// </summary>
    internal static class OptimizedModManager
    {
        private static int ThreadCount => Math.Max(4, Environment.ProcessorCount - 1);

        // ── Texture/string byte pre-fetch cache ───────────────────────────────
        // Populated in background during XML loading, consumed on main thread during
        // ReloadContentInt. Eliminates all disk I/O from the main thread texture phase.
        private static readonly ConcurrentDictionary<string, byte[]> _byteCache
            = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        private static volatile Task _prefetchTask = null;
        private static volatile bool _prefetchStarted = false;

        // Skip pathologically large individual files; everything else is cached and then
        // evicted on first read (GetCachedBytes), so the cache drains during the texture
        // phase instead of being held for the whole session. No total cap on purpose: a cap
        // starves the optimization on big modlists (most textures end up uncached -> the
        // main thread reads them cold from disk, which is the slow path we're avoiding).
        private const long MaxFileBytes = 16L * 1024 * 1024;         // skip files > 16 MB
        private static long _prefetchBytes;

        /// <summary>Called by patched LoadModContent — starts background byte pre-fetch.</summary>
        internal static void StartContentPrefetch(List<ModContentPack> runningMods)
        {
            if (_prefetchStarted) return;
            _prefetchStarted = true;
            _byteCache.Clear();
            _prefetchBytes = 0;

            _prefetchTask = Task.Run(() =>
            {
                try
                {
                    var allFiles = new ConcurrentBag<string>();

                    // Collect texture files across all mods in parallel. Only textures are
                    // consumed from the cache (the LoadTextureViaImageConversion patch); sounds
                    // and strings used to be prefetched too but nothing ever read them back,
                    // so they were pure wasted RAM — dropped.
                    Parallel.ForEach(runningMods,
                        new ParallelOptions { MaxDegreeOfParallelism = ThreadCount },
                        mod =>
                        {
                            foreach (var folder in mod.foldersToLoadDescendingOrder)
                                CollectFiles(folder, "Textures", allFiles, IsTextureExt);
                        });

                    // Read bytes in parallel, skipping only pathologically large files.
                    Parallel.ForEach(allFiles,
                        new ParallelOptions { MaxDegreeOfParallelism = ThreadCount },
                        path =>
                        {
                            try
                            {
                                var info = new FileInfo(path);
                                if (!info.Exists || info.Length > MaxFileBytes) return;
                                if (_byteCache.TryAdd(path, File.ReadAllBytes(path)))
                                    Interlocked.Add(ref _prefetchBytes, info.Length);
                            }
                            catch { /* silently skip unreadable files */ }
                        });

                    Log.Message($"[StartupOpt] Texture pre-fetch done: {_byteCache.Count} files, " +
                        $"~{Interlocked.Read(ref _prefetchBytes) / (1024 * 1024)} MB (evicted on use).");
                }
                catch (Exception ex)
                {
                    Log.Warning("[StartupOpt] Content pre-fetch error: " + ex.Message);
                }
            });
        }

        /// <summary>Returns pre-fetched bytes for a file path, or null if not cached.</summary>
        public static byte[] GetCachedBytes(string fullPath)
        {
            // Evict on first read: once a texture's bytes are handed to Unity we no longer
            // need them, so free the memory immediately instead of holding it all session.
            if (_byteCache.TryRemove(fullPath, out var bytes))
            {
                Interlocked.Add(ref _prefetchBytes, -bytes.Length);
                return bytes;
            }
            return null;
        }

        /// <summary>Waits for pre-fetch to complete (called at start of texture loading).</summary>
        internal static void EnsurePrefetchComplete()
        {
            if (_prefetchTask != null && !_prefetchTask.IsCompleted)
            {
                Log.Message("[StartupOpt] Waiting for content pre-fetch to finish...");
                _prefetchTask.Wait();
            }
            _prefetchTask = null;
        }

        private static void CollectFiles(string folder, string subfolder,
            ConcurrentBag<string> results, Func<string, bool> extFilter)
        {
            try
            {
                string dir = Path.Combine(folder, subfolder);
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    if (extFilter(Path.GetExtension(f)))
                        results.Add(f);
                }
            }
            catch { }
        }

        private static bool IsTextureExt(string ext)
        {
            string e = ext.ToLowerInvariant();
            return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".psd" || e == ".dds";
        }
        // ── LoadModXML parallel replacement ───────────────────────────────────

        internal static List<LoadableXmlAsset> LoadModXML_Parallel(
            List<ModContentPack> runningMods,
            bool hotReload)
        {
            // Kick off content pre-fetch in background — it runs while we parse XML
            if (!hotReload)
                StartContentPrefetch(runningMods);

            var results = new List<LoadableXmlAsset>[runningMods.Count];

            Parallel.For(0, runningMods.Count,
                new ParallelOptions { MaxDegreeOfParallelism = ThreadCount },
                i =>
                {
                    ModContentPack mod = runningMods[i];
                    try
                    {
                        results[i] = new List<LoadableXmlAsset>(mod.LoadDefs(hotReload));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[StartupOpt] Could not load defs for mod " +
                            mod.PackageIdPlayerFacing + ": " + ex);
                        results[i] = new List<LoadableXmlAsset>();
                    }
                });

            int total = 0;
            for (int i = 0; i < results.Length; i++)
                if (results[i] != null) total += results[i].Count;

            var combined = new List<LoadableXmlAsset>(total);
            for (int i = 0; i < results.Length; i++)
                if (results[i] != null)
                    combined.AddRange(results[i]);

            return combined;
        }

        // ── ParseAndProcessXML sequential replacement ─────────────────────────
        // (kept sequential — DirectXmlToObjectNew caches are not thread-safe)

        internal static void ParseAndProcessXML_Optimized(
            XmlDocument xmlDoc,
            Dictionary<XmlNode, LoadableXmlAsset> assetlookup,
            bool hotReload,
            List<Def> patchedDefs,
            List<ModContentPack> runningMods)
        {
            XmlNodeList childNodes = xmlDoc.DocumentElement.ChildNodes;
            var list = new List<XmlNode>(childNodes.Count);
            foreach (XmlNode node in childNodes)
                list.Add(node);

            DeepProfiler.Start("Loading asset nodes " + list.Count);
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    XmlNode node = list[i];
                    if (node.NodeType == XmlNodeType.Element)
                    {
                        LoadableXmlAsset asset = null;
                        assetlookup.TryGetValue(node, out asset);
                        XmlInheritance.TryRegister(node, asset?.mod);
                    }
                }
            }
            finally { DeepProfiler.End(); }

            DeepProfiler.Start("XmlInheritance.Resolve()");
            try { XmlInheritance.Resolve(); }
            finally { DeepProfiler.End(); }

            if (hotReload)
            {
                foreach (ModContentPack mod in runningMods)
                    mod.ClearDefs();
            }
            patchedDefs.Clear();

            bool useLegacy = GenCommandLine.CommandLineArgPassed("legacy-xml-deserializer");
            var toHotReloadCopy = new List<(Def src, Def dst)>();

            DeepProfiler.Start("Loading defs for " + list.Count + " nodes");
            try
            {
                ParseDefs_Sequential(list, assetlookup, !useLegacy, hotReload, patchedDefs, toHotReloadCopy);
            }
            finally { DeepProfiler.End(); }

            if (toHotReloadCopy.Count == 0)
                return;

            LongEventHandler.ExecuteWhenFinished(delegate
            {
                Parallel.ForEach(toHotReloadCopy, delegate ((Def src, Def dst) toCopy)
                {
                    Def src = toCopy.src;
                    Def dst = toCopy.dst;
                    ushort shortHash = dst.shortHash;
                    ushort index = dst.index;
                    ushort debugRandomId = dst.debugRandomId;
                    ModContentPack mcp = dst.modContentPack;
                    var treeNode = (dst as ThingCategoryDef)?.treeNode;
                    Gen.MemberwiseShallowCopy(src, dst);
                    dst.shortHash = shortHash;
                    dst.index = index;
                    dst.debugRandomId = debugRandomId;
                    dst.modContentPack = mcp;
                    if (treeNode != null)
                        ((ThingCategoryDef)dst).treeNode = treeNode;
                    src.defName += "_HotReloadedThrowaway";
                    src.ResolveDefNameHash();
                    src.ClearCachedData();
                });
            });
        }

        private static void ParseDefs_Sequential(
            List<XmlNode> list,
            Dictionary<XmlNode, LoadableXmlAsset> assetlookup,
            bool useNewDeserializer,
            bool hotReload,
            List<Def> patchedDefs,
            List<(Def src, Def dst)> toHotReloadCopy)
        {
            // NOTE: original does NOT have a NodeType early-exit; non-element nodes
            // return null from DefFromNode and get dropped by the null check below.
            foreach (XmlNode node in list)
            {
                // MayRequire — exact match of original IL: .ToLower().Split(char, None)
                string mayRequire = node.Attributes?["MayRequire"]?.Value.ToLower();
                if (mayRequire != null && !ModLister.AllModsActiveNoSuffix(mayRequire.Split(',')))
                    continue;

                // MayRequireAnyOf — null attribute → null array, not empty-split
                var mayRequireAnyAttr = node.Attributes?["MayRequireAnyOf"];
                string[] mayRequireAny = mayRequireAnyAttr != null
                    ? mayRequireAnyAttr.Value.ToLower().Split(',')
                    : null;
                if (!mayRequireAny.NullOrEmpty() && !ModLister.AnyModActiveNoSuffix((IEnumerable<string>)mayRequireAny))
                    continue;

                LoadableXmlAsset asset = null;
                assetlookup.TryGetValue(node, out asset);

                Def def = useNewDeserializer
                    ? DirectXmlToObjectNew.DefFromNodeNew(node, asset)
                    : DirectXmlLoader.DefFromNode(node, asset);
                if (def == null) continue;

                // HOT-RELOAD path only — exact match of original (passes true for specialCaseForSoundDefs)
                if (hotReload)
                {
                    Def existingDef = GenDefDatabase.GetDefSilentFail(def.GetType(), def.defName, true);
                    if (existingDef != null)
                    {
                        toHotReloadCopy.Add((def, existingDef));
                        def = existingDef;
                    }
                }

                // Route to mod.defs or patchedDefs — use asset.name directly (non-null in mod branch)
                ModContentPack mod = asset?.mod;
                if (mod != null) mod.AddDef(def, asset.name);
                else patchedDefs.Add(def);
            }
        }
    }
}
