using System;
using System.Collections.Generic;

namespace DataChannelUnity.Internal
{
    internal static class HandleTable
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<int, PeerConnection> Pcs = new Dictionary<int, PeerConnection>();
        private static readonly Dictionary<int, DataChannel> Dcs = new Dictionary<int, DataChannel>();

        public static void Register(PeerConnection pc)
        {
            lock (Gate) Pcs[pc.NativeHandle] = pc;
        }

        public static void UnregisterPc(int handle)
        {
            lock (Gate) Pcs.Remove(handle);
        }

        public static void Register(DataChannel dc)
        {
            lock (Gate) Dcs[dc.NativeHandle] = dc;
        }

        public static void UnregisterDc(int handle)
        {
            lock (Gate) Dcs.Remove(handle);
        }

        public static bool TryGetPc(int handle, out PeerConnection pc)
        {
            lock (Gate) return Pcs.TryGetValue(handle, out pc);
        }

        public static bool TryGetDc(int handle, out DataChannel dc)
        {
            lock (Gate) return Dcs.TryGetValue(handle, out dc);
        }

        /// <summary>
        /// 把当前所有 DataChannel 拷进调用方提供的复用 List。
        /// </summary>
        /// <remarks>
        /// pump 的数据段**必须**遍历快照而不是字典本身：拉到消息会**当场派发**，
        /// 而应用在回调里 <c>Dispose()</c> 通道或 <c>CreateDataChannel()</c> 都完全合法，
        /// 两者都会改动这里的字典 —— <c>Dictionary</c> 枚举器在集合被修改后
        /// <c>MoveNext</c> 直接抛。那个异常来自**我们自己的迭代**，每订阅者的隔离罩
        /// 不住它，会穿透 pump。
        /// </remarks>
        public static void SnapshotDataChannels(List<DataChannel> into)
        {
            into.Clear();
            lock (Gate)
            {
                foreach (var kv in Dcs)
                    into.Add(kv.Value);
            }
        }
    }
}
