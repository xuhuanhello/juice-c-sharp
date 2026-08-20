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
            DataChannelRuntime.Preload(); // #146 被动化后，读属性不再触发加载 —— 测试侧显式预热。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip. If the plugin was just rebuilt, restart the Editor.");
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
                "After the first subscriber throws, the other two must still receive. Missing 1 and 2 means isolation "
                + "was written as one try around the whole Invoke.");
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
                "Message events must be isolated per subscriber as well.");
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
                "A subscriber that threw must keep receiving later messages. Receiving only once means it was auto-unsubscribed.");
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
                    "The check must still hold when offset + count overflows to a negative value: it is the last gate before an out-of-bounds read.");
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

                AssertPassedArgumentChecks(() => dc.Send(data, 16, 0), "empty slice where offset == length");
                AssertPassedArgumentChecks(() => dc.Send(data, 0, 16), "the whole array");
                AssertPassedArgumentChecks(() => dc.Send(Array.Empty<byte>()), "zero-length array");
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
            catch (ArgumentException e) { Assert.Fail(what + " was wrongly rejected by argument validation: " + e.Message); }
            catch (DataChannelException) { /* 越过校验、到了原生 —— 正是本用例要的 */ }
        }

        // ------------------------------------------------------------------
        // 三、pump 自身的契约（#148 重入守卫 / #149 无预算）
        // ------------------------------------------------------------------

        /// <summary>
        /// 回调里重入 <c>Pump()</c>：内层抛、外层活、守卫复位、后续照常（#148 决议的四断言）。
        /// </summary>
        /// <remarks>
        /// 守卫防的是**运行期静默数据损坏**：内层 pump 覆写复用的消息缓冲，外层回调
        /// 手里的 <c>ReadOnlySpan</c> 在回调期间变质 —— 恰是 #38 选 Span 要在编译期
        /// 消灭的那类事故从公开的 <c>Pump</c> 绕回来。异常必然被外层自己的每订阅者
        /// 隔离接住（重入时外层必在栈上），所以「抛」是响亮且不崩的。
        /// </remarks>
        [Test]
        public void ReentrantPump_InsideMessageCallback_ThrowsAndOuterPumpSurvives()
        {
            LogAssert.ignoreFailingMessages = true;   // 内层异常经隔离层记 Error，是预期产物

            var caughtReentrancy = false;
            Exception wrongException = null;
            var tailDelivered = false;
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
                        if (bytes.Length == 1)
                        {
                            try { DataChannelRuntime.Pump(); }
                            catch (InvalidOperationException) { caughtReentrancy = true; }
                            catch (Exception e) { wrongException = e; }
                        }
                        else
                        {
                            tailDelivered = true;
                        }
                    };
                };

                var dc = a.CreateDataChannel("reentrancy-guard");
                dc.Opened += () => dc.Send(new byte[1]);

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 15000 && !caughtReentrancy && wrongException == null)
                {
                    DataChannelRuntime.Pump();
                    Thread.Sleep(5);
                }

                Assert.IsNull(wrongException,
                    "Re-entrant Pump must throw InvalidOperationException specifically; got: " + wrongException);
                Assert.IsTrue(caughtReentrancy, "The re-entrant Pump call did not throw — the guard is missing.");

                // 守卫复位：外层结束后直接泵必须正常（try/finally 的回归钉子）。
                DataChannelRuntime.Pump();

                dc.Send(new byte[2]);
                sw.Restart();
                while (sw.ElapsedMilliseconds < 15000 && !tailDelivered)
                {
                    DataChannelRuntime.Pump();
                    Thread.Sleep(5);
                }
                Assert.IsTrue(tailDelivered,
                    "Delivery after the re-entrancy incident is broken: either the guard poisoned the pump "
                    + "or the outer dispatch did not survive the inner throw.");

                incoming?.Dispose();
                dc.Dispose();
            }
        }

        /// <summary>
        /// 控制段无每帧预算：≥300 个积压的 <c>DcClosed</c> 必须在**单次** <c>Pump()</c> 里全部派发（#149）。
        /// </summary>
        /// <remarks>
        /// 300 越过被删掉的旧上限 256，专打「预算被凭直觉加回来」的回归 ——
        /// 那个上限正是首个提交里没人对质的遗留（#149 的考古）。规格条文在
        /// SPEC §6「both drain fully」与 SPEC:524（预算会把 DcClosed 推后）。
        /// </remarks>
        [Test]
        public void ControlDrain_HasNoPerFrameBudget_AllClosedEventsInOnePump()
        {
            LogAssert.ignoreFailingMessages = true;   // 级联关闭期间的原生日志是预期产物

            const int Channels = 300;
            var closed = 0;
            var adopted = 0;
            var aChannels = new List<DataChannel>(Channels);

            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                a.LocalDescriptionGenerated += (sdp, t) => b.SetRemoteDescription(sdp, t);
                b.LocalDescriptionGenerated += (sdp, t) => a.SetRemoteDescription(sdp, t);
                a.LocalCandidateGenerated += (c, mid) => b.AddRemoteCandidate(c, mid);
                b.LocalCandidateGenerated += (c, mid) => a.AddRemoteCandidate(c, mid);
                b.DataChannelReceived += _ => adopted++;

                for (int i = 0; i < Channels; i++)
                {
                    var dc = a.CreateDataChannel("budget-" + i);
                    dc.Closed += () => closed++;
                    aChannels.Add(dc);
                }

                // 夹具就位：A 侧全开（活查询）+ B 侧全收养（收养走 pump 派发）。
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 30000
                       && (adopted < Channels || aChannels.Exists(c => c.State != DataChannelState.Open)))
                {
                    DataChannelRuntime.Pump();
                    Thread.Sleep(5);
                }
                Assert.AreEqual(Channels, adopted,
                    "Not all incoming channels were adopted; the fixture never reached its premise.");

                // 关键段开始：**从这里起不再泵**，让 DcClosed 在无界队列里积压。
                // 同步 [Test] 体内没有别人会泵（EditMode 无 PlayerLoop，EditorPump
                // 被测试体阻塞），队列只进不出。
                b.Dispose();

                // 等原生侧全部翻到 Closed —— State 是活查询，不需要泵；
                // 状态翻转与事件入队之间有微小的在途窗口，再留 500ms 收尾。
                sw.Restart();
                while (sw.ElapsedMilliseconds < 15000
                       && aChannels.Exists(c => c.State != DataChannelState.Closed))
                {
                    Thread.Sleep(10);
                }
                Assert.IsTrue(aChannels.TrueForAll(c => c.State == DataChannelState.Closed),
                    "Native close never completed; the fixture never reached its premise.");
                Thread.Sleep(500);

                Assert.AreEqual(0, closed, "Events were dispatched before the single pump — something else pumped.");

                DataChannelRuntime.Pump();   // 单次。

                Assert.AreEqual(Channels, closed,
                    "One Pump() must dispatch every backlogged DcClosed. Fewer than " + Channels
                    + " means a per-frame budget crept back into the control drain (SPEC §6: both segments drain fully).");

                foreach (var dc in aChannels) dc.Dispose();
            }
        }
    }
}
