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
            public void DealNewGame(byte[] seed = null, int deckCount = 1)
            {
                // Create and shuffle the shoe. With a seed the shuffle is deterministic and
                // independently verifiable (provably fair); without one it uses a crypto shuffle.
                Deck = new Deck(deckCount);
                if (seed != null) Deck.Shuffle(seed);
                else Deck.Shuffle();

                // Reset hands for dealer and all players
                Dealer.NewHand();
                Dealer.CurrentDeck = Deck;

                foreach (var p in Players)
                {
                    p.NewHand();
                    p.CurrentDeck = Deck;
                }

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
                    Dealer.CurrentDeck = Deck;

                    foreach (var p in Players)
                    {
                        p.CurrentDeck = Deck;
                    }
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
            /// Deck note: each round reshuffles a fresh shoe in <see cref="DealNewGame"/>, so consuming fewer
            /// cards here cannot leak into a later round or affect the provably-fair commitment.
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
