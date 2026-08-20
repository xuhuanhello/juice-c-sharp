# Android arm64-v8a。
#
# 本文件是**数据**，不是逻辑：CMakeLists.txt 按 CMAKE_SYSTEM_NAME 包含它，
# 不做任何平台判断（决议 #81）。加平台 = 加一份文件。
#
# 交叉编译**怎么发生**不在这里，在 native/cross/android-arm64.cmake（toolchain
# file，传给 -DCMAKE_TOOLCHAIN_FILE=）。两份文件职责不同，别合并：
#   cross/     → 怎么交叉编译（给 CMake 的工具链）
#   platforms/ → 这个平台的产物长什么样（给我们自己的构建逻辑）

set(DCU_PLATFORM_KEY  "android")
set(DCU_PLUGIN_REL    "Android/${CMAKE_ANDROID_ARCH_ABI}")   # 无 libs/ 段（SPEC §8）
set(DCU_ARTIFACT_NAME "libdatachannel_unity.so")

# 复用 Linux 那份**生成物**：它按链接器格式（ELF）产出，与平台身份无关。
# 这就是决议 #79 说的「共用实现，不共用身份」——平台键是独立的 `android`，
# 但 ELF 版本脚本只有一种写法，没有理由生成两份相同的文件。
set(DCU_EXPORT_FILE "${DCU_GEN_EXPORTS_DIR}/linux-version-script.map")
set(DCU_EXPORT_LINK_OPTIONS
  "LINKER:--version-script=${DCU_EXPORT_FILE}"
  "LINKER:--exclude-libs,ALL"
)

# ---------- 16 KB 页对齐（决议 #81） ----------
#
# **显式写，不吃 NDK r27 的默认值。** 三条理由：
#   1. 我们钉的是 r27 **大版本**，小版本取镜像上最新的（#79）—— 默认值恰恰是
#      跨小版本可能变的那类东西，而我们主动放弃了小版本的控制权。
#   2. 默认值不写在仓库里，读代码的人看不见。
#   3. 成本为零：默认已是它，这就是一次无害确认；哪天不是了，这是唯一挡住的东西。
set(DCU_EXTRA_LINK_OPTIONS "LINKER:-z,max-page-size=16384")

# 上面那条 flag 只是「我们请求了」。链接器可能忽略、可能被别的 flag 覆盖、NDK
# 可能换行为 —— **真正的保证是下面这条验收**，由 audit_plugin.py 读实际的
# PT_LOAD 对齐。两者是「请求 + 验收」，不是双保险；缺了验收，flag 就是一句愿望。
#
# 只有本平台声明它。桌面三份不声明，audit 也就不检查 —— 「谁需要 16 KB」这个
# 事实住在这里、与 flag 挨着，不会一个改了另一个忘了。
#
# 范围边界：本包只保证 `.so` 自身对齐。它在 APK/AAB 里压不压缩、zip 条目对不对
# 齐，由采用者的打包配置决定，已出图（map #76 的 Out of scope，依据见
# docs/research/android-packaging-alignment.md）。
set(DCU_REQUIRE_PAGE_ALIGN 16384)

# 入库的 .so 剥掉 DWARF（NDK Clang 的 Release 会编进完整 .debug_*）。
# 未剥离副本在 Symbols~/，与 Plugins/ 路径镜像。
set(DCU_STRIP_DEBUG ON)
set(DCU_STRIP_ARGS "--strip-debug")
set(DCU_STAGE_PDB OFF)
