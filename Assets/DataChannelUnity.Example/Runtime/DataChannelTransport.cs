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

        // GetMTU 返回的固定常量。取 1282 = Tugboat 的值（1350 - 68），**故意**和它
        // 一样：bring-up 阶段要拿本 Transport 和 Tugboat 做 A/B，同一个打包粒度才
        // 让差异指向传输层而不是包大小。
        //
        // 契约 4.2 的四条硬约束都满足：① 连接建立前就被调用、拿不到协商值，所以这
        // 是静态常量不是协商结果；② 结果被永久缓存，运行期改不了；③ FishNet 再净扣
        // 2 字节且扣完 <= 100 视为无效，1282 远在其上；④ 两档返回同值，绕开
        // SetLowestMTUs 里 allLowest 那处从不更新的可疑逻辑。
        //
        // SCTP 自己会分片，所以这个值和 PeerConnectionConfig.Mtu 解耦 —— 它只是
        // 「给 FishNet 的打包尺度」，Synapse 的注释是直接先例。最终由 #119 定。
        private const int MtuBytes = 1282;

        // ── 归 #120 的，暂取 ────────────────────────────────────────────────
        //
        // connectionId 从 0 起单调递增、**永不复用**（Synapse 的先例）。契约 4.3 的
        // 约束：必须 >= 0（ServerManager 会当场踢）、必须避开 int.MaxValue（那是
        // SIMULATED_CLIENTID_VALUE）、存活期唯一；不要求连续/单调/从 0 起。
        //
        // 选永不复用是因为复用会让「拿着过期 id 去发」这类 bug 静默命中**另一个**
        // peer；不复用则查表失败，响亮。最终由 #120 定。
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

        [SerializeField]
        [Tooltip("host 支持的 client 数上限。实现 GetMaximumClients 只为免掉基类默认实现那条 warning（契约 3.7）。")]
        private int _maximumClients = 8;
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
        /// 真正的发送。**不能抛** —— 契约 3.2：调用点在 IterateOutgoing 的双层循环里，
        /// 一次抛会把该帧剩余所有连接的发送全打断。而我们的 DataChannel.Send 在通道
        /// 未 open 时**会抛**（SendCore 刻意不预检 open 状态，交给原生失败后
        /// RequireOk 抛），所以这个 try/catch 是契约要求的，不是防御性冗余。
        /// </summary>
        private void SendOn(Peer peer, byte channelId, ArraySegment<byte> segment)
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
            SendOn(_clientPeer, channelId, segment);
        }

        public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        {
            // -1 是广播语义（Tugboat 见 UNSET_CLIENTID_VALUE 就 SendToAll）。正常路径
            // 上 TransportManager 总带真实 id，所以这里只要**确保不把 -1 当成一个真
            // 连接去查表**即可（契约 3.2）。
            if (connectionId < 0) return;
            _serverPeers.TryGetValue(connectionId, out var peer);
            SendOn(peer, channelId, segment);
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
        /// 空实现，**这是有意的**。契约 1.4：Send* 是「入队」、IterateOutgoing 是
        /// 「冲刷」，但我们的 DataChannel.Send 是同步 P/Invoke，在 Send* 里就已经出去
        /// 了（契约 5.2：出站没有对齐问题）。所以没有要冲刷的东西。
        ///
        /// 代价是放弃了「一帧内合并/限流」的唯一位置 —— 那归 #119 的背压决策，真要做
        /// 时把 Send* 改成入队、在这里冲刷即可，结构已经留好。
        /// </summary>
        public override void IterateOutgoing(bool asServer) { }
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

        private void OnPeerChannelClosed(Peer peer, bool asServer)
        {
            if (!peer.StartedReported) return;
            peer.StartedReported = false;

            if (asServer)
            {
                // Stopped 必须**后于**该连接最后一条数据（契约 2.3 第 3 条）。我们的
                // DcClosed 已经先 DrainChannel 再 raise，所以入桶顺序天然合规：那条
                // 消息已经在 _inboundToServer 里排在这个状态事件之前。
                _pendingRemoteState.Enqueue(new RemoteConnectionStateArgs(
                    RemoteConnectionState.Stopped, peer.ConnectionId, Index));
            }
            else
            {
                _pendingClientState.Enqueue(LocalConnectionState.Stopping);
                _pendingClientState.Enqueue(LocalConnectionState.Stopped);
            }
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

        public override bool StartConnection(bool server)
        {
            // 返回值语义是「没有阻塞项」，不是「已连上」（契约 3.5）。
            if (server) return StartServer();
            return StartClient();
        }

        private bool StartServer()
        {
            if (_serverStartRequested) return false; // 幂等（契约 3.5）

            // 同步置位，**先于**事件投递 —— 见 _serverStartRequested 的注释。
            _serverStartRequested = true;
            _pendingServerState.Enqueue(LocalConnectionState.Starting);
            // server 侧没有要等的东西：它只是开始接受连接。Started 立刻入队。
            _pendingServerState.Enqueue(LocalConnectionState.Started);
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
            if (!_serverStartRequested)
            {
                // 第二步（两个进程 + wss 信令）才需要「client 先于 server」的路径。
                // 这一版明确只支持 host，缺 server 时响亮失败而不是静默连不上。
                Debug.LogError("[DataChannelTransport] 这一版只支持单进程 host：请先起 server。" +
                               "两个进程的路径要等 #121 的信令服务器。");
                return false;
            }

            _pendingClientState.Enqueue(LocalConnectionState.Starting);

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
            serverSide.Pc.DataChannelReceived += dc =>
            {
                if (dc.Label == UnreliableLabel)
                {
                    serverSide.Unreliable = dc;
                    WireChannel(serverSide, dc, Channel.Unreliable, asServer: true);
                }
                else
                {
                    serverSide.Reliable = dc;
                    WireChannel(serverSide, dc, Channel.Reliable, asServer: true);
                }
                // 被动接到的通道可能**已经**是 open 的（Opened 事件可能早于我们订阅），
                // 所以这里补判一次，否则 Started 永远不会上报。
                OnPeerChannelOpened(serverSide, asServer: true);
            };

            // client 侧主动开两条。#116 已查证第二条只开新 SCTP 流、不触发重新协商，
            // 所以两条只需一次 offer/answer。
            clientSide.Reliable = clientSide.Pc.CreateDataChannel(ReliableLabel, ReliableInit);
            WireChannel(clientSide, clientSide.Reliable, Channel.Reliable, asServer: false);
            clientSide.Unreliable = clientSide.Pc.CreateDataChannel(UnreliableLabel, UnreliableInit);
            WireChannel(clientSide, clientSide.Unreliable, Channel.Unreliable, asServer: false);

            return true;
        }

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
                return true;
            }

            if (_clientPeer == null) return false;
            _pendingClientState.Enqueue(LocalConnectionState.Stopping);
            _clientPeer?.Dispose();
            _clientPeer = null;
            _pendingClientState.Enqueue(LocalConnectionState.Stopped);
            return true;
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
