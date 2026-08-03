#!/usr/bin/env bash
# 本机 macOS arm64 构建（与 CI 同一条路径：CMake）。
# 用法: ./native/scripts/build-macos-arm64.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD="${DCU_BUILD_DIR:-$ROOT/build/macos-arm64}"

# 上游依赖缺失时自动拉取
if [[ ! -f "$ROOT/subprojects/mbedtls/CMakeLists.txt" \
   || ! -f "$ROOT/subprojects/libdatachannel/CMakeLists.txt" ]]; then
  echo "==> bootstrap via fetch-deps.sh"
  "$ROOT/scripts/fetch-deps.sh"
fi

cmake -S "$ROOT" -B "$BUILD" \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES=arm64

cmake --build "$BUILD" --parallel "$(sysctl -n hw.ncpu 2>/dev/null || echo 4)"

BUNDLE="$ROOT/../Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle"
echo "==> audit"
"$ROOT/scripts/audit-macos-plugin.sh" "$BUNDLE"

echo "Done → $BUNDLE"
