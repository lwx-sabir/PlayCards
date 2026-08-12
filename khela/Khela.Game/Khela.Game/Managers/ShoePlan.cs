using System;
using System.Linq;

namespace Khela.Game.Managers
{
    /// <summary>
    /// Decides, at the start of a round, whether the table deals on from its existing shoe or gets a fresh one —
    /// and if fresh, what that shoe looks like.
    ///
    /// This is deliberately a PURE function of the table's current state plus configuration. The shoe is the one
    /// piece of table state that now spans rounds, so getting this decision wrong doesn't just spoil a round: it
    /// can freeze a table on a stale shoe, reuse a (server seed, nonce) pair, or make the audit certify a hand
    /// against cards it never saw. Keeping it free of Redis, the wallet and the clock is what makes it testable.
    /// </summary>
    public sealed class ShoePlan
    {
        /// <summary>Decks in the shoe, already clamped to what the audit can reconstruct.</summary>
        public int Decks { get; init; }

        /// <summary>True when this round must be dealt from a brand-new shuffle.</summary>
        public bool NewShoe { get; init; }

        /// <summary>Why <see cref="NewShoe"/> is set — for logging and for tests to assert intent, not just outcome.</summary>
        public string Reason { get; init; }

        /// <summary>Nonce the shoe's shuffle is derived from. Advances only when the shoe is replaced.</summary>
        public long ShoeNonce { get; init; }

        /// <summary>Hash of the shoe as shuffled. Carried over unchanged when dealing on from the existing shoe;
        /// filled in by the caller once it has built the new shuffle.</summary>
        public string ShoeHash { get; set; }

        /// <summary>Cards in the shoe when it was shuffled.</summary>
        public int ShoeSize { get; init; }

        /// <summary>Cards LEFT when the cut card is reached. 0 = no cut card (a reshuffle-every-round table).</summary>
        public int CutCardAt { get; init; }

        /// <summary>Cards already dealt from this shoe before this round — the offset that makes a single hand
        /// replayable once one shuffle spans many hands. Always 0 on a fresh shoe.</summary>
        public int ShoeDealtAtRoundStart { get; init; }

        /// <summary>
        /// Has the table gone completely empty? This is the ONLY condition that retires a shoe outside the cut card.
        ///
        /// It is deliberately "the last player left", not "a player left": a shoe has to survive players coming and
        /// going. On a busy table someone sits down or stands up every few minutes, and rebuilding the shoe each time
        /// would make it meaningless — the cut card would never be reached and every remaining player's read of the
        /// shoe would reset under them. Joining likewise never touches the shoe; a player who sits down mid-shoe
        /// joins the shoe already in play.
        /// </summary>
        public static bool TableIsEmpty(BlackjackTable table)
            => table?.Seats == null || !table.Seats.Any(s => s.Player != null);

        /// <summary>
        /// May a settled hand's shuffle seed be published yet?
        ///
        /// A multi-deck shoe spans many hands, so its seed is also the order of every card still to come out of it.
        /// Releasing it while the shoe is in play hands any player the rest of the deal, dealer hole cards included.
        /// The proof therefore waits until the shoe is retired — which costs nothing, because the commitment was
        /// published before the deal and cannot be changed afterwards.
        ///
        /// Fails CLOSED: anything ambiguous (no shoe id recorded, table state unavailable) withholds. A late proof
        /// is a nuisance; an early one is the whole game.
        /// </summary>
        /// <param name="handShoeId">The <c>ShoeId</c> recorded on the settled hand.</param>
        /// <param name="liveShoeHash">The shoe the table is dealing from NOW, or null if the table is gone.</param>
        public static bool MayRevealSeed(string handShoeId, string liveShoeHash)
        {
            // Nothing identifies which shoe this hand came from, so we cannot show it is retired.
            if (string.IsNullOrEmpty(handShoeId)) return false;

            // No table, or no shoe on it: whatever this hand was dealt from is no longer in play.
            if (string.IsNullOrEmpty(liveShoeHash)) return true;

            return !string.Equals(handShoeId, liveShoeHash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Work out what this round should be dealt from.
        /// </summary>
        /// <param name="table">The table as loaded, before anything about this round has been written to it.</param>
        /// <param name="deckCount">Configured decks per shoe (<c>Blackjack:DeckCount</c>).</param>
        /// <param name="penetrationPercent">Configured % of the shoe dealt before the cut card.</param>
        /// <param name="reshuffleEveryRound">Force a fresh shuffle each round regardless of shoe size.</param>
        public static ShoePlan Decide(BlackjackTable table, int deckCount, int penetrationPercent, bool reshuffleEveryRound)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));

            int decks = Math.Clamp(deckCount, 1, BlackjackTableManager.MaxSupportedDecks);
            bool singleDeck = decks == 1;
            int cardsRemaining = table.Game?.CardsRemaining ?? 0;

            // A shoe is only allowed to continue if it IS the shoe the current configuration describes. Every test
            // below is a way that can stop being true; each one, left unchecked, keeps a wrong shoe in play.
            //
            // Note these compare against the shoe the ENGINE is actually holding (Game.ShoeDeckCount / CardsRemaining)
            // rather than trusting the table's own metadata — metadata can be stale or describe a shoe that was
            // prepared for a deal that then failed, and it must never be able to pin the table to the wrong shoe.
            string reason =
                  singleDeck                                        ? "single deck — reshuffled every round"
                : reshuffleEveryRound                               ? "configured to reshuffle every round"
                // "no shoe" is tested BEFORE "deck count changed" so a fresh or just-emptied table reports why it
                // really has no shoe, rather than blaming a configuration change that never happened.
                : string.IsNullOrEmpty(table.ShoeHash)              ? "no shoe on this table yet"
                : table.ShoeSize <= 0                               ? "shoe size unknown"
                : (table.Game?.ShoeDeckCount ?? 0) != decks         ? "deck count changed — shoe no longer matches configuration"
                : cardsRemaining <= 0                               ? "shoe is spent"
                : table.CutCardAt <= 0                              ? "shoe has no cut card — nothing would ever rotate it"
                : cardsRemaining > table.ShoeSize                   ? "more cards in play than the shoe holds — stale shoe"
                : (table.Game?.ShoeExtensions ?? 0) > 0             ? "shoe was extended mid-round — no longer the shoe its hash describes"
                : cardsRemaining <= table.CutCardAt                 ? "cut card reached"
                : null;

            bool newShoe = reason != null;

            // Continuing: keep everything as it is and just record where in the shoe this round starts.
            if (!newShoe)
            {
                return new ShoePlan
                {
                    Decks = decks,
                    NewShoe = false,
                    Reason = "dealing on from the current shoe",
                    ShoeNonce = table.ShoeNonce,
                    ShoeHash = table.ShoeHash,
                    ShoeSize = table.ShoeSize,
                    CutCardAt = table.CutCardAt,
                    ShoeDealtAtRoundStart = Math.Max(0, table.ShoeSize - cardsRemaining),
                };
            }

            // Fresh shoe. The nonce advances per SHOE, not per round — one shoe spans many rounds, so re-deriving
            // from RoundNonce mid-shoe would name a shuffle that never produced these cards.
            //
            // One-time catch-up for tables that predate the shoe: those rounds already burned nonces 1..N against
            // this table's server seed, so starting at 1 would replay shuffles a player could have recorded.
            // A (server seed, nonce) pair must never be reused.
            long nonce = table.ShoeNonce;
            if (nonce == 0 && table.RoundNonce > 0) nonce = table.RoundNonce;
            nonce += 1;

            int shoeSize = decks * 52;

            return new ShoePlan
            {
                Decks = decks,
                NewShoe = true,
                Reason = reason,
                ShoeNonce = nonce,
                ShoeHash = null,                 // the caller shuffles and fills this in
                ShoeSize = shoeSize,
                // No cut card when the shoe is replaced every round anyway — it would never be reached, and the
                // client would draw a marker for something that never happens.
                CutCardAt = singleDeck || reshuffleEveryRound
                    ? 0
                    : CutCardFor(shoeSize, penetrationPercent, table.MaxPlayers),
                ShoeDealtAtRoundStart = 0,
            };
        }

        /// <summary>
        /// Where the cut card sits, as cards LEFT when it is reached.
        ///
        /// Beyond setting penetration, the cut also keeps a round finishable: hits, splits and doubles consume far
        /// more than the opening deal. A round has no hard card limit, so the reserve is not a proof — the shoe can
        /// still extend itself if it ever runs dry (see <c>BlackJackGame.ExtendShoe</c>) — it just has to make that
        /// vanishingly rare.
        ///
        /// So the reserve is sized on a REALISTIC bad round, roughly a dozen cards per seat plus the dealer, not on
        /// a theoretical maximum. Sizing it for the worst imaginable round instead (every seat resplit to four
        /// hands, every hand running long) puts the floor above the configured cut and silently pins every table to
        /// it — which makes the penetration setting do nothing across most of its range. It must stay below the
        /// configured cut at ordinary settings: at the shipped 6 decks / 75% on a 5-seat table the reserve is 72 and
        /// the configured cut is 78, so the configuration wins and the floor only bites at extreme penetration.
        /// </summary>
        public static int CutCardFor(int shoeSize, int penetrationPercent, int maxPlayers)
        {
            int seats = Math.Max(1, maxPlayers);
            int perRoundReserve = (seats + 1) * 12;
            int configured = (int)(shoeSize * (100 - Math.Clamp(penetrationPercent, 10, 95)) / 100.0);
            // Never reserve more than half the shoe, or a small shoe could never deal a round at all.
            int floor = Math.Min(perRoundReserve, shoeSize / 2);
            return Math.Clamp(configured, floor, Math.Max(floor, shoeSize - 1));
        }
    }
}
