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
        /// Peak and mean over the ticks at or after <see cref="MeasureFromTick"/>. Ticks before the
        /// break are excluded on purpose: spawn messages and the connection handshake are large
        /// one-offs on the reliable channel, and averaging them into the per-tick figure would
        /// describe a burst nobody will ever see again.
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

            int peakTotal = 0, peakUnreliable = 0, peakReliable = 0, peakMessages = 0;
            uint peakTick = 0;
            long sumUnreliable = 0, sumTotal = 0;
            int counted = 0;
            int overMtu = 0;
            int peakBacklog = 0, peakBacklogReliable = 0, peakBacklogUnreliable = 0;
            uint peakBacklogTick = 0;
            int ticksWithBacklog = 0;

            foreach (uint tick in ticks)
            {
                TickRecord r = _byTick[tick];
                if (tick < MeasureFromTick)
                    continue;

                counted++;
                sumUnreliable += r.Unreliable;
                sumTotal += r.Total;

                if (r.Total > peakTotal)
                {
                    peakTotal = r.Total;
                    peakTick = tick;
                    peakMessages = r.Messages;
                }

                if (r.Unreliable > peakUnreliable)
                    peakUnreliable = r.Unreliable;
                if (r.Reliable > peakReliable)
                    peakReliable = r.Reliable;

                // 1282 is what GetMTU reports (DataChannelTransport.MtuBytes). #131 predicted this
                // would never be crossed; recorded rather than asserted so the answer comes from
                // the measurement.
                if (r.Unreliable > 1282)
                    overMtu++;

                if (r.Backlog > 0)
                    ticksWithBacklog++;
                if (r.Backlog > peakBacklog)
                {
                    peakBacklog = r.Backlog;
                    peakBacklogTick = tick;
                }

                if (r.BacklogReliable > peakBacklogReliable)
                    peakBacklogReliable = r.BacklogReliable;
                if (r.BacklogUnreliable > peakBacklogUnreliable)
                    peakBacklogUnreliable = r.BacklogUnreliable;
            }

            sb.AppendLine($"ticksCounted={counted}");
            sb.AppendLine($"peakTotal={peakTotal}B at tick {peakTick} ({peakMessages} messages)");
            sb.AppendLine($"peakUnreliable={peakUnreliable}B peakReliable={peakReliable}B");
            if (counted > 0)
            {
                sb.AppendLine($"meanTotal={sumTotal / (double)counted:F1}B " +
                              $"meanUnreliable={sumUnreliable / (double)counted:F1}B");
            }

            sb.AppendLine($"ticksOverMtu(1282)={overMtu}");
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
