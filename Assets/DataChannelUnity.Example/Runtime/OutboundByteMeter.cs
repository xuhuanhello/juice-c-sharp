using System.Collections.Generic;
using System.Text;
using FishNet.Managing;
using FishNet.Managing.Timing;
using FishNet.Transporting;
using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// Measures outbound bytes per FishNet tick, split by channel. This is #136's acceptance line
    /// and #130's input: the burst that decides GetMTU and the backpressure policy has to be
    /// measured under real load, never estimated (#113 Notes — an invented 64 KB/1 MB pair was
    /// withdrawn for exactly that reason).
    ///
    /// Why the tick is the bucket, and why it can be read straight from the callback: FishNet
    /// flushes a tick's queued packets from TimeManager's tick loop, and both IterateOutgoing calls
    /// happen *before* LocalTick is incremented (TimeManager.cs:765-773). So during a send,
    /// LocalTick still names the tick whose data is going out — no off-by-one correction.
    ///
    /// Host mode is a real measurement here, not a stand-in. FishNet's server sends to the host's
    /// own client through the ordinary path: the transform RPC is an [ObserversRpc] whose
    /// ExcludeServer defaults to false (Attributes.cs:70), and TransportManager.SendToClients has
    /// no special case for the local connection — so Transport.SendToClient runs for connection 0
    /// and the bytes are counted. The receiving side then drops the payload
    /// (NetworkTransform.cs:2299), but that is after transmission and does not change the count.
    /// </summary>
    public sealed class OutboundByteMeter : MonoBehaviour
    {
        private sealed class TickRecord
        {
            public int Reliable;
            public int Unreliable;
            public int Messages;

            /// <summary>
            /// SCTP's send backlog after this tick was flushed, summed over connections. Separate from
            /// the byte counts because it answers a different question: those say how much was handed
            /// to the transport, this says how much has not left yet.
            /// </summary>
            public int BacklogReliable;
            public int BacklogUnreliable;

            public int Total => Reliable + Unreliable;
            public int Backlog => BacklogReliable + BacklogUnreliable;
        }

        private readonly Dictionary<uint, TickRecord> _byTick = new();
        private DataChannelTransport _transport;
        private TimeManager _timeManager;
        private bool _subscribed;

        /// <summary>Set by whoever drives the shot, so ticks can be split into before/during.</summary>
        public uint MeasureFromTick { get; set; }

        public int TicksRecorded => _byTick.Count;

        private void Awake()
        {
            TrySubscribe();
        }

        private void OnEnable()
        {
            TrySubscribe();
        }

        /// <summary>
        /// Retried from Update as well as Awake: the transport is found through NetworkManager,
        /// which resolves its managers in its own Awake, and Unity gives no ordering guarantee
        /// between two Awakes. Failing silently here would produce an empty report that looks like
        /// "nothing was sent" rather than "never attached".
        /// </summary>
        private void TrySubscribe()
        {
            if (_subscribed)
                return;

            var manager = FindObjectOfType<NetworkManager>();
            if (manager == null)
                return;

            _timeManager = manager.TimeManager;
            Transport found = manager.TransportManager == null
                ? null
                : manager.TransportManager.Transport;

            _transport = found as DataChannelTransport;

            if (_transport == null)
            {
                // A wrong transport must not read as "nothing was sent". FishNet substitutes Tugboat
                // without complaint when no Transport is on the NetworkManager
                // (TransportManager.cs:268), and a measurement taken over UDP sockets would look
                // entirely plausible in the report.
                if (found != null)
                {
                    Debug.LogError($"[OutboundByteMeter] Transport is {found.GetType().Name}, " +
                                   "not DataChannelTransport — nothing will be measured. FishNet " +
                                   "falls back to Tugboat when the transport component is missing.");
                    _subscribed = true; // Do not retry every frame; the scene needs fixing.
                }

                return;
            }

            _transport.OutboundSent += OnOutboundSent;
            _transport.OutboundFlushed += OnOutboundFlushed;
            _subscribed = true;
        }

        /// <summary>
        /// Samples the send backlog once per tick, after the flush. #130 needs this curve because the
        /// upstream send queue is unbounded and send() never fails — so "cannot keep up" never surfaces
        /// as an error, only as this number climbing.
        /// </summary>
        private void OnOutboundFlushed(bool asServer)
        {
            if (!asServer || _transport == null)
                return;

            uint tick = _timeManager == null ? 0u : _timeManager.LocalTick;
            int reliable = 0;
            int unreliable = 0;

            foreach (int connectionId in _transport.ServerConnectionIds)
            {
                if (_transport.TryGetBufferedAmount(true, connectionId, Channel.Reliable, out int r))
                    reliable += r;
                if (_transport.TryGetBufferedAmount(true, connectionId, Channel.Unreliable, out int u))
                    unreliable += u;
            }

            // Recorded even when zero, and that matters: a flat zero is itself the finding for an
            // in-process loopback, where nothing throttles the link. Skipping empty samples would make
            // "no backlog" indistinguishable from "never sampled".
            TickRecord record = RecordFor(tick);
            record.BacklogReliable = Mathf.Max(record.BacklogReliable, reliable);
            record.BacklogUnreliable = Mathf.Max(record.BacklogUnreliable, unreliable);
        }

        private TickRecord RecordFor(uint tick)
        {
            if (!_byTick.TryGetValue(tick, out TickRecord record))
            {
                record = new TickRecord();
                _byTick[tick] = record;
            }

            return record;
        }

        private void Update()
        {
            TrySubscribe();
        }

        private void OnDisable()
        {
            if (_transport != null && _subscribed)
            {
                _transport.OutboundSent -= OnOutboundSent;
                _transport.OutboundFlushed -= OnOutboundFlushed;
            }

            _subscribed = false;
        }

        private void OnOutboundSent(bool asServer, int connectionId, Channel channel, int bytes)
        {
            // Server-side only. The host's client also sends (pings, timing adjustments), but the
            // claim under test is "host replicating 16 balls", and mixing the client's own upstream
            // into the same bucket would inflate it with traffic that has nothing to do with the
            // balls.
            if (!asServer)
                return;

            uint tick = _timeManager == null ? 0u : _timeManager.LocalTick;
            TickRecord record = RecordFor(tick);

            if (channel == Channel.Reliable)
                record.Reliable += bytes;
            else
                record.Unreliable += bytes;

            record.Messages++;
        }

        public void Reset()
        {
            _byTick.Clear();
        }

        /// <summary>
        /// GetMTU 报的那个数（<c>DataChannelTransport.MtuBytes</c>，#130 实测定死）。越过它会让
        /// FishNet 的分片路径把超大消息**改走 Reliable**，于是过期的球位会被重传（#119）。
        /// </summary>
        public const int MtuBytes = 1282;

        /// <summary>
        /// <see cref="Report()"/> 里那些数的结构化版本，给真机报告用。
        ///
        /// 存在的理由是「一份真相」：报告需要这些数当 JSON 字段，而从 <see cref="Report()"/>
        /// 那段文本里正则抠数会让格式变成契约 —— 那段文本是给人读的。
        /// </summary>
        public struct Summary
        {
            public uint MeasureFromTick;
            public int TicksRecorded;
            public int TicksCounted;

            public int PeakTotal;
            public int PeakUnreliable;
            public int PeakReliable;
            public int PeakMessages;
            public uint PeakTick;

            public double MeanTotal;
            public double MeanUnreliable;

            /// <summary>越过 <see cref="MtuBytes"/> 的 tick 数。判据是 0。</summary>
            public int TicksOverMtu;

            public int PeakBacklog;
            public int PeakBacklogReliable;
            public int PeakBacklogUnreliable;
            public uint PeakBacklogTick;

            /// <summary>背压非零的 tick 数。真 TURN 链路上恒  0 说明压根没压出背压。</summary>
            public int TicksWithAnyBacklog;
        }

        /// <summary>
        /// Peak and mean over the ticks at or after <see cref="MeasureFromTick"/>. Ticks before the
        /// break are excluded on purpose: spawn messages and the connection handshake are large
        /// one-offs on the reliable channel, and averaging them into the per-tick figure would
        /// describe a burst nobody will ever see again.
        /// </summary>
        public Summary Summarise()
        {
            var summary = new Summary
            {
                MeasureFromTick = MeasureFromTick,
                TicksRecorded = _byTick.Count
            };

            if (_byTick.Count == 0)
                return summary;

            var ticks = new List<uint>(_byTick.Keys);
            ticks.Sort();

            long sumUnreliable = 0, sumTotal = 0;

            foreach (uint tick in ticks)
            {
                TickRecord r = _byTick[tick];
                if (tick < MeasureFromTick)
                    continue;

                summary.TicksCounted++;
                sumUnreliable += r.Unreliable;
                sumTotal += r.Total;

                if (r.Total > summary.PeakTotal)
                {
                    summary.PeakTotal = r.Total;
                    summary.PeakTick = tick;
                    summary.PeakMessages = r.Messages;
                }

                if (r.Unreliable > summary.PeakUnreliable)
                    summary.PeakUnreliable = r.Unreliable;
                if (r.Reliable > summary.PeakReliable)
                    summary.PeakReliable = r.Reliable;

                // #131 predicted this would never be crossed; recorded rather than asserted so the
                // answer comes from the measurement.
                if (r.Unreliable > MtuBytes)
                    summary.TicksOverMtu++;

                if (r.Backlog > 0)
                    summary.TicksWithAnyBacklog++;
                if (r.Backlog > summary.PeakBacklog)
                {
                    summary.PeakBacklog = r.Backlog;
                    summary.PeakBacklogTick = tick;
                }

                if (r.BacklogReliable > summary.PeakBacklogReliable)
                    summary.PeakBacklogReliable = r.BacklogReliable;
                if (r.BacklogUnreliable > summary.PeakBacklogUnreliable)
                    summary.PeakBacklogUnreliable = r.BacklogUnreliable;
            }

            if (summary.TicksCounted > 0)
            {
                summary.MeanTotal = sumTotal / (double)summary.TicksCounted;
                summary.MeanUnreliable = sumUnreliable / (double)summary.TicksCounted;
            }

            return summary;
        }

        /// <summary>
        /// The same numbers as <see cref="Summarise"/>, laid out for a human plus the per-tick detail.
        /// The field names are parsed by the PlayMode tests, so they are effectively frozen.
        /// </summary>
        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"measureFromTick={MeasureFromTick} ticksRecorded={_byTick.Count}");

            if (_byTick.Count == 0)
            {
                sb.AppendLine("NO OUTBOUND BYTES RECORDED — meter never saw a send.");
                return sb.ToString();
            }

            var ticks = new List<uint>(_byTick.Keys);
            ticks.Sort();

            Summary s = Summarise();
            int counted = s.TicksCounted;
            int peakTotal = s.PeakTotal, peakUnreliable = s.PeakUnreliable;
            int peakReliable = s.PeakReliable, peakMessages = s.PeakMessages;
            uint peakTick = s.PeakTick;
            int overMtu = s.TicksOverMtu;
            int peakBacklog = s.PeakBacklog;
            int peakBacklogReliable = s.PeakBacklogReliable;
            int peakBacklogUnreliable = s.PeakBacklogUnreliable;
            uint peakBacklogTick = s.PeakBacklogTick;
            int ticksWithBacklog = s.TicksWithAnyBacklog;

            sb.AppendLine($"ticksCounted={counted}");
            sb.AppendLine($"peakTotal={peakTotal}B at tick {peakTick} ({peakMessages} messages)");
            sb.AppendLine($"peakUnreliable={peakUnreliable}B peakReliable={peakReliable}B");
            if (counted > 0)
            {
                sb.AppendLine($"meanTotal={s.MeanTotal:F1}B " +
                              $"meanUnreliable={s.MeanUnreliable:F1}B");
            }

            sb.AppendLine($"ticksOverMtu({MtuBytes})={overMtu}");
            sb.AppendLine($"peakBacklog={peakBacklog}B at tick {peakBacklogTick} " +
                          $"(reliable {peakBacklogReliable}B, unreliable {peakBacklogUnreliable}B)");
            sb.AppendLine($"ticksWithAnyBacklog={ticksWithBacklog}/{counted}");
            sb.AppendLine();
            sb.AppendLine("per-tick detail (tick: unreliable + reliable = total, msgs, backlog):");

            foreach (uint tick in ticks)
            {
                TickRecord r = _byTick[tick];
                string mark = tick < MeasureFromTick ? " (pre-break)" : "";
                sb.AppendLine($"  {tick,6}: {r.Unreliable,6} + {r.Reliable,6} = {r.Total,6}  " +
                              $"msgs={r.Messages,3}  backlog={r.Backlog,6}{mark}");
            }

            return sb.ToString();
        }
    }
}
