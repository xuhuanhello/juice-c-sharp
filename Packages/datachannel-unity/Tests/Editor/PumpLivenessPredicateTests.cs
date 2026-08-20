using System.Reflection;
using NUnit.Framework;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// 托管档：存活判定纯谓词的边界表测（#147，依据 #145 的实测）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「编辑器暂停」「移动端挂起」没法在测试进程里自演（暂停会冻结测试运行器
    /// 自己，#145 是真编辑器手测定案的）——所以判定被抽成纯谓词，边界在这里
    /// 钉死；「真抹除 → 触发 + 重试恰一次」的集成面由
    /// <c>PumpLivenessPlayModeTests</c> 守着。两半合起来才是完整覆盖。
    /// </para>
    /// <para>
    /// **反射而非 InternalsVisibleTo**：#39 拒绝为可测性开内部面，且谓词没有
    /// 公开路径可走（它的可观测效果恰恰是那些没法自演的场景）。反射把开口
    /// 限制在本测试自身；谓词改名时这里当场红 —— 红得响亮，不是静默失效。
    /// </para>
    /// </remarks>
    public sealed class PumpLivenessPredicateTests
    {
        private static string Judge(double staleSeconds, long frameDelta, bool isPlaying)
        {
            var m = typeof(DataChannelRuntime).GetMethod(
                "JudgePumpLiveness", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(m,
                "DataChannelRuntime.JudgePumpLiveness was not found. If it was renamed or made non-static, "
                + "update this table test in the same change — the predicate is the testable half of #147.");
            return m.Invoke(null, new object[] { staleSeconds, frameDelta, isPlaying }).ToString();
        }

        [Test]
        public void FreshPump_IsSilent_RegardlessOfFrames()
        {
            // 不够陈旧就沉默，帧数无关 —— 5s 阈值是第一道门。
            Assert.AreEqual("Silent", Judge(4.9, 0, true));
            Assert.AreEqual("Silent", Judge(4.9, 1000, true));
            Assert.AreEqual("Silent", Judge(0.0, 1000, false));
        }

        [Test]
        public void FrozenLoop_IsSilent_EvenWhenVeryStale()
        {
            // #145 的暂停场景在体外复现：极陈旧但帧没推进 = 循环本身没在跑，
            // 不是泵的故障。旧实现在这里误报「第三方抹了 PlayerLoop」并烧掉
            // 唯一的重试额度 —— 本组三条就是那次实测的回归钉子。
            Assert.AreEqual("Silent", Judge(30.0, 0, true));
            Assert.AreEqual("Silent", Judge(5.1, 1, true));   // 健康稳态 delta ∈ {0,1}
            Assert.AreEqual("Silent", Judge(5.1, 2, true));   // 余量帧
        }

        [Test]
        public void AdvancingLoopWithoutPump_IsStalled_FromThreeFramesOn()
        {
            // 帧在推进而泵没跑 —— 真停摆，3 帧是触发下界。
            Assert.AreEqual("Stalled", Judge(5.1, 3, true));
            Assert.AreEqual("Stalled", Judge(5.1, 1000, true));
            Assert.AreEqual("Stalled", Judge(30.0, 3, true));
        }

        [Test]
        public void EditMode_Notice_PrecedesFrameCheck()
        {
            // 判序契约：编辑模式先于帧判定 —— 编辑模式下帧号不推进，若先查帧，
            // 「常驻 pump 缺席」的如实提示会被 Silent 吞掉。
            Assert.AreEqual("EditModeNotice", Judge(5.1, 0, false));
            Assert.AreEqual("EditModeNotice", Judge(30.0, 1000, false));
        }
    }
}
