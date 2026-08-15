using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// The room-level, host-authoritative turn machine: #132's rules, #135's replication, #134's
    /// seats and reconnect. One <see cref="NetworkObject"/> in the scene, and the only
    /// <see cref="NetworkBehaviour"/> in the example besides the sixteen balls' NetworkTransforms.
    ///
    /// ## Why the seat state lives here and not on the connection
    ///
    /// #134's easiest mistake to make: when a client drops, FishNet destroys its
    /// <see cref="NetworkConnection"/> and finishes its cleanup the moment <c>Stopped</c> arrives.
    /// Anything worth keeping — which half of the rack is yours, how much of it you have cleared —
    /// has to already be somewhere else by then. So seats are held on this object, which outlives
    /// every connection in the room, and a reconnecting client is matched to one by token rather
    /// than by connection id (#120 never reuses ids).
    ///
    /// ## What is deliberately not here
    ///
    /// No presentation and no UI. This class exposes a readable public face — <see cref="State"/>,
    /// <see cref="StateChanged"/>, <see cref="ReconnectSecondsRemaining"/> — and stops there; the
    /// aiming, power and rematch controls are a separate ticket. Aiming and cue placement never
    /// cross the network at all (#127): they are local intent until the shot is submitted.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public sealed class BilliardsGame : NetworkBehaviour, ISeatAuthority
    {
        #region Tunables

        [SerializeField]
        [Tooltip("一回合不出杆多久算超时（秒）。#132 定 60；测试里调小以便在合理时长内验到。")]
        private float _turnTimeoutSeconds = BilliardsRules.TurnTimeoutSeconds;

        [SerializeField]
        [Tooltip("客户端掉线后座位留多久（秒）。#134 定 30；测试里调小。")]
        private float _seatHoldSeconds = BilliardsRules.SeatHoldSeconds;

        #endregion

        #region Seats

        /// <summary>
        /// One seat's identity, host-side only. The token is what survives a disconnect: it is
        /// room-scoped (#134), so a client keeps the same half of the rack across rematches.
        /// </summary>
        private sealed class Seat
        {
            /// <summary>Current connection, or -1 when empty or held.</summary>
            public int ConnectionId = -1;

            /// <summary>Room-scoped token this seat's occupant presents on reconnect. Host-issued.</summary>
            public string Token;

            /// <summary>Seconds of hold left, or 0 when not being held.</summary>
            public float HoldRemaining;

            public bool IsOccupied => ConnectionId >= 0;
            public bool IsHeld => HoldRemaining > 0f;
        }

        private readonly Seat[] _seats =
        {
            new Seat(),
            new Seat()
        };

        /// <summary>
        /// Tokens presented at signalling time, before FishNet has a connection to attach them to.
        /// Cleared as each connection resolves to a seat.
        /// </summary>
        private readonly Dictionary<int, string> _presentedTokens = new();

        #endregion

        #region State

        private BilliardsRack _rack;
        private DataChannelTransport _transport;

        private readonly Vector2[] _positions = new Vector2[BilliardsRules.BallCount];

        /// <summary>
        /// The current state. Authoritative on the host; on a client it is whatever the last state
        /// RPC carried. Both sides read it the same way, which is the point of sending it in full.
        /// </summary>
        public BilliardsState State { get; private set; } = new BilliardsState
        {
            Phase = BilliardsPhase.Lobby,
            TurnSeat = BilliardsRules.SeatNone,
            Winner = BilliardsRules.SeatNone,
            BallPositions = new Vector2[BilliardsRules.BallCount]
        };

        /// <summary>Raised on both sides after <see cref="State"/> changes.</summary>
        public event Action<BilliardsState> StateChanged;

        /// <summary>
        /// Raised on the host when a shot has been adjudicated, with the referee's verdict. The
        /// reason string is the only part of a foul that is not derivable from the state, so it is
        /// surfaced here rather than put on the wire.
        /// </summary>
        public event Action<ShotOutcome, TurnVerdict> ShotJudged;

        private float _turnElapsed;
        private bool _breakPending = true;
        private int _shooterThisShot = BilliardsRules.SeatNone;
        private ushort _pocketedBeforeShot;

        /// <summary>
        /// Seconds left on the reconnect hold, for the countdown #134 requires. Host-side it is the
        /// real remaining time; client-side it is a local timer started when the flag went up,
        /// because sending the number every tick would cost more than the whole state message.
        /// </summary>
        public float ReconnectSecondsRemaining { get; private set; }

        private bool _clientCountdownRunning;

        /// <summary>Set when a state RPC arrived before the balls had spawned (#135 §6).</summary>
        private bool _pendingApply;
        private BilliardsState _pendingState;

        /// <summary>Which seat this client believes it holds. Set from the host's welcome.</summary>
        public int LocalSeat { get; private set; } = BilliardsRules.SeatNone;

        /// <summary>True on a client that has just been told the game it left is void (#134).</summary>
        public bool LocalGameVoided { get; private set; }

        #endregion

        #region Lifecycle

        private void Awake()
        {
            _rack = FindObjectOfType<BilliardsRack>();
            if (_rack == null)
            {
                Debug.LogError("[BilliardsGame] No BilliardsRack in the scene; there is nothing to " +
                               "referee. Rebuild the scene with Tools/DataChannel Example/Build " +
                               "Billiards Scene.");
                return;
            }

            _rack.ShotSettled += OnShotSettled;

            // Registered in Awake, well before any connection: the transport asks for the local
            // token while sending its offer, and on a reconnect that happens as early as the client
            // can act at all. Waiting for OnStartClient would be too late.
            _transport = FindObjectOfType<DataChannelTransport>();
            if (_transport != null)
                _transport.SeatAuthority = this;
        }

        private void OnDestroy()
        {
            if (_rack != null)
                _rack.ShotSettled -= OnShotSettled;

            // Left dangling this would keep a destroyed behaviour answering admission questions
            // after a scene change — and the answers would be about a game that no longer exists.
            if (_transport != null && ReferenceEquals(_transport.SeatAuthority, this))
                _transport.SeatAuthority = null;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;

            _breakPending = true;
            PublishState(BilliardsPhase.Lobby, BilliardsRules.SeatNone, BilliardsFlags.None,
                BilliardsRules.SeatNone);
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // A buffered state RPC may have arrived before this object's balls spawned (#135 §6:
            // SendBufferedRpcs runs per NetworkObject, and there is no cross-object ordering).
            // Applying it again here is safe because the message is a full, idempotent snapshot.
            if (_pendingApply)
            {
                ApplyState(_pendingState);
                _pendingApply = false;
            }
        }

        #endregion

        #region Seats and reconnect (#134)

        /// <summary>
        /// The one place a connection becomes a seat, or stops being one.
        ///
        /// The host's own loopback client arrives here too — #120 gave it a real connection id and a
        /// full <c>Started</c> flow rather than short-circuiting it, so there is no separate path for
        /// it and no faked event. It is told apart by asking the transport, not by assuming it
        /// connects first: seat 0 belongs to the host by #132, and inferring that from arrival order
        /// would put a remote client in it on the day the order changes.
        /// </summary>
        private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState == RemoteConnectionState.Started)
                SeatConnection(connection.ClientId);
            else
                ReleaseConnection(connection.ClientId);
        }

        private void SeatConnection(int connectionId)
        {
            _presentedTokens.TryGetValue(connectionId, out string token);
            _presentedTokens.Remove(connectionId);

            bool isHost = _transport != null && _transport.IsLoopbackConnection(connectionId);
            if (isHost)
            {
                _seats[BilliardsRules.SeatHost].ConnectionId = connectionId;
                _seats[BilliardsRules.SeatHost].HoldRemaining = 0f;
                Debug.Log($"[BilliardsGame] 座位 {BilliardsRules.SeatHost}（host）← connection {connectionId}");
                TryBeginGame();
                return;
            }

            // A token that matches a held seat is the reconnect (#134). Checked before looking for a
            // free seat, because the held seat is not free — that is the whole point of holding it.
            int reclaimed = SeatHeldFor(token);
            if (reclaimed >= 0)
            {
                Seat seat = _seats[reclaimed];
                seat.ConnectionId = connectionId;
                seat.HoldRemaining = 0f;
                Debug.Log($"[BilliardsGame] 座位 {reclaimed} 被令牌取回 ← connection {connectionId}");

                // Same token, so the client keeps what it stored: reissuing here would invalidate
                // the copy it is holding and break the *next* reconnect.
                SendWelcome(connectionId, reclaimed, seat.Token, reconnected: true);
                ClearReconnectWaitIfDone();
                return;
            }

            int free = FirstFreeSeat();
            if (free < 0)
            {
                // Admission is the signalling layer's job (#134), so reaching here means the two
                // disagreed. Say so rather than silently seating nobody: a client with no seat looks
                // exactly like one whose UI is broken.
                Debug.LogWarning($"[BilliardsGame] connection {connectionId} 连上了但没有空座位 —— " +
                                 "信令层的满员拦截与座位表不一致。");
                return;
            }

            _seats[free].ConnectionId = connectionId;
            _seats[free].HoldRemaining = 0f;
            _seats[free].Token = NewToken();
            Debug.Log($"[BilliardsGame] 座位 {free} ← connection {connectionId}（新令牌已下发）");

            SendWelcome(connectionId, free, _seats[free].Token, reconnected: false);
            TryBeginGame();
        }

        /// <summary>
        /// A connection dropped. The seat is held rather than freed — and #134's trap is that this is
        /// the last moment anything can be read from that connection, because FishNet destroys it as
        /// soon as this returns.
        /// </summary>
        private void ReleaseConnection(int connectionId)
        {
            for (int seat = 0; seat < _seats.Length; seat++)
            {
                if (_seats[seat].ConnectionId != connectionId)
                    continue;

                _seats[seat].ConnectionId = -1;

                if (seat == BilliardsRules.SeatHost)
                {
                    // The host's own client leaving means the process is going away: FishNet 4.7.2
                    // has no host migration, and the whole game state lives in this physics world.
                    // Nothing to hold.
                    Debug.Log("[BilliardsGame] host 的本地 client 断了，这一局结束。");
                    return;
                }

                _seats[seat].HoldRemaining = _seatHoldSeconds;
                Debug.Log($"[BilliardsGame] 座位 {seat} 留 {_seatHoldSeconds:F0}s 等重连" +
                          $"（connection {connectionId} 断了）");

                // The shot in flight is deliberately left to finish (#134): host-authoritative
                // physics is already running, #131's stop criterion will fire on its own, and the
                // resulting position is exactly the snapshot the state RPC carries. Freezing would
                // need an unfreeze path, and a shot lasts ~5 s against a 30 s window.
                PublishState(State.Phase, State.TurnSeat,
                    CurrentFlags() | BilliardsFlags.AwaitingReconnect, State.Winner);
                return;
            }
        }

        private int SeatHeldFor(string token)
        {
            if (string.IsNullOrEmpty(token))
                return -1;

            for (int seat = 0; seat < _seats.Length; seat++)
            {
                if (_seats[seat].IsHeld && _seats[seat].Token == token)
                    return seat;
            }

            return -1;
        }

        private int FirstFreeSeat()
        {
            // Seat 0 is the host's by #132's pre-assigned groups, so a remote client never takes it
            // even when it happens to be empty.
            for (int seat = BilliardsRules.SeatClient; seat < _seats.Length; seat++)
            {
                if (!_seats[seat].IsOccupied && !_seats[seat].IsHeld)
                    return seat;
            }

            return -1;
        }

        /// <summary>Counts down every held seat, and voids the game if one runs out (#134).</summary>
        private void TickSeatHolds(float delta)
        {
            bool anyHeld = false;
            bool expired = false;

            for (int seat = 0; seat < _seats.Length; seat++)
            {
                if (!_seats[seat].IsHeld)
                    continue;

                _seats[seat].HoldRemaining -= delta;
                if (_seats[seat].HoldRemaining > 0f)
                {
                    anyHeld = true;
                    continue;
                }

                _seats[seat].HoldRemaining = 0f;
                // The token is kept, not cleared: a client that comes back late still presents it,
                // and comparing it against the seat is how the host knows to say "that game is
                // void" instead of greeting a stranger.
                expired = true;
                Debug.Log($"[BilliardsGame] 座位 {seat} 的 {_seatHoldSeconds:F0}s 留座窗口到了，这一局作废。");
            }

            ReconnectSecondsRemaining = anyHeld ? MaxHoldRemaining() : 0f;

            if (expired)
                VoidGame();
        }

        private float MaxHoldRemaining()
        {
            float most = 0f;
            foreach (Seat seat in _seats)
            {
                if (seat.HoldRemaining > most)
                    most = seat.HoldRemaining;
            }

            return most;
        }

        /// <summary>
        /// The hold expired: this game is over with nobody winning. Stated with a flag and a winner
        /// of <see cref="BilliardsRules.SeatNone"/> rather than left for the client to infer, because
        /// "abandoned" and "lost" want different words on screen (#134).
        /// </summary>
        private void VoidGame()
        {
            _breakPending = true;
            PublishState(BilliardsPhase.GameOver, BilliardsRules.SeatNone,
                BilliardsFlags.Abandoned, BilliardsRules.SeatNone);
        }

        private void ClearReconnectWaitIfDone()
        {
            if (HeldSeatCount > 0)
                return;

            PublishState(State.Phase, State.TurnSeat,
                CurrentFlags() & ~BilliardsFlags.AwaitingReconnect, State.Winner);
        }

        /// <summary>
        /// Tells one client which seat it has and what to present if it has to come back.
        ///
        /// A <see cref="TargetRpcAttribute"/> rather than part of the state message: the token is
        /// that client's alone, and #135's state RPC goes to every observer. Putting it there would
        /// hand each client the other's token.
        /// </summary>
        [TargetRpc]
        private void SendWelcome(NetworkConnection target, int seat, string token, bool reconnected)
        {
            LocalSeat = seat;
            LocalGameVoided = false;

            string room = _transport == null ? null : _transport.RoomCode;
            if (!string.IsNullOrEmpty(room) && !string.IsNullOrEmpty(token))
                PlayerPrefs.SetString(TokenKey(room), token);

            Debug.Log(reconnected
                ? $"[BilliardsGame] 已接回座位 {seat}（局面完整，你断线过）"
                : $"[BilliardsGame] 入座 {seat}");
        }

        /// <summary>
        /// Overload taking a connection id, so the seat code does not have to resolve
        /// <see cref="NetworkConnection"/> objects it has no other use for.
        /// </summary>
        private void SendWelcome(int connectionId, int seat, string token, bool reconnected)
        {
            if (!ServerManager.Clients.TryGetValue(connectionId, out NetworkConnection conn))
            {
                Debug.LogWarning($"[BilliardsGame] connection {connectionId} 已经不在 Clients 里，" +
                                 "令牌没送出去。");
                return;
            }

            SendWelcome(conn, seat, token, reconnected);
        }

        /// <summary>
        /// Room-scoped, per #134: the seat a token stands for is a property of the room, not of a
        /// connection or a single game. A per-connection token would be reissued on every connect,
        /// so the one in hand at reconnect time would always be the previous one.
        /// </summary>
        private static string TokenKey(string roomCode) => $"dcu.billiards.seat-token.{roomCode}";

        private static string NewToken() => Guid.NewGuid().ToString("N");

        #endregion

        #region Turn machine

        /// <summary>
        /// Both seats filled and no game running: break. The host breaks the first game of a room;
        /// after that the break belongs to whoever lost the last one (#132).
        /// </summary>
        private void TryBeginGame()
        {
            if (!IsServerStarted)
                return;
            if (State.Phase != BilliardsPhase.Lobby)
                return;
            if (!_seats[BilliardsRules.SeatHost].IsOccupied || !_seats[BilliardsRules.SeatClient].IsOccupied)
                return;

            _rack.ResetRack();
            _breakPending = true;
            PublishState(BilliardsPhase.Break, _nextBreakSeat, BilliardsFlags.None,
                BilliardsRules.SeatNone);
            Debug.Log($"[BilliardsGame] 两人齐了，座位 {_nextBreakSeat} 开球。");
        }

        private byte _nextBreakSeat = BilliardsRules.SeatHost;

        /// <summary>
        /// A shot: direction, power, and where to put the cue ball — five floats on the reliable
        /// channel, exactly as #132 specifies. Aiming itself never crosses the network, so this is
        /// the first and only time the opponent learns anything about it.
        ///
        /// <c>RequireOwnership = false</c> because nobody owns this object: it is room-level, and
        /// authority here is the seat, which the host checks below. Owning it would mean handing one
        /// player the object that adjudicates both.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void SubmitShot(Vector2 direction, float power, Vector2 cueSpot,
            NetworkConnection sender = null)
        {
            int seat = SeatOf(sender);
            if (seat < 0)
            {
                Debug.LogWarning("[BilliardsGame] 收到没有座位的连接发来的出杆，已忽略。");
                return;
            }

            ApplyShot(seat, direction, power, cueSpot);
        }

        /// <summary>
        /// Host-side shot handling, shared by the RPC above and by anything driving the host's own
        /// seat. Every check is here rather than split with the caller, so a shot cannot reach the
        /// table by a path that skips one.
        /// </summary>
        private void ApplyShot(int seat, Vector2 direction, float power, Vector2 cueSpot)
        {
            if (State.Phase != BilliardsPhase.Break && State.Phase != BilliardsPhase.Aim)
            {
                Debug.LogWarning($"[BilliardsGame] 座位 {seat} 在 {State.Phase} 阶段出杆，已忽略。");
                return;
            }

            if (State.TurnSeat != seat)
            {
                Debug.LogWarning($"[BilliardsGame] 座位 {seat} 出杆但现在是座位 {State.TurnSeat} 的回合，已忽略。");
                return;
            }

            // Nothing is accepted while a seat is being held: the opponent is mid-reconnect and the
            // table is theirs to come back to (#134).
            if (State.HasFlag(BilliardsFlags.AwaitingReconnect))
            {
                Debug.Log("[BilliardsGame] 正在等对手重连，这一杆不受理。");
                return;
            }

            if (State.HasFlag(BilliardsFlags.BallInHand))
                PlaceCueBallLegally(cueSpot);
            else if (_rack.CueBall != null && _rack.CueBall.IsPocketed)
                // Cannot happen through the rules — a scratch always grants ball in hand — but a cue
                // ball left in the pocket would make the shot a no-op and the turn would hang.
                PlaceCueBallLegally(new Vector2(BilliardsTable.HeadSpot.x, BilliardsTable.HeadSpot.z));

            _shooterThisShot = seat;
            _pocketedBeforeShot = _rack.PocketedMask();

            // Read before the strike: the shot's own break exemption has to be the phase it was
            // taken in, not the phase it settles in.
            _breakPending = State.Phase == BilliardsPhase.Break;

            _rack.Break(new Vector3(direction.x, 0f, direction.y),
                Mathf.Clamp(power, 0f, BilliardsRules.MaxPower));

            PublishState(BilliardsPhase.Simulate, (byte)seat,
                CurrentFlags() & ~BilliardsFlags.BallInHand, State.Winner);
        }

        /// <summary>
        /// Snaps a requested cue spot to the nearest legal one and puts the ball there. #132 is
        /// explicit that this never refuses: a rejection costs a round trip plus a UI state, and the
        /// snap is the same function that clamps an escaped ball back into play.
        /// </summary>
        private void PlaceCueBallLegally(Vector2 wanted)
        {
            var occupied = new List<Vector2>(BilliardsRules.BallCount);
            foreach (BilliardsBall ball in _rack.Balls)
            {
                if (ball.IsCueBall || ball.IsPocketed)
                    continue;

                Vector3 p = ball.Body.position;
                occupied.Add(new Vector2(p.x, p.z));
            }

            Vector2 legal = BilliardsTable.NearestLegalCueSpot(wanted, occupied);
            if ((legal - wanted).sqrMagnitude > 1e-6f)
                Debug.Log($"[BilliardsGame] 摆球位 {wanted} 非法，已吸附到 {legal}。");

            _rack.PlaceCueBall(legal);
        }

        private int SeatOf(NetworkConnection connection)
        {
            if (connection == null)
                return -1;

            for (int seat = 0; seat < _seats.Length; seat++)
            {
                if (_seats[seat].ConnectionId == connection.ClientId)
                    return seat;
            }

            return -1;
        }

        /// <summary>
        /// The table has settled: adjudicate, then publish. Runs inside the physics step that
        /// detected the stop, which is why the whole verdict is computed from values already read
        /// rather than by touching the rack again.
        /// </summary>
        private void OnShotSettled(float seconds)
        {
            if (!IsServerStarted)
                return;

            // The scene's BilliardsBreakProbe fires its own break half a second into play (#136's
            // scaffolding), and the burst measurement drives the rack directly. Neither is a turn, so
            // anything that settles outside Simulate is not this machine's business.
            if (State.Phase != BilliardsPhase.Simulate)
                return;

            var shot = new ShotOutcome
            {
                Shooter = _shooterThisShot,
                WasBreak = _breakPending,
                FirstContact = _rack.FirstContact,
                PocketedBefore = _pocketedBeforeShot,
                PocketedAfter = _rack.PocketedMask()
            };

            TurnVerdict verdict = BilliardsReferee.Judge(shot);
            ShotJudged?.Invoke(shot, verdict);

            Debug.Log($"[BilliardsGame] 座位 {shot.Shooter} 这一杆 {seconds:F2}s：" +
                      $"首碰={shot.FirstContact} 落袋掩码 {shot.PocketedBefore:X4}→{shot.PocketedAfter:X4} " +
                      $"判定={(verdict.Foul ? "犯规" : "合法")}" +
                      (string.IsNullOrEmpty(verdict.FoulReason) ? "" : $"（{verdict.FoulReason}）"));

            _breakPending = false;

            if (verdict.ReRack)
            {
                _rack.ResetRack();
                PublishState(BilliardsPhase.Break, (byte)shot.Shooter, BilliardsFlags.None,
                    BilliardsRules.SeatNone);
                return;
            }

            if (verdict.GameOver)
            {
                // The loser breaks the next one (#132), so this is recorded now rather than derived
                // later from the winner — a rematch resets everything else.
                _nextBreakSeat = (byte)BilliardsRules.OtherSeat(verdict.Winner);
                PublishState(BilliardsPhase.GameOver, BilliardsRules.SeatNone, BilliardsFlags.None,
                    verdict.Winner);
                return;
            }

            byte next = verdict.ContinueTurn
                ? (byte)shot.Shooter
                : (byte)BilliardsRules.OtherSeat(shot.Shooter);

            // Put the cue ball back before publishing, so the snapshot clients receive shows the
            // table they will actually aim at rather than a cue ball still in the pocket.
            if (verdict.BallInHand && _rack.CueBall != null && _rack.CueBall.IsPocketed)
                PlaceCueBallLegally(new Vector2(BilliardsTable.HeadSpot.x, BilliardsTable.HeadSpot.z));

            BilliardsFlags flags = CurrentFlags() & ~BilliardsFlags.BallInHand;
            if (verdict.BallInHand)
                flags |= BilliardsFlags.BallInHand;

            PublishState(BilliardsPhase.Aim, next, flags, BilliardsRules.SeatNone);
        }

        /// <summary>
        /// The 60 s turn clock (#132) and the 30 s seat hold (#134), both host-side.
        ///
        /// Driven from <c>Update</c> on wall-clock time rather than from the tick: neither is a
        /// physics quantity, and #136 recorded that a duration expressed in ticks silently changes
        /// meaning when TickRate does — the same reason #131's stop criterion is in seconds.
        /// </summary>
        private void Update()
        {
            if (!IsServerStarted)
            {
                TickClientCountdown(Time.deltaTime);
                return;
            }

            TickSeatHolds(Time.deltaTime);

            bool awaitingShot = State.Phase == BilliardsPhase.Aim || State.Phase == BilliardsPhase.Break;
            if (!awaitingShot || State.HasFlag(BilliardsFlags.AwaitingReconnect))
            {
                // The clock is paused, not merely not-read, while a seat is held: a player should not
                // lose their turn to somebody else's connection.
                _turnElapsed = 0f;
                return;
            }

            _turnElapsed += Time.deltaTime;
            if (_turnElapsed < _turnTimeoutSeconds)
                return;

            // Passes the turn and nothing else — #132 is explicit that a timeout is not a foul, so
            // the next player gets no ball in hand.
            byte next = (byte)BilliardsRules.OtherSeat(State.TurnSeat);
            Debug.Log($"[BilliardsGame] 座位 {State.TurnSeat} {_turnTimeoutSeconds:F0}s 没出杆，换座位 {next}" +
                      "（不算犯规）。");
            _turnElapsed = 0f;
            PublishState(BilliardsPhase.Aim, next, CurrentFlags() & ~BilliardsFlags.BallInHand,
                State.Winner);
        }

        /// <summary>
        /// Client-side countdown for the reconnect wait. Local because #134 wants the number visible
        /// — thirty seconds of silence is indistinguishable from a hang — while #135's message has no
        /// room for a value that changes every tick. The flag goes on the wire; the clock is local.
        /// </summary>
        private void TickClientCountdown(float delta)
        {
            if (!_clientCountdownRunning)
                return;

            ReconnectSecondsRemaining = Mathf.Max(0f, ReconnectSecondsRemaining - delta);
        }

        #endregion

        #region Rematch (#132 §4)

        /// <summary>
        /// One seat's rematch consent. Two bits, both visible to both players, and no timeout on
        /// agreeing — #132 removed that deliberately, so the only thing that ends the wait is the
        /// opponent leaving, which #134 handles.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void SubmitRematchReady(NetworkConnection sender = null)
        {
            int seat = SeatOf(sender);
            if (seat < 0)
                return;

            SetRematchReady(seat);
        }

        private void SetRematchReady(int seat)
        {
            if (State.Phase != BilliardsPhase.GameOver && State.Phase != BilliardsPhase.RematchPending)
                return;

            BilliardsFlags flags = CurrentFlags() |
                                   (seat == BilliardsRules.SeatHost
                                       ? BilliardsFlags.HostReady
                                       : BilliardsFlags.ClientReady);

            bool bothReady = (flags & BilliardsFlags.HostReady) != 0 &&
                             (flags & BilliardsFlags.ClientReady) != 0;

            if (!bothReady)
            {
                PublishState(BilliardsPhase.RematchPending, BilliardsRules.SeatNone, flags,
                    State.Winner);
                return;
            }

            _rack.ResetRack();
            _breakPending = true;
            // Ready bits cleared with the rack: they belong to the game that just ended, and a stale
            // one would make the next rematch start half-agreed.
            PublishState(BilliardsPhase.Break, _nextBreakSeat, BilliardsFlags.None,
                BilliardsRules.SeatNone);
            Debug.Log($"[BilliardsGame] 双方都同意再来一局，座位 {_nextBreakSeat}（上一局的输家）开球。");
        }

        #endregion

        #region Replication (#135)

        private BilliardsFlags CurrentFlags() => (BilliardsFlags)State.Flags;

        /// <summary>
        /// Publishes the whole state. Host-side: sets it locally and sends it.
        ///
        /// Full every time, per #135 — which makes it idempotent, and that is what lets the same
        /// message serve as the reconnect snapshot and survive arriving before the balls have
        /// spawned. An incremental version would need the client to keep a copy of what it thinks
        /// the state is, which is one more thing that can be wrong.
        /// </summary>
        private void PublishState(BilliardsPhase phase, byte turnSeat, BilliardsFlags flags, byte winner)
        {
            _rack.WritePositions(_positions);

            var state = new BilliardsState
            {
                Pocketed = _rack.PocketedMask(),
                Phase = phase,
                TurnSeat = turnSeat,
                Flags = (byte)flags,
                Winner = winner,
                BallPositions = _positions
            };

            if (phase == BilliardsPhase.Aim || phase == BilliardsPhase.Break)
                _turnElapsed = 0f;

            State = state;
            StateChanged?.Invoke(state);

            SendState(state.Pocketed, (byte)state.Phase, state.TurnSeat, state.Flags, state.Winner,
                _positions);
        }

        /// <summary>
        /// #135's one message: reliable, buffered, and carrying the state together with the ball
        /// snapshot so the two cannot disagree.
        ///
        /// <c>BufferLast</c> is doing two jobs. It backfills a late joiner (a SyncVar's only real
        /// advantage here, bought for free), and it *is* #134's reconnect snapshot — a returning
        /// client gets sixteen spawn messages, each carrying its ball's transform, and then this.
        /// No third message format, and none invented by us.
        ///
        /// <c>ExcludeServer</c> is left at its default <c>false</c> on purpose. The host's own client
        /// therefore receives this through the loopback PeerConnection like any other client, which is
        /// what makes the byte figures in #130/#136 real bytes over real DTLS rather than a local
        /// call — see <see cref="OutboundByteMeter"/>.
        ///
        /// Parameters are spelled out rather than passed as one struct because FishNet's generated
        /// serializer would add a length prefix for the array either way, and this keeps the wire
        /// layout readable against #135's table: 2 + 1 + 1 + 1 + 1 + 16×8 bytes plus the array's
        /// packed count and the RPC's own header.
        /// </summary>
        [ObserversRpc(BufferLast = true)]
        private void SendState(ushort pocketed, byte phase, byte turnSeat, byte flags, byte winner,
            Vector2[] positions)
        {
            // The host already set this in PublishState; applying the loopback copy again would
            // fire StateChanged twice for one change. Harmless — the message is idempotent — but a
            // subscriber counting turns would count double.
            if (IsServerInitialized)
                return;

            var state = new BilliardsState
            {
                Pocketed = pocketed,
                Phase = (BilliardsPhase)phase,
                TurnSeat = turnSeat,
                Flags = flags,
                Winner = winner,
                BallPositions = positions
            };

            // #135 §6: no cross-NetworkObject ordering exists, so this can arrive before the balls
            // are spawned. Keep it and apply it again on spawn rather than trying to order it.
            if (!IsClientInitialized)
            {
                _pendingState = state;
                _pendingApply = true;
                return;
            }

            ApplyState(state);
        }

        private void ApplyState(BilliardsState state)
        {
            bool wasWaiting = State.HasFlag(BilliardsFlags.AwaitingReconnect);
            bool nowWaiting = state.HasFlag(BilliardsFlags.AwaitingReconnect);

            State = state;

            if (nowWaiting && !wasWaiting)
            {
                // Start the local clock on the edge, not on every message: the host sends this flag
                // once, and #134 wants a countdown rather than a frozen number.
                ReconnectSecondsRemaining = _seatHoldSeconds;
                _clientCountdownRunning = true;
            }
            else if (!nowWaiting)
            {
                _clientCountdownRunning = false;
                ReconnectSecondsRemaining = 0f;
            }

            if (state.HasFlag(BilliardsFlags.Abandoned))
                LocalGameVoided = true;

            StateChanged?.Invoke(state);
        }

        #endregion

        #region Public face (for UI, and for the verification harness)

        /// <summary>
        /// Take a shot at a point on the table. Both players go through here, host included: the
        /// host's <see cref="SubmitShot"/> travels its own loopback PeerConnection like any other
        /// client's (#120 kept that loopback real), so there is one code path and its bytes are
        /// measured rather than skipped.
        /// </summary>
        /// <param name="aimAt">Point to aim through, on the table plane.</param>
        /// <param name="power">Impulse, clamped by the host to <see cref="BilliardsRules.MaxPower"/>.</param>
        /// <param name="cueSpot">Where to place the cue ball; ignored unless ball in hand is set.</param>
        public void Shoot(Vector2 aimAt, float power, Vector2 cueSpot)
        {
            if (_rack?.CueBall == null)
                return;

            // Aim is computed from the authoritative snapshot, not from the rendered transform
            // (#135 §4): the cue ball on screen lags by about two ticks of interpolation, and waiting
            // for it to catch up would add latency for no correctness.
            Vector2 from = State.HasFlag(BilliardsFlags.BallInHand)
                ? cueSpot
                : State.BallPositions != null && State.BallPositions.Length > BilliardsRules.CueBall
                    ? State.BallPositions[BilliardsRules.CueBall]
                    : ToTable(_rack.CueBall.Body.position);

            Vector2 direction = aimAt - from;
            if (direction.sqrMagnitude < 1e-8f)
            {
                Debug.LogWarning("[BilliardsGame] 瞄准点与白球重合，方向无从算起，这一杆没发出。");
                return;
            }

            SubmitShot(direction.normalized, power, cueSpot);
        }

        /// <summary>Signals this client's consent to a rematch (#132 §4).</summary>
        public void OfferRematch() => SubmitRematchReady();

        /// <summary>Host-side: is this seat currently being held for a reconnect (#134)?</summary>
        public bool IsSeatHeld(int seat) =>
            seat >= 0 && seat < _seats.Length && _seats[seat].IsHeld;

        /// <summary>Host-side: the connection currently in a seat, or -1.</summary>
        public int SeatConnectionId(int seat) =>
            seat >= 0 && seat < _seats.Length ? _seats[seat].ConnectionId : -1;

        private static Vector2 ToTable(Vector3 world) => new Vector2(world.x, world.z);

        #endregion

        #region ISeatAuthority

        public int HeldSeatCount
        {
            get
            {
                int held = 0;
                foreach (Seat seat in _seats)
                {
                    if (seat.IsHeld)
                        held++;
                }

                return held;
            }
        }

        public bool TokenReclaimsSeat(string token) => SeatHeldFor(token) >= 0;

        public void RemoteTokenPresented(int connectionId, string token)
        {
            if (string.IsNullOrEmpty(token))
                return;

            _presentedTokens[connectionId] = token;
        }

        public string LocalSeatToken(string roomCode)
        {
            if (string.IsNullOrEmpty(roomCode))
                return null;

            string key = TokenKey(roomCode);
            return PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;
        }

        #endregion
    }
}
