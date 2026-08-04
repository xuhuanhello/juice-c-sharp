#!/usr/bin/env python3
"""从唯一权威清单生成各平台的链接期导出白名单（map #46 决议 #50）。

权威源是 native/exports/expected-symbols.txt —— 仓库里**唯一**手写的符号清单。
本脚本把它翻译成三种链接器各自的格式，产物落在构建目录，不入库。

为什么三份都生成，而不只生成 Windows 的那份：
    macOS 与 Linux 原本用的是通配符（`_dcu_*` / `dcu_*`），不会过期，所以
    「防止失同步」不是理由。真正的理由是**断言强度**：通配符让链接器执行的是
    一个模式，而 audit 执行的是精确的 20 条 —— 两个不同强度的断言。三份都从
    权威源生成之后，一个手滑写出的 dcu_foo 会在**链接期**就红，而不是等到
    audit 事后比对。更早的失败点。

    Windows 那份则是**必须**生成：MSVC 的 .def 不支持通配，只能逐条列举，
    而逐条列举的东西一定会烂 —— 入库的那份确实烂了（带着 S2 删除的四个符号、
    缺六个现存符号），是本次改动要消灭的东西。

为什么必须拿 dcu.h 的 DCU_API 标注来交叉校验：
    三份白名单从清单生成之后，实际导出集就是清单的函数 —— audit 的第 3 条
    「导出集必须与清单逐符号一致」于是变成**恒真**：从清单里删掉一个名字，
    链接器少导出一个，两边照样相等。那是一个不可能失败的门禁，正是
    CONTRIBUTING 第一条点名的病。

    唯一独立于清单的真相源是**源码里的 DCU_API 标注**。本脚本断言两者集合
    相等，任一侧漏改都在**配置期**硬失败 —— 比链接期和 audit 都早。

    这同时满足 #47 复验得出的硬约束：`ld -r -exported_symbols_list` 只能降级
    不能提升，白名单里出现任何没打 DCU_API 的名字，iOS 链接会直接失败。
    在配置期就拦住，比让链接器在 iOS 那一步才报要好。
"""

from __future__ import annotations

import argparse
import re
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


SYMBOL_RE = re.compile(r"^dcu_[A-Za-z0-9_]+$")

# `DCU_API int dcu_pc_create(const dcu_pc_config *config, int *out_pc);`
DCU_API_RE = re.compile(r"^\s*DCU_API\s+[A-Za-z_][A-Za-z0-9_ *]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*\(")

GENERATED_NOTE = (
    "本文件由 native/scripts/gen_exports.py 生成，请勿手改。\n"
    "权威源：native/exports/expected-symbols.txt"
)


def parse_expected(path: Path) -> list[str]:
    """读权威清单。允许 # 注释与空行；其余每行一个符号名。"""
    if not path.is_file():
        die(f"缺少权威符号清单 {path}\n"
            "       它是门禁的一部分，不是可选文件。")

    names: list[str] = []
    seen: dict[str, int] = {}
    for lineno, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        if not SYMBOL_RE.match(line):
            die(f"{path}:{lineno}: 不是合法的符号名: {line!r}\n"
                "       每行只能有一个 dcu_ 开头的符号名（不带平台前缀下划线）。")
        if line in seen:
            die(f"{path}:{lineno}: 符号 {line} 与第 {seen[line]} 行重复。")
        seen[line] = lineno
        names.append(line)

    if not names:
        die(f"{path} 里一个符号都没有。\n"
            "       空清单会让链接器隐藏掉全部导出，插件将无法被 P/Invoke 加载 ——\n"
            "       这正是「缺席变成沉默」，所以此处硬失败而不是生成一份空白名单。")

    return sorted(names)


def parse_dcu_api(path: Path) -> list[str]:
    """从 dcu.h 抽出所有打了 DCU_API 的函数名 —— 唯一独立于清单的真相源。"""
    if not path.is_file():
        die(f"缺少公共头文件 {path}（交叉校验的真相源，不是可选文件）。")

    names = [m.group(1) for m in
             (DCU_API_RE.match(line) for line in path.read_text(encoding="utf-8").splitlines())
             if m]
    if not names:
        die(f"{path} 里一条 DCU_API 声明都没抽到。\n"
            "       这几乎肯定是本脚本的正则与头文件写法脱节了，而不是真的没有导出 ——\n"
            "       所以此处硬失败，不静默按「零个导出」继续。")
    return sorted(names)


def cross_check(expected: list[str], annotated: list[str],
                expected_path: Path, header_path: Path) -> None:
    """清单与 DCU_API 标注必须集合相等，否则配置期硬失败。"""
    missing = sorted(set(annotated) - set(expected))   # 标了 DCU_API，清单里没有
    extra = sorted(set(expected) - set(annotated))     # 清单里有，却没标 DCU_API

    if not missing and not extra:
        return

    lines = [f"导出清单与 {header_path.name} 的 DCU_API 标注不一致："]
    for n in missing:
        lines.append(f"  {header_path.name} 标了 DCU_API，但 {expected_path.name} 里没有: {n}")
    for n in extra:
        lines.append(f"  {expected_path.name} 里有，但 {header_path.name} 没标 DCU_API: {n}")
    lines += [
        "",
        "  两者必须集合相等。理由：白名单从清单生成之后，audit 的「导出集与清单一致」",
        "  变成恒真（删一个名字，链接器就少导出一个，两边照样相等）。DCU_API 标注是",
        "  唯一独立于清单的真相源，这条交叉校验是该门禁真正的支点。",
        "",
        "  另：iOS 的 `ld -r -exported_symbols_list` 只能降级不能提升 —— 上面「没标",
        "  DCU_API」那一类若放过去，会在 iOS 链接时才炸。",
        "",
        "  若这是**有意的 ABI 变更**：两边一起改，并在同一个 commit 里 bump",
        "  dcu.h 的 DCU_ABI_VERSION（SPEC §11）。",
    ]
    die("\n".join(lines))


def comment_block(prefix: str) -> str:
    return "".join(f"{prefix} {line}\n" for line in GENERATED_NOTE.splitlines())


def render_macho(names: list[str]) -> str:
    # Apple 平台的 C 符号带前导下划线。ld64 支持通配，但这里逐条列举 —— 见模块注释。
    return comment_block("#") + "".join(f"_{n}\n" for n in names)


def render_version_script(names: list[str]) -> str:
    # GNU ld 的 version script 只认 C 风格注释，不认 #。
    header = "/*\n" + "".join(f" * {line}\n" for line in GENERATED_NOTE.splitlines()) + " */\n"
    body = "".join(f"    {n};\n" for n in names)
    return header + "{\n  global:\n" + body + "  local:\n    *;\n};\n"


def render_def(names: list[str]) -> str:
    return (
        comment_block(";")
        + "LIBRARY datachannel_unity\nEXPORTS\n"
        + "".join(f"    {n}\n" for n in names)
    )


def die(msg: str) -> "None":
    print(f"ERROR: {msg}", file=sys.stderr)
    raise SystemExit(1)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--expected", required=True, type=Path,
                    help="权威清单 native/exports/expected-symbols.txt")
    ap.add_argument("--header", required=True, type=Path,
                    help="公共头 native/dcu/include/dcu.h，用于 DCU_API 交叉校验")
    ap.add_argument("--out-dir", required=True, type=Path,
                    help="生成物输出目录（构建目录内，不入库）")
    args = ap.parse_args()

    names = parse_expected(args.expected)
    cross_check(names, parse_dcu_api(args.header), args.expected, args.header)
    args.out_dir.mkdir(parents=True, exist_ok=True)

    outputs = {
        "macos-exported-symbols.txt": render_macho(names),
        "linux-version-script.map": render_version_script(names),
        "windows-exports.def": render_def(names),
    }
    for filename, content in outputs.items():
        (args.out_dir / filename).write_text(content, encoding="utf-8")

    print(f"gen_exports: {len(names)} 个符号 → {len(outputs)} 份白名单 @ {args.out_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
