using System;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.Launcher;
using STS2Mobile.Steam;

namespace STS2Mobile.Patches;

// Core patches for the mobile launcher flow. Intercepts GameStartupWrapper to show
// the Steam login UI before the game starts, injects cloud save support via SteamKit2,
// and delegates sync logic to CloudSyncCoordinator.
public static class LauncherPatches
{
    internal static bool CloudSyncEnabled = true;
    internal static string SavedAccountName;
    internal static string SavedRefreshToken;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.PatchCritical(
            harmony,
            typeof(NGame),
            "GameStartupWrapper",
            prefix: PatchHelper.Method(typeof(LauncherPatches), nameof(GameStartupWrapperPrefix))
        );

        PatchHelper.Patch(
            harmony,
            typeof(SaveManager),
            "ConstructDefault",
            prefix: PatchHelper.Method(typeof(LauncherPatches), nameof(ConstructDefaultPrefix))
        );

        PatchHelper.PatchCritical(
            harmony,
            typeof(CloudSaveStore),
            "SyncCloudToLocal",
            prefix: PatchHelper.Method(typeof(LauncherPatches), nameof(SyncCloudToLocalPrefix))
        );
    }

    public static bool GameStartupWrapperPrefix(object __instance, ref Task __result)
    {
        __result = RunLauncherThenGame(__instance);
        return false;
    }

    public static bool ConstructDefaultPrefix(ref SaveManager __result)
    {
        PatchHelper.Log(
            $"[Cloud] ConstructDefaultPrefix called. HasToken={SavedRefreshToken != null}, CloudSync={CloudSyncEnabled}"
        );

        if (!CloudSyncEnabled)
        {
            PatchHelper.Log("[Cloud] Cloud sync disabled by user — using local-only SaveManager");
            return true;
        }

        if (SavedAccountName == null || SavedRefreshToken == null)
        {
            PatchHelper.Log("[Cloud] No saved credentials — using local-only SaveManager");
            return true;
        }

        try
        {
            var localStore = new GodotFileIo(UserDataPathProvider.GetAccountScopedBasePath(null));
            var cloudStore = new SteamKit2CloudSaveStore(SavedAccountName, SavedRefreshToken);
            var wrappedStore = new CloudSaveStore(localStore, cloudStore);

            __result = new SaveManager(wrappedStore);
            PatchHelper.Log("[Cloud] Created SaveManager with SteamKit2 cloud store");
            return false;
        }
        catch (Exception ex)
        {
            PatchHelper.Log(
                $"[Cloud] Cloud store injection failed, falling back to local: {ex.Message}"
            );
            return true;
        }
    }

    public static bool SyncCloudToLocalPrefix(
        CloudSaveStore __instance,
        string path,
        ref Task __result
    )
    {
        __result = CloudSyncCoordinator.AutoSyncFileAsync(
            __instance.LocalStore,
            __instance.CloudStore,
            path
        );
        return false;
    }

    private static async Task RunLauncherThenGame(object game)
    {
        var gameNode = (Node)game;

        // 1. Initialize local settings before NInputManager's asynchronous startup resumes.
        // The launcher delays GameStartup, but input initialization starts from the scene's
        // _Ready callback and requires SettingsSave to exist.
        SaveManager.Instance.InitSettingsData();
        PatchHelper.Log("[Launcher] Local settings initialized before input startup");

        // 2. Wait for authentication and the explicit Play action.
        var launcher = new LauncherUI();
        gameNode.AddChild(launcher);
        launcher.SetGameMode(true);
        launcher.Initialize();
        PatchHelper.Log("Launcher UI displayed");

        await launcher.WaitForLaunch();
        PatchHelper.Log("User launched game, proceeding to startup...");

        // 3. Recreate SaveManager with the authenticated account's cloud store.
        var instanceField = typeof(SaveManager).GetField(
            "_instance",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        if (instanceField != null)
        {
            instanceField.SetValue(null, null);
            PatchHelper.Log("[Cloud] Reset SaveManager._instance for cloud store re-injection");
        }

        launcher.QueueFree();

        // 4. Complete the normal game startup sequence.
        if (ShaderWarmupScreen.NeedsWarmup())
        {
            var warmup = new ShaderWarmupScreen();
            gameNode.AddChild(warmup);
            warmup.Initialize();
            await warmup.WaitForCompletion();
            warmup.QueueFree();
        }

        SaveManager.Instance.InitSettingsData();

        var gameStartup = game.GetType()
            .GetMethod("GameStartup", BindingFlags.NonPublic | BindingFlags.Instance);

        try
        {
            await (Task)gameStartup.Invoke(game, null);
        }
        catch (TargetInvocationException ex)
        {
            PatchHelper.Log($"Game startup failed: {ex.InnerException?.Message}");
            throw ex.InnerException ?? ex;
        }
    }
}
