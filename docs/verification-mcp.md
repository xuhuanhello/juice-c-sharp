# DataChannel Unity — MCP self-verification checklist

**Ticket:** [#21](https://github.com/xuhuanhello/juice-c-sharp/issues/21)  
**Map:** [#16](https://github.com/xuhuanhello/juice-c-sharp/issues/16)  
**Purpose:** Agents must verify the package via **Unity MCP** (not “please test manually”), using a fixed sequence and expected results.

**Prerequisite:** Unity Editor has this project open (`juice-c-sharp`) with MCP for Unity connected. If multiple editors are connected, select this instance (`set_active_instance` / `mcpforunity://instances`).

**Native product path:** `Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle` (built via Meson — see `docs/SPEC.md` §9).

---

## 0. Offline native gate (optional but fast)

Run from repo root before Editor work:

```bash
./native/scripts/audit-macos-plugin.sh \
  Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle
```

**Expect:** `OK: 18 dcu_* exports, no forbidden crypto dylibs`  
**Expect `otool -L`:** only `@loader_path/…`, system `CoreFoundation` / `Security` / `libc++` / `libSystem` — no Homebrew openssl/mbedtls.

---

## 1. Refresh / compile ready

| MCP tool | Params |
|----------|--------|
| `refresh_unity` | `mode=force`, `scope=all`, `compile=request`, `wait_for_ready=true` |

**Expect:** success / editor ready for tools.  
If `instance_selection_required`, set active instance to this project and retry.

---

## 2. Clear console

| MCP tool | Params |
|----------|--------|
| `read_console` | `action=clear` |

---

## 3. EditMode tests

| MCP tool | Params |
|----------|--------|
| `run_tests` | `mode=EditMode`, `assembly_names=["DataChannelUnity.Tests.Editor"]`, `include_details=true`, `include_failed_tests=true`, `init_timeout=120000` |
| `get_test_job` | `job_id=<from run_tests>`, `wait_timeout=120`, `include_details=true` |

**Expect:**

| Field | Value |
|-------|--------|
| `status` | `succeeded` |
| `summary.total` | `4` |
| `summary.passed` | `4` |
| `summary.failed` | `0` |
| `resultState` | `Passed` |

Tests covered:

- `DataChannelInit_DefaultsReliableOrdered`
- `IceServer_CtorWithUrl`
- `PeerConnectionConfig_Defaults`
- `RedactIceCredentials_HidesUserInfo`

---

## 4. Native load + PeerConnection create (Editor)

| MCP tool | Params |
|----------|--------|
| `execute_code` | Roslyn/auto; call `DataChannelRuntime.EnsureNative()`, create/dispose `PeerConnection` |

Example body (abbreviated):

```csharp
DataChannelUnity.DataChannelRuntime.EnsureNative();
var available = DataChannelUnity.DataChannelRuntime.IsNativeAvailable;
var abi = available ? DataChannelUnity.DataChannelRuntime.AbiVersion : -1;
using (var pc = new DataChannelUnity.PeerConnection(new DataChannelUnity.PeerConnectionConfig()))
  return new { available, abi, handle = pc.NativeHandle };
```

**Expect:** `nativeAvailable=true`, `abiVersion=1`, `handle > 0`, no exception.

---

## 5. Dual Peer loopback (in-process fake signal)

| MCP tool | Params |
|----------|--------|
| `execute_code` | Two `PeerConnection`s, wire local description/candidate events to the other peer, `CreateDataChannel`, `Pump()` until both sides receive a message or ~8s timeout |

**Expect:** `success=true`, `gotA=true`, `gotB=true` within a few seconds (typically &lt; 1s on localhost).

---

## 6. Console errors

| MCP tool | Params |
|----------|--------|
| `read_console` | `action=get`, `types=["error"]`, optional `filter_text=DataChannel` |

**Expect:** no `DllNotFoundException`, no missing `datachannel_unity` / `__Internal`, no plugin load errors related to this package.

---

## 7. Scene Play Mode (DualPeer)

`Samples~/DualPeerLoopback` lives under UPM `Samples~` and is **not** compiled into the package until imported. For MCP Play verification this project uses:

`Assets/DataChannelVerify/DualPeerPlayDriver.cs` (same logic as the sample; namespace `DataChannelUnity.Verify`).

| MCP tool | Params |
|----------|--------|
| `manage_gameobject` | `action=create`, `name=DualPeerPlayDriver`, `components_to_add=["DualPeerPlayDriver"]` |
| `read_console` | `action=clear` |
| `manage_editor` | `action=play` |
| (wait ~2–5s; domain reload may disconnect MCP — retry tools) | |
| `read_console` | `types=all`, `filter_text=DualPeer` (or unfiltered) |
| `manage_editor` | `action=stop` |
| `manage_scene` | `action=save` (optional) |

**Expect console lines** (order may vary slightly):

- `A local offer` / `B local answer`
- `A DC open — sending ping` / `B DC open`
- `B received: ping-from-a` / `A received: pong-from-b`
- **`DualPeerLoopback SUCCESS`**

**Expect:** zero errors related to `DllNotFound` / plugin load.

---

## Baseline recorded (2026-08-02, juice-c-sharp @ 2022.3.62f3, macOS arm64)

| Step | Result |
|------|--------|
| Native audit | PASS — 18 `dcu_*`, no homebrew crypto dylibs |
| EditMode | **4/4 Passed** (~0.5s) |
| `EnsureNative` + `PeerConnection` | `available=true`, `abi=1`, handle created |
| Dual peer via `execute_code` | `success=true` in ~126ms |
| **Scene Play `DualPeerPlayDriver`** | **`DualPeerLoopback SUCCESS`** (ping/pong in console); 0 errors |
| Console DataChannel errors | 0 |

**Note:** Batchmode `-runTests` cannot open the project while the GUI Editor already has it open (`Multiple Unity instances cannot open the same project`). Prefer MCP while the Editor is running, or close the Editor before batchmode. Entering Play Mode may drop the MCP session briefly; retry after `refresh_unity` / reconnect.

---

## Failure playbook

| Symptom | Action |
|---------|--------|
| `DllNotFoundException` / native unavailable | Rebuild: `cd native && ./scripts/build-macos-arm64.sh` (Meson entry); confirm `.bundle` only under `Plugins/macOS/arm64/` |
| Audit fails on crypto dylibs | Product path must use subprojects MbedTLS static (not brew OpenSSL dylib) |
| Audit fails on non-`dcu_*` exports | Check `native/exports/macos-exported-symbols.txt` + linker flags |
| EditMode redact test fails | Regex must match `turn:user:pass@host` (`:(?://)?` not `://?`) |
| MCP `no_unity_session` / multi-instance | Connect MCP for **this** project; `set_active_instance` from `mcpforunity://instances` |
| Dual peer timeout | Ensure `DataChannelRuntime.Pump()` is called in the wait loop (Editor execute_code is not the PlayerLoop) |

---

## Agent policy

After changing native plugins, C# interop, or packaging:

1. Run this checklist via MCP.  
2. Do **not** mark packaging/native tickets done without recording step results (pass/fail + key fields).  
3. On failure, fix and re-run the failed steps before closing the ticket.
