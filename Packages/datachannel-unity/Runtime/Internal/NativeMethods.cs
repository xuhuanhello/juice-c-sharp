using System;
using System.Runtime.InteropServices;

namespace DataChannelUnity.Internal
{
    internal static class NativeMethods
    {
#if UNITY_IOS && !UNITY_EDITOR
        public const string DllName = "__Internal";
#elif UNITY_WEBGL && !UNITY_EDITOR
        public const string DllName = "__Internal";
#else
        public const string DllName = "datachannel_unity";
#endif

        // 独立编号，刻意不与上游 RTC_ERR_* 逐值相同（SPEC §4）。
        public const int Success = 0;
        public const int ErrInvalid = (int)DataChannelError.Invalid;
        public const int ErrFailure = (int)DataChannelError.Failure;
        public const int ErrNotAvail = (int)DataChannelError.NotAvailable;
        public const int ErrTooSmall = (int)DataChannelError.TooSmall;
        public const int ErrUpstreamUnknown = (int)DataChannelError.UpstreamUnknown;

        /// <summary>状态枚举越界时原生侧带出的值（dcu.h 的 DCU_STATE_UNKNOWN）。</summary>
        public const int StateUnknown = -1;

        /// <summary>dcu.h 的 DCU_LABEL_MAX_BYTES。实测上界，不是理论值。</summary>
        public const int LabelMaxBytes = 65535;

        public enum EventType : int
        {
            None = 0,
            LocalDescription = 1,
            LocalCandidate = 2,
            ConnectionState = 3,
            GatheringState = 4,
            IncomingDataChannel = 5,
            DcOpen = 6,
            DcClosed = 7,
            DcError = 8
            // 没有 DcMessage：消息不进事件队列，改由 dcu_dc_receive 逐通道拉取。
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EventHeader
        {
            public EventType type;
            public int pc;
            public int dc;
            public int state;
            public int payload_len;
            public int payload2_len;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IceServerNative
        {
            public IntPtr urls;           // char**
            public int url_count;
            public IntPtr username;       // char*
            public IntPtr credential;     // char*
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PcConfigNative
        {
            public IntPtr ice_servers;    // IceServerNative*
            public int ice_server_count;
            public int transport_policy;  // 0 All, 1 RelayOnly
            public ushort port_range_begin;
            public ushort port_range_end;
            public IntPtr bind_address;
            public int enable_ice_tcp;    // bool as int for stable layout
            public int enable_ice_udp_mux;
            public int mtu;
            public int max_message_size;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DcInitNative
        {
            public int ordered;
            public int reliable;
            public uint max_retransmits;
            public uint max_packet_lifetime;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_abi_version(out int version);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_init();

        /// <summary>
        /// 关闭并回收；<paramref name="undestroyed"/> 带出此刻**仍未被销毁**的对象数。
        /// </summary>
        /// <remarks>
        /// 计数由 dcu 层自己的句柄表给出，**不依赖上游也不依赖日志桥** ——
        /// 上游 <c>rtcCleanup()</c> 返回 void，还把「N objects were not properly
        /// destroyed」和「Cleanup timeout」两条最有价值的诊断 try/catch 吞进 plog，
        /// 于是它在死锁时也报成功。
        /// </remarks>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_shutdown(out int undestroyed);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_set_log_level(int level);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_pc_create(ref PcConfigNative config, out int pc);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_pc_close(int pc);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_pc_destroy(int pc);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_pc_set_remote_description(
            int pc, byte[] sdp, int sdp_len, byte[] type, int type_len);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_pc_add_remote_candidate(
            int pc, byte[] cand, int cand_len, byte[] mid, int mid_len);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_pc_create_data_channel(
            int pc, byte[] label, int label_len, ref DcInitNative init, out int dc);

        // 判定这条连接走的是直连还是中继，并带出远端候选的 SDP。缓冲语义：
        // 长度先填精确值再判容量（TooSmall 时 verdict 已写入）；这是**活查询**，
        // 没有队首可消费 —— 重试是重新查询，结果可能随连接状态变化。
        // 未连接或无选中候选对时返回 ErrNotAvail。判据在 native 侧合成，见 dcu.h。
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_pc_connection_path(
            int pc, out int verdict, byte[] buf, int cap, out int len);

        /// <summary>
        /// 发送。**声明成 <see cref="IntPtr"/> 不是 ABI 变更** —— C 侧
        /// <c>dcu_dc_send(int, const void*, int)</c> 一字未动，改的只是这里的封送方式。
        /// </summary>
        /// <remarks>
        /// 澄清一个容易反过来记的事实：blittable <c>byte[]</c> 的默认封送本来就是
        /// **钉住而非复制**，所以 <c>Send(byte[])</c> 全量发送从来不是问题；真正在复制的
        /// 是 <c>offset != 0</c> 的切片和 <c>ReadOnlySpan.ToArray()</c> —— 后者把 Span
        /// 重载存在的全部意义抹平了。改 <c>IntPtr</c> + <c>fixed</c> 之后三条路径都零拷贝。
        /// </remarks>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_dc_send(int dc, IntPtr data, int len);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_dc_close(int dc);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_dc_destroy(int dc);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_dc_buffered_amount(int dc, out int amount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_dc_state(int dc, out int state);

        /// <summary>仅供契约测试，默认惰性。见 dcu.h。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_test_set_open_race_delay_ms(int ms);

        // 单次原子取事件：填充 header + 两段载荷并弹出；缓冲不足则填好 header
        // （含两个精确长度）但**不弹出**，返回 ErrTooSmall。
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_event_next(out EventHeader header,
            byte[] buf, int cap, byte[] buf2, int cap2);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_event_queue_depth(out int depth);

        // 取一条桥接过来的原生日志。dropped 是「自上次读取以来丢弃的条数」，
        // 队列为空时也会填。
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_log_next(out int level, byte[] buf, int cap,
            out int len, out int dropped);

        // 语义与上游 rtcReceiveMessage 相同：peek -> 拷贝 -> 成功才丢弃。
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_dc_receive(int dc, byte[] buf, int cap, out int len);
    }
}
