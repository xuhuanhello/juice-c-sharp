using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：<c>dcu_event_next</c> 的单次原子语义与 TOO_SMALL 幂等重试
    /// （#30 决议 1 / SPEC §4）。
    ///
    /// EditMode 没有 PlayerLoop，故本组**刻意**手动调 <c>Pump()</c> ——
    /// 与 PlayMode 那条「不许出现 Pump()」的测试分工不同，别混淆。
    /// </summary>
    public sealed class EventQueueAtomicityTests
    {
        // 托管侧 payload 缓冲基线是 64KB，上游对端上限默认 256KB（SDP 里协商）。
        // 200000 稳过基线、稳在上限之下。
        private const int LargeSize = 200000;

        [Test]
        public void LargePayload_RetriesOnce_AndConsumesEventExactlyOnce()
        {
            DataChannelRuntime.Preload(); // #146 被动化后，读属性不再触发加载 —— 测试侧显式预热。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip. If the plugin was just rebuilt, restart the Editor.");

            byte[] gotLarge = null;
            string gotSentinel = null;
            DataChannel incoming = null;

            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                a.LocalDescriptionGenerated += (sdp, t) => b.SetRemoteDescription(sdp, t);
                b.LocalDescriptionGenerated += (sdp, t) => a.SetRemoteDescription(sdp, t);
                a.LocalCandidateGenerated += (c, mid) => b.AddRemoteCandidate(c, mid);
                b.LocalCandidateGenerated += (c, mid) => a.AddRemoteCandidate(c, mid);

                b.DataChannelReceived += ch =>
                {
                    incoming = ch;
                    ch.MessageReceived += bytes =>
                    {
                        if (bytes.Length == LargeSize) gotLarge = bytes.ToArray();
                        else gotSentinel = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
                    };
                };

                var payload = new byte[LargeSize];
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = (byte)(i % 251);

                var dc = a.CreateDataChannel("atomicity");
                dc.Opened += () =>
                {
                    // 顺序很重要：大载荷先发。若 TOO_SMALL 误把事件消费掉，大载荷会丢；
                    // 若重试路径把队首弹了两次，哨兵会丢。两条断言合起来正好钉住
                    // 「恰好消费一次」。
                    dc.Send(payload);
                    dc.Send(System.Text.Encoding.UTF8.GetBytes("tail"));
                };

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 15000 && (gotLarge == null || gotSentinel == null))
                {
                    DataChannelRuntime.Pump();
                    Thread.Sleep(5);
                }

                dc.Dispose();
                incoming?.Dispose();

                Assert.IsNotNull(gotLarge,
                    "The over-baseline payload never arrived. If the TOO_SMALL branch consumes the event, it is lost forever.");
                Assert.AreEqual(LargeSize, gotLarge.Length, "Payload length mismatch: the retry did not fetch the complete data.");
                for (int i = 0; i < LargeSize; i++)
                {
                    if (gotLarge[i] != (byte)(i % 251))
                        Assert.Fail($"Payload content differs at offset {i}: expected {(byte)(i % 251)}, actual {gotLarge[i]}.");
                }

                Assert.AreEqual("tail", gotSentinel,
                    "The sentinel message never arrived: the retry path may have popped the head twice.");
            }
        }
    }
}
