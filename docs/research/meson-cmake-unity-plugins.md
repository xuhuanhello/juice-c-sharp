# Research: Meson orchestration + CMake subproject → Unity Plugins

**Ticket:** [#4](https://github.com/xuhuanhello/juice-c-sharp/issues/4)  
**Parent map:** [#1](https://github.com/xuhuanhello/juice-c-sharp/issues/1)  
**Date:** 2026-08-02  
**Scope:** Research only — feasibility of **Meson top-level orchestration + `cmake.subproject` (or equivalent)** to produce **Unity-ready Plugins** for:

| Platform | Arch |
|----------|------|
| Android | arm64 |
| Windows | x64 |
| Windows | arm64 |
| iOS | arm64 |
| macOS | x64 |
| macOS | arm64 |
| WebGL | Emscripten (datachannel-wasm) |

**Primary sources consulted**

- Meson: [CMake module](https://mesonbuild.com/CMake-module.html), [Mixing build systems](https://mesonbuild.com/Mixing-build-systems.html), [Cross compilation](https://mesonbuild.com/Cross-compilation.html), [Machine files](https://mesonbuild.com/Machine-files.html), sample cross files (`wasm.txt`, `iphone.txt`)
- Upstream: [libdatachannel BUILDING.md / CMakeLists.txt](https://github.com/paullouisageneau/libdatachannel), [datachannel-wasm README / CMakeLists.txt](https://github.com/paullouisageneau/datachannel-wasm)
- Unity Manual (2022.3): Plugin Inspector, Native plug-ins, desktop plug-ins, iOS plug-ins, Android plug-ins, WebGL Emscripten native plug-ins
- Reference (do not treat as live dependency): [Walkerdine/dc-unity](https://github.com/Walkerdine/dc-unity) (CMake-only, Windows/Linux/WebGL)

---

## Executive answer

**Yes, with conditions — feasible as a multi-platform *orchestrator*, not as a single “one meson setup builds everything magically” path.**

| Question | Answer |
|----------|--------|
| Can Meson + `cmake.subproject` wrap **libdatachannel** for native shared/static plugins? | **Yes for simple cases** (desktop shared, Android NDK shared, static link of deps). Meson officially supports only *simple* mixed builds; complex CMake graphs are “best effort”. |
| Same pipeline for **datachannel-wasm** → Unity WebGL? | **Partially.** CMake under Emscripten works; Meson can drive it via Emscripten cross-file + CMake toolchain injection. Unity needs **static archive (`.a`) + `.jslib`**, not a shared lib. **Emscripten version must match Unity’s bundled toolchain** (2022.3 → Emscripten 3.1.8-unity). |
| Stable Unity Plugins layout for the full matrix? | **Yes as a packaging/install step**, largely independent of Meson vs pure CMake. Meson’s value is **one entrypoint + per-platform machine files + install/copy rules**, not eliminating platform toolchains. |
| Highest risk? | **WebGL (Emscripten lock + JS glue + C++-only wasm API)** and **iOS (static link / framework + Apple crypto + codesign)**; then **Windows arm64** (MSVC ARM64 + OpenSSL). |
| HarmonyOS future? | **Neutral to mild positive** — Meson cross-file discipline helps *when* OHOS/Harmony NDK toolchains exist; it does **not** remove the need for a CMake/OHOS toolchain and ABI work. |

**Recommended stance for the map (#1):** keep the full platform matrix; mark **phased delivery** (below), not platform cuts.

---

## 1. Patterns and pitfalls wrapping upstream CMake

### 1.1 Official Meson model

Meson documents CMake subprojects under `subprojects/`:

```meson
cmake = import('cmake')
opt = cmake.subproject_options()
opt.add_cmake_defines({
  'NO_MEDIA': true,
  'NO_WEBSOCKET': true,   # product choice; map can still enable later
  'NO_EXAMPLES': true,
  'NO_TESTS': true,
  'BUILD_SHARED_LIBS': true,
  'USE_NICE': false,      # use bundled libjuice
})
# Prefer not installing whole upstream tree into Unity Plugins
opt.set_install(false)

libdc = cmake.subproject('libdatachannel', options: opt)
# Target names come from CMake: datachannel / datachannel-static
dep = libdc.dependency('datachannel')
# or raw target:
# tgt = libdc.target('datachannel')
```

Key APIs (Meson CMake module):

| API | Role |
|-----|------|
| `cmake.subproject(name, options:)` | Configure + build CMake project as subproject |
| `subproject_options().add_cmake_defines({...})` | Pass `-DFOO=...` (replaces deprecated `cmake_options`) |
| `set_override_option` / `append_compile_args` / `append_link_args` | Override per target or globally |
| `set_install(bool[, target:])` | Override install; critical so Unity Plugins don’t get full CMake install tree |
| `dependency(target)` / `target(target)` / `target_list()` | Consume CMake targets |
| Machine file `[properties] cmake_toolchain_file` + `[cmake]` section | Cross-compile CMake subprojects (Meson ≥ 0.56) |

Meson **auto-generates** a CMake toolchain file from the cross/native file and injects glue; you **must not** only pass `-DCMAKE_TOOLCHAIN_FILE=...` from `meson.build` (unsupported — would skip Meson’s injection). Correct pattern:

```ini
# cross/android-arm64.ini (sketch)
[binaries]
c = '...'
cpp = '...'
ar = '...'
cmake = 'cmake'

[host_machine]
system = 'android'
cpu_family = 'aarch64'
cpu = 'aarch64'
endian = 'little'

[properties]
# Optional: include vendor NDK toolchain *after* Meson’s generated file
# cmake_toolchain_file = '/path/to/android.toolchain.cmake'
# cmake_defaults = true

[cmake]
CMAKE_SYSTEM_NAME = 'Android'
CMAKE_ANDROID_ARCH_ABI = 'arm64-v8a'
# CMAKE_ANDROID_NDK = '...'
```

### 1.2 Meson policy: mixing is “simple cases only”

From Meson’s *Mixing build systems* policy:

- Mixing is supported for **simple** setups.
- More complex interop is **not guaranteed** across Meson versions; breakages are not treated as Meson regressions.
- Preferred long-term alternative to deep mixing: build deps externally and consume via **pkg-config / installed artifacts**.

**Implication for this project:** treat `cmake.subproject('libdatachannel')` as the **happy path for CI orchestration**, but design a **fallback escape hatch**:

1. **Primary:** Meson root → `cmake.subproject` → link thin C ABI wrapper (Meson `shared_library` / `static_library`) → `install` into Unity Plugins paths.  
2. **Escape:** per-platform script that runs upstream CMake (or dc-unity-style) and only uses Meson for packaging, or pure CMake like Walkerdine/dc-unity.

Do **not** plan to rewrite libdatachannel’s build in Meson (explicitly out of scope in #1).

### 1.3 libdatachannel CMake facts that matter

From upstream `CMakeLists.txt` / `BUILDING.md` (v0.24.x class):

| Fact | Unity impact |
|------|----------------|
| Targets: `datachannel` (shared, default) and `datachannel-static` (`STATIC EXCLUDE_FROM_ALL`) | Plugins: prefer **one shared** artifact on desktop/Android; **static** on iOS/WebGL (or static-link everything into one dylib/framework). |
| `BUILD_SHARED_LIBS` ON by default; when shared + `BUILD_SHARED_DEPS_LIBS` OFF, deps build **static** and are private-linked | Good for single `datachannel.dll`/`.so`/`.dylib` if symbols are not re-exported issues; watch **OpenSSL** still being external. |
| Crypto: OpenSSL (default), GnuTLS, or **Mbed TLS** | Mobile/cross: **Mbed TLS or carefully vendored static OpenSSL** reduces “find OpenSSL” pain (macOS documented `OPENSSL_ROOT_DIR` pitfall). |
| ICE: libjuice submodule (default) or libnice | Prefer juice (submodule) for portable offline CI. |
| `NO_MEDIA` / `NO_WEBSOCKET` / `NO_EXAMPLES` / `NO_TESTS` | **Set ON** for DataChannel-only Unity package (smaller, fewer deps). Media needs CMake ≥ 3.21 if using submodule libsrtp. |
| `CAPI_STDCALL` | Windows: dc-unity sets `ON` for stdcall callbacks — product decision for C ABI. |
| Submodules: plog, usrsctp, libjuice, (libsrtp if media), json (examples) | Wrap must `git submodule update --init --recursive` or use a wrap with nested deps. |
| PIC: `CMAKE_POSITION_INDEPENDENT_CODE ON` | Helps static deps into shared plugin. |
| Apple: special SOVERSION handling; OpenSSL find quirks | macOS universal vs thin; iOS often wants static + MbedTLS. |
| Exports C API via `include/rtc/rtc.h` (`RTC_C_EXPORT`, `RTC_STATIC`) | Aligns with P/Invoke; **static builds need `RTC_STATIC`**. |

Recommended CMake defines for the Unity plugin product (starting point):

```text
NO_MEDIA=ON
NO_WEBSOCKET=ON          # map can re-enable; v1 out of scope for WS binding
NO_EXAMPLES=ON
NO_TESTS=ON
USE_NICE=OFF
BUILD_SHARED_LIBS=ON|OFF # per platform
# Prefer for mobile:
USE_MBEDTLS=ON           # or vendored static OpenSSL
```

### 1.4 datachannel-wasm CMake facts

| Fact | Unity impact |
|------|----------------|
| **Must** be compiled with Emscripten (`CMAKE_SYSTEM_NAME` match or fatal) | Separate backend from native libdatachannel. |
| Produces **STATIC** `datachannel-wasm` | Correct shape for Unity WebGL (link into player). |
| C++17 API under `wasm/include/rtc/*.hpp` — **no `rtc.h` C API** | Thin **C ABI wrapper cannot call wasm C API**; must wrap C++ or reimplement C facade over wasm C++. |
| `target_link_options` injects `--js-library` for `webrtc.js` and `websocket.js` | Unity WebGL needs those as **`.jslib`** assets (dc-unity renames `.js` → `.jslib` and copies next to the static lib). |
| Same C++ surface subset as libdatachannel (no media tracks) | Good for shared C++ wrapper code with `#if` / dual dep as in upstream README. |

### 1.5 Known pitfall catalog (Meson × CMake × this stack)

| Pitfall | Detail | Mitigation |
|---------|--------|------------|
| **Not all CMake projects work** | Meson docs: safest is still writing Meson for the dep | Keep escape hatch; pin Meson + CMake versions in CI |
| **Option kwarg deprecation** | `cmake_options` deprecated since 0.55; use `subproject_options` | Use modern API only |
| **Install pollution** | CMake `install(TARGETS datachannel ...)` can dump headers/libs everywhere | `set_install(false)` then explicit Meson `install_data` / custom install of the one plugin artifact |
| **Target name discovery** | Need exact CMake target name (`datachannel` not `LibDataChannel::...`) | `target_list()` in a probe build; document pinned names |
| **Static + PIC** | Historical issues (e.g. Meson #10764) with PIC detection for CMake static libs | Ensure upstream PIC (already ON); build **shared** wrapper that statically links deps where needed |
| **Shared vs static toggle** | `BUILD_SHARED_LIBS` interacts with `datachannel-static` EXCLUDE_FROM_ALL | Explicitly request the target you need; don’t rely on `default_library` alone for CMake targets |
| **Nested CMake `add_subdirectory` deps** | juice/usrsctp/plog as subdirs — Meson must see transitive link usage | Prefer linking the **final** `datachannel` shared so deps stay private; for static iOS, may need `target_link_libraries` of static + all archives or a unity static amalgam |
| **OpenSSL discovery** | Host contamination when cross-compiling; macOS system vs brew | Machine-file `CMAKE_FIND_ROOT_PATH`, `OPENSSL_ROOT_DIR`, or **Mbed TLS** / superbuild OpenSSL |
| **Windows MSVC + Ninja/Meson** | libdatachannel CI uses NMake; Meson typically Ninja + vsenv | Activate VS env (`vcvars`) before `meson setup`; test arm64 separately |
| **DLL dependency hell** | If OpenSSL/runtime are separate DLLs, Unity won’t ship them unless you place them | **Static-link crypto into the plugin** or ship companion DLLs next to the plugin with correct load rules |
| **Subproject location** | CMake subprojects must live under `subprojects/` | wrap file or git submodule at `subprojects/libdatachannel` |
| **Cross toolchain double-definition** | Manual `-DCMAKE_TOOLCHAIN_FILE` in meson.build unsupported | Only machine-file `cmake_toolchain_file` / `[cmake]` |
| **WebGL Emscripten mismatch** | LLVM object/bitcode not stable across Emscripten versions | Build WebGL plugins with **Unity’s Emscripten** or exact same version as Editor |
| **C API only on native** | wasm is C++-only | Own C facade compiled twice: native→libdatachannel, web→datachannel-wasm C++ |

### 1.6 Recommended architecture pattern

```text
native/  (or package/Native/)
  meson.build                 # project root
  meson.options
  cross/*.ini                 # per platform machine files
  subprojects/
    libdatachannel.wrap       # or git submodule
    datachannel-wasm.wrap     # WebGL only
  src/
    dc_unity_c_api.c(pp)      # stable C ABI for P/Invoke
  tools/
    package_unity_plugins.py  # copy artifacts → Packages/.../Plugins/...
```

**Thin C ABI wrapper** is always a **Meson target** (or small CMake sibling), not a fork of upstream:

- Native: `shared_library('datachannel_unity', ... dependencies: libdc.dependency('datachannel'))`  
  - Or static-link `datachannel-static` + deps into one shared plugin for fewer files.
- WebGL: `static_library` against `datachannel-wasm` + ship `.jslib`.
- iOS: `static_library` (or `.framework`) with `RTC_STATIC`.

Equivalent alternative that still satisfies “Meson orchestration”: Meson `custom_target` / `run_command` invoking CMake as an external superbuild (less elegant, more control for hard platforms). That is still valid if `cmake.subproject` chokes on a given platform.

---

## 2. Typical toolchain / cross-file needs per platform

Assumptions: CI multi-runner (not single Canadian-cross for all targets). Host OS per job as usual (macOS for Apple, Windows for MSVC, Linux for Android/WebGL possible).

### 2.1 Summary matrix

| Target | Build host | Compiler | Crypto suggestion | Meson mode | CMake notes | Artifact |
|--------|------------|----------|-------------------|------------|-------------|----------|
| Windows x64 | Windows | MSVC x64 | OpenSSL static or MbedTLS | Native file / vsenv | `BUILD_SHARED_LIBS=ON` | `datachannel_unity.dll` (+ PDB optional) |
| Windows arm64 | Windows arm64 or x64→arm64 | MSVC ARM64 | Same; verify OpenSSL arm64 | Cross or native arm64 | Same | `datachannel_unity.dll` |
| macOS x64 | macOS | Apple Clang | OpenSSL brew root or MbedTLS | Native (`x86_64`) | Shared dylib or bundle | `.dylib` or `.bundle` |
| macOS arm64 | macOS | Apple Clang | Same | Native (`aarch64`) | Same | Same |
| macOS universal | macOS | lipo of both | Same | Two builds + lipo | Optional product choice | One universal binary |
| iOS arm64 | macOS | Apple Clang + iPhoneOS SDK | **MbedTLS recommended** | Cross (`iphone.txt`-style) | Static; `CMAKE_SYSTEM_NAME=iOS` or SDK flags | `.a` or `.framework` |
| Android arm64 | Linux/macOS | NDK Clang `aarch64-linux-android` | MbedTLS or static OpenSSL | Cross + NDK | `ANDROID_ABI=arm64-v8a`, API level pin | `libdatachannel_unity.so` |
| WebGL | Linux/macOS | **Unity’s emcc/em++** | N/A (browser WebRTC) | Cross `system=emscripten` | datachannel-wasm only | `.a` + `.jslib` |

### 2.2 Per-platform notes

#### Windows x64

- Activate MSVC environment before Meson (`vswhere` / `ilammy/msvc-dev-cmd` style).
- libdatachannel official path: CMake + NMake; Meson+Ninja is fine if cl/link are on PATH.
- P/Invoke: export C API with `extern "C"`; decide `CAPI_STDCALL` consistently with C# `[UnmanagedFunctionPointer]`.
- Ship **one DLL**; statically link runtime (`/MT` vs `/MD`) must match Unity player expectations (Unity typically **`/MD`** — prefer dynamic CRT).

#### Windows arm64

- Use ARM64 VS toolset; do not assume x64 OpenSSL packages work.
- Test on real ARM64 Windows or at least `dumpbin`/load tests; higher risk than x64 because fewer prebuilt crypto binaries and less community coverage.
- Unity Plugin Inspector: CPU = ARM64, OS = Windows.

#### macOS x64 / arm64

- Prefer **separate thin binaries** (clear CPU in Plugin Inspector) unless product wants universal.
- Shared library forms Unity accepts: `.dylib`, `.bundle` (bundled folder), or framework.
- OpenSSL: set `OPENSSL_ROOT_DIR` (Homebrew path differs on Intel vs Apple Silicon) **or** MbedTLS.
- Minimum macOS version flags should be set in machine file `c_args` / `cpp_args`.
- Code signing: not a Meson problem; CI must sign if notarizing host apps — for UPM binary plugins, document consumer signing.

#### iOS arm64

- Cross file pattern: clang + `-isysroot $(xcrun --sdk iphoneos --show-sdk-path)` + `-arch arm64` + `-miphoneos-version-min=...` (see Meson `cross/iphone.txt`).
- Unity iOS plugins are often **statically linked** into the Xcode project (`DllImport("__Internal")` for pure static). Dynamic frameworks need “Add to Embedded Binaries”.
- Recommended product default: **static `.a`** (or `.xcframework` later) of *wrapper + libdatachannel-static + deps*, with `RTC_STATIC`.
- Simulator (x64/arm64) is optional for dev; not in v1 matrix but useful in CI.
- Bitcode is obsolete; ignore old bitcode requirements.

#### Android arm64

- NDK r25+ (align with Unity 2022.3’s expected NDK if possible).
- ABI: `arm64-v8a` only for matrix entry.
- `minSdk` / `ANDROID_PLATFORM` must be documented (e.g. 22/23+).
- Artifact name: `lib<datachannel_unity>.so` (Unity/Android loaders expect `lib` prefix conventions with `DllImport("name")` → `libname.so`).
- Prefer **self-contained .so** (static link libdatachannel + juice + usrsctp + mbedTLS into the plugin) to avoid `System.loadLibrary` chains.

#### WebGL (Emscripten)

- **Do not use host emsdk blindly.** Unity 2022.2+ ships **Emscripten 3.1.8-unity**; recommended plugin format is **GNU archive `.a` of Wasm objects** (`.bc` still accepted but slower).
- Recompile when Unity/Emscripten major changes — LLVM artifacts are not portable across versions.
- Multithreading: if Unity WebGL threads enabled, compile with `-pthread` consistently.
- datachannel-wasm links browser WebRTC via `--js-library`; for Unity, convert to **`.jslib`** plugins (same as dc-unity).
- Meson cross sample `wasm.txt` uses `emcc`/`em++`/`emar`, `host_machine.system = 'emscripten'`.
- CMake: `CMAKE_TOOLCHAIN_FILE` → Emscripten’s platform file via machine-file property, **or** build datachannel-wasm with a small dedicated CMake invocation and only package via Meson.

### 2.3 Crypto backend recommendation (cross-cutting)

| Backend | Pros | Cons |
|---------|------|------|
| OpenSSL (default) | Well tested in libdatachannel CI (Linux/macOS/Windows) | Find package hell; large; mobile/static awkward |
| Mbed TLS | Smaller; friendlier for mobile/static | Less used in default CI path; still need to validate ICE/DTLS |
| GnuTLS | Alternative | Extra deps (Nettle for WS) |

**Recommendation for Unity multi-platform plugins:** default CI to **Mbed TLS for iOS/Android**, OpenSSL or MbedTLS for desktop; keep one matrix row proving OpenSSL desktop if desired. Avoid runtime dependency on system OpenSSL dylibs in shipped games.

---

## 3. Unity plugin file / layout conventions

Unity 2022.3 Plugin Inspector path defaults and supported extensions:

**Recognized native extensions (non-exhaustive):**  
`.dll`, `.so`, `.dylib`, `.a`, `.bc`, `.bundle`, `.framework`, `.xcframework`, `.jslib`, `.jspre`, `.aar`, …

**Path-based defaults:**

| Path pattern | Default platforms |
|--------------|-------------------|
| `Assets/**/Plugins/(x86_64\|x86\|x64)/` | Standalone desktop; CPU from folder |
| `Assets/**/Plugins/iOS/` | iOS |
| `Assets/**/Editor/(arch)/` | Editor only |
| (no match) | Editor defaults — **always set Inspector or `.meta` explicitly in UPM** |

For a UPM package `Packages/com.xuhuanhello.datachannel/`, prefer **explicit** layout + committed `.meta` (CPU, OS, platform checkboxes). Do not rely only on folder magic.

### 3.1 Suggested package layout (normative proposal)

```text
Packages/com.xuhuanhello.datachannel/
  Runtime/
    ... C# ...
  Plugins/
    Android/
      libs/
        arm64-v8a/
          libdatachannel_unity.so
    iOS/
      libdatachannel_unity.a          # or DataChannelUnity.framework /
    x86_64/
      datachannel_unity.dll           # Windows x64 (folder name legacy; set meta OS=Windows)
      # macOS x64 alternative: place under macOS/x64 with meta
    ARM64/
      datachannel_unity.dll           # Windows arm64
    macOS/
      x64/
        datachannel_unity.bundle/   # or .dylib
      arm64/
        datachannel_unity.bundle/
    WebGL/
      libdatachannel_unity.a
      webrtc.jslib                  # from datachannel-wasm (and websocket.jslib if WS enabled)
```

**Notes**

| Platform | File | DllImport | Inspector |
|----------|------|-----------|-----------|
| Windows | `datachannel_unity.dll` | `"datachannel_unity"` | Windows; CPU x86_64 / ARM64 |
| macOS | `datachannel_unity.bundle` or `libdatachannel_unity.dylib` | `"datachannel_unity"` | macOS; CPU |
| Linux (if ever) | `libdatachannel_unity.so` | `"datachannel_unity"` | Linux |
| Android | `libdatachannel_unity.so` under ABI folder | `"datachannel_unity"` | Android; ARM64 |
| iOS static | `libdatachannel_unity.a` | `"__Internal"` | iOS; ARM64; static link |
| iOS framework | `.framework` / `.xcframework` | framework module name | Embed if dynamic |
| WebGL | `.a` + `.jslib` | `"__Internal"` / jslib auto | WebGL only |

**Editor plugins:** optional — only if you need native code in Editor (usually **no** for pure player networking). Avoid loading Android/iOS binaries into Editor.

**Git LFS:** large binaries under `Plugins/` as planned in #1; keep `.meta` in normal git.

### 3.2 dc-unity reference (what to copy vs avoid)

Walkerdine/dc-unity (CMake):

- Dual backend: Emscripten → datachannel-wasm static + copy JS as `.jslib`; else shared lib + libdatachannel `NO_MEDIA`.
- Platforms actually built: Windows, Linux, WebGL — **not** a full mobile matrix.
- **Reuse idea:** dual-backend CMake/Meson switch and jslib packaging.  
- **Avoid:** unmaintained as product baseline; incomplete platforms; no Meson; limited ABI discipline for UPM.

---

## 4. Risk ranking and phased delivery (keep full matrix)

### 4.1 Risk order (highest first)

| Rank | Platform | Why high risk |
|------|----------|---------------|
| 1 | **WebGL** | Separate upstream (datachannel-wasm); **no C API**; must ship **jslib**; **Emscripten version lock** to Unity; browser WebRTC quirks; threading flags; harder automated tests in CI |
| 2 | **iOS arm64** | Static/`__Internal` linking model; Apple SDK cross; crypto/static archives; Xcode embed settings; codesign on device; simulator matrix creep |
| 3 | **Windows arm64** | Toolchain + OpenSSL/MbedTLS availability; fewer reference builds; still “normal” DLL otherwise |
| 4 | **Android arm64** | NDK sysroot/API; self-contained `.so`; JNI not required for pure C ABI but load paths matter; device testing |
| 5 | **macOS x64/arm64** | Moderate; OpenSSL paths; notarization is app-level; dual arch packaging choice |
| 6 | **Windows x64** | Lowest; best upstream CI coverage; dc-unity precedent |

### 4.2 Phased delivery (do **not** remove platforms from the target matrix)

Keep all seven targets as **committed matrix rows** in the map. Ship quality gates in phases:

| Phase | Deliver plugins | Purpose |
|-------|-----------------|---------|
| **P0 – Spike** | Windows x64 **or** macOS host native | Prove Meson + `cmake.subproject(libdatachannel)` + thin C ABI + one Unity `DllImport` smoke test |
| **P1 – Desktop** | Windows x64, macOS arm64, macOS x64 | UPM layout, LFS, Plugin meta, Release builds |
| **P2 – Mobile dynamic** | Android arm64 | NDK cross-file, self-contained `.so` |
| **P3 – Mobile static** | iOS arm64 | Static archive, `__Internal`, device smoke |
| **P4 – Windows arm64** | Windows arm64 | After x64 pipeline is green (same scripts, different VS arch) |
| **P5 – WebGL** | WebGL `.a` + `.jslib` | Parallel track possible from P1; hard-gate on Emscripten=Unity version + C facade over C++ wasm API |

**Parallelization tip:** WebGL can start as soon as the **C ABI header** is stable, because the implementation backend is a different repo — do not block WebGL engineering on Android, but **do** treat WebGL as last **release** gate if resources are serial.

### 4.3 Reliability statement by platform

| Platform | “Reliably produce Unity-ready plugin?” |
|----------|----------------------------------------|
| Windows x64 | **Yes** — high confidence with Meson orchestration or pure CMake |
| macOS x64 / arm64 | **Yes** — medium-high; crypto/path discipline required |
| Android arm64 | **Yes** — medium; standard NDK cross patterns |
| Windows arm64 | **Yes, with effort** — medium; environment packaging |
| iOS arm64 | **Yes, with effort** — medium-low until static link graph proven |
| WebGL | **Yes, with a distinct pipeline** — medium-low; Meson helps packaging more than compilation semantics; Emscripten+jslib is the real product risk |

**Overall:** Meson top-level + CMake subproject is a **sound orchestration choice** for this matrix **if** the team accepts (a) per-platform machine files, (b) install/packaging scripts into Unity layout, (c) WebGL as a specialized backend, and (d) an escape hatch when `cmake.subproject` is insufficient.

---

## 5. HarmonyOS / OpenHarmony future

**Question:** Does Meson orchestration help future HarmonyOS, or is it neutral?

### Assessment: **Neutral → mild positive (not a decisive advantage)**

| Factor | Role of Meson |
|--------|----------------|
| OHOS/Harmony native code uses **CMake + NDK-style toolchains** in vendor docs | New **machine file / cmake toolchain** still required — same work as adding any NDK platform |
| Unity-on-Harmony (if/when) will define **its own plugin layout and ABI** | Packaging rules are product-specific; Meson install templates can be extended, but conventions are unknown today |
| Single orchestrator for “another ELF `.so` ARM target” | **Mild plus:** reusing Android-like cross patterns, option defines, and C ABI wrapper |
| Browser / special backends | Irrelevant unless Harmony Web stack appears |
| Policy risk | Meson still won’t fix vendor CMake quirks; escape hatch remains CMake-direct |

**Recommendation for #1 “future platforms” note:**  
Document an extension point: `cross/harmony-arm64.ini` + CMake defines + Plugins folder convention **TBD**. Do **not** claim Meson uniquely unlocks HarmonyOS. Do **not** put Harmony in v1 matrix.

---

## 6. Decision inputs for the map (actionable)

### 6.1 Keep

- Meson as **top-level orchestrator** for native + packaging.
- Upstream **CMake unchanged** (libdatachannel / datachannel-wasm).
- Full platform matrix with **phased delivery**, not cuts.
- Thin **stable C ABI** owned by this project; P/Invoke against that, not raw C++.

### 6.2 Spec defaults to lock in grilling / map

| Topic | Suggested default |
|-------|-------------------|
| Desktop linkage | One **shared** plugin that private-links static deps where possible |
| iOS linkage | **Static** `.a` (or later xcframework), `DllImport("__Internal")` |
| Android linkage | One **shared** `lib*.so` arm64-v8a, deps static inside |
| WebGL | `datachannel-wasm` static `.a` + required `.jslib`; build with Unity Emscripten |
| Crypto | MbedTLS on mobile; OpenSSL or MbedTLS on desktop (pick one primary in CI) |
| `NO_MEDIA` | ON |
| `NO_WEBSOCKET` | ON for v1 native unless map reopens WS |
| macOS | Separate x64 and arm64 artifacts first; universal optional later |
| Meson min version | ≥ 1.x with CMake module ≥ 0.56 features (toolchain section) |
| CMake min | ≥ 3.13 upstream; ≥ 3.21 only if media re-enabled |

### 6.3 Open risks (track, don’t block research close)

- Prove `cmake.subproject` actually configures libdatachannel with submodules on Windows MSVC and NDK (P0 spike).
- Define how static iOS links **usrsctp + juice + mbedTLS** without symbol clashes with other Unity plugins.
- WebGL C ABI: implement facade over C++ wasm headers.
- Align Emscripten with Unity 2022.3.62f3 exact path in CI.
- Windows arm64 OpenSSL/MbedTLS binary strategy.

---

## 7. Conclusion

**Meson top-level + `cmake.subproject` can reliably support a Unity Plugins pipeline for the stated matrix**, provided the project treats Meson as:

1. **Orchestration & options** (defines, cross files, wrapper target),  
2. **Packaging** into Unity/UPM plugin layouts, and  
3. **Not** a guarantee that every upstream CMake edge case works without an escape hatch.

Highest-risk rows (**WebGL**, **iOS**) remain in the matrix but should be **phased and explicitly staffed**; Meson does not remove their essential constraints (Emscripten lock / jslib / static Apple link). For **HarmonyOS**, Meson is **neutral-to-mildly helpful** as another cross-file consumer, not a strategic shortcut.

---

## Appendix A — Minimal meson sketch (illustrative)

```meson
project('datachannel-unity-native', 'c', 'cpp',
  version: '0.1.0',
  default_options: ['cpp_std=c++17', 'warning_level=2'])

cmake = import('cmake')
fs = import('fs')

opt = cmake.subproject_options()
opt.add_cmake_defines({
  'NO_MEDIA': true,
  'NO_WEBSOCKET': true,
  'NO_EXAMPLES': true,
  'NO_TESTS': true,
  'USE_NICE': false,
  'BUILD_SHARED_LIBS': true,
})
opt.set_install(false)

if host_machine.system() == 'emscripten'
  # Prefer dedicated wasm subproject / custom_target; sketch only
  wasm_opt = cmake.subproject_options()
  wasm_opt.set_install(false)
  wasm = cmake.subproject('datachannel-wasm', options: wasm_opt)
  # static link wrapper against datachannel-wasm
else
  dc = cmake.subproject('libdatachannel', options: opt)
  dc_dep = dc.dependency('datachannel')
  shared_library('datachannel_unity',
    'src/dc_unity_c_api.cpp',
    dependencies: dc_dep,
    install: true,
    install_dir: get_option('unity_plugin_dir'))
endif
```

## Appendix B — Source index

| Topic | URL |
|-------|-----|
| Meson CMake module | https://mesonbuild.com/CMake-module.html |
| Meson mixing policy | https://mesonbuild.com/Mixing-build-systems.html |
| Meson cross compilation | https://mesonbuild.com/Cross-compilation.html |
| Meson machine files (cmake_* properties) | https://mesonbuild.com/Machine-files.html |
| libdatachannel | https://github.com/paullouisageneau/libdatachannel |
| libdatachannel BUILDING | https://github.com/paullouisageneau/libdatachannel/blob/master/BUILDING.md |
| datachannel-wasm | https://github.com/paullouisageneau/datachannel-wasm |
| Unity Import and configure plug-ins (2022.3) | https://docs.unity3d.com/2022.3/Documentation/Manual/PluginInspector.html |
| Unity Native plug-ins | https://docs.unity3d.com/2022.3/Documentation/Manual/NativePlugins.html |
| Unity Desktop plug-ins | https://docs.unity3d.com/2022.3/Documentation/Manual/PluginsForDesktop.html |
| Unity WebGL Emscripten plug-ins | https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-native-plugins-with-emscripten.html |
| dc-unity (reference) | https://github.com/Walkerdine/dc-unity |
