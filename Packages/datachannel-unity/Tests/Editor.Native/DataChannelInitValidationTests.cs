using System;
using NUnit.Framework;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：需要插件加载。校验发生在 <see cref="PeerConnection.CreateDataChannel"/> 里，
    /// 而构造一个 PeerConnection 就需要原生 —— 所以这组用例够不着托管档。
    /// </summary>
    public sealed class DataChannelInitValidationTests
    {
        private static PeerConnection NewPeer()
        {
            // 缺席必须是失败，不是跳过（SPEC §11）。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip. If the plugin was just rebuilt, restart the Editor.");
            return new PeerConnection(new PeerConnectionConfig());
        }

        [Test]
        public void Reliable_WithMaxRetransmits_Throws()
        {
            using (var pc = NewPeer())
            {
                var init = new DataChannelInit { Reliable = true, MaxRetransmits = 3 };
                Assert.Throws<ArgumentException>(() => pc.CreateDataChannel("bad", init));
            }
        }

        [Test]
        public void Reliable_WithMaxPacketLifeTime_Throws()
        {
            using (var pc = NewPeer())
            {
                var init = new DataChannelInit { Reliable = true, MaxPacketLifeTime = 100 };
                Assert.Throws<ArgumentException>(() => pc.CreateDataChannel("bad", init));
            }
        }

        [Test]
        public void Unreliable_WithBothLimits_Throws()
        {
            using (var pc = NewPeer())
            {
                var init = new DataChannelInit
                {
                    Reliable = false, MaxRetransmits = 3, MaxPacketLifeTime = 100
                };
                // 上游也会抛（impl/datachannel.cpp: "Both maxPacketLifeTime and maxRetransmits are set"），
                // 但那会变成一个笼统的传输层错误码。在 C# 侧拦是为了给出能直接照做的信息。
                Assert.Throws<ArgumentException>(() => pc.CreateDataChannel("bad", init));
            }
        }

        [Test]
        public void Unreliable_WithExactlyOneLimit_IsAccepted()
        {
            using (var pc = NewPeer())
            {
                var init = new DataChannelInit { Reliable = false, MaxRetransmits = 3 };
                var dc = pc.CreateDataChannel("ok", init);
                Assert.IsNotNull(dc);
                dc.Dispose();
            }
        }

        [Test]
        public void Default_IsReliableOrdered_AndAccepted()
        {
            using (var pc = NewPeer())
            {
                var dc = pc.CreateDataChannel("ok");
                Assert.IsNotNull(dc);
                dc.Dispose();
            }
        }
    }
}
