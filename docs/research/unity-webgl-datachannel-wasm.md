# Research: Unity WebGL + datachannel-wasm 打包约束

**Ticket:** [#3](https://github.com/xuhuanhello/juice-c-sharp/issues/3)  
**Parent map:** [#1](https://github.com/xuhuanhello/juice-c-sharp/issues/1) — UPM `com.xuhuanhello.datachannel`，PeerConnection + DataChannel only  
**Project Unity:** 2022.3.62f3  
**Date:** 2026-08-02  
**Scope:** Research only — no package implementation in this ticket.

## Executive answer

To give C# the **same PeerConnection + DataChannel surface** on WebGL as on native libdatachannel:

1. **Do not ship raw libdatachannel on WebGL.** Browsers have no usable raw UDP for a full ICE/DTLS/SCTP stack. Use **[datachannel-wasm](https://github.com/paullouisageneau/datachannel-wasm)** (or an equivalent thin bridge) that wraps **browser `RTCPeerConnection` / `RTCDataChannel`**.
2. **Ship two plugin artifact kinds under WebGL:**
   - Static archive **`.a`** (Emscripten wasm objects): your **stable C ABI** (+ optionally datachannel-wasm C++ objects).
   - Emscripten JS libraries as Unity **`.jslib`**: at minimum datachannel-wasm’s `wasm/js/webrtc.js` (rename/copy to `.jslib`). Skip `websocket.js` for v1 if WebSocket is out of scope.
3. **C# always `DllImport`s the stable C ABI.** On WebGL the DLL name is **`__Internal`** (linked into the Unity player wasm). On desktop/mobile it is the native plugin name (e.g. `DataChannelUnity`).
4. **Rebuild `.a` with Emscripten matching Unity’s toolchain** (Unity **2022.2+ → Emscripten 3.1.8-unity**). Binary objects are not portable across Emscripten major lines.
5. **Signaling stays external** on all platforms, including WebGL. Package scope is PC+DC (+ ICE server config only), not a signal server.

---

## 1. datachannel-wasm build artifacts & Emscripten requirements

### What it is

[datachannel-wasm](https://github.com/paullouisageneau/datachannel-wasm) (v0.4.0 as of CMake project version; actively maintained, last push 2025-09) is a **C++ wrapper for Emscripten** that:

- Exposes a **subset of the libdatachannel C++ API** under `wasm/include/rtc/` (`PeerConnection`, `DataChannel`, `WebSocket`, config/description/candidate types).
- **Does not support media tracks / media transport** — aligns with “PC+DC only”.
- Implements the browser side via **`--js-library` glue**, not a port of libjuice/libsrtp/usrsctp.

Upstream README states the purpose: same C++ Data Channel / WebSocket code can target native (libdatachannel) or browsers (datachannel-wasm).

### Source layout (relevant)

| Path | Role |
|------|------|
| `wasm/src/*.cpp` | C++ wrappers (`peerconnection.cpp`, `datachannel.cpp`, …) |
| `wasm/include/rtc/*.hpp` | Public C++ headers (API surface subset) |
| `wasm/js/webrtc.js` | Emscripten JS library: `rtcCreatePeerConnection`, DC send/recv, ICE callbacks, … |
| `wasm/js/websocket.js` | Emscripten JS library for WebSocket (out of v1 package scope per map #1) |
| `CMakeLists.txt` | **Static** target `datachannel-wasm`; **requires** `CMAKE_SYSTEM_NAME` = Emscripten |

### CMake product shape

From upstream `CMakeLists.txt`:

- `add_library(datachannel-wasm STATIC …)` → typical output **`libdatachannel-wasm.a`** (GNU ar of wasm `.o` files under modern emsdk).
- Public include: `wasm/include`.
- **Link interface** injects JS libraries:

```cmake
target_link_options(datachannel-wasm PUBLIC
  "SHELL:--js-library \"…/wasm/js/webrtc.js\""
  "SHELL:--js-library \"…/wasm/js/websocket.js\"")
```

**Unity implication:** those `target_link_options` are **not** applied when Unity only consumes a prebuilt `.a`. You must **also place the JS files as `.jslib` plugins** in the Unity project so the player link gets `--js-library`.

### JS glue format (already Unity-compatible)

Both `webrtc.js` and `websocket.js` end with the standard Emscripten library pattern:

```js
autoAddDeps(WebRTC, '$WEBRTC');
mergeInto(LibraryManager.library, WebRTC);
```

That is the same mechanism Unity documents for **`.jslib`** (`--js-library`). They use `{{{ makeDynCall(...) }}}` for callbacks into wasm — processed by Emscripten at Unity WebGL link time.

Internal model:

- Integer handles map to JS `RTCPeerConnection` / `RTCDataChannel` objects (`peerConnectionsMap` / `dataChannelsMap`).
- Binary DC mode: `binaryType = 'arraybuffer'`.
- SDP/ICE events call registered function pointers with heap-allocated UTF-8 strings (caller frees where documented).

### Critical ABI fact: JS C symbols ≠ libdatachannel C API

datachannel-wasm’s **low-level C symbols implemented in JS** are a **browser-oriented mini-ABI**, not a drop-in for [libdatachannel `rtc/rtc.h`](https://github.com/paullouisageneau/libdatachannel):

| Concern | libdatachannel C API | datachannel-wasm JS `rtc*` |
|--------|----------------------|----------------------------|
| Create PC | `rtcCreatePeerConnection(const rtcConfiguration*)` | `rtcCreatePeerConnection(urls[], usernames[], passwords[], n)` |
| Get local SDP | fill caller buffer + size | returns `malloc`’d C string (caller frees) |
| Description callback | `(int pc, sdp, type, void* ptr)` | `(sdp, type, void* ptr)` — **no pc id** |
| Open/message callbacks | `(int id, …)` first | often **userPointer only** |
| DataChannel create | `rtcCreateDataChannel` / `Ex` + `rtcDataChannelInit` | flat args: `unordered`, `maxRetransmits`, `maxPacketLifeTime` |

The **C++ classes** in datachannel-wasm adapt those JS symbols into a libdatachannel-like C++ API. The **raw JS exports do not**.

**Package recommendation:** define and export a **project-owned stable C ABI** (map #1 / grilling #7). Implement:

- **Native:** against libdatachannel C (or C++) API.
- **WebGL:** against datachannel-wasm C++ **or** against the JS mini-ABI with an explicit adapter — never assume `rtc.h` symbols exist in the browser plugin.

### How to build for Unity consumption

```bash
# Use emsdk whose version matches Unity (see §5). Prefer Unity-bundled or 3.1.8 line for 2022.3.
source "$EMSDK/emsdk_env.sh"

cmake -B build-webgl \
  -DCMAKE_TOOLCHAIN_FILE="$EMSDK/upstream/emscripten/cmake/Modules/Platform/Emscripten.cmake" \
  -DCMAKE_BUILD_TYPE=Release
cmake --build build-webgl -j

# Artifacts of interest:
#   build-webgl/libdatachannel-wasm.a   (name may vary with generator)
#   deps path: wasm/js/webrtc.js → copy into Unity as webrtc.jslib
```

If the package builds **its own** C ABI sources for WebGL, either:

- **Link** `datachannel-wasm` as a static dependency and use the C++ API inside the ABI layer, **or**
- Compile only the ABI `.cpp` that `extern "C"`-calls the JS `rtc*` symbols (dc-unity style) and ship **only** `.jslib` for those symbols — then you must re-declare the **JS** signatures correctly (see §3).

Match Unity Player flags when compiling the `.a` (see Unity manual “Compile a static library as a Unity plug-in”):

| Player setting | Extra emcc flag |
|----------------|-----------------|
| Enable Exceptions = None | `-fno-exceptions` |
| Enable Native C/C++ Multithreading | `-pthread` |
| Enable WebAssembly 2023 | `-fwasm-exceptions -sSUPPORT_LONGJMP=wasm -mbulk-memory -mnontrapping-fptoint -msimd128 -msse4.2` |

C++17 is required (upstream sets `CXX_STANDARD 17`).

---

## 2. Unity WebGL plugin conventions

Primary docs (Unity **2022.3**):

- [WebGL native plug-ins for Emscripten](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-native-plugins-with-emscripten.html)
- [Interaction with browser scripting](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-interactingwithbrowserscripting.html)
- [Set up your JavaScript plug-in](https://docs.unity3d.com/2022.3/Documentation/Manual/web-interacting-browser-js.html)
- [Compile a static library as a Unity plug-in](https://docs.unity3d.com/2022.3/Documentation/Manual/web-interacting-browsers-library.html)

### Supported native plugin formats (by Unity version)

| Unity | Emscripten | Preferred plugin format |
|-------|------------|-------------------------|
| **2022.2+** (incl. **2022.3 LTS**) | **3.1.8-unity** | **`.a`** (wasm object archives); `.bc` still accepted but slower |
| 2021.2+ | 2.0.19.6-unity | `.a`, `.bc` |
| 2019.2–2021.1 | 1.38.11-unity | `.bc` only |

**Rule:** Emscripten used to compile plugins **must match** the version Unity links with. Recompile when upgrading Unity major WebGL toolchain.

Locate Unity’s version string:

```text
<Editor>/PlaybackEngines/WebGLSupport/BuildTools/Emscripten/emscripten/emscripten-version.txt
```

### `.jslib` / `.jspre`

| Extension | Emscripten flag | Role |
|-----------|-----------------|------|
| **`.jslib`** | `--js-library` | Defines callable symbols; `mergeInto(LibraryManager.library, { … })` |
| **`.jspre`** | `--pre-js` | Prepended into `*.framework.js`; not directly DllImport-able from C# |

Unity notes: **ES5 only** in `.jslib` / `.jspre` (no ES6 modules/syntax). datachannel-wasm glue is ES5-oriented and fine.

Place under e.g. `Packages/.../Plugins/WebGL/` or `Assets/Plugins/WebGL/`.

### C# interop pattern (WebGL)

```csharp
#if UNITY_WEBGL && !UNITY_EDITOR
    private const string Dll = "__Internal";
#else
    private const string Dll = "DataChannelUnity"; // native plugin name
#endif

[DllImport(Dll)]
private static extern int PeerConnection_New(/* … */);
```

- WebGL player is **IL2CPP → C++ → Emscripten**; native plugins are **statically linked** into the wasm module → **`__Internal`**.
- Editor Play Mode on desktop does **not** load WebGL plugins; use native libs or stubs for Editor.
- Callbacks from JS/wasm into managed code need **`[MonoPInvokeCallback]`** (AOT) and usually a main-thread marshal plan (grilling #8).

### PluginImporter (`.meta`) conventions

For each binary / jslib asset, set:

| Asset | Include platforms | Notes |
|-------|-------------------|--------|
| `*.a` (WebGL static) | **WebGL only** | CPU = Any / WebGL; disable Editor, Standalone, mobile |
| `webrtc.jslib` | **WebGL only** | Same; ensure not treated as TextAsset |
| Native `.dll` / `.so` / `.dylib` / `.a` / `.framework` | Per-platform | Opposite of WebGL |

Folder layout suggestion for the UPM package:

```text
Packages/datachannel-unity/
  Runtime/                     # C# API, DllImport surface
  Plugins/
    WebGL/
      libdatachannel_unity.a   # stable C ABI (+ wasm backend)
      webrtc.jslib             # from datachannel-wasm (PC+DC)
      # websocket.jslib        # only if vN adds WebSocket
    Windows/x86_64/…
    Android/arm64-v8a/…
    …
  package.json
```

CI (map #1): prebuild platform plugins → **Git LFS** under `Plugins/`.

### Player settings interactions

- **WebGL is always IL2CPP** — no Mono backend choice that changes P/Invoke strategy.
- Multithreading, exceptions, Wasm 2023: must stay consistent between prebuilt `.a` and player (table in §1).
- Optional: `PlayerSettings.WebGL.emscriptenArgs` for extra link flags; prefer baking requirements into the static lib + jslib rather than requiring consumers to set exotic args.

---

## 3. Walkerdine/dc-unity WebGL path — reuse vs avoid

**Repo:** [Walkerdine/dc-unity](https://github.com/Walkerdine/dc-unity)  
**Status:** last commits **2023-05-02**; not archived, but **unmaintained**; package.json targets **`"unity": "2022.2"`**.  
**Role for this project:** **reference only** (map #1) — do not take as a live dependency.

### What it did well (reuse as design notes)

1. **Single C# API / dual backend** idea: one managed surface; native = libdatachannel, WebGL = browser via datachannel-wasm glue.
2. **`DLL_NAME` switch:**
   ```csharp
   #if !UNITY_WEBGL
       public const string DLL_NAME = "DataChannelUnity";
   #else
       public const string DLL_NAME = "__Internal";
   #endif
   ```
3. **WebGL CMake:** build **STATIC** library; **copy** `webrtc.js` / `websocket.js` and **rename to `.jslib`** for Unity — correct packaging instinct.
4. **Thin `extern "C"` export layer** (`PeerConnection_*`, `Channel_*`, …) with `UNITY_INTERFACE_EXPORT` — same direction as “stable C ABI”.
5. **`#if __EMSCRIPTEN__` branches** for create-PC URL parsing and description string ownership differences (malloc’d vs fill-buffer).

### What is missing or broken (avoid / fix)

| Gap | Detail |
|-----|--------|
| **No shipped Plugins/** | Unity package tree is C# only (`unity/MyDataChannel/Runtime/*`). No `.a`, no `.jslib`, no PluginImporter metas — consumers must build themselves. |
| **Emscripten CMake does not link datachannel-wasm** | On Emscripten it only compiles `src/*.cpp` and copies JS; it does **not** `add_subdirectory(deps/datachannel-wasm)` or link the C++ static lib. WebGL depends entirely on JS `rtc*` symbols + thin C wrappers. |
| **Callback signature mismatch** | Emscripten typedefs in `w_rtc.hpp` follow **libdatachannel** shapes (`(int pc, sdp, type, ptr)`), but datachannel-wasm **JS** invokes description/candidate callbacks **without** the `pc` argument. Passing C#/`MonoPInvokeCallback` function pointers straight into `rtcSetLocalDescriptionCallback` is **ABI-incorrect** unless JS is patched. |
| **C API ≠ C++ API path** | Native uses libdatachannel **C** API; wasm JS mini-ABI differs (see §1). Compatibility is hand-rolled and incomplete. |
| **WebSocket / WebSocketServer in surface** | Map #1 v1 excludes WebSocket bindings; dc-unity includes them. |
| **Lifecycle / safety** | Finalizers calling native delete; fixed-size SDP buffers (4096); limited error model; no clear main-thread dispatch. |
| **Emscripten age** | Built against 2023-era emsdk mental model; must re-validate on **3.1.8-unity** and current datachannel-wasm. |
| **No CI matrix / LFS binaries** | Opposite of map #1 distribution model. |

### Verdict

| Reuse | Avoid |
|-------|--------|
| Concept: static `.a` + `.jslib` from datachannel-wasm JS | Dropping prebuilt WebGL artifacts |
| `__Internal` vs named DLL switch | Assuming JS `rtc*` == `rtc/rtc.h` |
| Idea of a narrow C export layer | Copying callback wiring without an adapter |
| ICE URL split for browser create-PC | Taking WebSocket server / full surface as v1 |
| — | Untested finalizer-based lifecycle |

**Preferred WebGL architecture for this package:**

```text
C# API
  → P/Invoke stable C ABI  (same symbol names all platforms)
      → [Native]  libdatachannel (NO_MEDIA)
      → [WebGL]   adapter in .a
                    → either datachannel-wasm C++  + webrtc.jslib
                    → or carefully typed externs to webrtc.jslib only
```

Do **not** re-export two different C ABIs to C#. One ABI, two implementations.

---

## 4. Browser constraints → package scope hard rules

These are not Unity bugs; they bound **what “WebGL support” can mean**.

### No raw UDP / no full libdatachannel stack in-browser

- Browser JS cannot open arbitrary UDP sockets for ICE/STUN/TURN the way libjuice does on native.
- **datachannel-wasm does not compile libdatachannel for wasm**; it **delegates** to `window.RTCPeerConnection`.
- Therefore WebGL builds **must not** try to link libdatachannel / libjuice / usrsctp for the data path.

### Signaling is always external

- WebRTC (browser and libdatachannel) only defines **how** to exchange media/data after SDP + candidates are known.
- **Offer/answer + ICE candidates** must be carried by the **application** (WebSocket, HTTPS, FishNet message, etc.).
- Map #1 already out-of-scopes “自建 Signal 服务端” — WebGL does not change that; it makes the requirement **more obvious**.

### ICE / TURN

- Configure STUN/TURN via **ICE server list** on `PeerConnection` (browser `iceServers`).
- TURN still works in browsers when URLs/credentials are valid; package only **passes config through**, does not run a TURN server (map #1).
- Host candidates / mDNS / consent may differ by browser privacy policy — document as platform variance, not ABI failure.

### Secure context & browser APIs

- `RTCPeerConnection` generally requires a **secure context** (HTTPS or localhost).
- Feature detect: datachannel-wasm returns `0` from `rtcCreatePeerConnection` if `window.RTCPeerConnection` is missing.
- Tab backgrounding, battery savers, and mobile browser freezes can delay ICE/DC — expose state callbacks; don’t assume desktop-like keepalive.

### Scope alignment (PC+DC only)

| In scope on WebGL | Out of scope |
|-------------------|--------------|
| PeerConnection create/close, states | Media tracks / RTP |
| createDataChannel, onDataChannel | Custom raw UDP transports |
| send/receive binary (and text if API allows) | Package-provided signal server |
| setRemoteDescription / addIceCandidate | Embedding a second full WebRTC stack |
| ICE server configuration | v1 WebSocket API (optional later via `websocket.jslib`) |

WebSocket support in datachannel-wasm is real but **orthogonal**; map #1 defers it. Shipping only `webrtc.jslib` is enough for PC+DC. (If both jslibs are shipped later, note `websocket.js` references `WEBRTC.allocUTF8FromString` in one helper — ordering/deps may matter.)

### Interop with native peers

- Browser PC can talk to **native libdatachannel** peers if SDP/ICE interop holds (standard WebRTC).
- Keep reliability options (ordered/unordered, maxRetransmits, maxPacketLifeTime) mapped consistently in the stable ABI.
- Max message size / bufferedAmount semantics should be documented per backend (browser vs native).

---

## 5. Unity 2022.3 LTS notes

This project pins **Unity 2022.3.62f3**.

| Topic | Guidance |
|-------|----------|
| Emscripten | **3.1.8-unity** for 2022.2+; compile WebGL `.a` with that line |
| Plugin format | Prefer **`.a`**, not legacy `.bc` |
| Rebuild policy | Any Unity upgrade that changes WebGLSupport Emscripten → **rebuild** WebGL plugins |
| C# backend | WebGL = IL2CPP only; AOT-safe callbacks required |
| `.jslib` language | ES5; datachannel-wasm glue OK |
| Multithreading | If Player enables C/C++ threads, compile plugin with **`-pthread`** and validate browser SharedArrayBuffer COOP/COEP headers for the host page |
| Exceptions | Match Player “Enable Exceptions” when building `.a` |
| Wasm 2023 | If enabled in Player, pass the documented SIMD/bulk-memory flags when building `.a` |
| Editor | WebGL `.a`/`.jslib` inactive in Editor; provide native Editor plugins or stub implementations for play-mode tests |
| dc-unity package target | Claimed 2022.2 — same Emscripten **family** as 2022.3, but still recompile and fix callback ABI |
| Third-party lesson | Other native WebGL plugins (e.g. Draco) break across Emscripten jumps — treat **toolchain pin + CI rebuild** as first-class |

Optional verification on a machine with this Editor installed:

```bash
cat "$UNITY_EDITOR/PlaybackEngines/WebGLSupport/BuildTools/Emscripten/emscripten/emscripten-version.txt"
```

---

## Recommended packaging checklist (for implementers)

Non-implementing summary of “done means”:

1. **Stable C ABI** headers shared by all platforms (symbols C# will DllImport).
2. **WebGL CI job:** emsdk **3.1.8** (or Unity-bundled) → build ABI (+ datachannel-wasm if linked) → `libdatachannel_unity.a`.
3. Copy **`webrtc.js` → `Plugins/WebGL/webrtc.jslib`** (pin datachannel-wasm git SHA).
4. **PluginImporter** metas: WebGL-only for `.a` and `.jslib`.
5. C#: `DllImport("__Internal")` under `UNITY_WEBGL && !UNITY_EDITOR`.
6. **Adapter tests:** create PC, exchange SDP/candidates via test signal double, open DC, send binary both directions — in a real browser build (not Editor).
7. Document host page requirements: HTTPS, optional COOP/COEP if threads, ICE server examples.
8. Do **not** commit to shipping `websocket.jslib` until WebSocket is in product scope.

---

## Sources

### Primary

- [paullouisageneau/datachannel-wasm](https://github.com/paullouisageneau/datachannel-wasm) — README, `CMakeLists.txt`, `wasm/js/webrtc.js`, `wasm/include/rtc/*`, `wasm/src/*`
- [paullouisageneau/libdatachannel](https://github.com/paullouisageneau/libdatachannel) — C API `include/rtc/rtc.h` (native contrast)
- [Walkerdine/dc-unity](https://github.com/Walkerdine/dc-unity) — CMake WebGL branch, `w_rtc.hpp`, C# `callback.cs` / `peerconnection.cs` (reference, 2023)
- Unity 2022.3 Manual:
  - [WebGL native plug-ins for Emscripten](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-native-plugins-with-emscripten.html)
  - [Interaction with browser scripting](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-interactingwithbrowserscripting.html)
  - [Set up your JavaScript plug-in](https://docs.unity3d.com/2022.3/Documentation/Manual/web-interacting-browser-js.html)
  - [Compile a static library as a Unity plug-in](https://docs.unity3d.com/2022.3/Documentation/Manual/web-interacting-browsers-library.html)

### Secondary context

- [libdatachannel.org](https://libdatachannel.org/) — notes datachannel-wasm as the browser compilation path for DC/WebSocket code
- Unity forum / ecosystem experience: Emscripten version skew breaks prebuilt WebGL native plugins (pattern, not specific to this package)

---

## Decisions this research locks for map #1

1. **WebGL backend = datachannel-wasm glue + package C ABI**, not libdatachannel-in-wasm.
2. **Ship `.a` + `webrtc.jslib`**, rebuilt for **Emscripten 3.1.8-unity** (Unity 2022.3).
3. **One C# / C ABI surface**; platform `#if` only for DllImport library name and Editor stubs.
4. **dc-unity is a prior-art sketch**, not a base to vendor; especially **do not copy callback registration** without fixing JS vs C callback layouts.
5. **Browser constraints reinforce** existing out-of-scope items: no media, no package signal/TURN servers, no raw UDP API.
6. **WebSocket jslib** optional later; not required for PC+DC v1.
