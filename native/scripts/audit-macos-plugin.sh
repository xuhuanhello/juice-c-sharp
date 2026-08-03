#!/usr/bin/env bash
# Fail if macOS plugin pulls system/brew crypto dylibs or exports non-dcu symbols.
set -euo pipefail
BIN="${1:?usage: audit-macos-plugin.sh <path-to-bundle-or-dylib>}"

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

COUNT=$(echo "$EXPORTS" | grep -c '^dcu_' || true)
echo "OK: $COUNT dcu_* exports, no forbidden crypto dylibs"
