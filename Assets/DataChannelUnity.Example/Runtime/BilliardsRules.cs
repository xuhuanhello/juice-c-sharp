namespace DataChannelUnity.Example
{
    /// <summary>
    /// Eight-ball adjudication, as pure functions over the two observables #132 settled: which ball
    /// the cue struck first, and where every ball ended up. Nothing here touches Unity, FishNet or
    /// the clock, so the whole rule set is testable without a scene.
    ///
    /// Group membership is a compile-time constant rather than state, because #132 pre-assigns it:
    /// seat 0 (the host) always has 1–7, seat 1 always has 9–15. That is what collapses "how many
    /// has each side left" and "is the eight still up" into derived values of one 16-bit pocketed
    /// mask — see <see cref="BilliardsState"/>. Storing them as well would be a second source of
    /// truth, and it would eventually disagree with the first.
    /// </summary>
    public static class BilliardsRules
    {
        public const int SeatHost = 0;
        public const int SeatClient = 1;
        public const int SeatCount = 2;

        /// <summary>No seat. Also the "no winner yet" value on the wire.</summary>
        public const byte SeatNone = 255;

        public const int CueBall = 0;
        public const int EightBall = 8;
        public const int BallCount = 16;

        /// <summary>
        /// Largest impulse a shot may carry. 4.0 is not a taste judgement: it is the highest power
        /// #137 measured as clean (settles in 4.63 s, pockets nothing on its own, never trips
        /// containment), and at 4.5 the same break starts hitting the containment clamp. Shots are
        /// clamped to it rather than rejected, for the same reason an illegal cue placement is
        /// snapped rather than refused (#132): a rejection costs a round trip and a UI state.
        /// </summary>
        public const float MaxPower = 4.0f;

        /// <summary>Seconds a player may sit on their turn before it passes (#132). Host-timed.</summary>
        public const float TurnTimeoutSeconds = 60f;

        /// <summary>
        /// How long a seat stays reserved after its client drops (#134). Deliberately shorter than
        /// <see cref="TurnTimeoutSeconds"/>: if waiting for a reconnect took longer than waiting for
        /// an ordinary shot, the opponent could not tell which of the two was happening.
        /// </summary>
        public const float SeatHoldSeconds = 30f;

        /// <summary>Which seat owns a numbered ball; <see cref="SeatNone"/> for the cue and the eight.</summary>
        public static byte OwnerOf(int ballNumber)
        {
            if (ballNumber >= 1 && ballNumber <= 7)
                return SeatHost;
            if (ballNumber >= 9 && ballNumber <= 15)
                return SeatClient;
            return SeatNone;
        }

        public static int OtherSeat(int seat) => seat == SeatHost ? SeatClient : SeatHost;
    }
}
