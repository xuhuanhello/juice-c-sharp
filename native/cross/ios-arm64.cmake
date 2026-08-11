# iOS arm64 的 CMake toolchain file（决议 #93）。
#
# 用法：
#   cmake -S native -B native/build/ios \
#         -DCMAKE_TOOLCHAIN_FILE=native/cross/ios-arm64.cmake \
#         -G Ninja -DCMAKE_BUILD_TYPE=Release
#
# **CMake 对 iOS 有一等支持**，不像 Android 那样需要 include 第三方 toolchain。
# 三个键在这里集中设好，CI YAML 里不用分散传参 —— 写错时文件不存在就是硬失败。
#
# 这份文件管**怎么交叉编译**；产物长什么样在 native/platforms/iOS.cmake。
# 两者职责不同，别合并。

set(CMAKE_SYSTEM_NAME iOS)

# device arm64 only（决议 #90 Notes §3：不出 Simulator 产物）
set(CMAKE_OSX_ARCHITECTURES "arm64" CACHE STRING "" FORCE)

# 12.0 是 Unity 2022.3 允许的最低 iOS 目标，实测 ld -r 在 iPhoneOS26.5 SDK 下接受。
# 与 ANDROID_PLATFORM=android-22 的原则相同：不多背任何采用者约束。
set(CMAKE_OSX_DEPLOYMENT_TARGET "12.0" CACHE STRING "" FORCE)
