# datachannel-unity — 原生插件构建（唯一入口）
#
# 布局：
#   native/CMakeLists.txt          (本文件)
#   native/dcu/                    项目自有的稳定 C ABI 层
#   native/subprojects/mbedtls/            上游，由 scripts/fetch-deps.sh 拉取
#   native/subprojects/libdatachannel/     同上
#   native/mbedtls/user_config.h   MbedTLS 用户配置（DTLS-SRTP）
#   native/exports/                各平台的导出符号白名单
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
    message(FATAL_ERROR "缺少 ${dep} —— 请先运行 native/scripts/fetch-deps.sh")
  endif()
endforeach()
# 项目自有源码：fetch-deps.sh 不会生成，缺失几乎必然是 .gitignore 误伤
if(NOT EXISTS "${MBEDTLS_USER_CFG}")
  message(FATAL_ERROR "缺少 ${MBEDTLS_USER_CFG}（项目自有源码，应随仓库提供）")
endif()
if(NOT EXISTS "${DCU_SRC}/src/dcu_impl.cpp")
  message(FATAL_ERROR "缺少 ${DCU_SRC}/src/dcu_impl.cpp（项目自有源码，应随仓库提供）")
endif()

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

# 仅导出 dcu_*（决策 #18）
if(APPLE)
  target_link_libraries(datachannel_unity PRIVATE
    "-framework CoreFoundation"
    "-framework Security"
  )
  target_link_options(datachannel_unity PRIVATE
    "LINKER:-exported_symbols_list,${EXPORTS_DIR}/macos-exported-symbols.txt"
  )
elseif(WIN32)
  target_link_options(datachannel_unity PRIVATE
    "/DEF:${EXPORTS_DIR}/windows-exports.def"
  )
else()
  target_link_options(datachannel_unity PRIVATE
    "LINKER:--version-script=${EXPORTS_DIR}/linux-version-script.map"
    "LINKER:--exclude-libs,ALL"
  )
endif()

# ---------- 暂存进 UPM Plugins 布局 ----------
# 平台映射走 CMAKE_SYSTEM_*，因此交叉编译时会跟随 toolchain file，
# 而不是像旧的 shell shim 那样按 host 猜。
if(DCU_STAGE_PLUGIN)
  if(APPLE)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(arm64|aarch64)$")
      set(DCU_PLUGIN_REL "macOS/arm64")
    else()
      set(DCU_PLUGIN_REL "macOS/x64")
    endif()
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

  find_package(Python3 COMPONENTS Interpreter REQUIRED)

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
endif()
