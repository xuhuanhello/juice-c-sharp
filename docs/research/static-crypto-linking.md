# Research: Fully static crypto for libdatachannel v0.24.5 (MbedTLS 3 vs OpenSSL static)

**Ticket:** https://github.com/xuhuanhello/juice-c-sharp/issues/17  
**Parent:** #16  
**Date:** 2026-08-02  
**Upstream pin studied:** [paullouisageneau/libdatachannel](https://github.com/paullouisageneau/libdatachannel) tag **`v0.24.5`**  
**Product constraint (SPEC):** MbedTLS only, static into plugin — no dual OpenSSL/MbedTLS matrix in v1  
**Local evidence:** macOS arm64 plugin currently **dynamically** links Homebrew OpenSSL 3

---

## Verdict (shipping recommendation)

| Question | Answer |
|----------|--------|
| What crypto should ship in plugins? | **Vendored Mbed TLS 3.6.x LTS, built as static archives, linked into the single plugin** |
| Can brew `mbedtls` (4.x) be used with v0.24.5? | **No.** Host brew default is **4.2.0**; headers required by libdatachannel (e.g. `mbedtls/ctr_drbg.h`) are **gone** |
| Feasible host pin without vendoring? | **`brew install mbedtls@3`** (currently **3.6.7**, keg-only) + `CMAKE_PREFIX_PATH=$(brew --prefix mbedtls@3)` — fine for **dev**, not ideal as sole CI/shipping strategy |
| Full-static OpenSSL as product default? | **No for v1.** Valid **escape hatch** for host hacks; larger, default upstream path, worse mobile story |
| Align with SPEC «全平台 MbedTLS»? | **Keep SPEC.** Do **not** open a dual shipping matrix. Document OpenSSL static only as non-shipping fallback |
| How to prove “no system crypto dylibs”? | Platform-specific link audit after every plugin build (see §5) |

**Recommended pin for `native/versions.lock`:**

```text
crypto=mbedtls-3.6.7          # or any 3.6.x ≥ security floor; track Mbed-TLS/mbedtls tags
# Prefer vendored source under native/third_party/mbedtls @ tag, not brew dylibs
```

---

## Primary sources

| Source | Role | URL / path |
|--------|------|------------|
| libdatachannel BUILDING | Crypto backend options | https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/BUILDING.md |
| libdatachannel CMakeLists | `USE_MBEDTLS`, `find_package(MbedTLS 3 REQUIRED)`, Apple OpenSSL static switch | local: `native/subprojects/libdatachannel/CMakeLists.txt` (tag v0.24.5) |
| Upstream MbedTLS CI | Official path uses **`brew install mbedtls@3`** | https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/.github/workflows/build-mbedtls.yml |
| Certificate gen (MbedTLS API surface) | Uses `ctr_drbg`, `entropy`, SHA, ECP, RSA | `native/subprojects/libdatachannel/src/impl/certificate.cpp` |
| Mbed TLS 4.0 release notes | Breaking API; TF-PSA-Crypto split | https://github.com/Mbed-TLS/mbedtls/releases/tag/mbedtls-4.0.0 |
| Mbed TLS 4.0 migration guide | Legacy DRBG/entropy apps no longer valid | https://github.com/Mbed-TLS/mbedtls/blob/mbedtls-4.0.0/docs/4.0-migration-guide.md |
| Mbed TLS BRANCHES / LTS | **3.6 LTS supported until at least March 2027** | https://github.com/Mbed-TLS/mbedtls/blob/development/BRANCHES.md ; releases text for 3.6.x |
| Homebrew `mbedtls` | Default formula → **4.2.0** (alias `mbedtls@4`) | https://formulae.brew.sh/formula/mbedtls |
| Homebrew `mbedtls@3` | Keg-only **3.6.7**; formula deprecation date **2027-03-31** | https://formulae.brew.sh/formula/mbedtls@3 |
| Mbed TLS license | Dual **Apache-2.0 OR GPL-2.0-or-later** (choose one) | https://github.com/Mbed-TLS/mbedtls/blob/development/LICENSE ; local Cellar `LICENSE` |
| OpenSSL 3 license | **Apache-2.0** | https://github.com/openssl/openssl/blob/master/LICENSE.txt |
| CMake `FindOpenSSL` | `OPENSSL_USE_STATIC_LIBS` selects static libs | https://cmake.org/cmake/help/latest/module/FindOpenSSL.html |
| Product SPEC | Crypto = MbedTLS only, static | `docs/SPEC.md` §3, §8 |
| Prior research | Meson + crypto backend table | `docs/research/meson-cmake-unity-plugins.md` §2.3 |
| Host build scripts (current) | macOS arm64 uses brew OpenSSL **shared** | `native/scripts/build-macos-arm64.sh` |
| MPL packaging research | Notices for transitive crypto | `docs/research/mpl-upm-binaries.md` |

Local measurements (this workspace, 2026-08-02):

```text
# Plugin link line (otool -L)
Packages/.../libdatachannel_unity.dylib
  → /opt/homebrew/opt/openssl@3/lib/libssl.3.dylib
  → /opt/homebrew/opt/openssl@3/lib/libcrypto.3.dylib
  (+ CoreFoundation, Security, libc++, libSystem)

# brew formulas on this machine
mbedtls      → 4.2.0 (linked)
mbedtls@3    → 3.6.7 (keg-only, not installed)
openssl@3    → provides both .dylib and .a (libcrypto.a ~8.2 MiB, libssl.a ~1.4 MiB)

# Headers under brew mbedtls 4.2 include/mbedtls/
MISSING: ctr_drbg.h, entropy.h, sha1.h, sha256.h, sha512.h, ecp.h, rsa.h
PRESENT: ssl.h, x509_crt.h, pk.h, md.h, error.h, build_info.h, ...
```

---

## 1. Facts: brew MbedTLS 4.x vs libdatachannel v0.24.5

### 1.1 What v0.24.5 expects

Upstream CMake (v0.24.5):

```cmake
option(USE_MBEDTLS "Use Mbed TLS instead of OpenSSL" OFF)
# ...
elseif(USE_MBEDTLS)
  if(NOT TARGET MbedTLS::MbedTLS)
    find_package(MbedTLS 3 REQUIRED)   # ← version component “3”
  endif()
  target_compile_definitions(datachannel PRIVATE USE_MBEDTLS=1)
  target_link_libraries(datachannel PRIVATE MbedTLS::MbedTLS)
  # same for datachannel-static
```

BUILDING.md: with `USE_MBEDTLS=1`, Mbed TLS replaces OpenSSL as the TLS/crypto backend; otherwise OpenSSL is default.

Upstream CI workflow `build-mbedtls.yml` (v0.24.5 tree) installs **`mbedtls@3`**, not unversioned `mbedtls`:

```yaml
- name: Install Mbed TLS
  run: brew update && brew install mbedtls@3
- name: cmake
  run: cmake -B build -DUSE_MBEDTLS=1 ... -DCMAKE_PREFIX_PATH=$(brew --prefix mbedtls@3)
```

### 1.2 Concrete API dependence on 3.x “legacy crypto”

Under `USE_MBEDTLS`, certificate generation in `src/impl/certificate.cpp` is not a thin PSA wrapper — it uses classic 3.x types and headers, including:

| API / type | Typical 3.x header |
|------------|--------------------|
| `mbedtls_ctr_drbg_context`, `mbedtls_ctr_drbg_seed`, `mbedtls_ctr_drbg_random`, … | `mbedtls/ctr_drbg.h` |
| `mbedtls_entropy_context`, `mbedtls_entropy_func` | `mbedtls/entropy.h` |
| `mbedtls_sha1` / `mbedtls_sha256` / `mbedtls_sha512` | `mbedtls/sha*.h` |
| `mbedtls_ecp_gen_key`, `MBEDTLS_ECP_DP_SECP256R1` | `mbedtls/ecp.h` |
| `mbedtls_rsa_gen_key` | `mbedtls/rsa.h` |
| `mbedtls_x509write_crt_*`, `mbedtls_pk_*` | `mbedtls/x509_crt.h`, `mbedtls/pk.h` |

Excerpt (local v0.24.5 tree):

```cpp
mbedtls_entropy_context entropy;
mbedtls_ctr_drbg_context drbg;
// ...
mbedtls_ctr_drbg_seed(&drbg, mbedtls_entropy_func, &entropy, ...);
mbedtls_ecp_gen_key(MBEDTLS_ECP_DP_SECP256R1, ..., mbedtls_ctr_drbg_random, &drbg);
```

### 1.3 Why Mbed TLS 4.x breaks this

Mbed TLS **4.0.0** release notes state significant **API breakage** and a split where PSA Crypto moves to **TF-PSA-Crypto**. The [4.0 migration guide](https://github.com/Mbed-TLS/mbedtls/blob/mbedtls-4.0.0/docs/4.0-migration-guide.md) is explicit that applications that used to own:

- `mbedtls_entropy_context`
- `mbedtls_ctr_drbg_context` / `mbedtls_hmac_drbg_context`

…as the library RNG **can no longer do that** in the same way: “This is no longer necessary, **or possible**. All features that require a random generator (RNG) now use the one provided by the PSA subsystem.”

On this host, brew **mbedtls 4.2.0** simply **does not install** `mbedtls/ctr_drbg.h` or `mbedtls/entropy.h` (verified). That matches the failure mode already noted in `native/versions.lock` and `native/scripts/build-macos-arm64.sh` comments.

**Conclusion:** libdatachannel **v0.24.5 is a Mbed TLS 3.x consumer**. It is **not** Mbed TLS 4-ready. A future libdatachannel release that ports certificate/DTLS code to PSA would be required before brew default `mbedtls` 4.x is usable.

### 1.4 Feasible pins

| Pin strategy | Version | Pros | Cons |
|--------------|---------|------|------|
| **Vendor source (recommended)** | Tag **`v3.6.7`** or current 3.6.x LTS patch | Reproducible; static-only; works on all CI images; survives brew default flips | Extra fetch/build step; you own security bumps until March 2027 LTS end |
| **brew `mbedtls@3`** | **3.6.7** today | Matches upstream CI; zero vendor tree | Keg-only; **dylib-oriented** install by default; formula deprecation **2027-03-31**; not available the same way on Win/Android/iOS CI |
| **system Linux package** | Distro-dependent 3.x | Convenient for Linux-only | Unreproducible across distros; may be shared `.so` |
| **brew / system mbedtls 4.x** | 4.2.x | Newest | **Incompatible** with v0.24.5 |

**Security floor:** stay on the **3.6 LTS line** and track [Mbed-TLS/mbedtls releases](https://github.com/Mbed-TLS/mbedtls/releases) for 3.6.x patches (LTS: bug/security fixes **until at least March 2027**). Prefer latest 3.6.x available at each CI rebuild rather than freezing forever on the first pin, but **never jump major to 4.x** without an upstream libdatachannel bump that documents MbedTLS 4 support.

**Suggested lock fields:**

```text
libdatachannel=v0.24.5
crypto=mbedtls
mbedtls=v3.6.7
# source: https://github.com/Mbed-TLS/mbedtls/tree/v3.6.7
```

---

## 2. Full-static OpenSSL as alternative (pros / cons)

OpenSSL is libdatachannel’s **default** backend (`USE_MBEDTLS=OFF`, `USE_GNUTLS=OFF`). CMake already special-cases Apple when `OPENSSL_USE_STATIC_LIBS` is set: it forces `libcrypto.a` / `libssl.a` under `OPENSSL_ROOT_DIR` instead of `.dylib` (see v0.24.5 `CMakeLists.txt` around the `OPENSSL_USE_STATIC_LIBS` block).

### 2.1 Pros

| Point | Detail |
|-------|--------|
| Upstream default | Best exercised path in many libdatachannel CI jobs (`build-openssl.yml`) |
| Feature completeness | Mature DTLS/TLS stack; fewer “did anyone test this backend?” surprises on desktop |
| License | OpenSSL **3.x = Apache-2.0** — permissive, redistributable in closed games with notices |
| Host convenience | brew `openssl@3` ships **static `.a` next to dylibs** (measured); Windows vcpkg/ nuget ecosystems know OpenSSL well |
| Escape hatch | If MbedTLS pin or find-module fails on a host, static OpenSSL can unblock a developer machine |

### 2.2 Cons (why not product default)

| Point | Detail |
|-------|--------|
| **Size** | Static `libcrypto.a` alone ~**8+ MiB** on this arm64 brew bottle; final plugin grows more than MbedTLS’s three archives (mbedcrypto+mbedtls+mbedx509 statics are typically a few MiB combined when stripped / configured for DTLS-only) |
| **Mobile / iOS / Android** | Static OpenSSL is doable but historically painful (export surface, bitcode-era folklore, NDK ABI, App Store scrutiny of crypto symbols). MbedTLS was designed for embed/static |
| **Find-package hell** | BUILDING.md documents `OPENSSL_ROOT_DIR` footguns on macOS; easy to **think** you linked static and still end up with brew **dylibs** (current repo state proves this) |
| **SPEC / CI matrix** | SPEC §3: *“MbedTLS only… No dual OpenSSL/MbedTLS matrix in v1”*. Dual shipping multiplies validation (DTLS handshake, cert gen, Android symbol conflicts) |
| **Runtime footguns** | Accidental mix of static OpenSSL symbols with another plugin’s OpenSSL (or system LibreSSL on older macOS) can cause subtle breakage if exports are not hidden |

### 2.3 When OpenSSL static is still useful

1. **Local developer bootstrap** on macOS when `mbedtls@3` is not installed and vendored tree not fetched yet (document as **non-shipping**).
2. **Regression row** in CI (optional, non-blocking) to prove libdatachannel still builds with OpenSSL — not a second artifact in the UPM package.
3. **Temporary unblock** if a MbedTLS security rebuild is mid-flight — still prefer re-pinning 3.6.x ASAP.

**License note (not legal advice):** Apache-2.0 (OpenSSL 3) and dual Apache-2.0/GPL (MbedTLS, choose Apache-2.0) are both redistributable with attribution. Shipping either requires `ThirdPartyNotices.md` / license texts (see `docs/research/mpl-upm-binaries.md`). MPL-2.0 still applies to libdatachannel sources regardless of crypto backend.

---

## 3. Platform recipes: one plugin, no system crypto paths

Goal for every **shipping** artifact:

- macOS: `otool -L` shows **no** `/opt/homebrew/**`, `/usr/local/opt/**`, `@rpath/libssl*`, `libmbedtls*.dylib` from brew  
- Linux (if ever): `ldd` / `readelf -d` shows **no** `NEEDED libssl|libcrypto|libmbedtls|libmbedcrypto|libmbedx509`  
- Windows: `dumpbin /dependents` shows **no** `libssl*.dll` / `libcrypto*.dll`  
- Android: `readelf -d` / `llvm-readobj` same as Linux  
- iOS: pure static `.a` (or static framework); no embedded crypto dylibs  

WebGL is **out of scope** for this ticket: datachannel-wasm uses **browser** WebRTC/TLS, not libdatachannel’s OpenSSL/MbedTLS.

### 3.0 Common CMake flags (libdatachannel)

```bash
cmake -S native/subprojects/libdatachannel -B build/libdc \
  -DCMAKE_BUILD_TYPE=Release \
  -DNO_MEDIA=ON \
  -DNO_WEBSOCKET=ON \
  -DNO_EXAMPLES=ON \
  -DNO_TESTS=ON \
  -DUSE_NICE=OFF \
  -DUSE_MBEDTLS=ON \
  -DUSE_GNUTLS=OFF \
  -DBUILD_SHARED_LIBS=OFF \
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
  -DCMAKE_PREFIX_PATH="<mbedtls-3.6-static-prefix>"
```

Then link the Unity ABI (`dcu_*`) as a **shared** plugin (desktop/Android) or **static** archive (iOS) against `datachannel-static` **and** the three MbedTLS static libraries, with symbol visibility limited where the toolchain allows (`-fvisibility=hidden` / MSVC `/GL`+export list for `dcu_*` only).

**Critical:** `BUILD_SHARED_LIBS=OFF` only makes **libdatachannel** static. Crypto is still whatever `find_package(MbedTLS)` resolves. If that finds brew **dylibs**, the final plugin will `NEEDED` them. You must point at a **static** MbedTLS install (or imported `.a` targets).

### 3.1 Vendor MbedTLS 3.6 once (all platforms reuse the pattern)

```bash
git clone --depth 1 --branch v3.6.7 https://github.com/Mbed-TLS/mbedtls.git third_party/mbedtls
cmake -S third_party/mbedtls -B build/mbedtls \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="$PWD/prefix/mbedtls" \
  -DENABLE_TESTING=OFF \
  -DENABLE_PROGRAMS=OFF \
  -DUSE_SHARED_MBEDTLS_LIBRARY=OFF \
  -DUSE_STATIC_MBEDTLS_LIBRARY=ON \
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON
  # plus platform toolchain / ANDROID_ABI / IOS_ARCH as needed
cmake --build build/mbedtls --target install -j
```

Optional size knobs (later engineering, not required for first green build): custom `mbedtls_config.h` disabling unused ciphersuites while keeping **DTLS 1.2 + ECDHE + cert generation** paths libdatachannel needs. Validate with upstream `tests` if config is narrowed.

Record the tag in `versions.lock` and fetch via `native/scripts/fetch-deps.sh` (extend alongside libdatachannel).

### 3.2 macOS (arm64 / x64 thin)

| Step | Action |
|------|--------|
| Crypto | Vendor static MbedTLS 3.6 (or `mbedtls@3` **only if** you force `.a` via `CMAKE_FIND_LIBRARY_SUFFIXES` / imported targets — prefer vendor) |
| libdatachannel | Flags in §3.0; `CMAKE_OSX_ARCHITECTURES=arm64` (or `x86_64`) — **thin**, not universal per SPEC |
| Final link | `clang++ -shared` plugin sources + `-force_load libdatachannel.a` + `libmbedtls.a libmbedx509.a libmbedcrypto.a` + `-framework CoreFoundation -framework Security` |
| **Do not** | `-L/opt/homebrew/opt/openssl@3/lib -lssl -lcrypto` (current `build-macos-arm64.sh`) |
| Audit | `otool -L libdatachannel_unity.dylib` → only system libs/frameworks |
| Install name | Prefer `@rpath` or `@loader_path` without absolute build paths; avoid encoding brew paths |

**Apple note:** `Security.framework` / `CoreFoundation` system linkage is expected and fine. That is **not** OpenSSL.

**If temporarily using OpenSSL static on macOS:**

```bash
cmake ... -DUSE_MBEDTLS=OFF \
  -DOPENSSL_ROOT_DIR="$(brew --prefix openssl@3)" \
  -DOPENSSL_USE_STATIC_LIBS=TRUE
# Final link must use .a only; re-run otool -L
```

Homebrew’s dual presence of `.a` and `.dylib` makes silent dylib pickup common — always audit.

### 3.3 Windows (x64; arm64 later)

| Step | Action |
|------|--------|
| Crypto | Build MbedTLS 3.6 with MSVC or clang-cl → `mbedtls.lib`, `mbedx509.lib`, `mbedcrypto.lib` (static) |
| Runtime CRT | Match Unity player expectation (typically **dynamic CRT `/MD`** for plugins); keep all static libs consistent |
| libdatachannel | Same CMake flags; generator `Ninja` or `NMake Makefiles` as upstream docs |
| Final link | `datachannel_unity.dll` links static libdatachannel + static MbedTLS; **no** `libssl-3-x64.dll` side-by-side |
| Audit | `dumpbin /dependents datachannel_unity.dll` |
| OpenSSL alt | vcpkg `openssl:x64-windows-static` + `OPENSSL_USE_STATIC_LIBS=ON` — larger DLL |

Export only `dcu_*` (and required Unity plugin entry if any). Hide OpenSSL/MbedTLS symbols to reduce clash risk with other native plugins.

### 3.4 Android (arm64-v8a)

| Step | Action |
|------|--------|
| Toolchain | NDK Clang, `ANDROID_ABI=arm64-v8a`, pinned API level (SPEC / meson research) |
| Crypto | Cross-build **static** MbedTLS 3.6 with the **same** NDK toolchain file |
| libdatachannel | §3.0 + Android toolchain; `BUILD_SHARED_LIBS=OFF` for deps |
| Final | Single `libdatachannel_unity.so` under `Plugins/Android/libs/arm64-v8a/` |
| Link | Prefer fully static crypto into the `.so`; use `-Wl,--whole-archive` (or equivalent) if archive member GC drops TLS objects |
| Audit | `llvm-readobj --needed-libs` / `readelf -d` — no `libmbedtls.so`, no `libssl.so` |
| Why not OpenSSL | Size + NDK packaging complexity; aligns with SPEC mobile preference for MbedTLS |

Avoid `System.loadLibrary("ssl")` chains entirely.

### 3.5 iOS (device arm64 static)

| Step | Action |
|------|--------|
| Product form | `libdatachannel_unity.a` (or later `.xcframework`) consumed as `__Internal` |
| Crypto | Static MbedTLS 3.6 for `arm64-apple-ios` (iphoneos SDK) |
| libdatachannel | Static; `RTC_STATIC` / no shared libdatachannel |
| Unity | IL2CPP links the `.a` into the Xcode project; **no** separate crypto framework |
| Audit | `nm -g` / `otool -l` on the final app binary or intermediate `.a` — no LC_LOAD_DYLIB for brew openssl/mbedtls |
| Simulator | Out of scope for v1 per SPEC |

Bitcode is obsolete on modern Xcode; do not reintroduce bitcode-specific OpenSSL myths into the build.

### 3.6 Link-graph mental model

```text
[Unity player]
    └── libdatachannel_unity.(dylib|dll|so|a)
            ├── dcu_*.o                  (project C ABI)
            ├── libdatachannel.a         (static, NO_MEDIA, NO_WEBSOCKET)
            │     ├── libjuice.a
            │     ├── libusrsctp.a
            │     └── (plog / json as configured)
            └── libmbedtls.a + libmbedx509.a + libmbedcrypto.a   ← vendored 3.6.x ONLY
```

Nothing outside the plugin should need to locate OpenSSL/MbedTLS at game runtime.

---

## 4. Alignment with SPEC («全平台 MbedTLS»)

### 4.1 What SPEC already requires

From `docs/SPEC.md`:

- §3: Crypto = **MbedTLS only**, static into plugin; **no dual OpenSSL/MbedTLS matrix in v1**
- §8: Self-contained: MbedTLS and backend deps **static-linked** into the plugin
- §9: Suggested defines include MbedTLS; `NO_MEDIA`, `NO_WEBSOCKET` (v1)

Meson top-level already sets `USE_MBEDTLS: true` in `native/meson.build`. Host script `build-macos-arm64.sh` **diverges** (OpenSSL shared) with an explicit comment that brew MbedTLS 4.x is incompatible — that is a **temporary host compromise**, not a SPEC change.

### 4.2 Recommendation: keep SPEC; fix the build; do not dual-track shipping

| Option | Recommendation |
|--------|----------------|
| Revise SPEC to “OpenSSL on desktop, MbedTLS on mobile” | **Reject for v1.** Doubles validation and contradicts explicit “no dual matrix” |
| Revise SPEC to “OpenSSL static everywhere” | **Reject.** Larger plugins; weaker mobile fit; abandons already-written Meson default |
| Keep SPEC; vendor MbedTLS 3.6.x; fix scripts + lock | **Accept.** Closes the brew-4 hole without policy churn |
| Document OpenSSL static as **non-shipping escape hatch** | **Accept.** README/scripts comment + this research doc only |

### 4.3 Concrete follow-ups (engineering, not this research ticket)

1. Extend `fetch-deps.sh` / `versions.lock` with `mbedtls=v3.6.7` (or current 3.6.x).
2. Replace OpenSSL dylib link in `build-macos-arm64.sh` with static MbedTLS 3.6 path; keep a `build-macos-arm64-openssl-dev.sh` if needed for debugging.
3. Add CI step: `otool -L` / `dumpbin` / `readelf` **fail the build** if forbidden crypto libs appear.
4. Update `ThirdPartyNotices.md` to pin MbedTLS 3.6.x source URL (Apache-2.0 choice).
5. Revisit only when **libdatachannel** documents Mbed TLS 4 / PSA support — then re-open a research ticket for major crypto bump.

### 4.4 Relationship to current `versions.lock`

Today:

```text
crypto=openssl-host-or-mbedtls3
```

That string correctly describes the **interim** host reality. After implementing this research, change to an unambiguous shipping pin, e.g.:

```text
crypto=mbedtls
mbedtls=v3.6.7
```

---

## 5. Verification checklist (definition of done for crypto static)

| Check | Command / criterion |
|-------|---------------------|
| Headers | Build log shows MbedTLS **3.6.x** include path, not brew 4.x |
| CMake | `USE_MBEDTLS=ON`; `USE_GNUTLS=OFF` |
| Archives | Final link line lists `libmbed*.a` (or `.lib`), not `-lmbedtls` resolving to dylib |
| macOS | `otool -L` free of `openssl@`, `libssl`, `libcrypto`, `libmbed*.dylib` from Cellar |
| Windows | No OpenSSL/MbedTLS DLLs beside the plugin |
| Android | Single `.so`; no extra `loadLibrary` for crypto |
| Functional | DTLS datachannel smoke (offer/answer + message) on at least one desktop + one mobile target |
| Notices | MbedTLS Apache-2.0 text + version in package notices |

---

## 6. Risk register

| Risk | Severity | Mitigation |
|------|----------|------------|
| Accidental brew OpenSSL dylib (current state) | **High** (broken players without brew) | Static MbedTLS + CI `otool` gate |
| Accidental brew MbedTLS 4 find | **High** (configure/compile fail or wrong API) | Vendor pin + `CMAKE_PREFIX_PATH` only to 3.6 prefix; never unversioned `mbedtls` |
| 3.6 LTS ends ~March 2027 | Medium | Track LTS end; plan either last 3.6 patches or libdatachannel upgrade to MbedTLS 4-capable release |
| Symbol clash with another Unity native plugin shipping OpenSSL/MbedTLS | Medium | Hide symbols; prefer static; document known conflict |
| Custom `mbedtls_config.h` too aggressive | Medium | Start with default 3.6 config; shrink only with handshake tests |
| Dual OpenSSL “just for desktop” creep | Medium | SPEC + this doc; shipping artifacts only from MbedTLS jobs |

---

## 7. Answers mapped to ticket questions

1. **brew MbedTLS 4.x incompatibility & pin**  
   Confirmed: brew default **4.2.0** lacks `mbedtls/ctr_drbg.h` (and other 3.x crypto headers) required by libdatachannel v0.24.5 certificate/DTLS code. Feasible pins: **vendored MbedTLS 3.6.x** (prefer **v3.6.7** / latest 3.6.x) or host **`mbedtls@3`** for dev only. Upstream CI itself uses `mbedtls@3`.

2. **Full-static OpenSSL tradeoffs**  
   Works (CMake supports `OPENSSL_USE_STATIC_LIBS`); better desktop familiarity; **larger**; less ideal mobile; Apache-2.0; risks dual matrix. Keep as **escape hatch**, not SPEC default.

3. **Per-platform single plugin without system crypto paths**  
   Vendor static MbedTLS 3.6 → static libdatachannel → one plugin; audit with `otool`/`ldd`/`dumpbin`/`readelf` as in §3–§5.

4. **SPEC alignment**  
   **Do not revise SPEC away from MbedTLS-only.** Fix builds to match SPEC. Dual-track only as non-shipping OpenSSL static docs/scripts.

---

## Appendix A — Why the current macOS plugin links brew OpenSSL

`native/scripts/build-macos-arm64.sh` configures libdatachannel with `USE_MBEDTLS=OFF` and links:

```bash
-L"$OPENSSL_ROOT/lib" -lssl -lcrypto
```

with `OPENSSL_ROOT` defaulting to `/opt/homebrew/opt/openssl@3`. That resolves to **shared** libraries. Measured:

```text
otool -L .../libdatachannel_unity.dylib
  /opt/homebrew/opt/openssl@3/lib/libssl.3.dylib
  /opt/homebrew/opt/openssl@3/lib/libcrypto.3.dylib
```

This is exactly the failure mode this research eliminates for shipping binaries.

## Appendix B — Size ballpark (order of magnitude, host bottles)

| Artifact (macOS arm64 brew, unstripped) | Approx size |
|-----------------------------------------|-------------|
| `libcrypto.a` (OpenSSL 3) | ~8.2 MiB |
| `libssl.a` (OpenSSL 3) | ~1.4 MiB |
| `libmbedcrypto.a` (MbedTLS 4.2 bottle — size proxy only; **not** API-compatible) | ~0.6 MiB |
| `libmbedtls.a` (same) | ~0.4 MiB |
| Current plugin dylib (OpenSSL **shared**) | ~1.9 MiB |

Expect a **static MbedTLS 3.6** plugin to be **larger than the current dylib** (crypto moves inside) but **smaller than a fully static OpenSSL** plugin. Measure again after the first static MbedTLS link; do not treat 4.x bottle sizes as exact 3.6 forecasts.

## Appendix C — Related docs / tickets

| Doc / ticket | Relevance |
|--------------|-----------|
| `docs/SPEC.md` §3, §8, §9 | Normative crypto + packaging |
| `docs/research/meson-cmake-unity-plugins.md` §2.3 | Earlier crypto backend table |
| `docs/research/mpl-upm-binaries.md` | Notices for redistributed static deps |
| Issue #16 | Parent tracking |
| Issue #11 | Upstream version pins |
| Issue #4 | Meson/CMake build research |
)
