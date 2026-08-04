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

        private DataChannelMessageHandler _messageReceived;
        private DataChannelMessageHandler[] _messageHandlers = Array.Empty<DataChannelMessageHandler>();

        /// <summary>
        /// 收到消息。载荷**只在回调期间有效**，见 <see cref="DataChannelMessageHandler"/>。
        /// </summary>
        /// <remarks>
        /// 这个事件写了自定义的 add/remove，只为**缓存 invocation list**：
        /// <c>GetInvocationList()</c> 每次调用分配一个 <c>Delegate[]</c>，放在刚做成
        /// 零分配的消息路径上，等于把「每条消息一个 <c>byte[]</c>」换成
        /// 「每条消息一个 <c>Delegate[]</c>」—— 白改。快照只在订阅变化时重建。
        ///
        /// 顺带解决另一件事：在回调里退订自己是合法且常见的写法，遍历快照天然安全。
        /// </remarks>
        public event DataChannelMessageHandler MessageReceived
        {
            add
            {
                _messageReceived = (DataChannelMessageHandler)Delegate.Combine(_messageReceived, value);
                RebuildMessageHandlers();
            }
            remove
            {
                _messageReceived = (DataChannelMessageHandler)Delegate.Remove(_messageReceived, value);
                RebuildMessageHandlers();
            }
        }

        private void RebuildMessageHandlers()
        {
            if (_messageReceived == null)
            {
                _messageHandlers = Array.Empty<DataChannelMessageHandler>();
                return;
            }
            var list = _messageReceived.GetInvocationList();
            var snapshot = new DataChannelMessageHandler[list.Length];
            for (int i = 0; i < list.Length; i++)
                snapshot[i] = (DataChannelMessageHandler)list[i];
            _messageHandlers = snapshot;
        }

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

        /// <summary>
        /// 空载荷时借它取一个**非空**指针。
        /// </summary>
        /// <remarks>
        /// <c>fixed</c> 作用在空数组 / 空 Span 上得到的是**空指针**，而「传 <c>null</c> +
        /// 长度 0 是否等价于一条空消息」是我们**没有核实过**的上游行为。绕开一个未验证
        /// 的假设只要这一行 —— 这正是 #31 立的方法论：要么把巧合变成被检查的不变量，
        /// 要么干脆绕开它。零长度消息本身是合法的（WebRTC 语义允许，常被当心跳用）。
        /// </remarks>
        private static readonly byte[] EmptyPayloadSentinel = new byte[1];

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
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must not be negative.");
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Count must not be negative.");
            // **写成减法而不是加法。** offset 与 count 此刻已知非负，所以
            // data.Length - offset 不可能溢出；而 offset + count 在 int.MaxValue
            // 附近会回绕成负数，让这个检查**整个失效**。有了 fixed 之后，
            // 这是越界读之前的最后一道闸 —— 原先那句加法是真的能被绕过去的。
            if (data.Length - offset < count)
                throw new ArgumentOutOfRangeException(nameof(count),
                    "offset + count exceeds the array length (length=" + data.Length
                    + ", offset=" + offset + ", count=" + count + "）。");

            SendCore(new ReadOnlySpan<byte>(data, offset, count));
        }

        public void Send(ReadOnlySpan<byte> data)
        {
            MainThread.Assert("DataChannel.Send");
            ThrowIfDisposed();
            SendCore(data);
        }

        /// <summary>三个重载共用的零拷贝发送。</summary>
        /// <remarks>
        /// **刻意不预检 open 状态**（#32 决议 1）：门禁建在缓存上时，一次丢失的
        /// 通知就等于一个永久卡死的通道。未 open 时发送由原生侧失败并如实报错。
        /// </remarks>
        private unsafe void SendCore(ReadOnlySpan<byte> data)
        {
            DataChannelRuntime.CheckPumpLiveness("DataChannel.Send");

            fixed (byte* payload = data)
            fixed (byte* sentinel = EmptyPayloadSentinel)
            {
                var p = data.Length == 0 ? sentinel : payload;
                DataChannelRuntime.RequireOk(
                    NativeMethods.dcu_dc_send(NativeHandle, (IntPtr)p, data.Length),
                    "dcu_dc_send");
            }
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
                    "This channel is disposed in cascade with its PeerConnection (PeerConnection.Dispose takes every child channel with it). "
                    + "A channel cannot outlive its PeerConnection: the native child handle dangles once the PC is destroyed.")
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
            SafeDispatch.Invoke(Opened, "DataChannel.Opened");
            var obs = _observer;
            if (obs != null) SafeDispatch.Observer(obs.OnOpen, "IDataChannelObserver.OnOpen");
        }

        internal void RaiseClosed()
        {
            SafeDispatch.Invoke(Closed, "DataChannel.Closed");
            var obs = _observer;
            if (obs != null) SafeDispatch.Observer(obs.OnClosed, "IDataChannelObserver.OnClosed");
        }

        internal void RaiseError(string message)
        {
            SafeDispatch.Invoke(ErrorOccurred, message, "DataChannel.ErrorOccurred");
            var obs = _observer;
            if (obs != null) SafeDispatch.Observer(() => obs.OnError(message), "IDataChannelObserver.OnError");
        }

        /// <summary>
        /// 消息派发。走**缓存快照**而非 <c>GetInvocationList()</c>，且逐订阅者隔离。
        /// </summary>
        /// <remarks>
        /// 这里没法复用 <see cref="SafeDispatch"/> 的泛型重载：载荷是
        /// <c>ReadOnlySpan&lt;byte&gt;</c>，C# 9 下 ref struct 不能作泛型实参，
        /// 也不能被 lambda 捕获。所以这一份 try/catch 是手写的。
        /// </remarks>
        internal void RaiseMessage(ReadOnlySpan<byte> data)
        {
            // 取一次引用即可：订阅变化会**换掉**这个数组而不是改它，
            // 所以回调里退订自己对本次遍历天然无害。
            var handlers = _messageHandlers;
            for (int i = 0; i < handlers.Length; i++)
            {
                try { handlers[i](data); }
                catch (Exception e) { SafeDispatch.Report("DataChannel.MessageReceived", e); }
            }

            var obs = _observer;
            if (obs == null) return;
            try { obs.OnMessage(data); }
            catch (Exception e) { SafeDispatch.Report("IDataChannelObserver.OnMessage", e); }
        }
    }
}
