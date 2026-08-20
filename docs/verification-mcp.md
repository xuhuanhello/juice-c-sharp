# DataChannel Unity — MCP self-verification checklist

**Tickets:** [#21](https://github.com/xuhuanhello/juice-c-sharp/issues/21), revised by [#39](https://github.com/xuhuanhello/juice-c-sharp/issues/39)  
**Maps:** [#16](https://github.com/xuhuanhello/juice-c-sharp/issues/16), [#26](https://github.com/xuhuanhello/juice-c-sharp/issues/26), [#46](https://github.com/xuhuanhello/juice-c-sharp/issues/46)  
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
> `refresh_unity` does **not** help; neither does re-importing the plugin.
>
> **Replace the plugin file — write to a temp name and `mv` over, or close the Editor first; never overwrite it in place while an Editor holds it.** On macOS, overwriting a dylib that a running Editor has mapped invalidates the file's kernel code-signature registration. The Editor keeps running on its in-memory copy, so nothing looks wrong — but from that moment **any process that maps the file's bytes is killed**: `git status` / `git commit` die with `SIGKILL (Code Signature Invalid)` the moment they hash the "modified" plugin, while `git log` / `git diff-files` stay alive (they never read the bytes), which points every finger except the right one. Measured 2026-08-19: repo-wide git kills — surviving `brew reinstall git`, hitting every git client identically — traced to exactly this. Recovery: restore a clean copy with `git checkout -- <plugin>` (a new inode carries a fresh registration), then restart the Editor.
>
> **Cheap machine check** before trusting any result — the Editor must have started *after* the plugin was written:
>
> ```csharp
> var plugin = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(),
>     "Packages/datachannel-unity/Plugins/macOS/datachannel_unity.dylib");
> var editorStarted = System.DateTime.Now.AddSeconds(-UnityEditor.EditorApplication.timeSinceStartup);
> return new { stale = editorStarted <= System.IO.File.GetLastWriteTime(plugin) };
> ```
>
> `stale = true` ⇒ stop and restart the Editor. Do not interpret any step below.

**Native product path:** `Packages/datachannel-unity/Plugins/macOS/datachannel_unity.dylib` (built via **CMake** — see `docs/SPEC.md` §9).

> **No literal expected numbers in this file.** Export counts, test counts and the ABI version live where they get checked — `native/exports/expected-symbols.txt` and `dcu.h` — not in prose that nobody runs. See §11 of the SPEC for why.

---

## 0. Offline native gate (optional but fast)

Run from repo root before Editor work:

```bash
python3 native/scripts/audit_plugin.py \
  --binary Packages/datachannel-unity/Plugins/macOS/datachannel_unity.dylib \
  --platform darwin \
  --expected native/exports/expected-symbols.txt
```

**Expect:** **exit code 0** — the exported symbols diff clean against `native/exports/expected-symbols.txt`, and every dependency inside the allowlist.  
**The dependency rule on macOS is a path-prefix allowlist** (`/System/Library/Frameworks/`, `/usr/lib/`, `@loader_path/…`) plus a crypto-name ban that applies even under those prefixes — `/usr/lib/libssl.dylib` genuinely exists, and a prefix rule alone would wave it through. Anything from `/opt/homebrew/` or `/usr/local/` is red.

The same script covers the other two platforms with `--platform windows` (`dumpbin`, name allowlist) and `--platform linux` (`readelf`, name allowlist); the artifact paths are in SPEC §8.

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

The probe is `Assets/DataChannelUnity.Verification/Editor/DomainReloadProbe.cs`, exposed as three menu items.

| Step | |
|------|--|
| 1 | **Tools/DataChannelUnity/域重载自证/1. 布置** — creates objects, deliberately leaves them undisposed, holds them in a static field, and records the current `EnterPlayModeOptions` |
| 2 | Force a domain reload — recompile a script, or `manage_editor` play/stop **if** the setting recorded in step 1 says that route reloads the domain |
| 3 | **…/2. 判定** — reads `dcu_shutdown`'s undestroyed count and writes the verdict |
| 4 | Read `Library/dcu-domain-reload-probe.json` |

**Expect:** `undestroyedAfterReload` = **0**, `verdict` starting with `通过`.

The artifact separates the failure modes rather than just saying "not zero": a count **equal to `plantedObjects`** means the teardown hooks never ran; anything strictly between means they ran partially.

**Check `expectedPath` in the artifact before believing a green run.** With `DisableDomainReload` on, entering play mode does *not* reload the domain — that route exercises `SubsystemRegistration` and never reaches `beforeAssemblyReload`, so a pass there has not tested the path you may think it did. Step 2 must be a script recompile to reach `beforeAssemblyReload` under that setting.

**Reading a line out of the Console does not satisfy this step.** The probe must emit something assertable; otherwise this is the same disease as `|| true` (SPEC §11).

> Step 3 calls `dcu_shutdown`, which tears the native library down — that is the point, it is the only way to ask "how many objects survived". The probe therefore calls `dcu_init()` again **after** reading the count, and you should leave that in place: `DataChannelRuntime` has no idea the library went away, its `_initAttempted` is still `true`, so `EnsureNative()` returns immediately and does **not** re-initialise. Measured consequence of getting this wrong: every subsequent `dcu_pc_create` in the session fails with `raw=-102`, taking 25 native-tier tests down with it.

---

## 8b. On-device smoke, before a platform's binary lands

**Only needed when landing a new platform binary** (SPEC §10 / CONTRIBUTING). Everything above runs in the Editor on the development machine; this one runs the same PlayMode suite on the *target device*, because that is the only place a wrong `.meta` or a binary Unity refuses to load actually shows up.

**Build the existing suite into a Player whenever the Test Runner route is used.** For a Play-distributed AAB, a Player-resident equivalent runner is also permitted when it writes a machine-readable report from the installed AAB path: the report must identify the Runtime contracts it exercises, include non-zero total/passed/failed counts and failure detail, and must not claim to be Unity Test Framework XML. **iOS uses that same alternative**, for a different reason — the Test Runner route is structurally unavailable there (see the box below).

**The command line is the only Test Runner route that produces the artifact.** ~~Test Runner → PlayMode → *Run all tests (`<target platform>`)*, **or** headless~~ — that "or" was wrong, and wrong in the way that costs a whole run: a Test Runner **UI** run writes **no result file at all**. The class that serialises the XML (`ResultsSavingCallbacks`) lives in the package's `CommandLineTest` namespace and is registered in exactly one place, `CommandLineTest/Executer.cs` — the `-runTests` path. A UI run goes through `WindowResultUpdater`, which only repaints the window. (Verified against `com.unity.test-framework@1.1.33`, the version this project pins. The two `"Export"` strings in `PlayModeTestListGUI.cs` are the *build* button's caption when the target exports an Android Studio / Xcode project — nothing to do with results.)

**iOS-specific prerequisites (decision #96):**

- In `ProjectSettings`, set `appleEnableAutomaticSigning: 1` and `appleDeveloperTeamID` to your team ID **locally** — do **not** commit these values. The Team ID exposes the maintainer's identity and would force all contributors into automatic signing for a team they do not belong to.
- ~~Unity uses `xcodebuild -scheme Unity-iPhone -destination "platform=iOS,id=<udid>" test -allowProvisioningUpdates`~~ — Unity *would* run exactly that, but **`-runTests` never reaches the code that runs it**; see the iOS box below. You still need an Apple Developer account and a paired device, because *you* now invoke `xcodebuild` yourself.
- **On a paid account with `-allowProvisioningUpdates`, installing needs no human at the device** (measured, #97): `devicectl device install app` succeeded first try with **no trust prompt to tap**. That step is AFK. A personal (free) team is expected to need one manual trust tap on the first run — *expected, not measured here*.
- Device UDID: `xcrun devicectl list devices` or Xcode → Window → Devices and Simulators.

> **iOS: the Test Runner route does not work, and cannot be made to (#97).**
>
> `-runTests -testPlatform iOS -batchmode` builds the Xcode project, reports `Build Finished, Result: Success`, then **never invokes `xcodebuild`** and dies on the 600-second heartbeat timeout. This is structural, not a misconfiguration:
>
> Launch is performed by `iOStvOSCommonBuildWindowExtension.DoBuildAndRun()` — an extension of the **Build Settings window**, reachable only from the *Build And Run* button. `-runTests` goes through `PlayerLauncher` → `BuildPipeline.BuildPlayer`, which does not go through it. Batchmode has no window, therefore no caller.
>
> The decisive evidence is a **double absence in the log**: both branches of `DoBuildAndRun` open with a `Debug.Log` (`"Invoking xcodebuild: …"` / `"Invoking ios-deploy: …"`), and *neither* line ever appears. Do not spend another run on `appleBuildAndRunType`, on hunting for a `Unity-iPhone Tests` scheme (Unity generates no separate XCTest bundle), or on `EditorPrefs` — none of those are on the path.
>
> **Use the Player-resident route below instead.** Verified on Unity 2022.3.62f3.

| Step | |
|------|--|
| 0 | **Close the GUI Editor.** Batchmode cannot open a project another Editor already has open (`Multiple Unity instances cannot open the same project`) |
| 1 | `Unity -runTests -testPlatform <Android\|StandaloneWindows64\|StandaloneLinux64\|StandaloneOSX> -buildTarget <target> -batchmode -projectPath . -testResults <host path>/smoke-<platform>.xml` — **`iOS` is not in that list on purpose**; see the box above and use §8b-iOS |
| 2 | Let it run **on the device**, not in the Editor |
| 3 | Attach the NUnit result XML to that platform's ticket |

> **`-buildTarget` is not optional, and leaving it out fails in a misleading way.** `-testPlatform` tells the test framework what to run; it does **not** switch the Editor's active build target. With the project left on another target, the run reaches `Running tests for <platform>` and *then* dies at the build:
>
> ```
> Error building player because build target was unsupported
> Build Finished, Result: Failure.
> ```
>
> That message reads as "the platform's build module is not installed" — measured against a machine where the module was fully present (`MacStandaloneSupport/Variations` held all six player variations) and the project simply sat on `iOS`. Adding `-buildTarget OSXUniversal` made the same command build and pass. Check the active target before believing the module is missing.
>
> **`OSXUniversal` is the value measured here.** The `-buildTarget` names are parsed in native code, so they cannot be enumerated by reflection from inside the Editor — take the other platforms' spelling from Unity's [Editor command line arguments](https://docs.unity3d.com/2022.3/Documentation/Manual/EditorCommandLineArguments.html) reference rather than from this table, and note that the `-buildTarget` name is **not** the same string as `-testPlatform` (`OSXUniversal` vs `StandaloneOSX`).

**The XML is written on the host, not on the device** — the Editor process drives the run over adb/USB and serialises the result itself, so there is nothing to `adb pull`. (This holds for the Test Runner route only. On the §8b-iOS route the XML is written **on the device** and has to be copied off — step 4 there.) `-testResults` is optional; without it the file lands at `<project>/TestResults-<ticks>.xml` (`ResultsSavingCallbacks.GetDefaultResultFilePath`). Both option names are spelled without the leading dash in `CommandLineTest/SettingsBuilder.cs` (`testPlatform`, `testResults`), i.e. pass them as `-testPlatform` / `-testResults`.

**Expect:** every test passed, and — just as importantly — **a non-zero test count**. Zero tests run is a failure: it means the plugin did not load, which is the whole reason this step exists.

The suite-level teardown assertions (step 9) come along for free, since they are part of the same assembly.

**What does not count as evidence:** a screenshot, a Console line, or a description of what you saw. The result XML is the artifact; a manual step still has to be machine-judged (SPEC §11).

**Reading a failure.** A `DllNotFoundException` on the device with everything green in CI points at the `.meta`, not the binary — check that `gen_plugin_meta.py --check` is clean and that the platform's entry in `PLATFORMS` matches what a real Editor writes (`native/exports/plugin-meta-golden/`).

### 8b-iOS. The Player-resident route

`Assets/DataChannelUnity.Verification/` holds a runner that reproduces the three Runtime PlayMode contracts without referencing the Unity Test Framework, and writes NUnit-3-shaped XML to `Application.persistentDataPath`.

| Step | |
|------|--|
| 0 | Close the GUI Editor; set Team ID locally (do not commit); `xcrun devicectl list devices` shows `available (paired)` |
| 1 | `Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod DataChannelUnity.Verification.Editor.BuildDeviceVerification.BuildIOS -deviceVerificationOutput <dir>` |
| 2 | `xcodebuild -project <dir>/Unity-iPhone.xcodeproj -scheme Unity-iPhone -configuration Release -destination "platform=iOS,id=<udid>" -allowProvisioningUpdates DEVELOPMENT_TEAM=<team> build` |
| 3 | `xcrun devicectl device install app --device <udid> <app path>` then `... process launch --device <udid> <bundle id>` |
| 4 | `xcrun devicectl device copy from --device <udid> --domain-type appDataContainer --domain-identifier <bundle id> --source Documents/device-verification.xml --destination ./smoke-ios.xml` |
| 5 | Attach `smoke-ios.xml` to the ticket |

The same **non-zero count** rule applies: the report carries `total` / `passed` / `failed`, and zero cases is a failure.

**Step 3 is the one step that needs a human, and only because the screen must be unlocked.** Installing is AFK; *launching* onto a locked device fails — and fails **loudly, within a second**, which is the good outcome:

```
The request to open "<bundle id>" failed. (FBSOpenApplicationServiceErrorDomain error 1)
NSLocalizedFailureReason = … denied by service delegate (SBMainWorkspace) for reason: Locked
    ("Unable to launch <bundle id> because the device was not, or could not be, unlocked")
```

**This is a different shape from Android's, and better.** Android's equivalent (map [#78](https://github.com/xuhuanhello/juice-c-sharp/issues/78)) is a *silent* run to the timeout when the device dozes — you learn nothing until the heartbeat gives up. iOS names the reason immediately. **The cost is that iOS has no `svc power stayon` equivalent**: there is no public command to unlock or keep the screen awake, so a person unlocks the phone. In practice, wrap step 3 in a retry loop and wait (two 15-second rounds sufficed in #97).

**"Does Unity kill `xcodebuild` when it exits?" does not apply on this route.** On Android, Unity kills the adb server out from under a run. Here Unity emits the Xcode project and quits at step 1; `xcodebuild` and `devicectl` are invoked afterwards from outside the Editor, so there is no "Unity finished" moment that could interfere. **The risk was routed around, not verified away** — it comes back for anything that puts Unity back in the driving seat.

The report's `framework` attribute says **`NOT Unity Test Framework`**, and must keep saying so. The alternative clause replaces the *tool* that produces the XML — not the "machine-judged, non-zero count" criterion. A report dressed up as UTF output would tell its reader the preferred route ran when it did not.

`DeviceVerificationRunner` declares its `DllImport` target as `__Internal` under `UNITY_IOS && !UNITY_EDITOR`: on iOS the plugin is a static archive linked straight into the executable, so there is no library name to look up. Getting this wrong turns all three cases red with `DllNotFoundException` — the same symptom as a wrong `.meta`, from a different cause.

---

## 9. Suite-level teardown

**Nothing to run by hand — this is now part of both native tiers.** Each of them carries a `NativeSuiteTeardown` `[SetUpFixture]` whose `[OneTimeTearDown]` drains the pump, then asserts.

**Expect:** `dcu_event_queue_depth()` is **0**, and `dcu_shutdown()` returns **0**.

These are machine-checkable and independent of the log bridge, which is exactly why they replace "grep the Console for an English success line". Read the result off `resultState`, not `summary.failed` — see the note in step 3.

Both halves are now real: the status code catches a failing shutdown (e.g. `rtc::Cleanup` timing out), and `out_undestroyed` catches objects nobody disposed. The count comes from the dcu layer's own handle table — **not** from upstream, whose `rtcCleanup()` returns `void` and swallows its two most useful diagnostics into plog, reporting success even when it deadlocks.

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
| `git status` / `git commit` killed (`SIGKILL (Code Signature Invalid)`), only in this repo, every git client | The plugin was overwritten **in place** while an Editor had it loaded — its signature registration is stale, and git dies hashing the file's content. `git checkout -- <plugin>` (new inode), restart the Editor. Prevention is in the prerequisite box: replace, never overwrite |
| `DllNotFoundException` / native unavailable | Rebuild: `./native/scripts/build-macos.sh` (CMake entry, SPEC §9); confirm `.bundle` only under `Plugins/macOS/` |
| `permission denied` running a script | The script lost its executable bit in git — `git update-index --chmod=+x native/scripts/<name>.sh`. **Do not `chmod` and move on**; that hides the same regression next time |
| Audit fails on crypto dylibs | Product path must use subprojects MbedTLS static (never brew OpenSSL) |
| Audit symbol diff non-empty | Deliberate ABI change → update `native/exports/expected-symbols.txt` **with** the `DCU_ABI_VERSION` bump. Otherwise upstream leaked a symbol — check visibility flags and the allowlist |
| Native tier reports 0 tests | Plugin not loaded. This is a failure, not a skip |
| Redaction test fails | Assert on the **public** logging path output (`credentials=redacted@`), not on the internal regex |
| MCP `no_unity_session` / multi-instance | Connect MCP for **this** project; `set_active_instance` from `mcpforunity://instances` |
| Dual peer timeout in step 5 | Ensure `DataChannelRuntime.Pump()` is called in the wait loop (`execute_code` is not the PlayerLoop) |
| Dual peer timeout in step 7 | The opposite — the pump should be running by itself. Suspect pump registration or a third-party `SetPlayerLoop` overwrite |
| PlayMode run stalls, a case reports **`Cancelled by user`** when nobody cancelled | The Editor window is **unfocused**. `get_test_job` says so in `blocked_reason: editor_unfocused` + `stuck_suspected: true`, but the per-case message names the wrong cause — read those two fields before believing it. PlayMode needs focus unless `PlayerSettings.runInBackground` is on. It is **on** in this project since `1e6b5b2` — decided as product behaviour for the FishNet example's two-instance testing (a backgrounded peer must keep ticking), not as a test fixture — so if you still hit this, first check the setting has not been reverted |

---

## When to run this

See [`CONTRIBUTING.md`](../CONTRIBUTING.md). The gate text lives there, not here — a manual should say *how*, not *whether*.
