using System.Collections.Generic;
using CardGames.VideoPoker;
using Row = Khela.Game.Games.VideoPoker.VideoPokerVariantSummary.PaytableRow;

namespace Khela.Game.Games.VideoPoker
{
    /// <summary>Turns a variant's <see cref="VideoPokerPaytable"/> into display rows (high → low) for the menu screen:
    /// per-coin multiplier + the gross at max bet (which captures the royal jackpot and the linear bonus rows). Purely
    /// presentational — the authoritative payout is always <see cref="VideoPokerPaytable.Payout"/> at settle.</summary>
    public static class VideoPokerPaytableRows
    {
        public static List<Row> For(VideoPokerVariant variant)
        {
            var p = variant.Paytable;
            int max = p.MaxBetCoins;
            var rows = new List<Row>
            {
                new Row { Hand = "Royal Flush", PerCoin = p.RoyalPerCoin, AtMaxCoins = p.RoyalAtMaxBet },
            };
            if (p.FourDeucesPerCoin > 0) rows.Add(new Row { Hand = "Four Deuces", PerCoin = p.FourDeucesPerCoin, AtMaxCoins = p.FourDeucesPerCoin * max });
            if (p.WildRoyal > 0) rows.Add(new Row { Hand = "Wild Royal Flush", PerCoin = p.WildRoyal, AtMaxCoins = p.WildRoyal * max });
            if (p.FiveOfAKind > 0) rows.Add(new Row { Hand = "Five of a Kind", PerCoin = p.FiveOfAKind, AtMaxCoins = p.FiveOfAKind * max });
            Add(rows, "Straight Flush", p.StraightFlush, max);
            Add(rows, "Four of a Kind", p.FourOfAKind, max);
            Add(rows, "Full House", p.FullHouse, max);
            Add(rows, "Flush", p.Flush, max);
            Add(rows, "Straight", p.Straight, max);
            Add(rows, "Three of a Kind", p.ThreeOfAKind, max);
            Add(rows, "Two Pair", p.TwoPair, max);
            if (p.PayingPair > 0) rows.Add(new Row { Hand = PairLabel(p.MinPayingPairRank), PerCoin = p.PayingPair, AtMaxCoins = p.PayingPair * max });
            return rows;
        }

        private static void Add(List<Row> rows, string hand, int perCoin, int max)
        {
            if (perCoin > 0) rows.Add(new Row { Hand = hand, PerCoin = perCoin, AtMaxCoins = perCoin * max });
        }

        private static string PairLabel(int minRank) => minRank switch
        {
            13 => "Kings or Better",
            12 => "Queens or Better",
            11 => "Jacks or Better",
            _ => $"Pair ({minRank}+)",
        };
    }
}
