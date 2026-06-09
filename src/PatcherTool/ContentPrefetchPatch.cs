using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.Linq;

namespace RimWorldPatcher;

/// <summary>
/// Patch 5 – ModContentLoader&lt;T&gt;.LoadTextureViaImageConversion
///
/// Intercepts file.ReadAllBytes() to check the pre-fetch cache first.
/// The pre-fetch runs in background during XML loading, so by the time
/// the main thread reaches texture loading, all bytes are already in RAM.
///
/// Transforms:
///   byte[] data = file.ReadAllBytes();
/// Into:
///   byte[] data = OptimizedModManager.GetCachedBytes(file.FullPath) ?? file.ReadAllBytes();
///
/// IL shape after patch:
///   ldarg.0              (file — already on stack from original load)
///   callvirt get_FullPath
///   call GetCachedBytes
///   dup
///   brtrue.s CACHED_OK   (non-null → skip ReadAllBytes)
///   pop
///   ldarg.0              (reload file for fallback)
///   callvirt ReadAllBytes  (original instruction)
///   CACHED_OK:
///   stloc.0              (data — original instruction, untouched)
/// </summary>
static class ContentPrefetchPatch
{
    public static void Apply(ModuleDefinition target)
    {
        Console.WriteLine("[Patch5] Patching LoadTextureViaImageConversion for cache pre-fetch...");

        // GetCachedBytes is now merged into the target module
        var helperType   = target.MustGetType("Verse.StartupOptimizer.OptimizedModManager");
        var getCachedRef = target.ImportReference(helperType.MustGetMethod("GetCachedBytes"));

        // ModContentLoader`1 is a generic type, but LoadTextureViaImageConversion is non-generic
        var loaderType = target.Types.FirstOrDefault(t => t.Name == "ModContentLoader`1")
            ?? throw new Exception("ModContentLoader`1 not found");
        // Method was renamed across 1.6 builds (LoadTexture -> LoadTextureViaImageConversion).
        // Try the current name, then the older one, before giving up.
        var method = loaderType.Methods.FirstOrDefault(m => m.Name == "LoadTextureViaImageConversion")
            ?? loaderType.Methods.FirstOrDefault(m => m.Name == "LoadTexture")
            ?? throw new Exception("LoadTextureViaImageConversion/LoadTexture not found");

        var body         = method.Body;
        var instructions = body.Instructions;

        // Find: callvirt VirtualFile::ReadAllBytes()
        int callIdx = -1;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].OpCode == OpCodes.Callvirt &&
                instructions[i].Operand is MethodReference mr &&
                mr.Name == "ReadAllBytes" && mr.DeclaringType.Name == "VirtualFile")
            {
                callIdx = i;
                break;
            }
        }
        if (callIdx < 0)
            throw new Exception("callvirt VirtualFile::ReadAllBytes not found");

        // The stloc AFTER ReadAllBytes is our branch target for the cache-hit path
        var afterReadAll = instructions[callIdx + 1];

        // Resolve VirtualFile.get_FullPath
        var virtualFileType = target.Types.FirstOrDefault(t => t.Name == "VirtualFile")
            ?? throw new Exception("VirtualFile not found");
        var getFullPathRef = target.ImportReference(
            virtualFileType.Methods.First(m => m.Name == "get_FullPath"));

        var il = body.GetILProcessor();
        var readAllBytesCall = instructions[callIdx];

        // Insert new instructions immediately before readAllBytesCall (in order):
        //   callvirt  get_FullPath    — file (already on stack) → string
        //   call      GetCachedBytes  — string → byte[]|null
        //   dup                       — [x, x]
        //   brtrue.s  afterReadAll    — if non-null, jump past ReadAllBytes
        //   pop                       — discard null
        //   ldarg.0                   — reload file for fallback
        // (readAllBytesCall stays in place as fallback)
        //   stloc.0 (= afterReadAll)  — receives byte[] regardless of which path

        var iGetFullPath = il.Create(OpCodes.Callvirt, getFullPathRef);
        var iGetCached   = il.Create(OpCodes.Call,     getCachedRef);
        var iDup         = il.Create(OpCodes.Dup);
        var iBrtrue      = il.Create(OpCodes.Brtrue_S, afterReadAll);
        var iPop         = il.Create(OpCodes.Pop);
        var iLoadFile    = il.Create(OpCodes.Ldarg_0);   // file is always arg0

        il.InsertBefore(readAllBytesCall, iGetFullPath);
        il.InsertBefore(readAllBytesCall, iGetCached);
        il.InsertBefore(readAllBytesCall, iDup);
        il.InsertBefore(readAllBytesCall, iBrtrue);
        il.InsertBefore(readAllBytesCall, iPop);
        il.InsertBefore(readAllBytesCall, iLoadFile);

        body.OptimizeMacros();

        Console.WriteLine("[Patch5] LoadTextureViaImageConversion → cache-first byte loading");
        Console.WriteLine("[Patch5] Done.");
    }
}
