# Linux x64。
#
# 本文件是**数据**，不是逻辑：CMakeLists.txt 按 CMAKE_SYSTEM_NAME 包含它，
# 不做任何平台判断（决议 #81）。加平台 = 加一份文件。

set(DCU_PLATFORM_KEY  "linux")
set(DCU_PLUGIN_REL    "Linux/${CMAKE_SYSTEM_PROCESSOR}")
set(DCU_ARTIFACT_NAME "libdatachannel_unity.so")

set(DCU_EXPORT_FILE "${DCU_GEN_EXPORTS_DIR}/linux-version-script.map")
set(DCU_EXPORT_LINK_OPTIONS
  "LINKER:--version-script=${DCU_EXPORT_FILE}"
  "LINKER:--exclude-libs,ALL"
)

# gcc 的 Release 默认没有 -g；全局加了 -g 之后这里必须剥，否则 Plugins/ 会
# 带着 DWARF 进采用者的 Player。未剥离副本在 Symbols~/。
set(DCU_STRIP_DEBUG ON)
set(DCU_STRIP_ARGS "--strip-debug")
set(DCU_STAGE_PDB OFF)
