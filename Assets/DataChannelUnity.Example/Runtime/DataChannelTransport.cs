// FishNet Transport，传输层走 datachannel-unity。
//
// 实现依据是 #115 的交付物 docs/research/fishnet-transport-contract.md（在
// research/fishnet-transport-contract 分支上，591 行，22 个成员逐条）。下面每处
// 「契约 x.y」都指那份文档的节号。
//
// **这一版是垂直切片第一步：单进程 host 模式。** server 与 client 同时起在同一个
// Transport 实例上（FishNet 的 host 就是 IsServerStarted && IsClientStarted，
// 契约 4.4），于是信令是同对象内的直接调用，不需要 #121 的信令服务器。第二步
// 才是两个进程 + wss 信令（#116 定的协议）。
//
// **代替 #119 / #120 暂取的决策，全部集中在 ProvisionalDecisions 区域**，每条都
// 写了理由与由谁最终定。它们是为了让垂直切片能跑，不是替那两张票做决定。
using System;
using System.Collections.Generic;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// 把 FishNet 的 22 个 Transport 成员落到 datachannel-unity 的 PeerConnection /
    /// DataChannel 上。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DataChannelTransport : Transport
    {
        #region ProvisionalDecisions

        // ── 归 #119 的，暂取 ────────────────────────────────────────────────
        //
        // 每条 peer 连接开 **两条** DataChannel，不做单通道复用加自己的头。
        // 理由：#116 已查证「第二条 DataChannel 只开新 SCTP 流、不触发重新协商」，
        // 所以两条的代价只是多一次 CreateDataChannel，换来 channelId 到通道的
        // 映射是恒等的、不用自己写头。最终由 #119 定。
        private const string ReliableLabel = "fishnet-reliable";
        private const string UnreliableLabel = "fishnet-unreliable";

        // Reliable 必须 Ordered=true —— 这一条**不是**暂取，是契约 4.1 的硬要求：
        // FishNet 把超 MTU 的分片强制改到 reliable（SPLIT_PACKET_CHANNELID），
        // 而分片重组依赖顺序。为性能把它翻成 false 会坏。
        private static DataChannelInit ReliableInit => new DataChannelInit
        {
            Ordered = true,
            Reliable = true,
        };

        // Unreliable 用「不可靠且不保序」。契约 4.1：FishNet 只承诺 Reliable 是
        // ordered reliable，对 Unreliable 的保序不作任何承诺，Tugboat 也落到
        // LiteNetLib 的 Unreliable（乱序到达）。所以这是合规的，不是偷懒。
        private static DataChannelInit UnreliableInit => new DataChannelInit
        {
            Ordered = false,
            Reliable = false,
            MaxRetransmits = 0,
        };

        // GetMTU 返回的固定常量。**#130 已实测定死为 1282**（不再是暂取）。
        //
        // 1282 = Tugboat 的值（1350 - 68），一开始是为了和它做 A/B —— 同一个打包粒度才让
        // 差异指向传输层而不是包大小。实测之后它有了独立的理由：
        //
        // **报大 MTU 的唯一动机是躲开 FishNet 的分片路径**（超 MTU 会被切片并强制挪到
        // reliable 有序，于是过期的球位置卡在重传后面）。而那条路径在 498 个 tick 里**一次
        // 都没被踩到**：峰值 192 字节/tick（TickRate 30）/ 188（60），离 1282 有 6.7 倍余量。
        // 所以那个动机在台球这个负载下没有实测支撑。
        //
        // **而报大是有代价的**：这个值有双重身份 —— 出站切片尺度 **＋ 入站踢人阈值**
        // （`FN:ServerManager.cs:735-742`，入站超 MTU 当场踢）。报大＝容忍更大的入站包，
        // 那是在没有收益的前提下放宽一道门。
        //
        // 契约 4.2 的四条硬约束仍然满足：① 连接建立前就被调用、拿不到协商值，所以这是静态
        // 常量不是协商结果；② 结果被永久缓存，运行期改不了；③ FishNet 再净扣 2 字节且扣完
        // <= 100 视为无效，1282 远在其上；④ 两档返回同值，绕开 SetLowestMTUs 里 allLowest
        // 那处从不更新的可疑逻辑。
        //
        // SCTP 自己会分片，所以这个值和 PeerConnectionConfig.Mtu 解耦 —— 它只是「给
        // FishNet 的打包尺度」，Synapse 的注释是直接先例。
        private const int MtuBytes = 1282;

        // ── 归 #120 的，暂取 ────────────────────────────────────────────────
        //
        // connectionId 从 0 起单调递增、**永不复用**。**#120 已定**（不再是暂取）。
        // 契约 4.3 的约束：必须 >= 0（ServerManager 会当场踢）、必须避开 int.MaxValue
        // （那是 SIMULATED_CLIENTID_VALUE）、存活期唯一；不要求连续/单调/从 0 起。
        //
        // 选永不复用是因为复用会让「拿着过期 id 去发」这类 bug 静默命中**另一个**
        // peer；不复用则查表失败，响亮。
        //
        // **代价已知并接受**（#120 → #134）：断线重连回来的是一个**新 id**，FishNet 眼里
        // 是新连接、新 NetworkConnection、新 owner。所以游戏层若要「接回原来那半边球」，
        // 必须自己有一个与 connectionId 解耦的身份（座位号 / 信令 peer id）。
        private int _nextConnectionId;

        // pump 对齐取契约 5.4 推荐的方案 D：在 IterateIncoming 里直接 Pump()，用
        // frameCount 闸住一帧一次。
        //
        // 相对方案 A（不动 pump、接受入站晚一帧）：D 是同帧交付，省掉 60fps 下那
        // 16.7ms 的纯增延迟 —— 对 tick 制的 FishNet 尤其值，输入晚一个 tick 是纯
        // 增延迟而非抖动，插值缓冲吸收不掉。
        // 相对方案 B（订阅 TimeManager.OnUpdate）：B 的失效模式是**静默**的 ——
        // 用户在 Inspector 把 _updateOrder 翻成 AfterTick 就退回晚一帧，而那是个
        // private [SerializeField]，我们读不到、连告警都发不出来。
        //
        // 代价（照 5.3 记下来，否则下一个人会当成 bug）：一帧 pump 两次，第二次是
        // PlayerLoop 尾部那次，几乎空转 —— 控制队列已空，每条通道一次
        // dcu_dc_receive 返回 NOT_AVAIL。且 DataChannelRuntime 的 _pumpTicks 每帧
        // +2，存活诊断的数字含义随之变化。
        //
        // **PlayerLoop 那个条目不能删**：非 FishNet 使用者要它，而且它是存活契约的
        // 兜底 —— 只靠 IterateIncoming 驱动的话，FishNet 一停 pump 就彻底停了，
        // CheckPumpLiveness 会在 5s 后误报「第三方抹掉了条目」并花掉那唯一一次重试。
        [SerializeField]
        [Tooltip("勾上＝方案 D：在 IterateIncoming 里 Pump()，同帧交付。取消＝方案 A：只靠 PlayerLoop 的 pump，入站晚一帧。")]
        private bool _pumpInsideIterateIncoming = true;

        // 台球是两人局，所以默认 2（#120）。**原先是 8，那个值会误导** —— 说 8 而实际
        // 只能玩 2。
        //
        // **这个值不拦任何东西，它只是如实转述。** #120 查明 FishNet 核心**从不读**
        // GetMaximumClients()：调用者只有各 transport 读自己 socket 的实现
        // （Multipass.cs:714、Tugboat.cs:365、Synapse.cs:261），ServerManager /
        // TransportManager 里一处都没有。Tugboat 是在自己的 ServerSocket 里靠 LiteNetLib
        // 拦的。
        //
        // 真正的拦在**房间层**（信令），用 #116 的 reject —— 见 OnSignalingDescription。
        // 拦在那一层就够：建 PeerConnection 必须过信令，绕不过去。
        [SerializeField]
        [Tooltip("host 支持的 client 数上限。台球是两人局故为 2。注意：这个值只是如实转述给 FishNet，真正的拦在信令层（满员回 reject）。")]
        private int _maximumClients = 2;
        #endregion

        #region State

        /// <summary>一条 peer 连接：一个 PeerConnection 加两条 DataChannel。</summary>
        private sealed class Peer
        {
            public int ConnectionId;
            public PeerConnection Pc;
            public DataChannel Reliable;
            public DataChannel Unreliable;

            /// <summary>
            /// 两条通道都 open 才算这条连接可用。**Started 必须等到这一刻才上报** ——
            /// 契约 2.4：不能在 Started raise 之前就让 SendToServer 丢数据。等两条都
            /// open 再报，FishNet 开始发时两条必然可写，于是不需要出站排队。
            /// </summary>
            public bool BothOpen => Reliable != null && Unreliable != null
                                    && Reliable.IsOpen && Unreliable.IsOpen;

            public bool StartedReported;

            public void Dispose()
            {
                Reliable?.Dispose();
                Unreliable?.Dispose();
                Pc?.Dispose();
                Reliable = null;
                Unreliable = null;
                Pc = null;
            }
        }

        /// <summary>入站一条消息。buffer 是拷贝 —— 见 EnqueueInbound 的说明。</summary>
        private struct Inbound
        {
            public int ConnectionId;
            public Channel Channel;
            public byte[] Buffer;
            public int Length;
        }

        // server 侧：connectionId → peer。host 的本地 client 也在这张表里占一个正常
        // 的 id（契约 4.4：host 的本地 client 是 Clients 集合里的普通
        // NetworkConnection，必须走完整的 Started 事件流程，不能短路）。
        private readonly Dictionary<int, Peer> _serverPeers = new Dictionary<int, Peer>();

        // client 侧：只有一条连到 host 的连接（契约 4.4 末：client 侧不需要 server
        // 能力，但 Iterate*(asServer: true) 在纯 client 上照样会被调，必须安全空转）。
        private Peer _clientPeer;

        // ── 信令（#128）──────────────────────────────────────────────────
        //
        // **两张表，别混**（#120）：#116 信封里的 from/to 是**信令层的 peer id**，不是
        // FishNet 的 connectionId。映射是 signalingPeerId → connectionId → Peer。
        private SignalingClient _signaling;
        private readonly Dictionary<string, int> _peerIdToConnectionId = new Dictionary<string, int>();
        private readonly Dictionary<int, string> _connectionIdToPeerId = new Dictionary<int, string>();

        /// <summary>host 侧建房后由服务器带回的 6 位房间码；client 侧是自己填的那个。</summary>
        public string RoomCode => _signaling?.RoomCode;

        /// <summary>信令是否已连上（诊断 HUD 用）。</summary>
        public bool SignalingConnected => _signaling != null && _signaling.IsConnected;

        [SerializeField]
        [Tooltip("client 要加入的房间码。host 侧留空 —— 房间码由服务器分配，见 RoomCode。")]
        private string _joinRoomCode = "";

        /// <summary>
        /// 运行时设房间码（房间 UI 用）。必须在 StartConnection(false) 之前调。
        /// </summary>
        public void SetJoinRoomCode(string code) => _joinRoomCode = code;

        private LocalConnectionState _serverState = LocalConnectionState.Stopped;
        private LocalConnectionState _clientState = LocalConnectionState.Stopped;

        // **门禁用这个，不用 _serverState。**
        //
        // _serverState 是「事件已投递」的状态：它要等 IterateIncoming 把队列排空才更
        // 新，也就是下一个 tick。而契约 3.5 明说 StartConnection 的返回值语义是「没有
        // 阻塞项」，不是「已连上」—— 所以 server 在 StartConnection(true) 返回 true 那
        // 一刻就算逻辑上起了。
        //
        // 拿 _serverState 做门禁会拒掉真正有效的启动：NetworkHudCanvases 的
        // AutoStartType.Host 在同一个 Start() 里**背靠背**调 ServerManager
        // .StartConnection() 与 ClientManager.StartConnection()（`NetworkHudCanvases
        // .cs:155-157`），中间没有任何 tick，于是 client 那次必然被误拒。
        //
        // 这与 #123 实测推翻 #118 的那处是**同一形状**：用事件缓存的状态当门禁，会拒掉
        // 真正有效的操作。那次在原生／托管之间，这次在适配层自己这一层。
        private bool _serverStartRequested;

        // 三个入站队列，分开是因为契约 2.3 把顺序写死了：先 local 连接状态，再
        // remote 连接状态，最后数据包。合到一个队列里就没法保证这个次序。
        private readonly Queue<LocalConnectionState> _pendingServerState = new Queue<LocalConnectionState>();
        private readonly Queue<LocalConnectionState> _pendingClientState = new Queue<LocalConnectionState>();
        private readonly Queue<RemoteConnectionStateArgs> _pendingRemoteState = new Queue<RemoteConnectionStateArgs>();
        private readonly Queue<Inbound> _inboundToServer = new Queue<Inbound>();
        private readonly Queue<Inbound> _inboundToClient = new Queue<Inbound>();

        // 收到的 span 只在回调期间有效，而 FishNet 要 ArraySegment —— 所以无论哪个
        // pump 方案都必须拷一次（契约 5.3 方案 A 的说明）。拷贝用的 buffer 在这里
        // 复用，避免每条消息一次 GC 分配。
        private readonly Stack<byte[]> _bufferPool = new Stack<byte[]>();

        private int _lastPumpFrame = -1;
        #endregion

        #region Events and Handle* forwarders

        public override event Action<ClientConnectionStateArgs> OnClientConnectionState;
        public override event Action<ServerConnectionStateArgs> OnServerConnectionState;
        public override event Action<RemoteConnectionStateArgs> OnRemoteConnectionState;
        public override event Action<ClientReceivedDataArgs> OnClientReceivedData;
        public override event Action<ServerReceivedDataArgs> OnServerReceivedData;

        // 这五个**不是**框架钩子（契约 2.1：FishNet 的任何 manager 都从不调它们，
        // Multipass 把五个全实现成空函数）。它们是「实现者自己调、用来 raise 自己那个
        // event」的转发器 —— 因为 abstract event 只能在声明类内 Invoke。照 Tugboat
        // 写成一行转发。
        public override void HandleClientConnectionState(ClientConnectionStateArgs args) => OnClientConnectionState?.Invoke(args);
        public override void HandleServerConnectionState(ServerConnectionStateArgs args) => OnServerConnectionState?.Invoke(args);
        public override void HandleRemoteConnectionState(RemoteConnectionStateArgs args) => OnRemoteConnectionState?.Invoke(args);
        public override void HandleClientReceivedDataArgs(ClientReceivedDataArgs args) => OnClientReceivedData?.Invoke(args);
        public override void HandleServerReceivedDataArgs(ServerReceivedDataArgs args) => OnServerReceivedData?.Invoke(args);
        #endregion

        #region Buffer pool

        private byte[] RentBuffer(int atLeast)
        {
            while (_bufferPool.Count > 0)
            {
                var candidate = _bufferPool.Pop();
                if (candidate.Length >= atLeast) return candidate;
            }
            // MTU 是上界：FishNet 自己分片，入站超 MTU 它会当场踢人（契约 4.2），
            // 所以正常流量不会超。仍按实际长度取上界，不假设。
            return new byte[Mathf.Max(atLeast, MtuBytes)];
        }

        private void ReturnBuffer(byte[] buffer)
        {
            if (buffer != null && _bufferPool.Count < 256) _bufferPool.Push(buffer);
        }
        #endregion

        #region Send

        // channelId 越界要容忍，不能抛。Tugboat 的做法是把 >= 2 的强制降成 reliable
        // 并 warning（契约 3.2）—— 照它,因为 reliable 是不丢数据的那一档，降级到它
        // 最坏是多花带宽，降到 unreliable 会丢。
        private static Channel SanitizeChannel(byte channelId)
        {
            return channelId == (byte)Channel.Unreliable ? Channel.Unreliable : Channel.Reliable;
        }

        /// <summary>
        /// 每条**成功送出**的出站消息触发一次：(asServer, connectionId, channel, bytes)。
        ///
        /// 这是量出站的唯一可靠位置。理由是本 Transport 的 IterateOutgoing 是空的
        /// （见那里的说明）—— Send* 里同步 P/Invoke 就出去了，所以「一个 tick 发了多少
        /// 字节」在别处都读不到：FishNet 的 IntermediateLayer 只给一个 toServer bool，
        /// 没有 channel 也没有 connectionId，而这两个正是要分的维度。
        ///
        /// 只在 dc.Send 没抛的路径上触发：抛了那条就是被丢的（见下面 catch），
        /// 计进去会把「丢了多少」记成「发了多少」。
        ///
        /// 纯诊断，不参与任何决策路径。#130 要定 GetMTU 与背压时量的就是这个。
        /// </summary>
        public event Action<bool, int, Channel, int> OutboundSent;

        /// <summary>
        /// 真正的发送。**不能抛** —— 契约 3.2：调用点在 IterateOutgoing 的双层循环里，
        /// 一次抛会把该帧剩余所有连接的发送全打断。而我们的 DataChannel.Send 在通道
        /// 未 open 时**会抛**（SendCore 刻意不预检 open 状态，交给原生失败后
        /// RequireOk 抛），所以这个 try/catch 是契约要求的，不是防御性冗余。
        /// </summary>
        private void SendOn(Peer peer, byte channelId, ArraySegment<byte> segment, bool asServer)
        {
            if (peer == null) return; // 未知 connectionId：静默丢（契约 3.2）

            var channel = SanitizeChannel(channelId);
            var dc = channel == Channel.Unreliable ? peer.Unreliable : peer.Reliable;
            if (dc == null) return;

            try
            {
                // 零长度要容忍（契约 3.2）；我们的 Send 显式支持零长，直接透传。
                // segment.Array 可能带 offset，用带 offset 的重载，不额外拷 ——
                // DataChannel.Send 会把字节拷进原生，返回后 segment 复用是安全的。
                dc.Send(segment.Array, segment.Offset, segment.Count);

                // 订阅者抛异常不能打断发送循环（同上：契约 3.2）。
                try
                {
                    OutboundSent?.Invoke(asServer, peer.ConnectionId, channel, segment.Count);
                }
                catch (Exception e)
                {
                    DataChannelLogOnce($"OutboundSent 订阅者抛出，已忽略：{e.GetType().Name}: {e.Message}");
                }
            }
            catch (Exception e)
            {
                // 出站背压/失败语义：**丢弃 + 一条日志**，暂取。
                // FishNet 的 Send* 是 void，没有「发不出去」的返回值，所以只能排队或
                // 丢（#119 第 4 问）。这一版选丢：排队要定上限与溢出策略，而那正是
                // #119 要定的东西，先不用实现替它做决定。
                //
                // BufferedAmount 是现成的背压读数（契约 6 表），真要排队时从它入手。
                DataChannelLogOnce($"发送失败（connectionId={peer.ConnectionId} channel={channel}），本条丢弃：{e.GetType().Name}: {e.Message}");
            }
        }

        private string _lastLoggedSendError;

        private void DataChannelLogOnce(string message)
        {
            // 同一条错误只报一次，否则每帧每连接一条会把 Console 淹掉，反而看不见
            // 第一次发生了什么。
            if (_lastLoggedSendError == message) return;
            _lastLoggedSendError = message;
            Debug.LogWarning($"[DataChannelTransport] {message}");
        }

        public override void SendToServer(byte channelId, ArraySegment<byte> segment)
        {
            SendOn(_clientPeer, channelId, segment, asServer: false);
        }

        public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        {
            // -1 是广播语义（Tugboat 见 UNSET_CLIENTID_VALUE 就 SendToAll）。正常路径
            // 上 TransportManager 总带真实 id，所以这里只要**确保不把 -1 当成一个真
            // 连接去查表**即可（契约 3.2）。
            if (connectionId < 0) return;
            _serverPeers.TryGetValue(connectionId, out var peer);
            SendOn(peer, channelId, segment, asServer: true);
        }
        #endregion

        #region Iterate

        public override void IterateIncoming(bool asServer)
        {
            // 一帧只 pump 一次。IterateIncoming 会被紧邻调两次（先 asServer: true 再
            // false，契约 1.3），闸在这里而不是只在 true 那次，是为了纯 client 上也
            // 能触发 —— 那种情况下 true 那次照样被调，但 client 侧的桶要在 false 那
            // 次才排空，两次都在同一帧内，所以仍是同帧交付。
            if (_pumpInsideIterateIncoming && Time.frameCount != _lastPumpFrame)
            {
                _lastPumpFrame = Time.frameCount;
                try
                {
                    // pump 是全局的：一次会把 server 侧和 client 侧的消息都拉出来，
                    // 所以必须先入桶、再按 asServer 分别排空（契约 5.3 方案 D）。
                    DataChannelRuntime.Pump();
                }
                catch (Exception e)
                {
                    // pump 内部会 raise 用户回调（包括与 FishNet 无关的通道），
                    // SafeDispatch 已兜住订阅者的异常，但 pump 自身若抛就会打断
                    // FishNet 的 tick。不能让它传出去。
                    Debug.LogError($"[DataChannelTransport] Pump 抛出，本帧入站可能不完整：{e}");
                }
            }

            // 顺序由契约 2.3 写死：local 连接状态 → remote 连接状态 → 数据包。
            if (asServer)
            {
                while (_pendingServerState.Count > 0)
                {
                    var state = _pendingServerState.Dequeue();
                    _serverState = state;
                    HandleServerConnectionState(new ServerConnectionStateArgs(state, Index));
                }

                while (_pendingRemoteState.Count > 0)
                    HandleRemoteConnectionState(_pendingRemoteState.Dequeue());

                while (_inboundToServer.Count > 0)
                {
                    var msg = _inboundToServer.Dequeue();
                    HandleServerReceivedDataArgs(new ServerReceivedDataArgs(
                        new ArraySegment<byte>(msg.Buffer, 0, msg.Length),
                        msg.Channel, msg.ConnectionId, Index));
                    // 事件返回即可回收（契约 2.5）。
                    ReturnBuffer(msg.Buffer);
                }
            }
            else
            {
                while (_pendingClientState.Count > 0)
                {
                    var state = _pendingClientState.Dequeue();
                    _clientState = state;
                    HandleClientConnectionState(new ClientConnectionStateArgs(state, Index));
                }

                while (_inboundToClient.Count > 0)
                {
                    var msg = _inboundToClient.Dequeue();
                    HandleClientReceivedDataArgs(new ClientReceivedDataArgs(
                        new ArraySegment<byte>(msg.Buffer, 0, msg.Length), msg.Channel, Index));
                    ReturnBuffer(msg.Buffer);
                }
            }
        }

        /// <summary>
        /// 每个 tick 冲刷完之后触发一次（参数是 asServer）。**这是采背压读数的正确位置**：
        /// 此刻本 tick 该发的全发完了，所以 BufferedAmount 报的是「一个 tick 之后还积着
        /// 多少」，正是 #130 要的那条曲线。
        ///
        /// 不用 `TimeManager.OnPostTick` 采：它在 `TryIterateData(false)` **之前**
        /// （`TimeManager.cs:751-766`），采到的是冲刷前的值。
        /// </summary>
        public event Action<bool> OutboundFlushed;

        /// <summary>
        /// 空实现（除了那个诊断钩子），**这是有意的**。契约 1.4：Send* 是「入队」、
        /// IterateOutgoing 是「冲刷」，但我们的 DataChannel.Send 是同步 P/Invoke，在
        /// Send* 里就已经出去了（契约 5.2：出站没有对齐问题）。所以没有要冲刷的东西。
        ///
        /// 代价是放弃了「一帧内合并/限流」的唯一位置 —— 那归 #119/#130 的背压决策，真要
        /// 做时把 Send* 改成入队、在这里冲刷即可，结构已经留好。
        /// </summary>
        public override void IterateOutgoing(bool asServer)
        {
            // server 侧才查：积压是「host 发不出去」的症状，而 client 侧那条上行只有出杆与
            // ping，量级差两个数量级（#131：出杆五个 float，一回合一次）。
            if (asServer)
                CheckBacklog();

            try
            {
                OutboundFlushed?.Invoke(asServer);
            }
            catch (Exception e)
            {
                DataChannelLogOnce($"OutboundFlushed 订阅者抛出，已忽略：{e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// 一条连接某一档通道的出站积压字节数，读的是上游 SCTP 的发送队列。
        ///
        /// 这是**唯一**能读到积压的地方：#130 已查明上游发送队列 `limit = 0`（不限长）、
        /// `send()` 从不失败，所以「发不出去」永远不会以错误的形式出现，只会以这个数一直
        /// 涨的形式出现。
        ///
        /// 返回 false 而不抛：调用点是诊断，而 `BufferedAmount` 在通道已 dispose 时会抛
        /// （`DataChannel.cs:112`），关闭过程中采样是正常的，不该把它变成异常。
        /// </summary>
        public bool TryGetBufferedAmount(bool asServer, int connectionId, Channel channel, out int bytes)
        {
            bytes = 0;

            Peer peer;
            if (asServer)
            {
                if (!_serverPeers.TryGetValue(connectionId, out peer)) return false;
            }
            else
            {
                peer = _clientPeer;
                if (peer == null) return false;
            }

            var dc = channel == Channel.Unreliable ? peer.Unreliable : peer.Reliable;
            if (dc == null || !dc.IsOpen) return false;

            try
            {
                bytes = dc.BufferedAmount;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>server 侧当前的 connectionId 列表（诊断用；分配的顺序即加入顺序）。</summary>
        public IEnumerable<int> ServerConnectionIds => _serverPeers.Keys;

        // ── 背压看守（#130 定）─────────────────────────────────────────────
        //
        // **只报，不丢、不断。** 判据是：上游发送队列 `limit = 0` 不限长、`send()` 从不
        // 失败，所以「发不出去」永远不会以错误的形式出现 —— 它只会以 BufferedAmount 一直
        // 涨的形式出现，而那是**没有症状的**。这条看守把一个无症状的失败变成有症状的。
        //
        // 为什么不丢：丢 unreliable 在语义上合法，但**触发点无法在本机验证** —— loopback
        // 没有瓶颈（结构上产生不出背压：它不过信令，拿不到 TURN 凭据，RelayOnly 走不通），
        // 所以任何丢弃阈值都只能靠推。#113 Notes 那条「阈值不许凭空猜」正是为此立的，一版
        // 凭空造的 64 KB/1 MB 已因此撤回。**报一条日志猜错了只是噪音，丢一条猜错了是丢数据。**
        //
        // 丢 reliable 从来不在选项内：FishNet 靠它送 spawn/despawn/SyncType，分片也在这条
        // 上（超 MTU 会被强制挪过来）。真要处理只能是断这条连接，而那个动作归 #120 的生命
        // 周期，不归这里。
        private const int BacklogWarnBytes = 12 * 1024;

        // 一条连接一档通道只报一次，排空后复位 —— 否则每 tick 一条会把 Console 淹掉，而
        // 淹掉之后就看不见第一次是什么时候开始的。复位是为了让**再次**发生时还会喊。
        private readonly HashSet<(int, Channel)> _backlogReported = new HashSet<(int, Channel)>();

        /// <summary>
        /// 每 tick 冲刷完之后查一遍积压。**采样点必须在冲刷之后** —— 冲刷之前采到的是上一
        /// 个 tick 的残留，那个数不回答「这个 tick 之后还积着多少」。
        ///
        /// 阈值 12 KiB 的来历：实测峰值 188 字节/tick @ TickRate 60 ＝ 约 11.3 KB/s，所以
        /// 12 KiB **约等于一整秒的峰值流量**。对一条载着球位置的 unreliable 通道来说，落后
        /// 一秒意味着积压里的数据全部过期了 16 倍以上（60Hz 下一个 tick 是 16 ms）——
        /// 换句话说，这个数不是「多少字节算多」，而是「链路已经落后一整秒」。
        /// </summary>
        private void CheckBacklog()
        {
            foreach (var kv in _serverPeers)
            {
                CheckOne(kv.Key, Channel.Unreliable, kv.Value.Unreliable);
                CheckOne(kv.Key, Channel.Reliable, kv.Value.Reliable);
            }

            void CheckOne(int connectionId, Channel channel, DataChannel dc)
            {
                if (dc == null || !dc.IsOpen) return;

                int buffered;
                try
                {
                    buffered = dc.BufferedAmount;
                }
                catch (Exception)
                {
                    // 关闭过程中采样是正常的，不该把它变成异常（BufferedAmount 在已 dispose
                    // 时会抛）。
                    return;
                }

                var key = (connectionId, channel);
                if (buffered < BacklogWarnBytes)
                {
                    _backlogReported.Remove(key);
                    return;
                }

                if (!_backlogReported.Add(key)) return;

                Debug.LogError(
                    $"[DataChannelTransport] 出站积压 {buffered} 字节（connectionId={connectionId} " +
                    $"channel={channel}），已超过约一秒的峰值流量（{BacklogWarnBytes} 字节）。" +
                    "上游发送队列不限长且 send() 从不失败，所以这个数只会继续涨 —— " +
                    "链路正在落后，而不是某一条消息出了问题。本条只报不丢（#130）。");
            }
        }
        #endregion

        #region Inbound plumbing

        /// <summary>
        /// 把回调里的 span 拷进桶。**拷贝不是额外代价** —— MessageReceived 交的是
        /// ReadOnlySpan&lt;byte&gt;，只在回调期间有效，而 FishNet 要 ArraySegment，所以
        /// 无论哪个 pump 方案都必须拷一次（契约 5.3）。
        /// </summary>
        private void EnqueueInbound(Queue<Inbound> queue, int connectionId, Channel channel, ReadOnlySpan<byte> data)
        {
            var buffer = RentBuffer(data.Length);
            data.CopyTo(buffer);
            queue.Enqueue(new Inbound
            {
                ConnectionId = connectionId,
                Channel = channel,
                Buffer = buffer,
                Length = data.Length,
            });
        }

        /// <summary>
        /// 被动接到一条通道（answerer 侧）：按 label 归档到 reliable / unreliable。
        ///
        /// ## label 严格匹配，认不出来就关掉（#119）
        ///
        /// 原先是 `if (label == UnreliableLabel) … else → Reliable`，即**任何不认识的
        /// label 都被静默当成 reliable**。单进程下两端是同一份代码，看不出来；两进程之后
        /// （#128）版本不一致的对端会被静默映射到错误的档 —— 一条 unreliable 流被当
        /// reliable 用，症状是「偶尔卡顿」，查起来极贵。所以两个都不匹配就记 Error 并关掉
        /// 那条通道，响亮地失败。
        ///
        /// label 字符串本身就是版本标记：语义变了就换 label，旧版本自然对不上、自然响亮，
        /// 不需要额外的 -v1 后缀。
        /// </summary>
        private void AttachReceivedChannel(Peer peer, DataChannel dc)
        {
            if (dc.Label == ReliableLabel)
            {
                peer.Reliable = dc;
                WireChannel(peer, dc, Channel.Reliable, asServer: true);
            }
            else if (dc.Label == UnreliableLabel)
            {
                peer.Unreliable = dc;
                WireChannel(peer, dc, Channel.Unreliable, asServer: true);
            }
            else
            {
                Debug.LogError(
                    $"[DataChannelTransport] 对端开了一条 label 认不出来的通道：\"{dc.Label}\"。" +
                    $"只接受 \"{ReliableLabel}\" 与 \"{UnreliableLabel}\" —— 大概是两端版本不一致。" +
                    "这条通道被关掉，不猜它是哪一档（猜错的症状是偶尔卡顿，比连不上难查得多）。");
                dc.Dispose(); // dcu_dc_destroy 内部先 close()，所以对端会收到关闭
                return;
            }

            // 被动接到的通道可能**已经**是 open 的（Opened 事件可能早于我们订阅），
            // 所以这里补判一次，否则 Started 永远不会上报。
            OnPeerChannelOpened(peer, asServer: true);
        }

        private void WireChannel(Peer peer, DataChannel dc, Channel channel, bool asServer)
        {
            dc.MessageReceived += (ReadOnlySpan<byte> data) =>
            {
                EnqueueInbound(asServer ? _inboundToServer : _inboundToClient,
                    peer.ConnectionId, channel, data);
            };

            dc.Opened += () => OnPeerChannelOpened(peer, asServer);

            dc.Closed += () => OnPeerChannelClosed(peer, asServer);
        }

        /// <summary>
        /// 两条通道都 open 才上报 Started。契约 2.4：不能在 Started raise 之前就让
        /// SendToServer 丢数据 —— 等两条都 open 再报，FishNet 开始发时两条必然可写。
        /// </summary>
        private void OnPeerChannelOpened(Peer peer, bool asServer)
        {
            if (peer.StartedReported || !peer.BothOpen) return;
            peer.StartedReported = true;

            if (asServer)
            {
                // server 侧：这条 peer 变成一个可用的 remote 连接。
                _pendingRemoteState.Enqueue(new RemoteConnectionStateArgs(
                    RemoteConnectionState.Started, peer.ConnectionId, Index));
            }
            else
            {
                _pendingClientState.Enqueue(LocalConnectionState.Started);
            }
        }

        /// <summary>
        /// 通道关闭 —— **两条通道任意一条**关闭都走这里（#120）。
        ///
        /// ## 这里必须自己清理，没人替我们做
        ///
        /// `_serverPeers.Remove` 原先全仓只有一个调用点：`StopConnection(int, bool)`。
        /// 而 FishNet **只在自己发起断开时**才回调那个方法（`TransportManager.cs:716-740`，
        /// 延迟 max(100ms, 2 ticks) 后传 true）；对**已经自己掉线**的连接它不回调 ——
        /// 它只清掉自己的 NetworkConnection。
        ///
        /// 于是网络掉线那条路上，Peer 连同 PeerConnection 与两条 DataChannel 的**原生
        /// 句柄留在表里没人释放**，直到 Shutdown。包里的 LeakTracker 会给这个记账。
        /// 这是 #120 查出来的泄漏，修在这里。
        ///
        /// ## 清理与上报解耦
        ///
        /// 旧代码开头是 `if (!peer.StartedReported) return;` —— 那让**第二条泄漏路径**
        /// 存在：ICE 在建立中途失败时通道会关而 Started 从未报过，于是直接 return，
        /// 表里那行连同 PC 一起留下。所以现在**清理无条件做，上报才看 StartedReported**。
        /// </summary>
        private void OnPeerChannelClosed(Peer peer, bool asServer)
        {
            var wasStarted = peer.StartedReported;
            peer.StartedReported = false;

            if (asServer)
            {
                // 先上报再 Dispose：Stopped 必须**后于**该连接最后一条数据（契约 2.3
                // 第 3 条）。我们的 DcClosed 已经先 DrainChannel 再 raise，所以那条消息
                // 已经在 _inboundToServer 里排在这个状态事件之前 —— 顺序天然合规。
                if (wasStarted)
                    _pendingRemoteState.Enqueue(new RemoteConnectionStateArgs(
                        RemoteConnectionState.Stopped, peer.ConnectionId, Index));

                // 幂等：StopConnection(int,bool) 也会删同一行，两条路都要能删。
                // Remove 对不存在的键返回 false，Dispose 自己也是幂等的（字段置 null）。
                if (_serverPeers.Remove(peer.ConnectionId))
                    peer.Dispose();
            }
            else
            {
                if (wasStarted)
                {
                    _pendingClientState.Enqueue(LocalConnectionState.Stopping);
                    _pendingClientState.Enqueue(LocalConnectionState.Stopped);
                }

                // 本地 client 那条：清引用并释放。host 模式下这条 peer 的**对端**
                // （_serverPeers 里那行）由上面 asServer 的分支各自清 —— 两个 PC 是
                // 独立对象，各自的 Closed 各走一次。
                if (ReferenceEquals(_clientPeer, peer))
                {
                    _clientPeer = null;
                    peer.Dispose();
                }
            }

            // 信令侧的映射也要跟着掉，否则 peerId → connectionId 会指向一个已删的行。
            ForgetSignalingMapping(peer.ConnectionId);
        }
        #endregion

        #region Start / Stop

        [SerializeField]
        [Tooltip("ICE 服务器。垂直切片第一步（单进程 host）留空即可 —— 两端都在本机，host 候选就能连通。")]
        private List<string> _iceServerUrls = new List<string>();

        [SerializeField]
        [Tooltip("勾上＝强制走 TURN 中继（IceTransportPolicy.RelayOnly）。同机两端永远是 Direct，靠这个开关才能在不换网络的前提下验到 Relayed 那条分支。")]
        private bool _forceRelay;

        private PeerConnectionConfig BuildConfig()
        {
            var cfg = new PeerConnectionConfig
            {
                TransportPolicy = _forceRelay ? IceTransportPolicy.RelayOnly : IceTransportPolicy.All,
            };
            foreach (var url in _iceServerUrls)
                if (!string.IsNullOrWhiteSpace(url)) cfg.IceServers.Add(new IceServer(url));
            return cfg;
        }

        /// <summary>
        /// 用**服务器下发的** iceServers 建配置（#128）。
        ///
        /// 这些凭据是 #117 定的时限 HMAC，由信令服务器每次建/进房重新签发 —— 客户端一个
        /// 秘密都不持有。所以它们**只能**从这里来，不能进 Inspector：`IceServer` 刻意没有
        /// `[Serializable]`，正是为了不让凭据落进 .unity / .prefab 随包发出去。
        ///
        /// Inspector 里的 `_iceServerUrls` 仍然叠加进来，方便本地调试塞一个自己的 STUN；
        /// 两者不冲突，`IceServers` 是个列表。
        /// </summary>
        private PeerConnectionConfig BuildConfig(List<IceServer> fromSignaling)
        {
            var cfg = BuildConfig();
            if (fromSignaling != null)
                foreach (var s in fromSignaling) cfg.IceServers.Add(s);
            return cfg;
        }

        public override bool StartConnection(bool server)
        {
            // 返回值语义是「没有阻塞项」，不是「已连上」（契约 3.5）。
            if (server) return StartServer();
            return StartClient();
        }

        /// <summary>
        /// 起 server：连信令并建房（#128）。
        ///
        /// **Started 仍然立刻入队，不等 room-created。** server 侧的 Started 语义是「开始
        /// 接受连接」（契约 3.5：返回值是「没有阻塞项」，不是「已连上」），而这时确实没有
        /// 阻塞项 —— 房间码晚几十毫秒到，期间也没有 client 能来。若信令连不上，会经
        /// OnSignalingFailed 如实报 Stopped。
        /// </summary>
        private bool StartServer()
        {
            if (_serverStartRequested) return false; // 幂等（契约 3.5）

            // 同步置位，**先于**事件投递 —— 见 _serverStartRequested 的注释。
            _serverStartRequested = true;
            _pendingServerState.Enqueue(LocalConnectionState.Starting);

            if (!EnsureSignaling(joinCode: null)) // null = 建房
            {
                _serverStartRequested = false;
                _pendingServerState.Enqueue(LocalConnectionState.Stopped);
                return false;
            }

            _pendingServerState.Enqueue(LocalConnectionState.Started);
            return true;
        }

        /// <summary>
        /// 连信令。<paramref name="joinCode"/> 为 null 表示建房（host），否则进房（client）。
        /// 已连上时直接复用 —— host 模式下 server 与 client 共用**同一条**信令连接。
        /// </summary>
        private bool EnsureSignaling(string joinCode)
        {
            if (_signaling != null) return true;

            string url;
            try
            {
                url = SignalingConfig.Load().signalingUrl;
            }
            catch (Exception e)
            {
                // 缺配置就响亮失败（CONTRIBUTING 的「让缺失变成失败」）—— 不给默认地址。
                Debug.LogError($"[DataChannelTransport] 读不到信令配置，无法起连接：{e.Message}");
                return false;
            }

            _signaling = new SignalingClient();
            _signaling.RoomCreated += OnSignalingRoomCreated;
            _signaling.Joined += OnSignalingJoined;
            _signaling.DescriptionReceived += OnSignalingDescription;
            _signaling.CandidateReceived += OnSignalingCandidate;
            _signaling.PeerLeft += OnSignalingPeerLeft;
            _signaling.RoomClosed += OnSignalingRoomClosed;
            _signaling.Failed += OnSignalingFailed;

            if (joinCode == null) _signaling.ConnectAndCreateRoom(url);
            else _signaling.ConnectAndJoinRoom(url, joinCode);
            return true;
        }

        /// <summary>
        /// 垂直切片第一步的 client：**同进程 host 模式**。
        ///
        /// FishNet 的 host 就是 server 与 client 同时起（契约 4.4），两端都在这个
        /// Transport 实例里，所以信令是同对象内的直接调用 —— 和包自己的双端环回测试
        /// 同构，不需要 #121 的信令服务器。
        ///
        /// 契约 4.4 要求 host 的本地 client 走**真 loopback**：它必须占一个正常的
        /// connectionId 并走完整的 Started 流程，否则要伪造 remote 连接事件，且
        /// GetConnectionState / GetConnectionAddress 都得为它开分支。
        /// </summary>
        private bool StartClient()
        {
            if (_clientPeer != null) return false; // 幂等，且不看事件缓存的 _clientState

            _pendingClientState.Enqueue(LocalConnectionState.Starting);

            // **两条路（#120 / #128）**：
            //
            // - 本地有 server 在跑 → host 模式，本地 client 走**进程内 loopback**。#120 定
            //   了保留真 loopback PC：短路能省的量算出来只有约 24 KB/s 过 DTLS，不值；而
            //   loopback 与远端走同一条代码路径，正是这个 example 的主要价值。
            //   loopback **不过 wss** —— 让它绕一趟服务器毫无意义，服务端还得转给自己。
            // - 本地没有 server → 纯 client，走 wss 进房。
            if (_serverStartRequested) return StartLocalLoopbackClient();
            return StartRemoteClient();
        }

        /// <summary>
        /// host 模式的本地 client：进程内 loopback，两个 PeerConnection 直接对接。
        ///
        /// 契约 4.4 要求它走**真 loopback**：必须占一个正常的 connectionId 并走完整的
        /// Started 流程，否则要伪造 remote 连接事件，且 GetConnectionState /
        /// GetConnectionAddress 都得为它开分支。
        /// </summary>
        private bool StartLocalLoopbackClient()
        {
            var connectionId = _nextConnectionId++;
            var cfg = BuildConfig();

            // 两个 PeerConnection：serverSide 是 host 眼里的这条 client 连接，
            // clientSide 是本地 client 自己那条。
            var serverSide = new Peer { ConnectionId = connectionId, Pc = new PeerConnection(cfg) };
            var clientSide = new Peer { ConnectionId = connectionId, Pc = new PeerConnection(cfg) };

            _serverPeers[connectionId] = serverSide;
            _clientPeer = clientSide;

            // 进程内信令：把两端的 description / candidate 直接对接。
            clientSide.Pc.LocalDescriptionGenerated += (sdp, type) => serverSide.Pc.SetRemoteDescription(sdp, type);
            serverSide.Pc.LocalDescriptionGenerated += (sdp, type) => clientSide.Pc.SetRemoteDescription(sdp, type);
            clientSide.Pc.LocalCandidateGenerated += (cand, mid) => serverSide.Pc.AddRemoteCandidate(cand, mid);
            serverSide.Pc.LocalCandidateGenerated += (cand, mid) => clientSide.Pc.AddRemoteCandidate(cand, mid);

            // server 侧被动接收两条通道。label 决定它是哪一档 —— 这也是选两条通道
            // 而非单通道复用的好处：映射是恒等的，不用自己写头。
            serverSide.Pc.DataChannelReceived += dc => AttachReceivedChannel(serverSide, dc);

            // client 侧主动开两条。#116 已查证第二条只开新 SCTP 流、不触发重新协商，
            // 所以两条只需一次 offer/answer。
            clientSide.Reliable = clientSide.Pc.CreateDataChannel(ReliableLabel, ReliableInit);
            WireChannel(clientSide, clientSide.Reliable, Channel.Reliable, asServer: false);
            clientSide.Unreliable = clientSide.Pc.CreateDataChannel(UnreliableLabel, UnreliableInit);
            WireChannel(clientSide, clientSide.Unreliable, Channel.Unreliable, asServer: false);

            return true;
        }

        /// <summary>
        /// 纯 client：经 wss 进房（#128）。
        ///
        /// **这里只连信令，不建 PeerConnection** —— PC 要等 `joined` 到达，因为那条消息
        /// 才带来两样必需的东西：host 的 peerId（发 offer 得知道发给谁）与服务器签发的
        /// iceServers（#117 的时限 TURN 凭据）。建完 PC 之后 client 当 offerer（#116）。
        /// </summary>
        private bool StartRemoteClient()
        {
            if (string.IsNullOrWhiteSpace(_joinRoomCode))
            {
                // 响亮失败，不静默连不上（CONTRIBUTING 的「让缺失变成失败」）。
                Debug.LogError("[DataChannelTransport] 要进房必须先给房间码：" +
                               "在 Inspector 填 _joinRoomCode，或运行时调 SetJoinRoomCode()。");
                _pendingClientState.Enqueue(LocalConnectionState.Stopped);
                return false;
            }

            if (!EnsureSignaling(_joinRoomCode.Trim()))
            {
                _pendingClientState.Enqueue(LocalConnectionState.Stopped);
                return false;
            }
            return true;
        }
        #endregion

        #region Signaling handlers

        /// <summary>host：房间建好了。房间码这时才可读 —— 房间 UI 要显示它。</summary>
        private void OnSignalingRoomCreated()
        {
            Debug.Log($"[DataChannelTransport] 房间已建立：{_signaling.RoomCode}（把这个码给对手）");
        }

        /// <summary>
        /// client：进房成功。**现在才建 PC 并当 offerer**（#116：client 是 offerer）。
        /// </summary>
        private void OnSignalingJoined()
        {
            var hostPeerId = _signaling.HostPeerId;
            if (string.IsNullOrEmpty(hostPeerId))
            {
                Debug.LogError("[DataChannelTransport] joined 没带 hostPeerId，无处发 offer。");
                _pendingClientState.Enqueue(LocalConnectionState.Stopped);
                return;
            }

            // 纯 client 只有一条连接。它在自己眼里的 connectionId 不来自服务器 ——
            // 用固定的 LocalClientConnectionId：client 侧的入站事件走 _inboundToClient，
            // FishNet 的 client 面不看 connectionId（契约 3.3 只有 server 侧用它）。
            var peer = new Peer
            {
                ConnectionId = LocalClientConnectionId,
                Pc = new PeerConnection(BuildConfig(_signaling.IceServers)),
            };
            _clientPeer = peer;
            _connectionIdToPeerId[LocalClientConnectionId] = hostPeerId;
            _peerIdToConnectionId[hostPeerId] = LocalClientConnectionId;

            // 出站信令：本地生成的 description / candidate 发给 host。
            peer.Pc.LocalDescriptionGenerated += (sdp, type) => _signaling.SendDescription(hostPeerId, sdp, type);
            peer.Pc.LocalCandidateGenerated += (cand, mid) => _signaling.SendCandidate(hostPeerId, cand, mid);

            // 当 offerer：建两条通道，这会触发 LocalDescriptionGenerated 发出 offer。
            // 两条只需一次 offer/answer —— #116 已查证第二条只开新 SCTP 流。
            peer.Reliable = peer.Pc.CreateDataChannel(ReliableLabel, ReliableInit);
            WireChannel(peer, peer.Reliable, Channel.Reliable, asServer: false);
            peer.Unreliable = peer.Pc.CreateDataChannel(UnreliableLabel, UnreliableInit);
            WireChannel(peer, peer.Unreliable, Channel.Unreliable, asServer: false);

            Debug.Log($"[DataChannelTransport] 已进房 {_signaling.RoomCode}，向 host {hostPeerId} 发 offer");
        }

        /// <summary>
        /// 收到 description。
        ///
        /// ## host 侧：这是「有新 client 来了」的**唯一**信号
        ///
        /// 服务端**不通知 host 有人进房** —— `join-room` 只回 `joined` 给进房的那个
        /// （server.py:139-153），没有 peer-joined 这种消息。所以 host 第一次知道某个
        /// client 存在，就是收到它的 offer 那一刻。这恰好是 #120 定的「信令出现即建行」，
        /// 也与 #116 让 client 当 offerer 对上。
        /// </summary>
        private void OnSignalingDescription(string from, string sdp, string sdpType)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(sdp)) return;

            // client 侧：这是 host 的 answer。
            if (_clientPeer != null && _connectionIdToPeerId.TryGetValue(LocalClientConnectionId, out var hostId)
                && from == hostId)
            {
                _clientPeer.Pc.SetRemoteDescription(sdp, sdpType);
                return;
            }

            if (!_serverStartRequested) return; // 不是 host，也不是给我们的

            // host 侧：认识的 peer → 直接喂给它的 PC（重发的 offer 等）。
            if (_peerIdToConnectionId.TryGetValue(from, out var existingId)
                && _serverPeers.TryGetValue(existingId, out var existing))
            {
                existing.Pc.SetRemoteDescription(sdp, sdpType);
                return;
            }

            // host 侧：陌生 peer = 新 client。**满员就在这里拦**（#120：上限归房间层）。
            // _serverPeers 含 host 自己的本地 loopback client，所以它天然算在人数里。
            if (_serverPeers.Count >= _maximumClients)
            {
                _signaling.SendReject(from, "room-full");
                Debug.Log($"[DataChannelTransport] 房间已满（{_serverPeers.Count}/{_maximumClients}），拒绝 {from}");
                return;
            }

            var connectionId = _nextConnectionId++;
            var peer = new Peer
            {
                ConnectionId = connectionId,
                Pc = new PeerConnection(BuildConfig(_signaling.IceServers)),
            };
            _serverPeers[connectionId] = peer;
            _peerIdToConnectionId[from] = connectionId;
            _connectionIdToPeerId[connectionId] = from;

            peer.Pc.LocalDescriptionGenerated += (s, t) => _signaling.SendDescription(from, s, t);
            peer.Pc.LocalCandidateGenerated += (c, m) => _signaling.SendCandidate(from, c, m);
            // host 是 answerer：被动接两条通道，不自己建。
            peer.Pc.DataChannelReceived += dc => AttachReceivedChannel(peer, dc);

            // 喂 offer —— 这会触发 answer 生成并经上面的订阅发回去。
            peer.Pc.SetRemoteDescription(sdp, sdpType);
            Debug.Log($"[DataChannelTransport] 新 client {from} → connectionId {connectionId}，已回 answer");
        }

        /// <summary>
        /// 收到 candidate。**必然晚于该 peer 的 description** —— 服务端一个连接一个协程
        /// 保住 per-sender FIFO，我们这侧一条接收循环 + 主线程单点排空保住它。所以这里
        /// 不需要自己缓存乱序到达的 candidate。
        /// </summary>
        private void OnSignalingCandidate(string from, string candidate, string mid)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(candidate)) return;

            if (_clientPeer != null && _connectionIdToPeerId.TryGetValue(LocalClientConnectionId, out var hostId)
                && from == hostId)
            {
                _clientPeer.Pc.AddRemoteCandidate(candidate, mid);
                return;
            }

            if (_peerIdToConnectionId.TryGetValue(from, out var id)
                && _serverPeers.TryGetValue(id, out var peer))
            {
                peer.Pc.AddRemoteCandidate(candidate, mid);
                return;
            }
            // 找不到就静默丢：正常的断开竞态里会走到这里（对端刚被清掉，它的 candidate
            // 还在路上）。报错无从补救，只会刷屏。
        }

        /// <summary>
        /// host：某个 client 的**信令**连接断了。
        ///
        /// **信令断 ≠ 掉线**（#116）。P2P 若还活着，这一局照常打下去 —— 真正的掉线判据是
        /// DataChannel 关闭，那条路走 OnPeerChannelClosed。所以这里只清信令侧的映射，
        /// **不动 _serverPeers、不报 Stopped**。
        /// </summary>
        private void OnSignalingPeerLeft(string peerId)
        {
            if (string.IsNullOrEmpty(peerId)) return;
            if (_peerIdToConnectionId.TryGetValue(peerId, out var id))
            {
                _peerIdToConnectionId.Remove(peerId);
                _connectionIdToPeerId.Remove(id);
                Debug.Log($"[DataChannelTransport] client {peerId}（connectionId {id}）的信令断了。" +
                          "P2P 若仍连通则游戏继续 —— 掉线以 DataChannel 关闭为准。");
            }
        }

        /// <summary>
        /// client：房间没了（host 走了）。FishNet 4.7.2 **没有 host migration**（#113 的
        /// Out of scope 已查证），局面全在旧 server 的物理世界里，所以这一局到此为止。
        /// 停掉本地 client；具体的收场形态（回主菜单还是留个终局画面）归 #134。
        /// </summary>
        private void OnSignalingRoomClosed(string reason)
        {
            Debug.LogWarning($"[DataChannelTransport] 房间已关闭（{reason}）。FishNet 没有 host migration，这一局结束。");
            StopConnection(false);
        }

        private void OnSignalingFailed(string code, string message)
        {
            Debug.LogError($"[DataChannelTransport] 信令失败 [{code}]：{message}");

            // 启动过程中失败必须如实报 Stopped，否则 FishNet 永远停在 Starting ——
            // 那是「静默连不上」，正是要避免的形态。
            if (_clientPeer == null && _clientState != LocalConnectionState.Stopped)
                _pendingClientState.Enqueue(LocalConnectionState.Stopped);
        }

        private void ForgetSignalingMapping(int connectionId)
        {
            if (_connectionIdToPeerId.TryGetValue(connectionId, out var peerId))
            {
                _connectionIdToPeerId.Remove(connectionId);
                _peerIdToConnectionId.Remove(peerId);
            }
        }

        /// <summary>
        /// 纯 client 侧那条连接的 connectionId。
        ///
        /// 取 0 而不是负数：`TryGetConnectionPath` 用**负数**表示「本地 client 那条」
        /// （见它的注释），所以这里必须是非负的，否则两个约定会撞。而 client 侧的
        /// connectionId 不会与 server 侧的表冲突 —— 纯 client 的 _serverPeers 是空的。
        /// </summary>
        private const int LocalClientConnectionId = 0;
        #endregion

        #region Stop

        public override bool StopConnection(bool server)
        {
            if (server)
            {
                if (!_serverStartRequested) return false; // 幂等（契约 3.5）
                _serverStartRequested = false;
                _pendingServerState.Enqueue(LocalConnectionState.Stopping);
                foreach (var kv in _serverPeers) kv.Value.Dispose();
                _serverPeers.Clear();
                _pendingServerState.Enqueue(LocalConnectionState.Stopped);
                DisposeSignalingIfIdle();
                return true;
            }

            if (_clientPeer == null) return false;
            _pendingClientState.Enqueue(LocalConnectionState.Stopping);
            _clientPeer?.Dispose();
            _clientPeer = null;
            ForgetSignalingMapping(LocalClientConnectionId);
            _pendingClientState.Enqueue(LocalConnectionState.Stopped);
            DisposeSignalingIfIdle();
            return true;
        }

        /// <summary>
        /// server 与 client **共用一条**信令连接（host 模式下两者都在这个 Transport 里），
        /// 所以只有两边都停了才能拆它。少了这个判断，host 停 client 会把 server 的房间
        /// 一起断掉 —— 服务端见 host 的 socket 断就销毁房间（server.py:200-210）。
        /// </summary>
        private void DisposeSignalingIfIdle()
        {
            if (_serverStartRequested || _clientPeer != null) return;
            _signaling?.Dispose();
            _signaling = null;
            _peerIdToConnectionId.Clear();
            _connectionIdToPeerId.Clear();
        }

        /// <summary>
        /// <paramref name="immediately"/> 两个值都当 true 处理。契约 3.5：4.7.2 里没有
        /// 一份叶子实现真的区分它，FishNet 自己唯一的调用点恒传 true，而且**上层已经
        /// 替我们做完了「等数据发完」的延迟**（max(100ms, 2 ticks)），transport 层再拖
        /// 一次没有价值。
        /// </summary>
        public override bool StopConnection(int connectionId, bool immediately)
        {
            if (!_serverPeers.TryGetValue(connectionId, out var peer)) return false; // 未知 id 返回 false，不抛
            peer.Dispose();
            _serverPeers.Remove(connectionId);
            _pendingRemoteState.Enqueue(new RemoteConnectionStateArgs(
                RemoteConnectionState.Stopped, connectionId, Index));
            return true;
        }

        /// <summary>
        /// 先 client 后 server（照 Tugboat 的次序，契约 3.5）。
        ///
        /// **只挂 OnDestroy，绝不挂 finalizer** —— SPEC 明确要求 finalizer 只入队、
        /// 绝不调 dcu_*，而 Tugboat 是从 finalizer 也调 Shutdown 的（契约 3.5 标了
        /// 这条对我们危险）。
        /// </summary>
        public override void Shutdown()
        {
            StopConnection(false);
            StopConnection(true);
        }

        /// <summary>
        /// 排空信令（#128）。
        ///
        /// ## 为什么在 Update 而不是 IterateIncoming
        ///
        /// 信令必须在 FishNet 开始 iterate **之前**就能推进：client 从 `join-room` 到
        /// `Started` 之间，FishNet 那侧还停在 Starting，把信令挂在它的 tick 上等于让连接
        /// 建立依赖一个尚未开始的循环。`Update` 只要组件 enabled 就跑，没有这个耦合。
        ///
        /// 排空**必须在主线程**：`SetRemoteDescription` / `AddRemoteCandidate` 都带
        /// `MainThread.Assert`。而 Drain 是顺序处理、一条不落 —— per-sender FIFO 在那里
        /// 兑现（见 SignalingClient 的类注释）。
        /// </summary>
        private void Update() => _signaling?.Drain();

        private void OnDestroy() => Shutdown();
        #endregion

        #region Queries

        /// <summary>同步返回缓存的状态，不能有副作用 —— 会被高频调用（契约 3.1）。</summary>
        public override LocalConnectionState GetConnectionState(bool server)
            => server ? _serverState : _clientState;

        /// <summary>未知 id 返回 Stopped，不抛（契约 3.1）。</summary>
        public override RemoteConnectionState GetConnectionState(int connectionId)
        {
            return _serverPeers.TryGetValue(connectionId, out var peer) && peer.StartedReported
                ? RemoteConnectionState.Started
                : RemoteConnectionState.Stopped;
        }

        /// <summary>
        /// 只被 NetworkConnection.ToString() 和 GetAddress() 消费，**不参与任何逻辑
        /// 判断**（契约 3.1），所以精确性是可靠性问题而非正确性问题。
        ///
        /// 报远端候选的 SDP：走 relay 时它就是 TURN 的地址，那正好是「数据实际从哪
        /// 来」。#120 第 4 问要定报 TURN 还是报对端 —— 因为不参与逻辑，这里先报手上
        /// 真实拿得到的那个。
        ///
        /// 未知 id 返回 string.Empty，不抛。
        /// </summary>
        public override string GetConnectionAddress(int connectionId)
        {
            if (!_serverPeers.TryGetValue(connectionId, out var peer) || peer.Pc == null)
                return string.Empty;

            // TryGetConnectionPath 在未连接时按契约返回 false（原生侧状态门禁），
            // 所以这里不需要自己判状态。
            return peer.Pc.TryGetConnectionPath(out var path, out var sdp)
                ? $"{path}:{sdp}"
                : string.Empty;
        }

        /// <summary>
        /// 固定常量，两档同值 —— 理由见 MtuBytes 的注释（契约 4.2 的四条硬约束）。
        /// 忽略 channel 参数，和 Tugboat 一致。
        /// </summary>
        public override int GetMTU(byte channel) => MtuBytes;

        /// <summary>
        /// 读出某条连接走的是直连还是中继。
        ///
        /// 这不是 FishNet 契约的一部分 —— 是本 Transport 额外开的口，给诊断 UI 用。
        /// <see cref="GetConnectionAddress"/> 也带这个信息，但那是给日志看的字符串，
        /// 让 UI 去解析它就等于把格式当 API。
        ///
        /// <paramref name="connectionId"/> 传负数表示「本地 client 那条」。
        /// </summary>
        public bool TryGetConnectionPath(int connectionId, out ConnectionPath path, out string remoteCandidateSdp)
        {
            path = default;
            remoteCandidateSdp = null;

            var peer = connectionId < 0
                ? _clientPeer
                : (_serverPeers.TryGetValue(connectionId, out var p) ? p : null);

            if (peer?.Pc == null) return false;
            return peer.Pc.TryGetConnectionPath(out path, out remoteCandidateSdp);
        }

        // 实现这两个只为免掉基类默认实现那条 warning（契约 3.7）。上限的真正决定归
        // #120 第 5 问；硬上界是 int.MaxValue - 1。
        public override int GetMaximumClients() => _maximumClients;

        public override void SetMaximumClients(int value) => _maximumClients = value;
        #endregion
    }
}
