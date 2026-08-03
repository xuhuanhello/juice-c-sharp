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
                "原生插件未加载。这是失败而非跳过 —— 若刚重建过插件，需重启 Editor："
                + "Unity 的原生插件进程级加载、永不卸载（docs/verification-mcp.md）。");

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

                b.DataChannel += ch =>
                {
                    incoming = ch;
                    ch.Message += bytes =>
                    {
                        gotB = Encoding.UTF8.GetString(bytes);
                        ch.Send(Encoding.UTF8.GetBytes("pong"));
                    };
                };

                outgoing = a.CreateDataChannel("playmode-smoke");
                outgoing.Open += () =>
                {
                    aOpened = true;
                    outgoing.Send(Encoding.UTF8.GetBytes("ping"));
                };
                outgoing.Message += bytes => gotA = Encoding.UTF8.GetString(bytes);

                // 只推进帧。事件要么由已注册的 PlayerLoop pump 送达，要么送不到 ——
                // 后者正是本测试要抓的回归。
                var deadline = Time.realtimeSinceStartup + TimeoutSeconds;
                while (Time.realtimeSinceStartup < deadline && (gotA == null || gotB == null))
                    yield return null;

                // 断言前先释放通道：#29 的级联释放尚未实现，PeerConnection.Dispose
                // 不会带走子通道，而断言失败会跳过后续语句。
                outgoing?.Dispose();
                incoming?.Dispose();

                Assert.IsTrue(aOpened,
                    "出向通道从未收到 Open 事件 —— pump 没在跑，或事件没被派发。");
                Assert.AreEqual("ping", gotB,
                    "对端没收到消息：入向通道的消息派发未经 PlayerLoop 到达。");
                Assert.AreEqual("pong", gotA,
                    "本端没收到回包：出向通道的消息派发未经 PlayerLoop 到达。");
            }
        }
    }
}
