using System.Collections.Generic;
using System.Linq;

namespace CardGames.Blackjack
{
    /// <summary>Main-hand outcome of a settled blackjack hand.</summary>
    public enum HandOutcome { Blackjack, Win, Push, Lose, Bust }

    /// <summary>Insurance side-bet result for a hand (None = no insurance was placed).</summary>
    public enum InsuranceResult { None, Win, Lose }

    /// <summary>
    /// Rule-derived per-hand settlement result — one per played hand (a split yields two). This is the
    /// AUTHORITATIVE source of what a hand pays: the wallet is credited from <see cref="GrossReturn"/>, not
    /// from the engine's mutated mirror balance. <see cref="Stake"/> already reflects any double/split debited
    /// earlier. All money is decimal (no float).
    /// </summary>
    public sealed class HandSettlement
    {
        public int SeatNumber { get; set; }
        public int HandIndex { get; set; }
        public decimal Stake { get; set; }            // main-hand stake (includes a double-down's extra)
        public decimal InsuranceStake { get; set; }
        public int FinalValue { get; set; }
        public HandOutcome Outcome { get; set; }
        public InsuranceResult Insurance { get; set; }
        public decimal PayoutMultiplier { get; set; } // gross multiplier on the main stake (2.5 / 2 / 1 / 0)
        public decimal GrossReturn { get; set; }      // total gross returned incl. insurance — the credited value

        // Convenience accessors for the audit layer (GameHandParticipant fields).
        public bool Bust => Outcome == HandOutcome.Bust;
        public bool Blackjack => Outcome == HandOutcome.Blackjack;
        public string OutcomeCode => Outcome switch
        {
            HandOutcome.Blackjack => "blackjack",
            HandOutcome.Win => "win",
            HandOutcome.Push => "push",
            HandOutcome.Bust => "bust",
            _ => "lose",
        };
    }

    /// <summary>
    /// Pure blackjack settlement. Given a played-out game (dealer has acted) it decides each in-round hand's
    /// outcome from the END-STATE (cards + dealer), applies that outcome to the engine mirror via the
    /// <see cref="Player"/> mutators (for stats + the tripwire delta), and — independently — computes the
    /// rule-derived gross payout. The manager credits the wallet from the rule value and cross-checks it
    /// against the mirror delta (<see cref="ReconcilePayout"/>): a mismatch fails loudly instead of paying
    /// silently. Extracted from BlackjackTableManager so the decision logic is unit-testable without
    /// Redis/EF/SignalR.
    ///
    /// Payout rules (total-return multiplier on the per-hand stake): Blackjack (natural) 2.5x, Win 2x,
    /// Push 1x (stake back), Lose/Bust 0; insurance win returns 3x the insurance stake (2:1 plus the stake).
    /// </summary>
    public static class BlackjackSettlement
    {
        public const decimal InsuranceWinMultiplier = 3m; // 2:1 plus the returned stake

        /// <summary>The single source of the main-hand gross multiplier (stake already reflects double/split).</summary>
        public static decimal MainPayoutMultiplier(HandOutcome outcome) => outcome switch
        {
            HandOutcome.Blackjack => 2.5m,
            HandOutcome.Win => 2m,
            HandOutcome.Push => 1m,
            HandOutcome.Lose => 0m,
            HandOutcome.Bust => 0m,
            _ => 0m,
        };

        /// <summary>Rule-derived gross return for a hand = main payout + insurance payout. The credited value.</summary>
        public static decimal GrossReturnFor(HandOutcome outcome, decimal stake, InsuranceResult insurance, decimal insuranceStake)
            => stake * MainPayoutMultiplier(outcome)
             + (insurance == InsuranceResult.Win ? insuranceStake * InsuranceWinMultiplier : 0m);

        /// <summary>
        /// Cross-check the rule-derived payout against the engine mirror delta. The rule value is ALWAYS the
        /// amount to credit; a mismatch means the engine's mutated balance disagrees with the rules (a future
        /// AddWin/multiplier bug, a side bet, etc.) and must be flagged + healed, never paid silently.
        /// </summary>
        public static (decimal credit, bool mismatch) ReconcilePayout(decimal ruleComputed, decimal engineMirrorDelta)
            => (ruleComputed, ruleComputed != engineMirrorDelta);

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
                    var stake = handState.Bet;            // capture before the mutators zero it
                    var insStake = handState.InsuranceBet;
                    var playerNatural = player.Hands.Count == 1 && handState.Hand.Cards.Count == 2 && playerTotal == 21;

                    // Insurance pays 2:1 (3x total) only when the dealer has a natural.
                    var insurance = InsuranceResult.None;
                    if (insStake > 0)
                    {
                        insurance = dealerBlackjack ? InsuranceResult.Win : InsuranceResult.Lose;
                        if (insurance == InsuranceResult.Win) player.AddInsurancePayout(insStake); // mirror path
                        handState.InsuranceBet = 0;
                    }

                    // Decide the main-hand outcome from the end-state, then apply it to the engine mirror via
                    // the Player mutators (independent code path → the tripwire can catch a mutator bug).
                    HandOutcome outcome;
                    if (playerTotal > 21) { outcome = HandOutcome.Bust; player.AddLoss(i); }
                    else if (playerNatural)
                    {
                        if (dealerBlackjack) { outcome = HandOutcome.Push; player.AddPush(i); }       // both naturals -> push
                        else { outcome = HandOutcome.Blackjack; player.AddWin(2.5m, i); }             // 3:2
                    }
                    else if (dealerBlackjack) { outcome = HandOutcome.Lose; player.AddLoss(i); }       // dealer natural beats non-natural
                    else if (dealerBust || playerTotal > dealerTotal) { outcome = HandOutcome.Win; player.AddWin(2m, i); }
                    else if (playerTotal == dealerTotal) { outcome = HandOutcome.Push; player.AddPush(i); }
                    else { outcome = HandOutcome.Lose; player.AddLoss(i); }

                    results.Add(new HandSettlement
                    {
                        SeatNumber = player.SeatNumber,
                        HandIndex = i,
                        Stake = stake,
                        InsuranceStake = insStake,
                        FinalValue = playerTotal,
                        Outcome = outcome,
                        Insurance = insurance,
                        PayoutMultiplier = MainPayoutMultiplier(outcome),
                        GrossReturn = GrossReturnFor(outcome, stake, insurance, insStake),
                    });
                }
            }

            return results;
        }
    }
}
