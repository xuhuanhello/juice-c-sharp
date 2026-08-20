using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：消息尺寸两端的契约（#154 决议 1、2）。
    ///
    /// 两条各守一个**此前零覆盖**的断裂点：
    /// 消息路径的 TOO_SMALL 不消费重试（EventQueueAtomicityTests 测的是控制队列，
    /// 不是 <c>dcu_dc_receive</c> + <c>_messageBuf</c>），以及
    /// <c>MaxMessageSize</c> 配置从 C# 到上游的封送贯通。
    ///
    /// EditMode 没有 PlayerLoop，故本组手动调 <c>Pump()</c>。
    /// </summary>
    public sealed class MessageSizeContractTests
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
            // 缺席必须是失败，不是跳过（SPEC §11）。
            DataChannelRuntime.Preload(); // #146 被动化后，读属性不再触发加载 —— 测试侧显式预热。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip. If the plugin was just rebuilt, restart the Editor.");
        }

        [TearDown]
        public void DrainAndRestore()
        {
            // 与 ErrorCodeFidelityTests 同款：谁触发原生错误，谁负责在自己结束前排空
            // 日志队列，否则那条 Error 会炸在下一个无辜的测试上。TearDown 里要再设一次
            // ignoreFailingMessages —— 框架在进入 TearDown 前会把测试体里设的重置掉。
            LogAssert.ignoreFailingMessages = true;
            for (int i = 0; i < 30; i++)
            {
                DataChannelRuntime.Pump();
                Thread.Sleep(2);
            }
        }

        /// <summary>
        /// 超过 pump 消息缓冲基线（64KB）的单条消息必须完整送达。
        /// </summary>
        /// <remarks>
        /// 它防的是 <c>dcu_impl.cpp</c> 文件头不变量 2 **亲自点名**的陷阱：
        /// 收消息若把 <c>peek() → 拷贝 → 成功才 receive()</c> 重写成直觉的
        /// 「<c>receive()</c> 再拷贝」，调用方缓冲不足时那条消息就**丢了**，
        /// 且编译、门禁全绿 —— reliable 通道上即协议违约。128KB 载荷强制第一次
        /// <c>dcu_dc_receive</c> 走 TOO_SMALL、托管侧 <c>EnsureCapacity</c> 增长、
        /// 重试拿到**同一条**消息，字节必须一致。
        ///
        /// 尾部的 BufferedAmount 归零断言是该导出托管接线的唯一覆盖（#154 审定：
        /// 一行，不单开测试）。
        ///
        /// 注意：本条假设套件里没有更早发送 ≥128KB 消息的用例（缓冲只涨不缩，
        /// 先被撑大则 TOO_SMALL 路径被路过）。今天成立；若未来新增更大载荷的
        /// 用例，把这里的载荷再调大一档即可。
        /// </remarks>
        [Test]
        public void MessageOverPumpBufferBaseline_IsDeliveredIntact()
        {
            const int Size = 128 * 1024;
            var payload = new byte[Size];
            for (int i = 0; i < Size; i++) payload[i] = (byte)(i * 31 + 7);

            byte[] received = null;
            var extraMessages = 0;
            DataChannel incoming = null;

            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                Connect(a, b, ch =>
                {
                    incoming = ch;
                    ch.MessageReceived += bytes =>
                    {
                        if (received == null) received = bytes.ToArray();
                        else extraMessages++;
                    };
                }, out var dc, "oversize-baseline");

                dc.Opened += () => dc.Send(payload);

                PumpUntil(() => received != null);

                Assert.IsNotNull(received, "The 128KB message never arrived.");
                Assert.AreEqual(Size, received.Length,
                    "Wrong length: the TOO_SMALL retry did not hand back the same message intact.");
                CollectionAssert.AreEqual(payload, received,
                    "Byte mismatch: the buffer-too-small retry must re-read the SAME unconsumed message.");
                Assert.AreEqual(0, extraMessages,
                    "Exactly one message was sent; more than one arriving means the retry duplicated it.");

                // 送达已确认，发送侧缓冲应当排空归零 —— 该属性托管接线的唯一覆盖。
                PumpUntil(() => dc.BufferedAmount == 0, 5000);
                Assert.AreEqual(0, dc.BufferedAmount,
                    "BufferedAmount must drain back to 0 after the peer confirmed delivery.");

                incoming?.Dispose();
                dc.Dispose();
            }
        }

        /// <summary>
        /// 超过协商 <c>MaxMessageSize</c> 的发送必须以 <c>Invalid</c> 失败，
        /// 且异常文本点名那个旋钮。
        /// </summary>
        /// <remarks>
        /// 表面测失败形态，实际钉的是 <c>PcConfigNative.max_message_size →
        /// cfg.maxMessageSize</c> 这条配置封送链 —— 在本条之前**没有任何证据**
        /// 它生效。先发一条限内消息证明通道在该配置下工作正常，再让超限发送失败，
        /// 使失败可归因于尺寸而不是通道本身。
        ///
        /// 精确阈值语义（含边界字节、DCEP 开销）归上游；本条只断言
        /// 「超过配置上限的发送以 Invalid 失败」，不追边界（#154 决议 2）。
        /// </remarks>
        [Test]
        public void SendOverConfiguredMaxMessageSize_FailsAsInvalid_AndNamesTheKnob()
        {
            // 本用例**刻意**触发原生错误；那条经日志桥的 Error 是预期产物，不是失败。
            LogAssert.ignoreFailingMessages = true;

            var smallDelivered = false;
            DataChannel incoming = null;

            using (var a = new PeerConnection(new PeerConnectionConfig { MaxMessageSize = 1024 }))
            using (var b = new PeerConnection(new PeerConnectionConfig { MaxMessageSize = 1024 }))
            {
                Connect(a, b, ch =>
                {
                    incoming = ch;
                    ch.MessageReceived += _ => smallDelivered = true;
                }, out var dc, "max-message-size");

                var opened = false;
                dc.Opened += () =>
                {
                    opened = true;
                    dc.Send(new byte[256]); // 限内哨兵：证明该配置下通道本身是好的。
                };

                PumpUntil(() => opened && smallDelivered);
                Assert.IsTrue(smallDelivered,
                    "The in-limit sentinel never arrived, so the oversize failure below would be unattributable.");

                var ex = Assert.Throws<DataChannelException>(() => dc.Send(new byte[4096]),
                    "A send over the configured MaxMessageSize must fail; if this passed, the config field never reached upstream.");
                Assert.AreEqual(DataChannelError.Invalid, ex.ErrorCode,
                    "Oversize is a caller-fixable input problem and must map to Invalid, not be flattened.");
                Assert.That(ex.Message, Does.Contain("MaxMessageSize"),
                    "The self-fixable message shape must name the knob the caller needs to check.");

                incoming?.Dispose();
                dc.Dispose();
            }
        }
    }
}
