#!/bin/bash
# Builds ProSight.app for macOS. Run on a Mac with the .NET 8 SDK installed.
#   ./build-mac.sh              → Apple Silicon (osx-arm64)
#   ./build-mac.sh osx-x64      → Intel Macs
# Output: dist/<rid>/ProSight.app and dist/<rid>/ProSight-<version>-<rid>.zip
#
# Signing: the app is ad-hoc signed so it runs on the Mac that built it. To give it to other people, sign with a
# Developer ID and notarize (see README → "Distributing the Mac app"); otherwise Gatekeeper blocks it on their Macs.
set -euo pipefail
cd "$(dirname "$0")"

RID="${1:-osx-arm64}"
case "$RID" in osx-arm64|osx-x64) ;; *) echo "Unsupported runtime '$RID' (use osx-arm64 or osx-x64)" >&2; exit 2 ;; esac
command -v dotnet >/dev/null || { echo "The .NET 8 SDK is required: https://dotnet.microsoft.com/download" >&2; exit 1; }

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"
OUT="dist/$RID"
APP="$OUT/ProSight.app"
rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "→ Publishing ProSight $VERSION for $RID"
dotnet publish ProSight.Desktop/ProSight.Desktop.csproj -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -o "$OUT/publish"

cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
sed "s/__VERSION__/$VERSION/g" ProSight.Desktop/Info.plist > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist" >/dev/null

echo "→ Building the app icon"
ICONSET="$OUT/ProSight.iconset"
mkdir -p "$ICONSET"
SRC="ProSight/Resources/etr-logo.png"
for s in 16 32 128 256 512; do
  sips -z "$s" "$s" "$SRC" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s * 2)) $((s * 2)) "$SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/ProSight.icns"
rm -rf "$ICONSET"

echo "→ Signing (ad-hoc)"
codesign --force --deep --sign - --timestamp=none "$APP"
codesign --verify --deep --strict "$APP"

echo "→ Self-test"
"$APP/Contents/MacOS/ProSight" --selftest || { echo "Self-test reported failures (see above)." >&2; exit 1; }

ditto -c -k --keepParent "$APP" "$OUT/ProSight-$VERSION-$RID.zip"
echo "✓ $APP"
echo "✓ $OUT/ProSight-$VERSION-$RID.zip"
