using System;

namespace DataChannelUnity
{
    /// <summary>
    /// DataChannel 创建参数。对应 W3C 的 <c>RTCDataChannelInit</c>（领域词汇，故保留这个 C 味的名字）。
    /// </summary>
    /// <remarks>
    /// 刻意**没有** <c>[Serializable]</c>：理由见 <see cref="IceServer"/>。
    /// </remarks>
    public sealed class DataChannelInit
    {
        /// <summary>是否保序投递。默认 <c>true</c>。</summary>
        public bool Ordered { get; set; } = true;

        /// <summary>
        /// 是否可靠投递。默认 <c>true</c>。为 <c>true</c> 时
        /// <see cref="MaxRetransmits"/> 与 <see cref="MaxPacketLifeTime"/> 都必须为 0。
        /// </summary>
        public bool Reliable { get; set; } = true;

        /// <summary>最大重传次数。仅在 <see cref="Reliable"/> 为 <c>false</c> 时有意义。</summary>
        public uint MaxRetransmits { get; set; }

        /// <summary>传输与重传的时间窗（毫秒）。仅在 <see cref="Reliable"/> 为 <c>false</c> 时有意义。</summary>
        public uint MaxPacketLifeTime { get; set; }

        /// <summary>
        /// 校验可靠性设置的互斥关系，不合法则抛 <see cref="ArgumentException"/>。
        /// 由 <see cref="PeerConnection.CreateDataChannel"/> 调用。
        /// </summary>
        /// <remarks>
        /// W3C 规范里 maxRetransmits 与 maxPacketLifeTime 互斥；我们多一个 <see cref="Reliable"/>
        /// 标志，规则因而更严也更清楚。不校验也会失败 —— 上游
        /// <c>impl/datachannel.cpp</c> 会抛 <c>"Both maxPacketLifeTime and maxRetransmits are set"</c>。
        /// 在 C# 侧拦是为了给出**能直接照做**的错误信息，而不是一个笼统的传输层错误码。
        /// </remarks>
        internal void Validate()
        {
            if (Reliable)
            {
                if (MaxRetransmits != 0 || MaxPacketLifeTime != 0)
                    throw new ArgumentException(
                        "DataChannelInit: when Reliable = true, both MaxRetransmits and MaxPacketLifeTime must be 0. "
                        + "For unreliable delivery, set Reliable = false first, then set one of them.",
                        nameof(Reliable));
            }
            else if (MaxRetransmits != 0 && MaxPacketLifeTime != 0)
            {
                throw new ArgumentException(
                    "DataChannelInit: MaxRetransmits and MaxPacketLifeTime are mutually exclusive; set at most one (leave the other at 0).",
                    nameof(MaxRetransmits));
            }
        }
    }
}
