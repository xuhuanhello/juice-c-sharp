# Research: Walkerdine/dc-unity 架构验尸（复用 vs 避免）

| Field | Value |
| --- | --- |
| Ticket | [#6](https://github.com/xuhuanhello/juice-c-sharp/issues/6) (part of Map [#1](https://github.com/xuhuanhello/juice-c-sharp/issues/1)) |
| Upstream | [Walkerdine/dc-unity](https://github.com/Walkerdine/dc-unity) |
| Inspected at | 2026-08-02 via GitHub API (`main`, recursive tree + raw blobs) |
| Last meaningful push | 2023-05-02 (`50cb6bb` “Refactor”) |
| Active window | ~2 weeks (2023-04-17 → 2023-05-02); 5 commits |
| Claimed platforms | WebGL, Windows, Linux |
| License | **None** (repo has no `LICENSE`; do not copy code verbatim) |
| Stars / issues | 0 / 0 |
| Verdict for `com.xuhuanhello.datachannel` | **Reference only** — harvest patterns, do not fork as base |

---

## 1. What it is

A thin **Unity native plugin + C# P/Invoke** layer over:

| Platform path | Dependency | Build product |
| --- | --- | --- |
| Native (non-Emscripten) | git submodule `deps/libdatachannel` | shared lib `DataChannelUnity` linked to `datachannel` |
| WebGL / Emscripten | git submodule `deps/datachannel-wasm` | static lib `DataChannelUnity` + copied `*.jslib` |

Public surface (C# namespace `WebRTC`):

- `PeerConnection` — ICE servers (URL strings only), SDP/candidate, state callbacks, create DC
- `DataChannel` / base `Channel` — send, open/error/message/bufferedAmountLow
- `WebSocket` / `WebSocketServer` — signaling helpers (server excluded on WebGL)

There are **no** samples, tests, CI, prebuilt `Plugins/`, or platform `.meta` files in the tree.

### 1.1 Layout (complete tree of blobs)

```
CMakeLists.txt
.gitmodules                    # libdatachannel + datachannel-wasm
include/
  IUnityInterface.h            # full Unity plugin header (only export macros needed)
  w_rtc.hpp                    # EMSCRIPTEN vs rtc/rtc.h bridge
  w_channel.hpp | w_datachannel.hpp | w_peerconnection.hpp
  w_websocket.hpp | w_websocketserver.hpp
src/
  w_channel.cpp | w_datachannel.cpp | w_peerconnection.cpp
  w_websocket.cpp | w_websocketserver.cpp
unity/MyDataChannel/
  package.json                 # minimal UPM manifest
  Runtime/
    MyDataChannelPackage.asmdef
    callback.cs                # DLL name only
    channel.cs | datachannel.cs | peerconnection.cs
    websocket.cs | websocketserver.cs
```

### 1.2 CMake essentials

```cmake
# Native: SHARED + add_subdirectory(deps/libdatachannel)
option(NO_MEDIA "..." ON)
option(CAPI_STDCALL "..." ON)   # Windows callback calling convention
option(NO_EXAMPLES ON) option(NO_TESTS ON)

# Emscripten: STATIC; copy wasm/js/{webrtc,websocket}.js → *.jslib
# No link step to datachannel-wasm C++ shown — assumes external JS + C API symbols
```

README build recipes: Linux/`make`, Windows/`cmake -A x64` then `make` (latter is wrong for MSVC generators), WebGL via `Emscripten.cmake` toolchain.

---

## 2. Architecture (how layers connect)

```
┌─────────────────────────────────────────────────────────────┐
│ C#  WebRTC.*  (DllImport DataChannelUnity | __Internal)     │
│  - static Dictionary<int,T> instance map                    │
│  - [MonoPInvokeCallback] static thunks → Action delegates   │
│  - finalizers call delete/close                             │
└───────────────────────────┬─────────────────────────────────┘
                            │ P/Invoke (int handles)
┌───────────────────────────▼─────────────────────────────────┐
│ C++ wrappers  PeerConnection_* / Channel_* / DataChannel_*  │
│  UNITY_INTERFACE_EXPORT + UNITY_INTERFACE_API (__stdcall W) │
│  thin forward to rtc* / ws*                                 │
└───────────────────────────┬─────────────────────────────────┘
          ┌─────────────────┴─────────────────┐
          ▼                                   ▼
   rtc/rtc.h (libdatachannel)      w_rtc.hpp EMSCRIPTEN decls
   + CAPI_STDCALL callbacks        ≈ datachannel-wasm C API
```

**Handle model:** both backends use **integer IDs** from the C API (`rtcCreatePeerConnection` → `pc`, etc.). Wrappers do not own C++ objects; they re-export the C ABI with Unity-friendly names/macros.

**WebGL shim (`w_rtc.hpp`):** under `__EMSCRIPTEN__`, re-declares a subset of callback typedefs and `extern` C functions matching datachannel-wasm style (split ice URL/user/pass arrays; `char*` getters that caller `free`s). Native path includes real `rtc/rtc.h`.

**ICE on WebGL:** `PeerConnection_new` re-implements URL parsing (RFC 3986 regex + percent-decode) to split `turn:user:pass@host:port?…` into the three parallel arrays wasm expects. Native path only fills a minimal `rtcConfiguration` with ice server URL pointers and hard-coded defaults.

---

## 3. REUSE — patterns worth keeping for a modern standard binding

These are **ideas and constraints**, not code to copy (no license).

### 3.1 Dual backend: libdatachannel ↔ datachannel-wasm

- One C-shaped surface for C#; `#if __EMSCRIPTEN__` / `#if UNITY_WEBGL` only at the lowest glue.
- C#: `DLL_NAME = "DataChannelUnity"` vs `"__Internal"` for WebGL.
- **Why:** Unity WebGL cannot load a separate `.dll`; symbols must be linked into the player (`__Internal`) while JS bits arrive as `.jslib`. Native players want a normal shared plugin.

**Adopt:** keep a single managed API with two native backends; pin both upstreams; document the WebGL link/jslib pipeline explicitly (dc-unity only half-documents this).

### 3.2 Integer C handles, not opaque `IntPtr` objects

libdatachannel’s C API already uses `int` IDs + `rtcSetUserPointer`. That maps cleanly to:

- P/Invoke without custom marshallers
- WebGL/wasm where the same ID space exists
- Dictionary-based managed object maps (with better lifetime rules than dc-unity)

**Adopt:** stable C ABI in terms of `int` handles + explicit create/destroy; document ID validity and double-free behavior.

### 3.3 Windows callback calling convention alignment

```cmake
option(CAPI_STDCALL "Set calling convention of C API callbacks stdcall" ON)
```

libdatachannel’s `RTC_API` becomes `__stdcall` on Windows when `CAPI_STDCALL` is set — matching Unity’s typical `DllImport` / `Winapi` delegate marshalling and `UNITY_INTERFACE_API` on the wrapper exports.

**Adopt:** for any Windows plugin that registers managed callbacks into libdatachannel, **build libdatachannel with `CAPI_STDCALL=ON`** and declare matching `CallingConvention` on C# delegates. Mismatch = silent stack corruption.

### 3.4 DataChannel-only build flags

```cmake
option(NO_MEDIA ON)
option(NO_EXAMPLES ON)
option(NO_TESTS ON)
```

**Adopt:** v1 is PeerConnection + DataChannel (+ ICE config), not media tracks. Smaller binaries and fewer transitive deps. (Websocket is a separate product decision — Map #1 puts WS out of v1 scope.)

### 3.5 Unity export macro discipline

Using `UNITY_INTERFACE_EXPORT` / `UNITY_INTERFACE_API` on every entry point is the correct Windows/Linux/Android pattern.

**Adopt:** a **minimal** export header (or Unity’s plugin API headers as a dependency), not a vendored full `IUnityInterface.h` if you only need the macros. Prefer explicit `extern "C"` + platform visibility attributes in the stable C ABI crate/layer.

### 3.6 IL2CPP-safe static callback thunks

```csharp
[MonoPInvokeCallback(typeof(LocalDescriptionCallback))]
private static void OnLocalDescriptionCallback(...)
```

**Adopt:** all native→managed entry points must be **static** methods with `[AOT.MonoPInvokeCallback]`. Instance methods and lambdas will break under IL2CPP.

### 3.7 Shared “channel” operations (concept)

libdatachannel treats DataChannel and WebSocket as message channels with shared open/error/message/send/bufferedAmount APIs. A thin shared low-level binding is reasonable.

**Adopt carefully:** share **native/C ABI** helpers if both exist; do **not** force WebSocket into the v1 public package surface (Map #1). Prefer composition over a fragile C# inheritance tree (`DataChannel : Channel`, `WebSocket : Channel` with duplicated registration methods).

### 3.8 Platform exclusion for server-only APIs

`WebSocketServer` wrapped in `#if !UNITY_WEBGL` / `#if !__EMSCRIPTEN__` is correct.

**Adopt:** compile-time stripping of APIs that cannot exist on a platform; mirror with asmdef / define constraints if needed.

### 3.9 Emscripten `.jslib` packaging idea

Copying `webrtc.js` / `websocket.js` and renaming to `.jslib` is the right **Unity WebGL** convention for injecting JS.

**Adopt:** ship `.jslib` (and any required pre/post js) under UPM `Plugins/WebGL/` with correct plugin importer settings — not as a manual post-build copy outside the package.

### 3.10 Thin forwarder philosophy

Wrappers are mostly 1:1 renames (`PeerConnection_onLocalDescription` → `rtcSetLocalDescriptionCallback`). That is the right complexity budget for a stable ABI: **do not** re-express WebRTC as a thick C++ OO layer for Unity.

**Adopt:** stable C ABI package/layer + thin C#; keep behavioral logic in libdatachannel / wasm.

---

## 4. AVOID — defects and design choices to not repeat

### 4.1 Lifecycle: finalizers only, no `IDisposable`

```csharp
~PeerConnection() { PeerConnection_delete(_id); }
~DataChannel()    { DataChannel_close(_id); }
```

Problems:

- Non-deterministic destroy order vs native callbacks still firing
- No explicit `Close()` / `Dispose()` for game code
- Finalizer thread ≠ Unity main thread; native teardown may race
- Instance remains in static `Dictionary` forever (leaks managed wrappers; stale callbacks)

**Avoid:** finalizer-only native ownership. **Prefer:** `IDisposable`, deterministic close, remove from maps on dispose, suppress finalizer, optional “alive” generation/token to ignore late callbacks.

### 4.2 No main-thread marshalling

Native ICE/DC callbacks run on libdatachannel worker threads (and browser threads on WebGL). dc-unity invokes C# `Action`s **directly** from those threads.

**Avoid:** assuming Unity API / game state is safe in callbacks. **Prefer:** lock-free queue or `SynchronizationContext` / player-loop pump; document thread guarantees in the public API (see grilling #8).

### 4.3 Unsynchronized global instance maps

```csharp
private static Dictionary<int, PeerConnection> instances = ...;
// callbacks: instances?[pc].descriptionCallback(...)
```

No lock, no weak refs, no removal. Concurrent create/destroy + callbacks = races; disposed entries still reachable.

**Avoid** bare static dictionaries. **Prefer:** concurrent map + explicit unregister; or pass a GCHandle/`gchandle` as user pointer with careful free.

### 4.4 `userPointer` abused as `int`

```csharp
PeerConnection_setUserPointer(int id, int userId); // C# side
// C++: rtcSetUserPointer(id, ptr) with void*
```

Marshalling an `int` into a `void*` slot is accidental on 32-bit and wrong-shaped on 64-bit; dual fields `_id` vs `Id` confuse “native handle” vs “user cookie”. Lookups already key on native `pc`/`dc` id, so the user pointer is redundant noise.

**Avoid:** overloading user pointer as a second integer id. **Prefer:** either real `IntPtr` user data (GCHandle) **or** pure handle→object map with no user pointer.

### 4.5 Header / implementation / C# symbol drift

| Symbol | Header | `.cpp` | C# |
| --- | --- | --- | --- |
| DataChannel destroy | `DataChannel_delete` | `DataChannel_close` | `DataChannel_close` |
| Channel closed cb | `Channel_onClosed` declared | **not implemented** | unused |
| WebSocket open/closed query | `isOpen` / `isClosed` declared | **not implemented** | unused |
| WebSocket destroy name | `WebSocket_delete` | `WebSocket_close` | `WebSocket_close` |

**Avoid:** hand-written triple surface without a single source of truth. **Prefer:** generate P/Invoke from headers, or one `dc_abi.h` consumed by both sides; CI symbol checks.

### 4.6 Buffer safety holes

Emscripten SDP getters:

```cpp
char* str = rtcGetLocalDescription(pc);
strcpy(buffer, str);   // ignores size
(void)size;
free(str);
```

C# allocates fixed `4096` / `512` / `128` byte scratch buffers for SDP and labels — no length query, no grow, no error if truncated.

**Avoid:** unbounded `strcpy` and magic buffer sizes. **Prefer:** size-query APIs (libdatachannel already returns needed size when buffer is null/too small in many getters) or length-prefixed out-params; throw/return error on truncation.

### 4.7 WebGL ICE URL parser in the Unity plugin

~80 lines of regex URL parse + percent-decode live in `w_peerconnection.cpp` only for Emscripten, with magic capture indices (`opt[2]`, `opt[6]`, `opt[10]`…). Duplicates logic that belongs in datachannel-wasm or in managed config types.

**Avoid:** re-parsing ICE URIs in the plugin. **Prefer:** structured ICE server config in C# (urls, username, credential) marshalled as arrays/structs — aligns with grilling #9 and avoids TURN URL credential encoding bugs.

### 4.8 Incomplete `rtcConfiguration` surface

Native `PeerConnection_new` only accepts `const char** ice_servers` and hard-codes:

- `iceTransportPolicy = ALL`
- `enableIceTcp = false`, `enableIceUdpMux = false`
- `portRange* = 0`, `mtu = 0`, `maxMessageSize = 0`
- no proxy, no bind address, no force relay

**Avoid:** URL-string-only configuration as the long-term ABI. **Prefer:** versioned config struct (or builder) covering STUN/TURN fields Map #1 needs, with safe defaults.

### 4.9 Callback registration API shape

```csharp
public void OnLocalDescription(Action<int,string,string> callback)
```

- Imperative `OnX(callback)` overwrites silently; not C# events
- Always passes `userId` into the Action even when useless
- `OnDataChannel` delivers raw `int dc` instead of a `DataChannel` wrapper
- No unsubscribe / multicast

**Avoid:** mirroring C setter style 1:1 as the **public** C# API. **Prefer:** thin private P/Invoke + public events/`IObservable`/callbacks that don’t leak handles; construct managed `DataChannel` on remote open.

### 4.10 Message path GC pressure

Every message: `new byte[size]` + `Marshal.Copy`. Fine for prototypes; bad for game tick / high-rate DC.

**Avoid** as the only path. **Prefer:** optional caller-owned buffer, `Span`/`Memory` (with care), or pooled arrays; binary vs string distinction (libdatachannel uses negative size for binary in some APIs — verify and document).

### 4.11 Namespace and product naming collisions

- Namespace **`WebRTC`** collides with Unity’s `Unity.WebRTC` / community mental model
- Package display name `MyDataChannel`, asmdef `MyDataChannelPackage`, package name `dc-unity` — inconsistent placeholders
- No `com.*` reverse-DNS id

**Avoid:** placeholder names in a public UPM package. **Prefer:** `com.xuhuanhello.datachannel`, namespace e.g. `DataChannel` / `Xuhuanhello.DataChannel`, asmdef matching.

### 4.12 UPM layout is a sketch, not a shippable package

Missing for a real UPM:

| Expectation | dc-unity |
| --- | --- |
| `package.json` description, author, license, changelog, samples | only name/version/displayName/unity |
| `Runtime/` + `Editor/` split | Runtime only |
| `Plugins/<platform>/` with `.meta` PluginImporter | **absent** — consumers must build and drop binaries themselves |
| Documentation / samples | none in package |
| `LICENSE.md` / third-party notices | none |
| Versioned native binary policy (CI + LFS) | none |

**Avoid:** “source-only C# + you figure out natives.” **Prefer:** Map #1 plan — CI builds per platform, Git LFS under `Plugins/`, importer settings checked in.

### 4.13 CMake / packaging hygiene

- `file(GLOB SOURCES …)` — rebuild dependency tracking issues
- No install rules, no `CMAKE_OSX_ARCHITECTURES`, no iOS toolchain, no Android NDK
- Windows README uses `make` after VS generator
- Emscripten branch does not `add_subdirectory` wasm or set link flags/emscripten ports; integration is incomplete for a drop-in Unity WebGL plugin
- No version pins / tags on submodules in docs
- No CI matrix

**Avoid** as the production build system of record. Map #1 already chooses **Meson orchestration + upstream CMake subbuilds**; treat dc-unity CMake as a historical sketch.

### 4.14 Scope creep: WebSocket in the “datachannel” package

dc-unity ships WS client + WS server next to PeerConnection. Map #1 explicitly **out-of-scope for v1** for WebSocket bindings and signal servers.

**Avoid:** pulling WS into the first stable ABI. Keep PeerConnection + DataChannel (+ ICE) minimal; optional future module.

### 4.15 No error model

Most C APIs return `int` status; wrappers discard or return raw ints as handles without checking `< 0`. C# never throws or surfaces `rtcGetError` / last error strings consistently.

**Avoid:** silent failure. **Prefer:** checked create (throw or `Result`), and error callbacks that remain after dispose is defined.

### 4.16 Legal / process

No license on the repo. Even if patterns are generic, **do not copy** substantial code blocks. Reimplement against libdatachannel (MPL-2.0) and datachannel-wasm licenses with proper notices (see research #5).

---

## 5. Platform matrix: present vs missing

| Platform | dc-unity | Notes for modern package |
| --- | --- | --- |
| Windows x64 | Claimed (CMake `-A x64`) | Need arm64 too (Map #1) |
| Linux x64 | Claimed | Editor/server; document glibc baseline |
| WebGL | Claimed (Emscripten + jslib) | Incomplete packaging; needs dedicated research (#3) |
| macOS x64 / arm64 | **Missing** | Universal vs split artifacts TBD |
| iOS arm64 | **Missing** | Static lib vs `.framework` vs dylib — grilling #10 |
| Android arm64 | **Missing** | NDK + JNI-less C plugin under `Plugins/Android` |
| Windows arm64 | **Missing** | Map #1 includes it |
| Console / other | N/A | Out of scope |

Also missing operationally: code signing, Apple bitcode history (obsolete), IL2CPP vs Mono test matrix, Editor vs player plugin flags (`CPU`, `OS`, `isPreloaded`).

---

## 6. Mapping onto juice-c-sharp / Map #1 decisions

| Map #1 intent | dc-unity lesson |
| --- | --- |
| UPM `Packages/datachannel-unity` (`com.xuhuanhello.datachannel`) | Replace placeholder package.json / asmdef / namespace |
| Stable C ABI + P/Invoke + thin C# | Keep thin C ABI idea; rewrite surface with tests & versioning |
| Meson top-level + upstream CMake | Do **not** adopt dc-unity CMake as product build |
| CI → Git LFS `Plugins/` | Explicitly fix dc-unity’s “no binaries” gap |
| Platforms: Android arm64, Win x64/arm64, iOS, macOS x64/arm, WebGL | Expand far beyond Win/Linux/WebGL sketch |
| No media; no FishNet coupling; no signal server product | Align: drop media (already); **defer** WS; keep transport-agnostic |
| Reference dc-unity, don’t depend on it | Confirmed: unmaintained, unlicensed, incomplete |

Downstream grilling tickets should treat this autopsy as negative constraints:

- **#7 C ABI** — handle-based, stdcall on Win, size-safe strings, full ICE config, no header drift
- **#8 C# API / threads** — Dispose, main-thread pump, no finalizer ownership
- **#9 ICE config** — structured servers, not only URL strings + plugin-side URL parse
- **#10 Plugins shape** — real per-platform layout; iOS/Android/macOS first-class
- **#3 WebGL** — finish what dc-unity only sketched (jslib + `__Internal` + link)

---

## 7. Scorecard (cheat sheet)

| Area | Reuse? | One-liner |
| --- | --- | --- |
| Dual native / wasm backend | **Yes** | Same managed API, two backends |
| int handles + C API | **Yes** | Matches libdatachannel & wasm |
| `CAPI_STDCALL` + export macros | **Yes** | Required on Windows |
| `NO_MEDIA` | **Yes** | DataChannel-only product |
| `MonoPInvokeCallback` static thunks | **Yes** | IL2CPP requirement |
| `.jslib` rename idea | **Yes** | Ship inside UPM WebGL plugins |
| Thin forwarder wrappers | **Yes (idea)** | Don’t invent a thick OO native layer |
| Finalizer lifecycle | **No** | Use `IDisposable` + unregister |
| Direct threaded callbacks | **No** | Marshal to player loop |
| Static Dictionary maps | **No** as-is | Need sync + removal |
| `int` as `void*` user data | **No** | GCHandle or map only |
| Fixed SDP buffers / `strcpy` | **No** | Size-query APIs |
| WebGL ICE URL regex in C++ | **No** | Structured C# config |
| WebSocket in v1 core | **No** | Map #1 OOS |
| Incomplete UPM / no Plugins | **No** | CI + LFS + meta |
| Namespace `WebRTC` / My* names | **No** | Reverse-DNS package id |
| CMake GLOB, no mobile/mac | **No** | Meson+CMake matrix |
| Copy code (no license) | **No** | Reimplement patterns only |

---

## 8. Sources inspected

All via `gh api repos/Walkerdine/dc-unity/...` on `main` (blobs listed in §1.1), plus:

- Repo metadata: `created_at` 2023-04-17, `pushed_at` 2023-05-02, description “WebRTC datachannels plugin for unity with support for WebGL, Windows and Linux”
- Commits: Initial → README → WebSocketServer → two “Refactor” commits
- Upstream C API cross-check: [paullouisageneau/libdatachannel `include/rtc/rtc.h`](https://github.com/paullouisageneau/libdatachannel/blob/master/include/rtc/rtc.h) (`CAPI_STDCALL`, handle IDs, callback typedefs)
- Local Map context: `xuhuanhello/juice-c-sharp` issue #1 body (destination package & platform matrix)

---

## 9. Bottom line

**dc-unity proves the feasible shape** of a Unity DataChannel binding (C handle ABI, dual wasm/native, stdcall callbacks, IL2CPP static thunks, jslib hooks) and **fails as a foundation** (lifecycle, threads, safety, platforms, packaging, license, maintenance).

For `com.xuhuanhello.datachannel`: **reuse the architectural pattern, reimplement every layer cleanly, expand the platform matrix, and treat WebSocket/media as non-goals for v1.**
