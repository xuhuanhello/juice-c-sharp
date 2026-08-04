#!/usr/bin/env python3
"""生成 UPM 插件的 PluginImporter `.meta`（决议 #52）。

**产生与校验是同一个机制**：本脚本生成 `.meta`，CI 重跑它并 diff，不一致就红。
不需要 Unity，所以能进 CI —— 而 CI 里没有 Unity（#43，不可逆转）。

为什么纯文本校验够用：#49 实测发现，纯文本能查的那一类，恰好就是 Unity 完全
**不查**、写错必然**静默失效**的那一类：

- 平台键名拼错 == 没写这条。无报错、无重写，错字原样留在磁盘上。
- Windows `.dll` 开 `Any Platform`，在 macOS 上是纯静默：构建期最终路径为空串
  不拷贝，Editor 期只得到 `DllNotFoundException`，Console 零条日志 —— 与
  「文件压根不存在」不可区分。

平台开关的取值不是从文档抄的，是 2022.3.62f3 让 Editor 通过 PluginImporter API
配置后**自己写到磁盘上的字节**（#49 §3）。黄金样本入库在 native/exports/
plugin-meta-golden/，本脚本的输出必须与之匹配 —— 否则生成器写错时，生成物与
仓库永远一致、门禁永远绿，机制会退化成「不可能失败的检查」。

GUID 的硬约束：一旦入库就不能变（变了等于换了一个资产）。所以本脚本**从现有
`.meta` 读回 GUID 并保留**，不重新生成整个文件。新平台第一次落地时用
`--allow-new-guid` 铸一个，之后永远保留。默认模式下缺 `.meta` 即硬失败 ——
否则 CI 每次跑都会铸出不同的 GUID，diff 永远红。
"""

from __future__ import annotations

import argparse
import re
import sys
import uuid
from pathlib import Path

GUID_RE = re.compile(r"^guid:\s*([0-9a-f]{32})\s*$", re.MULTILINE)

# 与黄金样本比对时要归一化掉的两行，各有明确理由：
#
# guid —— 黄金样本里是探针资产的 GUID，本来就该不同。
#
# isOverridable —— Unity 给新导入的插件写 `0`，本包**有意写 `1`**：
#   DefaultPluginImporterExtension.CheckFileCollisions 在采用者于自己的 Assets/
#   放了同名插件时，会跳过「可覆盖」的那一份而不是报冲突。这对一个要发布的 UPM
#   包是正确的。PluginImporter 只有 GetIsOverridable() 没有 setter，所以这一位
#   **只能靠手写 .meta 得到**，真 Editor 永远产不出它 —— 归一化是唯一的选择，
#   而不是把它当成偏差。（Unity 会忠实读回：实测 GetIsOverridable()=True。）
NORMALIZE = (
    (GUID_RE, "guid: <normalized>"),
    (re.compile(r"^  isOverridable:\s*[01]\s*$", re.MULTILINE), "  isOverridable: <normalized>"),
)


def normalize(text: str) -> str:
    for pattern, replacement in NORMALIZE:
        text = pattern.sub(replacement, text)
    return text

# 每个平台一条：产物相对 Plugins/ 的路径 → platformData 条目。
#
# 条目顺序与 settings 键序都按 ASCII 排序 —— Unity 写出来就是这个顺序
# （大写字母排在小写前，所以 iPhone 落在 Standalone 之后）。
#
# 范围是 map #46 定的**五个**平台（原为六个，macOS 两个架构合并为一份 universal）。
# Windows ARM64 已出图（2022.3 的 Standalone Windows 没有 ARM64 槽位，编辑器
# 源码显式返回空路径）；WebGL 也出图。
PLATFORMS: dict[str, dict] = {
    "Windows/x86_64/datachannel_unity.dll": {
        "Any": {"enabled": 0, "settings": {}},
        "Editor": {"enabled": 1, "settings": {
            "CPU": "x86_64", "DefaultValueInitialized": "true", "OS": "Windows"}},
        "Standalone": {"target": "Win64", "enabled": 1, "settings": {"CPU": "x86_64"}},
    },
    # macOS 是**单一 universal bundle**，没有架构子目录（见 native/CMakeLists.txt
    # 开头的理由）。因此 CPU 是 AnyCPU —— 而「两份同名 bundle 必须靠 CPU 字段
    # 区分」正是 #49 查出的 CheckFileCollisions 冲突与 reimport 刷 error 的根源，
    # 这里从根上不存在。
    "macOS/datachannel_unity.bundle": {
        "Any": {"enabled": 0, "settings": {}},
        "Editor": {"enabled": 1, "settings": {
            "CPU": "AnyCPU", "DefaultValueInitialized": "true", "OS": "OSX"}},
        "Standalone": {"target": "OSXUniversal", "enabled": 1, "settings": {"CPU": "AnyCPU"}},
    },
    # `.so` 对 Android 也是候选，Unity 导入时会写一条默认关闭的 Android 条目。
    # 手写时省略它行为一致，但目标是与 Unity 产出逐字节一致，所以必须在。
    "Linux/x86_64/libdatachannel_unity.so": {
        "Android": {"enabled": 0, "settings": {"Is16KbAligned": "false"}},
        "Any": {"enabled": 0, "settings": {}},
        "Editor": {"enabled": 1, "settings": {
            "CPU": "x86_64", "DefaultValueInitialized": "true", "OS": "Linux"}},
        "Standalone": {"target": "Linux64", "enabled": 1, "settings": {"CPU": "x86_64"}},
    },
    "Android/arm64-v8a/libdatachannel_unity.so": {
        "Android": {"enabled": 1, "settings": {"CPU": "ARM64", "Is16KbAligned": "false"}},
        "Any": {"enabled": 0, "settings": {}},
        "Editor": {"enabled": 0, "settings": {"DefaultValueInitialized": "true"}},
    },
    # iPhone 段是 `settings: {}` —— 真 Editor 对一个只设了 SetCompatibleWithPlatform
    # 的静态 .a 就写这个。（#49 文档 §3.7 列的那四个键 AddToEmbeddedBinaries /
    # CPU / CompileFlags / FrameworkDependencies 是探针显式设过之后才写出来的；
    # 缺省即 false/AnyCPU/空，语义相同。以现场实测为准。）
    # 将来若 .a 需要链接系统框架，就是往 FrameworkDependencies 填。
    "iOS/libdatachannel_unity.a": {
        "Any": {"enabled": 0, "settings": {}},
        "Editor": {"enabled": 0, "settings": {"DefaultValueInitialized": "true"}},
        "iPhone": {"target": "iOS", "enabled": 1, "settings": {}},
    },
}


class MetaError(Exception):
    pass


def render(guid: str, platform_data: dict) -> str:
    """按 Unity 的字节形状渲染。注意多处**尾随空格**，Unity 写出来就是那样。"""
    lines = [
        "fileFormatVersion: 2",
        f"guid: {guid}",
        "PluginImporter:",
        "  externalObjects: {}",
        "  serializedVersion: 2",
        "  iconMap: {}",
        "  executionOrder: {}",
        "  defineConstraints: []",
        "  isPreloaded: 0",
        # isOverridable: 1 是有意的，且只能靠手写 .meta 得到（PluginImporter 只有
        # GetIsOverridable 没有 setter）。它让采用者在自己的 Assets/ 放同名插件时
        # 覆盖本包这份，而不是构建报冲突。
        "  isOverridable: 1",
        "  isExplicitlyReferenced: 0",
        "  validateReferences: 1",
        "  platformData:",
    ]

    for group in sorted(platform_data):
        entry = platform_data[group]
        target = entry.get("target", "" if group == "Any" else group)
        lines += [
            "  - first:",
            f"      {group}: {target}".rstrip() + (" " if not target else ""),
            "    second:",
            f"      enabled: {entry['enabled']}",
        ]
        settings = entry["settings"]
        if not settings:
            lines.append("      settings: {}")
        else:
            lines.append("      settings:")
            for key in sorted(settings):
                value = settings[key]
                lines.append(f"        {key}: {value}".rstrip() + ("" if value else " "))

    lines += ["  userData: ", "  assetBundleName: ", "  assetBundleVariant: "]
    return "\n".join(lines) + "\n"


def read_guid(meta_path: Path, allow_new: bool) -> str:
    if meta_path.is_file():
        match = GUID_RE.search(meta_path.read_text(encoding="utf-8"))
        if not match:
            raise MetaError(
                f"{meta_path} 里没有找到合法的 guid 行。\n"
                "  GUID 一旦入库就不能变（变了等于换了一个资产），所以这里不会\n"
                "  替你铸一个新的 —— 请先弄清这份 .meta 为什么坏了。")
        return match.group(1)

    if not allow_new:
        raise MetaError(
            f"{meta_path} 不存在。\n"
            "  新平台第一次落地时用 --allow-new-guid 铸一个 GUID，之后永远保留。\n"
            "  默认模式刻意不铸：否则 CI 每次跑都会得到不同的 GUID，diff 永远红，\n"
            "  这条门禁会立刻退化成噪音而被人关掉。")
    return uuid.uuid4().hex


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--plugin-root", required=True, type=Path,
                    help="Packages/datachannel-unity/Plugins")
    ap.add_argument("--golden", type=Path, default=None,
                    help="黄金样本目录（Editor 实写的字节）。给了就一并核对")
    ap.add_argument("--check", action="store_true",
                    help="只核对不写盘；有差异则非零退出（CI 用）")
    ap.add_argument("--allow-new-guid", action="store_true",
                    help="允许为尚不存在的 .meta 铸新 GUID（新平台第一次落地时用）")
    args = ap.parse_args()

    drift: list[str] = []
    absent: list[str] = []
    for rel, platform_data in PLATFORMS.items():
        asset_path = args.plugin_root / rel
        meta_path = args.plugin_root / (rel + ".meta")

        # `.meta` 必须当且仅当产物存在时存在。给不存在的资产写 .meta 会产生孤儿，
        # Unity 下一次刷新就把它删掉 —— 那样这条门禁每次都红，而红的原因是我们
        # 自己造的垃圾。这也正是 #53「权威源 = Plugins/ 目录内容」的直接后果：
        # 平台验过才入库二进制，二进制在才有 .meta，清单不可能撒谎。
        if not asset_path.exists():
            absent.append(rel)
            if meta_path.is_file():
                drift.append(f"  产物不存在却有 .meta（孤儿，Unity 刷新会删掉它）: {rel}")
            continue

        content = render(read_guid(meta_path, args.allow_new_guid), platform_data)

        if args.golden:
            golden = args.golden / (rel.replace("/", "__") + ".meta")
            if not golden.is_file():
                raise MetaError(
                    f"缺少黄金样本 {golden}\n"
                    "  它是本门禁真正的支点：没有它，生成器写错时生成物与仓库永远\n"
                    "  一致、门禁永远绿。缺失即硬失败，不降级为「只比对仓库」。")
            expected = golden.read_text(encoding="utf-8")
            if normalize(expected) != normalize(content):
                drift.append(f"  生成结果与黄金样本不符: {rel}\n"
                             f"    黄金样本: {golden}")
                continue

        current = meta_path.read_text(encoding="utf-8") if meta_path.is_file() else None
        if current == content:
            continue

        if args.check:
            drift.append(f"  仓库里的 .meta 与生成结果不符: {rel}")
        else:
            meta_path.parent.mkdir(parents=True, exist_ok=True)
            meta_path.write_text(content, encoding="utf-8")
            print(f"  写入 {meta_path}")

    if drift:
        raise MetaError(
            ".meta 校验失败：\n" + "\n".join(drift) + "\n\n"
            "  修法：跑 `python3 native/scripts/gen_plugin_meta.py --plugin-root <…>`\n"
            "  重新生成，并把 diff 一起提交。若差异来自平台开关的**有意变更**，\n"
            "  必须先更新黄金样本 —— 而黄金样本只能由真 Editor 产出，不能手写。")

    done = len(PLATFORMS) - len(absent)
    print(f"OK: {done}/{len(PLATFORMS)} 个平台的 .meta 与生成结果一致"
          + ("，且与黄金样本一致" if args.golden else ""))
    if absent:
        # 明说跳过了什么。一个「6 个里过了 1 个」的绿勾若不写清楚，读起来
        # 会像「6 个都过了」—— 那正是把覆盖面的缺口变成沉默。
        print("尚未入库、因此跳过的平台（产物不存在，属预期 —— 分批入库，见 #53）：")
        for rel in absent:
            print(f"    {rel}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except MetaError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
