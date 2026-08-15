using System.Text;
using DataChannelUnity.Example;
using UnityEditor;
using UnityEngine;

namespace DataChannelUnity.EditorTools
{
    /// <summary>
    /// Runs the break without play mode by stepping physics directly, so the result can be checked
    /// from a batch-mode invocation. This exists because the editor bridge does not reliably survive
    /// entering play mode: a verification channel that dies when the thing under test starts is no
    /// channel at all.
    ///
    /// What this does *not* cover: anything that only happens in play mode — FishNet's tick, the
    /// pump, replication. It covers the physics claims of #136 and nothing beyond them.
    /// </summary>
    public static class BilliardsHeadlessCheck
    {
        /// <summary>
        /// Aims a ball at each pocket in turn and checks it actually drops. A break that happens to
        /// sink nothing proves nothing about the pockets, and this is the claim the whole redesign
        /// rests on — the surface has holes and gravity does the rest.
        ///
        /// Also rolls a ball along a rail past a side pocket. The design this replaced swallowed
        /// every ball that did that, so it is worth showing the new one does not.
        /// </summary>
        private static string CheckPocketsSwallow()
        {
            var sb = new StringBuilder();
            var rack = Object.FindObjectOfType<BilliardsRack>();
            BilliardsBall cue = rack.CueBall;
            float step = 1f / 30f;

            sb.AppendLine("pocket drop test (ball nudged at each pocket):");
            for (int i = 0; i < BilliardsTable.Pockets.Length; i++)
            {
                Vector3 pocket = BilliardsTable.Pockets[i];
                rack.ResetRack();

                // Start a little inside the pocket and push it outward along Z, the direction the
                // notch is open.
                var from = new Vector3(
                    Mathf.Clamp(pocket.x, -BilliardsTable.MaxX + 0.02f, BilliardsTable.MaxX - 0.02f),
                    BilliardsTable.BallY,
                    Mathf.Sign(pocket.z) * (BilliardsTable.HalfWidth - 0.20f));
                cue.Restore(from);
                cue.Strike(new Vector3(0f, 0f, Mathf.Sign(pocket.z) * 0.55f));

                float t = 0f;
                while (t < 4f && !cue.IsPocketed)
                {
                    Physics.Simulate(step);
                    rack.Step(step);
                    t += step;
                }

                sb.AppendLine($"  pocket {i} at ({pocket.x,6:F3},{pocket.z,6:F3}): " +
                              $"pocketed={cue.IsPocketed} after {t:F2}s");
            }

            // Rail-run test: travel along the +Z rail and pass the side pocket at x = 0.
            // The start must clear the corner notch, which reaches 2*PocketNotchHalf inward from
            // the corner — starting inside it drops the ball before it has travelled at all.
            rack.ResetRack();
            float clearOfCorner = -BilliardsTable.HalfLength +
                                  BilliardsTable.PocketNotchHalf * 2f + 0.15f;
            cue.Restore(new Vector3(clearOfCorner, BilliardsTable.BallY,
                BilliardsTable.MaxZ - 0.001f));
            cue.Strike(new Vector3(1.6f, 0f, 0f));

            float rt = 0f;
            float zWhilePassing = float.NaN;
            float captureX = float.NaN;
            float travelled = 0f;
            float startX = cue.Body.position.x;

            while (rt < 4f)
            {
                Vector3 before = cue.Body.position;
                Physics.Simulate(step);
                rack.Step(step);
                rt += step;

                Vector3 now = cue.Body.position;
                travelled = Mathf.Max(travelled, Mathf.Abs(now.x - startX));

                if (now.x > -0.12f && now.x < 0.12f && !cue.IsPocketed)
                    zWhilePassing = now.z;

                if (cue.IsPocketed && float.IsNaN(captureX))
                    captureX = before.x;
            }

            // What matters is *where* it was taken, not whether it was ever taken: on a real table a
            // ball hugging the cushion does drop into the side pocket. The design this replaced was
            // wrong because it captured anywhere along the rail, the pocket line being only a ball
            // radius away for the whole length of the table.
            sb.AppendLine($"rail-run along +Z rail: travelledX={travelled:F3} " +
                          $"zWhilePassingSidePocket={zWhilePassing:F4} " +
                          $"capturedAtX={(float.IsNaN(captureX) ? "never" : captureX.ToString("F3"))}");
            sb.AppendLine("  (captured at a pocket x — 0.00 or ±1.42 — is correct; " +
                          "captured mid-rail would be the old bug)");

            rack.ResetRack();
            return sb.ToString();
        }

        [MenuItem("Tools/DataChannel Example/Run Break Check (no play mode)")]
        public static void Run()
        {
            var report = new StringBuilder();
            SimulationMode restore = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;

            try
            {
                // Batch mode starts with no scene open, so load it rather than assuming the caller
                // did. Harmless when it is already open.
                const string scenePath =
                    "Assets/DataChannelUnity.Example/Scenes/Billiards over DataChannel.unity";
                if (Object.FindObjectOfType<BilliardsRack>() == null)
                {
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                        scenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                }

                var rack = Object.FindObjectOfType<BilliardsRack>();
                if (rack == null)
                {
                    Debug.LogError($"[HeadlessCheck] No BilliardsRack after opening {scenePath}.");
                    return;
                }

                foreach (BilliardsBall ball in Object.FindObjectsOfType<BilliardsBall>(true))
                    rack.Register(ball);

                rack.ResetRack();
                report.AppendLine($"balls registered: {rack.Balls.Count}");

                float step = 1f / 30f; // Matches TickRate 30, where physics actually runs.
                int clamps = 0;
                int pocketed = 0;
                float peakSpeed = 0f;
                int peakMoving = 0;
                float settleAt = -1f;

                rack.ShotSettled += s => settleAt = s;
                rack.BallClamped += _ => clamps++;
                rack.BallPocketed += _ => pocketed++;

                rack.Break(Vector3.right, 4.0f);

                float elapsed = 0f;
                const float limit = 30f;
                while (settleAt < 0f && elapsed < limit)
                {
                    Physics.Simulate(step);
                    rack.Step(step);
                    elapsed += step;

                    int moving = 0;
                    foreach (BilliardsBall ball in rack.Balls)
                    {
                        if (ball.IsPocketed)
                            continue;
                        float speed = ball.Velocity.magnitude;
                        if (speed > BilliardsRack.StopLinearSpeed)
                            moving++;
                        if (speed > peakSpeed)
                            peakSpeed = speed;
                    }

                    if (moving > peakMoving)
                        peakMoving = moving;
                }

                report.AppendLine($"settled={(settleAt >= 0f ? $"{settleAt:F2}s" : "NO (hit limit)")}");
                report.AppendLine($"pocketed={pocketed} clamps={clamps}");
                report.AppendLine($"peakSpeed={peakSpeed:F3} peakMovingBalls={peakMoving}");
                report.AppendLine($"wallSteps={elapsed / step:F0}");

                float lowest = float.MaxValue, highest = float.MinValue;
                foreach (BilliardsBall ball in rack.Balls)
                {
                    Vector3 p = ball.Body.position;
                    report.AppendLine($"  ball {ball.Number,2} pocketed={ball.IsPocketed,-5} " +
                                      $"x={p.x,8:F4} y={p.y,8:F4} z={p.z,8:F4}");
                    if (!ball.IsPocketed)
                    {
                        lowest = Mathf.Min(lowest, p.y);
                        highest = Mathf.Max(highest, p.y);
                    }
                }

                report.AppendLine($"in-play Y range: {lowest:F4} .. {highest:F4} " +
                                  $"(surface is {BilliardsTable.BallY:F4})");
            }
            finally
            {
                Physics.simulationMode = restore;
            }

            report.AppendLine();
            report.AppendLine(CheckPocketsSwallow());

            string path = System.IO.Path.Combine(Application.dataPath, "..", "Logs",
                "break-headless.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, report.ToString());
            Debug.Log($"[HeadlessCheck] written to {System.IO.Path.GetFullPath(path)}\n{report}");
        }
    }
}
