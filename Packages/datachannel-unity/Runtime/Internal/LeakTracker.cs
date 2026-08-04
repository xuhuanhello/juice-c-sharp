using System.Collections.Concurrent;

namespace DataChannelUnity.Internal
{
    /// <summary>被终结器记下的一次泄漏。</summary>
    /// <remarks>
    /// 终结器里能做的事只有「填好这个结构并入队」，所以它必须是**纯数据** ——
    /// 不含任何需要调用才能取到的字段。创建栈在**构造时**就抓好了，正因为
    /// 终结时刻已经没有任何办法知道对象是从哪儿来的。
    /// </remarks>
    internal struct LeakRecord
    {
        public bool IsPeerConnection;
        public int Handle;
        public string Label;
        public string CreationSite;
    }

    /// <summary>
    /// 泄漏诊断（SPEC §6 / #29 决议 4、8、9，两档收窄见 #45 决议 3）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **终结器只做一件事：一次无锁入队。** 不 P/Invoke（<c>rtcDelete*</c> 会持锁
    /// 等回调收敛，域卸载时同步跑终结器就是每次点 Play 卡编辑器）、不取 HandleTable
    /// 的锁（防御纵深：将来 pump 若在持锁期间做阻塞调用，死锁路径会复活）、
    /// 也不碰 <c>Debug.Log*</c>（2022.3 对它在非主线程上的行为没有任何承诺）。
    /// 日志与摘表全部推迟到主线程 pump 的 <see cref="Drain"/>。
    /// </para>
    /// <para>
    /// 泄漏诊断**没有被整个砍掉**，尽管砍掉能把整套终结器机制一并删除。忘记
    /// <c>Dispose</c> 是这份清单上触发概率最高的一项，而唯一的替代兜底
    /// （<c>dcu_shutdown</c> 的未销毁计数）只能告诉你「漏了 N 个」，
    /// 说不出是谁、从哪儿来 —— 对把本包当依赖用的人是大海捞针。
    /// </para>
    /// </remarks>
    internal static class LeakTracker
    {
        // 终结器线程与主线程之间唯一的通道。入队无锁。
        private static readonly ConcurrentQueue<LeakRecord> Pending = new ConcurrentQueue<LeakRecord>();

        /// <summary>
        /// 抓创建现场。<c>Enabled</c> 下返回创建栈，否则返回 <c>null</c>。
        /// </summary>
        /// <remarks>
        /// 只有两档而不是三档（#45 决议 3）：中间那档「报泄漏但不带栈」的价值很小 ——
        /// 「在哪儿创建的」几乎就是泄漏诊断的全部信息量。对象创建不是热路径
        /// （一次会话个位数到几十个），抓栈的开销可接受；创建量真的很大的应用可以设
        /// <see cref="LeakDetectionMode.Disabled"/>。
        /// </remarks>
        internal static string CaptureSite()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (DataChannelLog.LeakDetection != LeakDetectionMode.Enabled) return null;
            // skipFrames = 2：跳过本方法与调用它的构造函数，让栈顶是应用代码。
            return new System.Diagnostics.StackTrace(2, true).ToString();
#else
            // Release 里根本没有终结器，抓了也没人读。
            return null;
#endif
        }

        /// <summary>终结器唯一允许调用的方法。**只入队，不做别的。**</summary>
        internal static void ReportFromFinalizer(LeakRecord record)
        {
            Pending.Enqueue(record);
        }

        /// <summary>
        /// 主线程侧：把积压的泄漏记录打成日志并把表项摘掉。由 pump 调用。
        /// </summary>
        internal static void Drain()
        {
            while (Pending.TryDequeue(out var r))
            {
                // 摘表无条件做 —— 那是簿记，与要不要报告无关。
                if (r.IsPeerConnection) HandleTable.UnregisterPc(r.Handle);
                else HandleTable.UnregisterDc(r.Handle);

                if (DataChannelLog.LeakDetection != LeakDetectionMode.Enabled) continue;

                var what = r.IsPeerConnection
                    ? "PeerConnection(handle=" + r.Handle + ")"
                    : "DataChannel(handle=" + r.Handle + ", label=\"" + r.Label + "\")";

                var site = r.CreationSite != null
                    ? "创建于：\n" + r.CreationSite
                    : "（创建时 LeakDetection 为 Disabled，故没有创建栈。）";

                DataChannelLog.Emit(LogLevel.Error,
                    what + " 被 GC 回收时仍未 Dispose()。**原生对象没有被销毁** —— "
                    + "终结器刻意不调 dcu_*，所以这是一次真实的原生泄漏，不是误报。"
                    + "请显式 Dispose，或用 using。\n" + site);
            }
        }

        /// <summary>域重载 / 进入播放模式时清空积压，避免把上个域的记录报进新域。</summary>
        internal static void Clear()
        {
            while (Pending.TryDequeue(out _)) { }
        }
    }
}
