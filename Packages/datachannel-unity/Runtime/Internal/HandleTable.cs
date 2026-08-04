using System;
using System.Collections.Generic;

namespace DataChannelUnity.Internal
{
    /// <summary>
    /// 原生句柄 → 托管对象的查找表。**只做查找，绝不让任何东西活着。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两张表都持**弱引用**（SPEC §6 / #29 决议 2）。原先持强引用时，对象被字典 root 住，
    /// GC 永远不会触发终结器 —— 于是「忘记 Dispose 由终结器兜底」这套设计从物理上
    /// 就不可能生效，泄漏是静默的双份（托管 + 原生）。
    /// </para>
    /// <para>
    /// 存活由**所有权边**保证而不是由这张表：应用持有 <see cref="PeerConnection"/>，
    /// PC 强持有它的子 <see cref="DataChannel"/>。反过来「由 PC 查子通道」做不到 ——
    /// DC 事件不携带 pc 句柄，那要改 ABI 外加在 C 层自建 dc→pc 映射。
    /// </para>
    /// <para>
    /// **为什么要有这张表？** <c>datachannel-rs</c> 用 <c>rtcSetUserPointer</c> 挂
    /// <c>Box&lt;Self&gt;</c>，一张表都不要。那之所以安全，是因为它的 <c>Drop</c>
    /// **阻塞**等回调收敛、且确定性地在拥有者线程上跑 —— 两个前提在 GC 语言里都不成立。
    /// 句柄表不是绕远路，在 GC 语言里它**就是**那个设计。
    /// </para>
    /// </remarks>
    internal static class HandleTable
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<int, WeakReference<PeerConnection>> Pcs =
            new Dictionary<int, WeakReference<PeerConnection>>();
        private static readonly Dictionary<int, WeakReference<DataChannel>> Dcs =
            new Dictionary<int, WeakReference<DataChannel>>();

        // 清扫用的复用列表。清扫只在 pump 的快照阶段发生（见 SnapshotDataChannels），
        // 所以它天然是单线程访问，但仍在锁内使用以保持一致。
        private static readonly List<int> DeadKeys = new List<int>();

        public static void Register(PeerConnection pc)
        {
            lock (Gate) Pcs[pc.NativeHandle] = new WeakReference<PeerConnection>(pc);
        }

        public static void UnregisterPc(int handle)
        {
            lock (Gate) Pcs.Remove(handle);
        }

        public static void Register(DataChannel dc)
        {
            lock (Gate) Dcs[dc.NativeHandle] = new WeakReference<DataChannel>(dc);
        }

        public static void UnregisterDc(int handle)
        {
            lock (Gate) Dcs.Remove(handle);
        }

        /// <summary>
        /// 查 PC。条目还在但目标已被回收时**当作查不到**，且不在此处摘表 ——
        /// 摘表属于清扫，只在快照阶段做。
        /// </summary>
        public static bool TryGetPc(int handle, out PeerConnection pc)
        {
            lock (Gate)
            {
                if (Pcs.TryGetValue(handle, out var weak) && weak.TryGetTarget(out pc))
                    return true;
            }
            pc = null;
            return false;
        }

        /// <summary>查 DC。语义同 <see cref="TryGetPc"/>。</summary>
        public static bool TryGetDc(int handle, out DataChannel dc)
        {
            lock (Gate)
            {
                if (Dcs.TryGetValue(handle, out var weak) && weak.TryGetTarget(out dc))
                    return true;
            }
            dc = null;
            return false;
        }

        /// <summary>
        /// 把当前所有存活的 DataChannel 拷进调用方提供的复用 List，**并顺带清扫**
        /// 两张表里目标已被回收的条目。
        /// </summary>
        /// <remarks>
        /// <para>
        /// pump 的数据段**必须**遍历快照而不是字典本身：拉到消息会**当场派发**，
        /// 而应用在回调里 <c>Dispose()</c> 通道或 <c>CreateDataChannel()</c> 都完全合法，
        /// 两者都会改动这里的字典 —— <c>Dictionary</c> 枚举器在集合被修改后
        /// <c>MoveNext</c> 直接抛。那个异常来自**我们自己的迭代**，每订阅者的隔离罩
        /// 不住它，会穿透 pump。
        /// </para>
        /// <para>
        /// 弱引用清扫也**只在这里**做，绝不在派发过程中做 —— 理由同上：清扫是一次
        /// 字典改动，放在派发中间就是在自己脚下拆桥。
        /// </para>
        /// </remarks>
        public static void SnapshotDataChannels(List<DataChannel> into)
        {
            into.Clear();
            lock (Gate)
            {
                DeadKeys.Clear();
                foreach (var kv in Dcs)
                {
                    if (kv.Value.TryGetTarget(out var dc)) into.Add(dc);
                    else DeadKeys.Add(kv.Key);
                }
                for (int i = 0; i < DeadKeys.Count; i++) Dcs.Remove(DeadKeys[i]);

                DeadKeys.Clear();
                foreach (var kv in Pcs)
                {
                    if (!kv.Value.TryGetTarget(out _)) DeadKeys.Add(kv.Key);
                }
                for (int i = 0; i < DeadKeys.Count; i++) Pcs.Remove(DeadKeys[i]);
                DeadKeys.Clear();
            }
        }
    }
}
