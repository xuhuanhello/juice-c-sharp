#!/usr/bin/env python3
"""Stage Meson/CMake-built datachannel_unity into UPM Plugins layout."""
from __future__ import annotations

import argparse
import os
import pathlib
import shutil
import subprocess
import sys


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--binary", required=True, help="Path to built shared library")
    ap.add_argument("--host-system", required=True, choices=["darwin", "windows", "linux"])
    ap.add_argument("--plugin-root", required=True)
    ap.add_argument("--rel", required=True, help="e.g. macOS/arm64")
    args = ap.parse_args()

    binary = pathlib.Path(args.binary)
    if not binary.is_file():
        # Meson may pass target name; search nearby
        print(f"binary not found: {binary}", file=sys.stderr)
        return 1

    plugin_root = pathlib.Path(args.plugin_root).resolve()
    out_dir = plugin_root / args.rel
    out_dir.mkdir(parents=True, exist_ok=True)

    # Remove forbidden dual product
    for junk in out_dir.glob("libdatachannel_unity.dylib*"):
        if junk.is_file():
            junk.unlink()
        elif junk.is_dir():
            shutil.rmtree(junk)

    if args.host_system == "darwin":
        bundle = out_dir / "datachannel_unity.bundle"
        if bundle.exists():
            shutil.rmtree(bundle)
        mac = bundle / "Contents" / "MacOS"
        mac.mkdir(parents=True)
        dest = mac / "datachannel_unity"
        shutil.copy2(binary, dest)
        subprocess.run(
            ["install_name_tool", "-id", "@loader_path/datachannel_unity", str(dest)],
            check=False,
        )
        plist = bundle / "Contents" / "Info.plist"
        plist.write_text(
            """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>datachannel_unity</string>
  <key>CFBundleIdentifier</key><string>com.xuhuanhello.datachannel.unity</string>
  <key>CFBundlePackageType</key><string>BNDL</string>
</dict></plist>
""",
            encoding="utf-8",
        )
        print(f"Installed {bundle}")
        audit = pathlib.Path(__file__).resolve().parent / "audit-macos-plugin.sh"
        if audit.is_file():
            subprocess.run(["bash", str(audit), str(bundle)], check=True)
    else:
        # Windows/Linux: copy with expected name
        if args.host_system == "windows":
            dest = out_dir / "datachannel_unity.dll"
        else:
            dest = out_dir / "libdatachannel_unity.so"
        shutil.copy2(binary, dest)
        print(f"Installed {dest}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
