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
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

def _force_utf8_output() -> None:
    """Windows 上非 TTY 的 stdout 默认是 cp1252，本脚本的中文输出会直接
    UnicodeEncodeError（首次 Windows CI 实跑时炸在这里）。在脚本自身修而不是
    靠 workflow 设 PYTHONIOENCODING —— 那样无论谁怎么调用它都成立。"""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8")


_force_utf8_output()


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
# bcrypt/crypt32 是 **Windows 自带的系统 crypto API**，不是我们捆绑的 crypto 库
# ——libjuice/libdatachannel 用它们取随机数与证书。它们不违反「crypto 必须静态
# 链接」：那条规则针对的是 OpenSSL/MbedTLS 那种被打包进产品的库。
WINDOWS_ALLOWED_NAMES = {
    "kernel32.dll", "advapi32.dll", "ws2_32.dll", "iphlpapi.dll",
    "bcrypt.dll", "crypt32.dll", "user32.dll", "ole32.dll",
    "msvcrt.dll", "ucrtbase.dll", "msvcp140.dll",
    # 首次 Windows CI 实跑才看到的：CRT 拆成了这几个
    "vcruntime140.dll", "vcruntime140_1.dll",
}

# UCRT 的 API set。它们是系统的一部分，且会随 CRT 版本增删（本次实测就出现了
# convert/filesystem/heap/math/runtime/stdio/string/time/utility 九个）。逐个
# 列举必然过期，用前缀规则 —— 与 macOS 用路径前缀同理：仍是允许列表，任何不以
# 此开头的 DLL 依然必须在上面的显式集合里。
WINDOWS_ALLOWED_PREFIXES = ("api-ms-win-",)


class AuditError(Exception):
    pass


def run(cmd: list[str]) -> str:
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        raise AuditError(
            f"Command failed (rc={proc.returncode}): {' '.join(cmd)}\n{proc.stderr.strip()}")
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
        raise AuditError(f"The {why} path passed in does not exist: {explicit}")
    found = shutil.which(fallback)
    if found:
        return found
    raise AuditError(
        f"Cannot find {fallback} ({why}).\n"
        "  This gate will not skip a check because a tool is missing: that would make\n"
        "  'never ran' and 'ran and passed' look identical in the report. Install the\n"
        "  tool, or have CMake pass its path explicitly.")


def find_dumpbin(linker: str | None) -> str:
    """定位 dumpbin.exe。三条路依次尝试，全失败才硬失败。

    CMake 的 MSVC 分支不提供 NM，dumpbin 更是完全没建模（CMakeFindBinUtils 只给
    LINKER/MT/AR），而 dumpbin 在 runner 镜像里**不在 PATH**（#48 查实）。

    1. `--linker` 的同目录 —— dumpbin.exe 与 link.exe 同在 VC/Tools/MSVC/*/bin/。
       CMake 从 POST_BUILD 调用时走这条。
    2. PATH —— 开发者在 Developer Command Prompt 里手跑时走这条。
    3. vswhere —— 上面两条都没有时（例如 CI 里从普通 bash 直接调本脚本，
       首次 Windows 实跑就栽在这儿）。vswhere.exe 的路径是 Microsoft 固定的，
       所有装了 VS 的机器都在同一处。

    第 3 条是本脚本能**独立使用**的关键：审计工具不该要求调用者先懂 MSVC 的
    目录结构。
    """
    if linker and not linker.endswith("-NOTFOUND"):
        candidate = Path(linker).parent / "dumpbin.exe"
        if candidate.is_file():
            return str(candidate)

    found = shutil.which("dumpbin")
    if found:
        return found

    vswhere = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) \
        / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
    if vswhere.is_file():
        try:
            root = run([str(vswhere), "-latest", "-products", "*",
                        "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                        "-property", "installationPath"]).strip()
        except AuditError:
            root = ""
        if root:
            hits = sorted(Path(root).glob("VC/Tools/MSVC/*/bin/Host*/*/dumpbin.exe"))
            if hits:
                return str(hits[-1])

    raise AuditError(
        "Cannot find dumpbin.exe (reads the export table and dependencies).\n"
        "  Tried in order: the --linker directory, PATH, and the VS install located via vswhere.\n"
        "  本门禁不会因为工具缺失而跳过检查 —— 那会让「没验过」和「验过且通过」\n"
        "  'never ran' and 'ran and passed' look identical in the report.")


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
            "The artifact depends on a dynamic crypto library, so static linking did NOT take effect:\n"
            + "".join(f"    {d}\n" for d in bad_crypto)
            + "  Typical cause: OpenSSL is installed on the build machine and libdatachannel's find_package picked it up.\n"
              "  The product path is vendored static MbedTLS (SPEC sections 3/9); brew or system crypto is forbidden.")

    if platform == "darwin":
        unexpected = [d for d in deps
                      if not any(d.startswith(p) for p in MACOS_ALLOWED_PREFIXES)]
        allowed_desc = "、".join(MACOS_ALLOWED_PREFIXES)
    elif platform == "linux":
        unexpected = [d for d in deps if d not in LINUX_ALLOWED_NAMES]
        allowed_desc = "、".join(sorted(LINUX_ALLOWED_NAMES))
    else:
        unexpected = [d for d in deps
                      if d.lower() not in WINDOWS_ALLOWED_NAMES
                      and not any(d.lower().startswith(p) for p in WINDOWS_ALLOWED_PREFIXES)]
        allowed_desc = ("、".join(sorted(WINDOWS_ALLOWED_NAMES))
                        + ", plus the prefixes " + "、".join(WINDOWS_ALLOWED_PREFIXES))

    if unexpected:
        raise AuditError(
            "The artifact depends on libraries outside the allowlist:\n"
            + "".join(f"    {d}\n" for d in unexpected)
            + f"  Allowed: {allowed_desc}\n"
              "  This is deliberately an allowlist, not a denylist: bans only stop shapes already encountered.\n"
              "  If this is a legitimate new system dependency from upstream, add it to the allowlist in this script with a reason;\n"
              "  if it is not, it most likely means some dependency was not linked statically.")


def check_exports(actual: set[str], expected_file: Path) -> int:
    if not expected_file.is_file():
        raise AuditError(
            f"Missing the export list {expected_file}\n"
            "  It is part of the gate, not an optional file. Do not delete it to make the audit pass.")

    expected = set()
    for raw in expected_file.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].strip()
        if line:
            expected.add(line)

    non_dcu = sorted(n for n in actual if not n.startswith("dcu_"))
    if non_dcu:
        raise AuditError(
            "Non-dcu_* symbols are exported (the allowlist let something through):\n"
            + "".join(f"    {n}\n" for n in non_dcu[:50]))

    missing = sorted(expected - actual)
    extra = sorted(actual - expected)
    if missing or extra:
        lines = [f"The export set does not match {expected_file.name}:"]
        lines += [f"  missing (in the list, not exported by the plugin): {n}" for n in missing]
        lines += [f"  extra (exported by the plugin, not in the list): {n}" for n in extra]
        lines += [
            "",
            "  Note: the link-time allowlist is now generated from this same list (gen_exports.py), so ",
            "  'someone deleted a name from the list' will NOT be caught here. That case is caught by ",
            "  the DCU_API cross-check in gen_exports.py, at configure time. What this catches is ",
            "  'in the list but not in the binary' (missing or renamed implementation), and an artifact that is not from this build.",
        ]
        raise AuditError("\n".join(lines))

    return len(actual)


def main() -> int:
    ap = argparse.ArgumentParser(description="Offline gate for the native plugin")
    ap.add_argument("--binary", required=True, type=Path)
    ap.add_argument("--platform", required=True, choices=["darwin", "linux", "windows"])
    ap.add_argument("--expected", required=True, type=Path)
    ap.add_argument("--nm", default=None, help="CMAKE_NM")
    ap.add_argument("--readelf", default=None, help="CMAKE_READELF")
    ap.add_argument("--linker", default=None,
                    help="CMAKE_LINKER (used on Windows to locate dumpbin.exe in the same directory)")
    args = ap.parse_args()

    binary = args.binary
    # macOS 传进来的可能是 .bundle 目录
    if binary.is_dir():
        inner = binary / "Contents" / "MacOS" / "datachannel_unity"
        if inner.is_file():
            binary = inner
    if not binary.is_file():
        raise AuditError(f"Artifact does not exist: {args.binary}")

    if args.platform == "darwin":
        nm = resolve_tool(args.nm, "nm", "reads exported symbols")
        otool = resolve_tool(None, "otool", "reads dynamic dependencies")
        # universal 产物的**每个架构都要单独过一遍**，两条断言都是。只查宿主架构
        # 等于让另外半个二进制没有门禁 —— 而它照样会随包发给采用者。
        archs = macho_archs(binary)
        print(f"==> architectures: {' '.join(archs)}")
        actual, deps = None, []
        for arch in archs:
            arch_exports = exports_macos(binary, nm, arch)
            arch_deps = deps_macos(binary, otool, arch)
            print(f"==> dependencies (darwin/{arch})")
            for d in arch_deps:
                print(f"    {d}")
            check_deps("darwin", arch_deps)
            count = check_exports(arch_exports, args.expected)
            print(f"    {arch}: {count} dcu_* exports match the list")
            if actual is not None and arch_exports != actual:
                raise AuditError(
                    "The universal artifact has different export sets per architecture, so one half did not link as expected:\n"
                    f"    only in {archs[0]}: {sorted(actual - arch_exports)}\n"
                    f"    only in {arch}: {sorted(arch_exports - actual)}")
            actual, deps = arch_exports, arch_deps
        print(f"OK: exports and dependencies passed for all {len(archs)} architecture(s)")
        return 0
    elif args.platform == "linux":
        nm = resolve_tool(args.nm, "nm", "读取导出符号")
        readelf = resolve_tool(args.readelf, "readelf", "reads DT_NEEDED")
        actual, deps = exports_linux(binary, nm), deps_linux(binary, readelf)
    else:
        dumpbin = find_dumpbin(args.linker)
        actual, deps = exports_windows(binary, dumpbin), deps_windows(binary, dumpbin)

    print(f"==> dependencies ({args.platform})")
    for d in deps:
        print(f"    {d}")
    check_deps(args.platform, deps)

    print("==> exported symbols")
    count = check_exports(actual, args.expected)

    print(f"OK: {count} dcu_* exports match {args.expected.name}; all dependencies are within the allowlist")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except AuditError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
