#!/usr/bin/env bash
# Repack the published StS2 Launcher APK with a freshly built STS2Mobile.dll.
#
# Why repack: upstream's build needs a custom Godot 4.5.1 Android AAR, a patched
# FMOD bridge and the game's own assemblies, none of which are published. The
# released APK already contains that engine, so we keep it and replace only the
# managed patcher assemblies.
set -euo pipefail

BASE_APK="$1"          # released StS2Launcher-v<ver>.apk
PATCHER_DIR="$2"       # directory holding the new managed DLLs to inject
OUT_APK="$3"           # output path

ANDROID_HOME="${ANDROID_HOME:-/home/ubuntu/Android}"
BT="$ANDROID_HOME/build-tools/35.0.0"
KEYSTORE="${KEYSTORE:-/home/ubuntu/workspace/sts2-work/keys/sts2-release.jks}"
KS_ALIAS="${KS_ALIAS:-sts2}"
KS_PASS="${KS_PASS:?set KS_PASS (keystore password)}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "[1/4] unpack base apk"
unzip -q "$BASE_APK" -d "$WORK/apk"

echo "[2/4] inject managed assemblies"
shopt -s nullglob
for dll in "$PATCHER_DIR"/*.dll; do
    cp "$dll" "$WORK/apk/assets/dotnet_bcl/$(basename "$dll")"
    echo "  + $(basename "$dll")"
done
shopt -u nullglob

echo "[3/4] repack (resources.arsc stored+aligned, rest deflated)"
python3 - "$WORK/apk" "$WORK/unsigned.apk" <<'PY'
import os, struct, sys, zipfile, zlib

src, out = sys.argv[1], sys.argv[2]
ALIGN = 4


def deflate(data):
    c = zlib.compressobj(9, zlib.DEFLATED, -15)
    return c.compress(data) + c.flush()


class AligningWriter:
    """Minimal zip writer that 4-byte aligns stored entries (zipalign equivalent)."""

    def __init__(self, path):
        self.fh = open(path, "wb")
        self.central = []

    def add(self, rel, data, stored, align):
        offset = self.fh.tell()
        method = zipfile.ZIP_STORED if stored else zipfile.ZIP_DEFLATED
        payload = data if stored else deflate(data)
        name = rel.encode()
        padding = b""
        if align:
            pad = (-(offset + 30 + len(name))) % ALIGN
            padding = b"\x00" * pad
        crc = zlib.crc32(data) & 0xFFFFFFFF
        dos_time = (0 << 11) | (0 << 5) | 0
        dos_date = ((1981 - 1980) << 9) | (1 << 5) | 1
        self.fh.write(b"PK\x03\x04")
        self.fh.write(
            struct.pack(
                "<HHHHHIIIHH",
                20, 0, method, dos_time, dos_date,
                crc, len(payload), len(data), len(name), len(padding),
            )
        )
        self.fh.write(name)
        self.fh.write(padding)
        self.fh.write(payload)
        self.central.append((name, method, dos_time, dos_date, crc, len(payload), len(data), offset, len(padding)))

    def close(self):
        start = self.fh.tell()
        for name, method, dos_time, dos_date, crc, csize, usize, offset, extra_len in self.central:
            self.fh.write(b"PK\x01\x02")
            self.fh.write(
                struct.pack(
                    "<HHHHHHIIIHHHHHII",
                    20, 20, 0, method, dos_time, dos_date,
                    crc, csize, usize, len(name), 0, 0, 0, 0, 0o644 << 16 >> 16, offset,
                )
            )
            self.fh.write(name)
        size = self.fh.tell() - start
        self.fh.write(b"PK\x05\x06")
        self.fh.write(struct.pack("<HHHHIIH", 0, 0, len(self.central), len(self.central), size, start, 0))
        self.fh.close()


w = AligningWriter(out)
count = 0
for root, _dirs, files in os.walk(src):
    for name in sorted(files):
        path = os.path.join(root, name)
        rel = os.path.relpath(path, src).replace(os.sep, "/")
        if rel.startswith("META-INF/"):
            continue
        with open(path, "rb") as fh:
            data = fh.read()
        stored = rel == "resources.arsc"
        w.add(rel, data, stored, stored)
        count += 1
w.close()
print("entries:", count)
PY

echo "[4/4] sign (v1+v2+v3)"
mkdir -p "$(dirname "$OUT_APK")"
"$BT/apksigner" sign \
    --ks "$KEYSTORE" --ks-key-alias "$KS_ALIAS" \
    --ks-pass "pass:$KS_PASS" --key-pass "pass:$KS_PASS" \
    --v1-signing-enabled true --v2-signing-enabled true --v3-signing-enabled true \
    --min-sdk-version 24 --out "$OUT_APK" "$WORK/unsigned.apk"

"$BT/apksigner" verify --verbose "$OUT_APK" | head -8
ls -la "$OUT_APK"
