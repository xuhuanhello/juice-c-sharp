# Research: Unity 2022.3 的 Android 打包链对预编译 `.so` 的处置（AGP 7.4.2 / `useLegacyPackaging` / zipalign）

| Field | Value |
| --- | --- |
| Ticket | [#77](https://github.com/xuhuanhello/juice-c-sharp/issues/77)（part of map [#76](https://github.com/xuhuanhello/juice-c-sharp/issues/76)） |
| Date | 2026-08-05 |
| Versions studied | **AGP 7.4.2 / Gradle 7.5.1**（Unity 2022.3.38f1+ 自带，维护者实机确认，本文另附 Unity 官方文档佐证）；本机 Editor 2022.3.62f3 |
| Subject | SPEC §16 的 16 KB 对齐三层里的**第二层与第三层**；map [#76](https://github.com/xuhuanhello/juice-c-sharp/issues/76) Notes 那张表 |
| Method | **读 AGP 7.4.2 自己发布的 sources jar**（Google Maven，版本钉死）+ Android / Unity 官方文档。无 Android Build Support，Editor 侧文件一律未验 |
| Status | 完成。三问全部有判定；两条「只能拆真 APK 才知道」已单列交棒 [#82](https://github.com/xuhuanhello/juice-c-sharp/issues/82)，一条「只能读 `AndroidPlayer/` 才知道」交棒 [#78](https://github.com/xuhuanhello/juice-c-sharp/issues/78) |

**一句话结论**：开图时那个猜测 **证实了，但证实的是一条比猜测更长的链** —— AGP 7.4.2 的 `useLegacyPackaging` 默认值确实是 `minSdk < 23`，本仓库 `AndroidMinSdkVersion: 22` 落在压缩侧；但 **AGP 7.4.2 真正据以打包的不是这个 DSL 值，而是合并后 manifest 里的 `android:extractNativeLibs`**，DSL 只负责决定要不要往 manifest 里注入那个属性、以及在两者打架时打一条 warning。第三层（`zipalign -P 16`）在这条链上**不是「默认没开」而是「结构上不存在」**：AGP 7.4.2 从不调用 `zipalign` 二进制，对齐是它自己在进程内做的，常量 `4096` 硬编码，整个 7.4.2 源码树里 `16384` **零命中**。而 Google 官方要求的第三层是**有前提的** —— 它只约束「以**非压缩**形态随包发布的 `.so`」。两件事合起来指向一个反直觉的推论（§5，**标为推断**）：在 Unity 2022.3 默认配置下第二层「失败」，恰恰使第三层**变得无关**，我们握住的第一层就是充分的。这条推论是本文最值钱也最需要真机证伪的一条。

---

## 0. Verdict 速查

| 问题 | 判定 | 分级 |
| --- | --- | --- |
| **Q1** AGP 7.4.2 `jniLibs.useLegacyPackaging` 默认值随 minSdk 在 23 翻转？ | **证实**。默认 `null`，落到 `minSdk < 23` → `true`（压缩）。见 §2.1，读的是 7.4.2 自己的源码 | **源码**（版本钉死） |
| 本仓库落在哪一侧？ | **压缩侧**。`ProjectSettings/ProjectSettings.asset:176` = `AndroidMinSdkVersion: 22` | **实测**（本地文件） |
| 但 AGP 7.4.2 真的按这个 DSL 值打包吗？ | **不是**。它按**合并后 manifest 的 `extractNativeLibs`** 打包，DSL 值只驱动 manifest 注入 + 一条 warning。源码里带 `TODO (b/149770867)` 明说这是过渡态。见 §2.2 | **源码** |
| 那谁能翻盘？ | 任何往 manifest 写 `android:extractNativeLibs="false"` 的人**都能**，minSdk 22 也照样生效（代价：一条 warning）。见 §2.3 | **源码** |
| **Q2** Unity 2022.3 的模板/manifest 里写死了这两个值吗？ | **查不到**。本机无 `AndroidPlayer/`，读不了实际文件。文档侧只能给出「Unity 2022.3 一个字都没提这两个键」这个**负面证据**。见 §4 | **查不到** |
| 采用者能不能改、改哪个文件？ | **能**，五个模板 + Custom Main Manifest，全在 Publishing Settings 里开关。具体改哪个取决于 Q2 的答案。见 §4.2 | **文档** |
| **Q3** `zipalign -P 16` 在这条链上跑不跑？ | **跑不了，也没人跑**。AGP 7.4.2 不调用 `zipalign` 二进制；`isZipAlignEnabled` 被标 `@Deprecated("no longer has any effect")`；对齐常量硬编码 `4096`；`16384` 全树零命中。见 §3 | **源码** |
| 那要几版 AGP 才行？ | **8.5.1+**（Google 官方原话）。Unity 2022.3 永远到不了 | **文档** |
| 第二层失败时第三层还成立吗？ | **不成立，且无关** —— ApkFlinger 在 `COMPRESSED` 模式下 `pageAlignPredicate = { false }`，`.so` 在 zip 里连 4 KB 都不对齐（退回 4 字节）。而 Google 的第三层要求只约束非压缩 `.so`。见 §5 | **源码** + **文档** |
| **所以我们的链接期 `-Wl,-z,max-page-size=16384` 够不够？** | **推断：够**，因为压缩存放的 `.so` 会被安装器解压到文件系统，此后只有 ELF LOAD 对齐说话。**这条必须真机证伪** → [#82](https://github.com/xuhuanhello/juice-c-sharp/issues/82)。见 §5 | **推断** |
| 旁证：Unity 自己怎么在 AGP 7.4.2 上支持 16 KB 的？ | Unity **2022.3.56f1** 加了 16 KB 支持，而 2022.3 全程 AGP ≤ 7.4.2。这只有在「压缩路径够用」的前提下自洽。**是旁证不是证明** | **推断** |
| AAB 路径一样吗？ | **不一样**。`useLegacyPackagingFromBundle` 默认 **`false`**，与 minSdk 无关 → 出 AAB 时 `.so` 非压缩。此时第三层**重新变得要命**，而 AGP 7.4.2 只会给 4 KB。见 §6 | **源码** |
| `Is16KbAligned` 该写什么？ | **本票答不了**，但缩小了：它是 **Editor 侧**的东西，跟 AGP 那条链没有交点。见 §7 | **查不到** |

---

## 1. 一手来源

| 来源 | 覆盖什么 |
| --- | --- |
| **AGP 7.4.2 sources jar**，从 Google Maven 拉的（复现命令见下）：`com.android.tools.build:gradle-api:7.4.2`、`:gradle:7.4.2`、`:builder:7.4.2`、`:apkzlib:7.4.2` | §2、§3、§5、§6 的全部结论。**版本钉死**，不是从别的版本外推 |
| [Support 16 KB page sizes — Android Developers](https://developer.android.com/guide/practices/page-sizes) | 三层要求逐条；AGP 8.5.1 门槛；`zipalign -P 16` 的用法与它是**校验**命令这件事；backcompat 模式 |
| [AGP 3.6.0 release notes — Native libraries packaged uncompressed by default](https://developer.android.com/build/releases/past-releases/agp-3-6-0-release-notes) | `extractNativeLibs` 默认翻转的**起点**，以及它「page aligned and packaged uncompressed」的措辞 |
| [AGP 4.2.0 release notes — Use the DSL to package compressed native libraries](https://developer.android.com/build/releases/past-releases/agp-4-2-0-release-notes) | `useLegacyPackaging` 取代 `extractNativeLibs` 这件事的官方声明 |
| [AGP DSL reference — `JniLibsPackagingOptions`](https://developer.android.com/reference/tools/gradle-api/8.0/com/android/build/api/dsl/JniLibsPackagingOptions) | 与 7.4.2 源码 KDoc **逐字一致**的公开文档面（见 §2.1 的注意事项） |
| [Unity 2022.3 Manual — Gradle for Android](https://docs.unity3d.com/2022.3/Documentation/Manual/android-gradle-overview.html) | Gradle/AGP 版本对照表（**佐证了维护者给的既定事实，并暴露了一条分界线**，见 §4.1）；五个模板各自管什么 |
| [Unity 2022.3 Manual — Android requirements and compatibility](https://docs.unity3d.com/2022.3/Documentation/Manual/android-requirements-and-compatibility.html) | 「supports Android 5.1 (API level 22) and above」；16 KB 支持要求 **2022.3.56f1+**；Editor 会对 4 KB 对齐的插件 `.so` 报 warning |
| [Unity 2022.3 Manual — Android Player Settings](https://docs.unity3d.com/2022.3/Documentation/Manual/class-PlayerSettingsAndroid.html) | Publishing Settings 里五个模板 + Custom Main Manifest 的开关与各自对应的文件名 |
| [Unity 2022.3 Manual — Android App Manifest](https://docs.unity3d.com/2022.3/Documentation/Manual/android-manifest.html) | manifest 合并的输入有哪些；**通篇不含 `extractNativeLibs`** |
| 本仓库 `ProjectSettings/ProjectSettings.asset`（本地实测） | `AndroidMinSdkVersion: 22`、`AndroidTargetArchitectures: 1`、`AndroidBuildApkPerCpuArchitecture: 0` |

**复现命令**（任何人都能在一分钟内重跑本文 §2/§3/§5/§6 的全部源码结论）：

```sh
cd /tmp && mkdir agp742 && cd agp742
for a in gradle-api gradle builder apkzlib; do
  curl -sSLO "https://dl.google.com/dl/android/maven2/com/android/tools/build/$a/7.4.2/$a-7.4.2-sources.jar"
done
for a in gradle-api gradle builder apkzlib; do mkdir -p x/$a && (cd x/$a && unzip -qo ../../$a-7.4.2-sources.jar); done
grep -rn 16384 x/          # → 零命中，这就是第三层的判决
```

> **一条方法论上的注意**：`developer.android.com/reference/tools/gradle-api/7.4/...` 这个**版本钉死的文档 URL 现在会被重定向到当前版本（9.3）的页面**，抓下来的是 9.x 的内容。想引 7.4 的原文，要么去读 7.4.2 的 sources jar（本文的做法），要么明确标注引的是哪个版本的页面。本文引 8.0 的 DSL 页只作为「公开文档与源码 KDoc 逐字一致」的佐证，**判定依据是 7.4.2 的源码本身**。

---

## 2. Q1：`useLegacyPackaging` 的默认值

### 2.1 判定：**证实** —— 猜测成立，且能钉到行

`gradle-api-7.4.2-sources.jar` → `com/android/build/api/dsl/JniLibsPackagingOptions.kt`，**逐字**：

```kotlin
/** Packaging options for native library (.so) files */
@Incubating
interface JniLibsPackagingOptions {
    /**
     * Whether to use the legacy convention of compressing all .so files in the APK. If null, .so
     * files will be uncompressed and page-aligned when minSdk >= 23.
     */
    var useLegacyPackaging: Boolean?
```

类型是 `Boolean?`，**默认值是 `null`（未设），不是 `false`**。真正做决定的是实现，`gradle-7.4.2-sources.jar` → `com/android/build/api/variant/impl/JniLibsApkPackagingImpl.kt`：

```kotlin
import com.android.sdklib.AndroidVersion.VersionCodes.M

class JniLibsApkPackagingImpl(
    dslPackagingOptions: PackagingOptions,
    variantServices: VariantServices,
    minSdk: Int
) : JniLibsPackagingImpl(dslPackagingOptions, variantServices),
    JniLibsApkPackaging {

    override val useLegacyPackaging =
        variantServices.provider {
            dslPackagingOptions.jniLibs.useLegacyPackaging ?: (minSdk < M)
        }
```

`M` = API 23。`?: (minSdk < M)` 就是那个翻转点，**没有第二个条件**。

代入本仓库：`ProjectSettings/ProjectSettings.asset:176` 是 `AndroidMinSdkVersion: 22`，`22 < 23` → `useLegacyPackaging = true` → 走 legacy（压缩）。

> 开图时的原话是「minSdk ≥ 23 才默认不压缩存放」。**这句话本身准确**。分级从「推断」升到「源码明载」。

### 2.2 但这不是打包时真正读的值 —— 一条猜测里没有的链

猜测隐含「DSL 值 → 打包行为」是直连的。**在 AGP 7.4.2 上不是**。实际是两跳：

**第一跳，DSL → manifest**。`gradle-7.4.2` → `com/android/build/gradle/tasks/ProcessApplicationManifest.kt:187-193`：

```kotlin
optionalFeatures.get().plus(
    mutableListOf<Invoker.Feature>().also {
        if (!jniLibsUseLegacyPackaging.get()) {
            it.add(Invoker.Feature.DO_NOT_EXTRACT_NATIVE_LIBS)
        }
    }
),
```

只有在 `useLegacyPackaging == false` 时才让 manifest merger 注入 `android:extractNativeLibs="false"`。我们这边是 `true` → **什么都不注入**。

**第二跳，manifest → 打包**。`com/android/build/gradle/tasks/PackageAndroidArtifact.java:817-820`：

```java
NativeLibrariesPackagingMode nativeLibsPackagingMode =
        PackagingUtils.getNativeLibrariesLibrariesPackagingMode(
                manifestData.getExtractNativeLibs());
// Warn if params.getJniLibsUseLegacyPackaging() is not compatible with
// nativeLibsPackagingMode. We currently fall back to what's specified in the manifest, but
// in future versions of AGP, we should use what's specified via
// params.getJniLibsUseLegacyPackaging().
```

注意那句注释：**「We currently fall back to what's specified in the manifest」**。往下是两条对称的 warning（`TODO (b/149770867) make this an error in future AGP versions`），但**只是 warning，不改行为**。

而 `builder-7.4.2` → `com/android/builder/packaging/PackagingUtils.java:239-248`：

```java
public static NativeLibrariesPackagingMode getNativeLibrariesLibrariesPackagingMode(
        @Nullable Boolean extractNativeLibs) {
    // The default is "true", so we only package *.so files differently if
    // android:extractNativeLibs is explicitly set to "false".
    if (Boolean.FALSE.equals(extractNativeLibs)) {
        return NativeLibrariesPackagingMode.UNCOMPRESSED_AND_ALIGNED;
    } else {
        return NativeLibrariesPackagingMode.COMPRESSED;
    }
}
```

完整链条（minSdk 22）：

```
useLegacyPackaging = null ─→ minSdk 22 < 23 ─→ true
   └→ ProcessApplicationManifest：不注入 DO_NOT_EXTRACT_NATIVE_LIBS
        └→ 合并后 manifest 里没有 extractNativeLibs 属性
             └→ PackagingUtils：属性不是显式 false ⇒ COMPRESSED
                  └→ .so 被 deflate 压缩进 APK，且不做页对齐（§3.2）
```

### 2.3 推论：manifest 侧能翻盘，DSL 侧也能

因为**最终裁决权在 manifest**，两条路都通，而且互不依赖：

| 想要非压缩存放 | 怎么做 | 在 AGP 7.4.2 上生效吗 |
| --- | --- | --- |
| 抬 minSdk 到 23+ | Player Settings 的 Minimum API Level | **生效**（DSL 翻 false → 注入 manifest → 打包读到 false） |
| 显式写 DSL | `mainTemplate`/`launcherTemplate` 里 `packagingOptions { jniLibs { useLegacyPackaging false } }` | **生效**，同上，且与 minSdk 无关 |
| 直接写 manifest | Custom Main Manifest 里 `android:extractNativeLibs="false"` | **生效**，minSdk 22 也生效，**代价是一条 warning**（`PackagingOptions.jniLibs.useLegacyPackaging should be set to false because …`） |

第三行值得记一笔：map [#76](https://github.com/xuhuanhello/juice-c-sharp/issues/76) 的判断是「AAR 携带不了第二层 —— 库 manifest 写的 `extractNativeLibs` 会被 AGP 按自己的打包决策注入/覆盖」。**在 AGP 7.4.2 上这个判断需要一点修正**：AGP 7.4.2 并不覆盖 manifest 的显式值，它**服从**这个值，只是抱怨。真正会把库 manifest 的意愿推翻的是 manifest merger 的合并规则（app 模块与库模块冲突时需要 `tools:replace`），不是打包任务。**结论没变**（AAR 仍然只能声明一个可能被上层推翻的意愿，且我们本来就不出 AAR），但理由要换成 merger 那一层。

---

## 3. Q3：`zipalign -P 16` 由谁执行

### 3.1 判定：**在这条链上没有任何环节执行它**

**(a) AGP 7.4.2 根本不调用 `zipalign` 二进制。** 全树 `grep -i zipalign` 只命中两类：`BuildTypeImpl`/`ide` 里的建模字段，和这条：

`gradle-api-7.4.2` → `com/android/build/api/dsl/BuildType.kt:250-251`：

```kotlin
@Deprecated("Changing the value of isZipAlignEnabled no longer has any effect")
var isZipAlignEnabled: Boolean
```

对齐是 AGP 在**自己进程内**做的，从来不 fork build-tools 的 `zipalign`。因此「`zipalign` 跟 build-tools 版本什么关系」这个问法在 AGP 链上**不适用** —— build-tools 里那个 `zipalign` 二进制在 AGP 构建中扮演的角色是**事后校验工具**（Google 那篇文档给的 `zipalign -v -c -P 16 4 APK` 带 `-c` = check），不是打包工具。

**(b) 默认打包器是 ApkFlinger，它的页对齐常量硬编码 4096。**

`gradle-7.4.2` → `com/android/build/gradle/options/BooleanOption.kt:213`：

```kotlin
USE_NEW_APK_CREATOR("android.useNewApkCreator", true, FeatureStage.SoftlyEnforced(VERSION_8_0)),
```

默认 `true` → `GlobalTaskCreationConfigImpl.kt:250-255` 选 `ApkCreatorType.APK_FLINGER`。

`builder-7.4.2` → `com/android/builder/internal/packaging/ApkFlinger.kt:291`：

```kotlin
private const val PAGE_ALIGNMENT = 4096L
```

**(c) 备用打包器 apkzlib 同样硬编码 4096。**

`apkzlib-7.4.2` → `com/android/tools/build/apkzlib/zfile/ApkZFileCreator.java:40-42`：

```java
/** Shared libraries are alignment at 4096 boundaries. */
private static final AlignmentRule SO_RULE =
    AlignmentRules.constantForSuffix(NATIVE_LIBRARIES_SUFFIX, 4096);
```

**(d) 决定性的负面证据**：`grep -rn 16384` 跑遍 `gradle-api` + `gradle` + `builder` + `apkzlib` 的 7.4.2 全部源码 → **零命中**。这不是「默认关着的开关」，是**这个版本里不存在这个数**。

### 3.2 附带发现：压缩模式下连 4 KB 都不对齐

`ApkFlinger.kt:83-99`：

```kotlin
when (creationData.nativeLibrariesPackagingMode) {
    NativeLibrariesPackagingMode.COMPRESSED -> {
        noCompressPredicate = creationData.noCompressPredicate
        pageAlignPredicate = Predicate { false }
    }
    NativeLibrariesPackagingMode.UNCOMPRESSED_AND_ALIGNED -> {
        ...
        pageAlignPredicate =
            Predicate { it?.endsWith(SdkConstants.DOT_NATIVE_LIBS) ?: false }
    }
```

配合 `ApkFlinger.kt:208-212`：`pageAlignPredicate` 为假时走 `source.align(DEFAULT_ALIGNMENT)`，注释写着「by default all uncompressed entries are aligned at 4 byte boundaries」。

也就是说，map 表里第二层与第三层**不是两个独立的开关，是一个二选一的分支**：`COMPRESSED` 分支下第三层的代码路径根本不执行。

### 3.3 Google 官方那篇文档要求的到底是哪几件事

逐条列，并标注归谁管：

| # | 要求（官方原文） | 归谁 | 我们的状态 |
| --- | --- | --- | --- |
| 1 | 「16 KB devices require the shared libraries' ELF segments to be aligned properly using 16 KB ELF alignment」；NDK r27 及以下用 `-Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384` | **链接期，我们的活** | 第一层。CI 用 NDK 27.3（见 `.github/workflows/plugins-matrix.yml:226` 的注释），需显式加 flags |
| 1b | 「**If your app uses any prebuilt shared libraries, you must also recompile them in the same way**」 | **我们**（我们正是别人的 prebuilt） | 这句就是本包存在的理由 |
| 2 | 「16 KB devices require apps that ship with **uncompressed** shared libraries to align them on a 16 KB zip-aligned boundary. To do this, you need to upgrade to Android Gradle Plugin (AGP) version **8.5.1 or higher**」 | **app 模块的 AGP** | 第二+三层。AGP 7.4.2 **做不到**，且注意这条**以「uncompressed」为前提** → §5 |
| 3 | 「If you can't upgrade AGP to version 8.5.1 or higher, then the alternative is to **switch to use compressed shared libraries**」（`jniLibs { useLegacyPackaging true }`） | app 模块 | **Google 自己给的退路，正好就是 Unity 2022.3 的默认状态** |
| 4 | 移除对 `PAGE_SIZE` 常量的硬依赖，改用 `getpagesize()` / `sysconf(_SC_PAGESIZE)`；检查 `mmap()` 等需要页对齐参数的调用 | **上游代码（libdatachannel / libjuice / usrsctp / OpenSSL）** | **未查**。本票没覆盖，值得单独看一眼 |
| 5 | `GNU_RELRO` 段必须存在（「Combining a RELRO-enabled section with a non-RELRO-enabled section … crashes an app」），且**每个** `.so` 都要查 | 链接期，我们的活 | **未查**。`audit_plugin.py` 现在不看这个，加一条断言很便宜 |
| 6 | 校验手段：`llvm-objdump -p x.so \| grep LOAD` 要看到 `align 2**14`；`zipalign -v -c -P 16 4 app.apk` 要 Verification successful（需 build-tools **35.0.0+**） | 验收方 | 交棒 [#82](https://github.com/xuhuanhello/juice-c-sharp/issues/82)，正好定义「什么算数」 |
| 7 | Play 政策：targeting API 35+ 的应用必须支持 16 KB；「**Starting February 1, 2027**, if your app updates don't support 16 KB memory page sizes, you won't be able to release these updates」 | 采用者 | 仓库注释里记的是 2025-11-01（新应用/更新的门槛），官方页面现在给的强制截止是 2027-02-01。两个日期是不同口径，**注释可以补一句** |

> 第 4 条和第 5 条是本票**顺带捞出来的、原本不在三个子问题里**的要求。第 5 条尤其便宜 —— `readelf -l` 已经在 audit 链上了。

---

## 4. Q2：Unity 2022.3 的模板与 manifest

### 4.1 先修正一件版本上的事

Unity 官方文档 [Gradle for Android](https://docs.unity3d.com/2022.3/Documentation/Manual/android-gradle-overview.html) 的对照表把 2022.3 **切成两段**：

| Unity 版本区间 | Gradle | AGP |
| --- | --- | --- |
| **2022.3.38f1+** | 7.5.1 | **7.4.2** |
| 2022.2.0a18 – **2022.3.37f1** | 7.2 | **7.1.2** |

维护者给的「Unity 2022.3 自带 AGP 7.4.2」**在 2022.3.38f1 及以后成立**，本机 2022.3.62f3 落在这一段。但 SPEC 若要写「Unity 2022.3 = AGP 7.4.2」，**得带上 `.38f1+` 这个下界**，否则对 2022.3.0–.37 的采用者是错的。

顺带：**16 KB 支持要 2022.3.56f1+**（Unity 官方 [Android requirements and compatibility](https://docs.unity3d.com/2022.3/Documentation/Manual/android-requirements-and-compatibility.html)：「Update Unity to 2022.3.56f1 or later version」）。这个下界比 `.38f1` 更高，所以**对本包而言，2022.3.56f1 才是真正的下界**。这条建议进 SPEC。

### 4.2 能改什么、改哪个文件（**文档明载**）

[Android Player Settings](https://docs.unity3d.com/2022.3/Documentation/Manual/class-PlayerSettingsAndroid.html) 的 Publishing Settings 里，六个相关开关：

| 开关 | 生成的文件 | 落在 | 管什么 |
| --- | --- | --- | --- |
| Custom Main Gradle Template | `mainTemplate.gradle` | `Assets/Plugins/Android/` | `unityLibrary` 模块 —— 「how to build your Android application as a library」 |
| Custom Launcher Gradle Template | `launcherTemplate.gradle` | 同上 | `launcher` 模块 —— 「instructions on how to build your Android application」 |
| Custom Base Gradle Template | `baseProjectTemplate.gradle` | 同上 | 「configuration that's shared between all other templates and Gradle projects」 |
| Custom Gradle Properties Template | `gradleTemplate.properties` | 同上 | Gradle 构建环境；文档明说其中一项是「to avoid compressing native libs when building an app bundle」 |
| Custom Gradle Settings Template | `settingsTemplate.gradle` | 同上 | `settings.gradle`，模块清单 |
| Custom Main Manifest | Unity Library Manifest（`AndroidManifest.xml`） | 同上 | §2.3 第三行那条路 |

**关键定位**：`useLegacyPackaging` 属于 `android { packagingOptions { … } }`，而**打包 APK 的是 `launcher` 模块**，不是 `unityLibrary`。所以要在 Unity 侧动第二层，**首选是 `launcherTemplate.gradle`**，不是采用者更常听说的 `mainTemplate.gradle`。这条是**从 AGP 语义 + Unity 模块划分推断**的，未经实际构建验证。

那条 gradle.properties 里的键，从 Google 文档反查就是 `android.bundle.enableUncompressedNativeLibs`（Google 16 KB 那篇给 AGP ≤ 8.0 的退路正是把它设 `false`）。它在 AGP 7.4.2 里的默认值是 **`true`**（`BooleanOption.kt:232-237`）—— 但 **Unity 的默认模板把它写成了什么，查不到**（见 §4.3）。

### 4.3 判定：**查不到** —— 而且是有边界的查不到

本机（mac）**没装 Unity 的 Android Build Support**：

```
/Applications/Unity/Hub/Editor/2022.3.62f3/PlaybackEngines/
└── iOSSupport          ← 只有这一个
```

`PlaybackEngines/AndroidPlayer/Tools/GradleTemplates/` 下的实际模板文件、以及 Unity 自己那份 `AndroidManifest.xml` 模板，**本会话读不到，不猜**。交棒 [#78](https://github.com/xuhuanhello/juice-c-sharp/issues/78)。

文档侧我能提供的是**三条负面证据**，它们缩小了范围但不构成答案：

1. Unity 2022.3 的 [Android App Manifest](https://docs.unity3d.com/2022.3/Documentation/Manual/android-manifest.html) 页**通篇不含 `extractNativeLibs`**。该页列举 Unity 会自动往 manifest 里加什么（各类权限、配置项），`extractNativeLibs` 不在其列。
2. Unity 2022.3 的 ScriptReference 里**没有 `Unity.Android.Gradle` 这个命名空间** —— `Unity.Android.Gradle.JniLibs.UseLegacyPackaging` 在 2022.3 文档下是 **404**，在 **6000.0 下存在**，且其描述与 AGP 的 KDoc **逐字相同**（「Whether to use the legacy convention of compressing all .so files in the APK. If null, .so files will be uncompressed and page-aligned when minSdk >= 23.」）。同理 `Unity.Android.Gradle.Manifest.Application.AttributesContainer.ExtractNativeLibs` 也只在 6000.0 有。
   → **推论**：Unity 2022.3 **没有**给这两个值提供脚本化入口，只能靠模板文本。Unity 6 才把它们提升成一等公民。
3. Unity 2022.3 的 Player Settings 文档在描述 `gradleTemplate.properties` 时**只提到 app bundle 那一项**与原生库压缩有关，没有提 APK 路径的任何等价物。

**#78 该带回来的三个具体答案**（把问题问死，避免那张票再开一次研究）：

- `AndroidPlayer/Tools/GradleTemplates/launcherTemplate.gradle` 里有没有 `packagingOptions` / `jniLibs` / `useLegacyPackaging` 字样？有的话值是什么？
- Unity 那份 Library / Launcher `AndroidManifest.xml` 模板里，`<application>` 上有没有 `android:extractNativeLibs`？值是什么？
- `AndroidPlayer/Tools/GradleTemplates/gradleTemplate.properties` 里 `android.bundle.enableUncompressedNativeLibs` 写没写、写成什么？

---

## 5. 三层的真实关系 —— 本文最重要、也最需要证伪的一节

map [#76](https://github.com/xuhuanhello/juice-c-sharp/issues/76) Notes 那张表把三层并列，读起来像是三个都要满足。**从 Google 自己的措辞和 AGP 的代码看，它们不是并列的**：

> 「16 KB devices require apps that ship with **uncompressed** shared libraries to align them on a 16 KB zip-aligned boundary.」

第三层的约束对象是**以非压缩形态随包发布**的 `.so`。Google 在同一页给的官方退路更直白：

> 「If you can't upgrade AGP to version 8.5.1 or higher, then the alternative is to **switch to use compressed shared libraries**.」

配上 §3.2 的代码（`COMPRESSED` 分支下 `pageAlignPredicate = { false }`，压根不走对齐路径），三层的真实形状是：

```
第一层  ELF LOAD 段 16 KB 对齐   ← 无条件必需，链接期，我们的活
   │
   └─ 第二层 .so 是否非压缩存放？
        ├─ 压缩（AGP legacy，Unity 2022.3 默认）
        │     → 安装器把 .so 解压到文件系统，从文件 dlopen
        │     → zip 内偏移与对齐**与运行时无关**
        │     → 第三层不适用。代价：安装体积变大
        │
        └─ 非压缩（minSdk ≥ 23，或显式设置）
              → 直接从 APK 内 mmap
              → **第三层变成硬要求**：zip 条目须 16 KB 对齐
              → AGP 7.4.2 只给 4096 ⇒ 死路
```

**推论（分级：推断，未实测）**：在 Unity 2022.3 的默认配置（minSdk 22 → 压缩）下，**我们只需要握住第一层**。第二层的「失败」不是我们的损失，它反而把第三层那个 AGP 7.4.2 够不着的要求**绕过去了**。

**支持这条推论的三样东西**：

1. Google 把「改用压缩共享库」明确列为 AGP ≤ 8.5 的**官方退路**，而不是「凑合能跑」。
2. AGP 代码里两个分支互斥，压缩分支不碰对齐（§3.2）。
3. **旁证**：Unity 在 **2022.3.56f1** 交付了 16 KB 支持，而 2022.3 整条线的 AGP 最高只到 7.4.2 —— 一个连 `16384` 这个数都没有的 AGP。Unity 能在这条链上声称支持 16 KB，与「压缩路径够用」自洽；若第三层是无条件必需的，Unity 这次交付在技术上无法成立（除非 Unity 自己做了 AGP 之外的后处理 —— **这个可能性没被排除**，见下）。

**这条推论没有排除的两件事，正是 [#82](https://github.com/xuhuanhello/juice-c-sharp/issues/82) 该拆 APK 去看的**（§8）。

**一个必须一起记住的连带结论**：如果将来因为别的理由把 minSdk 抬到 23+（map [#76](https://github.com/xuhuanhello/juice-c-sharp/issues/76) 已拍板「抬到 23 甚至 24 都可接受」），**第二层会翻到非压缩侧，第三层随即变成硬要求，而 AGP 7.4.2 给不出 16 KB**。也就是说：

> **在 Unity 2022.3 上，抬 minSdk 到 23+ 可能让 16 KB 兼容性变差，而不是变好。**

这与「抬 minSdk 是安全的保守动作」的直觉相反，且直接约束 `ANDROID_PLATFORM` 那张票 —— 它现在不只是「取几能编过」的纯事实问题，还多了一条**上界方向的风险**。这条同样是**推断**，同样等 [#82](https://github.com/xuhuanhello/juice-c-sharp/issues/82) 定夺；但在定夺之前，**不要**因为「保守」就顺手把 minSdk 抬到 23。

---

## 6. AAB 路径是另一条链（**源码明载**）

Unity 可以出 APK，也可以出 AAB（Build App Bundle）。两条路的默认值**不同**：

`JniLibsApkPackagingImpl.kt`（同一个类，紧挨着的两个字段）：

```kotlin
override val useLegacyPackaging =
    variantServices.provider {
        dslPackagingOptions.jniLibs.useLegacyPackaging ?: (minSdk < M)      // APK：看 minSdk
    }

override val useLegacyPackagingFromBundle =
    variantServices.provider {
        dslPackagingOptions.jniLibs.useLegacyPackaging ?: false             // AAB：恒 false
    }
```

`JniLibsApkPackaging.kt` 的 KDoc 说明了为什么：

> 「If false, .so files will be compressed only when generating APKs from the app bundle **when targeting devices with API level < M**.」

即：AAB 里带的是非压缩 `.so`，由 Play 在**分发时**按目标设备的 API level 决定要不要压。再叠加 `PackageBundleTask.kt:583-590`，`ENABLE_UNCOMPRESSED_NATIVE_LIBS_IN_BUNDLE`（`android.bundle.enableUncompressedNativeLibs`）默认 **`true`**（`BooleanOption.kt:232-237`）。

**后果**：走 AAB 时，`.so` 在 API 23+ 的设备上是**非压缩**的 → **第三层重新成为硬要求** → AGP 7.4.2 的 bundletool 配置只会给 `PAGE_ALIGNMENT_4K`。Google 那页给的自查命令就是为这个：

```sh
bundletool dump config --bundle=<my .aab> | grep alignment
# PAGE_ALIGNMENT_16K = 好；PAGE_ALIGNMENT_4K = 从这个 AAB 生成的 APK 是 4 KB 对齐的
```

**这一条把风险从「假设」变成了「有具体命令可查」**：AAB 是上 Google Play 的**唯一**格式，所以这不是边缘路径，而是采用者的主路径。**[#82](https://github.com/xuhuanhello/juice-c-sharp/issues/82) 必须同时看 APK 和 AAB**，只测 APK 会漏掉真正会伤到采用者的那条。

---

## 7. 对 `Is16KbAligned` 的影响（缩小，未解决）

map 的 Not-yet-specified 里问：`Is16KbAligned` 是**给 Unity 读的**（影响打包行为）还是**Unity 写给自己看的**（纯记录）？原话是「等 AGP 那张 research 出来才能问得准」。

**本票能给的，是把它和 AGP 那条链切开**：

- §2 / §3 走完全程，`Is16KbAligned` **在 AGP 侧没有任何对应物**。AGP 的输入只有 minSdk、DSL、合并后的 manifest；`.meta` 的这个字段从头到尾没有出现在 Gradle 那一侧。
- 所以它**只可能是 Editor 侧**的东西 —— 落点是 Unity 官方文档记的那条行为：「if your project contains a plug-in with a `.so` file that's aligned to 4 KB instead of 16 KB, **the Unity Editor displays a warning during the build process**」（[Android requirements and compatibility](https://docs.unity3d.com/2022.3/Documentation/Manual/android-requirements-and-compatibility.html)）。
- **推断**：`Is16KbAligned` 极可能是那条 warning 的数据来源（Editor 导入 `.so` 时读 ELF、把结论缓存进 `.meta`），因此写错的后果是**报错信息失真**，不是打包行为改变。

**分级：推断。本票不下结论。** 判定它需要的是**读 Editor 侧代码或实测构建**，而不是再读 AGP —— 这一点现在是确定的。建议把 map 里那条 open question 的措辞从「等 AGP 那张 research」改成「等 Editor 侧（[#78](https://github.com/xuhuanhello/juice-c-sharp/issues/78) 的机器）实测」，因为 AGP 这条线索到此为止了。

---

## 8. 只能拆真 APK / AAB 才知道的 —— 交棒 [#82](https://github.com/xuhuanhello/juice-c-sharp/issues/82)

以下每一条本文都**没有**用似是而非的话填上。

| # | 问题 | 怎么答 | 为什么文档答不了 |
| --- | --- | --- | --- |
| **8.1** | Unity 2022.3.56f1+ 导出的 **APK** 里，我们的 `libdatachannel_unity.so` **是压缩的还是 STORED**？ | `unzip -lv app.apk \| grep datachannel`，看 Method 列（`Defl:N` vs `Stored`） | 取决于 Unity 模板/manifest 的实际内容（§4.3 查不到），以及 Unity 有没有做 AGP 之外的后处理 |
| **8.2** | 若是 STORED，它在 zip 里的偏移**是不是 16384 的倍数**？ | `zipalign -v -c -P 16 4 app.apk`（build-tools 35.0.0+），或 `unzip -lv` 看偏移 | §3 证明 AGP 7.4.2 给不出 16 KB。**若实测是 16 KB 对齐的，就说明 Unity 2022.3.56f1 在 AGP 之外做了后处理** —— 那是本文最想被证伪的一点，也是唯一能解释 Unity 如何在 AGP 7.4.2 上交付 16 KB 支持的另一种可能 |
| **8.3** | 合并后的 `AndroidManifest.xml` 里 `android:extractNativeLibs` **是什么值**？ | `apkanalyzer manifest print app.apk`，或 Android Studio 的 APK Analyzer | §2.2 证明它是最终裁决者；§4.3 说明它的来源本会话读不到 |
| **8.4** | 构建日志里有没有出现那条 AGP warning（`PackagingOptions.jniLibs.useLegacyPackaging should be set to …`）？ | 看 Gradle 构建输出 | 出现 = Unity 的 manifest 与 DSL 打架（§2.2），本身就是 8.3 的强线索 |
| **8.5** | **AAB 路径**：`bundletool dump config --bundle=app.aab \| grep alignment` 给的是 `PAGE_ALIGNMENT_16K` 还是 `PAGE_ALIGNMENT_4K`？ | 同左 | §6。**这条最可能出问题，且是上架的主路径，不要漏** |
| **8.6** | 真 16 KB 设备上 `.so` 是否真被加载、dual-peer smoke 是否跑通；有没有落进 **16 KB backcompat 模式**？ | map 已拍板的加载档验收；backcompat 可用 `adb shell setprop pm.16kb.app_compat.disabled true` 强制关掉再测 | Google 那页记了 backcompat 模式会让「4 KB LOAD 对齐」或「4 KB zip 对齐的非压缩 ELF」的应用**照样能跑** —— 也就是说**不关掉它就测不出真结果**，这正是 map 说的「怕的烂法」的一个新变种，值得写进验收判据 |
| **8.7** | 上游（libjuice/libdatachannel/usrsctp/OpenSSL）有没有 `PAGE_SIZE` 硬依赖或页对齐 `mmap` 假设？ | 源码检查 + 真机 | §3.3 第 4 条。本票未覆盖 |

> **8.2 是本文的自证伪点**。如果实测显示 Unity 出的 APK 里 `.so` 既是 STORED 又是 16 KB 对齐的，那么 §5 的推论虽然仍然成立（第一层充分），但 §5 的第三条旁证和 §6 的风险评估都要重写 —— 因为那意味着 Unity 在 AGP 之外自己动了手，我们对这条链的理解就还缺一块。

---

## 9. 分级汇总

按 [#77](https://github.com/xuhuanhello/juice-c-sharp/issues/77) 的输出纪律，三类分开。

### 9.1 文档 / 源码明载（可以直接写进 SPEC）

1. AGP 7.4.2 `jniLibs.useLegacyPackaging` 默认 `null`，实际值 = `minSdk < 23` —— `JniLibsApkPackagingImpl.kt`。**Q1 的猜测证实**。
2. 本仓库 `AndroidMinSdkVersion: 22`（`ProjectSettings/ProjectSettings.asset:176`），落在压缩侧。
3. AGP 7.4.2 的打包任务读的是**合并后 manifest 的 `extractNativeLibs`**，不是 DSL 值；DSL 只驱动注入 + warning —— `PackageAndroidArtifact.java:817` 起，`PackagingUtils.java:239`。
4. 未显式写 `extractNativeLibs="false"` 时，默认按 `COMPRESSED` 打包 —— `PackagingUtils.java:241-247`。
5. AGP 7.4.2 **不调用** `zipalign` 二进制；`isZipAlignEnabled` 已 `@Deprecated("no longer has any effect")`。
6. AGP 7.4.2 的页对齐常量硬编码 `4096`（ApkFlinger `PAGE_ALIGNMENT`、apkzlib `SO_RULE`），**全树无 `16384`**。默认打包器是 ApkFlinger（`android.useNewApkCreator` 默认 `true`）。
7. `COMPRESSED` 模式下 `.so` 在 zip 里退回 4 字节对齐，**连 4 KB 都不对** —— `ApkFlinger.kt:83-99`。
8. Google 官方：16 KB zip 对齐需 **AGP 8.5.1+**；且该要求**以「uncompressed」为前提**；官方给 AGP ≤ 8.5 的退路就是**改用压缩**。
9. `useLegacyPackagingFromBundle` 默认 **`false`**，与 minSdk 无关；`android.bundle.enableUncompressedNativeLibs` 默认 `true` → **AAB 路径与 APK 路径行为不同**。
10. Unity 2022.3 的 AGP 7.4.2 **有下界**：`2022.3.38f1+`；更早的 2022.3 是 AGP 7.1.2。
11. Unity 16 KB 支持要 **2022.3.56f1+**；Editor 会对 4 KB 对齐的插件 `.so` 在构建期报 warning。
12. Unity 2022.3 支持的最低 API level 是 **22**。
13. Unity 2022.3 提供五个 Gradle 模板 + Custom Main Manifest，全在 Publishing Settings，落在 `Assets/Plugins/Android/`。
14. Unity 2022.3 **没有** `Unity.Android.Gradle` 命名空间（2022.3 ScriptReference 404，6000.0 存在）→ 2022.3 无脚本化入口，只能改模板文本。

### 9.2 从文档/源码推断（**不要**当事实写进 SPEC）

1. **第二层与第三层不是并列关系，是互斥分支；压缩路径下第三层不适用，第一层即充分。**（§5，最重要的一条）
2. 由 1 推出：**在 Unity 2022.3 上抬 minSdk 到 23+ 反而可能让 16 KB 兼容性变差**。（§5 末尾，直接约束 `ANDROID_PLATFORM` 那张票）
3. Unity 2022.3.56f1 能在 AGP 7.4.2 上交付 16 KB 支持，与「压缩路径够用」自洽 —— 但**没有排除** Unity 自己做了 AGP 之外后处理的可能。（§5 / §8.2）
4. 要在 Unity 侧改 `useLegacyPackaging`，首选 **`launcherTemplate.gradle`**（打 APK 的是 `launcher` 模块），不是 `mainTemplate.gradle`。（§4.2）
5. `Is16KbAligned` 在 AGP 侧无对应物，只可能是 Editor 侧的东西，极可能是那条构建期 warning 的数据来源；写错的后果是**信息失真而非行为改变**。（§7）
6. map 里「AAR 携带不了第二层」的**结论仍成立，但理由要换** —— AGP 7.4.2 的打包任务**服从** manifest 的显式值，真正推翻库意愿的是 manifest merger 的合并规则。（§2.3）

### 9.3 查不到（本会话的硬边界）

1. **Unity 2022.3 默认的 `launcherTemplate.gradle` / `mainTemplate.gradle` / `gradleTemplate.properties` / manifest 模板里到底写了什么。** 本机无 `PlaybackEngines/AndroidPlayer/`（只有 `iOSSupport`）。→ [#78](https://github.com/xuhuanhello/juice-c-sharp/issues/78)，具体要问的三个问题见 §4.3。
2. **Unity 2022.3.56f1 的完整 changelog 原文。** `unity.com/releases/editor/whats-new/2022.3.56f1` 对本会话返回 **403**；「Android: Added support for 16KB page sizes」这句只有二手来源。**16 KB 支持要 2022.3.56f1+ 这个事实本身有官方文档（§9.1 第 11 条），够用了**；缺的只是那一行 changelog 的原文。
3. **`.so` 在真实 APK/AAB 里的实际形态与偏移。** → [#82](https://github.com/xuhuanhello/juice-c-sharp/issues/82)，七条见 §8。
4. **上游代码的 `PAGE_SIZE` / `mmap` 页假设**，以及各 `.so` 的 `GNU_RELRO` 现状。本票未覆盖（§3.3 第 4、5 条）。

---

## 10. 建议的后续动作（不在本票内执行）

| 动作 | 依据 | 归属 |
| --- | --- | --- |
| SPEC §16 / §9 里「Unity 2022.3 = AGP 7.4.2」补上 `.38f1+` 下界；并记 16 KB 需 `2022.3.56f1+` | §4.1 | SPEC 更新 |
| map [#76](https://github.com/xuhuanhello/juice-c-sharp/issues/76) 的三层表改成**分支图**，并加注「第三层以非压缩为前提」 | §5 | map 更新 |
| `ANDROID_PLATFORM` 那张票加一条**反向约束**：抬 minSdk 到 23+ 在 AGP 7.4.2 上有让 16 KB 变差的风险，需先由 [#82](https://github.com/xuhuanhello/juice-c-sharp/issues/82) 定夺 | §5 末尾 | 那张票 |
| [#82](https://github.com/xuhuanhello/juice-c-sharp/issues/82) 的验收判据加两条：**必须同时测 AAB**、**必须关掉 16 KB backcompat 再测** | §6 / §8.5 / §8.6 | [#82](https://github.com/xuhuanhello/juice-c-sharp/issues/82) |
| `audit_plugin.py` 的 Android 分支加 `GNU_RELRO` 断言（`readelf -l` 已在链上，很便宜） | §3.3 第 5 条 | audit |
| `.github/workflows/plugins-matrix.yml:226` 的注释补一句 Play 的 **2027-02-01** 强制截止（与已记的 2025-11-01 是不同口径） | §3.3 第 7 条 | 注释 |
| map 里 `Is16KbAligned` 那条 open question 的措辞从「等 AGP 那张 research」改成「等 Editor 侧实测」 | §7 | map 更新 |
