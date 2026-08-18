using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：所有权与生命周期（#29 / SPEC §6）。
    ///
    /// 这组守的是 S6 的核心决议 —— **PeerConnection 拥有它的 DataChannel**，
    /// <c>Dispose</c> 级联且**先子后父**。级联不是洁癖：上游
    /// <c>rtcDeletePeerConnection</c> 只 close + 摘掉 PC 自己，不清子通道的表项
    /// （<c>capi.cpp:437-444</c>），不级联漏的就不只是我们那张表，连 libdatachannel
    /// 自己的 <c>dataChannelMap</c> 一起漏。
    ///
    /// <c>Dispose</c> 幂等**刻意分在两个档**（SPEC §11）：托管档只能证明托管侧不炸，
    /// 证不了原生句柄没被销毁两次。合成一条会让「幂等已覆盖」掩盖原生侧没测。
    /// </summary>
    public sealed class OwnershipLifetimeTests
    {
        private readonly List<string> _captured = new List<string>();

        /// <summary>句柄不存在时 <c>DcuHandleTable</c> 抛的文本，经日志桥到达托管侧。</summary>
        private const string StaleDcHandle = "DataChannel handle does not exist";
        private const string StalePcHandle = "PeerConnection handle does not exist";

        private void Capture(LogLevel level, string message) => _captured.Add(message);

        [SetUp]
        public void Setup()
        {
            // 缺席必须是失败，不是跳过（SPEC §11）。
            DataChannelRuntime.Preload(); // #146 被动化后，读属性不再触发加载 —— 测试侧显式预热。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip. If the plugin was just rebuilt, restart the Editor.");
            _captured.Clear();
            DataChannelLog.MessageLogged += Capture;
        }

        [TearDown]
        public void Teardown()
        {
            LogAssert.ignoreFailingMessages = true;
            PumpAWhile();   // 谁触发，谁负责排空 —— 别把原生日志留给下一个测试
            DataChannelLog.MessageLogged -= Capture;
        }

        private static void PumpAWhile(int iterations = 40)
        {
            for (int i = 0; i < iterations; i++)
            {
                DataChannelRuntime.Pump();
                Thread.Sleep(2);
            }
        }

        [Test]
        public void PeerConnectionDispose_CascadesToItsChannels()
        {
            var pc = new PeerConnection(new PeerConnectionConfig());
            var a = pc.CreateDataChannel("cascade-a");
            var b = pc.CreateDataChannel("cascade-b");

            pc.Dispose();

            Assert.AreEqual(DataChannelState.Closed, a.State, "Child channel a was not disposed in cascade.");
            Assert.AreEqual(DataChannelState.Closed, b.State, "Child channel b was not disposed in cascade.");
            Assert.Throws<ObjectDisposedException>(() => a.Send(new byte[] { 1 }));
            Assert.Throws<ObjectDisposedException>(() => b.Send(new byte[] { 1 }));
        }

        [Test]
        public void CascadeDisposedChannel_ExceptionMessageNamesTheCause()
        {
            var pc = new PeerConnection(new PeerConnectionConfig());
            var dc = pc.CreateDataChannel("cause");
            pc.Dispose();

            var ex = Assert.Throws<ObjectDisposedException>(() => dc.Send(new byte[] { 1 }));

            // 行为与自行 Dispose 一致，**只有消息不同**（#29 决议 6）。这一句是整个
            // 级联设计里唯一朝用户暴露的地方 —— 少了它，「我明明没释放这个通道」
            // 会变成一次纯猜谜。
            StringAssert.Contains("cascade", ex.Message,
                "For a cascade-disposed channel, ObjectDisposedException must name the parent PC disposal as the cause.");
        }

        [Test]
        public void CascadeDisposedChannel_DoesNotRaiseClosed()
        {
            var pc = new PeerConnection(new PeerConnectionConfig());
            var dc = pc.CreateDataChannel("no-closed");
            var closedFired = false;
            dc.Closed += () => closedFired = true;

            pc.Dispose();
            PumpAWhile();   // 原生 close 会推一条 DcClosed 进队列；它必须查不到句柄而被丢弃

            // 触发 Closed 会在 PC「已标记 disposed、子列表遍历到一半」的中间态上跑
            // 用户回调（重入）；而且 Closed 的语义应是「通道被关闭了」，
            // 不是「你自己刚把它释放了」。
            Assert.IsFalse(closedFired,
                "Cascade disposal must not raise Closed events (#29, resolution 6).");
        }

        [Test]
        public void ChannelDisposedDirectly_IsNotDestroyedAgainByItsPeer()
        {
            var pc = new PeerConnection(new PeerConnectionConfig());

            // **阳性对照**：先证明原生错误文本确实会经日志桥到达这里。没有它，
            // 下面那条「没有出现陈旧句柄错误」会因为日志压根没接通而永远绿 ——
            // 「没跑」和「跑了，绿」在报告里长得一模一样（CONTRIBUTING 的那条原则）。
            LogAssert.ignoreFailingMessages = true;
            var badCfg = new PeerConnectionConfig();
            badCfg.IceServers.Add(new IceServer("ftp://example.com:3478"));
            Assert.Throws<DataChannelException>(() => new PeerConnection(badCfg));
            PumpAWhile();
            Assert.That(_captured, Has.Some.Contains("protocol"),
                "Positive control failed: native error text never reached the managed log, so the later assertions in this test are meaningless.");
            _captured.Clear();

            var dc = pc.CreateDataChannel("direct");
            dc.Dispose();       // 应用自行释放：PC 必须把它从子列表摘除
            pc.Dispose();       // 若没摘除，这里会对同一个句柄再销毁一次
            PumpAWhile();

            Assert.That(_captured, Has.None.Contains(StaleDcHandle),
                "The parent PC called dcu_dc_destroy again on a child that had already disposed itself: it was not removed from the child list (#29, resolution 1).");
            Assert.That(_captured, Has.None.Contains(StalePcHandle));
        }

        [Test]
        public void Dispose_IsIdempotent_OnBothTypes()
        {
            var pc = new PeerConnection(new PeerConnectionConfig());
            var dc = pc.CreateDataChannel("idempotent");

            Assert.DoesNotThrow(() => dc.Dispose());
            Assert.DoesNotThrow(() => dc.Dispose());
            Assert.DoesNotThrow(() => pc.Dispose());
            Assert.DoesNotThrow(() => pc.Dispose());
            PumpAWhile();

            // 幂等不只是「第二次不抛」——第二次**根本不该进原生**。
            Assert.That(_captured, Has.None.Contains(StaleDcHandle));
            Assert.That(_captured, Has.None.Contains(StalePcHandle));
        }

        [Test]
        public void ChannelDisposedAfterItsPeer_IsIdempotentToo()
        {
            var pc = new PeerConnection(new PeerConnectionConfig());
            var dc = pc.CreateDataChannel("late");

            pc.Dispose();                               // 级联已经把 dc 释放掉了
            Assert.DoesNotThrow(() => dc.Dispose());    // 应用不知情，仍然会调
            PumpAWhile();

            Assert.That(_captured, Has.None.Contains(StaleDcHandle),
                "Disposing again after a cascade is NORMAL usage (using blocks, object pools) and must not hit a zombie handle.");
        }

        /// <summary>
        /// 入向通道由接收端的 PC 拥有，并随它级联释放（#29 决议 3）。
        /// </summary>
        /// <remarks>
        /// 这条比出向那条更值得测：入向通道是**我们在 Dispatch 里 new 出来的**，
        /// 没有任何一行应用代码要求它存在。「无人订阅就拒收」被否掉之后，
        /// 它的归属只剩这一条边 —— 断了就是一个应用永远够不着的原生泄漏。
        /// </remarks>
        [Test]
        public void IncomingChannel_IsOwnedByReceivingPeer_AndCascades()
        {
            DataChannel incoming = null;
            string got = null;

            var a = new PeerConnection(new PeerConnectionConfig());
            var b = new PeerConnection(new PeerConnectionConfig());
            try
            {
                a.LocalDescriptionGenerated += (sdp, t) => b.SetRemoteDescription(sdp, t);
                b.LocalDescriptionGenerated += (sdp, t) => a.SetRemoteDescription(sdp, t);
                a.LocalCandidateGenerated += (c, mid) => b.AddRemoteCandidate(c, mid);
                b.LocalCandidateGenerated += (c, mid) => a.AddRemoteCandidate(c, mid);

                // **刻意不订阅 incoming.MessageReceived 之外的任何东西，也不持有它** ——
                // 除了这个测试用来断言的局部变量。所有权必须来自 PC，不是来自订阅。
                b.DataChannelReceived += ch =>
                {
                    incoming = ch;
                    ch.MessageReceived += bytes => got = Encoding.UTF8.GetString(bytes.ToArray());
                };

                var outgoing = a.CreateDataChannel("owned-by-b");
                outgoing.Opened += () => outgoing.Send(Encoding.UTF8.GetBytes("hi"));

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 15000 && got == null)
                {
                    DataChannelRuntime.Pump();
                    Thread.Sleep(5);
                }

                Assert.AreEqual("hi", got, "The loopback never connected, so the ownership assertions below cannot mean anything.");
                Assert.IsNotNull(incoming);
                Assert.AreSame(b, incoming.Peer, "An inbound channel's Peer must be the receiving PC.");

                b.Dispose();    // 只释放 PC，不碰 incoming

                Assert.AreEqual(DataChannelState.Closed, incoming.State,
                    "The inbound channel was not disposed in cascade with its PC. The application cannot reach it, so this is a pure native leak.");
                var ex = Assert.Throws<ObjectDisposedException>(() => incoming.Send(new byte[] { 1 }));
                StringAssert.Contains("cascade", ex.Message);
            }
            finally
            {
                a.Dispose();
                b.Dispose();
            }
        }
    }
}
