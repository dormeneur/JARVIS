#!/bin/sh
# Builds Jarvis.app: a self-contained .NET publish of Jarvis.App.Mac plus the Swift bridge, signed
# with the hardened runtime. Usage:
#   packaging/macos/make-app.sh [arm64|x64] [version]
# Signing identity: $JARVIS_SIGN_IDENTITY ("Developer ID Application: …"), or ad-hoc ("-") when unset.
# Notarizing (needs a Developer ID identity): set JARVIS_NOTARY_PROFILE to a notarytool keychain
# profile (xcrun notarytool store-credentials <profile> --apple-id … --team-id …).
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
desktop="$(cd "$here/../.." && pwd)"
arch="${1:-$(uname -m | sed 's/x86_64/x64/')}"
version="${2:-1.0.0}"
swiftarch="$( [ "$arch" = x64 ] && echo x86_64 || echo arm64 )"
out="$desktop/artifacts/macos-$arch"
app="$out/Jarvis.app"
identity="${JARVIS_SIGN_IDENTITY:--}"
# Secure timestamps come from Apple and need a real identity; ad-hoc signatures have none.
timestamp="$( [ "$identity" = - ] && echo --timestamp=none || echo --timestamp )"

rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# The native bridge for the target architecture, then the app around it.
"$desktop/native/build.sh" "$swiftarch"
dotnet publish "$desktop/src/Jarvis.App.Mac" -c Release -r "osx-$arch" --self-contained true \
  -p:NativeArchitecture="$swiftarch" -p:Version="$version" -p:DebugType=none -o "$app/Contents/MacOS"

sed "s/__VERSION__/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
if [ -f "$desktop/assets/Jarvis.icns" ]; then cp "$desktop/assets/Jarvis.icns" "$app/Contents/Resources/"; fi

# Sign inside-out. .NET keeps its assemblies beside the executable in Contents/MacOS, where
# codesign treats every file as nested code, so each one is signed; then the bundle.
entitlements="$here/Jarvis.entitlements"
find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/Jarvis" | while read -r file; do
  codesign --force "$timestamp" --options runtime --entitlements "$entitlements" --sign "$identity" "$file"
done
codesign --force "$timestamp" --options runtime --entitlements "$entitlements" --identifier com.jarvis.desktop --sign "$identity" "$app"
codesign --verify --deep --strict "$app"
echo "Built $app (signed with ${identity})"

if [ -n "${JARVIS_NOTARY_PROFILE:-}" ] && [ "$identity" != "-" ]; then
  ditto -c -k --keepParent "$app" "$out/Jarvis.zip"
  xcrun notarytool submit "$out/Jarvis.zip" --keychain-profile "$JARVIS_NOTARY_PROFILE" --wait
  xcrun stapler staple "$app"
  echo "Notarized and stapled."
fi
