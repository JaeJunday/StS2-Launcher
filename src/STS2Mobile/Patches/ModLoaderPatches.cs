using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace STS2Mobile.Patches;

// Redirects the game's mod scan to Android external storage without relying on
// private fields that changed in newer game assemblies.
public static class ModLoaderPatches
{
    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(ModManager),
            "Initialize",
            transpiler: PatchHelper.Method(typeof(ModLoaderPatches), nameof(InitializeTranspiler))
        );
        PatchHelper.Patch(
            harmony,
            typeof(ModManager),
            "ReadSteamMods",
            prefix: PatchHelper.Method(typeof(ModLoaderPatches), nameof(ReadSteamModsPrefix))
        );
    }

    /// <summary>Rewrites the game's desktop mods path to the Android external directory.</summary>
    public static IEnumerable<CodeInstruction> InitializeTranspiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
<<<<<<< HEAD
        // The base-game update refactored ModManager: it removed the _initialized and
        // _loadedMods fields, renamed LoadModsInDirRecursive -> ReadModsInDirRecursive,
        // replaced Mod.wasLoaded with Mod.state, and ModManager.LoadedMods with
        // ModManager.GetLoadedMods(). The external-mods sideload logic here relied on the
        // old shape, so it is disabled until ported. Steam Workshop mods are unaffected
        // (the game's own ModManager.Initialize still handles those).
        PatchHelper.Log(
            "[Mods] External-mods sideloading disabled on this build "
                + "(pending port to the updated ModManager API)."
        );
=======
        // 1. Locate Path.Combine(directoryName, "mods") in ModManager.Initialize.
        var matcher = new CodeMatcher(instructions).MatchStartForward(
            new CodeMatch(OpCodes.Ldstr, "mods")
        );

        // 2. Replace the desktop path construction with the validated launcher path.
        if (matcher.IsValid)
        {
            matcher.Advance(-1);
            matcher.RemoveInstructions(3);
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldstr, AppPaths.ExternalModsDir));
            PatchHelper.Log($"[Mods] Redirected scan to {AppPaths.ExternalModsDir}");
        }
        else
        {
            PatchHelper.Log(
                "[Mods] ModManager.Initialize path pattern changed; external mods are disabled"
            );
        }

        return matcher.InstructionEnumeration();
>>>>>>> b0846fc (Fix game update caches and Steam mobile auth)
    }

    public static bool ReadSteamModsPrefix() => false;
}
