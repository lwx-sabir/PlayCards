using System;
using System.Collections.Generic;
using CardGames.Platforms;

namespace CardGames.ThreeCardPoker
{
    /// <summary>
    /// 3-card poker hand categories, ordered so a higher value beats a lower one.
    /// NOTE the 3-card quirk: a <b>Straight outranks a Flush</b> — with three cards a straight (720 combos)
    /// is rarer than a flush (1096), the inverse of 5-card poker. Do NOT reuse a 5-card ordering here.
    /// </summary>
    public enum ThreeCardCategory
    {
        HighCard = 0,
        Pair = 1,
        Flush = 2,
        Straight = 3,
        ThreeOfAKind = 4,
        StraightFlush = 5,
    }

    /// <summary>
    /// Evaluated rank of a 3-card hand: its <see cref="Category"/> plus a descending tie-break key.
    /// Comparable — <c>a.CompareTo(b) &gt; 0</c> means a beats b, <c>== 0</c> is a card-by-card tie (a push in
    /// the base game). The Ace is HIGH everywhere (value 14, above the King's 13) EXCEPT the A-2-3 "wheel",
    /// which is the LOWEST straight (its high card is the 3); that exception is baked into the tie-break key.
    /// Pure and engine-only — no wallet/table/paytable knowledge.
    /// </summary>
    public readonly struct ThreeCardHandRank : IComparable<ThreeCardHandRank>
    {
        public ThreeCardCategory Category { get; }
        private readonly int _k0, _k1, _k2;   // descending tie-break ranks

        public ThreeCardHandRank(ThreeCardCategory category, int k0, int k1, int k2)
        {
            Category = category; _k0 = k0; _k1 = k1; _k2 = k2;
        }

        /// <summary>The three ordered tie-break ranks (primary first). Exposed for paytable/inspection use.</summary>
        public (int, int, int) Key => (_k0, _k1, _k2);

        /// <summary>The hand's high card for tie-break purposes (3 for the A-2-3 wheel, 14 for ace-high hands).</summary>
        public int HighCard => _k0;

        /// <summary>True for a suited A-K-Q straight flush — the "mini royal" top-line hand.</summary>
        public bool IsMiniRoyal => Category == ThreeCardCategory.StraightFlush && _k0 == 14 && _k1 == 13 && _k2 == 12;

        public int CompareTo(ThreeCardHandRank other)
        {
            if (Category != other.Category) return Category.CompareTo(other.Category);
            if (_k0 != other._k0) return _k0.CompareTo(other._k0);
            if (_k1 != other._k1) return _k1.CompareTo(other._k1);
            return _k2.CompareTo(other._k2);
        }

        public override string ToString() => $"{Category}[{_k0},{_k1},{_k2}]";
    }

    /// <summary>Evaluates exactly 3 cards into a comparable <see cref="ThreeCardHandRank"/>.</summary>
    public static class ThreeCardEvaluator
    {
        public static ThreeCardHandRank Evaluate(Card a, Card b, Card c)
            => Evaluate(new[] { a, b, c });

        public static ThreeCardHandRank Evaluate(IReadOnlyList<Card> cards)
        {
            if (cards == null || cards.Count != 3)
                throw new ArgumentException("Three Card Poker needs exactly 3 cards.", nameof(cards));

            // Face values as ints (Two=2 … King=13, Ace=14), sorted descending.
            int r0 = (int)cards[0].FaceVal, r1 = (int)cards[1].FaceVal, r2 = (int)cards[2].FaceVal;
            Sort3Desc(ref r0, ref r1, ref r2);

            bool flush = cards[0].Suit == cards[1].Suit && cards[1].Suit == cards[2].Suit;
            bool trips = r0 == r1 && r1 == r2;

            // Straight detection: a run of 3 consecutive ranks (covers A-K-Q down to 4-3-2), PLUS the A-2-3
            // wheel where the Ace plays LOW. Everything else is not a straight.
            bool normalStraight = !trips && r0 - r1 == 1 && r1 - r2 == 1;
            bool wheel = r0 == 14 && r1 == 3 && r2 == 2;   // A-2-3
            bool straight = normalStraight || wheel;

            // Category — remember: Straight (3) ranks ABOVE Flush (2).
            ThreeCardCategory cat;
            if (straight && flush) cat = ThreeCardCategory.StraightFlush;
            else if (trips)        cat = ThreeCardCategory.ThreeOfAKind;
            else if (straight)     cat = ThreeCardCategory.Straight;
            else if (flush)        cat = ThreeCardCategory.Flush;
            else if (r0 == r1 || r1 == r2) cat = ThreeCardCategory.Pair;   // sorted desc → a pair is adjacent
            else                   cat = ThreeCardCategory.HighCard;

            // Tie-break key.
            int k0, k1, k2;
            if (wheel)
            {
                // Ace LOW: the straight's high card is the 3 → key [3,2,1], below every other straight/SF.
                k0 = 3; k1 = 2; k2 = 1;
            }
            else if (cat == ThreeCardCategory.Pair)
            {
                // Pair rank first, kicker last — so pair strength dominates the kicker in comparisons.
                if (r0 == r1) { k0 = r0; k1 = r1; k2 = r2; }   // pair is the top two, kicker is r2
                else          { k0 = r1; k1 = r2; k2 = r0; }   // pair is the bottom two, kicker is r0
            }
            else
            {
                // Straights / flush / trips / high card: plain descending, Ace high.
                k0 = r0; k1 = r1; k2 = r2;
            }

            return new ThreeCardHandRank(cat, k0, k1, k2);
        }

        private static void Sort3Desc(ref int a, ref int b, ref int c)
        {
            if (a < b) (a, b) = (b, a);
            if (b < c) (b, c) = (c, b);
            if (a < b) (a, b) = (b, a);
        }
    }
}
