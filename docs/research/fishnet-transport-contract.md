# FishNet `Transport` 的行为契约，以及两套 pump 怎么对齐

> 调研产物，服务 [#115](https://github.com/xuhuanhello/juice-c-sharp/issues/115)，为 [#119](https://github.com/xuhuanhello/juice-c-sharp/issues/119)（channel 映射与出站背压）、[#120](https://github.com/xuhuanhello/juice-c-sharp/issues/120)（host 拓扑与 connectionId）交代前置事实。
>
> **本文只读代码，不改任何东西，也没有落任何实现。**

## 读法

版本：FishNet **4.7.2**，本机已解析在 `Library/PackageCache/com.firstgeargames.fishnet@de19b5d664/`。

引用为省版式用两个前缀，其余路径原样：

| 前缀 | 展开 |
|------|------|
| `FN:` | `Library/PackageCache/com.firstgeargames.fishnet@de19b5d664/Runtime/` |
| `DCU:` | `Packages/datachannel-unity/` |

每条事实标出处。**我读到的**直接给 `文件:行`；**我推断的**一律以「推断：」起头。凡是标了「未实测」的，都还没上过 Unity。

`FN:Transporting/Transport.cs` 里 `abstract` 成员**正好 22 个**（我逐个数过：`:42/47/51/55/61/67/73/79/85/94/102/109/115/120/126/140/146/232/238/248/253/262`）。另有 13 个 `virtual` 成员带默认实现，可以不管 —— 下面第 3 节会点明哪几个「不管」是有代价的。

## 0. 结论摘要

1. **一帧之内，FishNet 的两个 iterate 都发生在 `Update` 阶段的最前面**（`NetworkReaderLoop` 挂 `DefaultExecutionOrder(short.MinValue)`），而**我们的 pump 被 append 到 `Update` 子系统列表的末尾**。所以现状是：FishNet 先跑完 iterate，我们的 pump 才跑 —— **入站数据天然晚一帧**。这是本 ticket 的核心风险，第 5 节给四个方案。
2. **`Handle*` 那五个方法不是基类给实现者调的钩子，也不是实现者必须被动响应的回调 —— 它们是「实现者自己调、用来 raise 自己那个 event」的转发器。** FishNet 的任何 manager 都从不调它们（全仓 grep 证实）；`Multipass` 把五个全实现成空函数。
3. **数据必须在 `IterateIncoming` 里 raise，不能随到随 raise。** 三份参照实现（Tugboat / Synapse / Yak 残件）都是「回调线程只入队，`IterateIncoming` 出队并 raise」。而且顺序被写死：先 local 连接状态，再 remote 连接状态，最后数据包。
4. **`connectionId` 由 transport 自己分配**，只有两条硬约束：`>= 0`（`< 0` 当场 kick），且 `!= int.MaxValue`（那是 `SIMULATED_CLIENTID_VALUE`）。
5. **分片是 FishNet 自己的事，不是 transport 的事。** FishNet 按 `GetMTU` 切好再交给我们，并且**入站超 MTU 会当场踢人**。`GetMTU` 会在**连接建立之前**被调用并**永久缓存**。
6. **`Unreliable` 在 FishNet 语义里是「不可靠、不保序」**，但两份参照实现都把它落到了「不可靠但保序」的传输原语上。这一条对 #119 是决定性的，见第 4.1 节。

## 1. 一帧之内：谁在驱动谁

### 1.1 驱动链

FishNet 不用 PlayerLoop，用两个 MonoBehaviour，靠 `DefaultExecutionOrder` 卡住相对次序：

| 组件 | 执行序 | Unity 回调 | 转调 |
|------|--------|-----------|------|
| `FN:Transporting/NetworkReaderLoop.cs:8` | `short.MinValue`（`:7`） | `Update()`（`:27-30`） | `TimeManager.TickUpdate()` |
| 同上 | 同上 | `FixedUpdate()`（`:22-25`） | `TimeManager.TickFixedUpdate()` |
| `FN:Transporting/NetworkWriterLoop.cs:8` | `short.MaxValue`（`:7`） | `LateUpdate()`（`:22-25`） | `TimeManager.TickLateUpdate()` |

两个 loop 由 `TimeManager` 自己 `AddComponent` 上去（`FN:Managing/Timing/TimeManager.cs:434-441`），不需要用户摆。

**`TickLateUpdate` 里没有 iterate** —— 它只 `OnLateUpdate?.Invoke()`（`TimeManager.cs:409-413`）。名字叫 Writer 容易误会：**两个 iterate 都在 `Update`**，不在 `LateUpdate`。

### 1.2 `TickUpdate` 内部次序

```
TickUpdate()                                    TimeManager.cs:368
├── (BeforeTick 时) OnUpdate?.Invoke()          :378-379   ← Tugboat 在这里 poll socket
└── MethodLogic() → IncreaseTick()              :381 / :393
    └── do { ... } while (_elapsedTickTime >= timePerSimulation)   :721-778
        ├── OnPreTick?.Invoke()                 :725-726
        ├── TryIterateData(incoming: true)      :733-734
        ├── OnTick / 物理 / OnPostTick          :736-763
        └── TryIterateData(incoming: false)     :766-767
```

`_updateOrder` 默认 `BeforeTick`（`TimeManager.cs:158`，枚举 `:47-51`），所以 **`OnUpdate` 在 iterate 之前**。它是 `[SerializeField]` 的私有字段，**用户能在 Inspector 里翻成 `AfterTick`**（`:156-158`）—— 第 5 节方案 B 的代价就挂在这一条上。

### 1.3 一帧调几次：incoming 一次，outgoing 可能多次

`TryIterateData`（`TimeManager.cs:1098-1123`）是全部答案：

```csharp
if (incoming) {
    int frameCount = Time.frameCount;
    if (frameCount == _lastIncomingIterationFrame) return;   // :1111-1112
    _lastIncomingIterationFrame = frameCount;                // :1113
    NetworkManager.TransportManager.IterateIncoming(asServer: true);   // :1115
    NetworkManager.TransportManager.IterateIncoming(asServer: false);  // :1116
} else {
    NetworkManager.TransportManager.IterateOutgoing(asServer: true);   // :1120
    NetworkManager.TransportManager.IterateOutgoing(asServer: false);  // :1121
}
```

- **`IterateIncoming`：每帧最多一轮**，靠 `Time.frameCount` 闸住。原注释（`:1102-1109`）说明理由：一帧内数据不可能来第二次，但一帧可能有多个 tick。
- **`IterateOutgoing`：每 tick 一轮**，一帧有几个 tick 就几轮。低帧率高 tickrate 下 `do-while` 会转多圈（`:778`）。
- **两侧的次序恒定：先 `asServer: true`，再 `asServer: false`。** 两次调用**紧邻**，中间没有别的 FishNet 逻辑。
- **server 与 client 两侧都会被调，不管那一侧有没有起。** 实现者必须容忍「没起也被调」—— Tugboat 靠 socket 内部的状态检查兜（`FN:.../Tugboat/Core/ServerSocket.cs:438-448`）。

**还有一条计划外的 `IterateOutgoing`**：`ServerManager.SendDisconnectMessages(conns, iterate: true)` 会直接调 `TransportManager.IterateOutgoing(asServer: true)`（`FN:Managing/Server/ServerManager.cs:350-351`）。所以 `IterateOutgoing` **不保证只在 tick 循环里被调**，实现必须可重入。

### 1.4 `TransportManager` 这一层加了什么

```csharp
internal void IterateIncoming(bool asServer) {                 // TransportManager.cs:633
    OnIterateIncomingStart?.Invoke(asServer);
    Transport.IterateIncoming(asServer);
    OnIterateIncomingEnd?.Invoke(asServer);
}
```

`IterateOutgoing`（`:644-796`）做的事多得多，而且**顺序对我们有意义**：

1. `asServer` 且所有 server 都停了 → 直接 return（`:646-647`）。
2. 遍历脏连接、逐 channel 取 `PacketBundle`，**对每个 buffer 调一次 `Transport.SendToClient`**（`:700`）/ `Transport.SendToServer`（`:775`）。
3. 处理 `Disconnecting` 连接：延后 `max(100ms, 2 ticks)` 再断（`:716-725`），到点调 `Transport.StopConnection(clientId, true)`（`:736`）。
4. **最后**才 `Transport.IterateOutgoing(asServer)`（`:794`）。

**所以 `Send*` 是「入队」，`IterateOutgoing` 是「冲刷」** —— 一次 `IterateOutgoing` 之内 `Send*` 会被调 N 次，然后我们的 `IterateOutgoing` 被调 1 次。推断：如果我们在 `Send*` 里就直接 `DataChannel.Send`，`IterateOutgoing` 就可以是空实现，但这样会放弃「一帧内合并/限流」的唯一位置 —— 归 #119 定。

### 1.5 线程

FishNet 全部在 Unity 主线程：驱动源是 MonoBehaviour 的 `Update`/`LateUpdate`。**`Transport` 的所有 22 个成员都只在主线程被调**（推断，但推断链很短：唯一驱动是 1.1 的两个 MonoBehaviour，加上用户从自己脚本调 `ServerManager`/`ClientManager` 的 API —— 那也是主线程）。

反过来，FishNet **不要求** transport 内部单线程。Tugboat 的 LiteNetLib 回调就落在别的线程，所以它的入站队列是 `ConcurrentQueue`（`FN:.../Tugboat/Core/ServerSocket.cs:49,57`），出站队列是普通 `Queue`（`:53`，只被主线程碰）。

这一条对我们是**好消息**：我们的契约（`DCU:docs/SPEC.md:487-488`，一切公开 API 与事件都在主线程）比 FishNet 要求的更严，严的方向是安全的。

## 2. 事件与 `Handle*`：谁调谁

### 2.1 `Handle*` 不是钩子

全仓 grep `Handle*` 五个方法的调用点，结果只有两类：**基类的声明**，和**各 transport 自己的实现与自己的调用**。没有第三类。

| 调用点 | 位置 |
|--------|------|
| Tugboat：连接状态 | `FN:.../Tugboat/Core/CommonSocket.cs:39,41` |
| Tugboat：remote 状态 | `FN:.../Tugboat/Core/ServerSocket.cs:454` |
| Tugboat：server 数据 | `FN:.../Tugboat/Core/ServerSocket.cs:466` |
| Tugboat：client 数据 | `FN:.../Tugboat/Core/ClientSocket.cs:252` |
| Synapse：同构四处 | `FN:.../Synapse/Core/CommonSocket.cs:45,47`、`ServerSocket.cs:191,199`、`ClientSocket.cs:120` |

**Tugboat 的实现体就是一行转发**（`FN:.../Tugboat/Tugboat.cs:182-203, 276-293`）：

```csharp
public override void HandleClientConnectionState(ClientConnectionStateArgs a) => OnClientConnectionState?.Invoke(a);
```

**`Multipass` 把五个全部实现成 `{ }`**（`FN:.../Multipass/Multipass.cs:1074-1078`）—— 它不需要它们，因为它是**订阅子 transport 的 event**（`Multipass.cs:156-160`）而不是被调 `Handle*`。这是「`Handle*` 不是框架钩子」最硬的证据：如果框架会调，`Multipass` 这五个空实现就是五个丢事件的 bug。

**对实现者的意思**：`Handle*` 我可以完全不用，直接在 `IterateIncoming` 里 `OnServerReceivedData?.Invoke(...)` 也行。但**照抄 Tugboat 的转发写法有一个实际好处** —— 抽象成员必须实现（否则编译不过），而 C# 的 `abstract event` 只能在**声明它的类内部**被 `Invoke`。既然必须写这五个方法，让它们做转发正好。

### 2.2 谁订阅这三对事件

| 事件 | 订阅方 | 位置 |
|------|--------|------|
| `OnServerReceivedData` / `OnServerConnectionState` / `OnRemoteConnectionState` | `ServerManager` | `FN:Managing/Server/ServerManager.cs:493-495` |
| `OnClientReceivedData` / `OnClientConnectionState` | `ClientManager` | `FN:Managing/Client/ClientManager.cs:286-287` |

订阅/退订成对（`ServerManager.cs:499-501`、`ClientManager.cs:293-294`）。推断：`Transport.Index` 变化或换 transport 会走退订路径，所以我们的事件不能在退订后仍投递 —— 正常 `?.Invoke` 写法自动满足。

### 2.3 事件的顺序约束（三份实现完全一致，所以这是契约）

`IterateIncoming` 内部的次序被两份独立实现写成同一个形状：

```
1. while (LocalConnectionStates.TryDequeue(...))  → SetConnectionState → Handle{Server,Client}ConnectionState
2. if (state != Started) { ResetQueues(); if (Stopped) { StopSocket(); return; } }
3. while (_remoteConnectionEvents.TryDequeue(...)) → HandleRemoteConnectionState
4. while (_incoming.TryDequeue(...))              → Handle{Server,Client}ReceivedDataArgs
```

- Tugboat server：`FN:.../Tugboat/Core/ServerSocket.cs:429-471`
- Tugboat client：`FN:.../Tugboat/Core/ClientSocket.cs:227-256`
- Synapse server：`FN:.../Synapse/Core/ServerSocket.cs:167-204`

Tugboat 在 `:431-433` 写明了理由：*"Run local connection states first so we can begin to read for data at the start of the frame"*。

**四条硬约束**（违反任一条，FishNet 侧会出错，见 2.4）：

1. **本地连接状态先于一切。**
2. **`RemoteConnectionState.Started` 必须先于该连接的任何数据。** 否则 `ServerManager.ParseReceived` 在 `Clients` 里查不到连接，**当场 kick**（`FN:Managing/Server/ServerManager.cs:717-721`）。
3. **`RemoteConnectionState.Stopped` 必须后于该连接的最后一条数据**，否则那条数据被丢（`Clients.Remove` 已发生）。
4. **状态不重复上报。** Tugboat 靠 `SetConnectionState` 的 `if (connectionState == _connectionState) return;` 去重（`FN:.../Tugboat/Core/CommonSocket.cs:33-35`）。`ServerManager.Transport_OnRemoteConnectionState` 对 `Started` **无条件** `Clients.Add(...)`（`ServerManager.cs:621-622`），重复上报同一个 id 会往 `Dictionary` 里插重复键 —— 推断：直接抛 `ArgumentException`。

**好消息：我们的 pump 已经满足第 3 条。** `DcClosed` 在 raise 之前先 `DrainChannel(d2)`（`DCU:Runtime/DataChannelRuntime.cs:704-712`），正是「关闭前的消息不丢不乱序」。SPEC 也把它写成规范（`DCU:docs/SPEC.md:298`）。

### 2.4 一个反直觉的重入约束

`ClientManager` 在 `OnClientConnectionState` 的处理里**当场回调 `SendToServer`**（发 Version 包）：

```csharp
// FN:Managing/Client/ClientManager.cs:369-373
PooledWriter writer = WriterPool.Retrieve();
writer.WritePacketIdUnpacked(PacketId.Version);
writer.WriteString(NetworkManager.FISHNET_VERSION);
NetworkManager.TransportManager.SendToServer((byte)Channel.Reliable, writer.GetArraySegment());
```

也就是说 **`SendToServer` 会在 `IterateIncoming` 执行到一半时被调**（我们正 raise `OnClientConnectionState`）。两条要求：

1. **`SendToServer` 必须可重入**，不能假设「只在 `IterateOutgoing` 期间被调」。
2. **`Started` 事件 raise 的那一刻，`SendToServer` 必须已经能收下数据。** Tugboat 天然满足：`SetConnectionState` 先改 `_connectionState` 再 raise（`CommonSocket.cs:37-41`），而 `SendToServer` 检查的正是那个字段（`ClientSocket.cs:264-265`）。**如果顺序反了，Version 包会被静默丢掉，客户端永远握不上手。**

同一形状还有一处：`Tugboat.StartConnection` → `ClientSocket.StartConnection` 里**先入队 `Starting` 再当场 `IterateIncoming()`**（`ClientSocket.cs:110-113`；server 侧 `ServerSocket.cs:250-253` 同样），注释写「Iterate to cause state changes to invoke」。**所以 `Handle*ConnectionState` 也会从 `StartConnection` 内部被 raise，不只从 pump。**

### 2.5 缓冲区寿命：事件返回即失效

Tugboat 在 `Handle*ReceivedDataArgs` **返回后立刻** `incoming.Dispose()`，把 `byte[]` 还进 `ByteArrayPool`（`ServerSocket.cs:466-469`、`ClientSocket.cs:252-254`；`Dispose` 见 `FN:.../Tugboat/Core/Supporting.cs:37-40`）。

**意思是 FishNet 承诺同步消费完 `args.Data`，事件返回后 transport 可以回收那块内存。** 确认：`ServerManager.ParseReceived`（`ServerManager.cs:704`）与 `ClientManager.ParseReceived`（`ClientManager.cs:424`）全程同步。

`ServerReceivedDataArgs` 上有个 `FinalizeMethod`（`FN:Transporting/EventStructures.cs:48`）看着像是给延迟回收用的 —— **它在 4.7.2 里从没被调过一次**（全仓 grep 只有 `EventStructures.cs:48,56,65` 三处声明与赋 null）。**不能依赖它。**

## 3. 22 个成员：实现者视角

「必须」= 违反会坏；「不能」= 违反会坏；「可以」= 有自由度。

### 3.1 连接状态与地址（9 个）

**`string GetConnectionAddress(int connectionId)`** — `Transport.cs:42`
- 必须：对未知 id 返回 `string.Empty`，**不能抛**。Tugboat 对未知 id 返回空串加一条 warning（`FN:.../Tugboat/Core/ServerSocket.cs:211-216`）。
- 只被两处消费，都是诊断用：`NetworkConnection.ToString()`（`FN:Connection/NetworkConnection.cs:277`）和 `GetAddress()`（`FN:Connection/NetworkConnection.QOL.cs:37`）。**不参与任何逻辑判断**，所以返回值的精确性是可靠性问题而非正确性问题 → #120 第 4 问可以自由决定 relay 时报什么。
- 可以：server 没起时返回空串（Tugboat 就这么干，`ServerSocket.cs:203-209`）。

**`event Action<ClientConnectionStateArgs> OnClientConnectionState`** — `Transport.cs:47`
**`event Action<ServerConnectionStateArgs> OnServerConnectionState`** — `Transport.cs:51`
**`event Action<RemoteConnectionStateArgs> OnRemoteConnectionState`** — `Transport.cs:55`
- 必须：主线程 raise（推断，依据 1.5）。
- 必须：`args.TransportIndex` 填 `Transport.Index`（基类在 `Initialize` 里给的，`Transport.cs:29-33`）。Multipass 靠它路由（`Multipass.cs:361`）。
- 必须：`LocalConnectionState` 状态机不跳步也不重复。四态 `Stopped/Stopping/Starting/Started`（`FN:Transporting/ConnectionStates.cs:12-24`，`[Flags]` 但当普通枚举用）。
- 不能：在 `Started` raise 之前就让 `SendToServer` 丢数据（见 2.4）。
- `RemoteConnectionState` 只有 `Stopped = 0` 与 `Started = 2`（`ConnectionStates.cs:46-56`，**没有 1**，别自己造中间态）。

**`void HandleClientConnectionState(ClientConnectionStateArgs)`** — `Transport.cs:61`
**`void HandleServerConnectionState(ServerConnectionStateArgs)`** — `Transport.cs:67`
**`void HandleRemoteConnectionState(RemoteConnectionStateArgs)`** — `Transport.cs:73`
- **不是框架钩子**，见 2.1。可以写成 `{ }`（Multipass 就是）。
- 可以：照 Tugboat 写成一行转发，因为 `abstract event` 只能在声明类内 `Invoke`。

**`LocalConnectionState GetConnectionState(bool server)`** — `Transport.cs:79`
- 必须：**同步返回缓存的状态，不能有副作用**，会被高频调用。
- 必须：没起时返回 `Stopped`，不抛。
- 注意 Multipass 对 `server: true` 直接**报错并返回 `Stopped`**（`Multipass.cs:264-276`），因为多 transport 下这个问题没有唯一答案 —— 单 transport 不受影响。

**`RemoteConnectionState GetConnectionState(int connectionId)`** — `Transport.cs:85`
- 必须：未知 id 返回 `Stopped`，不抛（`FN:.../Tugboat/Core/ServerSocket.cs:19-26`）。
- 文档说「只能在 server 上调」（`Transport.cs:82`），但**没有任何强制** → 我们也不必强制。

### 3.2 发送（2 个）

**`void SendToServer(byte channelId, ArraySegment<byte> segment)`** — `Transport.cs:94`
**`void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)`** — `Transport.cs:102`
- **必须：立刻拷走 `segment`，不能持有。** 上游是 `PacketBundle` 的复用 buffer，`IterateOutgoing` 里 `ProcessPacketBundle` 一返回就 `ppb.Reset(false)`（`FN:Managing/Transporting/TransportManager.cs:705,780`）。Tugboat 在 `Packet` 构造里当场 `Buffer.BlockCopy`（`FN:.../Tugboat/Core/Supporting.cs:21-30`）。
- **必须：不能抛。** 调用点在 `IterateOutgoing` 的双层循环里（`TransportManager.cs:687-703`），一次抛异常会把该帧剩下所有连接的发送全打断。**这是我们最容易踩的一条 —— `DataChannel.Send` 在通道未 open 时会抛**（`DCU:Runtime/DataChannel.cs:168-179`，`SendCore` 刻意不预检 open 状态，交给原生失败后 `RequireOk` 抛）。适配层必须 try/catch。
- 必须：容忍未知 `connectionId`（静默丢，Tugboat：`peer != null` 才发，`ServerSocket.cs:407-411`）。
- 必须：容忍越界 `channelId`。Tugboat 用 `SanitizeChannel` 把 `>= 2` 的强制降成 reliable 并 warning（`FN:.../Tugboat/Tugboat.cs:566-573`）。
- 必须：容忍**零长度** `segment`（推断：FishNet 不保证非空；我们的 `Send` 已显式支持零长，`DCU:Runtime/DataChannel.cs:129`+`:175`）。
- 可以：入队而不立即发（Tugboat/Synapse 都入队，靠 `IterateOutgoing` 冲刷）。
- **`connectionId == -1` 是广播语义**：Tugboat 见 `UNSET_CLIENTID_VALUE` 就 `SendToAll`（`ServerSocket.cs:400-403`）。推断：`TransportManager` 的正常路径总带真实 id（`:700` 传 `conn.ClientId`），所以这条广播分支我们**可以不实现**，但要确保不会把 `-1` 当成一个真连接去查表。

### 3.3 接收（3 个 + 1 个 virtual）

**`event Action<ClientReceivedDataArgs> OnClientReceivedData`** — `Transport.cs:109`
**`event Action<ServerReceivedDataArgs> OnServerReceivedData`** — `Transport.cs:120`
- **必须：只在 `IterateIncoming` 期间 raise**（见第 2.3 节；三份实现一致）。
- 必须：`args.Channel` 如实反映收到时的 channel。`ServerManager` 用它做 MTU 检查（`ServerManager.cs:736`）。
- 必须：`args.ConnectionId` 是分配给该连接的 id；`args.TransportIndex` 填 `Transport.Index`。
- 必须：保住**消息边界**。FishNet 按「一次 raise = 一个完整包」解析：`ParseReceived` 直接 `reader.ReadTickUnpacked()` 读头（`ServerManager.cs:754`）。**流式/粘包会当场解析错乱。** DataChannel 天然是消息式的，这条免费成立。
- 可以：事件返回后立即回收 buffer（见 2.5）。

**`void HandleClientReceivedDataArgs(...)`** — `Transport.cs:115`
**`void HandleServerReceivedDataArgs(...)`** — `Transport.cs:126`
- 同 3.1 的 `Handle*`：不是钩子，可空实现。

**`virtual float GetPacketLoss(bool asServer)`** — `Transport.cs:132`，默认 `0f`
- 纯诊断。可以不实现。

### 3.4 迭代（2 个）

**`void IterateIncoming(bool asServer)`** — `Transport.cs:140`
- **必须：把本轮所有待投递的东西按 2.3 的顺序投完。**
- 必须：`asServer` 只处理对应一侧。**不能**在 `asServer: true` 时投 client 侧数据（推断：会打乱 `ClientManager` 的 `OnIterateIncomingEnd(server)` 语义 —— 它靠 `!server` 决定是否 `IterateObjectCache`，`FN:Managing/Client/ClientManager.cs:416-417`）。
- 必须：没起也能被调，且不炸（1.3）。
- 必须：可重入 —— `StartConnection` 内部会直接调它（2.4 末）。
- 每帧最多一轮（1.3）。

**`void IterateOutgoing(bool asServer)`** — `Transport.cs:146`
- 必须：冲刷该侧待发数据。
- 必须：可重入，且一帧可能多轮（1.3）。
- 必须：不能抛（同 3.2 理由，调用点 `TransportManager.cs:794`）。

### 3.5 启停（4 个）

**`bool StartConnection(bool server)`** — `Transport.cs:232`
- 返回值语义是「**没有阻塞项**」，不是「已连上」（Yak 注释说得最白：`FN:Plugins/Yak/Yak.cs:259`）。
- 必须：异步连接过程中先上报 `Starting`，连上后 `Started`。
- 可以：同步返回 `true` 后一切走事件。

**`bool StopConnection(bool server)`** — `Transport.cs:238`
- 必须：上报 `Stopping` 然后 `Stopped`。Tugboat 先入队 `Stopping` 再 `StopSocket()`，后者补入队 `Stopped`（`FN:.../Tugboat/Core/ServerSocket.cs:271-279`、`CommonSocket.cs:188-192`）。
- 必须：重复调用要幂等 —— 已 `Stopped`/`Stopping` 时返回 `false`（`ServerSocket.cs:273-274`）。

**`bool StopConnection(int connectionId, bool immediately)`** — `Transport.cs:248`
- **`immediately` 的两种语义，在 4.7.2 里没有一份叶子实现真的区分。** Tugboat 直接忽略这个参数（`FN:.../Tugboat/Tugboat.cs:480-483`：`return ServerSocket.StopConnection(connectionId);`），Synapse 同样忽略（`FN:.../Synapse/Synapse.cs:359-361`）。Multipass 会**如实转发**给子 transport（`Multipass.cs:1023`），但子 transport 收到后照样不用 —— 所以整条链上没有任何一处真正区分这两种语义。
- 文档给的定义（`Transport.cs:244-247`）：`true` = 「abruptly stop the client socket」，且明确说*「技术手段因 transport 而异」*，并建议不要绕过 `ServerManager` 直接调非 immediate 版本。
- **FishNet 自己只在一个地方调它，且恒传 `true`**：`TransportManager` 的延迟断开，等 `max(100ms, 2 ticks)` 让本 tick 的数据发完之后（`FN:Managing/Transporting/TransportManager.cs:716-740`，`:736` 传 `true`）。Multipass 在 id 耗尽踢人时也传 `true`（`Multipass.cs:372`）。
- **对实现者的结论**：`immediately: false` 事实上是**死路径**（推断，基于全仓调用点只有上述两处且都传 `true`）。安全做法是两者都当 `true`：**FishNet 已经在上层替我们做完了「等数据发完」的延迟**，所以 transport 层再拖一次没有价值。
- 必须：不能抛；未知 id 返回 `false`（`ServerSocket.cs:291-293`）。
- 可以：不自己 raise `Stopped`，等底层断开回调来（Tugboat 刻意如此，`ServerSocket.cs:297-299` 那行注释掉的代码就是证据）。

**`void Shutdown()`** — `Transport.cs:253`
- 必须：client 与 server 都停。Tugboat：`StopConnection(false); StopConnection(true);`（`Tugboat.cs:488-493`）——**先 client 后 server**。
- 会从 `OnDestroy` 和 finalizer 调（`Tugboat.cs:15-18, 125-130`）。推断：**finalizer 路径对我们是危险的** —— `DCU:docs/SPEC.md:473` 明确要求 finalizer 只入队、绝不调 `dcu_*`。所以我们的 `Shutdown` 只能挂 `OnDestroy`，不能挂 finalizer。

### 3.6 Channel（1 个）

**`int GetMTU(byte channel)`** — `Transport.cs:262` — 见第 4.2 节，事情最多。

### 3.7 那 13 个 virtual：哪几个不实现是有代价的

| 成员 | 默认 | 不实现的代价 |
|------|------|-------------|
| `IsLocalTransport(int)` `:155` | `false` | **无代价 —— 4.7.2 里是死代码。** 唯一调用点是 `TransportManager.QOL.cs:18-26` 的包装，而那个包装**没有任何调用者**（全仓 grep 证实）。文档说的「several security checks are disabled」在 4.7.2 已不存在 |
| `GetTimeout(bool)` `:162` / `SetTimeout(float,bool)` `:168` | `-1f` / `{ }` | **无代价 —— 全仓无调用者。** 超时由 `ServerManager.CheckClientTimeout` 独立实现（`ServerManager.cs:377-420`），不看 transport |
| `GetMaximumClients()` `:174` / `SetMaximumClients(int)` `:185` | 警告 + `-1` | 有代价：默认实现会**打日志**。#120 第 5 问要定上限，届时应实现掉，否则用户查上限就吃一条 warning |
| `SetClientAddress`/`GetClientAddress` `:195/:200` | `{ }` / `""` | 有代价：`ClientManager.StartConnection(address, port)` 会调 `SetClientAddress`（`ClientManager.cs:342`）。不实现则这个重载静默失效。`GetClientAddress` 只在日志里用（`ClientManager.cs:382`） |
| `SetServerBindAddress`/`GetServerBindAddress` `:207/:213` | `{ }` / `""` | 低：WebRTC 场景语义本就不同。可留空但应在 XML doc 里说明 |
| `SetPort`/`GetPort` `:219/:224` | `{ }` / `0` | 有代价：`ServerManager.StartConnection(port)` 调 `SetPort`（`ServerManager.cs:370`），`ClientManager.StartConnection(address, port)` 同样（`:343`） |

## 4. #119 / #120 的前置事实

### 4.1 channel 语义：`Unreliable` 是否保序

**FishNet 的枚举只有两档**（`FN:Transporting/Channels.cs:6-16`），注释是全部的规范文本：

```csharp
public enum Channel : byte {
    /// Data will be sent ordered reliable.
    Reliable = 0,
    /// Data will be sent unreliable.
    Unreliable = 1
}
```

- `Reliable` = **可靠 + 保序**（"ordered reliable"，明说了）。
- `Unreliable` = **不可靠**，**对保序未作任何承诺**。

`CHANNEL_COUNT = 2`（`FN:Managing/Transporting/TransportManager.cs:172`），且 `InitializeToServerBundles` 的注释写明「即使 transport 只支持 reliable，也要为 unreliable 建好」（`:331-334`）—— **两档必须都存在**，不能只实现一档。

**两份参照实现实际落到了什么原语上**：

| 实现 | `Reliable` | `Unreliable` |
|------|-----------|-------------|
| Tugboat | `DeliveryMethod.ReliableOrdered` | `DeliveryMethod.Unreliable`（`FN:.../Tugboat/Core/ServerSocket.cs:390`、`ClientSocket.cs:200`） |
| Synapse | 内部 reliable 配置 | 见 `UnreliableSegmentMode` |

LiteNetLib 的 `DeliveryMethod.Unreliable` 是**不可靠且不保序**（会乱序到达）。

**给 #119 的结论**：`Unreliable` 映射到 `Ordered = false, Reliable = false` 是**符合契约**的，而且这正是 `DCU:docs/SPEC.md:648` 给状态同步流量推荐的档位（"A newer message strictly supersedes an older one"）。不需要为了保序而牺牲什么 —— FishNet 没要求。

**但有一条例外必须处理**：FishNet 会把**超 MTU 的分片强制改到 reliable**（`TransportManager.cs:597-601`：`channelId = SPLIT_PACKET_CHANNELID`，而 `SPLIT_PACKET_CHANNELID = (byte)Channel.Reliable`，`:197`）。分片重组依赖顺序，所以 reliable 那条**必须 `Ordered = true`**，不能为了性能翻成 false。

两份实现还都有一个**兜底降级**：unreliable 上遇到超 MTU 的包，就地改用 reliable 并打 warning（`FN:.../Tugboat/Core/ServerSocket.cs:393-397`、`ClientSocket.cs:203-207`）。推断：这是 UDP 特有的（单包不能超 MTU），SCTP 会自己分片，**我们不需要这个降级** —— 但如果我们要它，SPEC 第 233 行那条「never lie about the mode the application chose」需要先被讨论，因为悄悄把 unreliable 改成 reliable 正是一种「lie」。

### 4.2 `GetMTU` 与分片责任

**分片是 FishNet 的责任。** 证据链：

1. `TransportManager.SendSplitMessage`（`:576-627`）在数据超 `GetLowestMTU(channelId)` 时自己切片，每片带 `PacketId.Split` + 片数头。
2. `ServerManager.ParseReceived` 的注释直说：*"FishNet internally splits packets so nothing should ever arrive over MTU."*（`ServerManager.cs:735`）
3. **入站超 MTU 会被当场踢**（`ServerManager.cs:736-742`）：
   ```csharp
   int channelMtu = NetworkManager.TransportManager.GetMTU(args.TransportIndex, (byte)args.Channel);
   if (segment.Count > channelMtu) { ExceededMTUKick(segment.Count, channelMtu); return; }
   ```

**所以 `GetMTU` 的返回值有双重身份：既是出站切片的尺度，也是入站的踢人阈值。** 报大了 → 我们可能收到超过自己 `MaxMessageSize` 的包；报小了 → 白白多切片，且**对端发来的正常包会把自己人踢掉**。

**四条硬约束**：

1. **`GetMTU` 会在连接建立之前被调用。** `TransportManager.InitializeOnce_Internal` → `SetLowestMTUs()`（`TransportManager.cs:208`、`:226-260`）。**这时没有任何 PeerConnection，拿不到协商后的值。** 所以不能返回「协商结果」，必须返回一个**静态的、保守的**常量。
2. **结果被永久缓存。** `SetLowestMTUs` 开头 `if (_lowestMtu != 0) return;`（`:229-230`），运行期改不了。
3. **FishNet 还要再扣一层 reserve**：`GetMTUWithReserve` = `mtu - MINIMUM_MTU_RESERVE(1) - _customMtuReserve(默认 1)`（`:346-360`、`:130`、`:177`），即**默认净扣 2 字节**。而且**扣完 `<= 100` 会返回 `INVALID_MTU`(-1) 并 warning**（`:351-357`）—— 所以返回值必须显著大于 102。
4. **两个 channel 应当返回同一个值。** `SetLowestMTUs` 里 `_lowestMtu = Mathf.Min(allLowest, channelLowest)`（`:258`）而 `allLowest` 在循环里**从未被更新**（始终是 `int.MaxValue`），所以 `_lowestMtu` 实际等于**最后一个 channel**（Unreliable）的值，不是两者最小值。这看着像上游 bug，但**只要两档返回相同的值，这个差异就完全不可观测** —— 这是最省事的规避，也不必赌它是不是 bug。

**参照值**：

| 实现 | `GetMTU` 返回 | 出处 |
|------|--------------|------|
| Tugboat | `1350 - 68 = 1282`（→ FishNet 侧净 1280） | `FN:.../Tugboat/Tugboat.cs:583`，常量 `:115` 与 `FN:.../LiteNetLib/NetConstants.cs:49` |
| Yak（本地） | `5000` | `FN:Plugins/Yak/Yak.cs:26,313` |
| Synapse | `MaximumTransmissionUnit`，注释写明*「Synapse handles segmentation internally; this value guides FishNet's packet sizing」* | `FN:.../Synapse/Synapse.cs:415-421` |

**Synapse 那条注释是我们的直接先例**：底层自己会分片时，`GetMTU` 就退化成「给 FishNet 的一个打包尺度」，不必等于任何物理 MTU。SCTP 同理。

`Tugboat.GetMTU` **忽略 `channel` 参数**（`Tugboat.cs:581-584`），与第 4 条约束自然一致。

**给 #119 的结论**：`GetMTU` 应返回一个固定常量，与 `PeerConnectionConfig.Mtu`（默认 `Automatic = 0`，`DCU:Runtime/PeerConnectionConfig.cs:41`）**解耦**。上界由我们的 `MaxMessageSize` 决定（默认 `Automatic`，实际落到上游 `DEFAULT_LOCAL_MAX_MESSAGE_SIZE = 256KB`，见 `DCU:docs/SPEC.md:565`），但**没有理由报满 256KB**：报得越大，`PacketBundle` 的常驻 buffer 越大（`_toServerBundles` 按 MTU 建，`TransportManager.cs:337-338`），而 FishNet 的 `MaximumClientPacketSize` 默认只有 20480（`:75`）。

### 4.3 `connectionId`：谁分配、取值约束

**transport 自己分配。** FishNet 只消费，从不指定。

**保留值**（`FN:Connection/NetworkConnection.cs:194-215`）：

| 常量 | 值 | 含义 |
|------|-----|------|
| `UNSET_CLIENTID_VALUE` | `-1` | 未设置；在 `SendToClient` 里是「广播」（3.2） |
| `MAXIMUM_CLIENTID_VALUE` | `int.MaxValue` | 上限 |
| `MAXIMUM_CLIENTID_WITHOUT_SIMULATED_VALUE` | `int.MaxValue - 1` | 真实连接的实际上限 |
| `SIMULATED_CLIENTID_VALUE` | `int.MaxValue` | 「不走 socket 的模拟本地 client」专用 |

**唯一被强制的检查**（`FN:Managing/Server/ServerManager.cs:606-614`）：

```csharp
int id = args.ConnectionId;
if (id < 0) {
    Kick(args.ConnectionId, KickReason.UnexpectedProblem, LoggingType.Error,
        $"The transport you are using supplied an invalid connection Id of {id}. " +
        $"Connection Id values must range between 0 and {NetworkConnection.MAXIMUM_CLIENTID_VALUE}. ...");
    return;
}
```

注释原文写着 *"Sanity check to make sure transports are following proper types/ranges"* —— 这就是给 transport 实现者的约束。

**所以约束是**：
- **必须 `>= 0`**（强制，会踢）。
- **必须 `!= int.MaxValue`**，因为那是 `SIMULATED_CLIENTID_VALUE`（未强制，推断：撞上会与模拟 client 混淆）。安全上界取 `int.MaxValue - 1`。
- **必须在存活连接之间唯一。** `Clients.Add(id, conn)` 无条件插入（`ServerManager.cs:622`），撞键推断会抛。
- **不要求连续、不要求从 0 起、不要求单调。** 两份实现两种做法：Tugboat 用 LiteNetLib 的 `peer.Id`（`ServerSocket.cs:325,333,339`，会复用），Synapse 用 `Interlocked.Increment` 单调递增且**永不复用**（`FN:.../Synapse/Core/ServerSocket.cs:340`）。
- **可以复用已断开连接的 id。** Multipass 明确把 id 还进队列复用（`Multipass.cs:405`）。

**Multipass 证明 id 只需在单个 transport 内唯一**：它给每个「(transportIndex, transportId)」映射一个独立的 `multipassId`（`Multipass.cs:17-48`、`:338-417`），进出方向双向改写 `args.ConnectionId`。所以我们不必操心与其他 transport 的 id 冲突。

### 4.4 host 拓扑：本地 client 占不占 id

**占，而且是一条真实连接。**

- `IsHostStarted => IsServerStarted && IsClientStarted`（`FN:Managing/NetworkManager.QOL.cs:46`）—— **host 不是一种特殊模式，只是两者同时起**。
- `NetworkConnection.IsHost => NetworkManager.IsServerStarted && this == NetworkManager.ClientManager.Connection`（`FN:Connection/NetworkConnection.QOL.cs:19`）—— host 的本地 client **是 `Clients` 集合里的一个普通 `NetworkConnection`**，有自己的 `ClientId`。
- 而 `ClientId` 只能来自 transport 的 `RemoteConnectionState.Started`（`ServerManager.cs:618-622`）。

**所以：host 的本地 client 必须占用一个正常的 `connectionId`，并且走完整的「Started 事件 → 数据」流程。** 它不能是短路旁路，除非我们自己造一个假的 remote 连接事件。

**`SIMULATED_CLIENTID_VALUE` 是为「完全不走 socket」准备的，而它对应的 Yak transport 在 4.7.2 里是残件**：`Yak._server` / `_client` 两个字段**从未被赋值**（`FN:Plugins/Yak/Yak.cs:15-19`，`Initialize` 是空的 `:29-31`），于是 `StartServer()` 恒返回 `false`（`:263`），`StartClient()` 恒返回 `true` 但什么都不做（`:279-282`），`IterateIncoming`/`IterateOutgoing`/`SendToServer`/`SendToClient` 全是空实现（`:109-117`、`:157-169`）。**Yak 不是可用的 host 参照，也不是「本地 client 短路」的现成先例。**

**给 #120 的结论**：host 自己的本地 client 走**真 loopback** 是唯一与 FishNet 契约一致的做法（否则要伪造 remote 连接事件，且 `GetConnectionState(connectionId)` / `GetConnectionAddress` 都要为它开分支）。代价是 host 上多一条 PeerConnection —— 但换来 host 与 client 代码路径完全不分叉。

**client 侧不需要 server 能力**（#120 第 3 问）：`ClientManager` 只订阅 `OnClientReceivedData` / `OnClientConnectionState` 两个事件（`ClientManager.cs:286-287`），从不碰 server 侧。但注意 **`IterateIncoming(asServer: true)` 与 `IterateOutgoing(asServer: true)` 在纯 client 上照样会被调**（1.3），必须安全地什么都不做。

## 5. 两套 pump 怎么对齐（本 ticket 的重点）

### 5.1 现状：两套 pump 各自在哪

**我们的 pump**（`DCU:Runtime/DataChannelRuntime.cs`）：

- 插在 **`Update` 子系统列表的末尾** —— `InsertPump` 找到 `typeof(Update)` 后 `list.Add(pump)`（`:290-315`，关键在 `:309` 是 `Add` 而非 `Insert(0, ...)`）。先 `RemoveAll` 去重（`:308`），所以幂等。
- 两段结构，都排空：控制段 `DrainControlEvents`（`:543-579`）→ 数据段 `DrainMessages`（`:581-598`）。完整次序见 `Pump()`（`:331-356`）：`LeakTracker.Drain` → `DrainNativeLogs` → `WarnIfControlQueueBacklogged` → `DrainControlEvents` → `DrainMessages`。
- 主线程强制：`MainThread.Assert`（`:333`），throw，但靠 `[Conditional("UNITY_EDITOR")]` + `[Conditional("DEVELOPMENT_BUILD")]` 在 release player 里**擦除调用点**（`DCU:Runtime/Internal/MainThread.cs:48-49`）。

**FishNet 的 iterate**：`Update` 阶段最前（1.1）。

**关键：这两个位置的相对次序是已测的，不是推的。** 本仓 `docs/research/unity-native-plugin-lifecycle.md:52-57` 有一份 2022.3.62f3 的实测 dump：

```text
Update 的实际内容：
  R3.R3LoopRunners+R3Update
  UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate          ← FishNet 的 Update() 在这里
  UnityEngine.PlayerLoop.Update+ScriptRunDelayedDynamicFrameRate
  UnityEngine.PlayerLoop.Update+ScriptRunDelayedTasks
  UnityEngine.PlayerLoop.Update+DirectorUpdate
                                                                  ← 我们 Add 到这后面
```

MonoBehaviour 的 `Update()` 在 `ScriptRunBehaviourUpdate` 里跑，而我们 append 在 `DirectorUpdate` 之后。**所以一帧的实际次序是：**

```
Update
├── ScriptRunBehaviourUpdate
│   └── NetworkReaderLoop.Update()          [执行序 short.MinValue，本阶段最先]
│       └── TimeManager.TickUpdate()
│           ├── OnUpdate                    ← Tugboat 在这里 poll
│           ├── IterateIncoming(true), IterateIncoming(false)
│           └── IterateOutgoing(true), IterateOutgoing(false)
├── ...(其余三条 native 子系统)
└── DataChannelRuntime.Pump()               ← 我们在这里，已经晚了
PreLateUpdate / LateUpdate
└── NetworkWriterLoop.LateUpdate() → TickLateUpdate()   [不 iterate]
```

### 5.2 问题的准确形状：只有入站，出站没问题

**出站没有对齐问题。** FishNet 在帧内早期调 `SendToClient`/`SendToServer`，然后调我们的 `IterateOutgoing`。`DataChannel.Send` 是**同步 P/Invoke**（`DCU:Runtime/DataChannel.cs:168-179`），**不经过 pump**。所以出站在同一帧内就出去了，pump 位置无关。

**入站晚一帧。** 链路：native 线程收到消息 → 进上游队列 → **我们的 pump 拉取并 raise `MessageReceived`** → 适配层缓冲 → **FishNet 的 `IterateIncoming` 取走**。而 pump 在帧尾、`IterateIncoming` 在帧首，所以帧 N 拉到的消息，要到**帧 N+1** 才进 FishNet。

**代价量级**：60fps 下 **+16.7ms**，叠加在网络 RTT 之上。对 FishNet 尤其难受 —— 它是 tick 制的，客户端输入晚一个 tick 到服务器，是**纯增延迟**（不是抖动，抖动还能靠缓冲吸收）。而且 host 侧 loopback 连接本该是零延迟的，却也吃这一帧。

同样晚一帧的还有**控制事件**：`DcOpen` / `ConnectionState` 决定我们何时上报 `RemoteConnectionState.Started`，所以连接建立也慢一帧。这个无所谓（建连本来就是几十到几百毫秒量级）。

### 5.3 四个方案

#### 方案 A：不动，适配层缓冲，接受 +1 帧

`MessageReceived` 回调把数据拷进「按 connectionId 分桶」的队列；`IterateIncoming(asServer)` 排空对应侧的桶，逐条 `Handle*ReceivedDataArgs`。

**代价**
- **入站恒定 +1 帧（60fps 约 16.7ms）。** 这是纯增延迟。
- 队列要定上限与溢出策略 —— 又一个要定的决策（与 #119 的出站背压不是同一件事）。
- 峰值内存：一帧内到达的全部消息都要驻留。

**好处**
- **零耦合、零风险**：不碰 pump 注册，不依赖 FishNet 内部任何东西。
- 拷贝**不是额外代价**：我们的 `MessageReceived` 交的是 `ReadOnlySpan<byte>`，只在回调期间有效（`DCU:Runtime/DataChannelMessageHandler.cs:8-11`），而 FishNet 要 `ArraySegment<byte>` —— **无论哪个方案都必须拷一次**。这一条不是 A 的缺点。
- 天然满足 2.3 的顺序约束（我们自己控制入桶顺序）。

#### 方案 B：适配层订阅 `TimeManager.OnUpdate`，在里面 `Pump()`

`_updateOrder` 默认 `BeforeTick`，所以 `OnUpdate` 在 `IterateIncoming` **之前**、同一帧内（1.2）。**这正是 Tugboat 放 `PollSocket` 的位置**（`FN:.../Tugboat/Tugboat.cs:210-214`，订阅于 `Initialize` `:122`，退订于 `OnDestroy` `:129`）。

**代价**
- **用户能在 Inspector 把 `_updateOrder` 翻成 `AfterTick`**（`TimeManager.cs:156-158`），届时 `OnUpdate` 落到 iterate **之后**，**静默退回 +1 帧**。而它是 `private [SerializeField]`，我们**读不到**（除了反射），所以连告警都发不出来。
- **一帧会 pump 两次**（`OnUpdate` 一次 + PlayerLoop 一次）。第二次通常空转，但每通道每帧多一次 `dcu_dc_receive` P/Invoke，且 `_pumpTicks` 计数翻倍 —— 存活诊断报的数字会偏离直觉（`:339`）。
- **与 SPEC 的一条设计意图同向冲突**：`docs/SPEC.md:585` 立的原则是「**exactly one answer to "what is driving the pump right now"**」。注意那句原文说的是 edit mode 与 play mode 之间的分工，**不是**在讲 play mode 内部只能有一个驱动源 —— 所以这不是硬违规，但 B 确实让「谁在驱动 pump」多出一个答案，而那条原则的精神正是反对这个。
- 耦合 `TimeManager` 生命周期：`Initialize` 订阅、`OnDestroy` 退订，漏一个就泄漏。

**好处**
- 同帧交付，**零额外延迟**。
- 与 Tugboat 同构 —— 官方实现就在这个位置 poll，位置本身是被祝福的。

#### 方案 C：把 PlayerLoop 条目往前挪（`Update` 列表头部，或 `EarlyUpdate`）

把 `:309` 的 `list.Add(pump)` 改成 `list.Insert(0, pump)`，或整体移到 `EarlyUpdate`。这样 pump 在**所有** MonoBehaviour 之前跑，自然早于 FishNet。

**代价**
- **改的是包的全局行为，不是适配层的行为。** 所有使用者的 `MessageReceived` 回调时机都从「MonoBehaviour 之后」变成「之前」。这是**语义变更**，得单独立决策（推断：可能影响现有测试与示例的时序假设）。
- **仍然不保证赢**：如果第三方包也往 `Update` 头部 `Insert(0, ...)`，谁先谁后取决于注册顺序，而这是不可控的。相比之下 FishNet 的 `short.MinValue` 是在 `ScriptRunBehaviourUpdate` **内部**排序，与我们不在同一竞技场，所以 `EarlyUpdate` 变体是稳的、`Insert(0)` 变体不稳。
- `EarlyUpdate` 变体要重新验证域重载/进出播放的行为（`docs/research/unity-native-plugin-lifecycle.md` 那套结论是针对 `Update` 测的）。**未实测。**

**好处**
- 同帧交付，且**不依赖 FishNet 任何内部细节** —— 对非 FishNet 使用者也是净收益（更早拿到数据）。
- 不增加 pump 次数。

#### 方案 D：适配层在 `IterateIncoming` 里直接调 `DataChannelRuntime.Pump()`

`Pump()` 是 public，SPEC 明确说它「stays public for tests and custom loops」（`docs/SPEC.md:490`、`:407`）。**我们就是那个 custom loop。**

在 `IterateIncoming(asServer: true)` 开头 pump 一次（用 `Time.frameCount` 闸住，一帧只一次），消息在回调里当场入桶，然后两次 `IterateIncoming` 各排空自己那一侧的桶。

**为什么还需要桶**：pump 是**全局**的（`DCU:Runtime/DataChannelRuntime.cs:581-598` 遍历 `HandleTable` 全部通道），一次调用会把 server 侧和 client 侧的消息**都**拉出来。而 `IterateIncoming(asServer: true)` 期间不该投递 client 侧数据（3.4）。由于两次 `IterateIncoming` 紧邻且顺序固定（1.3），在 `true` 那次 pump、把 client 侧的存桶、`false` 那次排空，**仍然是同帧**。

**代价**
- **一帧 pump 两次**（`IterateIncoming` 一次 + PlayerLoop 尾部一次）。第二次几乎空转：控制队列已空，每通道一次 `dcu_dc_receive` 返回 `NOT_AVAIL`。host 带 16 条通道时约 18 次多余 P/Invoke/帧 —— 可忽略，但要写进文档否则下一个人会以为是 bug。
- `_pumpTicks` 每帧 +2，存活诊断的数字含义变化（同 B）。
- **不能删掉 PlayerLoop 条目**：非 FishNet 使用者要它，而且它是存活契约的兜底 —— FishNet 停掉后若只剩 `IterateIncoming` 驱动，pump 就彻底停了，`CheckPumpLiveness` 会在 5s 后误报「第三方抹掉了条目」并**花掉那唯一一次重试**（`DCU:Runtime/DataChannelRuntime.cs:472-499`，阈值 `PumpStaleSeconds = 5.0` 在 `:20`）。留着 PlayerLoop 条目正好避免这个假阳性。
- **重入面变大**：pump 会在 `IterateIncoming` 内部 raise `MessageReceived`，于是**别的、与 FishNet 无关的通道**的用户回调也会在 FishNet 的 iterate 内部跑。合法但意外（推断：若那个回调抛异常，`SafeDispatch` 会兜住，`DCU:Runtime/DataChannel.cs:302`，所以不会打断 FishNet）。

**好处**
- **同帧交付**，且**不依赖 `_updateOrder`**（这是相对 B 的决定性优势 —— B 的失效模式是静默的，D 没有这个失效模式）。
- **不改包的全局行为**（相对 C 的优势）：`Pump()` 的语义就是「按需驱动」，本来就是为这个场景留的。
- **控制段先于数据段**（`:350-351`）恰好满足 2.3 的顺序要求：连接状态事件在同一次 pump 里先出来，数据后出来。
- 只动适配层，包本体零改动。

### 5.4 取舍

**推荐 D，其次 A。**

D 是唯一同时满足三条的方案：同帧、不依赖 FishNet 私有字段、不改包的全局语义。它的代价（每帧多一次近空转 pump、`_pumpTicks` 翻倍）是**可量化且可文档化**的；B 的代价（用户翻个 Inspector 开关就静默退化）**不可检测**，这是量级上的差别 —— 一个是已知常量开销，一个是潜伏故障。

A 是保底：如果实测发现 +1 帧在目标场景可接受（比如回合制、或已有插值缓冲），A 的零风险最划算。**建议按 A 的结构实现（桶是 D 也要的），把 D 做成一个开关** —— 两者的差别只是「谁触发那次 pump」，代码结构完全一样。

C 不该在这张 ticket 里定：它改的是包的公共时序语义，影响面超出 FishNet 适配，应该单独立 issue。如果哪天决定做，`EarlyUpdate` 变体优于 `Insert(0)` 变体（后者赢不稳）。

### 5.5 三条与 pump 无关但必须一起处理的

1. **`DrainControlEvents` 每次 pump 上限 256 条**（`DCU:Runtime/DataChannelRuntime.cs:545`：`for (int safety = 0; safety < 256; safety++)`），而 SPEC §6 写的是「`dcu_event_next` **until the queue is empty**」且「Neither segment has a per-frame budget」（`docs/SPEC.md:494,497`）。**代码与规范不一致，规范是规范性的**（CLAUDE.md 立的规矩），所以按字面 256 是个 bug。实践上：一次 pump 涌入 >256 条控制事件才会触发，host 启动时批量建连有可能。**不属于本 ticket，建议单独开 issue 核实。** 对适配层的影响：溢出的事件顺延到下一次 pump，顺序不乱，所以 2.3 的约束不破。
2. **`Send` 会抛，而 FishNet 的 `SendToClient` 不能抛**（3.2）。适配层必须 try/catch 并决定失败语义（丢弃 + 计数？触发断开？）—— 归 #119 的背压决策。
3. **`MainThread.Assert` 在 release player 里被擦除**（`DCU:Runtime/Internal/MainThread.cs:48-49`）。FishNet 全程主线程（1.5），所以正常路径无影响；但**适配层不要依赖这个断言在生产中挡住误用**。

## 6. 落到 #119 / #120 的直接输入

| 待定 | 本文交代的事实 |
|------|--------------|
| #119-2 `Unreliable` 是否保序 | **不保序**。FishNet 只承诺 `Reliable` 是 "ordered reliable"（`Channels.cs:9-15`），Tugboat 落到 LiteNetLib `Unreliable`（不可靠不保序）。→ `Ordered=false, Reliable=false` 合规 |
| #119-2 reliable 那条能否不保序 | **不能。** 分片重组强制走 reliable 且依赖顺序（`TransportManager.cs:197,597-601`）→ 必须 `Ordered=true` |
| #119-3 谁分片 | **FishNet 自己分**，并且入站超 MTU **当场踢人**（`ServerManager.cs:735-742`）。我们只需保住消息边界 |
| #119-3 `GetMTU` 返回什么 | 固定常量，与 `PeerConnectionConfig.Mtu` 解耦；连接前就被调且**永久缓存**（`TransportManager.cs:208,229-230`）；FishNet 再扣 2 字节，扣后 ≤100 视为无效（`:346-357`）；两档应返回同值（`:258` 那个 `allLowest` 的规避）。Synapse 先例：底层自分片时这只是「打包尺度」 |
| #119-4 出站背压 | `Send*` **不能抛**（`TransportManager.cs:687-703` 双层循环），但我们的 `DataChannel.Send` **会抛**（未 open 时）。`BufferedAmount`（`DCU:Runtime/DataChannel.cs:107-118`）是唯一现成的背压读数 |
| #120-1 host 本地 client | **必须占一个正常 connectionId 并走完整 Started 流程**（`NetworkManager.QOL.cs:46`、`NetworkConnection.QOL.cs:19`、`ServerManager.cs:618-622`）。Yak 在 4.7.2 是残件，不是可用先例 |
| #120-2 id 分配规则 | transport 自己分配；`>= 0` 强制（`ServerManager.cs:610-614`），避开 `int.MaxValue`；存活期唯一；可复用；不必连续/单调。Multipass 证明只需单 transport 内唯一 |
| #120-3 client 侧要不要 server 能力 | **不要**，但 `Iterate*(asServer: true)` 在纯 client 上照样被调，必须安全空转（`TimeManager.cs:1115-1121`） |
| #120-4 `GetConnectionAddress` | 只用于日志/诊断（`NetworkConnection.cs:277`、`QOL.cs:37`），**不参与逻辑** → relay 时报什么都不会破功能。未知 id 返回 `string.Empty`，不抛 |
| #120-5 上限 | `GetMaximumClients()` 是 `virtual`，不实现只是打 warning（`Transport.cs:174-179`）。硬上界 `int.MaxValue - 1` |
| #120-6 掉线清理 | `RemoteConnectionState.Stopped` 必须**后于**该连接最后一条数据（2.3 第 3 条）；我们的 `DcClosed` 已先 `DrainChannel` 再 raise（`DataChannelRuntime.cs:704-712`），天然合规 |

## 7. 没做的事

- **没上 Unity 实测。** 5.1 的帧内次序建立在两块实测数据上（本仓那份 PlayerLoop dump + FishNet 源码的执行序属性），但**「+1 帧」这个结论本身没有实测过**。方案定下来后应当在真机上量一次单向延迟。
- **`immediately: false` 是死路径**这一条是推断（依据：全仓两处调用点都传 `true`）。若哪天 FishNet 加了非 immediate 路径，3.5 那条结论要复核。
- **Multipass 组合场景没有验证。** 本文只确认了 Multipass 会重映射 id，没验证我们的 transport 塞进 Multipass 是否还成立。
- **没读 `LatencySimulator`**（`FN:Managing/Transporting/LatencySimulator.cs`，仅 `DEVELOPMENT` 下启用），它会插在 `Transport.SendToClient` 之前（`TransportManager.cs:696-700`）。推断：对我们透明，但开发期调试如果发现发送时序不对，先看它。




