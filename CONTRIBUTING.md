# Contributing

**Decision:** [#39](https://github.com/xuhuanhello/juice-c-sharp/issues/39)

This file is the **gate text**: what has to happen, and when. It is deliberately separate from the other two layers —

| | |
|--|--|
| [`docs/SPEC.md`](./docs/SPEC.md) | The specification — *what* must be true. Tool-agnostic |
| **this file** | The gates — *what to do, when* |
| [`docs/verification-mcp.md`](./docs/verification-mcp.md) | The manual — *how* to execute verification in this project, with Unity MCP |

The spec is normative. Where the code disagrees with it, the code is wrong.

---

## One principle above the checklists

> **Make absence a failure, not a silence.**

`Assert.Ignore("native plugin missing")`, a fallback `chmod +x`, `|| true`, an ignored exit code — these are one disease with four faces. Each makes *"this never ran"* and *"this ran and passed"* look identical in a report.

This project has already been bitten three times:

- `meson subprojects download || true` hid an exit code of 2 **and** would have hidden any real network failure.
- CI's fallback `chmod +x` hid the `core.fileMode` trap that made a real `git clone` fail to build — the fallback existed *because* someone had hit the problem before and papered over it.
- A "clean clone builds" claim passed because it was verified with `rsync -a` (which preserves working-tree permissions) instead of a real clone.

It is written as a principle rather than three bans because a list of bans only blocks the shapes already encountered. When you are about to add a fallback, ask what it will look like on the day it fires and nobody notices.

---

## Changing the implementation

Applies to any change under `native/`, `Packages/datachannel-unity/Runtime/`, or the plugin packaging.

1. **Check the sequencing constraint first.** The libdatachannel C++ API migration must land **before** the ownership / event-ABI / error-code / lifecycle rewrites (SPEC §2, §14). If your change is one of those and the migration has not happened, do the migration first.
2. **Managed tier green** — `DataChannelUnity.Tests.Editor`, in your local Editor.

   > **Nothing on this list is enforced by CI.** Unity does not run in CI at all ([#43](https://github.com/xuhuanhello/juice-c-sharp/issues/43), SPEC §11): licensing a Unity job would turn every fork PR red on an empty secret, and tests that need Unity are worth more inside a real Editor anyway. CI runs the native build, the exported-symbol diff, the crypto-dylib audit, the script-executable-bit assertion and shell/Python syntax — **that is the only thing that will stop you automatically.** Everything below is on you.
3. **Native tiers green** — `DataChannelUnity.Tests.Editor.Native` and `DataChannelUnity.Tests.Runtime`. A tier that reports **zero tests run** is a failure, not a pass: it means the plugin did not load.
4. **Offline native gate** — `native/scripts/audit-macos-plugin.sh` exits 0, with the exported-symbol diff clean against `native/exports/expected-symbols.txt`.
5. **If you changed the ABI on purpose** — update `expected-symbols.txt` and bump `DCU_ABI_VERSION` **in the same commit**. That is the whole reason the expectations file lists names rather than a count: the two changes land in one diff, so a forgotten bump is visible in review.
6. **Run the MCP checklist** ([`docs/verification-mcp.md`](./docs/verification-mcp.md)) and **record the results** — pass/fail plus the key fields — on the ticket or PR. Do not close a native or packaging ticket without them, and do not substitute "please test this yourself".
7. **Suite teardown** — `dcu_shutdown()` reports 0 undestroyed objects; `dcu_event_queue_depth()` is 0.
8. **On failure, fix and re-run the failed steps** before closing anything.

### Adding a required-contract test

The gate list in SPEC §11 is not a coverage target. A contract belongs on it only if **measurement or research overturned an intuition** about it. The gate's job is to stop a future implementer from reverting a decision because it looks wrong; behaviour that matches intuition is adequately covered by ordinary tests and review.

Evidence for the rule, from this project: two reference bindings' connectivity tests both missed the "create a DataChannel *after* connecting" race — not out of laziness, but because that path is not the one intuition reaches for.

### Verifying reproducibility locally

**Use a real `git clone` into a temporary directory.** `cp` and `rsync` do not count — `rsync -a` preserves working-tree permissions, and this repository has `core.fileMode = false`, so a file's committed mode and its local mode can differ silently. That exact gap once produced a passing verification and a failing clone.

CI's `actions/checkout` *is* a real clone and restores modes from git, so CI already covers this; the rule is for local checks.

---

## Upgrading libdatachannel

Four steps, all machine-checkable:

1. Update `native/versions.lock` per the semver policy (SPEC §3).
2. **Runtime contract tests green.** These *replace* the compile-time `static_assert` on upstream enum values, which the C++ API route makes impossible to express. They must confirm that every upstream state/exception maps to the expected dcu value, and that an out-of-range raw value lands on `Unknown` rather than throwing.
3. Exported-symbol diff clean. A difference here means upstream leaked symbols through the allowlist.
4. All three test tiers plus the dual-peer PlayMode smoke green.

> **Known gap, stated rather than hidden:** step 2 catches values *appended* to an upstream enum (they fall out of range → `Unknown`). It does **not** catch values *inserted in the middle*, which silently change what existing values mean while every assertion stays green. C++ enums are not reflectable and there is no cheap exhaustive check. The gap is deliberate.

"Read the upstream CHANGELOG by hand" is deliberately **not** on this list. It is the one item no machine can enforce, and keeping it would dilute the four that can.

---

## Working with decisions

Decisions live as closed issues on three wayfinder maps ([#1](https://github.com/xuhuanhello/juice-c-sharp/issues/1), [#16](https://github.com/xuhuanhello/juice-c-sharp/issues/16), [#26](https://github.com/xuhuanhello/juice-c-sharp/issues/26)); SPEC §15 indexes all of them with the section each one landed in.

**Read the spec, not the tickets.** The tickets carry the arguments; the spec carries the conclusions, and it is written so that you should not need to go back to them. If you do need to, that is a defect in the spec worth reporting.

**Changing a decision is a new decision.** Silent drift — implementing something the spec does not say, or quietly not implementing something it does — is the failure mode the whole structure exists to prevent. Note that decisions have been reversed before (Meson, SPEC §9), and the honest reason was recorded: it had never actually been implemented.

### Before adding a defensive mechanism

Read the relevant upstream path **in full** before concluding something is missing. This project has twice built, or nearly built, a guard against a failure that could not occur:

- A "poison event permanently blocks the unbounded control queue" scenario was reasoned out from a code fragment, and a new native-side invariant was recommended for it. Measurement showed the function's return domain is exhaustively `OK / NOT_AVAIL / TOO_SMALL / INVALID` with no data-dependent failure path — the failure was **physically impossible**. The proposed invariant would have had no structural support at all; it would have depended on every future maintainer remembering it.
- Three "gaps" in the native callback layer turned out to be upstream's deliberate design (callbacks notify, state is queried) rather than oversights — and six reference implementations agreed with upstream, not with us.

When pricing a new invariant, price *who maintains it and what enforces it*, not just the lines of code.
