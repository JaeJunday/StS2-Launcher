#!/usr/bin/env bash
# Build the fork: compile STS2Mobile.dll from source, then repack it into the
# released 0.2.0 APK (which carries the custom Godot 4.5.1 engine, FMOD/Spine
# bridges and the Java layer upstream never published).
#
# Inputs:
#   upstream/godot-export/.godot/mono/publish/arm64/{sts2,GodotSharp,0Harmony}.dll
#       game assemblies for the target build (Steam depot 2868841, branch set in
#       LauncherModel -> public-beta by default)
#   $BASE_APK  released StS2Launcher-v0.2.0.apk
#
# Usage: tools/build-fork-apk.sh [base.apk] [out.apk]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_APK="${1:-$HOME/workspace/sts2-work/StS2Launcher-v0.2.0.apk}"
OUT_APK="${2:-$ROOT/dist/StS2Launcher-fork-$(date +%Y%m%d)-arm64.apk}"
REF_DIR="$ROOT/upstream/godot-export/.godot/mono/publish/arm64"
KEYS="${KEYS:-$HOME/workspace/sts2-work/keys/sts2-release.jks}"
KS_PASS="${KS_PASS:-sts2fork2026}"

# This host has no per-user runtime dir (builder machines / containers often do not),
# so pin one dotnet can actually create.
export XDG_RUNTIME_DIR=/tmp/dotnet-xdg
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$HOME/.dotnet-home}"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export PATH="$HOME/.dotnet:$PATH"

mkdir -p "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"

for needed in sts2.dll GodotSharp.dll 0Harmony.dll; do
    if [ ! -f "$REF_DIR/$needed" ]; then
        echo "missing reference assembly: $REF_DIR/$needed" >&2
        echo "copy the game's managed DLLs there first (see PLAN.md section 4)" >&2
        exit 2
    fi
done

echo "[1/4] dotnet publish"
dotnet publish "$ROOT/src/STS2Mobile" -c Release

PUBLISH="$ROOT/src/STS2Mobile/bin/Release/net9.0/publish"
INJECT="$ROOT/build/inject"
rm -rf "$INJECT" && mkdir -p "$INJECT"

for dll in STS2Mobile.dll SteamKit2.dll protobuf-net.dll protobuf-net.Core.dll \
           System.IO.Hashing.dll ZstdSharp.dll; do
    if [ -f "$PUBLISH/$dll" ]; then
        cp "$PUBLISH/$dll" "$INJECT/"
    else
        echo "warning: $dll not produced by publish" >&2
    fi
done

echo "[2/4] regenerate BCL ownership list"
python3 "$ROOT/tools/gen_bcl_list.py" "$BASE_APK" "$ROOT/src/STS2Mobile/Maintenance/BclAssets.g.cs"

echo "[3/4] repack + sign"
KS_PASS="$KS_PASS" KEYSTORE="$KEYS" \
    bash "$ROOT/tools/repack.sh" "$BASE_APK" "$INJECT" "$OUT_APK"

echo "[4/4] done"
sha256sum "$OUT_APK"
