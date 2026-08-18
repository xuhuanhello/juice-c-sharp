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
            DataChannelRuntime.Preload(); // #146 被动化后，读属性不再触发加载 —— 测试侧显式预热。
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

        // ------------------------------------------------------------------
        // PeerConnectionConfig 的组装点校验（#151：明确无意义的输入在边界失败）
        // ------------------------------------------------------------------

        /// <summary>倒置端口区间在构造时抛 —— 旧行为是直通上游，失败成难归因的 ICE 错误。</summary>
        [Test]
        public void InvertedPortRange_ThrowsAtConstruction()
        {
            DataChannelRuntime.Preload(); // 校验在 EnsureNative 之后才会走到，先确保原生在场。
            var cfg = new PeerConnectionConfig { PortRangeBegin = 5000, PortRangeEnd = 4000 };
            Assert.Throws<ArgumentException>(() => new PeerConnection(cfg),
                "begin > end binds nothing; it must fail at the construction boundary, not downstream.");
        }

        /// <summary>空 IceServer（new 了忘填 URL）在构造时抛 —— 旧行为是静默跳过，typo 无声消失。</summary>
        [Test]
        public void IceServerWithNoUrls_ThrowsAtConstruction()
        {
            DataChannelRuntime.Preload();
            var cfg = new PeerConnectionConfig();
            cfg.IceServers.Add(new IceServer());
            Assert.Throws<ArgumentException>(() => new PeerConnection(cfg),
                "An IceServer with no URLs does nothing; silently skipping it hides a construction slip.");
        }
    }
}
