# juice-c-sharp

`datachannel-unity` — a UPM package wrapping libdatachannel (PeerConnection + DataChannel) for Unity 2022.3.

**Before changing anything under `native/` or `Packages/datachannel-unity/`, read [`CONTRIBUTING.md`](./CONTRIBUTING.md).** It holds the verification gates, and they are not optional.

| Need | File |
|------|------|
| What must be true (normative) | [`docs/SPEC.md`](./docs/SPEC.md) |
| What to do and when | [`CONTRIBUTING.md`](./CONTRIBUTING.md) |
| How to verify, with Unity MCP | [`docs/verification-mcp.md`](./docs/verification-mcp.md) |
| Domain vocabulary | [`CONTEXT.md`](./CONTEXT.md) |

Two things worth knowing before reading code:

- **The spec now describes the code as it is.** All nine steps of SPEC §14 have landed, so that list is a record of the order the work was done in, not a to-do list. Where the code and the spec disagree, the spec is still normative and the code is the bug — that has not changed.
- **Platforms: 5/5 landed** (macOS universal, Windows x64, Linux x64, Android arm64-v8a, iOS arm64), each with a CI build record in `Report~/`. WebGL is the only one outside, deliberately — it is a different behavioural contract, not one more toolchain ([#46](https://github.com/xuhuanhello/juice-c-sharp/issues/46)).
