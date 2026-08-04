using System;
using DataChannelUnity.Internal;

namespace DataChannelUnity
{
    public sealed class DataChannel : IDisposable
    {
        private readonly object _gate = new object();
        private bool _disposed;
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

        internal DataChannel(PeerConnection peer, int handle, string label, bool ownsCreation)
        {
            Peer = peer ?? throw new ArgumentNullException(nameof(peer));
            NativeHandle = handle;
            Label = label ?? string.Empty;
        }

        /// <summary>单个观察者，非多播；再次赋值会静默覆盖上一个。</summary>
        public IDataChannelObserver Observer
        {
            get => _observer;
            set => _observer = value;
        }

        public int BufferedAmount
        {
            get
            {
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
            ThrowIfDisposed();
            var arr = data.ToArray();
            DataChannelRuntime.RequireOk(
                NativeMethods.dcu_dc_send(NativeHandle, arr, arr.Length),
                "dcu_dc_send");
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            try { NativeMethods.dcu_dc_close(NativeHandle); } catch { /* ignore */ }
            try { NativeMethods.dcu_dc_destroy(NativeHandle); } catch { /* ignore */ }
            HandleTable.UnregisterDc(NativeHandle);
            GC.SuppressFinalize(this);
        }

        ~DataChannel()
        {
            try
            {
                if (!_disposed)
                {
                    NativeMethods.dcu_dc_destroy(NativeHandle);
                    HandleTable.UnregisterDc(NativeHandle);
                }
            }
            catch { /* finalizer */ }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DataChannel));
        }

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
