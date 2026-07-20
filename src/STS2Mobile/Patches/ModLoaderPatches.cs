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
        // The base game refactored ModManager over 2026 (LoadModsInDirRecursive ->
        // ReadModsInDirRecursive, Mod.wasLoaded -> Mod.state), so this patch no longer
        // touches those private members. Only the "mods" directory string literal is
        // rewritten, which has stayed stable across the refactors; if the pattern is
        // gone the original bytecode is left untouched and external mods stay disabled.
        var matcher = new CodeMatcher(instructions).MatchStartForward(
            new CodeMatch(OpCodes.Ldstr, "mods")
        );

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
    }

    public static bool ReadSteamModsPrefix() => false;
}
