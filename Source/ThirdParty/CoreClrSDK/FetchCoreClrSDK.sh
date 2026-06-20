#!/usr/bin/env bash
# FetchCoreClrSDK.sh — download the Android arm64 CoreCLR runtime + BCL from NuGet
# and populate Source/ThirdParty/CoreClrSDK/Android/{lib,runtime}.
#
# The CoreClrSDK binaries (.so / .dll) are git-ignored (they're large, version-bound
# build artifacts). Run this script once after cloning (or when upgrading the .NET
# version) to populate the SDK. Mirrors how MonoSDK is populated from GitHub Releases.
#
# Usage:
#   ./FetchCoreClrSDK.sh              # default version (see RUNTIME_VERSION below)
#   ./FetchCoreClrSDK.sh 10.0.9       # explicit version
#
# Requirements: curl (or wget) + unzip on PATH.

set -euo pipefail

RUNTIME_VERSION="${1:-10.0.9}"
NUGET_URL="https://globalcdn.nuget.org/packages/microsoft.netcore.app.runtime.android-arm64.${RUNTIME_VERSION}.nupkg"

# Resolve the SDK root (directory containing this script).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SDK_ROOT="$SCRIPT_DIR"
LIB_DIR="$SDK_ROOT/Android/lib"
RUNTIME_DIR="$SDK_ROOT/Android/runtime"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "=== FetchCoreClrSDK: .NET ${RUNTIME_VERSION} (android-arm64) ==="
echo "SDK root: $SDK_ROOT"

# Download the nupkg (it's a zip).
NUPKG="$WORK_DIR/runtime.nupkg"
echo "Downloading $NUGET_URL ..."
if command -v curl >/dev/null 2>&1; then
  curl -sL "$NUGET_URL" -o "$NUPKG"
elif command -v wget >/dev/null 2>&1; then
  wget -q "$NUGET_URL" -O "$NUPKG"
else
  echo "ERROR: need curl or wget on PATH" >&2
  exit 1
fi

if [ ! -s "$NUPKG" ]; then
  echo "ERROR: download failed or empty (check version $RUNTIME_VERSION exists on NuGet)" >&2
  exit 1
fi

# Extract the nupkg.
echo "Extracting nupkg ..."
unzip -q "$NUPKG" -d "$WORK_DIR/extracted"

# Native .so → Android/lib/ (only .so; skip .a static libs, .dex, .jar).
mkdir -p "$LIB_DIR"
rm -f "$LIB_DIR"/*.so
cp "$WORK_DIR"/extracted/runtimes/android-arm64/native/*.so "$LIB_DIR"/
echo "Installed native .so to $LIB_DIR:"
ls -1 "$LIB_DIR"

# BCL managed .dll → Android/runtime/.
mkdir -p "$RUNTIME_DIR"
rm -f "$RUNTIME_DIR"/*.dll
cp "$WORK_DIR"/extracted/runtimes/android-arm64/lib/net10.0/*.dll "$RUNTIME_DIR"/
DLL_COUNT=$(ls -1 "$RUNTIME_DIR"/*.dll | wc -l)
echo "Installed $DLL_COUNT BCL .dll to $RUNTIME_DIR"

echo "=== Done. CoreClrSDK populated for android-arm64 (.NET ${RUNTIME_VERSION}). ==="
