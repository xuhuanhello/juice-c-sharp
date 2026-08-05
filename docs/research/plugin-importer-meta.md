# Research: Unity 2022.3 `PluginImporter` 的 `.meta` 格式，与「不启动 Unity 能否校验」

| Field | Value |
| --- | --- |
| Ticket | [#49](https://github.com/xuhuanhello/juice-c-sharp/issues/49)（part of map [#46](https://github.com/xuhuanhello/juice-c-sharp/issues/46)） |
| Date | 2026-08-04 |
| Unity version studied | **2022.3.62f3**（本机 Editor 实测；对照 Unity 6000.0 的编辑器源码） |
| Subject | SPEC §8 的七平台 `Plugins/` 布局与 `.meta`；CI 无 Unity（§10 / [#43](https://github.com/xuhuanhello/juice-c-sharp/issues/43)）下的校验可行性 |
| Status | 完成。**只给事实与代价，不给「要不要做校验器」的取舍** —— 那是被本票挡住的 grilling 票 [#52](https://github.com/xuhuanhello/juice-c-sharp/issues/52) |

**一句话结论**：`.meta` 的平台开关是一张 **(group, target) → {enabled, settings}** 的稀疏表，**Unity 对它几乎不做任何校验** —— 平台键名写错会被当作「这条不存在」静默丢弃，CPU 值写错只有打开 Plugin Inspector 时才可能报一句 warning，`.dll` 挂到 macOS 上是**静默不拷贝**、P/Invoke 失败与「插件根本不存在」的报错完全一样。因此**纯文本校验能覆盖的错误类别，恰恰就是 Unity 自己不管的那一类**；能查什么、代价多少见 §6。

---

## 0. Verdict 速查

| 问题 | 结论 |
| --- | --- |
| `platformData` 怎么编码？ | `serializedVersion: 2` 下是一个 **list of `first`/`second` 对**；`first` 是单键映射 `<group>: <target>`。全部键名见 §2.2（实测自 `BuildPipeline`，不是猜的） |
| `Any Platform` 怎么写？ | 条目 `Any: `（target 为空串）的 `enabled: 1`。**排除项是另一条** `: Any`（group 为空串），settings 里是 `Exclude <target>: 0/1` |
| 七平台目标 YAML？ | §3，八段，全部是 **2022.3.62f3 自己写出来的字节**，不是从文档字段表复述的 |
| 仓库现有的 `datachannel_unity.bundle.meta` 对不对？ | 形状是 Unity 生成的，但有 **三处会在补齐矩阵时立刻爆的问题**（§4）：`Standalone CPU: AnyCPU`、Editor 段缺 `OS`/`CPU`、以及由此产生的同名 bundle 冲突 |
| `.dll` 打开 `Any Platform`，macOS Editor 会怎样？ | **完全静默**。构建期 `CalculateFinalPluginPath` 返回空串 → 不拷贝，无 warning；Editor 期 P/Invoke 抛 `DllNotFoundException: <name>`，Console **一行日志都没有**，与「文件压根不存在」不可区分（§5.1，实测） |
| 平台键名写错（`Standalone: Windows64`）？ | **静默等价于「没写这条」**。无报错、无重写、`.meta` 原样留在磁盘上（§5.2，实测） |
| CPU 值写错（`x86_65`）？ | Windows 侧照样拷贝（只有 `None`/`ARM64` 被特判）；macOS 侧会拷进一个**名字就是那个错值**的子目录 `x86_65/`（§5.3，实测） |
| Unity 自己校验 `.meta` 吗？ | 官方 Package Validation Suite 与其 **无 Unity** 的 PvpXray 验证器加起来只有两条：`.meta` **存在性** 与 **guid 唯一性 + 32 位十六进制**。平台开关一个字都不查（§7，读源码） |
| 不启动 Unity 能校验吗？ | **能，而且不难** —— `.meta` 是纯 YAML，键值域是封闭的、可枚举的（§2.2 / §2.3）。但它只能证明「这份 `.meta` 说的是我想说的话」，**不能**证明「这个二进制真能在那台机器上加载」（§6） |
| Windows ARM64 呢？ | **2022.3 没有这个槽位**。`DesktopPluginImporterExtension` 对 Windows 显式写着「Windows on Arm64 is not supported for Standalone Windows in this version of Unity」并返回空路径 → 静默丢弃。Unity 6000.0 才把 Win64 改成多架构（§5.4） |

---

## 1. 一手来源

| 来源 | 覆盖什么 |
| --- | --- |
| [Unity 2022.3 Manual — Plugin Inspector](https://docs.unity3d.com/2022.3/Documentation/Manual/PluginInspector.html) | `Any Platform` 的语义、Editor/Standalone 的 CPU/OS 选项、「Unity does not validate your settings」 |
| [Unity 2022.3 ScriptReference — `PluginImporter`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/PluginImporter.html) | 公开 API 面（`Set/GetPlatformData`、`Set/GetEditorData`、`SetCompatibleWithAnyPlatform`、`SetExcludeFromAnyPlatform`、`isPreloaded`、`GetIsOverridable`…） |
| UnityCsReference **2022.3** 分支（Unity 官方发布的编辑器 C# 源码，Reference-Only License）：`Editor/Mono/ImportSettings/DesktopPluginImporterExtension.cs`、`Editor/Mono/ImportSettings/EditorPluginImporterExtension.cs`、`Editor/Mono/Modules/DefaultPluginImporterExtension.cs`、`Modules/AssetPipelineEditor/Public/PluginImporter.bindings.cs` | CPU/OS 的**取值域**、构建期最终路径算法、文件冲突检测、值解析失败时的行为 |
| 同上 **6000.0** 分支的 `DesktopPluginImporterExtension.cs` | Windows ARM64 支持的分界点 |
| **本机 Editor 2022.3.62f3 实测**（Unity MCP `execute_code`，2026-08-04） | §3 全部 YAML、§5 全部「写错了会怎样」、§2.2 的键名表 |
| 真实包：`com.unity.webrtc@3.0.0`（从 `download.packages.unity.com` 官方 registry 拉的 tarball）、`com.unity.collab-proxy@2.12.4`、`com.unity.visualscripting@1.9.4`、`com.unity.ide.visualstudio@2.0.22`、Editor 内置 `com.unity.rendering.denoising` | 现实中多平台原生插件的 `.meta` 长什么样、同名逐架构插件怎么区分、`serializedVersion: 3` 的存在 |
| `com.unity.package-validation-suite@0.85.0-preview` 的 `Editor/ValidationSuite/ValidationTests/MetaFilesValidation.cs`、`Standards/US0112-PackageContainsMetafile.cs`、`Lib/PvpXray/MetaFileVerifierV4.cs`、`Lib/PvpXray/MetaGuidVerifier.cs` | 「有没有人做过 `.meta` 自动校验」的**权威答案**（§7） |

> **注意一个坑**：`/private/tmp/com.unity.webrtc-3.0.0-pre.8/` 是个**只有目录、零个文件**的空壳（`find -type f` 计数为 0），从它身上读不出任何 `.meta`。本文用的是从官方 registry 重新拉的 `com.unity.webrtc@3.0.0` tarball。

---

## 2. 格式

### 2.1 骨架

```yaml
fileFormatVersion: 2
guid: 746b7f4799a504254879b15f1a0910d8     # 32 位小写十六进制
folderAsset: yes                           # 见 §2.5
PluginImporter:
  externalObjects: {}
  serializedVersion: 2                     # 见 §2.4
  iconMap: {}
  executionOrder: {}
  defineConstraints: []                    # PluginImporter.DefineConstraints
  isPreloaded: 0                           # Plugin Inspector 的 "Load on startup"
  isOverridable: 1                         # 见 §4
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData: …                          # §2.2
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

`platformData` 之外的字段与平台无关，`.meta` 里恒定出现。注意 `userData:` / `assetBundleName:` / `assetBundleVariant:` 后面各有**一个尾随空格**（Unity 写的就是这样）。

### 2.2 `platformData`：键名表

`serializedVersion: 2` 的形状是一个**列表**，每项是 `first`（单键映射 `<group>: <target>`）+ `second`（`enabled` + `settings`）：

```yaml
  platformData:
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: x86_64
```

键名不是随便起的，来自 `BuildPipeline.GetBuildTargetGroupName()` / `GetBuildTargetName()`。本机 2022.3.62f3 实测输出：

| `BuildTarget` | group（`first` 的键） | target（`first` 的值） |
| --- | --- | --- |
| `StandaloneWindows` | `Standalone` | `Win` |
| `StandaloneWindows64` | `Standalone` | `Win64` |
| `StandaloneLinux64` | `Standalone` | `Linux64` |
| `StandaloneOSX` | `Standalone` | `OSXUniversal` |
| `Android` | `Android` | `Android` |
| `iOS` | **`iPhone`** | `iOS` |
| `WebGL` | `WebGL` | `WebGL` |
| `tvOS` | `tvOS` | `tvOS` |
| （编辑器） | `Editor` | `Editor`（`BuildPipeline.GetEditorTargetName()`） |

两个特殊条目：

| 条目 | 含义 |
| --- | --- |
| `Any: `（group=`Any`，target 空串） | **Any Platform 开关本身**。`enabled: 1` = 勾上 |
| `: Any`（group 空串，target=`Any`） | **Any Platform 的排除表**。`enabled` 恒为 0，语义全在 `settings` 的 `Exclude <target>: 0/1` 里 |

下面这段是让 2022.3.62f3 自己写出来的（`SetCompatibleWithAnyPlatform(true)` + `SetExcludeEditorFromAnyPlatform(true)` + `SetExcludeFromAnyPlatform("WebGL"/"OSXUniversal", true)`）：

```yaml
  platformData:
  - first:
      : Any
    second:
      enabled: 0
      settings:
        Exclude Editor: 1
        Exclude OSXUniversal: 1
        Exclude WebGL: 1
  - first:
      Any: 
    second:
      enabled: 1
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
```

**条目是稀疏的**：Unity 只序列化被动过的平台，没有的条目 == 该平台不兼容。排序是按 `first` 的 group 名做 ordinal 升序（空串 < `Android` < `Any` < `Editor` < `Standalone` < `WebGL` < `iPhone` < `tvOS`）。

### 2.3 `settings` 的键与取值域

| key | 出现在 | 取值域 | 来源 |
| --- | --- | --- | --- |
| `CPU` | Standalone / Editor / Android / iPhone | 见下 | `DefaultPluginImporterExtension.cpuKey` |
| `OS` | **仅 Editor** | `AnyOS` / `OSX` / `Windows` / `Linux` | `EditorPluginImporterExtension.EditorPluginOSArchitecture` |
| `DefaultValueInitialized` | 仅 Editor | `true` | `PluginImporter.ClearSettings()` 写入 |
| `Exclude <target>` | 仅 `: Any` | `0` / `1` | §2.2 |
| `AddToEmbeddedBinaries` | 仅 iPhone | `true` / `false` | iOS 扩展；`webrtc.framework` 是 `true`，`.a` 是 `false` |
| `CompileFlags` | 仅 iPhone | 自由字符串（可空） | 同上 |
| `FrameworkDependencies` | 仅 iPhone | `;` 分隔，如 `CoreFoundation;CoreAudio;Metal;` | 同上（取自 `com.unity.webrtc` 的 `webrtc.framework.meta`） |
| `Is16KbAligned` | 仅 Android | `true` / `false` | 2022.3.62f3 导入 `.so` 时**自动写入** `false` |

`CPU` 的合法值按平台不同：

| 平台 | 合法值 | 备注 |
| --- | --- | --- |
| Standalone `Win` | `None` / `x86` / `AnyCPU` | `DesktopSingleCPUProperty(StandaloneWindows, x86)` |
| Standalone `Win64` | `None` / `x86_64` / `AnyCPU` | **单架构**。`ARM64` 可以写进去，但构建期被丢弃（§5.4） |
| Standalone `Linux64` | `None` / `x86_64` / `AnyCPU` | 单架构 |
| Standalone `OSXUniversal` | `None` / `x86_64` / `ARM64` / `AnyCPU` | **多架构**（`DesktopMultiCPUProperty`） |
| `Editor` | `AnyCPU` / `x86_64` / `ARM64` | 且受 `OS` 约束：`OS: AnyOS` 时只允许 `AnyCPU`；`OS: Windows`/`Linux` 时不允许 `ARM64`（`EditorPluginImporterExtension.CanSelectArch`） |
| `Android` | `ARMv7` / `ARM64` / `X86` / `X86_64` / `AnyCPU` | `com.unity.webrtc` 的 `.aar` 写的是 `ARMv7` |
| `iPhone` | `AnyCPU` | 实测与真实包一致 |
| `WebGL` | —— | `settings: {}`，没有 CPU 键 |

### 2.4 `serializedVersion` 有第二种形状，而且 2022.3 读不了

`com.unity.visualscripting@1.9.4` 里 `Editor/…/Assemblies/WINARM64/Unity.VisualScripting.sqlite3.dll.meta` 是 **`serializedVersion: 3`**，`platformData` 变成了一张**直接以 target 为键的映射**：

```yaml
  serializedVersion: 3
  platformData:
    Any:
      enabled: 0
      settings:
        Exclude Editor: 0
        Exclude Win64: 1
    Editor:
      enabled: 1
      settings:
        CPU: ARM64
        DefaultValueInitialized: true
        OS: Windows
    Win64:
      enabled: 0
      settings:
        CPU: None
```

**本机实测：2022.3.62f3 把这份 `platformData` 整个丢掉了，且不报一个字。** 同一个包里 `WINx64/` 那份是 `serializedVersion: 2`，读出来一切正常：

```text
…/Assemblies/WINx64/Unity.VisualScripting.sqlite3.dll     EditorOS='Windows' EditorCPU='x86_64' Win64='AnyCPU'
…/Assemblies/WINARM64/Unity.VisualScripting.sqlite3.dll   EditorOS=''        EditorCPU=''      Win64=''
```

也就是说：**`.meta` 的 schema 是带版本的，新版 Editor 写出来的 `.meta` 在 2022.3 里静默降级成「什么都没配」**。这条既是格式事实，也是 §6 的一条硬约束 —— 任何校验器都必须先认 `serializedVersion`，而且「本仓库只写 2」本身就是一条得有人守的不变量。

### 2.5 `folderAsset: yes` 与插件目录

- macOS `.bundle` / iOS `.framework` 在文件系统上是目录。`com.unity.webrtc` 的 `webrtc.framework.meta`、`com.unity.ide.visualstudio` 的 `AppleEventIntegration.bundle.meta` 都带 `folderAsset: yes`；但 **2022.3.62f3 新导入一个 `.bundle` 时不写这一行**（实测）。手工加上去再 reimport，Unity 原样保留且行为不变 —— 这一行是**装饰性的**。
- 更重要的一条：`.androidlib` / `.bundle` / `.framework` / `.plugin` 这四种后缀的**目录内部文件不被 AssetDB 导入**（UUM-9421，backport 到 2022.2+）。所以 `.bundle` 只需要目录本身那**一份** `.meta`，里面**不该**有任何 `.meta`。这一条同时被 Unity 官方两个校验器写死（§7）。

---

## 3. 七平台目标 `.meta`（2022.3.62f3 实际产出）

下面每一段都是**让本机 Editor 通过 `PluginImporter` API 配置后自己写到磁盘上的字节**，只把探针文件名换成了 SPEC §8 的产物名。`guid` 请勿照抄。

### 3.1 Windows x64 —— `Plugins/Windows/x86_64/datachannel_unity.dll`

```yaml
fileFormatVersion: 2
guid: <32 hex>
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 1
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any: 
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: x86_64
        DefaultValueInitialized: true
        OS: Windows
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: x86_64
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

### 3.2 Windows ARM64 —— `Plugins/Windows/ARM64/datachannel_unity.dll`

```yaml
  platformData:
  - first:
      Any: 
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: ARM64
```

**这份 `.meta` 在 2022.3 里等于零。** Unity 接受并保存它，但构建 Windows 时最终路径解析为空串 → 插件不进包，无任何提示（§5.4）。

### 3.3 macOS arm64 —— `Plugins/macOS/arm64/datachannel_unity.bundle`

```yaml
  platformData:
  - first:
      Any: 
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: ARM64
        DefaultValueInitialized: true
        OS: OSX
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 1
      settings:
        CPU: ARM64
```

### 3.4 macOS x64 —— `Plugins/macOS/x64/datachannel_unity.bundle`

与 3.3 逐字相同，只是两处 `ARM64` 换成 `x86_64`：

```yaml
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: x86_64
        DefaultValueInitialized: true
        OS: OSX
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 1
      settings:
        CPU: x86_64
```

> **两份同名 thin bundle 靠且只靠这两个 `CPU` 字段区分。** 见 §4 / §5.5，这是本仓库现有 `.meta` 的实际缺陷所在。

### 3.5 Linux x64 —— `Plugins/Linux/x86_64/libdatachannel_unity.so`

```yaml
  platformData:
  - first:
      Android: Android
    second:
      enabled: 0
      settings:
        Is16KbAligned: false
  - first:
      Any: 
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: x86_64
        DefaultValueInitialized: true
        OS: Linux
  - first:
      Standalone: Linux64
    second:
      enabled: 1
      settings:
        CPU: x86_64
```

注意那条 `Android: Android` / `enabled: 0` —— **Unity 自己加的**：`.so` 对 Android 也是候选，导入时平台扩展会写入一条默认关闭的条目（含 `Is16KbAligned: false`）。手写 `.meta` 时可以不写它，行为一致；但如果目标是「与 Unity 产出逐字节一致」，它必须在。

### 3.6 Android arm64-v8a —— `Plugins/Android/arm64-v8a/libdatachannel_unity.so`

```yaml
  platformData:
  - first:
      Android: Android
    second:
      enabled: 1
      settings:
        CPU: ARM64
        Is16KbAligned: false
  - first:
      Any: 
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
```

### 3.7 iOS arm64 —— `Plugins/iOS/libdatachannel_unity.a`

```yaml
  platformData:
  - first:
      Any: 
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      iPhone: iOS
    second:
      enabled: 1
      settings:
        AddToEmbeddedBinaries: false
        CPU: AnyCPU
        CompileFlags: 
        FrameworkDependencies: 
```

`AddToEmbeddedBinaries: false` 对静态 `.a` 是正确的 —— `com.unity.webrtc` 的 `.framework`（动态）才写 `true`，并在 `FrameworkDependencies` 里列 `CoreFoundation;CoreAudio;Metal;`。若将来 `.a` 需要链接系统框架，就是往这里填。

### 3.8 WebGL —— `Plugins/WebGL/libdatachannel_unity.a` 与 `webrtc.jslib`

两者的 `.meta` **完全一样**：

```yaml
  platformData:
  - first:
      Any: 
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      WebGL: WebGL
    second:
      enabled: 1
      settings: {}
```

WebGL 没有 CPU 概念，`settings: {}`。

---

## 4. 既有样本审计：`Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle.meta`

现状（`git show 712e6f3:` 起就是这样，至今未变）：

```yaml
  isPreloaded: 0
  isOverridable: 1
  platformData:
  - first:
      Any: 
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        DefaultValueInitialized: true      # ← 没有 OS，没有 CPU
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 1
      settings:
        CPU: AnyCPU                        # ← 不是 ARM64
```

**它是 Unity 生成的吗？** 形状（字段顺序、`DefaultValueInitialized: true`、条目排序、尾随空格）与 Unity 产出逐字一致，本机 Editor 也确实把它当成一份正常的 `PluginImporter` 读进来（`isNativePlugin=True`、`OSXUniversal compat=True CPU=AnyCPU`）。所以**骨架是 Unity 写的**，但有一个字段不是 Unity 新建插件时的默认值：`isOverridable: 1`（新导入的插件 Unity 写 `0`，实测；`com.unity.collab-proxy` 的原生插件也是 `0`，而 `com.unity.webrtc` 是 `1`）。`PluginImporter` 只有 `GetIsOverridable()` 没有 setter，所以这一位只能靠手写 `.meta` 得到 —— 而 Unity 会忠实读回它（实测 `GetIsOverridable()=True`）。

`isOverridable: 1` 对一个要发布的 UPM 包**是正确的**：`DefaultPluginImporterExtension.CheckFileCollisions` 里，当采用者在自己的 `Assets/` 放了同名插件时，可覆盖的那份会被跳过而不是报冲突。这一条留着。

**三处会在补齐矩阵时立刻爆的问题：**

| # | 现状 | 后果 | 依据 |
| --- | --- | --- | --- |
| 1 | `Standalone: OSXUniversal` 的 `CPU: AnyCPU` | 构建期最终路径是 `datachannel_unity.bundle`（无架构子目录）。x64 那份一旦入库、也写 `AnyCPU`，两者最终路径相同 → **`CheckFileCollisions` 返回 true**，构建报 `Debug.LogError`：`Plugin 'datachannel_unity.bundle' is used from several locations` | 实测：两份同名 bundle 均 `AnyCPU` → `CheckFileCollisions=True`；改成 `ARM64` / `x86_64` → `False`，最终路径变成 `ARM64/…` 与 `x86_64/…` |
| 2 | `Editor: Editor` 段没有 `OS` / `CPU` | 等价于 `OS: AnyOS` + `CPU: AnyCPU`。x64 那份入库后，两份同名 bundle 都对 Editor 声称 AnyCPU → **Editor 导入期直接报错**：`Multiple plugins with the same name 'datachannel_unity' (found at …arm64… and …x64…). That means one or more plugins are set to be compatible with Editor. Only one plugin at the time can be used by Editor.` | 实测：两份 Editor `CPU: AnyCPU` → 每次 reimport 都刷这条 error；改成 `ARM64` / `x86_64`（`OS: OSX`）→ **一条都没有** |
| 3 | 同上 | `OS: AnyOS` 意味着这份 macOS `.bundle` 也对 Windows / Linux Editor 声称兼容。后果不是报错，而是 §5.1 那种静默失败 | `EditorPluginImporterExtension.EditorPluginOSArchitecture` |

问题 2 的修复方式在 Unity 自家包里有现成样板：`com.unity.collab-proxy@2.12.4` 的 `libonigwrap` 用**五份同名文件**（`win-x86` / `win-x64` / `win-arm64` / `osx-x64` / `osx-arm64`）只靠 Editor 段的 `OS` + `CPU` 区分：

```yaml
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: ARM64
        DefaultValueInitialized: true
        OS: OSX
```

（其余四份分别是 `OS: OSX`/`CPU: x86_64`、`OS: Windows`/`CPU: x86`、`OS: Windows`/`CPU: x86_64`、`OS: Windows`/`CPU: ARM64`。）

**顺带**：`Plugins/{Windows,macOS,Android,iOS,WebGL}` 及其子目录的 `.meta` 都是 `DefaultImporter` + `folderAsset: yes`，这是对的 —— 只有插件文件本身才该是 `PluginImporter`。SPEC §8 的树里没有 `Linux/`，其 `.meta`（`Plugins/Linux.meta`、`Plugins/Linux/x86_64.meta`）目前也不存在。

---

## 5. 「写错了会怎样」—— 全部实测，不是推断

实测环境：本机 Editor 2022.3.62f3，探针资产 `Assets/MetaProbe/`（已删除，工作树已恢复）。判定用的是构建管线真正调用的两个函数：`DesktopPluginImporterExtension.CalculateFinalPluginPath`（决定拷到哪 / 拷不拷）与 `PluginImporter.GetCompatibleWithPlatformOrAnyPlatform`（决定进不进候选集）。

### 5.1 Windows `.dll` 打开 `Any Platform`，在 macOS 上

| 观察点 | 结果 |
| --- | --- |
| `GetCompatibleWithPlatformOrAnyPlatform` | 对 **每一个** target 都是 `True`，包括 `Editor` 和 `OSXUniversal` |
| `PluginImporter.GetImporters("OSXUniversal")` | **包含**这份 `.dll` |
| `CalculateFinalPluginPath("OSXUniversal", …)` | **`''`（空串）** → `GetCompatiblePlugins` 直接 `continue`，不拷贝 |
| 导入期 Console | **空** |
| Editor 里 P/Invoke 它 | `DllNotFoundException: probe_native assembly:<unknown assembly> type:<unknown type> member:(null)`，Console **零条**附加日志 |

**对照实验**（用来区分「Unity 试着加载了但失败」和「Unity 根本没试」）：把同一份垃圾字节命名为 `.dylib`、正确地标成 macOS + Editor 兼容（`OS: OSX`、`CPU: ARM64`），再 P/Invoke —— **报错一模一样**，Console 同样零条。

> 结论：**Editor 侧不存在「编辑期报错」这个选项**。平台标错、文件格式不对、文件根本不存在，三者在 Editor 里产生完全相同的一句 `DllNotFoundException`。这与文档里 Android 那句「Unity does not validate your settings」是同一件事，只是文档没说它对所有平台都成立。

源码依据：`DesktopPluginImporterExtension.IsUsableOnOSX()` 只认 `bundle` / `dylib` / `xcprivacy` / `.so` / C++ 源文件；`.dll` 落不进去 → `CalculateFinalPluginPath` 直接 `return string.Empty`。**没有任何一条 `Debug.LogWarning` 在这条路径上。**

（一个反直觉的副作用：`IsUsableOnOSX` 里含 `IsLinuxLibrary`，所以一份 Linux `.so` 若被标成 macOS 兼容，最终路径解析为 `libdatachannel_unity.so` —— **会被真的拷进 macOS 包里**。）

### 5.2 平台键名 / group 名写错

对同一份 `.dll` 反复重写 `.meta` 文本再 `ForceUpdate` 重导入：

| `.meta` 里写的 | `GetCompatibleWithPlatform("Win64")` | `OrAnyPlatform` | Console |
| --- | --- | --- | --- |
| `Standalone: Win64` + `CPU: x86_64`（基准） | `True` | `True` | 空 |
| `Standalone: Windows64`（target 拼错） | **`False`** | **`False`** | **空** |
| `Stanalone: Win64`（group 拼错） | **`False`** | **`False`** | **空** |
| 根本没有这条 | `False` | `False` | 空 |

**拼错 == 没写。** 而且 Unity **不会重写 `.meta`**：写进去的 `Standalone: Win64ARM`、`CPU: x86_65`、`OS: Windwos` 在重导入之后仍然逐字留在磁盘上。所以「`.meta` 看起来配了 Windows」和「Unity 认为配了 Windows」之间，肉眼没有任何区别。

### 5.3 `CPU` 值写错

| 写的值 | `Win64` 最终路径 | `OSXUniversal` 最终路径 |
| --- | --- | --- |
| `x86_64`（正确） | `x86_64/…dll` | —— |
| `x86_65`（乱写） | `x86_64/…dll`（**照样拷**） | —— |
| `X86_64`（大小写错） | `x86_64/…dll`（照样拷） | —— |
| 没有 `CPU` 键 | `x86_64/…dll`（照样拷） | —— |
| `None` | **`''`**（静默丢弃） | —— |
| `ARM64`（macOS，正确） | —— | `ARM64/…bundle` |
| `arm64`（macOS，小写） | —— | **`arm64/…bundle`** |

Windows 的路径算法只特判 `None` 和 `ARM64`，其余一律按 target 推出 `x86_64` —— 所以乱写 CPU 在 Windows 上**没有后果**。macOS 走的是通用算法 `Path.Combine(cpu, filename)`，**乱写什么就建什么目录**：小写 `arm64` 会生成 `arm64/` 而不是 `ARM64/`（在大小写敏感的文件系统上就是另一个目录了）。

Unity 唯一一处会抱怨 CPU 值的地方是 `DefaultPluginImporterExtension.Property.ParseStringValue`：

```csharp
Debug.LogWarning("Failed to parse value ('" + valueString + "') for " + key + ", platform: " + platformName + …);
```

但它只在 **Plugin Inspector 被打开**、且该平台处于兼容状态时才跑（`if (inspector.importer.GetCompatibleWithPlatform(platformName))`，注释明写是为了「避免对已禁用平台的过期值刷屏」，case 909247）。**构建、导入、CI 都不经过它。**

### 5.4 Windows ARM64：2022.3 没有这个槽位

`DesktopPluginImporterExtension.CalculateFinalPluginPath`（2022.3 分支）：

```csharp
if (pluginForWindows)
{
    if (string.Compare(cpu, nameof(DesktopPluginCPUArchitecture.ARM64), true) == 0)
    {
        // Windows on Arm64 is not supported for Standalone Windows in this version of Unity
        return string.Empty;
    }
    …
}
```

实测吻合：`Standalone: Win64` + `CPU: ARM64` → 最终路径 `''`，无任何提示。Inspector 侧同理 —— 2022.3 的 Windows 是 `DesktopSingleCPUProperty`（只有 x86 / x86_64），根本没有 ARM64 这个选项。

**分界点在 Unity 6000.0**：同一个文件在 `6000.0` 分支上把注释改成「One build target for 32bit that supports 1 CPU architectue. The other for 64 bit that supports multiple 64 bit architectures (x64 and ARM64)」，并改成 `m_Windows64 = new DesktopMultiCPUProperty(BuildTarget.StandaloneWindows64, x86_64, ARM64)`。

map #46 的矩阵里有 Windows arm64。**在 2022.3 上，那一格的 Standalone 插件没有任何 `.meta` 写法能让它进包**（Editor 侧倒是可以：`Editor` 段的 `OS: Windows` + `CPU: ARM64` 是允许的，`com.unity.collab-proxy` 的 `win-arm64/libonigwrap.dll` 就是这么写的）。

### 5.5 同名文件冲突：两种不同的报错

| 场景 | 谁报 | 什么时候 | 消息 |
| --- | --- | --- | --- |
| 两份同名插件都对 **Editor** 兼容且 `CPU` 无法区分 | Editor 导入管线 | **每次 reimport**（编辑期就能看见） | `Multiple plugins with the same name 'X' (found at 'A' and 'B'). That means one or more plugins are set to be compatible with Editor. Only one plugin at the time can be used by Editor.` |
| 两份同名插件的**构建期最终路径**相同，且都不是「可被项目覆盖」 | `DefaultPluginImporterExtension.CheckFileCollisions` | **构建时** | `Plugin 'X' is used from several locations: … would be copied to <PluginPath>/…` + `Please fix plugin settings and try again.` |

这是本节唯一两处 Unity **真的会吼**的地方，而且都要求「两份同名文件同时存在」。**单份 `.meta` 写错，Unity 永远不吼。**

---

## 6. 不启动 Unity 能不能校验一份 `.meta`

### 6.1 能查的（纯文本，无 Unity）

`.meta` 是 UTF-8 的 YAML 1.1 子集，Unity 写出来的形状高度规整，键值域封闭。以下全部是**可判定**的：

| 类别 | 具体检查 | 依据 |
| --- | --- | --- |
| 结构 | `fileFormatVersion: 2`；`guid` 匹配 `^guid: ([0-9a-f]{32})$`；顶层只有一个 importer 键 | PvpXray `MetaGuidVerifier` 就是这么做的 |
| 存在性/配对 | 每个资产有 `.meta`，每个 `.meta` 有资产；`.bundle`/`.framework`/`.plugin`/`.androidlib` **内部不得有** `.meta` | PvpXray `MetaFileVerifierV4`、PVS `US-0112` |
| guid | 包内唯一 | PvpXray `PVP-27-1` |
| schema 版本 | `serializedVersion: 2`（**不是 3**，否则 2022.3 静默丢弃全部平台配置，§2.4） | 本机实测 |
| 平台键名 | `first` 的 `<group>: <target>` 必须落在 §2.2 的封闭表里 —— **这正是 Unity 完全不查、且写错必然静默失效的那一类**（§5.2） | 实测 + `BuildPipeline` |
| settings 键名与取值 | `CPU` / `OS` / `Exclude *` / iOS 三键 / `Is16KbAligned` 的取值域（§2.3），含 `OS`×`CPU` 的合法组合约束 | `EditorPluginImporterExtension.CanSelectArch` 等 |
| 路径 ↔ 平台一致性 | `Plugins/Windows/x86_64/*.dll` 必须且只能启用 `Standalone: Win64`+`CPU: x86_64`；`.dll` 不得对 macOS/Linux/Editor-non-Windows 兼容；`Any: enabled: 1` 在本仓库应恒为 0 | SPEC §8 的表；`IsUsableOnWindows/OSX/Linux` |
| 跨文件不变量 | 两份 macOS thin bundle 的 `Standalone CPU` 与 `Editor CPU` 必须一个 `ARM64` 一个 `x86_64`（否则 §5.5 两种报错都会来） | 实测 |
| 已知的死格子 | `Standalone: Win64` + `CPU: ARM64` 在 2022.3 上恒为空操作（§5.4） | 2022.3 编辑器源码 |

工具面代价：`.meta` 是 `key: value` + `- ` 列表 + 两空格缩进的普通块式 YAML，**但现成解析器不能无条件直接吃** —— 实测（PyYAML 6，本机）：

| 样本 | `yaml.safe_load` |
| --- | --- |
| 本仓库 `datachannel_unity.bundle.meta`（无排除表） | **OK**，`first` 解析成 `[('Any', None), ('Editor','Editor'), ('Standalone','OSXUniversal')]` |
| `com.unity.visualscripting` 的 `serializedVersion: 3` | **OK** |
| `com.unity.webrtc` 的 `webrtc.framework.meta`（有 `: Any`） | **`ParserError: expected <block end>, but found ':'`** |
| `com.unity.collab-proxy` 的 `libonigwrap.dylib.meta`（有 `: Any`） | **同上，失败** |

坏掉的只有 §2.2 那条**空键**行 `      : Any`。一行正则前处理（把行首的 `: ` 补成 `"": `）之后 PyYAML 解析正常，`first` 变成 `('', 'Any')`。所以代价是「一个脚本 + 一张平台/键值表 + 一条已知的 YAML 前处理」，不是「自己写 YAML 解析器」；CI 里已有 Python 语法检查这一档（SPEC §10）。**本仓库今天所有 `.meta` 都没有排除表，所以这条坑是潜伏的** —— 一旦有人在 Inspector 里勾了 `Any Platform` 再排除某平台，它就会出现。

### 6.2 查不了的

| 查不了 | 为什么 |
| --- | --- |
| 二进制真能被那台机器加载 | 文本里没有这个信息。`.meta` 只说「Unity 应该把它当哪个平台」，不说「它是不是那个平台」 |
| 二进制的架构与 `CPU` 是否相符 | 需要读 Mach-O/PE/ELF 头。这**不是 `.meta` 校验**，是另一件事（离线可做：`lipo -info` / `dumpbin /headers` / `readelf -h`，属于 §10 的 audit 一档） |
| Unity 的实际行为是否与本文表格一致 | 平台扩展是 Editor 内部实现，会随版本改（§2.4 / §5.4 已经是两个活生生的版本漂移例子）。任何校验器都是把 **某个 Unity 版本的实现**固化成一张表，Unity 升级时这张表必须跟着改，且**没有任何机制会提醒你它过期了** |
| `.meta` 写对了、二进制也对，插件就能用 | map #46 已经拍板：「装进 Unity 能加载」在 CI 里物理上验不了，靠每平台至少一次真机 dual-peer smoke |

### 6.3 代价，说清楚

1. **一张手写的平台/键值表**（§2.2 + §2.3）。它的正确性来自本次实测与 2022.3 的编辑器源码，**不来自任何官方 schema —— Unity 没有发布过 `.meta` 的 schema**。
2. **版本漂移无人报警**。§2.4 的 `serializedVersion: 3` 与 §5.4 的 Windows ARM64 说明这张表是有保质期的；表过期时校验器不会变红，只会开始**说谎**（放行错的，或拦住对的）。这是 CONTRIBUTING「让缺席成为失败」原则要盯的形状。
3. **它证明的东西比直觉上小**：它证明的是「这份 `.meta` 说的话，和我们打算说的话一致」。SPEC §8 那张表本身对不对、二进制对不对，都在它的射程之外。

### 6.4 现实校准

本仓库**已经有一个真实的对照组**：`Packages/datachannel-unity/Plugins/macOS/arm64/datachannel_unity.bundle.meta` 从第一次提交起就带着 §4 的三个问题，在一台每天开着 Editor、且插件确实加载成功的机器上，**存活了整个开发期没有被发现** —— 因为它的错误要等到第二个平台入库才会显形。§6.1 表里的「跨文件不变量」那一行，正好是它。

---

## 7. 别人怎么做的

### 7.1 真实包里 `.meta` 是手写还是 Unity 生成？

看不出来，也没必要看出来 —— **它们全都是 Unity 产出的形状**，然后**逐字提交进 git**（`com.unity.webrtc` 的 tarball 里 `Runtime/Plugins/**` 每个二进制旁边都有 `.meta`；`com.unity.collab-proxy`、`com.unity.visualscripting`、Editor 内置的 `com.unity.rendering.denoising` 同理）。几点观察：

- `com.unity.webrtc@3.0.0` 的 `.meta` 里带着大量**历史遗留条目**：`Standalone: Linux`、`Standalone: LinuxUniversal`、`Facebook: Win` / `Facebook: Win64`（Facebook Gameroom 平台早已移除）。说明这些文件是**多年来被不同版本 Unity 反复重写**并原样提交的，没人清理过。
- 同一个包里可以同时存在 `serializedVersion: 2` 和 `3` 两种 schema（`com.unity.visualscripting`），而且**在 2022.3 上后者是坏的**（§2.4）。Unity 官方包也没有守住这条。
- 一个可直接抄的样板：`com.unity.collab-proxy` 的 `libonigwrap` —— 五份同名原生库，靠 Editor 段 `OS`+`CPU` 区分（§4）。

### 7.2 有没有人做过 `.meta` 的自动校验？

**Unity 自己做了，而且是无 Unity 的。** `com.unity.package-validation-suite@0.85.0-preview` 里有两层：

| 层 | 需要 Unity？ | 关于 `.meta` 的检查 |
| --- | --- | --- |
| ValidationSuite（Editor 内）`MetaFilesValidation` → `US-0112 PackageContainsMetafile` | 是 | **只查存在性**：每个文件/目录有没有对应的 `.meta`。带 `.` 前缀、`~` 结尾、`node_modules` 跳过；`.androidlib`/`.bundle`/`.plugin`/`.framework` 目录**不递归进去** |
| **PvpXray**（`Lib/PvpXray/`，纯 .NET、不依赖 Editor） | **否** | `PVP-26-4`（`MetaFileVerifierV4`）：资产↔`.meta` 双向配对、不得为隐藏资产配 `.meta`、插件目录内部豁免；`PVP-27-1`（`MetaGuidVerifier`）：`^guid: ([0-9a-f]{32})$` 且包内唯一 |

**两层加起来都不看 `PluginImporter` 一眼。** 全仓库 grep `PluginImporter` 只命中一条注释（`Verifier.cs` 里引用 `PluginImporter::GetLoadableDirectoryExtensionTypes` 来解释那四个插件目录后缀）和一个自身的 `.meta`。

也就是说：**「无 Unity 校验 `.meta`」这条路 Unity 自己走过并且在产线上跑着，只是他们止步于存在性与 guid，没有碰平台开关。** 平台开关那部分没有先例可抄，但也没有技术障碍 —— §5 已经证明它是纯文本可判定的。

---

## 8. 这些事实碰到了 SPEC 的哪些地方（只陈述，不改）

| SPEC | 事实 |
| --- | --- |
| §8 树 | 缺 `Plugins/Linux/x86_64/`（map #46 已知要补）。macOS 两份 thin bundle **同名**，因此 §3.3/§3.4 的 `CPU` 字段不是可选项而是必需项 |
| §8「Explicit `.meta` for every plugin」 | 成立，且要加一条限定：`.bundle` 内部**不得**有 `.meta`（§2.5） |
| §8 表「Windows arm64 → Editor: Yes (matching CPU)」 | Editor 侧成立；**Standalone 侧在 2022.3 不成立**（§5.4） |
| §10「CI 无 Unity」 | `.meta` 的文本校验不需要 Unity（§6.1）；但它替代不了真机 smoke（§6.2） |
| §11「让缺席成为失败」 | §5 的全部错误形态都是「静默」，即**天然的「跑了/没跑」不可区分**。§6.3 第 2 条（表过期时校验器开始说谎）是这个病的第五种脸 |

---

## 附录 A：复现步骤

1. **真实包样本**（无需 Unity）：
   ```bash
   curl -sSL -o /tmp/webrtc.tgz https://download.packages.unity.com/com.unity.webrtc/-/com.unity.webrtc-3.0.0.tgz
   tar xzf /tmp/webrtc.tgz -C /tmp/webrtc && find /tmp/webrtc -name '*.meta' -path '*Plugins*'
   curl -sSL -o /tmp/pvs.tgz https://download.packages.unity.com/com.unity.package-validation-suite/-/com.unity.package-validation-suite-0.85.0-preview.tgz
   ```
   本机另有现成样本：`Library/PackageCache/com.unity.collab-proxy@2.12.4/Lib/Editor/TextMateSharp/Onigwrap/libonigwrap/*/`、
   `Library/PackageCache/com.unity.visualscripting@1.9.4/Editor/VisualScripting.Core/Dependencies/Assemblies/WIN*/`、
   `/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/Resources/PackageManager/BuiltInPackages/com.unity.rendering.denoising/Runtime/Plugin/*/`。

2. **编辑器源码**（无需 Unity）：
   ```bash
   for f in Editor/Mono/ImportSettings/DesktopPluginImporterExtension.cs \
            Editor/Mono/ImportSettings/EditorPluginImporterExtension.cs \
            Editor/Mono/Modules/DefaultPluginImporterExtension.cs \
            Modules/AssetPipelineEditor/Public/PluginImporter.bindings.cs; do
     curl -sSO https://raw.githubusercontent.com/Unity-Technologies/UnityCsReference/2022.3/$f
   done
   ```

3. **本机 Editor 实测**（Unity MCP `execute_code`）：在 `Assets/` 下建一组占位文件（`.dll` / `.so` / `.a` / `.jslib` / `.bundle` 目录），`AssetDatabase.Refresh` 后用 `PluginImporter` API 配置 → `SaveAndReimport` → 直接读磁盘上的 `.meta`。判定用
   `UnityEditor.DesktopPluginImporterExtension.CalculateFinalPluginPath(target, importer)`（反射构造）与
   `PluginImporter.GetCompatibleWithPlatformOrAnyPlatformBuildTarget(target)`（反射，internal）。
   探针资产用完即删（本次已删，工作树无残留）。

4. **YAML 可解析性**（无需 Unity）：
   ```bash
   python3 -c "import yaml; yaml.safe_load(open('<某个带 : Any 的 .meta>'))"   # ParserError
   python3 -c "
   import yaml, re
   t = open('<同一个文件>').read()
   print(yaml.safe_load(re.sub(r'(?m)^(\s+): (\S)', r'\1\"\": \2', t))['PluginImporter']['platformData'][0]['first'])
   "   # -> {'': 'Any'}
   ```
