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
    /// native 档：订阅者异常隔离与发送边界（#38 决议 5、6、10 / SPEC §6）。
    /// </summary>
    public sealed class PumpRobustnessTests
    {
        [SetUp]
        public void RequireNative()
        {
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "原生插件未加载。这是失败而非跳过 —— 若刚重建过插件，需重启 Editor。");
        }

        [TearDown]
        public void Drain()
        {
            LogAssert.ignoreFailingMessages = true;
            for (int i = 0; i < 40; i++) { DataChannelRuntime.Pump(); Thread.Sleep(2); }
        }

        // ------------------------------------------------------------------
        // 一、异常隔离
        // ------------------------------------------------------------------

        /// <summary>
        /// 控制事件：第一个订阅者抛异常，后面两个照常收到。
        /// </summary>
        /// <remarks>
        /// 多播委托按顺序调用，**第一个抛出的会让后面全都收不到** —— 这是 .NET 的
        /// 既定行为，不是我们能改的。所以隔离必须做在每个订阅者上。把整个
        /// <c>Invoke</c> 包一个 try 看起来更省事，但那等于在库内部重犯 #30 判定为
        /// 协议违约的错：订阅者甲抛异常导致订阅者乙**这条永远收不到**，
        /// 失败模式就是丢消息，只是挪到了分发环节，而且没有重传路径。
        /// </remarks>
        [Test]
        public void ControlEvent_ThrowingSubscriber_DoesNotStopTheOthers()
        {
            LogAssert.ignoreFailingMessages = true;   // 订阅者异常会被记 Error，是预期产物

            var reached = new List<int>();
            using (var pc = new PeerConnection(new PeerConnectionConfig()))
            {
                pc.LocalDescriptionGenerated += (sdp, t) => { reached.Add(0); throw new InvalidOperationException("boom"); };
                pc.LocalDescriptionGenerated += (sdp, t) => reached.Add(1);
                pc.LocalDescriptionGenerated += (sdp, t) => reached.Add(2);

                pc.CreateDataChannel("isolation-control");

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 10000 && reached.Count < 3)
                {
                    DataChannelRuntime.Pump();
                    Thread.Sleep(5);
                }
            }

            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, reached,
                "第一个订阅者抛异常之后，后面两个必须照常收到。少了 1 和 2 说明隔离"
                + "被写成了「整个 Invoke 包一个 try」。");
        }

        /// <summary>消息事件：同一条契约，走的是缓存 invocation list 那条路径。</summary>
        [Test]
        public void MessageEvent_ThrowingSubscriber_DoesNotStopTheOthers()
        {
            LogAssert.ignoreFailingMessages = true;

            var reached = new List<int>();
            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                a.LocalDescriptionGenerated += (sdp, t) => b.SetRemoteDescription(sdp, t);
                b.LocalDescriptionGenerated += (sdp, t) => a.SetRemoteDescription(sdp, t);
                a.LocalCandidateGenerated += (c, mid) => b.AddRemoteCandidate(c, mid);
                b.LocalCandidateGenerated += (c, mid) => a.AddRemoteCandidate(c, mid);

                b.DataChannelReceived += ch =>
                {
                    ch.MessageReceived += _ => { reached.Add(0); throw new InvalidOperationException("boom"); };
                    ch.MessageReceived += _ => reached.Add(1);
                    ch.MessageReceived += _ => reached.Add(2);
                };

                var dc = a.CreateDataChannel("isolation-message");
                dc.Opened += () => dc.Send(Encoding.UTF8.GetBytes("x"));

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 15000 && reached.Count < 3)
                {
                    DataChannelRuntime.Pump();
                    Thread.Sleep(5);
                }
            }

            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, reached,
                "消息事件的隔离粒度同样必须是每订阅者。");
        }

        /// <summary>抛异常的订阅者**不会**被自动退订。</summary>
        /// <remarks>
        /// <para>
        /// 「连抛 N 次就踢掉」的熔断被明确排除：静默改变别人建立的订阅关系，
        /// 比日志刷屏更坏，而刷屏已经由 5 秒节流处理掉了。这与 #45 决议 2
        /// 砍掉 pump 无限自愈是同一个形状。
        /// </para>
        /// <para>
        /// 用**消息**事件而不是 candidate 事件来验：candidate 的条数取决于本机有几张
        /// 网卡，「至少两次」在单接口的机器上根本不成立 —— 那会让这条用例的红绿
        /// 取决于跑它的机器，而不是取决于代码。发两条消息是确定的。
        /// </para>
        /// </remarks>
        [Test]
        public void ThrowingSubscriber_IsNotAutoUnsubscribed()
        {
            LogAssert.ignoreFailingMessages = true;

            var calls = 0;
            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                a.LocalDescriptionGenerated += (sdp, t) => b.SetRemoteDescription(sdp, t);
                b.LocalDescriptionGenerated += (sdp, t) => a.SetRemoteDescription(sdp, t);
                a.LocalCandidateGenerated += (c, mid) => b.AddRemoteCandidate(c, mid);
                b.LocalCandidateGenerated += (c, mid) => a.AddRemoteCandidate(c, mid);

                b.DataChannelReceived += ch =>
                    ch.MessageReceived += _ =>
                    {
                        calls++;
                        throw new InvalidOperationException("boom");
                    };

                var dc = a.CreateDataChannel("no-auto-unsubscribe");
                dc.Opened += () =>
                {
                    dc.Send(Encoding.UTF8.GetBytes("one"));
                    dc.Send(Encoding.UTF8.GetBytes("two"));
                };

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 15000 && calls < 2)
                {
                    DataChannelRuntime.Pump();
                    Thread.Sleep(5);
                }
            }

            Assert.AreEqual(2, calls,
                "抛过异常的订阅者必须继续收到后续消息。只收到一次说明它被自动退订了。");
        }

        // ------------------------------------------------------------------
        // 二、发送边界
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>offset + count</c> 在 <c>int.MaxValue</c> 附近回绕，检查会**整个失效**。
        /// </summary>
        /// <remarks>
        /// 这不是理论洁癖：旧写法是 <c>offset + count &gt; data.Length</c>，
        /// 当 <c>count == int.MaxValue</c> 且 <c>offset == 1</c> 时，
        /// <c>1 + int.MaxValue</c> 回绕成 <c>int.MinValue</c>，
        /// <c>int.MinValue &gt; 16</c> 为 false，于是检查放行 —— 而 S7 之后这条路径
        /// 直通 <c>fixed</c> 指针，放行的结果是越界读。
        ///
        /// 新写法 <c>data.Length - offset &lt; count</c>：offset 与 count 此刻已知
        /// 非负，减法不可能溢出。
        /// </remarks>
        [Test]
        public void Send_BoundsCheck_HoldsNearIntMaxValue()
        {
            using (var pc = new PeerConnection(new PeerConnectionConfig()))
            {
                var dc = pc.CreateDataChannel("bounds");
                var data = new byte[16];

                Assert.Throws<ArgumentOutOfRangeException>(() => dc.Send(data, 1, int.MaxValue),
                    "offset + count 回绕成负数时检查必须仍然成立 —— 这是越界读之前的最后一道闸。");
                Assert.Throws<ArgumentOutOfRangeException>(() => dc.Send(data, int.MaxValue, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => dc.Send(data, 0, 17));
                Assert.Throws<ArgumentOutOfRangeException>(() => dc.Send(data, -1, 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => dc.Send(data, 0, -1));
                Assert.Throws<ArgumentNullException>(() => dc.Send(null));
            }
        }

        /// <summary>
        /// 合法的边界值不该被误拒 —— 尤其是「空切片」这几种。
        /// </summary>
        /// <remarks>
        /// 零长度消息是合法的（WebRTC 语义允许，常被当心跳用）。通道此刻还没 open，
        /// 所以原生侧会失败并抛 <see cref="DataChannelException"/> —— 那是**正确**的
        /// 结果：本用例要证的是它**没有**在参数校验这一层就被拦下来。
        /// 端到端的零长度投递另有 <c>PullModeTests.ZeroLengthMessage_IsDelivered</c> 守着。
        /// </remarks>
        [Test]
        public void Send_LegalEdgeOffsets_AreNotRejectedByArgumentChecks()
        {
            LogAssert.ignoreFailingMessages = true;

            using (var pc = new PeerConnection(new PeerConnectionConfig()))
            {
                var dc = pc.CreateDataChannel("bounds-ok");
                var data = new byte[16];

                AssertPassedArgumentChecks(() => dc.Send(data, 16, 0), "offset == length 的空切片");
                AssertPassedArgumentChecks(() => dc.Send(data, 0, 16), "整个数组");
                AssertPassedArgumentChecks(() => dc.Send(Array.Empty<byte>()), "零长度数组");
            }
        }

        /// <summary>
        /// 断言这次调用**越过了参数校验**，而不是断言它成功。
        /// </summary>
        /// <remarks>
        /// 通道此刻还没 open，所以原生侧会失败并抛 <see cref="DataChannelException"/> ——
        /// 那正是「已经走到原生了」的证据。只有 <see cref="ArgumentException"/> 一族
        /// 才说明它在托管层就被误拒了。
        /// </remarks>
        private static void AssertPassedArgumentChecks(Action action, string what)
        {
            try { action(); }
            catch (ArgumentException e) { Assert.Fail(what + " 被参数校验误拒：" + e.Message); }
            catch (DataChannelException) { /* 越过校验、到了原生 —— 正是本用例要的 */ }
        }
    }
}
