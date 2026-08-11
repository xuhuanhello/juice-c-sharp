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
- CI (GitHub Actions) producing plugins; maintainers commit the binaries into git (§10 — deliberately **not** LFS)

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
| Export surface | **Only `dcu_*`**. `native/exports/expected-symbols.txt` is the one hand-written source (undecorated names); the three link-time files (Mach-O list, ELF version script, PE `.def`) are **generated** from it by `gen_exports.py` into the build directory and **never committed** |
| Compile | `-fvisibility=hidden` (+ inlines hidden) on wrapper and deps where possible |
| macOS product | **One** artifact, a **universal** `datachannel_unity.dylib` (arm64 + x86_64) — no per-arch subdirectories, no `.bundle` (§8) |

**Decisions:** [#17](https://github.com/xuhuanhello/juice-c-sharp/issues/17), [#18](https://github.com/xuhuanhello/juice-c-sharp/issues/18), [#19](https://github.com/xuhuanhello/juice-c-sharp/issues/19), [#47](https://github.com/xuhuanhello/juice-c-sharp/issues/47), [#50](https://github.com/xuhuanhello/juice-c-sharp/issues/50)

#### What hidden visibility does and does not buy (measured, [#47](https://github.com/xuhuanhello/juice-c-sharp/issues/47))

`-fvisibility=hidden` **does** propagate into the vendored subprojects. Measured on the shipped macOS artifact: **27** external symbols escape, not the hundreds an earlier reading of #18 assumed. Treat any restatement of a four-digit leak count as stale — the number is in `docs/research/platform-symbol-audit.md`, together with how it was taken.

What it does **not** buy is duplicate-symbol safety in a static library. Two plugins that each vendor MbedTLS both define the same symbols; the default lazy linking **does not error**, it silently binds to whichever came first. We enable `MBEDTLS_SSL_DTLS_SRTP` and another plugin most likely does not, so the failure mode is *the wrong implementation, quietly* — worse than a link error. This is why the iOS `.a` narrows symbols with a one-step `ld -r -exported_symbols_list` (§9) rather than relying on visibility flags alone.

Two constraints follow, and both are load-bearing:

- **`ld -r -exported_symbols_list` can only demote, never promote.** Naming an already-hidden symbol in the list makes `ld` fail outright.
- Therefore **`expected-symbols.txt` must equal the set annotated `DCU_API` in the sources, exactly.** One extra name is a hard link failure on iOS, not a silent pass — which makes the iOS link a second enforcer of the same list the audit diffs against.

The Windows side has its own asymmetry worth stating, because the shape invites the opposite assumption: the export gate on Windows is `DCU_API` (`__declspec(dllexport)`), **not** the `.def` file. A `.def` that is missing symbols passes silently; one naming deleted symbols fails hard (LNK2001). It is kept only as a generated artifact, where neither mode can occur.

### Package semver (this UPM)

| Change | Bump |
|--------|------|
| Upstream patch, no public/API/`dcu_*` break | patch |
| Upstream minor / behavior change, API compatible | minor |
| `dcu_*` or public C# break | major |
| Docs/samples only | patch or docs-only |

Maintainers only bump pins into `main` with rebuild + tests + an entry in **`Packages/datachannel-unity/CHANGELOG.md`** naming the old and new tags. External PRs that only change pins without rebuild evidence are rejected.

Releases are **git tags** (`v<version>`), because that is what a UPM git-URL install pins to; the tag and `package.json`'s `version` must agree.

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

> **Back-pressure points outward, and that is a cost, not only a feature.** The mechanism above couples *local* slowness to *remote* throughput: an application whose frame rate drops, or whose message callback is expensive, will throttle the peer that is sending to it. Nothing is lost, but the peer's send rate falls, and for latency-sensitive traffic the queued messages are also **stale by the time they are delivered** — classic head-of-line blocking.
>
> This is the right default (see the reliability rule below), but it is the wrong behaviour for state synchronisation, where a fresher message strictly supersedes an older one. **That case is served by choosing the channel mode, not by weakening the guarantee** — see §6, *Choosing a channel mode*.

**The rule is not "never lose data" — it is "never lie about the mode the application chose".** An application that opens an unreliable channel expects gaps and writes code that tolerates them. An application that opens a *reliable* channel does not: SCTP has already paid for retransmission and head-of-line blocking on its behalf, so discarding the message afterwards, on our own floor, silently breaks a guarantee it is relying on and may be building deltas, chunked transfers or RPC on top of. A general-purpose P2P library does not get to assume the traffic is periodic state that "the next update will fix".

The same rule read on the control queue is stronger and cheaper: dropping a `DcClosed` leaves the managed state machine permanently desynchronised — the application keeps sending on a dead channel and **no later event corrects it**. Enforcing it costs one decision (don't bound that queue), not a mechanism.

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
| — | new `DataChannelLog.LeakDetection` (`Disabled` / `Enabled`) | Two modes, not three ([#45](https://github.com/xuhuanhello/juice-c-sharp/issues/45)) — see *Ownership and lifetime* |

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
| **Incoming DataChannels are always accepted** | Created and owned by the PC even with no subscriber. Rejecting "unwanted" channels is timing-sensitive: an application that subscribes one frame later (or after an `await`) would have its channel silently closed — far harder to diagnose than a leak. Exceeding a child-count threshold (**1024**, an internal constant, the same order as the control-queue depth) warns once; it never refuses. The real ceiling is elsewhere — SCTP stream ids are `uint16` |
| **An incoming channel whose parent is already gone is destroyed on the spot** | The `IncomingDataChannel` event can be dequeued after the application disposed its `PeerConnection` (or dropped it and let it be collected). There is no owner left to cascade from, and upstream's `rtcDeletePeerConnection` does not erase child entries, so the pump calls `dcu_dc_destroy` on the orphan itself. Dropping the event without this would be a native leak nothing in the process can reach |
| **Leak diagnostics are mandatory** | On by default in Editor / Development builds, off in Release. `Enabled` includes the **creation-time** stack trace |
| **Cascade-disposed children behave like directly-disposed ones** | …except the exception message names the cause. They do **not** raise `Closed`: doing so would run user callbacks while the parent is half-torn-down, and `Closed` should mean "the channel was closed", not "you just released it" |

The leak report is a gated contract (§11) because the intuition here was measurably wrong: with the table holding **strong** references, the object was rooted, so the finalizer could *never* run — the "forgetting `Dispose` is caught by the finalizer" fallback had never once worked. The weak table and the leak report are two halves of one mechanism; testing only one of them tests nothing.

**Finalizers do exactly one thing: enqueue.** A finalizer records the handle and leak info on a lock-free queue; the main-thread pump logs it and removes the table entry. Finalizers must **not** call any `dcu_*` function, must **not** take the handle-table lock, and must **not** call `Debug.LogError` — 2022.3 makes no thread-safety promise for it off the main thread, and P/Invoking `rtcDelete*` from the finalizer thread blocks until callbacks quiesce, which during a domain unload means the Editor hangs on every entry into play mode.

Finalizers exist **only** under `#if UNITY_EDITOR || DEVELOPMENT_BUILD`; `LeakDetection` gates the reporting at runtime. Both layers are needed, not one: having a finalizer at all is a per-type cost (every instance is queued for finalization and survives an extra GC generation) that no runtime switch can remove. Unity's own `DisposeSentinel` is conditionally compiled for the same reason.

**Two modes, not three** ([#45](https://github.com/xuhuanhello/juice-c-sharp/issues/45)): `Enabled` captures the creation stack. A middle mode that reports leaks *without* the stack was dropped — "where was it created" is essentially the whole diagnostic payload, and a third mode costs its own semantics, spec text and tests. Object creation is not a hot path (single digits to a few dozen per session), so the capture is affordable; an application that creates far more can select `Disabled`.

Removing leak diagnostics **entirely** was considered and rejected, even though it would delete the finalizer machinery outright — a genuine structural simplification. Forgetting `Dispose` is the highest-probability failure on this list, and the only fallback (§4, `dcu_shutdown`'s undestroyed count) reports *that* something leaked, never *which object* or *where it came from* — a needle-in-haystack for anyone consuming this as a package.

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

> **Accepted weakness, stated plainly — and it runs in both directions.**
>
> *Inward:* a fast peer can stretch the pump segment linearly with its traffic, and nothing stands in the way. The correct fix is a per-connection inbound rate limit with disconnection, at a layer that can react to *that peer* — out of scope here (§1).
>
> *Outward:* because draining is what keeps the upstream queue empty, **a slow frame here throttles the peer sending to us** (§4). A hitch in the application propagates across the wire as reduced send rate for the remote side. This is inherent to back-pressure, not a defect, but it must not be discovered in the field: it is the reason the slow-frame warning below exists, and the reason state-sync traffic belongs on an unreliable channel.
>
> Because both are accepted, both must at least be **visible**: see below.

#### Observability

Internal constants; **no configuration surface** (a knob has to be given semantics, specified, and tested, and there is no evidence these values are wrong):

| Signal | Threshold |
|--------|-----------|
| Slow pump frame | **4 ms** (of a 16.7 ms frame at 60 fps). `Stopwatch.GetTimestamp()` twice per frame; always compiled in |
| Control-queue backlog | depth > **1024** — same order as upstream's `RECV_QUEUE_LIMIT`; reaching it means the pump is not running or a callback is stuck |
| Pump liveness | Each `Pump()` stamps a monotonic counter and a wall-clock timestamp. `PeerConnection` creation, `CreateDataChannel` and `Send` check "how long since the last pump"; past a seconds-scale threshold, log an error naming the likely cause and fix, then **re-register once** |
| Throttling | One warning per category per **5 s**, carrying the occurrence count and peak for the period |

The liveness check is not about registration failing at startup (nearly impossible) but about being **erased afterwards**: any third-party package that rebuilds from `GetDefaultPlayerLoop()` and calls `SetPlayerLoop` drops our entry while our `_pumpRegistered` flag still says `true`. That is not hypothetical here — R3, already vendored in this repository, inserts into the PlayerLoop.

**The value is detection, not repair.** The failure is loud (nothing works at all) but the *attribution* is terrible — the first suspicion is always the network or the TURN server, never the frame loop. So the error message must name the likely cause and the fix.

**Re-registration is attempted exactly once** ([#45](https://github.com/xuhuanhello/juice-c-sharp/issues/45), narrowing the original "self-heal"). If the entry is erased again, log that retrying has stopped and leave it erased. Repeated silent re-insertion is a tug-of-war with another package over shared state — the same shape as the auto-unsubscribe idea rejected under *Exception isolation* below: **silently changing state that someone else established is worse than a loud log.**

The threshold must not be measured in `Time.frameCount` — unreliable in edit mode, where the pump is resident. Use a **monotonic wall clock**: `Stopwatch.GetTimestamp()`, which the slow-frame warning already samples twice per frame, so it costs nothing extra and needs no `#if UNITY_EDITOR` fork for `EditorApplication.timeSinceStartup`. The two were **measured to be equivalent** across a play/stop cycle — neither is reset by entering or leaving play mode, and the elapsed interval agrees to within the printed precision (28.16 s vs 28.1 s over one cycle). Check only when the application calls an API; never poll in the background.

**Edit mode takes a separate branch, and it is a warning, not an error.** Until the resident edit-mode pump exists (the lifecycle work in §14 step 5), the pump genuinely does not run in edit mode — so reporting it is *correct*, but treating it as "a third party erased us" is not, for three measured reasons:

1. The wording sends the reader after a third-party package that does not exist.
2. Re-registration is **useless** there — edit mode does not run `PlayerLoop`'s `Update` at all.
3. That useless retry **spends the one-shot retry budget**, so when a third party really does erase the entry in play mode, the protection is already gone.

All three were observed: one `new PeerConnection` in edit mode reported *"pump has not run for 934.8 s"* and set the retry flag. Note that checking `_pumpRegistered` alone does **not** guard against this — leaving play mode does not trigger a domain reload (§6, measured again here: the pump tick counter went 4147 → 8358 without resetting and the flag stayed `true`), so a flag set in play mode survives into edit mode. Once the resident edit-mode pump lands, the timestamp keeps advancing and this branch stops firing on its own; **no temporary branch is left for someone to remember to remove.**

`Pump()` also stamps a monotonic counter, reported for diagnosis. Do **not** read "counter is non-zero" as "it was registered, then erased" — a manual `Pump()` call bumps it too.

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
- **Shrink: never.** Resident buffer size settles at the largest payload seen, and that is bounded — see below.
- Log the first growth beyond baseline (Info, with size).

**Why never shrinking is safe** ([#45](https://github.com/xuhuanhello/juice-c-sharp/issues/45), superseding the hysteresis rule): a single message cannot be arbitrarily large. `PeerConnection::remoteMaxMessageSize()` returns the smaller of the local limit (`DEFAULT_LOCAL_MAX_MESSAGE_SIZE` = **256 KB**) and the peer's SDP-advertised value, so with default configuration the buffers settle at roughly **0.5 MB total** (256 KB message + 256 KB payload + 4 KB payload2). Building a two-window peak tracker to reclaim that is not worth its own specification, tests and failure modes. An application that raises `MaxMessageSize` raises the resident bound with it — which is what it asked for, and should be documented as such rather than silently clawed back.

"Fixed capacity plus a temporary array when oversized" remains rejected even though it is simpler: an application whose normal payload is 200 KB (video slices, map data, save sync) would allocate on **every** message, quietly undoing the zero-allocation guarantee under the name of an overflow path. Note that grow-and-keep does *not* have this problem — it preserves zero-allocation and merely holds memory, which is why it survives while the temporary-array scheme does not.

### Editor and application lifecycle

**One principle; the five scenarios follow from it, so there is nothing to memorise:**

> **The domain is about to die** → the managed side is about to lose every reference → the only option is the sledgehammer, `dcu_shutdown()`.  
> **The domain is still alive** → we still hold the references → use the precise tool, `DisposeAllLive()`, and do not swing the hammer.

| Scenario | Action |
|----------|--------|
| **Edit mode** | Pump is **resident** — driven by `EditorApplication.update`, **not** `PlayerLoop` (see below); native init is **lazy**. `beforeAssemblyReload` records "was it initialised" in `SessionState`; `afterAssemblyReload` re-creates **only if it was** |
| `beforeAssemblyReload` (edit-mode recompile, and entering play mode with Reload Domain on) | `DisposeAllLive()` → `UnregisterPump()` → **`dcu_shutdown()`** |
| `ExitingPlayMode` | `DisposeAllLive()` → `UnregisterPump()`, **no shutdown**. Nothing to re-install on `EnteredEditMode` — the edit-mode pump is a separate, always-subscribed `EditorApplication.update` handler |
| Reload Domain **disabled** | `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` → `DisposeAllLive()` + pump dedup, **no shutdown** |
| `EditorApplication.quitting` | Unsubscribe our own handlers first, then `DisposeAllLive()` + full shutdown |
| Player `Application.quitting` | `DisposeAllLive()` only, **no shutdown** |

**The edit-mode pump cannot be a `PlayerLoop` entry — `PlayerLoop` does not run in edit mode.** This was measured, not read off a doc: a pump entry inserted into the tree in edit mode was verifiably present *and* `Pump()` had not run for 934.8 s. An earlier version of this table said "`EnteredEditMode` → `RegisterPump()`", which cannot deliver the resident pump it was asking for. `EditorApplication.update` is the only mechanism that does; it is subscribed once from `[InitializeOnLoadMethod]` and skips itself while `isPlaying`, leaving play mode to the `PlayerLoop` entry so there is exactly one answer to "what is driving the pump right now".

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

#### Choosing a channel mode

The reliability flags are the place where an application decides whether it wants delivery or freshness. Document it, because the default is the safe one, not the fast one:

| Traffic | Mode | Why |
|---------|------|-----|
| State synchronisation (positions, transforms, periodic snapshots) | `Reliable = false`, `Ordered = false` | A newer message strictly supersedes an older one. Reliability buys nothing and costs head-of-line blocking; back-pressure from a slow receiver would throttle the sender for data that is already stale (§4) |
| Commands, events, RPC, deltas, chunked transfers | `Reliable = true` (default) | A gap cannot be recovered by "the next message"; losing one desynchronises or corrupts reassembly |

This is the correct location for the trade-off: the application knows the shape of its traffic, the library does not. **The library's job is to make both modes available and to lie about neither** — which is why a reliable channel never drops on our floor (§4), and why an unreliable one drops without apology.

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

**Decisions:** [#10](https://github.com/xuhuanhello/juice-c-sharp/issues/10), revised by map [#46](https://github.com/xuhuanhello/juice-c-sharp/issues/46) — [#49](https://github.com/xuhuanhello/juice-c-sharp/issues/49), [#52](https://github.com/xuhuanhello/juice-c-sharp/issues/52), [#55](https://github.com/xuhuanhello/juice-c-sharp/issues/55), [#65](https://github.com/xuhuanhello/juice-c-sharp/issues/65)

### Tree

```text
Packages/datachannel-unity/
  package.json                    # name: com.xuhuanhello.datachannel
  Runtime/                        # C# + asmdef
  Editor/                         # PluginPlatformGuard (§10)
  Plugins/                        # binaries and their .meta — nothing else
    Windows/
      x86_64/datachannel_unity.dll
    macOS/
      datachannel_unity.dylib              # ONE universal file: arm64 + x86_64
    Linux/
      x86_64/libdatachannel_unity.so       # note the lib prefix
    Android/
      arm64-v8a/libdatachannel_unity.so    # no libs/ segment
    iOS/
      libdatachannel_unity.a               # device arm64 static
    WebGL/
      libdatachannel_unity.a
      webrtc.jslib                         # no websocket.jslib
  Report~/                        # one build record per shipped binary (§10)
  Samples~/                       # preferred for dual-peer sample
```

**Three shapes in that tree were decided against an earlier version of this spec, and the earlier version is left visible rather than overwritten** — the rule is that changing a decision is a new decision:

- ~~`macOS/x64/…dylib` + `macOS/arm64/…dylib` — thin, one per arch, explicitly no `lipo`~~ → **one universal `.dylib`**. #10's table recorded "no universal" with *no reason attached at all*, and that alone is enough to overturn it: the decision existed, the argument never did. Against it: `CMAKE_OSX_ARCHITECTURES="arm64;x86_64"` produces both in one command with no extra job or toolchain file, the LFS cost is the same (a full-matrix refresh measures ~20 MB either way, [#54](https://github.com/xuhuanhello/juice-c-sharp/issues/54)), and it removes **two artifacts with the same file name**, which [#49](https://github.com/xuhuanhello/juice-c-sharp/issues/49) identified as the actual source of Unity's `CheckFileCollisions` conflict and the reimport error storm.
- ~~macOS ships a `.bundle`~~ → **a plain `.dylib`**. Measured: `CalculateFinalPluginPath` treats the two identically, and Unity's own `com.unity.burst` ships a universal `.dylib`. The directory form imposed four separate special cases — `.gitattributes` had to match by path (the Mach-O inside a `.bundle` has no extension), a second exception was needed for `Info.plist`, `stage_plugin.py` had to build `Contents/MacOS/` and write a plist template, and `gen_support_table.py` needed prefix matching for a directory artifact. **The fourth one shipped a real bug**: the first version matched exactly, so macOS could never be reported as landed.
- ~~`Windows/ARM64/datachannel_unity.dll`~~ → **removed, and not deferred**. Unity 2022.3's Standalone Windows target has **no ARM64 slot**: the editor sources return an empty plugin path for that combination, and multi-architecture Windows only arrives in Unity 6000.0. On 2022.3 the artifact has no consumer — building it produces a binary that cannot be installed into any Player. Confirmed independently from two directions ([#48](https://github.com/xuhuanhello/juice-c-sharp/issues/48) from the platform-support surface, [#49](https://github.com/xuhuanhello/juice-c-sharp/issues/49) from the editor sources and a real Editor's written `.meta`). If this package's minimum Unity is ever raised to 6000.0, that is a new decision, not a resumption of this one.

### Per-platform rules

| Platform | Artifact | DllImport | Editor | Audit: symbols read with | Audit: dependency rule |
|----------|----------|-----------|--------|--------------------------|------------------------|
| Windows x64 | `.dll`, CRT `/MD`, self-contained | `datachannel_unity` | Yes | `dumpbin` | Name allowlist — PE imports carry no path — plus the `api-ms-win-` prefix, since UCRT's API sets change with the CRT version |
| macOS universal | **One** `.dylib`, arm64 + x86_64 | `datachannel_unity` | Yes | `nm` + `otool` + `lipo` | Path-prefix allowlist (`/System/Library/Frameworks/`, `/usr/lib/`, `@loader_path`) |
| Linux x64 | `libdatachannel_unity.so` | `datachannel_unity` | Yes | `nm` + `readelf` | Name allowlist — `DT_NEEDED` carries only sonames |
| Android arm64-v8a | `libdatachannel_unity.so` | `datachannel_unity` | No | `nm` + `readelf` | As Linux |
| iOS arm64 | static `.a`, symbols narrowed by `ld -r` | `__Internal` | No (no simulator v1) | `nm -g` on the archive *(designed, not built — §9)* | **None** — see the gap in §11 |
| WebGL | `.a` + `webrtc.jslib` | `__Internal` | No | — | — |

**A crypto-name ban sits on top of the allowlist**, because macOS really does ship `/usr/lib/libssl.dylib` — a path-prefix rule alone would wave it through. It bans the bundled-crypto family (openssl, mbedtls, gnutls, wolfssl); Windows' `bcrypt.dll` / `crypt32.dll` are **allowed** and are not an exception to it — they are the OS's own crypto API, which libjuice and libdatachannel use for randomness and certificates, not a crypto library of ours that failed to link statically.

**These are allowlists on purpose, not denylists.** The dependency table is the only observable evidence that static linking actually took effect — a build script saying so is not evidence, and this repository has been burned exactly there (§10: a CI job installed brew OpenSSL and was, in fact, broken). A denylist only stops the shapes already encountered. A legitimate new system dependency from upstream is added to the list **with its reason**, in the script.

- Explicit `.meta` for every plugin; do not rely on folder magic alone. Every `.meta` under `Plugins/` is **generated and diffed in CI** — see §10 and §11 for the mechanism and its limits.
- **`Plugins/` holds binaries and their `.meta`, nothing else.** Build records live in `Report~/` (§10). A pure build record is not an asset, and minting a GUID for a file that nothing will ever reference is issuing an ID card to a non-asset.
- **Binaries are committed directly to git, not through LFS** (§10 — LFS silently breaks the UPM git-URL install, which is this package's only delivery path). `.meta` files are ordinary git as well. Every landed artifact is a single file, so `.gitattributes` matches **by extension**; the path-matching rules and the `Info.plist` exception that the `.bundle` once required are gone with it.
- Self-contained: crypto + backend deps **static-linked** into the plugin (see §3 linking rules).
- **Linux's `lib` prefix is not cosmetic** — `libdatachannel_unity.so` is what `DllImport("datachannel_unity")` resolves to on ELF, and it is why the Linux artifact name differs from Windows' and macOS'.

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

**Decisions / research:** [#4](https://github.com/xuhuanhello/juice-c-sharp/issues/4), [#24](https://github.com/xuhuanhello/juice-c-sharp/issues/24), ~~[#23](https://github.com/xuhuanhello/juice-c-sharp/issues/23) / [#25](https://github.com/xuhuanhello/juice-c-sharp/issues/25)~~ **rescinded by** [#27](https://github.com/xuhuanhello/juice-c-sharp/issues/27); map [#46](https://github.com/xuhuanhello/juice-c-sharp/issues/46) — [#50](https://github.com/xuhuanhello/juice-c-sharp/issues/50), [#51](https://github.com/xuhuanhello/juice-c-sharp/issues/51), [#55](https://github.com/xuhuanhello/juice-c-sharp/issues/55),  
`docs/research/meson-subprojects-static-graph.md`, `docs/research/platform-symbol-audit.md`, `docs/research/platform-ci-toolchains.md`

### Product entry (local + CI)

```bash
./native/scripts/fetch-deps.sh
cmake -S native -B native/build/macos -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build/macos
# thin wrapper (same path):
./native/scripts/build-macos.sh
python3 ./native/scripts/audit_plugin.py --binary Packages/datachannel-unity/Plugins/macOS/datachannel_unity.dylib --platform darwin --expected native/exports/expected-symbols.txt
```

**Per-platform entry.** All three landed platforms build **natively on their own host**; only the generator differs. Windows uses CMake's Visual Studio generator because it locates MSVC by itself — no pre-`vcvars` step, and therefore no third-party action to set one up:

| Target | Configure | Build |
|--------|-----------|-------|
| macOS universal | `-G Ninja -DCMAKE_BUILD_TYPE=Release` | `cmake --build <dir>` |
| Linux x64 | `-G Ninja -DCMAKE_BUILD_TYPE=Release` | `cmake --build <dir>` |
| Windows x64 | `-G "Visual Studio 17 2022" -A x64` (multi-config) | `cmake --build <dir> --config Release` |

macOS needs no `-DCMAKE_OSX_ARCHITECTURES`: `native/CMakeLists.txt` defaults it to `arm64;x86_64`, so the universal artifact is what a plain configure produces.

**`native/cross/android-arm64.cmake` is a thin NDK toolchain wrapper.** Android is the one landed cross-compiled target: the wrapper finds `ANDROID_NDK_ROOT` / `ANDROID_NDK_HOME`, fixes `ANDROID_ABI=arm64-v8a`, `ANDROID_PLATFORM=android-22`, and includes the NDK's own `build/cmake/android.toolchain.cmake`. It deliberately does not recreate CMake's built-in Android support, which the NDK does not support or test. iOS is still the target expected to need a project-owned toolchain file.

| Rule | Detail |
|------|--------|
| **CMake is the only product entry** | `native/CMakeLists.txt` — same for local mac and CI; no “dev uses brew openssl .a + clang” product path |
| **Sources in `subprojects/`** | `mbedtls` @ lock, `libdatachannel` @ lock — fetched by `fetch-deps.sh`, never committed |
| **Staging + audit** | `POST_BUILD` custom commands on the plugin target; no shell shim in between |
| **Platform mapping** | `CMAKE_SYSTEM_NAME` / `CMAKE_SYSTEM_PROCESSOR`, so it **follows the toolchain file** if one is ever supplied; `native/cross/` is reserved for CMake toolchain files and is currently empty (above) |
| **MbedTLS** | Built from **subprojects source** with `MBEDTLS_USER_CONFIG_FILE` → `MBEDTLS_SSL_DTLS_SRTP`; static `.a` only; injected as `MbedTLS::MbedTLS` into libdatachannel (**not** brew find_package) |
| **libdatachannel** | `USE_MBEDTLS=ON`, `BUILD_SHARED_LIBS=OFF`, `NO_MEDIA/NO_WEBSOCKET`, hidden visibility |
| **Exports** | `expected-symbols.txt` is the single hand-written list; the per-platform link-time files are **generated** from it (`gen_exports.py`) and git-ignored, so an ABI change is one file plus `DCU_ABI_VERSION` and cannot drift (§3, §11) |
| **Install** | `stage_plugin.py` (POST_BUILD) stages one artifact per platform into UPM `Plugins/` — universal `.dylib`, `.dll`, `lib….so` (§8) |
| **Generated alongside** | `gen_plugin_meta.py` writes the artifact's `.meta`, `gen_build_info.py` writes its build record into `Report~/` (§10) — same POST_BUILD chain, same Python-called-from-CMake shape |
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

### Tooling and declared floors

**The post-build tools are Python, invoked from CMake `POST_BUILD`** — `stage_plugin.py`, `audit_plugin.py`, `gen_plugin_meta.py`, `gen_build_info.py`. A `cmake -P` script was chosen first and then reversed on measurement: in script mode CMake defines **none** of `CMAKE_NM` / `CMAKE_OBJDUMP` / `CMAKE_LINKER` — not even `CMAKE_SYSTEM_NAME` — because `CMakeFindBinUtils` runs at configure time inside `project()`. Its one remaining advantage over a shell script (immunity to CRLF and to the executable-bit trap on Windows) Python has too, with better text handling. Tool paths are passed in from CMake either way.

Two constraints that came out of the same investigation and still hold:

- **No `sed` / `grep` / `cut` in any build-time tool.** They do not exist on Windows runners; swapping bash's traps for coreutils' traps is not progress.
- **Under MSVC, CMake exposes only `LINKER MT AR`** — no `NM`, no `dumpbin`. `dumpbin.exe` sits next to `link.exe`, so derive it from `CMAKE_LINKER`. This also disposes of "dumpbin is in the image but not on `PATH`".

**Toolchain versions are pinned explicitly, not left to the runner default** ([#51](https://github.com/xuhuanhello/juice-c-sharp/issues/51)): Xcode via `xcode-select` (26.6, matching the development machine), MSVC via the `windows-2022` image rather than `windows-latest` (which now means VS 2026, where the generator name `Visual Studio 17 2022` does not resolve at all). A hosted mac runner cannot be locked the way a container can — Xcode does not install into a Linux image — so selecting the version explicitly is the only equivalent available, and without it GitHub rotating an image silently changes our deployment target and linker behaviour.

**Declared Linux floor: Ubuntu 22.04 / glibc 2.35.** This is a **declaration, not a measurement** — there is no Docker build and no verified compatibility floor below it. It follows from `runs-on: ubuntu-22.04` being the oldest surviving Ubuntu label (`ubuntu-20.04` was retired), and it is **one notch above what Unity 2022.3 itself declares** (Ubuntu 20.04 / glibc 2.31), so adopters must be told rather than left to discover it on an old system. Carrying a whole Docker build path for a distribution that reached end of standard support in 2025-05 is not worth it; declaring the floor is the honest alternative.

### iOS symbol narrowing (decided, not yet built)

The static `.a` narrows its symbols in **one step**, `ld -r -exported_symbols_list`, not by renaming with a prefix:

```
ar x <archives>                    # explode to .o
ld -r -arch <arch> -platform_version <plat> <min> <sdk> \
   -exported_symbols_list <generated list> -o combined.o <all .o>
ar crs libdatachannel_unity.a combined.o
```

Verified end-to-end on a real iPhoneOS SDK: non-allowlisted symbols go `T` → `t`, cross-object references stay intact (`nm -u` empty), the final link exits 0, and a second archive defining the same names links alongside without collision. Prefix renaming is deliberately **not** copied from the reference implementation that does it: that project exports `sqlite3_*`, which *must* collide with the system libsqlite3; we export `dcu_*`, which does not, and our upstream is ~195 sources plus MbedTLS's build-time code generation rather than a single-file amalgamation.

The consequence worth keeping: after narrowing, `nm -g` on the archive really does show only `dcu_*`, so **the iOS export gate is not a vacuous assertion** — it is as strict as the other platforms'. One thing is left to measure when iOS actually produces an artifact: `ld -r` will also localise libc++'s weak symbols (vtables, typeinfo, template instantiations). Our boundary is pure C with no C++ types crossing it, so the expectation is a size cost rather than a correctness problem — expectation, not proof.

### Phased platforms

**Android arm64-v8a has landed alongside the desktop batch:** it is CI cross-compiled with NDK r27, its `PT_LOAD` segments are audited at `>= 0x4000`, its generated PluginImporter metadata declares `Is16KbAligned: true`, and the package ships CI provenance at `Report~/Android-arm64-v8a.json`. The remaining targets are:

| Remaining | Why it is where it is |
|-----------|-----------------------|
| **WebGL** | Out of scope for now (§16 and map [#46](https://github.com/xuhuanhello/juice-c-sharp/issues/46)): not one more toolchain but a different behaviour contract — it needs the datachannel-wasm C facade, and §8 records that the back-pressure guarantee is physically unavailable there |
| **iOS arm64** | The cost is in `native/`, not in CI: `add_library(... SHARED ...)` is hard-coded, the staging step branches on `if(APPLE)` and would build a `.bundle` around a static library, the audit has no static-archive branch, and the `ld -r` narrowing above is designed but unbuilt. **Today the target produces no artifact at all** |
| ~~Windows arm64~~ | **Out of the matrix** — no ARM64 slot on 2022.3's Standalone Windows (§8) |

> **Correction, measured:** an earlier version of this table recorded Android as blocked by a floor conflict — "Unity 2022.3 supports API 22, but libjuice's `getifaddrs` needs API 24". **That does not hold at the pinned versions, and the claim was inferred from a grep rather than from reading the code.** libjuice's `socket.h` defines `NO_IFADDRS` for `__ANDROID__` *unconditionally*, and `udp.c` takes a `SIOCGIFCONF` branch there instead — it never calls `getifaddrs` on Android at any API level. libdatachannel itself does not call it, and usrsctp's only use sits inside `#if defined(__APPLE__) || defined(__DragonFly__) || defined(__FreeBSD__)`.
>
> **Resolution:** Android is built at `ANDROID_PLATFORM=android-22`; the NDK link against API-22 stubs, CI audit and 16 KB-device AAB smoke all pass. Upstream's internal candidate-gathering route is not an acceptance criterion for this binding: the package verifies that its own native library loads and dual-peer traffic flows, and does not prescribe libdatachannel's platform internals.

The desktop three did not arrive by copying a template platform one after another; they came up **side by side in one matrix**, and Android joined by extending the same three data boundaries: the artifact's name and shape (`stage_plugin.py`), the tools used to read symbols and dependencies (`audit_plugin.py`), and the shape of the dependency allowlist (paths on Mach-O, names on PE and ELF).

---

## 10. CI, binary distribution, and signing

**Decisions:** [#13](https://github.com/xuhuanhello/juice-c-sharp/issues/13), [#20](https://github.com/xuhuanhello/juice-c-sharp/issues/20), workflow alignment [#36](https://github.com/xuhuanhello/juice-c-sharp/issues/36), first commit and LFS [#35](https://github.com/xuhuanhello/juice-c-sharp/issues/35); map [#46](https://github.com/xuhuanhello/juice-c-sharp/issues/46) — [#51](https://github.com/xuhuanhello/juice-c-sharp/issues/51), [#53](https://github.com/xuhuanhello/juice-c-sharp/issues/53), [#54](https://github.com/xuhuanhello/juice-c-sharp/issues/54), [#65](https://github.com/xuhuanhello/juice-c-sharp/issues/65), [#68](https://github.com/xuhuanhello/juice-c-sharp/issues/68)

### Local vs CI

| Role | Builds | Checks |
|------|--------|--------|
| **Local (default)** | **mac only** (`native/scripts/build-macos.sh` — one universal artifact) | Developer may run `audit_plugin.py` |
| **CI** | **Full matrix** | Link audit (no system crypto dylibs), export allowlist (`dcu_*` only), script-executable-bit assertion, `.meta` and support-table regeneration diff, shell/Python syntax. **No Unity, therefore no C# tests** — §11 |

### Rules the workflows must keep

- **No dependency installation that contradicts §3/§9.** `brew install openssl@3` + `OPENSSL_ROOT` was removed: it directly conflicts with the fully static vendored MbedTLS product path, which means that job had in fact been broken.
- **No fallback `chmod +x`, and do not test the filesystem bit either.** The workflow asserts on **`git ls-files -s native/scripts/`** — every entry must be `100755` — and on failure prints the fix (`git update-index --chmod=+x`). Testing `-x` on disk is worse than useless on Windows: under Git Bash the bit is fabricated from the shebang when msys2 has no ACLs, so the assertion is **always true** and the exact regression it exists for passes green. The git index mode is the thing that was actually lost in [#35](https://github.com/xuhuanhello/juice-c-sharp/issues/35), and it reads identically on every runner.
- **`defaults.run.shell: bash` on every job that runs shell.** Windows runners default to `pwsh`, where the assertion above is a *syntax error* — and an assertion that cannot run is **always false**, the same disease from the other side.
- **CRLF is fixed in `.gitattributes`, not in the workflow.** Runner images carry a system-level `core.autocrlf=true` that `actions/checkout` does not override, which turns `set -o pipefail` into `invalid option name` and makes `versions.lock` parse as `v0.24.5\r`. A repository rule also protects local Windows developers; a job-level setting protects only CI.
- **No Unity job, and the workflow says why in place.** A licensed Unity job was removed rather than left permanently skipped ([#43](https://github.com/xuhuanhello/juice-c-sharp/issues/43), §11) — a job that will never be enabled is a gate-shaped thing that is not a gate. The comment left in its place exists so the next person does not reach for a licence again.
- **Artifacts upload with their build record.** Uploading the binary alone leaves the maintainer holding no provenance, and a locally regenerated record has `ci: null`, which the landing gate below (correctly) rejects — so a CI artifact would be impossible to land. Both paths share the package root as their common ancestor, so the zip contains `Plugins/…` and `Report~/…` — exactly the shape it has to be unpacked into.

### Landing binaries: in batches, not all at once

~~Plugin binaries are not committed until the matrix produces all of them — a partial matrix is worse than none.~~ **Overturned ([#53](https://github.com/xuhuanhello/juice-c-sharp/issues/53)): binaries land in batches.** Desktop (macOS universal, Windows x64, Linux x64) is the first batch and has landed; Android and iOS are the second.

The original rule was not wrong for its context — it was written when **nobody knew which platforms would work**, and a partial matrix then meant an adopter discovering the gap as a `DllNotFoundException` on an end user's device. Two mechanisms have since removed that context, and both are load-bearing for the reversal:

- **A build-time guard.** `PluginPlatformGuard` (`IPreprocessBuildWithReport`) **fails the build** when the target platform has no binary under `Plugins/`, naming the platform and pointing at the support table. Without it, batching would be using adopters as the detector: the default behaviour is a successful build followed by a `DllNotFoundException` on the user's device that is indistinguishable from "file missing" ([#49](https://github.com/xuhuanhello/juice-c-sharp/issues/49)).
- **A support list that cannot lie.** Its authoritative source is **the contents of `Plugins/` itself**. Because a binary is only committed after that platform's on-device smoke passes, "is there a binary" and "was this platform verified" are the *same fact*, and the directory cannot go stale against itself. The human-readable table in the package README is **generated** from it by `gen_support_table.py` and diffed in CI — the same produce-and-verify-with-one-mechanism shape as the `.meta` generator (§11).

**A batch may be committed when, and only when:** every platform in it is green in CI (build + exported-symbol diff + dependency allowlist); each has a **real-device machine-judged smoke result** attached to its ticket (§11); and the `.meta` and support-table regeneration diffs are clean.

**Those conditions are the whole trigger — there is no schedule.** Android arm64-v8a satisfied them and is landed. iOS remains a separate, unfinished second-batch platform; a batch is not owed a release date.

### Plugin binaries are committed directly — **not** through Git LFS

~~Plugin binaries are LFS-tracked; keeping them in LFS is what preserves the UPM git-URL install.~~ **Overturned ([#73](https://github.com/xuhuanhello/juice-c-sharp/issues/73)), and the reason is the exact inverse of the original one: LFS is what *breaks* the git-URL install.**

What happens is silent by construction. UPM clones the repository; if the Git LFS objects are not fetched — the client is missing, credentials are missing, or the `?path=` subfolder form defeats the `.gitattributes` lookup — it checks out the **pointer files**, a 132-byte text stub, with no error and no warning. Unity then loads that stub and reports `expected x64 architecture, but was Unknown architecture`. **v0.1.0 shipped this way on all three platforms**, and every gate was green while it did.

The original argument for LFS was repository size. [#54](https://github.com/xuhuanhello/juice-c-sharp/issues/54) measured a full-matrix refresh at ~20 MB, which is precisely the measurement that makes the argument collapse: LFS was buying a size saving that does not matter, at the price of the package's only delivery path. Unity's own documentation now advises against putting a package's essential assets under LFS.

Committing the bytes directly is also **verifiable here**, which the alternative is not: a clone with `GIT_LFS_SKIP_SMUDGE=1` yields real binaries, so the result cannot depend on any LFS behaviour at all. The rejected alternative — a second `.gitattributes` at the package root, which Unity documents as the supported location for `?path=` installs — leaves correctness resting on the adopter's LFS client, credentials and clone depth, each failing the same silent way.

Two consequences worth stating:

- **`-filter` is the operative attribute.** Unity's `.gitattributes` template routes `*.dll` / `*.so` / `*.a` through an `[attr]lfs` macro that expands to `filter=lfs diff=lfs merge=lfs -text`. The `binary` macro is only `-diff -merge -text` and **does not touch `filter`**. Marking the plugin paths `binary` alone leaves `.dll` and `.so` in LFS while `.dylib` — absent from the template — appears fixed. That asymmetry is why this first read as "only Windows is broken".
- **A gate now asserts what git stores**, not what is on disk: every landed binary's blob must begin with the right magic (`MZ`, `\x7fELF`, Mach-O, `!<arch>`). Nothing else could have caught this — every other check reads the working tree, where the developer's smudge filter has already substituted the real bytes, and the audit inspects a freshly built artifact rather than the landed one. It asserts what the artifact **is** rather than how the filter is configured, because an attribute check would pass on an empty file or a truncated one.

History keeps the old LFS objects; nothing is rewritten. New commits simply store the bytes.

**Refresh policy: on release only, and do not strip.** A full-matrix refresh was measured at roughly **20 MB** ([#54](https://github.com/xuhuanhello/juice-c-sharp/issues/54)) — five times smaller than the estimate that had made storage look like a constraint, so quota does not drive this. What does drive it is that every refresh is permanent history; tying refreshes to releases keeps that history meaningful. Stripping is declined for the opposite reason it is usually adopted: the size it would save is not needed, and symbols are what make a crash report from an adopter actionable.

### Provenance: one build record per shipped binary

Every landed binary has a JSON build record in **`Packages/datachannel-unity/Report~/`**, flat-named after the binary's directory — `macOS.json`, `Windows-x86_64.json`, `Linux-x86_64.json`.

~~The build record sits in the same directory as the binary.~~ **Overturned ([#65](https://github.com/xuhuanhello/juice-c-sharp/issues/65)).** #54 justified the same directory by a use case that does not hold: **no adopter goes rummaging through `Plugins/` for a json file on a hunch.** The real path is a maintainer pointing at it, and the README saying where it is. So: **`Plugins/` holds binaries and their `.meta`, nothing else** (§8), and a pure build record — which no asset will ever reference — does not get a `.meta`, because minting a GUID is issuing an ID card to a non-asset.

`~` makes Unity's asset database ignore the directory outright (the same mechanism as `Samples~`), so the records ship with the package without appearing in the Project window.

**The unforgeable pairing survives the move intact.** The gate still derives the expected record from *the binary's own path*; only the derivation changed by one line. That derivation lives in exactly one place, `gen_plugin_meta.report_name`, shared by the writer (`gen_build_info.py`) and the reader (`gen_support_table.py --check`), so there is no second table for the two ends to disagree about. The record's file name also appears as a **`Build record` column in the generated support table** — for the same reason the platform rows are generated at all: a hand-written "where is the record for my platform" list is one more thing that goes stale on the day a platform lands and nobody remembers the README.

**The adopter-facing path is part of the decision, not incidental documentation.** The package README states where `Report~/` ends up for each install method — `Library/PackageCache/com.xuhuanhello.datachannel@<hash>/Report~/` for a git URL — and asks for the matching record when reporting a bug against a binary. That sentence is the entire replacement for the use case #65 struck down.

**Fields (`schema: 1`):** `schema`, `plugin`, `abi_version`, `platform`, `architectures`, `built_at`, `source.commit`, `upstream`, `toolchain`, `ci`.

**The landing criterion is `ci != null` and `ci.event != pull_request`. There is no third condition.**

- `ci: null` means a local build: no run URL, and its `source.commit` describes the checkout rather than what was compiled.
- A `pull_request` run's commit is GitHub's synthetic merge ref, which stops existing once the PR merges — the binary would become untraceable. Land from a push-to-main run, or from `plugins-matrix.yml`.
- **There is deliberately no `source.dirty` field, and that absence is the point** ([#68](https://github.com/xuhuanhello/juice-c-sharp/issues/68)). It existed briefly and was removed: a CI build *is* a fresh checkout of `source.commit`, so "was the working tree clean" is **constant across the entire population the gate ever reads** and carries no information. The question it appears to answer is answered directly by `ci`. It is written down here because it is the kind of field a reader looking at `ci: null` and `commit` will feel is missing and reinvent. (It was never part of #54's field list either — it arrived with the implementation, unargued, which is why removing it needs no strikethrough: there was no decision to overturn.)
- **`dcu_build_info()` as an exported function stays rejected** (#54): it would change the ABI and destroy the byte-for-byte reproducibility check from [#27](https://github.com/xuhuanhello/juice-c-sharp/issues/27).

### PR vs release

| Gate | Requirement |
|------|-------------|
| **PR** | ~~At least one mac build + audit~~ → **one representative of each of the three toolchain shapes**: macOS (Mach-O / clang / `nm`+`otool`), Windows x64 (PE / MSVC / `dumpbin`), Linux x64 (ELF / gcc / `readelf`). Plus the local checklist in `CONTRIBUTING.md` (not automated — §11) |
| **Release / maintainer binary commit** | The landing conditions above, for every platform in the batch; artifacts → maintainer commit to `Plugins/` + `Report~/` (plain git, no LFS) |

**The PR criterion is new information, not platform count.** A change to `CMakeLists.txt` or `dcu.h` can perfectly well be green on macOS and red on Windows or Linux, and the full matrix only runs weekly — worst case is seven days broken with a pile of commits on top. Conversely a second platform of the *same* shape (Android is ELF like Linux, iOS is Mach-O like macOS) adds nearly nothing on a PR, so those stay in the full matrix.

### GitHub Actions (actual job shape)

In the **full matrix** (`.github/workflows/plugins-matrix.yml`) jobs are split **by host**, each with an internal `strategy.matrix` listing the targets it owns. Same-host targets then share their preparation steps (Xcode / apt / MSVC) and **not one `if:` is needed**; adding a target is adding a matrix row.

| Job | `runs-on` | Targets today | Reserved for |
|-----|-----------|---------------|--------------|
| `macos` | `macos-26` (Xcode pinned to 26.6) | macOS universal | iOS arm64 |
| `ubuntu` | `ubuntu-22.04` | Linux x64 | Android arm64-v8a |
| `windows` | `windows-2022` | Windows x64 | — |

`fail-fast: false` throughout, so one red platform does not hide the state of the others. It runs on dispatch, on a weekly schedule and on release, uploading artifacts for the maintainer to land; **it never pushes to `main` itself.**

The **PR workflow** (`.github/workflows/pr.yml`) needs only one target per host, so it is a single `native` job matrixed over the three, plus a `static-checks` job (shell/Python syntax, `.meta` regeneration diff, support-table regeneration diff). It runs on every PR and every push to `main`. The two workflows will diverge in shape as mobile targets land — the full matrix grows rows, the PR workflow deliberately does not (see the criterion above).

The two disabled platforms are documented **in the workflow, at the end, with the specific thing each is blocked on** rather than left as an optimistic commented-out row. That shape has bitten this repository before: `windows-exports.def` sat in the skeleton listing four deleted symbols and missing six live ones, precisely because nothing ever ran it.

### Signing

- **No** codesign/notarize/Authenticode of plugins in CI or repo.
- **No** Apple/Windows certs stored in the project.
- iOS `.a` unsigned; adopters sign the final Xcode app.
- Document: **adopter owns final app signing and store compliance**.

---

## 11. Samples and testing

**Decisions:** [#14](https://github.com/xuhuanhello/juice-c-sharp/issues/14), [#39](https://github.com/xuhuanhello/juice-c-sharp/issues/39); map [#46](https://github.com/xuhuanhello/juice-c-sharp/issues/46) — [#50](https://github.com/xuhuanhello/juice-c-sharp/issues/50), [#52](https://github.com/xuhuanhello/juice-c-sharp/issues/52), [#53](https://github.com/xuhuanhello/juice-c-sharp/issues/53)

### Sample (required)

- In-process **two PeerConnections** + **in-memory fake signal** (SDP/candidates).
- After connect, send/receive **≥ 1 binary** DataChannel message.
- Lives in `Samples~`. Document how to plug real Signal + IceServers.
- The sample is a **human-facing demo, not a gate**. Its protocol choreography is intentionally duplicated by the PlayMode smoke test: `Samples~` is never compiled by Unity, so no asmdef can reference it, and hoisting the choreography into `Runtime/` would ship test scaffolding in the public API. The two are not copies of one thing — they are two things for two audiences (a readable `MonoBehaviour` with narration; an assertion-only `[UnityTest]`).
- Not required: public STUN/TURN lab scene; FishNet sample.

### Where things run

**Decision:** [#43](https://github.com/xuhuanhello/juice-c-sharp/issues/43)

The dividing line is **"does it need Unity?"** Anything that does runs **locally, in a real Editor**; only what does not need Unity runs in CI.

| | Runs where |
|--|-----------|
| All three C# test tiers (below) | **Local Unity Editor** |
| Per-platform on-device smoke, before that platform's binary lands | **Real device**, machine-judged (below) |
| Native build, exported-symbol diff, dependency allowlist, script-executable-bit assertion | **CI** — one job per toolchain shape (macOS / Windows x64 / Linux x64), on every PR and push to `main` (§10) |
| Shell / Python syntax, `.meta` regeneration diff, support-table regeneration diff | **CI** |

**Unity tests must not be added to CI.** The reason is not cost or convenience: GitHub does not pass repository secrets to `pull_request` runs from a fork, but **repository variables are visible**. Enabling a licensed Unity job upstream therefore means every external contributor's PR starts the job, receives an empty licence, and turns red on something unrelated to their change and unfixable by them. (The converse worry — that a contributor could exfiltrate the maintainer's licence — does not arise for the same reason; that risk belongs to `pull_request_target`, which this repo does not use.)

Tests that need Unity are also simply more trustworthy inside a real Editor: the PlayerLoop, domain reload and plugin loading are exactly the counter-intuitive paths the gate list exists for, and a CI-shaped imitation of them buys less confidence than it costs.

> **Accepted cost, stated rather than hidden: no automated gate covers C# at all.** A compile error in `Runtime/` is caught when the Editor is next opened, not by CI. For a single-maintainer package whose author has the Editor open daily this is a reasonable trade — but nobody should read this spec and believe there is a net under the C# side. **The automated gate that turns red is the native build + audit job.**
>
> Compiling the pure-managed logic into a plain .NET project so `dotnet test` could gate it in CI is **rejected**: it means maintaining a second compilation of the same sources to verify what the maintainer's Editor verifies anyway. Recorded here so it is not repeatedly rediscovered.

### Test tiers

Tier boundary is **"does it need the native plugin loaded?"**

| Tier | Assembly | Content |
|------|----------|---------|
| **Managed** | `DataChannelUnity.Tests.Editor` | Pure C# contracts, zero P/Invoke |
| **Native / EditMode** | `DataChannelUnity.Tests.Editor.Native` | Contracts requiring the plugin |
| **Native / PlayMode** | `DataChannelUnity.Tests.Runtime` | Dual-peer loopback, PlayerLoop pump; headless via `-runTests -testPlatform PlayMode` |

The split survives the decision above, but for a different reason than it was originally given: not CI reachability, but **local ergonomics** — when the plugin is missing or freshly broken, the managed tier still runs, and that is exactly when knowing the pure logic is intact is worth most.

**Tier selection happens at the call site** (assembly filter), never inside a test. Assembly separation is chosen over `[Category("Native")]` because it fails safe: a category would require whoever runs the suite to *remember* an exclusion flag, and the principle below exists precisely to stop relying on remembering.

The PlayMode tier exists for what EditMode structurally cannot cover: `RegisterPump()` installs into `PlayerLoop`, so the smoke test must assert that messages flow **without anyone calling `Pump()` manually**. (Today no verification covers the registration path at all; the Edit-mode loopback calls `Pump()` by hand.)

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
| `PeerConnection.Dispose` cascades to its channels — including **incoming** ones — and a directly-disposed child is not destroyed a second time | Native / EditMode |
| An undisposed object that is collected **is reported**, with its creation stack | Native / EditMode |
| Pump re-registers **once** after a third party overwrites `SetPlayerLoop`, and stops retrying if erased again | Native / PlayMode |
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

It lives at `Assets/DataChannelUnity.Verification/Editor/` — the host project, not the package, since it is not something a consumer of the UPM package should receive. Three menu items under *Tools/DataChannelUnity/域重载自证/*: plant, judge, clear. The artifact is `Library/dcu-domain-reload-probe.json`.

**The judgement.** Planting creates objects and deliberately does not dispose them, **holding them in a static field** — without that they may be collected before the reload, `DisposeAllLive()` would never see them, and the verdict would track GC timing rather than code correctness. After the reload, `dcu_shutdown`'s undestroyed count separates the two outcomes cleanly: **0** means the teardown hooks ran; **exactly the planted count** means they did not run at all; anything between means they ran partially.

**The probe records `EnterPlayModeOptions` before judging**, because that switch decides which of the two paths in §6 is under test — `DisableDomainReload` means entering play mode does *not* reload the domain, so that route exercises `SubsystemRegistration` and never touches `beforeAssemblyReload`. Without sampling the setting first, a green run does not tell you which path it proved.

### Per-platform on-device smoke (gates landing, §10)

CI can prove a binary builds, exports only `dcu_*` and links nothing forbidden. It cannot prove the binary **loads inside Unity** on that platform, because there is no Unity in CI ([#43](https://github.com/xuhuanhello/juice-c-sharp/issues/43)) — and `.meta` mistakes surface only on a real device. So each platform's binary is committed only after one **real-device dual-peer smoke**.

**The device smoke emits a machine-readable test report.** Prefer the existing `DataChannelUnity.Tests.Runtime` assembly built into a Player via the Test Runner and retain its NUnit XML. When the Play-distributed AAB installation path itself is under test, a Player-resident equivalent runner may emit the report from `Application.persistentDataPath`; its report must name the Runtime contracts it exercises, include total/passed/failed counts and failure detail, and must not describe itself as a Unity Test Framework result. In both forms the evidence is a structured file, not a line read out of a Console — reading a log line is the same disease as `|| true`, in its fourth form. The suite-level teardown assertions (`dcu_shutdown()` → 0, `dcu_event_queue_depth()` → 0) are required in the report.

**Zero tests run is a failure, not a pass** — it means the plugin did not load, which is exactly what this step exists to detect.

The report goes on the ticket for that platform; §10's landing conditions require one per platform in the batch. **Stated cost:** the Test Runner route drags the whole test assembly onto the target device; the AAB route carries only its equivalent runner.

### The `.meta` files are generated, and that is also how they are checked

Producing and verifying `.meta` are **one mechanism** ([#52](https://github.com/xuhuanhello/juice-c-sharp/issues/52)): `gen_plugin_meta.py` writes them, and CI re-runs it and diffs. It needs no Unity, so it can live in CI at all — which works out only because of what [#49](https://github.com/xuhuanhello/juice-c-sharp/issues/49) measured: the parts that are checkable as plain text are exactly the parts Unity itself never validates and fails **silently** on.

GUIDs are read back from the existing file and preserved — a landed GUID cannot change.

**One mechanism producing and checking has an inherent hole**: if the generator itself is wrong, its output and the repository agree forever and the gate is permanently green. That is closed by **golden samples** in `native/exports/plugin-meta-golden/` — bytes a real Editor (2022.3.62f3) actually wrote to disk, not our reading of the documentation. The generator's output must match them, so the source of truth is Unity's own output. This also disposes of the `serializedVersion` trap by construction (a `.meta` claiming `serializedVersion: 3` has its entire `platformData` silently dropped on 2022.3).

**The samples must be re-collected from a real Editor when the Unity version changes** — never edited or renamed by hand, which would quietly turn them from an independent source of truth into a copy of the generator's output. The procedure is in `CONTRIBUTING.md`; it was exercised when macOS moved from `.bundle` to `.dylib` (§8) and the sample was re-collected rather than renamed.

### Known gaps in the automated gates

Stated explicitly, in the same spirit as the enum-insertion gap below — a gap that is written down is a decision; a gap that is merely absent is an accident waiting to be rediscovered.

- **No dependency gate on the iOS static library** ([#50](https://github.com/xuhuanhello/juice-c-sharp/issues/50)). The dependency check reads a dynamic artifact's dependency table against a path-prefix allowlist. A `.a` has no dependency table; the only proxy is its undefined-symbol set (322 of them, measured), which drifts with compiler and optimisation level. High maintenance, low catch rate — so the gate is **not built**, and iOS is covered on exports only.
- **The `.meta` checker cannot cover everything, and the split is not arbitrary.** What it does cover is what plain text can express — and that happens to be the class Unity never validates, where a mistake is silently ineffective. What it cannot cover is anything only a real Editor decides. This is why the on-device smoke above is a landing condition rather than a nicety: it is the only thing standing behind the other class.

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

**The notices do not vary by platform.** Every platform links the same vendored set from the same pins (§3) — the artifact differs, the dependency set does not — so landing a new platform does not by itself change `ThirdPartyNotices.md`. Only a pin change does. Item 2's mapping is per binary, and each binary's exact source pins are recorded in its build record under `Report~/` (§10).

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
      CHANGELOG.md          ← release history; §3's pin-bump gate names it
      Runtime/
      Editor/               ← build-time platform guard (§10)
      Plugins/              ← binaries + their .meta, nothing else (§8; not LFS)
      Report~/              ← one build record per shipped binary (§10)
      Samples~/
      Tests/                ← three test assemblies (§11)
  native/
    CMakeLists.txt          ← the only build entry
    versions.lock
    cross/                  ← reserved for CMake toolchain files; empty today (§9)
    dcu/                    ← stable C ABI sources (include/ + src/)
    exports/                ← expected-symbols.txt (the one hand-written list)
                              + plugin-meta-golden/ (Editor-written .meta samples)
    scripts/                ← fetch-deps, build wrappers, audit, generators
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
6. **Tests** (§11): three assemblies, the required-contract list, the persistent domain-reload probe; delete `Assets/DataChannelVerify/`. *(The exported-symbol diff was pulled forward out of this step — it gates step 2, so it had to exist first, and it is already in place.)*
7. Expand plugins. ~~Android → iOS → Win arm64 → WebGL (+ jslib)~~ — **the desktop batch (macOS universal, Windows x64, Linux x64) and Android arm64-v8a are built by CI and landed**; Win arm64 left the matrix (§8). iOS remains the unfinished mobile platform, with WebGL after it if at all.
8. `CONTRIBUTING.md` gates in force; GHA + the maintainer landing flow. **In force as of the desktop batch** — the workflows, the batch landing conditions and the build-time platform guard are all live (§10).
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

### Map [#46](https://github.com/xuhuanhello/juice-c-sharp/issues/46) — desktop platform matrix (CI output and LFS landing)

| Topic | Issue | Section |
|-------|-------|---------|
| Symbol visibility and audit tooling across the matrix | [#47](https://github.com/xuhuanhello/juice-c-sharp/issues/47) | §3, §8, §9 |
| CI runner and cross-compilation constraints | [#48](https://github.com/xuhuanhello/juice-c-sharp/issues/48) | §8, §9, §10 |
| `PluginImporter` `.meta` format; checkable without Unity | [#49](https://github.com/xuhuanhello/juice-c-sharp/issues/49) | §8, §10, §11 |
| One symbol list or many; criterion for static-library platforms | [#50](https://github.com/xuhuanhello/juice-c-sharp/issues/50) | §3, §9, §11 |
| CI job shape; native vs cross split; declared Linux floor | [#51](https://github.com/xuhuanhello/juice-c-sharp/issues/51) | §9, §10 |
| How `.meta` is produced and kept from being wrong | [#52](https://github.com/xuhuanhello/juice-c-sharp/issues/52) | §8, §11 |
| Batch landing: threshold, verified-platform list, minimum smoke evidence | [#53](https://github.com/xuhuanhello/juice-c-sharp/issues/53) | §10, §11 |
| LFS size, refresh cadence, binary provenance | [#54](https://github.com/xuhuanhello/juice-c-sharp/issues/54) | §10 |
| Windows x64 template built end to end; desktop batch landed; #10 partly overturned | [#55](https://github.com/xuhuanhello/juice-c-sharp/issues/55) | §8, §9, §10 |
| Write these decisions back | [#56](https://github.com/xuhuanhello/juice-c-sharp/issues/56) | — |
| Where the build record lives (overturns #54's "same directory") | [#65](https://github.com/xuhuanhello/juice-c-sharp/issues/65) | §8, §10 |
| Landing the move into `Report~/` | [#66](https://github.com/xuhuanhello/juice-c-sharp/issues/66) | §10 |
| `source.dirty` removed — constant over everything the gate reads | [#68](https://github.com/xuhuanhello/juice-c-sharp/issues/68) | §10 |

Research notes from this map: `docs/research/platform-symbol-audit.md`, `platform-ci-toolchains.md`, `plugin-importer-meta.md`.

### After the maps closed

| Topic | Issue | Section |
|-------|-------|---------|
| Unity tests do not run in CI (supersedes part of #39) | [#43](https://github.com/xuhuanhello/juice-c-sharp/issues/43) | §10, §11 |
| Plugin binaries out of Git LFS — LFS silently broke the git-URL install (overturns map #46's distribution premise) | [#73](https://github.com/xuhuanhello/juice-c-sharp/issues/73) | §8, §10 |

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
| ~~Android's 16 KB page alignment~~ | **Resolved:** CI uses NDK r27 with explicit `-z,max-page-size=16384` and audits every `PT_LOAD` at `>= 0x4000`; generated Android PluginImporter metadata records `Is16KbAligned: true`; a Play-distributed AAB passed the device Runtime smoke. APK/AAB zip packaging remains an adopter/build-system concern, not a package binary requirement (§9). |
| **HarmonyOS** | Waiting on Unity/tooling |
| ~~Implementation milestones / PR slicing~~ | **Done** — [#44](https://github.com/xuhuanhello/juice-c-sharp/issues/44) slices §14 steps 2–3 into nine vertical cuts, each leaving the tree green |
| Optional later | Selected-candidate-pair API, device farm CI, WebSocket bindings, FishNet transport mapping |
