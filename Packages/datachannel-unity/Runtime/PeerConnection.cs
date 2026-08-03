using System;
using System.Text;
using DataChannelUnity.Internal;

namespace DataChannelUnity
{
    public sealed class PeerConnection : IDisposable
    {
        private readonly object _gate = new object();
        private bool _disposed;
        private IPeerConnectionObserver _observer;

        public int NativeHandle { get; }
        public ConnectionState ConnectionState { get; private set; } = ConnectionState.New;
        public GatheringState GatheringState { get; private set; } = GatheringState.New;

        public event Action<string, string> LocalDescriptionGenerated;
        public event Action<string, string> LocalCandidateGenerated;
        public event Action<ConnectionState> ConnectionStateChanged;
        public event Action<GatheringState> GatheringStateChanged;
        public event Action<DataChannel> DataChannel;

        public PeerConnection(PeerConnectionConfig config = null)
        {
            DataChannelRuntime.EnsureNative();
            if (!DataChannelRuntime.IsNativeAvailable)
                throw new DataChannelException("Native plugin is not available. Build datachannel_unity per docs/SPEC.md.");

            config = config ?? new PeerConnectionConfig();
            using (var builder = new NativeConfigBuilder(config))
            {
                var cfg = builder.Config;
                var handle = NativeMethods.dcu_pc_create(ref cfg);
                NativeHandle = DataChannelRuntime.RequireCreate(handle, "dcu_pc_create");
            }

            HandleTable.Register(this);
        }

        public void SetObserver(IPeerConnectionObserver observer) => _observer = observer;

        public DataChannel CreateDataChannel(string label, DataChannelInit init = null)
        {
            ThrowIfDisposed();
            if (label == null) throw new ArgumentNullException(nameof(label));
            init = init ?? new DataChannelInit();

            var labelBytes = Encoding.UTF8.GetBytes(label);
            var ninit = new NativeMethods.DcInitNative
            {
                ordered = init.Ordered ? 1 : 0,
                reliable = init.Reliable ? 1 : 0,
                max_retransmits = init.MaxRetransmits,
                max_packet_lifetime = init.MaxPacketLifeTime
            };

            var dcHandle = NativeMethods.dcu_pc_create_data_channel(
                NativeHandle, labelBytes, labelBytes.Length, ref ninit);
            dcHandle = DataChannelRuntime.RequireCreate(dcHandle, "dcu_pc_create_data_channel");
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
            DataChannel?.Invoke(channel);
            _observer?.OnDataChannel(channel);
        }
    }
}
