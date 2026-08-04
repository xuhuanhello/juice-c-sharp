using System;
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

        private readonly object _gate = new object();
        private bool _disposed;
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
            DataChannelRuntime.EnsureNative();
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

            HandleTable.Register(this);
        }

        /// <summary>单个观察者，非多播；再次赋值会静默覆盖上一个。</summary>
        public IPeerConnectionObserver Observer
        {
            get => _observer;
            set => _observer = value;
        }

        public DataChannel CreateDataChannel(string label, DataChannelInit init = null)
        {
            ThrowIfDisposed();
            if (label == null) throw new ArgumentNullException(nameof(label));
            init = init ?? new DataChannelInit();
            init.Validate();

            var labelBytes = Encoding.UTF8.GetBytes(label);
            // 超界 label 在「连接前创建」这条路径上是**静默失败**：正句柄、不 open、
            // 不 closed、无 error，而 Send 仍返回成功且消息真发上线，对端判协议违规
            // 关流。两层都校验，这里是第一层（能给出可读的错误）。
            if (labelBytes.Length > MaxDataChannelLabelBytes)
                throw new ArgumentException(
                    "DataChannel label 超过 " + MaxDataChannelLabelBytes
                    + " 字节（UTF-8），实际 " + labelBytes.Length + "。这是上游 SCTP 线格式的实测上界。",
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
            var dc = new DataChannel(this, dcHandle, label, ownsCreation: true);
            HandleTable.Register(dc);
            return dc;
        }

        public void SetRemoteDescription(string sdp, string type)
        {
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
            ThrowIfDisposed();
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            var cB = Encoding.UTF8.GetBytes(candidate);
            byte[] mB = mid == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(mid);
            DataChannelRuntime.RequireOk(
                NativeMethods.dcu_pc_add_remote_candidate(
                    NativeHandle, cB, cB.Length, mB, mB.Length),
                "dcu_pc_add_remote_candidate");
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            try { NativeMethods.dcu_pc_close(NativeHandle); } catch { /* ignore */ }
            try { NativeMethods.dcu_pc_destroy(NativeHandle); } catch { /* ignore */ }
            HandleTable.UnregisterPc(NativeHandle);
            GC.SuppressFinalize(this);
        }

        ~PeerConnection()
        {
            try
            {
                if (!_disposed)
                {
                    NativeMethods.dcu_pc_destroy(NativeHandle);
                    HandleTable.UnregisterPc(NativeHandle);
                }
            }
            catch { /* finalizer path */ }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PeerConnection));
        }

        internal void RaiseLocalDescription(string sdp, string type)
        {
            LocalDescriptionGenerated?.Invoke(sdp, type);
            _observer?.OnLocalDescription(sdp, type);
        }

        internal void RaiseLocalCandidate(string candidate, string mid)
        {
            LocalCandidateGenerated?.Invoke(candidate, mid);
            _observer?.OnLocalCandidate(candidate, mid);
        }

        internal void RaiseConnectionState(ConnectionState state)
        {
            ConnectionState = state;
            ConnectionStateChanged?.Invoke(state);
            _observer?.OnConnectionStateChange(state);
        }

        internal void RaiseGatheringState(GatheringState state)
        {
            GatheringState = state;
            GatheringStateChanged?.Invoke(state);
            _observer?.OnGatheringStateChange(state);
        }

        internal void RaiseIncomingDataChannel(DataChannel channel)
        {
            DataChannelReceived?.Invoke(channel);
            _observer?.OnDataChannel(channel);
        }
    }
}
