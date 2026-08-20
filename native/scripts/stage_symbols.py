#!/usr/bin/env python3
"""把暂存后、剥离前的插件拷进 Symbols~/，路径与 Plugins/ 镜像。

Unity 因目录名以 `~` 结尾而忽略它（与 Report~/、Samples~ 同一手法），所以
采用者从 git-URL 安装就能拿到崩溃符号，而不进 Player、不铸 .meta。

本脚本只拷。Plugins/ 上的 strip 是后面一条 POST_BUILD，由平台文件的
DCU_STRIP_DEBUG / DCU_STRIP_ARGS 驱动 —— 拷和剥必须拆开，两份才是同一次编译。

Windows 的行号在 PDB 里，不在 DLL 里：可选 --pdb 把链接器写出的 PDB 放到
与 DLL 同目录、与产物同 stem 的名字下（datachannel_unity.pdb），不跟 CMake
的 PREFIX=lib 走。
"""
from __future__ import annotations

import argparse
import pathlib
import shutil
import sys


def _force_utf8_output() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8")


_force_utf8_output()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--binary", required=True,
                    help="Staged plugin path (Plugins/<rel>/<artifact>), before strip")
    ap.add_argument("--symbols-root", required=True,
                    help="Packages/datachannel-unity/Symbols~")
    ap.add_argument("--rel", required=True, help="e.g. macOS or Android/arm64-v8a")
    ap.add_argument("--artifact-name", required=True)
    ap.add_argument("--pdb", default="",
                    help="MSVC program database to copy beside the dll (optional)")
    args = ap.parse_args()

    src = pathlib.Path(args.binary)
    if not src.is_file():
        print(f"staged plugin not found: {src}", file=sys.stderr)
        return 1

    dest_dir = pathlib.Path(args.symbols_root).resolve() / args.rel
    dest_dir.mkdir(parents=True, exist_ok=True)
    dest = dest_dir / args.artifact_name
    if dest.exists():
        dest.unlink()
    shutil.copy2(src, dest)
    print(f"Installed {dest}")

    if args.pdb:
        pdb_src = pathlib.Path(args.pdb)
        if not pdb_src.is_file():
            print(f"PDB not found: {pdb_src}", file=sys.stderr)
            return 1
        pdb_dest = dest.with_suffix(".pdb")
        if pdb_dest.exists():
            pdb_dest.unlink()
        shutil.copy2(pdb_src, pdb_dest)
        print(f"Installed {pdb_dest}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
