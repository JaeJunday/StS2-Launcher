# Revival plan: StS2-Launcher fork (target: current Slay the Spire 2 build)

Status: measured, patch set merged, build inputs partially blocked (needs game assemblies).

## 1. Measured state of the upstream project

| Fact | Value |
| --- | --- |
| Last upstream commit | `1e97cf8` 2026-03-17 (repo `pushed_at` 2026-04-06) |
| Last release | `0.2.0`, 2026-03-15, single APK `StS2Launcher-v0.2.0.apk` (46.7 MB) |
| Open issues | 27, newest 2026-07-03; reports of black screens (Apr 17, Jun 19 updates), scrambled card/relic art, depot-key download failures, act-2 crashes |
| Open pull requests | 15 unmerged community fixes (#38, #49, #51, #56, #57, #59, #60, #66, #68, #69, #70 …) |
| Game version when upstream last shipped | ~v0.103 |
| Current game version | v0.111.0 (public beta v0.108.0 shipped 2026-07-02) |

Upstream is effectively unmaintained: every reported regression after March has a
community PR, and none was merged.

## 2. What actually breaks (root causes, in order of impact)

1. **Black screen after Play.** `FontControlUtils.ApplyLocaleFontSubstitution` began
   throwing `NullReferenceException`; the exception escapes every `MegaLabel._Ready()`,
   so the whole UI tree stays uninitialized. Fixed by a Harmony *finalizer* that swallows
   only that NRE (PR #60 / #66, merged here as `FontSubstitutionPatches`).
2. **Black screen before that.** Android appends Unicode locale extensions
   (`de-DE-u-mu-celsius`, `zh-CN-#Hans-u-mu-celsius`) to the system locale; the game's
   `NullPlatformUtilStrategy.GetRawLanguage` feeds that straight into `CultureInfo`
   → `CultureNotFoundException`/`FormatException`. Patched with a postfix normalizer
   that keeps language+region and never throws (PR #38 / #57, hardened here).
3. **Scrambled card / relic / potion art.** After a game PCK update the mobile texture
   import cache (`.godot/imported`, `etc2_cache/.godot/imported`) still holds atlases
   baked from the previous PCK. Fix: invalidate that cache when the PCK mtime changes,
   and refresh game assemblies without letting desktop CoreCLR DLLs overwrite the
   Android Mono BCL (PR #49/#56 lineage, ported here).
4. **Mod loader API drift.** `ModManager._initialized` / `_loadedMods` /
   `LoadModsInDirRecursive` / `Mod.wasLoaded` are gone in current `sts2.dll`;
   the old reflection targets threw `NullReferenceException` inside a critical patch.
   Reimplemented as a bytecode-level rewrite of the `"mods"` path literal only, which
   survives the refactor and degrades to "external mods disabled" instead of crashing.
5. **Steam mobile confirmation drops.** Switching to the Steam app for approval kills
   the confirmation socket → `AsyncJobFailedException`. Retry once, then fall back to
   entering a Steam Guard code (PR lineage from the Chinese fork).
6. **Steam branch selection.** `public` is hardcoded; current builds are often on
   `public-beta`. Branch is now a launcher setting (PR #69 / #70).

## 3. Build reality (why this fork repacks instead of compiling the app)

Upstream never published the inputs its own `scripts/build.sh` needs:

- custom Godot 4.5.1 `custom_build` Android AAR (the patched Vulkan pipeline-cache and
  ubershader engine) — not in the repo, not on Maven,
- patched FMOD Android bridge matching the game's bank version,
- Spine runtime (licensing),
- `upstream/godot-export/.godot/mono/publish/arm64/{sts2,GodotSharp,0Harmony}.dll`
  (game assemblies),
- release keystore (`android/sts2.keystore`).

The released 0.2.0 APK *does* contain the engine (`libgodot_android.so` = Godot
4.5.1.stable.mono.custom_build, FMOD + Spine bridges, mono 9.0 runtime). So this fork
rebuilds only the managed patcher (`STS2Mobile.dll`) and repacks it into that APK:

```
tools/repack.sh <base.apk> <dir-with-new-dlls> <out.apk>
```

`resources.arsc` stays stored + 4-byte aligned, everything else deflated, then
`apksigner` v2/v3 with the fork keystore. Verified: output re-signs and verifies.

Because the fork uses its own signing key, the app must be uninstalled before installing
(different signature); downloaded game files and launcher-side saves live under the app's
private dir and are lost unless backed up first.

## 4. Remaining work (blocked on game assemblies)

1. Obtain `sts2.dll` + `GodotSharp.dll` + `0Harmony.dll` for the exact build the user runs
   (Steam depot 2868841, app 2868840) — needs a Steam account that owns the game.
2. Diff the patched members against that assembly: `NGame.GameStartupWrapper`,
   `SaveManager.ConstructDefault`, `CloudSaveStore.SyncCloudToLocal`,
   `FontControlUtils.ApplyLocaleFontSubstitution`, `NullPlatformUtilStrategy.GetRawLanguage`,
   `ModManager.Initialize`, plus the UI/layout/input patch targets. Anything missing is
   logged as `FAILED …` by `PatchHelper` and must be re-pointed.
3. Compile `STS2Mobile.dll` (`dotnet publish -c Release`) — currently the only build
   errors are 70 × `CS0246` (missing reference types from `sts2.dll`), i.e. the source
   itself is consistent after the merge.
4. Repack + sign, verify install on a device, confirm: launcher UI → Steam login →
   depot download → game boot → no black screen → correct card art → cloud save.

## 5. Deliberately not done

- No engine rebuild (impossible without the custom Godot source/FMOD SDK).
- No package-id change (keeps data layout; requires uninstall due to signature change).
- No bundled game assets — everything is still downloaded from Steam with the user's
  own account.
