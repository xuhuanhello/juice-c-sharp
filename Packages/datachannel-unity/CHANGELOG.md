# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the versioning policy is
in [`docs/SPEC.md`](../../docs/SPEC.md) §3 — upstream patch → patch, upstream minor
or behaviour change → minor, `dcu_*` or public C# break → **major**.

## [0.1.1] — 2026-08-05

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

[0.1.1]: https://github.com/xuhuanhello/juice-c-sharp/releases/tag/v0.1.1
[0.1.0]: https://github.com/xuhuanhello/juice-c-sharp/releases/tag/v0.1.0
