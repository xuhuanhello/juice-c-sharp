# juice-c-sharp

`datachannel-unity` — a UPM package wrapping libdatachannel (PeerConnection + DataChannel) for Unity 2022.3.

**Before changing anything under `native/` or `Packages/datachannel-unity/`, read [`CONTRIBUTING.md`](./CONTRIBUTING.md).** It holds the verification gates, and they are not optional.

| Need | File |
|------|------|
| What must be true (normative) | [`docs/SPEC.md`](./docs/SPEC.md) |
| What to do and when | [`CONTRIBUTING.md`](./CONTRIBUTING.md) |
| How to verify, with Unity MCP | [`docs/verification-mcp.md`](./docs/verification-mcp.md) |
| Domain vocabulary | [`CONTEXT.md`](./CONTEXT.md) |

Two things worth knowing before reading code: the spec describes a **target state the code has not reached yet** (SPEC §14 gives the order), and the first step in that order is a hard sequencing constraint (SPEC §2).
