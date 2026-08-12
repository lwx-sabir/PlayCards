using CardGames.Platforms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CardGames.Blackjack
{ 
        public class BlackJackGame
        {
            #region Fields / Properties

            [JsonInclude]
            public Deck Deck { get; private set; }

            /// <summary>Seed the current shoe was shuffled from. Kept so the shoe can be extended reproducibly if it
            /// ever runs dry mid-round; null for a shoe built without a seed.</summary>
            [JsonInclude]
            public byte[] ShoeSeed { get; private set; }

            /// <summary>Decks the current shoe was built from.</summary>
            [JsonInclude]
            public int ShoeDeckCount { get; private set; }

            /// <summary>How many times the current shoe has been extended mid-round (see <see cref="ExtendShoe"/>).
            /// Normally 0 — a non-zero value means the cut card is sized too deep for how the table actually plays.</summary>
            [JsonInclude]
            public int ShoeExtensions { get; private set; }

            [JsonInclude]
            public Player Dealer { get; private set; }

            [JsonInclude]
            public List<Player> Players { get; private set; }

            [JsonInclude]
            public bool StandOnSoft17 { get; set; } = true;

            [JsonInclude]
            public bool StandOnHard17 { get; set; } = true;

            #endregion

            #region Constructors

            // Parameterless constructor for JSON deserialization
            public BlackJackGame()
            {
                Players = new List<Player>();
                Dealer = new Player("dealer", -1, "Dealer");
                Deck = new Deck();
            }

            // Main constructor for creating a new game
            public BlackJackGame(List<Player> playerInfos) : this()
            {
                foreach (var info in playerInfos)
                {
                    Players.Add(new Player(info.Id, info.Balance, info.Name, info.Image));
                }
            }

            #endregion

            #region Game Methods

            /// <summary>
            /// Deals a new game
            /// </summary>
            /// <summary>
            /// Build and shuffle a NEW shoe. With a seed the shuffle is deterministic and independently verifiable
            /// (provably fair); without one it uses a crypto shuffle. Separate from <see cref="DealNewGame"/> so a
            /// multi-deck shoe can PERSIST across rounds and only be replaced when the cut card is reached.
            /// </summary>
            public void StartShoe(byte[] seed, int deckCount)
            {
                Deck = new Deck(deckCount);
                if (seed != null) Deck.Shuffle(seed);
                else Deck.Shuffle();

                ShoeSeed = seed;
                ShoeDeckCount = deckCount;
                ShoeExtensions = 0;
                AttachDeck();
            }

            /// <summary>
            /// Append a continuation to a shoe that ran dry mid-round. A round has no hard card limit — resplits and
            /// long low-card hands can in principle out-run any cut card — and failing part-way through would leave
            /// live stakes on a hand that can never finish. So instead of failing, the shoe grows.
            ///
            /// The continuation is derived from the shoe's own seed plus the extension number, so it stays fully
            /// reproducible: anyone replaying the hand from the recorded seed rebuilds the same extra cards. Reaching
            /// this at all means the cut card is set too deep for how the table plays; it is expected to stay at zero.
            /// </summary>
            private void ExtendShoe(Deck deck)
            {
                if (deck == null) return;

                ShoeExtensions += 1;
                var decks = ShoeDeckCount > 0 ? ShoeDeckCount : 1;
                var extension = new Deck(decks);

                if (ShoeSeed != null)
                    extension.Shuffle(CardGames.Provable.ProvableShuffle.DeriveSeed(ShoeSeed, "shoe-ext", ShoeExtensions));
                else
                    extension.Shuffle();

                deck.Cards.AddRange(extension.Cards);
            }

            /// <summary>Cards left in the current shoe (0 when there is no shoe).</summary>
            [JsonIgnore]
            public int CardsRemaining => Deck != null && Deck.Cards != null ? Deck.Cards.Count : 0;

            /// <summary>
            /// Point the dealer and every player at THE shoe. Everyone draws through their own <c>CurrentDeck</c>
            /// reference, so they must all be the same object — otherwise each seat quietly deals itself cards from a
            /// private deck and two seats can receive the same physical card.
            ///
            /// This matters because a table is stored as JSON between every action: object identity does not survive
            /// that round-trip, so <c>CurrentDeck</c> is <see cref="JsonIgnoreAttribute">not serialised</see> and must
            /// be re-attached after every load. Safe to call repeatedly.
            /// </summary>
            public void AttachDeck()
            {
                if (Deck == null) return;
                Deck.OnExhausted = ExtendShoe;   // also not serialised — see Deck.OnExhausted
                if (Dealer != null) Dealer.CurrentDeck = Deck;
                foreach (var p in Players) p.CurrentDeck = Deck;
            }

            /// <param name="newShoe">
            /// True (default) = the legacy single-deck behaviour: build a fresh shoe for this round. False = DEAL FROM
            /// THE EXISTING SHOE, which is what a real multi-deck game does — the shoe survives between rounds and is
            /// only replaced once the cut card is reached. A shoe is still created if there isn't one, or if it can't
            /// cover the deal.
            /// </param>
            public void DealNewGame(byte[] seed = null, int deckCount = 1, bool newShoe = true)
            {
                int needed = (Players.Count(p => p.InRound) + 1) * 2;   // 2 cards each, plus the dealer
                if (newShoe || Deck == null || Deck.Cards == null || Deck.Cards.Count < needed)
                    StartShoe(seed, deckCount);

                // Reset hands for dealer and all players
                Dealer.NewHand();
                foreach (var p in Players) p.NewHand();
                AttachDeck();

                // Deal two cards to each player and dealer
                for (int i = 0; i < 2; i++)
                {
                    foreach (var p in Players)
                    {
                        if (!p.InRound) continue; // seated-but-waiting players aren't dealt this round
                        p.Hand.Cards.Add(Deck.Draw());
                    }

                    var dealerCard = Deck.Draw();
                    if (i == 1) dealerCard.IsCardUp = false; // dealer second card facedown
                    Dealer.Hand.Cards.Add(dealerCard);
                }

                // Flag each opening hand that's a splittable pair (the "pairs dealt" lifetime stat) — captured NOW,
                // because a later hit/split changes the cards. Set on hand 0 only; a split reuses hand 0 so it survives.
                foreach (var p in Players)
                {
                    if (!p.InRound) continue;
                    var hs = p.Hands[0];
                    if (hs.Hand.Cards.Count == 2 && Player.CanSplitPair(hs.Hand.Cards[0], hs.Hand.Cards[1]))
                        hs.WasDealtPair = true;
                }
            }

            /// <summary>
            /// Dealer plays according to standard blackjack rules: reveal the hole card, then draw to 17
            /// (honouring StandOnSoft17 / StandOnHard17).
            ///
            /// EXCEPTION — she does NOT draw when no live hand is left to beat (every in-round hand busted,
            /// the casino-standard "all players bust" case). She reveals and the round ends there, exactly as
            /// a real dealer takes the cards without playing out a hand nobody can win. See
            /// <see cref="AnyHandNeedsDealerToDraw"/> for why that is outcome-neutral.
            /// </summary>
            public void DealerPlay()
            {
                Dealer.Hand.Cards[1].IsCardUp = true; // flip second card

                // Nothing left to beat → stand on the reveal. Drawing here would be pure theatre: it cannot
                // change a single payout (proof in AnyHandNeedsDealerToDraw), and it makes the table look like
                // the dealer is still playing a round that is already decided.
                if (!AnyHandNeedsDealerToDraw()) return;

                while (true)
                {
                    bool isSoft;
                    var total = Dealer.Hand.GetSumOfHand(out isSoft);

                    if (total > 17)
                        break;

                    if (total == 17)
                    {
                        if (isSoft && StandOnSoft17)
                            break;
                        if (!isSoft && StandOnHard17)
                            break;
                    }

                    if (total < 17 || (!StandOnHard17 && total == 17) || (!StandOnSoft17 && isSoft && total == 17))
                    {
                        Dealer.Hit();
                        continue;
                    }

                    break;
                }
            }

            /// <summary>
            /// True when at least one in-round hand's payout can still DEPEND on the dealer's final total —
            /// i.e. there is something left to beat. False ⇒ the dealer must not draw.
            ///
            /// Outcome-neutrality (this is a money path, so the rule is proved against
            /// <see cref="BlackjackSettlement.Settle"/>, not asserted):
            ///  • A BUST hand is decided by <c>playerTotal &gt; 21</c>, which Settle tests FIRST — before any
            ///    dealer comparison. Its result is identical whatever the dealer holds.
            ///  • A NATURAL is decided by <c>dealerBlackjack</c>, which is <c>dealerTotal == 21 &amp;&amp;
            ///    Cards.Count == 2</c> — a property of the dealer's two OPENING cards only. Drawing a third
            ///    card can never create or destroy it. (Insurance resolves off the same flag, so it is
            ///    unaffected too.) The natural test here mirrors Settle's exactly, splits included.
            /// Every other hand reaches the <c>dealerBust || playerTotal &gt; dealerTotal</c> comparison and
            /// therefore DOES need her to play out — those return true.
            ///
            /// Deck note: with a persistent shoe, cards NOT drawn here stay in the shoe and are dealt in a later
            /// round — so skipping the dealer's draw does shift what comes next. That is correct and matches a real
            /// table (an unplayed dealer hand burns no cards), and it cannot affect the provably-fair commitment,
            /// which covers the shoe's order rather than which round each card lands in.
            /// </summary>
            private bool AnyHandNeedsDealerToDraw()
            {
                bool sawHand = false;
                foreach (var p in Players)
                {
                    if (!p.InRound) continue;   // seated-but-waiting players aren't in this round
                    for (int i = 0; i < p.Hands.Count; i++)
                    {
                        sawHand = true;
                        var hand = p.Hands[i].Hand;
                        var total = hand.GetSumOfHand();
                        if (total > 21) continue;                       // bust — loses regardless of her total
                        // Natural: mirrors BlackjackSettlement's test (a split hand is never a natural).
                        if (p.Hands.Count == 1 && hand.Cards.Count == 2 && total == 21) continue;
                        return true;                                     // a live hand she still has to beat
                    }
                }
                // No in-round hands at all (empty or abandoned table): fall back to the plain house rule. There is
                // nothing to settle either way, so this keeps DealerPlay's standalone contract — draw to 17 — intact
                // rather than silently changing it for a degenerate table.
                return !sawHand;
            }

            #endregion
        }
}
