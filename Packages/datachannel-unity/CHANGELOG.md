# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the versioning policy is
in [`docs/SPEC.md`](../../docs/SPEC.md) §3 — upstream patch → patch, upstream minor
or behaviour change → minor, `dcu_*` or public C# break → **major**.

## [Unreleased]

## [0.5.0] — 2026-08-20

### Changed

- **The shipped Android `.so` no longer contains DWARF.** NDK Clang's Release
  build embeds full `.debug_*` (24 MB on disk, 21 MB of it debug). Store apps
  ship a stripped Release binary; Bugly / Firebase / Play Console take a
  **separate** unstripped copy of the same compile. `strip --strip-debug` runs
  on the staged plugin; the unstripped file is a CI artifact and a GitHub
  Release asset, not in `Plugins/`. Linux was already free of DWARF.
- **Native plugins rebuilt from `main`**
  ([run 32385550254](https://github.com/xuhuanhello/juice-c-sharp/actions/runs/32385550254)).
  `Report~/` now names `refs/heads/main` and `source.commit` `fd6525d`. Tag
  `v0.4.0` remains on the feature branch; pin this one.

## [0.4.0] — 2026-08-20

### Changed

- **Native no longer loads at startup** ([#146]). `Bootstrap` used to load and
  initialise the native library eagerly at `BeforeSceneLoad` in players; it now loads
  only on the first `new PeerConnection(...)` or an explicit
  `DataChannelRuntime.Preload()`. Apps that ship the package but never use it no
  longer pay dlopen + `rtc::Preload` at startup. The PlayerLoop pump is still wired
  automatically — it is managed-only and effectively free while native is not loaded.
- **`IsNativeAvailable` / `AbiVersion` are now passive probes** ([#146]). Reading
  them no longer triggers a native load; they return `false` / `0` until the library
  is loaded. If you relied on reading them to force-load the plugin, call
  `DataChannelRuntime.Preload()` instead.

- **`Pump()` is guarded against re-entrancy** ([#148]): calling it from inside an
  event/message callback now throws `InvalidOperationException` instead of silently
  corrupting the shared message buffer your `ReadOnlySpan<byte>` still points into.
  The throw is always contained by the pump's own per-subscriber isolation — loud,
  never fatal.
- **Invalid configurations fail at construction** ([#151]): an inverted port range
  (`PortRangeBegin > PortRangeEnd`, both set) and an `IceServer` with no URLs now
  throw `ArgumentException` instead of failing downstream as an unattributable ICE
  error / being silently skipped. One-sided port settings still pass through.
- **`MessageReceived` add/remove now asserts the main-thread contract** ([#151]) —
  it is the one subscription accessor where a background-thread subscribe could be
  silently lost.
- **The ABI banner is visible at the release default level** ([#140], landed by
  [#142]): `Native library initialized (abi=N)` no longer passes through the
  log-level gate, so non-Development players (default `Warning`) finally show which
  binary they run — previously the gate dropped it exactly in the builds whose bug
  reports need it. It is emitted **at most once per process** (the managed latch
  [#141] showed native init idempotence does not provide), still redacted, still
  dispatched through `MessageLogged` (as `Info`), and silenced only by
  `LogLevel.None`. Ordinary `Info` lines stay gated exactly as before.

### Fixed

- **The connection-path gate moved to native** ([#123]): managed
  `ConnectionState` is an event cache and refused a connection that was already
  up (9/10 of first reads after the channel opened). Native reads
  `rtc::PeerConnection::state()`.
- **Pump-liveness false positives on frozen loops no longer burn the self-heal**
  ([#147], measured in [#145]): the "a third party rebuilt the PlayerLoop" diagnostic
  now requires the loop to have advanced ≥ 3 frames while ≥ 5 s stale. Pausing the
  editor (or a mobile app being suspended) used to fire it after any >5 s freeze,
  misattribute the cause, and spend the single re-registration retry — leaving a
  later genuine erasure with no self-heal. Liveness checks also now cover
  `SetRemoteDescription` / `AddRemoteCandidate`, closing the answerer-flow blind spot.
- **The control-event drain's leftover 256-per-frame cap was removed** ([#149]):
  both pump segments now drain fully, as the spec always said. The cap predated the
  no-budget decision and could delay a backlog of `DcClosed` events across frames.
- **Native lifecycle hardening** ([#150]): a failed `dcu_init` no longer latches as
  permanent success (init is retryable); a timed-out `dcu_shutdown` no longer races
  the next init (the pending cleanup gates it, fail-fast); incoming channels adopted
  during shutdown teardown are now destroyed and counted into the undestroyed bill
  instead of leaking and stalling cleanup. Handle-space exhaustion (2³¹ allocations)
  now fails loudly instead of being undefined behaviour.

### Added

- **`dcu_pc_connection_path` / `PeerConnection.TryGetConnectionPath`** ([#123],
  [#118]): ABI 2 → 3 (20 → 21 exports). Answers whether the selected ICE pair is
  Direct or Relayed. The verdict is synthesized in native (`local is relay ||
  remote is relay`) — judging only the remote candidate would mis-report the TURN
  side as Direct. C# is a two-value enum with no `Unknown`; the call returns
  `false` until the connection is up.
- **`DataChannelRuntime.Preload()`** ([#146]): optional explicit warm-up, mirroring
  upstream `rtcPreload`'s "lazy by default, preload if you care" shape — idempotent,
  main-thread-only, throws `DataChannelException` on failure (the lazy path only
  logs). Call it from a loading screen to keep first-connection latency out of the
  "join" click, or to pin the load moment while bisecting a crash.

[#118]: https://github.com/xuhuanhello/juice-c-sharp/issues/118
[#123]: https://github.com/xuhuanhello/juice-c-sharp/issues/123
[#140]: https://github.com/xuhuanhello/juice-c-sharp/issues/140
[#141]: https://github.com/xuhuanhello/juice-c-sharp/issues/141
[#142]: https://github.com/xuhuanhello/juice-c-sharp/issues/142
[#145]: https://github.com/xuhuanhello/juice-c-sharp/issues/145
[#146]: https://github.com/xuhuanhello/juice-c-sharp/issues/146
[#147]: https://github.com/xuhuanhello/juice-c-sharp/issues/147
[#148]: https://github.com/xuhuanhello/juice-c-sharp/issues/148
[#149]: https://github.com/xuhuanhello/juice-c-sharp/issues/149
[#150]: https://github.com/xuhuanhello/juice-c-sharp/issues/150
[#151]: https://github.com/xuhuanhello/juice-c-sharp/issues/151

## [0.3.0] — 2026-08-12

### Added

- **iOS arm64 prebuilt binary** (`Plugins/iOS/libdatachannel_unity.a`). A static
  archive, so it is absorbed into the adopter's executable at final link; there is no
  runtime library to load. Self-contained: libdatachannel, libjuice, usrsctp and
  MbedTLS are merged in with `libtool -static`, then narrowed with
  `ld -r -exported_symbols_list -all_load` so the **external defined set is exactly
  the 20 `dcu_*`** — no crypto name is exported, and the dependency implementations
  are present rather than left unresolved. Built by CI (`Report~/iOS.json` carries the
  run URL and commit) and smoke-tested on a real device: iPhone SE 3rd gen
  (`iPhone14,6`), iOS 27.0, Unity 2022.3.62f3, **3/3 passed**. Deployment target 12.0.
  Device only — no simulator slice (SPEC §8).

### Fixed

- **The iOS archive is no longer missing its dependencies.** A `STATIC` target has no
  link step, so `target_link_libraries` recorded the dependencies without copying any
  code in; the archive held only the wrapper and carried 89 unresolved references, 32
  of them `rtc::`. Anyone building for iOS hit `symbol(s) not found for architecture
  arm64` when Xcode linked `UnityFramework`. This shape never reached a release — it
  existed on `main` between the v0.2.0 tag and this one.
- **Common symbols no longer escape the export gate.** `-fno-common` is now a global
  convention for `native/` C code, so C tentative definitions get a section and the
  existing narrowing step localises them. Eleven external `C` symbols used to survive
  — ten from usrsctp plus MbedTLS's `mbedtls_cipher_supported`, including an orphan
  `foo` — and a common merges *silently* with a same-named common in the adopter's
  final link, sharing one storage location with no error or warning. Desktop binaries
  are unaffected: the macOS export and dependency sets are byte-identical across the
  change.

### Changed

- **The offline plugin audit asks three independent questions on a static archive**
  (SPEC §11): are the intended symbols exported (`nm -g`, now including type `C`), are
  the unintended ones hidden (`nm -Ujg`), and **is the implementation actually there**
  (`nm -u`). The third is new; the first two both score full marks on a wrapper-only
  archive, which is why the missing-dependency bug shipped past them.

### Notes for iOS adopters

- **Device SDK only.** Building against the Simulator SDK fails at link time — the
  archive has no simulator slice. The build-time platform guard reports this rather
  than letting the linker do it.
- **Size trade-off, disclosed.** Narrowing localises the C++ runtime along with
  everything else (852 vtable/typeinfo symbols, 1495 `std::` members become
  archive-local). Since the ABI boundary is pure C, no C++ type crosses it, so this
  costs size rather than correctness: an app that also links libc++ elsewhere may
  carry a second static copy of the parts used here.

## [0.2.0] — 2026-08-11

### Added

- **Android arm64-v8a prebuilt binary.** Self-contained: libdatachannel, libjuice,
  MbedTLS and usrsctp are statically linked; only `dcu_*` is exported. The `PT_LOAD`
  segments are 16 KB page-aligned (`-Wl,-z,max-page-size=16384`), and CI asserts
  `min align >= 0x4000` on every build. The plugin loads in a Unity Player (Editor
  does not load Android plugins) and the `Is16KbAligned` PluginImporter flag is set
  to `true`. Smoke-tested on a real 16 KB-page device (Samsung Galaxy A56 / SM-A566B,
  Android 16, `PAGE_SIZE=16384`): dual-peer, 2/2 passed, AAB install path. Build
  record at `Report~/Android-arm64-v8a.json`.
- **`PluginPlatformGuard` checks both platform and ABI.** Previously a build targeting
  Android arm64-v8a would pass the guard even if only an ARMv7-only binary was present
  (wrong ABI — the guard checked the platform label but not the subdirectory). The
  guard now enumerates actual plugin subdirectories and fails explicitly when the ABI
  is missing.

### Notes on 16 KB and Google Play distribution

This package guarantees that its `.so` is 16 KB-aligned internally. It does **not**
control how Unity packages the binary into an APK or AAB:

- **APK:** Unity 2022.3 + AGP 7.4.2 compresses the `.so` when `minSdk < 23` (default
  22). Compressed entries are exempt from the zip page-alignment requirement.
- **AAB:** the `.so` is stored uncompressed by default. AGP 7.4.2 zip-aligns to 4 KB;
  Google requires AGP 8.5.1+ for 16 KB zip alignment, which Unity 2022.3 never
  reaches. Adopters targeting a fully Play-compliant 16 KB AAB should upgrade to
  **Unity 2022.3.56f1+** (Unity's minimum for its own 16 KB support declaration) and
  verify the final artifact with `zipalign -c -P 16`. See README for the full
  breakdown, and `docs/research/android-packaging-alignment.md` for the source
  research.



### Fixed

- **The plugin binaries were unusable when the package was installed from a Git
  URL — on every platform.** They were tracked with Git LFS, so what an adopter
  received was a 132-byte pointer file rather than the library, with no error or
  warning anywhere; Unity reported `expected x64 architecture, but was Unknown
  architecture` and the native plugin never loaded. **v0.1.0 is affected and
  should not be used.** The binaries are now committed as ordinary git objects,
  which makes the result independent of any LFS client, credentials or clone
  behaviour. A CI gate now asserts that what git stores really is a binary
  (SPEC §10).

Nothing else changed: the binaries are byte-for-byte the ones from v0.1.0, and
no source, compiler flag or public API moved.

## [0.1.0] — 2026-08-05

> **Withdrawn — do not install.** Its plugin binaries reach adopters as Git LFS
> pointer files (see 0.1.1). Everything below still describes the package;
> only the delivery was broken.

First release. Desktop only.

### Added

- **C# API** — `PeerConnection`, `DataChannel`, `IceServer` / `PeerConnectionConfig`,
  `DataChannelLog`. Events are delivered on the **Unity main thread** by a PlayerLoop
  pump; receiving is pull-based, so a slow application applies real back-pressure to
  the peer instead of dropping messages (SPEC §4, §6).
- **Stable C ABI** (`dcu_*`, `DCU_ABI_VERSION` 2) — return code plus out-parameter
  throughout, error numbering independent of upstream's, one atomic
  `dcu_event_next`.
- **Prebuilt binaries** for **macOS (universal: arm64 + x86_64)**, **Windows x64**
  and **Linux x64**, each self-contained: Mbed TLS, usrsctp, libjuice and
  libdatachannel are linked statically and only `dcu_*` is exported.
- **A build record per binary** in `Report~/` — commit, CI run URL, upstream pins,
  compiler and SDK. Attach the one for your platform when reporting a bug against a
  binary; see the package README for where the directory lands on disk.
- **A build-time platform guard.** Building for a platform this package has no binary
  for **fails the build**, naming the platform, instead of producing a player that
  throws `DllNotFoundException` on a user's device.
- Dual-peer loopback sample (Package Manager → Samples) and three test assemblies.

### Known limitations

- **Android and iOS are not shipped yet.** They land as a second batch, each after
  its own real-device verification (SPEC §10). Building for them stops with an
  explicit error rather than failing at runtime.
- **WebGL is not shipped**, and its behaviour contract would differ: the
  no-message-is-dropped guarantee is physically unavailable there (SPEC §8).
- **Declared Linux floor is Ubuntu 22.04 / glibc 2.35** — one notch above what Unity
  2022.3 itself declares. This is a declared floor, not a measured one.
- **No code signing or notarisation.** Adopters own the final application's signing
  and store compliance.

### Notes on the binaries in this release

They were built by CI at `8cf54c6`. Everything committed since then is documentation
and provenance plumbing — no change to any compiled source or compiler flag — so they
are current for this tag. Their build records still carry a `source.dirty` field that
has since been removed from the schema (it was constant across everything the landing
gate reads, and therefore carried no information); it disappears on the next binary
refresh rather than being edited by hand, because CI artifacts are not hand-edited.

[0.5.0]: https://github.com/xuhuanhello/juice-c-sharp/releases/tag/v0.5.0
[0.4.0]: https://github.com/xuhuanhello/juice-c-sharp/releases/tag/v0.4.0
[0.3.0]: https://github.com/xuhuanhello/juice-c-sharp/releases/tag/v0.3.0
[0.2.0]: https://github.com/xuhuanhello/juice-c-sharp/releases/tag/v0.2.0
[0.1.1]: https://github.com/xuhuanhello/juice-c-sharp/releases/tag/v0.1.1
[0.1.0]: https://github.com/xuhuanhello/juice-c-sharp/releases/tag/v0.1.0
