using System.Collections.Generic;

namespace CardGames.Blackjack.CardGames.Blackjack
{
    /// <summary>Per-hand outcome of a settled round — one per played hand (a split yields two). Lets the
    /// table layer write a per-hand audit trail without re-deriving the settlement decision.</summary>
    public sealed class HandSettlement
    {
        public int SeatNumber { get; set; }
        public int HandIndex { get; set; }
        public decimal Bet { get; set; }            // the hand's stake, captured before settle zeroed it
        public decimal InsuranceBet { get; set; }
        public int FinalValue { get; set; }
        public bool Bust { get; set; }
        public bool Blackjack { get; set; }          // natural (2-card 21 on an unsplit hand)
        public string Outcome { get; set; }          // "win" | "lose" | "push"
        public decimal Payout { get; set; }          // gross returned to the mirror for THIS hand, incl. insurance
    }

    /// <summary>
    /// Pure blackjack settlement: given a played-out game (dealer has acted), decides each in-round
    /// hand's outcome and applies the payout to the player's mirror balance. Extracted from
    /// BlackjackTableManager so the win/loss/push/3:2/insurance decision logic is unit-testable
    /// without Redis/EF/SignalR. The manager reconciles the resulting mirror delta to the wallet.
    ///
    /// Payout convention (total-return multiplier): even-money win returns 2x the stake, a 3:2 natural
    /// returns 2.5x, a push returns 1x (the stake), insurance returns 3x (2:1 plus the stake), a loss 0.
    ///
    /// Returns the per-hand outcomes (decided here) so the caller can audit each split hand independently.
    /// </summary>
    public static class BlackjackSettlement
    {
        public static List<HandSettlement> Settle(BlackJackGame game)
        {
            var results = new List<HandSettlement>();
            var dealerTotal = game.Dealer.Hand.GetSumOfHand();
            var dealerBust = dealerTotal > 21;
            var dealerBlackjack = dealerTotal == 21 && game.Dealer.Hand.Cards.Count == 2;

            foreach (var player in game.Players)
            {
                if (!player.InRound) continue; // waiting players didn't play this round

                for (int i = 0; i < player.Hands.Count; i++)
                {
                    var handState = player.Hands[i];
                    var playerTotal = handState.Hand.GetSumOfHand();
                    var bet = handState.Bet;                 // capture before AddWin/AddPush/AddLoss zero it
                    var insBet = handState.InsuranceBet;
                    var playerNatural = player.Hands.Count == 1 && handState.Hand.Cards.Count == 2 && playerTotal == 21;
                    decimal insurancePayout = 0m;

                    if (handState.InsuranceBet > 0)
                    {
                        if (dealerBlackjack)
                        {
                            player.AddInsurancePayout(handState.InsuranceBet);
                            insurancePayout = handState.InsuranceBet * 3m; // total return (stake + 2:1)
                        }
                        handState.InsuranceBet = 0;
                    }

                    string outcome;
                    decimal payout; // gross the settlement adds to the mirror for this hand (excl. insurance)

                    if (playerTotal > 21)
                    {
                        player.AddLoss(i);
                        outcome = "lose"; payout = 0m;
                    }
                    else if (playerNatural)
                    {
                        // A natural blackjack is a 2-card 21 on an unsplit hand; it pays 3:2.
                        if (dealerBlackjack) { player.AddPush(i); outcome = "push"; payout = bet; }     // both naturals -> push
                        else { player.AddWin(2.5m, i); outcome = "win"; payout = bet * 2.5m; }          // 3:2 (returns 2.5x)
                    }
                    else if (dealerBlackjack)                          // dealer natural beats any non-natural hand
                    {
                        player.AddLoss(i);
                        outcome = "lose"; payout = 0m;
                    }
                    else if (dealerBust || playerTotal > dealerTotal)
                    {
                        player.AddWin(2, i);
                        outcome = "win"; payout = bet * 2m;
                    }
                    else if (playerTotal == dealerTotal)
                    {
                        player.AddPush(i);
                        outcome = "push"; payout = bet;
                    }
                    else
                    {
                        player.AddLoss(i);
                        outcome = "lose"; payout = 0m;
                    }

                    results.Add(new HandSettlement
                    {
                        SeatNumber = player.SeatNumber,
                        HandIndex = i,
                        Bet = bet,
                        InsuranceBet = insBet,
                        FinalValue = playerTotal,
                        Bust = playerTotal > 21,
                        Blackjack = playerNatural,
                        Outcome = outcome,
                        Payout = payout + insurancePayout
                    });
                }
            }

            return results;
        }
    }
}
