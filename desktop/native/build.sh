#!/bin/sh
# Builds libjarvis_mac.dylib (the macOS platform bridge) for this Mac's architecture, or for the
# one given (arm64 | x86_64 | universal). Usage: native/build.sh [arch] [output-dir]
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
arch="${1:-$(uname -m)}"
out="${2:-$here/build}"
mkdir -p "$out"

build() {
  swiftc -emit-library -O -swift-version 5 -module-name JarvisMac \
    -target "$1-apple-macos14.0" \
    -Xlinker -install_name -Xlinker @rpath/libjarvis_mac.dylib \
    -o "$2" "$here"/JarvisMac/*.swift
}

if [ "$arch" = universal ]; then
  build arm64 "$out/libjarvis_mac.arm64.dylib"
  build x86_64 "$out/libjarvis_mac.x86_64.dylib"
  lipo -create -output "$out/libjarvis_mac.dylib" "$out/libjarvis_mac.arm64.dylib" "$out/libjarvis_mac.x86_64.dylib"
  rm "$out/libjarvis_mac.arm64.dylib" "$out/libjarvis_mac.x86_64.dylib"
else
  build "$arch" "$out/libjarvis_mac.dylib"
fi
