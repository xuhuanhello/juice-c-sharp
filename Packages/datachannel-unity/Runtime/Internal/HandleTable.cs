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
    }
}
