#!/usr/bin/env bash
# 本机 macOS 构建（与 CI 同一条路径：CMake）。产物是**单一 universal bundle**
# （arm64 + x86_64），不是每架构一份 —— 见 native/CMakeLists.txt 开头的理由。
# 用法: ./native/scripts/build-macos.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD="${DCU_BUILD_DIR:-$ROOT/build/macos}"

# 上游依赖缺失时自动拉取
if [[ ! -f "$ROOT/subprojects/mbedtls/CMakeLists.txt" \
   || ! -f "$ROOT/subprojects/libdatachannel/CMakeLists.txt" ]]; then
  echo "==> bootstrap via fetch-deps.sh"
  "$ROOT/scripts/fetch-deps.sh"
fi

cmake -S "$ROOT" -B "$BUILD" \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES="arm64;x86_64"

cmake --build "$BUILD" --parallel "$(sysctl -n hw.ncpu 2>/dev/null || echo 4)"

PLUGIN="$ROOT/../Packages/datachannel-unity/Plugins/macOS/datachannel_unity.dylib"
echo "==> audit"
python3 "$ROOT/scripts/audit_plugin.py" \
  --binary "$PLUGIN" --platform darwin \
  --expected "$ROOT/exports/expected-symbols.txt"

echo "Done → $PLUGIN"
