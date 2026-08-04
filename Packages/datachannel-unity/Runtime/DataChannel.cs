using System;
using DataChannelUnity.Internal;

namespace DataChannelUnity
{
    public sealed class DataChannel : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _creationSite;
        private bool _disposed;
        private bool _disposedByParent;
        private IDataChannelObserver _observer;

        public PeerConnection Peer { get; }
        internal int NativeHandle { get; }
        public string Label { get; }
        /// <summary>
        /// 通道当前状态。**每次读都是一次活查询**，不是缓存的事件结果。
        /// </summary>
        public DataChannelState State
        {
            get
            {
                MainThread.Assert("DataChannel.State");
                if (_disposed) return DataChannelState.Closed;
                DataChannelRuntime.RequireOk(
                    NativeMethods.dcu_dc_state(NativeHandle, out var raw), "dcu_dc_state");
                switch (raw)
                {
                    case 1: return DataChannelState.Open;
                    case 2: return DataChannelState.Closed;
                    default: return DataChannelState.Connecting;
                }
            }
        }

        public bool IsOpen => State == DataChannelState.Open;

        /// <summary>供 pump 在派发过程中逐项验活。不走 P/Invoke。</summary>
        internal bool IsDisposed => _disposed;

        public event Action Opened;
        public event Action Closed;
        public event Action<string> ErrorOccurred;
        /// <summary>收到消息。载荷**只在回调期间有效**，见 <see cref="DataChannelMessageHandler"/>。</summary>
        public event DataChannelMessageHandler MessageReceived;

        internal DataChannel(PeerConnection peer, int handle, string label)
        {
            Peer = peer ?? throw new ArgumentNullException(nameof(peer));
            NativeHandle = handle;
            Label = label ?? string.Empty;
            _creationSite = LeakTracker.CaptureSite();
        }

        /// <summary>单个观察者，非多播；再次赋值会静默覆盖上一个。</summary>
        public IDataChannelObserver Observer
        {
            get => _observer;
            set
            {
                MainThread.Assert("DataChannel.Observer");
                _observer = value;
            }
        }

        public int BufferedAmount
        {
            get
            {
                MainThread.Assert("DataChannel.BufferedAmount");
                ThrowIfDisposed();
                DataChannelRuntime.RequireOk(
                    NativeMethods.dcu_dc_buffered_amount(NativeHandle, out var n),
                    "dcu_dc_buffered_amount");
                return n;
            }
        }

        public void Send(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            Send(data, 0, data.Length);
        }

        public void Send(byte[] data, int offset, int count)
        {
            MainThread.Assert("DataChannel.Send");
            ThrowIfDisposed();
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            // **刻意不预检 open 状态**（#32 决议 1）：门禁建在缓存上时，一次丢失的
            // 通知就等于一个永久卡死的通道。未 open 时发送由原生侧失败并如实报错。
            // （上面这个越界检查在 int.MaxValue 附近会溢出，改写归 S7。）

            byte[] slice = data;
            if (offset != 0 || count != data.Length)
            {
                slice = new byte[count];
                Buffer.BlockCopy(data, offset, slice, 0, count);
            }

            DataChannelRuntime.RequireOk(
                NativeMethods.dcu_dc_send(NativeHandle, slice, count),
                "dcu_dc_send");
        }

        public void Send(ReadOnlySpan<byte> data)
        {
            MainThread.Assert("DataChannel.Send");
            ThrowIfDisposed();
            var arr = data.ToArray();
            DataChannelRuntime.RequireOk(
                NativeMethods.dcu_dc_send(NativeHandle, arr, arr.Length),
                "dcu_dc_send");
        }

        public void Dispose()
        {
            MainThread.Assert("DataChannel.Dispose");
            if (!MarkDisposed()) return;
            ReleaseNative();
            // 自行释放时必须把自己从父 PC 的子列表里摘掉，否则父 Dispose 会二次销毁
            // 同一个句柄（#29 决议 1）。级联路径不走这里 —— 父在遍历前已清空列表。
            Peer.ForgetChild(this);
        }

        /// <summary>
        /// 级联释放：由父 <see cref="PeerConnection.Dispose"/> 调用。
        /// </summary>
        /// <remarks>
        /// 行为与自行 Dispose **一致**，只有两处刻意的差别（#29 决议 6）：
        /// 后续误用的异常消息会点明成因是「父 PC 被释放」，以及
        /// **不触发 <see cref="Closed"/>** —— 触发它会在 PC 处于「已标记 disposed、
        /// 子列表遍历到一半」的中间态上跑用户回调（重入），而且 <c>Closed</c> 的语义
        /// 应当是「通道被关闭了」而不是「你自己刚把它释放了」。
        /// </remarks>
        internal void DisposeFromParent()
        {
            _disposedByParent = true;
            if (!MarkDisposed()) return;
            ReleaseNative();
        }

        private bool MarkDisposed()
        {
            lock (_gate)
            {
                if (_disposed) return false;
                _disposed = true;
            }
            return true;
        }

        private void ReleaseNative()
        {
            try { NativeMethods.dcu_dc_close(NativeHandle); } catch { /* ignore */ }
            try { NativeMethods.dcu_dc_destroy(NativeHandle); } catch { /* ignore */ }
            HandleTable.UnregisterDc(NativeHandle);
            GC.SuppressFinalize(this);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// **只做一次无锁入队**，别的什么都不做（SPEC §6 / #29 决议 8）。
        /// 不调 <c>dcu_*</c>、不取 HandleTable 的锁、不碰 <c>Debug.Log*</c>。
        /// 因此原生对象在这里**没有**被销毁 —— 那正是要报告的泄漏本身。
        /// </summary>
        ~DataChannel()
        {
            // 构造函数抛出时句柄还是 0，什么都没分配 —— 报出来是纯噪音。见 PeerConnection。
            if (NativeHandle == 0) return;

            LeakTracker.ReportFromFinalizer(new LeakRecord
            {
                IsPeerConnection = false,
                Handle = NativeHandle,
                Label = Label,
                CreationSite = _creationSite
            });
        }
#endif

        private void ThrowIfDisposed()
        {
            if (!_disposed) return;
            throw _disposedByParent
                ? new ObjectDisposedException(nameof(DataChannel),
                    "该通道随其 PeerConnection 一起被级联释放（PeerConnection.Dispose 会带走它的所有子通道）。"
                    + "若需要通道活得比 PC 久，那是不成立的 —— 原生侧的子通道句柄在 PC 销毁后即悬空。")
                : new ObjectDisposedException(nameof(DataChannel));
        }

        /// <summary>
        /// 诊断用文本。句柄收 internal 之后这是它的唯一公开出口，见
        /// <see cref="PeerConnection.ToString"/>。
        /// </summary>
        public override string ToString() =>
            "DataChannel(handle=" + NativeHandle + ", label=\"" + Label + "\", disposed=" + _disposed + ")";

        internal void RaiseOpen()
        {
            Opened?.Invoke();
            _observer?.OnOpen();
        }

        internal void RaiseClosed()
        {
            Closed?.Invoke();
            _observer?.OnClosed();
        }

        internal void RaiseError(string message)
        {
            ErrorOccurred?.Invoke(message);
            _observer?.OnError(message);
        }

        internal void RaiseMessage(ReadOnlySpan<byte> data)
        {
            MessageReceived?.Invoke(data);
            _observer?.OnMessage(data);
        }
    }
}
