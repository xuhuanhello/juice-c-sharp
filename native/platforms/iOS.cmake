# iOS arm64 —— device-only 静态归档（决议 #92 / #93）。
#
# 本文件是**数据**，不是逻辑：CMakeLists.txt 按 CMAKE_SYSTEM_NAME 包含它，
# 不做任何平台判断（决议 #81）。加平台 = 加一份文件。
#
# 与其它平台文件的关键差异：
#   - 产物是静态 .a，不是动态库；add_library 类型由本文件的 DCU_LIBRARY_TYPE 控制
#   - DCU_EXPORT_LINK_OPTIONS 设空：iOS 没有链接期；符号收窄在下游 ld -r 步骤
#   - DCU_EXPORT_FILE 复用 macos-exported-symbols.txt（格式相同：Mach-O 白名单，
#     前导 _，传给 ld -r -exported_symbols_list）

set(DCU_PLATFORM_KEY  "ios")
set(DCU_PLUGIN_REL    "iOS")           # SPEC §8 layout；无架构子目录
set(DCU_ARTIFACT_NAME "libdatachannel_unity.a")

# ld -r 收窄用这份白名单（narrow_ios_archive.py 里的 --symbols-list）
set(DCU_EXPORT_FILE "${DCU_GEN_EXPORTS_DIR}/macos-exported-symbols.txt")

# 静态库没有链接期，符号控制走 ld -r，这里设空
set(DCU_EXPORT_LINK_OPTIONS "")

# iOS 静态库：STATIC 而非 SHARED
set(DCU_LIBRARY_TYPE "STATIC")

# 部署目标（12.0）由 toolchain file 传入 CMAKE_OSX_DEPLOYMENT_TARGET；
# SDK 版本在 narrow_ios_archive.py 里用 xcrun 实时查，不在这里硬编码。
