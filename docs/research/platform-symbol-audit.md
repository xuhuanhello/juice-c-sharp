# Research: 七平台的符号可见性与 audit 工具（含静态库硬点）

**Ticket:** https://github.com/xuhuanhello/juice-c-sharp/issues/47
**Parent:** #46（平台产物补齐到桌面矩阵）｜ **决策票:** #50（grilling，取舍由人来定）
**Date:** 2026-08-04
**性质:** 只给事实与代价，不做取舍。凡本机能测的都实测了；测不了的（Windows / Linux / Android 工具链本机不存在）明确标为「文档来源」。

**本机工具链（所有实测数字的产生环境）**

| 项 | 值 |
|---|---|
| Xcode | 26.6（Build 17F113） |
| `ld` | `PROJECT:ld-1267`（新链接器） |
| clang | Apple clang 21.0.0 (clang-2100.1.1.101)，target `arm64-apple-darwin25.5.0` |
| SDK | macOS 26.5；iPhoneOS 26.5（iOS 复验用的是真的 `--sdk iphoneos`） |
| 本机**没有**的 | `dumpbin` / MSVC、`readelf` / binutils、Android NDK、`llvm-objcopy`、`llvm-readobj`（Unity 只装了 iOSSupport 模块） |

**被测产物（均为仓库当前构建的真实产物）**

| 产物 | 路径 | 说明 |
|---|---|---|
| macOS arm64 插件 | `Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle` | 2026-08-04 14:01 构建，`audit-macos-plugin.sh` 当场跑绿 |
| 六个上游静态归档 | `native/build/macos-arm64/**/*.a` | mbedtls ×3、libdatachannel、libjuice、usrsctp |
| dcu 目标文件 | `native/build/macos-arm64/CMakeFiles/datachannel_unity.dir/dcu/src/dcu_impl.cpp.o` | 用来拼 iOS 形态的归档 |

---

## Verdict（结论速览）

| 问题 | 事实 |
|---|---|
| 「静态库的全局符号会全部进入采用者的链接单元」是否属实 | **不属实（对本仓库当前配置而言）**。`native/CMakeLists.txt` 顶层的 `CMAKE_C/CXX_VISIBILITY_PRESET hidden` **确实**传播进了两个 vendored 子工程：六个归档共 4824 个已定义外部符号里，**4797 个是 `private external`（hidden）**，真正 external 的只有 **27** 个（26 个 libc++ 弱 vtable + 1 个 `_timingsafe_bcmp`）（§1） |
| 那还有没有 duplicate symbol 风险 | **有，而且 `-fvisibility=hidden` 挡不住。** 实测：两个归档都用 hidden 编译、都定义 `_mbedtls_ssl_setup`、且成员都被拉入 → `ld: 1 duplicate symbols`。`private external` 仍参与同一次链接的全局命名空间（§2.1） |
| 默认（惰性）链接会报错吗 | **不会 —— 这才是真正危险的形态。** 归档成员按需拉取，只有一份 mbedtls 被拉进来，链接通过，两个插件**静默共用一份实现**。我们的 mbedtls 开了 `MBEDTLS_SSL_DTLS_SRTP`，对方的多半没开（§2.2） |
| 什么时候会变成硬失败 | 任一侧被 `-force_load` / `-all_load` 强拉时。实测立刻报 `_psa_cipher_encrypt_setup` / `_mbedtls_aes_crypt_xts` 等重复。Unity trampoline 只在 **GameAssembly** target 上加 `-ObjC`，而 `-ObjC` 只强拉「实现了 ObjC 类/分类或 Swift 类型」的成员——我们的归档是纯 C/C++，**不会**被 `-ObjC` 强拉（§2.3） |
| 工具级收窄手段哪个真的有效 | **`ld -r`（部分链接）**：把所有 hidden 符号变成 `non-external`（真正的 local）。冲突面从 4854 个 private-external 塌缩到 0；再叠 **`nmedit -s`** 可把外部面精确收到 **20 个 `dcu_*`**（§3） |
| `ld -r -exported_symbols_list` 能用吗 | **能，但只能降级不能提升**（原写「不能」，见 §3.2 补注）。列已 `external` 的 `DCU_API` 组 → exit 0 且其余真本地化；列一个已 hidden 的符号 → `ld: cannot export hidden symbol`。约束：白名单须与 `DCU_API` 集合完全一致（§3.2） |
| `objcopy --localize-hidden` 呢 | **Apple 工具链里没有 `objcopy`/`llvm-objcopy`**（`xcrun --find` 失败）。它是 GNU/ELF 方向的工具，Apple 侧的对应物是 `nmedit`（§3.3） |
| `ld -r` 会牺牲什么 | 死代码剥离**不受影响**：`ld -r` 输出保留 `MH_SUBSECTIONS_VIA_SYMBOLS`（`otool -h` flags `0x2000`），实测 `-dead_strip` 后最终可执行文件字节数完全相同。真正的代价在别处（§3.5） |
| 参照实现怎么做的 | 五份实测。**没有一份用 `ld -r`+`nmedit`。** Unity 官方 WebRTC 用**动态 framework** 绕开整个问题；Unity 自己的 `libiPhone-lib.a` 用 hidden + **给第三方库加前缀改名**（`Unityplcrash*`、`Unityprotobuf*`），但把 FreeType（`FT_*`，42 个）大喇喇留在外面（§4） |
| 「没有禁用 crypto 动态依赖」在 `.a` 上怎么表达 | 静态库**没有依赖表**（无 `LC_LOAD_DYLIB` / 无 `DT_NEEDED`）。等价断言是**未定义符号集**：实测本仓库的 iOS 形态归档有 322 个未定义符号，全是 libc++/libSystem，**crypto 相关 0 个**（§5） |
| 一份 `expected-symbols.txt` 能不能三平台复用 | **形态上能。** 该文件本来就是**去装饰**的（`audit-macos-plugin.sh` 用 `sed 's/^_//'` 剥掉 Mach-O 前缀），Windows x64/arm64 的 C 符号本就无前导下划线，ELF 也无。差异只在**提取命令**，不在清单内容（§6） |
| `linux-version-script.map` 与 `windows-exports.def` 现状 | 都来自 **8247f64**（S1 首次提交，2026-08-03），此后**一字未改**。「从未被任何构建路径使用」这句话**需要修正**：`native/CMakeLists.txt` 的 `WIN32` / `else()` 分支**确实引用了它们**，只是那两个分支从没执行过。`.map` 是通配符不会过期；`.def` 是 S1 的 18 条硬清单，**4 条已删除的符号会让 MSVC 链接以 LNK2001 硬失败**，另有 6 条现存符号缺失（§7） |

---

## 1. 现状实测：当前构建到底泄漏了什么

### 1.1 已交付的 macOS arm64 插件

```
$ bash native/scripts/audit-macos-plugin.sh Packages/.../datachannel_unity.bundle
==> otool -L …
	@loader_path/datachannel_unity
	/System/Library/Frameworks/CoreFoundation.framework/…/CoreFoundation
	/System/Library/Frameworks/Security.framework/…/Security
	/usr/lib/libc++.1.dylib
	/usr/lib/libSystem.B.dylib
==> exported globals (nm -gU)
OK: 20 dcu_* exports match expected-symbols.txt, no forbidden crypto dylibs
```

**20 个导出**，SPEC §4 标题写的是 19 —— 差的是测试钩子 `dcu_test_set_open_race_delay_ms`，`expected-symbols.txt` 的注释里已写明这是有意的。本票说的「19」按 SPEC §4 的产品面理解；实际二进制与清单都是 20。

顺带一个可复用的小事实：`nm -u` 显示这个 dylib 的 321 个未定义符号里，**`CoreFoundation` / `Security` 的符号数是 0** —— `native/CMakeLists.txt` 里那两个 `-framework` 是空依赖，只是让 `otool -L` 的输出多两行。

### 1.2 六个上游静态归档的真实符号构成

| 归档 | 成员数 | `nm -gU` 已定义外部符号 |
|---|---|---|
| `libdatachannel-static.a` | 63 | 2674 |
| `libmbedcrypto.a` | 82 | 874 |
| `libmbedtls.a` | 19 | 304 |
| `libmbedx509.a` | 10 | 106 |
| `libjuice-static.a` | 22 | 257 |
| `libusrsctp.a` | 24 | 609 |
| **并集（去重）** | 220 | **4824** |

但 `nm -gU` 是**误导性的**：它把 Mach-O 的 `private external`（= `visibility=hidden`）也算作「外部」。用 `nm -m` 分类后：

| 类别 | 去重符号数 |
|---|---|
| `private external` / `weak private external`（hidden） | **4797** |
| `external` / `weak external`（真·全局） | **27** |

那 27 个是：**26 个 libc++ `std::__1::` 模板实例的弱 vtable**（`__ZTVNSt3__1…`，以 regex 内部类为主，weak，会合并，不冲突）+ **1 个 C 符号 `_timingsafe_bcmp`**。

抽样验证隐藏确实生效：

```
$ nm -m libmbedtls.a            | grep ' _mbedtls_ssl_setup$'
… (__TEXT,__text) private external _mbedtls_ssl_setup
$ nm -m libdatachannel-static.a | grep ' _rtcCreatePeerConnection$'
… (__TEXT,__text) private external _rtcCreatePeerConnection
$ nm -m libusrsctp.a            | grep ' _usrsctp_init$'
… (__TEXT,__text) private external _usrsctp_init
```

**结论：`docs/research/symbol-visibility.md`（#18）里「macOS arm64 dylib 泄漏 ~1551 个符号」的旧状态已经不成立** —— 那是 exports 允许列表与 hidden 预设落地之前的数字。

### 1.3 唯一逃逸的 C 符号：`_timingsafe_bcmp`，以及它为什么逃逸

`usrsctp` 在 `sctp_userspace.c:142` 为 Apple 平台自带一份 `timingsafe_bcmp` 实现。同一个 `.o` 里的兄弟符号全是 `private external`，只有它是 `external`。

最小复现（本机跑通）：

```c
// tsb.c —— 带系统头
#include <strings.h>
int timingsafe_bcmp(const void *a, const void *b, unsigned long n) { … }
int my_other_fn(void) { return 1; }
```
```
$ clang -c -fvisibility=hidden -o tsb.o tsb.c && nm -m tsb.o
… private external _my_other_fn
… external         _timingsafe_bcmp     ← 逃逸

# 去掉 #include <strings.h> 之后：
… private external _timingsafe_bcmp     ← 不再逃逸
```

原因是 clang 的「**系统头里声明的符号获得 default 可见性**」规则：macOS SDK 的 `usr/include/_string.h` 里确有 `int timingsafe_bcmp(const void *, const void *, size_t);`（本机 grep 确认），`-fvisibility=hidden` 不覆盖它。`-fno-builtin` 也不影响（实测仍为 external）。

这一个符号是**当前唯一**会与「另一个也打包了 usrsctp 的插件」正面撞名的 C 符号。

---

## 2. 硬点 (a)：duplicate symbol 到底会不会发生

### 2.1 `-fvisibility=hidden` 不能阻止重复符号

受控实验：两个「厂商」归档，各自把 `mbedtls_ssl_setup` 和自己的入口函数放在**同一个 `.o`** 里（所以两个成员必然都被拉入），app 同时调用两个入口。

| A 的可见性 | B 的可见性 | 额外链接参数 | 结果 |
|---|---|---|---|
| default | default | — | **duplicate symbol `_mbedtls_ssl_setup`** |
| hidden | default | — | **duplicate symbol** |
| **hidden** | **hidden** | — | **duplicate symbol** |
| hidden | hidden | `-Wl,-all_load` | **duplicate symbol** |
| default | default | `-Wl,-all_load` | **duplicate symbol** |

`private external` 在同一次链接里**仍然占据全局命名空间**。这是本票最反直觉的一条：`nm -gU` 看起来干净的归档，照样会撞。

### 2.2 惰性加载让它「不报错」，而那是更坏的结果

把 `mbedtls_ssl_setup` 放回**独立成员**（真实归档的形态），链接就通过了 —— 因为 ld 只按未定义符号惰性拉取成员，第二份定义所在的成员根本没被拉进来。

用**真实归档**复验（我们的 `libdcu_naive.a` + 一个也打包了 `libmbedcrypto.a` 的假想插件 `libvendorB.a`）：

```
$ clang -o app app.o libdcu_naive.a libvendorB.a -lc++ …
exit=0
copies of _mbedtls_sha256   : 1
copies of _mbedtls_ssl_setup: 1
```

**只有一份实现，两个插件共用。** 本仓库的 mbedtls 是带 `MBEDTLS_USER_CONFIG_FILE` → `MBEDTLS_SSL_DTLS_SRTP` 编译的（SPEC §3/§9）；如果被换成对方那份没开 DTLS-SRTP 的，失败会出现在运行期握手上，而不是链接期。

这一条对 #50 的取舍最要紧：**默认路径下这个问题不表现为链接错误，而表现为静默的实现替换。**

### 2.3 什么时候会变成硬失败

任一侧被强制全量加载时：

```
$ clang -o app app.o -Wl,-force_load,libdcu_naive.a -Wl,-force_load,libvendorB.a …
duplicate symbol '_psa_cipher_encrypt_setup' in: libvendorB.a[56](psa_crypto.c.o) / libdcu_naive.a[118](psa_crypto.c.o)
duplicate symbol '_mbedtls_aes_crypt_xts'    in: libvendorB.a[3](aes.c.o)        / libdcu_naive.a[65](aes.c.o)
duplicate symbol '_mbedtls_cipher_base_lookup_table' …
```

触发条件（`man ld` 原文）：

| 参数 | 原文 |
|---|---|
| `-all_load` | "Loads all members of static archive libraries." |
| `-force_load path_to_archive` | "Loads all members of the specified static archive library." |
| `-ObjC` | "Loads all members of static archive libraries that implement an Objective-C class, category or a Swift struct, class or an extension." |

**Unity 2022.3.62f3 的 iOS trampoline 实测**（`PlaybackEngines/iOSSupport/Trampoline/Unity-iPhone.xcodeproj/project.pbxproj`）：`OTHER_LDFLAGS = "-ObjC"` 出现在 4 个 build configuration 上，全部属于 **`GameAssembly`** 这个 `PBXNativeTarget`；`UnityFramework` target 用的是 `-weak_framework CoreMotion -weak-lSystem`。

因为 `-ObjC` 只强拉实现了 ObjC/Swift 类型的成员，而 `dcu` + mbedtls + libdatachannel + juice + usrsctp 里**一个 ObjC 类都没有**，所以 Unity 默认工程不会因为 `-ObjC` 把我们的归档全量拉入。风险来自**采用者自己**加 `-all_load` / `-force_load`（不少三方 iOS SDK 的接入文档要求这么做）。

---

## 3. 硬点 (b)：工具级收窄手段与各自的代价

### 3.1 `ld -r`（部分链接）—— 唯一把 hidden 真正变成 local 的手段

**一手依据（`man ld`，"Options when creating an object file"）：**

> `-keep_private_externs` — Don't turn private external (aka visibility=hidden) symbols into static symbols, but rather leave them as private external in the resulting object file.

也就是说：`ld -r` **默认就会**把 hidden 符号降级为 static/local，这个选项是用来**关掉**该行为的。实测吻合：

```
$ ld -r -o merged.o a.o
$ nm -m merged.o
… external                              _dcu_init            ← visibility("default") 保留
… non-external (was a private external) _mbedtls_ssl_setup   ← 被本地化
```

在真实的六归档 + `dcu_impl.o` 上跑（`ld -r` 对归档同样惰性拉取，顺带丢掉用不到的成员）：

| 产物 | 字节 | 外部(external) | 其中非 mangled C | 私有外部(hidden) | 已本地化 | 未定义 |
|---|---|---|---|---|---|---|
| **A** `libtool -static` 直接合并 | 5 090 760 | 48 | 21 | **4854** | 0 | 1913 |
| **B** `ld -r` 部分链接 | 3 376 256 | 48 | 21 | 10 | **8628** | 322 |
| **C** `ld -r` + `nmedit -s` | 3 391 272 | **20** | **20** | 10 | 8628 | 322 |
| **D** `nmedit` 直接作用于多成员归档（**错误做法**，见 §3.4） | 4 560 880 | 20 | 20 | 11 | 4843 | 1913 |

A、B 的 21 个 C 外部符号 = 20 个 `dcu_*` + `_timingsafe_bcmp`；另 27 个全是 `__ZTV` 开头的 libc++ 弱 vtable（六归档贡献 26 个，`dcu_impl.o` 贡献 1 个 `__shared_ptr_emplace<atomic<bool>>`）。C 把这 28 个非 `dcu_*` 的也一并收掉了。

端到端复验（真实归档 + 强制全量加载对方）：

```
$ clang -o app app.o libdcu_ldr_nmedit.a -Wl,-force_load,libvendorB.a -lc++ …
exit=0
app 3 031 800 bytes；dcu 导出 20 个；_mbedtls_sha256 本地副本 2 份；外部 mbedtls 符号 0 个
```

`-all_load` 全局开关下同样通过（A 形态则报重复）。

### 3.2 `ld -r -exported_symbols_list` —— 只能降级，不能提升（标题原为「用不了」，见下方补注）

```
$ ld -r -exported_symbols_list keep.txt -o merged.o a.o
ld: cannot export hidden symbol _dcu_init file 'a.o' for architecture arm64
```

`-exported_symbols_list` 的语义（`man ld`）是把**未列出的**全局符号当作 `__private_extern__`；它不能把一个已经 hidden 的符号**提升**回 export。在我们的场景里它也没必要 —— `DCU_API` 的 `visibility("default")` 已经把「谁是公开面」编码在源码里。反向的 `-unexported_symbols_list` / `-unexported_symbol` 可用：实测 `ld -r -unexported_symbol _timingsafe_bcmp` 能把那个逃逸符号一并本地化，同时 `_dcu_init` 保持 external。

> **补注（2026-08-04，起因是对照 [unity-sqlcipher-net-openharmony](https://github.com/xuhuanhello/unity-sqlcipher-net-openharmony) 的 `merge-static-libs.sh` 在产线上正是这么用的）**
>
> 上面的结论**对它测的那个输入成立，但标题「用不了」过头了**。分两种输入实测（Xcode 26.6 / ld-1267 / iPhoneOS26.5 SDK，`-fvisibility=hidden` 编译）：
>
> | 白名单内容 | 结果 |
> |---|---|
> | 只列**已经是 `external`** 的符号（打了 `DCU_API` 的那组） | **exit 0**；未列出的 `private external` 全部变 `non-external (was a private external)`，即真 local |
> | 列了一个**已 hidden** 的符号 | `ld: cannot export hidden symbol ...`，失败 —— 即本节原本记录的现象 |
>
> 也就是说 `-exported_symbols_list` **只能降级，不能提升**。**在我们的真实情形里它可用**，因为白名单就等于 `DCU_API` 那一组，本来就是 default，从不要求提升。
>
> **由此得到一条硬约束（且是好性质）**：白名单必须与源码中 `DCU_API` 标注的集合**完全一致**。多写一个没标注的名字，iOS 链接**直接红** —— 硬失败而非静默通过，正好与「一份权威符号清单」的决议（[#50](https://github.com/xuhuanhello/juice-c-sharp/issues/50)）互相加固。
>
> 本节其余内容（`-unexported_symbol` 可用、`objcopy` 在 Apple 侧不存在、`ld -r` 默认本地化）不受影响。

### 3.3 `objcopy --localize-hidden` —— Apple 侧不存在

```
$ xcrun --find llvm-objcopy
xcrun: error: unable to find utility "llvm-objcopy"
$ which objcopy
(无)
```

Xcode 26.6 工具链里有 `llvm-nm` / `llvm-objdump` / `nm` / `nmedit` / `strip` / `libtool` / `ld`，**没有** `objcopy`、`llvm-objcopy`、`llvm-readobj`、`readelf`。`--localize-hidden` 是 GNU binutils / ELF 方向的手段：Android / Linux 的 `.so` 用不上它（版本脚本更直接），iOS 的 `.a` 则要走 Apple 自己的 `nmedit`。

### 3.4 `nmedit -s` —— 精确收窄，但**顺序不能错**

`man nmedit`：

> Nmedit changes the global symbols not listed in the list_file file of the -s list_file option to static symbols. … Another example is an object that is made up of a number of other objects that will be loaded into an executable would built and then have its symbol table edited with:
> `% ld -o relocatable.o -r a.o b.o c.o`
> `% nmedit -s interface_symbols relocatable.o`

man 页给的就是 `ld -r` 之后再 `nmedit`。**反过来做会把归档打断** —— 实测把 `nmedit -s` 直接作用在多成员归档上（表里的 D），链接立刻炸：

```
Undefined symbols for architecture arm64:
  "rtc::InitLogger(rtc::LogLevel, std::__1::function<…>)", referenced from:
      _dcu_init in libdcu_nmedit.a[2](dcu_impl.o)
  "rtc::DataChannel::send(…)", referenced from: _dcu_dc_send …
```

因为跨成员的引用在 `nmedit` 之后失去了可解析的定义。

### 3.5 各手段的代价清单（给 #50 用）

| 代价项 | 事实 |
|---|---|
| **死代码剥离粒度** | **不受损**。`ld -r` 输出保留 `MH_SUBSECTIONS_VIA_SYMBOLS`（`otool -h` flags = `0x2000`），最终链接器仍能按函数剥离。玩具工程实测：A 与 B 两种归档在 `-dead_strip` 下产出**完全相同的字节数**（261 656） |
| **归档体积** | A 5.09 MB → B 3.38 MB → C 3.39 MB。`ld -r` 反而**变小**，因为惰性拉取丢掉了用不到的成员（216 个成员 → 1 个对象） |
| **调试符号** | `nmedit` 会同步改调试信息（man 页明说这是它与 `strip` 的区别）；但 `ld -r` 之后归档只剩一个巨大 `.o`，崩溃栈里的「哪个成员」信息变模糊 |
| **libc++ 弱 vtable 被本地化（仅 C 方案）** | C 方案把 27 个 `std::__1::` 弱 vtable 也变成 local。它们本是 weak/COMDAT，本来就会合并；本地化后 app 里多一份副本。只要不跨 ABI 边界传 C++ 类型 / 抛 C++ 异常（dcu 层本来就全 catch 住），语义无影响 —— 但这是个**需要有人记住的前提**，不是结构性保证 |
| **构建步骤** | 从「`libtool -static` 一步」变成「`ld -r` →（`nmedit -s`）→ `libtool -static`」三步且必须保序；`nmedit` 的 keep-list 又是**第二份**符号清单，与 `expected-symbols.txt` 存在漂移可能 |
| **`ld -r` 的平台参数** | 跨平台调用需要显式 `-arch` / `-platform_version`（iOS 复验时必须给），不给会用 host 默认值 |
| **仍不解决的** | 采用者装了**同一个** datachannel-unity 的两个版本；以及 `dcu_*` 自己撞名（前缀独占，实际不可能） |

### 3.6 iOS 真机 SDK 复验

上述全部结论在 `xcrun --sdk iphoneos clang -target arm64-apple-ios13.0` 下重跑一遍，结论一致：

```
=== iOS: naive archive, both members forced in ===
duplicate symbol '_mbedtls_ssl_setup' in: libB.a[2](b.o) / libA_naive.a[2](a.o)

=== iOS: ld -r + nmedit ===
… external                              _dcu_init
… non-external (was a private external) _mbedtls_ssl_setup
exit=0   app_part: Mach-O 64-bit executable arm64
```

没有任何 macOS 专属行为 —— 同一个 `ld64`、同一个 Mach-O 格式。

---

## 4. 硬点 (c)：五份参照实现实测

> 本节按 CONTRIBUTING 的「先数实现再断言上游有 bug」办。**结论是：这个问题真实存在且可复现，但只有一份实现为它做了专门处理，一份靠换产物形态绕开，其余三份没有处理也没有出事。**

### 4.1 `com.unity.webrtc`（Unity 官方）—— 换产物形态绕开

本机两个版本 + 官方 tgz 都实测过：

| 版本 | iOS 产物 | 文件类型 | 导出数（`nm -gU`） | 泄漏 BoringSSL/OpenSSL 符号 |
|---|---|---|---|---|
| 2.4.0-exp.11 | `Runtime/Plugins/iOS/webrtc.framework/webrtc`（77 068 184 B） | **Mach-O DYLIB arm64** | 483 | **0** |
| 3.0.0（官方 tgz） | 同上（8 554 856 B） | **Mach-O DYLIB arm64** | 172 | **0** |

`.meta` 的 iOS 段：

```yaml
      iPhone: iOS
    second:
      enabled: 1
      settings:
        AddToEmbeddedBinaries: true
        CPU: AnyCPU
        FrameworkDependencies: CoreFoundation;CoreAudio;Metal;
```

C# 侧仍然是 `#if UNITY_IOS internal const string Lib = "__Internal";`（`Runtime/Scripts/WebRTC.cs:636`）—— 动态 framework 被链进 app，`__Internal` 在链接期即可解析。

构建脚本（`gh api repos/Unity-Technologies/com.unity.webrtc/contents/BuildScripts~/build_plugin_ios.sh`）确认这是**刻意的产物形态**：`xcodebuild archive -scheme WebRTCPlugin` 产出 `@rpath/webrtc.framework` 后直接拷进包里。

**Unity 官方的 WebRTC 包在 iOS 上根本没有 `.a`，所以它不需要回答本票的问题。** 这是一条 SPEC §8 目前锁死为 `.a` 的替代路径；可行性与代价归 #50。

### 4.2 Unity 自己的 `libiPhone-lib.a` —— hidden + **给第三方库改名**

`/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/iOSSupport/Trampoline/Libraries/libiPhone-lib.a`（206 093 248 B，1635 个成员）：

| 类别 | 符号表条目数 |
|---|---|
| `non-external` | 91 704 |
| `private external` | 71 142 |
| `weak private external` | 54 839 |
| **`external`** | **534** |
| **`weak external`** | **95** |

去重后真·外部符号 **614** 个（其中 507 个是非 mangled 的 C 名）。按前缀：

| 前缀 | 个数 | 备注 |
|---|---|---|
| `UNITY*` / `Unity*` | 175+ | 自己的 ABI |
| **`Unityplcrash*`** | **157** | PLCrashReporter，**加了 `Unity` 前缀** |
| `plcrash*`（未加前缀） | 6 | 漏网 |
| **`Unityprotobuf*`** | **19** | protobuf，**加了 `Unity` 前缀** |
| `OBJC*` | 54 | ObjC 运行时元数据 |
| **`FT_*`** | **42** | **FreeType，没加前缀** |
| `LZ4_decompress_safe_partial_usingDict` | 1 | 没加前缀 |

crypto / usrsctp / juice / srtp 符号 **0 个** —— 与我们的集合不相交，**Unity 引擎本身不会跟我们撞**。

两个可直接用于 #50 的读法：

1. **一份非常成熟的 Unity 官方 iOS 静态库，确实把符号冲突当成真问题**，但它的手段是**前缀改名**，不是 `ld -r` 本地化。
2. 它**没有**做本地化：还有 125 981 条 private-external 条目留在归档里 —— 按 §2.1 的实测，这些照样会与另一份同名定义撞。也就是说 Unity 接受了这个残余风险，并且把 FreeType 这种最容易撞的 C API 留在了外面。

### 4.3 `Cysharp/YetAnotherHttpHandler` —— 真的发 iOS `.a`，且几乎不做处理

`src/YetAnotherHttpHandler/Plugins/…/runtimes/ios-arm64/native/libCysharp.Net.Http.YetAnotherHttpHandler.Native.a`（26 074 640 B，627 个成员，Rust + hyper + rustls + ring）：

| 类别 | 条目数 |
|---|---|
| `non-external` | 17 487 |
| `private external` | 7 191 |
| `weak private external` | 185 |
| **`external`** | **4 888** |
| `weak external` | 2 |

去重后真·外部符号 **4890** 个 —— 比我们的 48 个高两个数量级。**但这不是「他们有 bug」**：其中 4841 个是 Rust mangling（`__ZN…17h<16 位 hash>E`），hash 里编码了 crate 名与版本，天然自带命名空间；纯 C 名只有 **49** 个，其中 46 个是他们自己的 `yaha_*` ABI，另 3 个是工具链符号（`___isOSVersionAtLeast` 等）。`ring` 的 C/汇编 crypto 层泄漏 **0** 个外部符号。

**这一条修正了本票问题的规模判断**：静态库符号冲突是**扁平、无版本 hash 的 C 名**（`mbedtls_*`、`usrsctp_*`、`FT_*`）特有的问题，Rust / C++ mangling 基本自愈。我们的暴露面恰恰是最坏的那一类。

### 4.4 `com.unity.burst` 的 iOS/macOS RTL

`Library/PackageCache/com.unity.burst@1.8.21/.Runtime/libs/burstRTL_m_arm64.a`：20 个成员，**23 个 external，0 个 private external** —— 全是 libc 数学/内存函数（`_memcpy`、`_floorf`、`_setjmp` …）。规模太小，参考价值有限；它说明 Unity 自己发 `.a` 时也没有统一策略。

### 4.5 `Walkerdine/dc-unity`（同上游的另一份 Unity 绑定）

`CMakeLists.txt` 全文读过：只有 `SHARED`（非 Emscripten）与 `STATIC`（Emscripten）两个分支，**没有 iOS 目标**，也**没有任何可见性 / 导出控制**。仓库已有的 `docs/research/dc-unity-autopsy.md` 已定性；此处只补一条：它对本票的问题没有可参考的答案。

### 4.6 五份的横向表

| 实现 | iOS 产物 | 对第三方符号做了什么 | 残余暴露面 |
|---|---|---|---|
| com.unity.webrtc | **动态 framework** | 换形态绕开：dylib 导出表天然只放 172 个 | 0 crypto |
| Unity `libiPhone-lib.a` | 静态 `.a` | hidden + **前缀改名**（PLCrash / protobuf） | 614 外部，含 42 个 `FT_*` |
| YetAnotherHttpHandler | 静态 `.a` | 什么都没做（Rust mangling 自带命名空间） | 4890 外部，纯 C 名 49 个 |
| com.unity.burst | 静态 `.a` | 无（本来就没有第三方 C 库） | 23 外部 |
| Walkerdine/dc-unity | 无 iOS | 无 | — |
| **本仓库当前配置** | 尚未产出（见 §7.4） | hidden 预设已生效 | **48 外部，纯 C 名 21 个** |

---

## 5. 「无禁用 crypto 动态依赖」这条在静态库上怎么表达

SPEC §3 的原文是 `otool -L` / `ldd` / `dumpbin` 不得列出 openssl/mbedtls。**静态库根本没有依赖表** —— `.a` 是 `ar` 归档，成员是 `MH_OBJECT`，既没有 `LC_LOAD_DYLIB` 也没有 `DT_NEEDED`。iOS 与 WebGL 这两个平台上，这条断言在字面上无法执行。

**等价断言是「未定义符号集」**：一个自足的静态库，其未定义符号必须全部落在「宿主必然提供的系统库」里；任何 `SSL_*` / `EVP_*` / `mbedtls_*` 形态的未定义符号都意味着它需要一份外部 crypto。

本仓库的 iOS 形态归档实测：

```
undefined 符号总数        : 322
  C++ (libc++/libc++abi)  : 192
  libSystem / libc        : 130
  CoreFoundation/Security : 0
  crypto（SSL|EVP|BIO|CRYPTO|OPENSSL|ERR|RAND|X509|mbedtls|psa）: 0
```

完整的非 C++ 未定义列表全是 `_malloc` / `_socket` / `_getaddrinfo` / `_arc4random_buf` / `___stack_chk_fail` 这一类。

**提取命令**（可直接写进 audit 脚本）：

```bash
# 静态库的「依赖审计」= 未定义符号审计
nm -m "$A" | grep '(undefined)' | awk '{print $NF}' | sort -u \
  | grep -iE '^_(SSL|EVP|BIO|CRYPTO|OPENSSL|ERR|RAND|X509|mbedtls|psa)_' && exit 1
```

顺带一条对 Linux/Android 同样重要的事实：**`ldd` 不能跨架构用** —— 它实际是运行动态加载器。在 x86 runner 上审计交叉编译出的 arm64 Android `.so`，能用的是读 `.dynamic` 段的工具（`readelf -d` / `llvm-readelf --needed-libs` / `objdump -p`），不是 `ldd`。`otool -L` 则**可以**跨架构读 Mach-O 的 load command，没有这个限制。

---

## 6. 七平台的 audit 工具与符号装饰

### 6.1 装饰差异（一手来源）

Microsoft Learn《Decorated names》原文：

> The default calling convention is `__cdecl`. **In a 64-bit environment, C or `extern "C"` functions are only decorated when using the `__vectorcall` calling convention.**
> …For ARM64EC functions with C linkage …, a `#` is prepended to the decorated name.

| 目标 | C 符号形态 | 说明 |
|---|---|---|
| Windows **x64** | `dcu_init` | 无前导下划线（x86 的 `__cdecl` 才有） |
| Windows **ARM64** | `dcu_init` | 同上。**ARM64EC 是另一回事**（前缀 `#`），Unity 2022.3 的 Windows ARM64 播放器用的是普通 ARM64 |
| macOS / iOS（Mach-O） | `_dcu_init` | 前导下划线是 ABI 的一部分 |
| Linux / Android（ELF） | `dcu_init` | 无 |
| WebGL（Emscripten/wasm） | `_dcu_init` 形态出现在 `EXPORTED_FUNCTIONS` 里 | 与 Mach-O 巧合同形，机制不同 |

### 6.2 `expected-symbols.txt` 已经是去装饰形态

`native/scripts/audit-macos-plugin.sh` 里：

```bash
EXPORTS=$(nm -gU "$BIN" | awk '{print $3}' | sed 's/^_//' || true)
```

清单侧则是 `sed -e 's/#.*//' -e 's/[[:space:]]//g'`。也就是说 **`expected-symbols.txt` 本来就存的是无前缀名**，不是「从 macOS `nm` 抄下来的形态」—— 那句描述适用于 `macos-exported-symbols.txt`（内容是 `_dcu_*`），不适用于 `expected-symbols.txt`。

三个动态平台的**清单内容可以完全一致**，差异只在提取命令：

| 平台 | 产物 | 导出面提取 | 依赖面提取 |
|---|---|---|---|
| Windows x64 / ARM64 | `.dll` | `dumpbin /EXPORTS` 的名字列（无下划线，可直接比） | `dumpbin /DEPENDENTS` |
| macOS x64 / arm64 | `.bundle` 内的 Mach-O | `nm -gU \| awk '{print $3}' \| sed 's/^_//'`（现状） | `otool -L` |
| Linux（如果做） | `.so` | `nm -D --defined-only` / `readelf --dyn-syms` 过滤 `GLOBAL` + 非 `UND` | `readelf -d \| grep NEEDED` |
| Android arm64 | `.so` | 同上，用 NDK 的 `llvm-nm` / `llvm-readelf`（**不能**用 host `nm`） | 同上，**不能**用 `ldd` |
| iOS arm64 | `.a` | **没有导出表**：`nm -m` 分类，断言「`external` 集 == 清单」（§3 的 C 方案下正好 20 个） | **没有依赖表**：断言未定义符号集（§5） |
| WebGL | `.a` + `.jslib` | Emscripten `EXPORTED_FUNCTIONS`；`.a` 是 wasm 对象，Mach-O/ELF 工具一律不适用 | 同上，另有 `.jslib` 侧的 JS 依赖 |

### 6.3 尚未验证、必须在首次构建时测而不是预测的点

- **ELF 链接器自造符号**：`__bss_start` / `_edata` / `_end` 在 `local: *;` 下是否一定不进 `.dynsym`，bfd / gold / lld 表现可能不同。本机无 ELF 工具链，无法实测。
- **Android**：NDK 的 lld 对 `--version-script` 与 `--exclude-libs,ALL` 的支持。GNU ld 文档说 `--exclude-libs`「available only for ELF and PE targeted ports」，ELF 下「symbols affected by this option will be treated as hidden」。
- **WebGL**：`.a` 是 wasm 目标文件，本文所有 Mach-O 结论**一律不适用**；SPEC §8 的 WebGL facade 本身还没规格化，其 `dcu_*` 集合是否与原生一致也未定。
- **`dcu_test_set_open_race_delay_ms`**：随产品出货是 SPEC §11 的有意决定，但它在 iOS `.a` 的 keep-list 里保不保留、WebGL 侧存不存在，是 #50 的取舍点。

---

## 7. 既有资产核对：两个从未跑过的导出文件

### 7.1 来历

```
$ git log --follow --format='%h %ad %s' --date=short -- native/exports/linux-version-script.map
8247f64 2026-08-03 feat(native): dcu stable C ABI over libdatachannel, CMake build
$ git log --follow --format='%h %ad %s' --date=short -- native/exports/windows-exports.def
8247f64 2026-08-03 feat(native): dcu stable C ABI over libdatachannel, CMake build
```

两个文件都在 **S1 首次提交**（`8247f64`）落地，此后**一次都没改过**。同期的 `expected-symbols.txt` 则跟着 S2/S3/S4/S5 改了四次（`64beed0`、`9af4f43`、`a783cc8`、`241eea4`）。

### 7.2 「从未被任何构建路径使用」需要修正

`native/CMakeLists.txt` 里它们是**被引用的**：

```cmake
elseif(WIN32)
  target_link_options(datachannel_unity PRIVATE "/DEF:${EXPORTS_DIR}/windows-exports.def")
else()
  target_link_options(datachannel_unity PRIVATE
    "LINKER:--version-script=${EXPORTS_DIR}/linux-version-script.map"
    "LINKER:--exclude-libs,ALL")
endif()
```

准确的说法是：**代码路径存在，只是从没执行过**（本仓库只在 macOS 上构建过）。它们不是死文件，是「第一次跑 Windows/Linux job 就会立刻生效」的活文件 —— 这让 §7.3 的漂移更要紧，而不是更不要紧。

### 7.3 内容与当前 ABI 的逐条比对

`linux-version-script.map`：

```
{ global: dcu_*; local: *; };
```

**通配符，与 20 个符号一致，且永远不会过期。** 代价是它**无法逐符号把关**：假如某天多出一个 `dcu_oops`，版本脚本会照放，只有 `expected-symbols.txt` + audit 脚本能拦住。`macos-exported-symbols.txt`（`_dcu_*`）是同一形态、同一性质。

`windows-exports.def`：18 条，是 **S1 时代的 ABI**。机器算出的差集：

| 类别 | 符号 |
|---|---|
| **`.def` 里有、当前 ABI 已删除**（4 条） | `dcu_event_peek`、`dcu_event_copy_payload`、`dcu_event_copy_payload2`、`dcu_event_pop` |
| **当前 ABI 有、`.def` 缺失**（6 条） | `dcu_log_next`、`dcu_dc_receive`、`dcu_dc_state`、`dcu_event_next`、`dcu_event_queue_depth`、`dcu_test_set_open_race_delay_ms` |

两半的后果**完全不同**：

- **前 4 条会硬失败。** Microsoft Learn《LNK2001》原文：「**Exported .def file symbol issues** — This error occurs when an export listed in a .def file isn't found. It could be because the export doesn't exist, is spelled incorrectly, or uses C++ decorated names.」→ Windows job 第一次跑就会红，而且红在正确的地方。这符合 CONTRIBUTING 的「让缺席变成失败」。
- **后 6 条会静默通过。** `dcu.h` 的 `DCU_API` 在 `_WIN32` + `DCU_BUILD` 下展开为 `__declspec(dllexport)`，而《EXPORTS》文档写明四种导出方式「**All four methods can be used in the same program**」—— 也就是说 `.def` 在这里**不是限制性白名单，而是叠加项**：即使不在 `.def` 里，`dllexport` 过的 `dcu_*` 照样进导出表。

  推论（对 #50 直接有用）：**Windows 侧真正的导出闸门是源码里的 `DCU_API`，不是 `.def`。** `.def` 唯一的额外价值是「列了但不存在 → LNK2001」这一侧的过期检测；它拦不住「多导出了什么」。拦「多导出」的仍然只有 `dumpbin /EXPORTS` + `expected-symbols.txt` 的逐行 diff（外加保持 `CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS` 为 OFF —— 现状是没设置，CMake 默认即 OFF）。

### 7.4 顺带发现（属于 #55/#56，不属于本票，但会挡住它们）

- **iOS 目前根本产不出产物。** `native/CMakeLists.txt` 里 `add_library(datachannel_unity SHARED …)` 是硬编码的；且 staging 分支按 `APPLE` 判断，`CMAKE_SYSTEM_NAME=iOS` 时 `APPLE` 为真，会去做 `.bundle`。要产出 SPEC §8 要求的 `libdatachannel_unity.a`，构建系统本身要先改。
- `native/CMakeLists.txt` 的 `PREFIX "lib"` 是无条件的，Windows 上会得到 `libdatachannel_unity.dll`；不过 `stage_plugin.py` 在拷贝时改名为 `datachannel_unity.dll`，所以**不构成 bug**，只是 CMake 产物名与暂存名不一致，读代码时容易误判。
- `.def` 的 `LIBRARY datachannel_unity` 与上一条无关（`LIBRARY` 不决定 `/OUT`），但会写进 PE 导出目录的内部名。

---

## 8. 复现方式

所有实测都在 `/tmp/dcu-symtest/` 下用普通命令跑的，关键几步：

```bash
# 0. 素材（六个归档 + dcu 目标文件）
cp native/build/macos-arm64/libdatachannel/libdatachannel-static.a          /tmp/dcu-symtest/
cp native/build/macos-arm64/mbedtls/library/libmbed{crypto,x509,tls}.a      /tmp/dcu-symtest/
cp native/build/macos-arm64/libdatachannel/deps/libjuice/libjuice-static.a  /tmp/dcu-symtest/
cp native/build/macos-arm64/libdatachannel/deps/usrsctp/usrsctplib/libusrsctp.a /tmp/dcu-symtest/
cp native/build/macos-arm64/CMakeFiles/datachannel_unity.dir/dcu/src/dcu_impl.cpp.o \
   /tmp/dcu-symtest/dcu_impl.o

# 1. 分类符号（关键：nm -gU 会把 hidden 也算作外部，必须用 nm -m）
nm -m X.a | grep -vE '\(undefined\)|\(common\)' | grep -E '\) (external|weak external)' \
          | awk '{print $NF}' | sort -u

# 2. 三种 iOS 形态归档
LIBS="libdatachannel-static.a libmbedcrypto.a libmbedx509.a libmbedtls.a libjuice-static.a libusrsctp.a"
libtool -static -o libdcu_naive.a dcu_impl.o $LIBS                          # A
ld -r -o all.o dcu_impl.o $LIBS && libtool -static -o libdcu_partial.a all.o # B
cp all.o all_nm.o && nmedit -s keep20.txt all_nm.o \
  && libtool -static -o libdcu_ldr_nmedit.a all_nm.o                        # C

# 3. 双插件冲突复现（libvendorB.a = 一个也打包了 libmbedcrypto.a 的假想插件）
clang -o app app.o libdcu_naive.a      -Wl,-force_load,libvendorB.a -lc++ …  # 报重复
clang -o app app.o libdcu_ldr_nmedit.a -Wl,-force_load,libvendorB.a -lc++ …  # 通过
```

---

## 9. 一手来源清单

| 来源 | 用到的事实 |
|---|---|
| `man ld`（Xcode 26.6，ld-1267） | `-keep_private_externs`（反证 `ld -r` 默认本地化）、`-all_load` / `-force_load` / `-ObjC` / `-load_hidden` / `-hidden-lx` / `-exported_symbols_list` / `-unexported_symbols_list` 的原文 |
| `man nmedit` | `-s list_file` 语义，以及 `ld -r` → `nmedit` 的官方顺序 |
| Apple SDK `usr/include/_string.h` | `timingsafe_bcmp` 的系统声明（解释可见性逃逸） |
| [MS Learn — Decorated names](https://learn.microsoft.com/en-us/cpp/build/reference/decorated-names?view=msvc-170) | 「In a 64-bit environment, C or `extern "C"` functions are only decorated when using `__vectorcall`」；ARM64EC 的 `#` 前缀 |
| [MS Learn — EXPORTS](https://learn.microsoft.com/en-us/cpp/build/reference/exports?view=msvc-170) | 四种导出方式「All four methods can be used in the same program」 |
| [MS Learn — LNK2001](https://learn.microsoft.com/en-us/cpp/error-messages/tool-errors/linker-tools-error-lnk2001?view=msvc-170) | 「Exported .def file symbol issues — This error occurs when an export listed in a .def file isn't found.」 |
| [MS Learn — DUMPBIN options](https://learn.microsoft.com/en-us/cpp/build/reference/dumpbin-options?view=msvc-170) | `/EXPORTS`、`/DEPENDENTS`、`/SYMBOLS`、`/IMPORTS` 的存在与适用面 |
| [GNU ld — Options](https://sourceware.org/binutils/docs/ld/Options.html) | `--exclude-libs`「available only for ELF and PE targeted ports」/ ELF 下「treated as hidden」；`-Bsymbolic` |
| [Unity 2022.3 — Plugin Inspector](https://docs.unity3d.com/2022.3/Documentation/Manual/PluginInspector.html) | iOS 平台设置四项；`Add to Embedded Binaries` =「Unity sets the Xcode project options to copy the plug-in file into the final application package」，推荐用于「dynamically loaded libraries, bundles and frameworks」 |
| [Unity 2022.3 — Create a native plug-in for iOS](https://docs.unity3d.com/2022.3/Documentation/Manual/ios-native-plugin-create.html) | `DllImport("__Internal")`；C++/ObjC++ 需 `extern "C"` |
| `Unity-Technologies/com.unity.webrtc` `BuildScripts~/build_plugin_ios.sh`（gh api 读原文） | iOS 产物是 `xcodebuild archive` 出的 `webrtc.framework` |
| 本机 `com.unity.webrtc` 2.4.0-exp.11 / 3.0.0 二进制 | DYLIB 形态、483 / 172 个导出、0 crypto、`AddToEmbeddedBinaries: true` |
| 本机 Unity 2022.3.62f3 iOSSupport | `libiPhone-lib.a` 的符号分布与前缀改名证据；trampoline `project.pbxproj` 里 `-ObjC` 的归属 target |
| `Cysharp/YetAnotherHttpHandler`（下载真实 `.a` 后实测） | 26 MB iOS `.a`，4890 外部符号，纯 C 名 49 个 |
| `Walkerdine/dc-unity` `CMakeLists.txt`（gh api 读原文） | 无 iOS 目标、无可见性控制 |
| 仓库内 | `native/CMakeLists.txt`、`native/scripts/audit-macos-plugin.sh`、`native/scripts/stage_plugin.py`、`native/exports/*`、`docs/SPEC.md` §3/§4/§8/§9/§11、`CONTRIBUTING.md`、`docs/research/symbol-visibility.md`（#18） |

---

## Document control

| 项 | 值 |
|---|---|
| Path | `docs/research/platform-symbol-audit.md` |
| Issue | https://github.com/xuhuanhello/juice-c-sharp/issues/47 |
| Parent | https://github.com/xuhuanhello/juice-c-sharp/issues/46 |
| 下游取舍票 | https://github.com/xuhuanhello/juice-c-sharp/issues/50 |
| Research date | 2026-08-04 |
| Kind | Research only —— 不做取舍，不改构建 |
| 前置文档 | `docs/research/symbol-visibility.md`（#18）。本文修正了它关于「dylib 泄漏 ~1551 符号」的过期现状，并把它对 iOS 部分链接的**推测**换成了实测 |
