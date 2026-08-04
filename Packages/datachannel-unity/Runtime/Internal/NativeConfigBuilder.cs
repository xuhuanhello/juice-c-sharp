using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DataChannelUnity.Internal
{
    /// <summary>
    /// Builds native-config heap blocks for dcu_pc_create. Caller must Dispose.
    /// </summary>
    internal sealed class NativeConfigBuilder : IDisposable
    {
        private readonly List<IntPtr> _owned = new List<IntPtr>();
        private bool _disposed;

        public NativeMethods.PcConfigNative Config;

        /// <summary>
        /// 构造期间的分配用 try/catch 包住（#29 决议 7）。
        /// </summary>
        /// <remarks>
        /// 构造函数抛出时，调用方拿不到对象，因此**永远不会**去 Dispose 它 ——
        /// 而此时已经 <c>AllocHGlobal</c> 出去的那几块就彻底没人管了。
        /// 本类刻意没有终结器兜底（那会把非托管释放搬到 GC 线程），所以自己收拾。
        /// </remarks>
        public NativeConfigBuilder(PeerConnectionConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            try
            {
                Build(config);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void Build(PeerConnectionConfig config)
        {
            var servers = config.IceServers ?? new List<IceServer>();
            var serverStructs = new NativeMethods.IceServerNative[Math.Max(servers.Count, 0)];

            for (int i = 0; i < servers.Count; i++)
            {
                var s = servers[i] ?? new IceServer();
                var urls = s.Urls ?? new List<string>();
                var urlPtrs = new IntPtr[urls.Count];
                for (int u = 0; u < urls.Count; u++)
                    urlPtrs[u] = AllocUtf8(urls[u] ?? string.Empty);

                IntPtr urlsArray = IntPtr.Zero;
                if (urlPtrs.Length > 0)
                {
                    urlsArray = Marshal.AllocHGlobal(IntPtr.Size * urlPtrs.Length);
                    _owned.Add(urlsArray);
                    for (int u = 0; u < urlPtrs.Length; u++)
                        Marshal.WriteIntPtr(urlsArray, u * IntPtr.Size, urlPtrs[u]);
                }

                serverStructs[i] = new NativeMethods.IceServerNative
                {
                    urls = urlsArray,
                    url_count = urlPtrs.Length,
                    username = string.IsNullOrEmpty(s.Username) ? IntPtr.Zero : AllocUtf8(s.Username),
                    credential = string.IsNullOrEmpty(s.Credential) ? IntPtr.Zero : AllocUtf8(s.Credential)
                };
            }

            IntPtr serversPtr = IntPtr.Zero;
            if (serverStructs.Length > 0)
            {
                int size = Marshal.SizeOf<NativeMethods.IceServerNative>();
                serversPtr = Marshal.AllocHGlobal(size * serverStructs.Length);
                _owned.Add(serversPtr);
                for (int i = 0; i < serverStructs.Length; i++)
                    Marshal.StructureToPtr(serverStructs[i], serversPtr + i * size, false);
            }

            Config = new NativeMethods.PcConfigNative
            {
                ice_servers = serversPtr,
                ice_server_count = serverStructs.Length,
                transport_policy = (int)config.TransportPolicy,
                port_range_begin = config.PortRangeBegin,
                port_range_end = config.PortRangeEnd,
                bind_address = string.IsNullOrEmpty(config.BindAddress) ? IntPtr.Zero : AllocUtf8(config.BindAddress),
                enable_ice_tcp = config.EnableIceTcp ? 1 : 0,
                enable_ice_udp_mux = config.EnableIceUdpMux ? 1 : 0,
                mtu = config.Mtu,
                max_message_size = config.MaxMessageSize
            };
        }

        private IntPtr AllocUtf8(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s + "\0");
            var p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            _owned.Add(p);
            return p;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                if (_owned[i] != IntPtr.Zero)
                    Marshal.FreeHGlobal(_owned[i]);
            }
            _owned.Clear();
        }
    }
}
