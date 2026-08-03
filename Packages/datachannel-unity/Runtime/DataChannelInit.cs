using System;

namespace DataChannelUnity
{
    [Serializable]
    public sealed class DataChannelInit
    {
        public bool Ordered { get; set; } = true;
        public bool Reliable { get; set; } = true;
        public uint MaxRetransmits { get; set; }
        public uint MaxPacketLifeTime { get; set; }
    }
}
