using System.Text;
using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// Verification scaffolding for #136, not gameplay: fires one scripted break and records what
    /// happened so the numbers can be read back afterwards. Scripted rather than hand-aimed
    /// because #130 needs a repeatable peak — a "dead centre, full power" break is the same shot
    /// every run, which is what makes its burst comparable across TickRate settings.
    /// </summary>
    [RequireComponent(typeof(BilliardsRack))]
    public sealed class BilliardsBreakProbe : MonoBehaviour
    {
        [SerializeField] private bool _breakOnStart = true;
        [SerializeField] private float _power = 4.0f;
        [SerializeField] private float _delaySeconds = 0.5f;

        private BilliardsRack _rack;
        private float _elapsed;
        private bool _fired;

        public bool Fired => _fired;
        public bool Settled { get; private set; }
        public float SettleSeconds { get; private set; }
        public int ClampEvents { get; private set; }
        public int PocketedCount { get; private set; }
        public float PeakSpeed { get; private set; }
        public int PeakMovingBalls { get; private set; }

        private void Awake()
        {
            _rack = GetComponent<BilliardsRack>();
            _rack.ShotSettled += OnSettled;
            _rack.BallPocketed += _ => PocketedCount++;
            _rack.BallClamped += OnClamped;
        }

        private void OnDestroy()
        {
            if (_rack == null)
                return;

            _rack.ShotSettled -= OnSettled;
            _rack.BallClamped -= OnClamped;
        }

        private void OnSettled(float seconds)
        {
            Settled = true;
            SettleSeconds = seconds;
            Debug.Log($"[BreakProbe] settled in {seconds:F2}s | pocketed={PocketedCount} " +
                      $"clamps={ClampEvents}(越界 {ClampEvents - PocketSideClampEvents}) " +
                      $"peakSpeed={PeakSpeed:F2}m/s " +
                      $"peakMoving={PeakMovingBalls}");
            WriteReportFile();
        }

        /// <summary>
        /// Also dumps the report to a file next to the project. The editor bridge this is normally
        /// read through does not reliably survive entering play mode, and a verification channel
        /// that dies exactly when the thing under test starts is no channel at all.
        /// </summary>
        private void WriteReportFile()
        {
            try
            {
                string path = System.IO.Path.Combine(
                    Application.dataPath, "..", "Logs", "break-probe.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, Report());
                Debug.Log($"[BreakProbe] report written to {System.IO.Path.GetFullPath(path)}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BreakProbe] could not write report: {e.Message}");
            }
        }

        /// <summary>
        /// Containment fired. Counted always; only *logged* when the ball was not on its way into a
        /// pocket.
        ///
        /// **Every ball that drops into a corner pocket trips containment**, and the original wording
        /// ("leaving play") therefore described the common case wrongly. The geometry forces it: the
        /// notch runs out to |x| = 1.420 while containment's limit is MaxX = 1.3915, so a ball centre
        /// has to cross that limit to be over the hole at all. It is clamped back to 1.3915, which is
        /// still over the notch (which starts at 1.290), so it drops anyway — the pocket works and the
        /// counter ticks.
        ///
        /// Measured on a real two-process game (#139): 6 clamps against 5 balls pocketed, and eleven
        /// warnings in the console that all meant "a ball went in". That is worse than noise — the one
        /// thing this warning exists to catch is a ball beating the rails, and a reader who has learnt
        /// to ignore it will miss that too.
        /// </summary>
        private void OnClamped(BilliardsBall ball)
        {
            ClampEvents++;

            // Near a pocket mouth ⇒ this is a drop, not an escape. Radius is the notch plus a ball,
            // which is the same span BilliardsTable uses to decide a cue ball is over a hole.
            Vector3 p = ball.Body.position;
            float nearPocket = BilliardsTable.PocketNotchHalf + BilliardsTable.BallRadius * 2f;
            foreach (Vector3 pocket in BilliardsTable.Pockets)
            {
                float dx = p.x - pocket.x;
                float dz = p.z - pocket.z;
                if (dx * dx + dz * dz <= nearPocket * nearPocket)
                {
                    PocketSideClampEvents++;
                    return;
                }
            }

            // Not near any pocket: the rails were genuinely beaten. This is the case worth shouting
            // about, and now it is the only one that shouts.
            Debug.LogWarning($"[BreakProbe] containment caught ball {ball.Number} leaving play at " +
                             $"({p.x:F3}, {p.z:F3}) — not near a pocket, so the rails were beaten.");
        }

        /// <summary>
        /// Of <see cref="ClampEvents"/>, how many were a ball dropping into a pocket rather than
        /// escaping. Reported rather than hidden: the difference is what makes the total readable.
        /// </summary>
        public int PocketSideClampEvents { get; private set; }

        private void Update()
        {
            if (!_fired)
            {
                if (!_breakOnStart)
                    return;

                _elapsed += Time.deltaTime;
                if (_elapsed < _delaySeconds)
                    return;

                _rack.Break(Vector3.right, _power);
                _fired = true;
                Debug.Log($"[BreakProbe] break fired: power={_power} dir=+X");
                return;
            }

            SampleMotion();
        }

        /// <summary>
        /// Sampled every frame rather than at settle time: the peak matters for #130 and it is
        /// gone by the time the table is still.
        /// </summary>
        private void SampleMotion()
        {
            int moving = 0;
            float fastest = 0f;

            foreach (BilliardsBall ball in _rack.Balls)
            {
                if (ball.IsPocketed)
                    continue;

                float speed = ball.Velocity.magnitude;
                if (speed > BilliardsRack.StopLinearSpeed)
                    moving++;
                if (speed > fastest)
                    fastest = speed;
            }

            if (fastest > PeakSpeed)
                PeakSpeed = fastest;
            if (moving > PeakMovingBalls)
                PeakMovingBalls = moving;
        }

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"fired={_fired} settled={Settled} settleSeconds={SettleSeconds:F2}");
            // clamps 拆成两半：进袋顺带触发的那些没有信息量，剩下的才是「球beat了库边」。
            sb.AppendLine($"pocketed={PocketedCount} clamps={ClampEvents} " +
                          $"(其中进袋顺带 {PocketSideClampEvents}，真正越界 {ClampEvents - PocketSideClampEvents})");
            sb.AppendLine($"peakSpeed={PeakSpeed:F3} peakMovingBalls={PeakMovingBalls}");
            sb.AppendLine($"shotInFlight={_rack.ShotInFlight}");
            foreach (BilliardsBall ball in _rack.Balls)
            {
                Vector3 p = ball.Body.position;
                sb.AppendLine($"  ball {ball.Number,2} pocketed={ball.IsPocketed,-5} " +
                              $"x={p.x,7:F4} y={p.y,7:F4} z={p.z,7:F4} " +
                              $"v={ball.Velocity.magnitude:F4}");
            }

            return sb.ToString();
        }
    }
}
