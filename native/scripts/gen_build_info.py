#!/usr/bin/env python3
"""Emit build-info.json next to a staged plugin artifact (decision #54).

Why this file exists
--------------------
An adopter reports a bug against a binary that shipped inside a UPM package.
They have no git history, no CI logs, and no way to tell which build they have.
`build-info.json` travels *with* the artifact and answers: which commit, which
CI run, which upstream pins, which compiler.

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
- Commit / dirty state: git.
- Compiler, architectures, deployment target: passed in by CMake, which is the
  only thing that actually knows them.
- CI run: the GitHub Actions environment.
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
    }


def main() -> int:
    ap = argparse.ArgumentParser(description="Emit build-info.json for a staged plugin")
    ap.add_argument("--out", required=True, type=Path, help="path of the build-info.json to write")
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
    status = git(args.repo_root, "status", "--porcelain")

    archs = [a for a in args.architectures.replace(",", ";").split(";") if a]

    info = {
        "schema": 1,
        "plugin": "datachannel_unity",
        "abi_version": read_abi_version(args.header),
        "platform": args.platform,
        "architectures": archs or ([args.target_processor] if args.target_processor else []),
        "built_at": datetime.now(timezone.utc).replace(microsecond=0).isoformat(),
        "source": {
            "commit": commit,
            # null when git is unavailable, so "unknown" is distinguishable
            # from "clean" -- the same reason the audit refuses to skip checks.
            "dirty": None if status is None else bool(status),
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

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(info, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
                        encoding="utf-8")
    where = "CI" if info["ci"] else "local"
    dirty = " (dirty worktree)" if info["source"]["dirty"] else ""
    print(f"build-info: {args.platform} {'/'.join(info['architectures'])} "
          f"abi={info['abi_version']} commit={(commit or 'unknown')[:9]}{dirty} [{where}]")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except BuildInfoError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
