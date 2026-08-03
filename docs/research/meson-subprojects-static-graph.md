# Research: Meson subprojects static graph (MbedTLS 3.6 + libdatachannel → `datachannel_unity`)

**Ticket:** [#24](https://github.com/xuhuanhello/juice-c-sharp/issues/24)  
**Parent map:** [#16](https://github.com/xuhuanhello/juice-c-sharp/issues/16)  
**Locked by:** [#23](https://github.com/xuhuanhello/juice-c-sharp/issues/23) (Meson-only product path; static deps as subprojects; no brew/system OpenSSL product default)  
**Aligned with:** [#17](https://github.com/xuhuanhello/juice-c-sharp/issues/17) (static crypto), [#18](https://github.com/xuhuanhello/juice-c-sharp/issues/18) (`dcu_*` only), [#19](https://github.com/xuhuanhello/juice-c-sharp/issues/19) (single macOS `.bundle`), SPEC §3/§8/§9  
**Date:** 2026-08-02  
**Pins studied:** libdatachannel **`v0.24.5`**, Mbed TLS **`v3.6.7`**, host Meson **1.11.2**, CMake **4.4.2**  
**Scope:** Research only — recommended **wrap / meson.build / cmake.subproject / config / export / install / cross-file** shape for a **fully static** plugin graph. Implementation lands in [#25](https://github.com/xuhuanhello/juice-c-sharp/issues/25).

---

## Verdict (shipping recommendation)

| Question | Answer |
|----------|--------|
| Product build entry (mac + CI) | **Meson only** under `native/`: `meson setup` → `compile` → `install` (or thin shell that only invokes those + audit) |
| How to bring in MbedTLS 3.6.x | **Meson subproject** (`mbedtls.wrap` @ `v3.6.7`) built as **`cmake.subproject`**, **static `.a` only**, with **user config enabling `MBEDTLS_SSL_DTLS_SRTP`** |
| How to bring in libdatachannel | **Meson subproject** (`libdatachannel.wrap` @ `v0.24.5`) as **`cmake.subproject`**, `USE_MBEDTLS=ON`, `BUILD_SHARED_LIBS=OFF`, hidden visibility, consume **`datachannel-static`** |
| Wire MbedTLS into libdatachannel | **Do not** rely on brew/`find_library` to system. Prefer **injected `MbedTLS::MbedTLS` IMPORTED targets** (or staged install prefix) so `find_package` is skipped — see §5 |
| Final artifact | Meson `library('datachannel_unity')` linking dcu wrapper + static graph; **export only `dcu_*`**; install into `Packages/datachannel-unity/Plugins/...` |
| Forbidden | Product path to `brew --prefix openssl` / system OpenSSL `.a` + hand-rolled `clang++` (current `build-macos-arm64.sh` default is **interim only**, to be demoted by #25) |
| brew `mbedtls` 4.x | **Incompatible** with v0.24.5 (see `docs/research/static-crypto-linking.md`) |

**Target link graph (concept):**

```text
meson (native/)
├── cmake.subproject mbedtls @ v3.6.7
│     static: libmbedcrypto.a + libmbedx509.a + libmbedtls.a
│     config: MBEDTLS_USER_CONFIG_FILE → MBEDTLS_SSL_DTLS_SRTP
├── cmake.subproject libdatachannel @ v0.24.5
│     USE_MBEDTLS=ON, BUILD_SHARED_LIBS=OFF, NO_MEDIA/NO_WEBSOCKET/...
│     links MbedTLS::MbedTLS (injected or staged)
│     embeds libjuice + usrsctp (+ plog) as its own subdirs
└── library datachannel_unity  (dcu_*.cpp, gnu_symbol_visibility=hidden)
      link: datachannel-static + (transitive mbedtls if not already private)
      export allowlist: dcu_* only
      install → Packages/datachannel-unity/Plugins/<OS>/<arch>/
```

---

## 1. Why this shape (facts from tree + upstream)

### 1.1 Locked policy (#23)

| Rule | Implication for #24 |
|------|---------------------|
| Meson is the **only** product entry | Research must produce a complete **Meson graph**, not a better shell recipe |
| Static deps as **subprojects** | `mbedtls` and `libdatachannel` live under `native/subprojects/` (wraps and/or git trees), participate in **cross-file** builds |
| No brew/system OpenSSL product path | Crypto comes from **vendored MbedTLS 3.6.x** static archives only |
| Shell becomes thin wrapper | `build-macos-arm64.sh` → `meson setup/compile/install` + `audit-macos-plugin.sh` |

### 1.2 Why MbedTLS must enable DTLS-SRTP (even with `NO_MEDIA=ON`)

libdatachannel v0.24.5 `src/impl/dtlstransport.cpp` under `USE_MBEDTLS` **always** configures DTLS-SRTP protection profiles (WebRTC use_srtp / key export path), e.g.:

```cpp
const mbedtls_ssl_srtp_profile srtpSupportedProtectionProfiles[] = {
    MBEDTLS_TLS_SRTP_AES128_CM_HMAC_SHA1_80,
    MBEDTLS_TLS_SRTP_UNSET,
};
// ...
mbedtls_ssl_conf_dtls_srtp_protection_profiles(&mConf, srtpSupportedProtectionProfiles);
```

Those types/APIs exist only when **`MBEDTLS_SSL_DTLS_SRTP`** is defined. Upstream Mbed TLS **3.6.7** ships the option **commented out** by default:

```c
//#define MBEDTLS_SSL_DTLS_SRTP   /* include/mbedtls/mbedtls_config.h ~2055 */
```

Prerequisite (already on by default in 3.6 stock config): **`MBEDTLS_SSL_PROTO_DTLS`**.

**Do not** enable full media/`libsrtp` for v1 (`NO_MEDIA=ON` stays). DTLS-SRTP **negotiation APIs in MbedTLS** are still required for the DTLS transport used by DataChannels.

This is exactly why the host script comments `FORCE_MBEDTLS` + `MBEDTLS_SSL_DTLS_SRTP` as a precondition, while defaulting to OpenSSL static as a temporary unblocker.

### 1.3 How libdatachannel finds MbedTLS

From `native/subprojects/libdatachannel/CMakeLists.txt` (v0.24.5):

```cmake
elseif(USE_MBEDTLS)
  if(NOT TARGET MbedTLS::MbedTLS)
    find_package(MbedTLS 3 REQUIRED)
  endif()
  target_link_libraries(datachannel-static PRIVATE MbedTLS::MbedTLS)
```

Implications:

1. If **`MbedTLS::MbedTLS` already exists**, `find_package` is skipped — this is the **preferred Meson injection point**.  
2. Otherwise `FindMbedTLS.cmake` (under `cmake/Modules/`) searches prefixes for `mbedtls/ssl.h` + `libmbedtls` / `libmbedcrypto` / `libmbedx509`.  
3. `find_package` does **not** understand Meson `dependency()` objects. You cannot pass a Meson dep into CMake’s find module without paths or imported targets.  
4. Host brew contamination is easy: default search paths include `/usr/local` and (with wrong `CMAKE_PREFIX_PATH`) Cellar **4.x** or shared dylibs.

### 1.4 Gaps in current `native/meson.build` (skeleton)

Existing file configures **only** libdatachannel cmake.subproject and a shared `datachannel_unity` library. Missing vs product requirements:

| Gap | Required by |
|-----|-------------|
| No mbedtls subproject / DTLS-SRTP config | #17, #23, this ticket |
| No injection of static MbedTLS into libdatachannel | `USE_MBEDTLS` path |
| No `gnu_symbol_visibility: 'hidden'` / inlines | #18 |
| No linker allowlists (`exports/*`) | #18 |
| Install path is `prefix/Plugins/<system>`, not UPM tree + arch | SPEC §8 / #19 |
| macOS produces default shared name, not **`.bundle`** | #19 |
| No PIC/visibility defines pushed into CMake deps | static→shared link hygiene |
| No cross-files under `native/cross/` | matrix later |
| `fetch-deps` puts mbedtls in `third_party/`, not `subprojects/` | #23 “subprojects graph” |

---

## 2. Recommended repository layout

```text
native/
  meson.build                 # product orchestration (replace skeleton)
  meson.options               # optional: plugin_install_root, buildtype knobs
  versions.lock               # pins (already present)
  dcu/
    include/dcu.h
    src/dcu_impl.cpp
  exports/                    # already present
    macos-exported-symbols.txt
    linux-version-script.map
    windows-exports.def
  mbedtls/
    user_config.h             # MBEDTLS_SSL_DTLS_SRTP (+ optional size knobs later)
  cross/                      # machine files (populate in #25+)
    macos-arm64.ini           # optional native file for thin arch
    macos-x64.ini
    android-arm64.ini
    ios-arm64.ini
    windows-x64.ini
  subprojects/
    mbedtls.wrap
    libdatachannel.wrap
    packagefiles/
      libdatachannel/
        0001-accept-predefined-mbedtls-targets.patch   # optional if using §5.A
        # or: dcu-mbedtls-import.cmake included via CMAKE_PROJECT_INCLUDE
      mbedtls/
        # usually none if user_config lives outside the wrap
  scripts/
    fetch-deps.sh             # keep for offline/bootstrap; wraps supersede for Meson
    build-macos-arm64.sh      # thin: meson + audit only
    audit-macos-plugin.sh
  third_party/mbedtls/        # LEGACY path from fetch-deps; migrate to subprojects/mbedtls
```

**UPM install root (default):**

```text
Packages/datachannel-unity/Plugins/
  macOS/arm64/datachannel_unity.bundle
  macOS/x64/datachannel_unity.bundle
  Windows/x86_64/datachannel_unity.dll
  ...
```

Use a Meson option, e.g. `plugin_install_root`, defaulting to  
`meson.project_source_root() / '../Packages/datachannel-unity/Plugins'`.

---

## 3. Wrap files

### 3.1 `native/subprojects/mbedtls.wrap`

```ini
[wrap-git]
directory = mbedtls
url = https://github.com/Mbed-TLS/mbedtls.git
revision = v3.6.7
depth = 1
# MbedTLS 3.6 may pull framework/ as submodule for full tree; prefer clone with submodules
clone-recursive = true

[provide]
# Optional: only if a Meson-native build is added later
# mbedtls = mbedtls_dep
```

**Notes:**

- Pin **exactly** `versions.lock` (`mbedtls=v3.6.7`). Bump lock + wrap together.  
- Prefer wrap over only `third_party/` so `meson subprojects download` / CI cache share one path.  
- If recursive clone is painful in CI, document `git submodule update --init --recursive` in `fetch-deps.sh` as bootstrap fallback **into** `subprojects/mbedtls`.

### 3.2 `native/subprojects/libdatachannel.wrap`

```ini
[wrap-git]
directory = libdatachannel
url = https://github.com/paullouisageneau/libdatachannel.git
revision = v0.24.5
depth = 1
clone-recursive = true

# Optional packagefiles for CMake injection patch:
# [wrap-file] is not mixed with wrap-git the same way; use:
# patch_directory = libdatachannel
# under [wrap-git] (Meson ≥ 0.63 style) when shipping packagefiles.
```

**Meson patch_directory pattern (when using §5.A):**

```ini
[wrap-git]
directory = libdatachannel
url = https://github.com/paullouisageneau/libdatachannel.git
revision = v0.24.5
depth = 1
clone-recursive = true
patch_directory = libdatachannel
```

With `subprojects/packagefiles/libdatachannel/` containing the patch or overlay CMake snippet.

**Submodules:** juice, usrsctp, plog must be present. Wrap `clone-recursive` or `fetch-deps.sh` is mandatory before first configure.

### 3.3 Do **not** wrap OpenSSL for product

OpenSSL may remain documented as a **non-shipping emergency** (see static-crypto research). It must not appear in the default Meson options graph.

---

## 4. MbedTLS config for DTLS-SRTP

### 4.1 Preferred: user config file (no edit of upstream headers)

`native/mbedtls/user_config.h`:

```c
/**
 * datachannel-unity — Mbed TLS user config (appended via MBEDTLS_USER_CONFIG_FILE).
 * Required by libdatachannel v0.24.5 DtlsTransport (USE_MBEDTLS).
 */
#pragma once

/* Negotiate DTLS-SRTP (RFC 5764) — mbedtls_ssl_conf_dtls_srtp_protection_profiles */
#ifndef MBEDTLS_SSL_DTLS_SRTP
#define MBEDTLS_SSL_DTLS_SRTP
#endif

/* Defensive: ensure DTLS exists if a future stock config disables it */
#ifndef MBEDTLS_SSL_PROTO_DTLS
#define MBEDTLS_SSL_PROTO_DTLS
#endif
```

Pass to MbedTLS CMake:

```text
MBEDTLS_USER_CONFIG_FILE=<abs-path>/native/mbedtls/user_config.h
```

MbedTLS CMake documents `MBEDTLS_USER_CONFIG_FILE` as **appended** to the default config (preferred over replacing entire `mbedtls_config.h` via `MBEDTLS_CONFIG_FILE`).

### 4.2 CMake defines for mbedtls `cmake.subproject`

| Define | Value | Why |
|--------|-------|-----|
| `ENABLE_PROGRAMS` | `OFF` | No apps |
| `ENABLE_TESTING` | `OFF` | Faster; no test deps |
| `USE_STATIC_MBEDTLS_LIBRARY` | `ON` | Product static |
| `USE_SHARED_MBEDTLS_LIBRARY` | `OFF` | **Never** ship dylibs |
| `CMAKE_POSITION_INDEPENDENT_CODE` | `ON` | Link into shared `.bundle`/`.so`/`.dll` |
| `CMAKE_C_VISIBILITY_PRESET` | `hidden` | Reduce export noise (#18) |
| `MBEDTLS_USER_CONFIG_FILE` | abs path to `user_config.h` | DTLS-SRTP |
| `DISABLE_PACKAGE_CONFIG_AND_INSTALL` | `OFF` only if using staged install (§5.B) | Optional |

### 4.3 Config verification (definition of done)

After first mbedtls object compile, assert in CI or a small check script:

```bash
# User config must be in the compile command line, and
nm build/.../libmbedtls.a | grep -i srtp   # or strings on ssl_tls.o
# Compile a one-liner that includes mbedtls/ssl.h and uses mbedtls_ssl_conf_dtls_srtp_protection_profiles
```

If libdatachannel configure/compile fails with missing `mbedtls_ssl_srtp_profile` / `mbedtls_ssl_conf_dtls_srtp_protection_profiles`, the user config was not applied.

### 4.4 Size trimming (later, not v1 gate)

Start with **stock 3.6 + DTLS_SRTP only**. Aggressive cipher stripping is optional engineering after smoke tests (handshake + one DC message). See risk table in static-crypto research.

---

## 5. Wiring MbedTLS → libdatachannel (critical design choice)

Meson does not natively make CMake `find_package(MbedTLS)` see another `cmake.subproject`’s targets ([meson#12067](https://github.com/mesonbuild/meson/issues/12067)-class limitation). Pick **one** of the following; **A is recommended**.

### 5.A Recommended: predefine `MbedTLS::MbedTLS` before `find_package`

libdatachannel already short-circuits:

```cmake
if(NOT TARGET MbedTLS::MbedTLS)
  find_package(MbedTLS 3 REQUIRED)
endif()
```

**Inject** imported targets via one of:

1. **`CMAKE_PROJECT_INCLUDE` / `CMAKE_PROJECT_TOP_LEVEL_INCLUDES`** (CMake 3.15+ / 3.24+) pointing at a small file owned by this repo, e.g. `native/cmake/dcu-mbedtls-import.cmake`, **or**  
2. A **packagefiles patch** that `include()`s the same file at the top of the crypto section, **or**  
3. `libdc_opts.append_cmake_args(...)` equivalent through `add_cmake_defines` if the file path is passed as a define and a one-line patch includes it.

**Sketch `native/cmake/dcu-mbedtls-import.cmake`:**

```cmake
# Invoked before/with libdatachannel configure when DCU_MBEDTLS_* vars are set.
if(DEFINED DCU_MBEDTLS_INCLUDE_DIR AND NOT TARGET MbedTLS::MbedTLS)
  add_library(MbedTLS::MbedCrypto STATIC IMPORTED GLOBAL)
  set_target_properties(MbedTLS::MbedCrypto PROPERTIES
    IMPORTED_LOCATION "${DCU_MBEDCRYPTO_LIBRARY}"
    INTERFACE_INCLUDE_DIRECTORIES "${DCU_MBEDTLS_INCLUDE_DIR}")

  add_library(MbedTLS::MbedX509 STATIC IMPORTED GLOBAL)
  set_target_properties(MbedTLS::MbedX509 PROPERTIES
    IMPORTED_LOCATION "${DCU_MBEDX509_LIBRARY}"
    INTERFACE_INCLUDE_DIRECTORIES "${DCU_MBEDTLS_INCLUDE_DIR}"
    INTERFACE_LINK_LIBRARIES MbedTLS::MbedCrypto)

  add_library(MbedTLS::MbedTLS STATIC IMPORTED GLOBAL)
  set_target_properties(MbedTLS::MbedTLS PROPERTIES
    IMPORTED_LOCATION "${DCU_MBEDTLS_LIBRARY}"
    INTERFACE_INCLUDE_DIRECTORIES "${DCU_MBEDTLS_INCLUDE_DIR}"
    INTERFACE_LINK_LIBRARIES "MbedTLS::MbedX509;MbedTLS::MbedCrypto")
endif()
```

**How Meson supplies paths:** after `mbedtls_proj = cmake.subproject('mbedtls', ...)`, resolve absolute paths to the subproject build artifacts. Practical options for #25:

| Approach | Pros | Cons |
|----------|------|------|
| Hard-code known CMake output names under `meson.current_build_dir()/subprojects/mbedtls-*/library/libmbed*.a` | Simple | Path layout depends on Meson/CMake generator |
| `mbedtls_proj.target('mbedtls')` + custom generator extracting `FULL_PATH` | More precise | Meson version / cmake backend quirks |
| `mbedtls_proj.dependency('mbedtls')` for **final** `datachannel_unity` link only + still inject for libdc compile | Needed if PRIVATE link does not re-export | Must still feed include dir into libdc |

IMPORTED locations may not exist at **configure** time; Ninja/CMake generally tolerate that until **build**. Order is enforced because Meson builds mbedtls cmake targets before consumers when dependencies are declared — ensure libdatachannel’s cmake build depends on mbedtls targets via Meson (`depends:` on a dummy custom_target, or link the final library against both deps so the scheduler builds mbedtls first). **Spike this on macOS arm64 first** (#25 P0).

**Also set** on libdatachannel cmake.subproject:

```text
CMAKE_MODULE_PATH=<libdatachannel>/cmake/Modules   # still useful for juice etc.
CMAKE_FIND_LIBRARY_SUFFIXES=.a                     # belt-and-suspenders if find runs
CMAKE_FIND_ROOT_PATH_MODE_LIBRARY=ONLY             # cross builds
# never CMAKE_PREFIX_PATH=/opt/homebrew
```

### 5.B Alternative: staged install prefix + `find_package`

1. Build mbedtls via cmake.subproject.  
2. `cmake --install` into `build/mbedtls-prefix` (custom_target).  
3. Point libdatachannel `CMAKE_PREFIX_PATH` at that prefix so `FindMbedTLS` resolves **static** libs.

**Problem:** libdatachannel is itself a `cmake.subproject` configured at **`meson setup`**, before install custom_targets run. So either:

- two-phase setup (bootstrap script builds mbedtls prefix, then meson setup) — **violates pure single-setup ideal**, or  
- only use staged prefix with a **non-cmake.subproject** external CMake invoke for libdatachannel (escape hatch).

Use 5.B only if 5.A fails on a platform.

### 5.C Escape hatch (SPEC §9)

Keep `native/scripts/build-macos-arm64-cmake.sh` style **CMake-direct** path for one hard platform, still producing the same artifact layout. Must still use vendored MbedTLS 3.6 + DTLS-SRTP — **not** brew OpenSSL. Not the product default.

### 5.D Explicitly reject

| Anti-pattern | Why |
|--------------|-----|
| `find_package` → brew `mbedtls` / `mbedtls@3` dylibs | Fails audit; `@3` not portable to Win/Android/iOS CI |
| `find_package` → brew OpenSSL | Forbidden by #23 for product |
| Building shared mbedtls “for simplicity” | Violates self-contained plugin + dylib audit |
| Dual OpenSSL desktop / MbedTLS mobile matrix | Forbidden by SPEC §3 |

---

## 6. `cmake.subproject` options for libdatachannel

```text
NO_MEDIA=ON
NO_WEBSOCKET=ON
NO_EXAMPLES=ON
NO_TESTS=ON
USE_NICE=OFF
USE_GNUTLS=OFF
USE_MBEDTLS=ON
BUILD_SHARED_LIBS=OFF
CMAKE_POSITION_INDEPENDENT_CODE=ON
CMAKE_C_VISIBILITY_PRESET=hidden
CMAKE_CXX_VISIBILITY_PRESET=hidden
CMAKE_VISIBILITY_INLINES_HIDDEN=ON
# Injection (5.A):
DCU_MBEDTLS_INCLUDE_DIR=...
DCU_MBEDTLS_LIBRARY=.../libmbedtls.a
DCU_MBEDX509_LIBRARY=.../libmbedx509.a
DCU_MBEDCRYPTO_LIBRARY=.../libmbedcrypto.a
# plus CMAKE_PROJECT_INCLUDE / patch as chosen
```

**Target to consume from Meson:**

```meson
datachannel_dep = libdc.dependency('datachannel-static')
```

Upstream names (v0.24.5): shared `datachannel`, static **`datachannel-static`** (`STATIC EXCLUDE_FROM_ALL` — request explicitly).

**Compile definitions for consumers of static lib:** `RTC_STATIC=1` on the dcu wrapper (and any TU including `rtc.h`).

**Apple frameworks** (link on final plugin, not optional):

```text
-framework CoreFoundation -framework Security
```

**set_install(false)** on both cmake subprojects so CMake does not dump headers/libs into the Unity tree; only Meson installs the one plugin artifact.

---

## 7. Recommended `native/meson.build` shape

Illustrative (not yet landed — #25 implements and adjusts paths after the first green configure):

```meson
project(
  'datachannel-unity',
  ['c', 'cpp'],
  version: '0.1.0',
  meson_version: '>=1.2.0',
  default_options: [
    'cpp_std=c++17',
    'warning_level=1',
    'default_library=shared',
    'b_ndebug=if-release',
  ],
)

cmake = import('cmake')
fs = import('fs')
host_sys = host_machine.system()
host_cpu = host_machine.cpu_family()

# --- paths ---
mbedtls_user_cfg = meson.current_source_dir() / 'mbedtls' / 'user_config.h'
exports_dir = meson.current_source_dir() / 'exports'
# Default: UPM Plugins tree (override with -Dplugin_install_root=...)
plugin_root = get_option('plugin_install_root')
if plugin_root == ''
  plugin_root = meson.project_source_root() / '..' / 'Packages' / 'datachannel-unity' / 'Plugins'
endif

# ========== 1) MbedTLS 3.6.x static + DTLS-SRTP ==========
mbedtls_opts = cmake.subproject_options()
mbedtls_opts.add_cmake_defines({
  'ENABLE_PROGRAMS': false,
  'ENABLE_TESTING': false,
  'USE_STATIC_MBEDTLS_LIBRARY': true,
  'USE_SHARED_MBEDTLS_LIBRARY': false,
  'CMAKE_POSITION_INDEPENDENT_CODE': true,
  'CMAKE_C_VISIBILITY_PRESET': 'hidden',
  'MBEDTLS_USER_CONFIG_FILE': mbedtls_user_cfg,
})
mbedtls_opts.set_install(false)
mbedtls_proj = cmake.subproject('mbedtls', options: mbedtls_opts)

# Target names from MbedTLS CMake: mbedcrypto, mbedx509, mbedtls
mbedcrypto_dep = mbedtls_proj.dependency('mbedcrypto')
mbedx509_dep   = mbedtls_proj.dependency('mbedx509')
mbedtls_dep    = mbedtls_proj.dependency('mbedtls')

# Resolve .a paths for injection (implementation detail for #25 — may use target full_path)
# dcu_mbedtls_inc = ... subprojects/mbedtls source include/
# dcu_mbedtls_lib = ... build dir library/libmbedtls.a  etc.

# ========== 2) libdatachannel static, USE_MBEDTLS ==========
libdc_opts = cmake.subproject_options()
libdc_opts.add_cmake_defines({
  'NO_MEDIA': true,
  'NO_WEBSOCKET': true,
  'NO_EXAMPLES': true,
  'NO_TESTS': true,
  'USE_NICE': false,
  'USE_GNUTLS': false,
  'USE_MBEDTLS': true,
  'BUILD_SHARED_LIBS': false,
  'CMAKE_POSITION_INDEPENDENT_CODE': true,
  'CMAKE_C_VISIBILITY_PRESET': 'hidden',
  'CMAKE_CXX_VISIBILITY_PRESET': 'hidden',
  'CMAKE_VISIBILITY_INLINES_HIDDEN': true,
  # 'CMAKE_PROJECT_INCLUDE': meson.current_source_dir() / 'cmake' / 'dcu-mbedtls-import.cmake',
  # 'DCU_MBEDTLS_INCLUDE_DIR': dcu_mbedtls_inc,
  # 'DCU_MBEDTLS_LIBRARY': dcu_mbedtls_lib,
  # 'DCU_MBEDX509_LIBRARY': dcu_mbedx509_lib,
  # 'DCU_MBEDCRYPTO_LIBRARY': dcu_mbedcrypto_lib,
})
libdc_opts.set_install(false)
libdc = cmake.subproject('libdatachannel', options: libdc_opts)
datachannel_dep = libdc.dependency('datachannel-static')

# ========== 3) dcu wrapper → datachannel_unity ==========
dcu_inc = include_directories('dcu/include')
dcu_cpp_args = ['-DDCU_BUILD', '-DRTC_STATIC=1']
dcu_link_args = []
dcu_name_suffix = []
dcu_vs_module_defs = []

if host_sys == 'darwin'
  dcu_link_args += [
    '-Wl,-exported_symbols_list,' + (exports_dir / 'macos-exported-symbols.txt'),
    '-framework', 'CoreFoundation',
    '-framework', 'Security',
  ]
  # Unity macOS product: single .bundle per arch (#19)
  dcu_name_suffix = 'bundle'
elif host_sys == 'linux' or host_sys == 'android'
  dcu_link_args += [
    '-Wl,--version-script,' + (exports_dir / 'linux-version-script.map'),
    '-Wl,--exclude-libs,ALL',
  ]
elif host_sys == 'windows'
  # MSVC: DCU_API dllexport + optional .def as belt-and-suspenders
  dcu_vs_module_defs = exports_dir / 'windows-exports.def'
endif

# Install path mapping (thin arch)
# darwin + aarch64 → macOS/arm64 ; darwin + x86_64 → macOS/x64
# windows + x86_64 → Windows/x86_64 ; android + aarch64 → Android/arm64-v8a
install_subdir_rel = 'UNKNOWN'
if host_sys == 'darwin'
  if host_cpu == 'aarch64'
    install_subdir_rel = 'macOS' / 'arm64'
  else
    install_subdir_rel = 'macOS' / 'x64'
  endif
elif host_sys == 'windows'
  if host_cpu == 'aarch64'
    install_subdir_rel = 'Windows' / 'ARM64'
  else
    install_subdir_rel = 'Windows' / 'x86_64'
  endif
elif host_sys == 'android'
  install_subdir_rel = 'Android' / 'arm64-v8a'
elif host_sys == 'linux'
  install_subdir_rel = 'Linux' / host_cpu
endif

dcu_kwargs = {
  'sources': ['dcu/src/dcu_impl.cpp'],
  'include_directories': dcu_inc,
  'dependencies': [datachannel_dep, mbedtls_dep, mbedx509_dep, mbedcrypto_dep],
  'cpp_args': dcu_cpp_args,
  'link_args': dcu_link_args,
  'gnu_symbol_visibility': 'inlineshidden',
  'install': true,
  'install_dir': plugin_root / install_subdir_rel,
  'name_prefix': '',
}

# name: datachannel_unity[.bundle|.dll|libdatachannel_unity.so]
if host_sys == 'darwin'
  dcu_lib = library('datachannel_unity', name_suffix: 'bundle', kwargs: dcu_kwargs)
elif host_sys == 'android' or host_sys == 'linux'
  dcu_lib = library('datachannel_unity', name_prefix: 'lib', kwargs: dcu_kwargs)
else
  dcu_lib = library(
    'datachannel_unity',
    vs_module_defs: dcu_vs_module_defs,
    kwargs: dcu_kwargs,
  )
endif
```

**Notes for implementers:**

- Exact `library()` kwargs differ slightly by Meson version (`name_suffix` vs `darwin_versions`); spike and pin.  
- Re-list mbedtls deps on the final link if `datachannel-static` does not re-export PRIVATE static crypto (often required with archive linking). Prefer **`--whole-archive` / `-force_load`** only if undefined TLS symbols appear.  
- iOS / WebGL: separate `static_library` branches (`DCU_STATIC`, no export list); WebGL uses `datachannel-wasm` subproject (out of this ticket’s primary graph but same Meson root later).

### 7.1 Suggested `meson.options`

```meson
option(
  'plugin_install_root',
  type: 'string',
  value: '',
  description: 'Absolute or relative root for Unity Plugins install (default: ../Packages/datachannel-unity/Plugins)',
)
```

### 7.2 Developer / CI commands (product path)

```bash
cd native
./scripts/fetch-deps.sh   # or: meson subprojects download
meson setup build/macos-arm64 --buildtype=release
meson compile -C build/macos-arm64
meson install -C build/macos-arm64 --no-rebuild
./scripts/audit-macos-plugin.sh \
  ../Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle
```

Thin wrapper `build-macos-arm64.sh` should **only** encode the above + env for Ninja parallelism — **no** OpenSSL `clang++` link line.

---

## 8. Export flags (align #18)

Files already in-tree:

| Platform | File | Mechanism |
|----------|------|-----------|
| macOS | `native/exports/macos-exported-symbols.txt` | `-Wl,-exported_symbols_list` contents: `_dcu_*` |
| Linux/Android | `native/exports/linux-version-script.map` | `-Wl,--version-script` + `--exclude-libs,ALL` |
| Windows | `native/exports/windows-exports.def` | `vs_module_defs` / `/DEF` + `DCU_API` |

**Compile (all GCC/Clang targets):**

```text
gnu_symbol_visibility: 'inlineshidden'   # -fvisibility=hidden + inlines
-DDCU_BUILD
```

**Deps:** push CMake visibility presets when building mbedtls + libdatachannel (table in §4/§6). Linker allowlist remains the **final gate** after static archives are merged.

**CI assert (macOS example):**

```bash
# No non-dcu global defined symbols in the dynamic export set
nm -gU datachannel_unity.bundle | awk '$3 !~ /^_dcu_/ { bad=1; print } END { exit bad }'
# No brew crypto dylibs
otool -L datachannel_unity.bundle | grep -E 'openssl|libssl|libcrypto|libmbed' && exit 1
```

---

## 9. Install mapping → Plugins

| Host | Artifact | Install directory under Plugins |
|------|----------|----------------------------------|
| macOS arm64 | `datachannel_unity.bundle` | `macOS/arm64/` |
| macOS x64 | `datachannel_unity.bundle` | `macOS/x64/` |
| Windows x64 | `datachannel_unity.dll` | `Windows/x86_64/` |
| Windows arm64 | `datachannel_unity.dll` | `Windows/ARM64/` |
| Android arm64 | `libdatachannel_unity.so` | `Android/arm64-v8a/` (SPEC tree; no extra `libs/` segment if using flat layout in SPEC §8) |
| iOS arm64 | `libdatachannel_unity.a` | `iOS/` |
| WebGL | `.a` + `webrtc.jslib` | `WebGL/` (separate backend) |

**macOS:** one **thin** `.bundle` per arch — **no** side-by-side `.dylib`, no universal lipo in v1 (#19).

`meson install` should be enough for local + CI; do not require a second Python packaging step for the happy path. Optional `DESTDIR` for staged CI artifacts.

---

## 10. Cross-file notes

### 10.1 Rules (Meson CMake module)

- Put toolchain facts in **machine files** under `native/cross/`.  
- **Do not** pass `-DCMAKE_TOOLCHAIN_FILE=...` from `meson.build` for cmake.subprojects — Meson injects its generated toolchain; overriding breaks glue.  
- Optional vendor toolchain: machine file `[properties] cmake_toolchain_file = ...` **after** Meson’s (see Meson CMake module docs).  
- Use `[cmake]` section for `CMAKE_SYSTEM_NAME`, Android ABI, iOS SDK vars.  
- For find-root discipline on cross builds:

```ini
[properties]
cmake_use_exe_wrapper = true
# ensure host brew is not searched
```

### 10.2 Per-platform sketches

#### macOS arm64 (native file optional)

```ini
# native/cross/macos-arm64.ini  (use as --native-file when forcing thin arch on universal hosts)
[binaries]
c = 'clang'
cpp = 'clang++'
ar = 'ar'
cmake = 'cmake'
strip = 'strip'

[host_machine]
system = 'darwin'
cpu_family = 'aarch64'
cpu = 'arm64'
endian = 'little'

[built-in options]
c_args = ['-arch', 'arm64']
cpp_args = ['-arch', 'arm64']
c_link_args = ['-arch', 'arm64']
cpp_link_args = ['-arch', 'arm64']

[cmake]
CMAKE_OSX_ARCHITECTURES = 'arm64'
```

#### Android arm64

```ini
# native/cross/android-arm64.ini
[binaries]
c = '...'       # NDK clang
cpp = '...'
ar = '...'
cmake = 'cmake'

[host_machine]
system = 'android'
cpu_family = 'aarch64'
cpu = 'aarch64'
endian = 'little'

[properties]
# cmake_toolchain_file = '/path/to/ndk/build/cmake/android.toolchain.cmake'

[cmake]
CMAKE_SYSTEM_NAME = 'Android'
CMAKE_ANDROID_ARCH_ABI = 'arm64-v8a'
# CMAKE_ANDROID_NDK = '...'
# ANDROID_PLATFORM = 'android-23'
```

Same mbedtls + libdatachannel cmake.subproject defines; PIC + static crypto mandatory for a single `.so`.

#### iOS arm64

```ini
[host_machine]
system = 'darwin'   # or ios depending on Meson version conventions used by the project
cpu_family = 'aarch64'
cpu = 'arm64'
endian = 'little'

[built-in options]
c_args = ['-isysroot', '.../iPhoneOS.sdk', '-arch', 'arm64', '-miphoneos-version-min=12.0']
# ...

[cmake]
CMAKE_SYSTEM_NAME = 'iOS'
CMAKE_OSX_ARCHITECTURES = 'arm64'
# CMAKE_OSX_SYSROOT = iphoneos
```

Product: **static** `libdatachannel_unity.a` (`DCU_STATIC`), not `.bundle`. Export lists do not apply the same way; still compile with hidden visibility and consider `libtool -static` / localize non-`dcu_*` before ship (symbol-visibility research).

#### Windows x64

- Activate MSVC env before `meson setup`.  
- CRT **`/MD`** to match Unity.  
- Same static mbedtls + `datachannel-static` + `.def` exports.  
- Audit: `dumpbin /dependents` — no `libssl`/`libcrypto` DLLs.

#### WebGL

- **Not** this graph: `datachannel-wasm` + Unity Emscripten **3.1.8-unity**.  
- Same Meson root can later `cmake.subproject('datachannel-wasm')` under an emscripten cross-file; no MbedTLS.

### 10.3 Cross-cutting cmake.subproject hygiene

| Item | Setting |
|------|---------|
| PIC | `CMAKE_POSITION_INDEPENDENT_CODE=ON` on mbedtls + libdc |
| Shared crypto | **OFF** always |
| Visibility | hidden presets on C/C++ for both |
| Install pollution | `set_install(false)` |
| Host path leaks | Never set `CMAKE_PREFIX_PATH` to Homebrew roots in product files |
| Parallelism | `meson compile -j` / `ninja -j` |

---

## 11. Relationship to existing scripts and `versions.lock`

| Current | Target after #25 |
|---------|------------------|
| `versions.lock` `crypto=openssl-static-host` | `crypto=mbedtls` + `mbedtls=v3.6.7` (already has mbedtls pin) |
| `fetch-deps.sh` → `third_party/mbedtls` | Prefer `subprojects/mbedtls` (wrap); keep script as bootstrap |
| `build-macos-arm64.sh` OpenSSL static link | Thin meson driver; OpenSSL path removed from product |
| `FORCE_MBEDTLS` experimental | Becomes the **only** crypto path |
| `meson.build` skeleton | Full graph per §7 |

---

## 12. Implementation checklist for #25 (non-normative order)

1. Add `native/mbedtls/user_config.h` (DTLS-SRTP).  
2. Add `mbedtls.wrap` + ensure tree under `subprojects/mbedtls` @ v3.6.7.  
3. Add `libdatachannel.wrap` (tree already present @ v0.24.5) + optional packagefiles for 5.A.  
4. Add `native/cmake/dcu-mbedtls-import.cmake` (or patch).  
5. Rewrite `native/meson.build` per §7; add `meson.options`.  
6. `meson setup build/macos-arm64 && meson compile` — fix target names / `.a` paths until green.  
7. Wire export flags + `.bundle` name; `meson install` into Plugins.  
8. Extend `audit-macos-plugin.sh` for export + dylib policy; run in script wrapper.  
9. Demote OpenSSL from `build-macos-arm64.sh` and `versions.lock`.  
10. Document exact commands in package README; CI job uses the same Meson entry.

**P0 success criteria (macOS arm64):**

- [ ] Configure/build with **zero** `OPENSSL_*` / brew crypto prefixes in product options  
- [ ] Final plugin is `datachannel_unity.bundle`  
- [ ] `otool -L` has no openssl/mbedtls dylibs from Cellar  
- [ ] Exported symbols ⊆ `_dcu_*`  
- [ ] Dual-peer smoke (or existing EditMode + native smoke) still passes  

---

## 13. Risk register

| Risk | Severity | Mitigation |
|------|----------|------------|
| cmake.subproject cannot see mbedtls for `find_package` | **High** | Injection 5.A; spike early on host mac |
| `MBEDTLS_USER_CONFIG_FILE` not applied (path quoting/spaces) | High | Abs path; compile-time assert for SRTP symbols |
| libdatachannel `datachannel-static` not built (`EXCLUDE_FROM_ALL`) | Medium | Explicit `dependency('datachannel-static')` / target |
| Static archive GC drops TLS objects | Medium | Relink mbedtls on final plugin; `-force_load` / `--whole-archive` if needed |
| Nested submodules missing (juice/usrsctp) | Medium | `clone-recursive` + fetch-deps guard |
| Meson/CMake version skew in CI | Medium | Pin Meson ≥1.2, CMake ≥3.16 in workflow; record in lock notes |
| macOS `.bundle` naming quirks | Low | Spike `name_suffix` vs custom `install_data` |
| cmake.subproject breaks on MSVC/iOS | Medium | SPEC escape hatch §9; still MbedTLS static |

---

## 14. Answers mapped to ticket questions

1. **How to make MbedTLS 3.6.x + DTLS-SRTP a subproject?**  
   `mbedtls.wrap` @ v3.6.7, `cmake.subproject` static-only, `MBEDTLS_USER_CONFIG_FILE` enabling `MBEDTLS_SSL_DTLS_SRTP` (§3–§4).

2. **How to make libdatachannel v0.24.5 a static cmake.subproject with USE_MBEDTLS?**  
   Wrap + defines in §6; consume `datachannel-static`; inject `MbedTLS::MbedTLS` (§5.A) so no brew find.

3. **How to attach dcu + export allowlist?**  
   Meson `library('datachannel_unity')` with hidden visibility + platform export files already under `native/exports/` (§7–§8).

4. **Install + cross-file?**  
   Install into UPM `Plugins/<OS>/<arch>/` (§9); machine files under `native/cross/` with Meson-injected CMake toolchain (§10).

5. **Alignment with #17/#18/#19/#23?**  
   Full static MbedTLS 3.6; `dcu_*` only; single `.bundle`; Meson-only product path; OpenSSL host script demoted.

---

## Appendix A — Primary sources

| Source | Role |
|--------|------|
| Issue #23 resolution | Meson-only + subprojects lock |
| `docs/SPEC.md` §3, §8, §9 | Pins, plugins, build system |
| `docs/research/static-crypto-linking.md` | MbedTLS 3.6 vs brew 4; static recipes |
| `docs/research/symbol-visibility.md` | Export allowlists / visibility |
| `docs/research/meson-cmake-unity-plugins.md` | cmake.subproject / cross-file patterns |
| libdatachannel v0.24.5 `CMakeLists.txt` | `USE_MBEDTLS`, `datachannel-static` |
| libdatachannel `src/impl/dtlstransport.cpp` | Hard requirement for DTLS-SRTP APIs |
| Mbed TLS 3.6.7 `mbedtls_config.h` | `MBEDTLS_SSL_DTLS_SRTP` default off |
| Meson CMake module | `cmake.subproject`, machine-file cmake injection |
| In-tree `native/meson.build`, `exports/*`, `versions.lock` | Current skeleton and pins |

## Appendix B — Explicit non-goals for this research

- Landing the meson rewrite (that is #25).  
- WebGL/datachannel-wasm graph detail (separate backend).  
- Rewriting libdatachannel’s build in pure Meson.  
- Dual shipping OpenSSL/MbedTLS.  
- Enabling `NO_MEDIA=OFF` / full libsrtp media pipeline.
