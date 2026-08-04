#!/usr/bin/env bash
# 原生插件离线门禁（SPEC §11）：
#   1. 不得链接系统/brew 的 crypto dylib
#   2. 不得导出任何非 dcu_* 符号
#   3. 导出集必须与 native/exports/expected-symbols.txt **逐个符号**一致
#
# 第 3 条刻意不是计数。计数放过改名，也放过「删一个加一个」的净零变化。
#
# 第 3 条的作用范围在「白名单改为生成物」之后变窄了，这里写明，免得后人高估它：
# 链接期白名单现在由 gen_exports.py 从同一份 expected-symbols.txt 生成，所以
# 「有人从清单里删掉一个名字」不再被本脚本发现（实际导出会跟着少一个，两边照样
# 相等）。那一类由 gen_exports.py 的 DCU_API 交叉校验在**配置期**拦下。
# 本脚本第 3 条仍然拦得住的是：清单里有、二进制里却没有（声明并列入了清单，但
# 实现缺失或改名），以及产物根本不是这次构建出来的那一份。
set -euo pipefail
BIN="${1:?usage: audit-macos-plugin.sh <path-to-bundle-or-dylib>}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXPECTED_FILE="${DCU_EXPECTED_SYMBOLS:-$SCRIPT_DIR/../exports/expected-symbols.txt}"

if [[ -d "$BIN" && -f "$BIN/Contents/MacOS/datachannel_unity" ]]; then
  BIN="$BIN/Contents/MacOS/datachannel_unity"
fi

echo "==> otool -L $BIN"
otool -L "$BIN"

if otool -L "$BIN" | grep -E 'libssl|libcrypto|libmbedtls|libmbedcrypto|libmbedx509|openssl|homebrew' ; then
  echo "ERROR: plugin links non-system crypto/homebrew dylibs" >&2
  exit 1
fi

echo "==> exported globals (nm -gU)"
EXPORTS=$(nm -gU "$BIN" | awk '{print $3}' | sed 's/^_//' || true)
NON_DCU=$(echo "$EXPORTS" | grep -v '^dcu_' | grep -v '^$' || true)
if [[ -n "${NON_DCU:-}" ]]; then
  echo "ERROR: non-dcu exports found:" >&2
  echo "$NON_DCU" | head -50 >&2
  exit 1
fi

if [[ ! -f "$EXPECTED_FILE" ]]; then
  echo "ERROR: 缺少导出清单 $EXPECTED_FILE" >&2
  echo "       它是门禁的一部分，不是可选文件；不要删掉它让 audit 变绿。" >&2
  exit 1
fi

# 清单侧：去掉注释与空行；实际侧：只取 dcu_*。两边排序后逐行 diff。
ACTUAL=$(echo "$EXPORTS" | grep '^dcu_' | sort -u)
EXPECTED=$(sed -e 's/#.*//' -e 's/[[:space:]]//g' "$EXPECTED_FILE" | grep -v '^$' | sort -u)

if ! DIFF=$(diff <(echo "$EXPECTED") <(echo "$ACTUAL")); then
  echo "ERROR: 导出集与 $EXPECTED_FILE 不一致" >&2
  echo "$DIFF" | sed -e 's/^</  缺少（清单里有、插件没导出）: /' \
                     -e 's/^>/  多出（插件导出、清单里没有）: /' \
                     -e '/^[0-9]/d' -e '/^---$/d' >&2
  echo >&2
  echo "  若这是**有意的 ABI 变更**：更新该文件，并在同一个 commit 里 bump" >&2
  echo "  dcu.h 的 DCU_ABI_VERSION（SPEC §11）。" >&2
  echo "  若不是：多出的符号通常意味着上游符号漏过了 exports 允许列表。" >&2
  exit 1
fi

COUNT=$(echo "$ACTUAL" | wc -l | tr -d ' ')
echo "OK: $COUNT dcu_* exports match $(basename "$EXPECTED_FILE"), no forbidden crypto dylibs"
