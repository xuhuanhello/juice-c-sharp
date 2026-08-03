# Domain glossary

## Package

- **datachannel-unity** — Open-source UPM package at `Packages/datachannel-unity`, id `com.xuhuanhello.datachannel`. PeerConnection + DataChannel bindings for Unity; not a game netcode stack.

## Native surface

- **Stable C ABI (`dcu_*`)** — Project-owned C export layer consumed by P/Invoke. Does not re-export libdatachannel `rtc*`. Same surface on native and WebGL. Version 2; 19 exported symbols.
- **dcu layer** — The implementation behind that ABI. Built on libdatachannel's **C++ API** with its own handle table; the choice of upstream API is invisible above `dcu.h`.
- **Handle** — Opaque `int32` allocated by the dcu handle table. Monotonic and **never reused**, which is what makes a stale event harmless: it necessarily misses lookup.
- **Return code + out parameter** — The one calling convention. No `dcu_*` function's return value doubles as data; success is always `rc == DCU_OK`.
- **Buffer-too-small retry** — `DCU_ERR_TOO_SMALL` fills in the required length and **does not consume** the item, so growing and retrying is idempotent. Exactly one retry is ever needed.
- **Auto-negotiation** — Library always produces local descriptions automatically after create-DC (offerer) or set-remote-offer (answerer). Apps only inject remote SDP/candidates.

## Queues and delivery

- **Event pump** — Per-frame main-thread routine with **two segments**: drain the control queue (`dcu_event_next`), then pull messages from each open channel (`dcu_dc_receive`). **Both drain fully; neither has a frame budget.** The pump is stateless and polls; there is no readiness event.
- **Control queue** — Unbounded queue of connection/channel lifecycle events. **Control events are never dropped** — a lost `DcClosed` desynchronises the managed state machine permanently. Safe to leave unbounded because its rate follows the connection count, not peer traffic.
- **Receive queue** — Upstream's per-channel message queue (1024 messages). Messages are **pulled**, never pushed into our own queue. When it fills, upstream's `push` blocks.
- **Back-pressure** — What that blocking produces: SCTP's receive window closes and the peer slows down. Nothing is dropped, so `Reliable = true` stays true. **Draining is the correct operating point** — a frame budget would trigger back-pressure artificially early. **Does not hold on WebGL** (no receive queue exists there; the browser's `onmessage` cannot be blocked).
- **Log queue** — Separate **bounded** queue (1024, drop-oldest + counter). Opposite policy to the control queue on purpose: log lines are droppable, control events are not.
- **Main-thread delivery** — All public events and observer callbacks run on the Unity main thread after the player-loop pump. Stronger than it sounds: **every public API, `Dispose` included, is main-thread-only.**
- **Per-subscriber isolation** — A throwing subscriber is caught, logged, and the remaining subscribers still receive the event. Wrapping the whole dispatch in one `try` would be dropping a message, which is the same protocol violation the queues refuse.
- **Dual subscription** — C# `event` is primary; optional observer interfaces; no UniRx/R3 package dependency.

## Ownership and lifetime

- **PeerConnection owns its DataChannels** — `Dispose` cascades, **children first**. The lookup table holds only weak references; liveness comes from the ownership edge.
- **Incoming DataChannels are always accepted** — created and owned even with no subscriber. Refusing "unwanted" channels is timing-sensitive and silently breaks late subscribers.
- **Enqueue-only finalizer** — A finalizer records a leak and nothing else: no P/Invoke, no table lock, no `Debug.Log*`. The main-thread pump does the reporting. Compiled in only for Editor / Development builds.
- **Leak detection** — Three modes (`Disabled` / `Enabled` / `EnabledWithStackTrace`), on by default outside Release.
- **Domain teardown principle** — *Domain about to die → sledgehammer (`dcu_shutdown()`). Domain still alive → scalpel (`DisposeAllLive()`).* All five Editor/player scenarios follow from this. Note that **exiting play mode does not trigger a domain reload** — it is handled explicitly.

## Errors and state

- **`DataChannelError` / `RawCode`** — The enum carries control flow; the raw integer is **for diagnostics only**. Keeping both is what makes an upstream code leak visible.
- **Independent error numbering** — dcu codes are deliberately not value-identical to `RTC_ERR_*`, so an accidental passthrough shows up as an undefined code instead of a plausible wrong one.
- **Live state query** — `DataChannel.State` reads the channel's actual state; open-ness is never cached, and `Send` performs no open pre-check. A missed notification therefore cannot permanently disable a channel.
- **Config failure** — Synchronous error at peer-connection create (bad params / cannot construct).
- **ICE failure** — Asynchronous `ConnectionState` after a PC exists (connectivity / relay / peer problems).

## Backends

- **Native backend** — libdatachannel (ICE via libjuice by default), consumed through its C++ API.
- **WebGL backend** — datachannel-wasm + browser WebRTC; C facade implements the same `dcu_*` ABI. **Same ABI, different behaviour** on the data path.

## Out of package

- **Signal** — Application-provided exchange of SDP/candidates (not implemented in the package).
- **TURN server** — External; package only accepts ICE server configuration.

## ICE configuration

- **IceServer** — Application-provided STUN/TURN endpoint description (`urls`, optional username/credential). Credentials are passed to the backend **structurally**; the package never assembles a URL containing them, and `IceServer` is deliberately not `[Serializable]` so credentials cannot be baked into a scene or prefab.
- **Short-term ICE credentials** — The per-session ufrag/pwd pair. Travels in the clear inside SDP by design, is logged by upstream itself, and is **explicitly outside the redaction rules** — unlike long-term TURN credentials.

## Plugin matrix

- **datachannel_unity** — Native plugin base name. Desktop/Android `DllImport("datachannel_unity")`; iOS/WebGL `DllImport("__Internal")`.
- **Self-contained plugin** — One binary per platform that statically includes wrapper + backend deps (no extra crypto DLLs for adopters).
- **Editor-capable plugins** — Windows and macOS player plugins also enabled for Editor on matching OS/CPU only.

## Managed API

- **DataChannelUnity** — Public C# namespace for the UPM package.
- **`DataChannelMessageHandler`** — The message event's delegate, `void(ReadOnlySpan<byte>)`. Custom rather than `Action<T>` because C# 9 cannot use a `ref struct` as a generic argument; Span rather than `byte[]`/`ArraySegment` so that reusing the pump's buffer is a compile error instead of silent corruption a frame later.
- **Public surface is a P2P API, not a pipe** — the criterion for what stays public. Native handles, pump registration and redaction are `internal`.

## Diagnostics

- **LogLevel** — Package logging verbosity; bridges libdatachannel logger; defaults Info in Editor/Development and Warning in release players; ICE credentials redacted.
- **Log bridge** — Native thread → static trampoline (enqueue only, runs under upstream's lock) → bounded queue → main-thread pump → managed log. **Changing the level never detaches the bridge.**
- **Throttled warning** — One message per category per 5 s, carrying occurrence count and peak. Used for slow pump frames (>4 ms), control-queue backlog (>1024) and dropped log lines.

## Verification

- **Test tiers** — Managed (no plugin, the only red-turning CI gate), Native/EditMode, Native/PlayMode. Split by **assembly**, not category, so CI needs no exclusion flag to remember.
- **Absence must be failure** — `Assert.Ignore`, fallback `chmod`, `|| true` and ignored exit codes are one disease: they make "never ran" and "ran, green" look identical.
- **Required contract** — A test earns a place on the gate list only if measurement or research **overturned an intuition**. Gates exist to stop a future implementer from reverting a decision by instinct.
- **Machine-judged** — Even manual steps must produce an assertable artifact; reading a line out of the Console does not count.
