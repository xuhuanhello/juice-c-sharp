# datachannel-unity — 原生插件构建（唯一入口）
#
# 布局：
#   native/CMakeLists.txt          (本文件)
#   native/dcu/                    项目自有的稳定 C ABI 层
#   native/platforms/<系统名>.cmake 每平台一份配置数据（#81）；加平台=加文件
#   native/cross/                  交叉编译的 toolchain file
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
# 交叉编译：追加 -DCMAKE_TOOLCHAIN_FILE=native/cross/<平台>.cmake
#   Android arm64-v8a：native/cross/android-arm64.cmake
# 平台配置按 CMAKE_SYSTEM_NAME 选取，因此**自动跟随 toolchain file**：
# toolchain 说这是 Android，就用 platforms/Android.cmake，没有第二处要同步。
cmake_minimum_required(VERSION 3.16)
project(datachannel_unity LANGUAGES C CXX)

# macOS universal 默认已移入 platforms/Darwin.cmake（决议 #92 §B）。
# iOS toolchain file 在平台文件包含之前设好 CMAKE_OSX_ARCHITECTURES=arm64，
# macOS 的 universal 默认不触发 —— 这消灭了此处最后一处 APPLE 平台分支（#81）。

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

# C 的 tentative definition 必须编成有节的定义，不留 common（决议 #104 / #108）。
#
# 为什么这是全局约定而不是 iOS 分支：visibility preset 上面那三行同理 —— 它描述的是
# 「本工程的 C 代码怎么编」，与目标平台无关。iOS 只是**唯一会暴露**它的平台：那里
# 产物是静态归档，符号原样交给采用者的最终链接；动态库平台的 common 在自己的链接期
# 就被解析掉了，外部看不见。为一个只在某平台可见的**通用**约定加平台分支，就是 #81
# 要消灭的那种分支。
#
# 它挡的是什么：common 符号（nm 的 `C`）在最终链接期会与采用者的同名 common
# **静默合并成同一块内存** —— 不报错、不警告。收窄前的归档里有 11 个，其中一个叫
# `_foo`（usrsctp `user_recv_thread.c:1564` 的孤立遗留变量），撞上采用者任何一个
# `int foo;` 就是这个后果。
#
# 为什么加了这行就够：`-fno-common` 把 tentative definition 编成 `S`（有节），
# 于是既有的 `ld -r -exported_symbols_list` 能把它降成 local `s` —— common 没有节，
# 降不了，这才是它此前逃过收窄的原因。实测链：`C _foo → S _foo → s _foo`，
# 同一次 ld -r 里 `_dcu_keep` 仍是 `T`。
#
# 为什么不需要给上游打补丁：那 11 个都是**单一翻译单元的 tentative 定义 + 头文件
# extern** 的正常形态（10 个来自 usrsctp，1 个是 mbedtls 的
# `_mbedtls_cipher_supported`），不依赖 common 的跨 TU 合并语义，所以不会产生
# duplicate definition。Apple clang 的 C 默认实测就是 `-fcommon`；不必去找
# 「哪里显式开了它」，那个地方不存在。
#
# 限定编译器而不是 if(NOT MSVC)：MSVC 没有 tentative definition 合并这个概念，
# 也不认这个 flag。列出认识它的三家，比排除法更说明「为什么只有它们需要」。
# GCC 10 / Clang 11 起本来就默认 -fno-common，对它们这行是**冗余但无害**的显式声明；
# 显式写出来，是为了让这条约定不依赖某个编译器版本的默认值。
add_compile_options($<$<COMPILE_LANG_AND_ID:C,AppleClang,Clang,GNU>:-fno-common>)

# Release 也带行号。崩溃还原要的是**同一次编译**的调试信息，不是把 DWARF 留在
# 采用者的 Player 里。Clang/GNU 的 Release 默认没有 -g（NDK Clang 是例外，会编进
# 完整 .debug_*）；MSVC 的 Release 默认不写 PDB。Plugins/ 是否剥掉，由平台文件的
# DCU_STRIP_DEBUG 决定；未剥离副本进 Symbols~/。这是全局约定，与 -fno-common 同理：
# 它描述「本工程的代码怎么编」，不是某平台的特例。
add_compile_options($<$<COMPILE_LANG_AND_ID:C,AppleClang,Clang,GNU>:-g>)
add_compile_options($<$<COMPILE_LANG_AND_ID:CXX,AppleClang,Clang,GNU>:-g>)
add_compile_options($<$<COMPILE_LANG_AND_ID:C,MSVC>:/Zi>)
add_compile_options($<$<COMPILE_LANG_AND_ID:CXX,MSVC>:/Zi>)

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
# 溯源文件的落点，与 Plugins/ **同级而非其子目录**（#65）：`~` 后缀让 Unity 的
# AssetDatabase 完全忽略这一层（与本包 Samples~ 同一手法），于是一份纯构建记录
# 不必铸 GUID、不必带 .meta。规矩是「Plugins/ 里只放二进制和它的 .meta」。
set(DCU_REPORT_ROOT "${NATIVE_ROOT}/../Packages/datachannel-unity/Report~"
    CACHE PATH "Packages/datachannel-unity/Report~ 的路径")
# 未剥离 / PDB 与 Plugins/ 路径镜像（SPEC §10）。同样用 `~`：进 UPM，不进
# Player，不铸 GUID。GitHub Release 上的拷贝是可选重复，不是权威来源。
set(DCU_SYMBOLS_ROOT "${NATIVE_ROOT}/../Packages/datachannel-unity/Symbols~"
    CACHE PATH "Packages/datachannel-unity/Symbols~ 的路径")

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

# ---------- 平台配置：数据，不是分支（决议 #81） ----------
# 每个平台一份 native/platforms/<CMAKE_SYSTEM_NAME>.cmake，声明该平台的全部数据。
# 本文件**不做任何平台判断** —— 加平台 = 加一份文件。
#
# 为什么不是 if/elseif 链：原先五处平台差异（插件目录、平台键、产物名、导出白名单
# 与传法、附加链接项）全是**查表**，没有一条是逻辑。而写成 if/else 会带一个
# `else()` 兜底，于是「未识别的平台」自动变成 linux —— 三处生成表达式都这样，其中
# gen_build_info 会**静默写错**（JSON 内容 platform=linux，文件名却是
# Android__arm64-v8a.json，名实矛盾且无门禁会红）。
#
# 数据驱动的形状里**没有 fall-through 可落**：文件不存在就是硬失败。缺陷是被
# 结构消掉的，不是被检查挡住的。
set(DCU_PLATFORM_FILE "${NATIVE_ROOT}/platforms/${CMAKE_SYSTEM_NAME}.cmake")
if(NOT EXISTS "${DCU_PLATFORM_FILE}")
  file(GLOB _dcu_platform_files "${NATIVE_ROOT}/platforms/*.cmake")
  set(_dcu_known "")
  foreach(_f IN LISTS _dcu_platform_files)
    get_filename_component(_n "${_f}" NAME_WE)
    string(APPEND _dcu_known " ${_n}")
  endforeach()
  message(FATAL_ERROR
    "No platform configuration for CMAKE_SYSTEM_NAME='${CMAKE_SYSTEM_NAME}'.\n"
    "  Expected: ${DCU_PLATFORM_FILE}\n"
    "  Known platforms:${_dcu_known}\n"
    "  Adding a platform means adding that file, not adding a branch here.")
endif()
include("${DCU_PLATFORM_FILE}")

# 平台文件必须声明的东西。少一个就硬失败，而不是让下游拿到空字符串继续 ——
# 空的 DCU_ARTIFACT_NAME 会让 audit 去查一个目录，报出的错与真正的原因无关。
foreach(_req IN ITEMS
    DCU_PLATFORM_KEY DCU_PLUGIN_REL DCU_ARTIFACT_NAME
    DCU_EXPORT_FILE DCU_EXPORT_LINK_OPTIONS
    DCU_STRIP_DEBUG DCU_STAGE_PDB)
  if(NOT DEFINED ${_req})
    message(FATAL_ERROR
      "${DCU_PLATFORM_FILE} does not set ${_req}.\n"
      "  Every platform file must set: DCU_PLATFORM_KEY, DCU_PLUGIN_REL, "
      "DCU_ARTIFACT_NAME, DCU_EXPORT_FILE, DCU_EXPORT_LINK_OPTIONS, "
      "DCU_STRIP_DEBUG, DCU_STAGE_PDB.\n"
      "  Optional: DCU_EXTRA_LINK_LIBRARIES, DCU_EXTRA_LINK_OPTIONS, "
      "DCU_REQUIRE_PAGE_ALIGN, DCU_LIBRARY_TYPE, DCU_STRIP_ARGS "
      "(required when DCU_STRIP_DEBUG is ON).")
  endif()
endforeach()
if(DCU_STRIP_DEBUG)
  if(NOT DEFINED DCU_STRIP_ARGS)
    message(FATAL_ERROR
      "${DCU_PLATFORM_FILE} sets DCU_STRIP_DEBUG but not DCU_STRIP_ARGS.\n"
      "  ON means Plugins/ is stripped after the Symbols~/ copy; the args are "
      "the strip invocation (GNU/llvm --strip-debug, Apple -S).")
  endif()
  if(NOT CMAKE_STRIP)
    message(FATAL_ERROR
      "DCU_STRIP_DEBUG is ON for '${DCU_PLATFORM_KEY}' but CMAKE_STRIP is empty.\n"
      "  Missing strip cannot be a silent skip: the shipped plugin would keep DWARF.")
  endif()
endif()
if(DCU_STAGE_PDB AND NOT MSVC)
  message(FATAL_ERROR
    "${DCU_PLATFORM_FILE} sets DCU_STAGE_PDB, but the compiler is not MSVC.\n"
    "  PDB is the MSVC debug format; other toolchains keep line info in the binary.")
endif()
message(STATUS
  "Platform: ${DCU_PLATFORM_KEY} → Plugins/${DCU_PLUGIN_REL}/${DCU_ARTIFACT_NAME}")

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
# ---------- 库类型：平台文件声明，静态 vs 共享 ----------
# iOS 是 STATIC（.a），其他平台是 SHARED（.dylib/.so/.dll）。
# 由 DCU_LIBRARY_TYPE 控制；不声明则默认 SHARED（向后兼容）。
if(NOT DEFINED DCU_LIBRARY_TYPE)
  set(DCU_LIBRARY_TYPE "SHARED")
endif()
add_library(datachannel_unity ${DCU_LIBRARY_TYPE} "${DCU_SRC}/src/dcu_impl.cpp")

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

# 仅导出 dcu_*（决策 #18）。白名单是生成物，见上面的 gen_exports.py 块；
# 用哪一份、怎么传给链接器，由平台文件声明。
if(DCU_EXTRA_LINK_LIBRARIES)
  target_link_libraries(datachannel_unity PRIVATE ${DCU_EXTRA_LINK_LIBRARIES})
endif()
target_link_options(datachannel_unity PRIVATE
  ${DCU_EXPORT_LINK_OPTIONS}
  ${DCU_EXTRA_LINK_OPTIONS}
)
# MSVC Release 默认不写 PDB。/Zi 编进了对象，还要链接器把它们收进 .pdb，
# 否则 DCU_STAGE_PDB 去拷一个不存在的文件 —— 缺席必须是失败。
target_link_options(datachannel_unity PRIVATE
  $<$<CXX_COMPILER_ID:MSVC>:/DEBUG:FULL>
)

# 白名单变了就必须重链。少了这条，改 expected-symbols.txt 会重新配置、
# 重新生成白名单，却仍然沿用旧的 .dylib —— 又是「改了源文件不重建」。
set_target_properties(datachannel_unity PROPERTIES LINK_DEPENDS "${DCU_EXPORT_FILE}")

# ---------- 暂存进 UPM Plugins 布局 ----------
# 落点、产物名、平台键全部来自平台文件（上面 include 的那份），这里不再推导。
if(DCU_STAGE_PLUGIN)
  # iOS 静态归档：先收窄（ld -r），再暂存。动态库跳过收窄直接暂存。
  # 收窄在暂存之前，因为 stage_plugin.py 从 TARGET_FILE 取文件路径，
  # narrow_ios_archive.py 的 --out 就写进同一个路径，stage 拿到的已经是收窄后的。
  if(DCU_PLATFORM_KEY STREQUAL "ios")
    # SDK 版本在构建时用 xcrun 实时查，不硬编码（决议 #93）
    execute_process(
      COMMAND xcrun --sdk iphoneos --show-sdk-version
      OUTPUT_VARIABLE DCU_IOS_SDK_VERSION
      OUTPUT_STRIP_TRAILING_WHITESPACE
      ERROR_QUIET
    )
    if(NOT DCU_IOS_SDK_VERSION)
      message(FATAL_ERROR
        "Cannot determine iPhoneOS SDK version (xcrun --sdk iphoneos --show-sdk-version failed).\n"
        "  Make sure Xcode is installed and 'xcode-select -p' points to a valid Xcode app.")
    endif()
    # 静态库没有链接期：target_link_libraries(datachannel_unity PRIVATE
    # datachannel-static) 只记录依赖关系，**不会**把依赖的代码搬进产物（#97）。
    # 所以这里必须把每个依赖的归档显式喂给合并步骤，否则入库的 .a 只含 wrapper
    # 而实现全缺 —— 且导出门禁看不出来（dcu_* 依然齐全）。
    # 用 $<TARGET_FILE:> 而不是写死路径：落点由各子项目自己决定，写死会在
    # 上游改布局时静默地指向一个不存在的文件。
    set(DCU_IOS_DEP_TARGETS
      datachannel-static
      juice-static
      usrsctp
      mbedtls
      mbedx509
      mbedcrypto
    )
    set(DCU_IOS_MERGE_ARGS "")
    foreach(dep IN LISTS DCU_IOS_DEP_TARGETS)
      if(NOT TARGET ${dep})
        message(FATAL_ERROR
          "iOS dependency target '${dep}' does not exist.\n"
          "  Every dependency must be merged into the shipped static archive by name;\n"
          "  a missing one produces an archive that passes the export gate and then\n"
          "  fails to link in Xcode (#97). Update DCU_IOS_DEP_TARGETS if the upstream\n"
          "  target was renamed.")
      endif()
      list(APPEND DCU_IOS_MERGE_ARGS --extra-archive "$<TARGET_FILE:${dep}>")
    endforeach()

    add_custom_command(TARGET datachannel_unity POST_BUILD
      COMMAND "${Python3_EXECUTABLE}"
              "${NATIVE_ROOT}/scripts/narrow_ios_archive.py"
              --archive      "$<TARGET_FILE:datachannel_unity>"
              ${DCU_IOS_MERGE_ARGS}
              --out          "${DCU_PLUGIN_ROOT}/${DCU_PLUGIN_REL}/${DCU_ARTIFACT_NAME}"
              --symbols-list "${DCU_EXPORT_FILE}"
              --arch         "arm64"
              --deployment-target "${CMAKE_OSX_DEPLOYMENT_TARGET}"
              --sdk-version  "${DCU_IOS_SDK_VERSION}"
              --tmp-dir      "${CMAKE_BINARY_DIR}/ios-narrow-tmp"
      COMMENT "Merging iOS dependency archives and narrowing with ld -r"
      VERBATIM
    )
  else()
    add_custom_command(TARGET datachannel_unity POST_BUILD
      COMMAND "${Python3_EXECUTABLE}"
              "${NATIVE_ROOT}/scripts/stage_plugin.py"
              --binary        "$<TARGET_FILE:datachannel_unity>"
              --platform      "${DCU_PLATFORM_KEY}"
              --artifact-name "${DCU_ARTIFACT_NAME}"
              --plugin-root   "${DCU_PLUGIN_ROOT}"
              --rel           "${DCU_PLUGIN_REL}"
      COMMENT "Staging plugin → ${DCU_PLUGIN_REL}/${DCU_ARTIFACT_NAME}"
      VERBATIM
    )
  endif()

  # 崩溃符号：先把暂存后、剥离前的文件拷进 Symbols~/（与 Plugins/ 路径镜像），
  # 再按平台数据剥 Plugins/。两步必须拆开，才是同一次编译的两份。
  # 剥不剥、怎么剥、要不要 PDB，都是平台文件的数据（DCU_STRIP_DEBUG /
  # DCU_STRIP_ARGS / DCU_STAGE_PDB），不是这里的 if(android)。
  set(DCU_STAGED_PLUGIN "${DCU_PLUGIN_ROOT}/${DCU_PLUGIN_REL}/${DCU_ARTIFACT_NAME}")
  set(DCU_STAGE_SYMBOLS_ARGS
    --binary        "${DCU_STAGED_PLUGIN}"
    --symbols-root  "${DCU_SYMBOLS_ROOT}"
    --rel           "${DCU_PLUGIN_REL}"
    --artifact-name "${DCU_ARTIFACT_NAME}"
  )
  if(DCU_STAGE_PDB)
    list(APPEND DCU_STAGE_SYMBOLS_ARGS --pdb "$<TARGET_PDB_FILE:datachannel_unity>")
  endif()
  add_custom_command(TARGET datachannel_unity POST_BUILD
    COMMAND "${Python3_EXECUTABLE}"
            "${NATIVE_ROOT}/scripts/stage_symbols.py"
            ${DCU_STAGE_SYMBOLS_ARGS}
    COMMENT "Staging unstripped copy → Symbols~/${DCU_PLUGIN_REL}/${DCU_ARTIFACT_NAME}"
    VERBATIM
  )
  if(DCU_STRIP_DEBUG)
    add_custom_command(TARGET datachannel_unity POST_BUILD
      COMMAND "${CMAKE_STRIP}" ${DCU_STRIP_ARGS} "${DCU_STAGED_PLUGIN}"
      COMMENT "Stripping debug from staged plugin (Symbols~/ keeps the unstripped copy)"
      VERBATIM
    )
  endif()

  # 溯源信息：写进 Report~/，文件名由平台目录拍平而来（#65 推翻 #54 的「同目录」）。
  # 报 bug 时要能反查这份二进制的来历，靠的就是它 —— 但反查是维护者指路、对方照路径
  # 取文件，所以文件在不在 Unity 的资产树里无关紧要，README 里写着它在哪才要紧。
  # 刻意不做成 dcu_build_info() 导出：那会动 ABI，且让二进制内容每次构建都变，
  # 毁掉 #27 赖以验证 Meson→CMake 迁移的字节可复现性。
  #
  # 只传目录 rel，不传拼好的路径：拍平名由 gen_plugin_meta.report_name 一处算出，
  # 门禁（gen_support_table.py）算的是同一个函数。两边各拼一份就是在制造一个
  # 「门禁去找的文件」和「构建写出的文件」可以悄悄错开的缝。
  add_custom_command(TARGET datachannel_unity POST_BUILD
    COMMAND "${Python3_EXECUTABLE}"
            "${NATIVE_ROOT}/scripts/gen_build_info.py"
            --report-root "${DCU_REPORT_ROOT}"
            --rel         "${DCU_PLUGIN_REL}"
            --repo-root "${NATIVE_ROOT}/.."
            --header    "${DCU_SRC}/include/dcu.h"
            --lock      "${NATIVE_ROOT}/versions.lock"
            --platform  "${DCU_PLATFORM_KEY}"
            --architectures     "${CMAKE_OSX_ARCHITECTURES}"
            --target-system     "${CMAKE_SYSTEM_NAME}"
            --target-processor  "${CMAKE_SYSTEM_PROCESSOR}"
            --compiler-id       "${CMAKE_CXX_COMPILER_ID}"
            --compiler-version  "${CMAKE_CXX_COMPILER_VERSION}"
            --deployment-target "${CMAKE_OSX_DEPLOYMENT_TARGET}"
            --cmake-version     "${CMAKE_VERSION}"
    COMMENT "Writing provenance → Report~/"
    VERBATIM
  )

  # 离线门禁：暂存之后无条件跑，三个平台一视同仁。
  # 与暂存拆成两条命令，是因为门禁需要 CMake 才知道的工具路径；从 stage_plugin.py
  # 透传只会多一层。SPEC §9 写的就是「staging + audit 都是 POST_BUILD 自定义命令，
  # 中间没有 shell shim」。
  #
  # MSVC 分支的 CMake 不提供 NM，dumpbin 更是完全没建模（CMakeFindBinUtils 只给
  # LINKER/MT/AR）——所以传 CMAKE_LINKER 进去，让脚本从 link.exe 同目录找 dumpbin.exe。
  #
  # 页对齐断言是**声明式**的（决议 #81）：只有声明了 DCU_REQUIRE_PAGE_ALIGN 的平台
  # 才检查。桌面三份平台文件不声明，于是这个参数根本不出现在命令行上 —— 检查的
  # 存在与否，跟着「谁需要它」这个事实走，而不是 audit 里再写一遍平台判断。
  set(DCU_AUDIT_EXTRA_ARGS "")
  if(DEFINED DCU_REQUIRE_PAGE_ALIGN)
    list(APPEND DCU_AUDIT_EXTRA_ARGS --require-page-align "${DCU_REQUIRE_PAGE_ALIGN}")
  endif()

  add_custom_command(TARGET datachannel_unity POST_BUILD
    COMMAND "${Python3_EXECUTABLE}"
            "${NATIVE_ROOT}/scripts/audit_plugin.py"
            --binary   "${DCU_PLUGIN_ROOT}/${DCU_PLUGIN_REL}/${DCU_ARTIFACT_NAME}"
            --platform "${DCU_PLATFORM_KEY}"
            --expected "${DCU_EXPECTED_SYMBOLS}"
            --nm       "${CMAKE_NM}"
            --readelf  "${CMAKE_READELF}"
            --linker   "${CMAKE_LINKER}"
            ${DCU_AUDIT_EXTRA_ARGS}
    COMMENT "Auditing plugin (exports + dependencies)"
    VERBATIM
  )
endif()
