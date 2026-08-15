using System;
using System.Collections.Generic;
using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// Owns the 16 balls: racks them, strikes the cue ball, decides when the table has settled,
    /// and enforces containment and pocketing. Host-authoritative and deliberately network-free —
    /// #136 stops at "can break, balls stop, bytes measurable", so replication lives elsewhere.
    /// No rules here either: no groups, no fouls, no win condition (#132 owns those).
    /// </summary>
    public sealed class BilliardsRack : MonoBehaviour
    {
        /// <summary>Linear speed under which a ball counts as still, m/s (#131 §3).</summary>
        public const float StopLinearSpeed = 0.02f;

        /// <summary>Angular speed under which a ball counts as still, rad/s (#131 §3).</summary>
        public const float StopAngularSpeed = 1.0f;

        /// <summary>
        /// How long every ball must stay under both thresholds before the table is settled.
        /// Expressed in seconds, not ticks: physics runs on the FishNet tick
        /// (PhysicsMode.TimeManager), so a tick count would silently change meaning with TickRate.
        /// </summary>
        public const float StopHoldSeconds = 0.2f;

        /// <summary>
        /// Backstop against a shot that never settles — contact jitter inside a cluster can keep
        /// bodies fractionally awake. Without this the turn hangs forever, which is the same
        /// failure class as a ball on the floor.
        /// </summary>
        public const float MaxShotSeconds = 15f;

        /// <summary>
        /// Rolling-resistance coefficient, applied as a constant deceleration of mu*g by
        /// <see cref="BilliardsBall.ApplyRollingResistance"/> (#137).
        ///
        /// Tuned on the real table rather than taken from a reference: at 0.03 a full-power break
        /// settles in 4.63 s, pockets 0 balls and never trips containment, which is inside what a
        /// real nine-foot table does (3–5 s, 0–2 balls). Neighbouring values measured 6.07 s at 0.02
        /// and 3.43 s at 0.05. It is deliberately larger than a textbook figure for cloth (~0.01)
        /// because it carries every dissipation the model omits — see that method's remarks.
        /// </summary>
        public const float RollingResistance = 0.03f;

        /// <summary>
        /// Restitution below this impact speed is treated as zero by PhysX, and Unity's default of 2
        /// m/s is far above billiards speeds: measured, a cushion returned e=0.000 at 1.9 m/s and
        /// e=0.950 at 2.1 m/s — a cliff, not a curve, so any ball slower than 2 m/s died on the
        /// cushion instead of rebounding. Since "let the ball come to rest against a cushion" is an
        /// ordinary shot, that made a whole class of position play impossible (#137).
        ///
        /// Set here at runtime rather than in ProjectSettings because it is a *global*: the setting is
        /// shared with every other scene, and this repo deliberately leaves such values alone
        /// (compare sleepThreshold in #131). Nothing else needs it — the billiards scene holds the
        /// only rigidbodies in the project — so the narrowest change is to own it for as long as this
        /// component is enabled and put it back afterwards.
        /// </summary>
        private const float BounceThreshold = 0.1f;

        private float _restoreBounceThreshold;

        private readonly List<BilliardsBall> _balls = new();
        private float _stillFor;
        private float _shotElapsed;
        private bool _shotInFlight;

        /// <summary>Raised on the host when the table settles. Argument is the shot's duration.</summary>
        public event Action<float> ShotSettled;

        /// <summary>Raised when a ball drops, after it has been parked.</summary>
        public event Action<BilliardsBall> BallPocketed;

        /// <summary>Raised when containment had to intervene — see BilliardsBall.ClampIntoPlay.</summary>
        public event Action<BilliardsBall> BallClamped;

        public IReadOnlyList<BilliardsBall> Balls => _balls;

        public bool ShotInFlight => _shotInFlight;

        public BilliardsBall CueBall { get; private set; }

        private FishNet.Managing.Timing.TimeManager _timeManager;
        private bool _drivingPhysics;

        private void Awake()
        {
            // Balls live in the scene, so collect them rather than wiring 16 references by hand.
            foreach (BilliardsBall ball in FindObjectsOfType<BilliardsBall>(true))
                Register(ball);

            if (_balls.Count != 16)
                Debug.LogWarning($"[Billiards] Registered {_balls.Count} balls; expected 16.");
        }

        /// <summary>
        /// Somebody has to own the physics clock, and which callback drives Step depends on who.
        ///
        /// With a NetworkManager on PhysicsMode.TimeManager, FishNet calls Physics.Simulate inside
        /// its tick and sets Physics.simulationMode = Script. FixedUpdate still fires in that mode
        /// but physics has not advanced in it, so asking "are the balls still?" there reads stale
        /// velocities — hence the post-simulation hook.
        ///
        /// Without a NetworkManager we cannot assume Unity is stepping physics either:
        /// simulationMode is a *global* that FishNet leaves on Script, and it survives a scene
        /// change. A scene with no NetworkManager therefore inherits a frozen world. Rather than
        /// depend on which scene ran last, take the clock explicitly.
        /// </summary>
        private void OnEnable()
        {
            _restoreBounceThreshold = Physics.bounceThreshold;
            Physics.bounceThreshold = BounceThreshold;

            // Deliberately not InstanceFinder: its getter logs "NetworkManager not found" every
            // call, which reads like a fault in a scene that is meant not to have one.
            var manager = FindObjectOfType<FishNet.Managing.NetworkManager>();
            _timeManager = manager == null ? null : manager.TimeManager;

            if (_timeManager != null)
            {
                _timeManager.OnPostPhysicsSimulation += OnPostPhysicsSimulation;
                return;
            }

            _drivingPhysics = Physics.simulationMode == SimulationMode.Script;
            if (_drivingPhysics)
                Debug.Log("[Billiards] No NetworkManager; this component is stepping physics " +
                          "itself because simulationMode is Script.");
        }

        private void OnDisable()
        {
            Physics.bounceThreshold = _restoreBounceThreshold;

            if (_timeManager != null)
                _timeManager.OnPostPhysicsSimulation -= OnPostPhysicsSimulation;
        }

        private void OnPostPhysicsSimulation(float delta) => Step(delta);

        private void FixedUpdate()
        {
            if (_timeManager != null)
                return;

            if (_drivingPhysics)
                Physics.Simulate(Time.fixedDeltaTime);

            Step(Time.fixedDeltaTime);
        }

        public void Register(BilliardsBall ball)
        {
            if (_balls.Contains(ball))
                return;

            _balls.Add(ball);
            if (ball.IsCueBall)
                CueBall = ball;
        }

        /// <summary>Fixed rack plus fixed cue position. Deterministic by construction (#132).</summary>
        public void ResetRack()
        {
            foreach (BilliardsBall ball in _balls)
            {
                Vector3 home = ball.IsCueBall
                    ? BilliardsTable.HeadSpot
                    : BilliardsTable.RackPosition(ball.Number);
                ball.Restore(home);
            }

            _shotInFlight = false;
            _stillFor = 0f;
            _shotElapsed = 0f;
        }

        /// <summary>
        /// Strikes the cue ball. Direction is flattened onto the table plane and normalised, so
        /// callers cannot smuggle vertical impulse in and beat the containment guarantee.
        /// </summary>
        public void Break(Vector3 direction, float power)
        {
            if (CueBall == null)
            {
                Debug.LogError("[Billiards] No cue ball registered; cannot break.");
                return;
            }

            Vector3 flat = new Vector3(direction.x, 0f, direction.z);
            if (flat.sqrMagnitude < 1e-6f)
            {
                Debug.LogWarning("[Billiards] Break direction is degenerate; ignoring.");
                return;
            }

            CueBall.Strike(flat.normalized * Mathf.Max(0f, power));
            _shotInFlight = true;
            _stillFor = 0f;
            _shotElapsed = 0f;
        }

        /// <summary>
        /// Runs on the host's physics step. Order matters: contain first (so a clamped ball is
        /// judged at its corrected position), then pocket, then test for settling.
        /// </summary>
        public void Step(float deltaTime)
        {
            // Rolling resistance is ours to apply because PhysX has no such term (#137). Forces
            // accumulate for the next Physics.Simulate, so applying it here — after the step rather
            // than before — costs one step of lag and keeps the physics-clock ownership above as the
            // single place that decides when a step happens.
            float deceleration = RollingResistance * Mathf.Abs(Physics.gravity.y);
            for (int i = 0; i < _balls.Count; i++)
                _balls[i].ApplyRollingResistance(deceleration, deltaTime);

            for (int i = 0; i < _balls.Count; i++)
            {
                if (_balls[i].ClampIntoPlay())
                    BallClamped?.Invoke(_balls[i]);
            }

            CapturePocketedBalls();

            if (!_shotInFlight)
                return;

            _shotElapsed += deltaTime;

            if (AllBallsStill())
                _stillFor += deltaTime;
            else
                _stillFor = 0f;

            bool settled = _stillFor >= StopHoldSeconds;
            bool timedOut = _shotElapsed >= MaxShotSeconds;

            if (!settled && !timedOut)
                return;

            if (timedOut && !settled)
            {
                Debug.LogWarning($"[Billiards] Shot hit the {MaxShotSeconds}s backstop without " +
                                 "settling; forcing every ball to rest.");
                foreach (BilliardsBall ball in _balls)
                    ball.Stop();
            }

            _shotInFlight = false;
            ShotSettled?.Invoke(_shotElapsed);
        }

        private void CapturePocketedBalls()
        {
            for (int i = 0; i < _balls.Count; i++)
            {
                BilliardsBall ball = _balls[i];
                if (ball.IsPocketed)
                    continue;

                if (!IsInPocket(ball.Body.position))
                    continue;

                ball.Pocket();
                BallPocketed?.Invoke(ball);
            }
        }

        /// <summary>
        /// Pockets are real holes in the playing surface, so this is the whole capture test: a ball
        /// below the surface fell through one. Whether it drops or rattles back out off the jaw is
        /// the solver's business, not a rule's.
        ///
        /// Worth contrasting with what this replaced. The first design froze Y and made pockets gaps
        /// in the cushion, which needed mouth intervals, segmented rails, and an exemption inside
        /// the containment clamp — three pieces of per-tick machinery to reproduce something gravity
        /// does for free. The mistake underneath was treating "cannot fly off the table" and "cannot
        /// fall through it" as one constraint when they are different directions.
        /// </summary>
        private static bool IsInPocket(Vector3 position) => position.y < BilliardsTable.FallThroughY;

        private bool AllBallsStill()
        {
            for (int i = 0; i < _balls.Count; i++)
            {
                BilliardsBall ball = _balls[i];
                if (ball.IsPocketed)
                    continue;

                if (ball.Velocity.sqrMagnitude > StopLinearSpeed * StopLinearSpeed)
                    return false;
                if (ball.AngularVelocity.sqrMagnitude > StopAngularSpeed * StopAngularSpeed)
                    return false;
            }

            return true;
        }
    }
}
