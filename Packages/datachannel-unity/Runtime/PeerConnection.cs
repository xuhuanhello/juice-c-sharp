using System;
using System.Collections.Generic;
using System.Text;
using DataChannelUnity.Internal;

namespace DataChannelUnity
{
    public sealed class PeerConnection : IDisposable
    {
        /// <summary>
        /// DataChannel label 的上界，单位 UTF-8 字节。超过即抛 <see cref="ArgumentException"/>。
        /// </summary>
        /// <remarks>
        /// 这是**实测的**线格式上界（SCTP DCEP 用 uint16 表示 label 长度），不是理论值：
        /// 65535 端到端可用，65536 越界。公开它是因为让用户命名通道的应用需要自己先校验，
        /// 而超界的失败形态极其隐蔽（见 <c>docs/SPEC.md</c> §4）。
        /// </remarks>
        public const int MaxDataChannelLabelBytes = 65535;

        /// <summary>
        /// 子通道数量的**告警**阈值 —— 只 warning，**永不拒收**（#29 决议 3）。
        /// </summary>
        /// <remarks>
        /// 取 1024，与控制队列深度告警同一量级（也是上游 <c>RECV_QUEUE_LIMIT</c> 的量级）。
        /// 它不是硬上限：SCTP 的 stream id 是 uint16，真正的天花板在 65535。
        /// 「无人订阅就拒收」被否掉的理由是**时序敏感** —— 应用晚一帧订阅
        /// （等 UniTask、await 之后）就会被我们悄悄关掉通道，比泄漏难查得多。
        /// </remarks>
        internal const int ChildWarnThreshold = 1024;

        private readonly object _gate = new object();
        private readonly string _creationSite;

        // PC **强持有**它的子通道（SPEC §6 的所有权图）。查找表只持弱引用，
        // 子通道的存活完全由这条边保证。
        private readonly List<DataChannel> _children = new List<DataChannel>();
        // 级联时先拷再遍历：子 Dispose 会回调 ForgetChild 改动 _children。
        private readonly List<DataChannel> _cascadeBuffer = new List<DataChannel>();

        private bool _disposed;
        private bool _warnedChildCount;
        private IPeerConnectionObserver _observer;

        internal int NativeHandle { get; }
        public ConnectionState ConnectionState { get; private set; } = ConnectionState.New;
        public GatheringState GatheringState { get; private set; } = GatheringState.New;

        public event Action<string, string> LocalDescriptionGenerated;
        public event Action<string, string> LocalCandidateGenerated;
        public event Action<ConnectionState> ConnectionStateChanged;
        public event Action<GatheringState> GatheringStateChanged;
        public event Action<DataChannel> DataChannelReceived;

        public PeerConnection(PeerConnectionConfig config = null)
        {
            MainThread.Assert("PeerConnection..ctor");
            DataChannelRuntime.EnsureNative();
            DataChannelRuntime.CheckPumpLiveness("new PeerConnection");
            if (!DataChannelRuntime.IsNativeAvailable)
                throw new DataChannelException("Native plugin is not available. Build datachannel_unity per docs/SPEC.md.");

            config = config ?? new PeerConnectionConfig();
            using (var builder = new NativeConfigBuilder(config))
            {
                var cfg = builder.Config;
                DataChannelRuntime.RequireOk(
                    NativeMethods.dcu_pc_create(ref cfg, out var handle), "dcu_pc_create");
                NativeHandle = handle;
            }

            _creationSite = LeakTracker.CaptureSite();
            HandleTable.Register(this);
        }

        /// <summary>单个观察者，非多播；再次赋值会静默覆盖上一个。</summary>
        public IPeerConnectionObserver Observer
        {
            get => _observer;
            set
            {
                MainThread.Assert("PeerConnection.Observer");
                _observer = value;
            }
        }

        public DataChannel CreateDataChannel(string label, DataChannelInit init = null)
        {
            MainThread.Assert("PeerConnection.CreateDataChannel");
            ThrowIfDisposed();
            DataChannelRuntime.CheckPumpLiveness("PeerConnection.CreateDataChannel");
            if (label == null) throw new ArgumentNullException(nameof(label));
            init = init ?? new DataChannelInit();
            init.Validate();

            var labelBytes = Encoding.UTF8.GetBytes(label);
            // 超界 label 在「连接前创建」这条路径上是**静默失败**：正句柄、不 open、
            // 不 closed、无 error，而 Send 仍返回成功且消息真发上线，对端判协议违规
            // 关流。两层都校验，这里是第一层（能给出可读的错误）。
            if (labelBytes.Length > MaxDataChannelLabelBytes)
                throw new ArgumentException(
                    "DataChannel label exceeds " + MaxDataChannelLabelBytes
                    + " bytes (UTF-8); actual " + labelBytes.Length + ". This is the measured upper bound of the upstream SCTP wire format.",
                    nameof(label));
            var ninit = new NativeMethods.DcInitNative
            {
                ordered = init.Ordered ? 1 : 0,
                reliable = init.Reliable ? 1 : 0,
                max_retransmits = init.MaxRetransmits,
                max_packet_lifetime = init.MaxPacketLifeTime
            };

            DataChannelRuntime.RequireOk(
                NativeMethods.dcu_pc_create_data_channel(
                    NativeHandle, labelBytes, labelBytes.Length, ref ninit, out var dcHandle),
                "dcu_pc_create_data_channel");
            var dc = new DataChannel(this, dcHandle, label);
            HandleTable.Register(dc);
            AdoptChild(dc);
            return dc;
        }

        /// <summary>
        /// 接住一条入向通道。**照单全收**，无人订阅也创建并由本 PC 持有（#29 决议 3）。
        /// </summary>
        /// <remarks>
        /// PC 已被释放时返回 <c>null</c>，并就地把原生句柄销毁 —— 上游
        /// <c>rtcDeletePeerConnection</c> 不清子通道的表项，不销毁就是一个谁也够不着
        /// 的原生泄漏（它会出现在 <c>dcu_shutdown</c> 的未销毁计数里）。
        /// </remarks>
        internal DataChannel AdoptIncomingDataChannel(int handle, string label)
        {
            if (_disposed)
            {
                try { NativeMethods.dcu_dc_destroy(handle); } catch { /* ignore */ }
                return null;
            }

            var dc = new DataChannel(this, handle, label);
            HandleTable.Register(dc);
            AdoptChild(dc);
            return dc;
        }

        private void AdoptChild(DataChannel dc)
        {
            int count;
            lock (_gate)
            {
                _children.Add(dc);
                count = _children.Count;
            }

            if (count > ChildWarnThreshold && !_warnedChildCount)
            {
                _warnedChildCount = true;
                DataChannelLog.Emit(LogLevel.Warning,
                    "PeerConnection(handle=" + NativeHandle + ") has more child channels than the advisory limit of "
                    + ChildWarnThreshold + " (currently " + count + "). Channels are NOT rejected: "
                    + "this is advisory only, and usually means channels are not being disposed, or the remote peer is churning them.");
            }
        }

        /// <summary>
        /// 子通道自行 <c>Dispose</c> 时把自己从子列表摘掉，避免父 Dispose 二次销毁。
        /// </summary>
        internal void ForgetChild(DataChannel dc)
        {
            lock (_gate) _children.Remove(dc);
        }

        public void SetRemoteDescription(string sdp, string type)
        {
            MainThread.Assert("PeerConnection.SetRemoteDescription");
            ThrowIfDisposed();
            if (sdp == null) throw new ArgumentNullException(nameof(sdp));
            if (type == null) throw new ArgumentNullException(nameof(type));
            var sdpB = Encoding.UTF8.GetBytes(sdp);
            var typeB = Encoding.UTF8.GetBytes(type);
            DataChannelRuntime.RequireOk(
                NativeMethods.dcu_pc_set_remote_description(
                    NativeHandle, sdpB, sdpB.Length, typeB, typeB.Length),
                "dcu_pc_set_remote_description");
        }

        public void AddRemoteCandidate(string candidate, string mid = null)
        {
            MainThread.Assert("PeerConnection.AddRemoteCandidate");
            ThrowIfDisposed();
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            var cB = Encoding.UTF8.GetBytes(candidate);
            byte[] mB = mid == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(mid);
            DataChannelRuntime.RequireOk(
                NativeMethods.dcu_pc_add_remote_candidate(
                    NativeHandle, cB, cB.Length, mB, mB.Length),
                "dcu_pc_add_remote_candidate");
        }

        /// <summary>
        /// 判定这条连接实际走的是直连还是 TURN 中继，并带出远端候选的 SDP。
        /// </summary>
        /// <param name="path">走的路。仅当返回 <c>true</c> 时有意义。</param>
        /// <param name="remoteCandidateSdp">
        /// 远端候选的 SDP 行（带 <c>a=</c> 前缀，与 <see cref="LocalCandidateGenerated"/>
        /// 的形态一致）。仅当返回 <c>true</c> 时非 <c>null</c>。只要判定不要它就写
        /// <c>out _</c>。
        /// </param>
        /// <returns>
        /// 连接尚未建立、或此刻没有选中候选对时返回 <c>false</c> —— 那是**正常态，不是错误**。
        /// </returns>
        /// <remarks>
        /// <para>
        /// **拉取，没有对应事件。** 上游没有「候选对变了」的事件源，而 ICE 一旦提名就
        /// 不再重选（RFC 8445 §8.1.1，libjuice 逐字实现）。要合成一个事件就得起一条
        /// 后台轮询线程，与本包「绝不后台轮询」的立场冲突。连上之后读一次即可。
        /// </para>
        /// <para>
        /// **判据不暴露，也不该由调用方拼。** 它是「候选对任一端是中继」，两端都要看：
        /// 走中继时，靠 TURN 那一侧看到的是自己的本地候选是中继、对面的远端候选是 host。
        /// 判据在 native 侧合成（见 <c>dcu.h</c>），本方法只搬结果。
        /// </para>
        /// <para>
        /// **本地候选类型故意不给。** 它在非中继路径上不是真实路径：libjuice 只为本地
        /// 中继候选建带本地端的候选对，其余情况回退到优先级最高的那条本地候选，通常是
        /// host —— 于是一条 srflx 路径的本地会被报成 host。判定内部只在「是中继」这个
        /// 方向上采信它，是安全的；把它交出去不是。
        /// </para>
        /// <para>
        /// **已知窗口。** 上游的选中候选对**从不被清空**，所以连接失败或断开之后，原生
        /// 层仍会带回上一次的判定。本方法用 <see cref="ConnectionState"/> 兜住这一点，
        /// 而那个状态是**事件缓存**而非活查询 —— 因此在「原生层已失败、状态事件还没派发」
        /// 的亚帧窗口内，本方法会返回上一次的判定。范围有界：限于单个连接失败之后，且
        /// ICE 的失败是终态、不会重新提名。连接存活期间读取不受影响。
        /// </para>
        /// </remarks>
        public bool TryGetConnectionPath(out ConnectionPath path, out string remoteCandidateSdp)
        {
            MainThread.Assert("PeerConnection.TryGetConnectionPath");
            ThrowIfDisposed();

            path = default;
            remoteCandidateSdp = null;

            // 门禁：见上文「已知窗口」。原生层不会自己拒绝一个已死连接的陈旧判定。
            // 取局部变量而不是写 ConnectionState != ConnectionState.Connected ——
            // 后者要靠 C# 的 Color-Color 规则消歧，包里没有这样的先例。
            var state = ConnectionState;
            if (state != DataChannelUnity.ConnectionState.Connected)
                return false;

            // 一次穿越就够：上游 JUICE_MAX_CANDIDATE_SDP_STRING_LEN 是 256，
            // 加上 "a=" 前缀仍在 288 以内。
            var buf = new byte[288];
            var rc = NativeMethods.dcu_pc_connection_path(
                NativeHandle, out var verdict, buf, buf.Length, out var len);

            if (rc == NativeMethods.ErrTooSmall)
            {
                // 按上面那个上界，这条路走不到。**留着不是防御性冗余** —— 那个
                // 288 是从上游常量推出来的，上游改了它这里就该跟着走，而不是
                // 静默截断或让一个 TooSmall 冒成异常。长度是精确值且未消费，
                // 所以一次重试必然成功。
                buf = new byte[len];
                rc = NativeMethods.dcu_pc_connection_path(
                    NativeHandle, out verdict, buf, buf.Length, out len);
            }

            if (rc == NativeMethods.ErrNotAvail)
                return false;

            DataChannelRuntime.RequireOk(rc, "dcu_pc_connection_path");

            path = (ConnectionPath)verdict;
            remoteCandidateSdp = len > 0 ? Encoding.UTF8.GetString(buf, 0, len) : string.Empty;
            return true;
        }

        /// <summary>
        /// 释放本连接**及其全部子通道**，顺序是**先子后父**。
        /// </summary>
        /// <remarks>
        /// 顺序不能反：<c>dcu_dc_destroy</c> 打在一个已销毁的 PC 的子句柄上就是打在僵尸上。
        /// 级联本身也不能省 —— 上游 <c>rtcDeletePeerConnection</c> 只 close + 摘掉 PC 自己
        /// （<c>capi.cpp:437-444</c>），不清子通道的表项，不级联漏的就不只是我们这张表，
        /// 连 libdatachannel 自己的 <c>dataChannelMap</c> 一起漏。
        /// </remarks>
        public void Dispose()
        {
            MainThread.Assert("PeerConnection.Dispose");
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                // 先拷再清空：子的 Dispose 路径会回调 ForgetChild 改动 _children。
                _cascadeBuffer.Clear();
                _cascadeBuffer.AddRange(_children);
                _children.Clear();
            }

            for (int i = 0; i < _cascadeBuffer.Count; i++)
                _cascadeBuffer[i].DisposeFromParent();
            _cascadeBuffer.Clear();

            try { NativeMethods.dcu_pc_close(NativeHandle); } catch { /* ignore */ }
            try { NativeMethods.dcu_pc_destroy(NativeHandle); } catch { /* ignore */ }
            HandleTable.UnregisterPc(NativeHandle);
            GC.SuppressFinalize(this);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// **只做一次无锁入队**（SPEC §6 / #29 决议 8）。见 <see cref="LeakTracker"/>。
        /// 注意这里连子通道都不碰 —— 父被回收时子也早已不可达，它们各自的终结器会各自入队。
        /// </summary>
        ~PeerConnection()
        {
            // 构造函数抛出（坏 ICE URL、原生不可用）时对象仍会被终结，但那时
            // NativeHandle 还是默认的 0 —— 什么都没分配，报出来就是纯噪音。
            // 上游 lastId 从 1 起，0 永远不是合法句柄。
            if (NativeHandle == 0) return;

            LeakTracker.ReportFromFinalizer(new LeakRecord
            {
                IsPeerConnection = true,
                Handle = NativeHandle,
                Label = null,
                CreationSite = _creationSite
            });
        }
#endif

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PeerConnection));
        }

        /// <summary>
        /// 诊断用文本。<c>NativeHandle</c> 收 internal 之后，**这是句柄的唯一公开出口**
        /// （SPEC §6「What is public」）—— 把句柄本身公开会招人存下来、传来传去、
        /// 在 Dispose 之后接着用，而每一个正当操作都已经有托管方法了。
        /// </summary>
        public override string ToString() =>
            "PeerConnection(handle=" + NativeHandle + ", disposed=" + _disposed + ")";

        // 以下五个 Raise 全部走每订阅者隔离（SPEC §6 / #38 决议 5）。控制事件稀疏，
        // 直接 GetInvocationList 的那次数组分配无关紧要 —— 与消息事件的缓存快照
        // 是同一语义、不同实现，按频率分档。
        internal void RaiseLocalDescription(string sdp, string type)
        {
            SafeDispatch.Invoke(LocalDescriptionGenerated, sdp, type, "PeerConnection.LocalDescriptionGenerated");
            var obs = _observer;
            if (obs != null)
                SafeDispatch.Observer(() => obs.OnLocalDescription(sdp, type), "IPeerConnectionObserver.OnLocalDescription");
        }

        internal void RaiseLocalCandidate(string candidate, string mid)
        {
            SafeDispatch.Invoke(LocalCandidateGenerated, candidate, mid, "PeerConnection.LocalCandidateGenerated");
            var obs = _observer;
            if (obs != null)
                SafeDispatch.Observer(() => obs.OnLocalCandidate(candidate, mid), "IPeerConnectionObserver.OnLocalCandidate");
        }

        internal void RaiseConnectionState(ConnectionState state)
        {
            ConnectionState = state;
            SafeDispatch.Invoke(ConnectionStateChanged, state, "PeerConnection.ConnectionStateChanged");
            var obs = _observer;
            if (obs != null)
                SafeDispatch.Observer(() => obs.OnConnectionStateChange(state), "IPeerConnectionObserver.OnConnectionStateChange");
        }

        internal void RaiseGatheringState(GatheringState state)
        {
            GatheringState = state;
            SafeDispatch.Invoke(GatheringStateChanged, state, "PeerConnection.GatheringStateChanged");
            var obs = _observer;
            if (obs != null)
                SafeDispatch.Observer(() => obs.OnGatheringStateChange(state), "IPeerConnectionObserver.OnGatheringStateChange");
        }

        internal void RaiseIncomingDataChannel(DataChannel channel)
        {
            SafeDispatch.Invoke(DataChannelReceived, channel, "PeerConnection.DataChannelReceived");
            var obs = _observer;
            if (obs != null)
                SafeDispatch.Observer(() => obs.OnDataChannel(channel), "IPeerConnectionObserver.OnDataChannel");
        }
    }
}
