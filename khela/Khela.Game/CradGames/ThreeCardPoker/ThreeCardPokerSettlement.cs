using System;
using System.Collections.Generic;
using CardGames.Platforms;

namespace CardGames.ThreeCardPoker
{
    /// <summary>The wagers a seat places pre-deal. Play is NOT here — it is always exactly the Ante and is only
    /// posted when the seat chooses PLAY (passed as <c>played</c> to <see cref="ThreeCardPokerSettlement.Settle"/>).</summary>
    public sealed class ThreeCardPokerBets
    {
        public decimal Ante { get; init; }
        public decimal PairPlus { get; init; }   // 0 = not placed
        public decimal Prime { get; init; }      // 0 = not placed
        public decimal SixCard { get; init; }    // 0 = not placed (the "3+3" / 6-Card Bonus)
    }

    /// <summary>
    /// Pure Three Card Poker settlement (no wallet/table/Redis). Given a seat's 3 cards, the dealer's 3, the bets,
    /// and whether the seat PLAYED (vs folded), returns the GROSS return of each bet circle — stake included where
    /// it is returned; <c>net = gross − staked</c>. Straight-beats-flush + the Ace-low wheel come from
    /// <see cref="ThreeCardEvaluator"/>. Every payout is a deterministic paytable lookup with no cross-seat coupling.
    /// </summary>
    public sealed class ThreeCardPokerSettlement
    {
        public decimal AnteReturn { get; private set; }
        public decimal PlayReturn { get; private set; }
        public decimal AnteBonus { get; private set; }        // additive winnings (no stake of its own)
        public decimal PairPlusReturn { get; private set; }
        public decimal PrimeReturn { get; private set; }
        public decimal SixCardReturn { get; private set; }
        public bool DealerQualified { get; private set; }
        public string Outcome { get; private set; } = "";     // win / lose / push / no_qualify / fold

        /// <summary>Total returned to the seat across every circle.</summary>
        public decimal TotalReturn => AnteReturn + PlayReturn + AnteBonus + PairPlusReturn + PrimeReturn + SixCardReturn;

        /// <summary>Dealer qualifies on Queen-high or better (any pair+ qualifies; a bare high card needs a Queen).</summary>
        public static bool DealerQualifies(ThreeCardHandRank dealer)
            => dealer.Category > ThreeCardCategory.HighCard
               || (dealer.Category == ThreeCardCategory.HighCard && dealer.HighCard >= (int)FaceValue.Queen);

        public static ThreeCardPokerSettlement Settle(
            IReadOnlyList<Card> playerCards, IReadOnlyList<Card> dealerCards,
            ThreeCardPokerBets bets, bool played, ThreeCardPokerPaytables pt = null)
        {
            if (bets == null) throw new ArgumentNullException(nameof(bets));
            pt ??= ThreeCardPokerPaytables.Default;

            var p = ThreeCardEvaluator.Evaluate(playerCards);
            var d = ThreeCardEvaluator.Evaluate(dealerCards);
            var r = new ThreeCardPokerSettlement { DealerQualified = DealerQualifies(d) };

            if (!played)
            {
                r.Outcome = "fold";                     // Ante forfeited, no Play posted, NO Ante Bonus
            }
            else
            {
                int cmp = p.CompareTo(d);
                if (!r.DealerQualified)
                {
                    r.AnteReturn = 2m * bets.Ante;       // Ante pays 1:1
                    r.PlayReturn = 1m * bets.Ante;       // Play pushes (Play stake == Ante)
                    r.Outcome = "no_qualify";
                }
                else if (cmp > 0)
                {
                    r.AnteReturn = 2m * bets.Ante;        // both pay 1:1
                    r.PlayReturn = 2m * bets.Ante;
                    r.Outcome = "win";
                }
                else if (cmp == 0)
                {
                    r.AnteReturn = 1m * bets.Ante;        // both push
                    r.PlayReturn = 1m * bets.Ante;
                    r.Outcome = "push";
                }
                else
                {
                    r.Outcome = "lose";                   // both lose (returns stay 0)
                }

                // Ante Bonus: only because the hand Played, on a straight+, INDEPENDENT of the showdown (even on a loss).
                r.AnteBonus = AnteBonusReturn(p, bets.Ante, pt);
            }

            // Side bets resolve independently and pay EVEN ON A FOLD (the seat's cards are still revealed).
            if (bets.PairPlus > 0m) r.PairPlusReturn = ComputePairPlus(p, bets.PairPlus, pt);
            if (bets.Prime > 0m) r.PrimeReturn = ComputePrime(playerCards, dealerCards, bets.Prime, pt);
            if (bets.SixCard > 0m) r.SixCardReturn = ComputeSixCard(playerCards, dealerCards, bets.SixCard, pt);

            return r;
        }

        // Ante Bonus is a bonus on the Ante (winnings only): Straight 1:1, Trips 4:1, Straight Flush 5:1.
        private static decimal AnteBonusReturn(ThreeCardHandRank p, decimal ante, ThreeCardPokerPaytables pt)
            => p.Category switch
            {
                ThreeCardCategory.StraightFlush => ante * pt.AnteBonusStraightFlush,
                ThreeCardCategory.ThreeOfAKind  => ante * pt.AnteBonusTrips,
                ThreeCardCategory.Straight      => ante * pt.AnteBonusStraight,
                _ => 0m,
            };

        // Pair Plus: "to 1" odds → gross = stake × (odds + 1) on a win, 0 on a loss (high card).
        private static decimal ComputePairPlus(ThreeCardHandRank p, decimal stake, ThreeCardPokerPaytables pt)
        {
            int odds = p.Category switch
            {
                ThreeCardCategory.StraightFlush => (p.IsMiniRoyal && pt.PairPlusMiniRoyal > 0) ? pt.PairPlusMiniRoyal : pt.PairPlusStraightFlush,
                ThreeCardCategory.ThreeOfAKind  => pt.PairPlusTrips,
                ThreeCardCategory.Straight      => pt.PairPlusStraight,
                ThreeCardCategory.Flush         => pt.PairPlusFlush,
                ThreeCardCategory.Pair          => pt.PairPlusPair,
                _ => -1,   // high card loses
            };
            return odds < 0 ? 0m : stake * (odds + 1);
        }

        // Prime: all 6 same colour pays the higher rate; else 3 player cards same colour; else lose.
        private static decimal ComputePrime(IReadOnlyList<Card> player, IReadOnlyList<Card> dealer, decimal stake, ThreeCardPokerPaytables pt)
        {
            bool playerSame = AllSameColour(player);
            bool allSix = playerSame && AllSameColour(dealer) && IsRed(player[0].Suit) == IsRed(dealer[0].Suit);
            if (allSix) return stake * (pt.PrimeSixSameColour + 1);
            if (playerSame) return stake * (pt.PrimeThreeSameColour + 1);
            return 0m;
        }

        // 6-Card Bonus / "3+3": best 5-card hand from the player's 3 + dealer's 3 (standard ranking, flush > straight);
        // pays on three-of-a-kind or better, independent of fold/showdown.
        private static decimal ComputeSixCard(IReadOnlyList<Card> player, IReadOnlyList<Card> dealer, decimal stake, ThreeCardPokerPaytables pt)
        {
            var six = new List<Card>(6);
            six.AddRange(player);
            six.AddRange(dealer);
            var best = FiveCardEvaluator.BestOfSix(six);
            int odds = best.Category switch
            {
                FiveCardCategory.StraightFlush => best.IsRoyal ? pt.SixCardRoyalFlush : pt.SixCardStraightFlush,
                FiveCardCategory.FourOfAKind   => pt.SixCardFourOfAKind,
                FiveCardCategory.FullHouse     => pt.SixCardFullHouse,
                FiveCardCategory.Flush         => pt.SixCardFlush,
                FiveCardCategory.Straight      => pt.SixCardStraight,
                FiveCardCategory.ThreeOfAKind  => pt.SixCardThreeOfAKind,
                _ => -1,   // two pair, pair, high card = lose
            };
            return odds < 0 ? 0m : stake * (odds + 1);
        }

        private static bool AllSameColour(IReadOnlyList<Card> cards)
        {
            bool red = IsRed(cards[0].Suit);
            for (int i = 1; i < cards.Count; i++)
                if (IsRed(cards[i].Suit) != red) return false;
            return true;
        }

        private static bool IsRed(Suit s) => s == Suit.Diamonds || s == Suit.Hearts;
    }
}
