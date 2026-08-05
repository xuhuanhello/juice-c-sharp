# macOS —— 单一 universal `.dylib`（#55 推翻 #10 的双 thin 与 `.bundle`）。
#
# 本文件是**数据**，不是逻辑：CMakeLists.txt 按 CMAKE_SYSTEM_NAME 包含它，
# 不做任何平台判断（决议 #81）。加平台 = 加一份文件。

set(DCU_PLATFORM_KEY  "darwin")
set(DCU_PLUGIN_REL    "macOS")          # 无架构子目录：universal 只有一份产物
set(DCU_ARTIFACT_NAME "datachannel_unity.dylib")

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
