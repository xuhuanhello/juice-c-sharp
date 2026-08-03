# Domain glossary

## Package

- **datachannel-unity** — Open-source UPM package at `Packages/datachannel-unity`, id `com.xuhuanhello.datachannel`. PeerConnection + DataChannel bindings for Unity; not a game netcode stack.

## Native surface

- **Stable C ABI (`dcu_*`)** — Project-owned C export layer consumed by P/Invoke. Does not re-export libdatachannel `rtc*`. Same surface on native and WebGL.
- **Event pump** — Native threads enqueue events; managed code drains into caller-provided buffers (no direct managed callbacks from native threads).
- **Auto-negotiation** — Library always produces local descriptions automatically after create-DC (offerer) or set-remote-offer (answerer). Apps only inject remote SDP/candidates.

## Backends

- **Native backend** — libdatachannel (ICE via libjuice by default).
- **WebGL backend** — datachannel-wasm + browser WebRTC; C facade implements the same `dcu_*` ABI.

## Out of package

- **Signal** — Application-provided exchange of SDP/candidates (not implemented in the package).
- **TURN server** — External; package only accepts ICE server configuration.

## ICE configuration

- **IceServer** — Application-provided STUN/TURN endpoint description (`urls`, optional username/credential). Package builds backend URIs; does not run STUN/TURN services.
- **Config failure** — Synchronous error at peer-connection create (bad params / cannot construct).
- **ICE failure** — Asynchronous `ConnectionState` after a PC exists (connectivity / relay / peer problems).

## Plugin matrix

- **datachannel_unity** — Native plugin base name. Desktop/Android `DllImport("datachannel_unity")`; iOS/WebGL `DllImport("__Internal")`.
- **Self-contained plugin** — One binary per platform that statically includes wrapper + backend deps (no extra crypto DLLs for adopters).
- **Editor-capable plugins** — Windows and macOS player plugins also enabled for Editor on matching OS/CPU only.

## Managed API

- **DataChannelUnity** — Public C# namespace for the UPM package.
- **Main-thread delivery** — All public events and observer callbacks run on the Unity main thread after an automatic player-loop pump of the native event queue.
- **Dual subscription** — C# `event` is primary; optional observer interfaces; no UniRx/R3 package dependency.

## Diagnostics

- **LogLevel** — Package logging verbosity; bridges libdatachannel logger; defaults Info in Editor/Development and Warning in release players; ICE credentials redacted.
