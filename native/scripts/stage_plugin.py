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


def _force_utf8_output() -> None:
    """Windows 上非 TTY 的 stdout 默认是 cp1252，本脚本的中文输出会直接
    UnicodeEncodeError（首次 Windows CI 实跑时炸在这里）。在脚本自身修而不是
    靠 workflow 设 PYTHONIOENCODING —— 那样无论谁怎么调用它都成立。"""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8")


_force_utf8_output()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--binary", required=True, help="Path to built shared library")
    # 平台键与产物名都由 native/platforms/<系统名>.cmake 声明后传进来，本脚本
    # **不再自己推导名字**（决议 #81）。两处各推一份就是两个真相源，而其中一份
    # 迟早会与另一份错开 —— 那正是 audit 去找的文件与构建写出的文件可以悄悄不同
    # 的那种缝（#65 在溯源文件名上已经踩过同一形状）。
    ap.add_argument("--platform", required=True,
                    choices=["darwin", "windows", "linux", "android"])
    ap.add_argument("--artifact-name", required=True,
                    help="e.g. datachannel_unity.dylib / libdatachannel_unity.so")
    ap.add_argument("--plugin-root", required=True)
    ap.add_argument("--rel", required=True, help="e.g. macOS or Android/arm64-v8a")
    args = ap.parse_args()

    binary = pathlib.Path(args.binary)
    if not binary.is_file():
        # Meson may pass target name; search nearby
        print(f"binary not found: {binary}", file=sys.stderr)
        return 1

    plugin_root = pathlib.Path(args.plugin_root).resolve()
    out_dir = plugin_root / args.rel
    out_dir.mkdir(parents=True, exist_ok=True)

    # 清掉遗留的旧形态：曾经的 .bundle 目录，以及带 lib 前缀的误产物。
    # 注意不能再无差别删 "libdatachannel_unity.dylib*" —— macOS 的正品现在就是
    # 一个 .dylib（不带 lib 前缀），glob 写宽了会把它自己删掉。
    for junk in list(out_dir.glob("*.bundle")) + list(out_dir.glob("libdatachannel_unity.dylib")):
        if junk.is_dir():
            shutil.rmtree(junk)
        elif junk.is_file():
            junk.unlink()

    dest = out_dir / args.artifact_name
    if dest.exists():
        dest.unlink()
    shutil.copy2(binary, dest)

    if args.platform == "darwin":
        # 单个 universal .dylib，不是 .bundle 目录。
        #
        # #10 的题面把「macOS bundle 还是 dylib」列为待决问题，决议表写了 bundle
        # 但**没有记任何理由**；#19 只是照着执行。实测两者在构建管线里完全等价
        # （CalculateFinalPluginPath 都返回非空、isNativePlugin 都为真、兼容位一样），
        # 而 Unity 官方的 Burst 与 collab-proxy 发的就是 .dylib。
        #
        # 目录形态只带来成本：.gitattributes 要按路径写 LFS 规则（里面的 Mach-O
        # 无扩展名，按扩展名的规则套不上）、还要再加一条 Info.plist 例外、这里要
        # 造 Contents/MacOS 与 plist、支持表要为目录写前缀匹配（第一版还写错了）。
        # 而 macOS 是六个平台里唯一的目录形态。
        #
        # install name 是 macOS 独有的一步，不是命名差异 —— 命名已经由
        # --artifact-name 统一处理掉了，这里剩下的才是真正的平台差异。
        # check=True：install name 设失败会让插件按绝对路径去找自己，在采用者机器上
        # 加载失败。旧代码用 check=False 把这个失败吞掉了 —— 那是「缺席变成沉默」。
        subprocess.run(
            ["install_name_tool", "-id", f"@loader_path/{args.artifact_name}", str(dest)],
            check=True,
        )

    print(f"Installed {dest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
