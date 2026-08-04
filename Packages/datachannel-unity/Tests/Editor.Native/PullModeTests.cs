using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：拉模式带来的两条反直觉契约（#30 / #38 / SPEC §4、§6）。
    ///
    /// EditMode 没有 PlayerLoop，故本组手动调 <c>Pump()</c>。
    /// </summary>
    public sealed class PullModeTests
    {
        private static void Connect(
            PeerConnection a, PeerConnection b,
            Action<DataChannel> onIncoming, out DataChannel outgoing, string label)
        {
            a.LocalDescriptionGenerated += (sdp, t) => b.SetRemoteDescription(sdp, t);
            b.LocalDescriptionGenerated += (sdp, t) => a.SetRemoteDescription(sdp, t);
            a.LocalCandidateGenerated += (c, mid) => b.AddRemoteCandidate(c, mid);
            b.LocalCandidateGenerated += (c, mid) => a.AddRemoteCandidate(c, mid);
            b.DataChannelReceived += ch => onIncoming(ch);
            outgoing = a.CreateDataChannel(label);
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

        [SetUp]
        public void RequireNative()
        {
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip. If the plugin was just rebuilt, restart the Editor.");
        }

        /// <summary>
        /// 拉模式一落地就会出现的新故障：拉到消息**当场派发**，而应用在回调里
        /// <c>Dispose()</c> 通道或 <c>CreateDataChannel()</c> 都完全合法且常见 ——
        /// 两者都改动 <c>HandleTable</c> 的字典，<c>Dictionary</c> 迭代中被修改直接抛。
        ///
        /// 关键在于**那个异常来自我们自己的迭代**，不是订阅者的异常，
        /// 每订阅者的隔离罩不住它，会穿透 pump。写代码时没人想得到这一层。
        /// </summary>
        [Test]
        public void MutatingChannelsInsideCallback_DoesNotEscapePump()
        {
            var received = false;
            var mutated = false;
            DataChannel incoming = null;
            DataChannel createdInCallback = null;

            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                Connect(a, b, ch =>
                {
                    incoming = ch;
                    ch.MessageReceived += _ =>
                    {
                        received = true;
                        // 在派发过程中同时做「删」和「增」两种字典改动。
                        createdInCallback = b.CreateDataChannel("born-in-callback");
                        ch.Dispose();
                        mutated = true;
                    };
                }, out var dc, "reentrancy");

                dc.Opened += () => dc.Send(Encoding.UTF8.GetBytes("x"));

                // 若 pump 会被穿透，异常会从这里的 Pump() 抛出来，测试直接红。
                PumpUntil(() => mutated);

                // 再多泵几帧，确保带着「刚被改过的表」继续遍历也不炸。
                for (int i = 0; i < 30; i++) { DataChannelRuntime.Pump(); Thread.Sleep(2); }

                createdInCallback?.Dispose();
                incoming?.Dispose();
                dc.Dispose();

                Assert.IsTrue(received, "The message never arrived, so this test did not exercise what it claims to.");
                Assert.IsTrue(mutated, "The callback did not run to completion; it probably threw at the Dispose/Create call.");
            }
        }

        /// <summary>
        /// 零长度消息合法（#38 决议 6）—— WebRTC 语义允许，常用作心跳。
        /// 直觉会把它当成错误拒掉，所以它进必测清单。
        /// </summary>
        [Test]
        public void ZeroLengthMessage_IsDelivered()
        {
            int? receivedLength = null;
            var gotTail = false;
            DataChannel incoming = null;

            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                Connect(a, b, ch =>
                {
                    incoming = ch;
                    ch.MessageReceived += bytes =>
                    {
                        if (receivedLength == null) receivedLength = bytes.Length;
                        else gotTail = true;
                    };
                }, out var dc, "zero-length");

                dc.Opened += () =>
                {
                    dc.Send(Array.Empty<byte>());
                    // 哨兵：若零长度消息被静默吞掉，第一条到达的会是哨兵，
                    // receivedLength 就会是 4 而不是 0 —— 断言能分辨。
                    dc.Send(Encoding.UTF8.GetBytes("tail"));
                };

                PumpUntil(() => receivedLength != null && gotTail);

                dc.Dispose();
                incoming?.Dispose();

                Assert.IsNotNull(receivedLength, "No message was received at all.");
                Assert.AreEqual(0, receivedLength.Value,
                    "A zero-length message must arrive as-is; receiving 4 means it was swallowed and the sentinel arrived first.");
                Assert.IsTrue(gotTail, "The sentinel never arrived: delivery after a zero-length message is broken.");
            }
        }
    }
}
