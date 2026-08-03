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

        public const int Success = 0;
        public const int ErrInvalid = -1;
        public const int ErrFailure = -2;
        public const int ErrNotAvail = -3;
        public const int ErrTooSmall = -4;

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
            DcError = 8,
            DcMessage = 9
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
        public static extern int dcu_abi_version();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_init();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_shutdown();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_set_log_level(int level);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_pc_create(ref PcConfigNative config);

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
            int pc, byte[] label, int label_len, ref DcInitNative init);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_dc_send(int dc, byte[] data, int len);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_dc_close(int dc);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_dc_destroy(int dc);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_dc_buffered_amount(int dc);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_event_peek(out EventHeader header);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_event_copy_payload(byte[] buffer, int capacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_event_copy_payload2(byte[] buffer, int capacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dcu_event_pop();
    }
}
