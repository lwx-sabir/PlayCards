using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CardGames.Blackjack;
using CardGames.Platforms;
using Khela.Game.Database.Models;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Where the blackjack rules meet the money. The settle DECISIONS are unit-locked in CardGames.Tests; what is
    /// proven here is the RECONCILIATION the table manager performs on top of them — gross, stake and net derived from
    /// the rule table, then pushed through the REAL wallet against MySQL:
    ///
    ///     gross  = Σ HandSettlement.GrossReturn          (what gets credited)
    ///     staked = Σ (Stake + InsuranceStake)            (what the deal debited)
    ///     net    = gross − staked                        (what the banner announces)
    ///
    /// The property that matters, and the one no unit test can show: after a real debit-on-bet and a real
    /// credit-on-settle, the player's balance moved by exactly <c>net</c> — for every outcome, including splits,
    /// insurance and doubles.
    /// </summary>
    [Collection("khela-db")]
    public class SettleReconciliationTests
    {
        private readonly KhelaDbFixture _fx;
        public SettleReconciliationTests(KhelaDbFixture fx) => _fx = fx;

        private const decimal StartingChips = 10_000m;

        private static Card C(FaceValue v, Suit s = Suit.Spades) => new Card(s, v, true);

        /// <summary>A one-seat game with the dealer's and the player's cards dealt and the bet placed.</summary>
        private static (BlackJackGame Game, Player Player) Game(Card[] dealer, Card[] player, decimal bet = 100m)
        {
            var game = new BlackJackGame();
            foreach (var c in dealer) game.Dealer.Hand.Cards.Add(c);

            var p = new Player("p1", StartingChips, "P1") { InRound = true, SeatNumber = 1 };
            foreach (var c in player) p.GetHand(0).Hand.Cards.Add(c);
            p.GetHand(0).Bet = bet;
            game.Players.Add(p);
            return (game, p);
        }

        /// <summary>Settle exactly the way <c>BlackjackTableManager.SettleInternalAsync</c> does.</summary>
        private static (decimal Gross, decimal Staked, decimal Net, bool Mismatch) Reconcile(BlackJackGame game, Player player)
        {
            var before = player.Balance;
            var hands = BlackjackSettlement.Settle(game).Where(h => h.SeatNumber == player.SeatNumber).ToList();

            var computed = hands.Sum(h => h.GrossReturn);
            var mirrorDelta = player.Balance - before;
            var (gross, mismatch) = BlackjackSettlement.ReconcilePayout(computed, mirrorDelta);
            var staked = hands.Sum(h => h.Stake + h.InsuranceStake);
            return (gross, staked, gross - staked, mismatch);
        }

        /// <summary>Play the money exactly as a live round does: debit every stake at deal, credit the gross at settle.
        /// Returns the wallet's movement across the whole round.</summary>
        private async Task<decimal> RoundThroughWalletAsync(decimal staked, decimal gross)
        {
            var user = Guid.NewGuid().ToString();
            var round = Guid.NewGuid().ToString("N");
            using var stack = _fx.NewStack();

            await stack.Wallet.CreditAsync(user, CurrencyType.Chips, StartingChips, TransactionType.AdminAdjustment, $"seed:{round}");
            var opening = await stack.Wallet.GetBalanceAsync(user, CurrencyType.Chips);

            if (staked > 0m)
                await stack.Wallet.DebitAsync(user, CurrencyType.Chips, staked, TransactionType.Bet, $"bet:{round}");
            if (gross > 0m)
                await stack.Wallet.CreditAsync(user, CurrencyType.Chips, gross, TransactionType.Win, $"win:{round}");

            return await stack.Wallet.GetBalanceAsync(user, CurrencyType.Chips) - opening;
        }

        // ---- one scenario per outcome: the rules and the wallet must agree ----

        public static IEnumerable<object[]> Rounds() => new[]
        {
            // name, dealer, player, bet, expected net
            new object[] { "player 20 beats dealer 18", new[] { FaceValue.Ten, FaceValue.Eight }, new[] { FaceValue.Ten, FaceValue.Ten }, 100m, 100m },
            new object[] { "player 16 loses to dealer 18", new[] { FaceValue.Ten, FaceValue.Eight }, new[] { FaceValue.Ten, FaceValue.Six }, 100m, -100m },
            new object[] { "equal totals push", new[] { FaceValue.Ten, FaceValue.Eight }, new[] { FaceValue.Ten, FaceValue.Eight }, 100m, 0m },
            new object[] { "dealer busts", new[] { FaceValue.Ten, FaceValue.Six, FaceValue.Ten }, new[] { FaceValue.Ten, FaceValue.Seven }, 100m, 100m },
            new object[] { "natural pays 3:2", new[] { FaceValue.Ten, FaceValue.Eight }, new[] { FaceValue.Ace, FaceValue.King }, 100m, 150m },
        };

        [Theory]
        [MemberData(nameof(Rounds))]
        public async Task TheWalletMovesByExactlyTheRoundsNet(string name, FaceValue[] dealer, FaceValue[] player, decimal bet, decimal expectedNet)
        {
            var (game, p) = Game(dealer.Select(v => C(v)).ToArray(), player.Select(v => C(v, Suit.Hearts)).ToArray(), bet);

            var settle = Reconcile(game, p);

            Assert.False(settle.Mismatch, $"{name}: rule table and engine mirror disagree");
            Assert.Equal(expectedNet, settle.Net);
            Assert.Equal(expectedNet, await RoundThroughWalletAsync(settle.Staked, settle.Gross));
        }

        [Fact]
        public async Task ABustedHandStakesEverythingAndReturnsNothing()
        {
            var (game, p) = Game(
                new[] { C(FaceValue.Ten), C(FaceValue.Eight) },
                new[] { C(FaceValue.Ten, Suit.Hearts), C(FaceValue.Nine, Suit.Hearts), C(FaceValue.Five, Suit.Hearts) });

            var settle = Reconcile(game, p);

            Assert.Equal(0m, settle.Gross);              // nothing comes back…
            Assert.Equal(100m, settle.Staked);           // …and the stake is gone
            Assert.Equal(-100m, await RoundThroughWalletAsync(settle.Staked, settle.Gross));
        }

        [Fact]
        public async Task SplitHandsReconcileHandByHand_AndSumToTheSeatsNet()
        {
            // The case a seat-level net alone would misreport: one hand wins, the other loses. The per-hand values
            // have to sum to the seat delta, or the client's pay/collect choreography moves the wrong chips.
            var game = new BlackJackGame();
            game.Dealer.Hand.Cards.Add(C(FaceValue.Ten));
            game.Dealer.Hand.Cards.Add(C(FaceValue.Eight));           // dealer 18

            var p = new Player("p1", StartingChips, "P1") { InRound = true, SeatNumber = 1 };
            p.GetHand(0).Hand.Cards.Add(C(FaceValue.Ten, Suit.Hearts));
            p.GetHand(0).Hand.Cards.Add(C(FaceValue.Ten, Suit.Clubs));   // 20 — wins
            p.GetHand(0).Bet = 100m;

            var second = new PlayerHandState { Bet = 100m };
            second.Hand.Cards.Add(C(FaceValue.Ten, Suit.Diamonds));
            second.Hand.Cards.Add(C(FaceValue.Six, Suit.Diamonds));     // 16 — loses
            p.Hands.Add(second);
            game.Players.Add(p);

            var before = p.Balance;
            var hands = BlackjackSettlement.Settle(game).Where(h => h.SeatNumber == 1).OrderBy(h => h.HandIndex).ToList();
            var gross = hands.Sum(h => h.GrossReturn);
            var staked = hands.Sum(h => h.Stake + h.InsuranceStake);
            var net = gross - staked;

            Assert.Equal(2, hands.Count);
            Assert.Equal(200m, hands[0].GrossReturn);                   // winner returns stake + win
            Assert.Equal(0m, hands[1].GrossReturn);                     // loser returns nothing
            Assert.Equal(0m, net);                                      // …which nets to a wash overall
            Assert.Equal(net, hands.Sum(h => h.GrossReturn - (h.Stake + h.InsuranceStake)));   // per-hand sums to the seat
            Assert.Equal(p.Balance - before, gross);                    // mirror agrees with the rule table
            Assert.Equal(0m, await RoundThroughWalletAsync(staked, gross));
        }

        [Fact]
        public async Task InsuranceIsStakedAndPaidAlongsideTheMainHand()
        {
            // Dealer shows an Ace and has the natural: the main hand loses, insurance pays 2:1 on its half-stake,
            // so the round is a wash — the classic case where gross and stake must both include insurance or the
            // net is wrong in two directions at once.
            var game = new BlackJackGame();
            game.Dealer.Hand.Cards.Add(C(FaceValue.Ace));
            game.Dealer.Hand.Cards.Add(C(FaceValue.King));              // dealer natural

            var p = new Player("p1", StartingChips, "P1") { InRound = true, SeatNumber = 1 };
            p.GetHand(0).Hand.Cards.Add(C(FaceValue.Ten, Suit.Hearts));
            p.GetHand(0).Hand.Cards.Add(C(FaceValue.Nine, Suit.Hearts));  // 19, loses to the natural
            p.GetHand(0).Bet = 100m;
            p.GetHand(0).InsuranceBet = 50m;
            game.Players.Add(p);

            var settle = Reconcile(game, p);

            Assert.Equal(150m, settle.Staked);                          // 100 main + 50 insurance
            Assert.Equal(150m, settle.Gross);                           // insurance returns 50 + 100
            Assert.Equal(0m, settle.Net);
            Assert.Equal(0m, await RoundThroughWalletAsync(settle.Staked, settle.Gross));
        }

        [Fact]
        public async Task ADoubledHandStakesTwiceAndPaysOnTheDoubledStake()
        {
            var (game, p) = Game(
                new[] { C(FaceValue.Ten), C(FaceValue.Seven) },                                   // dealer 17
                new[] { C(FaceValue.Six, Suit.Hearts), C(FaceValue.Five, Suit.Hearts) });         // 11 → double
            p.GetHand(0).Bet = 200m;                                                              // the doubled stake
            p.GetHand(0).IsDoubled = true;
            p.GetHand(0).Hand.Cards.Add(C(FaceValue.Nine, Suit.Hearts));                          // 20, beats 17

            var settle = Reconcile(game, p);

            Assert.Equal(200m, settle.Staked);
            Assert.Equal(400m, settle.Gross);
            Assert.Equal(200m, settle.Net);                             // a double wins twice the base bet
            Assert.Equal(200m, await RoundThroughWalletAsync(settle.Staked, settle.Gross));
        }

        // ---- the tripwire and the retry ----

        [Fact]
        public void AMirrorThatDisagreesWithTheRuleTableIsFlagged_AndTheRuleValueIsCredited()
        {
            // If a future engine change makes AddWin drift from the rule table, the manager must credit the RULE and
            // shout — never silently pay whatever the mirror happened to say.
            var (credit, mismatch) = BlackjackSettlement.ReconcilePayout(ruleComputed: 250m, engineMirrorDelta: 300m);
            Assert.True(mismatch);
            Assert.Equal(250m, credit);

            var (agreed, stillMismatched) = BlackjackSettlement.ReconcilePayout(250m, 250m);
            Assert.False(stillMismatched);
            Assert.Equal(250m, agreed);
        }

        [Fact]
        public async Task ARetriedSettleCreditsTheRoundOnce()
        {
            // Settle can be re-entered (round driver + a player's own dealerPlay racing, or a crash mid-settle).
            // The credit is keyed on the round, so the second attempt is a no-op rather than a double payout.
            var user = Guid.NewGuid().ToString();
            var round = Guid.NewGuid().ToString("N");
            using var stack = _fx.NewStack();

            await stack.Wallet.CreditAsync(user, CurrencyType.Chips, StartingChips, TransactionType.AdminAdjustment, $"seed:{round}");
            await stack.Wallet.DebitAsync(user, CurrencyType.Chips, 100m, TransactionType.Bet, $"bet:{round}");

            await stack.Wallet.CreditAsync(user, CurrencyType.Chips, 200m, TransactionType.Win, $"win:{round}");
            await stack.Wallet.CreditAsync(user, CurrencyType.Chips, 200m, TransactionType.Win, $"win:{round}");   // retry

            Assert.Equal(StartingChips + 100m, await stack.Wallet.GetBalanceAsync(user, CurrencyType.Chips));
            Assert.Equal(1, await stack.Db.WalletTransactions.CountAsync(t => t.CorrelationId == $"win:{round}"));
        }
    }
}
