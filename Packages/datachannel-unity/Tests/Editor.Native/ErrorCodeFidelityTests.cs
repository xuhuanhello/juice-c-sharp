using NUnit.Framework;
using UnityEngine.TestTools;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：错误码保真与调用约定（#31 / SPEC §4）。
    ///
    /// 这组用例守的是 S1 的核心决议 —— **不认识的失败绝不压平成 Failure**。
    /// 压平丢掉的恰是最有诊断价值的那一位：Invalid（你传的参数不对，可自助修复）
    /// 被伪装成 Failure（运行时问题，只能提 issue）。迁移前这条路径返回的正是 Failure。
    /// </summary>
    public sealed class ErrorCodeFidelityTests
    {
        /// <summary>
        /// 未知协议 —— 上游 <c>IceServer::IceServer(const string&amp;)</c> 会抛
        /// <c>std::invalid_argument("Unknown ICE server protocol: ...")</c>。
        /// </summary>
        /// <remarks>
        /// **别改成「一个看起来乱七八糟的串」**。实测 <c>"not-a-url-at-all"</c> 是
        /// **被接受**的：<c>parse_url</c> 在没有 scheme 时默认成 <c>stun</c>，于是它
        /// 变成一台主机名为 <c>not-a-url-at-all</c>、端口 3478 的 STUN 服务器，不抛。
        /// 实测会抛的形态：未知协议、坏端口（<c>stun:host:notaport</c>）、
        /// <c>"://"</c>、<c>"turn:"</c>。
        /// </remarks>
        private const string BadUrl = "ftp://example.com:3478";

        [SetUp]
        public void RequireNative()
        {
            // 缺席必须是失败，不是跳过（SPEC §11）。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip. If the plugin was just rebuilt, restart the Editor.");
        }

        [TearDown]
        public void DrainAndRestore()
        {
            // 实测：测试体里设的 ignoreFailingMessages 在进入 TearDown 前会被框架重置，
            // 所以这里要**再设一次** —— 排空动作本身就会把预期的 Error 日志放出来。
            LogAssert.ignoreFailingMessages = true;
            DrainPendingNativeLogs();
        }

        // 原生日志是**异步**的：触发原生错误的测试若不在结束前排空队列，那条日志会被
        // 下一个碰巧会 pump 的测试排出来，炸在一个无辜的测试上。本套件实际撞到过：
        // ErrorCodeFidelityTests 触发的 "ftp" 错误炸在了 EventQueueAtomicityTests 上。
        // 所以：**谁触发，谁负责排空。**
        private static void DrainPendingNativeLogs()
        {
            for (int i = 0; i < 30; i++)
            {
                DataChannelRuntime.Pump();
                System.Threading.Thread.Sleep(2);
            }
        }

        [Test]
        public void AbiVersion_MatchesHeader_ViaOutParameter()
        {
            // 本条验的是「返回码 + out 参数」这条调用约定端到端成立：
            // 旧形状下版本号是**返回值**，新形状下它经 out 参数带出。
            //
            // 期望值**从 dcu.h 读**，不写死在这里。写死的那个数字曾经把方法名都
            // 带跑了（原名 AbiVersion_IsTwo_ViaOutParameter），于是每次有意的 ABI
            // 变更都要顺手改一处与本条意图无关的断言 —— 而它红的时候看着像是
            // 「ABI 坏了」，其实只是这里的数字过期了。
            // docs/verification-mcp.md 第 33 行对这件事有明文：期望数字住在被检查
            // 的地方（dcu.h / expected-symbols.txt），不住在别处的散文里。
            var header = System.IO.Path.Combine(
                System.IO.Directory.GetCurrentDirectory(), "native/dcu/include/dcu.h");

            // 缺席必须是失败，不是跳过（SPEC §11）。
            Assert.IsTrue(System.IO.File.Exists(header),
                "Cannot read the source of truth for DCU_ABI_VERSION: " + header);

            var m = System.Text.RegularExpressions.Regex.Match(
                System.IO.File.ReadAllText(header),
                @"^\s*#define\s+DCU_ABI_VERSION\s+(\d+)\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            Assert.IsTrue(m.Success, "No DCU_ABI_VERSION define found in " + header);

            var expected = int.Parse(m.Groups[1].Value);
            Assert.AreEqual(expected, DataChannelRuntime.AbiVersion,
                "The out-parameter calling convention must carry dcu.h's DCU_ABI_VERSION verbatim.");
        }

        [Test]
        public void MalformedIceUrl_MapsToInvalid_NotFlattenedToFailure()
        {
            // 本用例**刻意**触发原生错误；那些 Error 日志是预期产物，不是失败。
            LogAssert.ignoreFailingMessages = true;
            var cfg = new PeerConnectionConfig();
            cfg.IceServers.Add(new IceServer(BadUrl));

            var ex = Assert.Throws<DataChannelException>(() => new PeerConnection(cfg));

            Assert.AreEqual(DataChannelError.Invalid, ex.ErrorCode,
                "Upstream throws std::invalid_argument, which must map faithfully to Invalid. "
                + "Landing on Failure means the mapping was flattened again.");
            Assert.AreEqual((int)DataChannelError.Invalid, ex.RawCode,
                "RawCode must carry the raw ABI value through.");
        }

        [Test]
        public void GarbageRemoteDescription_MapsToInvalid()
        {
            // 本用例**刻意**触发原生错误；那些 Error 日志是预期产物，不是失败。
            LogAssert.ignoreFailingMessages = true;
            using (var pc = new PeerConnection(new PeerConnectionConfig()))
            {
                var ex = Assert.Throws<DataChannelException>(
                    () => pc.SetRemoteDescription("this is not sdp", "offer"));
                Assert.AreEqual(DataChannelError.Invalid, ex.ErrorCode);
            }
        }

        [Test]
        public void ExceptionMessage_SelfFixableShape_MentionsRawCode()
        {
            // 本用例**刻意**触发原生错误；那些 Error 日志是预期产物，不是失败。
            LogAssert.ignoreFailingMessages = true;
            var cfg = new PeerConnectionConfig();
            cfg.IceServers.Add(new IceServer(BadUrl));

            var ex = Assert.Throws<DataChannelException>(() => new PeerConnection(cfg));

            // 两类措辞的价值是告诉人「该查自己还是该找我们」；RawCode 始终带在消息里。
            Assert.That(ex.Message, Does.Contain("raw=" + (int)DataChannelError.Invalid));
            Assert.That(ex.Message, Does.Contain("invalid argument"));
        }

        [Test]
        public void ErrorEnum_ValuesAreIndependentlyNumbered()
        {
            // 独立编号是本片的立身之本：与上游 RTC_ERR_* 的 -1..-4 刻意错开，
            // 这样任何「直接透传上游码」的回归都会落到 Unknown 而不是伪装成合法码。
            Assert.AreEqual(-101, (int)DataChannelError.Invalid);
            Assert.AreEqual(-102, (int)DataChannelError.Failure);
            Assert.AreEqual(-103, (int)DataChannelError.NotAvailable);
            Assert.AreEqual(-104, (int)DataChannelError.TooSmall);
            Assert.AreEqual(-105, (int)DataChannelError.UpstreamUnknown);

            foreach (var raw in new[] { -1, -2, -3, -4 })
                Assert.AreNotEqual(raw, (int)DataChannelError.Invalid,
                    "dcu error codes must not be value-identical to the upstream RTC_ERR_* codes.");
        }
    }
}
