#!/usr/bin/env bash
# shellcheck disable=SC2034
LOCK="$(cd "$(dirname "$0")/.." && pwd)/versions.lock"
LIBDATACHANNEL_TAG="$(grep '^libdatachannel=' "$LOCK" | cut -d= -f2)"
DATACHANNEL_WASM_TAG="$(grep '^datachannel-wasm=' "$LOCK" | cut -d= -f2)"
MBEDTLS_TAG="$(grep '^mbedtls=' "$LOCK" | cut -d= -f2)"
