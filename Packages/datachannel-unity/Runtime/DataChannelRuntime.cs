using System;
using System.Text;
using DataChannelUnity.Internal;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace DataChannelUnity
{
    /// <summary>
    /// Library lifecycle and main-thread event pump.
    /// </summary>
    public static class DataChannelRuntime
    {
        private static bool _nativeReady;
        private static bool _initAttempted;
        private static bool _pumpRegistered;
        private static byte[] _payloadBuf = new byte[65536];
        private static byte[] _payload2Buf = new byte[4096];

        public static bool IsNativeAvailable
        {
            get
            {
                EnsureNative();
                return _nativeReady;
            }
        }

        public static int AbiVersion
        {
            get
            {
                EnsureNative();
                if (!_nativeReady) return 0;
                return NativeMethods.dcu_abi_version();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void OnDomainReload()
        {
            _nativeReady = false;
            _initAttempted = false;
            _pumpRegistered = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            DataChannelLog.EnsureDefaults();
            EnsureNative();
            RegisterPump();
        }

        public static void EnsureNative()
        {
            if (_initAttempted) return;
            _initAttempted = true;
            DataChannelLog.EnsureDefaults();
            try
            {
                var rc = NativeMethods.dcu_init();
                if (rc == NativeMethods.Success)
                {
                    _nativeReady = true;
                    NativeMethods.dcu_set_log_level((int)DataChannelLog.Level);
                    DataChannelLog.Emit(LogLevel.Info, "Native library initialized (abi=" + NativeMethods.dcu_abi_version() + ").");
                }
                else
                {
                    DataChannelLog.Emit(LogLevel.Error, "dcu_init failed: " + rc);
                }
            }
            catch (DllNotFoundException e)
            {
                DataChannelLog.Emit(LogLevel.Error,
                    "Native plugin '" + NativeMethods.DllName + "' not found. Build and place Plugins per docs/SPEC.md. " + e.Message);
            }
            catch (Exception e)
            {
                DataChannelLog.Emit(LogLevel.Error, "Native init exception: " + e.Message);
            }
        }

        public static void RegisterPump()
        {
            if (_pumpRegistered) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!InsertPump(ref loop))
            {
                DataChannelLog.Emit(LogLevel.Warning, "Failed to insert PlayerLoop pump; call DataChannelRuntime.Pump() manually.");
                return;
            }
            PlayerLoop.SetPlayerLoop(loop);
            _pumpRegistered = true;
        }

        private static bool InsertPump(ref PlayerLoopSystem loop)
        {
            var pump = new PlayerLoopSystem
            {
                type = typeof(DataChannelRuntime),
                updateDelegate = Pump
            };

            if (loop.subSystemList == null) return false;
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type == typeof(Update))
                {
                    var subs = loop.subSystemList[i].subSystemList;
                    var list = subs != null
                        ? new System.Collections.Generic.List<PlayerLoopSystem>(subs)
                        : new System.Collections.Generic.List<PlayerLoopSystem>();
                    // Avoid duplicate inserts on domain reload edge cases.
                    list.RemoveAll(s => s.type == typeof(DataChannelRuntime));
                    list.Add(pump);
                    loop.subSystemList[i].subSystemList = list.ToArray();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Drain native event queue and raise managed events on the calling thread (must be main thread).
        /// </summary>
        public static void Pump()
        {
            if (!_nativeReady) return;

            for (int safety = 0; safety < 256; safety++)
            {
                var rc = NativeMethods.dcu_event_peek(out var header);
                if (rc == NativeMethods.ErrNotAvail || header.type == NativeMethods.EventType.None)
                    break;
                if (rc != NativeMethods.Success)
                {
                    DataChannelLog.Emit(LogLevel.Warning, "dcu_event_peek failed: " + rc);
                    break;
                }

                string payload = null;
                string payload2 = null;
                byte[] binary = null;

                if (header.payload_len > 0)
                {
                    EnsureCapacity(ref _payloadBuf, header.payload_len);
                    var n = NativeMethods.dcu_event_copy_payload(_payloadBuf, _payloadBuf.Length);
                    if (n == NativeMethods.ErrTooSmall)
                    {
                        EnsureCapacity(ref _payloadBuf, header.payload_len);
                        n = NativeMethods.dcu_event_copy_payload(_payloadBuf, _payloadBuf.Length);
                    }
                    if (n >= 0)
                    {
                        if (header.type == NativeMethods.EventType.DcMessage)
                        {
                            binary = new byte[n];
                            Buffer.BlockCopy(_payloadBuf, 0, binary, 0, n);
                        }
                        else
                        {
                            payload = Encoding.UTF8.GetString(_payloadBuf, 0, n);
                        }
                    }
                }

                if (header.payload2_len > 0)
                {
                    EnsureCapacity(ref _payload2Buf, header.payload2_len);
                    var n2 = NativeMethods.dcu_event_copy_payload2(_payload2Buf, _payload2Buf.Length);
                    if (n2 >= 0)
                        payload2 = Encoding.UTF8.GetString(_payload2Buf, 0, n2);
                }

                NativeMethods.dcu_event_pop();
                Dispatch(header, payload, payload2, binary);
            }
        }

        private static void EnsureCapacity(ref byte[] buf, int need)
        {
            if (buf == null || buf.Length < need)
                buf = new byte[Math.Max(need, 1024)];
        }

        private static void Dispatch(NativeMethods.EventHeader h, string p1, string p2, byte[] binary)
        {
            switch (h.type)
            {
                case NativeMethods.EventType.LocalDescription:
                    if (HandleTable.TryGetPc(h.pc, out var pc1))
                        pc1.RaiseLocalDescription(p1 ?? string.Empty, p2 ?? string.Empty);
                    break;
                case NativeMethods.EventType.LocalCandidate:
                    if (HandleTable.TryGetPc(h.pc, out var pc2))
                        pc2.RaiseLocalCandidate(p1 ?? string.Empty, p2);
                    break;
                case NativeMethods.EventType.ConnectionState:
                    if (HandleTable.TryGetPc(h.pc, out var pc3))
                        pc3.RaiseConnectionState((ConnectionState)h.state);
                    break;
                case NativeMethods.EventType.GatheringState:
                    if (HandleTable.TryGetPc(h.pc, out var pc4))
                        pc4.RaiseGatheringState((GatheringState)h.state);
                    break;
                case NativeMethods.EventType.IncomingDataChannel:
                    if (HandleTable.TryGetPc(h.pc, out var pc5))
                    {
                        var dc = new DataChannel(pc5, h.dc, p1 ?? string.Empty, ownsCreation: false);
                        HandleTable.Register(dc);
                        pc5.RaiseIncomingDataChannel(dc);
                    }
                    break;
                case NativeMethods.EventType.DcOpen:
                    if (HandleTable.TryGetDc(h.dc, out var d1))
                        d1.RaiseOpen();
                    break;
                case NativeMethods.EventType.DcClosed:
                    if (HandleTable.TryGetDc(h.dc, out var d2))
                        d2.RaiseClosed();
                    break;
                case NativeMethods.EventType.DcError:
                    if (HandleTable.TryGetDc(h.dc, out var d3))
                        d3.RaiseError(p1 ?? "error");
                    break;
                case NativeMethods.EventType.DcMessage:
                    if (HandleTable.TryGetDc(h.dc, out var d4))
                        d4.RaiseMessage(binary ?? Array.Empty<byte>());
                    break;
            }
        }

        internal static int RequireCreate(int code, string what)
        {
            if (code > 0) return code;
            throw new DataChannelException(what + " failed with code " + code, code);
        }

        internal static void RequireOk(int code, string what)
        {
            if (code == NativeMethods.Success) return;
            throw new DataChannelException(what + " failed with code " + code, code);
        }
    }
}
