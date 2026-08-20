using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：主线程契约的回归 —— **一条，不采样更多**（#154 决议 3）。
    /// </summary>
    /// <remarks>
    /// 执法助手全库只有一个（<c>MainThread.Assert</c>），它真实的断裂点是
    /// **主线程身份捕获的接线**：EditMode 下唯一会跑的捕获入口是
    /// <c>[InitializeOnLoadMethod]</c>，恰是域重载类变更最容易静默弄断的东西；
    /// 捕获断裂后所有断言退化为「记一次日志然后放行」。本条从后台线程调一次
    /// <c>Send</c>，能同时证明捕获与断言两截都活着。第二条起测的就是各调用点的
    /// 复制粘贴，不测（#154 的过度设计审计）。
    ///
    /// 边界如实声明：断言是 <c>[Conditional("UNITY_EDITOR")/("DEVELOPMENT_BUILD")]</c>，
    /// 本测试跑在 Editor 下所以有效；Release 下契约靠文档（#29 已定的取舍），
    /// 本条不声称覆盖它。
    /// </remarks>
    public sealed class MainThreadContractTests
    {
        [SetUp]
        public void RequireNative()
        {
            // 缺席必须是失败，不是跳过（SPEC §11）。
            DataChannelRuntime.Preload(); // #146 被动化后，读属性不再触发加载 —— 测试侧显式预热。
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "Native plugin not loaded. This is a failure, not a skip. If the plugin was just rebuilt, restart the Editor.");
        }

        [Test]
        public void Send_FromBackgroundThread_ThrowsInvalidOperation()
        {
            using (var pc = new PeerConnection(new PeerConnectionConfig()))
            {
                // 不需要建连：Send 的主线程断言在一切原生调用之前，
                // 通道未 open 与否不影响本条要证明的事。
                var dc = pc.CreateDataChannel("main-thread-contract");

                Exception caught = null;
                var task = Task.Run(() =>
                {
                    try { dc.Send(new byte[1]); }
                    catch (Exception e) { caught = e; }
                });

                Assert.IsTrue(task.Wait(5000), "The background call did not complete in time.");
                Assert.IsNotNull(caught,
                    "Send from a background thread did not throw. Either the main-thread capture wiring is broken "
                    + "(the assert degraded to log-once-then-allow) or the assert was removed from Send.");
                Assert.IsInstanceOf<InvalidOperationException>(caught,
                    "The main-thread contract must surface as InvalidOperationException; got: " + caught.GetType().Name);

                dc.Dispose();
            }
        }
    }
}
