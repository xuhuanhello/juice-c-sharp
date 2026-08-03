# Research: MPL-2.0 obligations for prebuilt libdatachannel binaries in a UPM package

**Ticket:** [#5](https://github.com/xuhuanhello/juice-c-sharp/issues/5) (wayfinder research; part of #1)  
**Scope:** Open-source Unity Package Manager (UPM) package redistributing **precompiled libdatachannel** binaries (typically via Git LFS), plus a **custom C ABI layer** and **C# bindings**; also **datachannel-wasm** (MIT) if used for WebGL/WASM paths.  
**Status:** Research only — not legal advice. Confirm with counsel before shipping compliance-critical packaging.

---

## Primary sources

| Source | Role | URL / identifier |
|--------|------|------------------|
| Mozilla Public License 2.0 (full text) | Controlling license terms | https://www.mozilla.org/MPL/2.0/ |
| MPL 2.0 FAQ (Mozilla; last updated 2024-01-30) | Authoritative non-binding guidance | https://www.mozilla.org/en-US/MPL/2.0/FAQ/ |
| SPDX license list entry | Machine-readable id `MPL-2.0` | https://spdx.org/licenses/MPL-2.0.html — OSI-approved, FSF libre |
| libdatachannel | Upstream MPL-2.0 since **v0.18** (earlier: LGPLv2.1+) | https://github.com/paullouisageneau/libdatachannel — `LICENSE` is full MPL-2.0 |
| datachannel-wasm | MIT | https://github.com/paullouisageneau/datachannel-wasm — `LICENSE` (MIT) |
| SPDX MIT | Permissive companion for WASM path | https://spdx.org/licenses/MIT.html |

> FAQ disclaimer (Mozilla): the FAQ is intended to be accurate and helpful but **is not the license**; it does not substitute for reading the license or obtaining legal advice.

---

## Scenario under study

Assumed UPM package layout (illustrative):

```text
com.example.juice/                    # open-source UPM package (e.g. MIT/Apache-2.0 top-level)
  package.json
  LICENSE                             # package's own license
  Runtime/
    Plugins/
      *.dll / *.so / *.dylib / *.a    # prebuilt native: libdatachannel (+ static deps)  [Git LFS]
      juice_c_abi.*                   # custom C ABI layer (own sources + prebuilds)
    Scripts/                          # C# bindings (own sources)
  ThirdPartyNotices.md                # notices + source-offer map (recommended)
  licenses/
    MPL-2.0.txt
    MIT-datachannel-wasm.txt          # if shipping/using datachannel-wasm
    ...                               # crypto / usrsctp / etc. as needed
```

Distribution of those native binaries (through Git LFS, npm-style UPM git URL, release assets, or player builds that embed them) is distribution of **Executable Form** of Covered Software under MPL §1.6 / §3.2.

---

## 1. Source provision for linked / distributed object code

### 1.1 Key definitions (MPL §1)

- **Covered Software** (§1.4): Source Code Form bearing the Exhibit A notice (or equivalent attachment), its **Executable Form**, and **Modifications**, including portions thereof.
- **Executable Form** (§1.6): any form other than Source Code Form — includes `.dll`, `.so`, `.dylib`, static archives, and object files.
- **Source Code Form** (§1.13): the form preferred for making modifications.
- **Larger Work** (§1.7): Covered Software combined with other material **in separate file(s)** that is not Covered Software.
- **Modifications** (§1.10):
  - (a) any source file resulting from addition to / deletion from / modification of Covered Software contents; **or**
  - (b) any **new** source file that **contains** any Covered Software.

### 1.2 Distribution of Executable Form (MPL §3.2)

If you distribute Covered Software in Executable Form:

1. **§3.2(a)** — That Covered Software **must also be made available** in Source Code Form under §3.1, **and** you **must inform recipients** of the Executable Form **how they can obtain** a copy of that Source Code Form:
   - by **reasonable means**
   - in a **timely manner**
   - at a charge **no more than the cost of distribution** to the recipient
2. **§3.2(b)** — You **may** distribute the Executable Form under the MPL **or sublicense it under different terms**, **provided** those terms **do not limit or alter** recipients’ rights in the Source Code Form under the MPL.

Source Code Form itself (§3.1) must:

- be under the terms of the MPL;
- inform recipients that Source Code Form is governed by the MPL and how to obtain a copy of the License;
- **not** attempt to alter or restrict recipients’ rights in Source Code Form.

### 1.3 What Mozilla’s FAQ says for “we compiled / ship binaries” cases

| FAQ | Situation | Obligation summary |
|-----|-----------|--------------------|
| **Q5** | Mere *use* of MPL software | No MPL obligation until you **distribute outside** your organization. |
| **Q6** | Distribute only **inside** organization | Nothing (private modification/distribution). |
| **Q7** | Redistribute **complete, unchanged** executables built by someone else who already complied | Typically nothing extra **if** their §3.1/§3.2 notices and source availability check out. For **libraries** or partial redistributions, you may still need steps so users are informed of rights (§3.2(a)). |
| **Q8** | You distribute executables/libraries you compiled from **unchanged** third-party MPL source (standalone or Larger Work) | **Must** tell recipients **where to get source** for the MPLed code (§3.2). Executable may use a license of your choice if it does not interfere with MPL source rights. |
| **Q10** | Executable based on **modified** MPL source | Make MPL-licensed portions (incl. Modifications) available per §3.1 and tell recipients how to obtain them (§3.2). |
| **Q26** | What is “reasonable” for source offer | Internet distribution is the modern norm; mechanisms that add cost/complexity without necessity (e.g. courier-only) are generally **not** reasonable. |

### 1.4 Application to Git LFS prebuilts in an open-source UPM package

| Fact pattern | MPL analysis |
|--------------|--------------|
| Package hosts `libdatachannel` shared/static libs via **Git LFS** | Distributing **Executable Form** → §3.2 applies to the package maintainer. |
| Binaries built from **unmodified** upstream tag (e.g. `v0.24.5`) | Source offer may point to the **corresponding upstream source tag/commit** (and preferably also document exact build flags/toolchain). Still **must inform recipients** how to obtain that Source Code Form (FAQ Q8). |
| Binaries built from **patched** upstream or non-upstream commit | Source offer must include **your modified Source Code Form** under MPL (§3.1, §3.2, FAQ Q9–Q10) — not only stock upstream. |
| Static linking of MPL code into a single native plugin binary | Still Executable Form of Covered Software (and portions thereof). File-level copyleft does **not** disappear because of static linking (FAQ Q11 explicitly contemplates static linking into a proprietary Larger Work **without** open-sourcing non-MPL files). Source offer still covers **Covered Software** (and Modifications), not the entire game. |
| Dynamic linking (`.dll`/`.so`/`.dylib` next to managed assemblies) | Same §3.2 notice + source-availability duties for the Covered Software binary. |
| C# / IL assemblies that only **P/Invoke** the native library | Not Covered Software merely by linking/calling (separate files; see §2). |
| “We already open-source the whole UPM repo” | Satisfies source availability **if** the repo actually contains (or clearly offers) the Source Code Form matching the shipped binaries — **or** a durable, documented pointer to it. Shipping **only** binaries + no source map is non-compliant. |

**Practical minimum for §3.2(a) in-repo:**

1. A short **SOURCE-OFFER** (section in `ThirdPartyNotices.md` or `README`) stating, for each prebuilt artifact:
   - component name and version/commit;
   - license (`MPL-2.0`);
   - exact URL(s) to Source Code Form (upstream tag **and/or** fork/archive of any patches);
   - build recipe reference (CMake options, TLS backend, `NO_MEDIA`, etc.) so recipients can regenerate the binary.
2. Full **`MPL-2.0` license text** shipped with the package (e.g. `licenses/MPL-2.0.txt`).
3. Do **not** put terms on the binary that purport to strip MPL source rights (§3.2(b)).

**Cost / “timely”:** Hosting source on public GitHub (upstream + optional patch fork) at no charge meets the “no more than cost of distribution” and internet-as-reasonable-means guidance (FAQ Q26).

### 1.5 What is *not* required for mere binary redistribution of unmodified Covered Software

- You need **not** dual-license your entire UPM package as MPL-2.0.
- You need **not** open-source unrelated package files (C# bindings, docs, custom ABI in **separate** files with **no** MPL code inside them) — FAQ Q11 / §1.7 Larger Work.
- You need **not** ship full source **inside** every Git LFS blob; a clear, durable offer of Source Code Form is enough (§3.2(a)), though shipping or vendoring matching source is often easiest to audit.

---

## 2. Obligations if we modify upstream or add wrapper files

### 2.1 File-level copyleft (core design)

MPL is **weak / file-level** copyleft (FAQ Q1, Q11, Q12):

- Copyleft attaches to **files containing** MPL-covered code.
- **New files that contain no MPL-licensed code are not Modifications** and need not be under MPL, even when compiled/linked/distributed together as a Larger Work (FAQ Q11).
- Contrast: LGPL (library-based) / GPL (broader derivative-work) scopes (FAQ Q12).

### 2.2 Matrix: our expected work products

| Work product | Typical treatment under MPL | Required if we *distribute* it |
|--------------|----------------------------|--------------------------------|
| Unmodified libdatachannel sources used only to build | Covered Software (upstream) | Source available under MPL; notices preserved (§3.1, §3.4) |
| Patches / forks editing upstream `.cpp`/`.hpp`/etc. | **Modifications** (§1.10(a)) | Those files **must** be MPL-2.0; recipients informed; source offered with binaries (§3.1–3.2, FAQ Q9–Q10) |
| New file that **copy-pastes** or **embeds** MPL source | **Modification** (§1.10(b)) | Entire new file is Covered Software → MPL-2.0 + source offer |
| New **C ABI** `.c`/`.h` that only `#include`s public headers and calls the C API, without embedding Covered Software source | **Not** a Modification *of Covered Software* under §1.10 (separate file, no MPL code *in* the file) — part of Larger Work | Own license of choice (e.g. MIT). Still must comply for any Covered Software you ship alongside. |
| C# P/Invoke / managed wrappers (separate `.cs` files) | Larger Work / non-Covered (no MPL code in file) | Own license of choice |
| Unity `.meta`, `package.json`, samples, docs | Not Covered Software | Own license of choice |
| Mere **compilation** of unchanged MPL sources | Does **not** make you a “Contributor” (FAQ Q24) | Still §3.2 if you distribute the Executable Form |

### 2.3 Notices on modified source (§3.4, Exhibit A / SPDX)

- **§3.4:** You may not remove or alter the **substance** of license notices (copyright, patent notices, warranty disclaimers, liability limitations) in Source Code Form, except to fix known factual inaccuracies.
- **Exhibit A** (or equivalent attachment per §1.4):  
  `This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. ...`  
  If impractical to put in-file, place where a recipient would look (e.g. LICENSE in that directory) (Exhibit A; FAQ Q22).
- **SPDX** (FAQ Q4, Q27): `// SPDX-License-Identifier: MPL-2.0` can satisfy notice-related goals where communities use SPDX/REUSE; Mozilla still recommends Exhibit A text for consistency.
- Optional: accurate additional copyright lines for *your* Modifications.

### 2.4 Secondary Licenses (GPL family) — usually optional, not required

- §3.3 allows combining with GPL/LGPL/AGPL **Secondary Licenses** and offering recipients a choice **if** Covered Software is **not** “Incompatible With Secondary Licenses” (§1.5 / Exhibit B).
- libdatachannel ships standard MPL-2.0 text; treat as **compatible with Secondary Licenses by default** unless Exhibit B is attached (FAQ Q25).
- **You are not required** to dual-license under GPL merely because you ship libdatachannel. Secondary License mechanics matter mainly if *you* want GPL combination options.

### 2.5 datachannel-wasm (MIT) — if used

MIT terms (upstream `LICENSE`, copyright Paul-Louis Ageneau and others):

- Grant is broad (use, copy, modify, merge, publish, distribute, sublicense, sell).
- **Condition:** retain the **copyright notice and permission notice** in all copies or substantial portions of the Software.
- **No** copyleft / source-offer obligation analogous to MPL §3.2.
- Compatible with closed-source games; only **notice preservation** (+ usual warranty disclaimer).

If the UPM package vendors datachannel-wasm sources or ships WASM artifacts derived from it: include MIT text + copyright line in `ThirdPartyNotices` / `licenses/`.

---

## 3. Minimum LICENSE / third-party notices / docs checklist (package)

### 3.1 Hard requirements driven by MPL (Covered Software you distribute)

| # | Item | Basis |
|---|------|--------|
| 1 | Recipients can obtain **Source Code Form** of all MPL Covered Software **matching** shipped Executable Form (unmodified upstream **or** your Modifications) | §3.1, §3.2(a) |
| 2 | **Inform** recipients of binaries **how** to obtain that source (URL / path), timely, ≤ cost of distribution | §3.2(a), FAQ Q8/Q10/Q26 |
| 3 | Provide / point to a copy of the **MPL-2.0** license | §3.1 (“how they can obtain a copy of this License”); Exhibit A |
| 4 | **Do not** strip or gut upstream copyright / license / disclaimer notices in Source Code Form you distribute | §3.4 |
| 5 | Any **Modifications** you distribute in source must be under **MPL-2.0** with appropriate notice | §3.1, §1.10, FAQ Q9 |
| 6 | Terms on Executable Form must **not** restrict MPL rights in Source Code Form | §3.2(b) |
| 7 | If you add warranty/support for a fee, make clear it is **from you alone** (and indemnity to Contributors) | §3.5 |

### 3.2 Recommended UPM package compliance layout (minimum practical set)

```text
package.json
  - "license": "<YOUR_PACKAGE_SPDX>"          # e.g. MIT — package's own code
  - optional: license URL / documentationUrl

LICENSE                                      # your package license for first-party code

licenses/
  MPL-2.0.txt                                # full Mozilla MPL 2.0 text
  MIT-datachannel-wasm.txt                   # if applicable
  <other dependency license texts>           # see §3.3

ThirdPartyNotices.md                         # single place auditors look
  - table: component | version/commit | SPDX | source URL | binary path
  - explicit SOURCE OFFER paragraph for MPL Executable Form
  - crypto backend choice (OpenSSL / Mbed TLS / GnuTLS) + version

README.md or docs/compliance.md
  - short “Licensing” section pointing to ThirdPartyNotices
  - note that closed-source games may use the package (file-level copyleft)
  - build reproducibility notes for native plugins

Native plugin folders
  - keep MPL-covered prebuilts clearly named / versioned
  - avoid mixing unlicensed third-party blobs without notices
```

**SPDX identifiers to declare where appropriate:**

| Component | SPDX |
|-----------|------|
| libdatachannel (native, ≥0.18) | `MPL-2.0` |
| libjuice (default ICE backend; same author; MPL-2.0) | `MPL-2.0` |
| datachannel-wasm | `MIT` |
| First-party C ABI + C# (if you so choose) | e.g. `MIT` or `Apache-2.0` |
| Package composite | Often `MIT AND MPL-2.0 AND ...` style documentation; `package.json` `"license"` should reflect **your** primary grant and defer details to ThirdPartyNotices |

### 3.3 Dependency / statically linked third-party notices (beyond MPL)

Prebuilt `libdatachannel` almost always **embeds or links** other code. Those licenses apply **in addition** to MPL when you redistribute the binary. Track the **actual** build configuration.

Common stack (from upstream README / CMake options; verify per release):

| Dependency | Typical role | License notes (verify at pin) |
|------------|--------------|-------------------------------|
| **libjuice** | ICE (default) | **MPL-2.0** — same §3.2 source-offer class as libdatachannel if included in Executable Form |
| **usrsctp** | SCTP data channels | **BSD-3-Clause** — retain copyright + conditions in documentation/materials with binary redistributions |
| **plog** | logging | **MIT** — retain notice |
| **libsrtp** | media (if not `NO_MEDIA`) | Cisco-style BSD-ish / project license — retain notices; pin and copy exact file |
| **OpenSSL 3.x** / **Mbed TLS** / **GnuTLS** (+ Nettle, etc.) | TLS/DTLS | Apache-2.0 / dual / LGPL-family depending on choice — **document which backend you ship**; LGPL backends have their own source obligations |
| **nlohmann/json** | examples primarily | MIT — only if linked into shipped plugin |

**Checklist action:** For every shipped native artifact, freeze a **SBOM-like** line (name, version, SPDX, source URL, static vs dynamic) in `ThirdPartyNotices.md`. Re-run when bumping libdatachannel or changing CMake flags (`NO_MEDIA`, `USE_GNUTLS`, `USE_MBEDTLS`, system vs submodule deps).

### 3.4 Git LFS–specific packaging notes

- LFS pointers in git are fine; **recipients who fetch LFS objects receive Executable Form** → notices must be in the **non-LFS** tree (README / ThirdPartyNotices / licenses/), not only inside binary blobs.
- Tag releases so a given package version maps 1:1 to binary build ids and source commits.
- If UPM consumers might disable LFS, document that plugins are required and where the source offer lives so partial clones still see compliance docs.

### 3.5 Optional but high-value

- REUSE.toml / per-file SPDX headers on first-party sources (FAQ Q27).
- `NOTICE` aggregation for Apache-2.0 components (if OpenSSL 3.x / other Apache deps).
- CI check: fail if plugin version metadata ≠ ThirdPartyNotices pin.
- Publish a source tarball / `vendor/libdatachannel-<commit>.tar.gz` if you fear upstream tag deletion (durability of §3.2 offer).

---

## 4. Downstream impact for closed-source game adopters

*(Draft language suitable for a future product/spec “Compliance” subsection.)*

### 4.1 What MPL does *not* force on a closed-source game

Per MPL Larger Work rules and FAQ **Q11**:

- Using libdatachannel (including **static or dynamic** linking) does **not** require open-sourcing the game’s C#, assets, or other **separate files** that contain no MPL code.
- Proprietary licensing of the game as a whole remains possible **provided** MPL obligations for Covered Software are met.
- MPL is intentionally less “viral” than GPL; file-level copyleft is the design point (FAQ Q1, Q12).

### 4.2 What closed-source adopters still must do

When they **distribute** a player build (or any package) that includes MPL Covered Software in Executable Form:

1. **Source for Covered Software:** Ensure recipients can get Source Code Form of the MPL portions (libdatachannel, libjuice if included, **and any Modifications** they or their vendors made) under MPL terms (§3.2(a)).
   - Often satisfied by **passing through** this package’s ThirdPartyNotices / source URLs, **or** pointing to the same upstream tags — **if** binaries match and were not further patched.
   - If the studio patches native code or upgrades binaries, **they** become responsible for offering **their** corresponding source.
2. **Notices:** Do not remove required license/copyright notices for MPL and other third-party components (§3.4; MIT/BSD notice conditions).
3. **No rights stripping:** Game EULAs must not purport to cancel recipients’ rights to MPL Source Code Form (§3.2(b)).
4. **MIT pieces** (e.g. datachannel-wasm, plog, first-party MIT code): preserve copyright and permission notices only — no source-offer mandate.
5. **Internal-only** use (no distribution outside the organization): FAQ Q6 — no MPL distribution obligations.

### 4.3 Suggested one-paragraph “spec” summary

> **Third-party native stack:** Prebuilt libdatachannel (≥0.18) is licensed under **MPL-2.0** (SPDX: `MPL-2.0`). Redistribution of those binaries requires a **source offer** for the MPL Covered Software and preservation of notices; it does **not** require open-sourcing separately authored game or binding code that does not contain MPL code (file-level copyleft / Larger Work). Optional **datachannel-wasm** is **MIT** (notice-only). Package consumers shipping closed-source titles must retain `ThirdPartyNotices` (or equivalent), ensure MPL source availability for the exact native bits they ship, and avoid EULA terms that restrict MPL source rights. This is not legal advice.

### 4.4 Risk / edge cases for adopters (flag in compliance section)

| Risk | Mitigation |
|------|------------|
| Studio ships **patched** native plugin without publishing patch sources | Non-compliant under §3.2; publish patch fork or include sources in their OSS notice site |
| Studio **strips** ThirdPartyNotices from player builds | Restore notices in game legal/credits UI or install tree |
| Wrong TLS backend (e.g. LGPL GnuTLS) without LGPL compliance | Prefer OpenSSL 3.x (Apache-2.0) or document/comply with chosen backend |
| Using **libdatachannel &lt; 0.18** | Historical **LGPLv2.1+** — different obligations; avoid or treat as LGPL |
| Assuming “Unity plugin = no native license duties” | False — Executable Form still triggers §3.2 |

---

## 5. Concise answers to the ticket questions

1. **Source provision for linked/distributed object code**  
   Distributing prebuilt libdatachannel (Git LFS or otherwise) is §3.2 Executable Form distribution: **make matching Source Code Form available under MPL**, and **tell recipients how to get it** (reasonable internet means, timely, ≤ cost). Unmodified builds may point at upstream tags; modified builds must offer **your** sources. Linking (static/dynamic) does not by itself force open-sourcing non-MPL files (FAQ Q11).

2. **Modifying upstream or adding wrappers**  
   Edits to Covered Software files = Modifications → **must be MPL-2.0** with notices (§3.1, §3.4). New C ABI / C# files that **do not contain** MPL code are **not** Modifications and may use another license as part of a Larger Work (FAQ Q11). Copy-paste of MPL code into a new file **does** make that file Covered Software (§1.10(b)).

3. **Minimum package checklist**  
   Full MPL text; ThirdPartyNotices with **versioned source offer** for every native artifact; preserve copyright/license notices; document deps (especially crypto + libjuice); MIT notice for datachannel-wasm; do not EULA-away MPL source rights; pin build recipe for reproducibility.

4. **Closed-source game adopters**  
   May keep game code closed; must pass through **notices + MPL source offer** for native Covered Software they redistribute; handle their own patches; honor MIT/BSD notice-only deps; avoid LGPL backends unless prepared to comply.

---

## 6. References (clickable)

- MPL 2.0 text: https://www.mozilla.org/MPL/2.0/
- MPL 2.0 FAQ: https://www.mozilla.org/en-US/MPL/2.0/FAQ/
- SPDX MPL-2.0: https://spdx.org/licenses/MPL-2.0.html
- SPDX MIT: https://spdx.org/licenses/MIT.html
- libdatachannel: https://github.com/paullouisageneau/libdatachannel  
  - License note in README: MPL 2.0 since v0.18  
  - Site: https://libdatachannel.org/
- datachannel-wasm: https://github.com/paullouisageneau/datachannel-wasm  
  - MIT `LICENSE` (Copyright (c) 2017-2022 Paul-Louis Ageneau and others)
- libjuice (typical ICE dep, MPL-2.0): https://github.com/paullouisageneau/libjuice

---

## Document control

| Field | Value |
|-------|--------|
| Path | `docs/research/mpl-upm-binaries.md` |
| Issue | https://github.com/xuhuanhello/juice-c-sharp/issues/5 |
| Research date | 2026-08-02 |
| Disclaimer | Informational research for packaging design; **not legal advice**. |
