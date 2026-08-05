# Research: CI runner 与交叉编译工具链的现实约束

**Ticket:** https://github.com/xuhuanhello/juice-c-sharp/issues/48
**Parent:** #46（Map: 平台产物补齐到桌面矩阵）
**Date:** 2026-08-04
**范围:** 七个平台（Win x64 / Win arm64 / mac x64 / mac arm64 / Linux x64 / Android arm64-v8a / iOS arm64）在 GitHub Actions 上的 runner、原生 vs 交叉、工具链来源与已知坑。
**不在范围:** 切分决策（原生还是交叉、用哪个 runner）—— 那是 #51 的事。本文只给事实与代价。

**方法:** 只查一手来源 —— `actions/runner-images` 仓库的镜像 README 与安装脚本、GitHub Actions 官方文档、Unity 2022.3 官方文档、Android NDK / CMake / MSVC 官方文档、上游 libdatachannel / MbedTLS / libjuice / msys2-runtime 的实际源码，以及本仓库 `native/` 下脚本的实际内容。**本机能验的都本机验了**（见附录 B）。每条结论标出处；推断与实测分别标注。

---

## 0. 结论速览

| 平台 | 可用 runner（2026-08 实况） | 原生/交叉 | 首要坑（详见对应章节） |
|------|------------------------------|-----------|------------------------|
| macOS arm64 | `macos-latest` = **macOS 26 arm64**（不再是 macos-14/15） | 原生 | 唯一已打通的一条。Xcode/SDK 会随 `-latest` 漂移 |
| macOS x64 | **`macos-13` 标签已不存在**；现为 `macos-15-intel` / `macos-26-intel`（标准 runner，公开仓库免费） | 二选一 | 也可在 arm64 runner 上 `-DCMAKE_OSX_ARCHITECTURES=x86_64`（§9） |
| Windows x64 | `windows-latest` = Windows Server 2025 + VS 2022 Ent 17.14 | 原生 | **CRLF**（§2.2）、**可执行位断言恒真**（§3）、`windows-exports.def` 已失效（§4.4）、`dumpbin` 不在 PATH（§4.3） |
| Windows arm64 | `windows-11-arm` 已是**标准 runner，公开仓库免费无限** | 原生可选 | **Unity 2022.3 根本没有 Windows Arm player**（§5.1）；若走交叉，`-A ARM64` 会把产物暂存到 `Windows/x86_64`（§5.2） |
| Linux x64 | `ubuntu-latest` = 24.04（glibc **2.39**） | 原生 | Unity 2022.3 声明支持 Ubuntu 20.04/18.04/CentOS 7；连 `ubuntu-22.04`（glibc 2.35）都比 20.04 新（§8） |
| Android arm64-v8a | `ubuntu-latest`，NDK **27.3.13750724** 预装 | 交叉 | Unity 2022.3 用的是 **r23b**；16 KB page 对齐 r27 需显式 flags；`getifaddrs` 要 API ≥ 24（§6） |
| iOS arm64 | `macos-latest`（Xcode 26.6，iOS SDK 26.x） | 交叉 | 现有 CMakeLists 在 iOS 下会走 **APPLE→`.bundle`** 分支并产出 SHARED 库，两处都不对（§7.3） |

**跨平台的三条硬事实：**

1. 骨架注释里的五条预设，**两条已经不成立**（`macos-13` 标签消失、Win arm64 已有原生 runner），一条成立但不完整（`dumpbin` 存在却不在 PATH），两条成立（Android NDK toolchain file、iOS `nm -gU`）。见 §1。
2. Windows 上那条「断言脚本可执行位」的检查，**两种写法都违反 CONTRIBUTING 的第一原则**：不写 `shell: bash` 则恒假（PowerShell 语法错，永远红），写了则恒真（永远绿）。查到底的证据链在 §3。
3. Windows job 在跑到构建之前就会被两件事挡住：checkout 出来的 `.sh` / `versions.lock` 是 **CRLF**（§2.2），以及 `native/exports/windows-exports.def` 与当前 ABI **已经对不上**（§4.4）。这两条都与 runner 选型无关。

---

## 1. 骨架里的五条预设，逐条核实

`.github/workflows/plugins-matrix.yml:61-66` 的注释骨架：

| 骨架预设 | 核实结果 | 出处 |
|----------|----------|------|
| `macos-x64: runs-on: macos-13` | **不成立。** runner-images 的标签表里 macOS 最老的是 macos-14（且已标 deprecated）。x64 现在的标签是 `macos-15-intel` / `macos-26-intel`（或 `-large`） | runner-images README「Available Runner Image Labels」 |
| `windows-x64: runs-on: windows-latest —— audit 换 dumpbin /exports` | **半成立。** `windows-latest` = Windows Server 2025，VS 2022 Enterprise 17.14 已装 `VC.Tools.x86.x64`，`dumpbin.exe` 随之存在；但它**不在 PATH**，微软文档明说「You can't start it from a system command prompt unless you set the environment correctly」 | Windows2025-Readme.md；MSVC DUMPBIN Reference |
| `windows-arm64: runs-on: windows-latest —— 交叉，需 toolchain file` | **过时。** `windows-11-arm` 现在是**标准 runner**（4 CPU / 16 GB），GitHub 文档写明「Use of the standard GitHub-hosted runners is free and unlimited on public repositories」，原生构建可行；交叉仍可行但有 §5.2 的陷阱 | github-hosted-runners 文档；Windows11-Arm64-Readme.md |
| `android-arm64: runs-on: ubuntu-latest —— NDK toolchain file，audit 换 readelf --dyn-syms` | **成立。** ubuntu-24.04 预装 NDK 27.3.13750724 并设了 `ANDROID_NDK_HOME`；NDK 官方文档确认 toolchain file 路径为 `$NDK/build/cmake/android.toolchain.cmake`，且**明确不推荐** CMake 内建的 Android 支持 | Ubuntu2404-Readme.md；NDK「Using CMake」 |
| `ios-arm64: runs-on: macos-latest —— 静态 .a，audit 换 nm -gU` | **成立但不完整。** `macos-latest` 现在是 macOS 26 arm64 + Xcode 26.6 + iOS SDK 26.x；`.a` 与 `nm -gU` 没问题，但现有 CMakeLists/stage_plugin 在 iOS 上会走错分支（§7.3） | macos-26-arm64-Readme.md；本机读码 |

另外骨架第 58 行写「交叉编译一律通过 `-DCMAKE_TOOLCHAIN_FILE=native/cross/<平台>.cmake` 传入」—— `native/cross/` **至今是空目录**（本机 `ls`），与 #46 的描述一致。

---

## 2. Windows runner 上的 bash 脚本

### 2.1 默认 shell 是 pwsh，不是 bash

GitHub Actions 官方文档的 shell 表：`pwsh` 是「the default shell used on Windows」；`bash` 需要显式指定，且「When specifying a bash shell on Windows, the bash shell included with Git for Windows is used」，实际执行的是 `bash --noprofile --norc -eo pipefail {0}`。

**后果:** `pr.yml:30-39` / `plugins-matrix.yml:27-36` 那段 `run: |` 用的是 bash 语法（`[[ ! -x ... ]]`、`for s in ...`），照抄到 Windows job 而不加 `shell: bash`，会被 PowerShell 解析并直接失败。`run: ./native/scripts/fetch-deps.sh` 在 pwsh 下也不会走 Git Bash。

### 2.2 CRLF：checkout 出来的脚本与数据文件都会被转换（证据链闭合）

四步，每步都是一手来源：

1. 本仓库 `.gitattributes` **没有** `* text=auto`，也没有任何 `*.sh` / `*.py` / `*.lock` 规则（本机 `git ls-files --eol native/scripts` 显示 `attr/` 为空）。因此这些文件的换行行为完全由 `core.autocrlf` 决定。
2. runner 镜像的 `images/windows/scripts/build/Install-Git.ps1` 只跑了一条 git config：`git config --system --add safe.directory "*"`。它传的安装选项是 `/COMPONENTS=gitlfs`、`/o:PathOption=CmdTools`、`/o:BashTerminalOption=ConHost`、`/o:EnableSymlinks=Enabled` —— **没有传 `/o:CRLFOption`**。
3. Git for Windows 的静默安装文档写明 `CRLFOption` 的默认值是 `CRLFAlways`；安装器源码 `installer/install.iss` 里，`ReplayChoice('CRLF Option','CRLFAlways')`（L2377）选中的分支对应 `Cmd:='true'` 并执行 `GitSystemConfigSet('core.autocrlf',Cmd)`（L3266-3271）。即 **系统级 `core.autocrlf=true`**。
4. `actions/checkout` 不覆盖它：`git-source-provider.ts` 里只写 `safe.directory` 与子模块的 `gc.auto 0`；在 `actions/checkout` 与 `actions/runner-images` 两个仓库做 GitHub 代码搜索，`autocrlf` **零命中**。

结论：Windows runner 上 checkout 出来的 `native/scripts/*.sh`、`*.py`、`native/versions.lock` **都是 CRLF**。

**这会怎么坏 —— 本机实测（附录 B-1）：**

- 一个 CRLF 的脚本，`#!` 行本身**没问题**：msys2-runtime 的 shebang 解析用 `strcspn (ptr, "\r\n")`（`winsup/cygwin/spawn.cc:1250`），`\r` 会被当成行尾切掉。所以「bad interpreter: ^M」不是本项目会遇到的形态 —— 这条流行说法在这里是错的。
- 真正炸的是脚本体。本机用 CRLF 复刻 `set -euo pipefail` + `read-lock.sh` 的解析逻辑，bash 直接报 `set: pipefail: invalid option name` 并以 1 退出。
- 即使 bash 容忍（Git Bash 是 bash 5.x，Cygwin 系 bash 有 `igncr` 选项但默认关闭 —— **此条为推断，未在 Windows 上实测**），`versions.lock` 是**数据**：`read-lock.sh` 用 `grep | cut -d= -f2` 取值，CRLF 下拿到的是 `v0.24.5\r`，随后 `git clone --branch "v0.24.5<CR>"` 必然失败。这条与 bash 的容忍度无关。

**已知的修法与代价**（不做选择）：
| 修法 | 代价 |
|------|------|
| `.gitattributes` 加 `*.sh text eol=lf` / `*.py` / `versions.lock` | 一行配置，跨所有 runner 与所有开发者生效；但改的是全仓库策略，需与 Unity 模板段落共存 |
| job 里先 `git config --global core.autocrlf false` 再 checkout | 每个 Windows job 都要记得写；忘了就沉默地回归 —— 正是 CONTRIBUTING 反对的形状 |
| Windows 上不跑 bash 脚本，改用 PowerShell/Python 等价物 | 两套脚本两处维护，`fetch-deps` 的语义要复制一遍 |

### 2.3 三个脚本在 Git Bash 下的可移植性（逐行读过）

| 脚本 | 用到的外部工具 | Git Bash 下 | 备注 |
|------|----------------|-------------|------|
| `read-lock.sh` | `grep`、`cut` | 可用 | 无 GNU 专有选项；唯一风险是 §2.2 的 CRLF |
| `fetch-deps.sh` | `git`、`mkdir -p`、`rm -rf`、`[[ -d/-f ]]` | 可用 | 无 `realpath` / `readlink -f`；根目录用 `"$(cd "$(dirname "$0")/.." && pwd)"` —— 这个写法在 msys 下正常，得到的是 `/d/a/...` 形式的 POSIX 路径 |
| `stage_plugin.py` | `shutil` / `pathlib`，`install_name_tool` 仅在 darwin 分支 | 可用 | Windows 分支只 `copy2` 成 `datachannel_unity.dll`，**不跑任何 audit**（macOS 分支跑 `audit-macos-plugin.sh`，`check=True`）。即：Windows 平台的暂存路径上目前没有门禁 |
| `audit-macos-plugin.sh` | `otool`、`nm`、`diff <(...)` | 仅 macOS | 进程替换 `<(...)` 在 Git Bash 可用，但 `otool`/`nm` 不存在 —— 本来就要换成 Windows 版 audit |
| `build-macos-arm64.sh` | `sysctl -n hw.ncpu` | 仅 macOS | 同上，Windows 需要自己的 build 脚本 |

**没有发现**任何 GNU 专有工具（无 `readlink -f`、`realpath`、`sed -i`、`mktemp -d --tmpdir`、`stat -c`）。也就是说，除 CRLF 外，`fetch-deps.sh` + `read-lock.sh` 这条路在 Git Bash 下没有语法/工具层面的障碍。

**两个仍要当心的 Windows 专有行为：**

- **msys 参数路径转换。** MSYS2 官方文档：从 msys shell 启动原生程序时，「all the arguments that look like Unix paths will get auto converted to Windows」，并且这个启发式「converts arguments that look like Unix paths while they are not」。将来若从 bash 里调 `cmake`，形如 `/DEF:...`、`-DCMAKE_TOOLCHAIN_FILE=/d/a/...` 的参数会被改写；逃生口是 `MSYS2_ARG_CONV_EXCL`。注意当前 `CMakeLists.txt:114` 的 `/DEF:` 是 CMake 直接传给 link.exe 的，**不经过 bash argv**，因此不受影响。
- **长路径。** Git for Windows 的 `core.longpaths` 默认 false。runner 镜像在 OS 层把 `LongPathsEnabled` 注册表值设成了 1（`images/windows/scripts/build/Configure-BaseImage.ps1`），但 git 自身的 `core.longpaths` 仍是另一回事。`fetch-deps.sh` 会递归拉 libdatachannel 的子模块（`deps/libjuice`、`deps/usrsctp`、`deps/libsrtp`、`deps/plog`、`deps/json`，各自还有子模块），路径深度值得在第一次跑 Windows job 时观察。

另：`fetch-deps.sh:27` 的 `git submodule update --init --recursive --depth 1 || true` 是一处现存的 `|| true`，与 CONTRIBUTING 的第一原则直接冲突（子模块拉取失败会以「构建时缺文件」的形式在很后面才暴露）。与本票的 Windows 问题无关，但既然读到了就记下来。

---

## 3. 「脚本可执行位断言」在 Windows 上到底是什么行为

**问题:** `pr.yml:30-39` 的 `[[ ! -x "$s" ]]`，在 `core.fileMode=false` + Git for Windows 不保存 unix 权限位的前提下，在 Windows runner 上返回什么？

### 3.1 证据链（每一步都是源码或官方文档）

1. **git 侧不提供权限位。** 本仓库 `.git/config` 有 `core.filemode=false`（本机 `git config --list --show-origin` 实测）；索引里五个脚本的 mode 都是 `100755`（本机 `git ls-files -s native/scripts` 实测）。Git for Windows 的 `git.exe` 是原生 Win32 程序（只有 bash 及 unix 工具走 msys2-runtime），checkout 时不会、也无法把 100755 落成 NTFS 上的「可执行位」。
2. **msys2 的挂载默认 `noacl`。** `msys2-runtime` 的 `winsup/cygwin/mount.cc`：根挂载点 `MOUNT_SYSTEM | MOUNT_IMMUTABLE | MOUNT_AUTOMATIC | MOUNT_NOACL`（L552-553），cygdrive（即 `/d/a/...` 这类盘符路径，GitHub workspace 就在这里）`cygdrive_flags = MOUNT_NOPOSIX | MOUNT_CYGDRIVE | MOUNT_NOACL`（L559）。
3. **noacl ⇒ 权限不从 ACL 来。** `winsup/cygwin/sec/base.cc:290` 的 `get_file_attribute()` 只有在 `pc.has_acls()` 为真时才去读安全描述符；否则一路走到函数末尾 `return -1`。
4. **于是 stat 走「伪造权限 + 探测文件头」分支。** `winsup/cygwin/fhandler/disk_file.cc:477` 起：`if (!get_file_attribute (...))` 为假 ⇒ 进入 else 分支，先给 `STD_RBITS`/`STD_WBITS`，再「No known suffix, check file header. This catches binaries and shebang scripts」，读前 3 字节交给 `has_exec_chars()`，命中就 `buf->st_mode |= STD_XBITS`（L551-560）。
5. **`has_exec_chars` 的判据。** `winsup/cygwin/local_includes/path.h:488-494`：前两字节是 `#!`、`:\n` 或 `MZ` 即视为可执行。
6. **本仓库五个脚本的前两字节全是 `#!`**（本机 `head -1 native/scripts/*` 实测：四个 `#!/usr/bin/env bash`，一个 `#!/usr/bin/env python3`）。

**结论:** 在 Git Bash 下，`[[ -x native/scripts/xxx.sh ]]` **恒为真** —— 真值来自「文件以 `#!` 开头」，与 git 索引里的 100755 毫无关系。把某个脚本的可执行位在 git 里丢掉（正是 #35 那次回归），Windows 上的这条断言**照样绿**。

旁证（同一套语义的另一处）：`winsup/cygwin/spawn.cc:1278-1281` 在执行脚本前检查可执行性时写着 `if (real_path.has_acls () && check_file_access (real_path, X_OK, true) < 0)` —— noacl 时这个检查整体被跳过。也就是说 msys 下「没有可执行位的脚本」根本也能被执行。

### 3.2 两种写法，两种坏法

| Windows job 的写法 | 实际行为 | 违反 CONTRIBUTING 的方式 |
|--------------------|----------|--------------------------|
| 照抄 macOS job，不加 `shell: bash` | pwsh 解析 `[[ ... ]]` / `fail=0` 失败 ⇒ **恒假**（永远红，且红得与脚本权限无关） | 一个永远失败的门禁不是门禁，等于逼人把它删掉或加 `continue-on-error` |
| 加 `shell: bash` | **恒真**（§3.1）⇒ 永远绿 | 正是「让缺席变成沉默」：报告里「这条从没验到过」与「验过且通过」长得一模一样 |

两条都成立，**都**违反第一原则。

### 3.3 可移植的等价断言（事实与代价，不做选择）

| 方案 | 是否真的测到「git 里记录的可执行位」 | 代价 |
|------|--------------------------------------|------|
| `git ls-files -s native/scripts` 检查 mode 是否 `100755` | 是。索引 mode 与平台无关，Windows / macOS / Linux 输出一致（本机已验输出格式，见附录 B-3） | 断言对象从「工作区文件」变成「索引记录」；语义其实更贴近 #35 那次真实故障（committed mode 丢了） |
| 只在 Unix runner 上跑这条断言，Windows job 显式不跑 | 是（在跑的地方） | 需要在 workflow 里写明「这里为什么没有」，否则下一个人会照抄进 Windows |
| 在 Windows 上保留 `[[ -x ]]` | **否** | 沉默的假绿 |

---

## 4. Windows x64：编译器、CRT 与 audit

### 4.1 runner 上有什么（Windows2025-Readme.md 实录）

- Windows Server 2025，`OS Version: 10.0.26100`；Visual Studio **Enterprise 2022, 17.14.37502.11**，含 `VC.Tools.x86.x64`、`VC.Tools.ARM64`、`VC.Tools.ARM64EC`、`VC.Llvm.Clang`(+ClangToolset)、`VC.CMake.Project`、Windows 11 SDK 26100。
- 独立工具：CMake **3.31.6**、Ninja **1.13.2**、LLVM **20.1.8**、Python **3.12.10**、Git **2.55.0.windows.3**（含 Git LFS 3.7.1）、MSYS2 在 `C:\msys64`「pre-installed on image but not added to PATH」。

### 4.2 MSVC vs clang-cl，以及 SPEC §8 的 `/MD`

- **CRT `/MD` 是 CMake 的默认行为**，不需要额外设置：CMake 文档说 `CMAKE_MSVC_RUNTIME_LIBRARY` 未设时默认取 `MultiThreaded$<$<CONFIG:Debug>:Debug>DLL`（受策略 CMP0091 管辖），即 Release 下 `-MD`。SPEC §8 的「CRT `/MD` 且自包含」在默认配置下即满足；**要当心的是相反方向** —— 某个子项目若自作主张改成 `/MT`，就会与主目标混用 CRT。本次核查 MbedTLS v3.6.7 的 `CMakeLists.txt`：MSVC 分支只加 `/W3 /utf-8`（L275-278）与 `/WX`（L281-283），**没有**改运行时库。
- **clang-cl 也在镜像里**（VS 的 `VC.Llvm.Clang` 组件 + 独立 LLVM 20.1.8）。事实层面两条都可行；差异在于 `.def` 导出、`/WX` 兼容性与谁有上游背书（见 4.3）。本文不选。

### 4.3 上游对 Windows 的实际覆盖（决定了「已知问题」的可信度）

libdatachannel v0.24.5 的 7 个 workflow 里，**只有 `build-openssl.yml` 有 windows job**：

```yaml
build-windows:
  runs-on: windows-latest
  steps:
  - uses: actions/checkout@v6
  - uses: ilammy/msvc-dev-cmd@v1
  - name: install packages
    run: choco install openssl
  - name: cmake
    run: cmake -B build -G "NMake Makefiles" -DUSE_GNUTLS=0 -DWARNINGS_AS_ERRORS=1
```

`BUILDING.md` 给的 MSVC 指引也是 `-G "NMake Makefiles"` + `nmake`。

三个可直接引用的事实：
1. **MSVC 编译 libdatachannel 本身有上游 CI 覆盖**（`WARNINGS_AS_ERRORS=1` 都开着），但那是 **OpenSSL** 后端。
2. **MSVC × MbedTLS 的组合没有任何上游 CI 覆盖** —— `build-mbedtls.yml` 只有 ubuntu 与 macos 两个 job，且都用 `brew install mbedtls@3`。我们要走的正是这条无覆盖的路。
3. **上游拿 MSVC 环境的办法是第三方 action `ilammy/msvc-dev-cmd@v1`**。这是 Ninja / NMake 生成器在 Windows 上的通用前提（`cl.exe` 必须在环境里）。另一条不引第三方 action 的路是 `-G "Visual Studio 17 2022" -A x64`（VS 生成器自己找工具链），代价是多配置生成器 —— 构建要带 `--config Release`，且 `$<TARGET_FILE:...>` 的路径多一层配置目录。

**MbedTLS 侧的已知问题：** `MBEDTLS_FATAL_WARNINGS` 默认 **ON**，在 MSVC 下等于 `/WX`（`CMakeLists.txt:281-283`）。上游只用 GCC/Clang 跑 CI，MSVC 的 warning 集合不同 —— 这是 Windows 首次构建最可能撞上的一堵墙，逃生口是 `-DMBEDTLS_FATAL_WARNINGS=OFF`（代价：放弃上游对该项目的 warning 门禁，本项目的产物门禁不受影响）。

### 4.4 audit：`dumpbin` 在，但要先有环境；而 `.def` 已经先坏了

- `dumpbin /exports` 的可用性：微软文档「We recommend you run DUMPBIN from the Visual Studio command prompt. You can't start it from a system command prompt unless you set the environment correctly.」也就是说 audit 步骤与构建步骤一样需要 vcvars（`ilammy/msvc-dev-cmd` 或 `vswhere` + `vcvarsall.bat`）。镜像里还有独立 LLVM 20.1.8，`llvm-readobj --coff-exports` / `llvm-nm` 是不需要 vcvars 的替代品 —— 两条路都存在，代价分别是「多一个第三方 action / 多一段 vswhere 脚本」与「audit 工具在不同平台不同家族」。
- **更前置的问题：`native/exports/windows-exports.def` 与当前 ABI 已经对不上。** 本机 diff（附录 B-2）：清单 `expected-symbols.txt` 有 20 个符号，`.def` 只列了 18 个，且
  - `.def` **缺** 6 个：`dcu_dc_receive`、`dcu_dc_state`、`dcu_event_next`、`dcu_event_queue_depth`、`dcu_log_next`、`dcu_test_set_open_race_delay_ms`
  - `.def` **多** 4 个已被删除的：`dcu_event_peek`、`dcu_event_pop`、`dcu_event_copy_payload`、`dcu_event_copy_payload2`

  多出来的那 4 个足以让链接失败：微软 LNK2001 文档的「Exported .def file symbol issues」一节原话是「This error occurs when an export listed in a .def file isn't found」，而 LNK2001 后面跟的是 fatal error LNK1120。**Windows 构建会在跑到 audit 之前就失败**。
- 根因是形态差异，值得记下来：macOS 用 `_dcu_*`、Linux 用 `dcu_*;` —— **两边都是通配符，零维护**；MSVC 的 `.def` **不支持通配符**，必须逐个列名，于是它成了唯一会随 ABI 变更而腐烂的导出清单。Windows 平台的「符号允许列表」天然比另外两个平台贵。

---

## 5. Windows arm64

### 5.1 先问「谁来加载它」：Unity 2022.3 没有 Windows Arm player

Unity 2022.3 官方 System requirements 的 Player 一栏，Windows 是「x86, x64 architecture with SSE2 instruction set support」—— **没有 ARM/ARM64**。ARM64 在同一页只出现在 UWP、ChromeOS、Embedded、Android/iOS 行。

Unity 自己的说法是 Windows on Arm 运行时从 **2023.1 Tech Stream** 开始支持，LTS 上要到 Unity 6（"Enabled Windows ARM64 Player compilation"）。
> 出处：unity.com 的 "Unity runtime on Arm-based Windows devices" 博客与 Unity 6 release notes。**该博客页直接抓取返回 403**，此条来自 unity.com 域内检索摘要，标注为二手；但 2022.3 侧的结论不依赖它 —— 上面那条 2022.3 官方 system-requirements 引文已经足够。

Editor 侧同理：2022.3 的 Editor 要求 x64，Arm Windows 上跑的是 x64 模拟，加载的会是 `Plugins/Windows/x86_64` 的 DLL。

**因此：** SPEC §8 树里的 `Windows/ARM64/datachannel_unity.dll` 在 Unity 2022.3 里**没有消费者**。这不改变「要不要做」的决定（那是 #51 的事），但它把 Win arm64 的收益从「支持一个平台」降为「为将来的 Unity 版本预留」。

### 5.2 两条路的事实

| 路 | 事实 | 代价 |
|----|------|------|
| **原生** `windows-11-arm` | 标准 runner，公开仓库免费无限；镜像有 VS 2022 Ent 17.14、CMake 4.4.0、Ninja 1.13.2、Git 2.55、LLVM 20.1.6、Python 3.13 | 镜像**没列 MSYS2**（x64 镜像有），bash 只有 Git for Windows 自带的那份；CMake 版本与 x64 镜像不同（4.4.0 vs 3.31.6），行为漂移面多一处 |
| **交叉** `-A ARM64`（VS 生成器）或 toolchain file | VS 2022 已装 `VC.Tools.ARM64` / ARM64EC，交叉工具链在 x64 镜像上是现成的 | **陷阱：** CMake 文档说 `CMAKE_SYSTEM_PROCESSOR` 在非交叉时等于 `CMAKE_HOST_SYSTEM_PROCESSOR`，且「a toolchain file should set the `CMAKE_SYSTEM_PROCESSOR` variable」。只给 `-A ARM64` 而不给 toolchain file 时 `CMAKE_SYSTEM_NAME` 仍是 Windows（不算交叉），`CMAKE_SYSTEM_PROCESSOR` 仍是 `AMD64` ⇒ `CMakeLists.txt:134-138` 会把 arm64 的 DLL 暂存进 `Plugins/Windows/x86_64/`。要避开，必须写 `native/cross/windows-arm64.cmake` 同时设 `CMAKE_SYSTEM_NAME=Windows` + `CMAKE_SYSTEM_PROCESSOR=ARM64`，这恰好就是骨架注释里说的形态 |

---

## 6. Android arm64-v8a

### 6.1 runner 上的 NDK

`ubuntu-latest`（24.04.4 LTS）预装三个 NDK：**27.3.13750724（default）**、28.2.13676358、29.0.14206865。环境变量：

```
ANDROID_HOME / ANDROID_SDK_ROOT = /usr/local/lib/android/sdk
ANDROID_NDK = ANDROID_NDK_HOME = ANDROID_NDK_ROOT = /usr/local/lib/android/sdk/ndk/27.3.13750724
ANDROID_NDK_LATEST_HOME         = /usr/local/lib/android/sdk/ndk/29.0.14206865
```

（`windows-latest` 镜像也带同样三个 NDK，路径 `C:\Android\android-sdk\ndk\...`；macOS 26 镜像的 Android 条目只列了 bundled CMake。）

### 6.2 toolchain file 的标准形态

NDK 官方文档给的命令行形态就是骨架注释说的那种：

```bash
cmake -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake \
      -DANDROID_ABI=$ABI -DANDROID_PLATFORM=android-$MINSDKVERSION ...
```

并且文档明确警告不要用 CMake 内建的 Android 支持：「CMake has its own built-in NDK support which has behavior differences compared to the NDK's CMake toolchain file. Android does not support or test the built-in workflow. We recommend using our toolchain file.」—— 所以「自写 toolchain file」在这里没有位置；`native/cross/android-arm64.cmake` 若存在，正确形态是**薄薄一层**：设好 `ANDROID_ABI` / `ANDROID_PLATFORM` / `ANDROID_STL` 后 `include` NDK 那份，或者干脆直接把 NDK 那份当 `CMAKE_TOOLCHAIN_FILE` 并用 `-D` 传参。

其它已核实的默认值：
- `ANDROID_ABI` 是**必填**，命令行下一次只能构建一个 ABI（`arm64-v8a` 是我们要的）。
- `ANDROID_PLATFORM`（别名 `ANDROID_NATIVE_API_LEVEL`）命令行下「defaults to the lowest API level supported by the NDK in use」，且「NDK libraries cannot be run on devices with an API level below the `ANDROID_PLATFORM` value with which the code was built」。
- `ANDROID_STL` 默认 **`c++_static`** —— 对我们这种「只出一个 .so」的插件正好（避免与采用者 App 里 Unity 自带的 libc++ 版本打架）。

### 6.3 `ANDROID_PLATFORM` 该定多少：两个下界打架

| 来源 | 下界 | 出处 |
|------|------|------|
| Unity 2022.3 支持的最低 Android | **API 22**（Android 5.1） | Unity 2022.3 System requirements：「5.1 (API 22)+」 |
| libjuice 用 `getifaddrs()` 枚举本地网卡 | **API 24** | bionic `libc/include/ifaddrs.h`：`int getifaddrs(...) __INTRODUCED_IN(24)`；libjuice `src/udp.c:521-523` 把它包在 `#ifndef NO_IFADDRS` 里 |

事实：定 `android-22` 能编过（因为有 `#ifndef NO_IFADDRS` 的逃生口，但要自己传 `-DNO_IFADDRS`，libjuice 的 CMakeLists **没有**把它做成 option），代价是**丢掉 host candidate 枚举**，ICE 只剩 srflx/relay。定 `android-24` 则两个下界都满足，代价是采用者的 `minSdkVersion` 被我们抬到 24。

### 6.4 Unity 用 r23b，我们用 r27 —— 以及 16 KB page

- Unity 2022.3 官方「Supported dependency versions」：NDK **r23b (23.1.7779620)**、JDK 11、Build tools 34.0.0。runner 上的默认 NDK 是 27.3。产物是预编译 `.so`，NDK 版本不必与 Unity 一致，但 `ANDROID_STL=c++_static` 这条就是为此存在的（不共享 libc++_shared 就不存在版本冲突）。
- **16 KB page 对齐是当下的硬要求：** Android 官方文档「Starting November 1st, 2025, all new apps and updates to existing apps submitted to Google Play and targeting Android 15+ devices must support 16 KB page sizes on 64-bit devices」，且「NDK version r28 and higher compile 16 KB-aligned by default」，r27 及以下需显式：

  ```cmake
  target_link_options(... PRIVATE "-Wl,-z,max-page-size=16384" "-Wl,-z,common-page-size=16384")
  ```

  文档还特意点名预编译库：「If your app uses any prebuilt shared libraries, you must also recompile them in the same way」—— 我们出的正是这种 prebuilt。runner 默认 NDK 是 **27.3，需要显式加 flags**；改用镜像里的 28.2/29.0 则默认对齐（代价：与 `ANDROID_NDK_HOME` 不一致，要在 workflow 里写死版本号，镜像更新时会漂）。

### 6.5 现有 CMakeLists 在 Android 下的表现（读码）

`CMakeLists.txt:139-140` 已有 `elseif(ANDROID) set(DCU_PLUGIN_REL "Android/${CMAKE_ANDROID_ARCH_ABI}")`，而 L151 传给 `stage_plugin.py` 的 `--host-system` 在非 APPLE/非 WIN32 时是 `linux` ⇒ 产物落成 `Android/arm64-v8a/libdatachannel_unity.so`，与 SPEC §8 的树一致。**Android 是四个未打通平台里唯一暂存路径已经对的。** 导出控制走 `else()` 分支的 version-script + `--exclude-libs,ALL`（L116-120），对 Android 的 lld 同样适用。

---

## 7. iOS arm64

### 7.1 runner

`macos-latest` 现在是 **macOS 26 arm64**（macOS 26.5.2），Xcode **26.6 default**（另装 26.0.1–26.5），iOS device SDK `iphoneos26.0`–`iphoneos26.5`，CMake 4.4.0，Ninja 1.13.2，Homebrew 6.0.13。

### 7.2 CMake 的标准做法

CMake `cmake-toolchains(7)`：设 `CMAKE_SYSTEM_NAME=iOS`；**Xcode 生成器「is recommended」**，`Unix Makefiles` 与 `Ninja` 也能用，但「push CPU selection and code signing onto the project」（即要自己给 arch 与签名）。默认 SDK 为 `iphoneos`（device），simulator 是 `iphonesimulator` —— SPEC §8 已定 device-only，所以**默认值就是我们要的**，`CMAKE_OSX_SYSROOT` 通常不必设。arch 用 `CMAKE_OSX_ARCHITECTURES=arm64`，部署目标用 `CMAKE_OSX_DEPLOYMENT_TARGET`。

**部署目标取多少（本机实测）：** 本机安装的 Unity **2022.3.62f3** 的 iOS trampoline（`PlaybackEngines/iOSSupport/Trampoline/Unity-iPhone.xcodeproj/project.pbxproj`）里 `IPHONEOS_DEPLOYMENT_TARGET` 出现两个值：**12.0** 与 15.0。与 Unity 文档一致：「iOS 12+」，且「From Unity 2022.3.72f1 and later, the minimum supported version of iOS is 13」（我们钉的是 .62，故 12.0）。

### 7.3 现有 CMakeLists / stage_plugin 在 iOS 下会走错两处（读码结论）

1. `add_library(datachannel_unity SHARED ...)`（L88）—— iOS 要的是 **static `.a`**（SPEC §8 + `__Internal`）。
2. 平台映射与暂存都按 `APPLE` 判断：`if(APPLE)` 分支会挑 `macOS/arm64`（L127-132），`--host-system` 也会算成 `darwin`（L151）⇒ `stage_plugin.py` 走 darwin 分支，**给一个静态库造 `.bundle` 并调用 `install_name_tool` 和 macOS 版 audit**。iOS 必须在这两处之前先分流（`if(CMAKE_SYSTEM_NAME STREQUAL "iOS")`）。

这两条是本机读码得出的，与 runner 选型无关 —— 也就是说 iOS 的成本主要不在 CI，在 `native/` 自身。

（`.a` 没有导出表、§3 的符号隐藏在它上面不成立 —— #46 已经把这条列为已知硬点，本文不重复展开。）

---

## 8. Linux x64 与 glibc

| 事实 | 值 | 出处 |
|------|----|------|
| `ubuntu-latest` | Ubuntu **24.04.4 LTS** | Ubuntu2404-Readme.md |
| noble (24.04) 的 libc6 | **2.39**-0ubuntu8.8 | packages.ubuntu.com |
| jammy (22.04) 的 libc6 | **2.35**-0ubuntu3.14 | 同上 |
| 仍可用的更老 runner | `ubuntu-22.04` / `ubuntu-22.04-arm`（20.04 已无标签） | runner-images README |
| Unity 2022.3 声明的 Linux Player 环境 | 「Ubuntu 20.04, Ubuntu 18.04, and CentOS 7」，x64 + SSE2 | Unity 2022.3 System requirements |
| Unity 声明的 glibc 版本 | **没有。** 该页全文未提 glibc | 同上（已逐字确认） |

**问题的形状：** glibc 向后兼容（老程序在新 glibc 上能跑），但**不向前**。在 24.04（2.39）上编出的 `.so`，只要引用了 2.35 之后新增的符号版本（典型如 `__isoc23_strtol`、新的 `fortify` 变体），在 Ubuntu 20.04（glibc 2.31）上加载就会 `GLIBC_2.xx not found`。而 Unity 官方点名支持 20.04 与 18.04 —— **连 `ubuntu-22.04` runner（2.35）都比 20.04 新**。

三条路与代价（不做选择）：

| 路 | 代价 |
|----|------|
| 用 `ubuntu-22.04` runner | 最省事；仍不满足 Unity 声明的 20.04/18.04，只是把风险面缩小。且 22.04 runner 也会有退役日 |
| 在 job 里跑容器（`jobs.<id>.container:`，如 `ubuntu:20.04` 或 manylinux 系镜像） | 真正把 glibc 下界钉死，且与 runner 镜像退役解耦；代价是容器里要自己装 cmake/ninja/编译器，构建步骤与其它平台不再同形，缓存与 checkout 行为也要重验 |
| 不管 glibc，只静态化 libstdc++/libgcc | 解决不了 glibc 本身的符号版本问题（libstdc++ 静态化只解决 C++ ABI），属于半个措施 |

补充：`CMakeLists.txt:142` 的 `DCU_PLUGIN_REL = "Linux/${CMAKE_SYSTEM_PROCESSOR}"` 在 x64 Linux 上得到 `Linux/x86_64`，`stage_plugin.py` 落成 `libdatachannel_unity.so` —— 与 #46 要补进 SPEC §8 的形态一致。

---

## 9. macOS x64（顺带核实，因为它在桌面批里）

- `macos-13` 标签**已不存在**；runner-images 现有的 macOS 标签是 macos-14（deprecated）、macos-15 / macos-15-intel / macos-15-large、macos-26 / macos-26-intel / macos-26-large、xcode-27（preview）。
- `macos-15-intel` / `macos-26-intel` 是**标准 runner**（4 CPU / 14 GB），公开仓库免费无限（GitHub 文档原话见 §1）。
- 另一条路是在 arm64 runner 上 `-DCMAKE_OSX_ARCHITECTURES=x86_64`：同一个 Xcode SDK 同时含两个 slice，CMake 文档也把 `CMAKE_OSX_ARCHITECTURES` 列为跨 Apple 平台设 arch 的标准手段。代价：产物无法在 CI 上原生跑（本来也不跑）；SPEC §8 已定 thin bundle，不涉及 universal 合并。
- 注意 `CMakeLists.txt:128-132` 用 `CMAKE_SYSTEM_PROCESSOR` 决定 `macOS/arm64` 还是 `macOS/x64` —— 在 arm64 host 上只给 `-DCMAKE_OSX_ARCHITECTURES=x86_64`，`CMAKE_SYSTEM_PROCESSOR` 仍是 `arm64`（同 §5.2 的陷阱），会把 x64 产物暂存进 `macOS/arm64/`。这是既有代码的行为，不是预测。

---

## 10. 代价一览（供 #51 切分时取用）

| 决定点 | 选项 A | 选项 B | 已核实的差别 |
|--------|--------|--------|--------------|
| Win x64 生成器 | `-G Ninja` + `ilammy/msvc-dev-cmd` | `-G "Visual Studio 17 2022" -A x64` | A 与 macOS job 同形，但引第三方 action；B 无第三方依赖，但多配置生成器要 `--config Release`，产物路径多一层 |
| Win audit 工具 | `dumpbin /exports`（需 vcvars） | `llvm-readobj --coff-exports`（LLVM 20.1.8 已装） | A 是骨架预设、微软官方；B 不需要 vcvars，但 audit 工具跨平台不同族 |
| Win 上的 bash | 全部 job 加 `shell: bash` + 修 CRLF | Windows 用 PowerShell/Python 复刻 | A 一次修（`.gitattributes`）即全局生效；B 两套脚本 |
| 可执行位断言 | 换成 `git ls-files -s` 全平台统一 | Windows 显式不跑并写明原因 | 两者都能满足「缺席=失败」；A 覆盖面更大，B 改动更小 |
| Win arm64 | `windows-11-arm` 原生 | x64 上 `-A ARM64` + toolchain file | 原生免费可用；交叉必须补 toolchain file 否则暂存路径错。两者都缺 Unity 2022.3 的消费者 |
| Android NDK | 用默认 27.3 + 显式 16 KB flags | 在 workflow 里钉 28.2/29.0 | 前者跟随 `ANDROID_NDK_HOME`，后者默认 16 KB 对齐但版本写死会随镜像漂 |
| Android minSdk | `android-22`（Unity 下界）+ `-DNO_IFADDRS` | `android-24` | 前者丢 host candidate；后者抬高采用者 minSdk |
| Linux glibc | `ubuntu-22.04` runner | 容器（20.04/manylinux） | 前者省事但不满足 Unity 声明的下界；后者钉死下界但构建步骤不同形 |
| mac x64 | `macos-15-intel` / `macos-26-intel` 原生 | arm64 上 `-DCMAKE_OSX_ARCHITECTURES=x86_64` | 两者都免费；交叉要顺带修 `DCU_PLUGIN_REL` 的判断（§9） |

---

## 11. 本文没有回答的（留给 #51 / 后续票）

- 七个平台各自走原生还是交叉 —— 本文只给事实与代价。
- Windows 平台 `.def` 逐符号维护的长期方案（生成？还是接受手工维护并加一条 CI 比对）。
- `windows-exports.def` 现在的失配要不要单独出一张修复票（它挡在任何 Windows CI 工作之前）。
- Windows arm64 在 Unity 2022.3 无消费者的前提下，是否仍留在矩阵里。
- iOS `.a` 的符号冲突（#46 已列为待定）与体积/LFS 刷新策略。

---

## 附录 A：一手来源清单

| 来源 | 用于 | 位置 |
|------|------|------|
| runner-images README（标签表） | 各平台 runner 标签、`-latest` 指向、preview/deprecated | https://github.com/actions/runner-images/blob/main/README.md |
| Windows2025-Readme.md | windows-latest 的 VS/CMake/Ninja/Git/Python/MSYS2/NDK | `images/windows/Windows2025-Readme.md` |
| Windows11-Arm64-Readme.md | windows-11-arm 的软件清单与缺项 | `images/windows/Windows11-Arm64-Readme.md` |
| Ubuntu2404-Readme.md | ubuntu-latest 的 NDK 版本与 `ANDROID_NDK_*` | `images/ubuntu/Ubuntu2404-Readme.md` |
| macos-26-arm64-Readme.md | macos-latest 的 Xcode / iOS SDK | `images/macos/macos-26-arm64-Readme.md` |
| Install-Git.ps1 | 镜像装 Git 时只设 `safe.directory`，未设 `core.autocrlf` | `images/windows/scripts/build/Install-Git.ps1` |
| Configure-BaseImage.ps1 | OS 层 `LongPathsEnabled=1` | `images/windows/scripts/build/Configure-BaseImage.ps1` |
| actions/checkout `git-source-provider.ts` | checkout 只设 `safe.directory` / `gc.auto` | https://github.com/actions/checkout |
| GitHub 代码搜索 | `autocrlf` 在 actions/checkout 与 actions/runner-images 中 **0 命中** | `gh search code` |
| GitHub Actions 文档：GitHub-hosted runners | 标准 runner 规格；「free and unlimited on public repositories」 | https://docs.github.com/en/actions/reference/runners/github-hosted-runners |
| GitHub Actions 文档：workflow syntax（shell） | Windows 默认 `pwsh`；`bash` = Git for Windows 的 bash | https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax |
| Git for Windows 静默安装文档 | `CRLFOption` 默认 `CRLFAlways` | https://gitforwindows.org/silent-or-unattended-installation |
| Git for Windows `installer/install.iss` | `CRLFAlways` ⇒ `GitSystemConfigSet('core.autocrlf','true')`（L2377, L3266-3271） | git-for-windows/build-extra |
| msys2-runtime `winsup/cygwin/mount.cc` | 根挂载与 cygdrive 默认 `MOUNT_NOACL`（L552-559） | msys2/msys2-runtime |
| msys2-runtime `winsup/cygwin/sec/base.cc` | `get_file_attribute()` 无 ACL 时 `return -1`（L290+） | 同上 |
| msys2-runtime `winsup/cygwin/fhandler/disk_file.cc` | shebang ⇒ `STD_XBITS`（L517-566） | 同上 |
| msys2-runtime `winsup/cygwin/local_includes/path.h` | `has_exec_chars`：`#!` / `:\n` / `MZ`（L488-494） | 同上 |
| msys2-runtime `winsup/cygwin/spawn.cc` | shebang 用 `strcspn(ptr,"\r\n")`；`has_acls()` 才查 X_OK（L1248-1281） | 同上 |
| MSYS2 文档：Filesystem Paths | 原生程序参数的自动路径转换与 `MSYS2_ARG_CONV_EXCL` | https://www.msys2.org/docs/filesystem-paths/ |
| CMake：`CMAKE_MSVC_RUNTIME_LIBRARY` | 未设时默认 `MultiThreaded$<$<CONFIG:Debug>:Debug>DLL`（= `/MD`），CMP0091 | cmake.org |
| CMake：`CMAKE_SYSTEM_PROCESSOR` | 非交叉时 = host；交叉时应由 toolchain file 设置 | cmake.org |
| CMake：`cmake-toolchains(7)` | Android 变量族；iOS 用 `CMAKE_SYSTEM_NAME=iOS`，Xcode 生成器 recommended | cmake.org |
| MSVC：DUMPBIN Reference | 「You can't start it from a system command prompt unless you set the environment correctly」 | learn.microsoft.com |
| MSVC：Linker Tools Error LNK2001 | 「Exported .def file symbol issues ... This error occurs when an export listed in a .def file isn't found」 | learn.microsoft.com |
| Android NDK：Using CMake | toolchain file 路径、`ANDROID_ABI`/`ANDROID_PLATFORM`/`ANDROID_STL` 默认值、不推荐 CMake 内建支持 | developer.android.com/ndk/guides/cmake |
| Android：Support 16 KB page sizes | Play 2025-11-01 要求、r28 默认对齐、r27 的 linker flags、prebuilt 也要重编 | developer.android.com/guide/practices/page-sizes |
| bionic `libc/include/ifaddrs.h` | `getifaddrs __INTRODUCED_IN(24)` | aosp-mirror/platform_bionic |
| libjuice `src/udp.c` / `CMakeLists.txt` | `#ifndef NO_IFADDRS`；无对应 CMake option | paullouisageneau/libjuice |
| libdatachannel v0.24.5 `BUILDING.md` + `.github/workflows/*` | MSVC 用 NMake；windows job 只在 OpenSSL 工作流里，用 `ilammy/msvc-dev-cmd`；MbedTLS 工作流只有 linux+macos | paullouisageneau/libdatachannel @ v0.24.5 |
| MbedTLS v3.6.7 `CMakeLists.txt` | MSVC 分支只加 `/W3 /utf-8` 与 `/WX`（`MBEDTLS_FATAL_WARNINGS` 默认 ON），不动 CRT | Mbed-TLS/mbedtls @ v3.6.7 |
| Unity 2022.3 System requirements | Windows Player 只有 x86/x64；Linux 列 20.04/18.04/CentOS 7 且**未提 glibc**；Android「5.1 (API 22)+」；iOS「12+」（2022.3.72f1 起为 13） | docs.unity3d.com/2022.3 |
| Unity 2022.3 Supported dependency versions | NDK **r23b (23.1.7779620)**、JDK 11、Build tools 34.0.0 | docs.unity3d.com/2022.3 |
| 本机 Unity 2022.3.62f3 iOS trampoline | `IPHONEOS_DEPLOYMENT_TARGET = 12.0`（另有 15.0） | `/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/iOSSupport/Trampoline/Unity-iPhone.xcodeproj/project.pbxproj` |
| packages.ubuntu.com | jammy libc6 2.35；noble libc6 2.39 | packages.ubuntu.com |

**二手/未直接抓取的（已在正文标注）：** unity.com 的 "Unity runtime on Arm-based Windows devices" 博客（直接抓取 403，仅用于「2023.1 起支持」这一条旁证；2022.3 的结论不依赖它）。

---

## 附录 B：本机验证记录

**B-1 CRLF 脚本在 bash 下的行为（macOS，bash 3.2.57）**

用 CRLF 复刻 `set -euo pipefail` + `read-lock.sh` 的 `grep|cut` 解析：

```
$ bash /tmp/crlftest/t.sh
/tmp/crlftest/t.sh: line 2: set: pipefail: invalid option name
EXIT=1
```

即脚本在第 2 行即死。Git Bash 的 bash 是 5.x，未在 Windows 上实测（本项目无 Windows 机器）；但 `versions.lock` 被 CRLF 污染后 `cut -d= -f2` 会带出 `\r` 这一点与 bash 版本无关。

**B-2 `windows-exports.def` 与 `expected-symbols.txt` 的实测差异（HEAD = 3a6d6fe）**

```
expected=20  def=18
清单有、def 没有: dcu_dc_receive dcu_dc_state dcu_event_next
                  dcu_event_queue_depth dcu_log_next dcu_test_set_open_race_delay_ms
def 有、清单没有: dcu_event_copy_payload dcu_event_copy_payload2
                  dcu_event_peek dcu_event_pop
```

对照：`macos-exported-symbols.txt` 内容是 `_dcu_*`（通配），`linux-version-script.map` 是 `global: dcu_*;`（通配）—— 只有 Windows 需要逐符号维护。

**B-3 权限位与换行属性**

```
$ git config --list --show-origin | grep filemode
file:.../.git/config    core.filemode=false

$ git ls-files -s native/scripts
100755 ... native/scripts/audit-macos-plugin.sh
100755 ... native/scripts/build-macos-arm64.sh
100755 ... native/scripts/fetch-deps.sh
100755 ... native/scripts/read-lock.sh
100755 ... native/scripts/stage_plugin.py

$ git ls-files --eol native/scripts native/versions.lock
i/lf  w/lf  attr/    ...      # attr 为空 = 无 .gitattributes 规则

$ head -1 native/scripts/*        # 五个文件前两字节全是 "#!"
```

**B-4 其它**

- `native/cross/` 存在但为空目录。
- `native/subprojects/` 不存在（未 fetch，符合「never committed」）。
- 五个脚本逐行读过，未发现 `realpath` / `readlink -f` / `sed -i` / `stat -c` / `mktemp --tmpdir` 等 GNU 专有用法。
