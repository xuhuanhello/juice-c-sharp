# Research: Symbol visibility for `datachannel_unity` (export only `dcu_*`)

**Ticket:** [#18](https://github.com/xuhuanhello/juice-c-sharp/issues/18)  
**Parent map:** [#16](https://github.com/xuhuanhello/juice-c-sharp/issues/16) (native packaging hardening)  
**Date:** 2026-08-02  
**Scope:** Research only — how to **default-hide** symbols in the Unity native plugin after **static-linking** libdatachannel + crypto/deps, and **export only** the project ABI (`dcu_*`, plus any platform-required entry points). Platforms: **macOS, Linux, Windows, iOS, Android** (WebGL noted for contrast).

**Primary sources consulted**

| Source | What it covers |
|--------|----------------|
| [Apple — Dynamic Library Design Guidelines](https://developer.apple.com/library/archive/documentation/DeveloperTools/Conceptual/DynamicLibraries/100-Articles/DynamicLibraryDesignGuidelines.html) | Export strategy: `-fvisibility`, visibility attribute, **exported / unexported symbols lists**, performance of small export sets |
| Apple `ld` man page (Xcode toolchain; local `man ld`) | `-exported_symbols_list`, `-exported_symbol`, `-unexported_symbols_list`, `-load_hidden`, `-hidden-l`, wildcards |
| Clang driver / docs: `-fvisibility=`, `-fvisibility-inlines-hidden`; [LTO Visibility](https://clang.llvm.org/docs/LTOVisibility.html); [AttributeReference](https://clang.llvm.org/docs/AttributeReference.html) | Default visibility; `visibility("default"|"hidden")`; Windows dllimport/dllexport interaction |
| [GNU ld — VERSION / `--version-script`](https://sourceware.org/binutils/docs/ld/VERSION.html) | ELF version scripts: `global:` / `local: *;` allowlists |
| GNU ld `--exclude-libs` (binutils ld Options) | Hide symbols pulled from static archives on ELF (and i386 PE) |
| [MSVC — Module-Definition (.def) Files](https://learn.microsoft.com/en-us/cpp/build/reference/module-definition-dot-def-files?view=msvc-170) | `/DEF`, `EXPORTS`; alternate to `__declspec(dllexport)` |
| [MSVC — Exporting from a DLL using `__declspec(dllexport)`](https://learn.microsoft.com/en-us/cpp/build/exporting-from-a-dll-using-declspec-dllexport?view=msvc-170) | Preferred explicit export markup on Windows |
| [CMake `WINDOWS_EXPORT_ALL_SYMBOLS`](https://cmake.org/cmake/help/latest/prop_tgt/WINDOWS_EXPORT_ALL_SYMBOLS.html) | Auto-exports **all** globals into a generated `.def` — **must stay OFF** |
| [Meson `gnu_symbol_visibility`](https://mesonbuild.com/Release-notes-for-0-48-0.html) / [function ref](https://mesonbuild.com/Reference-manual_functions.html) | `hidden` / `inlineshidden` on `library()` / `shared_library()` |
| In-tree: `native/dcu/include/dcu.h` (`DCU_API`), `native/meson.build`, macOS arm64 build artifact |

**Repo evidence (2026-08-02, macOS arm64 dylib)**

| Metric | Value |
|--------|-------|
| Global defined symbols (`nm -gU`) | **~1551** |
| Of which `dcu_*` | **18** |
| Major leakers | mangled `rtc::*` C++, `usrsctp_*` / `sctp_*`, `SCTP_M_*` |
| Dynamic deps (separate from this ticket) | still pulls Homebrew OpenSSL (`otool -L` → `libssl.3` / `libcrypto.3`) |

So today the ABI is correct at the **C# / header** level (`dcu_*` only), but the **shared object re-exports nearly the entire static graph**. That violates SPEC (“Do not re-export upstream `rtc*`”), risks symbol collisions with other Unity plugins, and slows dynamic loading.

---

## Executive answer

| Question | Answer |
|----------|--------|
| How to export only `dcu_*`? | **Two layers:** (1) compile with **default hidden** + explicit export on `DCU_API`; (2) **linker allowlist** that is the final gate after static-linking deps. |
| Is `-fvisibility=hidden` enough alone? | **No**, once you pull in prebuilt `.a` objects compiled with default visibility (libdatachannel, usrsctp, juice, crypto). Linker filtering is required. |
| macOS | `-fvisibility=hidden` + `-Wl,-exported_symbols_list,<file>` (`_dcu_*`); optional `-load_hidden` / `-hidden-l` on archives. |
| Linux / Android | `-fvisibility=hidden` + `-Wl,--version-script=<map>` (`dcu_*; local: *;`) + `-Wl,--exclude-libs,ALL`. |
| Windows (MSVC) | **Only** `__declspec(dllexport)` / `.def` exports leave the DLL; do **not** enable `WINDOWS_EXPORT_ALL_SYMBOLS`. Keep `DCU_API` + optional explicit `.def`. |
| iOS (static `.a` → `__Internal`) | Dynamic export lists do not apply the same way; use **hidden visibility when compiling all objects**, then **partial-link / localize** non-`dcu_*` before shipping the archive to reduce clashes with other plugins. |
| Crypto / OpenSSL leakage | Visibility hides **symbols**; it does **not** remove a **dynamic** OpenSSL dependency. Static crypto is map #16; treat as a separate link step from export filtering. |

**Recommended project stance:** keep the existing `DCU_API` macro; add **per-OS linker allowlist files** under something like `native/exports/`; wire them in Meson/CMake; enforce with **CI `nm` / `dumpbin` checks**.

---

## 1. What “export” means per Unity plugin kind

| Platform product | Loader model | What “export only `dcu_*`” means |
|------------------|--------------|-----------------------------------|
| macOS `.bundle` / `.dylib` | Dynamic plugin | Only `dcu_*` in the **dynamic symbol table** (Mach-O external globals) |
| Linux `.so` | Dynamic plugin | Only `dcu_*` in `.dynsym` (default-vis / version-script global) |
| Windows `.dll` | Dynamic plugin | Only `dcu_*` in PE export table |
| Android `.so` | Dynamic plugin (JNI/il2cpp load) | Same as Linux ELF |
| iOS static `.a` | Linked into player; `DllImport("__Internal")` | Symbols are **not** a separate DSO; goal is (a) **`dcu_*` remain linkable** for the player, (b) **minimize global name pollution** so usrsctp/mbedTLS do not collide with other plugins |
| WebGL `.a` + `.jslib` | Static into wasm; `DllImport("__Internal")` | Emscripten `EXPORTED_FUNCTIONS` / `EMSCRIPTEN_KEEPALIVE` allowlist — same *idea*, different toolchain (out of focus here; see WebGL research) |

Unity P/Invoke resolves by **exported C name** (no C++ mangling). The wrapper already uses `extern "C"` and the `dcu_*` prefix (SPEC §4).

**Platform “must keep” beyond `dcu_*`:** for a pure P/Invoke plugin this project does **not** need `UnityPluginLoad` / `UnityPluginUnload` unless it adopts the low-level Unity native plugin interface. Do **not** re-export `rtc*`, OpenSSL, mbedTLS, usrsctp, or juice APIs.

---

## 2. Shared design (all dynamic platforms)

### 2.1 Source-level export macro (already present)

`native/dcu/include/dcu.h` already implements the standard dual pattern:

```c
#if defined(_WIN32) && !defined(DCU_STATIC)
#  if defined(DCU_BUILD)
#    define DCU_API __declspec(dllexport)
#  else
#    define DCU_API __declspec(dllimport)
#  endif
#else
#  define DCU_API __attribute__((visibility("default")))
#endif
```

All public entry points are marked `DCU_API`. Build the shared plugin with `-DDCU_BUILD`. Static iOS / WebGL may define `DCU_STATIC` so Windows-style dllimport is not used on headers included by consumers (if any).

This matches Clang’s model: with `-fvisibility=hidden`, only `visibility("default")` symbols remain candidates for dynamic export ([Clang LTO Visibility](https://clang.llvm.org/docs/LTOVisibility.html) documents the same attribute / `-fvisibility=` pairing).

### 2.2 Compile-time default hidden (GCC/Clang)

| Flag | Role |
|------|------|
| `-fvisibility=hidden` | Global definitions default to **hidden** (not dynamically exported) |
| `-fvisibility-inlines-hidden` | Inline C++ member functions default hidden (smaller dynsym, fewer surprises) |

Apply to:

1. **Wrapper** TUs (`dcu_impl.cpp`) — required.  
2. **All static deps** (libdatachannel, libjuice, usrsctp, libsrtp, mbedTLS/OpenSSL static) — **strongly preferred**, via CMake `CMAKE_C_VISIBILITY_PRESET=hidden`, `CMAKE_CXX_VISIBILITY_PRESET=hidden`, `CMAKE_VISIBILITY_INLINES_HIDDEN=ON` when building those archives for the plugin.

Meson: `gnu_symbol_visibility: 'hidden'` or `'inlineshidden'` on `library('datachannel_unity', ...)`.

**Limitation:** Visibility is an **object-file** property. An archive built earlier **without** hidden visibility still contributes **default-visible** globals when you link the final `.dylib`/`.so`. That is exactly why the current macOS dylib exports ~1500 non-`dcu_*` symbols after static-linking libdatachannel objects.

### 2.3 Link-time allowlist (final gate)

Regardless of how deps were built, force the **export set** at final link:

| OS | Mechanism | Allowlist shape |
|----|-----------|-----------------|
| macOS | `ld -exported_symbols_list file` | One symbol per line; C names need leading `_`; wildcards `*` `?` `[...]` supported |
| Linux / Android | `ld --version-script=file` | `global: dcu_*; local: *;` |
| Windows MSVC | `.def` `EXPORTS` and/or `__declspec(dllexport)` only | Explicit names (no ELF-style `local: *`) |
| Windows MinGW | `.def` and/or `--version-script` / `--exclude-libs` | Prefer `.def` + no auto-export-all |

Apple’s guidelines explicitly recommend combining **`-fvisibility` + visibility attribute** and optionally **exported symbols lists**, and note that fewer exports improve load performance.

### 2.4 Static archives of third-party code

| Toolchain | Recommended extra |
|-----------|-------------------|
| Apple `ld` | `-load_hidden path/to/lib.a` or `-hidden-lfoo` — treat archive globals as if `visibility=hidden` while still linking them. Ideal when wrapping static libdatachannel into a dylib. |
| GNU ld / gold / lld (ELF) | `-Wl,--exclude-libs,ALL` (or named archives) — symbols from archives become **hidden** on ELF; still available for resolution inside the DSO. |
| MSVC | No Unix-style auto-export of static lib globals into a DLL; **do not** turn on export-all. |

**Defense in depth order (dynamic plugins):**

1. Build deps + wrapper with hidden visibility.  
2. Prefer archive-hide flags (`--exclude-libs` / `-load_hidden`).  
3. Apply **allowlist** (exported_symbols_list / version-script / `.def`).  
4. CI assert export table.

---

## 3. Platform recipes

### 3.1 macOS (`.bundle` preferred for Unity; `.dylib` same flags)

**Compiler**

```text
-fvisibility=hidden -fvisibility-inlines-hidden -DDCU_BUILD
```

**Export file** e.g. `native/exports/dcu.macos.exports` (Mach-O C symbols are underscored):

```text
# datachannel_unity public ABI — Apple ld -exported_symbols_list
# Leading underscore is required for C symbols on Darwin.
_dcu_*
```

Wildcards are documented in Apple `ld`: `*` matches any run of characters. An explicit list of the current 18 symbols is also fine and slightly stricter for review.

**Link flags (clang driver)**

```text
-Wl,-exported_symbols_list,native/exports/dcu.macos.exports
```

Optional but useful when linking static libdatachannel / usrsctp / juice / crypto:

```text
-Wl,-load_hidden,path/to/libdatachannel.a
-Wl,-load_hidden,path/to/libjuice.a
# ...or -hidden-ldatachannel if -L/-l style
```

`-exported_symbols_list` alone already demotes non-listed globals to `__private_extern__` (hidden) in the **output** file — sufficient as the hard gate even if input `.o` files were default-visible.

**Verify**

```bash
nm -gU Plugins/macOS/arm64/datachannel_unity.bundle | grep -v ' _dcu_'
# expect: empty (aside from possibly only dcu lines)
nm -gU ... | grep ' _dcu_'   # expect full ABI set
```

**Do not** use `-Wl,-undefined,dynamic_lookup` for release plugins (hides missing symbols; current fallback script uses it as last resort).

### 3.2 Linux (`.so`)

**Compiler:** same `-fvisibility=hidden -fvisibility-inlines-hidden`.

**Version script** e.g. `native/exports/dcu.version`:

```text
# ELF version script — GNU ld / gold / lld
# https://sourceware.org/binutils/docs/ld/VERSION.html
{
  global:
    dcu_*;
  local:
    *;
};
```

No leading underscore on ELF C symbols.

**Link flags**

```text
-Wl,--version-script=native/exports/dcu.version
-Wl,--exclude-libs,ALL
-Wl,-Bsymbolic   # optional: prefer local binding inside the DSO
```

`--exclude-libs ALL` is documented for ELF: archive symbols are treated as **hidden** so they do not enter the dynamic export set even if the version script were misconfigured for some patterns. Explicit `dcu_*` remain global because they come from the wrapper objects (or are listed under `global:`).

**Verify**

```bash
nm -D --defined-only libdatachannel_unity.so | awk '$2 ~ /[TtWw]/'
# or: readelf -Ws | grep -v UND
# only dcu_* (and maybe weak C++ RT helpers if something went wrong)
```

### 3.3 Android (NDK, arm64-v8a `.so`)

Same as Linux: NDK Clang is a Clang/LLVM toolchain with GNU-compatible linker scripts on LLD.

```text
-fvisibility=hidden -fvisibility-inlines-hidden
-Wl,--version-script=native/exports/dcu.version
-Wl,--exclude-libs,ALL
```

Ship one ABI (arm64) per SPEC; apply the same export assert in CI on the packaged `libdatachannel_unity.so`.

**JNI note:** this package uses P/Invoke / IL2CPP direct native calls, not `JNI_OnLoad`. Do not export JNI entry points unless you deliberately add them.

### 3.4 Windows (MSVC `.dll`)

MSVC does **not** use `-fvisibility`. Export surface is:

1. **`__declspec(dllexport)`** on definitions (already `DCU_API` when `DCU_BUILD`), and/or  
2. A **module-definition file** passed with `/DEF:dcu.def` ([Microsoft Learn — .def files](https://learn.microsoft.com/en-us/cpp/build/reference/module-definition-dot-def-files?view=msvc-170)).

**Example** `native/exports/dcu.def`:

```text
LIBRARY datachannel_unity
EXPORTS
    dcu_abi_version
    dcu_init
    dcu_shutdown
    dcu_set_log_level
    dcu_pc_create
    dcu_pc_close
    dcu_pc_destroy
    dcu_pc_set_remote_description
    dcu_pc_add_remote_candidate
    dcu_pc_create_data_channel
    dcu_dc_send
    dcu_dc_close
    dcu_dc_destroy
    dcu_dc_buffered_amount
    dcu_event_peek
    dcu_event_copy_payload
    dcu_event_copy_payload2
    dcu_event_pop
```

**Critical: keep auto-export off**

- CMake `WINDOWS_EXPORT_ALL_SYMBOLS` / `CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS` must remain **OFF**. That property generates a `.def` of **all** globals from `.obj` files and would re-create the Unix-style leak on Windows.
- Do not pass linker `/EXPORT:*` for non-`dcu` symbols.

**Static-linked deps on MSVC:** objects from static `.lib` files are **not** automatically placed in the DLL export table. Combined with explicit `dllexport` only on `dcu_*`, MSVC is often “safe by default” compared to ELF/Mach-O. Still add an explicit `.def` allowlist as CI-stable documentation of the ABI and as protection against accidental `dllexport` elsewhere (e.g. if an upstream header is compiled with export macros).

**Verify**

```text
dumpbin /EXPORTS datachannel_unity.dll
```

Only `dcu_*` (plus CRT decoration noise if any).

**MinGW note:** PE ports of GNU ld may auto-export; use a `.def` and/or `--exclude-libs` and never rely on default auto-export.

### 3.5 iOS (static `.a`, `DllImport("__Internal")`)

Unity iOS plugins are commonly **static archives** linked into the Xcode player. There is **no separate DSO export table** at load time:

- P/Invoke looks up **`dcu_*` as global symbols in the main binary**.  
- Every **global** symbol from usrsctp / mbedTLS / juice also lands in the app’s global namespace and can **collide** with another plugin that ships the same libraries.

**What works**

| Technique | Effect |
|-----------|--------|
| Compile all TUs with `-fvisibility=hidden`; only `DCU_API` → `default` | Correct for any future dynamic framework; limited help for pure static multi-`.a` collisions of **same-named** strong symbols |
| **Partial link** with export list | `ld -r -exported_symbols_list dcu.macos.exports -o dcu_all.o …` then `libtool -static -o libdatachannel_unity.a dcu_all.o` — non-listed globals become private_extern / non-global in the relocatable object, greatly reducing clash surface |
| `-unexported_symbols_list` on partial link | Inverse: hide known bad prefixes if allowlist is hard |
| Prefer one **merged** archive of wrapper+deps | Avoid shipping many `.a` that each re-export the same third-party globals |

**What does not apply the same way**

- `--version-script` / PE `.def` are for **linked images** (dylib/so/dll), not for “hide inside a static `.a`” unless you partial-link first.  
- Apple `-exported_symbols_list` on the **final app** is controlled by Unity/Xcode, not by the plugin author.

**Practical recommendation for this repo**

1. Build iOS objects with **hidden visibility** + static mbedTLS (no system OpenSSL).  
2. Produce a **single** `libdatachannel_unity.a` via **relocatable partial link** + `_dcu_*` allowlist so third-party symbols are not global.  
3. Document that adopters must not also link another copy of usrsctp/libdatachannel with overlapping globals if partial-link is skipped.  
4. Optional future: ship a **dynamic framework** with normal `exported_symbols_list` (Unity supports frameworks; SPEC currently locks static `.a` for v1).

**Simulator / bitcode:** out of scope for export policy; keep arm64 device as v1.

### 3.6 WebGL (contrast only)

Emscripten keeps functions if listed in `-sEXPORTED_FUNCTIONS` / annotated with `EMSCRIPTEN_KEEPALIVE`. Map the same allowlist of `dcu_*` names (with leading `_` in the JSON list as Emscripten expects). Do not export raw JS mini-ABI `rtc*` through the managed surface (see `docs/research/unity-webgl-datachannel-wasm.md`).

---

## 4. Suggested allowlist files (project layout)

```text
native/exports/
  dcu.macos.exports   # _dcu_*
  dcu.version         # ELF version script
  dcu.def             # MSVC EXPORTS
```

**Current `dcu_*` surface** (from macOS `nm` + `dcu.h`; keep in sync when ABI grows):

| Symbol |
|--------|
| `dcu_abi_version` |
| `dcu_init` / `dcu_shutdown` / `dcu_set_log_level` |
| `dcu_pc_create` / `dcu_pc_close` / `dcu_pc_destroy` |
| `dcu_pc_set_remote_description` / `dcu_pc_add_remote_candidate` / `dcu_pc_create_data_channel` |
| `dcu_dc_send` / `dcu_dc_close` / `dcu_dc_destroy` / `dcu_dc_buffered_amount` |
| `dcu_event_peek` / `dcu_event_copy_payload` / `dcu_event_copy_payload2` / `dcu_event_pop` |

Prefer generating `.def` / checklists from `dcu.h` in CI later; wildcards (`_dcu_*` / `dcu_*`) reduce drift for Unix.

---

## 5. Meson / CMake wiring sketch

### 5.1 Meson (top-level plugin)

```meson
dcu_link_args = []
dcu_link_depends = []
dcu_vs_module_defs = []

if host_machine.system() == 'darwin'
  exp = files('exports/dcu.macos.exports')
  dcu_link_args += ['-Wl,-exported_symbols_list,' + exp.full_path()]
  dcu_link_depends += exp
elif host_machine.system() == 'windows'
  # MSVC: vs_module_defs; MinGW: --kill-at / .def as needed
  dcu_vs_module_defs = files('exports/dcu.def')
else
  # linux, android, …
  map = files('exports/dcu.version')
  dcu_link_args += [
    '-Wl,--version-script,' + map.full_path(),
    '-Wl,--exclude-libs,ALL',
  ]
  dcu_link_depends += map
endif

dcu_lib = library(
  'datachannel_unity',
  sources: ['dcu/src/dcu_impl.cpp'],
  include_directories: dcu_inc,
  dependencies: [datachannel_dep],
  cpp_args: ['-DDCU_BUILD'],
  gnu_symbol_visibility: 'inlineshidden',  # -fvisibility=hidden + inlines
  link_args: dcu_link_args,
  link_depends: dcu_link_depends,
  vs_module_defs: dcu_vs_module_defs,
  install: true,
)
```

Propagate visibility into the CMake subproject when possible:

```meson
libdc_opts.add_cmake_defines({
  'CMAKE_C_VISIBILITY_PRESET': 'hidden',
  'CMAKE_CXX_VISIBILITY_PRESET': 'hidden',
  'CMAKE_VISIBILITY_INLINES_HIDDEN': true,
  'BUILD_SHARED_LIBS': false,
  # …
})
```

Note: upstream may still force default visibility on its own `RTC_API` macros for **shared** builds; with `BUILD_SHARED_LIBS=false` and static archives, prefer relying on the **final link allowlist** + `--exclude-libs` / `-load_hidden`.

### 5.2 CMake (fallback script / dcu target)

```cmake
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS OFF)

if(APPLE)
  target_link_options(datachannel_unity PRIVATE
    "LINKER:-exported_symbols_list,${CMAKE_SOURCE_DIR}/exports/dcu.macos.exports")
  # Optional per archive:
  # target_link_options(... "LINKER:-load_hidden,${LIBDATACHANNEL_A}")
elseif(MSVC)
  target_sources(datachannel_unity PRIVATE exports/dcu.def)
  # or: set_property(TARGET datachannel_unity PROPERTY LINK_FLAGS "/DEF:exports/dcu.def")
elseif(UNIX)
  target_link_options(datachannel_unity PRIVATE
    "LINKER:--version-script=${CMAKE_SOURCE_DIR}/exports/dcu.version"
    "LINKER:--exclude-libs,ALL")
endif()
```

---

## 6. Static crypto vs symbol visibility (do not conflate)

| Concern | Symptom | Fix layer |
|---------|---------|-----------|
| Dynamic OpenSSL | `otool -L` / `ldd` shows `libssl`/`libcrypto` | Link **static** mbedTLS (or static OpenSSL) into the plugin; map #16 |
| Exported OpenSSL/`rtc` symbols | `nm -gU` / `nm -D` lists `SSL_*`, `rtc*`, `usrsctp_*` | Visibility + allowlist + exclude-libs / load_hidden |
| Two copies of usrsctp in one iOS app | Duplicate symbol linker errors | Partial-link localize or single merged plugin archive |

Hiding symbols **does not** satisfy “self-contained, no system OpenSSL”. Both are required for map #16.

---

## 7. CI / release verification checklist

Add a small script (e.g. `native/scripts/check-exports.sh`) used on every platform artifact:

| Platform | Command idea | Pass criterion |
|----------|--------------|----------------|
| macOS | `nm -gU "$PLUGIN" \| awk '{print $NF}'` | Every defined global matches `_dcu_*` (or allowlisted exceptions empty) |
| Linux/Android | `nm -D --defined-only` / `llvm-nm` | Every dynamic defined text/data symbol matches `dcu_*` |
| Windows | `dumpbin /EXPORTS` | Only `dcu_*` names |
| iOS `.a` | `nm -g` on archive members after partial-link | No global `usrsctp_*` / `mbedtls_*` / mangled `rtc` if localization applied; `dcu_*` present |

Fail the build if any of: `rtcCreate`, `usrsctp_`, `SSL_`, `mbedtls_`, OpenSSL soname in dynamic deps (separate assert).

---

## 8. Interaction with current tree

| Item | Status |
|------|--------|
| `DCU_API` visibility/dllexport | **Done** in `dcu.h` |
| Meson `gnu_symbol_visibility` | **Missing** on `datachannel_unity` |
| Linker allowlist files | **Missing** |
| macOS arm64 artifact | **Leaks ~1551 globals**; only 18 `dcu_*` |
| OpenSSL dylib dependency | **Present** (packaging/crypto ticket, not fixed by export lists) |
| Upstream libdatachannel `RTC_API` | Irrelevant to C# if not re-exported; still pollutes dynsym until allowlist |

No need to change the C# `DllImport("datachannel_unity")` / `__Internal` matrix for this work.

---

## 9. Concise answers to the ticket questions

1. **`-fvisibility=hidden`**  
   Yes for GCC/Clang on macOS/Linux/Android/iOS **compile** of wrapper + ideally all static deps. Pair with `visibility("default")` on `DCU_API`. Insufficient alone for prebuilt default-visible archives.

2. **`exported_symbols_list` / version script**  
   - macOS: `-exported_symbols_list` with `_dcu_*` (Apple `ld` man; Apple Dynamic Library guidelines).  
   - Linux/Android: `--version-script` with `global: dcu_*; local: *;` (GNU ld VERSION).  
   These are the **authoritative** export filters after static-linking.

3. **MSVC `.def` / `dllexport`**  
   Keep `DCU_API` → `__declspec(dllexport)` under `DCU_BUILD`; optional explicit `dcu.def` via `/DEF`. Never enable `WINDOWS_EXPORT_ALL_SYMBOLS`.

4. **Avoid OpenSSL / upstream symbol leakage after static link**  
   - Dynamic export: allowlist + `--exclude-libs,ALL` (ELF) / `-load_hidden` (Apple) / no export-all (MSVC).  
   - Runtime dependency: static-link crypto (mbedTLS pin) so the plugin does not `DT_NEEDED` system OpenSSL.  
   - iOS multi-plugin: partial-link / localize non-`dcu_*` in the shipped `.a`.

5. **Unity-required extras**  
   None beyond `dcu_*` for the current P/Invoke design. Do not export `rtc*` “for convenience.”

---

## 10. Implementation order (for map #16 follow-through)

1. Add `native/exports/{dcu.macos.exports,dcu.version,dcu.def}`.  
2. Wire flags into Meson + macOS CMake fallback script; rebuild mac arm64.  
3. Assert `nm -gU` shows only `_dcu_*`.  
4. Propagate hidden visibility into libdatachannel CMake defines; add `--exclude-libs` / `-load_hidden`.  
5. Fix static crypto (drop Homebrew OpenSSL dylib) as a **separate** packaging step.  
6. Extend CI matrix: Linux version-script, Windows dumpbin, iOS partial-link policy.  
7. Optionally generate export lists from `dcu.h` to prevent ABI drift.

---

## 11. References (primary)

- Apple Dynamic Library Design Guidelines (symbol exporting strategies, `-fvisibility`, export lists):  
  https://developer.apple.com/library/archive/documentation/DeveloperTools/Conceptual/DynamicLibraries/100-Articles/DynamicLibraryDesignGuidelines.html  
- Apple `ld` (Xcode): `-exported_symbols_list`, `-load_hidden`, `-hidden-l` — local `man ld`  
- Clang: `-fvisibility=`; https://clang.llvm.org/docs/LTOVisibility.html  
- GNU ld VERSION / `--version-script`: https://sourceware.org/binutils/docs/ld/VERSION.html  
- GNU ld `--exclude-libs`: binutils ld Options (`--exclude-libs ALL`)  
- MSVC module-definition files: https://learn.microsoft.com/en-us/cpp/build/reference/module-definition-dot-def-files?view=msvc-170  
- MSVC `__declspec(dllexport)`: https://learn.microsoft.com/en-us/cpp/build/exporting-from-a-dll-using-declspec-dllexport?view=msvc-170  
- CMake `WINDOWS_EXPORT_ALL_SYMBOLS`: https://cmake.org/cmake/help/latest/prop_tgt/WINDOWS_EXPORT_ALL_SYMBOLS.html  
- Meson `gnu_symbol_visibility`: https://mesonbuild.com/Reference-manual_functions.html  

---

## Document control

| Field | Value |
|-------|--------|
| Path | `docs/research/symbol-visibility.md` |
| Issue | https://github.com/xuhuanhello/juice-c-sharp/issues/18 |
| Parent | https://github.com/xuhuanhello/juice-c-sharp/issues/16 |
| Research date | 2026-08-02 |
| Kind | Research only — not an implementation PR |
