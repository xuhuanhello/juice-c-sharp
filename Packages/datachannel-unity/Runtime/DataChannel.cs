using System;
using DataChannelUnity.Internal;

namespace DataChannelUnity
{
    public sealed class DataChannel : IDisposable
    {
        private readonly object _gate = new object();
        private bool _disposed;
        private bool _open;
        private IDataChannelObserver _observer;

        public PeerConnection Peer { get; }
        internal int NativeHandle { get; }
        public string Label { get; }
        public bool IsOpen => _open && !_disposed;

        public event Action Opened;
        public event Action Closed;
        public event Action<string> ErrorOccurred;
        public event Action<byte[]> MessageReceived;

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
            if (!_open)
                throw new InvalidOperationException("DataChannel is not open.");

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
            if (!_open)
                throw new InvalidOperationException("DataChannel is not open.");
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
                _open = false;
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
            _open = true;
            Opened?.Invoke();
            _observer?.OnOpen();
        }

        internal void RaiseClosed()
        {
            _open = false;
            Closed?.Invoke();
            _observer?.OnClosed();
        }

        internal void RaiseError(string message)
        {
            ErrorOccurred?.Invoke(message);
            _observer?.OnError(message);
        }

        internal void RaiseMessage(byte[] data)
        {
            MessageReceived?.Invoke(data);
            _observer?.OnMessage(data);
        }
    }
}
