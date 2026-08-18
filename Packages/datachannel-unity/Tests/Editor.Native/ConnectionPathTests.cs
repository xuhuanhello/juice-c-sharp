using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：选中候选对的判定契约（#118 决议，依据 #114 的上游调研）。
    ///
    /// EditMode 没有 PlayerLoop，故本组手动调 <c>Pump()</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **这里只覆盖不需要 TURN 的那一半。** <c>Relayed</c> 方向、以及「失败后返回
    /// 陈旧判定」那个已知窗口，都要真的中继与真的断网才能构造，属于 #122 / #125
    /// 的真机环节 —— 不在 EditMode 里假装验过。
    /// </para>
    /// <para>
    /// **TooSmall 那条契约故意不在这里验。** 公开面自己分配 288 字节，而上游
    /// <c>JUICE_MAX_CANDIDATE_SDP_STRING_LEN</c> 是 256，所以那条路从公开面走不到；
    /// 要构造它就得自己调 <c>dcu_pc_create</c> 拿句柄，而那需要在测试里重新声明
    /// <c>PcConfigNative</c> 的结构布局 —— 给同一个内存布局立第二个真相源，
    /// 它会静默漂移。那条契约已由 <c>dcu_dc_receive</c> / <c>dcu_log_next</c> 的
    /// 既有测试覆盖，本函数是同一条契约的同一份实现。
    /// </para>
    /// </remarks>
    public sealed class ConnectionPathTests
    {
        private static void Connect(
            PeerConnection a, PeerConnection b, out DataChannel outgoing, string label)
        {
            a.LocalDescriptionGenerated += (sdp, t) => b.SetRemoteDescription(sdp, t);
            b.LocalDescriptionGenerated += (sdp, t) => a.SetRemoteDescription(sdp, t);
            a.LocalCandidateGenerated += (c, mid) => b.AddRemoteCandidate(c, mid);
            b.LocalCandidateGenerated += (c, mid) => a.AddRemoteCandidate(c, mid);
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
            DataChannelRuntime.Preload(); // #146 被动化后，读属性不再触发加载 —— 测试侧显式预热。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip. If the plugin was just rebuilt, restart the Editor.");
        }

        /// <summary>
        /// 连接尚未建立时返回 <c>false</c> —— 那是**正常态，不是错误**。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 这条容易被写成抛异常，或返回某个 <c>Unknown</c> 档。两者都错：调用方在
        /// 连接过程中读一次是完全正常的用法，做成异常路径会逼每个调用方包 try/catch；
        /// 而 <c>Unknown</c> 档会把「还没有答案」和「有答案但不知道是什么」混成一个
        /// 值 —— 后者在这条路上不可达（#114：连上之后候选对必然存在）。
        /// </para>
        /// <para>
        /// 本条**确实穿越了 ABI**：状态门禁在原生侧，所以 <c>false</c> 是原生层给的
        /// <c>NotAvailable</c>，不是托管侧短路的结果。早先的实现把门禁放在托管侧，那时
        /// 这条测试对着一个**缺少该符号的旧二进制**也会绿 —— 一条自产的 false green。
        /// </para>
        /// </remarks>
        [Test]
        public void BeforeConnected_ReturnsFalse()
        {
            using (var pc = new PeerConnection(new PeerConnectionConfig()))
            {
                Assert.AreNotEqual(ConnectionState.Connected, pc.ConnectionState,
                    "前置不成立：这个 PC 不该已经连上。");

                Assert.IsFalse(pc.TryGetConnectionPath(out _, out var sdp),
                    "尚未连接时必须返回 false。");
                Assert.IsNull(sdp, "返回 false 时 SDP 出参必须是 null。");
            }
        }

        /// <summary>
        /// 本机回环必然是直连：两端同在一台机器上，host 候选就能打通，
        /// 不存在中继的可能。
        /// </summary>
        /// <remarks>
        /// 顺带钉住 SDP 的**形态**：带 <c>a=</c> 前缀，与 <c>LocalCandidateGenerated</c>
        /// 同形。上游 <c>Candidate</c> 有两个转换 —— <c>candidate()</c> 不带前缀、
        /// <c>operator string()</c> 带 —— 同一个 API 面上混用两种会让调用方无从判断
        /// 该不该自己拼前缀。
        /// </remarks>
        [Test]
        public void Loopback_IsDirect_OnBothEnds()
        {
            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                Connect(a, b, out var dc, "path-direct");
                PumpUntil(() => dc.State == DataChannelState.Open);
                Assert.AreEqual(DataChannelState.Open, dc.State, "前置不成立：通道没开。");

                // 两端都问：判据是「任一端是中继」，所以两侧都该报直连。只问一端
                // 会漏掉「判据取错了端」这类错误 —— 那正是 #114 里最难的一条。
                Assert.IsTrue(a.TryGetConnectionPath(out var pathA, out var sdpA),
                    "已连接的一端必须能读出判定。");
                Assert.IsTrue(b.TryGetConnectionPath(out var pathB, out var sdpB),
                    "已连接的另一端也必须能读出判定。");

                Assert.AreEqual(ConnectionPath.Direct, pathA, "回环必须是直连。");
                Assert.AreEqual(ConnectionPath.Direct, pathB, "回环必须是直连。");

                foreach (var sdp in new[] { sdpA, sdpB })
                {
                    Assert.IsNotNull(sdp, "已连接时 SDP 出参不该是 null。");
                    Assert.IsTrue(sdp.StartsWith("a=candidate:", StringComparison.Ordinal),
                        "远端候选 SDP 必须带 a= 前缀，与 LocalCandidateGenerated 同形。实际：" + sdp);
                }
            }
        }

        /// <summary>
        /// 通道刚 open、而连接状态事件**还没派发**时就问，必须已经能答。
        /// </summary>
        /// <remarks>
        /// 这条钉的是「状态门禁放原生侧而不是托管侧」这个决定。托管侧的
        /// <c>ConnectionState</c> 是事件缓存，落后到下一次泵派发为止；而
        /// <c>DataChannel.State</c> 是活查询，会**先**变 Open。所以「循环等到通道
        /// open 就立刻问」是最自然的调用方写法，而它恰好落在缓存还没更新的那一瞬。
        ///
        /// 早先的实现把门禁放在托管侧，这条写法就会拿到 false —— 一个真正已连接的
        /// 连接被自己的门禁拒掉。那个 bug 是用 MCP 实测撞出来的，不是推演出来的，
        /// 所以它值得一条回归测试：注意本测试**故意不在取值前调 Pump()**。
        /// </remarks>
        [Test]
        public void RightAfterChannelOpens_BeforeStateEventDispatched_CanStillAnswer()
        {
            // 那个「缓存还没更新」的时刻不是每次都出现：实测 10 轮里命中 9 次，
            // 另一次两个事件同一帧到。所以**重试到抓住它为止** —— 否则本条有约
            // 一成的概率在没验到任何东西的情况下变绿，正是本项目反复被咬的那种形态。
            const int attempts = 8;
            var observedLag = false;

            for (var i = 0; i < attempts && !observedLag; i++)
            {
                using (var a = new PeerConnection(new PeerConnectionConfig()))
                using (var b = new PeerConnection(new PeerConnectionConfig()))
                {
                    Connect(a, b, out var dc, "path-no-lag-" + i);

                    // 等到通道 open —— 那是活查询，可能先于状态事件到达。
                    PumpUntil(() => dc.State == DataChannelState.Open);
                    Assert.AreEqual(DataChannelState.Open, dc.State, "前置不成立：通道没开。");

                    // 关键：此后**不**再 Pump()。
                    var cachedState = a.ConnectionState;
                    var ok = a.TryGetConnectionPath(out var path, out var sdp);

                    if (cachedState != ConnectionState.Connected)
                    {
                        // 抓到了：缓存落后，而调用仍必须成功。
                        observedLag = true;
                        Assert.IsTrue(ok,
                            "通道已 open 就必须能答，与状态事件派发与否无关。缓存状态当时是：" + cachedState);
                        Assert.AreEqual(ConnectionPath.Direct, path, "回环必须是直连。");
                        Assert.IsNotNull(sdp, "能答时 SDP 不该是 null。");
                    }
                    else
                    {
                        // 没抓到（两个事件同一帧到）。这一轮不构成证据，但调用也该成功。
                        Assert.IsTrue(ok, "已连接就必须能答。缓存状态：" + cachedState);
                    }
                }
            }

            // 抓不到不算过。要么是那个时序窗口真的消失了（那么本条已不再验证它宣称的
            // 东西，该由人重新判断），要么是实现变了 —— 两种都该让人看见，而不是静默绿。
            Assert.IsTrue(observedLag,
                attempts + " 轮都没能观测到「通道已 open 而缓存状态未更新」这个时刻。"
                + "本条测试因此没有验证到它宣称的东西 —— 请重新判断这个窗口是否还存在，"
                + "而不是调高重试次数了事。");
        }

        /// <summary>
        /// 同一条连接上连读两次，判定必须一致 —— 提名之后 ICE 不再重选
        /// （RFC 8445 §8.1.1，libjuice 逐字实现）。
        /// </summary>
        /// <remarks>
        /// 这条钉的是「拉取而非事件」这个决定的前提。若判定会在连接存活期间变化，
        /// 那么快照形态就是错的、就该有事件 —— 所以这个前提值得有一条测试看着，
        /// 而不是只写在注释里。
        /// </remarks>
        [Test]
        public void WhileConnected_VerdictIsStable()
        {
            using (var a = new PeerConnection(new PeerConnectionConfig()))
            using (var b = new PeerConnection(new PeerConnectionConfig()))
            {
                Connect(a, b, out var dc, "path-stable");
                PumpUntil(() => dc.State == DataChannelState.Open);
                Assert.AreEqual(DataChannelState.Open, dc.State, "前置不成立：通道没开。");

                Assert.IsTrue(a.TryGetConnectionPath(out var first, out var firstSdp));

                // 中间泵若干帧，给「候选对会变」这种可能性一个出现的机会。
                for (var i = 0; i < 20; i++)
                {
                    DataChannelRuntime.Pump();
                    Thread.Sleep(5);
                }

                Assert.IsTrue(a.TryGetConnectionPath(out var second, out var secondSdp));
                Assert.AreEqual(first, second, "存活期间判定必须稳定。");
                Assert.AreEqual(firstSdp, secondSdp, "存活期间远端候选也必须稳定。");
            }
        }
    }
}
