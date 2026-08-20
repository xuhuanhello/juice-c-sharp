using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：进程身份横幅 —— SPEC §7 的 identity-banner 例外（#140 定设计，#142 落地）。
    ///
    /// 横幅在 <c>EnsureNative</c> 的成功分支里，而 <c>_initAttempted</c> 在一个域里
    /// 只放行一次 init —— 所以这些测试用反射把 init 门（与横幅 latch）拨回去，
    /// 重演一次**真实** init：<c>dcu_init</c> 幂等，第二次照样返回 Success ——
    /// #141 的两行正是这么来的，本套件顺带把那个形状变成了回归钉子。
    /// latch 本身不放宽（#142 明说别为可测性放宽）；测试改的是自己的观测方式。
    ///
    /// 反射而非 InternalsVisibleTo，同 <c>PumpLivenessPredicateTests</c> 的理由（#39）：
    /// 开口限制在本测试自身，字段改名时这里当场红 —— 红得响亮，不是静默失效。
    /// </summary>
    public sealed class ProcessIdentityBannerTests
    {
        private const string BannerMarker = "Native library initialized";

        private readonly List<LogLevel> _dispatchedLevels = new List<LogLevel>();
        private readonly List<string> _dispatchedMessages = new List<string>();
        private readonly List<LogType> _consoleTypes = new List<LogType>();
        private readonly List<string> _consoleLines = new List<string>();
        private LogLevel _savedLevel;

        private void CaptureDispatch(LogLevel level, string message)
        {
            _dispatchedLevels.Add(level);
            _dispatchedMessages.Add(message);
        }

        private void CaptureConsole(string condition, string stackTrace, LogType type)
        {
            if (!condition.Contains("[DataChannelUnity]")) return;
            _consoleTypes.Add(type);
            _consoleLines.Add(condition);
        }

        private static FieldInfo RuntimeField(string name)
        {
            var f = typeof(DataChannelRuntime).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(f,
                "DataChannelRuntime." + name + " was not found. If the field was renamed, update this suite "
                + "in the same change — it is the testable half of the SPEC §7 identity-banner exception (#142).");
            return f;
        }

        /// <summary>只拨 init 门：让 EnsureNative 再走一遍完整成功分支（#141 的形状）。</summary>
        private static void RewindInitGate() => RuntimeField("_initAttempted").SetValue(null, false);

        /// <summary>拨回横幅 latch：让「这个进程还没发过横幅」可以被重演。</summary>
        private static void RewindBannerLatch() => RuntimeField("_abiBannerEmitted").SetValue(null, false);

        private void ClearCaptures()
        {
            _dispatchedLevels.Clear();
            _dispatchedMessages.Clear();
            _consoleTypes.Clear();
            _consoleLines.Clear();
        }

        private int DispatchedBannerCount()
        {
            int n = 0;
            for (int i = 0; i < _dispatchedMessages.Count; i++)
                if (_dispatchedMessages[i].Contains(BannerMarker)) n++;
            return n;
        }

        private int ConsoleBannerCount()
        {
            int n = 0;
            for (int i = 0; i < _consoleLines.Count; i++)
                if (_consoleLines[i].Contains(BannerMarker)) n++;
            return n;
        }

        [SetUp]
        public void Setup()
        {
            DataChannelRuntime.Preload(); // #146 被动化后，读属性不再触发加载 —— 测试侧显式预热。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip. If the plugin was just rebuilt, restart the Editor.");
            _savedLevel = DataChannelLog.Level;
            ClearCaptures();
            DataChannelLog.MessageLogged += CaptureDispatch;
            Application.logMessageReceived += CaptureConsole;
        }

        [TearDown]
        public void Teardown()
        {
            PumpAWhile();               // 谁触发，谁负责排空 —— 别留给下一个测试
            Application.logMessageReceived -= CaptureConsole;
            DataChannelLog.MessageLogged -= CaptureDispatch;
            DataChannelLog.Level = _savedLevel;
        }

        private static void PumpAWhile(int iterations = 20)
        {
            for (int i = 0; i < iterations; i++)
            {
                DataChannelRuntime.Pump();
                Thread.Sleep(2);
            }
        }

        /// <summary>
        /// #140 的整个起因：Info=4 &gt; Warning=3，旧路径在 Release 默认档把横幅丢掉 ——
        /// 恰好是 bug 报告最需要 ABI 号的那批构建。搬出门禁后它必须出现，
        /// 且经 MessageLogged 派发时级别参数是它诚实的严重度 Info，
        /// Console 形态是 Debug.Log（不是告警）。
        /// </summary>
        [Test]
        public void Banner_IsEmitted_AtReleaseDefaultLevel()
        {
            DataChannelLog.Level = LogLevel.Warning;   // 非 Development Player 的默认档（SPEC §7）
            RewindInitGate();
            RewindBannerLatch();
            ClearCaptures();

            DataChannelRuntime.Preload();

            Assert.AreEqual(1, DispatchedBannerCount(),
                "The ABI banner must reach MessageLogged exactly once at the release default level. "
                + "Zero means the level gate is back in front of it (the #140 defect).");
            var idx = _dispatchedMessages.FindIndex(m => m.Contains(BannerMarker));
            Assert.AreEqual(LogLevel.Info, _dispatchedLevels[idx],
                "The banner's MessageLogged level parameter must be Info — its honest severity. "
                + "Subscribers' own filtering policy is theirs, not ours to pre-chew.");

            Assert.AreEqual(1, ConsoleBannerCount(),
                "The ABI banner must reach the Console exactly once at the release default level.");
            var cidx = _consoleLines.FindIndex(m => m.Contains(BannerMarker));
            Assert.AreEqual(LogType.Log, _consoleTypes[cidx],
                "The banner must go out through Debug.Log — it is not an alert, so LogWarning/LogError are wrong.");
        }

        /// <summary>
        /// None 是绝对静默 —— 唯一一档用户明确表达了意图的值（#140 Q3），
        /// 横幅也得闭嘴。
        /// </summary>
        [Test]
        public void Banner_IsSilent_AtLevelNone()
        {
            DataChannelLog.Level = LogLevel.None;
            RewindInitGate();
            RewindBannerLatch();
            ClearCaptures();

            DataChannelRuntime.Preload();

            Assert.AreEqual(0, DispatchedBannerCount(),
                "LogLevel.None is absolute silence; the banner bypasses the verbosity gate, not the user's explicit mute.");
            Assert.AreEqual(0, ConsoleBannerCount(),
                "LogLevel.None must silence the banner on the Console too.");
        }

        /// <summary>
        /// 边界第一条（每进程至多发一次）的直接护栏，且它有过真实先例：#141 正是
        /// init 在同一个域里合法地跑了两次、横幅跟着印了两次 —— 当时只是 Editor
        /// Console 噪声，搬出门禁后同类回归会出现在每个使用者的 Release 日志里。
        /// 原生 g_inited 的幂等挡不住它：幂等的是 init，不是那行日志。
        /// </summary>
        [Test]
        public void Banner_EmitsAtMostOnce_WhenInitRunsTwiceInOneProcess()
        {
            DataChannelLog.Level = LogLevel.Warning;
            RewindInitGate();
            RewindBannerLatch();
            ClearCaptures();

            DataChannelRuntime.Preload();   // 第一次：出横幅
            RewindInitGate();               // 只拨 init 门，不动 latch —— 重演 #141 的第二次 init
            DataChannelRuntime.Preload();   // 第二次：完整成功分支再走一遍，latch 必须挡下横幅

            Assert.AreEqual(1, DispatchedBannerCount(),
                "Init legitimately ran twice in one process; the banner must still be emitted at most once (#141).");
            Assert.AreEqual(1, ConsoleBannerCount(),
                "The second init must not print a second Console banner (#141).");
        }

        /// <summary>
        /// 防回归的另一半：搬出门禁**不是**把 Release 默认抬到 Info（#140 明确否掉的
        /// 方案 C）。普通 Info 行 —— 比如 DataChannelRuntime 里 buffer 首次超基线
        /// 那条 —— 在 Warning 档必须照旧被丢掉。两条一起放出来就是回归。
        /// </summary>
        [Test]
        public void OrdinaryInfoLine_StaysGated_AtReleaseDefault()
        {
            DataChannelLog.Level = LogLevel.Warning;
            ClearCaptures();

            var emit = typeof(DataChannelLog).GetMethod(
                "Emit", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(LogLevel), typeof(string) }, null);
            Assert.IsNotNull(emit,
                "DataChannelLog.Emit(LogLevel, string) was not found. If it was renamed, update this suite "
                + "in the same change — this test pins that ordinary Info lines stay behind the level gate (#140/#142).");
            emit.Invoke(null, new object[] { LogLevel.Info, "ordinary info probe line (must stay gated)" });

            Assert.IsFalse(_dispatchedMessages.Exists(m => m.Contains("ordinary info probe line")),
                "An ordinary Info line reached MessageLogged at Level=Warning: the level gate regressed "
                + "into 'release default raised to Info' — the option #140 explicitly refused.");
            Assert.IsFalse(_consoleLines.Exists(m => m.Contains("ordinary info probe line")),
                "An ordinary Info line reached the Console at Level=Warning.");
        }

        /// <summary>
        /// 横幅本身不含凭据；这条钉的是入口的处理链完整性 —— 绕过的只有级别门禁，
        /// 不是全部处理（#142）：脱敏照常。
        /// </summary>
        [Test]
        public void IdentityBanner_PassesRedaction()
        {
            DataChannelLog.Level = LogLevel.Warning;
            ClearCaptures();

            var emitIdentity = typeof(DataChannelLog).GetMethod(
                "EmitProcessIdentity", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(emitIdentity,
                "DataChannelLog.EmitProcessIdentity was not found. If it was renamed, update this suite "
                + "in the same change — it is the managed half of the SPEC §7 identity-banner exception (#142).");
            emitIdentity.Invoke(null, new object[] { "identity probe with turn:alice:s3cret@host inside" });

            var all = string.Join(" | ", _dispatchedMessages);
            Assert.That(all, Does.Contain("credentials=redacted@"),
                "The identity entry point must run redaction like every other log line: "
                + "it bypasses the level gate, not the rest of the pipeline.");
            Assert.That(all, Does.Not.Contain("s3cret"),
                "Plaintext credentials passed through the identity entry point unredacted.");
        }
    }
}
