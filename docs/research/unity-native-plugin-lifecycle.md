# Research: Unity 2022.3 原生插件在域重载 / 进出播放 / 应用退出下的生命周期

| Field | Value |
| --- | --- |
| Ticket | [#28](https://github.com/xuhuanhello/juice-c-sharp/issues/28) (part of [#26](https://github.com/xuhuanhello/juice-c-sharp/issues/26)) |
| Date | 2026-08-02 |
| Unity version studied | **2022.3 LTS** (project pins 2022.3.62f3) |
| Subject | `Packages/datachannel-unity` — `dcu_*` C ABI + P/Invoke over libdatachannel v0.24.5 |
| Status | 完成。§4 为决策阻塞项，已给出可直接施工的结论 |

**一句话结论**：Unity 2022.3 不会替你善后任何东西——PlayerLoop 要自己摘、native 全局态要自己 `dcu_shutdown()`、finalizer 里**绝不能**碰 native。

---

## 0. Verdict（施工建议速查）

| 问题 | 结论 |
| --- | --- |
| 自定义 PlayerLoop 系统会跨域重载/进出播放存活吗？ | **托管委托必然随域销毁**；结构是否残留 Unity 无任何承诺、文档只字未提。**唯一正确写法**：每次域重载后重新插入、插入前按 `type` 去重、`ExitingPlayMode` 时主动摘除（R3 / UniTask / Unity Entities 三家共识） |
| 不摘会怎样？ | 真正的痛点不是 PlayerLoop，而是 **native 侧 `g_queue` 无上界增长 + 上一场 PeerConnection 永久存活**，只能重启 Editor（§1.4 B1/B2） |
| 反注册主钩子用哪个？ | Editor：**`AssemblyReloadEvents.beforeAssemblyReload`**（同时覆盖重编译与进入播放）+ **`playModeStateChanged == ExitingPlayMode`** + **`EditorApplication.quitting`**；Player：**`Application.quitting`** |
| `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` 的真实作用？ | **不是域重载钩子**。它是「进播放时手动把静态量归零」的官方指定手段，专治 *Reload Domain 被关闭*。当前 `DataChannelRuntime.OnDomainReload()` 命名误导，应改名 `ResetStaticsOnEnterPlayMode()` |
| 编辑器不卸载原生库，怎么清 native 全局态？ | 只能 C# 显式调 **`dcu_shutdown()`**。它幂等、未 init 时零成本、最坏阻塞 10 s。`rtcPreload → rtcCleanup → rtcPreload` 循环是上游支持的 |
| `dcu_shutdown()` 谁调？ | 上面四个钩子**全部**调，外加 `SubsystemRegistration` 里补一刀。调之前先 `Dispose()` 所有存活对象，让 `rtcCleanup` 的 `"N objects were not properly destroyed"` 变成真告警 |
| **finalizer 里 P/Invoke `rtcDelete*` 安全吗？** | **不安全，必须删掉。** 已用 v0.24.5 源码证实 `rtcDelete*` → `resetCallbacks()` → `synchronized_callback` 持锁等待在途回调返回；而 Unity 官方步骤表写明域卸载会**同步跑 finalizer**——阻塞 = **每次点 Play 都卡编辑器主线程** |
| **"只记日志、从不 P/Invoke"的 finalizer 呢？** | **安全且地道**，Unity 第一方 `DisposeSentinel` 就是这个形状。但必须满足 §4.4 的 6 条硬约束；**首选变体**：finalizer 只把字符串塞进 `ConcurrentQueue`，由既有主线程 pump 输出 |
| `SafeHandle` 值得上吗？ | **不值得**。它解决双重释放/回收竞态，**完全不解决阻塞**；CER 保证在 Mono/.NET Core 上已是 no-op；且本项目句柄是 `int` 不是 `IntPtr` |
| 有哪个成熟插件可以直接抄？ | **`com.unity.webrtc` 的 `ContextManager`**（§5.1）——抄它的钩子骨架与弱引用表；**不要**抄它的 finalizer |

---

## 1. `PlayerLoop.SetPlayerLoop` 在 2022.3 的实际存活行为

### 1.1 官方文档说了什么（几乎什么都没说）

2022.3 的三个 ScriptReference 页面（`PlayerLoop`、`GetCurrentPlayerLoop`、`GetDefaultPlayerLoop`、`SetPlayerLoop`）**通篇没有出现 "domain reload" / "play mode" / "reset" 任何一个词**。唯一相关的一句是 `SetPlayerLoop` 的：

> "The new update order will not take effect until the next full player loop iteration, but the changes will be immediately visible in subsequent calls to `GetCurrentPlayerLoop`."
> — <https://docs.unity3d.com/2022.3/Documentation/ScriptReference/LowLevel.PlayerLoop.SetPlayerLoop.html>

所以本节结论全部来自 **本机实测 + Unity 第一方包源码里对该行为的防御性处理**，而不是文档。

### 1.2 本机实测（2026-08-02，本仓库 Editor 实例）

```text
unityVersion               = 2022.3.62f3
isPlaying                  = False        (Edit Mode)
enterPlayModeOptionsEnabled= False        (即：Reload Domain 与 Reload Scene 均生效)
GetDefaultPlayerLoop() → Update 子系统数 = 4
GetCurrentPlayerLoop() → Update 子系统数 = 5

Update 的实际内容（Edit Mode 下）：
  R3.R3LoopRunners+R3Update                        [MANAGED delegate -> R3.UnityFrameProvider.Run]
  UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate          (native)
  UnityEngine.PlayerLoop.Update+ScriptRunDelayedDynamicFrameRate  (native)
  UnityEngine.PlayerLoop.Update+ScriptRunDelayedTasks             (native)
  UnityEngine.PlayerLoop.Update+DirectorUpdate                    (native)

Initialization / EarlyUpdate 同样各自插着一条 R3 系统。
DataChannelUnity.DataChannelRuntime → 【不在循环中】
```

三条可直接下结论的观测：

1. **Edit Mode 下 PlayerLoop 是活的，而且第三方包确实往里插了托管系统**（R3 通过 `[InitializeOnLoadMethod]`）。"PlayerLoop 只在播放时存在"是错的。
2. **`GetDefaultPlayerLoop()` 返回的是干净的 4 项，`GetCurrentPlayerLoop()` 返回含 R3 的 5 项。** 用 `GetDefaultPlayerLoop()` 当基底再 `SetPlayerLoop` 会**直接抹掉 R3**（本仓库依赖 R3）。本项目 `RegisterPump()` 用的是 `GetCurrentPlayerLoop()`，✅ 正确。
3. **本包的 pump 在 Edit Mode 完全不存在**，因为只有 `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)] Bootstrap()` 会调 `RegisterPump()`。Editor 工具、EditMode 测试拿不到任何事件。这是现存缺口。

### 1.3 跨域重载 / 进出播放的存活模型

托管委托（`updateDelegate`）是托管堆上的对象；域重载会销毁整个 Unity Child Domain（官方步骤表里写死了 "The Unity Child Domain is destroyed … GC and finalizers are called … Threads are terminated"）。所以委托**不可能**以"同一个活对象"的形式跨域存活。剩下的问题只是：Unity 把 native PlayerLoop 结构**重置**了，还是**留着一批空/悬垂槽位**。

| 场景 | 托管委托 | 结构 | 你必须做的事 | 证据强度 |
| --- | --- | --- | --- | --- |
| **Edit Mode 脚本重编译**（域重载） | 随域销毁 | 新域里需要重新插入 | `[InitializeOnLoad(Method)]` 重装 | 强：R3/UniTask/Entities 全部这么做，且本机实测 Edit Mode 下 R3 系统在位（editor 启动至今已重编译多次） |
| **进入播放，Reload Domain 开（本项目当前设置）** | 随域销毁 | 需要重新插入 | `[RuntimeInitializeOnLoadMethod]` 重装 | 强 |
| **进入播放，Reload Domain 关** | **不销毁，继续有效** | **原样保留**——Edit Mode 时插进去的系统会直接在播放里继续跑 | 在 `SubsystemRegistration` 里**先移除旧项再插新项** | 强：Unity Entities 的 `DefaultWorldInitialization` 就是为这个场景写的，注释原文 "Destroys Editor World when entering Play Mode **without Domain Reload**"，实现是 `GetCurrentPlayerLoop()` → `RemoveWorldFromPlayerLoop` → `SetPlayerLoop` |
| **退出播放 → Edit Mode** | 取决于是否发生域重载 | Unity **没有**提供"退出播放会帮你还原 PlayerLoop"的任何承诺 | 自己在 `ExitingPlayMode` 摘掉 | 强：Unity DOTS 的 `RuntimeContentSystem` 明确在 `PlayModeStateChange.ExitingPlayMode` 调 `RemoveFromPlayerLoop()` |

> **明确未确认**：我**没有**在这台机器上做"插一个探针系统 → 触发域重载/进出播放 → 回读循环"的破坏性实验（会改动用户正在使用的 Editor 会话，且悬垂委托理论上有崩溃风险）。因此"Unity 究竟是把槽位清空、还是把整个循环重置成 default"这一底层机制**未经实测确认**。§7 给了 2 分钟可复现的验证脚本。
>
> 但**这不影响工程结论**：无论 Unity 内部怎么做，正确写法都是"每次域重载后重新插入，插入前按 `type` 去重，退出播放时主动摘除"——这三条恰好是 R3 / UniTask / Unity Entities 三家共同的做法。

### 1.4 不反注册，本项目具体会坏在哪

按严重程度排序（全部是本仓库当前代码的真实后果，不是泛泛而谈）：

| # | 后果 | 机理 |
| --- | --- | --- |
| **B1** | **`g_queue` 无上界增长，直到重启 Editor** | 退出播放后 C# 侧 pump 没了（域重载），但 native 侧 `g_inited` 仍为 `true`、上一场的 `rtcPeerConnection` 仍活着、libdatachannel 线程池仍在跑。回调继续 `g_queue.push()`，`DcuEvent` 里带 `std::vector<uint8_t> payload`。**一条还在收数据的 DataChannel 会让 `std::deque` 无限涨**。`dcu_shutdown()` 从来没人调 → 只能重启 Editor 才清得掉 |
| **B2** | **上一场播放的 PeerConnection 永久存活** | 域重载销毁了 `HandleTable` 和所有托管 `PeerConnection`，但 native 侧没有任何人调 `rtcDeletePeerConnection`。ICE/STUN/TURN 保活流量、DTLS 会话、SCTP 心跳在编辑器后台**继续跑**。多点几次 Play 就叠加多份 |
| **B3** | **重进播放后 pump 会先吐出上一场的事件** | `dcu_init()` 因 `g_inited.exchange(true)` 已为真而直接 no-op，`g_queue` 里的旧事件原样留着。新一轮 `Pump()` 第一帧就会 peek 到旧 pc/dc 句柄 → `HandleTable.TryGetPc` 失败 → 静默丢弃。**目前只是浪费，但一旦将来句柄复用就是错投递**（幸好不会，见 §3.4） |
| **B4** | **Edit Mode 下没有 pump** | 见 §1.2 观测 3。EditMode 单元测试无法验证事件路径 |
| **B5** | 重复插入 | 现状 `InsertPump()` 里 `list.RemoveAll(s => s.type == typeof(DataChannelRuntime))` 已经防住了，✅ 保留 |
| **B6** | 悬垂托管委托被调用 | 理论风险；未实测（见 §7）。若 Unity 不清理，会在重编译后的第一帧抛异常或更糟 |

**B1 + B2 才是真正的痛点，而它们都不是靠"摘 PlayerLoop"解决的，是靠 `dcu_shutdown()` 解决的（§3）。** 摘 PlayerLoop 只解决 B5/B6。

---

## 2. 反注册钩子的触发时机与遗漏场景

### 2.1 权威时序：进入播放模式的完整步骤

Unity 2022.3 手册《Details of disabling Domain and Scene Reload》给了**逐条有序**的官方列表（Domain Reload + Scene Reload 都开启时）：

> 1. The AssemblyReloadEvent **`beforeAssemblyReload`** event is raised.
> 2. The C# domain is stopped: a. `OnDisable()` is called for all ScriptableObjects and MonoBehaviours. b. Unity waits for all async operations to finish.
> 3. The state of all MonoBehaviours and ScriptableObjects is serialized. …
> 4. Managed wrappers are disconnected from native Unity objects.
> 5. The Unity Child Domain is reloaded: a. Mono domain unload: i. The `AppDomain.DomainUnload` event is raised. ii. The Unity Child Domain is destroyed — **1. GC and finalizers are called. 2. Threads are terminated. 3. All JIT info is deleted.** b. The new Unity Child Domain is created.
> 6. The assemblies are loaded: System → Unity → **User assemblies**.
> 7. **The synchronization context is initialized.**
> 8. The scripting state is restored … i. Constructors are called, and **statics are assigned their default values**. …
> 9. Methods with the **`InitializeOnLoad` and `InitializeOnLoadMethod`** are called.
> 10. The AssemblyReloadEvent **`afterAssemblyReload`** is called.
>
> — <https://docs.unity3d.com/2022.3/Documentation/Manual/configurable-enter-play-mode-details.html>

三个对本项目至关重要的细节：

- **第 5.a.ii.1 步：`GC and finalizers are called`。** 域卸载会**同步**跑终结器 pass。这就是 §4 R1 的出处——finalizer 里阻塞 = 编辑器主线程在这一步挂起。
- **第 5.a.ii.2 步：`Threads are terminated`。** 被终止的是**托管线程**；libdatachannel 的 native 线程池不在此列，**它会活过域重载**。这正是 B2 的根因。
- **第 9 步在第 10 步之前**：`InitializeOnLoadMethod` 先于 `afterAssemblyReload`。若两者都用了，注意顺序。

`[RuntimeInitializeOnLoadMethod]` 不在这张表里——它属于"运行时启动序列"，官方另有一张表，且明确 "The above details are when starting up a Player build. **When entering Play mode in the Editor the same invocations are ensured.**"（<https://docs.unity3d.com/2022.3/Documentation/ScriptReference/RuntimeInitializeOnLoadMethodAttribute.html>）。顺序是：低层系统 → **`SubsystemRegistration`** + `AfterAssembliesLoaded` → `BeforeSplashScreen` → 第一个场景开始加载 → `BeforeSceneLoad` → `Awake`/`OnEnable` → `AfterSceneLoad`。

### 2.2 逐钩子：触发时机 / 覆盖 / 遗漏

| 钩子 | 精确触发点 | 覆盖 | **遗漏的场景** |
| --- | --- | --- | --- |
| `AssemblyReloadEvents.beforeAssemblyReload` | 域重载**开始前**（进入播放流程第 1 步）。Editor-only | Edit Mode 脚本重编译；进入播放（Reload Domain 开）；关闭 Editor 前的域卸载 | ① **Reload Domain 关时进入播放不触发**；② Player 里**不存在**这个 API；③ **退出播放时是否触发未确认**（§7）；④ Editor 崩溃/强杀不触发 |
| `AssemblyReloadEvents.afterAssemblyReload` | 新域装配完成（第 10 步，在 `InitializeOnLoadMethod` **之后**） | 重建 native 全局态 | 同上 |
| `[InitializeOnLoad]` / `[InitializeOnLoadMethod]` | 第 9 步。**每次**域重载都跑。Editor-only | Edit Mode + 进入播放的重装 | Player 不存在；Reload Domain 关时**不触发** |
| `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` | 运行时启动最早期，第一个场景加载前。**进入播放一定触发，与 Reload Domain 开关无关** | Player 冷启动；每次进入播放 | **退出播放不触发**；**Edit Mode 重编译不触发**；**它根本不是"域重载"钩子** —— 本项目 `DataChannelRuntime.OnDomainReload()` 的命名是误导，它真正的语义是「**进播放模式时手动把静态量归零**」（Unity 官方 `DomainReloading.html` 里给的正是这个用法） |
| `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` | 第一个场景对象已加载、`Awake` 之前 | 本项目 `Bootstrap()` 用它 | 同上；且比 `SubsystemRegistration` 晚，静态归零必须在它之前 |
| `EditorApplication.playModeStateChanged` | `ExitingEditMode` → `EnteredPlayMode` → `ExitingPlayMode` → `EnteredEditMode`。官方定义：`EnteredEditMode`/`EnteredPlayMode` 发生在 "**during the next update** of the Editor application" | **唯一能感知"退出播放"的钩子** | Editor-only；关闭 Editor 不触发；`Entered*` 是**延后一帧**的，不能用来做"必须在场景加载前完成"的事 |
| `Application.quitting` | 官方原文：**"`Application.quitting` is invoked when exiting Play mode."** 且 "raised when the quitting process **cannot be cancelled**" | Player 退出；**编辑器退出播放** | **不代表"关闭编辑器"**；强杀/崩溃不触发；**iOS 通常是挂起而非退出**（官方原文，要用 `OnApplicationPause`）；UWP 无退出事件 |
| `EditorApplication.quitting` | 关闭 Unity Editor | `com.unity.webrtc` 用它做最终 `DisposeInternal()` | 强杀不触发 |
| 隐藏代理 `MonoBehaviour.OnDisable`（`[ExecuteAlways]` + `HideFlags`） | 进入播放的第 2.a 步、退出播放、域卸载 | Unity Entities 的兜底方案 | 需要一个 GameObject；执行顺序要靠 meta 里的 `executionOrder` 压到最后 |

### 2.3 为什么 Unity 自己也要上"代理 MonoBehaviour"这种土办法

`com.unity.entities` 的 `DefaultWorldInitialization` 把这件事写得非常直白：

> 1) When switching to Play Mode Editor World (if created) has to be destroyed …
> 2) When switching to Edit Mode Game World has to be destroyed …
> 3) When Unloading Domain (as well as Editor/Player exit) Editor or Game World has to be destroyed …
> **Point 1) is covered by `RuntimeInitializeOnLoadMethod` attribute. For points 2) and 3) there are no entry point in the Unity API** and they have to be handled by a proxy MonoBehaviour which in `OnDisable` can drive the World cleanup for both Exit Play Mode and Domain Unload.
> — <https://github.com/needle-mirror/com.unity.entities/blob/master/Unity.Entities/DefaultWorldInitialization.cs>

**这句"no entry point in the Unity API"要正确理解**：`DefaultWorldInitialization` 位于 **runtime assembly**，用不了 `UnityEditor.AssemblyReloadEvents` / `EditorApplication`，所以只能用代理 MonoBehaviour。**本项目不受这个限制**——只要把编辑器专用逻辑放进 `Editor/` asmdef，`beforeAssemblyReload` + `playModeStateChanged` + `EditorApplication.quitting` 三个钩子就足以覆盖 Editor 侧全部场景，不需要代理 GameObject。这也正是 `com.unity.webrtc` 的选择。

### 2.4 覆盖矩阵（哪个场景该由谁兜）

| 场景 | Editor 侧负责的钩子 | Player 侧 |
| --- | --- | --- |
| Edit Mode 脚本重编译 | `beforeAssemblyReload` → shutdown；`afterAssemblyReload`（或 `[InitializeOnLoadMethod]`）→ re-init + 重装 pump | n/a |
| 进入播放（Reload Domain **开**） | 同上（进播放流程第 1 步就是 `beforeAssemblyReload`） | n/a |
| 进入播放（Reload Domain **关**） | `beforeAssemblyReload` **不触发** → 只能靠 `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` 做 shutdown + init + 去重重装 | n/a |
| 退出播放 | `playModeStateChanged == ExitingPlayMode` → shutdown + 摘 pump | `Application.quitting`（在编辑器里这个也会响，可作双保险） |
| 关闭 Editor | `EditorApplication.quitting` → shutdown | n/a |
| Player 正常退出 | n/a | `Application.quitting` → shutdown |
| Player 被杀 / 崩溃 / iOS 挂起 | n/a | **无钩子**。进程消亡即回收，可接受；iOS 需另配 `OnApplicationPause` 决定是否主动断连 |

---

## 3. 原生全局状态跨域重载的清理策略（`dcu_shutdown()` 归谁调）

### 3.1 前提：编辑器**永远不会**卸载已加载的原生库（官方原文）

> "When you import a plug-in, Unity loads it into memory. **A native plug-in cannot be unloaded; it remains loaded in a Unity session even after you change its settings. To unload the plug-in, you must restart Unity.**"
> — <https://docs.unity3d.com/2022.3/Documentation/Manual/PluginInspector.html>

推论：`libdatachannel_unity.dylib` 里的 `g_inited`、`g_queue`、libdatachannel 的 `ThreadPool` / `Init::Instance()` 静态单例、以及 capi 的 `peerConnectionMap`/`dataChannelMap`，**生命周期 = 整个 Editor 会话**，跨越任意多次域重载和进出播放。域重载对它们**零影响**。

这直接推出一条设计原则：

> **托管侧的"重置"只是把静态字段清零，native 侧必须被显式命令去重置。** 二者之间唯一的桥就是 `dcu_shutdown()` / `dcu_init()`。当前代码 `OnDomainReload()` 只做了前半句（`_nativeReady = false; _initAttempted = false; _pumpRegistered = false;`），后半句完全缺失——这就是 §1.4 的 B1/B2。

### 3.2 `dcu_shutdown()` 到底做了什么（逐层）

```cpp
int dcu_shutdown(void) {
    if (!g_inited.exchange(false)) return DCU_OK;   // 幂等：没 init 过直接返回
    g_queue.clear();                                 // std::deque 清空，锁保护
    rtcCleanup();
    return DCU_OK;
}
```

`rtcCleanup()`（`src/capi.cpp`）：

```cpp
size_t count = eraseAll();          // 清空 pc/dc/userPointer 四张 map，丢掉所有 shared_ptr
if (count != 0)
    PLOG_INFO << count << " objects were not properly destroyed before cleanup";   // ← native 自带泄漏计数
if (rtc::Cleanup().wait_for(10s) == std::future_status::timeout)
    throw std::runtime_error("Cleanup timeout (possible deadlock or undestructible object)");
```

`rtc::Cleanup()`（`src/impl/init.cpp`）：释放全局 token → token 析构时**起一条 detached 线程**（`"RTC cleanup"`）跑 `doCleanup()`：

```cpp
void Init::doCleanup() {
    std::lock_guard lock(mMutex);
    if (mGlobal) return;                       // 若期间已经重新 preload，则整个 cleanup 跳过
    if (!std::exchange(mInitialized, false)) return;
    ThreadPool::Instance().join();             // ← 真正回收线程池
    ThreadPool::Instance().clear();
    SctpTransport::Cleanup(); DtlsTransport::Cleanup(); IceTransport::Cleanup();
}
```

四条要点：

1. **`dcu_shutdown()` 是幂等的、且未 init 时零成本。** 可以无脑多处调用。
2. **它可能阻塞最长 10 秒。** 所以绝不能放在 finalizer / 后台线程 / 高频路径上，只能放在明确的生命周期钩子里（编辑器卡 ≤10 s 是可以接受的最坏情况，而且只在真有未关连接时才会接近）。
3. **`rtcPreload()` 之后可以再 `rtcCleanup()` 再 `rtcPreload()`** —— `doInit()` 由 `std::exchange(mInitialized, true)` 守卫，会重新 `ThreadPool::Instance().spawn(count)`。**init/cleanup 循环是上游支持的**，这一点很关键，意味着"域重载时拆掉、之后重建"是可行方案而不是一次性操作。
4. **`shutdown` 紧接着 `init` 存在良性竞态**：detached 的 cleanup 线程若晚于新的 `rtcPreload()` 拿到 `mMutex`，会看到 `mGlobal != nullptr` 而**整个跳过清理**（线程池不重建，继续复用）。两种顺序的终态都是自洽的，但"线程池到底有没有被回收"是不确定的。**不要在同一帧里 shutdown 后立刻 init**；跨域重载天然隔了很久，没问题。

### 3.3 谁调、什么时候调（推荐方案）

```
Editor/DataChannelEditorLifecycle.cs   （Editor asmdef，[InitializeOnLoad]）
├─ static ctor（每次域重载都跑，第 9 步）
│    ├─ AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload
│    ├─ EditorApplication.playModeStateChanged   += OnPlayModeChanged
│    ├─ EditorApplication.quitting               += OnEditorQuit
│    └─ DataChannelRuntime.RegisterPump()        ← 让 Edit Mode 也有 pump（补 B4）
│
├─ OnBeforeReload()            → UnregisterPump(); dcu_shutdown()
├─ OnPlayModeChanged(state)
│    ├─ ExitingPlayMode        → UnregisterPump(); dcu_shutdown()
│    └─ EnteredEditMode        → RegisterPump()            (Edit Mode 恢复)
└─ OnEditorQuit()              → 先把三个事件 -= 掉，再 dcu_shutdown()

Runtime/DataChannelRuntime.cs
├─ [RuntimeInitializeOnLoadMethod(SubsystemRegistration)] ResetStatics()
│    ├─ _nativeReady = _initAttempted = _pumpRegistered = false;
│    └─ #if UNITY_EDITOR  dcu_shutdown();  #endif    ← 覆盖 Reload Domain 关的场景
├─ [RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]      Bootstrap()
│    ├─ EnsureNative()  → dcu_init()
│    ├─ RegisterPump()  （按 type 去重后插入）
│    └─ Application.quitting += OnQuitting     ← Player 侧；编辑器里退播放也会响，双保险
└─ OnQuitting() → DisposeAllLive(); UnregisterPump(); dcu_shutdown();
```

关键判断逐条说明：

| 问题 | 结论 | 理由 |
| --- | --- | --- |
| `dcu_shutdown()` 该由 C# 调还是 native 自动？ | **C#，显式调** | native 没有任何办法感知域重载；`DllMain`/`__attribute__((destructor))` 只在库卸载时跑，而库**永不卸载**（§3.1） |
| 编辑器里主入口是哪个钩子？ | **`AssemblyReloadEvents.beforeAssemblyReload`** | 它是进入播放流程的**第 1 步**，同时也覆盖 Edit Mode 重编译。一个钩子吃掉两个场景。`com.unity.webrtc` 同款 |
| 为什么还要在 `SubsystemRegistration` 里再 shutdown 一次？ | **补 Reload Domain 被关掉的场景** | 该场景下 `beforeAssemblyReload` 不触发。而 `dcu_shutdown()` 幂等且未 init 时零成本——Reload Domain 开时这次调用就是个 `exchange` + 立即返回，没有代价 |
| 退出播放要不要 shutdown？ | **要** | 否则 B1/B2 原样存在（native PC 在 Edit Mode 里继续跑）。这是 `com.unity.webrtc` **没做**、而 Unity Entities 的 `RuntimeContentSystem` **做了**（`ExitingPlayMode` → `ContentDeliveryGlobalState.Cleanup()`）的一点 |
| Player 里谁调？ | **`Application.quitting`** | 官方保证"不可取消阶段"才触发。崩溃/强杀无钩子，进程消亡即回收，可接受 |
| shutdown 之前要不要先 `Dispose()` 所有存活对象？ | **要，而且必须先** | 让 `Dispose()` 走正常的主线程级联路径（PC → 子 DC），`rtcCleanup()` 只做兜底。这样 `"N objects were not properly destroyed before cleanup"` 这条 native 日志就变成了**真正的告警信号**而不是常态噪音 |
| 要不要新增 ABI？ | **建议加两个** | ① `int dcu_is_inited(void)` —— 让 C# 能判断 native 侧真实状态（域重载后 C# 静态量已清零，但 native 可能仍 inited）；② `int dcu_queue_depth(void)` —— 让 B1 那种队列膨胀可观测、可断言。两者都是只读、无副作用、无阻塞 |

### 3.4 一条免费的安全性质：句柄不会被复用

`src/capi.cpp` 里 `int lastId = 0;` 是文件级静态，只有 `++lastId` 一处递增，**`eraseAll()` 不重置它**。所以在一次 Editor 会话内（=库加载的整个生命周期），pc/dc 句柄**单调递增、永不复用**，即使跨多次 `rtcCleanup()`/`rtcPreload()`。

推论：**一个跨域重载残留下来的旧句柄，绝不会意外指向新会话的某个对象**——最坏情况只是 `getDataChannel()` 抛 `invalid_argument`、被 `wrap()` 转成 `RTC_ERR_INVALID` 返回。这让 §1.4 的 B3 从"可能错投递"降级为"只是浪费"。

**但不要依赖它**：这是 v0.24.5 的实现细节，不是 ABI 承诺。本项目的 `dcu_*` 层若要对外承诺"句柄不复用"，应当自己维护一个 generation counter，而不是赌上游。

## 4. Finalizer 线程 P/Invoke 阻塞式 destroy 的风险与业界做法

> 这是本票据里**唯一有活决策被卡住**的一节，先给结论：
>
> **当前 `~DataChannel()` / `~PeerConnection()` 里 P/Invoke `dcu_dc_destroy` / `dcu_pc_destroy` 的写法必须删掉。**
> 换成「**finalizer 只记日志、绝不进 native、绝不加锁**」——这在 Unity 2022.3 里不仅安全，而且**正是 Unity 自己 `Unity.Collections` 的做法**（`DisposeSentinel`）。

### 4.1 先证明前提：`rtcDelete*` 确实会阻塞等回调收敛

这不是理论担忧，v0.24.5 源码可以逐层证实（本仓库已 vendored 在 `native/subprojects/libdatachannel`，`git describe` = `v0.24.5`）：

```cpp
// src/capi.cpp
int rtcDeleteDataChannel(int dc) {
    return wrap([dc] {
        auto dataChannel = getDataChannel(dc);   // 取全局 mutex
        dataChannel->close();                    // ← 阻塞点
        eraseDataChannel(dc);                    // 再取全局 mutex
        return RTC_ERR_SUCCESS;
    });
}
int rtcDeletePeerConnection(int pc) {
    return wrap([pc] {
        auto peerConnection = getPeerConnection(pc);
        peerConnection->close();                 // ← 阻塞点
        erasePeerConnection(pc);
        return RTC_ERR_SUCCESS;
    });
}
```

```cpp
// src/impl/datachannel.cpp
void DataChannel::close() {
    ...
    if (!mIsClosed.exchange(true)) {
        if (transport && mStream.has_value())
            transport->closeStream(mStream.value());
        triggerClosed();
        resetCallbacks();        // ← 真正的阻塞源
    }
}
```

`resetCallbacks()` 只是把每个 `synchronized_callback` 赋 `nullptr`，而 `synchronized_callback` 的定义（`include/rtc/utils.hpp`）是：

```cpp
synchronized_callback &operator=(std::function<void(Args...)> func) {
    std::lock_guard lock(mutex);      // 赋值要拿锁
    set(std::move(func));
    return *this;
}
bool operator()(Args... args) const {
    std::lock_guard lock(mutex);      // 调用**全程持锁**
    ...
}
```

也就是说：**赋 `nullptr` 会一直阻塞，直到当前正在 libdatachannel worker 线程上执行的那次回调返回为止**。本项目的回调是 `dcu_impl.cpp` 里的 `on_dc_message` 等 → `g_queue.push()` → `std::mutex`。正常情况下这只有微秒级，但它**在语义上就是"等回调收敛"**，且 libdatachannel 自己也承认这条路可能死锁——`rtcCleanup()` 里写死了 10 秒超时：

```cpp
// src/capi.cpp
if (rtc::Cleanup().wait_for(10s) == std::future_status::timeout)
    throw std::runtime_error("Cleanup timeout (possible deadlock or undestructible object)");
```

所以票据里"会阻塞等待回调收敛"的前提**成立且有源码依据**。

### 4.2 在 Unity 2022.3 具体会炸成什么样

| # | 风险 | 为什么在 2022.3 具体成立 |
| --- | --- | --- |
| R1 | **进入播放模式时编辑器卡死** | Unity 官方列出的进入播放流程明确包含：`Mono domain unload` → `The Unity Child Domain is destroyed` → **`GC and finalizers are called`** → `Threads are terminated`（见 §2 引用）。finalizer 里阻塞 = 域卸载阻塞 = **主线程挂起**。这不是"偶发"，是每次点 Play 都走的路径 |
| R2 | **单条 finalizer 线程被堵死** | Mono/CoreCLR 都只有**一条**终结器线程。堵住它 → 所有可终结对象永远不被回收 → 托管内存单调增长，且后续所有 `~Foo()`（包括别的库的）都不再运行 |
| R3 | **和 `HandleTable` 的锁互锁** | 现状 `~DataChannel()` 之后调 `HandleTable.UnregisterDc()` → `lock (Gate)`。主线程 `Pump()` 里 `TryGetDc()` 也要 `Gate`。终结器线程只要在持 `Gate` 期间进 native 阻塞，**主线程下一帧 `Pump()` 立刻冻结**。这是真实可复现的挂起路径，与 GC 时机无关 |
| R4 | **终结顺序不确定** | 锁定的设计是「PC 强引用其子 DC」。当 PC 与其 DC 在同一次 GC 中同时不可达时，**C# 不保证 `~DataChannel` 先于 `~PeerConnection`**。目前恰好不会 UAF（capi 的 map 持 `shared_ptr`，PC 删除不会连带 erase 子 DC 的 id），但这是**巧合而非契约**——一旦上游改成级联 erase，就变成对已释放 id 的 P/Invoke |
| R5 | **在 native 全局态已拆掉之后触发** | `beforeAssemblyReload` 里调 `dcu_shutdown()`（§3 的建议）之后，域卸载的 finalizer pass 才跑。此时 `rtcDeleteDataChannel` 会走到 `getDataChannel()` 抛 `std::invalid_argument`、被 `wrap()` 吞掉返回 `RTC_ERR_INVALID`——**这次是良性的**，但依赖的是"C++ 异常没跨 ABI 边界逃逸"这一实现细节 |
| R6 | **Player 退出时 finalizer 根本不保证运行** | `Application.quitting` 之后 IL2CPP/Mono 不承诺跑完所有终结器。所以 finalizer **本来就不是可靠的清理机制**，把 native 释放挂在它上面是把不可靠的东西当主路径 |
| R7 | **终结器线程上禁止碰 Unity API** | 终结器线程不是主线程；`Time.*`、`Application.isPlaying`、任何 `UnityEngine.Object` 解引用都会抛 `UnityException`。finalizer 里抛异常在 Mono 上会被吞（也可能打到 Console），但你会丢掉诊断本身 |

### 4.3 业界四种做法的实际评价

| 做法 | 谁在用 | 解决了什么 | **没**解决什么 | 对本项目 |
| --- | --- | --- | --- | --- |
| **`SafeHandle`** | .NET BCL（`SafeFileHandle` 等） | 句柄回收竞态与 recycle attack（官方原文："preventing handles from being reclaimed prematurely by garbage collection and from being **recycled by the operating system** to reference unintended unmanaged objects"）；顺序有保证——"**all the noncritical finalizers are called before any of the critical finalizers**" | **完全没解决阻塞**——`ReleaseHandle()` 照样在终结器线程上跑。而且它的契约要求 "the critical finalizer and anything it calls, such as `SafeHandle.ReleaseHandle()`, **must be in a constrained execution region**"，而 CER 在现代运行时已被标注 `[Obsolete("The Constrained Execution Region (CER) feature is **not supported**.", DiagnosticId="SYSLIB0004")]` ⇒ Unity 上拿不到"保证执行"那层价值 | **不采用**。且本项目句柄是 `int` 不是 `IntPtr`（`0`/负数为无效），套 `SafeHandle` 还要造假指针，纯负收益 |
| **彻底禁用 finalizer** | `Unity.Collections` 的 `NativeArray`（release 构建下**没有**任何 finalizer）、UniTask | 零终结器风险 | 泄漏彻底静默，用户忘了 `Dispose()` 没有任何提示 | 与已锁定的「泄漏诊断必须有」冲突 |
| **finalizer 只记日志，不进 native** | **Unity 官方 `DisposeSentinel`** | 泄漏可见（带分配处调用栈），且**永不阻塞、永不进 native、永不加锁** | 不回收 native 资源（这是**特性**：泄漏就该被修，不该被悄悄兜底） | ✅ **就是本项目该采用的形状** |
| **延迟销毁队列**（finalizer 只入队，主线程 pump 里真删） | 大量社区插件；概念上等价于 com.unity.webrtc 的 `s_syncContext.Post(...)` 延迟销毁 | 既能可靠回收 native，又不在终结器线程阻塞 | 复活语义（resurrect）复杂；主线程 pump 里做阻塞销毁会掉帧；且**会掩盖泄漏** | ❌ 与「`Dispose()` 仅主线程 + 泄漏必须报错」的锁定决策直接冲突。若将来要兜底再议 |

### 4.4 直接回答："只记日志、从不 P/Invoke 的 finalizer，在 Unity 2022.3 安全且地道吗？"

**安全 —— 是；地道 —— 是，而且有 Unity 第一方先例。**

Unity 自己的 `Unity.Collections.LowLevel.Unsafe.DisposeSentinel` 官方文档原文：

> "DisposeSentinel is used to automatically detect memory leaks. … The **DisposeSentinel finalizer**, which is invoked when there are no more references to the native container that owns it, **checks if the referenced data has been disposed correctly or not, and in case it is not, it logs an error containing the information about when the initial allocation happened**. The DisposeSentinel class is available only when `ENABLE_UNITY_COLLECTIONS_CHECKS` is defined."
> — <https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Unity.Collections.LowLevel.Unsafe.DisposeSentinel.html>

逐条对上本项目已锁定的需求：

| 锁定的需求 | `DisposeSentinel` 的对应做法 |
| --- | --- |
| 未 `Dispose` 就被 GC → 报 error | 正是它的唯一职责 |
| 带创建处调用栈 | "information about when the initial allocation happened" |
| Editor/Dev 默认开、Release 关 | `ENABLE_UNITY_COLLECTIONS_CHECKS`（Editor + Development Build 定义） |
| 不在 finalizer 里释放 native | 它**不**释放，只报告；释放仍必须靠 `Dispose()` |

运行时开关也有先例：`Unity.Collections.NativeLeakDetection.Mode`（`Disabled` / `Enabled` / `EnabledWithStackTrace`）——本项目做 `DataChannelLog.LeakDetectionMode` 时可以照抄这个三档形状。

**实现时的硬约束（这几条不遵守，"只记日志"也会出事）：**

1. **只允许 `UnityEngine.Debug.LogError(string)`**，不带 `context` 参数（`Object context` 重载会在终结器线程上解引用 `UnityEngine.Object` → `UnityException`）。
2. **不要在 finalizer 里 `lock (HandleTable.Gate)`**。锁定的设计里表持**弱引用**，被 GC 的条目本来就该由主线程 pump 或下次 `TryGet` 时惰性清扫；finalizer 一个字都不要写表。
3. **不要读任何 Unity 静态状态**（`Application.*`、`Time.*`、`Debug.unityLogger.logEnabled` 也别读——用自己的 `static volatile bool`）。
4. **调用栈在构造时抓，不在 finalizer 里抓**。`new StackTrace(true)` 有明显成本（读 pdb/mdb 解析行号），必须由 `LeakDetectionMode` 门控；Release 下连字段都不该存。
5. **`Dispose()` 里 `GC.SuppressFinalize(this)`**（现状已有，保留）。
6. `Debug.LogError` 从后台线程调用是社区普遍依赖的行为，**但 Unity 官方文档并未明文承诺线程安全**（见 §7）。若要 100% 稳妥：finalizer 只把字符串塞进一个 `ConcurrentQueue<string>`，由既有的 PlayerLoop pump 在主线程 drain 后再 `Debug.LogError` ——本项目**已经有主线程 pump**，这条几乎零成本，且顺带让日志带上正确的 Console 上下文与帧号。**推荐直接上这个变体。**

### 4.5 那 native 资源谁来收？

- **`Dispose()`（主线程，已锁定）是唯一正式释放路径。** PC 级联销毁子 DC。
- **兜底不是 finalizer，是 `dcu_shutdown()`。** 域重载 / 退出播放 / 退出应用时统一 `rtcCleanup()`，其 `eraseAll()` 会一次性丢掉所有 PC/DC 的 `shared_ptr`，并打印 `"N objects were not properly destroyed before cleanup"`——**native 侧自带的泄漏计数**，正好和 C# 侧的 `DisposeSentinel` 式报错互为佐证。详见 §3。

## 5. `com.unity.webrtc` 与其他成熟插件的具体做法

### 5.1 `com.unity.webrtc` — `ContextManager`（可直接抄的骨架）

`Runtime/Scripts/Context.cs` 顶部这 40 行就是 Unity 官方对本票据前三问的完整答案：

```csharp
// Ensure class initializer is called whenever scripts recompile
#if UNITY_EDITOR
[InitializeOnLoad]
#endif
class ContextManager
{
#if UNITY_EDITOR
    static ContextManager() { Init(); }                       // 每次域重载都跑

    static void OnBeforeAssemblyReload() { WebRTC.DisposeInternal(); }
    static void OnAfterAssemblyReload()  { WebRTC.InitializeInternal(); }

    internal static void Init()
    {
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        AssemblyReloadEvents.afterAssemblyReload  += OnAfterAssemblyReload;
        EditorApplication.quitting += Quit;                   // 编辑器用 EditorApplication.quitting
    }
#else
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    internal static void Init()
    {
        Application.quitting += Quit;                         // Player 用 Application.quitting
        WebRTC.InitializeInternal();
    }
#endif
    internal static void Quit()
    {
#if UNITY_EDITOR
        AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        AssemblyReloadEvents.afterAssemblyReload  -= OnAfterAssemblyReload;
#endif
        WebRTC.DisposeInternal();
    }
}
```
— <https://github.com/Unity-Technologies/com.unity.webrtc/blob/main/Runtime/Scripts/Context.cs>
（3.0.0-pre.8 tag 下同一文件同一实现；该 tag 的 `package.json` 声明 `"unity": "2020.3"`，即**这就是 2022.3 上会跑的代码**）

可提炼的五条模式：

| # | 模式 | 说明 |
| --- | --- | --- |
| P1 | **`#if UNITY_EDITOR` 与 Player 走两套完全不同的注册路径** | 编辑器靠 `[InitializeOnLoad]` 静态构造，Player 靠 `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`。**不要**试图用一套覆盖两边 |
| P2 | **native 全局态在 `beforeAssemblyReload` 拆、在 `afterAssemblyReload` 重建** | 而不是在进出播放时拆。这样"域重载"这一个概念统一覆盖了「脚本重编译」和「点 Play」两种触发源 |
| P3 | **编辑器用 `EditorApplication.quitting`，Player 用 `Application.quitting`** | 二者不可互换（`Application.quitting` 在编辑器里表示的是"退出播放"，不是"关编辑器"，见 §2） |
| P4 | **退出时先反注册自己的事件订阅，再拆 native** | `Quit()` 里先 `-=` 两个 AssemblyReloadEvents，避免关编辑器过程中再触发一次重建 |
| P5 | **`Context.Dispose()` 遍历弱引用表，把所有还活着的托管包装体 `Dispose()` 掉** | 见下 |

`Context.Dispose()`：

```csharp
foreach (var value in table.CopiedValues)     // WeakReferenceTable
{
    if (value == null) continue;
    (value as IDisposable)?.Dispose();        // 主动级联 Dispose 所有存活对象
}
table.Clear();
NativeMethods.ContextDestroy(id);
```

**注意 `WeakReferenceTable`**（`Runtime/Scripts/WeakReferenceTable.cs`）：内部是 `Hashtable` + `WeakReference`，读写一律 `Hashtable.Synchronized(...)`。这和本项目已锁定的「句柄查找表持弱引用」完全一致——**官方也是这么做的**，`HandleTable.cs` 现状的强引用 `Dictionary` 是要改的那个。

### 5.2 `com.unity.webrtc` 做错 / 不该抄的地方

| 反面点 | 现状 | 为什么别抄 |
| --- | --- | --- |
| **finalizer 直接 P/Invoke** | `~RTCPeerConnection() { this.Dispose(); }`、`~RTCDataChannel() { this.Dispose(); }`，而 `Dispose()` 里是 `Close()` + `Context.DeletePeerConnection(self)` + `Table.Remove(self)` | 这正是 §4 判定要避开的形状：终结器线程上进 native、并且动共享表。它能"大体工作"是因为有 `!WebRTC.Context.IsNull` 这道静态守卫（Context 已销毁就跳过 P/Invoke），本质是**用一个全局开关把终结器变成 no-op**——不如干脆别 P/Invoke |
| **没有主线程断言** | `Dispose()` 可从任意线程调 | 本项目已锁定「所有公开 API 含 `Dispose()` 仅主线程，Editor/Dev 下断言」，比它严格，好事 |
| **无泄漏诊断** | 忘了 `Dispose()` 只会静默由 finalizer 兜底 | 本项目要的 `DisposeSentinel` 式报错比它强 |

它用来把 native 回调搬到主线程的方式也值得对照：`[AOT.MonoPInvokeCallback]` 静态 thunk → `WebRTC.Sync(ptr, action)` → `s_syncContext.Post(...)`（`ExecutableUnitySynchronizationContext`），投递前还检查 `s_context == null || !Table.ContainsKey(ptr)` 就丢弃——**"对象已销毁则丢弃迟到回调"**。本项目用 native 侧队列 + PlayerLoop pump 达到同样目的，但**同一条"迟到事件要能被安全丢弃"的规则必须保留**（`Pump()` 里的 `TryGetPc/TryGetDc` 失败即 `break`/跳过，现状已经是对的）。

### 5.3 `Unity.Collections` — 泄漏诊断的第一方范式

见 §4.4。要点：`DisposeSentinel` 的 finalizer **只报错、不释放**，门控在 `ENABLE_UNITY_COLLECTIONS_CHECKS`，报错内容包含**分配处**信息；运行时开关是 `NativeLeakDetection.Mode`（三档）。

### 5.4 UniTask / R3 — PlayerLoop 注册的社区事实标准

两者实现几乎一致（R3 的 `PlayerLoopHelper` 就在本仓库 `Library/PackageCache/com.cysharp.r3@c6f8a932d9/Runtime/PlayerLoopHelper.cs`）：

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
static void Init()
{
#if UNITY_EDITOR
    // When domain reload is disabled, re-initialization is required when entering play mode;
    // otherwise, pending tasks will leak between play mode sessions.
    var domainReloadDisabled = UnityEditor.EditorSettings.enterPlayModeOptionsEnabled &&
        UnityEditor.EditorSettings.enterPlayModeOptions.HasFlag(UnityEditor.EnterPlayModeOptions.DisableDomainReload);
    if (!domainReloadDisabled && runners != null) return;
#endif
    var playerLoop = PlayerLoop.GetCurrentPlayerLoop();   // 不是 GetDefaultPlayerLoop()
    Initialize(ref playerLoop);
}

#if UNITY_EDITOR
[InitializeOnLoadMethod]
static void InitOnEditor() { Init(); EditorApplication.update += ForceEditorPlayerLoopUpdate; }
#endif
```

四条可抄的规则：

1. **`GetCurrentPlayerLoop()`，不要 `GetDefaultPlayerLoop()`**。后者会把别的包插进去的系统全部抹掉（UniTask 只在 `< 2019.3` 才用 Default）。本项目 `RegisterPump()` 已经用的是 `GetCurrentPlayerLoop()`，✅。
2. **插入前先按 `type` 删除同类型旧项**（UniTask 的 `RemoveRunner()`）。本项目 `InsertPump()` 里 `list.RemoveAll(s => s.type == typeof(DataChannelRuntime))` 已做，✅。
3. **显式处理"域重载被禁用"**：`EditorSettings.enterPlayModeOptionsEnabled && ...HasFlag(EnterPlayModeOptions.DisableDomainReload)`。这是 2022.3 上唯一可靠的"我这次进播放到底有没有被 reset"判定方式。
4. **编辑器里也要装**（`[InitializeOnLoadMethod]`），否则 Edit Mode 下 pump 不跑，Editor 工具/测试拿不到事件。**本项目当前只在 `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` 里 `RegisterPump()`，Edit Mode 完全没有 pump** —— 这是一个现存缺口（§6）。

值得注意的是：UniTask/R3 **都不在退出播放时把自己的 PlayerLoopSystem 摘掉**，只在 `playModeStateChanged` 的 `ExitingEditMode` / `EnteredEditMode` 时 **drain + clear 自己的队列**。也就是说社区共识是「**清空状态，而不是拆结构**」（原因见 §1）。

## 6. Recommendation — 落到本仓库的具体改法

按「先做能救命的、再做能省心的」排序。每条都标了对应的 SPEC 条款或本文小节。

### 6.1 P0 — 现在就该改的三件事

**① 删掉两个 finalizer 里的 P/Invoke，换成只记日志（§4）**

`Runtime/DataChannel.cs:99-110` 与 `Runtime/PeerConnection.cs:104-115` 现状：

```csharp
~DataChannel()
{
    try
    {
        if (!_disposed)
        {
            NativeMethods.dcu_dc_destroy(NativeHandle);   // ← 终结器线程进 native，会阻塞
            HandleTable.UnregisterDc(NativeHandle);       // ← 终结器线程抢主线程也用的锁
        }
    }
    catch { }
}
```

目标形状（两个类同构）：

```csharp
#if UNITY_EDITOR || DEVELOPMENT_BUILD
private readonly string _creationStack;   // 构造时按 LeakDetectionMode 决定是否抓
#endif

~DataChannel()
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    if (_disposed) return;
    // 不进 native、不加锁、不碰任何 Unity 静态状态、不走 DataChannelLog.Emit
    DataChannelLeakLog.Enqueue(
        "DataChannel(label=" + Label + ", handle=" + NativeHandle +
        ") was garbage-collected without Dispose(). Created at:\n" + _creationStack);
#endif
}
```

约束（逐条对应 §4.4）：

- `#if UNITY_EDITOR || DEVELOPMENT_BUILD` —— 与本包既有惯例一致（`DataChannelLog.EnsureDefaults()` 已用同一组 define）。Release 下**连 finalizer 本身都不编译进去**，`_creationStack` 字段也不存在。
- **不要走 `DataChannelLog.Emit()`**：它会 `Message?.Invoke(...)` 调用**用户注册的事件处理器**——那会把用户代码拉到终结器线程上执行，直接违反已锁定的「主线程 only」约定。必须用一条独立的、只写 `ConcurrentQueue<string>` 的路径。
- `DataChannelLeakLog` 的出队与 `Debug.LogError` 由 `DataChannelRuntime.Pump()` 在主线程做。
- 三档开关照抄 Unity：`Disabled` / `Enabled` / `EnabledWithStackTrace`（对齐 `Unity.Collections.NativeLeakDetection.Mode`），Editor/Dev 默认 `EnabledWithStackTrace`。
- `Dispose()` 里保留现有的 `GC.SuppressFinalize(this)`。

**② 补上 `dcu_shutdown()` 的调用点（§3.3）**

新增 `Packages/datachannel-unity/Editor/DataChannelUnity.Editor.asmdef`（目前包里只有 `Runtime/` 和 `Tests/Editor/`，没有 Editor 运行时程序集）+ 一个 `[InitializeOnLoad]` 类，骨架直接照 `com.unity.webrtc` 的 `ContextManager`（§5.1）：

```csharp
[InitializeOnLoad]
static class DataChannelEditorLifecycle
{
    static DataChannelEditorLifecycle()
    {
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        EditorApplication.playModeStateChanged    += OnPlayModeChanged;
        EditorApplication.quitting                += OnEditorQuit;
        DataChannelRuntime.RegisterPump();          // Edit Mode 也要 pump（补 B4）
    }

    static void OnBeforeReload()          => DataChannelRuntime.TearDown();
    static void OnEditorQuit()
    {
        AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;   // 先摘订阅（P4）
        EditorApplication.playModeStateChanged    -= OnPlayModeChanged;
        DataChannelRuntime.TearDown();
    }
    static void OnPlayModeChanged(PlayModeStateChange s)
    {
        if (s == PlayModeStateChange.ExitingPlayMode) DataChannelRuntime.TearDown();
        else if (s == PlayModeStateChange.EnteredEditMode) DataChannelRuntime.RegisterPump();
    }
}
```

`DataChannelRuntime.TearDown()` = `DisposeAllLive()` → `UnregisterPump()` → `dcu_shutdown()`，顺序不能反（§3.3 最后一行）。

**③ `HandleTable` 改弱引用（§5.1）**

`Runtime/Internal/HandleTable.cs` 现在两张 `Dictionary<int, T>` 持**强引用**，且从不在 GC 时清理 —— 这意味着**任何 `PeerConnection` / `DataChannel` 只要没被 `Dispose()` 就永远不会被 GC**，于是 §6.1① 的泄漏 finalizer **永远不会触发**。两件事必须一起改，否则泄漏诊断是死代码。

对齐已锁定决策：表持 `WeakReference`（`com.unity.webrtc` 的 `WeakReferenceTable` 是现成参考），PC 强引用其子 DC。`TryGet*` 命中已死的弱引用时顺手 `Remove`（惰性清扫）。

### 6.2 P1 — 语义修正

| 改动 | 位置 | 理由 |
| --- | --- | --- |
| `OnDomainReload()` → `ResetStaticsOnEnterPlayMode()` | `DataChannelRuntime.cs:41` | 它是 `RuntimeInitializeOnLoadMethod(SubsystemRegistration)`，**不在**任何域重载路径上触发（§2.2）。现名会持续误导后来者 |
| 该方法体内追加 `dcu_shutdown()`（`#if UNITY_EDITOR`） | 同上 | 覆盖 *Reload Domain 被关闭* 的进入播放路径；Reload Domain 开时这次调用是零成本 no-op（§3.3） |
| `Bootstrap()` 里追加 `Application.quitting += ...` | `DataChannelRuntime.cs:49` | Player 侧唯一的 shutdown 入口；编辑器里退播放也会响，与 `ExitingPlayMode` 互为双保险（官方原文："`Application.quitting` is invoked when exiting Play mode"） |
| 新增 `UnregisterPump()` | `DataChannelRuntime.cs` | 目前只有 `RegisterPump()`。实现就是 `InsertPump()` 的镜像：`GetCurrentPlayerLoop()` → 按 `typeof(DataChannelRuntime)` 过滤 → `SetPlayerLoop()`。参照 `ScriptBehaviourUpdateOrder.RemoveFromCurrentPlayerLoop()` |
| 主线程断言 | 所有 public API + `Dispose()` | 已锁定决策，`#if UNITY_EDITOR \|\| DEVELOPMENT_BUILD` 下比对 `Thread.CurrentThread.ManagedThreadId`（在 `SubsystemRegistration` 时缓存主线程 id，照 UniTask 的做法） |

### 6.3 P2 — 可观测性（建议新增两个只读 ABI）

| ABI | 用途 |
| --- | --- |
| `int dcu_is_inited(void)` | 域重载后 C# 静态量已清零，但 native 可能仍 `g_inited == true`。没有这个查询，C# 无法判断"要不要先 shutdown" |
| `int dcu_queue_depth(void)` | 让 §1.4 B1 的队列膨胀可观测。可在 PlayMode 测试里断言"退出播放后重进，队列深度为 0" |

两者都是只读、无副作用、无阻塞，加进 `dcu.h` 时记得 bump `DCU_ABI_VERSION`。

### 6.4 SPEC 对齐

`docs/SPEC.md` §6「Threading」里那句 **"Editor domain reload must unregister pump cleanly"** 是本文的直接来源。建议把它扩写成三句，因为"unregister pump"其实是最不重要的那一件：

```
- Editor domain reload / exiting play mode / application quit must:
  1. Dispose all live PeerConnections (cascading to their DataChannels) on the main thread
  2. Unregister the PlayerLoop pump
  3. Call dcu_shutdown() — the Editor never unloads the native library, so native
     global state (g_inited, event queue, libdatachannel thread pool, live PCs)
     survives domain reload and must be torn down explicitly
- Finalizers must never P/Invoke. They may only report leaks (Editor/Dev builds).
```

### 6.5 验收清单

| 检查 | 方法 |
| --- | --- |
| 退出播放后 native 无残留 | 退出播放，等 2 s，Console 里应看到 `dcu_shutdown` 的 native 日志；`dcu_is_inited()` 返回 0 |
| 队列不膨胀 | 建连收数据 → 退出播放 → 重进播放 → `dcu_queue_depth()` 首帧为 0 |
| 未 Dispose 会报错 | 建一个 PC 不 Dispose，置空引用，`GC.Collect()` + `WaitForPendingFinalizers()`，下一帧 Console 出现带创建栈的 error |
| 编辑器不卡 | 建连状态下点 Play / 改脚本触发重编译，**编辑器不得出现 >1 s 的卡顿**（有卡顿说明还有 finalizer 或 shutdown 在阻塞主线程） |
| 反复进出播放不叠加 | 进出播放 10 次，`otool`/Activity Monitor 观察线程数与内存应回到基线 |
| Edit Mode 有 pump | Edit Mode 下 `GetCurrentPlayerLoop()` 的 `Update` 子系统里能看到 `DataChannelUnity.DataChannelRuntime` |

---

## 7. 未能确认 / 需实测验证的点

| # | 未确认的事 | 为什么没确认 | 怎么验证 |
| --- | --- | --- | --- |
| U1 | **Unity 2022.3 在域重载时到底是"清空 PlayerLoop 里的托管槽位"还是"整体重置为 default"** | 需要往用户正在使用的 Editor 里插探针并触发域重载；悬垂托管委托理论上有崩溃风险，用户 AFK，判定为不该做的破坏性实验 | 见下方脚本。**§1.3 的工程结论不依赖这个答案** |
| ~~U2~~ **已实测确认** | **退出播放不触发域重载，因此 `beforeAssemblyReload` 在退出时不会响** | ~~官方步骤表只覆盖"进入播放"~~ | **已于 2026-08-02 经 Unity MCP 在本机 2022.3.62f3（`enterPlayModeOptionsEnabled = False`，即 Reload Domain 开）实测：驱动一次 Play→Stop 后，Editor.log 中进入播放之后再无任何 `Reloading assemblies` / `Domain Reload Profiling`；另有一组历史会话的进出播放呈现同样结果（两次独立观测）。顺带测得进入播放的钩子顺序为 `playModeStateChanged == ExitingEditMode` **先于** `beforeAssemblyReload`。<br><br>**推论**：播放期的 C# 静态量会原样漏进 Edit Mode；「只靠域重载事件驱动」的模型（`com.unity.webrtc` 即如此）在退出播放这一环有真窟窿，必须显式挂 `ExitingPlayMode`。详见 [#37](https://github.com/xuhuanhello/juice-c-sharp/issues/37)。<br><br>**方法学教训**：首次尝试用 MCP `execute_code` 挂钩子失败——动态编译的内存程序集随域重载一起销毁，用会被这次转换销毁的东西去测量这次转换，方向就是错的。最终结论改由 Editor.log 的域重载痕迹间接得出，零侵入。U1 若要实测，必须放**持久 Editor 脚本**。 |
| U3 | **`UnityEngine.Debug.Log*(string)` 从后台/终结器线程调用是否官方保证线程安全** | 2022.3 的 `Debug.Log` 文档页对线程只字未提；社区普遍依赖但无官方承诺 | 不必验证——§6.1① 采用的 `ConcurrentQueue` + 主线程输出方案绕开了这个问题 |
| U4 | **`rtcDelete*` 的阻塞在本项目回调形状下的实际时长分布** | 需要真实建连压测 | 建连打流，在 `dcu_dc_destroy` 两侧插时间戳，统计 p50/p99 |
| U5 | **`dcu_shutdown()` 后立刻 `dcu_init()` 时，libdatachannel 线程池到底有没有被真正回收** | `doCleanup()` 在 detached 线程上跑且带 `if (mGlobal) return` 的提前返回，结果依赖调度（§3.2 要点 4） | 在 `dcu_init`/`dcu_shutdown` 前后打印线程数（macOS 用 `task_threads`，或直接看 Activity Monitor） |
| U6 | **iOS / Android 上 `Application.quitting` 的实际触发率** | 官方明说 iOS "usually suspended as they don't quit"；Android 后台被杀也不触发 | 移动端冒烟测试时单独验证；必要时配 `OnApplicationPause(true)` 做主动断连 |

U1 / U2 的最小复现脚本（**会改动 Editor 会话，请在一个空项目或征得同意后跑**）：

```csharp
// Editor/PlayerLoopSurvivalProbe.cs
[InitializeOnLoad]
static class PlayerLoopSurvivalProbe
{
    const string Key = "dcu.probe";
    static PlayerLoopSurvivalProbe()
    {
        // 1) 报告上一次域装配后，探针是否还在循环里
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        bool found = Find(loop);
        Debug.Log($"[probe] after reload: presentInLoop={found}, marker={SessionState.GetString(Key,"<none>")}");

        // 2) 记录钩子命中
        AssemblyReloadEvents.beforeAssemblyReload += () =>
            SessionState.SetString(Key, SessionState.GetString(Key,"") + "|beforeReload");
        EditorApplication.playModeStateChanged += s =>
            SessionState.SetString(Key, SessionState.GetString(Key,"") + "|" + s);

        // 3) 重新插入探针（type 与 delegate 都来自持久程序集）
        Insert(ref loop);
        PlayerLoop.SetPlayerLoop(loop);
    }
    // Find/Insert 用 typeof(PlayerLoopSurvivalProbe) 做 marker，updateDelegate 用本类的空静态方法
}
```

操作序列：改一个脚本触发重编译 → 看 `presentInLoop` → 点 Play → 看 → 点 Stop → 看 → 把 *Reload Domain* 关掉再走一遍。四组读数就能把 §1.3 的表格从"推断"升级为"实测"。

---

## 8. Sources

### 8.1 Unity 官方文档（2022.3）

| 内容 | URL |
| --- | --- |
| **进入播放模式的完整有序步骤**（含 "GC and finalizers are called"） | <https://docs.unity3d.com/2022.3/Documentation/Manual/configurable-enter-play-mode-details.html> |
| Domain Reloading — 关闭后必须用 `SubsystemRegistration` 手动归零静态量 | <https://docs.unity3d.com/2022.3/Documentation/Manual/DomainReloading.html> |
| Configurable Enter Play Mode | <https://docs.unity3d.com/2022.3/Documentation/Manual/ConfigurableEnterPlayMode.html> |
| **"A native plug-in cannot be unloaded … you must restart Unity"** | <https://docs.unity3d.com/2022.3/Documentation/Manual/PluginInspector.html> |
| `RuntimeInitializeOnLoadMethodAttribute` — 各 LoadType 的执行顺序 | <https://docs.unity3d.com/2022.3/Documentation/ScriptReference/RuntimeInitializeOnLoadMethodAttribute.html> |
| `RuntimeInitializeLoadType` | <https://docs.unity3d.com/2022.3/Documentation/ScriptReference/RuntimeInitializeLoadType.html> |
| `AssemblyReloadEvents` | <https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssemblyReloadEvents.html> |
| `PlayModeStateChange` — 四态定义 | <https://docs.unity3d.com/2022.3/Documentation/ScriptReference/PlayModeStateChange.html> |
| **`Application.quitting` "is invoked when exiting Play mode"** | <https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Application-quitting.html> |
| `PlayerLoop.SetPlayerLoop` / `GetCurrentPlayerLoop` / `GetDefaultPlayerLoop`（**均未提及域重载**） | <https://docs.unity3d.com/2022.3/Documentation/ScriptReference/LowLevel.PlayerLoop.html> |
| **`DisposeSentinel`** — finalizer 只报泄漏、带分配处信息、`ENABLE_UNITY_COLLECTIONS_CHECKS` 门控 | <https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Unity.Collections.LowLevel.Unsafe.DisposeSentinel.html> |
| `NativeLeakDetection.Mode` — 三档泄漏检测开关 | <https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Unity.Collections.NativeLeakDetection.html> |

### 8.2 Unity 第一方包源码

| 内容 | 位置 |
| --- | --- |
| **`ContextManager`** — `[InitializeOnLoad]` + `beforeAssemblyReload`/`afterAssemblyReload` + `EditorApplication.quitting`；Player 侧 `SubsystemRegistration` + `Application.quitting` | <https://github.com/Unity-Technologies/com.unity.webrtc/blob/main/Runtime/Scripts/Context.cs>（3.0.0-pre.8 同实现，其 `package.json` 声明 `"unity": "2020.3"`） |
| `WeakReferenceTable`（`Hashtable` + `WeakReference` + `Hashtable.Synchronized`） | <https://github.com/Unity-Technologies/com.unity.webrtc/blob/main/Runtime/Scripts/WeakReferenceTable.cs> |
| `RefCountedObject` / `~RTCPeerConnection() { Dispose(); }`（**反面教材**：终结器直接 P/Invoke） | `Runtime/Scripts/RefCountedObject.cs`、`Runtime/Scripts/RTCPeerConnection.cs`（同仓库） |
| **`DefaultWorldInitialization`** — "no entry point in the Unity API"；`SubsystemRegistration` 里移除残留 PlayerLoop 项；代理 MonoBehaviour `OnDisable` | <https://github.com/needle-mirror/com.unity.entities/blob/master/Unity.Entities/DefaultWorldInitialization.cs> |
| **`RuntimeContentSystem`** — `ExitingPlayMode` 时 `RemoveFromPlayerLoop()` + 全局 `Cleanup()`；Edit Mode 用 `EditorApplication.update` | <https://github.com/needle-mirror/com.unity.entities/blob/master/Unity.Entities/Content/RuntimeContentSystem.cs> |
| `ScriptBehaviourUpdateOrder.RemoveFromCurrentPlayerLoop()` | <https://github.com/needle-mirror/com.unity.entities/blob/master/Unity.Entities/ScriptBehaviourUpdateOrder.cs> |
| `UnitySynchronizationContext` — "SynchronizationContext must be set before any user code is executed. This is done on Initial domain load and domain reload at MonoManager ReloadAssembly" | <https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Runtime/Export/Scripting/UnitySynchronizationContext.cs> |

### 8.3 社区事实标准

| 内容 | 位置 |
| --- | --- |
| UniTask `PlayerLoopHelper` — `GetCurrentPlayerLoop()`、`RemoveRunner()` 去重、`DisableDomainReload` 判定、`[InitializeOnLoadMethod]` 装 Edit Mode | <https://github.com/Cysharp/UniTask/blob/master/src/UniTask/Assets/Plugins/UniTask/Runtime/PlayerLoopHelper.cs> |
| R3 `PlayerLoopHelper`（同构，**本仓库已依赖**） | 本地：`Library/PackageCache/com.cysharp.r3@c6f8a932d9/Runtime/PlayerLoopHelper.cs`；上游：<https://github.com/Cysharp/R3/blob/main/src/R3.Unity/Assets/R3.Unity/Runtime/PlayerLoopHelper.cs> |

### 8.3b .NET 官方（`SafeHandle` 相关判断的依据）

| 内容 | URL |
| --- | --- |
| `SafeHandle` — critical finalization、"all the noncritical finalizers are called before any of the critical finalizers"、`ReleaseHandle()` "must be in a constrained execution region" | <https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.safehandle> |
| `RuntimeHelpers.PrepareConstrainedRegions` — `[Obsolete("The Constrained Execution Region (CER) feature is not supported.", DiagnosticId="SYSLIB0004")]` | <https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.runtimehelpers.prepareconstrainedregions> |

### 8.4 libdatachannel v0.24.5（本仓库 vendored，`native/subprojects/libdatachannel`，`git describe` = `v0.24.5`）

| 内容 | 位置 |
| --- | --- |
| `rtcDeleteDataChannel` / `rtcDeletePeerConnection` → `close()` → `erase*()` | `src/capi.cpp:437,959` |
| `rtcCleanup()` — `eraseAll()` + `"N objects were not properly destroyed before cleanup"` + `wait_for(10s)` + `"Cleanup timeout (possible deadlock or undestructible object)"` | `src/capi.cpp:1754` |
| `DataChannel::close()` → `triggerClosed()` + `resetCallbacks()` | `src/impl/datachannel.cpp` |
| **`synchronized_callback`** — `operator()` 全程持锁，赋 `nullptr` 需同一把锁 ⇒ `resetCallbacks()` 阻塞等在途回调 | `include/rtc/utils.hpp:53-85` |
| `Init::doInit()` / `doCleanup()` / `TokenPayload` 析构起 detached `"RTC cleanup"` 线程 | `src/impl/init.cpp:36-170` |
| `int lastId = 0;` 永不重置 ⇒ 句柄不复用 | `src/capi.cpp:41` |
| 上游仓库 | <https://github.com/paullouisageneau/libdatachannel/tree/v0.24.5> |

### 8.5 本仓库

| 内容 | 位置 |
| --- | --- |
| PlayerLoop pump + init + 误名的 `OnDomainReload` | `Packages/datachannel-unity/Runtime/DataChannelRuntime.cs` |
| 强引用句柄表（待改弱引用） | `Packages/datachannel-unity/Runtime/Internal/HandleTable.cs` |
| `Dispose()` + finalizer 对（finalizer 待改） | `Packages/datachannel-unity/Runtime/DataChannel.cs:99`、`Runtime/PeerConnection.cs:104` |
| `#if UNITY_EDITOR \|\| DEVELOPMENT_BUILD` 既有惯例 | `Packages/datachannel-unity/Runtime/DataChannelLog.cs:24` |
| `g_inited` / `g_queue` / `dcu_init` / `dcu_shutdown` | `native/dcu/src/dcu_impl.cpp:15-16,177-191`、`native/dcu/src/dcu_queue.hpp` |
| "Editor domain reload must unregister pump cleanly"（本文起点，未实现） | `docs/SPEC.md` §6 Threading |

### 8.6 本机实测

2026-08-02，`/Users/xsmxu/Projects/Unity/juice-c-sharp`，Unity **2022.3.62f3**，Edit Mode，经 MCP `execute_code`（内存编译，未落盘、未触发重载）：

```text
enterPlayModeOptionsEnabled = False
GetDefaultPlayerLoop() Update 子系统 = 4
GetCurrentPlayerLoop() Update 子系统 = 5   ← 多出的是 R3.R3LoopRunners+R3Update
R3 在 Initialization / EarlyUpdate / Update 各插一条，updateDelegate → R3.UnityFrameProvider.Run
DataChannelUnity.DataChannelRuntime：不在循环中（Edit Mode 下本包无 pump）
```

**未做**破坏性实验（插探针 + 触发域重载/进出播放），原因与补做方法见 §7。
