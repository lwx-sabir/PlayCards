using System;
using System.Collections.Generic;
using CardGames.Platforms;

namespace CardGames.VideoPoker
{
    /// <summary>
    /// Video-poker 5-card categories — <b>Flush &gt; Straight</b> (normal 5-card ranking). Royal Flush is its OWN top
    /// category (video poker pays it separately). Wild variants add <b>Five of a Kind</b> and <b>Wild Royal</b> (an
    /// A-K-Q-J-10 flush made WITH a wild, which pays less than a natural royal). Values are a canonical high-to-low
    /// rank used to pick the best assignment of wilds; the actual PAYOUT is a per-variant paytable lookup.
    /// </summary>
    public enum VideoPokerCategory
    {
        HighCard = 0, Pair = 1, TwoPair = 2, ThreeOfAKind = 3, Straight = 4,
        Flush = 5, FullHouse = 6, FourOfAKind = 7, StraightFlush = 8,
        FiveOfAKind = 9, WildRoyal = 10, RoyalFlush = 11,
    }

    /// <summary>
    /// An evaluated 5-card hand: <see cref="Category"/> + <see cref="PrimaryRank"/> (the main group's rank — the pair
    /// for a One Pair, so a paytable can gate "Jacks or Better"; the quad/five rank otherwise), <see cref="Kicker"/>
    /// (the odd 5th card on quads — the bonus-family tier), and <see cref="WildCount"/> (wilds in the hand — the
    /// Deuces-Wild "Four Deuces" bonus is <c>WildCount == 4</c>). A descending tie-break key keeps comparisons exact.
    /// </summary>
    public readonly struct VideoPokerHandRank : IComparable<VideoPokerHandRank>
    {
        public VideoPokerCategory Category { get; }
        public int PrimaryRank { get; }
        public int Kicker { get; }
        public int WildCount { get; }
        private readonly int _k0, _k1, _k2, _k3, _k4;

        public VideoPokerHandRank(VideoPokerCategory cat, int primary, int kicker, int wildCount, int k0, int k1, int k2, int k3, int k4)
        { Category = cat; PrimaryRank = primary; Kicker = kicker; WildCount = wildCount; _k0 = k0; _k1 = k1; _k2 = k2; _k3 = k3; _k4 = k4; }

        public int CompareTo(VideoPokerHandRank o)
        {
            if (Category != o.Category) return Category.CompareTo(o.Category);
            if (_k0 != o._k0) return _k0.CompareTo(o._k0);
            if (_k1 != o._k1) return _k1.CompareTo(o._k1);
            if (_k2 != o._k2) return _k2.CompareTo(o._k2);
            if (_k3 != o._k3) return _k3.CompareTo(o._k3);
            return _k4.CompareTo(o._k4);
        }

        public override string ToString() => Category + (PrimaryRank > 0 ? $"({PrimaryRank})" : "") + (WildCount > 0 ? $"[w{WildCount}]" : "");
    }

    /// <summary>
    /// Pure 5-card evaluator for video poker (copied from the 3CP FiveCardEvaluator — plug-and-play, references no
    /// other game). <see cref="Evaluate"/> is the natural (no-wild) path. <see cref="EvaluateWild"/> handles wild
    /// variants by BRUTE FORCE — it tries every substitution of the wild cards and returns the best hand, so it is
    /// correct by construction (no fragile "complete the category" logic). Runtime cost is fine (one hand per draw;
    /// ≤3 wilds ⇒ ≤52³ tries, all stack-allocated); the 4-wild case (Deuces "four deuces") is short-circuited.
    /// </summary>
    public static class VideoPokerEvaluator
    {
        /// <summary>Natural (no-wild) evaluation of 5 distinct cards.</summary>
        public static VideoPokerHandRank Evaluate(IReadOnlyList<Card> cards)
        {
            if (cards == null || cards.Count != 5)
                throw new ArgumentException("Video-poker evaluation needs exactly 5 cards.", nameof(cards));

            Span<int> ranks = stackalloc int[5];
            Span<int> suits = stackalloc int[5];
            for (int i = 0; i < 5; i++) { ranks[i] = (int)cards[i].FaceVal; suits[i] = (int)cards[i].Suit; }
            return Rank5(ranks, suits, 0);
        }

        /// <summary>
        /// Wild-variant evaluation. <paramref name="isWild"/> flags the wild cards (e.g. Deuces = <c>FaceVal == Two</c>).
        /// Returns the best hand achievable by substituting the wilds, with <see cref="VideoPokerHandRank.WildCount"/>
        /// set and any wild-made royal reported as <see cref="VideoPokerCategory.WildRoyal"/> (not natural Royal).
        /// </summary>
        public static VideoPokerHandRank EvaluateWild(IReadOnlyList<Card> cards, Func<Card, bool> isWild)
        {
            if (cards == null || cards.Count != 5)
                throw new ArgumentException("Video-poker evaluation needs exactly 5 cards.", nameof(cards));
            if (isWild == null) return Evaluate(cards);

            var natR = new List<int>(5);
            var natS = new List<int>(5);
            for (int i = 0; i < 5; i++)
                if (!isWild(cards[i])) { natR.Add((int)cards[i].FaceVal); natS.Add((int)cards[i].Suit); }
            int w = 5 - natR.Count;
            if (w == 0) return Evaluate(cards);
            if (w >= 4)   // 4 wilds + ≤1 natural = five of a kind of the 5th card's rank (Deuces "Four Deuces", paid via WildCount).
            {
                int r = natR.Count > 0 ? natR[0] : 14;
                return new VideoPokerHandRank(VideoPokerCategory.FiveOfAKind, r, 0, w, r, r, r, r, r);
            }

            var r5 = new int[5];
            var s5 = new int[5];
            for (int i = 0; i < natR.Count; i++) { r5[i] = natR[i]; s5[i] = natS[i]; }

            var best = default(VideoPokerHandRank);
            bool have = false;

            void Recurse(int slot)
            {
                if (slot == 5)
                {
                    var rank = Rank5(r5, s5, w);
                    if (!have || rank.CompareTo(best) > 0) { best = rank; have = true; }
                    return;
                }
                for (int rr = 2; rr <= 14; rr++)
                    for (int ss = 0; ss < 4; ss++)
                    {
                        r5[slot] = rr; s5[slot] = ss;
                        Recurse(slot + 1);
                    }
            }
            Recurse(natR.Count);
            return best;
        }

        // ---- core: rank a concrete 5-card hand from its (rank, suit) values; handles counts up to 5 (five of a kind). ----
        private static VideoPokerHandRank Rank5(ReadOnlySpan<int> r, ReadOnlySpan<int> s, int wildCount)
        {
            Span<int> counts = stackalloc int[15];   // rank 2..14
            for (int i = 0; i < 5; i++) counts[r[i]]++;

            bool flush = true;
            for (int i = 1; i < 5; i++) if (s[i] != s[0]) { flush = false; break; }

            Span<int> present = stackalloc int[5];
            int distinct = 0;
            for (int rk = 14; rk >= 2; rk--) if (counts[rk] > 0) present[distinct++] = rk;

            int straightHigh = 0;
            if (distinct == 5)
            {
                if (present[0] - present[4] == 4) straightHigh = present[0];                        // normal run
                else if (present[0] == 14 && present[1] == 5 && present[4] == 2) straightHigh = 5;  // A-2-3-4-5 wheel
            }
            bool straight = straightHigh > 0;

            Span<int> key = stackalloc int[5];
            int ki = 0, topCount = 0, secondCount = 0, primaryRank = 0;
            for (int cnt = 5; cnt >= 1; cnt--)
                for (int rk = 14; rk >= 2; rk--)
                    if (counts[rk] == cnt)
                    {
                        if (topCount == 0) { topCount = cnt; primaryRank = rk; }
                        else if (secondCount == 0) secondCount = cnt;
                        for (int t = 0; t < cnt; t++) key[ki++] = rk;
                    }

            int kicker = 0;
            if (topCount == 4) for (int rk = 14; rk >= 2; rk--) if (counts[rk] == 1) { kicker = rk; break; }

            bool wild = wildCount > 0;
            VideoPokerCategory cat;
            if (topCount == 5) cat = VideoPokerCategory.FiveOfAKind;
            else if (straight && flush) cat = straightHigh == 14 ? (wild ? VideoPokerCategory.WildRoyal : VideoPokerCategory.RoyalFlush) : VideoPokerCategory.StraightFlush;
            else if (topCount == 4) cat = VideoPokerCategory.FourOfAKind;
            else if (topCount == 3 && secondCount == 2) cat = VideoPokerCategory.FullHouse;
            else if (flush) cat = VideoPokerCategory.Flush;
            else if (straight) cat = VideoPokerCategory.Straight;
            else if (topCount == 3) cat = VideoPokerCategory.ThreeOfAKind;
            else if (topCount == 2 && secondCount == 2) cat = VideoPokerCategory.TwoPair;
            else if (topCount == 2) cat = VideoPokerCategory.Pair;
            else cat = VideoPokerCategory.HighCard;

            if (straight)
            {
                if (straightHigh == 5) { key[0] = 5; key[1] = 4; key[2] = 3; key[3] = 2; key[4] = 1; }
                else for (int t = 0; t < 5; t++) key[t] = straightHigh - t;
            }

            int primary = topCount >= 2 ? primaryRank : 0;
            return new VideoPokerHandRank(cat, primary, kicker, wildCount, key[0], key[1], key[2], key[3], key[4]);
        }
    }
}
