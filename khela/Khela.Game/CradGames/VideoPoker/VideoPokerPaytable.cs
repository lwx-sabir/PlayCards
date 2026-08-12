namespace CardGames.VideoPoker
{
    /// <summary>
    /// A video-poker paytable as DATA (never hardcoded in the engine) — the "win-rate dial". Payouts are stated
    /// PER COIN; a category returns <c>multiplier × coins</c> EXCEPT the royal flush, which jumps at max bet (the
    /// classic 250/coin at 1–4 coins, a flat 4,000 at 5 = 800/coin). <see cref="Payout"/> returns the GROSS coins
    /// returned (0 = a losing hand). Ships Jacks or Better 9/6 and Deuces Wild (full-pay). Wild variants add the
    /// <see cref="WildRoyal"/>, <see cref="FiveOfAKind"/> and <see cref="FourDeucesPerCoin"/> rows; the bonus-family
    /// kicker tiers (which read <see cref="VideoPokerHandRank.PrimaryRank"/> + <see cref="VideoPokerHandRank.Kicker"/>)
    /// extend it later.
    /// </summary>
    public sealed class VideoPokerPaytable
    {
        public string Name { get; init; } = "Jacks or Better 9/6";

        // Per-coin multipliers for the natural categories. The royal is the non-linear special below.
        public int StraightFlush { get; init; } = 50;
        public int FourOfAKind { get; init; } = 25;
        public int FullHouse { get; init; } = 9;    // the "9" in 9/6
        public int Flush { get; init; } = 6;         // the "6" in 9/6
        public int Straight { get; init; } = 4;
        public int ThreeOfAKind { get; init; } = 3;
        public int TwoPair { get; init; } = 2;
        public int PayingPair { get; init; } = 1;             // the "Jacks or Better" line
        public int MinPayingPairRank { get; init; } = 11;     // Jack (Kings-or-Better = 13; Deuces removes pairs = 15)

        // Wild-variant rows (0 = not offered by this table).
        public int WildRoyal { get; init; } = 0;      // A-K-Q-J-10 flush completed WITH a wild (pays < a natural royal)
        public int FiveOfAKind { get; init; } = 0;    // only reachable with wilds
        public int FourDeucesPerCoin { get; init; } = 0;   // Deuces-Wild bonus: all four 2s held (WildCount == 4)

        // Royal flush: linear per-coin below max bet, a flat jackpot at max bet.
        public int RoyalPerCoin { get; init; } = 250;
        public int MaxBetCoins { get; init; } = 5;
        public int RoyalAtMaxBet { get; init; } = 4000;

        /// <summary>Full-pay Jacks or Better 9/6 (RTP 99.54% @ max bet, optimal play).</summary>
        public static VideoPokerPaytable JacksOrBetter96 => new();

        /// <summary>
        /// Full-pay Deuces Wild (25-15-9-5-3-2, RTP 100.76% @ max bet, optimal play). Deuces are always wild, the
        /// minimum paying hand is three of a kind, and the ranking adds Five of a Kind + Wild Royal + the Four Deuces
        /// bonus (a flat 200/coin for holding all four 2s, sitting just under the natural royal).
        /// </summary>
        public static VideoPokerPaytable DeucesWildFullPay => new()
        {
            Name = "Deuces Wild (full-pay)",
            FourDeucesPerCoin = 200,
            WildRoyal = 25,
            FiveOfAKind = 15,
            StraightFlush = 9,
            FourOfAKind = 5,
            FullHouse = 3,
            Flush = 2,
            Straight = 2,
            ThreeOfAKind = 1,
            TwoPair = 0,
            PayingPair = 0,
            MinPayingPairRank = 15,   // no pair pays
            // Royal + max-bet jackpot unchanged (250 / 4000).
        };

        /// <summary>Gross coins returned for <paramref name="rank"/> at a bet of <paramref name="coins"/> (1..5).
        /// 0 = a losing hand. The royal is the only non-linear row; the Four Deuces bonus overrides the category.</summary>
        public int Payout(VideoPokerHandRank rank, int coins)
        {
            if (coins < 1) return 0;
            // Four Deuces (all four wilds) — a Deuces-Wild bonus above Five of a Kind, paid linearly per coin.
            if (FourDeucesPerCoin > 0 && rank.WildCount == 4) return FourDeucesPerCoin * coins;
            switch (rank.Category)
            {
                case VideoPokerCategory.RoyalFlush:
                    return coins >= MaxBetCoins ? RoyalAtMaxBet : RoyalPerCoin * coins;
                case VideoPokerCategory.WildRoyal:     return WildRoyal * coins;
                case VideoPokerCategory.FiveOfAKind:   return FiveOfAKind * coins;
                case VideoPokerCategory.StraightFlush: return StraightFlush * coins;
                case VideoPokerCategory.FourOfAKind:   return FourOfAKind * coins;
                case VideoPokerCategory.FullHouse:     return FullHouse * coins;
                case VideoPokerCategory.Flush:         return Flush * coins;
                case VideoPokerCategory.Straight:      return Straight * coins;
                case VideoPokerCategory.ThreeOfAKind:  return ThreeOfAKind * coins;
                case VideoPokerCategory.TwoPair:       return TwoPair * coins;
                case VideoPokerCategory.Pair:
                    return rank.PrimaryRank >= MinPayingPairRank ? PayingPair * coins : 0;   // Jacks or Better only
                default: return 0;   // low pair / high card
            }
        }
    }
}
