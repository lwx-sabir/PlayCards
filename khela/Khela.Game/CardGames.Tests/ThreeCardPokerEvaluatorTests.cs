using CardGames.Platforms;
using CardGames.ThreeCardPoker;
using Xunit;

namespace CardGames.Tests
{
    /// <summary>
    /// Locks the 3-card evaluator: category order (STRAIGHT &gt; FLUSH), the Ace-low A-2-3 wheel, tie-breaks,
    /// and that suits never break ties — the rules most likely to be mis-coded.
    /// </summary>
    public class ThreeCardPokerEvaluatorTests
    {
        private static Card C(FaceValue f, Suit s = Suit.Spades) => new Card(s, f, true);
        private static ThreeCardHandRank R(Card a, Card b, Card c) => ThreeCardEvaluator.Evaluate(a, b, c);

        // ---- category detection ----
        [Fact] public void StraightFlush_Detected() =>
            Assert.Equal(ThreeCardCategory.StraightFlush, R(C(FaceValue.Nine), C(FaceValue.Ten), C(FaceValue.Jack)).Category);

        [Fact] public void Trips_Detected() =>
            Assert.Equal(ThreeCardCategory.ThreeOfAKind,
                R(C(FaceValue.Seven, Suit.Spades), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs)).Category);

        [Fact] public void Straight_Detected() =>
            Assert.Equal(ThreeCardCategory.Straight,
                R(C(FaceValue.Nine, Suit.Spades), C(FaceValue.Ten, Suit.Hearts), C(FaceValue.Jack, Suit.Clubs)).Category);

        [Fact] public void Flush_Detected() =>
            Assert.Equal(ThreeCardCategory.Flush, R(C(FaceValue.Two), C(FaceValue.Six), C(FaceValue.Ten)).Category); // all spades, no run

        [Fact] public void Pair_Detected() =>
            Assert.Equal(ThreeCardCategory.Pair,
                R(C(FaceValue.Five, Suit.Spades), C(FaceValue.Five, Suit.Hearts), C(FaceValue.King, Suit.Clubs)).Category);

        [Fact] public void HighCard_Detected() =>
            Assert.Equal(ThreeCardCategory.HighCard,
                R(C(FaceValue.Two, Suit.Spades), C(FaceValue.Six, Suit.Hearts), C(FaceValue.Ten, Suit.Clubs)).Category);

        // ---- the 3-card quirk: straight beats flush ----
        [Fact]
        public void Straight_Beats_Flush()
        {
            var straight = R(C(FaceValue.Nine, Suit.Spades), C(FaceValue.Ten, Suit.Hearts), C(FaceValue.Jack, Suit.Clubs));
            var flush = R(C(FaceValue.Two), C(FaceValue.Six), C(FaceValue.King)); // all spades
            Assert.True(straight.CompareTo(flush) > 0);
        }

        // ---- full category ladder ----
        [Fact]
        public void CategoryLadder_Ordered()
        {
            var sf   = R(C(FaceValue.Nine), C(FaceValue.Ten), C(FaceValue.Jack));                                          // spades run
            var trip = R(C(FaceValue.Seven, Suit.Spades), C(FaceValue.Seven, Suit.Hearts), C(FaceValue.Seven, Suit.Clubs));
            var str  = R(C(FaceValue.Nine, Suit.Spades), C(FaceValue.Ten, Suit.Hearts), C(FaceValue.Jack, Suit.Clubs));
            var fl   = R(C(FaceValue.Two), C(FaceValue.Six), C(FaceValue.King));                                           // spades
            var pr   = R(C(FaceValue.Five, Suit.Spades), C(FaceValue.Five, Suit.Hearts), C(FaceValue.King, Suit.Clubs));
            var hi   = R(C(FaceValue.Two, Suit.Spades), C(FaceValue.Six, Suit.Hearts), C(FaceValue.King, Suit.Clubs));

            Assert.True(sf.CompareTo(trip) > 0);
            Assert.True(trip.CompareTo(str) > 0);
            Assert.True(str.CompareTo(fl) > 0);   // straight > flush
            Assert.True(fl.CompareTo(pr) > 0);
            Assert.True(pr.CompareTo(hi) > 0);
        }

        // ---- Ace-low wheel ----
        [Fact]
        public void Wheel_A23_IsAStraight() =>
            Assert.Equal(ThreeCardCategory.Straight,
                R(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Two, Suit.Hearts), C(FaceValue.Three, Suit.Clubs)).Category);

        [Fact]
        public void Wheel_IsLowestStraight_LosesTo_432()
        {
            var wheel = R(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Two, Suit.Hearts), C(FaceValue.Three, Suit.Clubs));
            var four32 = R(C(FaceValue.Four, Suit.Spades), C(FaceValue.Three, Suit.Hearts), C(FaceValue.Two, Suit.Clubs));
            Assert.True(wheel.CompareTo(four32) < 0);   // A-2-3 is the LOWEST straight
        }

        [Fact]
        public void AKQ_IsHighestStraight()
        {
            var akq = R(C(FaceValue.Ace, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.Queen, Suit.Clubs));
            var kqj = R(C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Hearts), C(FaceValue.Jack, Suit.Clubs));
            Assert.True(akq.CompareTo(kqj) > 0);
            Assert.Equal(14, akq.HighCard);
        }

        // ---- mini royal (top line) ----
        [Fact]
        public void MiniRoyal_Is_AKQ_Suited_And_TopStraightFlush()
        {
            var mini = R(C(FaceValue.Ace), C(FaceValue.King), C(FaceValue.Queen));        // all spades
            var kqjSuited = R(C(FaceValue.King), C(FaceValue.Queen), C(FaceValue.Jack));  // all spades
            Assert.True(mini.IsMiniRoyal);
            Assert.False(kqjSuited.IsMiniRoyal);
            Assert.True(mini.CompareTo(kqjSuited) > 0);
        }

        [Fact]
        public void WheelSuited_IsLowestStraightFlush()
        {
            var wheelSf = R(C(FaceValue.Ace), C(FaceValue.Two), C(FaceValue.Three));  // all spades
            var trip = R(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.Ace, Suit.Clubs));
            var otherSf = R(C(FaceValue.Two), C(FaceValue.Three), C(FaceValue.Four)); // all spades
            Assert.Equal(ThreeCardCategory.StraightFlush, wheelSf.Category);
            Assert.False(wheelSf.IsMiniRoyal);
            Assert.True(wheelSf.CompareTo(trip) > 0);      // still an SF → beats trips
            Assert.True(wheelSf.CompareTo(otherSf) < 0);   // but the lowest SF
        }

        // ---- pair tie-breaks ----
        [Fact]
        public void PairRank_Dominates_Kicker()
        {
            var aces2 = R(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.Two, Suit.Clubs));
            var kingsQ = R(C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.Queen, Suit.Clubs));
            Assert.True(aces2.CompareTo(kingsQ) > 0);   // pair of aces (low kicker) beats pair of kings (high kicker)
        }

        [Fact]
        public void EqualPair_BrokenBy_Kicker()
        {
            var fivesK = R(C(FaceValue.Five, Suit.Spades), C(FaceValue.Five, Suit.Hearts), C(FaceValue.King, Suit.Clubs));
            var fivesQ = R(C(FaceValue.Five, Suit.Spades), C(FaceValue.Five, Suit.Hearts), C(FaceValue.Queen, Suit.Clubs));
            Assert.True(fivesK.CompareTo(fivesQ) > 0);
        }

        // ---- trips + high card ordering ----
        [Fact]
        public void Trips_Ordered_ByRank()
        {
            var aaa = R(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Ace, Suit.Hearts), C(FaceValue.Ace, Suit.Clubs));
            var kkk = R(C(FaceValue.King, Suit.Spades), C(FaceValue.King, Suit.Hearts), C(FaceValue.King, Suit.Clubs));
            Assert.True(aaa.CompareTo(kkk) > 0);
        }

        [Fact]
        public void HighCard_AceIsHigh()
        {
            var aceHi = R(C(FaceValue.Ace, Suit.Spades), C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Five, Suit.Clubs));
            var kingHi = R(C(FaceValue.King, Suit.Spades), C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Five, Suit.Clubs));
            Assert.True(aceHi.CompareTo(kingHi) > 0);
        }

        // ---- push: suits never break ties ----
        [Fact]
        public void IdenticalRanks_DifferentSuits_Tie()
        {
            var a = R(C(FaceValue.King, Suit.Spades), C(FaceValue.Queen, Suit.Hearts), C(FaceValue.Nine, Suit.Clubs));
            var b = R(C(FaceValue.King, Suit.Hearts), C(FaceValue.Queen, Suit.Clubs), C(FaceValue.Nine, Suit.Diamonds));
            Assert.Equal(0, a.CompareTo(b));   // card-by-card tie → a push; suit never breaks it
        }
    }
}
