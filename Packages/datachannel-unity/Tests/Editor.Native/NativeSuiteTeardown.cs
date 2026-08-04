using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// 套件收尾断言（SPEC §11「Suite-level teardown」/ CONTRIBUTING 第 7 条）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两条判据都**不依赖日志桥**，也不靠在 Console 里 grep 一行英文 ——
    /// 那是「让缺席变成沉默」那个病的第四种形态。
    /// </para>
    /// <para>
    /// **为什么直接 P/Invoke 而不是走包的内部方法。** <c>NativeMethods</c> 是
    /// <c>internal</c>，而 #39 明确否掉「为可测性开 <c>InternalsVisibleTo</c>」。
    /// 这里声明的两个符号不是内部实现细节，它们是 <c>dcu.h</c> 里那个**稳定的公开
    /// C ABI**，并且被 <c>expected-symbols.txt</c> 的导出清单门禁逐名字看着。
    /// 用 ABI 去验 ABI，没有打开任何内部面。
    /// </para>
    /// <para>
    /// 代价是这个文件在两个 native 档程序集里各有一份 —— asmdef 边界不允许共享，
    /// 而为两个类型建第三个程序集不值。
    /// </para>
    /// <para>
    /// **读结果时要看对字段。** 用临时探针实测过：本方法失败时，
    /// <c>get_test_job</c> 报的是 <c>summary.failed = 0</c> 而
    /// <c>resultState = Failed(Child)</c> —— <c>SetUpFixture</c> 不是一个 test case，
    /// 它的失败不进 failed 计数。只看 <c>summary.failed</c> 的人会把红的读成绿的。
    /// </para>
    /// </remarks>
    [SetUpFixture]
    public sealed class NativeSuiteTeardown
    {
        private const string Dll = "datachannel_unity";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dcu_shutdown(out int undestroyed);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dcu_init();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dcu_event_queue_depth(out int depth);

        [OneTimeTearDown]
        public void AssertDrainedAndShutDownCleanly()
        {
            // 缺席必须是失败：插件没加载时不静默跳过收尾断言。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "原生插件未加载，套件收尾断言无法执行。这是失败而非跳过。");

            LogAssert.ignoreFailingMessages = true;

            // 先给 pump 机会排空最后一批事件（原生回调是异步的）。
            for (int i = 0; i < 40; i++)
            {
                DataChannelRuntime.Pump();
                Thread.Sleep(2);
            }

            Assert.AreEqual(0, dcu_event_queue_depth(out var depth), "dcu_event_queue_depth 调用本身失败。");
            Assert.AreEqual(0, depth,
                "控制事件队列没排空。队列是无界的、永不丢事件，所以积压只可能来自"
                + "「pump 没跑」或「某个回调卡住了」—— 两者都是缺陷。");

            var rc = dcu_shutdown(out var undestroyed);

            Assert.AreEqual(0, rc,
                "dcu_shutdown 调用本身失败（上游 Cleanup 超时会落到这里）。");

            // S6 时这条判据只覆盖了一半 —— 当时 dcu_shutdown 只返回状态码，
            // 「漏了几个对象」那一位没人看着。S8 让它经 out 参数带出未销毁计数，
            // 这里才补齐。计数由 dcu 层的句柄表自己给出，不依赖上游、也不依赖日志桥
            // （上游 rtcCleanup 返回 void 且把自己最有价值的两条诊断吞进 plog）。
            Assert.AreEqual(0, undestroyed,
                "套件跑完时仍有 " + undestroyed + " 个原生对象没被销毁。"
                + "有 PeerConnection / DataChannel 没被 Dispose —— 找那个忘了 using 的测试。");

            // 收尾之后把原生库恢复到托管侧以为的状态：DataChannelRuntime 的
            // _initAttempted / _nativeReady 仍是 true，不重新 init 的话，同一个域里
            // 后续任何用法都会打在一个已 Cleanup 的库上。这发生在断言**之后**，
            // 不掩盖任何失败。
            dcu_init();
        }
    }
}
