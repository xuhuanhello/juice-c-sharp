# DataChannel Unity — MCP self-verification checklist

**Tickets:** [#21](https://github.com/xuhuanhello/juice-c-sharp/issues/21), revised by [#39](https://github.com/xuhuanhello/juice-c-sharp/issues/39)  
**Maps:** [#16](https://github.com/xuhuanhello/juice-c-sharp/issues/16), [#26](https://github.com/xuhuanhello/juice-c-sharp/issues/26)  
**Purpose:** This is the **manual** — how verification is executed in this project with Unity MCP. *What* must be verified is [`docs/SPEC.md` §11](./SPEC.md); *when* it must be run is [`CONTRIBUTING.md`](../CONTRIBUTING.md).

**Prerequisite:** Unity Editor has this project open (`juice-c-sharp`) with MCP for Unity connected. If multiple editors are connected, select this instance (`set_active_instance` / `mcpforunity://instances`).

> ## ⚠️ Rebuilt the plugin? Restart the Editor first.
>
> Unity loads a native plug-in **once per Editor session and never unloads it** — not on domain reload, not on exiting play mode, not on re-import:
>
> > "A native plug-in cannot be unloaded; it remains loaded in a Unity session even after you change its settings. To unload the plug-in, you must restart Unity."
> > — [Unity 2022.3 Plugin Inspector](https://docs.unity3d.com/2022.3/Documentation/Manual/PluginInspector.html)
>
> So running this checklist after a rebuild, without restarting, **verifies the previous binary and reports a pass**. That is a false green of exactly the kind this project keeps getting caught by — it is indistinguishable from a real one in every field the checklist reads.
>
> `refresh_unity` does **not** help; neither does re-importing the `.bundle`.
>
> **Cheap machine check** before trusting any result — the Editor must have started *after* the plugin was written:
>
> ```csharp
> var bundle = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(),
>     "Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle/Contents/MacOS/datachannel_unity");
> var editorStarted = System.DateTime.Now.AddSeconds(-UnityEditor.EditorApplication.timeSinceStartup);
> return new { stale = editorStarted <= System.IO.File.GetLastWriteTime(bundle) };
> ```
>
> `stale = true` ⇒ stop and restart the Editor. Do not interpret any step below.

**Native product path:** `Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle` (built via **CMake** — see `docs/SPEC.md` §9).

> **No literal expected numbers in this file.** Export counts, test counts and the ABI version live where they get checked — `native/exports/expected-symbols.txt` and `dcu.h` — not in prose that nobody runs. See §11 of the SPEC for why.

---

## 0. Offline native gate (optional but fast)

Run from repo root before Editor work:

```bash
./native/scripts/audit-macos-plugin.sh \
  Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle
```

**Expect:** **exit code 0** — the exported symbols diff clean against `native/exports/expected-symbols.txt`, and no forbidden crypto dylibs.  
**Expect `otool -L`:** only `@loader_path/…`, system `CoreFoundation` / `Security` / `libc++` / `libSystem` — no Homebrew openssl/mbedtls.

A diff failure means one of two things, and the script's output distinguishes them: a symbol was renamed or added deliberately (update the expectations file **in the same commit as the `DCU_ABI_VERSION` bump**), or upstream leaked a symbol through the allowlist.

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

## 3. EditMode tests — two tiers

### 3a. Managed tier (no native plugin required)

| MCP tool | Params |
|----------|--------|
| `run_tests` | `mode=EditMode`, `assembly_names=["DataChannelUnity.Tests.Editor"]`, `include_details=true`, `include_failed_tests=true`, `init_timeout=120000` |
| `get_test_job` | `job_id=<from run_tests>`, `wait_timeout=120`, `include_details=true` |

### 3b. Native / EditMode tier (plugin must be loaded)

| MCP tool | Params |
|----------|--------|
| `run_tests` | same, with `assembly_names=["DataChannelUnity.Tests.Editor.Native"]` |
| `get_test_job` | as above |

**Expect, for both:**

| Field | Value |
|-------|--------|
| `status` | `succeeded` |
| `summary.failed` | `0` |
| `resultState` | `Passed` |

Do **not** assert `summary.total` and do not enumerate case names here — the checklist is not a copy of the test directory, and both go stale on the next test added.

**`summary.failed` alone is not enough — you must read `resultState`.** The suite-level teardown (step 9) lives in an NUnit `[SetUpFixture]`, which is *not* a test case: measured with a deliberate probe, a failing `[OneTimeTearDown]` reports `summary.failed = 0` **and** `resultState = Failed(Child)`. Anyone checking only the failure count reads a red run as green.

**A skipped/ignored native test is a failure, not a pass** (SPEC §11, *absence must be failure*). If the native tier reports zero tests run, the plugin did not load — treat it as red.

---

## 4. Native load + PeerConnection create (Editor)

| MCP tool | Params |
|----------|--------|
| `execute_code` | Roslyn/auto; check availability and ABI, create/dispose a `PeerConnection` |

Example body (abbreviated):

```csharp
var available = DataChannelUnity.DataChannelRuntime.IsNativeAvailable;
var abi = available ? DataChannelUnity.DataChannelRuntime.AbiVersion : -1;
using (var pc = new DataChannelUnity.PeerConnection(new DataChannelUnity.PeerConnectionConfig()))
  return new { available, abi, state = pc.ConnectionState.ToString() };
```

**Expect:** `available=true`, no exception, and `abi` **equal to `DCU_ABI_VERSION` in `native/dcu/include/dcu.h`** — read the header, do not compare against a number written here.

`NativeHandle` is `internal` (SPEC §6); use `ToString()` if a handle is wanted for diagnostics.

---

## 5. Dual Peer loopback via `execute_code` (in-process fake signal)

| MCP tool | Params |
|----------|--------|
| `execute_code` | Two `PeerConnection`s, wire local description/candidate events to the other peer, `CreateDataChannel`, `Pump()` until both sides receive a message or ~8s timeout |

**Expect:** `success=true`, `gotA=true`, `gotB=true` within a few seconds (typically &lt; 1s on localhost).

This step drives the pump **by hand** — `execute_code` is not the PlayerLoop. It therefore proves the event path but says nothing about pump registration; that is what step 7 is for.

---

## 6. Console errors

| MCP tool | Params |
|----------|--------|
| `read_console` | `action=get`, `types=["error"]`, optional `filter_text=DataChannel` |

**Expect:** no `DllNotFoundException`, no missing `datachannel_unity` / `__Internal`, no plugin load errors related to this package.

**Expected, not a failure:** a `dropped N log messages` warning when running at Verbose under load — the log queue is bounded by design (SPEC §7). Do not treat it as a defect, and do not write a "console must be perfectly clean" gate that cannot tell it apart from a real one.

---

## 7. PlayMode smoke (dual peer, real PlayerLoop)

| MCP tool | Params |
|----------|--------|
| `run_tests` | `mode=PlayMode`, `assembly_names=["DataChannelUnity.Tests.Runtime"]`, `include_details=true`, `include_failed_tests=true` |
| `get_test_job` | `job_id=<from run_tests>`, `wait_timeout=180`, `include_details=true` |

**Expect:** `status=succeeded`, `summary.failed=0`.

**What this tier is for:** it asserts that messages flow **without anyone calling `Pump()` manually** — i.e. that `RegisterPump()` actually installed into the PlayerLoop. No other step covers that path.

> There must be **no `DataChannelRuntime.Pump()` call anywhere in this assembly.** Adding one to make a red test green destroys the only thing the tier verifies. The assertions were confirmed to depend on the pump by a throw-away mutation (same test, main thread blocked instead of yielding → nothing delivered, test failed) — a gate never observed failing is not known to work.

Currently covers the **before-connect** channel-creation order only. The complementary **after-connect** order (§4, where the open race lives) arrives with the rest of the required-contract list.

Headless equivalent, when the Editor is not holding the project open:

```bash
Unity -runTests -testPlatform PlayMode -batchmode \
  -projectPath . -testResults /tmp/playmode.xml
```

Entering play mode may briefly drop the MCP session; retry after `refresh_unity`.

---

## 8. Domain-reload lifecycle (manual step, machine-judged)

The test framework does not survive a domain reload, so this cannot be an ordinary test — and a probe compiled dynamically through `execute_code` is destroyed by the very transition it is trying to observe. Use a **persistent Editor script** committed to the project.

| Step | |
|------|--|
| 1 | With the persistent probe present, create a PeerConnection and a DataChannel in edit mode |
| 2 | Force a domain reload (recompile, or `manage_editor` play/stop with Reload Domain on) |
| 3 | Read the probe's **artifact** — a live-object count written to `SessionState` or a file |

**Expect:** live objects **0** after `beforeAssemblyReload`, and after exiting play mode.

**Reading a line out of the Console does not satisfy this step.** The probe must emit something assertable; otherwise this is the same disease as `|| true` (SPEC §11).

---

## 9. Suite-level teardown

**Nothing to run by hand — this is now part of both native tiers.** Each of them carries a `NativeSuiteTeardown` `[SetUpFixture]` whose `[OneTimeTearDown]` drains the pump, then asserts.

**Expect:** `dcu_event_queue_depth()` is **0**, and `dcu_shutdown()` returns **0**.

These are machine-checkable and independent of the log bridge, which is exactly why they replace "grep the Console for an English success line". Read the result off `resultState`, not `summary.failed` — see the note in step 3.

> **Half of this is not covered yet, stated rather than implied.** `dcu_shutdown()` currently returns only a status code, so the assertion catches a failing shutdown (e.g. `rtc::Cleanup` timing out) but *not* how many objects were left undestroyed. Making it return that count is [#37](https://github.com/xuhuanhello/juice-c-sharp/issues/37) decision 7 and lands with step 5 of §14.

The fixture calls `dcu_init()` again after the assertion, so the Editor is not left holding a library the managed side still believes is initialised. That restore happens **after** the assertion and hides nothing.

---

## Baseline snapshot (2026-08-03, juice-c-sharp @ 2022.3.62f3, macOS arm64)

> **This is a historical record, not an expectation.** It describes the tree **before** the implementation-hardening work (`DCU_ABI_VERSION` 1, 18 exports, C API implementation). Do not compare a current run against these numbers — compare against `expected-symbols.txt` and `dcu.h`.

| Step | Result at that time |
|------|---------------------|
| Native audit | PASS — 18 `dcu_*`, no homebrew crypto dylibs |
| EditMode | 4/4 Passed (~0.5s) |
| Native load + `PeerConnection` | `available=true`, `abi=1`, handle created |
| Dual peer via `execute_code` | `success=true` in ~126ms |
| Scene Play dual-peer driver | ping/pong completed; 0 errors |
| Console DataChannel errors | 0 |

Recorded in [#42](https://github.com/xuhuanhello/juice-c-sharp/issues/42) as the pre-migration baseline for the C++ API move.

**Note:** Batchmode `-runTests` cannot open the project while the GUI Editor already has it open (`Multiple Unity instances cannot open the same project`). Prefer MCP while the Editor is running, or close the Editor before batchmode.

---

## Failure playbook

| Symptom | Action |
|---------|--------|
| Behaviour looks unchanged after a rebuild | The Editor is still running the **old** binary — restart it. See the prerequisite box at the top; this fails *silently* and looks like a pass |
| `DllNotFoundException` / native unavailable | Rebuild: `./native/scripts/build-macos-arm64.sh` (CMake entry, SPEC §9); confirm `.bundle` only under `Plugins/macOS/arm64/` |
| `permission denied` running a script | The script lost its executable bit in git — `git update-index --chmod=+x native/scripts/<name>.sh`. **Do not `chmod` and move on**; that hides the same regression next time |
| Audit fails on crypto dylibs | Product path must use subprojects MbedTLS static (never brew OpenSSL) |
| Audit symbol diff non-empty | Deliberate ABI change → update `native/exports/expected-symbols.txt` **with** the `DCU_ABI_VERSION` bump. Otherwise upstream leaked a symbol — check visibility flags and the allowlist |
| Native tier reports 0 tests | Plugin not loaded. This is a failure, not a skip |
| Redaction test fails | Assert on the **public** logging path output (`credentials=redacted@`), not on the internal regex |
| MCP `no_unity_session` / multi-instance | Connect MCP for **this** project; `set_active_instance` from `mcpforunity://instances` |
| Dual peer timeout in step 5 | Ensure `DataChannelRuntime.Pump()` is called in the wait loop (`execute_code` is not the PlayerLoop) |
| Dual peer timeout in step 7 | The opposite — the pump should be running by itself. Suspect pump registration or a third-party `SetPlayerLoop` overwrite |

---

## When to run this

See [`CONTRIBUTING.md`](../CONTRIBUTING.md). The gate text lives there, not here — a manual should say *how*, not *whether*.
