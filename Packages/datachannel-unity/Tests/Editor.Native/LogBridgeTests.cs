using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：日志桥与凭证脱敏（#33 / SPEC §7）。
    ///
    /// 脱敏测试**刻意走公开日志路径**而不是直接调那个正则（它已收 internal，
    /// 且 #39 明确否掉为可测性开 InternalsVisibleTo）。走公开路径顺带覆盖了
    /// 「脱敏有没有被真正接进日志路径」—— 正则写对了但没被调用时，
    /// 直接调内部方法的测法会全绿。
    /// </summary>
    public sealed class LogBridgeTests
    {
        private readonly List<string> _captured = new List<string>();
        private LogLevel _savedLevel;

        private void Capture(LogLevel level, string message) => _captured.Add(message);

        [SetUp]
        public void Setup()
        {
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "原生插件未加载。这是失败而非跳过 —— 若刚重建过插件，需重启 Editor。");
            _savedLevel = DataChannelLog.Level;
            _captured.Clear();
            DataChannelLog.MessageLogged += Capture;
        }

        [TearDown]
        public void Teardown()
        {
            PumpAWhile();               // 谁触发，谁负责排空 —— 别留给下一个测试
            DataChannelLog.MessageLogged -= Capture;
            DataChannelLog.Level = _savedLevel;
        }

        private void PumpAWhile(int iterations = 40)
        {
            for (int i = 0; i < iterations; i++)
            {
                DataChannelRuntime.Pump();
                Thread.Sleep(2);
            }
        }

        [Test]
        public void NativeFailureText_ReachesManagedLog()
        {
            // 本用例**刻意**触发原生错误；那些 Error 日志是预期产物，不是失败。
            LogAssert.ignoreFailingMessages = true;
            // 迁移时 dcu_wrap 把 e.what() 丢掉了（当时没有日志出口），桥就位后补回。
            // 错误码告诉你是哪一类，文本告诉你是哪一个。
            var cfg = new PeerConnectionConfig();
            cfg.IceServers.Add(new IceServer("ftp://example.com:3478"));
            Assert.Throws<DataChannelException>(() => new PeerConnection(cfg));

            PumpAWhile();

            Assert.That(_captured, Has.Some.Contains("protocol"),
                "上游异常文本必须经桥到达托管日志，否则只剩一个不知所云的错误码。");
        }

        /// <summary>
        /// 凭证脱敏，**经公开日志路径验证**。
        /// </summary>
        /// <remarks>
        /// 触发形态是实测选的，不是猜的：只有 <c>parse_url</c> 失败那条异常会携带
        /// **完整 URL**（<c>"Invalid ICE server URL: " + url</c>）。端口非法只带端口串，
        /// 未知协议只带协议名，都够不着凭证。<c>turn:user:pass@</c>（缺主机）是实测
        /// 会走到那条分支、且形态能被脱敏正则匹配的最小输入。
        ///
        /// 顺带一提，实测还发现 <c>turn:user:pass@host:99999999</c> 会被**接受**
        /// （端口经 stoul 后截断），所以别拿「看起来更离谱」的串来替换它。
        /// </remarks>
        [Test]
        public void IceCredentialsInNativeLog_AreRedacted()
        {
            // 本用例**刻意**触发原生错误；那些 Error 日志是预期产物，不是失败。
            LogAssert.ignoreFailingMessages = true;
            var cfg = new PeerConnectionConfig();
            cfg.IceServers.Add(new IceServer("turn:alice:s3cret@"));
            Assert.Throws<DataChannelException>(() => new PeerConnection(cfg));

            PumpAWhile();

            var all = string.Join(" | ", _captured);
            Assert.That(all, Does.Contain("credentials=redacted@"),
                "含凭证的 URL 必须被脱敏后才进日志。");
            Assert.That(all, Does.Not.Contain("s3cret"),
                "凭证明文出现在日志里 —— 脱敏没接进日志路径，或正则没匹配上这个形态。");
        }

        [Test]
        public void RepeatedLevelChanges_DoNotDetachTheBridge()
        {
            // 本用例**刻意**触发原生错误；那些 Error 日志是预期产物，不是失败。
            LogAssert.ignoreFailingMessages = true;
            // 回归上游 InitLogger 的非幂等：appender 已存在时传 nullptr 会把回调置空
            // 并静默回落 std::cout —— 桥没了、零告警。dcu 层结构性消除了这条路径。
            for (int i = 0; i < 5; i++)
            {
                DataChannelLog.Level = LogLevel.Debug;
                DataChannelLog.Level = LogLevel.Info;
            }

            _captured.Clear();
            var cfg = new PeerConnectionConfig();
            cfg.IceServers.Add(new IceServer("ftp://example.com:3478"));
            Assert.Throws<DataChannelException>(() => new PeerConnection(cfg));

            PumpAWhile();

            Assert.That(_captured, Has.Some.Contains("protocol"),
                "连续改级别之后桥必须仍然活着。收不到说明 InitLogger 的 nullptr 路径复活了。");
        }
    }
}
