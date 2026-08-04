using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DataChannelUnity.Internal
{
    /// <summary>
    /// 同类告警每 5 秒最多一条，附本期发生次数与峰值（SPEC §6 / #38 决议 4）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 它是**其它三条告警可用的前提**（#45 决议 4）：#33 的 dropped 计数、
    /// 本片的慢帧告警、#30 的队列深度告警都靠它才不至于刷屏。砍掉它等于连带
    /// 砍掉那三条 —— 而 #38 接受 pump 那条软肋（高速对端能让 pump 段随流量
    /// 线性变长）的**前提条件**恰恰就是「必须可见」。
    /// </para>
    /// <para>
    /// 「过去 5 秒有 143 帧超时，峰值 31ms」比连刷 143 条有用得多，而且
    /// 后者会把 Console 冲爆、顺带把真正要看的东西挤没。
    /// </para>
    /// </remarks>
    internal static class Throttle
    {
        internal const double WindowSeconds = 5.0;

        private static readonly long WindowTicks = (long)(Stopwatch.Frequency * WindowSeconds);
        private static readonly Dictionary<string, Window> Windows = new Dictionary<string, Window>();

        private struct Window
        {
            public long StartTicks;
            public int Suppressed;
            public double Peak;
        }

        /// <summary>
        /// 记一次发生。返回 <c>true</c> 表示现在该说话。
        /// </summary>
        /// <param name="category">节流键。异常类告警用「事件名 + 异常类型」，否则一个坏订阅者会把别人的告警也压掉。</param>
        /// <param name="sample">本次的量值（毫秒、深度……）。没有量值的传 0。</param>
        /// <param name="suppressed">**上一个**窗口里被压掉的条数。首次发生为 0。</param>
        /// <param name="peak">上一个窗口的峰值与本次样本的较大者。</param>
        /// <remarks>
        /// 窗口内的**第一次**立即放行，不是攒满 5 秒再报 —— 故障刚发生时最需要
        /// 那一条，让它等 5 秒等于在最要紧的时刻沉默。之后每 5 秒一条汇总。
        /// </remarks>
        internal static bool Note(string category, double sample, out int suppressed, out double peak)
        {
            var now = Stopwatch.GetTimestamp();

            if (!Windows.TryGetValue(category, out var w) || now - w.StartTicks >= WindowTicks)
            {
                suppressed = w.Suppressed;
                peak = Math.Max(w.Peak, sample);
                Windows[category] = new Window { StartTicks = now, Suppressed = 0, Peak = sample };
                return true;
            }

            w.Suppressed++;
            w.Peak = Math.Max(w.Peak, sample);
            Windows[category] = w;
            suppressed = 0;
            peak = w.Peak;
            return false;
        }

        /// <summary>把「上一期还有多少条被压掉」拼成人话；没有被压掉的返回空串。</summary>
        internal static string SuppressedSuffix(int suppressed, double peak, string unit)
        {
            if (suppressed <= 0) return string.Empty;
            return "（上一个 " + WindowSeconds + " 秒窗口内另有 " + suppressed
                   + " 次同类，峰值 " + peak.ToString("0.##") + unit + "）";
        }

        /// <summary>域重载 / 进入播放模式时清空，避免拿上个域的窗口压住新域的第一条。</summary>
        internal static void Clear() => Windows.Clear();
    }
}
