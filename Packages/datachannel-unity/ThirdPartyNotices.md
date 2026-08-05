# Third-party notices

This package **ships prebuilt binaries** under `Plugins/` (macOS universal, Windows x64, Linux x64). Everything below is linked **statically** into each of them — there is no separate library to obtain at runtime, and that is exactly why these notices exist.

Each shipped binary names the exact sources it was built from in its own build record under `Report~/` (`macOS.json`, `Windows-x86_64.json`, `Linux-x86_64.json`): the `upstream` block carries the pins, `source.commit` the commit of this repository, and `ci.run_url` the run that produced it. **The build record, not this file, is the authoritative answer to "which sources are inside the binary I have"** — this file is the licence map.

The pins are also in `native/versions.lock`; the transitive commits below are the submodules of the pinned libdatachannel tag.

## Compiled into the shipped binaries

| Component | Version | Licence | Source |
|-----------|---------|---------|--------|
| libdatachannel | `v0.24.5` | **MPL-2.0** | https://github.com/paullouisageneau/libdatachannel/tree/v0.24.5 |
| libjuice | `3c40a35` (submodule) | **MPL-2.0** | https://github.com/paullouisageneau/libjuice/tree/3c40a3545b6b1b62c7adee7f8f2bd58aa290afd6 |
| usrsctp | `fec583d` (submodule) | BSD-3-Clause | https://github.com/sctplab/usrsctp/tree/fec583d54493f879d2ae44a743423bf8a04371ab |
| plog | `94899e0` (submodule) | MIT | https://github.com/SergiusTheBest/plog/tree/94899e0b926ac1b0f4750bfbd495167b4a6ae9ef |
| Mbed TLS | `v3.6.7` | Apache-2.0 **or** GPL-2.0-or-later (dual; used here under Apache-2.0) | https://github.com/Mbed-TLS/mbedtls/tree/v3.6.7 |

Mbed TLS is built from source with a user config enabling `MBEDTLS_SSL_DTLS_SRTP` (SPEC §3/§9). No system or Homebrew OpenSSL/Mbed TLS is loaded — a CI gate checks each binary's dependencies against an allowlist for exactly this.

## In the pinned tree, but not in the shipped binaries

`libsrtp` (Cisco, BSD-3-Clause) and `nlohmann/json` (MIT) are submodules of the libdatachannel tag but are **not compiled in**: this build sets `NO_MEDIA` and `NO_WEBSOCKET`. Verified by scanning the shipped macOS binary's symbol table — no libsrtp or nlohmann symbols are present. (The `*srtp*` symbols that do appear belong to Mbed TLS's DTLS-SRTP extension, which is a different thing with a similar name.)

`datachannel-wasm` (`v0.4.0`, MIT — https://github.com/paullouisageneau/datachannel-wasm/tree/v0.4.0) is the intended WebGL backend. **WebGL is not built or shipped**, so nothing from it is in this package today; the pin is recorded because the build records carry it.

## Obtaining the source

All of the above are unmodified upstream trees at the commits named, so the GitHub URLs above satisfy MPL-2.0's requirement to make Source Code Form available. If you distribute a **modified** build, you must supply source for your modifications on the same terms.

## Package-owned code

The C# (`DataChannelUnity`) and the `dcu_*` wrapper sources in this repository are under `LICENSE.md` (MIT) unless a file header says otherwise. They are a Larger Work around the MPL-2.0 components, not derived from them.
