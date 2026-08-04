# datachannel-unity — 原生插件构建（唯一入口）
#
# 布局：
#   native/CMakeLists.txt          (本文件)
#   native/dcu/                    项目自有的稳定 C ABI 层
#   native/subprojects/mbedtls/            上游，由 scripts/fetch-deps.sh 拉取
#   native/subprojects/libdatachannel/     同上
#   native/mbedtls/user_config.h   MbedTLS 用户配置（DTLS-SRTP）
#   native/exports/                唯一手写的导出符号清单（各平台白名单由它生成）
#
# 用法：
#   ./scripts/fetch-deps.sh
#   cmake -S native -B native/build/<name> -G Ninja -DCMAKE_BUILD_TYPE=Release
#   cmake --build native/build/<name>
#
# 交叉编译：追加 -DCMAKE_TOOLCHAIN_FILE=<file>。产物暂存路径由
# CMAKE_SYSTEM_NAME / CMAKE_SYSTEM_PROCESSOR 推导，因此会跟随 toolchain file。
cmake_minimum_required(VERSION 3.16)
project(datachannel_unity LANGUAGES C CXX)

# macOS 产物是**单一 universal bundle**，不是每架构一份 thin。
# 这推翻了 #10 的「明确不做（v1）：macOS universal lipo」—— 那一行没有记任何理由，
# 与当年 Meson 被推翻时同类（决定存在、论证不存在）。改的理由见 #55：
#   - 一条命令出两个架构，不需要额外的 job 或 toolchain file（实测）
#   - LFS 成本基本相同（universal 5.0 MB vs 两份 thin 合计 4.8 MB）
#   - `.meta` 从两份必须靠 CPU 字段区分，变成一份 CPU: AnyCPU —— 而「两份同名
#     bundle」正是 #49 查出的 CheckFileCollisions 冲突与 reimport 刷 error 的根源，
#     走 universal 之后这个问题从根上不存在
# 代价：采用者的 App 多带另一半（约 2.4 MB）；audit 必须逐架构检查（已实现）。
if(APPLE AND NOT CMAKE_OSX_ARCHITECTURES)
  set(CMAKE_OSX_ARCHITECTURES "arm64;x86_64" CACHE STRING "macOS universal" FORCE)
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

# 本工程只暂存 Unity 插件，跳过嵌套的 install/export 规则
# （libdatachannel 的 install(EXPORT) 在 MbedTLS 不同属一个 export set 时会失败）
set(CMAKE_SKIP_INSTALL_RULES ON)

set(NATIVE_ROOT      "${CMAKE_CURRENT_SOURCE_DIR}")
set(MBEDTLS_SRC      "${NATIVE_ROOT}/subprojects/mbedtls")
set(LIBDC_SRC        "${NATIVE_ROOT}/subprojects/libdatachannel")
set(DCU_SRC          "${NATIVE_ROOT}/dcu")
set(MBEDTLS_USER_CFG "${NATIVE_ROOT}/mbedtls/user_config.h")
set(EXPORTS_DIR      "${NATIVE_ROOT}/exports")

option(DCU_STAGE_PLUGIN "构建后把产物暂存进 UPM Plugins 布局" ON)
set(DCU_PLUGIN_ROOT "${NATIVE_ROOT}/../Packages/datachannel-unity/Plugins"
    CACHE PATH "Packages/datachannel-unity/Plugins 的路径")

# ---------- 前置检查 ----------
# 上游 checkout：缺失时 fetch-deps.sh 能补
foreach(dep IN ITEMS "${MBEDTLS_SRC}" "${LIBDC_SRC}")
  if(NOT EXISTS "${dep}/CMakeLists.txt")
    message(FATAL_ERROR "Missing ${dep} -- run native/scripts/fetch-deps.sh first")
  endif()
endforeach()
# 项目自有源码：fetch-deps.sh 不会生成，缺失几乎必然是 .gitignore 误伤
if(NOT EXISTS "${MBEDTLS_USER_CFG}")
  message(FATAL_ERROR "Missing ${MBEDTLS_USER_CFG} (first-party source; it ships with the repository)")
endif()
if(NOT EXISTS "${DCU_SRC}/src/dcu_impl.cpp")
  message(FATAL_ERROR "Missing ${DCU_SRC}/src/dcu_impl.cpp (first-party source; it ships with the repository)")
endif()

find_package(Python3 COMPONENTS Interpreter REQUIRED)

# ---------- 各平台导出白名单：从唯一权威清单生成 ----------
# 仓库里只手写 expected-symbols.txt 一份；三种链接器格式是**生成物**，
# 落在构建目录、不入库（决议 #50）。入库的生成物会成为第二个真相源，
# 而那正是这条决议要消灭的病 —— 曾经入库的 windows-exports.def 就烂了：
# 带着 S2 已删除的四个符号（会 LNK2001 硬失败），又缺六个现存符号（静默通过）。
set(DCU_EXPECTED_SYMBOLS "${EXPORTS_DIR}/expected-symbols.txt")
set(DCU_GEN_EXPORTS_DIR  "${CMAKE_BINARY_DIR}/exports")

# 改动权威清单必须触发重新配置。#27 拆掉 Meson 的理由之一就是「改了源文件
# 不触发重建」—— 同一个缺陷不要在这里复现。
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
  "${DCU_EXPECTED_SYMBOLS}" "${DCU_SRC}/include/dcu.h")

execute_process(
  COMMAND "${Python3_EXECUTABLE}" "${NATIVE_ROOT}/scripts/gen_exports.py"
          --expected "${DCU_EXPECTED_SYMBOLS}"
          --header   "${DCU_SRC}/include/dcu.h"
          --out-dir  "${DCU_GEN_EXPORTS_DIR}"
  RESULT_VARIABLE DCU_GEN_EXPORTS_RC
  OUTPUT_VARIABLE DCU_GEN_EXPORTS_OUT
  ERROR_VARIABLE  DCU_GEN_EXPORTS_ERR
)
if(NOT DCU_GEN_EXPORTS_RC EQUAL 0)
  message(FATAL_ERROR
    "Failed to generate the export allowlists (rc=${DCU_GEN_EXPORTS_RC}):\n${DCU_GEN_EXPORTS_ERR}")
endif()
message(STATUS "${DCU_GEN_EXPORTS_OUT}")

# ---------- MbedTLS 3.6.x 静态 + DTLS-SRTP ----------
set(ENABLE_PROGRAMS OFF CACHE BOOL "" FORCE)
set(ENABLE_TESTING OFF CACHE BOOL "" FORCE)
set(USE_STATIC_MBEDTLS_LIBRARY ON CACHE BOOL "" FORCE)
set(USE_SHARED_MBEDTLS_LIBRARY OFF CACHE BOOL "" FORCE)
set(MBEDTLS_USER_CONFIG_FILE "${MBEDTLS_USER_CFG}" CACHE FILEPATH "" FORCE)

add_subdirectory("${MBEDTLS_SRC}" "${CMAKE_BINARY_DIR}/mbedtls")

# libdatachannel 在 find_package 之前会先找 MbedTLS::MbedTLS
if(NOT TARGET MbedTLS::MbedTLS)
  add_library(MbedTLS::MbedCrypto ALIAS mbedcrypto)
  add_library(MbedTLS::MbedX509 ALIAS mbedx509)
  add_library(MbedTLS::MbedTLS ALIAS mbedtls)
endif()

# ---------- libdatachannel 静态 ----------
set(NO_MEDIA ON CACHE BOOL "" FORCE)
set(NO_WEBSOCKET ON CACHE BOOL "" FORCE)
set(NO_EXAMPLES ON CACHE BOOL "" FORCE)
set(NO_TESTS ON CACHE BOOL "" FORCE)
set(USE_NICE OFF CACHE BOOL "" FORCE)
set(USE_GNUTLS OFF CACHE BOOL "" FORCE)
set(USE_MBEDTLS ON CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)

list(APPEND CMAKE_MODULE_PATH "${LIBDC_SRC}/cmake/Modules")
add_subdirectory("${LIBDC_SRC}" "${CMAKE_BINARY_DIR}/libdatachannel")

# ---------- datachannel_unity 插件 ----------
add_library(datachannel_unity SHARED "${DCU_SRC}/src/dcu_impl.cpp")

target_include_directories(datachannel_unity PRIVATE
  "${DCU_SRC}/include"
  "${LIBDC_SRC}/include"
  "${CMAKE_BINARY_DIR}/libdatachannel/include"
)
target_compile_definitions(datachannel_unity PRIVATE DCU_BUILD RTC_STATIC=1)
target_link_libraries(datachannel_unity PRIVATE datachannel-static)

set_target_properties(datachannel_unity PROPERTIES
  OUTPUT_NAME "datachannel_unity"
  PREFIX "lib"
)

# 仅导出 dcu_*（决策 #18）。白名单是生成物，见上面的 gen_exports.py 块。
if(APPLE)
  target_link_libraries(datachannel_unity PRIVATE
    "-framework CoreFoundation"
    "-framework Security"
  )
  set(DCU_EXPORT_FILE "${DCU_GEN_EXPORTS_DIR}/macos-exported-symbols.txt")
  target_link_options(datachannel_unity PRIVATE
    "LINKER:-exported_symbols_list,${DCU_EXPORT_FILE}"
  )
elseif(WIN32)
  set(DCU_EXPORT_FILE "${DCU_GEN_EXPORTS_DIR}/windows-exports.def")
  target_link_options(datachannel_unity PRIVATE "/DEF:${DCU_EXPORT_FILE}")
else()
  set(DCU_EXPORT_FILE "${DCU_GEN_EXPORTS_DIR}/linux-version-script.map")
  target_link_options(datachannel_unity PRIVATE
    "LINKER:--version-script=${DCU_EXPORT_FILE}"
    "LINKER:--exclude-libs,ALL"
  )
endif()

# 白名单变了就必须重链。少了这条，改 expected-symbols.txt 会重新配置、
# 重新生成白名单，却仍然沿用旧的 .dylib —— 又是「改了源文件不重建」。
set_target_properties(datachannel_unity PROPERTIES LINK_DEPENDS "${DCU_EXPORT_FILE}")

# ---------- 暂存进 UPM Plugins 布局 ----------
# 平台映射走 CMAKE_SYSTEM_*，因此交叉编译时会跟随 toolchain file，
# 而不是像旧的 shell shim 那样按 host 猜。
if(DCU_STAGE_PLUGIN)
  if(APPLE)
    # 单一 universal 产物，没有架构子目录（见文件开头的理由）
    set(DCU_PLUGIN_REL "macOS")
  elseif(WIN32)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(ARM64|arm64|aarch64)$")
      set(DCU_PLUGIN_REL "Windows/ARM64")
    else()
      set(DCU_PLUGIN_REL "Windows/x86_64")
    endif()
  elseif(ANDROID)
    set(DCU_PLUGIN_REL "Android/${CMAKE_ANDROID_ARCH_ABI}")
  else()
    set(DCU_PLUGIN_REL "Linux/${CMAKE_SYSTEM_PROCESSOR}")
  endif()

  add_custom_command(TARGET datachannel_unity POST_BUILD
    COMMAND "${Python3_EXECUTABLE}"
            "${NATIVE_ROOT}/scripts/stage_plugin.py"
            --binary      "$<TARGET_FILE:datachannel_unity>"
            --host-system "$<IF:$<BOOL:${APPLE}>,darwin,$<IF:$<BOOL:${WIN32}>,windows,linux>>"
            --plugin-root "${DCU_PLUGIN_ROOT}"
            --rel         "${DCU_PLUGIN_REL}"
    COMMENT "Staging plugin → ${DCU_PLUGIN_REL}"
    VERBATIM
  )

  # 溯源信息：与二进制同目录，随它一起入库（#54）。
  # 采用者手里只有一个包目录，没有 git 历史也没有 CI 日志 —— 报 bug 时要能反查
  # 这份二进制的来历，靠的就是它。刻意不做成 dcu_build_info() 导出：那会动 ABI，
  # 且让二进制内容每次构建都变，毁掉 #27 赖以验证 Meson→CMake 迁移的字节可复现性。
  add_custom_command(TARGET datachannel_unity POST_BUILD
    COMMAND "${Python3_EXECUTABLE}"
            "${NATIVE_ROOT}/scripts/gen_build_info.py"
            --out       "${DCU_PLUGIN_ROOT}/${DCU_PLUGIN_REL}/build-info.json"
            --repo-root "${NATIVE_ROOT}/.."
            --header    "${DCU_SRC}/include/dcu.h"
            --lock      "${NATIVE_ROOT}/versions.lock"
            --platform  "$<IF:$<BOOL:${APPLE}>,darwin,$<IF:$<BOOL:${WIN32}>,windows,linux>>"
            --architectures     "${CMAKE_OSX_ARCHITECTURES}"
            --target-system     "${CMAKE_SYSTEM_NAME}"
            --target-processor  "${CMAKE_SYSTEM_PROCESSOR}"
            --compiler-id       "${CMAKE_CXX_COMPILER_ID}"
            --compiler-version  "${CMAKE_CXX_COMPILER_VERSION}"
            --deployment-target "${CMAKE_OSX_DEPLOYMENT_TARGET}"
            --cmake-version     "${CMAKE_VERSION}"
    COMMENT "Writing build-info.json"
    VERBATIM
  )

  # 离线门禁：暂存之后无条件跑，三个平台一视同仁。
  # 与暂存拆成两条命令，是因为门禁需要 CMake 才知道的工具路径；从 stage_plugin.py
  # 透传只会多一层。SPEC §9 写的就是「staging + audit 都是 POST_BUILD 自定义命令，
  # 中间没有 shell shim」。
  #
  # MSVC 分支的 CMake 不提供 NM，dumpbin 更是完全没建模（CMakeFindBinUtils 只给
  # LINKER/MT/AR）——所以传 CMAKE_LINKER 进去，让脚本从 link.exe 同目录找 dumpbin.exe。
  add_custom_command(TARGET datachannel_unity POST_BUILD
    COMMAND "${Python3_EXECUTABLE}"
            "${NATIVE_ROOT}/scripts/audit_plugin.py"
            --binary   "${DCU_PLUGIN_ROOT}/${DCU_PLUGIN_REL}/$<IF:$<BOOL:${APPLE}>,datachannel_unity.bundle,$<IF:$<BOOL:${WIN32}>,datachannel_unity.dll,libdatachannel_unity.so>>"
            --platform "$<IF:$<BOOL:${APPLE}>,darwin,$<IF:$<BOOL:${WIN32}>,windows,linux>>"
            --expected "${DCU_EXPECTED_SYMBOLS}"
            --nm       "${CMAKE_NM}"
            --readelf  "${CMAKE_READELF}"
            --linker   "${CMAKE_LINKER}"
    COMMENT "Auditing plugin (exports + dependencies)"
    VERBATIM
  )
endif()
