using System;
using System.Collections.Generic;
using CardGames.Platforms;

namespace CardGames.ThreeCardPoker
{
    /// <summary>
    /// STANDARD 5-card poker categories (note: here Flush &gt; Straight, the normal ranking — the OPPOSITE of the
    /// 3-card game). Used only by the 6-Card Bonus (best 5 of the player's 3 + dealer's 3).
    /// </summary>
    public enum FiveCardCategory
    {
        HighCard = 0, Pair = 1, TwoPair = 2, ThreeOfAKind = 3, Straight = 4,
        Flush = 5, FullHouse = 6, FourOfAKind = 7, StraightFlush = 8,
    }

    /// <summary>Evaluated 5-card rank: <see cref="Category"/> (+ <see cref="IsRoyal"/> for an ace-high straight
    /// flush) and a descending tie-break key so best-of-N selection is exact.</summary>
    public readonly struct FiveCardHandRank : IComparable<FiveCardHandRank>
    {
        public FiveCardCategory Category { get; }
        public bool IsRoyal { get; }
        private readonly int _k0, _k1, _k2, _k3, _k4;

        public FiveCardHandRank(FiveCardCategory cat, bool royal, int k0, int k1, int k2, int k3, int k4)
        { Category = cat; IsRoyal = royal; _k0 = k0; _k1 = k1; _k2 = k2; _k3 = k3; _k4 = k4; }

        public int CompareTo(FiveCardHandRank o)
        {
            if (Category != o.Category) return Category.CompareTo(o.Category);
            if (_k0 != o._k0) return _k0.CompareTo(o._k0);
            if (_k1 != o._k1) return _k1.CompareTo(o._k1);
            if (_k2 != o._k2) return _k2.CompareTo(o._k2);
            if (_k3 != o._k3) return _k3.CompareTo(o._k3);
            return _k4.CompareTo(o._k4);
        }

        public override string ToString() => $"{Category}{(IsRoyal ? "(Royal)" : "")}";
    }

    /// <summary>Standard 5-card poker evaluator + best-of-6, for the 6-Card Bonus.</summary>
    public static class FiveCardEvaluator
    {
        public static FiveCardHandRank Evaluate(IReadOnlyList<Card> cards)
        {
            if (cards == null || cards.Count != 5)
                throw new ArgumentException("Five-card evaluation needs exactly 5 cards.", nameof(cards));

            Span<int> counts = stackalloc int[15];   // rank 2..14
            bool flush = true;
            Suit s0 = cards[0].Suit;
            for (int i = 0; i < 5; i++)
            {
                counts[(int)cards[i].FaceVal]++;
                if (cards[i].Suit != s0) flush = false;
            }

            // Distinct ranks (descending) for straight detection.
            Span<int> present = stackalloc int[5];
            int distinct = 0;
            for (int r = 14; r >= 2; r--) if (counts[r] > 0) present[distinct++] = r;

            int straightHigh = 0;
            if (distinct == 5)
            {
                if (present[0] - present[4] == 4) straightHigh = present[0];                       // normal run of 5
                else if (present[0] == 14 && present[1] == 5 && present[4] == 2) straightHigh = 5; // A-5-4-3-2 wheel (Ace low)
            }
            bool straight = straightHigh > 0;

            // Tie-break key in group order (count desc, then rank desc), plus the two largest group sizes.
            Span<int> key = stackalloc int[5];
            int ki = 0, topCount = 0, secondCount = 0;
            for (int cnt = 4; cnt >= 1; cnt--)
                for (int r = 14; r >= 2; r--)
                    if (counts[r] == cnt)
                    {
                        if (topCount == 0) topCount = cnt;
                        else if (secondCount == 0) secondCount = cnt;
                        for (int t = 0; t < cnt; t++) key[ki++] = r;
                    }

            FiveCardCategory cat;
            bool royal = false;
            if (straight && flush) { cat = FiveCardCategory.StraightFlush; royal = straightHigh == 14; }
            else if (topCount == 4) cat = FiveCardCategory.FourOfAKind;
            else if (topCount == 3 && secondCount == 2) cat = FiveCardCategory.FullHouse;
            else if (flush) cat = FiveCardCategory.Flush;                 // Flush > Straight (standard 5-card)
            else if (straight) cat = FiveCardCategory.Straight;
            else if (topCount == 3) cat = FiveCardCategory.ThreeOfAKind;
            else if (topCount == 2 && secondCount == 2) cat = FiveCardCategory.TwoPair;
            else if (topCount == 2) cat = FiveCardCategory.Pair;
            else cat = FiveCardCategory.HighCard;

            // For straights (incl. straight flush) the sequence high card is the tie-break; wheel counts as 5-high.
            if (straight)
            {
                if (straightHigh == 5) { key[0] = 5; key[1] = 4; key[2] = 3; key[3] = 2; key[4] = 1; }
                else for (int t = 0; t < 5; t++) key[t] = straightHigh - t;
            }

            return new FiveCardHandRank(cat, royal, key[0], key[1], key[2], key[3], key[4]);
        }

        /// <summary>Best 5-card hand out of 6 cards — evaluates the C(6,5)=6 subsets and returns the strongest.</summary>
        public static FiveCardHandRank BestOfSix(IReadOnlyList<Card> six)
        {
            if (six == null || six.Count != 6)
                throw new ArgumentException("BestOfSix needs exactly 6 cards.", nameof(six));

            var buf = new Card[5];
            FiveCardHandRank best = default;
            for (int drop = 0; drop < 6; drop++)
            {
                int idx = 0;
                for (int i = 0; i < 6; i++) if (i != drop) buf[idx++] = six[i];
                var rank = Evaluate(buf);
                if (drop == 0 || rank.CompareTo(best) > 0) best = rank;
            }
            return best;
        }
    }
}
