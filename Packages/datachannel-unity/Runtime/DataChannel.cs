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
        public int NativeHandle { get; }
        public string Label { get; }
        public bool IsOpen => _open && !_disposed;

        public event Action Open;
        public event Action Closed;
        public event Action<string> Error;
        public event Action<byte[]> Message;

        internal DataChannel(PeerConnection peer, int handle, string label, bool ownsCreation)
        {
            Peer = peer ?? throw new ArgumentNullException(nameof(peer));
            NativeHandle = handle;
            Label = label ?? string.Empty;
        }

        public void SetObserver(IDataChannelObserver observer) => _observer = observer;

        public int BufferedAmount
        {
            get
            {
                ThrowIfDisposed();
                var n = NativeMethods.dcu_dc_buffered_amount(NativeHandle);
                if (n < 0)
                    throw new DataChannelException("dcu_dc_buffered_amount failed", n);
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

#if NET_STANDARD_2_1 || UNITY_2021_2_OR_NEWER
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
#endif

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
            Open?.Invoke();
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
            Error?.Invoke(message);
            _observer?.OnError(message);
        }

        internal void RaiseMessage(byte[] data)
        {
            Message?.Invoke(data);
            _observer?.OnMessage(data);
        }
    }
}
