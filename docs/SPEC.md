# datachannel-unity — Implementation Specification (v1)

**Status:** Ready to implement  
**Map:** [Map: datachannel-unity UPM 架构与平台规格](https://github.com/xuhuanhello/juice-c-sharp/issues/1)  
**Package path:** `Packages/datachannel-unity`  
**Package id:** `com.xuhuanhello.datachannel`  
**Unity baseline:** 2022.3 LTS (reference project: 2022.3.62f3)  
**Glossary:** [`CONTEXT.md`](../CONTEXT.md)  
**Research:** [`docs/research/`](./research/)

This document consolidates all closed wayfinder decisions (#2–#14). Implementation must follow it; expanding scope requires a new decision, not silent drift.

---

## 1. Product boundary

### In scope (v1)

- Open-source UPM package with:
  - Stable C ABI (`dcu_*`) + P/Invoke + thin idiomatic C# API
  - Prebuilt native plugins for the platform matrix (§6)
  - PeerConnection + DataChannel only (P2P data path)
- Application-supplied **signaling transport** (SDP / ICE candidates) and **ICE server config** (STUN/TURN URLs)
- Meson top-level orchestration wrapping upstream **CMake** builds
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
| Pure Meson rewrite of upstream builds | Orchestration only |
| Full shippable multiplayer game | This map ends at implementable package spec |
| HarmonyOS | Extension placeholder only (§11) |

**Reference only (do not fork):** [Walkerdine/dc-unity](https://github.com/Walkerdine/dc-unity) — see `docs/research/dc-unity-autopsy.md`.

---

## 2. Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ C#  DataChannelUnity.*  (events + optional observers)       │
│  PlayerLoop pump → dcu_poll / copy / pop                    │
│  IDisposable PeerConnection / DataChannel                   │
└───────────────────────────┬─────────────────────────────────┘
                            │ P/Invoke dcu_* only
┌───────────────────────────▼─────────────────────────────────┐
│ Stable C ABI (project-owned)                                │
│  queue events; copy to caller buffers; no managed callbacks │
└─────────────┬───────────────────────────────┬───────────────┘
              │ native                        │ WebGL
              ▼                               ▼
     libdatachannel v0.24.5          datachannel-wasm v0.4.0
     (+ libjuice ICE, MbedTLS)       (+ browser WebRTC + webrtc.jslib)
```

- **One** `dcu_*` surface for all platforms.
- WebGL must **not** embed libdatachannel as a normal UDP stack; use browser WebRTC via datachannel-wasm + C facade.
- Do **not** re-export upstream `rtc*` symbols from the plugin.

---

## 3. Upstream pins and versioning

**Decision:** [#11](https://github.com/xuhuanhello/juice-c-sharp/issues/11)

| Component | Pin | Notes |
|-----------|-----|--------|
| libdatachannel | **`v0.24.5`** | git tag |
| datachannel-wasm | **`v0.4.0`** | git tag |
| Transitive (libjuice, usrsctp, …) | Follow pinned trees | No floating `latest` |
| Crypto | **Static into plugin** — product target **Mbed TLS 3.6.x** (`mbedtls=v3.6.7` in lock); **must not** load system/Homebrew OpenSSL or MbedTLS **dylibs** | brew `mbedtls` 4.x incompatible with libdatachannel v0.24.5. Host mac script may temporarily **static-link OpenSSL `.a`** into the plugin (self-contained) until MbedTLS is built with `MBEDTLS_SSL_DTLS_SRTP` for libdatachannel. No dual *shipping* matrix of backends. |
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

**Decision:** [#7](https://github.com/xuhuanhello/juice-c-sharp/issues/7), logging extension [#12](https://github.com/xuhuanhello/juice-c-sharp/issues/12), create config [#9](https://github.com/xuhuanhello/juice-c-sharp/issues/9)

### Principles

| Rule | Value |
|------|--------|
| Prefix | `dcu_*` only; no `rtc*` export |
| Handles | Opaque `int32` |
| Events | Native enqueue; caller-provided buffers on drain |
| Negotiation | **Always auto-negotiate** (no create_offer / set_local_description) |
| Payloads | Binary only: pointer + `len >= 0` |
| Sync errors | `0` ok; negative codes (`invalid`, `failure`, `not_avail`, `too_small`); create returns **positive handle or negative error** |

### Required capability groups

**Global**

- `dcu_abi_version` (or major/minor query)
- `dcu_init` / `dcu_shutdown` (no shutdown from inside event handling)
- Event pump: peek header (type, handle, payload size) → copy payload into caller buffer → pop
- Log bridge: level + optional log sink (`dcu_set_log_level` / callback — names flexible)

**PeerConnection**

- `dcu_pc_create` with config (§5)
- `dcu_pc_close` / `dcu_pc_destroy`
- `dcu_pc_set_remote_description(pc, sdp, sdp_len, type, type_len)`
- `dcu_pc_add_remote_candidate(pc, cand, cand_len, mid, mid_len)` — mid optional
- `dcu_pc_create_data_channel(pc, label, reliability: ordered/reliable + optional maxRetransmits / maxPacketLifeTime)`

**DataChannel**

- `dcu_dc_send(dc, data, len)` — `len >= 0`
- `dcu_dc_close` / `dcu_dc_destroy`
- `dcu_dc_buffered_amount(dc)`

**Event types (via pump)**

| Event | Purpose |
|-------|---------|
| LocalDescription | Local SDP + type for app signaling |
| LocalCandidate | Trickle candidate + mid |
| ConnectionState | PC connection state |
| GatheringState | Gathering progress/complete |
| IncomingDataChannel | Remote-created DC (handle + label metadata) |
| DcOpen / DcClosed / DcError | Channel lifecycle |
| DcMessage | Binary payload |

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

Symbol names may be refined in implementation PRs; **surface expansion requires a new decision**.

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
| `credential` | Optional; native builds URI and percent-encodes reserved chars |

Priority: if username/credential set, inject into each url; else use urls as-is (may already embed credentials).

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

**Decision:** [#8](https://github.com/xuhuanhello/juice-c-sharp/issues/8)

### Namespace and types

- Namespace: **`DataChannelUnity`**
- Types: `PeerConnection`, `DataChannel`, `IceServer`, `PeerConnectionConfig`, `DataChannelInit`, `ConnectionState`, `GatheringState`, `LogLevel`, …
- Single Runtime asmdef; **no** UniRx/R3 package dependency
- Document that users may wrap events with `Observable.FromEvent` themselves

### Threading

- All public **events** and **observer** callbacks run on the **Unity main thread**
- Package registers **PlayerLoop** (or equivalent) pump calling native drain
- Expose `DataChannelRuntime.Pump()` (name flexible) for tests / custom loops
- Editor domain reload must unregister pump cleanly

### Subscription

1. **Primary:** C# `event`s  
2. **Optional:** `IPeerConnectionObserver` / `IDataChannelObserver` (same events, main thread)  
3. Dual subscription order: **events first, then observers** (fixed; document in XML docs)

Suggested event set (names flexible if equivalent):

- PC: `LocalDescriptionGenerated`, `LocalCandidateGenerated`, `ConnectionStateChanged`, `GatheringStateChanged`, `DataChannel` (incoming)
- DC: `Open`, `Closed`, `Error`, `Message`

### Lifecycle and errors

- `PeerConnection` and `DataChannel` are **`IDisposable`** (close + destroy + unregister). Finalizer-only is forbidden.
- Sync misuse → exceptions (`ArgumentException`, `InvalidOperationException`, optional `DataChannelException`)
- Create failure → throw; no half-open object
- ICE failure → `ConnectionState` events only
- `Send` when not open / disposed / native fail → throw

### DataChannel

```csharp
CreateDataChannel(string label, DataChannelInit init = null);
// DataChannelInit: Ordered (default true), Reliable (default true),
//   MaxRetransmits / MaxPacketLifeTime when unreliable
Send(ReadOnlySpan<byte> / byte[]);  // no string overload
int BufferedAmount { get; }
```

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

---

## 7. Logging and diagnostics

**Decision:** [#12](https://github.com/xuhuanhello/juice-c-sharp/issues/12)

| Item | Spec |
|------|------|
| Upstream | Bridge libdatachannel logger |
| C# | `SetLogLevel`, optional log callback |
| Defaults | Editor or Development Player → **Info**; non-Development Player → **Warning** |
| Secrets | **Redact** ICE URIs containing credentials in logs |
| Stats v1 | `BufferedAmount` only; no selected-pair / RTT panel |

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

---

## 9. Native build system

**Decisions / research:** [#4](https://github.com/xuhuanhello/juice-c-sharp/issues/4), [#23](https://github.com/xuhuanhello/juice-c-sharp/issues/23), [#24](https://github.com/xuhuanhello/juice-c-sharp/issues/24), [#25](https://github.com/xuhuanhello/juice-c-sharp/issues/25),  
`docs/research/meson-subprojects-static-graph.md`

### Product entry (local + CI)

```bash
cd native
./scripts/fetch-deps.sh          # or meson subprojects download
meson setup build/macos-arm64 --buildtype=release
meson compile -C build/macos-arm64
# thin wrapper (same path):
./scripts/build-macos-arm64.sh
./scripts/audit-macos-plugin.sh ../Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle
```

| Rule | Detail |
|------|--------|
| **Meson is the only product entry** | Same for local mac and CI; no “dev uses brew openssl .a + clang” product path |
| **Sources in `subprojects/`** | `mbedtls` @ lock, `libdatachannel` @ lock, `dcu_superbuild` (CMake superbuild) |
| **Why CMake superbuild under Meson** | Meson `cmake.subproject` cannot compile sources *outside* that subproject dir; nested `add_subdirectory` of sibling trees becomes empty targets. Superbuild CMake under Meson `custom_target` is the supported orchestration shape. |
| **MbedTLS** | Built from **subprojects source** with `MBEDTLS_USER_CONFIG_FILE` → `MBEDTLS_SSL_DTLS_SRTP`; static `.a` only; injected as `MbedTLS::MbedTLS` into libdatachannel (**not** brew find_package) |
| **libdatachannel** | `USE_MBEDTLS=ON`, `BUILD_SHARED_LIBS=OFF`, `NO_MEDIA/NO_WEBSOCKET`, hidden visibility |
| **Exports** | `native/exports/*` allowlist (`dcu_*` only) |
| **Install** | Single macOS `.bundle` per arch into UPM `Plugins/` |

### Crypto backend note (why MbedTLS appears in SPEC)

| | OpenSSL | MbedTLS 3.6 |
|--|---------|-------------|
| **libdatachannel default** | **Yes (default)** | Optional (`USE_MBEDTLS=ON`) |
| **“Better crypto?”** | No absolute ranking for this project | Not chosen for “stronger crypto” |
| **Why SPEC preferred MbedTLS** | Larger static footprint; historically awkward mobile packaging | Smaller, common for static/mobile game plugins; LTS 3.6 |
| **Product rule** | Forbidden as *system/brew dylib*; static vendored OpenSSL only as escape hatch | **Product static default** when DTLS-SRTP user_config is applied |

### Phased platforms

Risk order: WebGL > iOS > Win arm64 > Android > macOS > Win x64.

---

## 10. CI, LFS, and signing

**Decisions:** [#13](https://github.com/xuhuanhello/juice-c-sharp/issues/13), [#20](https://github.com/xuhuanhello/juice-c-sharp/issues/20)

### Local vs CI

| Role | Builds | Checks |
|------|--------|--------|
| **Local (default)** | **mac only** (`native/scripts/build-macos-arm64.sh`, optional x64) | Developer may run `audit-macos-plugin.sh` |
| **CI** | **Full matrix** | Link audit (no system crypto dylibs), export allowlist (`dcu_*` only), EditMode tests |

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

**Decision:** [#14](https://github.com/xuhuanhello/juice-c-sharp/issues/14)

### Sample (required)

- In-process **two PeerConnections** + **in-memory fake signal** (SDP/candidates).
- After connect, send/receive **≥ 1 binary** DataChannel message.
- Runs in **Editor** (desktop plugins) and optionally desktop standalone.
- Prefer `Samples~`. Document how to plug real Signal + IceServers.
- Not required: full public STUN/TURN lab scene; FishNet sample.

### Test gates

| Layer | Content | Gate |
|-------|---------|------|
| EditMode | Pure C# validation, errors, mockable interop façade | **Required on PR** |
| PlayMode smoke | Dual-peer fake signal, ≥1 message, timeout fails | Required when desktop native present / before release |
| Device / WebGL | Manual checklist | Release self-check; not daily PR blocker |

Deferred: device farm, fault injection, automated WebGL browser CI.

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
  docs/
    SPEC.md                 ← this file
    research/               ← closed research notes
  Packages/
    datachannel-unity/      ← UPM package
      package.json
      Runtime/
      Plugins/              ← LFS binaries
      Samples~/
  native/                   ← name flexible
    versions.lock
    meson.build             ← orchestration
    cross/                  ← meson cross files
    dcu/                    ← stable C ABI sources
    subprojects/ or deps/   ← pinned upstream tags
  .github/workflows/        ← GHA
  Tests/                    ← EditMode / PlayMode (or under package)
```

---

## 14. Implementation checklist (non-normative order)

1. Scaffold UPM `package.json`, asmdef, empty Runtime API stubs.  
2. `native/versions.lock` + Meson + libdatachannel pin; desktop shared plugin exporting `dcu_*`.  
3. Event queue + C# PlayerLoop pump + dual-peer PlayMode smoke.  
4. ICE config marshaling + logging bridge + redaction.  
5. Expand plugins: Android → iOS → Win arm64 → WebGL (+ jslib).  
6. Samples~, EditMode suite, GHA, LFS maintainer flow.  
7. ThirdPartyNotices + README (signaling ownership, signing, platforms).

---

## 15. Decision index

| Topic | Issue |
|-------|--------|
| Map | [#1](https://github.com/xuhuanhello/juice-c-sharp/issues/1) |
| libdatachannel C API research | [#2](https://github.com/xuhuanhello/juice-c-sharp/issues/2) |
| WebGL / datachannel-wasm research | [#3](https://github.com/xuhuanhello/juice-c-sharp/issues/3) |
| Meson + CMake research | [#4](https://github.com/xuhuanhello/juice-c-sharp/issues/4) |
| MPL / binaries research | [#5](https://github.com/xuhuanhello/juice-c-sharp/issues/5) |
| dc-unity autopsy | [#6](https://github.com/xuhuanhello/juice-c-sharp/issues/6) |
| Stable C ABI | [#7](https://github.com/xuhuanhello/juice-c-sharp/issues/7) |
| C# API / threads | [#8](https://github.com/xuhuanhello/juice-c-sharp/issues/8) |
| ICE config | [#9](https://github.com/xuhuanhello/juice-c-sharp/issues/9) |
| Plugins matrix | [#10](https://github.com/xuhuanhello/juice-c-sharp/issues/10) |
| Upstream pins | [#11](https://github.com/xuhuanhello/juice-c-sharp/issues/11) |
| Logging | [#12](https://github.com/xuhuanhello/juice-c-sharp/issues/12) |
| CI / signing | [#13](https://github.com/xuhuanhello/juice-c-sharp/issues/13) |
| Samples / tests | [#14](https://github.com/xuhuanhello/juice-c-sharp/issues/14) |
| This SPEC task | [#15](https://github.com/xuhuanhello/juice-c-sharp/issues/15) |

---

## 16. Open after this map

Not required to start implementation:

- HarmonyOS extension when Unity/tooling exists  
- Implementation milestone / PR slicing (separate planning)  
- Optional later: selected candidate pair API, device farm CI, WS bindings, FishNet transport map
