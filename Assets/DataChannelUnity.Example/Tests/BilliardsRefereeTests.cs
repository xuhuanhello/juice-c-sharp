using NUnit.Framework;
using UnityEngine;

namespace DataChannelUnity.Example.Tests
{
    /// <summary>
    /// #132's rule table, read back as assertions. EditMode: <see cref="BilliardsReferee"/> is a
    /// pure function of two observables, so none of this needs a scene, a tick, or a connection —
    /// and the rules are the part most likely to be quietly changed by a later edit.
    ///
    /// The cases are named after the rows of that table rather than after the code, so a mismatch
    /// tells you which decision was broken.
    /// </summary>
    public sealed class BilliardsRefereeTests
    {
        private const int Host = BilliardsRules.SeatHost;     // solids 1–7
        private const int Client = BilliardsRules.SeatClient; // stripes 9–15

        private static ushort Mask(params int[] balls)
        {
            ushort mask = 0;
            foreach (int ball in balls)
                mask |= (ushort)(1 << ball);
            return mask;
        }

        private static ShotOutcome Shot(int shooter, int firstContact, ushort before, ushort after,
            bool wasBreak = false) => new ShotOutcome
        {
            Shooter = shooter,
            FirstContact = firstContact,
            PocketedBefore = before,
            PocketedAfter = after,
            WasBreak = wasBreak
        };

        [Test]
        public void PocketingOwnBallKeepsTheTable()
        {
            TurnVerdict v = BilliardsReferee.Judge(Shot(Host, 3, Mask(), Mask(3)));

            Assert.IsFalse(v.Foul);
            Assert.IsTrue(v.ContinueTurn, "Potting one of your own is what earns another shot (#132).");
            Assert.IsFalse(v.BallInHand);
        }

        [Test]
        public void LegalShotWithNoPotPassesTheTurn()
        {
            TurnVerdict v = BilliardsReferee.Judge(Shot(Host, 1, Mask(), Mask()));

            Assert.IsFalse(v.Foul);
            Assert.IsFalse(v.ContinueTurn);
            Assert.IsFalse(v.BallInHand, "A legal miss is not a foul, so no ball in hand.");
        }

        [Test]
        public void CueBallPocketedIsAFoulAndGivesBallInHand()
        {
            TurnVerdict v = BilliardsReferee.Judge(Shot(Host, 3, Mask(), Mask(0, 3)));

            Assert.IsTrue(v.Foul);
            Assert.IsTrue(v.BallInHand);
            Assert.IsFalse(v.ContinueTurn, "A scratch never keeps the table, even having potted one.");
        }

        [Test]
        public void FirstContactOnTheOpponentsGroupIsAFoul()
        {
            TurnVerdict v = BilliardsReferee.Judge(Shot(Host, 11, Mask(), Mask()));

            Assert.IsTrue(v.Foul);
            Assert.IsTrue(v.BallInHand);
            StringAssert.Contains("11", v.FoulReason);
        }

        [Test]
        public void StrikingNothingIsAFoul()
        {
            TurnVerdict v = BilliardsReferee.Judge(Shot(Host, -1, Mask(), Mask()));

            Assert.IsTrue(v.Foul, "#132: a whiff counts as wrong first contact.");
            Assert.IsTrue(v.BallInHand);
        }

        /// <summary>
        /// The exemption #132 says the fixed rack makes mandatory: the cue ball always reaches the
        /// 1 ball first, so without this the stripes player fouls on their own break every time.
        /// </summary>
        [Test]
        public void BreakDoesNotCheckFirstContact()
        {
            TurnVerdict v = BilliardsReferee.Judge(Shot(Client, 1, Mask(), Mask(), wasBreak: true));

            Assert.IsFalse(v.Foul, "The break is exempt from the first-contact rule (#132).");
        }

        [Test]
        public void ScratchOnTheBreakIsStillAFoul()
        {
            TurnVerdict v = BilliardsReferee.Judge(Shot(Host, 1, Mask(), Mask(0, 2), wasBreak: true));

            Assert.IsTrue(v.Foul, "The exemption covers first contact only, not the cue ball.");
            Assert.IsTrue(v.BallInHand);
        }

        [Test]
        public void EightOnTheBreakReRacksForTheSamePlayer()
        {
            TurnVerdict v = BilliardsReferee.Judge(
                Shot(Host, 1, Mask(), Mask(8), wasBreak: true));

            Assert.IsTrue(v.ReRack);
            Assert.IsTrue(v.ContinueTurn, "#132: same player breaks again.");
            Assert.IsFalse(v.GameOver, "Nobody wins or loses on the eight from the break.");
        }

        /// <summary>
        /// #132 voids the scratch as well when the eight goes on the break. Asserted separately
        /// because the two rules are checked in an order that could hide this one.
        /// </summary>
        [Test]
        public void EightAndCueBallTogetherOnTheBreakStillOnlyReRacks()
        {
            TurnVerdict v = BilliardsReferee.Judge(
                Shot(Host, 1, Mask(), Mask(0, 8), wasBreak: true));

            Assert.IsTrue(v.ReRack);
            Assert.IsFalse(v.GameOver);
        }

        [Test]
        public void EightAfterClearingOwnGroupWins()
        {
            ushort cleared = Mask(1, 2, 3, 4, 5, 6, 7);
            TurnVerdict v = BilliardsReferee.Judge(Shot(Host, 8, cleared, Mask(1, 2, 3, 4, 5, 6, 7, 8)));

            Assert.IsTrue(v.GameOver);
            Assert.AreEqual((byte)Host, v.Winner);
            Assert.IsFalse(v.Foul);
        }

        [Test]
        public void EightBeforeClearingOwnGroupLoses()
        {
            TurnVerdict v = BilliardsReferee.Judge(Shot(Host, 3, Mask(3), Mask(3, 8)));

            Assert.IsTrue(v.GameOver);
            Assert.AreEqual((byte)Client, v.Winner, "#132: otherwise the potter loses.");
        }

        [Test]
        public void EightWithAScratchLosesEvenWhenTheGroupIsClear()
        {
            ushort cleared = Mask(9, 10, 11, 12, 13, 14, 15);
            TurnVerdict v = BilliardsReferee.Judge(
                Shot(Client, 8, cleared, (ushort)(cleared | Mask(0, 8))));

            Assert.IsTrue(v.GameOver);
            Assert.AreEqual((byte)Host, v.Winner);
        }

        /// <summary>
        /// The reading #132's sentence leaves open, pinned down: "already cleared" is the mask
        /// *before* the shot, so the last group ball and the eight on one stroke is a loss. Recorded
        /// as a test because both readings fit the words, and a later edit could flip it silently.
        /// </summary>
        [Test]
        public void LastGroupBallAndEightOnTheSameShotLoses()
        {
            ushort before = Mask(1, 2, 3, 4, 5, 6);      // 7 still up
            ushort after = Mask(1, 2, 3, 4, 5, 6, 7, 8); // 7 and the eight both go down

            TurnVerdict v = BilliardsReferee.Judge(Shot(Host, 7, before, after));

            Assert.IsTrue(v.GameOver);
            Assert.AreEqual((byte)Client, v.Winner,
                "The eight was struck while a group ball was still up, which WPA 8-ball treats as " +
                "a loss — and it is the reading that agrees with the first-contact rule.");
        }

        /// <summary>
        /// #132's "natural collapse": once the group is clear, "your group" *is* {8}, so shooting
        /// at anything else is the ordinary wrong-first-contact foul with no special case.
        /// </summary>
        [Test]
        public void AfterClearingTheGroupFirstContactMustBeTheEight()
        {
            ushort cleared = Mask(1, 2, 3, 4, 5, 6, 7);
            TurnVerdict v = BilliardsReferee.Judge(Shot(Host, 11, cleared, cleared));

            Assert.IsTrue(v.Foul);
            Assert.IsTrue(v.BallInHand);
        }

        [Test]
        public void PocketingAnOpponentBallEarnsNothingButIsNotAFoul()
        {
            TurnVerdict v = BilliardsReferee.Judge(Shot(Host, 3, Mask(), Mask(11)));

            Assert.IsFalse(v.Foul, "Legal contact, so no foul — the ball simply counts for them.");
            Assert.IsFalse(v.ContinueTurn, "#132: potting theirs does not keep the table.");
        }

        [Test]
        public void GroupsAreAssignedByNumber()
        {
            Assert.AreEqual((byte)Host, BilliardsRules.OwnerOf(1));
            Assert.AreEqual((byte)Host, BilliardsRules.OwnerOf(7));
            Assert.AreEqual((byte)Client, BilliardsRules.OwnerOf(9));
            Assert.AreEqual((byte)Client, BilliardsRules.OwnerOf(15));
            Assert.AreEqual(BilliardsRules.SeatNone, BilliardsRules.OwnerOf(8));
            Assert.AreEqual(BilliardsRules.SeatNone, BilliardsRules.OwnerOf(0));
        }

        /// <summary>
        /// The derived quantities #135 chose not to send. Asserted because the whole reason they are
        /// absent from the wire is that they can be recomputed — if that stops being true, the
        /// message is missing a field.
        /// </summary>
        [Test]
        public void RemainingAndEightAreDerivedFromTheMask()
        {
            var state = new BilliardsState { Pocketed = Mask(1, 2, 3, 11) };

            Assert.AreEqual(4, state.Remaining(Host), "1,2,3 down of seven.");
            Assert.AreEqual(6, state.Remaining(Client), "11 down of seven.");
            Assert.IsTrue(state.EightStillUp);
            Assert.IsFalse(state.HasClearedGroup(Host));

            var clear = new BilliardsState { Pocketed = Mask(1, 2, 3, 4, 5, 6, 7) };
            Assert.IsTrue(clear.HasClearedGroup(Host));
            Assert.IsFalse(clear.HasClearedGroup(Client));
        }
    }

    /// <summary>
    /// The cue-ball placement function, which #132 makes load-bearing twice: it is both "snap an
    /// illegal ball-in-hand spot to the nearest legal one" and the containment clamp. EditMode
    /// because it is pure geometry.
    /// </summary>
    public sealed class BilliardsCuePlacementTests
    {
        [Test]
        public void SpotOutsideTheTableIsClampedIn()
        {
            Vector2 legal = BilliardsTable.NearestLegalCueSpot(new Vector2(99f, -99f), null);

            Assert.LessOrEqual(Mathf.Abs(legal.x), BilliardsTable.MaxX + 1e-4f);
            Assert.LessOrEqual(Mathf.Abs(legal.y), BilliardsTable.MaxZ + 1e-4f);
        }

        [Test]
        public void SpotOnAnotherBallIsPushedClear()
        {
            var occupied = new[] { new Vector2(0.2f, 0.1f) };
            Vector2 legal = BilliardsTable.NearestLegalCueSpot(occupied[0], occupied);

            float gap = (legal - occupied[0]).magnitude;
            Assert.GreaterOrEqual(gap, BilliardsTable.BallRadius * 2f,
                "Two balls may touch but not overlap; overlapping would have PhysX resolve the " +
                "placement instead of the rules.");
        }

        /// <summary>
        /// The one that is easy to miss: the pockets are holes in the surface (#136), so a cue ball
        /// placed over a mouth falls through and the shot is lost before it is taken.
        /// </summary>
        [Test]
        public void SpotOverAPocketIsPushedOntoTheCloth()
        {
            foreach (Vector3 pocket in BilliardsTable.Pockets)
            {
                var mouth = new Vector2(pocket.x, pocket.z);
                Vector2 legal = BilliardsTable.NearestLegalCueSpot(mouth, null);

                Assert.Greater((legal - mouth).magnitude, BilliardsTable.PocketNotchHalf,
                    $"Cue ball left over the pocket at {mouth}; it would drop through.");
            }
        }

        [Test]
        public void LegalSpotIsLeftAlone()
        {
            var wanted = new Vector2(-0.5f, 0.2f);
            Vector2 legal = BilliardsTable.NearestLegalCueSpot(wanted, new[] { new Vector2(0.8f, 0f) });

            Assert.AreEqual(wanted.x, legal.x, 1e-4f);
            Assert.AreEqual(wanted.y, legal.y, 1e-4f);
        }

        /// <summary>
        /// A crowded request must still terminate with something legal. The relaxation is bounded
        /// (a nudge away from one ball can push into another), so this is the case that would show
        /// up as a placement that never converged.
        /// </summary>
        [Test]
        public void CrowdedSpotStillResolvesToSomethingLegal()
        {
            var occupied = new System.Collections.Generic.List<Vector2>();
            for (int i = 0; i < 15; i++)
                occupied.Add(new Vector2(-0.1f + i * 0.01f, 0f));

            Vector2 legal = BilliardsTable.NearestLegalCueSpot(Vector2.zero, occupied);

            Assert.LessOrEqual(Mathf.Abs(legal.x), BilliardsTable.MaxX + 1e-4f);
            Assert.LessOrEqual(Mathf.Abs(legal.y), BilliardsTable.MaxZ + 1e-4f);
            foreach (Vector2 ball in occupied)
            {
                // One ball radius rather than two: with fifteen balls in a line the bounded
                // relaxation cannot always reach full separation, and the guarantee that matters is
                // that the result is on the table and not inside another ball's centre.
                Assert.Greater((legal - ball).magnitude, BilliardsTable.BallRadius,
                    "Placement ended inside another ball.");
            }
        }
    }
}
