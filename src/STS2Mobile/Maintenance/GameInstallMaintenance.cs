using System;
using System.IO;
using System.Linq;
using Godot;

namespace STS2Mobile.Maintenance;

// Post-update repair that upstream only ever implemented in the Java activity.
//
// Two regressions show up whenever the downloaded game payload changes while the
// launcher stays installed:
//   1. stale texture imports under .godot/imported keep the previous PCK's atlases,
//      so cards / relics / potions render as shuffled texture fragments;
//   2. the copies of the game assemblies under .godot/mono/publish/arm64 are not
//      refreshed, so patched members are missing and the game boot-loops or
//      silently drops patches.
// This runs once per payload change, before any game scene is loaded.
public static class GameInstallMaintenance
{
    private const string PckName = "SlayTheSpire2.pck";
    private const string StateDirName = ".sts2mobile";
    private const string StateFileName = "payload_state.txt";
    private const string RestartFlagFileName = "restart_required.txt";

    private static readonly string[] CacheDirs =
    {
        ".godot/imported",
        "etc2_cache/.godot/imported",
    };

    // True when stale assemblies were removed and the process must restart before
    // the refreshed ones can be loaded.
    public static bool RestartRequired { get; private set; }

    public static void RunAtStartup()
    {
        try
        {
            var filesDir = ResolveFilesDir();
            if (filesDir == null)
            {
                PatchHelper.Log("[Maintenance] App files dir not found; skipping");
                return;
            }

            var gameDir = Path.Combine(filesDir, "game");
            var pck = Path.Combine(gameDir, PckName);
            if (!File.Exists(pck))
                return; // launcher-only mode, nothing downloaded yet

            var stamp = $"{File.GetLastWriteTimeUtc(pck).Ticks}:{new FileInfo(pck).Length}";
            var statePath = Path.Combine(filesDir, StateDirName, StateFileName);
            var previous = File.Exists(statePath) ? File.ReadAllText(statePath).Trim() : "";

            if (previous == stamp)
            {
                RefreshStaleAssemblies(filesDir, gameDir);
                return;
            }

            PatchHelper.Log(
                $"[Maintenance] Game payload changed ('{previous}' -> '{stamp}'); "
                    + "purging stale texture imports"
            );

            var removed = CacheDirs.Sum(relative => Purge(Path.Combine(filesDir, relative)));
            RefreshStaleAssemblies(filesDir, gameDir);

            Directory.CreateDirectory(Path.GetDirectoryName(statePath) ?? filesDir);
            File.WriteAllText(statePath, stamp);
            PatchHelper.Log($"[Maintenance] Removed {removed} stale import entries");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Maintenance] Failed: {ex.Message}");
        }
    }

    // Deletes game-owned assemblies that no longer match the downloaded payload so
    // the Java activity re-copies them on the next boot. APK-owned BCL names are kept.
    private static void RefreshStaleAssemblies(string filesDir, string gameDir)
    {
        var destDir = Path.Combine(filesDir, ".godot/mono/publish/arm64");
        var srcDir = FindAssembliesDir(gameDir);
        if (!Directory.Exists(destDir) || srcDir == null || !Directory.Exists(srcDir))
            return;

        var removed = 0;
        foreach (var src in Directory.EnumerateFiles(srcDir, "*.dll"))
        {
            var name = Path.GetFileName(src);
            if (BclAssets.FileNames.Contains(name))
                continue;

            var dest = Path.Combine(destDir, name);
            if (!File.Exists(dest))
                continue;

            var srcInfo = new FileInfo(src);
            var destInfo = new FileInfo(dest);
            if (SameContent(srcInfo, destInfo))
                continue;

            try
            {
                File.Delete(dest);
                removed++;
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Maintenance] Could not drop stale {name}: {ex.Message}");
            }
        }

        if (removed == 0)
            return;

        RestartRequired = true;
        var flag = Path.Combine(filesDir, StateDirName, RestartFlagFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(flag) ?? filesDir);
        File.WriteAllText(flag, DateTime.UtcNow.ToString("O"));
        PatchHelper.Log(
            $"[Maintenance] Dropped {removed} stale game assemblies; "
                + "next launch will use the refreshed payload (restart required)"
        );
    }

    private static bool SameContent(FileInfo a, FileInfo b) =>
        a.Length == b.Length && a.LastWriteTimeUtc <= b.LastWriteTimeUtc;

    private static string FindAssembliesDir(string gameDir)
    {
        if (!Directory.Exists(gameDir))
            return null;

        var candidate = Directory
            .EnumerateDirectories(gameDir, "data_*")
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();

        return candidate ?? Path.Combine(gameDir, "data_sts2_windows_x86_64");
    }

    // Godot's user:// maps to the app's internal files dir on Android; fall back to
    // the two fixed locations for com.game.sts2launcher if it does not.
    private static string ResolveFilesDir()
    {
        var candidates = new[]
        {
            OS.GetUserDataDir(),
            "/data/user/0/com.game.sts2launcher/files",
            "/data/data/com.game.sts2launcher/files",
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            if (Directory.Exists(Path.Combine(candidate, "game")))
                return candidate;
        }

        return null;
    }

    private static int Purge(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;

        var removed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    removed += Purge(entry);
                    Directory.Delete(entry);
                }
                else
                {
                    File.Delete(entry);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                PatchHelper.Log($"[Maintenance] Purge failed for {entry}: {ex.Message}");
            }
        }

        return removed;
    }
}
