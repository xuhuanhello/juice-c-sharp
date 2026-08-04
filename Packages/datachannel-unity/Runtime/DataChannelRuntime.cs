using System;
using System.Text;
using DataChannelUnity.Internal;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace DataChannelUnity
{
    /// <summary>
    /// Library lifecycle and main-thread event pump.
    /// </summary>
    public static class DataChannelRuntime
    {
        private static bool _nativeReady;
        private static bool _initAttempted;
        private static bool _pumpRegistered;
        private static byte[] _payloadBuf = new byte[65536];
        private static byte[] _payload2Buf = new byte[4096];

        // 消息缓冲与控制缓冲**分开**（SPEC §6）：SDP/candidate 稳定在几 KB，
        // 消息可能几 MB，共用一个就是让前者永远按后者的尺寸躺着。
        private static byte[] _messageBuf = new byte[65536];

        // 复用的通道快照，零分配。见 HandleTable.SnapshotDataChannels 的说明。
        private static readonly System.Collections.Generic.List<DataChannel> ChannelSnapshot =
            new System.Collections.Generic.List<DataChannel>();

        public static bool IsNativeAvailable
        {
            get
            {
                EnsureNative();
                return _nativeReady;
            }
        }

        public static int AbiVersion
        {
            get
            {
                EnsureNative();
                if (!_nativeReady) return 0;
                return NativeMethods.dcu_abi_version(out var v) == NativeMethods.Success ? v : 0;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsOnEnterPlayMode()
        {
            _nativeReady = false;
            _initAttempted = false;
            _pumpRegistered = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            DataChannelLog.EnsureDefaults();
            EnsureNative();
            RegisterPump();
        }

        internal static void EnsureNative()
        {
            if (_initAttempted) return;
            _initAttempted = true;
            DataChannelLog.EnsureDefaults();
            try
            {
                var rc = NativeMethods.dcu_init();
                if (rc == NativeMethods.Success)
                {
                    _nativeReady = true;
                    NativeMethods.dcu_set_log_level((int)DataChannelLog.Level);
                    NativeMethods.dcu_abi_version(out var abi);
                    DataChannelLog.Emit(LogLevel.Info, "Native library initialized (abi=" + abi + ").");
                }
                else
                {
                    DataChannelLog.Emit(LogLevel.Error, "dcu_init failed: " + rc);
                }
            }
            catch (DllNotFoundException e)
            {
                DataChannelLog.Emit(LogLevel.Error,
                    "Native plugin '" + NativeMethods.DllName + "' not found. Build and place Plugins per docs/SPEC.md. " + e.Message);
            }
            catch (Exception e)
            {
                DataChannelLog.Emit(LogLevel.Error, "Native init exception: " + e.Message);
            }
        }

        internal static void RegisterPump()
        {
            if (_pumpRegistered) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!InsertPump(ref loop))
            {
                DataChannelLog.Emit(LogLevel.Warning, "Failed to insert PlayerLoop pump; call DataChannelRuntime.Pump() manually.");
                return;
            }
            PlayerLoop.SetPlayerLoop(loop);
            _pumpRegistered = true;
        }

        private static bool InsertPump(ref PlayerLoopSystem loop)
        {
            var pump = new PlayerLoopSystem
            {
                type = typeof(DataChannelRuntime),
                updateDelegate = Pump
            };

            if (loop.subSystemList == null) return false;
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type == typeof(Update))
                {
                    var subs = loop.subSystemList[i].subSystemList;
                    var list = subs != null
                        ? new System.Collections.Generic.List<PlayerLoopSystem>(subs)
                        : new System.Collections.Generic.List<PlayerLoopSystem>();
                    // Avoid duplicate inserts on domain reload edge cases.
                    list.RemoveAll(s => s.type == typeof(DataChannelRuntime));
                    list.Add(pump);
                    loop.subSystemList[i].subSystemList = list.ToArray();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Drain native event queue and raise managed events on the calling thread (must be main thread).
        /// </summary>
        /// <summary>
        /// 排空原生事件并在调用线程（必须是主线程）上派发。
        /// </summary>
        /// <remarks>
        /// 两段结构（SPEC §6）：先排空**控制**队列，再逐通道**拉取**消息。
        /// 两段都排空，**都不设每帧预算** —— 系统本来就会自己收敛：pump 排空则
        /// 上游 mRecvQueue 不涨、无背压；应用回调慢则帧变长、拉取变慢、队列涨到
        /// RECV_QUEUE_LIMIT 而阻塞，真背压顶回 SCTP。设预算是人为提前触发背压。
        ///
        /// 已接受的软肋：高速对端能让本段随流量线性变长。可见性靠慢帧告警（S7）。
        /// </remarks>
        public static void Pump()
        {
            if (!_nativeReady) return;
            DrainControlEvents();
            DrainMessages();
        }

        private static void DrainControlEvents()
        {
            for (int safety = 0; safety < 256; safety++)
            {
                var rc = NativeMethods.dcu_event_next(out var header,
                    _payloadBuf, _payloadBuf.Length, _payload2Buf, _payload2Buf.Length);

                if (rc == NativeMethods.ErrTooSmall)
                {
                    // header 里两个长度都是**精确值**，且事件未被消费；单消费者契约
                    // 保证两次调用之间队首不变，所以扩容后**一次重试必然成功**。
                    EnsureCapacity(ref _payloadBuf, header.payload_len);
                    EnsureCapacity(ref _payload2Buf, header.payload2_len);
                    rc = NativeMethods.dcu_event_next(out header,
                        _payloadBuf, _payloadBuf.Length, _payload2Buf, _payload2Buf.Length);
                }

                if (rc == NativeMethods.ErrNotAvail || header.type == NativeMethods.EventType.None)
                    break;

                if (rc != NativeMethods.Success)
                {
                    DataChannelLog.Emit(LogLevel.Warning,
                        "dcu_event_next failed: " + MapError(rc) + " (raw=" + rc + ")");
                    break;
                }

                string payload = null;
                string payload2 = null;
                if (header.payload_len > 0)
                    payload = Encoding.UTF8.GetString(_payloadBuf, 0, header.payload_len);
                if (header.payload2_len > 0)
                    payload2 = Encoding.UTF8.GetString(_payload2Buf, 0, header.payload2_len);

                Dispatch(header, payload, payload2);
            }
        }

        private static void DrainMessages()
        {
            // 遍历**快照**而非字典本身：拉到消息会当场派发，而应用在回调里
            // Dispose() 通道或 CreateDataChannel() 都合法，两者都改动 HandleTable
            // 的字典 —— Dictionary 迭代中被修改会抛，且那个异常来自我们自己的迭代，
            // 每订阅者的隔离罩不住它。
            HandleTable.SnapshotDataChannels(ChannelSnapshot);
            for (int i = 0; i < ChannelSnapshot.Count; i++)
            {
                var dc = ChannelSnapshot[i];
                if (dc == null || dc.IsDisposed || !dc.IsOpen) continue;
                DrainChannel(dc, requireOpen: true);
            }
            ChannelSnapshot.Clear();
        }

        /// <summary>拉空一个通道的接收队列。每次拉取前**逐项验活**。</summary>
        private static void DrainChannel(DataChannel dc, bool requireOpen)
        {
            while (true)
            {
                // 回调可能刚把它 Dispose 掉，也可能刚把它关掉。
                if (dc.IsDisposed || (requireOpen && !dc.IsOpen)) return;

                var rc = NativeMethods.dcu_dc_receive(
                    dc.NativeHandle, _messageBuf, _messageBuf.Length, out var n);

                if (rc == NativeMethods.ErrTooSmall)
                {
                    EnsureCapacity(ref _messageBuf, n);
                    rc = NativeMethods.dcu_dc_receive(
                        dc.NativeHandle, _messageBuf, _messageBuf.Length, out n);
                }

                if (rc == NativeMethods.ErrNotAvail) return;

                if (rc != NativeMethods.Success)
                {
                    DataChannelLog.Emit(LogLevel.Warning,
                        "dcu_dc_receive failed: " + MapError(rc) + " (raw=" + rc + ")");
                    return;
                }

                dc.RaiseMessage(new ReadOnlySpan<byte>(_messageBuf, 0, n));
            }
        }

        private static void EnsureCapacity(ref byte[] buf, int need)
        {
            if (buf == null || buf.Length < need)
                buf = new byte[Math.Max(need, 1024)];
        }

        private static void Dispatch(NativeMethods.EventHeader h, string p1, string p2)
        {
            switch (h.type)
            {
                case NativeMethods.EventType.LocalDescription:
                    if (HandleTable.TryGetPc(h.pc, out var pc1))
                        pc1.RaiseLocalDescription(p1 ?? string.Empty, p2 ?? string.Empty);
                    break;
                case NativeMethods.EventType.LocalCandidate:
                    if (HandleTable.TryGetPc(h.pc, out var pc2))
                        pc2.RaiseLocalCandidate(p1 ?? string.Empty, p2);
                    break;
                case NativeMethods.EventType.ConnectionState:
                    if (HandleTable.TryGetPc(h.pc, out var pc3))
                        pc3.RaiseConnectionState(MapConnectionState(h.state));
                    break;
                case NativeMethods.EventType.GatheringState:
                    if (HandleTable.TryGetPc(h.pc, out var pc4))
                        pc4.RaiseGatheringState(MapGatheringState(h.state));
                    break;
                case NativeMethods.EventType.IncomingDataChannel:
                    if (HandleTable.TryGetPc(h.pc, out var pc5))
                    {
                        var dc = new DataChannel(pc5, h.dc, p1 ?? string.Empty, ownsCreation: false);
                        HandleTable.Register(dc);
                        pc5.RaiseIncomingDataChannel(dc);
                    }
                    break;
                case NativeMethods.EventType.DcOpen:
                    if (HandleTable.TryGetDc(h.dc, out var d1))
                        d1.RaiseOpen();
                    break;
                case NativeMethods.EventType.DcClosed:
                    if (HandleTable.TryGetDc(h.dc, out var d2))
                    {
                        // 先把该通道的接收队列拉空再报 Closed，否则「关闭前收到的
                        // 消息」会丢或乱序（SPEC §4）。此刻句柄仍可解析 —— 原生对象
                        // 要到 dcu_dc_destroy 才从表里摘除。
                        // requireOpen: false —— 通道已经关了，但残留消息仍该投递。
                        DrainChannel(d2, requireOpen: false);
                        d2.RaiseClosed();
                    }
                    break;
                case NativeMethods.EventType.DcError:
                    if (HandleTable.TryGetDc(h.dc, out var d3))
                        d3.RaiseError(p1 ?? "error");
                    break;
            }
        }

        /// <summary>
        /// 把 ABI 的原始返回值映射到 <see cref="DataChannelError"/>。
        /// 不认识的值落 <see cref="DataChannelError.Unknown"/>，原始数值仍由
        /// <see cref="DataChannelException.RawCode"/> 带出。
        /// </summary>
        internal static DataChannelError MapError(int raw)
        {
            switch (raw)
            {
                case NativeMethods.ErrInvalid: return DataChannelError.Invalid;
                case NativeMethods.ErrFailure: return DataChannelError.Failure;
                case NativeMethods.ErrNotAvail: return DataChannelError.NotAvailable;
                case NativeMethods.ErrTooSmall: return DataChannelError.TooSmall;
                case NativeMethods.ErrUpstreamUnknown: return DataChannelError.UpstreamUnknown;
                default: return DataChannelError.Unknown;
            }
        }

        // RequireCreate 已删除：#31 把 ABI 统一成「返回码 + out 参数」之后，
        // 「返回值兼作数据」的两种形状都没了，判成功只剩 rc == DCU_OK 一种写法。
        internal static void RequireOk(int code, string what)
        {
            if (code == NativeMethods.Success) return;
            throw NewException(code, what);
        }

        /// <summary>
        /// 异常消息分**两类**措辞而非逐码一句：分流的价值是告诉人**该查自己还是该找我们**，
        /// 再细就是维护负担。<c>RawCode</c> 始终带在消息里。
        /// </summary>
        private static DataChannelException NewException(int code, string what)
        {
            var err = MapError(code);
            var selfFixable = err == DataChannelError.Invalid || err == DataChannelError.TooSmall;
            var message = selfFixable
                ? what + ": 参数无效 (code=" + err + ", raw=" + code
                  + ")。检查通道是否已 Dispose、载荷是否超过协商的 MaxMessageSize。"
                : what + ": 运行时失败 (code=" + err + ", raw=" + code
                  + ")。这通常不是用法问题，请附 DataChannelLog 输出提 issue。";
            return new DataChannelException(message, err, code);
        }

        private static ConnectionState MapConnectionState(int raw)
        {
            switch (raw)
            {
                case 0: return ConnectionState.New;
                case 1: return ConnectionState.Connecting;
                case 2: return ConnectionState.Connected;
                case 3: return ConnectionState.Disconnected;
                case 4: return ConnectionState.Failed;
                case 5: return ConnectionState.Closed;
                default:
                    DataChannelLog.Emit(LogLevel.Warning,
                        "Unrecognised ConnectionState from native: " + raw + " -> Unknown");
                    return ConnectionState.Unknown;
            }
        }

        private static GatheringState MapGatheringState(int raw)
        {
            switch (raw)
            {
                case 0: return GatheringState.New;
                case 1: return GatheringState.InProgress;
                case 2: return GatheringState.Complete;
                default:
                    DataChannelLog.Emit(LogLevel.Warning,
                        "Unrecognised GatheringState from native: " + raw + " -> Unknown");
                    return GatheringState.Unknown;
            }
        }
    }
}
