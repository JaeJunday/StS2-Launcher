using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.Patches;

// Disables desktop-only platform features that are unavailable or unnecessary on mobile:
// Steam initialization, Sentry crash reporting, system info logging, and telemetry opt-in.
public static class PlatformPatches
{
    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(NGame),
            "InitializePlatform",
            prefix: PatchHelper.Method(typeof(PlatformPatches), nameof(InitializePlatformPrefix))
        );

        PatchHelper.Patch(
            harmony,
            typeof(OsDebugInfo),
            "LogSystemInfo",
            prefix: PatchHelper.Method(typeof(PlatformPatches), nameof(SkipPrefix))
        );

        PatchHelper.PatchGetter(
            harmony,
            typeof(PrefsSave),
            "UploadData",
            prefix: PatchHelper.Method(typeof(PlatformPatches), nameof(ReturnFalsePrefix))
        );

        // NullPlatformUtilStrategy's constructor calls CreateDirectory(".") which
        // fails on Android because "." is not a valid absolute Godot path.
        PatchHelper.Patch(
            harmony,
            typeof(GodotFileIo),
            "CreateDirectory",
            prefix: PatchHelper.Method(typeof(PlatformPatches), nameof(CreateDirectoryPrefix))
        );

        // Skip Sentry crash reporting. Not useful for our mobile port and the
        // Sentry GDExtension is not bundled in the Android build.
        PatchHelper.Patch(
            harmony,
            typeof(SentryService),
            "Initialize",
            prefix: PatchHelper.Method(typeof(PlatformPatches), nameof(SkipPrefix))
        );

        // Android appends Unicode extensions for regional preferences
        // (e.g. "de-DE-u-mu-celsius" or "zh-CN-#Hans-u-mu-celsius") which the game's
        // CultureInfo lookup cannot parse; the resulting exception aborts every label's
        // _Ready() and shows up as a black screen after Play.
        PatchGetRawLanguage(harmony);
    }

    public static bool InitializePlatformPrefix(ref Task<bool> __result)
    {
        PatchHelper.Log("Skipping Steam initialization (mobile)");
        __result = Task.FromResult(true);
        return false;
    }

    public static bool SkipPrefix() => false;

    public static bool ReturnFalsePrefix(ref bool __result)
    {
        __result = false;
        return false;
    }

    // Skip paths that aren't valid Godot absolute paths (must contain "://").
    public static bool CreateDirectoryPrefix(GodotFileIo __instance, string directoryPath)
    {
        var fullPath = __instance.GetFullPath(directoryPath);
        if (!fullPath.Contains("://"))
            return false;
        return true;
    }

    // Installed by reflection: the type moved between game builds, and a missing type
    // must not abort the rest of the patch set.
    private static void PatchGetRawLanguage(Harmony harmony)
    {
        try
        {
            var sts2Asm = typeof(NGame).Assembly;
            var nullStrategyType = sts2Asm.GetType(
                "MegaCrit.Sts2.Core.Platform.Null.NullPlatformUtilStrategy"
            );
            if (nullStrategyType == null)
            {
                PatchHelper.Log("Locale fix: NullPlatformUtilStrategy not found, skipping");
                return;
            }

            var method = nullStrategyType.GetMethod(
                "GetRawLanguage",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            );
            if (method == null)
            {
                PatchHelper.Log("Locale fix: GetRawLanguage not found, skipping");
                return;
            }

            harmony.Patch(
                method,
                postfix: new HarmonyMethod(
                    typeof(PlatformPatches).GetMethod(
                        nameof(GetRawLanguagePostfix),
                        BindingFlags.Public | BindingFlags.Static
                    )
                )
            );
            PatchHelper.Log("Patched NullPlatformUtilStrategy.GetRawLanguage (locale fix)");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"Locale fix failed: {ex.Message}");
        }
    }

    // Keep language + region, drop Unicode/private extension subtags, never throw.
    public static void GetRawLanguagePostfix(ref string __result)
    {
        var raw = __result;
        var sanitized = NormalizeAndroidLanguage(raw);
        if (sanitized != raw)
            PatchHelper.Log($"[Locale] Normalized '{raw}' to '{sanitized}'");
        __result = sanitized;
    }

    /// <summary>Keep the language and region while removing Android locale extensions.</summary>
    private static string NormalizeAndroidLanguage(string rawLanguage)
    {
        if (string.IsNullOrWhiteSpace(rawLanguage))
            return "en-US";

        var normalized = rawLanguage.Replace('_', '-');
        var fragments = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries);

        // 1. Validate the required language subtag at the Android locale boundary.
        if (
            fragments.Length == 0
            || fragments[0].Length is < 2 or > 3
            || !ContainsOnlyAsciiLetters(fragments[0])
        )
        {
            PatchHelper.Log($"[Locale] Unusable locale '{rawLanguage}'; falling back to 'en-US'");
            return "en-US";
        }

        // 2. Retain the two-letter region before any Unicode/private extension.
        string region = null;
        for (var index = 1; index < fragments.Length; index++)
        {
            var fragment = fragments[index];
            if (fragment.Length == 1 || fragment.Equals("#u", StringComparison.OrdinalIgnoreCase))
                break;
            if (fragment.StartsWith('#'))
                continue;
            if (fragment.Length == 2 && ContainsOnlyAsciiLetters(fragment))
            {
                region = fragment;
                break;
            }
        }

        // 3. Return a CultureInfo-compatible identifier used by the game's own mapper.
        return region == null ? fragments[0] : $"{fragments[0]}-{region}";
    }

    private static bool ContainsOnlyAsciiLetters(string value)
    {
        foreach (var character in value)
        {
            var isUppercaseAscii = character is >= 'A' and <= 'Z';
            var isLowercaseAscii = character is >= 'a' and <= 'z';
            if (!isUppercaseAscii && !isLowercaseAscii)
                return false;
        }
        return true;
    }
}
