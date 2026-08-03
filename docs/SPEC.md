# datachannel-unity — Implementation Specification (v1)

**Status:** Ready to implement (implementation-hardening decisions folded in 2026-08-03)  
**Maps:** [#1 UPM 架构与平台规格](https://github.com/xuhuanhello/juice-c-sharp/issues/1) · [#16 原生打包 hardening](https://github.com/xuhuanhello/juice-c-sharp/issues/16) · [#26 实现层 hardening](https://github.com/xuhuanhello/juice-c-sharp/issues/26)  
**Package path:** `Packages/datachannel-unity`  
**Package id:** `com.xuhuanhello.datachannel`  
**Unity baseline:** 2022.3 LTS (reference project: 2022.3.62f3)  
**Glossary:** [`CONTEXT.md`](../CONTEXT.md)  
**Research:** [`docs/research/`](./research/)

This document consolidates all closed wayfinder decisions from the three maps above. Implementation must follow it; expanding scope requires a new decision, not silent drift.

> **Read §2 first.** It carries a hard sequencing constraint — the libdatachannel **C++ API migration must land before** the ownership / event-ABI / error-code / lifecycle work in §4 and §6. Doing it afterwards costs 2–3× as much.

**Parts of this spec describe a target state the code has not reached yet.** Where current sources contradict the spec, the spec wins and the code is wrong; §14 gives the order in which to close the gap.

---

## 1. Product boundary

### In scope (v1)

- Open-source UPM package with:
  - Stable C ABI (`dcu_*`) + P/Invoke + thin idiomatic C# API
  - Prebuilt native plugins for the platform matrix (§6)
  - PeerConnection + DataChannel only (P2P data path)
- Application-supplied **signaling transport** (SDP / ICE candidates) and **ICE server config** (STUN/TURN URLs)
- **CMake-only** native build orchestrating upstream CMake projects (§9)
- CI (GitHub Actions) producing plugins; maintainers commit via **Git LFS**

### Out of scope (v1)

| Item | Notes |
|------|--------|
| Signal **server** implementation / protocol | App owns signaling |
| TURN **server** | Configure only via IceServer |
| FishNet / game netcode transports | Separate effort |
| libjuice-only ICE binding | Superseded by libdatachannel |
| Media (audio/video tracks) | — |
| WebSocket client/server bindings | — |
| Meson (as build system or orchestrator) | Removed by [#27](https://github.com/xuhuanhello/juice-c-sharp/issues/27); see §9 |
| Inbound rate limiting / malicious-peer defence | Belongs at a connection layer that can react per peer, not in the frame loop ([#38](https://github.com/xuhuanhello/juice-c-sharp/issues/38)); separate effort |
| Full shippable multiplayer game | This map ends at implementable package spec |
| HarmonyOS | Extension placeholder only (§16) |

**Reference only (do not fork):** [Walkerdine/dc-unity](https://github.com/Walkerdine/dc-unity) — see `docs/research/dc-unity-autopsy.md`.

---

## 2. Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ C#  DataChannelUnity.*  (events + optional observers)       │
│  PlayerLoop pump: control drain + per-channel message pull  │
│  IDisposable PeerConnection (owns) → DataChannel            │
└───────────────────────────┬─────────────────────────────────┘
                            │ P/Invoke dcu_* only
┌───────────────────────────▼─────────────────────────────────┐
│ Stable C ABI (project-owned)                                │
│  own handle table; queue control + log events;              │
│  copy into caller buffers; no managed callbacks             │
└─────────────┬───────────────────────────────┬───────────────┘
              │ native: C++ API               │ WebGL
              ▼                               ▼
     libdatachannel v0.24.5          datachannel-wasm v0.4.0
     (+ libjuice ICE, MbedTLS)       (+ browser WebRTC + webrtc.jslib)
```

- **One** `dcu_*` surface for all platforms.
- WebGL must **not** embed libdatachannel as a normal UDP stack; use browser WebRTC via datachannel-wasm + C facade.
- Do **not** re-export upstream `rtc*` symbols from the plugin.

### The dcu layer is built on libdatachannel's C++ API

**Decision:** [#41](https://github.com/xuhuanhello/juice-c-sharp/issues/41) (research), [#42](https://github.com/xuhuanhello/juice-c-sharp/issues/42)

`native/dcu` consumes `rtc::PeerConnection` / `rtc::DataChannel` / `rtc::IceServer` directly and **owns its own handle table**; it does not go through `rtcCreatePeerConnection` and the `int` handles registered inside `src/capi.cpp`.

This is an **implementation choice, not an ABI change** — `dcu.h` and the whole C# side are unaffected by it. What it buys:

| | |
|--|--|
| Structured ICE credentials | `rtc::IceServer` takes `username` / `password` as fields; no URI string assembly, no percent-encoding (§5, §7) |
| Child-DataChannel cleanup | `rtcDeletePeerConnection` does **not** erase the PC's DataChannel map entries (`capi.cpp:437-444`); that leak is unfixable from the C route because `dataChannelMap` is upstream-private |
| Faithful errors | 12 lines of exception mapping, more accurate than `capi.cpp`'s `wrap()` (which has no `catch(...)` and flattens the code) |
| Label handling | `dc->label()` returns `std::string` — the fixed-buffer + `RTC_ERR_TOO_SMALL` path disappears entirely |

Non-issues, verified: `RTC_CPP_EXPORT` expands to nothing under `RTC_STATIC` (`include/rtc/common.hpp:12-24`), so it does not conflict with the `dcu_*`-only export allowlist; symbol bloat is independent of the API choice.

> **Sequencing constraint (normative).** The migration **must be completed before** the ownership (#29), event-ABI (#30), error-code (#31) and lifecycle (#37) work is implemented. Those four already require rewriting most of `dcu_impl.cpp`; riding along costs ~2–3 person-days (net ≈ +70/−63 lines), whereas migrating after they land costs 5–8 and drags the tests with it. If implementation is postponed indefinitely, re-evaluate — a rewrite with nobody following it is pure cost.

**Invariants the migration must not break** (each fails *silently*):

1. **Incoming DataChannels must have `onOpen` wired inside the `onDataChannel` callback body.** Upstream implements "register-then-replay" through call ordering: `triggerPendingDataChannels` invokes the data-channel callback and only then `triggerOpen`, with `resetOpenCallback()` clearing `mOpenTriggered` first (`impl/peerconnection.cpp:1274,1288-1302`). Deferring the wiring loses every incoming `DcOpen` and nothing reports an error.
2. **Keep `peek()` / `receive()` as the receive pair.** `rtcReceiveMessage` peeks, copies, and discards **only on success** (`capi.cpp:878-899`) — that is exactly the "buffer too small ⇒ do not consume" contract of §4. The intuitive C++ rewrite (`receive()` then copy) **drops the message** when the caller's buffer is short, which on a reliable channel is a protocol violation.
3. **The outgoing-only open re-check** (§4, `DcOpen`) must not be added to the incoming path — it would reorder events.

**Known regression:** the compile-time `static_assert` gate on upstream enum values (§4) is impossible on the C++ route, since it raises exception types rather than returning `RTC_ERR_*` values. It is replaced by runtime contract tests (§11). This is the migration's only substantive downside.

**One assumption deliberately left unverified:** whether browsers reject `turn:user:pass@host`-style userinfo. It was *not* load-bearing for this decision; do not later assume it was checked.

### Backend shape is shared in name only

The native and WebGL backends share the `dcu_*` surface and the ICE-configuration shape (`rtc::IceServer`'s structured constructor is identical in both trees). They do **not** share the data path: datachannel-wasm's `rtc::Channel` has no `receive()` / `peek()` / `availableAmount()` and **no receive queue at all** — a message with no callback attached is dropped (`wasm/src/channel.cpp:73-75`). Coverage is roughly 13/30 `PeerConnection` methods, 10/15 `Channel`, 1/17 `Configuration`. Consequences for back-pressure are in §8.

---

## 3. Upstream pins and versioning

**Decision:** [#11](https://github.com/xuhuanhello/juice-c-sharp/issues/11)

| Component | Pin | Notes |
|-----------|-----|--------|
| libdatachannel | **`v0.24.5`** | git tag |
| datachannel-wasm | **`v0.4.0`** | git tag |
| Transitive (libjuice, usrsctp, …) | Follow pinned trees | No floating `latest` |
| Crypto | **Static into plugin** — **Mbed TLS 3.6.x** (`mbedtls=v3.6.7` in lock), built from `subprojects/` with a user config enabling `MBEDTLS_SSL_DTLS_SRTP`; **must not** load system/Homebrew OpenSSL or MbedTLS **dylibs** | brew `mbedtls` 4.x is incompatible with libdatachannel v0.24.5. The historical OpenSSL escape hatch is **gone** — the script that used it was deleted (#27) and the CI step that installed brew OpenSSL was removed (#36); it had been contradicting this row all along. No dual *shipping* matrix of backends. |
| Unity | **2022.3 LTS** | Reference 2022.3.62f3 |
| WebGL toolchain | **Unity-bundled Emscripten** | 2022.3 → **3.1.8-unity**; rebuild on Unity major bumps |

**Reproduce builds** via `native/versions.lock` consumed by CI and local scripts.

### Linking & symbols (map #16)

| Rule | Requirement |
|------|-------------|
| Self-contained | All crypto + usrsctp + juice + libdatachannel **static** into the single plugin binary |
| No device crypto drift | Post-build audit: `otool -L` / `ldd` / `dumpbin` must not list openssl/mbedtls from `/opt/homebrew`, `/usr/local`, or vcpkg shared trees |
| Export surface | **Only `dcu_*`** (Apple: `native/exports/macos-exported-symbols.txt`; ELF: `linux-version-script.map`; Windows: `windows-exports.def`) |
| Compile | `-fvisibility=hidden` (+ inlines hidden) on wrapper and deps where possible |
| macOS product | **One** artifact per arch: `datachannel_unity.bundle` only — **no** side-by-side `.dylib` |

**Decisions:** [#17](https://github.com/xuhuanhello/juice-c-sharp/issues/17), [#18](https://github.com/xuhuanhello/juice-c-sharp/issues/18), [#19](https://github.com/xuhuanhello/juice-c-sharp/issues/19)

### Package semver (this UPM)

| Change | Bump |
|--------|------|
| Upstream patch, no public/API/`dcu_*` break | patch |
| Upstream minor / behavior change, API compatible | minor |
| `dcu_*` or public C# break | major |
| Docs/samples only | patch or docs-only |

Maintainers only bump pins into `main` with rebuild + tests + CHANGELOG (old/new tags). External PRs that only change pins without rebuild evidence are rejected.

---

## 4. Stable C ABI (`dcu_*`)

**Decision:** [#7](https://github.com/xuhuanhello/juice-c-sharp/issues/7), logging extension [#12](https://github.com/xuhuanhello/juice-c-sharp/issues/12), create config [#9](https://github.com/xuhuanhello/juice-c-sharp/issues/9), event contract [#30](https://github.com/xuhuanhello/juice-c-sharp/issues/30), error codes and enums [#31](https://github.com/xuhuanhello/juice-c-sharp/issues/31), event semantics [#32](https://github.com/xuhuanhello/juice-c-sharp/issues/32), log queue [#33](https://github.com/xuhuanhello/juice-c-sharp/issues/33)

**ABI version: `DCU_ABI_VERSION` = 2.** Version 1 was the pre-hardening surface; the changes below are breaking, and since the package is unreleased there is no compatibility shim. Exported symbols: **19** (enumerated below and mirrored in `native/exports/expected-symbols.txt`, §11).

### Principles

| Rule | Value |
|------|--------|
| Prefix | `dcu_*` only; no `rtc*` export |
| Handles | Opaque `int32`, allocated by the dcu handle table, **never reused** |
| Calling convention | **Every function returns a status code; every value it produces travels through an out parameter.** No function's return value doubles as data |
| Events | Native enqueue; **single atomic** dequeue copying into caller-provided buffers |
| Messages | **Not** enqueued — pulled per channel (see *Control is pushed, data is pulled*) |
| Negotiation | **Always auto-negotiate** (no create_offer / set_local_description) |
| Payloads | Binary only: pointer + `len >= 0`; `len == 0` is legal |
| Pointer types | Text (`sdp` / `type` / `cand` / `mid` / `label`) is `const char*`; opaque bytes are `const void*` / `void*` |

The calling-convention rule replaces two earlier shapes that conflicted at `0`: handle-returning functions treated `0` as failure while count-returning functions treated it as a legal value. Nothing broke only because upstream's `lastId` happens to start at 1 — an implementation detail, not a contract. With `dcu_event_next` already forced into return-code + out-param form by its complexity, making the rest match is free consistency, and "did it succeed" collapses to a single spelling: `rc == DCU_OK`.

### Error codes

dcu error codes are **independently numbered** — deliberately *not* value-identical to `RTC_ERR_*`:

| Code | Value | Meaning |
|------|-------|---------|
| `DCU_OK` | `0` | Success |
| `DCU_ERR_INVALID` | `-101` | Caller passed something wrong; self-fixable |
| `DCU_ERR_FAILURE` | `-102` | Runtime failure |
| `DCU_ERR_NOT_AVAIL` | `-103` | Nothing to return right now |
| `DCU_ERR_TOO_SMALL` | `-104` | Caller buffer too small; see the retry contract below |
| `DCU_ERR_UPSTREAM_UNKNOWN` | `-105` | Upstream returned a code we do not recognise |

The old numbering matched `RTC_ERR_*` value-for-value, which turned every accidental `return rtcSomething(...)` passthrough into a *plausible-looking* dcu code — silently wrong. Under independent numbering the same leak surfaces as an undefined code and is visible at a glance. This also stops the ABI contract from being permanently welded to upstream's numbering habits.

**Fidelity is mandatory.** A single conversion function maps each upstream code to its dcu counterpart; **collapsing them into `FAILURE` is forbidden**, because the distinction lost is the most useful one (`INVALID` = "you can fix this yourself" vs `FAILURE` = "file an issue"). Unrecognised upstream codes map to `DCU_ERR_UPSTREAM_UNKNOWN`, never to `FAILURE`, and the raw value is carried out to the caller (§6, `RawCode`).

### Buffer-too-small retry contract

Applies to `dcu_event_next` and `dcu_dc_receive`:

> On `DCU_ERR_TOO_SMALL`, fill in the **required length** and **do not consume** the item (the event is not popped, the message is not discarded). Growing the buffer and retrying is idempotent.

This matches upstream (`capi.cpp:891-898` discards only when the copy succeeded), so the dcu layer needs no staging buffer of its own. Because the length written is exact and the single-consumer contract keeps the queue head still, **one retry always suffices**; a second `TOO_SMALL` means the single-consumer contract was violated, which is a bug to look at, not a condition to loop on.

### Return-value domains

`dcu_event_next` and `dcu_dc_receive` return **only** `DCU_OK` / `DCU_ERR_NOT_AVAIL` / `DCU_ERR_TOO_SMALL` / `DCU_ERR_INVALID`. This is verifiable by reading the function — there are no data-dependent failure paths.

> **Gate for future maintainers:** if either function ever gains another error code, that code's definition **must state whether it consumes the queue head**. (An earlier draft carried a broader "every error path must consume the head first" invariant; measurement showed the failure it guarded against cannot occur, and an invariant nobody can mechanically check is worse than none.)

### Control is pushed, data is pulled

| | Control events | Messages | Log lines |
|--|----------------|----------|-----------|
| Transport | dcu control queue | Upstream per-channel `mRecvQueue` | dcu log queue |
| Bound | **Unbounded** | 1024 msgs/channel (upstream `RECV_QUEUE_LIMIT`) | **1024, drop-oldest + counter** |
| On overflow | n/a — never dropped | `push` **blocks** ⇒ real back-pressure onto SCTP | Oldest dropped; count surfaced as one warning |
| Read by | `dcu_event_next` | `dcu_dc_receive`, per open channel | `dcu_log_next` |

No `rtcSetMessageCallback` is installed, so messages stay in upstream's queue (`impl/channel.cpp:64-68`: `flushPendingMessages` is `while (messageCallback)`). That is what makes the back-pressure real: when the application is slow, the queue fills, `push` blocks, and SCTP's receive window closes — the peer is forced to slow down. **Nothing is dropped**, which matters because dropping on a reliable channel would make `Reliable = true` a lie. The three queues have deliberately different policies: control events can never be dropped (a lost `DcClosed` desynchronises the managed state machine forever, with no recovery path), log lines can (§7).

The control queue is safe to leave unbounded precisely because data no longer flows through it: its growth rate is a function of the connection count, which the application controls, not of peer traffic.

**Polling, not readiness events.** No `DC_AVAILABLE` event exists; the pump walks open channels each frame and skips on `NOT_AVAIL`. The pump therefore holds **no state**. Upstream's `triggerAvailable` only fires on the empty→non-empty edge, so a readiness-event design would stall a channel until its next such edge — a bug class that is near-impossible to reproduce. Channel counts are single digits to a few dozen; a sub-microsecond P/Invoke per channel per frame is the cheaper trade.

**WebGL cannot honour the back-pressure guarantee** — see §8.

### Exported surface (19)

**Global**

| Symbol | Notes |
|--------|-------|
| `dcu_abi_version(int *out_version)` | |
| `dcu_init(void)` | Idempotent |
| `dcu_shutdown(int *out_undestroyed)` | Returns the count of **objects still alive** through the out parameter, maintained by the dcu layer itself (incremented on create, decremented on destroy). Upstream's `rtcCleanup()` returns `void` and swallows its own two most valuable diagnostics — "N objects were not properly destroyed" and "Cleanup timeout" — into plog (`capi.cpp:1754-1768`), so it currently reports success even when it deadlocks |
| `dcu_set_log_level(int level)` | §7; **never** detaches the log bridge |
| `dcu_log_next(...)` | Drains one bridged log line into caller buffers |
| `dcu_event_next(dcu_event_header *out_header, void *buf, int cap, void *buf2, int cap2)` | Single atomic dequeue: fills header + payloads and pops, or reports `TOO_SMALL` / `NOT_AVAIL` without popping |
| `dcu_event_queue_depth(int *out_depth)` | Read-only; feeds the backlog warning in §6 |

`dcu_init` / `dcu_shutdown` must not be called from inside event handling. There is no `dcu_is_inited` — the only thing that calls `dcu_shutdown` is domain teardown, which resets the managed statics in the same breath, so the two sides cannot disagree (§6).

**PeerConnection**

| Symbol | Notes |
|--------|-------|
| `dcu_pc_create(const dcu_pc_config *config, int *out_pc)` | Config per §5 |
| `dcu_pc_close(int pc)` / `dcu_pc_destroy(int pc)` | |
| `dcu_pc_set_remote_description(int pc, const char *sdp, int sdp_len, const char *type, int type_len)` | |
| `dcu_pc_add_remote_candidate(int pc, const char *cand, int cand_len, const char *mid, int mid_len)` | `mid` optional |
| `dcu_pc_create_data_channel(int pc, const char *label, int label_len, const dcu_dc_init *init, int *out_dc)` | Rejects labels > **65535** bytes (see *Label bound*) |

**DataChannel**

| Symbol | Notes |
|--------|-------|
| `dcu_dc_send(int dc, const void *data, int len)` | `len >= 0`; no open-state pre-check |
| `dcu_dc_close(int dc)` / `dcu_dc_destroy(int dc)` | |
| `dcu_dc_buffered_amount(int dc, int *out_amount)` | |
| `dcu_dc_state(int dc, int *out_state)` | Three-state live query: `Connecting` / `Open` / `Closed`, composed natively in one shot to avoid TOCTOU |
| `dcu_dc_receive(int dc, void *buf, int cap, int *out_len)` | Pull one message; `NOT_AVAIL` when empty |

### Event types (via `dcu_event_next`)

| Event | Payload | Notes |
|-------|---------|-------|
| `LocalDescription` | SDP + type | For app signaling |
| `LocalCandidate` | candidate + mid | Trickle |
| `ConnectionState` | mapped enum | See *State enums* |
| `GatheringState` | mapped enum | See *State enums* |
| `IncomingDataChannel` | dc handle + label | |
| `DcOpen` / `DcClosed` / `DcError` | dc handle (+ message) | See *Open/close semantics* |

`DcMessage` is **not** an event type — messages are pulled (above).

#### Open/close semantics

**Open state is a live query, not a cached flag.** `dcu_dc_state` is authoritative; `DcOpen` is a notification. This mirrors the browser's `readyState`, libwebrtc's `state()` and libdatachannel's `isOpen()`; of six reference bindings surveyed, only this project used to cache it, and that cache is what turned a missed notification into a permanently unusable channel. `dcu_dc_send` therefore performs **no** open-state pre-check.

**Outgoing channels re-check once after wiring.** A DataChannel created on an already-connected PeerConnection can open in the window between creation and callback registration (`Channel::onOpen` assigns, `triggerOpen` fires once, and is not sticky). After wiring, the outgoing path queries `isOpen()` once and synthesises `DcOpen`, de-duplicated by a per-channel atomic flag. **The incoming path must not do this** — there `mIsOpen` is already true while `mOpenTriggered` has just been reset, so a re-check would emit `DcOpen` *before* `IncomingDataChannel` and reverse the event order.

**`DcClosed` may arrive with no preceding `DcOpen`.** If a channel opens and closes inside the race window, only `DcClosed` is delivered. This is forced, not chosen: `isOpen()` is `!mIsClosed && mIsOpen`, and the C++ surface does not expose `mIsOpen`, so "opened then closed" is indistinguishable from "never opened". Synthesising `DcOpen` would be fabrication.

**Draining before close.** Before dispatching `DcClosed` for a channel, its receive queue must be drained; otherwise messages that arrived before the close are lost or reordered. The handle is still resolvable at that point.

#### Message semantics

- Text frames from the peer are delivered as their **UTF-8 bytes, transparently**; there is no separate text event (`String message dual-semantics` remains deferred, below). Interop friction is the application's to handle; the public surface stays minimal.
- **Embedded NUL bytes must not truncate.** The pull path carries a real size, unlike a `strlen`-based push path.
- **Zero-length messages are legal** (WebRTC permits them; they are a common heartbeat).

#### Label bound

DataChannel labels are limited to **65535 bytes**, validated in *both* the C# layer and the dcu layer.

This is upstream's real boundary made into a checked precondition, not an invented policy — 65535 was measured to work end-to-end and 65536 to fail. The failure it prevents is severe and entirely silent: for a channel created **before** the PeerConnection connects, an oversized label makes `open()` throw inside `iterateDataChannels`, where it is caught and reduced to one `PLOG_WARNING`. The caller gets a **positive handle**, the channel is neither open nor closed nor errored, `dcu_dc_send` **returns success**, the bytes go on the wire, and the peer — seeing traffic on a stream that never had a DCEP OPEN — closes it as a protocol violation. From the API's point of view everything succeeds until the moment the channel mysteriously closes. (Created *after* connect, the same label is properly rejected; the two paths fail differently, which is why both need test coverage — §11.)

### State enums

`ConnectionState` and `GatheringState` are mapped with an **explicit `switch`**, never cast through from upstream values:

- `static_assert` pins upstream's existing numeric values, so *renumbering* breaks the build. Since the upstream version is pinned (§3), that only fires when someone upgrades — exactly when the mapping should be re-read. **On the C++ API route this assert is not expressible; runtime contract tests replace it (§11).**
- The `switch`'s `default` catches *added* members, which no assert can. Unknown values map to an explicit `Unknown` member plus a warning. **`default` must never throw** — a reference binding that panics in an `extern "C"` callback on unknown enum values is exactly the failure mode to avoid.
- Dropping the event instead (leaving the app stuck on the previous state) or coercing to an existing member (lying) are both wrong.

The numeric values themselves stay aligned with upstream; with the `switch` in place that alignment is a checked invariant rather than a load-bearing coincidence.

### Deferred (must not export in v1)

- `rtc*` passthrough
- Manual offer/answer / `disable_auto_negotiation`
- Negotiated DC / manual stream id
- Separate IceState / SignalingState events
- bufferedAmountLow events
- Poll get_local/remote_description (locals already pushed as events)
- WebSocket / Media
- String message dual-semantics
- Direct C# function-pointer callbacks from native threads
- Per-channel available-byte queries (`rtcGetAvailableAmount` exists upstream; not needed while the pump drains)

Symbol names may be refined in implementation PRs; **surface expansion requires a new decision** and an update to `native/exports/expected-symbols.txt` in the same diff as the `DCU_ABI_VERSION` bump.

---

## 5. ICE / STUN / TURN configuration

**Decision:** [#9](https://github.com/xuhuanhello/juice-c-sharp/issues/9)

- Configure **only at PC create**; no runtime add/remove of ICE servers (recreate PC to change).
- Empty IceServer list allowed (host/local gathering only).

### IceServer (repeatable)

| Field | Meaning |
|-------|---------|
| `urls[]` | STUN/TURN URLs |
| `username` | Optional (TURN) |
| `credential` | Optional |

**Credentials are passed structurally, never assembled into a URL.** The dcu layer constructs `rtc::IceServer(url)` and assigns `username` / `password` as fields (all `rtc::IceServer` fields are public, and its URL constructor `url_decode`s any userinfo itself). The previous `build_ice_uri` / `percent_encode_userinfo` helpers are deleted.

**The reason is correctness, not secrecy.** Hand-assembling the URI carried three real bugs — a scheme-guessing fallback when the URL had no `://`, abandoning the whole assembly if the remainder contained `@`, and injecting credentials into `stun:` URLs where they have no meaning. Removing it *also* closes a leak path (upstream throws `"Invalid ICE server URL: " + url` on a malformed URL and logs it at `PLOG_ERROR`, above the release default level — with credentials in the string we put there), but that is a side effect. See §7 for the credential story end to end.

Applications may still embed credentials in `urls` themselves; the API does not forbid it, and log redaction covers that shape (§7).

**TURN credentials must not enter Unity assets.** `IceServer` is deliberately **not** `[Serializable]` (§6), so it cannot be filled in via the Inspector and cannot land in a `.unity` / `.prefab` file that ships inside the build and is trivially extracted. The intended source is the signaling server at runtime — commonly short-lived REST credentials. An application that genuinely wants Inspector-authored configuration must write its own DTO, and that extra step is exactly the point at which someone has to think about where the credential came from.

### Additional create fields

| Field | Semantics |
|-------|-----------|
| `transport_policy` | `All` \| `RelayOnly` |
| `port_range_begin` / `port_range_end` | `0` = automatic |
| `bind_address` | Optional; libjuice; **ignored on WebGL** |
| `enable_ice_tcp` | bool; WebGL may no-op |
| `enable_ice_udp_mux` | bool; libjuice; **ignored on WebGL** |
| `mtu` | `<= 0` automatic |
| `max_message_size` | `<= 0` default |

**Deferred:** `proxyServer` (libnice), `certificateType`, `forceMediaTransport`, sync reachability probe at create.

### Failure model

| Stage | Behavior |
|-------|----------|
| Config/create (sync) | Negative error / C# exception; **no** PC handle |
| ICE/connectivity (async) | PC exists; `ConnectionState` Failed/Disconnected etc. via pump |

---

## 6. C# public API and threading

**Decision:** [#8](https://github.com/xuhuanhello/juice-c-sharp/issues/8), ownership and lifetime [#29](https://github.com/xuhuanhello/juice-c-sharp/issues/29), error surface [#31](https://github.com/xuhuanhello/juice-c-sharp/issues/31), naming and config semantics [#34](https://github.com/xuhuanhello/juice-c-sharp/issues/34), domain reload [#37](https://github.com/xuhuanhello/juice-c-sharp/issues/37), pump [#38](https://github.com/xuhuanhello/juice-c-sharp/issues/38)

### Namespace and types

- Namespace: **`DataChannelUnity`**
- Types: `PeerConnection`, `DataChannel`, `IceServer`, `PeerConnectionConfig`, `DataChannelInit`, `ConnectionState`, `GatheringState`, `DataChannelState`, `DataChannelError`, `DataChannelException`, `LogLevel`, …
- Single Runtime asmdef; **no** UniRx/R3 package dependency
- `allowUnsafeCode` is **on and load-bearing** — the send path uses `fixed` (see *Send*). It is a deliberate build setting, not a leftover.
- Document that users may wrap events with `Observable.FromEvent` themselves

### What is public

**The public surface is a P2P API, not a pipe.** Anything whose only use is reaching through the abstraction is `internal`.

| Member | Visibility |
|--------|-----------|
| `PeerConnection.NativeHandle` / `DataChannel.NativeHandle` | **internal** — directly contradicts "application code never sees `dcu_*`"; every legitimate operation has a managed method, so exposing it only invites storing it, passing it around, and using it after `Dispose`. Diagnostics are served by `ToString()` |
| `DataChannelRuntime.RegisterPump()` / `UnregisterPump()` | **internal** — these implement a precise five-scenario lifecycle choreography plus liveness self-healing; leaving them public invites applications to fight it |
| `DataChannelRuntime.EnsureNative()` | **internal** — redundant with `IsNativeAvailable`, whose getter calls it |
| `DataChannelLog.RedactIceCredentials(string)` | **internal** — redaction is the library's job, not the caller's |
| `DataChannelRuntime.Pump()` / `IsNativeAvailable` / `AbiVersion`, `DataChannel.Peer` | **public** |

`Pump()` stays public for tests and custom loops, but its XML docs must state the main-thread requirement and that it stamps the liveness counter.

### Naming

| Was | Is | Why |
|-----|-----|-----|
| `PeerConnection.DataChannel` (event) | `DataChannelReceived` | Shadowed the `DataChannel` **type** inside the class; also out of family with the other events |
| `DataChannel.Open` | `Opened` | Wrong tense — the sibling `Closed` was already correct |
| `DataChannel.Message` | `MessageReceived` | Tense, and it removes the collision with `DataChannelLog.Message` |
| `DataChannel.Error` | `ErrorOccurred` | Every other event is a past-tense verb (precedent: `SerialPort.ErrorReceived`) |
| `DataChannelLog.Message` (event) | `MessageLogged` | Tense |
| `DataChannelLog.Level` + `SetLogLevel()` | `Level` property only | One state, two public entry points |
| `SetObserver()` (both types) | `Observer` property | Was set-only: unreadable, unremovable. Silent overwrite is the intended semantic — a single observer, not multicast |
| `DataChannelRuntime.OnDomainReload()` | `ResetStaticsOnEnterPlayMode()` | The name was simply false: it is hooked to `RuntimeInitializeOnLoadMethod(SubsystemRegistration)`, which runs on **entering play mode**, not on domain reload |
| `DataChannelException.ErrorCode` (`int`) | `ErrorCode` (`DataChannelError`) **+** new `RawCode` (`int`) | §4 error codes |
| — | `ConnectionState` / `GatheringState` gain an `Unknown` member | §4 state enums |
| — | new `DataChannelState` (`Connecting` / `Open` / `Closed`) + `DataChannel.State` | §4 open semantics |
| — | new `DataChannelLog.LeakDetection` (`Disabled` / `Enabled` / `EnabledWithStackTrace`) | Shape borrowed from `Unity.Collections.NativeLeakDetection.Mode` |

**Deliberately unchanged** (so nobody "fixes" them): `AbiVersion` / `Mtu` — PascalCase is correct for acronyms of three letters or more; `DataChannelInit` — it mirrors the W3C `RTCDataChannelInit` and is domain vocabulary; `LocalDescriptionGenerated`, `LocalCandidateGenerated`, `ConnectionStateChanged`, `GatheringStateChanged` — already right.

### Subscription

1. **Primary:** C# `event`s  
2. **Optional:** `IPeerConnectionObserver` / `IDataChannelObserver` (same events, main thread)  
3. Dual subscription order: **events first, then observers** (fixed; document in XML docs)

Event set:

- PC: `LocalDescriptionGenerated`, `LocalCandidateGenerated`, `ConnectionStateChanged`, `GatheringStateChanged`, `DataChannelReceived`
- DC: `Opened`, `Closed`, `ErrorOccurred`, `MessageReceived`

**Delegate types.** Nine of the ten events use `Action<T>`. The message event uses a custom delegate:

```csharp
public delegate void DataChannelMessageHandler(ReadOnlySpan<byte> data);
```

Unity 2022.3 is **C# 9**, where `ReadOnlySpan<T>` is a `ref struct` and cannot be a generic type argument (`allows ref struct` needs C# 13), so `Action<ReadOnlySpan<byte>>` does not compile. `IDataChannelObserver.OnMessage` takes the same shape.

Using `Action<T>` rather than `EventHandler<TEventArgs>` is an **explicit decision**, recorded here so that every static-analysis pass does not re-raise `CA1003`: `EventHandler<T>` wants an `EventArgs` subclass per event — by convention a class — which means **one heap allocation per message**, precisely the cost §4 and the pump design exist to remove. `sender` is redundant here (a subscriber already holds the `DataChannel`), and the "one handler, many sources" case is served by the observer interfaces.

### Ownership and lifetime

```
PeerConnection ──owns──▶ DataChannel        (strong reference, list of children)
       ▲                      ▲
       └── weak ──┬─── weak ──┘
                  │
            HandleTable        (lookup only: handle → object; never keeps anything alive)
```

| Rule | |
|------|--|
| **The PeerConnection owns its DataChannels** | `PeerConnection.Dispose()` cascades, **children first, then the parent** — destroying the PC first would leave `dcu_dc_destroy` firing at zombie handles. Upstream's `rtcDeletePeerConnection` only closes the PC and erases *it* (`capi.cpp:437-444`), so without the cascade libdatachannel's own `dataChannelMap` leaks too |
| **Disposing a child directly is fine** | The parent must drop it from its child list so it is not destroyed twice |
| **The lookup table holds weak references** | Liveness comes from the ownership edge, not from the table. DataChannel events do not carry their `pc` handle, so a parent-first lookup would need an ABI change plus a native dc→pc map — not worth it |
| **Incoming DataChannels are always accepted** | Created and owned by the PC even with no subscriber. Rejecting "unwanted" channels is timing-sensitive: an application that subscribes one frame later (or after an `await`) would have its channel silently closed — far harder to diagnose than a leak. Exceeding a child-count threshold warns; it never refuses |
| **Leak diagnostics are mandatory** | On by default in Editor / Development builds, off in Release, with the **creation-time** stack trace when `LeakDetection.EnabledWithStackTrace` is set |
| **Cascade-disposed children behave like directly-disposed ones** | …except the exception message names the cause. They do **not** raise `Closed`: doing so would run user callbacks while the parent is half-torn-down, and `Closed` should mean "the channel was closed", not "you just released it" |

**Finalizers do exactly one thing: enqueue.** A finalizer records the handle and leak info on a lock-free queue; the main-thread pump logs it and removes the table entry. Finalizers must **not** call any `dcu_*` function, must **not** take the handle-table lock, and must **not** call `Debug.LogError` — 2022.3 makes no thread-safety promise for it off the main thread, and P/Invoking `rtcDelete*` from the finalizer thread blocks until callbacks quiesce, which during a domain unload means the Editor hangs on every entry into play mode.

Finalizers exist **only** under `#if UNITY_EDITOR || DEVELOPMENT_BUILD`; the stack-trace capture is separately gated by `LeakDetection`. Both layers are needed, not one: having a finalizer at all is a per-type cost (every instance is queued for finalization and survives an extra GC generation) that no runtime switch can remove. Unity's own `DisposeSentinel` is conditionally compiled for the same reason.

**`SafeHandle` is not used.** It does not solve the blocking problem, CER support is an `[Obsolete]` no-op on modern runtimes, and these handles are `int`, not `IntPtr`.

> **Why a handle table at all?** `datachannel-rs` avoids one entirely by hanging a `Box<Self>` off `rtcSetUserPointer`. That is safe *because* its `Drop` blocks until callbacks have quiesced and runs deterministically on the owning thread. Neither premise holds in a GC language. The handle table is not a detour around a better design — in a GC language it **is** the design.

### Threading

- All public **events** and **observer** callbacks run on the **Unity main thread**.
- **Every public API — including `Dispose` — is main-thread-only**, asserted in Editor / Development builds. Nearly all of Unity is main-thread-only, so this is a zero-learning-cost contract. The alternative ("`Dispose` just marks; the pump does the work later") fails exactly when it is needed most: in edit mode, at application quit, and after a domain reload, the pump may not run again.
- The package installs a **PlayerLoop** pump. **Registration failure throws** rather than warning: a package that thinks it is pumping but is not is the worst possible state.
- `DataChannelRuntime.Pump()` stays public for tests and custom loops.

#### The pump has two segments, and both drain fully

1. **Control segment** — `dcu_event_next` until the queue is empty.
2. **Data segment** — for each live, open channel, `dcu_dc_receive` until `NOT_AVAIL`.

Neither segment has a per-frame budget, and they do not share one.

Draining is the *correct operating point* of the back-pressure loop built in §4: pump drains → the upstream queue does not grow → no back-pressure; application callbacks slow down → frames lengthen → pulling slows → the queue reaches `RECV_QUEUE_LIMIT` → `push` blocks → genuine back-pressure reaches SCTP. **A frame budget would trigger that back-pressure artificially early.** Independently: all three comparable implementations available locally (FishNet Tugboat, FishNet Synapse, `com.unity.webrtc`) drain both segments in the frame loop; Synapse's throttling lives at the **connection** layer, where a specific peer can actually be reacted to. A message-count budget would also be a poor proxy for the thing it protects (frame time) — one 4-byte message and one 10 MB message differ by six orders of magnitude.

The segments are kept separate because their producers differ: control-event rate is a function of the connection count (bounded, application-controlled), message rate is a function of peer traffic (unbounded). Sharing a budget would let traffic push `DcClosed` into a later frame, after which the application keeps sending on a dead channel.

> **Accepted weakness, stated plainly:** a fast peer can stretch the pump segment linearly with its traffic, and nothing stands in the way. The correct fix is a per-connection inbound rate limit with disconnection, at a layer that can react to *that peer* — out of scope here (§1). Because it is accepted, it must at least be **visible**: see below.

#### Observability

Internal constants; **no configuration surface** (a knob has to be given semantics, specified, and tested, and there is no evidence these values are wrong):

| Signal | Threshold |
|--------|-----------|
| Slow pump frame | **4 ms** (of a 16.7 ms frame at 60 fps). `Stopwatch.GetTimestamp()` twice per frame; always compiled in |
| Control-queue backlog | depth > **1024** — same order as upstream's `RECV_QUEUE_LIMIT`; reaching it means the pump is not running or a callback is stuck |
| Pump liveness | Each `Pump()` stamps a monotonic counter and a wall-clock timestamp. `PeerConnection` creation, `CreateDataChannel` and `Send` check "how long since the last pump"; past a seconds-scale threshold, log an error and attempt re-registration |
| Throttling | One warning per category per **5 s**, carrying the occurrence count and peak for the period |

The liveness check is not about registration failing at startup (nearly impossible) but about being **erased afterwards**: any third-party package that rebuilds from `GetDefaultPlayerLoop()` and calls `SetPlayerLoop` drops our entry while our `_pumpRegistered` flag still says `true`. Detection is an integer comparison and recovery reuses the existing type-keyed re-installation — cheaper than diagnosing it later. The threshold must not be measured in `Time.frameCount` (unreliable in edit mode, where the pump is resident); use `EditorApplication.timeSinceStartup` there. Check only when the application calls an API; never poll in the background.

No public read-only diagnostics snapshot: v1 observability is `BufferedAmount` only (§7). Logs are enough to attribute these problems; opening a diagnostics surface for application-side network HUDs would be its own decision.

#### Exception isolation is per subscriber

A multicast delegate invokes in order and stops at the first throwing subscriber. Isolation is therefore **per subscriber**: catch, log at Error with the full exception, continue to the next one, and **do not auto-unsubscribe** (silently altering subscriptions is a worse surprise than log spam, and spam is already handled by throttling, keyed on event type + exception type).

Wrapping the whole `Invoke` in one `try` is rejected: subscriber A throwing would mean subscriber B never receives *that message*, with no retransmission path — the same "dropped message = protocol violation" that §4 refuses at the queue, relocated to the dispatch step.

Implementation differs by frequency, semantics do not: control events call `GetInvocationList()` directly (sparse; the array allocation is irrelevant), the message event uses a **cached invocation list** rebuilt only when subscriptions change (~15 lines, one place) — otherwise the per-message `byte[]` we just removed would come back as a per-message `Delegate[]`. The cached snapshot also makes the common "unsubscribe myself from inside the callback" pattern safe.

The logging path isolates itself too: `DataChannelLog`'s own event is wrapped, and exceptions from *that* wrapper are **swallowed** — trying to log them would recurse forever.

#### Re-entrancy during dispatch

Pulling a message and dispatching it immediately means an application callback may legally `Dispose()` a channel or `CreateDataChannel()`. Both mutate the handle-table dictionaries, and `Dictionary` enumeration throws `InvalidOperationException` once the collection is modified — an exception raised by *our* iteration, which the per-subscriber isolation does not cover and which would escape the pump.

Therefore the data segment copies live, open channels into a **reused `List` snapshot** each frame and re-validates each entry before pulling. Channels created during the frame are pulled from the next frame, which is correct anyway — they are not open yet. The snapshot is rebuilt by scanning the handle table rather than maintaining a resident "open channels" list: a resident list is derived state that goes permanently wrong if one `Closed` is missed, and §4 requires the pump to stay stateless. Weak-reference sweeping happens during the snapshot phase, never mid-dispatch.

#### Buffers

- Baselines: **64 KB** payload, **4 KB** payload2. Control buffers and message buffers are **separate** — SDP and candidates sit at a few KB while messages may be megabytes; sharing means the small one is permanently sized for the big one.
- Growth: grow-to-fit.
- Shrink: with **hysteresis** — only after two consecutive 5-second windows whose peak stayed below the baseline. One window would thrash under bursty traffic, and shrinking itself allocates.
- Log the first growth beyond baseline (Info, with size); do not log shrinks.

"Fixed capacity plus a temporary array when oversized" is rejected even though it is simpler: an application whose normal payload is 200 KB (video slices, map data, save sync) would allocate on **every** message, quietly undoing the zero-allocation guarantee under the name of an overflow path. The guarantee has to hold at the sizes applications actually use, not at the sizes we guessed.

### Editor and application lifecycle

**One principle; the five scenarios follow from it, so there is nothing to memorise:**

> **The domain is about to die** → the managed side is about to lose every reference → the only option is the sledgehammer, `dcu_shutdown()`.  
> **The domain is still alive** → we still hold the references → use the precise tool, `DisposeAllLive()`, and do not swing the hammer.

| Scenario | Action |
|----------|--------|
| **Edit mode** | Pump is **resident**; native init is **lazy**. `beforeAssemblyReload` records "was it initialised" in `SessionState`; `afterAssemblyReload` re-creates **only if it was** |
| `beforeAssemblyReload` (edit-mode recompile, and entering play mode with Reload Domain on) | `DisposeAllLive()` → `UnregisterPump()` → **`dcu_shutdown()`** |
| `ExitingPlayMode` | `DisposeAllLive()` → `UnregisterPump()`, **no shutdown**; `EnteredEditMode` → `RegisterPump()` (deduplicated by type) |
| Reload Domain **disabled** | `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` → `DisposeAllLive()` + pump dedup, **no shutdown** |
| `EditorApplication.quitting` | Unsubscribe our own handlers first, then `DisposeAllLive()` + full shutdown |
| Player `Application.quitting` | `DisposeAllLive()` only, **no shutdown** |

Rationale for the two asymmetric ones:

- **Exiting play mode is handled explicitly** because *it does not trigger a domain reload* — measured twice on 2022.3.62f3 with Reload Domain enabled. Play-mode statics would otherwise leak straight into edit mode. `com.unity.webrtc` carries exactly this bug by relying on domain-reload events alone; `com.unity.entities`' `RuntimeContentSystem` handles `ExitingPlayMode` specially, which is it dodging the same hole.
- **The player does not call `dcu_shutdown()`.** The two halves have very different value: `DisposeAllLive()` sends DTLS/SCTP close notifications so the **peer learns immediately** instead of waiting out an ICE timeout — real value for a P2P library. Reclaiming a thread pool in a dying process is worth nothing, and `rtcCleanup` can block for ~10 s, while iOS grants roughly 5 s at termination and Android may ANR.

**Stale events are not purged.** The resident pump drains them naturally, and handles are never reused (upstream's `lastId` only increases and is not reset), so a stale event necessarily misses the lookup and is discarded harmlessly. Misdelivery is impossible.

**Not decided here:** whether to disconnect on `OnApplicationPause(true)`. `Application.quitting` frequently does not fire on iOS (suspended, not terminated) or Android (killed in background), but whether to tear down when backgrounded is *product* semantics — it depends on the game — not lifecycle hygiene.

### Errors

| Situation | What the caller gets |
|-----------|---------------------|
| Managed misuse (null, wrong state, used after dispose) | Standard .NET exceptions: `ArgumentNullException`, `ArgumentException`, `InvalidOperationException`, `ObjectDisposedException` |
| Native failure | `DataChannelException` |

**Keep these two spaces separate.** `datachannel-rs` splits C-side numeric failures from FFI marshalling failures and is right to; our split is already cleaner. Do not "improve" it by funnelling `ArgumentNullException` into `DataChannelException`.

`DataChannelException` exposes both:

- **`ErrorCode`** (`DataChannelError`: `Invalid` / `Failure` / `NotAvailable` / `TooSmall` / `UpstreamUnknown` / `Unknown`) — for control flow. Names describe **transport-plumbing** semantics and must not imply application meaning.
- **`RawCode`** (`int`) — **for diagnostics and bug reports only; never branch on it.** Document this split in the XML docs.

Keeping `RawCode` is the necessary counterpart to §4's independent numbering: the whole point is that an unrecognised code reveals an upstream leak, and if the public surface were the enum alone, a leaked `-3` would arrive as `Unknown` with the `-3` gone. Exposing only the raw `int` has the opposite failure — it invites applications to branch on plumbing codes. There is a single `Unknown` rather than two ("dcu didn't recognise the backend" vs "C# didn't recognise dcu") because to an application they mean the same thing; `RawCode` still lets a maintainer tell them apart.

Exception messages come in **two** shapes, not one per code — the useful signal is *whose problem is it*:

- `Invalid` / `TooSmall` → self-fixable: *"dcu_dc_send: invalid argument (code=Invalid, raw=-101). Check whether the channel is disposed and whether the payload exceeds the negotiated MaxMessageSize."*
- `Failure` / `NotAvailable` / `UpstreamUnknown` / `Unknown` → likely a bug: *"dcu_dc_send: runtime failure (code=Failure, raw=-102). This is usually not a usage problem; please attach DataChannelLog output and file an issue."*

Other rules: create failure throws, leaving no half-open object; ICE failures surface only as `ConnectionState` events; `Send` on a disposed channel throws. **`Send` does not pre-check the open state** (§4) — a send on a closed channel fails natively and is reported as such.

`NativeConfigBuilder`'s constructor wraps its allocations in `try`/`catch`, freeing whatever it already allocated before rethrowing; it has no finalizer to fall back on.

### DataChannel

```csharp
CreateDataChannel(string label, DataChannelInit init = null);   // label ≤ 65535 UTF-8 bytes
Send(ReadOnlySpan<byte>);
Send(byte[]);
Send(byte[] data, int offset, int count);
int BufferedAmount { get; }
DataChannelState State { get; }        // live query, not a cached flag
```

**`DataChannelInit` semantics**

| Field | Rule |
|-------|------|
| `Ordered` | default `true` |
| `Reliable` | default `true` |
| `MaxRetransmits` / `MaxPacketLifeTime` | `Reliable == true` → **both must be 0**, else `ArgumentException`. `Reliable == false` → **at most one** may be set, else `ArgumentException` |

The W3C spec makes the two `Max*` fields mutually exclusive; the extra `Reliable` flag makes our rule stricter and clearer. Upstream would fail anyway (`impl/datachannel.cpp:82-83` throws `"Both maxPacketLifeTime and maxRetransmits are set"`); validating in C# is about giving an error the caller can act on directly instead of a generic `DataChannelError.Invalid`.

`Mtu`, `MaxMessageSize`, `PortRangeBegin`, `PortRangeEnd` treat **`0` as "automatic"**. Give that value a named constant and say so in the XML docs rather than leaving it in upstream's header comments.

`IceServer`, `PeerConnectionConfig` and `DataChannelInit` are **not** `[Serializable]` — see §5.

**Send is zero-copy on all three overloads.** The P/Invoke declaration is `dcu_dc_send(int, IntPtr, int)`, called under `fixed`. (Note that default marshalling of a blittable `byte[]` already pins rather than copies, so `Send(byte[])` was never the problem; the actual copies were the `offset != 0` slice and `ReadOnlySpan.ToArray()`, which negated the entire point of the Span overload.) **This is not an ABI change** — the C signature is unchanged. Zero-length sends are legal; since `fixed` on an empty array yields a null pointer, take the address of a static dummy byte instead of relying on the unverified assumption that `(null, 0)` means "empty message".

Bounds checking is written `data.Length - offset < count`, not `offset + count > data.Length`: `offset` and `count` are already known non-negative, so the subtraction cannot overflow, whereas the addition wraps negative near `int.MaxValue` and disables the check. With `fixed` in the picture this is the last barrier before an out-of-bounds read.

No `string` overload (§4, deferred).

### Signaling shape (auto-negotiation)

**Out (events):** local SDP/type, local candidates  
**In (methods):** `SetRemoteDescription`, `AddRemoteCandidate`  
**No public** `CreateOffer` / `CreateAnswer` / `SetLocalDescription`

Typical offerer: create PC → subscribe → `CreateDataChannel` → emit local desc/cands via app signal → `SetRemoteDescription(answer)` + remote cands.  
Typical answerer: `SetRemoteDescription(offer)` → emit local answer/cands → receive remote DC event.

### P/Invoke

```csharp
#if UNITY_IOS || UNITY_WEBGL
  const string Dll = "__Internal";
#else
  const string Dll = "datachannel_unity";
#endif
```

Application code never sees `dcu_*` / `rtc_*`.

The `#if NET_STANDARD_2_1 || UNITY_2021_2_OR_NEWER` guard around the `ReadOnlySpan<byte>` overload is **deleted, not fixed**: `UNITY_2021_2_OR_NEWER` is unconditionally true on the 2022.3 baseline, and `NET_STANDARD_2_1` is not a symbol Unity defines at all (it emits `NET_STANDARD_2_0` / `NET_UNITY_4_8`). The condition never did anything; leaving it implies a compatibility concern that does not exist.

---

## 7. Logging and diagnostics

**Decision:** [#12](https://github.com/xuhuanhello/juice-c-sharp/issues/12), log bridge and redaction [#33](https://github.com/xuhuanhello/juice-c-sharp/issues/33)

| Item | Spec |
|------|------|
| Upstream | Bridge the libdatachannel logger (which also carries libjuice's) |
| C# | `DataChannelLog.Level` property; `MessageLogged` event |
| Defaults | Editor or Development Player → **Info**; non-Development Player → **Warning** |
| Secrets | **Redact** ICE URLs containing credentials in logs |
| Stats v1 | `BufferedAmount` only; no selected-pair / RTT panel |

### The path a log line takes

```
any libdatachannel thread (thread pool, libjuice, caller threads)
   │  upstream holds its callback lock for the whole call
   ▼
dcu static log trampoline          ← enqueue only; must never block
   ▼
bounded log queue (1024, drop-oldest + dropped counter)
   ▼
main-thread pump drains
   ▼
DataChannelLog.Emit  → redaction → MessageLogged event + Debug.Log*
```

**Native callbacks never enter managed code directly.** `Debug.LogError` has no documented thread-safety guarantee off the main thread in 2022.3, and the upstream callback runs **under a lock** (`synchronized_callback::operator()`), so *every* log line from *every* thread in the process is serialised through it. The trampoline must therefore be trivially fast; the bounded queue is as much protection for that critical section as it is a memory bound. (The lock is a `recursive_mutex`, so logging from inside a callback will not self-deadlock — the constraint is throughput, not recursion.)

**Log lines are droppable; control events are not.** That is why they use two queues with opposite policies (§4). When the pump drains a batch with `dropped > 0`, it emits one warning carrying the count. Under a Verbose stress test that warning is **expected behaviour**, not a failure — any "console must be clean" gate has to account for it (§11).

### Level changes never detach the bridge

`dcu_set_log_level` always passes the same static trampoline down to `rtcInitLogger`; **the callback parameter is never exposed to callers.** This structurally removes a trap in the upstream C API: `InitLogger` with an existing appender assigns `appender->callback = std::move(callback)`, so passing `nullptr` silently clears the callback, and `LogAppender::write` then falls back to `std::cout` (`src/global.cpp:59,65-80`) — bridge gone, credentials unredacted, zero diagnostics. Quieting the logs is done **only** through levels, including `LogLevel.None` → `RTC_LOG_NONE`.

(Upstream writes to **stdout**, not stderr — plog's `ConsoleAppender` defaults to `streamStdOut`.)

### Initialization dependency is one-directional

`DataChannelLog` owns managed state only — the level, the event, redaction — and **does not know that native exists**. Pushing the level down to native belongs solely to `DataChannelRuntime`:

```csharp
// DataChannelLog
public static LogLevel Level {
    get => _level;
    set { _level = value; DataChannelRuntime.OnLogLevelChanged(value); }   // one direction
}

// DataChannelRuntime — the only type that knows about native
internal static void OnLogLevelChanged(LogLevel l) {
    if (_nativeReady) NativeMethods.dcu_set_log_level((int)l);
}
```

The previous shape was mutually recursive (`EnsureNative` → `EnsureDefaults` → `SetLogLevel` → `IsNativeAvailable` → `EnsureNative`), terminating only because each side set its flag *before* calling the other. It worked, but any harmless refactor that moved a flag assignment after the call would turn it into a startup stack overflow. **Cut the cycle instead of adding a re-entrancy guard** — a guard swaps an implicit invariant for an explicit one that still depends on people remembering it.

### Exception logging

`DataChannelLog` needs an entry point that accepts an `Exception`, not just a `string`. Subscriber exceptions (§6) are the case that most needs a full stack — the error is in application code, and a flattened one-line `e.Message` leaves the reader guessing. Cooperate with `Debug.LogException` so the Console entry stays click-navigable.

### Credentials, end to end

| Stage | Rule |
|-------|------|
| **In** | Credentials must not enter assets — no `[Serializable]` config; supply them from the signaling server at runtime (§5) |
| **Through** | Credentials never enter a URL string — `rtc::IceServer` structured fields (§5) |
| **Out** | Logs redact the `scheme://user:pass@host` shape, covering the case where the application put credentials in `urls` itself |

Redaction stays exactly as broad as it is: `RedactIceCredentials` becomes `internal` with a static `Compiled` regex (it currently constructs a new, uncompiled `Regex` per log line). **No new rules, and no native-side redaction.**

**ICE short-term credentials are deliberately out of scope for redaction.** libjuice logs `password="…"` on an integrity-check failure at `JLOG_WARN` — which maps to plog *warning*, i.e. it is live at the release default level, not only under Debug — and `peerconnection.cpp:143` logs `ufrag`/`pwd` at Debug. These are single-session credentials that are already transmitted in the clear inside SDP, and upstream prints them itself. Building a cross-language scanning ruleset for them has no payoff, and native-side redaction would have to sit on that locked, every-single-line critical path.

On the normal path upstream logs ICE servers as `hostname:port` only and never touches the credentials (`impl/icetransport.cpp:110,170,633`).

### Known limitation

**A level change reaches libjuice only at the next PeerConnection creation.** `juice_set_log_level` is called once, from the `IceTransport` constructor, taking a snapshot of the then-current plog level (`impl/icetransport.cpp:85`); libdatachannel's public surface does not expose `juice_set_log_level`, so there is no way around it. Raising verbosity to debug an ICE problem therefore requires creating a new PeerConnection afterwards.

---

## 8. Plugins layout and PluginImporter

**Decision:** [#10](https://github.com/xuhuanhello/juice-c-sharp/issues/10)

### Tree

```text
Packages/datachannel-unity/
  package.json                    # name: com.xuhuanhello.datachannel
  Runtime/                        # C# + asmdef
  Plugins/
    Windows/
      x86_64/datachannel_unity.dll
      ARM64/datachannel_unity.dll
    macOS/
      x64/datachannel_unity.bundle/
      arm64/datachannel_unity.bundle/
    Android/
      arm64-v8a/libdatachannel_unity.so    # no libs/ segment
    iOS/
      libdatachannel_unity.a               # device arm64 static
    WebGL/
      libdatachannel_unity.a
      webrtc.jslib                         # no websocket.jslib
  Samples~/                           # preferred for dual-peer sample
```

### Per-platform rules

| Platform | Artifact | DllImport | Editor |
|----------|----------|-----------|--------|
| Windows x64 / arm64 | `.dll`, CRT `/MD`, self-contained | `datachannel_unity` | Yes (matching CPU) |
| macOS x64 / arm64 | **Thin** `.bundle` (not universal) | `datachannel_unity` | Yes |
| Android arm64 | `libdatachannel_unity.so` | `datachannel_unity` | No |
| iOS arm64 | static `.a` | `__Internal` | No (no simulator v1) |
| WebGL | `.a` + `webrtc.jslib` | `__Internal` | No |

- Explicit `.meta` for every plugin; do not rely on folder magic alone.
- Binaries in **Git LFS**; `.meta` in normal git.
- Self-contained: crypto + backend deps **static-linked** into the plugin (see §3 linking rules).
- macOS: never ship both `.bundle` and `.dylib` for the same arch.

### WebGL hard constraints

1. datachannel-wasm + browser WebRTC (not libdatachannel-in-wasm UDP fiction).  
2. Same `dcu_*` via C facade over wasm C++.  
3. Ship both `.a` and `webrtc.jslib`.  
4. Build with Unity’s Emscripten; recompile on toolchain jumps.

### WebGL exception: the back-pressure guarantee does not hold

§4 promises that nothing is dropped, because a full receive queue blocks `push` and pushes back onto SCTP. **On WebGL that is physically impossible.** datachannel-wasm's `Channel` has no `receive()` / `peek()` / `availableAmount()` and no receive queue at all — a message arrives, the callback runs, and with no callback attached the message is discarded (`wasm/src/channel.cpp:73-75`). The browser's `onmessage` cannot be blocked.

The WebGL facade must therefore buffer messages itself to emulate `dcu_dc_receive`, and it can only choose between **bounded with discard** and **unbounded growth** — the very dilemma the native design dissolved. **The ABI is identical; the behaviour is not.** Do not present cross-platform behaviour as uniform here.

The choice between those two is deferred with the facade itself, which is not yet specified.

---

## 9. Native build system

**Decisions / research:** [#4](https://github.com/xuhuanhello/juice-c-sharp/issues/4), [#24](https://github.com/xuhuanhello/juice-c-sharp/issues/24), ~~[#23](https://github.com/xuhuanhello/juice-c-sharp/issues/23) / [#25](https://github.com/xuhuanhello/juice-c-sharp/issues/25)~~ **rescinded by** [#27](https://github.com/xuhuanhello/juice-c-sharp/issues/27),  
`docs/research/meson-subprojects-static-graph.md`

### Product entry (local + CI)

```bash
./native/scripts/fetch-deps.sh
cmake -S native -B native/build/macos-arm64 -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build/macos-arm64
# cross-compiling: add -DCMAKE_TOOLCHAIN_FILE=native/cross/<file>.cmake
# thin wrapper (same path):
./native/scripts/build-macos-arm64.sh
./native/scripts/audit-macos-plugin.sh Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle
```

| Rule | Detail |
|------|--------|
| **CMake is the only product entry** | `native/CMakeLists.txt` — same for local mac and CI; no “dev uses brew openssl .a + clang” product path |
| **Sources in `subprojects/`** | `mbedtls` @ lock, `libdatachannel` @ lock — fetched by `fetch-deps.sh`, never committed |
| **Staging + audit** | `POST_BUILD` custom commands on the plugin target; no shell shim in between |
| **Platform mapping** | `CMAKE_SYSTEM_NAME` / `CMAKE_SYSTEM_PROCESSOR`, so it **follows the toolchain file**; `native/cross/` holds CMake toolchain files |
| **MbedTLS** | Built from **subprojects source** with `MBEDTLS_USER_CONFIG_FILE` → `MBEDTLS_SSL_DTLS_SRTP`; static `.a` only; injected as `MbedTLS::MbedTLS` into libdatachannel (**not** brew find_package) |
| **libdatachannel** | `USE_MBEDTLS=ON`, `BUILD_SHARED_LIBS=OFF`, `NO_MEDIA/NO_WEBSOCKET`, hidden visibility |
| **Exports** | `native/exports/*` allowlist (`dcu_*` only), checked against `expected-symbols.txt` (§11) |
| **Install** | Single macOS `.bundle` per arch into UPM `Plugins/` |
| **Scripts must be executable in git** | `git update-index --chmod=+x`; CI **asserts** this rather than running a fallback `chmod` (§11) |

### Why not Meson (this was reversed)

#23 / #25 locked "Meson is the only build entry"; **#27 rescinded that — not because Meson is bad, but because the decision was never actually implemented.** What it asked for was dependencies living in `subprojects/` and participating in cross-compilation. What existed was a 58-line shell launcher that compiled **no source files at all** (three existence guards, a `find_program('bash')`, and a `custom_target` that shelled out to CMake), and it carried two defects: editing a source file did not trigger a rebuild (`custom_target` had no `input` / `depend_files`), and `--cross-file` did nothing because the script hard-coded `cmake -G Ninja` with host-derived output paths. `meson subprojects download` also exited **2** — all three dependencies are CMake projects with no `meson.build` — and a `|| true` was hiding that exit code along with any genuine network failure.

Making Meson live up to the decision needed one of two things, both rejected: maintaining out-of-tree `meson.build` overlays for all three dependencies (usrsctp ships one and mbedtls has a third-party port, but **libdatachannel has none** — and it sits at the top of the tree with ~195 sources and 10 configuration switches, while mbedtls has build-time code generation), or `cmake.subproject()`, which #24 had already established cannot compile sources outside the subproject directory.

**Keep that last fact.** It is why the build originally detoured through Meson, and it is why it no longer does.

Verification of the switch: the produced binary was **byte-identical** to the pre-migration artifact (same compiler, same flags, same sources — behaviour preserved), and incremental rebuild was measured working (`touch dcu_impl.cpp` → recompile, relink, restage, re-audit).

### Crypto backend note (why MbedTLS appears in SPEC)

| | OpenSSL | MbedTLS 3.6 |
|--|---------|-------------|
| **libdatachannel default** | **Yes (default)** | Optional (`USE_MBEDTLS=ON`) |
| **“Better crypto?”** | No absolute ranking for this project | Not chosen for “stronger crypto” |
| **Why SPEC preferred MbedTLS** | Larger static footprint; historically awkward mobile packaging | Smaller, common for static/mobile game plugins; LTS 3.6 |
| **Product rule** | **Not used.** Forbidden as a system/brew dylib, and the static-vendored escape hatch was removed (#27 / #36) | **The product path**: vendored, static, with the DTLS-SRTP user config applied |

### Phased platforms

Risk order: WebGL > iOS > Win arm64 > Android > macOS > Win x64.

---

## 10. CI, LFS, and signing

**Decisions:** [#13](https://github.com/xuhuanhello/juice-c-sharp/issues/13), [#20](https://github.com/xuhuanhello/juice-c-sharp/issues/20), workflow alignment [#36](https://github.com/xuhuanhello/juice-c-sharp/issues/36), first commit and LFS [#35](https://github.com/xuhuanhello/juice-c-sharp/issues/35)

### Local vs CI

| Role | Builds | Checks |
|------|--------|--------|
| **Local (default)** | **mac only** (`native/scripts/build-macos-arm64.sh`, optional x64) | Developer may run `audit-macos-plugin.sh` |
| **CI** | **Full matrix** | Link audit (no system crypto dylibs), export allowlist (`dcu_*` only), managed-tier tests (§11) |

### Rules the workflows must keep

- **No dependency installation that contradicts §3/§9.** `brew install openssl@3` + `OPENSSL_ROOT` was removed: it directly conflicts with the fully static vendored MbedTLS product path, which means that job had in fact been broken.
- **No fallback `chmod +x`.** Instead the workflow **asserts** the scripts are executable and, on failure, prints the correct fix (`git update-index --chmod=+x`). Deleting the fallback alone would only fix today; the assertion makes the same regression surface immediately next time (§11).
- **Disabled jobs stay visible.** The Unity test job is gated on `if: vars.ENABLE_UNITY_TESTS == 'true'` rather than commented out — `secrets` cannot be used in a job-level `if`, `vars` can. When unset it shows as *skipped*: visible, and therefore not forgotten.

### Plugin binaries and LFS

Plugin binaries are **not** committed until the matrix produces all of them — a partial matrix (one platform of six) is worse than none. The `.gitattributes` LFS rules are nevertheless in place already, because retrofitting them later means rewriting history. They match **by path, not by extension**: the macOS artifact is an extensionless Mach-O inside a `.bundle` directory, which no extension-based template rule can catch.

### PR vs release / LFS

| Gate | Requirement |
|------|-------------|
| **PR** | EditMode + **at least one mac build + audit** |
| **Release / maintainer LFS commit** | **Full matrix green**; artifacts → maintainer commit to `Plugins/` + Git LFS |

### GitHub Actions (spec-level matrix)

| Runner | Outputs |
|--------|---------|
| windows | `datachannel_unity.dll` x64; arm64 when runner/toolchain available |
| macos | macOS thin **bundles** x64+arm64; iOS `.a` |
| ubuntu or macos | Android `arm64-v8a` `.so`; WebGL `.a` + `webrtc.jslib` |

Workflow skeleton: `.github/workflows/pr.yml` (light), `.github/workflows/plugins-matrix.yml` (full; manual/schedule/release).

### Signing

- **No** codesign/notarize/Authenticode of plugins in CI or repo.
- **No** Apple/Windows certs stored in the project.
- iOS `.a` unsigned; adopters sign the final Xcode app.
- Document: **adopter owns final app signing and store compliance**.

---

## 11. Samples and testing

**Decision:** [#14](https://github.com/xuhuanhello/juice-c-sharp/issues/14), [#39](https://github.com/xuhuanhello/juice-c-sharp/issues/39)

### Sample (required)

- In-process **two PeerConnections** + **in-memory fake signal** (SDP/candidates).
- After connect, send/receive **≥ 1 binary** DataChannel message.
- Lives in `Samples~`. Document how to plug real Signal + IceServers.
- The sample is a **human-facing demo, not a gate**. Its protocol choreography is intentionally duplicated by the PlayMode smoke test: `Samples~` is never compiled by Unity, so no asmdef can reference it, and hoisting the choreography into `Runtime/` would ship test scaffolding in the public API. The two are not copies of one thing — they are two things for two audiences (a readable `MonoBehaviour` with narration; an assertion-only `[UnityTest]`).
- Not required: public STUN/TURN lab scene; FishNet sample.

### Test tiers

Tier boundary is **"does it need the native plugin loaded?"** — which is exactly the CI-reachability boundary, since plugin binaries are not committed.

| Tier | Assembly | Content | Runner |
|------|----------|---------|--------|
| **Managed** | `DataChannelUnity.Tests.Editor` | Pure C# contracts, zero P/Invoke | **CI — the only automated gate that turns red** |
| **Native / EditMode** | `DataChannelUnity.Tests.Editor.Native` | Contracts requiring the plugin | Local Editor |
| **Native / PlayMode** | `DataChannelUnity.Tests.Runtime` | Dual-peer loopback, PlayerLoop pump | Local Editor; headless `-runTests -testPlatform PlayMode` |

**Tier selection happens at the call site** (assembly filter), never inside a test. Assembly separation is chosen over `[Category("Native")]` because it fails safe: CI already filters by assembly name and needs no change, and native tests are physically absent from what it compiles. A category would require CI to *remember* an exclusion flag — and the principle below exists precisely to stop relying on remembering.

The PlayMode tier exists for what EditMode structurally cannot cover: `RegisterPump()` installs into `PlayerLoop`, so the smoke test must assert that messages flow **without anyone calling `Pump()` manually**. (Today no verification covers the registration path at all; the Edit-mode loopback calls `Pump()` by hand.)

> **CI upgrade path, recorded but not adopted:** move the `editmode` job to `macos-latest` with `needs:` the native build job and `download-artifact`, and the native tiers become CI-runnable. Not adopted while no link in that chain is live (`UNITY_LICENSE` unset, `ENABLE_UNITY_TESTS` off) — a gate defined in terms of a dead chain is a gate that never runs.

### Hard constraint: absence must be failure

- No `Assert.Ignore` / `[Ignore]` for a missing native plugin.
- No fallback `chmod`, no `|| true`, no ignored exit codes.

These are one disease, not three rules: each makes "never ran" and "ran, green" indistinguishable in a report. This map caught three instances — `meson subprojects download || true` (hid exit code 2 *and* real network failures), CI's fallback `chmod +x` (hid the `core.fileMode` trap that made a real clone fail to build), and this one. Stated as a principle rather than three bans, because a list of bans only blocks the shapes already encountered.

### Required contracts

Selection principle: **only decisions that measurement or research overturned an intuition about**. A gate's job is to stop a future implementer from reverting a decision by intuition; intuitive behaviour is covered by ordinary smoke tests and review. The evidence for this rule comes from this map: two reference implementations' connectivity tests missed the "create a channel after connecting" race entirely — not from laziness, but because that path is not the one intuition reaches for.

| Contract | Tier |
|----------|------|
| Oversized DataChannel label rejected at both C# and dcu layers (bound: 65535) | Native / EditMode |
| DataChannel created **after** connect *and* **before** connect — both paths | Native / PlayMode |
| `TOO_SMALL` retry is idempotent: oversized payload succeeds after exactly one retry, consumed exactly once | Native / EditMode |
| Re-entrancy during dispatch (Dispose/Create a channel inside a callback) does not escape the pump | Native / EditMode |
| One throwing subscriber does not stop delivery to the others | Native / EditMode |
| Zero-length messages are legal; `Send` offset bounds hold near `int.MaxValue` | Native / EditMode |
| Buffer shrinks back to baseline after two quiet windows | Native / EditMode |
| Pump self-heals after a third party overwrites `SetPlayerLoop` | Native / PlayMode |
| Upstream state/exception mapping; out-of-range raw values map to `Unknown` (never throw) | Native / EditMode |
| Log bridge survives repeated `Level` changes (regression for the silent-detach trap, §7) | Native / EditMode |
| Domain-reload lifecycle | Native — **manual step permitted** (see below) |

Managed tier additionally covers: native-config marshalling alloc/free balance, `Dispose` idempotence for managed-only types, and ICE-credential redaction — the last **through the public logging entry point**, not by exposing internals. `Dispose` idempotence is deliberately split across two tiers rather than written as one contract, so that "Dispose idempotence is covered" cannot mask the native side being untested.

**Internal implementation details are not opened up for testability.** The redaction test does not get `InternalsVisibleTo`; it logs a malformed TURN URL carrying credentials and asserts the output reads `credentials=redacted@`. This is not fastidiousness — the real contract is "credentials are not in the log", not "the regex looks like this", and going through the public path additionally covers *whether redaction is wired into the logging path at all*, which calling the internal method directly cannot.

**No C++ test target.** The runtime contract tests that replace the dead `static_assert` are all triggerable from C# through P/Invoke (garbage SDP, oversized labels, malformed ICE URLs all reach the C++ catch). `native/` has no test infrastructure today and building one means a new target plus a new CI step. **Known cost:** "every upstream exception type is covered" cannot be guaranteed mechanically, only the paths reachable from the public surface — which are also the only paths a user can reach.

### Suite-level teardown

Every tier asserts on completion:

- `dcu_shutdown()` reports **0** undestroyed objects
- `dcu_event_queue_depth()` is **0** (control queue drained)

Both are far better than grepping the Console for an English log line, and neither depends on the log bridge.

### Manual steps must still be machine-judged

Domain-reload verification cannot live in the test framework — the framework does not survive the reload. It is verified by a **persistent Editor probe script**; a dynamically compiled probe is destroyed along with the domain, so measuring the transition with something the transition destroys does not work. The probe must emit a **machine-assertable artifact** (e.g. a live-object count in a file or `SessionState`); reading a log line out of the Console does not satisfy this gate — that is the same disease as `|| true`, in its fourth form.

### Upstream-upgrade gate

Upgrading libdatachannel requires, in addition to the tiers above:

1. `native/versions.lock` updated per the semver policy (§3)
2. **Runtime contract tests green** — this *replaces* the compile-time `static_assert` gate, which the C++ API migration (§2) makes impossible
3. Exported-symbol list diff clean (a difference means upstream leaked symbols)
4. All tiers plus the dual-peer PlayMode smoke green

> **Known gap:** step 2 catches values *appended* to an upstream enum (out of range → `Unknown`) but **not values inserted in the middle**, which silently change the meaning of existing values while every assertion stays green. C++ enums are not reflectable; this gap is left open deliberately.

"Read the upstream CHANGELOG by hand" is deliberately **not** on this list — it is the one item no machine can enforce, and keeping it would dilute the four that can.

### Exported-symbol list

`native/exports/expected-symbols.txt` holds the symbol **names**, not a count; the audit script diffs actual exports against it and exits non-zero on any difference. A count passes a rename (`dcu_dc_state` → `dcu_dc_stat`) and an accidental addition that offsets a removal. Changing it deliberately puts the change in the same diff as the `DCU_ABI_VERSION` bump, so a forgotten bump is visible in review.

Machine-enforcing "list changed ⇒ version must bump" is **not** adopted: it requires reading git history, which is fragile under shallow clones and CI, for the sole gain of turning review-visible into machine-enforced.

### Reproducibility

Clean-clone reproducibility is already gated: CI's `actions/checkout` *is* a real clone, restoring file modes from git, and the mac build job runs with no `chmod` and an explicit assertion that the scripts are executable. No new CI job is needed.

One local rule, though: **reproducibility must be verified with a real `git clone` into a temporary directory.** `cp` / `rsync` do not count — `rsync -a` preserves working-tree permissions, which is precisely how a "clean clone builds" claim once passed while a real clone failed with `permission denied` (this repo has `core.fileMode = false`, so five scripts had been committed as `100644`).

Deferred: device farm, fault injection, automated WebGL browser CI, back-pressure saturation tests (they need a channel flooded to 1024 — slow and fragile), libjuice's delayed log-level application (an upstream limitation; testing it tests upstream), weak-reference collection timing (needs a forced `GC.Collect()`, the archetypal fragile test).

### Where the gates are written down

Three layers plus an entry point, deliberately separated:

| Layer | File | Contains |
|-------|------|----------|
| **Specification** (tool-agnostic: *what* must be verified) | `docs/SPEC.md` §11 | This section. It names no MCP server — tooling is an implementation detail |
| **Gate text** (*what to do, when*) | `CONTRIBUTING.md` | The "changing the implementation" and "upgrading upstream" checklists |
| **Manual** (*how, in this project*) | `docs/verification-mcp.md` | Concrete Unity MCP steps |
| **Entry point** (*how it gets read*) | `CLAUDE.md` | A pointer to `CONTRIBUTING.md`, auto-loaded by agents each session |

The "changing native requires MCP self-verification" rule was already written down and still had no effect — not because it was missing, but because nobody was forced to encounter it. The entry-point layer exists to fix that, not to restate the rule.

---

## 12. License and compliance

**Research:** [#5](https://github.com/xuhuanhello/juice-c-sharp/issues/5), `docs/research/mpl-upm-binaries.md`

libdatachannel is **MPL-2.0** (use ≥ 0.18; avoid historical LGPL lines). datachannel-wasm is **MIT**.

### Minimum package checklist

1. Full **MPL-2.0** text for Covered Software as required.  
2. **ThirdPartyNotices** mapping each prebuilt binary → exact source tag/commit and how to obtain Source Code Form (GitHub tags OK if unmodified).  
3. Preserve copyright/notices in distributed forms.  
4. Document transitive deps (libjuice also MPL-2.0, usrsctp, MbedTLS, etc.).  
5. MIT notice for datachannel-wasm / jslib origins.  
6. New C ABI / C# files with **no** MPL code may use a separate license (e.g. MIT) as Larger Work; do not copy MPL code into them without keeping MPL.  
7. Do not add EULA terms that strip MPL source rights.

*Informational research only — not legal advice.*

---

## 13. Suggested repository layout (implementation)

```text
/
  CONTEXT.md
  CLAUDE.md                 ← pointer to CONTRIBUTING.md (agent entry point)
  CONTRIBUTING.md           ← gate checklists
  docs/
    SPEC.md                 ← this file
    verification-mcp.md     ← Unity MCP manual
    research/               ← closed research notes
  Packages/
    datachannel-unity/      ← UPM package
      package.json
      Runtime/
      Plugins/              ← LFS binaries
      Samples~/
      Tests/                ← three test assemblies (§11)
  native/
    CMakeLists.txt          ← the only build entry
    versions.lock
    cross/                  ← CMake toolchain files
    dcu/                    ← stable C ABI sources (include/ + src/)
    exports/                ← per-platform allowlists + expected-symbols.txt
    scripts/                ← fetch-deps, build wrappers, audit
    subprojects/            ← pinned upstream checkouts (not committed)
  .github/workflows/        ← GHA
```

---

## 14. Implementation checklist

Steps 1–3 are **ordered normatively** (§2); the rest is a suggested order.

The scaffold, the CMake build, the desktop plugin and a first pass at the event pump and ICE marshalling already exist. What remains is the hardening described in §4/§6/§7 — and the first move is not any of it.

1. **Migrate `dcu_impl.cpp` to the libdatachannel C++ API** (§2), preserving the three invariants listed there. Behaviour must not change: the pre-migration baseline is recorded in [#42](https://github.com/xuhuanhello/juice-c-sharp/issues/42) (offline audit, EditMode green, native create/dispose, dual-peer loopback, zero Console errors) and must be reproduced afterwards. **Do not** build the handle table against the C API first as a stepping stone — that writes it twice and defers exactly the hard part (handle allocation and liveness, type discrimination, cross-thread table locking) that the C API is currently doing for us.
2. **Rewrite the ABI surface** (§4): `dcu_event_next`, `dcu_dc_receive`, `dcu_dc_state`, `dcu_log_next`, `dcu_event_queue_depth`; return-code + out-param throughout; independent error numbering; explicit state mapping; `DCU_ABI_VERSION` → 2; update `native/exports/expected-symbols.txt` in the same commit.
3. **Rewrite the managed layer** (§6): ownership and cascade disposal, weak lookup table, enqueue-only finalizers, the two-segment pump with per-subscriber isolation and snapshot iteration, the `fixed` send path, the naming and visibility changes, config validation.
4. **Log bridge and credential path** (§7): bounded log queue, non-detaching level changes, structured `rtc::IceServer` assignment, one-directional initialization.
5. **Lifecycle wiring** (§6): the five domain / play-mode / quit scenarios.
6. **Tests** (§11): three assemblies, the required-contract list, the exported-symbol diff, the persistent domain-reload probe; delete `Assets/DataChannelVerify/`.
7. Expand plugins: Android → iOS → Win arm64 → WebGL (+ jslib).
8. `CONTRIBUTING.md` gates in force; GHA + LFS maintainer flow.
9. ThirdPartyNotices + README (signaling ownership, signing, platforms).

---

## 15. Decision index

### Map [#1](https://github.com/xuhuanhello/juice-c-sharp/issues/1) — UPM architecture and platform spec

| Topic | Issue | Section |
|-------|-------|---------|
| libdatachannel C API research | [#2](https://github.com/xuhuanhello/juice-c-sharp/issues/2) | §4 |
| WebGL / datachannel-wasm research | [#3](https://github.com/xuhuanhello/juice-c-sharp/issues/3) | §8 |
| Meson + CMake research | [#4](https://github.com/xuhuanhello/juice-c-sharp/issues/4) | §9 |
| MPL / binaries research | [#5](https://github.com/xuhuanhello/juice-c-sharp/issues/5) | §12 |
| dc-unity autopsy | [#6](https://github.com/xuhuanhello/juice-c-sharp/issues/6) | §1 |
| Stable C ABI v1 surface | [#7](https://github.com/xuhuanhello/juice-c-sharp/issues/7) | §4 |
| C# API and threading | [#8](https://github.com/xuhuanhello/juice-c-sharp/issues/8) | §6 |
| ICE config injection | [#9](https://github.com/xuhuanhello/juice-c-sharp/issues/9) | §5 |
| Plugins matrix | [#10](https://github.com/xuhuanhello/juice-c-sharp/issues/10) | §8 |
| Upstream pins | [#11](https://github.com/xuhuanhello/juice-c-sharp/issues/11) | §3 |
| Logging and diagnostics tier | [#12](https://github.com/xuhuanhello/juice-c-sharp/issues/12) | §7 |
| CI / signing | [#13](https://github.com/xuhuanhello/juice-c-sharp/issues/13) | §10 |
| Sample and test matrix | [#14](https://github.com/xuhuanhello/juice-c-sharp/issues/14) | §11 |
| Write this SPEC | [#15](https://github.com/xuhuanhello/juice-c-sharp/issues/15) | — |

### Map [#16](https://github.com/xuhuanhello/juice-c-sharp/issues/16) — native packaging hardening

| Topic | Issue | Section |
|-------|-------|---------|
| Static crypto (MbedTLS 3.6 vs OpenSSL) | [#17](https://github.com/xuhuanhello/juice-c-sharp/issues/17) | §3, §9 |
| Symbol hiding / `dcu_*`-only exports | [#18](https://github.com/xuhuanhello/juice-c-sharp/issues/18) | §3 |
| macOS single artifact shape | [#19](https://github.com/xuhuanhello/juice-c-sharp/issues/19) | §3, §8 |
| CI matrix vs local mac | [#20](https://github.com/xuhuanhello/juice-c-sharp/issues/20) | §10 |
| MCP self-verification checklist | [#21](https://github.com/xuhuanhello/juice-c-sharp/issues/21) | §11 |
| Packaging decisions written back | [#22](https://github.com/xuhuanhello/juice-c-sharp/issues/22) | — |
| ~~Meson as the only build entry~~ | [#23](https://github.com/xuhuanhello/juice-c-sharp/issues/23) **rescinded** | §9 |
| Meson subprojects static graph research | [#24](https://github.com/xuhuanhello/juice-c-sharp/issues/24) | §9 |
| ~~Replace shell builds with Meson~~ | [#25](https://github.com/xuhuanhello/juice-c-sharp/issues/25) **rescinded** | §9 |

### Map [#26](https://github.com/xuhuanhello/juice-c-sharp/issues/26) — implementation hardening

| Topic | Issue | Section |
|-------|-------|---------|
| Clean-clone reproducible build; **Meson → CMake-only** | [#27](https://github.com/xuhuanhello/juice-c-sharp/issues/27) | §9 |
| Native plugin lifecycle research (domain reload) | [#28](https://github.com/xuhuanhello/juice-c-sharp/issues/28) | §6 |
| Ownership and lifetime model | [#29](https://github.com/xuhuanhello/juice-c-sharp/issues/29) | §6 |
| Event ABI atomicity, single consumer, back-pressure | [#30](https://github.com/xuhuanhello/juice-c-sharp/issues/30) | §4, §8 |
| Error codes and enum boundaries | [#31](https://github.com/xuhuanhello/juice-c-sharp/issues/31) | §4, §6 |
| Callback semantics (open race / label / text) | [#32](https://github.com/xuhuanhello/juice-c-sharp/issues/32) | §4 |
| Log bridge and credential redaction | [#33](https://github.com/xuhuanhello/juice-c-sharp/issues/33) | §5, §7 |
| Public C# naming and config semantics | [#34](https://github.com/xuhuanhello/juice-c-sharp/issues/34) | §5, §6 |
| First commit and Plugins LFS | [#35](https://github.com/xuhuanhello/juice-c-sharp/issues/35) | §9, §10 |
| CI aligned with the CMake entry | [#36](https://github.com/xuhuanhello/juice-c-sharp/issues/36) | §9, §10 |
| Domain reload and application exit | [#37](https://github.com/xuhuanhello/juice-c-sharp/issues/37) | §4, §6 |
| Pump budget, exception isolation, allocation | [#38](https://github.com/xuhuanhello/juice-c-sharp/issues/38) | §6 |
| Verification gates | [#39](https://github.com/xuhuanhello/juice-c-sharp/issues/39) | §11 |
| Write these decisions back | [#40](https://github.com/xuhuanhello/juice-c-sharp/issues/40) | — |
| C API vs C++ API research | [#41](https://github.com/xuhuanhello/juice-c-sharp/issues/41) | §2 |
| C API vs C++ API decision | [#42](https://github.com/xuhuanhello/juice-c-sharp/issues/42) | §2 |

---

## 16. Open after these maps

Not required to start implementation. Each is deliberately unspecified, with the reason.

| Question | Why it is open |
|----------|----------------|
| **WebGL facade's receive buffering** | The native design dissolved the "drop oldest vs drop newest" dilemma via real back-pressure; §8 shows it returns intact on WebGL, where only bounded-discard or unbounded-growth are possible. Decided with the facade |
| **Whether pump thresholds get a configuration surface** | 4 ms slow-frame and 1024 depth are internal constants (§6). A 90 fps VR title and a 30 fps mobile title genuinely differ in tolerance, but no evidence yet says these values are wrong, and a knob costs semantics, spec text and tests |
| **Typed handles** (`SafeHandle` / strongly-typed struct wrappers) | `SafeHandle` is already ruled out (§6); whether to wrap the raw `int` in a struct is cosmetic and untouched |
| **Disconnecting when the app is backgrounded** | Product semantics, not lifecycle hygiene (§6) |
| **Observability beyond `BufferedAmount`** | Queue depth, drop counts, per-frame stats all currently go to logs, which suffice for attribution. Exposing them for application network HUDs should be its own decision, not a rider on another one |
| **Inbound rate limiting / malicious-peer defence** | §1 out of scope: the accepted weakness in §6 has its correct fix at a connection layer that can react per peer — a new mechanism, not a hardening of an existing one |
| **HarmonyOS** | Waiting on Unity/tooling |
| **Implementation milestones / PR slicing** | Separate planning |
| Optional later | Selected-candidate-pair API, device farm CI, WebSocket bindings, FishNet transport mapping |
