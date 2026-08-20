namespace DataChannelUnity.Example
{
    /// <summary>
    /// One pure function: a shot's two observables in, a verdict out. No Unity, no FishNet, no
    /// clock, no mutation — which is what makes #132's rule table checkable by reading this file
    /// against it, and testable without a scene.
    ///
    /// The order of the checks is the whole design. Pocketing the eight is judged *first*, because
    /// once it is down the game is over whatever else the shot did — a foul on the same shot changes
    /// who won, not whether the game ended.
    /// </summary>
    public static class BilliardsReferee
    {
        public static TurnVerdict Judge(ShotOutcome shot)
        {
            bool cuePotted = shot.WentDown(BilliardsRules.CueBall);
            bool eightPotted = shot.WentDown(BilliardsRules.EightBall);

            // The eight on the break is the one case where nothing else about the shot matters:
            // re-rack and the same player breaks again (#132). Deliberately checked before the cue
            // ball, because that rule voids a scratch on the same shot rather than punishing it.
            if (eightPotted && shot.WasBreak)
            {
                return new TurnVerdict
                {
                    ReRack = true,
                    ContinueTurn = true,
                    FoulReason = "eight on the break — re-rack, same player breaks"
                };
            }

            if (eightPotted)
                return JudgeEight(shot, cuePotted);

            // Ordinary shot. Both fouls #132 allows, and nothing else is one.
            bool wrongFirstContact = !shot.WasBreak && !IsLegalFirstContact(shot);

            if (cuePotted || wrongFirstContact)
            {
                return new TurnVerdict
                {
                    Foul = true,
                    BallInHand = true,
                    ContinueTurn = false,
                    FoulReason = cuePotted
                        ? "cue ball pocketed"
                        : shot.FirstContact < 0
                            ? "no ball struck"
                            : $"first contact was ball {shot.FirstContact}, not this player's group"
                };
            }

            // Legal. The table is kept only for pocketing one of your own — an opponent's ball
            // counts for them and earns nothing (#132).
            return new TurnVerdict
            {
                ContinueTurn = PottedOwn(shot),
                FoulReason = string.Empty
            };
        }

        /// <summary>
        /// The eight is down, and it was not the break. #132 states the outcome in one sentence:
        /// the potter wins if their group was already clear and the cue ball stayed up, otherwise
        /// they lose.
        /// </summary>
        /// <remarks>
        /// "Already clear" is read against the mask **before** the shot, and that is a real
        /// decision, not a detail. It means potting your last group ball and the eight on the same
        /// stroke is a loss — matching WPA 8-ball, where the eight is only legal once the group is
        /// off the table, so the eight in that shot was struck while a group ball was still up.
        /// Reading it after the shot would make the same stroke a win, and both readings fit the
        /// sentence in #132; this is the one that agrees with the rest of the rule set, since
        /// <see cref="IsLegalFirstContact"/> also judges the group from the pre-shot mask.
        /// </remarks>
        private static TurnVerdict JudgeEight(ShotOutcome shot, bool cuePotted)
        {
            var before = new BilliardsState { Pocketed = shot.PocketedBefore };
            bool clearedGroup = before.HasClearedGroup(shot.Shooter);
            bool won = clearedGroup && !cuePotted;

            return new TurnVerdict
            {
                GameOver = true,
                Winner = won
                    ? (byte)shot.Shooter
                    : (byte)BilliardsRules.OtherSeat(shot.Shooter),
                Foul = !won,
                FoulReason = won
                    ? string.Empty
                    : cuePotted
                        ? "eight pocketed with the cue ball"
                        : "eight pocketed before clearing own group"
            };
        }

        /// <summary>
        /// Legal first contact is the shooter's own group — and #132 notes the collapse that makes
        /// this one line cover shooting at the eight as well: once the group is clear, "own group"
        /// *is* <c>{8}</c>, so no special case is needed for the last ball of a game.
        /// </summary>
        private static bool IsLegalFirstContact(ShotOutcome shot)
        {
            if (shot.FirstContact < 0)
                return false; // A whiff is a foul (#132: "including having struck nothing").

            var before = new BilliardsState { Pocketed = shot.PocketedBefore };
            if (before.HasClearedGroup(shot.Shooter))
                return shot.FirstContact == BilliardsRules.EightBall;

            return BilliardsRules.OwnerOf(shot.FirstContact) == shot.Shooter;
        }

        private static bool PottedOwn(ShotOutcome shot)
        {
            for (int n = 1; n <= 15; n++)
            {
                if (n == BilliardsRules.EightBall)
                    continue;
                if (BilliardsRules.OwnerOf(n) == shot.Shooter && shot.WentDown(n))
                    return true;
            }

            return false;
        }
    }
}
