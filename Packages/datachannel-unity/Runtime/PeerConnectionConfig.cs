using System.Collections.Generic;

namespace DataChannelUnity
{
    /// <summary>
    /// PeerConnection 创建配置。**只在创建时生效**，不支持运行时增删 ICE 服务器
    /// （要改就重建 PeerConnection）。
    /// </summary>
    /// <remarks>
    /// 刻意**没有** <c>[Serializable]</c>：理由见 <see cref="IceServer"/>。
    /// </remarks>
    public sealed class PeerConnectionConfig
    {
        /// <summary>
        /// 表示「由后端自动决定」的取值。用于 <see cref="Mtu"/>、<see cref="MaxMessageSize"/>、
        /// <see cref="PortRangeBegin"/>、<see cref="PortRangeEnd"/>。
        /// </summary>
        public const int Automatic = 0;

        /// <summary>ICE 服务器列表。可以为空（只做 host/本地候选收集）。</summary>
        public List<IceServer> IceServers { get; set; } = new List<IceServer>();

        public IceTransportPolicy TransportPolicy { get; set; } = IceTransportPolicy.All;

        /// <summary>本地端口范围下界。<see cref="Automatic"/> 表示自动。</summary>
        public ushort PortRangeBegin { get; set; } = Automatic;

        /// <summary>本地端口范围上界。<see cref="Automatic"/> 表示自动。</summary>
        public ushort PortRangeEnd { get; set; } = Automatic;

        /// <summary>绑定地址，可选。仅 libjuice 有效；**WebGL 上被忽略**。</summary>
        public string BindAddress { get; set; }

        /// <summary>启用 ICE-TCP。WebGL 上可能是空操作。</summary>
        public bool EnableIceTcp { get; set; }

        /// <summary>启用 ICE UDP mux。仅 libjuice 有效；**WebGL 上被忽略**。</summary>
        public bool EnableIceUdpMux { get; set; }

        /// <summary>网络 MTU。<see cref="Automatic"/> 表示自动。</summary>
        public int Mtu { get; set; } = Automatic;

        /// <summary>DataChannel 本地最大消息尺寸。<see cref="Automatic"/> 表示用默认值。</summary>
        public int MaxMessageSize { get; set; } = Automatic;
    }
}
