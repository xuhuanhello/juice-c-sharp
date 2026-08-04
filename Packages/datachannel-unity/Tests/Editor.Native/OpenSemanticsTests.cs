using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：open 语义与 label 上界（#32 / SPEC §4）。
    ///
    /// 这两条都属于「被实测推翻过直觉」那一类，所以进必测清单：
    /// 六份参照实现里缓存 open 状态的只有我们旧版一家，而超界 label 的失败形态
    /// 是**正句柄 + 不 open + 不 closed + 无 error + Send 返回成功**。
    /// </summary>
    public sealed class OpenSemanticsTests
    {
        // 测试自己声明 P/Invoke，而不是去够 internal 的 NativeMethods。
        // 这不是绕过封装：注入钩子是**原生 ABI 的公开导出**，测原生契约就该走原生 ABI。
        // #39 已明确否掉为可测性开 InternalsVisibleTo。
        [DllImport("datachannel_unity", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dcu_test_set_open_race_delay_ms(int ms);

        [SetUp]
        public void RequireNative()
        {
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "原生插件未加载。这是失败而非跳过 —— 若刚重建过插件，需重启 Editor。");
        }

        [TearDown]
        public void ClearInjection()
        {
            dcu_test_set_open_race_delay_ms(0);
            // 谁触发，谁负责排空（见 ErrorCodeFidelityTests 的说明）。
            for (int i = 0; i < 20; i++) { DataChannelRuntime.Pump(); Thread.Sleep(2); }
        }

        private static void Wire(PeerConnection a, PeerConnection b, Action<DataChannel> onIncoming)
        {
            a.LocalDescriptionGenerated += (sdp, t) => b.SetRemoteDescription(sdp, t);
            b.LocalDescriptionGenerated += (sdp, t) => a.SetRemoteDescription(sdp, t);
            a.LocalCandidateGenerated += (c, mid) => b.AddRemoteCandidate(c, mid);
            b.LocalCandidateGenerated += (c, mid) => a.AddRemoteCandidate(c, mid);
            b.DataChannelReceived += ch => onIncoming(ch);
        }

        private static void PumpUntil(Func<bool> done, int timeoutMs = 15000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && !done())
            {
                DataChannelRuntime.Pump();
                Thread.Sleep(5);
            }
        }

        // ── label 上界 ────────────────────────────────────────────────────

        [Test]
        public void Label_AtBound_IsAccepted()
        {
            using (var pc = new PeerConnection(new PeerConnectionConfig()))
            {
                // 65535 是**实测可用**的上界，不是理论值。拒绝它就是拒绝一个合法通道。
                var dc = pc.CreateDataChannel(new string('M', PeerConnection.MaxDataChannelLabelBytes));
                Assert.IsNotNull(dc);
                dc.Dispose();
            }
        }

        [Test]
        public void Label_OneByteOver_IsRejectedAtManagedLayer()
        {
            using (var pc = new PeerConnection(new PeerConnectionConfig()))
            {
                var ex = Assert.Throws<ArgumentException>(
                    () => pc.CreateDataChannel(new string('O', PeerConnection.MaxDataChannelLabelBytes + 1)));
                Assert.That(ex.Message, Does.Contain(PeerConnection.MaxDataChannelLabelBytes.ToString()),
                    "错误信息要说出上界，否则调用方不知道该改成多少。");
            }
        }

        // **已知缺口，如实记账**：dcu 层自己那道 label 校验从托管档**够不着** ——
        // 托管层总是先校验，没有绕过它到达原生层的公开路径。原生那道是给非 C# 消费者
        // （WebGL facade、未来的其它绑定）的纵深防御。
        //
        // 让它可测需要测试自己复制 dcu_pc_config 的结构布局去直接建 PC，那会引入
        // **静默漂移**风险（布局一改，测试就在破坏内存而不是报错）—— 比一个记录在案的
        // 缺口更糟。SPEC §11 已收录本缺口。

        // ── open 补发 ─────────────────────────────────────────────────────

        /// <summary>
        /// 连接**之后**再建通道 —— #32 的 T1 路径。两家参照实现的连通性测试都只覆盖了
        /// 「先建 DC 再连接」，正好是竞态不存在的那条。
        ///
        /// 不注入延迟：正常情况下回调路径获胜，本用例证明这条路径本身是通的，
        /// 且**补发没有造成重复投递**（Open 事件恰好一次）。
        /// </summary>
        [Test]
        public void ChannelCreatedAfterConnect_OpensExactlyOnce()
        {
            RunAfterConnectScenario(injectDelayMs: 0, out var openCount);
            Assert.AreEqual(1, openCount,
                "Open 事件必须恰好一次。0 = 通知丢了且补发没救回来；2 = 去重失效。");
        }

        /// <summary>
        /// 同一条路径，但**人为把竞态窗口撑开**：wire 之前先睡 300ms，SCTP 的 DCEP
        /// 握手在这期间必然完成，于是 onOpen 回调**永远不会来** —— 只有补发能救。
        ///
        /// 没有这个注入点，上一条测试只能证明「连接后建通道能开」，
        /// 证明不了补发分支是对的（窗口约 1µs 对一个 RTT，正常跑几乎撞不上）。
        /// </summary>
        [Test]
        public void ChannelCreatedAfterConnect_WithForcedRace_StillOpensExactlyOnce()
        {
            RunAfterConnectScenario(injectDelayMs: 300, out var openCount);
            Assert.AreEqual(1, openCount,
                "强制竞态下 Open 仍必须恰好一次 —— 这条专门验补发分支。"
                + "0 说明补发没生效，2 说明补发与回调都投了、去重失效。");
        }

        private void RunAfterConnectScenario(int injectDelayMs, out int openCount)
        {
            var connected = false;
            var opens = 0;
            DataChannel incoming = null;

            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                Wire(a, b, ch => incoming = ch);
                a.ConnectionStateChanged += st =>
                {
                    if (st == ConnectionState.Connected) connected = true;
                };

                // 先用一条引导通道把连接建起来。
                var bootstrap = a.CreateDataChannel("bootstrap");
                PumpUntil(() => connected);
                Assert.IsTrue(connected, "连接没建立，后续断言无意义。");

                // 现在 PC 已连接 —— 这才是 T1 竞态存在的那条路径。
                dcu_test_set_open_race_delay_ms(injectDelayMs);
                var late = a.CreateDataChannel("after-connect");
                late.Opened += () => opens++;

                PumpUntil(() => opens > 0);
                // 多泵一会儿，好让「重复投递」有机会暴露出来。
                for (int i = 0; i < 40; i++) { DataChannelRuntime.Pump(); Thread.Sleep(5); }

                Assert.AreEqual(DataChannelState.Open, late.State,
                    "活查询必须报 Open —— 它是权威状态，与事件是否送达无关。");

                late.Dispose();
                bootstrap.Dispose();
                incoming?.Dispose();
            }

            openCount = opens;
        }

        // ── 活查询 ────────────────────────────────────────────────────────

        [Test]
        public void State_IsConnecting_BeforeConnect_AndClosed_AfterDispose()
        {
            using (var pc = new PeerConnection(new PeerConnectionConfig()))
            {
                var dc = pc.CreateDataChannel("state-probe");
                Assert.AreEqual(DataChannelState.Connecting, dc.State);
                Assert.IsFalse(dc.IsOpen);

                dc.Dispose();
                Assert.AreEqual(DataChannelState.Closed, dc.State,
                    "已 Dispose 的通道报 Closed，而不是抛 ObjectDisposedException —— "
                    + "状态查询是诊断手段，不该在最需要它的时候失效。");
            }
        }
    }
}
