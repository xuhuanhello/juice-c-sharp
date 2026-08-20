# macOS —— 单一 universal `.dylib`（#55 推翻 #10 的双 thin 与 `.bundle`）。
#
# 本文件是**数据**，不是逻辑：CMakeLists.txt 按 CMAKE_SYSTEM_NAME 包含它，
# 不做任何平台判断（决议 #81）。加平台 = 加一份文件。

set(DCU_PLATFORM_KEY  "darwin")
set(DCU_PLUGIN_REL    "macOS")          # 无架构子目录：universal 只有一份产物
set(DCU_ARTIFACT_NAME "datachannel_unity.dylib")

# macOS universal 默认（#55）。
# 原本在 CMakeLists.txt 顶部的 if(APPLE AND NOT CMAKE_OSX_ARCHITECTURES) 块，
# 现在移到这里（决议 #92 §B）：iOS toolchain file 在本文件被包含之前已把
# CMAKE_OSX_ARCHITECTURES 设成 arm64，NOT 守卫自然阻止 macOS 默认触发。
# 这消灭了 CMakeLists.txt 里最后一处 APPLE 平台分支（#81 的规矩）。
if(NOT CMAKE_OSX_ARCHITECTURES)
  set(CMAKE_OSX_ARCHITECTURES "arm64;x86_64" CACHE STRING "macOS universal" FORCE)
endif()

set(DCU_EXPORT_FILE "${DCU_GEN_EXPORTS_DIR}/macos-exported-symbols.txt")
set(DCU_EXPORT_LINK_OPTIONS
  "LINKER:-exported_symbols_list,${DCU_EXPORT_FILE}"
)

# libjuice / libdatachannel 取随机数与证书要用系统 API。它们是**系统自带的**，
# 不违反「crypto 必须静态链接」——那条针对的是被打包进产品的 OpenSSL/MbedTLS。
set(DCU_EXTRA_LINK_LIBRARIES
  "-framework CoreFoundation"
  "-framework Security"
)

# Apple strip 没有 GNU 的 --strip-debug；-S 是去掉调试符号。
# CMAKE_STRIP 是 /usr/bin/strip，不是 llvm-strip。
set(DCU_STRIP_DEBUG ON)
set(DCU_STRIP_ARGS "-S")
set(DCU_STAGE_PDB OFF)
