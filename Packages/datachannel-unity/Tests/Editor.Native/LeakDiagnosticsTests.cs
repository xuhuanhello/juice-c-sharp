using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// native 档：泄漏诊断（#29 决议 4、8、9 / #45 决议 3 / SPEC §6）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条守的东西**以前物理上不可能发生**，所以它值一张门禁票：查找表原先持
    /// **强引用**，对象被字典 root 住，GC 永远不会触发终结器 —— 「忘记 Dispose 由
    /// 终结器兜底」那套设计从来就没有生效过，泄漏是静默的双份。改成弱引用之后
    /// 终结器才第一次真的可达。**弱引用与泄漏诊断是同一件事的两半**，
    /// 只测其中一半等于没测。
    /// </para>
    /// <para>
    /// 本用例**刻意漏掉一个原生对象**，然后自己把它销毁 —— 否则它会被记在套件收尾
    /// 的未销毁计数上（那一位随 S8 才真正生效，但没理由现在就埋一颗雷）。
    /// 销毁走 <c>dcu.h</c> 的公开 C ABI，理由同 <see cref="NativeSuiteTeardown"/>。
    /// </para>
    /// </remarks>
    public sealed class LeakDiagnosticsTests
    {
        [DllImport("datachannel_unity", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dcu_pc_destroy(int pc);

        private readonly List<string> _captured = new List<string>();

        private void Capture(LogLevel level, string message) => _captured.Add(message);

        [SetUp]
        public void Setup()
        {
            Assert.IsTrue(DataChannelRuntime.IsNativeAvailable,
                "原生插件未加载。这是失败而非跳过 —— 若刚重建过插件，需重启 Editor。");
            _captured.Clear();
            DataChannelLog.MessageLogged += Capture;
        }

        [TearDown]
        public void Teardown()
        {
            LogAssert.ignoreFailingMessages = true;
            for (int i = 0; i < 40; i++) { DataChannelRuntime.Pump(); Thread.Sleep(2); }
            DataChannelLog.MessageLogged -= Capture;
        }

        /// <summary>
        /// 造一个 PC 然后**扔掉它**，只带回它的诊断文本。
        /// </summary>
        /// <remarks>
        /// 必须是独立方法且禁内联：局部变量留在当前帧的栈上时，保守式 GC 很可能
        /// 仍然把它当活的。返回 <c>ToString()</c> 而不是对象本身，正是为了让引用
        /// 随这一帧一起消失 —— 而句柄仍能被带出来（<c>NativeHandle</c> 是 internal，
        /// <c>ToString()</c> 是它唯一的公开出口）。
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string CreateAndAbandonPeerConnection()
        {
            var pc = new PeerConnection(new PeerConnectionConfig());
            return pc.ToString();
        }

        private static int ParseHandle(string description)
        {
            const string key = "handle=";
            var start = description.IndexOf(key, StringComparison.Ordinal);
            Assert.Greater(start, -1, "ToString() 里没有 handle= —— 句柄的唯一公开出口被改坏了。");
            start += key.Length;
            var end = start;
            while (end < description.Length && char.IsDigit(description[end])) end++;
            return int.Parse(description.Substring(start, end - start));
        }

        [Test]
        public void UndisposedPeerConnection_IsReportedWithItsCreationStack()
        {
            // 泄漏报告是 Error 级 —— 本用例的**预期产物**，不是失败。
            LogAssert.ignoreFailingMessages = true;
            Assert.AreEqual(LeakDetectionMode.Enabled, DataChannelLog.LeakDetection,
                "本用例的前提是泄漏诊断开着。");

            var handle = ParseHandle(CreateAndAbandonPeerConnection());

            try
            {
                // 编辑器里的 Mono 是保守式 GC，一轮不一定收得动。给它几轮，
                // 但**有上界** —— 超时就是失败，不是「大概是 GC 的锅」。
                var sw = Stopwatch.StartNew();
                var reported = false;
                while (sw.ElapsedMilliseconds < 10000 && !reported)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    DataChannelRuntime.Pump();
                    reported = _captured.Exists(m => m.Contains("被 GC 回收时仍未 Dispose"));
                    if (!reported) Thread.Sleep(20);
                }

                Assert.IsTrue(reported,
                    "未 Dispose 的 PeerConnection 被回收后没有任何泄漏报告。"
                    + "查找表若退回强引用，终结器就永远不会跑 —— 那正是这条门禁存在的原因。");

                var report = _captured.Find(m => m.Contains("被 GC 回收时仍未 Dispose"));
                StringAssert.Contains("handle=" + handle, report,
                    "泄漏报告必须点名是哪个对象。");
                StringAssert.Contains("创建于", report,
                    "Enabled 档必须带**创建时**的调用栈 —— 「在哪儿创建的」几乎就是"
                    + "泄漏诊断的全部信息量，两档合并（#45 决议 3）正是基于这一点。");
            }
            finally
            {
                // 本用例刻意制造的原生泄漏，由本用例自己收拾 —— 否则它会被记在套件收尾的
                // 未销毁计数上。**这一行拿掉，整个套件就会红**（S8 实测：resultState
                // 由 Passed 翻成 Failed(Child)），那正是收尾断言在起作用的证据。
                dcu_pc_destroy(handle);
            }
        }
    }
}
