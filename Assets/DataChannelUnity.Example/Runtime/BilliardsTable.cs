using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// Table geometry and the fixed rack. Pure data plus placement helpers; no networking,
    /// no rules. Everything here is deterministic — #132 requires zero randomness in code,
    /// so the only variation in a break comes from the player's aim and power.
    /// </summary>
    public static class BilliardsTable
    {
        /// <summary>Playing area along X, metres (nine-foot table, per #131/#136).</summary>
        public const float Length = 2.84f;

        /// <summary>Playing area along Z, metres.</summary>
        public const float Width = 1.42f;

        /// <summary>Ball radius, metres (57 mm diameter).</summary>
        public const float BallRadius = 0.0285f;

        /// <summary>
        /// Height of a ball centre at rest on the cloth. Where a ball is *placed*, not where it stays:
        /// gravity is on and Y is a live degree of freedom, so a ball in flight or dropping into a
        /// pocket sits elsewhere.
        /// </summary>
        public const float BallY = 0f;

        /// <summary>Rail thickness. Deliberately thick — see BilliardsBall for why.</summary>
        public const float RailThickness = 0.20f;

        /// <summary>Rail height, tall enough that a ball cannot ride over it.</summary>
        public const float RailHeight = 0.20f;

        /// <summary>
        /// Half-width of the square notch cut out of the playing surface at each pocket. A ball
        /// (57 mm) drops through comfortably while still being able to rattle on the lip.
        ///
        /// The notch is square because the surface is assembled from boxes and a box cannot cut a
        /// circle. The consequence is a straight lip rather than a curved one, which changes how a
        /// ball glances off the jaw slightly. Nothing above the geometry depends on the shape.
        /// </summary>
        public const float PocketNotchHalf = 0.065f;

        /// <summary>
        /// Below this height a ball has left the playing surface through a pocket. It is the whole
        /// capture test: falling in or rattling out is settled by the physics engine, not by a rule.
        /// </summary>
        public const float FallThroughY = -0.12f;

        /// <summary>
        /// Ceiling for the containment backstop. Balls have no business gaining height on a flat
        /// surface; if one does, something is wrong and it should be brought back rather than lost.
        /// </summary>
        public const float MaxY = 0.15f;

        public static float HalfLength => Length * 0.5f;
        public static float HalfWidth => Width * 0.5f;

        /// <summary>Furthest a ball centre may sit from the origin before it is out of play.</summary>
        public static float MaxX => HalfLength - BallRadius;
        public static float MaxZ => HalfWidth - BallRadius;

        /// <summary>Cue ball's fixed break position (head spot), one quarter along the table.</summary>
        public static Vector3 HeadSpot => new Vector3(-HalfLength * 0.5f, BallY, 0f);

        /// <summary>Rack apex (foot spot), three quarters along the table.</summary>
        public static Vector3 FootSpot => new Vector3(HalfLength * 0.5f, BallY, 0f);

        /// <summary>
        /// Six pockets: four corners plus two in the middle of the long rails. Just positions —
        /// each is a hole in the playing surface, and whether a ball drops in or rattles out is
        /// decided by the physics engine rather than by any test of ours.
        /// </summary>
        public static readonly Vector3[] Pockets =
        {
            new Vector3(-HalfLength, BallY, -HalfWidth),
            new Vector3(-HalfLength, BallY, HalfWidth),
            new Vector3(0f, BallY, -HalfWidth),
            new Vector3(0f, BallY, HalfWidth),
            new Vector3(HalfLength, BallY, -HalfWidth),
            new Vector3(HalfLength, BallY, HalfWidth)
        };

        /// <summary>One box making up part of the playing surface. Sizes are on the XZ plane.</summary>
        public readonly struct SurfacePiece
        {
            public readonly Vector2 Centre;
            public readonly Vector2 Size;

            public SurfacePiece(Vector2 centre, Vector2 size)
            {
                Centre = centre;
                Size = size;
            }
        }

        /// <summary>
        /// The playing surface, as boxes with the pockets left out.
        ///
        /// This is where the complexity of "pockets are real holes" actually lands: a box collider
        /// cannot have a hole cut in it, so the surface has to be assembled from pieces that avoid
        /// the notches. It is worth paying here rather than in a rule, because the result is static
        /// geometry — decided once at build time — instead of a test that runs every tick.
        ///
        /// Layout: pockets only ever sit on the two long rails, so only the strips along those
        /// rails are interrupted. Everything between them is one unbroken slab.
        /// </summary>
        public static SurfacePiece[] SurfacePieces()
        {
            float notch = PocketNotchHalf;
            var pieces = new System.Collections.Generic.List<SurfacePiece>();

            // Middle slab: full length, spanning the gap between the two notched edge strips.
            float middleHalfWidth = HalfWidth - notch;
            if (middleHalfWidth > 0f)
                pieces.Add(new SurfacePiece(Vector2.zero, new Vector2(Length, middleHalfWidth * 2f)));

            // Edge strips along ±Z, each interrupted by three notches (two corners, one side).
            foreach (float zSign in new[] { -1f, 1f })
            {
                float centreZ = zSign * (HalfWidth - notch * 0.5f);
                AddInterruptedRun(pieces, centreZ, notch);
            }

            return pieces.ToArray();
        }

        /// <summary>
        /// Emits the pieces of one edge strip, skipping the spans taken by pocket notches. The
        /// notch x-positions come from <see cref="Pockets"/> so the geometry and the pockets cannot
        /// drift apart — computing them twice is the classic way to end up with a table whose holes
        /// are not where its pockets are.
        /// </summary>
        private static void AddInterruptedRun(
            System.Collections.Generic.List<SurfacePiece> pieces, float centreZ, float notch)
        {
            var notchCentres = new System.Collections.Generic.List<float>();
            foreach (Vector3 pocket in Pockets)
            {
                if (!notchCentres.Contains(pocket.x))
                    notchCentres.Add(pocket.x);
            }

            notchCentres.Sort();

            float cursor = -HalfLength;
            foreach (float x in notchCentres)
            {
                // A corner notch is clipped by the table edge, so centring it on the corner leaves
                // an opening only half as wide — barely wider than a ball, which then bridges the
                // hole instead of dropping through it. Corners therefore extend inward by a full
                // notch width, giving every pocket the same clear span along the rail.
                bool atCorner = Mathf.Abs(Mathf.Abs(x) - HalfLength) < 0.0001f;
                float gapStart = atCorner && x < 0f ? x : x - notch;
                float gapEnd = atCorner && x > 0f ? x : x + notch;
                if (atCorner)
                {
                    if (x < 0f)
                        gapEnd = x + notch * 2f;
                    else
                        gapStart = x - notch * 2f;
                }

                float width = gapStart - cursor;
                if (width > 0.0005f)
                {
                    pieces.Add(new SurfacePiece(
                        new Vector2(cursor + width * 0.5f, centreZ),
                        new Vector2(width, notch)));
                }

                cursor = Mathf.Max(cursor, gapEnd);
            }

            float tail = HalfLength - cursor;
            if (tail > 0.0005f)
            {
                pieces.Add(new SurfacePiece(
                    new Vector2(cursor + tail * 0.5f, centreZ),
                    new Vector2(tail, notch)));
            }
        }

        /// <summary>
        /// Where a pocketed ball parks. Off the playing surface, in a fixed row, so the ball
        /// stays spawned and costs zero bytes once it stops (#131 §5).
        /// </summary>
        public static Vector3 ParkingSlot(int ballNumber)
        {
            const float spacing = BallRadius * 2.2f;
            return new Vector3(-HalfLength + spacing * ballNumber, BallY, HalfWidth + 0.35f);
        }

        /// <summary>
        /// The rack, row by row from the apex. Ball 8 sits in the middle of row three and the
        /// back corners hold one of each group, as in a real rack. Group membership itself is
        /// pre-assigned by number (#132), so this arrangement is cosmetic — but a rack that
        /// looks wrong invites the reader to think the groups come from it.
        /// </summary>
        private static readonly int[][] RackRows =
        {
            new[] { 1 },
            new[] { 9, 2 },
            new[] { 10, 8, 3 },
            new[] { 11, 4, 12, 5 },
            new[] { 6, 13, 7, 14, 15 }
        };

        /// <summary>
        /// Rack position for one numbered ball (1..15). Rows step back from the apex by one
        /// ball diameter times cos(30°), which is what makes a triangle tight rather than gapped.
        /// A small extra gap keeps balls from starting interpenetrated — otherwise the solver
        /// shoves them apart on frame one and the "fixed" rack is not the rack you specified.
        /// </summary>
        public static Vector3 RackPosition(int ballNumber)
        {
            // Must exceed Physics.defaultContactOffset (0.01 here), not merely be non-zero.
            // A real rack is tight, but a rack tighter than the contact offset is one PhysX already
            // considers fully in contact before the cue arrives: it resolves the whole cluster as a
            // single manifold and shoves it downtable as one lump instead of propagating the break
            // ball to ball. The visible symptom is a break with no lateral scatter — every ball
            // ends up against the far cushion, and from there they drain into the corner pockets
            // no matter how the mouths are shaped.
            const float gap = 0.012f;
            float diameter = BallRadius * 2f + gap;
            float rowStep = diameter * 0.8660254f; // cos(30°)

            for (int row = 0; row < RackRows.Length; row++)
            {
                int[] cells = RackRows[row];
                for (int cell = 0; cell < cells.Length; cell++)
                {
                    if (cells[cell] != ballNumber)
                        continue;

                    float x = FootSpot.x + rowStep * row;
                    float z = (cell - (cells.Length - 1) * 0.5f) * diameter;
                    return new Vector3(x, BallY, z);
                }
            }

            throw new System.ArgumentOutOfRangeException(nameof(ballNumber),
                $"Ball {ballNumber} is not in the rack; expected 1..15.");
        }
    }
}
