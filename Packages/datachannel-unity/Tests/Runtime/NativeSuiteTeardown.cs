using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// 套件收尾断言，PlayMode 档（SPEC §11「Suite-level teardown」）。
    /// </summary>
    /// <remarks>
    /// 与 <c>Tests/Editor.Native</c> 里的同名类是**有意的重复**：asmdef 边界不允许
    /// 共享一个文件，而为两个 <c>DllImport</c> 建第三个程序集不值。声明的两个符号
    /// 是 <c>dcu.h</c> 的稳定公开 C ABI（且被导出清单门禁看着），不是内部实现细节 ——
    /// 这里没有为可测性打开任何内部面（#39）。
    ///
    /// 本文件里出现 <c>Pump()</c> 是**收尾排空**，与
    /// <see cref="PumpRegistrationPlayModeTests"/> 里那条「绝不允许出现 Pump()」
    /// 的禁令不冲突：那条禁令守的是「不手动泵，消息也得通」这个断言的有效性，
    /// 而收尾不断言任何这类东西。
    /// </remarks>
    [SetUpFixture]
    public sealed class NativeSuiteTeardown
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string Dll = "__Internal";
#elif UNITY_WEBGL && !UNITY_EDITOR
        private const string Dll = "__Internal";
#else
        private const string Dll = "datachannel_unity";
#endif

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

            for (int i = 0; i < 40; i++)
            {
                DataChannelRuntime.Pump();
                Thread.Sleep(2);
            }

            Assert.AreEqual(0, dcu_event_queue_depth(out var depth), "dcu_event_queue_depth 调用本身失败。");
            Assert.AreEqual(0, depth,
                "控制事件队列没排空。队列无界、永不丢事件，积压只可能来自"
                + "「pump 没跑」或「某个回调卡住了」。");

            var rc = dcu_shutdown(out var undestroyed);

            Assert.AreEqual(0, rc, "dcu_shutdown 调用本身失败。");
            // 与 Editor.Native 档同一条：S8 之后这里才真的看住了「漏了几个」。
            Assert.AreEqual(0, undestroyed,
                "套件跑完时仍有 " + undestroyed + " 个原生对象没被销毁。");

            // 断言之后恢复原生库状态，见 Editor.Native 档同名方法的说明。
            dcu_init();
        }
    }
}
