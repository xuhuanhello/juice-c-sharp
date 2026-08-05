#!/usr/bin/env python3
"""Emit a staged plugin's provenance file into `Report~/` (decisions #54, #65).

Why this file exists
--------------------
A maintainer receives a bug report against a binary that shipped inside a UPM
package. Neither side has the CI logs to hand, and the adopter has no git
history at all. This file ships *inside the package* and answers: which commit,
which CI run, which upstream pins, which compiler -- the maintainer says "paste
me the file at this path" and gets a definitive answer.

Where it lands (#65, overriding #54's "beside the binary")
----------------------------------------------------------
`Packages/datachannel-unity/Report~/<flattened platform>.json`, e.g.
`Report~/Windows-x86_64.json`. The `~` suffix makes Unity ignore the directory
outright, so a pure build record needs no `.meta` and no minted GUID -- it is
not an asset and nothing will ever reference it. `Plugins/` is thereby kept to
binaries and their `.meta`, which is the rule that survives this one file.

The flattened name comes from `gen_plugin_meta.report_name`, the single place
that derives it; see there for why it is not computed at each call site.

Nothing reads this at runtime: `Report~` content cannot be built into a Player
(#65 also ruled out an Editor-side "copy build info" convenience -- it would
serve a use case already judged not to exist).

Deliberately NOT a `dcu_build_info()` export (#54): that would change the ABI
(new symbol in expected-symbols.txt, DCU_ABI_VERSION bump) and would make the
binary content differ on every build, destroying the byte-for-byte
reproducibility that #27 relied on to verify the Meson -> CMake migration.

Also deliberately not "just put it in the commit message": adopters receive a
package directory or a tarball, with no git history to search.

Sources of truth, each read from exactly one place
--------------------------------------------------
- ABI version: `dcu.h`. NOT versions.lock -- that file carried a `dcu_abi=1`
  key that nothing read and that had already drifted from the real value (2).
  Removing it is the same rule as the export list: one authoritative source.
- Upstream pins: `versions.lock`.
- Commit: git.
- Compiler, architectures, deployment target: passed in by CMake, which is the
  only thing that actually knows them.
- CI run: the GitHub Actions environment.

Deliberately NOT recorded: whether the worktree was dirty (#68). The gate only
ever examines CI-produced records, and a CI build is a fresh checkout of
`source.commit` -- so the field was constant across the entire population it
would be read against, which is another way of saying it carried no
information. The question it looked like it answered ("is this someone's local
build?") is answered directly by `ci`, which is null exactly then. It was never
part of decision #54's field list; it arrived with the implementation (#55 E),
and after the binaries landed it read `true` on every CI build -- an LFS pointer
being overwritten by the staged artifact, i.e. the build doing its job. Its only
remaining effect was to cast doubt on perfectly good artifacts.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from gen_plugin_meta import report_name  # noqa: E402  单一拍平名推导，不在这里重复一份


def _force_utf8_output() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8")


_force_utf8_output()

ABI_RE = re.compile(r"^\s*#define\s+DCU_ABI_VERSION\s+(\d+)\s*$", re.MULTILINE)


class BuildInfoError(Exception):
    pass


def git(repo_root: Path, *args: str) -> str | None:
    """Run git; return None when git is unavailable or the command fails.

    A missing git is not fatal: a source tarball has no .git, yet the artifact
    is still worth describing. The absence is recorded explicitly as null
    rather than silently omitted, so a reader can tell "unknown" from "clean".
    """
    try:
        proc = subprocess.run(["git", "-C", str(repo_root), *args],
                              capture_output=True, text=True)
    except OSError:
        return None
    return proc.stdout.strip() if proc.returncode == 0 else None


def read_abi_version(header: Path) -> int:
    if not header.is_file():
        raise BuildInfoError(f"Missing the public header {header}, "
                             "which is the source of truth for DCU_ABI_VERSION.")
    match = ABI_RE.search(header.read_text(encoding="utf-8"))
    if not match:
        raise BuildInfoError(
            f"No DCU_ABI_VERSION define found in {header}.\n"
            "  This almost certainly means the regex drifted from the header style.\n"
            "  Failing hard rather than recording a wrong or absent ABI version.")
    return int(match.group(1))


def read_pins(lock: Path) -> dict[str, str]:
    if not lock.is_file():
        raise BuildInfoError(f"Missing {lock}, which pins the upstream versions.")
    pins: dict[str, str] = {}
    for raw in lock.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].strip()
        if "=" in line:
            key, _, value = line.partition("=")
            pins[key.strip()] = value.strip()
    if not pins:
        raise BuildInfoError(f"{lock} contains no key=value pins at all.")
    return pins


def ci_block() -> dict | None:
    """GitHub Actions facts, or None when this is not a CI build."""
    run_id = os.environ.get("GITHUB_RUN_ID")
    if not run_id:
        return None
    server = os.environ.get("GITHUB_SERVER_URL", "https://github.com")
    repo = os.environ.get("GITHUB_REPOSITORY", "")
    return {
        "run_id": run_id,
        "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
        "run_url": f"{server}/{repo}/actions/runs/{run_id}" if repo else None,
        "workflow": os.environ.get("GITHUB_WORKFLOW"),
        "ref": os.environ.get("GITHUB_REF"),
        # The triggering event decides whether source.commit is a real commit.
        # For `pull_request`, GitHub checks out a **synthetic merge ref** whose SHA
        # stops existing once the PR is merged -- an artifact built there records a
        # commit nobody can look up. gen_support_table.py refuses to land those.
        "event": os.environ.get("GITHUB_EVENT_NAME"),
    }


def main() -> int:
    ap = argparse.ArgumentParser(description="Emit a staged plugin's provenance file")
    ap.add_argument("--report-root", required=True, type=Path,
                    help="Packages/datachannel-unity/Report~")
    ap.add_argument("--rel", required=True,
                    help="the plugin directory relative to Plugins/, e.g. macOS or Windows/x86_64; "
                         "the file name is derived from it (gen_plugin_meta.report_name)")
    ap.add_argument("--repo-root", required=True, type=Path)
    ap.add_argument("--header", required=True, type=Path, help="native/dcu/include/dcu.h")
    ap.add_argument("--lock", required=True, type=Path, help="native/versions.lock")
    ap.add_argument("--platform", required=True, choices=["darwin", "linux", "windows"])
    ap.add_argument("--architectures", default="", help="semicolon-separated; CMAKE_OSX_ARCHITECTURES")
    ap.add_argument("--target-system", default="")
    ap.add_argument("--target-processor", default="")
    ap.add_argument("--compiler-id", default="")
    ap.add_argument("--compiler-version", default="")
    ap.add_argument("--deployment-target", default="")
    ap.add_argument("--cmake-version", default="")
    args = ap.parse_args()

    commit = git(args.repo_root, "rev-parse", "HEAD")

    archs = [a for a in args.architectures.replace(",", ";").split(";") if a]

    info = {
        "schema": 1,
        "plugin": "datachannel_unity",
        "abi_version": read_abi_version(args.header),
        "platform": args.platform,
        "architectures": archs or ([args.target_processor] if args.target_processor else []),
        "built_at": datetime.now(timezone.utc).replace(microsecond=0).isoformat(),
        # Still an object with one key: what identifies a build may grow (a tag,
        # a branch), and `source.commit` is the name the gate's error messages
        # and the README already use.
        "source": {
            # null when git is unavailable, so "unknown" stays distinguishable
            # from a real SHA -- the same reason the audit refuses to skip checks.
            "commit": commit,
        },
        "upstream": read_pins(args.lock),
        "toolchain": {
            "cmake": args.cmake_version or None,
            "compiler_id": args.compiler_id or None,
            "compiler_version": args.compiler_version or None,
            "target_system": args.target_system or None,
            "deployment_target": args.deployment_target or None,
        },
        "ci": ci_block(),
    }

    out = args.report_root / report_name(args.rel)
    out.parent.mkdir(parents=True, exist_ok=True)
    # newline="\n": on Windows, text mode would translate "\n" to "\r\n", so the
    # artifact and the repo copy would differ byte-for-byte (git normalises on
    # commit, hiding it). Landing the desktop batch is where that showed up.
    out.write_text(json.dumps(info, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
                   encoding="utf-8", newline="\n")
    where = "CI" if info["ci"] else "local"
    print(f"build-info: {args.platform} {'/'.join(info['architectures'])} "
          f"abi={info['abi_version']} commit={(commit or 'unknown')[:9]} [{where}] "
          f"-> {out.name}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except BuildInfoError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
