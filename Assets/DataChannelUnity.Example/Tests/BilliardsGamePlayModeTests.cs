using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FishNet.Managing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace DataChannelUnity.Example.Tests
{
    /// <summary>
    /// #138's acceptance line: a whole game, from the break to a decided winner, on the real turn
    /// machine with real physics and the real state RPC.
    ///
    /// PlayMode, and it has to be. The rules themselves are covered without a scene by
    /// <see cref="BilliardsRefereeTests"/>; what only exists once time is running is everything this
    /// file is actually about — FishNet's tick, the balls' spawn, first contact arriving from a PhysX
    /// callback, and the state RPC going out over the loopback PeerConnection.
    ///
    /// ## What one process can and cannot show
    ///
    /// Host mode has exactly one connection: the host's own loopback (#120). Seat 1 is therefore
    /// never filled by a real client here, so these tests seat a synthetic second player and drive
    /// the host-side shot path directly. That covers the rules, the phase machine, the clock, the
    /// rematch handshake and the seat-hold logic — but **not** the ServerRpc's seat lookup, nor a
    /// real P2P drop and reconnect. Those need two processes and are recorded on the ticket
    /// separately rather than faked here.
    ///
    /// Headless run, with the Editor not holding the project:
    ///
    ///   Unity -batchmode -runTests -projectPath . -testPlatform PlayMode \
    ///     -testFilter DataChannelUnity.Example.Tests.BilliardsGamePlayModeTests \
    ///     -testResults Logs/playmode-billiards-game.xml -logFile Logs/playmode-game.log
    /// </summary>
    public sealed class BilliardsGamePlayModeTests
    {
        private const string ScenePath =
            "Assets/DataChannelUnity.Example/Scenes/Billiards over DataChannel.unity";

        /// <summary>
        /// Same 60 s as the burst test, for the same reason: StartServer creates a room through the
        /// live signalling server before reporting Started, so this waits on a network round trip plus
        /// ICE and DTLS on the loopback pair — and the first case in a run also pays native library
        /// initialisation.
        /// </summary>
        private const float HostStartTimeout = 60f;

        private const float ShotTimeout = 20f;

        private NetworkManager _manager;
        private BilliardsGame _game;
        private BilliardsRack _rack;

        /// <summary>
        /// Indices into <see cref="BilliardsTable.Pockets"/> for the four corners. The two side
        /// pockets are indices 2 and 3 and are deliberately left out — see the note where this is
        /// used.
        /// </summary>
        private static readonly int[] CornerPockets = { 0, 1, 4, 5 };

        [UnitySetUp]
        public IEnumerator StartHost()
        {
            yield return new EnterPlayMode();

            // Any NetworkManager still around is a previous test's, and it has to go before the
            // scene is loaded rather than after.
            //
            // FishNet's manager defaults to DontDestroyOnLoad with PersistenceType DestroyNewest, so
            // it survives a scene change and the *new* scene's manager is the one destroyed. The
            // survivor brings its transport with it, and therefore a connection-id counter that has
            // already advanced — #120 never reuses ids. Measured before this was added: seat 0 held
            // connection 2, then 4, 6, 8, 10 in successive tests, and every case after the first
            // failed, half of them by not finding the scene's objects at all.
            //
            // Opening the scene through the editor API instead is not available here: batch mode is
            // already in play mode by the time setup runs, and OpenScene throws there.
            foreach (NetworkManager stale in Object.FindObjectsOfType<NetworkManager>())
            {
                if (stale.ServerManager != null)
                    stale.ServerManager.StopConnection(true);
                if (stale.ClientManager != null)
                    stale.ClientManager.StopConnection();
                Object.DestroyImmediate(stale.gameObject);
            }

            AsyncOperation load = SceneManager.LoadSceneAsync(ScenePath, LoadSceneMode.Single);
            while (load != null && !load.isDone)
                yield return null;

            NetworkManager[] managers = Object.FindObjectsOfType<NetworkManager>();
            Assert.AreEqual(1, managers.Length,
                $"Expected exactly one NetworkManager, found {managers.Length}. More than one means " +
                "a previous test's manager persisted, and its transport state comes with it.");

            _manager = managers[0];

            _manager.ServerManager.StartConnection();
            _manager.ClientManager.StartConnection();

            float waited = 0f;
            while (!_manager.IsHostStarted && waited < HostStartTimeout)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            if (!_manager.IsHostStarted)
            {
                // The same two causes the burst test separates, and for the same reason: an
                // unreachable signalling server presents as an un-started host, which reads exactly
                // like a transport defect and sends the next person to debug the wrong layer.
                var transport = _manager.TransportManager.Transport as DataChannelTransport;
                bool signalling = transport != null && transport.SignalingConnected;
                Assert.Fail(signalling
                    ? $"Signalling is up (room={transport.RoomCode}) but the host did not start " +
                      $"within {HostStartTimeout}s — ICE or DTLS on the loopback pair did not finish."
                    : $"Host did not start within {HostStartTimeout}s and signalling never connected. " +
                      "StartServer creates a room first, so check the wss service before the transport.");
            }

            _game = Object.FindObjectOfType<BilliardsGame>();
            Assert.IsNotNull(_game, "No BilliardsGame in the scene; rebuild it.");
            _rack = Object.FindObjectOfType<BilliardsRack>();
            Assert.IsNotNull(_rack, "No BilliardsRack in the scene.");

            var nob = _game.GetComponent<FishNet.Object.NetworkObject>();
            Assert.IsTrue(nob != null && nob.IsSpawned,
                "BilliardsGame's NetworkObject never spawned — a zero SceneId is skipped silently, " +
                "so the turn machine would exist locally and replicate nothing.");

            // The host's loopback fills seat 0 through the ordinary connection path. Asserted rather
            // than assumed: if it did not, every turn below would be attributed to the wrong side.
            Assert.AreEqual(0, _game.SeatConnectionId(BilliardsRules.SeatHost),
                "Seat 0 should hold the host's loopback connection (#120 gives it a real id).");

            SeatSyntheticClient();
            yield return null;
        }

        #region Harness

        /// <summary>
        /// Fills seat 1 without a second process.
        ///
        /// Reflection, deliberately: the alternative is a public "seat a fake player" entry point on
        /// <see cref="BilliardsGame"/>, which would be production surface that exists only for tests
        /// and could be called in a real game. Reaching in from the test keeps that door shut, at the
        /// cost of this method breaking loudly if the field is renamed — which is the right trade for
        /// something only the harness needs.
        /// </summary>
        private void SeatSyntheticClient()
        {
            const int syntheticConnectionId = 4242;

            object seat = SeatObject(BilliardsRules.SeatClient);
            seat.GetType().GetField("ConnectionId").SetValue(seat, syntheticConnectionId);
            seat.GetType().GetField("Token").SetValue(seat, "synthetic-token");

            // The seat array is filled directly, so the game never saw the connection event that
            // normally starts play. Nudge it the same way that event would.
            Invoke("TryBeginGame");
        }

        private object SeatObject(int index)
        {
            FieldInfo seatsField = typeof(BilliardsGame)
                .GetField("_seats", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(seatsField, "BilliardsGame._seats was renamed; this harness needs updating.");

            var seats = (System.Array)seatsField.GetValue(_game);
            return seats.GetValue(index);
        }

        private void Invoke(string method, params object[] args)
        {
            MethodInfo m = typeof(BilliardsGame)
                .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(m, $"BilliardsGame.{method} was renamed; this harness needs updating.");
            m.Invoke(_game, args);
        }

        /// <summary>
        /// Takes a shot as a given seat, through the host-side path the ServerRpc lands in. The RPC
        /// itself is not exercised here — one process has one connection, so seat 1 has no client to
        /// send from.
        /// </summary>
        private void Shoot(int seat, Vector2 direction, float power, Vector2 cueSpot = default) =>
            Invoke("ApplyShot", seat, direction, power, cueSpot);

        /// <summary>
        /// Waits a number of FishNet ticks, not frames.
        ///
        /// Outbound bytes are flushed from the tick loop, and in batch mode a tick spans many frames —
        /// measured here, twelve `yield return null`s advanced LocalTick by one. So a measurement that
        /// waits on frames can close its window before anything has been sent, which is exactly how
        /// the two-publish sample first came back as "meter never saw a send".
        /// </summary>
        private IEnumerator WaitTicks(int ticks)
        {
            uint until = _manager.TimeManager.LocalTick + (uint)ticks;
            float guard = 0f;
            while (_manager.TimeManager.LocalTick < until && guard < 10f)
            {
                guard += Time.deltaTime;
                yield return null;
            }

            Assert.Less(guard, 10f, $"Waited 10s without advancing {ticks} ticks; the tick loop stalled.");
        }

        private IEnumerator WaitForSettle()
        {
            float waited = 0f;
            while (_game.State.Phase == BilliardsPhase.Simulate && waited < ShotTimeout)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Assert.Less(waited, ShotTimeout,
                "A shot never settled. #131's stop criterion has a 15 s backstop, so this means the " +
                "phase machine did not leave Simulate at all.");
        }

        /// <summary>
        /// Sets the table up so one nominated ball can be potted with a single straight shot, and
        /// returns the direction to shoot in.
        ///
        /// The geometry is measured, and the first version of it was wrong in a way worth recording:
        /// with the object ball 9 cm from the mouth it **fell in by itself**. The pocket is a hole in
        /// the surface (#136), and a ball centred inside notchHalf + one diameter (0.065 + 0.057 =
        /// 12.2 cm) is over the opening. So the shot appeared to pot every time while the cue ball was
        /// never even involved — first contact came back as −1, which the referee correctly reads as a
        /// whiff.
        ///
        /// Swept afterwards: placement is stable from 16 cm out (14 cm and closer still drops), and
        /// ball at 16 cm with the cue 12 cm behind it pots at powers 0.6, 0.9 and 1.2, leaving the cue
        /// ball up and settling in 0.57–0.70 s. 0.9 is used here.
        /// </summary>
        private Vector2 SetUpPot(int ballNumber, int pocketIndex, out Vector2 cueSpot)
        {
            Vector3 pocket = BilliardsTable.Pockets[pocketIndex];
            var mouth = new Vector2(pocket.x, pocket.z);

            // Toward the table centre. Normalising the mouth vector works for all six pockets,
            // including the two side ones where x is zero.
            Vector2 inward = (-mouth).normalized;

            Vector2 target = mouth + inward * 0.16f;
            cueSpot = target + inward * 0.12f;

            BilliardsBall ball = _rack.Ball(ballNumber);
            Assert.IsNotNull(ball, $"No ball {ballNumber} in the rack.");
            ball.Restore(new Vector3(target.x, BilliardsTable.BallY, target.y));

            _rack.PlaceCueBall(cueSpot);

            return -inward;
        }

        /// <summary>
        /// Moves every ball except the listed ones out of the way by pocketing them, so a scripted
        /// shot cannot be deflected by a ball that happens to be in the path.
        ///
        /// Pocketed rather than moved aside, and that detail matters: a ball placed off the table has
        /// no surface under it and falls forever, so it is never "still" and the shot runs to the 15 s
        /// backstop instead of settling. <c>Pocket()</c> makes it kinematic, which the stop criterion
        /// skips.
        /// </summary>
        private void ClearTableExcept(params int[] keep)
        {
            // The cue ball and the eight are never cleared. The cue ball for obvious reasons; the
            // eight because pocketing it from the harness would put it in the mask without any shot
            // having been adjudicated — the game would then read as "the eight is down" with no
            // verdict, no winner and no re-rack, which is a state the rules cannot produce.
            var keepSet = new HashSet<int>(keep) { BilliardsRules.CueBall, BilliardsRules.EightBall };

            foreach (BilliardsBall ball in _rack.Balls)
            {
                if (keepSet.Contains(ball.Number))
                    continue;
                if (!ball.IsPocketed)
                    ball.Pocket();
            }

            // Park the eight out of the way unless this shot is about it. Well clear of both lines the
            // scripted shots use: the corner approaches (which live within ~0.3 m of a corner) and the
            // +X line along z = 0 that HandOverTo shoots along.
            bool eightIsTheTarget = false;
            foreach (int n in keep)
            {
                if (n == BilliardsRules.EightBall)
                    eightIsTheTarget = true;
            }

            if (!eightIsTheTarget)
                PlaceOnTable(BilliardsRules.EightBall, new Vector2(0f, 0.45f));
        }

        /// <summary>Puts a ball back on the table at a spot, undoing <see cref="ClearTableExcept"/>.</summary>
        private void PlaceOnTable(int ballNumber, Vector2 at) =>
            _rack.Ball(ballNumber).Restore(new Vector3(at.x, BilliardsTable.BallY, at.y));

        private void SetPrivateFloat(string field, float value)
        {
            FieldInfo f = typeof(BilliardsGame)
                .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(f, $"BilliardsGame.{field} was renamed; this harness needs updating.");
            f.SetValue(_game, value);
        }

        #endregion

        /// <summary>
        /// The map's load-bearing condition: a game that reaches a decided winner.
        ///
        /// The break is the real sixteen-ball rack. After it, each shot clears the balls it does not
        /// need off the table — not to make the rules easier, but to make the *physics* short and
        /// repeatable: a full-rack game would take several minutes of real time and its shot outcomes
        /// would depend on where the break happened to leave things. Every rule below is adjudicated
        /// by the real referee against the real pocketed mask.
        /// </summary>
        [UnityTest]
        public IEnumerator PlaysAWholeGameToADecidedWinner()
        {
            Assert.AreEqual(BilliardsPhase.Break, _game.State.Phase,
                "Two seats are filled, so the machine should be waiting for a break.");
            Assert.AreEqual((byte)BilliardsRules.SeatHost, _game.State.TurnSeat,
                "#132: the host breaks the first game of a room.");

            // --- The break itself, on the full rack ---
            Shoot(BilliardsRules.SeatHost, Vector2.right, BilliardsRules.MaxPower);
            Assert.AreEqual(BilliardsPhase.Simulate, _game.State.Phase);
            yield return WaitForSettle();

            Assert.AreEqual(BilliardsPhase.Aim, _game.State.Phase,
                "After the break the machine should be waiting for the next shot.");
            Assert.Greater(_rack.FirstContact, 0,
                "First contact was never recorded during a sixteen-ball break. That callback is the " +
                "only source of #132's first observable, so every wrong-ball foul would go unnoticed.");

            // --- Seat 0 clears its group, one pot per shot ---
            int shots = 0;
            foreach (int ball in new[] { 1, 2, 3, 4, 5, 6, 7 })
            {
                // Whoever's turn it is, make it seat 0's: potting your own keeps the table, so this
                // only has to be forced if a previous shot handed over.
                if (_game.State.TurnSeat != BilliardsRules.SeatHost)
                    yield return HandOverTo(BilliardsRules.SeatHost);

                ClearTableExcept(ball, BilliardsRules.EightBall);

                // Corners only. The geometry was swept against a corner pocket, and a side pocket is
                // not the same shot: approached straight on from the middle of the table, a ball there
                // was seen to run along the cushion past the mouth instead of dropping. Rotating
                // through the four corners keeps the shots varied without leaving measured ground.
                Vector2 direction = SetUpPot(ball, CornerPockets[ball % CornerPockets.Length],
                    out Vector2 cueSpot);

                Shoot(BilliardsRules.SeatHost, direction, 0.9f, cueSpot);
                yield return WaitForSettle();
                shots++;

                Assert.IsTrue(_game.State.IsPocketed(ball),
                    $"Ball {ball} did not go down; the scripted geometry no longer pots reliably.");
                Assert.AreEqual(ball, _rack.FirstContact,
                    $"Ball {ball} was potted but first contact was recorded as {_rack.FirstContact}. " +
                    "A pot with no contact recorded would be judged a whiff, so this is the assertion " +
                    "that catches contact detection failing on a near-pocket shot specifically.");
                Assert.AreEqual((byte)BilliardsRules.SeatHost, _game.State.TurnSeat,
                    $"Potting own ball {ball} should keep the table (#132).");
            }

            Assert.IsTrue(_game.State.HasClearedGroup(BilliardsRules.SeatHost),
                "Seven pots should clear the solids.");
            Assert.IsTrue(_game.State.EightStillUp,
                "The eight must survive the group, or the game would already be over.");

            // Nothing is asserted about the stripes, and that is a limitation of the harness rather
            // than of the game: ClearTableExcept parks balls by pocketing them, so every ball moved
            // out of a shot's way is recorded in the mask as potted. The shooter's own progression is
            // still real — those seven were struck in and adjudicated one at a time — but "how many
            // has the opponent left" is not meaningful in this test.

            // --- The eight, which ends it ---
            ClearTableExcept(BilliardsRules.EightBall);
            Vector2 eightDirection = SetUpPot(BilliardsRules.EightBall, 4, out Vector2 eightCue);
            Shoot(BilliardsRules.SeatHost, eightDirection, 0.9f, eightCue);
            yield return WaitForSettle();
            shots++;

            Assert.AreEqual(BilliardsPhase.GameOver, _game.State.Phase,
                "The eight went down after a clear group, so the game is decided.");
            Assert.AreEqual((byte)BilliardsRules.SeatHost, _game.State.Winner);
            Assert.IsFalse(_game.State.HasFlag(BilliardsFlags.Abandoned),
                "This game was won, not abandoned.");

            Debug.Log($"[BilliardsGame:test] whole game decided in {shots} scripted shots plus the " +
                      $"break; winner=seat {_game.State.Winner}");
        }

        /// <summary>
        /// Passes the turn with a legal shot that pots nothing: contact the shooter's own ball out in
        /// the open. Used rather than a foul when the point is only to change whose turn it is.
        /// </summary>
        private IEnumerator HandOverTo(int seat)
        {
            int shooter = _game.State.TurnSeat;
            int ownBall = shooter == BilliardsRules.SeatHost ? 1 : 9;

            // If that ball is already down, any of the group will do; if the whole group is down the
            // legal target is the eight.
            if (_game.State.IsPocketed(ownBall))
            {
                ownBall = BilliardsRules.EightBall;
                for (int n = 1; n <= 15; n++)
                {
                    if (n != BilliardsRules.EightBall &&
                        BilliardsRules.OwnerOf(n) == shooter && !_game.State.IsPocketed(n))
                    {
                        ownBall = n;
                        break;
                    }
                }
            }

            ClearTableExcept(ownBall);
            PlaceOnTable(ownBall, new Vector2(0.3f, 0f));
            _rack.PlaceCueBall(new Vector2(0f, 0f));

            // Power 1.0 over 30 cm, and the number is not arbitrary. #137's constant deceleration is
            // mu*g = 0.03 * 9.81 ~ 0.294 m/s^2, so an impulse of p on a 1 kg ball travels
            // p^2 / (2 * 0.294) metres: 0.35 reaches only 21 cm and stops short. That is exactly what
            // happened on the first run of this file — the cue never arrived, which the referee
            // correctly called a whiff, and the "legal miss" this method is for became a foul.
            Shoot(shooter, Vector2.right, 1.0f, new Vector2(0f, 0f));
            yield return WaitForSettle();

            Assert.AreEqual(ownBall, _rack.FirstContact,
                $"The cue was meant to reach ball {ownBall}; a whiff here would be judged a foul and " +
                "this method's purpose is a *legal* miss.");

            Assert.AreEqual((byte)seat, _game.State.TurnSeat,
                "A legal shot that pots nothing should pass the turn (#132).");
            Assert.IsFalse(_game.State.HasFlag(BilliardsFlags.BallInHand),
                "A legal miss is not a foul, so the next player gets no ball in hand.");
        }

        /// <summary>
        /// #132's first foul, end to end: the cue ball goes down, the turn passes, the next player has
        /// ball in hand, and the cue ball is back on the table for them to aim at.
        /// </summary>
        /// <remarks>
        /// The table is emptied first, so this shot is also a whiff — the two fouls coincide, and the
        /// referee reports the scratch because it checks that first. Isolating them here was tried and
        /// abandoned: a scratch after a legal contact needs the cue to be deflected into a pocket, and
        /// swept over 21 geometries (thin clips at three offsets × three powers, and cuts at three
        /// offsets × three powers aimed at a side pocket) only one combination ever pocketed the cue
        /// — a 5.5 cm clip against a 5.7 cm ball, which is a hair away from being a miss and would
        /// flake. The pair *is* isolated, in <see cref="BilliardsRefereeTests"/>, where no physics is
        /// involved; what this test adds is the integration the referee cannot show: that a pocketed
        /// cue ball comes back.
        /// </remarks>
        [UnityTest]
        public IEnumerator ScratchIsAFoulAndHandsOverBallInHand()
        {
            yield return TakeTheBreak();

            // The break pots nothing at power 4.0 (#137 measured this, and the run log agrees: mask
            // 0000 → 0000), so it is a legal miss and the turn has already passed. Shooting as a
            // hard-coded seat 0 was ignored as out of turn on the first run of this file, which left
            // FirstContact still holding the break's value and made the failure look like a detection
            // bug.
            int shooter = _game.State.TurnSeat;

            ClearTableExcept();

            // Straight into a corner pocket. Measured: from 55 cm along the diagonal the cue ball
            // drops at power 1.4 and 2.0, and stops short at 0.9 or less.
            Vector3 pocket = BilliardsTable.Pockets[4];
            var mouth = new Vector2(pocket.x, pocket.z);
            Vector2 inward = (-mouth).normalized;
            Vector2 cueSpot = mouth + inward * 0.55f;
            _rack.PlaceCueBall(cueSpot);

            Shoot(shooter, -inward, 2.0f, cueSpot);
            yield return WaitForSettle();

            Assert.AreEqual((byte)BilliardsRules.OtherSeat(shooter), _game.State.TurnSeat,
                "A foul passes the turn.");
            Assert.IsTrue(_game.State.HasFlag(BilliardsFlags.BallInHand),
                "#132: every foul is punished with ball in hand.");
            Assert.IsFalse(_game.State.IsPocketed(BilliardsRules.CueBall),
                "The cue ball must be back on the table before the next player aims — otherwise the " +
                "next shot is a no-op and the turn hangs.");

            Vector2 cueRest = _game.State.BallPositions[BilliardsRules.CueBall];
            Assert.LessOrEqual(Mathf.Abs(cueRest.x), BilliardsTable.MaxX + 1e-3f,
                "The restored cue ball must be inside the playing area.");
            Assert.LessOrEqual(Mathf.Abs(cueRest.y), BilliardsTable.MaxZ + 1e-3f);
        }

        /// <summary>#132's second foul: first contact outside the shooter's group.</summary>
        [UnityTest]
        public IEnumerator WrongFirstContactIsAFoulAndHandsOverBallInHand()
        {
            yield return TakeTheBreak();

            // The break pots nothing, so the turn has already passed — take the shot as whoever
            // actually has the table, and leave only an *opponent's* ball on it.
            int shooter = _game.State.TurnSeat;
            int theirBall = shooter == BilliardsRules.SeatHost ? 11 : 3;

            ClearTableExcept(theirBall);
            PlaceOnTable(theirBall, new Vector2(0.3f, 0f));
            _rack.PlaceCueBall(new Vector2(0f, 0f));

            Shoot(shooter, Vector2.right, 1.0f, new Vector2(0f, 0f));
            yield return WaitForSettle();

            Assert.AreEqual(theirBall, _rack.FirstContact,
                "The cue had to actually reach the opponent's ball for this to be the wrong-contact " +
                "foul rather than a whiff — both are fouls, but only one is under test here.");
            Assert.AreEqual((byte)BilliardsRules.OtherSeat(shooter), _game.State.TurnSeat);
            Assert.IsTrue(_game.State.HasFlag(BilliardsFlags.BallInHand));
        }

        /// <summary>
        /// #132's break exemption, which the fixed rack makes mandatory: the cue ball always reaches
        /// the 1 ball first, so without it the stripes player fouls on their own break every time.
        /// </summary>
        [UnityTest]
        public IEnumerator BreakByTheStripesPlayerIsNotAFoul()
        {
            // Hand the break to seat 1 the way a rematch would, then let it break the full rack.
            SetPrivateFloat("_turnTimeoutSeconds", 3600f);
            FieldInfo nextBreak = typeof(BilliardsGame)
                .GetField("_nextBreakSeat", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(nextBreak, "BilliardsGame._nextBreakSeat was renamed.");
            nextBreak.SetValue(_game, (byte)BilliardsRules.SeatClient);

            Invoke("VoidGame");
            _game.OfferRematch();
            Invoke("SetRematchReady", BilliardsRules.SeatHost);
            Invoke("SetRematchReady", BilliardsRules.SeatClient);

            Assert.AreEqual(BilliardsPhase.Break, _game.State.Phase);
            Assert.AreEqual((byte)BilliardsRules.SeatClient, _game.State.TurnSeat,
                "#132: the loser breaks, and here that was set to the stripes player.");

            Shoot(BilliardsRules.SeatClient, Vector2.right, BilliardsRules.MaxPower);
            yield return WaitForSettle();

            Assert.AreEqual(1, _rack.FirstContact,
                "The fixed rack means the cue ball always reaches the 1 ball first — which is a solid.");
            Assert.IsFalse(_game.State.HasFlag(BilliardsFlags.BallInHand),
                "The break is exempt from the first-contact rule, so a stripes break is not a foul. " +
                "Without the exemption this is the shot that would make the game unplayable.");
        }

        /// <summary>The eight on the break: re-rack, same player breaks again (#132).</summary>
        [UnityTest]
        public IEnumerator EightOnTheBreakReRacks()
        {
            // Engineering the eight down on a real break is not repeatable, so the eight is placed at
            // a pocket and the shot is taken *in the Break phase* — which is what the rule keys on.
            ClearTableExcept(BilliardsRules.EightBall);
            Vector2 direction = SetUpPot(BilliardsRules.EightBall, 4, out Vector2 cueSpot);

            Assert.AreEqual(BilliardsPhase.Break, _game.State.Phase, "Setup should still owe a break.");
            Shoot(BilliardsRules.SeatHost, direction, 0.9f, cueSpot);
            yield return WaitForSettle();

            Assert.AreEqual(BilliardsPhase.Break, _game.State.Phase,
                "The eight on the break re-racks and owes another break.");
            Assert.AreEqual((byte)BilliardsRules.SeatHost, _game.State.TurnSeat,
                "#132: the same player breaks again.");
            Assert.IsFalse(_game.State.IsPocketed(BilliardsRules.EightBall),
                "A re-rack puts the eight back up.");
            Assert.AreEqual(0, _game.State.Pocketed,
                "A re-rack restores every ball, so nothing is pocketed.");
        }

        /// <summary>
        /// The 60 s turn clock (#132), run at a shortened setting. Shortened rather than waited out
        /// because the behaviour under test is "the turn passes and it is not a foul", and that is
        /// independent of the number — while a 60 s wait in a test is a minute of nothing per run.
        /// </summary>
        [UnityTest]
        public IEnumerator TurnTimeoutPassesTheTurnWithoutAFoul()
        {
            yield return TakeTheBreak();

            int before = _game.State.TurnSeat;
            SetPrivateFloat("_turnTimeoutSeconds", 0.4f);

            float waited = 0f;
            while (_game.State.TurnSeat == before && waited < 5f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Assert.AreNotEqual(before, _game.State.TurnSeat, "The turn clock never fired.");
            Assert.IsFalse(_game.State.HasFlag(BilliardsFlags.BallInHand),
                "#132: a timeout only changes whose turn it is — it is not a foul, so no ball in hand.");
            Assert.AreEqual(BilliardsPhase.Aim, _game.State.Phase);
        }

        /// <summary>Rematch takes both consents, and the loser breaks (#132 §4).</summary>
        [UnityTest]
        public IEnumerator RematchNeedsBothReadyAndTheLoserBreaks()
        {
            SetPrivateFloat("_turnTimeoutSeconds", 3600f);

            // Decide a game the short way: the eight down with the group still up loses.
            ClearTableExcept(BilliardsRules.EightBall, 1);
            yield return TakeTheBreak();

            ClearTableExcept(BilliardsRules.EightBall);
            Vector2 direction = SetUpPot(BilliardsRules.EightBall, 4, out Vector2 cueSpot);
            Shoot(_game.State.TurnSeat, direction, 0.9f, cueSpot);
            yield return WaitForSettle();

            Assert.AreEqual(BilliardsPhase.GameOver, _game.State.Phase);
            byte winner = _game.State.Winner;
            byte loser = (byte)BilliardsRules.OtherSeat(winner);

            Invoke("SetRematchReady", (int)winner);
            Assert.AreEqual(BilliardsPhase.RematchPending, _game.State.Phase,
                "One consent is not enough; the other player must see it pending.");
            Assert.IsTrue(_game.State.Phase == BilliardsPhase.RematchPending);

            Invoke("SetRematchReady", (int)loser);
            Assert.AreEqual(BilliardsPhase.Break, _game.State.Phase, "Both ready re-racks.");
            Assert.AreEqual(loser, _game.State.TurnSeat, "#132: the loser breaks the next game.");
            Assert.AreEqual(0, _game.State.Flags,
                "The ready bits belong to the game that ended; a stale one would start the next " +
                "rematch half-agreed.");
            yield return null;
        }

        private IEnumerator TakeTheBreak()
        {
            if (_game.State.Phase != BilliardsPhase.Break)
                yield break;

            Shoot(_game.State.TurnSeat, Vector2.right, BilliardsRules.MaxPower);
            yield return WaitForSettle();
        }

        #region Seats, tokens and the reconnect hold (#134)

        /// <summary>
        /// A dropped client's seat is held rather than freed, the opponent is told, and the balls stop
        /// accepting shots. The hold window is shortened; what is under test is the behaviour, not the
        /// number.
        /// </summary>
        [UnityTest]
        public IEnumerator DroppedClientSeatIsHeldAndBlocksShots()
        {
            yield return TakeTheBreak();
            SetPrivateFloat("_seatHoldSeconds", 1.5f);

            const int syntheticConnectionId = 4242;
            Invoke("ReleaseConnection", syntheticConnectionId);

            Assert.IsTrue(_game.IsSeatHeld(BilliardsRules.SeatClient),
                "The seat should be held, not freed — #120's ids are never reused, so freeing it " +
                "would let anybody take it.");
            Assert.AreEqual(1, _game.HeldSeatCount);
            Assert.IsTrue(_game.State.HasFlag(BilliardsFlags.AwaitingReconnect),
                "The opponent has to be told, or thirty seconds of silence reads as a hang (#134).");

            // No shot is accepted while the table is being kept for somebody.
            BilliardsPhase before = _game.State.Phase;
            Shoot(BilliardsRules.SeatHost, Vector2.right, 0.5f);
            Assert.AreEqual(before, _game.State.Phase,
                "A shot was accepted during a reconnect hold; the table belongs to the player " +
                "coming back.");
            yield return null;
        }

        /// <summary>
        /// The reconnect: a new connection presenting the seat's token gets that seat back, with the
        /// game as it left it. This is #134's main path.
        /// </summary>
        [UnityTest]
        public IEnumerator TokenReclaimsTheHeldSeatWithTheFrameIntact()
        {
            yield return TakeTheBreak();
            SetPrivateFloat("_seatHoldSeconds", 30f);

            ushort pocketedBefore = _game.State.Pocketed;
            byte turnBefore = _game.State.TurnSeat;

            const int oldConnectionId = 4242;
            const int newConnectionId = 5150;
            object seat = SeatObject(BilliardsRules.SeatClient);
            var token = (string)seat.GetType().GetField("Token").GetValue(seat);

            Invoke("ReleaseConnection", oldConnectionId);
            Assert.IsTrue(_game.IsSeatHeld(BilliardsRules.SeatClient));

            // Exactly what the transport does on an incoming offer that carries a token.
            Assert.IsTrue(_game.TokenReclaimsSeat(token),
                "The held seat should recognise its own token; without this the transport would " +
                "reject the returning player as room-full.");
            Assert.IsFalse(_game.TokenReclaimsSeat("not-the-token"),
                "A stranger's token must not open a held seat.");

            _game.RemoteTokenPresented(newConnectionId, token);
            Invoke("SeatConnection", newConnectionId);

            Assert.IsFalse(_game.IsSeatHeld(BilliardsRules.SeatClient), "The hold should be over.");
            Assert.AreEqual(newConnectionId, _game.SeatConnectionId(BilliardsRules.SeatClient),
                "#120 never reuses ids, so the seat must now point at the *new* connection.");
            Assert.IsFalse(_game.State.HasFlag(BilliardsFlags.AwaitingReconnect),
                "Nobody is being waited for any more.");

            Assert.AreEqual(pocketedBefore, _game.State.Pocketed,
                "The frame must survive the reconnect — that is the whole point of holding the seat.");
            Assert.AreEqual(turnBefore, _game.State.TurnSeat, "And so must whose turn it is.");
            yield return null;
        }

        /// <summary>
        /// Past the window, the game is void — stated with a flag and no winner rather than left for
        /// the client to infer (#134).
        /// </summary>
        [UnityTest]
        public IEnumerator HoldExpiringVoidsTheGame()
        {
            yield return TakeTheBreak();
            SetPrivateFloat("_seatHoldSeconds", 0.5f);

            Invoke("ReleaseConnection", 4242);

            float waited = 0f;
            while (_game.IsSeatHeld(BilliardsRules.SeatClient) && waited < 5f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Assert.IsFalse(_game.IsSeatHeld(BilliardsRules.SeatClient), "The hold never expired.");
            Assert.AreEqual(BilliardsPhase.GameOver, _game.State.Phase);
            Assert.AreEqual(BilliardsRules.SeatNone, _game.State.Winner,
                "Nobody won an abandoned game.");
            Assert.IsTrue(_game.State.HasFlag(BilliardsFlags.Abandoned),
                "#134: 'abandoned' and 'lost' want different words on screen, so it is a flag rather " +
                "than an inference from the winner.");
        }

        /// <summary>
        /// A third player arriving inside the hold window is refused with <c>seat-held</c>, not
        /// <c>room-full</c> — different information for the player, and #134's reason for a new code.
        ///
        /// The transport's arithmetic is what is checked: live peers plus held seats against the
        /// maximum. #134's fact 4 is that the live count alone drops when a client leaves, so the
        /// admission check would let a stranger into a seat that is being kept.
        /// </summary>
        [UnityTest]
        public IEnumerator HeldSeatIsCountedTowardTheRoomLimit()
        {
            yield return TakeTheBreak();
            SetPrivateFloat("_seatHoldSeconds", 30f);

            var transport = _manager.TransportManager.Transport as DataChannelTransport;
            Assert.IsNotNull(transport, "This scene must run on DataChannelTransport.");
            Assert.AreSame(_game, transport.SeatAuthority,
                "The game must have registered itself as the seat authority, or the transport has " +
                "nothing to ask and a held seat is invisible to admission.");

            Invoke("ReleaseConnection", 4242);

            Assert.AreEqual(1, transport.SeatAuthority.HeldSeatCount,
                "The transport reads the held count from the game layer; this is the number it adds " +
                "to its live peers before deciding whether the room is full.");

            object seat = SeatObject(BilliardsRules.SeatClient);
            var token = (string)seat.GetType().GetField("Token").GetValue(seat);
            Assert.IsTrue(transport.SeatAuthority.TokenReclaimsSeat(token),
                "The returning player must still be admitted past a full room.");
            Assert.IsFalse(transport.SeatAuthority.TokenReclaimsSeat(null),
                "A player with no token must not be.");
            yield return null;
        }

        #endregion

        #region The state message (#135)

        /// <summary>
        /// #135 costed the state message at about 140 bytes per turn. Measured here rather than
        /// trusted, the same way #136 measured the ball burst against #131's estimate — and for the
        /// same reason: an estimate that is never checked is indistinguishable from a wrong one.
        ///
        /// Taken with the table still, so nothing else is on the reliable channel: NetworkTransform
        /// sends nothing when no ball has moved, which is what makes the figure attributable.
        /// </summary>
        [UnityTest]
        public IEnumerator StateMessageCostsAboutWhatItWasCostedAt()
        {
            yield return TakeTheBreak();

            var meter = Object.FindObjectOfType<OutboundByteMeter>();
            Assert.IsNotNull(meter, "No OutboundByteMeter in the scene; nothing would be measured.");

            // Let the table go completely quiet first, so no transform deltas land in the sample.
            for (int i = 0; i < 60; i++)
                yield return null;

            meter.Reset();
            meter.MeasureFromTick = _manager.TimeManager.LocalTick;

            // One publish, with nothing else happening.
            Invoke("PublishState", _game.State.Phase, _game.State.TurnSeat,
                (BilliardsFlags)_game.State.Flags, _game.State.Winner);

            for (int i = 0; i < 10; i++)
                yield return null;

            string report = meter.Report();
            int peakReliable = PeakField(report, "peakReliable=");
            Debug.Log($"[BilliardsGame:test] one state publish, reliable peak = {peakReliable}B\n{report}");

            // Then two publishes inside one window, to separate the per-message cost from whatever
            // fixed cost a tick carries. Without this the single figure cannot be compared against
            // #135's layout at all: a message costed at 135 bytes of payload and measured at nearly
            // twice that could be either a big header or the body going out twice.
            meter.Reset();
            meter.MeasureFromTick = _manager.TimeManager.LocalTick;
            for (int i = 0; i < 2; i++)
            {
                Invoke("PublishState", _game.State.Phase, _game.State.TurnSeat,
                    (BilliardsFlags)_game.State.Flags, _game.State.Winner);
                yield return WaitTicks(2);
            }

            yield return WaitTicks(2);

            string twoReport = meter.Report();
            int twoPeak = PeakField(twoReport, "peakReliable=");
            Debug.Log($"[BilliardsGame:test] two state publishes, reliable peak = {twoPeak}B, " +
                      $"marginal = {twoPeak - peakReliable}B\n{twoReport}");

            Assert.Greater(peakReliable, 0,
                "No reliable bytes were sent for a state publish. Either the RPC did not go out or " +
                "the meter is not attached — both would make the byte claim unverifiable.");

            // A band, not a number: the exact size depends on FishNet's header and the array's packed
            // count, neither of which #135 pinned down. What matters is that it is the order of
            // magnitude costed — a hundred-odd bytes once a turn, nowhere near the 1282 MTU.
            Assert.LessOrEqual(peakReliable, 400,
                $"The state message came to {peakReliable}B against #135's ~140B estimate. That is " +
                "far enough out that the layout, not the estimate, should be looked at.");
        }

        private static int PeakField(string report, string prefix)
        {
            foreach (string line in report.Split('\n'))
            {
                int at = line.IndexOf(prefix, System.StringComparison.Ordinal);
                if (at < 0)
                    continue;

                string value = line.Substring(at + prefix.Length);
                int end = value.IndexOf('B');
                if (end > 0 && int.TryParse(value.Substring(0, end), out int bytes))
                    return bytes;
            }

            Assert.Fail($"Report has no '{prefix}' figure; the meter's format changed.");
            return 0;
        }

        #endregion

        [UnityTearDown]
        public IEnumerator StopHost()
        {
            // Tolerated only here, and only because an orderly shutdown genuinely logs an Error:
            // closing the host tears down DTLS and libdatachannel reports "DataChannel is closed"
            // at Error severity (DataChannelLog.cs:79). Whether an ordinary close should be an Error
            // at all is a package-level question, not this ticket's.
            LogAssert.ignoreFailingMessages = true;

            if (_manager != null)
            {
                if (_manager.ClientManager != null)
                    _manager.ClientManager.StopConnection();
                if (_manager.ServerManager != null)
                    _manager.ServerManager.StopConnection(true);
                yield return null;
            }

            yield return new ExitPlayMode();
        }
    }
}
