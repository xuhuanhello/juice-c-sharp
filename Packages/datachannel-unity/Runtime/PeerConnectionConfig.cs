using System;
using System.Collections.Generic;

namespace DataChannelUnity
{
    [Serializable]
    public sealed class PeerConnectionConfig
    {
        public List<IceServer> IceServers { get; set; } = new List<IceServer>();
        public IceTransportPolicy TransportPolicy { get; set; } = IceTransportPolicy.All;
        public ushort PortRangeBegin { get; set; }
        public ushort PortRangeEnd { get; set; }
        public string BindAddress { get; set; }
        public bool EnableIceTcp { get; set; }
        public bool EnableIceUdpMux { get; set; }
        public int Mtu { get; set; }
        public int MaxMessageSize { get; set; }
    }
}
