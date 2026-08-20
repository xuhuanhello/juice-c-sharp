# Windows x64。
#
# 本文件是**数据**，不是逻辑：CMakeLists.txt 按 CMAKE_SYSTEM_NAME 包含它，
# 不做任何平台判断（决议 #81）。加平台 = 加一份文件。

# ARM64 **不在矩阵里**（map #46 的 Out of scope）：Unity 2022.3 的 Standalone
# Windows 没有 ARM64 槽位，编辑器源码显式返回空路径，产出它等于产出一个装不进
# 任何 Player 的二进制。
#
# 这里断言而不是悄悄按 x86_64 暂存 —— 后者会把一个 ARM64 的 DLL 放进
# Plugins/Windows/x86_64/，是「不报错的错」。旧代码有一条 Windows/ARM64 分支，
# 但那条路径从未被走过。
if(NOT CMAKE_SYSTEM_PROCESSOR MATCHES "^(AMD64|amd64|x86_64|x64)$")
  message(FATAL_ERROR
    "Windows target processor is '${CMAKE_SYSTEM_PROCESSOR}', but only x64 is in the matrix.\n"
    "  Windows ARM64 has no Standalone slot on Unity 2022.3 (map #46, SPEC section 8).\n"
    "  If the minimum Unity version is ever raised to 6000.0, add its own platform file.")
endif()

set(DCU_PLATFORM_KEY  "windows")
set(DCU_PLUGIN_REL    "Windows/x86_64")
set(DCU_ARTIFACT_NAME "datachannel_unity.dll")

# 注意 `.def` **不是限制性白名单**：它缺符号会静默通过，多了已删符号才硬失败
# （#47 实测）。真正的导出闸门是 dcu.h 里的 DCU_API（__declspec(dllexport)）。
set(DCU_EXPORT_FILE "${DCU_GEN_EXPORTS_DIR}/windows-exports.def")
set(DCU_EXPORT_LINK_OPTIONS "/DEF:${DCU_EXPORT_FILE}")

# 行号在 PDB 里，不在 DLL 里。不要 llvm-strip / strip 这颗 DLL。
# PDB 只进 Symbols~/，不进 Plugins/（Unity 不打包它，铸 .meta 也没有消费者）。
set(DCU_STRIP_DEBUG OFF)
set(DCU_STAGE_PDB ON)
