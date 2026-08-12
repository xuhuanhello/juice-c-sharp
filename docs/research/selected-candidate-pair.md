# Research: 怎么知道一条连接走的是直连还是中继

**Ticket:** https://github.com/xuhuanhello/juice-c-sharp/issues/114
**支撑:** #118（定 C# 与 `dcu_*` 的 API 形状）
**Date:** 2026-08-12

**对照实现（按权重排序）:**

| 实现 | 位置 | 为什么看它 |
|---|---|---|
| **com.unity.webrtc 3.0.0** | `/Users/xsmxu/Library/Unity/cache/packages/packages.unity.com/com.unity.webrtc@3.0.0/` | 同一个引擎、同一门语言、同一类用户；且**做了候选对的 Editor 面板** |
| **libdatachannel v0.24.5** | `native/subprojects/libdatachannel`（vendored） | 我们的上游本体 |
| **libjuice 1.7.2** | `native/subprojects/libdatachannel/deps/libjuice`（`3c40a35`） | **真正决定行为的那一层** —— `native/CMakeLists.txt:196` 是 `set(USE_NICE OFF CACHE BOOL "" FORCE)`，libnice 分支我们编不进来 |
| `cephalofi/DataChannelDotnet` | GitHub（原 `ZetrocDev`，账号改名） | 同上游、同语言、同 P/Invoke 边界 |
| `Mimi8298/LibDataChannel.Net` | GitHub | 同上 |

---

## Verdict（结论速览）

| 问题 | 答案 |
|---|---|
| **业界判据是什么** | 读**候选的 `candidateType` 字符串**，看它是不是 `"relay"`。不是读 pair 上的某个布尔字段 —— 标准里没有这种字段（§1.1） |
| **从哪一端读** | com.unity.webrtc 的两个官方示例都只读**远端**（§1.1）。我们两端都能读，但**两端的可信度不对等**（§2.4） |
| **判据 `local.type()==Relayed \|\| remote.type()==Relayed` 对不对** | **对，结论可用**。但它成立的理由跟直觉不同，而且 `local.type()` 只在 `==Relayed` 这一个取值上可信（§2.4） |
| **`ConnectionState` 走到哪一档可读** | 我们的 `Connected`（含）之后**必然**可读。`Connecting` 期间**可能**可读、可能不可读，返回 `false` 是正常态，不是错误（§2.1） |
| **会不会重选** | **提名前会，提名后不会**。RFC 8445 8.1.1 禁止二次提名，libjuice 逐字实现（§2.2） |
| **有没有事件** | **没有**。上游没有 pair-changed 回调，com.unity.webrtc 和两份 .NET 绑定也都是 pull-only（§2.2、§3） |
| **要不要先 `resolve()`** | **不要**。`type()` 来自 `parse()`，与 resolve 正交；且两个后端返回前**已经**替我们 resolve 过了（§2.3） |
| **`Type::Unknown` 会出现吗** | 走 `getSelectedCandidatePair` 这条路**不会**，往返闭合（§2.3） |
| **我们缺的字段里有哪些真的需要** | **一个都不缺。** `state`/`nominated`/`selected` 对「直连还是中继」是锦上添花，两个官方示例一个都没读（§1.4） |
| **该做快照还是事件** | **同步快照**。事件在我们的架构里要新开一条轮询线程才能发出来 —— 为一个没人监听的变化付一条线程，不值（§4.1） |
| **该暴露完整 Candidate 还是只暴露类型** | **两者都要，但主角是一个 `enum`**：`Direct`/`Relayed` 的**判定结论**由 native 算，SDP 原文并排给出做诊断。不要暴露「本地候选类型」这个字段（§4.2） |
| **最大的坑** | **失败后返回陈旧的 pair** —— `agent->selected_pair` 从不被清空（§2.5）。C# 侧必须拿 `ConnectionState` 兜 |

---

## 1. 业界做法：com.unity.webrtc 3.0.0

下文路径都相对 `/Users/xsmxu/Library/Unity/cache/packages/packages.unity.com/com.unity.webrtc@3.0.0/`。

### 1.1 准确判据：读候选的 `candidateType` 字符串

**判据不在 pair 上，在候选上。** 完整取值链要走三跳：

```
RTCTransportStats.selectedCandidatePairId   ← 谁是「选中的」那一对
      ↓ 在 report 里查这个 id
RTCIceCandidatePairStats.remoteCandidateId  ← 这一对的远端候选是谁
      ↓ 在 report 里查这个 id
RTCIceCandidateStats.candidateType == "relay"   ← 判据本体
```

每一跳的出处：

| 跳 | 字段 | 出处 |
|---|---|---|
| 1 | `selectedCandidatePairId` | `Runtime/Scripts/RTCStats.cs:1600` |
| 2 | `localCandidateId` / `remoteCandidateId` | `Runtime/Scripts/RTCStats.cs:595` / `:600` |
| 3 | `candidateType` | `Runtime/Scripts/RTCStats.cs:756` |

第 3 跳是判据本体，它是**字符串**，不是枚举：

```csharp
// Runtime/Scripts/RTCStats.cs:753-756
/// <summary>
/// The type of the candidate (e.g., "host", "srflx", "prflx", "relay").
/// </summary>
public string candidateType { get { return GetString("candidateType"); } }
```

**两个官方示例把这条链子逐跳走完，且都只读远端**：`Samples~/PeerConnection/PeerConnectionSample.cs:144-170` 与 `Samples~/E2ELatency/E2ELatencySample.cs:222-248`，两处代码几乎逐字相同：

```csharp
// Samples~/PeerConnection/PeerConnectionSample.cs:144-150
foreach (var transportStatus in report.Stats.Values.OfType<RTCTransportStats>())
{
    if (report.Stats.TryGetValue(transportStatus.selectedCandidatePairId, out var tmp))
    {
        activeCandidatePairStats = tmp as RTCIceCandidatePairStats;
    }
}
```

接着拿 `activeCandidatePairStats.remoteCandidateId` 找候选，最后打印 `remoteCandidateStats.candidateType`（`:170`）。

**它们没读 `localCandidateId`。** 我 grep 了整包 `.cs`，`candidateType` 共 6 处：定义 1（`RTCStats.cs:756`）、Editor 面板 2（本地/远端各一，`Editor/PeerStatsView.cs:631`、`:675`）、测试 1（`Tests/Runtime/StatsReportTest.cs:86`）、示例 2（上面两个，**都是远端**）。

### 1.2 形态：异步报告，不是同步快照

`GetStats()` 返回可 `yield return` 的 async operation：

```csharp
// Runtime/Scripts/RTCPeerConnection.cs:1113-1117
public RTCStatsReportAsyncOperation GetStats()
{
    RTCStatsCollectorCallback callback = NativeMethods.PeerConnectionGetStats(GetSelfOrThrow());
    return GetStats(callback);
}
```

底下是原生回调投递（`Runtime/Scripts/RTCStatsCollectorCallback.cs:8,15-18`：`Action<RTCStatsReport> onStatsDelivered` + `Invoke`）。

**这个异步不是设计品味，是 libwebrtc 的 `GetStats` 本身就是回调式的**（跨线程收集整棵 stats 树）。我们上游的 `getSelectedCandidatePair` 是同步的，因为它只读 ICE agent 里一个已经算好的指针（§2.1）。**形态差异的根因在此，所以「该照哪个」不是风格选择题** —— 见 §4.1。

### 1.3 刷新频率与时机：1 秒轮询，只在 Play Mode

这条反过来回答了本 ticket 的原始问题（值会不会变、要不要轮询）——**业界答案是轮询，周期 1 秒**：

```csharp
// Editor/WebRTCStats.cs:27
private const int UpdateStatsInterval = 1;
```

```csharp
// Editor/WebRTCStats.cs:115-151（节选）
IEnumerator GetStatsPolling()
{
    while (true)
    {
        var peerList = WebRTC.PeerList;
        ...
                var op = peer.GetStats();
                yield return op;
                if (!op.IsError) { OnStats?.Invoke(peer, op.Value); ... }
        yield return new EditorWaitForSeconds(UpdateStatsInterval);
    }
}
```

生命周期挂在 Play Mode 上：`EnteredPlayMode` 起协程、`ExitingPlayMode` 停（`Editor/WebRTCStats.cs:85-97`），面板关闭也停（`:105-113`）。**没有任何 pair-changed 事件** —— 面板拿不到通知，只能自己数着秒问。

### 1.4 我们缺的字段：对「直连还是中继」一个都不缺

libdatachannel 只给两个 `Candidate`，标准 `RTCIceCandidatePairStats` 有 20+ 字段。逐条对一遍：

| 字段 | 我们有吗 | 判「直连/中继」需要吗 | 依据 |
|---|---|---|---|
| 候选的 `candidateType` | ✅ 等价物 `Candidate::type()` | **需要，且只需要它** | §1.1 三跳链的终点 |
| `selectedCandidatePairId` | ✅ 等价物：`getSelectedCandidatePair` 本身**就是**「选中的那一对」 | 需要，但不需要 id —— 上游直接把选中的对给我们，省掉第 1、2 跳 | §2.1 |
| `state`（`succeeded`/`failed`…） | ❌ | **不需要** | 两个官方示例都没读（§1.1）；我们有 `ConnectionState` 兜（§2.1） |
| `nominated` | ❌ | **不需要** | 同上。它影响「会不会再变」，不影响「现在是什么」（§2.2） |
| `selected` | ❌ | **不需要**。顺带纠一个前提：**com.unity.webrtc 也没有这个字段** | `Tests/Runtime/StatsReportTest.cs:34` 断言 `Dict.Count == 24`，`:41` 写着 `// Does not exist in the spec: Ignore.Pass(iceCandidatePairStats.writable);`；整个 `RTCIceCandidatePairStats`（`RTCStats.cs:585-700`）只有 `state` 与 `nominated` |
| `currentRoundTripTime` / `availableOutgoingBitrate` / 各类计数器 | ❌ | 不需要 —— 这些是 RTT/带宽面板的料，不是判据 | `Editor/CandidatePairGraphView.cs:7-22` 画的 16 条曲线全是这类 |

**结论：判据所需的信息一条不缺。** 缺的是 RTT/带宽面板（`docs/SPEC.md:698` 记的 `Stats v1 | BufferedAmount only; no selected-pair / RTT panel` 的后半句），那是另一件事。

### 1.5 顺带一个坑：com.unity.webrtc 自己有两套候选类型词表

同一个包里，候选类型有**两套不兼容的字符串**：

| 路径 | 词表 | 出处 |
|---|---|---|
| stats 路径 | `"host"` / `"srflx"` / `"prflx"` / `"relay"`（W3C 规范词表） | `Runtime/Scripts/RTCStats.cs:754` 注释 |
| `RTCIceCandidate` 对象路径 | `"local"` / `"stun"` / `"prflx"` / `"relay"`（libwebrtc 内部 cricket 词表） | `Runtime/Scripts/RTCIceCandidate.cs:113-128` |

```csharp
// Runtime/Scripts/RTCIceCandidate.cs:113-128
public static RTCIceCandidateType ParseRTCIceCandidateType(this string src)
{
    switch (src)
    {
        case "local":  return RTCIceCandidateType.Host;   // ← 不是 "host"
        case "stun":   return RTCIceCandidateType.Srflx;  // ← 不是 "srflx"
        case "prflx":  return RTCIceCandidateType.Prflx;
        case "relay":  return RTCIceCandidateType.Relay;
        default: throw new ArgumentException($"Invalid parameter: {src}");
    }
}
```

两个可借鉴之处：

- **stats 侧刻意不解析成枚举**，`candidateType` 留作 `string`（`RTCStats.cs:756`）。上面那个 `Parse` 遇到不认识的词**抛异常**，stats 侧不会 —— 上游多一个候选类型，字符串路径照常工作，枚举路径当场炸。
- 我们只有一套词表（RFC 8839 的 `host`/`srflx`/`prflx`/`relay`，见 §2.3），不会踩这个坑。但**「暴露成 enum 就得回答『不认识的值怎么办』」**对我们同样成立 —— §4.2 处理了。

---

## 2. 上游：libdatachannel v0.24.5 + libjuice 1.7.2

先立前提：**libnice 分支不用看。** `native/CMakeLists.txt:196` 是 `set(USE_NICE OFF CACHE BOOL "" FORCE)`，`FORCE` 意味着命令行也覆盖不了。`src/impl/icetransport.cpp` 里有两份 `getSelectedCandidatePair`（`:269` libjuice、`:954` libnice），**只有 `:269` 进我们的二进制**。下文凡说「后端」都指 libjuice。

调用链四层：

```
rtc::PeerConnection::getSelectedCandidatePair   include/rtc/peerconnection.hpp:97 / src/peerconnection.cpp:386-389
  → impl::IceTransport::getSelectedCandidatePair   src/impl/icetransport.cpp:269-284
    → juice_get_selected_candidates               deps/libjuice/src/juice.c:107-123
      → agent_get_selected_candidate_pair         deps/libjuice/src/agent.c:763-778
```

### 2.1 什么时候可读：`Connected` 之后必然可读，`Connecting` 期间 `false` 是正常态

最底层就是读一个指针，非空则拷贝：

```c
// deps/libjuice/src/agent.c:763-778
int agent_get_selected_candidate_pair(juice_agent_t *agent, ice_candidate_t *local,
                                      ice_candidate_t *remote) {
	conn_lock(agent);
	ice_candidate_pair_t *pair = agent->selected_pair;
	if (!pair) {
		conn_unlock(agent);
		return -1;              // ← 「还没有」，一路冒泡成 C++ 层的 false
	}

	if (local)
		*local = pair->local ? *pair->local : agent->local.candidates[0];   // ← §2.4 的全部争议在这一行
	if (remote)
		*remote = *pair->remote;

	conn_unlock(agent);
	return 0;
}
```

`-1` 经 `juice.c:114-115` 成 `JUICE_ERR_NOT_AVAIL`，经 `icetransport.cpp:272-283` 成 `false`。**所以「返回 false」只有一个含义：`agent->selected_pair` 还是空的。** 它不区分「ICE 还在打洞」与「出错了」。

`selected_pair` 何时非空？在 `agent_bookkeeping` 的选择循环里，**赋值发生在状态迁移之前**：

```c
// deps/libjuice/src/agent.c:1132-1158（节选，保留原注释）
	if (selected_pair) {
		// Change selected entry if this is a new selected pair
		if (agent->selected_pair != selected_pair) {
			JLOG_DEBUG(selected_pair->nominated ? "New selected and nominated pair"
			                                    : "New selected pair");
			agent->selected_pair = selected_pair;        // ← 唯一的赋值点
			...
		}

		if (nominated_pair) {
			// Completed
			// Do not allow direct transition from connecting to completed
			if (agent->state == JUICE_STATE_CONNECTING)
				agent_change_state(agent, JUICE_STATE_CONNECTED);
			agent_change_state(agent, JUICE_STATE_COMPLETED);
			...
		} else {
			// Connected
			agent_change_state(agent, JUICE_STATE_CONNECTED);
			...
		}
	}
```

**读到的事实：** 两处 `agent_change_state(..., JUICE_STATE_CONNECTED)` 都在 `if (selected_pair)` 块**内部**（`:1132`），赋值在块开头（`:1137`）。加上 `agent_change_state` 同步调回调（`agent.c:1252-1260`），得到一条硬不变式：

> **libjuice 状态 ≥ CONNECTED ⟹ `selected_pair` 非空。**

反向不成立：赋值先于状态迁移，所以存在「pair 已有、状态未迁」的瞬间。

再映射到我们的 `ConnectionState`（`Packages/datachannel-unity/Runtime/Enums.cs:10-19`）。libdatachannel 的 `State::Connected` **不是** ICE 连上就给：

| 触发 | 迁移 | 出处 |
|---|---|---|
| ICE → `Connecting` | `IceState::Checking` + **`State::Connecting`** | `src/impl/peerconnection.cpp:169-171` |
| ICE → `Connected` | `IceState::Connected` + `initDtlsTransport()`（**不动 `State`**） | `src/impl/peerconnection.cpp:172-175` |
| DTLS → `Connected` | 有 application m-line 则 `initSctpTransport()`，否则 **`State::Connected`** | `src/impl/peerconnection.cpp:249-253` |
| SCTP → `Connected` | **`State::Connected`** | `src/impl/peerconnection.cpp:334-335` |

我们必然有 DataChannel，走 SCTP 那条：`State::Connected` 要等 ICE **和** DTLS **和** SCTP 全部就位，而 ICE 严格早于 DTLS/SCTP。于是：

> **我们的 `ConnectionState.Connected`（含）之后，`getSelectedCandidatePair` 必然返回 `true`。**

**对 4 问之一的回答：** `Connecting` 期间调用**两种结果都可能** —— ICE 可能还在 Checking（`false`），也可能 ICE 已 Connected 而 DTLS/SCTP 未完（`true`）。**`Connecting` 期间的 `false` 是正常态，不是错误。** 这直接决定 C# 侧该用 try-get 而非抛异常（§4.2）。

### 2.2 会不会重选：提名前会，提名后不会

**会重选。** 证据是赋值点外面那层 `if`：

```c
// deps/libjuice/src/agent.c:1134-1137
		if (agent->selected_pair != selected_pair) {
			JLOG_DEBUG(selected_pair->nominated ? "New selected and nominated pair"
			                                    : "New selected pair");
			agent->selected_pair = selected_pair;
```

若不会变，这个比较和 `"New selected pair"` 这句 log 就没有存在理由。

选择规则在同一函数的排序循环里（`ordered_pairs` 按 pair 优先级降序）：

```c
// deps/libjuice/src/agent.c:1071-1086（节选，保留原注释）
	for (int i = 0; i < agent->candidate_pairs_count; ++i) {
		ice_candidate_pair_t *pair = agent->ordered_pairs[i];
		if (pair->nominated) {
			// RFC 8445 8.1.1. Nominating Pairs:
			// If more than one candidate pair is nominated by the controlling agent, and if the
			// controlled agent accepts multiple nominations requests, the agents MUST produce the
			// selected pairs and use the pairs with the highest priority.
			if (!nominated_pair) {
				nominated_pair = pair;
				selected_pair = pair;
			}
		} else if (pair->state == ICE_CANDIDATE_PAIR_STATE_SUCCEEDED) {
			if (!selected_pair)
				selected_pair = pair;
		} else if (...
```

即**已提名的对优先；否则取优先级最高的 SUCCEEDED 对**。提名前，随着更多对陆续 SUCCEEDED，`selected_pair` 会向更高优先级迁移 —— 这正是 issue 设想的「relay 兜底后又打通直连」。

**但提名之后不会再变。** RFC 8445 8.1.1 的规定被逐字抄进注释，紧跟着就是实现：

```c
// deps/libjuice/src/agent.c:1098-1111（节选，保留原注释）
	if (agent->mode == AGENT_MODE_CONTROLLING && nominated_pair) {
		// RFC 8445 8.1.1. Nominating Pairs:
		// Once the controlling agent has successfully nominated a candidate pair, the agent MUST
		// NOT nominate another pair for same component of the data stream within the ICE session.
		for (int i = 0; i < agent->candidate_pairs_count; ++i) {
			ice_candidate_pair_t *pair = agent->ordered_pairs[i];
			if (pair != nominated_pair && pair->state == ICE_CANDIDATE_PAIR_STATE_PENDING) {
				// Entries will be synchronized after the current loop.
				JLOG_VERBOSE("Cancelling check for non-nominated pair");
				pair->state = ICE_CANDIDATE_PAIR_STATE_FROZEN;
			}
		}
		pending_count = 0;
	}
```

一旦有 `nominated_pair`，其余候选对全被打成 `FROZEN`，对应 STUN entry 也被取消（`agent.c:1113-1123`）。此后每轮 bookkeeping 走到 `:1073` 的 `if (pair->nominated)` 都选中同一个对，`:1134` 的比较恒为假。

**提名对失效不会导致重选，会导致连接失败：**

```c
// deps/libjuice/src/agent.c:1125-1130
	if (nominated_pair && nominated_pair->state == ICE_CANDIDATE_PAIR_STATE_FAILED) {
		JLOG_WARN("Lost connectivity");
		agent_change_state(agent, JUICE_STATE_FAILED);
		atomic_store(&agent->selected_entry, NULL); // disallow sending
		return 0;
	}
```

**对 4 问之二的回答：** 会重选，窗口只在**提名完成之前**，即我们的 `ConnectionState` 到 `Connected` 之前。**提名后（`Connected` 之后）`selected_pair` 在整个 ICE session 内恒定。**

**没有事件。** libjuice 的 `juice_config_t` 回调只有 `cb_state_changed` / `cb_candidate` / `cb_gathering_done` / `cb_recv`，无 pair-changed；libdatachannel 侧 `include/rtc/peerconnection.hpp` 的 `on*` 只有 `onDataChannel` / `onTrack` / `onLocalDescription` / `onLocalCandidate` / `onStateChange` / `onIceStateChange` / `onGatheringStateChange` / `onSignalingStateChange`。**要事件只能自己轮询造** —— §4.1 论证为何不该造。

### 2.3 `resolve()` 与 `Type::Unknown`：都不用管

**`type()` 与 resolve 正交。** `mType` 在 `parse()` 里定，来源是 SDP 的 `typ` token：

```cpp
// src/candidate.cpp:76-79
	static const TypeMap_t TypeMap = {{"host", Type::Host},
	                                  {"srflx", Type::ServerReflexive},
	                                  {"prflx", Type::PeerReflexive},
	                                  {"relay", Type::Relayed}};
```

```cpp
// src/candidate.cpp:103-106
	if (auto it = TypeMap.find(mTypeString); it != TypeMap.end())
		mType = it->second;
	else
		mType = Type::Unknown;
```

`resolve()` 只写 `mFamily` / `mAddress` / `mPort`（`src/candidate.cpp:181-186`），**碰不到 `mType`**；`Family::Unresolved` 与 `Type::Unknown` 是两个互不相干的轴（`include/rtc/candidate.hpp:20-21` 分属两个 enum）。`type()` 就是 `return mType;`（`src/candidate.cpp:199`）。

**而且后端已经替我们 resolve 过了**，返回前就调：

```cpp
// src/impl/icetransport.cpp:269-284（libjuice 后端）
bool IceTransport::getSelectedCandidatePair(Candidate *local, Candidate *remote) {
	char sdpLocal[JUICE_MAX_CANDIDATE_SDP_STRING_LEN];
	char sdpRemote[JUICE_MAX_CANDIDATE_SDP_STRING_LEN];
	if (juice_get_selected_candidates(mAgent.get(), sdpLocal, JUICE_MAX_CANDIDATE_SDP_STRING_LEN,
	                                  sdpRemote, JUICE_MAX_CANDIDATE_SDP_STRING_LEN) == 0) {
		if (local) {
			*local = Candidate(sdpLocal, mMid);
			local->resolve(Candidate::ResolveMode::Simple);
		}
		...
```

（libnice 分支同样在返回前 resolve，`src/impl/icetransport.cpp:967-970` —— 换后端也不变。）

`ResolveMode::Simple` 带 `AI_NUMERICHOST`（`src/candidate.cpp:168-169`），只接受数值地址、不做 DNS。ICE agent 给出的地址本就是数值的，所以这次 resolve 是必成的本地解析，**不含网络往返**。

**对 4 问之三的回答：** `type()` 在 `Unresolved` 家族下照样返回正确类型，**不需要先 `resolve()`**；何况上游已调过，我们再调是无谓重复。

**`Type::Unknown` 走这条路不可达**，往返闭合：

| 环节 | 行为 | 出处 |
|---|---|---|
| libjuice 收远端 SDP | 只认 `host` / `srflx` / `relay`，其余 `ICE_PARSE_IGNORED` 丢弃 | `deps/libjuice/src/ice.c:86-95` |
| libjuice 入库 | `type == ICE_CANDIDATE_TYPE_UNKNOWN` 直接 `return -1` | `deps/libjuice/src/ice.c:238-239` |
| libjuice 生成 SDP | 只 emit `host` / `prflx` / `srflx` / `relay`，`default` 分支报错 `return -1` | `deps/libjuice/src/ice.c:340-358` |
| libdatachannel 再解析 | 上面四个词全在 `TypeMap` 里 | `src/candidate.cpp:76-79` |

所以 agent 里的候选类型必定已知，回吐的 SDP 必定是四词之一，libdatachannel 必定映射成具体 enum。（`prflx` 只能内部发现、不能从对端 SDP 进来 —— 对比 `ice.c:86-95` 无 `prflx` 分支与 `ice.c:344-345` 有 `prflx` 输出。）

**推断（未实测）：** `Type::Unknown` 只在两种与本路径无关的情形出现 —— 默认构造的 `Candidate`（`src/candidate.cpp:56-58` 把 `mType` 初始化成 `Type::Unknown`），以及我们自己拿任意字符串构造 `Candidate`。**判据实现不需要为 `Unknown` 设分支**，但若 §4.2 的 enum 暴露给 C#，仍应留 `Unknown = -1`（与 `ConnectionState.Unknown` 同惯例，`Packages/datachannel-unity/Runtime/Enums.cs:12`）作 ABI 余量。

### 2.4 两端可信度不对等：`local.type()` 只有 `==Relayed` 这一个取值可信

**这是本文最反直觉的一条，也是判据能否写对的关键。**

回到 §2.1 引的那行：

```c
// deps/libjuice/src/agent.c:772
	if (local)
		*local = pair->local ? *pair->local : agent->local.candidates[0];
```

`pair->local` **可以是空的**。哪些情况空？libjuice 只给**本地 relayed 候选**建带 `local` 的对：

```c
// deps/libjuice/src/agent.c:2492-2508（全文，注释为上游原文）
int agent_add_candidate_pairs_for_remote(juice_agent_t *agent, ice_candidate_t *remote) {
	// Here is the trick: local non-relayed candidates are undifferentiated for sending.
	// Therefore, we don't need to match remote candidates with local ones.
	if (agent_add_candidate_pair(agent, NULL, remote))
		return -1;

	// However, we need still to differenciate local relayed candidates
	for (int i = 0; i < agent->local.candidates_count; ++i) {
		ice_candidate_t *local = agent->local.candidates + i;
		if (local->type == ICE_CANDIDATE_TYPE_RELAYED &&
		    local->resolved.addr.ss_family == remote->resolved.addr.ss_family)
			if (agent_add_candidate_pair(agent, local, remote))
				return -1;
	}

	return 0;
}
```

我核了 `agent_add_candidate_pair` 的**全部三个**调用点（`grep -n "agent_add_candidate_pair(" src/agent.c`）：

| 调用点 | 传的 `local` | 上下文 |
|---|---|---|
| `agent.c:2495` | **`NULL`** | 非 relayed 路径（上面那段） |
| `agent.c:2503` | 非空 | `local->type == ICE_CANDIDATE_TYPE_RELAYED` 守卫内 |
| `agent.c:2297` | 非空 | 紧跟 `JLOG_DEBUG("Gathered relayed candidate: %s", buffer)`（`agent.c:2290`），注释为 `// Relayed candidates must be differenciated, so match them with already known remote candidates`（`agent.c:2292`） |

于是一条双向等价：

> **`pair->local != NULL` ⟺ 本地候选是 relayed。**

**上游为何这么设计（我推断，但有旁证）：** 非 relayed 的本地候选发包时是同一个 socket —— `host` 与 `srflx` 本就是同一个本地 socket 的两种视角（`srflx` 是它经 NAT 映射后的样子），所以「用哪个本地候选发」对非 relayed 候选没有区分度，正如注释所言 `undifferentiated for sending`。只有 relayed 需要区分，因为要走另一条 TURN 通道（对比 `agent.c:678-687`：`selected_entry->relay_entry` 非空走 `agent_channel_send`，否则 `agent_direct_send`）。旁证：`ice_update_candidate_pair` 在 `pair->local` 为空时**按 HOST 算本地优先级**（`deps/libjuice/src/ice.c:398-402`），说明上游自己就把「空 local」当 host 语义。

**那 `candidates[0]` 是什么？** `agent->local` 在收集后按优先级降序排过：

- `ice_sort_candidates` 是降序插入排序（`deps/libjuice/src/ice.c:256-267`，`while (--prev >= begin && prev->priority < priority)`）
- 调用点 `deps/libjuice/src/agent.c:300`，在 host / TCP 候选收集之后
- 类型偏好：`ICE_CANDIDATE_PREF_HOST 126` > `PEER_REFLEXIVE 110` > `SERVER_REFLEXIVE 100` > `RELAYED 0`（`deps/libjuice/src/ice.h:43-46`），按 RFC 8445 5.1.2.1 参与优先级计算（`deps/libjuice/src/ice.c:426-447`）

所以 `candidates[0]` 是**优先级最高的本地候选**；只要收集到过 host 候选（正常情形必然，`agent.c:259` 逐网卡建 host 候选），它就是个 **host 候选**。

**合起来是这张表：**

| 实际路径 | `pair->local` | `local.type()` 返回 | 是真的吗 |
|---|---|---|---|
| 本地 relayed（经自己的 TURN 发） | 非空，指向那个 relayed 候选 | `Relayed` | ✅ **真** |
| 本地 host（直连） | `NULL` | `Host`（来自 `candidates[0]`） | ⚠️ 巧合地对 |
| 本地 srflx（打洞成功） | `NULL` | **`Host`** —— 不是 `ServerReflexive` | ❌ **假**，是占位符 |

**这就是不对等的由来：`local.type()` 在 `== Relayed` 时可信（`pair->local` 非空，是真候选），在其他取值上不可信（是 `candidates[0]` 占位符，把 srflx 路径报成 host）。**

远端没有这个问题：`*remote = *pair->remote`（`agent.c:775`）无条件取真候选，`pair->remote` 在 `ice_create_candidate_pair` 里也不允许为空参与（`deps/libjuice/src/ice.c:387-388` 直接赋值，`:396-397` 仅在两者皆空时早退）。**`remote.type()` 全部取值可信。**

**对 4 问之四的回答，以及判据的准确形式：**

```cpp
// 正确
bool isRelayed = (local.type()  == rtc::Candidate::Type::Relayed) ||
                 (remote.type() == rtc::Candidate::Type::Relayed);
```

issue 里设想的形式**是对的，结论可用** —— 两端都要看，因为两边各自可能经 TURN：`local == Relayed` 表示**我们**经自己的 TURN 发出，`remote == Relayed` 表示我们把包**发往对端的 TURN 分配地址**。任一成立，这条路径就不是端到端直连。而它成立所依赖的恰好是 `local.type()` 唯一可信的那个取值 —— **判据没问题，但不能顺手把 `local.type()` 当「本地候选类型」暴露出去**（§4.2）。

**一个我没实测的边角（推断）：** 若一次收集里**零个** host 候选（例如 `transport_policy = RelayOnly`，`native/dcu/include/dcu.h:110` 的 `transport_policy /* 0 All, 1 RelayOnly */`），`candidates[0]` 可能是 relayed 候选，于是非 relayed 对也会被报成 `Relayed`。**这是假阳性，但无害** —— RelayOnly 下答案本来就是 relay。记在这里是因为它是「`candidates[0]` 是 host」这个推断的唯一反例；判据的正确性不依赖它。

### 2.5 最大的坑：失败后返回陈旧的 pair

**`agent->selected_pair` 一旦被赋值，就再也不会被清空。**

我把对这个字段的**全部**读写列了出来（`grep -n "agent->selected_pair =\|selected_pair = NULL\|->selected_pair" src/agent.c`）：

| 行 | 操作 | 上下文 |
|---|---|---|
| `agent.c:766` | **读** | `agent_get_selected_candidate_pair`，即 §2.1 那个 getter |
| `agent.c:1071` | 局部变量声明 | `ice_candidate_pair_t *selected_pair = NULL;` —— **这是 `agent_bookkeeping` 的局部变量，不是那个字段** |
| `agent.c:1134` | **读**（比较） | `if (agent->selected_pair != selected_pair)` |
| `agent.c:1137` | **写** | `agent->selected_pair = selected_pair;` —— **唯一的写入点** |
| `agent.c:1537` | 读 | `if (!agent->selected_pair \|\| !agent->selected_pair->nominated)` |
| `agent.c:1834` | 读 | 同上形状 |
| `agent.c:2484` | 读 | 同上形状 |

**一个写入点，赋的值恒非空**（`:1137` 在 `if (selected_pair)` 块内，`:1132`）。**没有任何一行把它写回 `NULL`。**

失败路径上清的是**另一个**字段 `selected_entry`：

```c
// deps/libjuice/src/agent.c:1125-1130（提名对失去连通性）
	if (nominated_pair && nominated_pair->state == ICE_CANDIDATE_PAIR_STATE_FAILED) {
		JLOG_WARN("Lost connectivity");
		agent_change_state(agent, JUICE_STATE_FAILED);
		atomic_store(&agent->selected_entry, NULL); // disallow sending
		return 0;
	}
```

三条失败/断开路径清的都是它，一条都没碰 `selected_pair`：

| 路径 | 行 | 清的字段 |
|---|---|---|
| 提名对 FAILED（"Lost connectivity"） | `agent.c:1128` | `selected_entry` |
| 连通性计时器超时 | `agent.c:1227` | `selected_entry` |
| `agent_conn_fail` | `agent.c:792` | `selected_entry` |

注释 `// disallow sending` 说明了意图：**清 `selected_entry` 是为了让发送路径失效**（`agent.c:672-673`：`selected_entry` 为空则 `agent_send` 直接失败），不是为了让「选中的对」这个**查询**失效。两个字段服务两个目的，上游只清了发送那个。

**`agent_create` 里那行算不算清空？** 不算：

```c
// deps/libjuice/src/agent.c:134-136
	agent->state = JUICE_STATE_DISCONNECTED;
	agent->mode = AGENT_MODE_UNKNOWN;
	agent->selected_entry = NULL;
```

我核了它的位置：在 `agent_create` 内部，紧跟 TURN server 配置拷贝之后（`agent.c:116-132`），是**新建 agent 的字段初始化**，不在任何状态迁移路径上。而且它清的仍是 `selected_entry`；`selected_pair` 靠 agent 结构体的 `calloc` 归零。**一个只在对象诞生时跑一次的初始化，构不成运行期的清空。**

**后果（读到的事实推出的必然结论）：**

> 连接失败或断开后，`getSelectedCandidatePair` **仍返回 `true`**，交出失败前最后一次选中的那个对。

libdatachannel 侧不会兜 —— `PeerConnection::getSelectedCandidatePair` 只转发，不查状态：

```cpp
// src/peerconnection.cpp:386-389
bool PeerConnection::getSelectedCandidatePair(Candidate *local, Candidate *remote) {
	auto iceTransport = impl()->getIceTransport();
	return iceTransport ? iceTransport->getSelectedCandidatePair(local, remote) : false;
}
```

`getIceTransport()` 是 `std::atomic_load(&mIceTransport)`（`src/impl/peerconnection.cpp:364-366`）—— ICE transport 在 `State::Failed` 之后不会自动置空，所以这层的 `false` 只覆盖「还没建 ICE transport」（`setLocalDescription` 之前），**不覆盖「连接已失败」**。

**这条决定了 C# 侧的形状：读取必须由 `ConnectionState` 把门，不能只信 native 的返回值。** 见 §4.2 决定 (4)。

**推断（未实测，但风险为零）：** 指针本身不会悬垂 —— `selected_pair` 指向 `agent->candidate_pairs` 数组内的元素，该数组是 agent 内的定长数组（`MAX_CANDIDATE_PAIRS_COUNT` 上限检查在 `agent.c:2408-2411`），只追加不搬移，生命周期同 agent。**所以这是「读到旧值」，不是 use-after-free。**

---

## 3. 两份 .NET 绑定：都绑了，都是 pull-only，都不解析类型

> ⚠️ 这两个仓库是第三方代码，仅作事实记录。

**`ZetrocDev/DataChannelDotnet` 现已 404**，账号改名，仓库现址 **`cephalofi/DataChannelDotnet`**（经 `gh search repos` 确认：同 description、非 fork、`pushed_at: 2025-09-05`）。

| 维度 | cephalofi/DataChannelDotnet | Mimi8298/LibDataChannel.Net |
|---|---|---|
| P/Invoke | ✅ `src/DataChannelDotnet.Bindings/Rtc.cs:400-401`，ClangSharp 机器生成（有 `[NativeTypeName]`、`.github/workflows/Update-Bindings.yml`），`sbyte*` | ✅ `LibDataChannel.Native/NativeRtc.cs:82-83`，手写，`EntryPoint` 改名 + `IntPtr` |
| 托管 API | `bool TryGetSelectedCandidatePair(out RtcCandidatePair? pair)`（`src/DataChannelDotnet/IRtcPeerConnection.cs:44`，实现 `Impl/RtcPeerConnection.cs:239-240` → `Internal/RtcHelpers.cs:82`） | `void GetSelectedCandidatePair(out string local, out string remote)`（`LibDataChannel/Connections/Rtc/RtcPeerConnection.cs:141-144` → `LibDataChannel.Native/.../NativeRtcPeerConnection.cs:240-251`） |
| 返回形状 | 只含两个字符串的类：`RtcCandidatePair { string? LocalCandidate; string? RemoteCandidate; }`（`src/DataChannelDotnet/Data/RtcCandidatePair.cs:3-7`） | 两个裸 `out string` |
| 「还没选中」怎么处理 | `RTC_ERR_NOT_AVAIL` → `return false` | **抛异常**（`NativeRtc.ThrowException`） |
| 解析候选类型成 enum | ❌ 无，SDP 原文直传 | ❌ 无 |
| pair-changed 事件 | ❌ 无 | ❌ 无 |
| buffer 约定 | 先探测：`func(id, null, 0)` 拿长度，≤4096 走 `stackalloc`，否则从 `MemoryPool` 租 256 KB（`Internal/RtcHelpers.cs:30-64`）。但 pair 那条**跳过探测**，直接租两块 128 KB（`RtcHelpers.cs:89-104`） | 硬编码 `const int StringBufferSize = 65535`（`NativeRtcPeerConnection.cs:16`），全类共用 |

**两点值得记：**

**其一，两份实现共同划出了设计空间的两端** —— `bool TryGet…(out Pair)` 把「还没选中」当正常态，`void Get…` 把它当异常。**结合 §2.1（`Connecting` 期间 `false` 是正常态），try-get 明显更合身**；抛异常的形状会让「连接建立中查一次」变成需要 try/catch 的操作。

**其二，「没人解析候选类型」不是疏忽，是 C API 的能力边界。** C API 只给 SDP 字符串：

```cpp
// src/capi.cpp:681-700（节选）
int rtcGetSelectedCandidatePair(int pc, char *local, int localSize, char *remote, int remoteSize) {
	...
		int localRet = copyAndReturn(string(localCand), local, localSize);
		...
		return std::max(localRet, remoteRet);
```

`string(localCand)` 走 `Candidate::operator string()`（`src/candidate.cpp:225-229`），吐出 `a=candidate:...` 整行。**C API 没有任何候选类型访问器**（`include/rtc/rtc.h:232` 只有这一个 pair 相关函数），绑定要暴露类型就得自己解析 SDP —— 两家都没做。

**而我们不受这个限制。** `docs/SPEC.md:74-80` 记着 dcu 层直接吃 C++ API（#41/#42），所以 `rtc::Candidate::type()` 对我们**直接可用**（`include/rtc/candidate.hpp:36`）。`docs/research/dcu-c-vs-cpp-api.md:332` 早已记下 `getSelectedCandidatePair()` 在 C++ API 有（`:93-97`）、C API 无 —— **这条能力差正好落在这个 ticket 上：两份绑定不能做的事，我们能做。**

---

## 4. 对 #118 的建议

### 4.1 做同步快照，不做事件

**建议：同步快照。** 理由按承重排序：

**其一，上游没有事件源，造一个要付一条线程。** §2.2 已证上游无 pair-changed 回调。我们的事件都是 native 侧回调入队、C# 侧 `Pump()` 出队（`native/dcu/include/dcu.h:82-95` 的 `dcu_event_type` 九个成员 + `dcu_event_next`；C# 侧 `Packages/datachannel-unity/Runtime/PeerConnection.cs:48-52` 五个 `public event`）。要发 `SelectedCandidatePairChanged`，就得有人**周期性调用** `getSelectedCandidatePair` 并比较 —— 那是一条新的轮询线程或一个新的定时器。

**其二，这跟包里已经写死的一条原则冲突。** `Packages/datachannel-unity/Runtime/DataChannelRuntime.cs:20`：

> `private const double PumpStaleSeconds = 5.0;   // 秒级；只在应用调 API 时查，绝不后台轮询`

**「绝不后台轮询」是这个包的既有立场。** 为一个变化窗口只存在于连接建立期间（§2.2）、且大多数应用只在连上后看一眼的值新增后台轮询，与之直接违背。

**其三，业界也没做事件。** com.unity.webrtc 有完整 stats 基础设施和专门的 Editor 面板，仍然只是 1 秒轮询（§1.3）；两份 .NET 绑定也都 pull-only（§3）。**四份独立实现，零个事件。**

**其四，代价确实很低。** `agent_get_selected_candidate_pair` 只是取锁读指针 + 两次结构体拷贝（§2.1）。锁是 `conn_lock`（`deps/libjuice/src/conn.c:227-232`，转发到 conn 模式的 `lock_func`），与 ICE 线程共用。

**代价，说清楚：** 这把锁与 ICE 处理线程共享，所以**主线程调用理论上可能被 ICE 线程短暂持锁挡住**。临界区里只有指针判空和两次 `ice_candidate_t` 拷贝，量级是纳秒到微秒。**我没有实测这个阻塞时长**（要在真实 ICE 活动下压测才算数）。据此的实践结论：**不要每帧调。** 放在 `ConnectionStateChanged` 到 `Connected` 时读一次，或诊断面板按需读，都是安全的。

**不做事件的代价：** 应用无法察觉「提名前 relay→直连」的中途切换。**我认为可接受** —— 那个窗口在 `Connected` 之前（§2.2），窗口内的中间值对应用没有决策价值：应用真正要知道的是「这条连上的路是不是走中继」，而那个值在 `Connected` 之后恒定。

### 4.2 暴露判定结论 enum + SDP 原文，不暴露「本地候选类型」

**建议的形状（按承重排序，不是最终签名）：**

**native 侧** —— 一次调用取回三样：判定结论、两端候选类型、两端 SDP 原文。

- 判定结论：`Direct` / `Relayed`，由 native 按 §2.4 的判据算好。
- 两端候选类型：**远端给完整枚举**（可信）；**本地只给「是否 relayed」这一位**，不给类型枚举。
- SDP 原文：两条字符串，诊断用。

**C# 侧** —— try-get 形状，且**必须由 `ConnectionState` 把门**：

```csharp
// 形状示意，非最终签名
public bool TryGetSelectedCandidatePair(out SelectedCandidatePair pair)
```

`Connecting` 期间返回 `false` 是正常态（§2.1），不该抛。同步 getter 包里已有先例：`DataChannel.BufferedAmount`（`Packages/datachannel-unity/Runtime/DataChannel.cs:107-118`）的 `MainThread.Assert` + `ThrowIfDisposed` + `RequireOk` 三段式可以照抄。

**四条设计决定，各自的理由和代价：**

**(1) 主角是判定结论 enum，不是让应用自己比字符串。**

理由：验收线要的就是这一位信息（`docs/SPEC.md:1442` 把 `Selected-candidate-pair API` 列在 "Optional later"，本 ticket 来兑现它）。判据本身有 §2.4 那层微妙之处 —— **让每个应用自己写 `local.type()==Relayed || remote.type()==Relayed` 就是让每个应用重新踩一遍那个坑**。算一次，算在能写下注释的地方。

代价：多一个 enum 要维护。**推断：** 建议留 `Unknown = -1` 作 ABI 余量，与 `ConnectionState.Unknown`（`Packages/datachannel-unity/Runtime/Enums.cs:12`）同惯例。

**(2) 不暴露「本地候选类型」字段 —— 这是 §2.4 的直接后果。**

理由：`local.type()` 在非 relayed 情形下是 `candidates[0]` 占位符，**会把 srflx 路径报成 host**（§2.4 那张表）。暴露它等于发布一个在常见情形下说谎的字段。远端类型可信，给完整枚举；本地只给「是否 relayed」这一位 —— **恰好是 `local.type()` 唯一可信的取值**。

代价：API 在两端上不对称，需在文档里解释。**我认为值得** —— 不对称的真话胜过对称的假话。若日后要补本地完整类型，得先在上游解决 `pair->local` 为空的问题（那是上游设计，不是 bug，见 §2.4 旁证）。

**(3) SDP 原文并排给出。**

理由：三处旁证。com.unity.webrtc 的 stats 侧刻意保留字符串而非枚举（§1.5）；两份 .NET 绑定都只给字符串（§3）；`Editor/PeerStatsView.cs:622-640` 的候选面板逐字段打印原始值。原文让诊断面板和高级用户看到我们没建模的东西（`foundation`、`priority`、`raddr`、`tcptype`），也让我们在判据出错时能被证伪。

代价：两条字符串的分配。**注意 buffer 约定：** 若日后走 C API 形状，`capi.cpp:681-700` 的返回值是**两个长度的 `std::max`**，且两个 buffer **各自独立 NUL 结尾**（`copyAndReturn`，`src/capi.cpp:183-193`）—— `Mimi8298` 在这里错了（`NativeRtcPeerConnection.cs:249-250` 用 `PtrToStringAnsi(ptr, maxSize - 1)` 显式传长度，不在 NUL 处停，较短的那条会带上 `\0` 和未初始化的栈字节）。**我们走 C++ API 不经过这个约定，但 dcu 的 C 门面若沿用「buffer + len」形状，得把「各自独立 NUL 结尾」写进 `dcu.h` 注释。**

**(4) 读取必须由 `ConnectionState` 把门 —— 这是 §2.5 的直接后果，本节最硬的一条。**

理由：native 侧的 `true` 不等于「这条路现在通着」，失败后它交出的是陈旧的 pair（§2.5）。**只信返回值会在连接失败后报告一个早已不存在的路径。** 门的位置：C# 侧在 `ConnectionState` 不是 `Connected` 时直接返回 `false`，不下探 native；或 dcu 侧查 `rtc::PeerConnection::state()` 再决定。

代价：C# 与 native 各持一份状态判断，有漂移风险。**推断（未实测）：** 我倾向把门放在 **dcu 侧**（查 `PeerConnection::state()`），因为那里离 `getSelectedCandidatePair` 最近、两个值取自同一个对象，比 C# 侧靠事件同步过来的状态副本更难漂移。**这条我没有验证 dcu 侧读 `state()` 的锁开销**，#118 落地时要量一下。

### 4.3 顺带：一条可执行的契约

com.unity.webrtc 把契约写成了断言字段数量的测试（`Tests/Runtime/StatsReportTest.cs:34` 的 `Assert.AreEqual(24, iceCandidatePairStats.Dict.Count)`）—— 上游加字段就红，逼人来看一眼。

我们可以更针对性，**§2.1 与 §2.5 两条不变式都可测**：

- **§2.1：** 建立一条本地环回连接，等 `ConnectionState.Connected`，断言 `TryGetSelectedCandidatePair` 返回 `true`。这钉住「`Connected` ⟹ 可读」。
- **§2.5：** 连上后关闭连接，断言**不再**返回 `true`。这钉住的是我们的门（决定 (4)）**没有**退化成裸转发 —— 上游那层此时仍会返回陈旧的 pair，所以这条测试专门守住我们加的那道门。

第二条尤其值得写：它测的不是上游行为，而是**我们对上游一个已知缺陷的补偿**，而这类补偿最容易在后续重构里被当成冗余代码删掉。
