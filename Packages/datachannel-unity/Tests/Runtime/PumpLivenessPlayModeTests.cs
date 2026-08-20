using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.TestTools;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// PlayMode 档：pump 被第三方从 <c>PlayerLoop</c> 抹掉之后的行为
    /// （#30 决议 5，按 #45 决议 2 收窄 / SPEC §6）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 它必须在 PlayMode，因为 EditMode 根本没有 <c>PlayerLoop</c>，抹不掉也装不回。
    /// </para>
    /// <para>
    /// 守的是一条**被刻意削掉一半**的机制：原设计是「检测 + 无限自愈」，#45 把自愈
    /// 降级成**重试一次后停手**。无限自愈是在跟另一个包来回抢 PlayerLoop，而且是
    /// 静默地抢；真正的收益在检测与归因 —— 故障本身极显眼（什么都不通），难的是
    /// 归因，第一嫌疑人永远是网络或 TURN，绝不会是帧循环。
    /// </para>
    /// <para>
    /// 所以本用例要钉住的是**两件事**：重试确实发生过一次，以及**第二次确实没有发生**。
    /// 只测前者的话，把代码改回无限自愈仍然全绿。
    /// </para>
    /// </remarks>
    public sealed class PumpLivenessPlayModeTests
    {
        // 必须大于 DataChannelRuntime 里那个 internal 的 PumpStaleSeconds（5 秒）。
        // 拿不到那个常量是**故意的**：#39 否掉了为可测性开 InternalsVisibleTo，
        // 而这里等久一点的代价只是几秒钟。
        private const float StaleWaitSeconds = 6.5f;

        private readonly List<string> _captured = new List<string>();

        private void Capture(LogLevel level, string message) => _captured.Add(message);

        [SetUp]
        public void Setup()
        {
            DataChannelRuntime.Preload(); // #146 被动化后，读属性不再触发加载 —— 测试侧显式预热。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip.");
            _captured.Clear();
            DataChannelLog.MessageLogged += Capture;
        }

        /// <summary>
        /// 无论断言成不成立都要把 pump 装回去并排空 —— 否则后续测试跑在一个死泵上，
        /// 而且套件收尾的 <c>dcu_event_queue_depth()==0</c> 会被本用例制造的积压弄红。
        /// </summary>
        [UnityTearDown]
        public IEnumerator RestorePump()
        {
            DataChannelLog.MessageLogged -= Capture;
            LogAssert.ignoreFailingMessages = true;
            if (!PumpIsRegistered()) InsertPumpEntry();
            for (int i = 0; i < 60; i++) yield return null;
        }

        [UnityTest]
        public IEnumerator ErasedPump_ReregistersOnce_ThenStopsRetrying()
        {
            // pump 异常路径全程记 Error，那是本用例的**预期产物**。
            LogAssert.ignoreFailingMessages = true;

            Assert.IsTrue(PumpIsRegistered(), "Precondition violated: the pump should already be registered when this test starts.");

            // ---- 第一轮：抹掉 -> 等 -> 调 API -> 应该重试注册一次 ----
            ErasePumpEntry();
            Assert.IsFalse(PumpIsRegistered(), "The simulation had no effect; the entry is still there.");

            yield return WaitRealtime(StaleWaitSeconds);
            TouchPublicApi();

            Assert.IsTrue(PumpIsRegistered(),
                "Once the pump has been stalled past the threshold, the first public API call must retry registration exactly once.");
            Assert.That(_captured, Has.Some.Contains("Retrying registration ONCE"),
                "The error must say what it did, and it MUST name the likely cause (a third-party SetPlayerLoop): "
                + "the entire value of the detection is attribution; the fault itself is already obvious.");
            Assert.That(_captured, Has.Some.Contains("SetPlayerLoop"),
                "An error that does not name the cause only tells people that something is broken.");

            _captured.Clear();

            // ---- 第二轮：再抹一次 -> 等 -> 调 API -> 必须**不再**装回去 ----
            ErasePumpEntry();
            yield return WaitRealtime(StaleWaitSeconds);
            TouchPublicApi();

            Assert.IsFalse(PumpIsRegistered(),
                "After being wiped a second time it must NOT re-insert: that is an endless tug-of-war with another package over the "
                + "PlayerLoop, and a silent one (#45, resolution 2).");
            Assert.That(_captured, Has.Some.Contains("Retries have STOPPED"),
                "Giving up must be stated out loud. Silently doing nothing is worse than silently retrying.");
        }

        // ------------------------------------------------------------------

        private static IEnumerator WaitRealtime(float seconds)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }

        /// <summary>调一次会触发存活检测的公开 API。检测**只在应用调 API 时发生**，不后台轮询。</summary>
        private static void TouchPublicApi()
        {
            using (var pc = new PeerConnection(new PeerConnectionConfig()))
            {
                // 创建即触发。这里不需要它做任何别的事。
            }
        }

        /// <summary>
        /// 只摘掉本包的条目。
        /// </summary>
        /// <remarks>
        /// 真实故障形态是第三方 <c>SetPlayerLoop(GetDefaultPlayerLoop())</c>，那会连带
        /// 丢掉**所有人**的条目。这里只摘我们的，因为对被测逻辑而言唯一相关的事实就是
        /// 「我们的条目没了」，而误伤测试框架自己的东西只会给本用例引入无关的不稳定。
        /// </remarks>
        private static void ErasePumpEntry()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                var subs = loop.subSystemList[i].subSystemList;
                if (subs == null) continue;
                var kept = new List<PlayerLoopSystem>(subs.Length);
                for (int j = 0; j < subs.Length; j++)
                    if (subs[j].type != typeof(DataChannelRuntime)) kept.Add(subs[j]);
                loop.subSystemList[i].subSystemList = kept.ToArray();
            }
            PlayerLoop.SetPlayerLoop(loop);
        }

        /// <summary>把 pump 条目装回去。与包内注册同形，但只用公开的 <c>Pump()</c>。</summary>
        private static void InsertPumpEntry()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(Update)) continue;
                var subs = loop.subSystemList[i].subSystemList;
                var list = subs != null
                    ? new List<PlayerLoopSystem>(subs)
                    : new List<PlayerLoopSystem>();
                list.Add(new PlayerLoopSystem
                {
                    type = typeof(DataChannelRuntime),
                    updateDelegate = DataChannelRuntime.Pump
                });
                loop.subSystemList[i].subSystemList = list.ToArray();
                PlayerLoop.SetPlayerLoop(loop);
                return;
            }
        }

        private static bool PumpIsRegistered() => Contains(PlayerLoop.GetCurrentPlayerLoop());

        private static bool Contains(PlayerLoopSystem system)
        {
            if (system.type == typeof(DataChannelRuntime)) return true;
            if (system.subSystemList == null) return false;
            for (int i = 0; i < system.subSystemList.Length; i++)
                if (Contains(system.subSystemList[i])) return true;
            return false;
        }
    }
}
