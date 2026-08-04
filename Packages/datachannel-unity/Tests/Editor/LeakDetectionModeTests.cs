using System;
using NUnit.Framework;

namespace DataChannelUnity.Tests
{
    /// <summary>
    /// 托管档：纯 C#，零 P/Invoke。
    ///
    /// 守的是 #45 决议 3 —— <see cref="LeakDetectionMode"/> **两档，不是三档**。
    /// 这条和 <see cref="ConfigSerializationTests"/> 是同一个形状：一个
    /// **看起来像遗漏、实际是被论证过的删减**，最容易被后人「顺手补全」。
    /// 中间那档（开泄漏诊断但不抓创建栈）被砍掉的理由是它几乎没有信息量 ——
    /// 「在哪儿创建的」就是泄漏诊断的全部价值，而多一档要多定义一套语义、
    /// 多写一段规格、多测一遍。
    /// </summary>
    public sealed class LeakDetectionModeTests
    {
        [Test]
        public void LeakDetectionMode_HasExactlyTwoModes()
        {
            CollectionAssert.AreEquivalent(
                new[] { LeakDetectionMode.Disabled, LeakDetectionMode.Enabled },
                Enum.GetValues(typeof(LeakDetectionMode)),
                "LeakDetectionMode must have exactly two tiers. If you are about to add EnabledWithStackTrace back, "
                + "read issue #45 resolution 3 first: Enabled already carries the creation stack.");
        }

        [Test]
        public void LeakDetection_DefaultsToEnabledInEditor()
        {
            // 编辑器 / Development 构建默认开，Release 默认关。终结器本身还有一层
            // 条件编译，两层是叠加而不是二选一 —— 见 SPEC §6。
            Assert.AreEqual(LeakDetectionMode.Enabled, DataChannelLog.LeakDetection,
                "Leak diagnostics must default to on in the Editor: forgetting Dispose is the most likely failure on this list, "
                + "and the only alternative fallback can just tell you that N objects leaked, not which ones or where from.");
        }

        [Test]
        public void LeakDetection_RoundTrips()
        {
            var saved = DataChannelLog.LeakDetection;
            try
            {
                DataChannelLog.LeakDetection = LeakDetectionMode.Disabled;
                Assert.AreEqual(LeakDetectionMode.Disabled, DataChannelLog.LeakDetection);
                DataChannelLog.LeakDetection = LeakDetectionMode.Enabled;
                Assert.AreEqual(LeakDetectionMode.Enabled, DataChannelLog.LeakDetection);
            }
            finally
            {
                DataChannelLog.LeakDetection = saved;
            }
        }
    }
}
