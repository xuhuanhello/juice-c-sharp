using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// One ball. Number 0 is the cue ball; 1..15 are the object balls.
    ///
    /// Leaving the table is structurally impossible rather than a rule (#132): the rails are thick,
    /// collision detection is continuous, and anything that still escapes sideways is snapped back
    /// silently — not a foul, not a pocket. The reason is not tidiness: a ball on the floor can never
    /// be pocketed, clearing your group is a precondition for winning, so one lost ball means the
    /// game can no longer be decided at all. That is exactly the load-bearing condition of the map.
    ///
    /// Gravity is on and no axis is frozen. Freezing Y was the earlier design, and it forced pockets
    /// to be gaps in the cushion, which needed mouth intervals, segmented rails and an exemption
    /// inside the containment clamp — three pieces of per-tick machinery to imitate falling. The
    /// mistake underneath was reading "cannot fly off the table" and "cannot fall through it" as one
    /// constraint when they are different directions: only the first is ours to enforce.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class BilliardsBall : MonoBehaviour
    {
        [SerializeField] private int _number;

        private Rigidbody _body;

        /// <summary>0 for the cue ball, 1..15 for object balls.</summary>
        public int Number => _number;

        public bool IsCueBall => _number == 0;

        /// <summary>Set by the host when this ball drops. Clients see it park; nothing else.</summary>
        public bool IsPocketed { get; private set; }

        /// <summary>
        /// Resolved on demand rather than only in Awake: whoever collects the balls may run its
        /// own Awake first, and Unity gives no ordering guarantee between the two.
        /// </summary>
        public Rigidbody Body
        {
            get
            {
                if (_body == null)
                {
                    _body = GetComponent<Rigidbody>();
                    ConfigureBody();
                }

                return _body;
            }
        }

        public Vector3 Velocity => Body.velocity;

        public Vector3 AngularVelocity => Body.angularVelocity;

        private void Awake()
        {
            _ = Body;
        }

        internal void SetNumber(int number) => _number = number;

        /// <summary>
        /// Gravity on, nothing frozen. Balls rest on a real surface and fall through the pocket
        /// notches, so whether a ball drops or rattles on the jaw is settled by the solver rather
        /// than by a rule of ours. Containment is only about not leaving the table sideways, which
        /// is a separate direction from falling — conflating the two is what produced the earlier
        /// design where pockets had to be gaps in the cushion.
        ///
        /// Continuous dynamic collision detection is not optional here. Physics runs on the FishNet
        /// tick (PhysicsMode.TimeManager), so a step is 33 ms at TickRate 30 — a ball at 3 m/s
        /// covers 10 cm per step, nearly two ball diameters. Discrete detection tunnels, and with a
        /// real floor it would tunnel *through the slate* as well as through rails.
        /// </summary>
        private void ConfigureBody()
        {
            _body.useGravity = true;
            _body.constraints = RigidbodyConstraints.None;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.interpolation = RigidbodyInterpolation.Interpolate;

            // PhysX does not model rolling resistance: a sphere on a plane rolls forever no matter
            // how much surface friction there is. angularDrag is the only lever, so it is doing the
            // job a rolling-resistance term should — an approximation, and the one most likely to
            // need retuning if the break feels wrong.
            _body.drag = 0.12f;
            _body.angularDrag = 1.4f;

            // #131: the project-wide sleepThreshold (0.005) corresponds to ~0.1 m/s, which is
            // 1.75 ball diameters per second — visibly still rolling. Lower it per body so PhysX
            // agrees with our own stop criterion instead of fighting it. The global setting in
            // ProjectSettings is shared with the rest of the repo and is deliberately untouched.
            _body.sleepThreshold = 0.0001f;
        }

        /// <summary>
        /// Places the ball at rest. Used for the rack, the head spot, and parking.
        ///
        /// The height is forced to the surface rather than taken from the caller: callers work in
        /// table coordinates where Y carries no meaning, and now that Y is a real degree of freedom
        /// a caller's stray zero would drop the ball through the slate.
        /// </summary>
        public void PlaceAt(Vector3 position)
        {
            var onSurface = new Vector3(position.x, BilliardsTable.BallY, position.z);
            Body.position = onSurface;
            transform.position = onSurface;
            Stop();
        }

        public void Stop()
        {
            Body.velocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
        }

        public void Strike(Vector3 impulse)
        {
            Body.WakeUp();
            Body.AddForce(impulse, ForceMode.Impulse);
        }

        /// <summary>
        /// Host-side: park this ball off the table. The ball stays spawned, so once it settles its
        /// NetworkTransform reports no change and costs nothing per tick (#131 §5).
        /// </summary>
        /// <remarks>
        /// The body is made kinematic, and that is what actually delivers the "costs nothing" half.
        /// The parking row sits beside the table with no surface under it, which was free when Y was
        /// frozen and is not now that gravity is on: a parked ball fell indefinitely — measured at
        /// −98 m and still accelerating through 36 m/s — because containment deliberately ignores
        /// pocketed balls, so nothing caught it.
        ///
        /// The visible cost was bandwidth, not appearance. A falling ball changes Y by far more than
        /// the 1 mm sensitivity threshold every tick, so each parked ball kept sending a Y delta
        /// (about 7 bytes/tick) for the rest of the match — the exact opposite of what #131 chose
        /// parking for, and invisible on screen because it happens off-camera.
        ///
        /// Kinematic rather than gravity-off: with no colliders and no gravity a ball is still
        /// simulated and can still be moved by a stray impulse, whereas a kinematic body stays where
        /// it is put. A floor under the parking row would also work but adds geometry that exists
        /// only to catch something that should not be moving at all.
        /// </remarks>
        public void Pocket()
        {
            if (IsPocketed)
                return;

            IsPocketed = true;
            foreach (Collider c in GetComponents<Collider>())
                c.enabled = false;

            PlaceAt(BilliardsTable.ParkingSlot(_number));
            Body.isKinematic = true;
        }

        /// <summary>Host-side: return this ball to play. Used when the rack is reset.</summary>
        public void Restore(Vector3 position)
        {
            IsPocketed = false;
            foreach (Collider c in GetComponents<Collider>())
                c.enabled = true;

            // Before PlaceAt: a kinematic body ignores the velocity reset that PlaceAt performs, so
            // restoring in the other order would return the ball to play still carrying whatever it
            // was doing when it dropped.
            Body.isKinematic = false;
            PlaceAt(position);
        }

        /// <summary>
        /// Last line of the containment guarantee, checked by the host each tick. Returns true when
        /// it had to intervene, which is worth logging: it means the rails were beaten and the
        /// physics configuration — not the rules — needs looking at.
        ///
        /// Horizontal only. Falling is not containment's business: a ball dropping below the surface
        /// is going into a pocket, which is the one exit the table is supposed to have. Only upward
        /// travel is corrected, because a ball gaining height on a flat surface means something has
        /// gone wrong and losing it would deadlock the game — clearing your group is a precondition
        /// for winning, so one lost ball means the game can never be decided.
        /// </summary>
        public bool ClampIntoPlay()
        {
            if (IsPocketed)
                return false;

            Vector3 p = Body.position;

            // A ball on its way into a pocket is legitimately outside the playing rectangle.
            // Clamping it would push it back out of the hole it just fell into.
            if (p.y < -BilliardsTable.BallRadius)
                return false;

            float x = Mathf.Clamp(p.x, -BilliardsTable.MaxX, BilliardsTable.MaxX);
            float z = Mathf.Clamp(p.z, -BilliardsTable.MaxZ, BilliardsTable.MaxZ);
            bool movedHorizontally = !Mathf.Approximately(x, p.x) || !Mathf.Approximately(z, p.z);

            bool tooHigh = p.y > BilliardsTable.MaxY;
            if (!movedHorizontally && !tooHigh)
                return false;

            Body.position = new Vector3(x, tooHigh ? BilliardsTable.BallY : p.y, z);
            Vector3 v = Body.velocity;
            if (tooHigh)
                v.y = 0f;
            if (movedHorizontally)
                v *= 0.25f;
            Body.velocity = v;

            return true;
        }
    }
}
