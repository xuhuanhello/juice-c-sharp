#!/usr/bin/env bash
# Bootstrap git trees into native/subprojects (Meson wraps use the same dirs).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# shellcheck source=read-lock.sh
source "$ROOT/scripts/read-lock.sh"

mkdir -p "$ROOT/subprojects"

fetch_repo() {
  local name="$1" url="$2" tag="$3"
  local dir="$ROOT/subprojects/$name"
  if [[ -d "$dir/.git" ]]; then
    echo "==> updating $name to $tag"
    git -C "$dir" fetch --depth 1 origin "refs/tags/$tag:refs/tags/$tag" 2>/dev/null || \
      git -C "$dir" fetch --tags origin
    git -C "$dir" checkout -f "$tag"
  elif [[ -d "$dir" && ! -d "$dir/.git" ]]; then
    echo "==> $dir exists without .git; leaving as-is"
    return 0
  else
    echo "==> cloning $name @ $tag"
    rm -rf "$dir"
    git clone --depth 1 --branch "$tag" "$url" "$dir"
  fi
  if [[ -f "$dir/.gitmodules" ]]; then
    git -C "$dir" submodule update --init --recursive --depth 1 || true
  fi
}

fetch_repo libdatachannel https://github.com/paullouisageneau/libdatachannel.git "$LIBDATACHANNEL_TAG"
fetch_repo datachannel-wasm https://github.com/paullouisageneau/datachannel-wasm.git "$DATACHANNEL_WASM_TAG"
fetch_repo mbedtls https://github.com/Mbed-TLS/mbedtls.git "$MBEDTLS_TAG"

echo "Done. subprojects: libdatachannel=$LIBDATACHANNEL_TAG datachannel-wasm=$DATACHANNEL_WASM_TAG mbedtls=$MBEDTLS_TAG"
