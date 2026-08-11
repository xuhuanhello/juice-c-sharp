# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the versioning policy is
in [`docs/SPEC.md`](../../docs/SPEC.md) §3 — upstream patch → patch, upstream minor
or behaviour change → minor, `dcu_*` or public C# break → **major**.

# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the versioning policy is
in [`docs/SPEC.md`](../../docs/SPEC.md) §3 — upstream patch → patch, upstream minor
or behaviour change → minor, `dcu_*` or public C# break → **major**.

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

[0.2.0]: https://github.com/xuhuanhello/juice-c-sharp/releases/tag/v0.2.0
[0.1.1]: https://github.com/xuhuanhello/juice-c-sharp/releases/tag/v0.1.1
[0.1.0]: https://github.com/xuhuanhello/juice-c-sharp/releases/tag/v0.1.0
