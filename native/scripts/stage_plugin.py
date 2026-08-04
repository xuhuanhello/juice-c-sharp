#!/usr/bin/env python3
"""把 CMake 构建出的 datachannel_unity 暂存进 UPM Plugins 布局。

只负责暂存。**离线门禁在 audit_plugin.py**，由 CMake 的第二条 POST_BUILD 命令
在本脚本之后调用 —— 拆开是因为 audit 需要 CMake 才知道的工具路径
（CMAKE_NM / CMAKE_READELF / CMAKE_LINKER），从这里透传只会多一层。

旧版本在 darwin 分支里 `if audit.is_file()` 才跑门禁，且 Windows/Linux 分支
根本不跑（#48 发现）—— 前者让「门禁文件没了」和「门禁通过了」长得一样，后者
让两个平台完全没有门禁。现在门禁由 CMake 无条件调用，三个平台一视同仁。
"""
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
        # check=True：install name 设失败会让插件按绝对路径去找自己，在采用者机器上
        # 加载失败。旧代码用 check=False 把这个失败吞掉了 —— 那是「缺席变成沉默」。
        subprocess.run(
            ["install_name_tool", "-id", "@loader_path/datachannel_unity", str(dest)],
            check=True,
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
