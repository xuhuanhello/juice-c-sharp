#!/usr/bin/env python3
"""原生插件离线门禁（SPEC §11，决议 #50）。

两条断言：

1. **导出集**必须与 native/exports/expected-symbols.txt 逐符号一致。
2. **依赖集**必须落在允许列表内，且不得含 crypto。

为什么用 Python 而不是 shell 或 `cmake -P`：
    shell 在 Windows runner 上有两个坑（checkout 出来是 CRLF、可执行位断言恒真，
    见 #48）。`cmake -P` 躲得开这两个坑，但脚本模式下拿不到 CMAKE_NM 等工具变量
    （实测全部未定义 —— 那些变量只在 project() 配置期存在），工具路径无论如何都
    得当参数传进来；而 CMake 的字符串处理远不如 Python。
    Python 由 CMake POST_BUILD 调用是本仓库既有形状（stage_plugin.py 就是），
    SPEC §9 已认可，不是新先例。

依赖检查为什么是允许列表：
    禁止列表要求预先列举所有敌人 —— 现有写法只认识 OpenSSL 与 MbedTLS，换成
    GnuTLS、wolfSSL 或多出一个 libz 全都放过。CONTRIBUTING 明文批过这个形状。
    它防的不是「上游依赖了什么」，而是「构建机上恰好装了什么」：libdatachannel
    的 find_package 可能悄悄找到系统/brew 的动态 crypto，构建全绿而产物不再自
    包含。依赖表是「静态链接是否生效」的唯一可观测证据。本仓库已因此坏过一次
    （SPEC §10：CI 曾有 brew install openssl@3 + OPENSSL_ROOT，那个 job 事实上
    是坏的）。而 #51 决定不用 Docker、放弃钉死构建宿主，对输出做绝对检查的价值
    随之上升 —— 两个决策是配套的。

三种产物的依赖信息形态并不相同，这里不强行统一：
    Mach-O 的 LC_LOAD_DYLIB 存**完整路径**，所以用路径前缀规则，上游合法新增一个
    /usr/lib 下的系统库不需要改任何东西。
    ELF 的 DT_NEEDED 与 PE 的导入表只存**名字**（libc.so.6 / KERNEL32.dll），
    拿不到路径，只能退回名字允许列表。
    （#50 的结论里写的「Linux 同形用路径前缀」不成立，此处按事实实现。）

静态库（iOS .a）不走本脚本：它没有依赖表，唯一代理是未定义符号集，会随编译器与
优化级别漂移 —— 决议 #50 明确不建这条门禁，缺口写进 SPEC 而不是假装有。
"""

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path

# 即便落在系统路径下也一律拒绝。macOS 的 /usr/lib/libssl.dylib 是真实存在的
# （系统自带的老版本），只靠路径前缀会把它放过去。
CRYPTO_DENY = re.compile(
    r"(libssl|libcrypto|libmbedtls|libmbedcrypto|libmbedx509|libgnutls|libwolfssl|openssl)",
    re.IGNORECASE,
)

# macOS：依赖是完整路径，用前缀规则 —— 上游合法新增系统库时无需改动此表。
MACOS_ALLOWED_PREFIXES = (
    "/System/Library/Frameworks/",
    "/usr/lib/",
    "@loader_path",
    "@rpath/datachannel_unity",
)

# Linux：DT_NEEDED 只有 soname。glibc 2.34 起 pthread/dl/rt 已并入 libc。
LINUX_ALLOWED_NAMES = {
    "libc.so.6", "libm.so.6", "libdl.so.2", "librt.so.1",
    "libpthread.so.0", "libstdc++.so.6", "libgcc_s.so.1",
    # 动态加载器自身。它会出现在 DT_NEEDED 里，但不是「依赖了某个第三方库」的
    # 那种依赖 —— 没有它任何动态链接的产物都跑不起来。首次 Linux CI 实跑时被
    # 这条门禁拦下才发现漏了；按架构各有一个名字。
    "ld-linux-x86-64.so.2", "ld-linux-aarch64.so.1",
}

# Windows：PE 导入表只有 DLL 名。
WINDOWS_ALLOWED_NAMES = {
    "kernel32.dll", "advapi32.dll", "ws2_32.dll", "iphlpapi.dll",
    "bcrypt.dll", "crypt32.dll", "user32.dll", "ole32.dll",
    "msvcrt.dll", "ucrtbase.dll", "vcruntime140.dll", "msvcp140.dll",
    "api-ms-win-crt-runtime-l1-1-0.dll",
}


class AuditError(Exception):
    pass


def run(cmd: list[str]) -> str:
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        raise AuditError(
            f"命令失败（rc={proc.returncode}）: {' '.join(cmd)}\n{proc.stderr.strip()}")
    return proc.stdout


def resolve_tool(explicit: str | None, fallback: str, why: str) -> str:
    """解析工具路径。找不到就硬失败 —— 绝不因为工具缺失而跳过检查。"""
    # CMake 对找不到的工具会把变量展开成 "<VAR>-NOTFOUND" 字面量（例如 macOS 上
    # 的 CMAKE_READELF）。当作「没传」处理，而不是当成一个叫这个名字的路径。
    if explicit and explicit.endswith("-NOTFOUND"):
        explicit = None
    if explicit:
        if Path(explicit).is_file() or shutil.which(explicit):
            return explicit
        raise AuditError(f"传入的 {why} 路径不存在: {explicit}")
    found = shutil.which(fallback)
    if found:
        return found
    raise AuditError(
        f"找不到 {fallback}（{why}）。\n"
        "  本门禁不会因为工具缺失而跳过检查 —— 那会让「没验过」和「验过且通过」\n"
        "  在报告里长得一模一样。请安装该工具，或由 CMake 显式传入其路径。")


# ---------- 导出符号 ----------

def macho_archs(binary: Path) -> list[str]:
    """产物里的架构。thin 返回一个，universal 返回多个。"""
    return run(["lipo", "-archs", str(binary)]).split()


def exports_macos(binary: Path, nm: str, arch: str) -> set[str]:
    # 必须显式指定 -arch。在 fat 二进制上 `nm -gU` 只报宿主架构 —— 实测一个
    # universal 产物只出 20 行（arm64 那半），x86_64 那半的导出集根本没被看过。
    # 那是个盲区，不是简洁。
    out = run([nm, "-arch", arch, "-gU", str(binary)])
    names = set()
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 3 and parts[1] in ("T", "S", "D", "B"):
            names.add(parts[2].lstrip("_"))
    return names


def exports_linux(binary: Path, nm: str) -> set[str]:
    out = run([nm, "-D", "--defined-only", str(binary)])
    names = set()
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 3 and parts[1] in ("T", "W", "D", "B", "R"):
            names.add(parts[2])
    return names


def exports_windows(binary: Path, dumpbin: str) -> set[str]:
    out = run([dumpbin, "/exports", str(binary)])
    names = set()
    started = False
    for line in out.splitlines():
        stripped = line.strip()
        if not started:
            # 表头形如 "ordinal hint RVA      name"
            if stripped.startswith("ordinal") and "name" in stripped:
                started = True
            continue
        if not stripped or stripped.startswith("Summary"):
            continue
        parts = stripped.split()
        # "   1    0 00001000 dcu_abi_version"
        if len(parts) >= 4 and parts[0].isdigit():
            names.add(parts[3])
    return names


# ---------- 依赖 ----------

def deps_macos(binary: Path, otool: str, arch: str) -> list[str]:
    # 两个必须显式处理的形态问题：
    #
    # 1. `otool -L` 的输出里，表头之后的**第一条是产物自己的 install name**
    #    （LC_ID_DYLIB），不是依赖。把它当依赖检查会产生假阳性 —— 本仓库的产物
    #    恰好因为 install name 是 @loader_path/... 而落在允许列表内，掩盖了它。
    #    用 `otool -D` 单独取出并剔除。
    # 2. 在 fat 二进制上，`otool -L` 会为**每个架构**打印一段，段首形如
    #    `path (architecture x86_64):`。不按架构取就会把那行标题当成依赖。
    #    所以这里逐架构调用，`-arch` 之后输出只剩一段。
    install_name = ""
    id_out = run([otool, "-arch", arch, "-D", str(binary)]).splitlines()
    if len(id_out) >= 2:
        install_name = id_out[1].strip()

    deps = []
    for line in run([otool, "-arch", arch, "-L", str(binary)]).splitlines()[1:]:
        stripped = line.strip()
        if not stripped or stripped.endswith(":"):
            continue
        dep = stripped.split(" (compatibility")[0].strip()
        if dep == install_name:
            continue
        deps.append(dep)
    return deps


def deps_linux(binary: Path, readelf: str) -> list[str]:
    out = run([readelf, "-d", str(binary)])
    return re.findall(r"\(NEEDED\)\s+Shared library:\s+\[([^\]]+)\]", out)


def deps_windows(binary: Path, dumpbin: str) -> list[str]:
    out = run([dumpbin, "/dependents", str(binary)])
    deps = []
    started = False
    for line in out.splitlines():
        stripped = line.strip()
        if "Image has the following dependencies" in stripped:
            started = True
            continue
        if not started:
            continue
        if stripped.startswith("Summary"):
            break
        if stripped.lower().endswith(".dll"):
            deps.append(stripped)
    return deps


# ---------- 断言 ----------

def check_deps(platform: str, deps: list[str]) -> None:
    bad_crypto = [d for d in deps if CRYPTO_DENY.search(d)]
    if bad_crypto:
        raise AuditError(
            "产物依赖了 crypto 动态库，说明静态链接**没有生效**：\n"
            + "".join(f"    {d}\n" for d in bad_crypto)
            + "  典型成因：构建机上装了 OpenSSL，libdatachannel 的 find_package 找到了它。\n"
              "  产品路径是 vendored 静态 MbedTLS（SPEC §3/§9），brew/系统 crypto 是禁止的。")

    if platform == "darwin":
        unexpected = [d for d in deps
                      if not any(d.startswith(p) for p in MACOS_ALLOWED_PREFIXES)]
        allowed_desc = "、".join(MACOS_ALLOWED_PREFIXES)
    elif platform == "linux":
        unexpected = [d for d in deps if d not in LINUX_ALLOWED_NAMES]
        allowed_desc = "、".join(sorted(LINUX_ALLOWED_NAMES))
    else:
        unexpected = [d for d in deps if d.lower() not in WINDOWS_ALLOWED_NAMES]
        allowed_desc = "、".join(sorted(WINDOWS_ALLOWED_NAMES))

    if unexpected:
        raise AuditError(
            "产物依赖了允许列表之外的库：\n"
            + "".join(f"    {d}\n" for d in unexpected)
            + f"  允许的是：{allowed_desc}\n"
              "  这里刻意用允许列表而非禁止列表：禁令只拦得住已经遇到过的形状。\n"
              "  若这是**上游合法新增的系统依赖**，把它加进本脚本的允许列表并说明理由；\n"
              "  若不是，它多半意味着某个依赖没被静态链接进来。")


def check_exports(actual: set[str], expected_file: Path) -> int:
    if not expected_file.is_file():
        raise AuditError(
            f"缺少导出清单 {expected_file}\n"
            "  它是门禁的一部分，不是可选文件；不要删掉它让 audit 变绿。")

    expected = set()
    for raw in expected_file.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].strip()
        if line:
            expected.add(line)

    non_dcu = sorted(n for n in actual if not n.startswith("dcu_"))
    if non_dcu:
        raise AuditError(
            "导出了非 dcu_* 符号（允许列表漏了东西）：\n"
            + "".join(f"    {n}\n" for n in non_dcu[:50]))

    missing = sorted(expected - actual)
    extra = sorted(actual - expected)
    if missing or extra:
        lines = [f"导出集与 {expected_file.name} 不一致："]
        lines += [f"  缺少（清单里有、插件没导出）: {n}" for n in missing]
        lines += [f"  多出（插件导出、清单里没有）: {n}" for n in extra]
        lines += [
            "",
            "  注意：链接期白名单现在也由这份清单生成（gen_exports.py），所以",
            "  「有人从清单里删了一个名字」不会在这里被发现 —— 那一类由",
            "  gen_exports.py 的 DCU_API 交叉校验在配置期拦下。这里拦的是",
            "  「清单里有、二进制里却没有」（实现缺失或改名），以及产物不是本次构建的。",
        ]
        raise AuditError("\n".join(lines))

    return len(actual)


def main() -> int:
    ap = argparse.ArgumentParser(description="原生插件离线门禁")
    ap.add_argument("--binary", required=True, type=Path)
    ap.add_argument("--platform", required=True, choices=["darwin", "linux", "windows"])
    ap.add_argument("--expected", required=True, type=Path)
    ap.add_argument("--nm", default=None, help="CMAKE_NM")
    ap.add_argument("--readelf", default=None, help="CMAKE_READELF")
    ap.add_argument("--linker", default=None,
                    help="CMAKE_LINKER（Windows 下用来推导同目录的 dumpbin.exe）")
    args = ap.parse_args()

    binary = args.binary
    # macOS 传进来的可能是 .bundle 目录
    if binary.is_dir():
        inner = binary / "Contents" / "MacOS" / "datachannel_unity"
        if inner.is_file():
            binary = inner
    if not binary.is_file():
        raise AuditError(f"产物不存在: {args.binary}")

    if args.platform == "darwin":
        nm = resolve_tool(args.nm, "nm", "读取导出符号")
        otool = resolve_tool(None, "otool", "读取动态依赖")
        # universal 产物的**每个架构都要单独过一遍**，两条断言都是。只查宿主架构
        # 等于让另外半个二进制没有门禁 —— 而它照样会随包发给采用者。
        archs = macho_archs(binary)
        print(f"==> 架构: {' '.join(archs)}")
        actual, deps = None, []
        for arch in archs:
            arch_exports = exports_macos(binary, nm, arch)
            arch_deps = deps_macos(binary, otool, arch)
            print(f"==> 依赖 (darwin/{arch})")
            for d in arch_deps:
                print(f"    {d}")
            check_deps("darwin", arch_deps)
            count = check_exports(arch_exports, args.expected)
            print(f"    {arch}: {count} 个 dcu_* 导出与清单一致")
            if actual is not None and arch_exports != actual:
                raise AuditError(
                    "universal 产物的各架构导出集不一致 —— 说明某一半没按预期链接：\n"
                    f"    只在 {archs[0]}: {sorted(actual - arch_exports)}\n"
                    f"    只在 {arch}: {sorted(arch_exports - actual)}")
            actual, deps = arch_exports, arch_deps
        print(f"OK: {len(archs)} 个架构各自的导出与依赖均通过")
        return 0
    elif args.platform == "linux":
        nm = resolve_tool(args.nm, "nm", "读取导出符号")
        readelf = resolve_tool(args.readelf, "readelf", "读取 DT_NEEDED")
        actual, deps = exports_linux(binary, nm), deps_linux(binary, readelf)
    else:
        # CMake 的 MSVC 分支不提供 NM，dumpbin 也完全没建模；但 dumpbin.exe 就在
        # link.exe 同目录（#48：它在镜像里，只是不在 PATH）。
        dumpbin = None
        if args.linker:
            candidate = Path(args.linker).parent / "dumpbin.exe"
            if candidate.is_file():
                dumpbin = str(candidate)
        dumpbin = resolve_tool(dumpbin, "dumpbin", "读取导出表与依赖")
        actual, deps = exports_windows(binary, dumpbin), deps_windows(binary, dumpbin)

    print(f"==> 依赖 ({args.platform})")
    for d in deps:
        print(f"    {d}")
    check_deps(args.platform, deps)

    print("==> 导出符号")
    count = check_exports(actual, args.expected)

    print(f"OK: {count} 个 dcu_* 导出与 {args.expected.name} 一致，依赖均在允许列表内")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except AuditError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
