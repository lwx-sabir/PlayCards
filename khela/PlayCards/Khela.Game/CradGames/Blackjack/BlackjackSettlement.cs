using System;

namespace CardGames.Blackjack.CardGames.Blackjack
{
    /// <summary>
    /// Pure blackjack settlement: given a played-out game (dealer has acted), decides each in-round
    /// hand's outcome and applies the payout to the player's mirror balance. Extracted from
    /// BlackjackTableManager so the win/loss/push/3:2/insurance decision logic is unit-testable
    /// without Redis/EF/SignalR. The manager reconciles the resulting mirror delta to the wallet.
    ///
    /// Payout convention (total-return multiplier): even-money win returns 2x the stake, a 3:2 natural
    /// returns 2.5x, a push returns 1x (the stake), insurance returns 3x (2:1 plus the stake), a loss 0.
    /// </summary>
    public static class BlackjackSettlement
    {
        public static void Settle(BlackJackGame game)
        {
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

                    if (handState.InsuranceBet > 0)
                    {
                        if (dealerBlackjack)
                        {
                            player.AddInsurancePayout(handState.InsuranceBet);
                        }
                        handState.InsuranceBet = 0;
                    }

                    if (playerTotal > 21)
                    {
                        player.AddLoss(i);
                        continue;
                    }

                    // A natural blackjack is a 2-card 21 on an unsplit hand; it pays 3:2.
                    var playerNatural = player.Hands.Count == 1 && handState.Hand.Cards.Count == 2 && playerTotal == 21;
                    if (playerNatural)
                    {
                        if (dealerBlackjack) player.AddPush(i);   // both naturals -> push
                        else player.AddWin(2.5m, i);              // 3:2 (returns 2.5x the stake)
                        continue;
                    }

                    if (dealerBlackjack)                          // dealer natural beats any non-natural hand
                    {
                        player.AddLoss(i);
                        continue;
                    }

                    if (dealerBust)
                    {
                        player.AddWin(2, i);
                        continue;
                    }

                    if (playerTotal > dealerTotal)
                    {
                        player.AddWin(2, i);
                    }
                    else if (playerTotal == dealerTotal)
                    {
                        player.AddPush(i);
                    }
                    else
                    {
                        player.AddLoss(i);
                    }
                }
            }
        }
    }
}
