using CardGames.Blackjack;
using Khela.Game.Managers;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// The shoe decision: does this round deal on from the existing shoe, or get a fresh one?
    ///
    /// The shoe is the only table state that now spans rounds, so a wrong answer here is not a spoiled round — it
    /// can freeze a table on a stale shoe, reuse a (server seed, nonce) pair, or make the audit certify a hand
    /// against cards it never saw. Every case below is one of those.
    /// </summary>
    public class ShoePlanTests
    {
        private const int Pen = 75;

        /// <summary>A table already dealing from a healthy six-deck shoe.</summary>
        private static BlackjackTable MidShoe(int decks = 6, int cardsRemaining = 200)
        {
            var t = new BlackjackTable
            {
                TableId = "t1",
                MaxPlayers = 3,
                RoundNonce = 10,
                ShoeNonce = 4,
                ShoeHash = "hash-of-current-shoe",
                ShoeSize = decks * 52,
                Game = new BlackJackGame(),
            };
            t.CutCardAt = ShoePlan.CutCardFor(t.ShoeSize, Pen, t.MaxPlayers);
            t.Game.StartShoe(new byte[] { 9 }, decks);
            while (t.Game.CardsRemaining > cardsRemaining) t.Game.Deck.Draw();
            return t;
        }

        private static ShoePlan Decide(BlackjackTable t, int decks = 6, bool reshuffleEveryRound = false)
            => ShoePlan.Decide(t, decks, Pen, reshuffleEveryRound);

        [Fact]
        public void AHealthyShoeIsDealtOnFrom()
        {
            var t = MidShoe();
            var plan = Decide(t);

            Assert.False(plan.NewShoe);
            Assert.Equal(t.ShoeNonce, plan.ShoeNonce);          // nonce must NOT advance mid-shoe
            Assert.Equal(t.ShoeHash, plan.ShoeHash);
            Assert.Equal(312 - 200, plan.ShoeDealtAtRoundStart); // the replay offset
        }

        [Fact]
        public void TheCutCardTriggersAFreshShoe()
        {
            var t = MidShoe(cardsRemaining: 200);
            t.CutCardAt = 201;                                   // pretend the cut sits just above where we are

            var plan = Decide(t);

            Assert.True(plan.NewShoe);
            Assert.Equal(t.ShoeNonce + 1, plan.ShoeNonce);
            Assert.Equal(0, plan.ShoeDealtAtRoundStart);
            Assert.Contains("cut card", plan.Reason);
        }

        [Fact]
        public void ACutReachedMidRoundStillLetsTheRoundBeDealtOnTheOldShoe()
        {
            // The check runs at the START of a round, so reaching the cut during a round is only observed next time.
            var t = MidShoe(cardsRemaining: 200);
            Assert.False(Decide(t).NewShoe);       // above the cut: deal on

            while (t.Game.CardsRemaining > t.CutCardAt) t.Game.Deck.Draw();   // the round consumes past the cut
            Assert.True(Decide(t).NewShoe);        // the NEXT round gets the new shoe
        }

        [Fact]
        public void ASingleDeckReshufflesEveryRoundAndHasNoCutCard()
        {
            var t = MidShoe(decks: 1, cardsRemaining: 40);
            var plan = Decide(t, decks: 1);

            Assert.True(plan.NewShoe);
            Assert.Equal(0, plan.CutCardAt);
            Assert.Equal(52, plan.ShoeSize);
        }

        [Fact]
        public void ReshuffleEveryRoundReproducesTheOriginalSixDeckBehaviour()
        {
            // The original game was six decks shuffled fresh every round. DeckCount 1 is NOT that, so this flag exists.
            var t = MidShoe();
            var plan = Decide(t, decks: 6, reshuffleEveryRound: true);

            Assert.True(plan.NewShoe);
            Assert.Equal(312, plan.ShoeSize);
            Assert.Equal(0, plan.CutCardAt);
        }

        [Fact]
        public void RaisingTheDeckCountAfterASingleDeckRoundRebuildsTheShoe()
        {
            // REGRESSION: a single-deck round leaves CutCardAt 0. Keying "should I rotate?" off the cut card alone
            // meant the table could never rotate again — it dealt on from the 52-card remnant forever, reusing one
            // seed while the recorded hash described a shoe nobody was playing.
            var t = MidShoe(decks: 1, cardsRemaining: 40);
            t.CutCardAt = 0;
            t.ShoeSize = 52;

            var plan = Decide(t, decks: 6);

            Assert.True(plan.NewShoe);
            Assert.Equal(312, plan.ShoeSize);
            Assert.True(plan.CutCardAt > 0);
            Assert.Equal(t.ShoeNonce + 1, plan.ShoeNonce);      // and the seed advances, so it is not reused
        }

        [Fact]
        public void LoweringTheDeckCountAlsoRebuilds()
        {
            var t = MidShoe(decks: 6, cardsRemaining: 200);
            var plan = Decide(t, decks: 2);

            Assert.True(plan.NewShoe);
            Assert.Equal(104, plan.ShoeSize);
        }

        [Fact]
        public void ATableThatPredatesTheShoeGetsARealShoeOnItsFirstDeal()
        {
            // Old tables carry a deck but no shoe metadata; they must not be dealt on as if the shoe were managed.
            var t = MidShoe();
            t.ShoeHash = null;
            t.ShoeSize = 0;
            t.CutCardAt = 0;

            var plan = Decide(t);

            Assert.True(plan.NewShoe);
            Assert.Equal(312, plan.ShoeSize);
        }

        [Fact]
        public void AnUnshuffledStarterDeckIsNeverDealtFrom()
        {
            // A brand-new BlackJackGame holds an UNSHUFFLED 52-card deck. It is big enough to cover an opening deal,
            // so anything that merely asks "are there enough cards?" would deal a round in perfect suit order.
            var t = new BlackjackTable { TableId = "t2", MaxPlayers = 3, Game = new BlackJackGame() };

            var plan = Decide(t);

            Assert.True(plan.NewShoe);
            Assert.Equal(312, plan.ShoeSize);
        }

        [Fact]
        public void MetadataDescribingMoreCardsThanTheShoeHoldsForcesARebuild()
        {
            // Stale/phantom metadata: the recorded shoe is smaller than the deck actually in play, so the recorded
            // hash cannot be describing these cards.
            var t = MidShoe(decks: 6, cardsRemaining: 300);
            t.ShoeSize = 52;

            Assert.True(Decide(t).NewShoe);
        }

        [Fact]
        public void AShoeThatHadToExtendItselfIsRetiredAtTheNextRound()
        {
            var t = MidShoe();
            while (t.Game.CardsRemaining > 0) t.Game.Deck.Draw();
            t.Game.Deck.Draw();                     // forces one extension

            Assert.True(t.Game.ShoeExtensions > 0);
            Assert.True(Decide(t).NewShoe);
        }

        [Fact]
        public void TheDeckCountIsClampedToWhatTheAuditCanReconstruct()
        {
            var t = MidShoe();
            Assert.Equal(BlackjackTableManager.MaxSupportedDecks, Decide(t, decks: 99).Decks);
            Assert.Equal(1, Decide(t, decks: 0).Decks);
            Assert.Equal(1, Decide(t, decks: -5).Decks);
        }

        [Fact]
        public void TheShoeNonceNeverRestartsBelowTheRoundNonceOnAPreExistingTable()
        {
            // Those rounds already burned nonces 1..N against this table's server seed. Restarting at 1 would
            // replay shuffles a player could have recorded.
            var t = MidShoe();
            t.ShoeNonce = 0;
            t.RoundNonce = 500;
            t.ShoeHash = null;

            Assert.Equal(501, Decide(t).ShoeNonce);
        }

        // ---- The reveal gate ----------------------------------------------------------------------------------
        // Thirteen lines in the verify endpoint are the only thing between a live 312-card shoe and a public
        // endpoint that would publish its whole order. The failure mode is silent — the endpoint keeps returning
        // 200 and the only symptom is a player who never loses — so it is pinned here.

        [Fact]
        public void AShoeStillBeingDealtFromKeepsItsSeedSecret()
        {
            Assert.False(ShoePlan.MayRevealSeed("shoe-A", liveShoeHash: "shoe-A"));
            Assert.False(ShoePlan.MayRevealSeed("shoe-A", liveShoeHash: "SHOE-A"));   // and not case-dodgeable
        }

        [Fact]
        public void ARetiredShoeReleasesItsProof()
        {
            Assert.True(ShoePlan.MayRevealSeed("shoe-A", liveShoeHash: "shoe-B"));
        }

        [Fact]
        public void AShoeWhoseTableIsGoneReleasesItsProof()
        {
            // No table (expired/deleted) or no shoe on it: nothing is being dealt from it any more.
            Assert.True(ShoePlan.MayRevealSeed("shoe-A", liveShoeHash: null));
            Assert.True(ShoePlan.MayRevealSeed("shoe-A", liveShoeHash: ""));
        }

        [Fact]
        public void AHandThatDoesNotSayWhichShoeItCameFromStaysSecret()
        {
            // Cannot be shown retired, so it is not revealed — the gate fails closed, never open.
            Assert.False(ShoePlan.MayRevealSeed(null, liveShoeHash: null));
            Assert.False(ShoePlan.MayRevealSeed("", liveShoeHash: "shoe-A"));
        }

        // ---- Who can retire a shoe -----------------------------------------------------------------------------
        // A shoe is retired by exactly two things: the cut card, and the table going COMPLETELY empty. Nothing a
        // player does by arriving or leaving may reshuffle a table that is still in use — on a 24h multiplayer
        // table that would rebuild the shoe every few minutes, the cut card would never be reached, and every
        // remaining player's read of the shoe would reset under them.

        private static BlackjackTable Seated(params int[] occupiedSeats)
        {
            var t = MidShoe(cardsRemaining: 200);
            t.Seats = new System.Collections.Generic.List<Seat>();
            for (int n = 1; n <= 5; n++)
            {
                var seat = new Seat { SeatNumber = n };
                if (occupiedSeats.Contains(n)) seat.Player = new Player($"p{n}", 100m, $"P{n}") { SeatNumber = n };
                t.Seats.Add(seat);
            }
            return t;
        }

        [Fact]
        public void ATableStillHoldingPlayersIsNotEmpty()
        {
            Assert.False(ShoePlan.TableIsEmpty(Seated(1, 2, 3)));
            Assert.False(ShoePlan.TableIsEmpty(Seated(4)));            // one straggler still counts
        }

        [Fact]
        public void OnlyTheLastPlayerLeavingEmptiesTheTable()
        {
            // Three seated; two leave. The table is NOT empty, so the shoe must not be retired.
            var t = Seated(1, 2, 3);
            t.Seats.First(s => s.SeatNumber == 2).Player = null;
            t.Seats.First(s => s.SeatNumber == 3).Player = null;
            Assert.False(ShoePlan.TableIsEmpty(t));
            Assert.False(Decide(t).NewShoe);                            // still dealing on the same shoe

            t.Seats.First(s => s.SeatNumber == 1).Player = null;        // now the last one goes
            Assert.True(ShoePlan.TableIsEmpty(t));
        }

        [Fact]
        public void APlayerJoiningMidShoeDoesNotReshuffle()
        {
            // Someone sits down at a table already in play: they join the shoe that is running, they do not reset it.
            var t = Seated(1);
            var before = Decide(t);
            Assert.False(before.NewShoe);

            t.Seats.First(s => s.SeatNumber == 3).Player = new Player("p3", 100m, "P3") { SeatNumber = 3 };
            var after = Decide(t);

            Assert.False(after.NewShoe);
            Assert.Equal(before.ShoeNonce, after.ShoeNonce);            // same shoe, same seed
            Assert.Equal(before.ShoeHash, after.ShoeHash);
            Assert.Equal(before.ShoeDealtAtRoundStart, after.ShoeDealtAtRoundStart);
        }

        [Fact]
        public void ABusyTableKeepsOneShoeAcrossConstantChurn()
        {
            // The 24h table: players arrive and leave repeatedly, but at least one seat is always taken. The shoe
            // must be the same shoe throughout — only the cut card may end it.
            var t = Seated(1);
            var nonce = t.ShoeNonce;
            for (int i = 0; i < 20; i++)
            {
                var seat = t.Seats.First(s => s.SeatNumber == (i % 4) + 2);
                seat.Player = (i % 2 == 0) ? new Player("x", 100m, "X") { SeatNumber = seat.SeatNumber } : null;

                Assert.False(ShoePlan.TableIsEmpty(t));                 // seat 1 never leaves
                var plan = Decide(t);
                Assert.False(plan.NewShoe);
                Assert.Equal(nonce, plan.ShoeNonce);
            }
        }

        [Fact]
        public void AnEmptiedTableStartsItsNextPlayerOnAFreshShoe()
        {
            // A table that empties retires its shoe (RemoveSeatCore), so whoever sits down next does not resume a
            // stranger's half-dealt one. That retirement is expressed by clearing the shoe identity; this pins that
            // ShoePlan then treats the table as needing a new shoe rather than dealing on from the leftover deck.
            var t = MidShoe(cardsRemaining: 200);
            t.ShoeHash = null;
            t.ShoeSize = 0;
            t.CutCardAt = 0;

            var plan = Decide(t);

            Assert.True(plan.NewShoe);
            Assert.Equal(312, plan.ShoeSize);
            Assert.Equal(0, plan.ShoeDealtAtRoundStart);
            Assert.Equal(t.ShoeNonce + 1, plan.ShoeNonce);

            // And the hands played on the retired shoe become verifiable the moment it is no longer live.
            Assert.True(ShoePlan.MayRevealSeed("shoe-they-played", liveShoeHash: t.ShoeHash));
        }

        [Fact]
        public void ThePenetrationSettingActuallyMovesTheCutCard()
        {
            // REGRESSION: an over-generous per-round reserve floored the cut above every configured value, so the
            // knob silently did nothing across its upper range and every table cut at 50% whatever was set.
            // Sized on the real house table: 6 decks, 5 seats.
            const int size = 312, seats = 5;

            int at60 = ShoePlan.CutCardFor(size, 60, seats);
            int at75 = ShoePlan.CutCardFor(size, 75, seats);
            int at85 = ShoePlan.CutCardFor(size, 85, seats);

            Assert.Equal(124, at60);                        // 40% left
            Assert.Equal(78, at75);                         // 25% left — the shipped default, honoured exactly
            Assert.True(at85 < at75, "a deeper penetration must cut later");
            Assert.True(at75 < at60, "a shallower penetration must cut earlier");
        }

        [Fact]
        public void TheReserveStillFloorsAnUnsafelyDeepPenetration()
        {
            // The floor must not be gone — only sized so it stops overriding sane settings.
            int cut = ShoePlan.CutCardFor(312, 95, maxPlayers: 5);
            Assert.Equal(72, cut);                          // (5 + 1) * 12, not the configured 15
        }

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(6)]
        [InlineData(8)]
        public void TheCutCardAlwaysLeavesEnoughToDealAndIsInsideTheShoe(int decks)
        {
            int size = decks * 52;
            foreach (var pen in new[] { 0, 10, 50, 75, 95, 100, 150, -20 })
            {
                int cut = ShoePlan.CutCardFor(size, pen, maxPlayers: 3);
                Assert.InRange(cut, 1, size - 1);
                Assert.True(cut >= (4 + 1) * 2, $"cut {cut} for {decks} decks at {pen}% cannot cover an opening deal");
            }
        }
    }
}
