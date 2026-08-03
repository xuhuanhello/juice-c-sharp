# Research: dcu 层架在 libdatachannel 的 C API 还是 C++ API

**Ticket:** https://github.com/xuhuanhello/juice-c-sharp/issues/41
**Parent:** #26 ｜ **决策票:** #42（grilling，人来定）
**Date:** 2026-08-03
**Upstream pin studied:** [paullouisageneau/libdatachannel](https://github.com/paullouisageneau/libdatachannel) tag **`v0.24.5`**（本地 `native/subprojects/libdatachannel`）
**WebGL 后端:** [paullouisageneau/datachannel-wasm](https://github.com/paullouisageneau/datachannel-wasm) `3d88eb8`（v0.4.0，本地 `native/subprojects/datachannel-wasm`）

---

## Verdict（结论速览）

| 问题 | 答案 |
|---|---|
| **总体推荐** | **迁移到 C++ API**，且**在 #29/#30/#31/#37 落地之前**做（§8.3） |
| 迁移代价 | **2–3 人日**；相对「本来就要做的重写」净增量约 **+70/−63 行**，基本持平（§1.3、§8.2） |
| 「全有或全无」前提是否属实 | **属实**。C API 句柄注册在 `capi.cpp` 匿名 namespace 的私有 map（`src/capi.cpp:28-30`），C++ 建对象拿不到 `int`（§5.2） |
| #30 的「控制推、数据拉」还成立吗 | **成立**。`flushPendingMessages` 的 `while (messageCallback)` 在 impl 层，两条路径共享（`src/impl/channel.cpp:64-79`）（§2.3） |
| `rtcReceiveMessage` 的 C++ 对应物 | **就是它自己**：`rtcReceiveMessage` = `Channel::peek()` + `Channel::receive()`（`src/capi.cpp:878-899`）。C++ 少一次拷贝（§3.1） |
| 背压（`RECV_QUEUE_LIMIT=1024`，满则阻塞）是否保留 | **逐字保留**。在 `impl/queue.hpp:91-93` + `impl/internals.hpp:46`，位于两条 API 之下（§3.2） |
| WebGL 两个后端会更一致吗 | **配置面：真的一致**（`IceServer` 结构化构造两侧同签名）。**数据面：假的** —— wasm 的 `rtc::Channel` **没有** `receive()`/`peek()`/`availableAmount()`/`onAvailable()`，无队列、无背压（§4.2） |
| 能否解决 `rtcDeletePeerConnection` 不清子 DC | **能**，且这是 C 路线**修不干净**的（`dataChannelMap` 是上游私有）（§5.1–5.2） |
| C++ 会引入异常跨边界问题吗 | **会，但是 12 行代码**，且比 C API 的 `wrap` **更准确**（后者无 `catch(...)`，压平信息）（§6.1） |
| `RTC_CPP_EXPORT` 与「仅导出 `dcu_*`」冲突吗 | **不冲突**。`RTC_STATIC` 下全平台展开为空（`include/rtc/common.hpp:12-24`）（§6.2） |
| C++ 符号膨胀 / 可见性 | **与 API 选择无关**。当前走 C API 的构建**已经**泄漏 ~1551 个符号含 mangled `rtc::*`（`docs/research/symbol-visibility.md`）（§6.3） |
| 五张已关票的**决议**要推翻吗 | **一条都不用。** 落地方案：#34 零改动、#29/#37 **变简单**、#30 几乎不变、**#31 有一处实质性退步**（§7） |
| **唯一实质性退步** | #31 的错误码 `static_assert` 编译期门禁失效（改 catch 异常类型，无法编译期断言），须以**运行时契约测试**代偿（§7.4） |

---

## Primary sources

| Source | Role | URL / path |
|--------|------|------------|
| libdatachannel C++ API 头 | `Channel::receive/peek/availableAmount`、`PeerConnection`、`IceServer` 结构化构造 | 本地 `native/subprojects/libdatachannel/include/rtc/{channel,datachannel,peerconnection,configuration,common,global}.hpp`（tag v0.24.5） |
| libdatachannel C API 实现 | 证明 C API 是 C++ API 的 lambda 包装；句柄 map；`wrap()`；`rtcDeletePeerConnection` 不级联 | 本地 `native/subprojects/libdatachannel/src/capi.cpp` |
| libdatachannel impl 层 | 推/拉分叉点、接收队列、背压 | 本地 `src/impl/{channel.cpp,datachannel.cpp,queue.hpp,internals.hpp,configuration.cpp}` |
| datachannel-wasm C++ API | WebGL 后端的**裁剪子集**；`IceServer` URL 构造不解析 | 本地 `native/subprojects/datachannel-wasm/wasm/include/rtc/*.hpp`、`wasm/src/*.cpp`、`wasm/js/webrtc.js`（v0.4.0 / `3d88eb8`） |
| 本项目 dcu 层 | 待重写的对象；`build_ice_uri` | `native/dcu/src/dcu_impl.cpp`、`native/dcu/include/dcu.h` |
| 上游仓库（对照） | libdatachannel / datachannel-wasm | https://github.com/paullouisageneau/libdatachannel/tree/v0.24.5 ； https://github.com/paullouisageneau/datachannel-wasm |
| 既有决议 | 事件 ABI / 所有权 / 错误码 / 公开面 / 生命周期 | 本仓 issues #30 / #29 / #31 / #34 / #37 的 resolution comment |
| 既有研究 | 符号可见性实测（~1551 符号）、finalizer/线程约束、WebGL 后端 | `docs/research/symbol-visibility.md`、`docs/research/unity-native-plugin-lifecycle.md`、`docs/research/unity-webgl-datachannel-wasm.md` |
| 产品约束 | 静态链接、仅导出 `dcu_*`、WebGL 例外 | `docs/SPEC.md` §2 §4 §8 §9 |

**本文未能确认 / 需实测的事项**（不要当成已知）：

1. **二进制体积变化未实测**（§6.3）。定性判断持平或略减，若为决策关键须真正写出 C++ 版本再比 `size`。
2. **迁移行数估算未经编译验证**（§1.3），来自静态阅读。
3. **datachannel-wasm 是否会补上 `receive()`** 未知（§4.2）。以 v0.4.0 / `3d88eb8` 为准；若上游补齐，§4.2 结论需重估。
4. **浏览器对 `turn:user:pass@host` 形式 userinfo 的实际处理**未实测（§4.1）。判断「很可能不工作」基于 WebRTC 规范要求凭证走 `RTCIceServer.username`/`.credential` 字段，**但未在真实浏览器上验证**。这一条若要作为迁移的主要理由，应先做一次 WebGL 冒烟测试。

---

## 1. 迁移到 C++ API 的完整代价（问题 1）

### 1.0 一个必须先说清的前提：基线不是「现在的 `dcu_impl.cpp`」

`native/dcu/src/dcu_impl.cpp` 现在 363 行、18 个 `dcu_*` 导出。但 **#30 / #29 / #31 / #37 的决议尚未落地**，而它们要求的改动已经覆盖了这个文件的大部分：

- #30：事件 ABI 从 `dcu_event_peek` / `copy_payload` / `copy_payload2` / `pop`（4 个函数，`dcu_impl.cpp:345-361`）换成单次原子的 `dcu_event_next`；**停止调用 `rtcSetMessageCallback`**（删 `dcu_impl.cpp:146` 与 `on_dc_message` 整个函数 `:126-140`）；新增 `dcu_dc_receive` / `dcu_event_queue_depth`。`DCU_ABI_VERSION` 1 → 2。
- #29：引入句柄表、PC→DC 从属关系、级联 dispose。现在**一张表都没有**（直接把 libdatachannel 的 `int` 当句柄透传给 C#）。
- #31：错误码独立编号 + 显式映射层。现在是 `return rc == 0 ? DCU_OK : DCU_ERR_FAILURE` 的粗暴压平，遍布 `:267/273/283/293/323/329/335`。
- #37：`dcu_shutdown()` 返回未销毁计数 + 自维护计数。现在 `dcu_shutdown` 只有 5 行（`:185-191`）。

**因此正确的问法不是「迁移要改多少」，而是「在本来就要重写的基础上，C++ 路线比 C 路线多写/少写多少」。** 下面按此口径逐项列。

### 1.1 逐项改动清单

| # | 项目 | C 路线（已计划的重写） | C++ 路线 | 增量 |
|---|---|---|---|---|
| 1 | **句柄表** | 仍需自建（记 PC→DC 从属、级联、代际），**且要与 `capi.cpp` 的私有 map 保持同步**（两份表） | 自建**一份**表，直接持 `shared_ptr<PeerConnection>` / `shared_ptr<DataChannel>` | **−**（少一份影子表） |
| 2 | **句柄分配 / 代际** | 复用上游 `int lastId` | 自写 `{index, generation}` 打包（约 40 行） | **+40 行**，但换掉 ABA 洞 |
| 3 | **表锁** | `capi.cpp` 内部有全局 `std::mutex`（`:40`），我们的影子表还要自己一把 | 自己一把 `std::mutex` | 持平 |
| 4 | **回调注册** | `rtcSetXxxCallback(id, fn)` ×9 | `obj->onXxx([h]{...})` ×9，**行数几乎相同** | 持平 |
| 5 | **回调「对象已死则丢弃」守卫** | `capi.cpp` 的 `getUserPointer` 顺带做（§2.2） | **必须自己写**：lambda 捕获句柄 → 查表验活 | **+**（真实新增，见 §5.3） |
| 6 | **异常边界 `wrap()`** | 上游 `capi.cpp:215-226` 已做 | 自写 12 行模板（§6.1） | **+12 行**，但更准确 |
| 7 | **ICE 配置** | `build_ice_uri` + `percent_encode_userinfo`（`:19-66`，**48 行**）+ 上游再解析回来 | `IceServer(host, port, user, pass, relayType)` 一行 | **−48 行**，且消掉正确性负担 |
| 8 | **`dcu_pc_config` → `Configuration`** | 填 `rtcConfiguration` 扁平结构体（`:239-254`） | 填 `rtc::Configuration`；`optional<size_t>` 等字段需判 0 再 `emplace` | 持平（略啰嗦一点） |
| 9 | **取 DC label** | `rtcGetDataChannelLabel(dc, buf, 256)`，**固定栈缓冲会截断**（`:151`） | `dc->label()` → `std::string` | **−**（顺手修一个 bug） |
| 10 | **收消息 `dcu_dc_receive`** | `rtcReceiveMessage`（peek+拷贝+receive 两阶段） | `receive()` 返回 `optional<message_variant>`，`std::visit` 分 binary/string | 持平；**少一次 payload 拷贝**（§3.1） |
| 11 | **文本/二进制区分** | 靠 `*size` 正负号（C API 自造编码） | `std::variant` 类型直接可判 | **−**（#30 已把这点转给 #32） |
| 12 | **`dcu_shutdown` 计数** | `rtcCleanup()` 返回 `void` 且**吞掉**两条诊断（`capi.cpp:1754-1768`），#37 决议自维护计数 | 自维护计数（同样要写）**＋** `rtc::Cleanup()` 返回 `std::shared_future<void>`，**超时可自定** | **−**（见 §7.5，解一个 #37 的真痛点） |
| 13 | **日志 / `dcu_set_log_level`** | `rtcInitLogger(rtcLogLevel, cb)` | `rtc::InitLogger(LogLevel, LogCallback)`，`LogCallback` 是 `std::function` | 持平 |
| 14 | **`rtcPreload`** | `rtcPreload()` | `rtc::Preload()` | 持平 |
| 15 | **枚举映射 `static_assert`** | #31 决议：`DCU_*` ↔ `RTC_ERR_*` / `rtcState` 显式映射 + `static_assert` 编译期门禁 | `rtcState` 类枚举仍在 `rtc.h`（C++ 枚举以其为底值，见 `peerconnection.hpp:45-52`），**状态码门禁保留**；但**错误码门禁消失**（无 `RTC_ERR_*` 可断言） | **⚠ 见 §7.4，这是唯一实质性退步** |

### 1.2 我们要自己维护什么

ticket 问「我们要自己维护什么（句柄表、生命周期、线程边界）」，逐个回答：

- **句柄表** —— 要。但如 §5.2 所述，**#29 的决议已经要求我们建表**，C 路线下这张表还得和 `capi.cpp` 的私有 map 双向对账。C++ 路线是「一份表」，C 路线是「两份表」。
- **生命周期** —— 要，但**更简单**。C++ 下我们直接持 `shared_ptr`，对象存活由我们的表决定；`rtcDeletePeerConnection` 那个不级联的坑（§5.1）不复存在。#30 要求的「派发 `DC_CLOSED` 前先把 recv queue 拉空」在 C++ 下更稳：我们手里的 `shared_ptr<DataChannel>` 保证对象仍在，C 路线则依赖「句柄删除前 `getChannel()` 仍有效」这个时序假设。
- **线程边界** —— **不变**。回调仍来自 libdatachannel 线程池（§2.1，两条路径同一处触发），事件仍入我们的队列，pump 仍在主线程排空。#29 的「主线程 only」、#28 的 finalizer 约束**一字不改**。C++ API 不引入任何新线程，`rtc::Cleanup()` 的等待语义与 `rtcCleanup()` 内部所用的是同一个（`capi.cpp:1761`）。

### 1.3 规模估算

`dcu_impl.cpp` 现有 363 行。按上表：

- **净删**：`build_ice_uri` + `percent_encode_userinfo` 48 行、`on_dc_message` 15 行（#30 本来就要删）。
- **净增**：句柄表 + 代际约 120–150 行（**#29 要求，C 路线同样要写**，C++ 路线独有的增量约 40 行代际逻辑）、`wrap()` 12 行、回调验活守卫散布约 20 行。
- **同形改写**（逐行对应、机械替换）：约 180 行 —— 回调注册、配置映射、每个 `dcu_*` 函数体。

**C++ 路线相对 C 路线的净增量：约 +70 行、−63 行，即基本持平**，主要成本不在行数而在**一次性的谨慎**（§5.3 的验活守卫、§6.4 的捕获纪律）。

> **必须明确的不确定性**：以上是静态阅读得出的估算，我**没有**实际写出 C++ 版本编译验证。真实工时的主要风险不在上表任何一项，而在「回调验活守卫」写错导致的偶发 use-after-free —— 这类 bug 不会在编译期暴露，只会在压测/退出时随机崩。**估算工时时应把测试成本按 1:1 计入，而非按代码行数外推。**

## 2. 回调模型的差异（问题 2）

### 2.1 C API 的回调就是 C++ 回调的一层 lambda 包装

C API 没有独立的回调机制。每个 `rtcSetXxxCallback` 都是「把函数指针 + user pointer 包成一个 `std::function` 塞给 C++ 对象」：

```cpp
// src/capi.cpp:766-777 — rtcSetMessageCallback
auto channel = getChannel(id);
if (cb)
    channel->onMessage(
        [id, cb](binary b) {
            if (auto ptr = getUserPointer(id))
                cb(id, reinterpret_cast<const char *>(b.data()), int(b.size()), *ptr);
        },
        [id, cb](string s) {
            if (auto ptr = getUserPointer(id))
                cb(id, s.c_str(), -int(s.size() + 1), *ptr);
        });
else
    channel->onMessage(nullptr);
```

`rtcSetLocalDescriptionCallback`（`src/capi.cpp:446-458`）等同理。所以：

- **触发时机、触发线程、触发次数完全相同** —— 两者调的是同一个 `impl::Channel` / `impl::PeerConnection`。
- C API 额外做的事只有两件：把 `shared_ptr` 对象映射成 `int`，以及把 `binary`/`string` 摊平成 `(const char*, int)`（用 **负 size 表示字符串**，这是 C API 自造的编码约定，C++ 侧是 `std::variant<binary, string>`）。

### 2.2 我们现在的用法对号入座

`dcu_impl.cpp` 现在写的是自由函数 + 入队（`dcu_impl.cpp:70-169`）。换成 C++ API 后形状几乎不变，只是签名从函数指针变成 lambda，且 `int pc` 要换成我们自己的句柄：

| 现在（C API） | 换成 C++ API |
|---|---|
| `rtcSetLocalDescriptionCallback(pc, on_local_description)` | `pc->onLocalDescription([h](Description d){ ... })` |
| `void on_state_change(int pc, rtcState s, void*)` | `pc->onStateChange([h](PeerConnection::State s){ ... })` |
| `rtcSetDataChannelCallback(pc, on_data_channel)` | `pc->onDataChannel([h](shared_ptr<DataChannel> dc){ ... })` |
| `rtcGetDataChannelLabel(dc, buf, 256)`（`dcu_impl.cpp:151`，**固定 256 字节栈缓冲，会截断**） | `dc->label()` → `std::string`，无截断 |

`void *user_ptr` 这一路**整体消失**：C++ 的 lambda 直接按值捕获我们自己的句柄。`userPointerMap` 那层间接（`src/capi.cpp:39-50`）不再需要。

> **注意一个真实的行为差异**：C API 的回调 lambda 里有 `if (auto ptr = getUserPointer(id))` 的守卫 —— 若 id 已从 map 中删除，回调**静默不投递**。这是 C API 顺手给的一层「对象已销毁则丢弃事件」保护。自管句柄表时必须自己复现这层守卫（见 §5.3），否则会向已销毁对象投递事件。

### 2.3 「控制推、数据拉」在 C++ API 上成立吗？——成立，且更直接

#30 的决议依赖一个性质：**不设 message callback，消息就留在 `mRecvQueue` 里**。这个性质由 `impl::Channel::flushPendingMessages` 保证：

```cpp
// src/impl/channel.cpp:64-79
void Channel::flushPendingMessages() {
    if (!mOpenTriggered)
        return;
    while (messageCallback) {      // ← 仅当 messageCallback 非空才排空
        auto next = receive();
        if (!next) break;
        try { messageCallback(*next); } catch (...) { ... }
    }
}
```

这段代码在 `rtc::impl` 层，**位于 C API 与 C++ API 的下方**，两条路径共享。`rtcSetMessageCallback(dc, nullptr)` 与 C++ 侧「不调用 `onMessage`」落到的是同一个 `messageCallback == nullptr` 状态。

**结论：#30 的推/拉混合模型在 C++ API 上原样成立。** 而且更干净 —— C API 下「不设回调」是一个需要注释解释的隐式约定（要读 `capi.cpp` 才知道 `nullptr` 会传导到哪里）；C++ 下就是字面意义的「不注册 `onMessage`」。

---

## 3. `rtcReceiveMessage` 的 C++ 对应物与背压（问题 3）

### 3.1 `rtcReceiveMessage` 逐字就是 `peek()` + `receive()`

```cpp
// src/capi.cpp:878-899（节选）
int rtcReceiveMessage(int id, char *buffer, int *size) {
    return wrap([&] {
        auto channel = getChannel(id);
        ...
        auto message = channel->peek();
        if (!message)
            return RTC_ERR_NOT_AVAIL;
        return std::visit(overloaded{
            [&](binary b) {
                int ret = copyAndReturn(std::move(b), buffer, *size);
                if (ret >= 0) {
                    *size = ret;
                    if (buffer) {
                        channel->receive(); // discard
                    }
                    return RTC_ERR_SUCCESS;
                } ...
```

即 `rtcReceiveMessage` = `Channel::peek()`（`include/rtc/channel.hpp:51`）拿到副本 → 拷进调用者缓冲 → `Channel::receive()`（同 `:50`）丢弃队头。`rtcGetAvailableAmount` 更是一行直传：

```cpp
// src/capi.cpp:860-862
int rtcGetAvailableAmount(int id) {
    return wrap([id] { return int(getChannel(id)->availableAmount()); });
}
```

**语义等价性：C++ API 是被包装的那一方，不存在「是否等价」的问题 —— 它是同一个函数。** C API 在此之上只加了「先 peek 再 receive」的两阶段协议，目的是让 C 调用者能在缓冲区不足时先问尺寸（`RTC_ERR_TOO_SMALL` + 回填 `*size`）。C++ 侧 `receive()` 直接返回 `optional<message_variant>`，尺寸协商这一整套**不需要**。

### 3.2 背压性质原样保留

`RECV_QUEUE_LIMIT` 与阻塞 push 都在 impl 层，两条 API 路径共享：

```cpp
// src/impl/internals.hpp:46
const size_t RECV_QUEUE_LIMIT = 1024; // Max per-channel queue size (messages)

// src/impl/datachannel.cpp:80
      mRecvQueue(RECV_QUEUE_LIMIT, message_size_func) {

// src/impl/datachannel.cpp:235-237（收到 String/Binary 时）
        mRecvQueue.push(message);
        triggerAvailable(mRecvQueue.size());

// src/impl/queue.hpp:91-93
template <typename T> void Queue<T>::push(T element) {
    ...
    mPushCondition.wait(lock, [this]() { return mLimit == 0 || mQueue.size() < mLimit || mStopping; });
```

`Queue::push` 在满时 `wait` 在 `mPushCondition` 上 —— 即**阻塞 SCTP 接收线程**，这正是 #30 想要的真实背压。而 `DataChannel::receive()` / `peek()` / `availableAmount()` 就是 `mRecvQueue.pop()` / `.peek()` / `.amount()`（`src/impl/datachannel.cpp:117-127`）。

**结论：#30 决议 2/3 在 C++ API 上完全成立，且少一层 `peek`+拷贝+`receive` 的往返。**

> 一个可量化的收益：C API 下取一条消息，若调用者要先问尺寸，会 `peek()` 两次（每次 `to_variant` **拷贝一份 payload**，见 `src/impl/datachannel.cpp:132-135`）。C++ 下 `receive()` 是 `std::move` 出队（`:117-119`），一次移动即可。对大消息这不是微优化。

---

## 4. WebGL 侧的影响：datachannel-wasm 逐类对照（问题 4）

这是本票**最重要**的一节，因为它推翻了「两个后端形状会更一致」这一直觉里最乐观的部分，同时也给出了 C++ 路线唯一无可替代的收益。

datachannel-wasm 的 C++ API **看起来**像 libdatachannel，但**是一个被大幅裁剪的子集**，且在关键处语义不同。逐个对照：

### 4.1 `rtc::IceServer` —— 完全一致，且这是 C++ 路线的最强论据

| | libdatachannel v0.24.5 | datachannel-wasm v0.4.0 |
|---|---|---|
| 头文件 | `include/rtc/configuration.hpp:18-41` | `wasm/include/rtc/configuration.hpp:31-55` |
| 结构化 STUN 构造 | `IceServer(string, uint16_t)` / `(string, string)` | **同名同签名** |
| 结构化 TURN 构造 | `IceServer(string, uint16_t, string user, string pass, RelayType)` | **同名同签名** |
| 字段 | `hostname/port/type/username/password/relayType` | **同名同类型** |
| `Type` 枚举 | `{ Stun, Turn }` | `{ Stun, Turn, Dummy }` ← 多一个 |
| URL 构造 `IceServer(const string&)` | **解析** URL（`src/configuration.cpp:43-87`），`url_decode` 出 username/password | **不解析**，整串塞进 `hostname` 并标记 `Type::Dummy`（`wasm/src/configuration.cpp:27`） |

wasm 头文件里那句注释是决定性的：

```cpp
// wasm/include/rtc/configuration.hpp:37-38
// Note: Contrary to libdatachannel, the URL constructor does not parse the URL.
// Instead, it creates a Dummy IceServer to pass the URL as-is to the browser.
```

后果非常具体。若我们**继续自己拼 URI**（现状 `build_ice_uri`，`dcu_impl.cpp:36-66`）：

- **native**：`rtcCreatePeerConnection` 走 `c.iceServers.emplace_back(string(config->iceServers[i]))`（`src/capi.cpp:395-396`），即调 URL 构造函数，把我们刚拼进去的 `user:pass@` **再解析出来**（`src/configuration.cpp:69-70` 的 `url_decode`）。一次无谓的编码/解码往返，且我们的 `percent_encode_userinfo`（`dcu_impl.cpp:19-34`）与它的 `url_decode` 必须逐字节互逆才不出错 —— 这是我们自己承担的、无人测试的正确性负担。
- **WebGL**：URL **原样**传给浏览器的 `RTCConfiguration`，`username`/`password` 字段留空。而浏览器对 `turn:user:pass@host` 这种 userinfo 形式的处理与 libdatachannel **不同**（WebRTC 规范要求凭证走 `RTCIceServer.username` / `.credential` 字段，URL 里的 userinfo 不是标准途径）。

即：**自拼 URI 的方案在两个后端上行为不一致，且 WebGL 侧很可能直接不工作。** 而结构化构造在两侧都走同一条路 —— wasm 的 `PeerConnection` 构造函数把结构化字段拆成三个平行数组传给 JS 胶水：

```cpp
// wasm/src/peerconnection.cpp:130-143
for (const IceServer &iceServer : config.iceServers) {
    username_ptrs.push_back(iceServer.username.c_str());
    password_ptrs.push_back(iceServer.password.c_str());
}
mId = rtcCreatePeerConnection(url_ptrs.data(), username_ptrs.data(), password_ptrs.data(),
                              config.iceServers.size());
```

**这一条是 C++ 路线唯一「不选就得自己补」的收益**：`IceServer` 的结构化构造在两个后端上逐字节同形。

### 4.2 `rtc::Channel` —— **严重不一致，拉模式在 WebGL 上根本不存在**

| 成员 | libdatachannel（`include/rtc/channel.hpp`） | datachannel-wasm（`wasm/include/rtc/channel.hpp`） |
|---|---|---|
| `close()` / `send(message_variant)` / `send(const byte*, size_t)` | ✅ `:27-29` | ✅ 同签名 |
| `isOpen()` / `isClosed()` / `bufferedAmount()` | ✅ `:31-34` | ✅ |
| `maxMessageSize()` | ✅ `:33` | ❌ **无** |
| `onOpen` / `onClosed` / `onError` / `onMessage` ×2 / `onBufferedAmountLow` | ✅ `:36-45` | ✅ 同签名 |
| `setBufferedAmountLowThreshold` | ✅ `:45` | ✅（`virtual`） |
| `resetCallbacks()` | ✅ `:47` | ❌ **无** |
| **`receive()`** | ✅ `:50` | ❌ **无** |
| **`peek()`** | ✅ `:51` | ❌ **无** |
| **`availableAmount()`** | ✅ `:52` | ❌ **无** |
| **`onAvailable()`** | ✅ `:53` | ❌ **无** |
| 实现形态 | `CheshireCat<impl::Channel>` pimpl | 裸虚基类，回调是直接成员 |

wasm 侧根本**没有接收队列**。消息到达即调回调，回调为空则**直接丢弃**：

```cpp
// wasm/src/channel.cpp:73-75
void Channel::triggerMessage(const message_variant data) {
    if (mMessageCallback)
        mMessageCallback(data);
}
```

链路是 `js/webrtc.js:414-417` 的 `dataChannel.onmessage` → `DataChannel::MessageCallback`（`wasm/src/datachannel.cpp:64-72`）→ `triggerMessage`。中间**没有任何缓冲，也没有任何背压** —— 浏览器的 `RTCDataChannel.onmessage` 不可暂停。

**结论（对问题 4 的直接回答）：不。「两个后端形状更一致」在数据面上是假的。**

- ICE 配置面：C++ 路线确实带来**真实**的一致性（§4.1）。
- 数据收发面：无论 native 走 C 还是 C++，**WebGL 侧都必须自己实现一个 `RECV_QUEUE_LIMIT` 等价物**（一个有界队列 + 丢弃或流控策略），才能对上 #30 的拉模式。C++ 路线在这里**一点忙都帮不上**。
- 且 WebGL 侧连背压都做不到真实：JS 单线程、`onmessage` 不可阻塞，队列满时只能**丢弃或无界增长**，不可能像 `Queue::push` 那样阻塞生产者。这是浏览器平台的硬限制，与 API 选择无关。

> **未能确认的点**：datachannel-wasm 的 `Channel` 缺 `receive()`，我未在其仓库中找到任何上游计划补齐的迹象（该 pin 为 v0.4.0 / `3d88eb8`）。若上游后续补上，本节结论需重估。

### 4.3 `rtc::PeerConnection` —— wasm 是明显子集

| 成员 | libdatachannel（`include/rtc/peerconnection.hpp`） | wasm（`wasm/include/rtc/peerconnection.hpp`） |
|---|---|---|
| `PeerConnection(Configuration)` | ✅ `:79`（按值） | ✅（`const &`） |
| `close()` | ✅ `:82` | ✅ |
| `state/iceState/gatheringState/signalingState` | ✅ `:85-88` | ✅ 枚举**值也对齐**（`RTC_NEW`=0…） |
| `localDescription()` / `remoteDescription()` | ✅ `:91-92` | ✅ |
| `createDataChannel(string, DataChannelInit)` | ✅ `:111` | ✅ 但 `DataChannelInit` 仅有 `reliability`（wasm `:39-41`），**无 `negotiated` / `id` / `protocol`** |
| `setRemoteDescription` / `addRemoteCandidate` | ✅ `:101-102` | ✅ |
| `onDataChannel` / `onLocalDescription` / `onLocalCandidate` / `on*StateChange` | ✅ `:113-123` | ✅ 全部同名 |
| **`setLocalDescription()`** | ✅ `:99` | ❌ **无**（wasm 依赖浏览器自动协商） |
| `gatherLocalCandidates()` | ✅ `:100` | ❌ 无 |
| `createOffer()` / `createAnswer()` | ✅ `:105-106` | ❌ 无 |
| `remoteMaxMessageSize()` / `localAddress()` / `remoteAddress()` / `maxDataChannelId()` / `getSelectedCandidatePair()` | ✅ `:93-97` | ❌ 无 |
| `resetCallbacks()` / `remoteFingerprint()` | ✅ `:125-126` | ❌ 无 |
| 统计 `bytesSent/bytesReceived/rtt/clearStats` | ✅ `:129-132` | ❌ 无 |
| 媒体 `addTrack` / `onTrack` / `setMediaHandler` | ✅ `:108-116` | ❌ 无（本项目不需要） |

`rtc::Configuration` 差距更大：libdatachannel 有 17 个字段（`include/rtc/configuration.hpp:66-96`），wasm **只有 `iceServers` 一个**（`wasm/include/rtc/configuration.hpp:57-59`）。我们现在从 `dcu_pc_config` 映射的 `portRangeBegin/End`、`bindAddress`、`enableIceTcp`、`enableIceUdpMux`、`mtu`、`maxMessageSize`、`iceTransportPolicy`（`dcu_impl.cpp:245-254`）**在 WebGL 上全部无处安放** —— 但这与 C/C++ 选择无关，是平台差异，`dcu` 的 C 门面本来就得在 WebGL 后端里静默忽略它们。

### 4.4 小结

C++ 路线让**两个后端的 `dcu_*` 实现能共享同一套写法**（同名类、同名方法、同名回调），这是真实的；但**能共享的只是控制面**，数据面（`receive`/`availableAmount`/背压）和配置面（除 `iceServers` 外的一切）在 wasm 上都不存在，必须各写各的。

把「一致性」量化：wasm 的 `PeerConnection` 覆盖了 libdatachannel 的约 **13/30** 个公开方法，`Channel` 覆盖约 **10/15**，`Configuration` 覆盖 **1/17**。所以**共享的是命名与调用形状，不是实现**。

---

## 5. 已知会被解决的问题：句柄表泄漏（问题 5）

### 5.1 缺陷确认

```cpp
// src/capi.cpp:437-444
int rtcDeletePeerConnection(int pc) {
    return wrap([pc] {
        auto peerConnection = getPeerConnection(pc);
        peerConnection->close();
        erasePeerConnection(pc);
        return RTC_ERR_SUCCESS;
    });
}
```

而 `erasePeerConnection`（`src/capi.cpp:98-103`）只动 `peerConnectionMap` 与 `userPointerMap`：

```cpp
void erasePeerConnection(int pc) {
    std::lock_guard lock(mutex);
    if (peerConnectionMap.erase(pc) == 0)
        throw std::invalid_argument("Peer Connection ID does not exist");
    userPointerMap.erase(pc);
}
```

**`dataChannelMap` 里属于该 PC 的条目一个都不删。** 这些 `shared_ptr<DataChannel>` 会一直存活到调用者对每个 dc 显式调 `rtcDeleteDataChannel`，或到 `rtcCleanup()` 的 `eraseAll()`（`src/capi.cpp:123-145`）。#29 的观察属实。

### 5.2 自管句柄表能否干净解决？——能

我们自己的表可以做 C API 做不到的事：**记录 PC → 子 DC 的从属关系**。#29 已决议「PC 拥有其 DataChannel，级联 dispose」，自管表下这就是字面实现：

```cpp
struct PcEntry {
    std::shared_ptr<rtc::PeerConnection> pc;
    std::vector<uint64_t> children;   // dc handles
};
```

`dcu_pc_destroy(h)` 时遍历 `children` 一并擦除。C API 下我们**做不到这件事**（`dataChannelMap` 是 `capi.cpp` 的匿名 namespace 私有变量，我们既读不到也删不干净），只能在 `dcu` 层自己**再维护一份**父子关系表，然后逐个调 `rtcDeleteDataChannel` —— 也就是说：**这份关系表无论如何都要写**，区别只是 C 路线下我们写的表是 `capi.cpp` 那份表的影子，两份表必须保持同步；C++ 路线下只有一份表。

### 5.3 但要接手三件 C API 原本代劳的事

自管句柄表不是白拿，必须自己实现：

1. **句柄分配与失效**。C API 用单调递增 `int lastId`（`src/capi.cpp:41`，`++lastId`）。我们应当**做得更好**：用 64 位 `{index, generation}` 打包，让「已销毁句柄被重用后误命中新对象」（ABA）不可能发生 —— C API 的 `int` 有这个洞（`lastId` 溢出后回绕）。这也正是 #29「weak lookup table」决议想要的形状。
2. **锁**。C API 有一把全局 `std::mutex mutex`（`src/capi.cpp:40`）保护所有 map。我们要自己加。#29 已决议主线程 only，但**回调来自 libdatachannel 的线程池线程**，查表仍需加锁。
3. **「对象已销毁则丢弃回调」守卫**。见 §2.2 —— C API 靠 `if (auto ptr = getUserPointer(id))` 顺手实现。我们必须显式写：回调 lambda 捕获句柄（而非裸指针），触发时先查表验活，查不到就丢弃。**这是迁移中最容易漏、也最容易造成 use-after-free 的一点，必须作为迁移的头号测试目标。**

---

## 6. 已知会被引入的问题：异常/符号/体积（问题 6）

逐条核查 ticket 列出的四项担忧。结论是：**三项不成立或代价极小，一项是真实但可控的新增工作**。

### 6.1 异常跨边界 —— 真实，但是 12 行代码

C API 的 `wrap()` 是唯一一处真正的「C API 帮我们挡掉的东西」：

```cpp
// src/capi.cpp:215-226
template <typename F> int wrap(F func) {
    try {
        return int(func());
    } catch (const std::invalid_argument &e) {
        PLOG_ERROR << e.what();
        return RTC_ERR_INVALID;
    } catch (const std::exception &e) {
        PLOG_ERROR << e.what();
        return RTC_ERR_FAILURE;
    }
}
```

C++ API 会抛异常，而 `dcu_*` 是 `extern "C"`，异常**不得**逃逸到 P/Invoke 边界（会直接 crash Unity）。所以我们必须自己写等价物。

但这有三点缓和：

1. **代码量就是上面那 12 行**，`wrap` 是一个模板函数，套在每个 `dcu_*` 上一次。
2. **#31 已经决议错误码独立于 `RTC_ERR_*`，并要求显式枚举映射 + `static_assert`。** 也就是说：即使走 C 路线，我们**本来也要**把 `RTC_ERR_INVALID` 翻译成 `DCU_ERR_INVALID`。C++ 路线只是把「翻译别人的返回码」换成「翻译自己捕获的异常类型」——工作量相当，而且**更准确**：C API 把 `std::invalid_argument` 之外的一切都压成 `RTC_ERR_FAILURE`，丢失了信息；我们自己 catch 可以区分 `std::invalid_argument` / `std::runtime_error` / `std::bad_alloc` / `std::logic_error`，映射出更有意义的 `DCU_ERR_*`。
3. 必须加 `catch (...)` 兜底（C API 的 `wrap` **没有** `catch (...)`，只 catch `std::exception` —— 非 `std::exception` 派生的异常会直接穿透 `capi.cpp` 逃逸出来。**我们现在这条路上就有这个洞**）。

**净评估：这一项 C++ 路线略优，不是劣。**

### 6.2 `RTC_CPP_EXPORT` 与「仅导出 `dcu_*`」冲突吗？——不冲突

```cpp
// include/rtc/common.hpp:12-24
#ifdef RTC_STATIC
#define RTC_CPP_EXPORT              // ← 展开为空
#else // dynamic library
#ifdef _WIN32
#ifdef RTC_EXPORTS
#define RTC_CPP_EXPORT __declspec(dllexport)
#else
#define RTC_CPP_EXPORT __declspec(dllimport)
#endif
#else // not WIN32
#define RTC_CPP_EXPORT              // ← 也展开为空
#endif
#endif
```

我们**静态链接** libdatachannel（SPEC §8）。`RTC_STATIC` 一定义，`RTC_CPP_EXPORT` **在所有平台上都展开为空**。非 Windows 平台即使不定义 `RTC_STATIC` 也是空。所以它不会在任何平台上强制导出任何东西，与「仅导出 `dcu_*`」零冲突。

### 6.3 符号可见性与静态链接下的 C++ 符号膨胀 —— 与 API 选择无关

这是本节最重要的澄清。`docs/research/symbol-visibility.md` 已经测过当前构建（**走的正是 C API**）：

> Global defined symbols (`nm -gU`): **~1551** ／ 其中 `dcu_*`: **18** ／ Major leakers: **mangled `rtc::*` C++**, `usrsctp_*` / `sctp_*`

**用 C API 并没有挡住任何 `rtc::*` C++ 符号** —— 因为 `capi.cpp` 本身就是 C++，它把整个 `rtc::PeerConnection` / `rtc::DataChannel` 实现拖进静态库，符号照样在。改用 C++ API 后，我们的 `.o` 里会多出若干 `rtc::` 相关的模板实例化与 vtable 引用，但**链进来的库目标文件是同一批**。

真正的补救措施在两条路线上完全相同，且 #18 已经给出：`-fvisibility=hidden` + 平台链接器白名单（macOS `-exported_symbols_list`、ELF `--version-script` + `--exclude-libs,ALL`、Windows `.def`）。这些手段过滤的是**最终链接产物的动态符号表**，对我们的 `.o` 里是否出现 `rtc::` 符号不敏感。

**净评估：不成立。C API 从未提供过这层保护。**

> 一个**未能量化**的点：改用 C++ API 后二进制体积的变化。我没有实测（需要真正写出 C++ 版本再对比 `size`）。定性判断是变化很小 —— 我们不再链入 `capi.cpp`（约 1500 行、一整套 map 与包装函数，**这部分是净减少**），换成我们自己的句柄表（更小）；新增的是若干模板实例化。**可能反而略微变小。** 若这一项是决策关键，应做实测而非采信本文。

### 6.4 一项 ticket 未列出、但真实存在的新增风险：`std::function` 生命周期

C API 的回调是**函数指针**，永远有效。C++ API 的回调是 `std::function`，**捕获什么就得对什么负责**。若 lambda 捕获了裸指针/引用而对象先死，就是 use-after-free。规避方式明确：**只捕获整数句柄（按值）**，触发时查表验活（§5.3 第 3 点）。这与 #29 的「weak lookup table」决议天然吻合。

必须同时注意：`resetCallbacks()`（`include/rtc/channel.hpp:47`、`peerconnection.hpp:125`）在销毁前应显式调用，否则 `std::function` 里捕获的东西活到对象析构为止。C API 下这件事由 `eraseChannel` 顺带完成。

## 7. 前五张已关票的落地方案改动量（问题 7）

**总结论：五张票的决议一条都不用推翻。** 落地方案的改动量按票列出，从小到大。

### 7.1 #34（公开 C# 面）—— 零改动

#34 决议的全部内容位于 **`dcu_*` C ABI 之上**：`IceServer` / `PeerConnectionConfig` / `DataChannelInit` 的形状、`[Serializable]` 删除、`Reliable` 与 `Max*` 互斥的 C# 侧校验、命名表、`Action` 委托。C ABI 本身不变（`dcu_*` 仍是 `extern "C"`），C# 侧**看不见** dcu 内部用的是 C 还是 C++。

顺带一提：#34 决议 2「`Reliable` 与 `Max*` 互斥在 C# 侧校验」引用的上游佐证 `src/impl/datachannel.cpp:82-83` 的 `throw std::invalid_argument("Both maxPacketLifeTime and maxRetransmits are set")` **在 impl 层**，两条路径都会抛。C 路线下它被 `wrap` 转成 `RTC_ERR_INVALID`，C++ 路线下我们自己 catch —— 结果相同。

### 7.2 #29（所有权 / 句柄表）—— 落地方案**变简单**，是净收益

| #29 决议 | C 路线落地 | C++ 路线落地 |
|---|---|---|
| PC 拥有其 DataChannel，级联 dispose | 自建父子表 + 逐个 `rtcDeleteDataChannel`；**且解决不了 `capi.cpp` 自己 `dataChannelMap` 的泄漏**（§5.1） | 表中 `PcEntry::children`，级联即遍历擦除；**泄漏根除** |
| weak lookup table | 上游 `int lastId` 单调递增（有回绕 ABA 洞） | `{index, generation}`，ABA 不可能 |
| 主线程 only、finalizer 不做 P/Invoke | **不变** | **不变** |
| 不采用 `SafeHandle` | **不变** | **不变** |

#29 论据里那句「**HandleTable 不是『没见过更好的设计』，而是 GC 语言里唯一正确的设计**」在两条路线上都成立且不受影响。

### 7.3 #30（事件 ABI / 推拉混合）—— 落地方案几乎不变

- 「控制推、数据拉」**成立**（§2.3），机制在 impl 层共享。
- `dcu_dc_receive` 从 `rtcReceiveMessage` 换成 `Channel::receive()`，**少一次拷贝**（§3.1）。
- 背压（`RECV_QUEUE_LIMIT = 1024`、`Queue::push` 阻塞）**逐字保留**（§3.2）。
- #30 已写进 SPEC 的例外「**WebGL 上背压保证不成立**」—— 本票 §4.2 独立证实并**加强**了它：wasm 的 `rtc::Channel` 连 `receive()` / `availableAmount()` 都没有，facade 必须自建队列，且**背压在浏览器上原理性不可能**（`onmessage` 不可阻塞）。这条例外与 C/C++ 选择无关，**不因走 C++ 而消解**。
- #30 交给 #32 的那条「拉模式下文本/二进制区分拿得到」在 C++ 下**更干净**：`message_variant` 是 `std::variant<binary, string>`，直接 `std::visit`，不必靠 `*size` 的正负号编码。

### 7.4 #31（错误码）—— **唯一实质性退步，必须写进决策依据**

#31 决议：错误码独立于 `RTC_ERR_*`，显式枚举映射 + **`static_assert` 编译期门禁**，并交代给 #39「`static_assert` 会在升级上游时炸，应写进升级流程」。

C++ 路线下这道门禁**部分失效**：

- **状态码门禁保留** —— `rtcState` / `rtcGatheringState` 等仍在 `include/rtc/rtc.h`，且 C++ 枚举**显式以其为底值**（`peerconnection.hpp:45-52`：`New = RTC_NEW, Connecting = RTC_CONNECTING, ...`）。`static_assert(static_cast<int>(PeerConnection::State::Connected) == DCU_STATE_CONNECTED)` 照样能写。
- **错误码门禁消失** —— 我们不再消费 `RTC_ERR_*`，改为 catch 异常类型。上游实际抛出的分布是（`native/subprojects/libdatachannel` 全树统计）：

  ```text
  144  throw std::runtime_error
  102  throw std::invalid_argument
   49  throw std::logic_error
    5  throw std::out_of_range
    1  throw std::exception
  ```

  这套映射**无法用 `static_assert` 检查**。若上游某天把一处 `invalid_argument` 改成 `runtime_error`，我们的 `DCU_ERR_INVALID` 会静默变成 `DCU_ERR_FAILURE`，**编译期无感知，只能靠运行时测试发现**。

  （注意 `out_of_range` 派生自 `logic_error`，`catch` 子句顺序必须先具体后宽泛，否则前者被后者吃掉。）

**代偿方案**：把 #39 的门禁从「编译期 `static_assert`」改成「**运行时契约测试**」—— 针对每个应产出 `DCU_ERR_INVALID` 的已知误用各写一个用例（传空指针、传不存在的句柄、同时设 `maxRetransmits` 与 `maxPacketLifeTime`），升级上游时跑。这比 `static_assert` 弱（覆盖的是我们想到的用例，不是全集），**这是 C++ 路线真实付出的代价，不应粉饰。**

顺带一个 C++ 路线的小胜：#31 决议 5 要求「`INVALID` 与 `FAILURE` 保真、不压平」，理由是「`INVALID` 可自助修复，`FAILURE` 只能提 issue」。C API 的 `wrap` 把 `invalid_argument` 之外的一切都压成 `RTC_ERR_FAILURE`（`capi.cpp:215-226`）—— 我们自己 catch 可以多分出 `logic_error`（我们自己调用序列错了）与 `bad_alloc`，**保真度比 C 路线更高**。

### 7.5 #37（生命周期 / `dcu_shutdown`）—— 落地方案**改善**，解掉一个已知痛点

#37 决议 6/7 记录了两个真实问题：

> **6** — `dcu_shutdown()` 在进程将死时回收线程池毫无意义，却**可能阻塞 10 秒** —— iOS `applicationWillTerminate` 只给约 5 秒，Android 可能 ANR。
> **7** — `rtcCleanup()` 返回 `void` 且**自己 try/catch 吞掉**两条最有价值的诊断，全进 plog。

源头在这里：

```cpp
// src/capi.cpp:1754-1768
void rtcCleanup() {
    try {
        size_t count = eraseAll();
        if (count != 0) {
            PLOG_INFO << count << " objects were not properly destroyed before cleanup";
        }
        if (rtc::Cleanup().wait_for(10s) == std::future_status::timeout)
            throw std::runtime_error("Cleanup timeout (possible deadlock or undestructible object)");
    } catch (const std::exception &e) {
        PLOG_ERROR << e.what();
    }
}
```

那个 **10 秒是 `capi.cpp` 写死的**，不是 `rtc::Cleanup()` 的性质。C++ API 暴露的是：

```cpp
// include/rtc/global.hpp:56
RTC_CPP_EXPORT std::shared_future<void> Cleanup();
```

拿到 `shared_future` 后，**超时由我们定**（iOS 上可以给 2 秒），超时与否是我们自己的返回值而非被吞进 plog 的日志行。#37 决议「自维护计数」在 C++ 路线下也更直接：计数就是我们自己表的 `size()`，不必依赖 `eraseAll()` 的返回值（该值我们根本拿不到，`rtcCleanup` 返回 `void`）。

#37 的其余部分 —— pump 在 Edit Mode 常驻、五个编辑器/播放器场景、退出播放不触发域重载的实测、句柄单调递增使陈旧事件必然 miss —— **全部不受影响**。最后一条需注意：若按 §5.3 改用 `{index, generation}` 句柄，「陈旧事件必然 miss」这条性质**更强**（代际不匹配即 miss，不依赖 `lastId` 不回绕）。

### 7.6 一票之外：#33（凭证脱敏）

#33 是本票的触发者。它发现「凭证泄露的第一环是我们自己拼 URI」。C++ 路线**从源头消除这一环**：不再有承载 `user:pass` 的 URI 字符串存在于我们的进程内存里（`build_ice_uri` 整个删除），凭证只以 `IceServer::username` / `::password` 字段存在。

需要说清的是**这不等于凭证不再需要脱敏**：`rtc::IceServer` 的字段仍在内存中，libdatachannel 内部日志仍可能打印 TURN 服务器信息，#33 的 `RedactIceCredentials` 仍需保留。C++ 路线消除的是**我们自己制造的**那一份拼接串，不是全部暴露面。

---

## 8. 推荐与代价估算

### 8.1 推荐

**推荐迁移到 C++ API（`<rtc/rtc.hpp>`），但排在 #29 / #30 / #31 / #37 的落地之前做，不要之后。**

理由按权重排序：

1. **C API 在本项目里买不到它通常能买到的东西。** C API 的价值是「稳定 ABI + 异常隔离 + 隐藏 C++ 复杂度」。但我们**静态链接**、**自己就是 C++**、**要对外提供的稳定 ABI 是 `dcu_*` 而不是 `rtc*`**。三条价值全部落空，只剩下 `wrap()` 那 12 行异常隔离（§6.1）和一个我们本来就要重建的句柄表（§5.2）。
2. **C API 正在给我们制造两个具体问题**：`rtcDeletePeerConnection` 不级联清子 DC（§5.1，#29 已发现）、`rtcConfiguration.iceServers` 是 `const char**` 逼我们拼 URI（§4.1，#33 已发现）。两者都不是我们的 bug，也都不是我们能在 C 路线上修干净的。
3. **§4.1 的跨后端一致性是真收益且不可替代**：`rtc::IceServer` 的结构化构造在 libdatachannel 与 datachannel-wasm 上**逐字同形**，而自拼 URI 的方案在两侧行为不同（wasm 不解析 URL，凭证走不进浏览器的 `RTCIceServer.username`）—— 即当前方案在 WebGL 上很可能**根本不工作**。
4. **#30 的推拉混合模型完好无损**（§2.3 / §3.2），这是最大的落地风险，已排除。

**必须同时接受的代价**（不要在决策时忽略）：

- §7.4 的错误码映射失去编译期门禁，须以运行时契约测试代偿。**这是唯一实质性退步。**
- §5.3 的「回调验活守卫」是新增的、编译期不可检的 use-after-free 风险面。
- §4.2 证明「两个后端更一致」在**数据面上是假的** —— 若有人以「WebGL 会更好做」为主要理由支持迁移，该理由**不成立**，应以 §4.1 的配置面理由取代。

### 8.2 代价估算

| 项 | 估算 | 置信度 |
|---|---|---|
| `dcu_impl.cpp` 改写（相对 C 路线的**净增量**） | 约 **+70 / −63 行**，基本持平 | 中（静态阅读，未编译验证） |
| 一次性设计工作（句柄表代际、验活守卫、`wrap`） | **0.5–1 人日** | 中 |
| 机械改写（回调注册、配置映射、18 个函数体） | **0.5 人日** | 较高 |
| 测试（验活守卫的并发/退出压测 + §7.4 的错误码契约测试） | **1–1.5 人日** | 低 —— 这是最大的不确定性 |
| **合计** | **2–3 人日** | — |
| 二进制体积变化 | 定性判断**持平或略减**（不再链入 `capi.cpp` 约 1500 行，换成更小的自管表） | **低 —— 未实测**，若为决策关键须实测 |
| 符号可见性 / 静态链接 | **零额外成本**，`RTC_STATIC` 下 `RTC_CPP_EXPORT` 全平台展开为空（§6.2）；#18 的链接器白名单方案不受影响（§6.3） | 高 |

### 8.3 Do it now vs. do it later

**判断：do it now —— 且「now」有明确定义：在 #29 / #30 / #31 / #37 落地之前。**

- **现在做，成本是 2–3 人日**，因为 §1.0 已论证：这四张票要求的改动**本来就要重写 `dcu_impl.cpp` 的大部分**（事件 ABI 换成 `dcu_event_next`、建句柄表、错误码映射层、shutdown 计数）。C++ 迁移搭这趟车，增量接近于零。
- **落地之后再做，成本是重写第二遍**：句柄表要从「`capi.cpp` 影子表」重构成「唯一表」、错误码映射层要从 `RTC_ERR_*` 翻译改成异常翻译、`dcu_dc_receive` 要从两阶段 peek/receive 改成单次 receive。粗估 **5–8 人日**，且届时已有测试要跟着改。
- **拖到 WebGL 后端动工之后再做，是最坏的时点**：那时 §4.1 的 ICE 结构化构造已经在 native 侧用 URI、在 wasm 侧用结构化字段，两套代码已分叉，合并成本再翻一倍。

**唯一支持 later 的论据**：若项目当前的首要目标是尽快让某个端到端场景跑通、且 #29/#30/#31/#37 也打算暂缓，那么保持现状、只在 #33 的范围内做脱敏，是合理的止损。但这条只在「四张票也一起缓」时成立 —— **若那四张票要动，C++ 迁移就应当同批动。**

### 8.4 若决定迁移，建议的落地顺序

1. 先建句柄表（`{index, generation}` + PC→DC 从属 + 表锁 + 验活守卫），**用 C API 跑通并测试**。此步与 API 选择无关，是 #29 的落地。
2. 换 `wrap()`：写 12 行异常映射（`invalid_argument` / `out_of_range` / `logic_error` / `runtime_error` / `bad_alloc` / `...`），补 §7.4 的运行时契约测试。
3. 逐个函数把 `rtc*(int)` 换成 `obj->method()`，句柄表在此步从「影子表」变成「唯一表」。
4. 最后换 ICE 配置：删 `build_ice_uri` + `percent_encode_userinfo`，改用 `IceServer` 结构化构造 —— **这一步是 #33 的实际修复**。

这样每一步都可独立编译、独立测试，且第 1 步无论最终是否迁移都不浪费。
