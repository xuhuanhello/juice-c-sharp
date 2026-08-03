# Research: libdatachannel C API (PeerConnection + DataChannel + ICE)

**Ticket:** https://github.com/xuhuanhello/juice-c-sharp/issues/2  
**Parent map:** #1 (`com.xuhuanhello.datachannel` / datachannel-unity)  
**Date:** 2026-08-02  
**Upstream pin studied:** [paullouisageneau/libdatachannel](https://github.com/paullouisageneau/libdatachannel) tag **`v0.24.5`** (`RTC_VERSION` 0.24.5)  
**License:** MPL-2.0 (since 0.18)

## Verdict (for v1 binding)

| Question | Answer |
|---|---|
| Does official C API cover PC + DC + ICE/STUN/TURN for v1? | **Yes.** First-class C surface in `include/rtc/rtc.h` + `DOC.md`. |
| Can Unity P/Invoke the official C API directly? | **Yes, technically.** Handles are `int` IDs; exports are `extern "C"`. |
| Is a thin native C wrapper still justified? | **Yes for product ABI**, not because the official C API is missing features. Justify it for: Unity main-thread event marshaling, freezing a subset ABI against 0.x churn, safer string/buffer ownership, and optional queueing so managed code never blocks native thread-pool callbacks. |

---

## Primary sources

| Source | URL / path |
|---|---|
| C API header | https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/include/rtc/rtc.h |
| C API docs | https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/DOC.md |
| Version macros | https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/include/rtc/version.h |
| C example (copy-paste) | https://github.com/paullouisageneau/libdatachannel/tree/v0.24.5/examples/copy-paste-capi |
| C API implementation | https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/src/capi.cpp |
| Callback dispatch (`Processor` → thread pool) | https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/src/impl/processor.hpp |
| PC state/candidate triggers | https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/src/impl/peerconnection.cpp |
| README (C bindings, platforms) | https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/README.md |
| CMake `CAPI_STDCALL` option | https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/CMakeLists.txt |
| Official site / mirrored docs | https://libdatachannel.org/ (reference pages mirror `DOC.md`) |
| Third-party Unity ref (not a dependency) | https://github.com/hanseuljun/datachannel-unity |
| Rust / Node bindings (patterns) | https://github.com/lerouxrgd/datachannel-rs , https://github.com/murat-dogan/node-datachannel |

Local clone used for source inspection: tag `v0.24.5` (detached HEAD).

---

## 1. PeerConnection lifecycle, SDP, candidates

### Create / close / destroy

```c
int rtcCreatePeerConnection(const rtcConfiguration *config); // returns pc id, or negative error
int rtcClosePeerConnection(int pc);
int rtcDeletePeerConnection(int pc);
```

- **Handle model:** opaque **integer IDs**, not raw pointers. Same pattern for DataChannel/Track/WebSocket. Good for P/Invoke and cross-boundary identity.
- `rtcDeletePeerConnection` implicitly closes if needed, then **blocks until scheduled callbacks for that PC return** (except if called *from* such a callback). After return, `pc` is invalid.
- Global lifecycle helpers: `rtcPreload()`, `rtcCleanup()`; cleanup must **never** be called from a callback.
- Opaque context: `rtcSetUserPointer(int id, void *ptr)` / `rtcGetUserPointer(int id)` — user pointer is passed as the last argument of every object callback. Child DC inherits PC’s user pointer at creation.

### Configuration (`rtcConfiguration`)

From `include/rtc/rtc.h` (v0.24.5):

```c
typedef struct {
    const char **iceServers;
    int iceServersCount;
    const char *proxyServer; // libnice only
    const char *bindAddress; // libjuice only, NULL means any
    rtcCertificateType certificateType;
    rtcTransportPolicy iceTransportPolicy;
    bool enableIceTcp;
    bool enableIceUdpMux; // libjuice only
    bool disableAutoNegotiation;
    bool forceMediaTransport;
    uint16_t portRangeBegin; // 0 = automatic
    uint16_t portRangeEnd;   // 0 = automatic
    int mtu;                 // <= 0 automatic
    int maxMessageSize;      // <= 0 default
} rtcConfiguration;
```

Layout of this struct has been **stable from v0.22.0 through v0.24.5** (field order/types unchanged; only comments/behavior for ICE-TCP evolved with libjuice).

**P/Invoke note:** C `bool` is 1 byte; no `#pragma pack` in the header — natural alignment. C# must use `[StructLayout(LayoutKind.Sequential)]` carefully (or avoid passing the full struct from managed code — see §6).

### SDP offer / answer / remote description

Recommended flow (matches official C example `examples/copy-paste-capi/offerer.c`):

1. Register callbacks (`rtcSetLocalDescriptionCallback`, `rtcSetLocalCandidateCallback`, state/gathering).
2. Create DC as offerer (`rtcCreateDataChannel`) **or** set remote offer as answerer.
3. Local SDP is delivered asynchronously via **callback**, not as a return value of create.

Key functions:

```c
// Initiate local description (type may be NULL → automatic). Does NOT take an SDP string.
int rtcSetLocalDescription(int pc, const char *type);

// Apply remote SDP (type may be NULL; DOC: automatic type "not recommended")
int rtcSetRemoteDescription(int pc, const char *sdp, const char *type);

// Trickle ICE
int rtcAddRemoteCandidate(int pc, const char *cand, const char *mid);

// Poll/get helpers (buffer + size; NULL buffer returns needed size including NUL)
int rtcGetLocalDescription(int pc, char *buffer, int size);
int rtcGetRemoteDescription(int pc, char *buffer, int size);
int rtcGetLocalDescriptionType(int pc, char *buffer, int size);
int rtcGetRemoteDescriptionType(int pc, char *buffer, int size);

// Niche: generate SDP without setting it (DOC: useless before rtcSetLocalDescription for normal use)
int rtcCreateOffer(int pc, char *buffer, int size);
int rtcCreateAnswer(int pc, char *buffer, int size);
```

**Auto-negotiation (default):** unless `disableAutoNegotiation == true`:

- After `rtcCreateDataChannel`, library calls `rtcSetLocalDescription` internally (offerer path).
- After `rtcSetRemoteDescription` with a remote **offer**, library answers automatically.

**Local description callback:**

```c
typedef void (*rtcDescriptionCallbackFunc)(int pc, const char *sdp, const char *type, void *ptr);
int rtcSetLocalDescriptionCallback(int pc, rtcDescriptionCallbackFunc cb);
```

**Local candidate callback (trickle):**

```c
typedef void (*rtcCandidateCallbackFunc)(int pc, const char *cand, const char *mid, void *ptr);
int rtcSetLocalCandidateCallback(int pc, rtcCandidateCallbackFunc cb);
```

**Add remote candidate:** `cand` is SDP candidate string with or without `a=` prefix; `mid` may be `NULL` for autodetection. Requires remote description already set.

### Other PC helpers useful for diagnostics

- `rtcGetLocalAddress` / `rtcGetRemoteAddress` — `"IP:PORT"` of selected candidates (may fail if not `RTC_CONNECTED`)
- `rtcGetSelectedCandidatePair`
- `rtcIsNegotiationNeeded`
- `rtcGetMaxDataChannelStream` / `rtcGetRemoteMaxMessageSize`

### Error codes (all APIs)

```c
#define RTC_ERR_SUCCESS   0
#define RTC_ERR_INVALID  -1
#define RTC_ERR_FAILURE  -2
#define RTC_ERR_NOT_AVAIL -3
#define RTC_ERR_TOO_SMALL -4
```

Create functions return **positive ID or negative error**.

---

## 2. DataChannel: create, reliability, send/recv, close

### Create

```c
int rtcCreateDataChannel(int pc, const char *label);
// equivalent to Ex with ordered+reliable, non-negotiated, auto stream id, empty protocol

int rtcCreateDataChannelEx(int pc, const char *label, const rtcDataChannelInit *init);

typedef struct {
    bool unordered;
    bool unreliable;
    unsigned int maxPacketLifeTime; // ignored if reliable
    unsigned int maxRetransmits;    // ignored if reliable
} rtcReliability;

typedef struct {
    rtcReliability reliability;
    const char *protocol; // empty if NULL
    bool negotiated;
    bool manualStream;
    uint16_t stream; // 0–65534 if manualStream
} rtcDataChannelInit;
```

| Setting | Meaning (DOC) |
|---|---|
| `unordered == false` | Ordered (default) |
| `unordered == true` | No ordering |
| `unreliable == false` | Reliable (default) |
| `unreliable == true` | Partial reliability |
| `maxPacketLifeTime` | ms window for tx/re-tx when unreliable; if non-zero takes precedence path |
| `maxRetransmits` | if unreliable **and** `maxPacketLifeTime == 0`: max retransmits (`0` = no retransmit) |
| `negotiated` / `manualStream` / `stream` | Out-of-band negotiated channels |

Incoming remote channels: `rtcSetDataChannelCallback(int pc, rtcDataChannelCallbackFunc cb)`  
Signature: `void cb(int pc, int dc, void *user_ptr)` — library assigns a new `dc` id.

### Channel common API (DC / Track / WebSocket share)

```c
int rtcSetOpenCallback(int id, rtcOpenCallbackFunc cb);
int rtcSetClosedCallback(int id, rtcClosedCallbackFunc cb);
int rtcSetErrorCallback(int id, rtcErrorCallbackFunc cb);
int rtcSetMessageCallback(int id, rtcMessageCallbackFunc cb);

int rtcSendMessage(int id, const char *data, int size);
// size >= 0 → binary of length size
// size < 0  → null-terminated UTF-8 string

int rtcClose(int id);
int rtcDelete(int id);          // or rtcDeleteDataChannel(int dc)
bool rtcIsOpen(int id);
bool rtcIsClosed(int id);

int rtcMaxMessageSize(int id);  // header symbol; DOC also names rtcGetMaxMessageSize
int rtcGetBufferedAmount(int id);
int rtcSetBufferedAmountLowThreshold(int id, int amount);
int rtcSetBufferedAmountLowCallback(int id, rtcBufferedAmountLowCallbackFunc cb);

// Poll path (only if MessageCallback is NOT set)
int rtcReceiveMessage(int id, char *buffer, int *size);
int rtcGetAvailableAmount(int id);
int rtcSetAvailableCallback(int id, rtcAvailableCallbackFunc cb);
```

**Message callback size convention** (critical for C#):

```c
typedef void (*rtcMessageCallbackFunc)(int id, const char *message, int size, void *ptr);
```

- Binary: `size >= 0` is byte length.
- String: `size` is **negative** (`-int(strlen+1)` in `capi.cpp`), pointer is NUL-terminated UTF-8.

**Send buffering:** messages may be buffered under congestion control; use `rtcGetBufferedAmount` + low-threshold callback for backpressure.

**Inspectors:**

```c
int rtcGetDataChannelStream(int dc);
int rtcGetDataChannelLabel(int dc, char *buffer, int size);
int rtcGetDataChannelProtocol(int dc, char *buffer, int size);
int rtcGetDataChannelReliability(int dc, rtcReliability *reliability);
```

### Minimal offerer sequence (from official example)

```c
rtcConfiguration config;
memset(&config, 0, sizeof(config));
int pc = rtcCreatePeerConnection(&config);
rtcSetUserPointer(pc, peer);
rtcSetLocalDescriptionCallback(pc, descriptionCallback);
rtcSetLocalCandidateCallback(pc, candidateCallback);
rtcSetStateChangeCallback(pc, stateChangeCallback);
rtcSetGatheringStateChangeCallback(pc, gatheringStateCallback);

int dc = rtcCreateDataChannel(pc, "test"); // triggers auto local offer if auto-neg on
rtcSetOpenCallback(dc, openCallback);
rtcSetClosedCallback(dc, closedCallback);
rtcSetMessageCallback(dc, messageCallback);

// later, from signaling:
rtcSetRemoteDescription(pc, sdp, "answer");
rtcAddRemoteCandidate(pc, candidate, NULL);
rtcSendMessage(dc, message, -1); // string

// teardown:
rtcDeleteDataChannel(dc);
rtcDeletePeerConnection(pc);
```

Source: `examples/copy-paste-capi/offerer.c` @ v0.24.5.

---

## 3. ICE / connection state callbacks and threading

### State enums (`rtc.h`)

```c
typedef enum {
    RTC_NEW = 0, RTC_CONNECTING = 1, RTC_CONNECTED = 2,
    RTC_DISCONNECTED = 3, RTC_FAILED = 4, RTC_CLOSED = 5
} rtcState;

typedef enum {
    RTC_ICE_NEW = 0, RTC_ICE_CHECKING = 1, RTC_ICE_CONNECTED = 2,
    RTC_ICE_COMPLETED = 3, RTC_ICE_FAILED = 4,
    RTC_ICE_DISCONNECTED = 5, RTC_ICE_CLOSED = 6
} rtcIceState;

typedef enum {
    RTC_GATHERING_NEW = 0,
    RTC_GATHERING_INPROGRESS = 1,
    RTC_GATHERING_COMPLETE = 2
} rtcGatheringState;

typedef enum {
    RTC_SIGNALING_STABLE = 0,
    RTC_SIGNALING_HAVE_LOCAL_OFFER = 1,
    RTC_SIGNALING_HAVE_REMOTE_OFFER = 2,
    RTC_SIGNALING_HAVE_LOCAL_PRANSWER = 3,
    RTC_SIGNALING_HAVE_REMOTE_PRANSWER = 4,
} rtcSignalingState;
```

### PC callback setters

```c
int rtcSetStateChangeCallback(int pc, rtcStateChangeCallbackFunc cb);
int rtcSetIceStateChangeCallback(int pc, rtcIceStateChangeCallbackFunc cb);
int rtcSetGatheringStateChangeCallback(int pc, rtcGatheringStateCallbackFunc cb);
int rtcSetSignalingStateChangeCallback(int pc, rtcSignalingStateCallbackFunc cb);
```

DOC documents `RTC_CONNECTING|CONNECTED|DISCONNECTED|FAILED|CLOSED` for PC state and `RTC_GATHERING_INPROGRESS|COMPLETE` for gathering (header also has `RTC_NEW` / `RTC_GATHERING_NEW`).

### Are callbacks off the main thread?

**Yes — treat all libdatachannel callbacks as concurrent / non-Unity-main-thread.**

Evidence from source (v0.24.5):

1. **Global thread pool**  
   - `rtcSetThreadPoolSize(unsigned int count)` with comment *“Applied when threads are spawned”* (`rtc.h`).  
   - Default pool size is `max(hardware_concurrency, MIN_THREADPOOL_SIZE=2)` (`src/impl/init.cpp`, `src/impl/internals.hpp`).

2. **Per-PeerConnection ordered processor**  
   - `impl::Processor` enqueues work onto `ThreadPool` and runs tasks in order (`src/impl/processor.hpp`).  
   - Local description, local candidates, most state transitions are scheduled via `mProcessor.enqueue(&PeerConnection::trigger<...>, ...)` (`src/impl/peerconnection.cpp`).

3. **State change example**  
   - Non-closed transitions: async via processor/thread pool.  
   - Transition to `Closed`: callback may run **synchronously** on the closing path (`changeState` steals and calls callback inline for `Closed`).

4. **Channel open/message/closed**  
   - `Channel::triggerOpen/Closed/Error` and `flushPendingMessages` invoke user callbacks directly on whatever thread reached `incoming` / open path (`src/impl/channel.cpp`) — typically network/SCTP/thread-pool related, **not** a Unity main loop.

5. **DOC lifecycle warnings**  
   - `rtcDelete*` / `rtcCleanup` block until scheduled callbacks return; never call cleanup from a callback. This only makes sense if callbacks run on library-owned threads.

**Binding implication (Unity):**

- Do **not** touch `UnityEngine` objects inside native callbacks.
- Prefer: native callback → lock-free/concurrent queue of owned copies (SDP/candidate/message bytes) → `SynchronizationContext` / main-thread pump in C#.
- A thin C wrapper that copies strings into heap buffers and enqueues events is the cleanest place for that boundary.

**Calling convention:** callbacks use `RTC_API`. Default is **cdecl**. Optional CMake `-DCAPI_STDCALL=ON` makes callbacks `__stdcall` on Windows — P/Invoke delegates must match the built library.

---

## 4. STUN / TURN configuration (credentials)

Configured **only** via `rtcConfiguration.iceServers` / `iceServersCount` at **PC create** time (URI strings).

### URI format (DOC.md)

```
[("stun"|"turn"|"turns") (":"|"://")]
[username ":" password "@"]
hostname[":" port]
["?transport=" ("udp"|"tcp"|"tls")]
```

Defaults:

| Piece | Default |
|---|---|
| Scheme | STUN |
| Port | 3478 (5349 over TLS) |
| Transport | UDP |

Examples from DOC:

- STUN: `mystunserver.org`
- TURN with password: `turn:myuser:12345678@turnserver.org`

**Credential encoding:** if username/password contain reserved characters, percent-encode; especially `:` → `%3A`, `@` → `%40`.

**Transport notes:**

- `?transport=tcp|tls` only for TURN with **libnice** backend; they govern TURN **control** connection; **relaying is always UDP**.
- Default ICE backend for libdatachannel is **libjuice** (built-in); libnice is optional. `proxyServer` is **libnice only**; `bindAddress` / `enableIceUdpMux` are **libjuice only**.
- `iceTransportPolicy`: `RTC_TRANSPORT_POLICY_ALL` (default) or `RTC_TRANSPORT_POLICY_RELAY` (relay-only candidates).
- `enableIceTcp`: generate TCP ICE candidates (ICE-TCP support expanded with libjuice in 0.24.x).

There is **no** separate C API to add/remove ICE servers after `rtcCreatePeerConnection`. Reconfigure by creating a new PC.

---

## 5. Recommended version pin and ABI/API stability

### Pin recommendation

| Item | Recommendation |
|---|---|
| **Pin** | **`v0.24.5`** (latest stable release as of research date; published 2026-06-12) |
| **Why** | Current patch of the 0.24 line; includes ICE-TCP via libjuice (0.24.0) and subsequent fixes |
| **Version macros** | `RTC_VERSION_MAJOR/MINOR/PATCH`, `RTC_VERSION` in `include/rtc/version.h` |
| **Upgrade policy** | Pin **exact git tag** in native build; treat **minor (0.x → 0.y)** as potentially breaking; re-run binding tests on every bump |

### Stability facts

- Project is still **0.x**. No published formal ABI stability / semver contract for the C API beyond “this is the C API” (`DOC.md` + `rtc.h`).
- README markets **C bindings** as first-class (platforms: Linux, Android, FreeBSD, macOS, iOS, Windows).
- Practical continuity: core PC/DC structs and functions used for data channels have been **consistent across 0.22–0.24** for `rtcConfiguration` / `rtcReliability` / `rtcDataChannelInit` field layout.
- Minor releases **do** add C symbols (e.g. `rtcCreateOffer`/`rtcCreateAnswer` in 0.23; media packetizer expansions; ICE features). Additive changes are common; field reordering has not been observed recently but is not guaranteed.
- Build-time feature flags change the header surface:
  - `RTC_ENABLE_MEDIA` (default 1)
  - `RTC_ENABLE_WEBSOCKET` (default 1)
  - For a DC-only Unity plugin, compile **with media/websocket disabled** if possible to shrink deps — but then do not expose those symbols from P/Invoke.
- Windows export: `RTC_C_EXPORT` uses `dllimport`/`dllexport`; static builds define `RTC_STATIC`.
- **MPL-2.0**: dynamic linking of unmodified library is the usual distribution path for Unity plugins; keep license notice in package.

### What “ABI stability” means here

| Layer | Stability |
|---|---|
| Official `rtc.h` C API | Mature and usable; **not** a frozen product ABI (0.x). |
| Binary layout of structs with `bool` / pointers | Platform-dependent; fragile if C# redefines full structs. |
| Integer handle IDs + function entry points | Relatively friendly for P/Invoke **when pinned to one build**. |

---

## 6. Thin native C wrapper vs direct P/Invoke of official C API

### Official C API already is a C ABI

Strengths for direct P/Invoke:

- `extern "C"` + `RTC_C_EXPORT`
- Opaque `int` handles (no C++ name mangling, no vtables)
- Callback function pointers with `user_ptr`
- Buffer+size string getters (can query size with `NULL` buffer)
- Documented error codes
- Official C samples prove the surface is intentional

Direct P/Invoke is **enough to prototype** PC+DC+ICE.

### Why a thin native wrapper is still justified for the UPM package

Parent map (#1) asks for a **stable C ABI export layer** for Unity. That is still the right product decision:

| Concern | Direct P/Invoke of `rtc.h` | Thin wrapper (`juice_dc_*` / `dcunity_*`) |
|---|---|---|
| Upstream 0.x renames/adds fields | C# `StructLayout` and DllImport must track every pin | Wrapper freezes **your** symbols; rebind inside native on upgrade |
| `rtcConfiguration` with `const char**` iceServers | Awkward/unsafe from C# (pinned string arrays, lifetime) | C# passes simple STUN/TURN arrays; native builds URI list |
| Callbacks on thread pool | Must be perfect on first try (GCHandle, no Unity API) | Wrapper copies payload → MPSC queue; C# polls on main thread |
| String lifetime in callbacks | `const char*` valid only during callback | Wrapper strdup / byte buffer owned until dequeued |
| Subset surface (no media/WS in v1) | Easy to accidentally bind unused APIs | Export only PC+DC+ICE config |
| Symbol / soname control | Depends on `libdatachannel` naming per platform | Single plugin name `datachannel` / `libdatachannel_unity` |
| Future wasm / dual backend | Harder to swap | Same C ABI can front native lib or Emscripten glue |

**Conclusion:**  
- **Feature coverage:** official C API is sufficient; **no need to wrap C++ headers**.  
- **Product ABI:** keep a **thin** C wrapper (or C++ file exporting pure C) that:
  1. Calls official `rtc_*` APIs only.
  2. Owns ICE server URI construction and `rtcConfiguration` zero-init.
  3. Marshals callbacks onto a thread-safe event queue (optional but strongly recommended for Unity).
  4. Versions its own `dc_api_version()` independently of `RTC_VERSION`.

Avoid a thick re-implementation; 1:1 pass-through plus queue + string helpers is enough.

### Suggested minimal export set for v1 (wrapper or curated P/Invoke)

```
// lifecycle
InitLogger, Preload, Cleanup, SetThreadPoolSize
// pc
CreatePeerConnection(config|ice_uri_list), ClosePC, DeletePC
SetLocalDescription, SetRemoteDescription, AddRemoteCandidate
GetLocal/RemoteDescription (or only callbacks)
// pc callbacks → events
OnLocalDescription, OnLocalCandidate, OnConnectionState, OnIceState, OnGatheringState, OnDataChannel
// dc
CreateDataChannel / CreateDataChannelEx, DeleteDataChannel
Send (binary), Close
GetLabel / IsOpen
// dc callbacks → events
OnOpen, OnClosed, OnMessage, OnError, OnBufferedAmountLow
// pump (if queued)
PollEvent / DrainEvents
```

Media, WebSocket, Track APIs: **out of scope for v1** (matches map #1).

---

## Concrete mapping: research questions → answers

1. **PC create/destroy, SDP, remote candidate**  
   `rtcCreatePeerConnection` / `rtcClosePeerConnection` / `rtcDeletePeerConnection`; local SDP via `rtcSetLocalDescription` + `rtcSetLocalDescriptionCallback`; remote via `rtcSetRemoteDescription`; trickle via `rtcAddRemoteCandidate` + `rtcSetLocalCandidateCallback`.

2. **DataChannel reliability / unordered / send / recv / close**  
   `rtcCreateDataChannel` / `Ex` + `rtcReliability` (`unordered`, `unreliable`, `maxPacketLifeTime`, `maxRetransmits`); `rtcSendMessage` / `rtcSetMessageCallback` or poll `rtcReceiveMessage`; `rtcClose` / `rtcDeleteDataChannel`.

3. **ICE / gathering callbacks; thread?**  
   Full set of state/ICE/gathering/signaling callbacks exists. Callbacks are delivered from **library thread pool / processor** (not Unity main thread). Design for concurrency.

4. **STUN/TURN credentials**  
   URI list in `rtcConfiguration.iceServers` with embedded `user:pass@`; percent-encoding rules documented; set at create only.

5. **Version pin**  
   Pin **`v0.24.5`**; expect additive churn in 0.x; re-validate bindings on upgrade.

6. **Wrapper vs direct P/Invoke**  
   Official C API is complete for v1 features; **still prefer a thin native export layer** for Unity-safe threading, ICE URI assembly, and ABI freeze — not for missing peerconnection/datachannel functionality.

---

## Gaps / non-issues for v1

| Topic | Status |
|---|---|
| Media / Tracks | Available in C API; **exclude** from v1 package surface |
| WebSocket | Available; **exclude** |
| Changing ICE servers on live PC | Not supported; recreate PC |
| Explicit “main thread” guarantee | **None** in DOC; source shows opposite |
| Formal ABI freeze from upstream | **None**; pin tag + own wrapper version |
| TURN TCP/TLS control | Backend-dependent (libnice); relaying still UDP |

---

## References (quick links)

- Header: https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/include/rtc/rtc.h  
- Docs: https://github.com/paullouisageneau/libdatachannel/blob/v0.24.5/DOC.md  
- C example: https://github.com/paullouisageneau/libdatachannel/tree/v0.24.5/examples/copy-paste-capi  
- Release v0.24.5: https://github.com/paullouisageneau/libdatachannel/releases/tag/v0.24.5  
- Thread pool API: `rtcSetThreadPoolSize` in `rtc.h`; implementation `src/impl/processor.hpp`, `src/impl/init.cpp`  
- Callback wiring: `src/capi.cpp` (`rtcSet*Callback` lambdas)
