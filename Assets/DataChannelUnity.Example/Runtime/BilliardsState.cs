using UnityEngine;

namespace DataChannelUnity.Example
{
    /// <summary>Where the turn machine is (#132 §6; the byte on the wire, #135).</summary>
    public enum BilliardsPhase : byte
    {
        /// <summary>Waiting for both seats to be filled.</summary>
        Lobby = 0,

        /// <summary>A break is owed. Distinct from <see cref="Aim"/> only in that first contact is not checked.</summary>
        Break = 1,

        Aim = 2,

        /// <summary>Balls are moving. Ends on #131's stop criterion.</summary>
        Simulate = 3,

        /// <summary>
        /// Adjudicating. Transient — entered and left inside one physics step, so it is never
        /// observed on the wire. Kept as a named value because #132's diagram names it, and a phase
        /// enum that omits the step where the rules actually run reads as if they run nowhere.
        /// </summary>
        Resolve = 4,

        GameOver = 5,

        /// <summary>Somebody has offered a rematch. Both ready bits flip it back to <see cref="Break"/>.</summary>
        RematchPending = 6
    }

    /// <summary>
    /// The whole low-frequency state, as it goes on the wire (#135): a pocketed mask, four bytes,
    /// and the authoritative ball positions.
    ///
    /// **Everything derivable is derived.** How many each side has left, whether the eight is still
    /// up, whether a seat has cleared its group — all of them are functions of
    /// <see cref="Pocketed"/>, because #132's pre-assigned groups make "ball number → whose" a
    /// compile-time constant. Sending them too would create a second source of truth that
    /// eventually disagrees with the first.
    /// </summary>
    public struct BilliardsState
    {
        /// <summary>Bit <c>n</c> set means ball <c>n</c> is off the table. Bit 0 is the cue ball.</summary>
        public ushort Pocketed;

        public BilliardsPhase Phase;

        /// <summary>Seat to shoot, or <see cref="BilliardsRules.SeatNone"/> outside a turn.</summary>
        public byte TurnSeat;

        /// <summary>See <see cref="BilliardsFlags"/>.</summary>
        public byte Flags;

        /// <summary>Only meaningful in <see cref="BilliardsPhase.GameOver"/>.</summary>
        public byte Winner;

        /// <summary>
        /// Authoritative resting positions, indexed by ball number, on the table plane.
        ///
        /// This is #131's snapshot, and its one job is that the final position of a shot is exact:
        /// the next player's aim is computed from these numbers, not from the interpolated
        /// transforms on screen. It is *not* how a late joiner catches up — the spawn message
        /// already carries each ball's transform (#135, fact 3).
        /// </summary>
        public Vector2[] BallPositions;

        public bool IsPocketed(int ballNumber) => (Pocketed & (1 << ballNumber)) != 0;

        public bool HasFlag(BilliardsFlags flag) => (Flags & (byte)flag) != 0;

        /// <summary>How many of a seat's own balls are still on the table. Derived, never stored.</summary>
        public int Remaining(int seat)
        {
            int count = 0;
            for (int n = 1; n <= 15; n++)
            {
                if (n == BilliardsRules.EightBall)
                    continue;
                if (BilliardsRules.OwnerOf(n) == seat && !IsPocketed(n))
                    count++;
            }

            return count;
        }

        public bool EightStillUp => !IsPocketed(BilliardsRules.EightBall);

        /// <summary>True once a seat may legally shoot at the eight. Derived, never stored.</summary>
        public bool HasClearedGroup(int seat) => Remaining(seat) == 0;
    }

    /// <summary>
    /// The flag bits of <see cref="BilliardsState.Flags"/>. One byte, so adding a bit costs nothing
    /// on the wire — which is why the reconnect wait is a flag rather than a phase: it can be true
    /// *during* any phase, and folding it into the phase enum would double every phase that can
    /// coexist with it.
    /// </summary>
    [System.Flags]
    public enum BilliardsFlags : byte
    {
        None = 0,

        /// <summary>The shooter may place the cue ball anywhere legal before shooting (#132).</summary>
        BallInHand = 1 << 0,

        HostReady = 1 << 1,
        ClientReady = 1 << 2,

        /// <summary>
        /// A seat is being held for a client that dropped (#134). No shot is accepted and the turn
        /// clock is paused while it is set. The remaining seconds are deliberately *not* on the
        /// wire: a client that sees this bit go up runs its own 30 s countdown, which costs a byte
        /// per turn instead of a byte per tick.
        /// </summary>
        AwaitingReconnect = 1 << 3,

        /// <summary>
        /// This game was abandoned rather than won — the held seat never came back. Paired with
        /// <see cref="BilliardsPhase.GameOver"/> and a winner of <see cref="BilliardsRules.SeatNone"/>,
        /// so "nobody won" is stated rather than inferred from a sentinel.
        /// </summary>
        Abandoned = 1 << 4
    }

    /// <summary>
    /// What one shot did, in exactly the two observables #132 reduced the rules to. Produced by the
    /// host from physics, consumed by <see cref="BilliardsReferee"/>.
    /// </summary>
    public struct ShotOutcome
    {
        /// <summary>The seat that took the shot.</summary>
        public int Shooter;

        /// <summary>True if first contact is not to be checked — the break (#132).</summary>
        public bool WasBreak;

        /// <summary>First object ball the cue touched, or -1 for a whiff.</summary>
        public int FirstContact;

        /// <summary>Pocketed mask before the shot.</summary>
        public ushort PocketedBefore;

        /// <summary>Pocketed mask after the shot.</summary>
        public ushort PocketedAfter;

        public bool WentDown(int ballNumber)
        {
            int bit = 1 << ballNumber;
            return (PocketedBefore & bit) == 0 && (PocketedAfter & bit) != 0;
        }
    }

    /// <summary>The referee's answer for one shot.</summary>
    public struct TurnVerdict
    {
        public bool Foul;

        /// <summary>Why, for the log and the UI. Empty when <see cref="Foul"/> is false.</summary>
        public string FoulReason;

        /// <summary>The shooter keeps the table.</summary>
        public bool ContinueTurn;

        /// <summary>Next shooter gets to place the cue ball anywhere legal.</summary>
        public bool BallInHand;

        /// <summary>The rack must be re-set and the same seat breaks again (eight on the break).</summary>
        public bool ReRack;

        public bool GameOver;

        /// <summary>Valid when <see cref="GameOver"/>; <see cref="BilliardsRules.SeatNone"/> if abandoned.</summary>
        public byte Winner;
    }
}
