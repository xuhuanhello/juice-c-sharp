# Android arm64-v8a 的 CMake toolchain file（决议 #79 §4）。
#
# 用法：
#   cmake -S native -B native/build/android \
#         -DCMAKE_TOOLCHAIN_FILE=native/cross/android-arm64.cmake \
#         -G Ninja -DCMAKE_BUILD_TYPE=Release
#
# **它是一层薄封装，必须 include NDK 自己那份**，不能自行 set(CMAKE_SYSTEM_NAME
# Android) 了事 —— 后者会掉进 CMake 内建的 Android 支持，而 NDK 官方原话是：
#
#   "CMake has its own built-in NDK support which has behavior differences
#    compared to the NDK's CMake toolchain file. Android does not support or
#    test the built-in workflow. We recommend using our toolchain file."
#
# （#48 录的原文。）
#
# 这份文件管**怎么交叉编译**；产物长什么样在 native/platforms/Android.cmake。
# 两者职责不同，别合并。

if(NOT ANDROID_NDK)
  foreach(_var ANDROID_NDK_ROOT ANDROID_NDK_HOME)
    if(DEFINED ENV{${_var}} AND NOT ANDROID_NDK)
      set(ANDROID_NDK "$ENV{${_var}}")
    endif()
  endforeach()
endif()

# 找不到就硬失败，不猜一个路径继续 —— CONTRIBUTING 的第一原则：
# 「Make absence a failure, not a silence.」
if(NOT ANDROID_NDK OR NOT EXISTS "${ANDROID_NDK}/build/cmake/android.toolchain.cmake")
  message(FATAL_ERROR
    "Cannot locate the Android NDK.\n"
    "  Looked at: -DANDROID_NDK=, then $ANDROID_NDK_ROOT, then $ANDROID_NDK_HOME\n"
    "  Got: '${ANDROID_NDK}'\n"
    "  Pass -DANDROID_NDK=/path/to/ndk, or export ANDROID_NDK_ROOT.\n"
    "  CI pins the r27 major version and picks the highest minor it finds (decision #79).")
endif()

set(ANDROID_ABI "arm64-v8a" CACHE STRING "" FORCE)

# `ANDROID_PLATFORM` **就是原生库的 minSdkVersion**（不是 targetSdkVersion —— 原生
# 侧没有那个概念）。它是**下界**，约束方向单一：
#
#     我们的 ANDROID_PLATFORM  ≤  采用者 App 的 minSdkVersion
#
# 编得比采用者高，`.so` 可能引用采用者最老设备上不存在的符号，dlopen 时
# `cannot locate symbol` 当场崩；编得低则永远安全（老符号一直在）。
#
# **22 是实测的结果，不是保守的默认值。**
#
# 决议 #80 先定 23，同时定下「先测再定」：Unity 2022.3 的默认 minSdk 是 22，比 23
# 低一档，于是采用者不改设置的默认配置就与我们的二进制不匹配 —— 而这个不匹配
# **要么完全无害，要么在最老的设备上 dlopen 崩**，分界线是「树里到底有没有东西
# 用到 API 23 才引入的符号」。
#
# 测法不是比对头文件，是**让链接器回答**：NDK 的 sysroot 按 API 级别分目录存 stub
# 库，降到 22 重链，只要有一个符号在 22 上不存在就会链接失败。#85 B3 实跑（PR #86，
# NDK 27.3.13750724）：**android-22 链接通过，导出集与依赖集不变**。
#
# 结论：没东西需要 23，那就不背这个与 Unity 默认值的不匹配 —— 按 #80 预先拍板的
# 两条分支取「调回 22」那条。`PluginPlatformGuard` 因此**不需要**加 minSdk 闸：
# 那道闸会拦一个不存在的故障，正是 CONTRIBUTING「before adding a defensive
# mechanism」点名的形状。
#
# 22 同时是 Unity 2022.3 允许的**最低** minSdk，所以这个值不挡任何一个 2022.3
# 能支持的采用者，也不多背一格没有消费者的兼容性（NDK r27 本身支持到 21，但
# Unity 2022.3 的 Player 不支持 21）。
set(ANDROID_PLATFORM "android-22" CACHE STRING "" FORCE)

# 静态 libc++：产物要自包含。符号可见性已是 hidden（CMakeLists 顶部），
# 所以静态 STL 不会把 libc++ 的符号泄漏到导出表。
set(ANDROID_STL "c++_static" CACHE STRING "" FORCE)

include("${ANDROID_NDK}/build/cmake/android.toolchain.cmake")
