namespace CardGames.ThreeCardPoker
{
    /// <summary>
    /// All Three Card Poker payouts as DATA (never hardcoded in settlement) so a schedule is a config choice /
    /// "win-rate dial", per the spec. Odds are stated "to 1" — a 4 means 4:1, i.e. a win returns
    /// <c>stake × (4 + 1)</c> (stake + winnings). Defaults ship the spec's recommended generous tables.
    /// </summary>
    public sealed class ThreeCardPokerPaytables
    {
        // ── Ante Bonus ── paid on the Ante for a straight-or-better, EVEN ON A LOSS, only if the hand PLAYED.
        //    Spec "Table 1": Straight 1:1, Trips 4:1, Straight Flush 5:1 (≈3.37% overall ante edge).
        public int AnteBonusStraight { get; init; } = 1;
        public int AnteBonusTrips { get; init; } = 4;
        public int AnteBonusStraightFlush { get; init; } = 5;

        // ── Pair Plus ── wins on a pair-or-better in the player's own 3 cards; pays independently of fold/showdown.
        //    Default 40/30/6/4/1 (SF/Trips/Straight/Flush/Pair) = 2.32% house edge (generous, retention-friendly).
        public int PairPlusStraightFlush { get; init; } = 40;
        public int PairPlusTrips { get; init; } = 30;
        public int PairPlusStraight { get; init; } = 6;
        public int PairPlusFlush { get; init; } = 4;
        public int PairPlusPair { get; init; } = 1;

        /// <summary>Optional TOP-LINE tier: a suited A-K-Q (mini royal) pays this instead of the straight-flush
        /// rate. 0 = disabled. Live house-banked 3-card tables pay up to 100:1 here.</summary>
        public int PairPlusMiniRoyal { get; init; } = 0;

        // ── Prime ── colour side bet: player's 3 cards same colour, or ALL 6 (player + dealer) same colour (higher).
        public int PrimeThreeSameColour { get; init; } = 3;
        public int PrimeSixSameColour { get; init; } = 4;

        // ── 6-Card Bonus / "3+3" ── best 5-card hand from player's 3 + dealer's 3 (STANDARD ranking, flush > straight);
        //    pays on three-of-a-kind or better. Default = WoO "Version 1-A" (the most generous, ≈8.56% edge).
        //    ⚠ Every other real table is 10–19% edge — keep 1-A if offered at all.
        public int SixCardRoyalFlush { get; init; } = 1000;
        public int SixCardStraightFlush { get; init; } = 200;
        public int SixCardFourOfAKind { get; init; } = 100;
        public int SixCardFullHouse { get; init; } = 20;
        public int SixCardFlush { get; init; } = 15;
        public int SixCardStraight { get; init; } = 10;
        public int SixCardThreeOfAKind { get; init; } = 7;

        public static ThreeCardPokerPaytables Default => new();
    }
}
