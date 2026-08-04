using System.Collections;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// PlayMode 档存在的**唯一理由**是它能覆盖 EditMode 结构上覆盖不到的东西：
    /// <see cref="DataChannelRuntime.RegisterPump"/> 装进 <c>PlayerLoop</c>，
    /// 所以本测试要断言的不是「消息通了」，而是
    /// **「没有人手动调 <c>Pump()</c>，消息也通了」**（SPEC §11）。
    ///
    /// 在此之前没有任何验证覆盖 pump 的注册路径：EditMode 的双 Peer 回环是在
    /// 等待循环里手动调 <c>Pump()</c> 的（<c>execute_code</c> 不是 PlayerLoop），
    /// 而场景里那个手动 driver 的判据是「Console 里有没有一行英文」。
    ///
    /// 因此本文件里**绝不允许出现 <c>DataChannelRuntime.Pump()</c>**。
    /// 若哪天有人为了让它变绿而加上那一行，这条测试就失去了全部意义。
    /// </summary>
    public sealed class PumpRegistrationPlayModeTests
    {
        private const float TimeoutSeconds = 20f;

        [UnityTest]
        public IEnumerator DualPeerLoopback_FlowsWithoutAnyManualPump()
        {
            // 缺席必须是失败，不是跳过（SPEC §11）：没有插件时不 Assert.Ignore，
            // 否则「压根没跑」和「跑过了，绿」在报告里长得一模一样。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip. If the plugin was just rebuilt, restart the Editor: "
                + "Unity loads native plugins process-wide and never unloads them (docs/verification-mcp.md).");

            string gotA = null, gotB = null;
            var aOpened = false;
            DataChannel incoming = null;
            DataChannel outgoing = null;

            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                // 进程内假信令：把一端的本地描述/候选直接喂给另一端。
                a.LocalDescriptionGenerated += (sdp, type) => b.SetRemoteDescription(sdp, type);
                b.LocalDescriptionGenerated += (sdp, type) => a.SetRemoteDescription(sdp, type);
                a.LocalCandidateGenerated += (cand, mid) => b.AddRemoteCandidate(cand, mid);
                b.LocalCandidateGenerated += (cand, mid) => a.AddRemoteCandidate(cand, mid);

                b.DataChannelReceived += ch =>
                {
                    incoming = ch;
                    ch.MessageReceived += bytes =>
                    {
                        gotB = Encoding.UTF8.GetString(bytes.ToArray());
                        ch.Send(Encoding.UTF8.GetBytes("pong"));
                    };
                };

                outgoing = a.CreateDataChannel("playmode-smoke");
                outgoing.Opened += () =>
                {
                    aOpened = true;
                    outgoing.Send(Encoding.UTF8.GetBytes("ping"));
                };
                outgoing.MessageReceived += bytes => gotA = Encoding.UTF8.GetString(bytes.ToArray());

                // 只推进帧。事件要么由已注册的 PlayerLoop pump 送达，要么送不到 ——
                // 后者正是本测试要抓的回归。
                var deadline = Time.realtimeSinceStartup + TimeoutSeconds;
                while (Time.realtimeSinceStartup < deadline && (gotA == null || gotB == null))
                    yield return null;

                // 通道**不用**在这里释放：S6 之后 PeerConnection.Dispose 级联带走
                // 它的子通道（#29 决议 1），而 using 保证 a/b 一定会被释放 ——
                // 即使下面的断言失败提前跳出。

                Assert.IsNotNull(incoming,
                    "The remote peer never received DataChannelReceived: inbound channel events were not dispatched through the PlayerLoop.");
                Assert.IsTrue(aOpened,
                    "The outbound channel never received an Open event: the pump is not running, or events were not dispatched.");
                Assert.AreEqual("ping", gotB,
                    "The remote peer received no message: inbound channel message dispatch did not arrive through the PlayerLoop.");
                Assert.AreEqual("pong", gotA,
                    "This side received no echo: outbound channel message dispatch did not arrive through the PlayerLoop.");
            }
        }
    }
}
