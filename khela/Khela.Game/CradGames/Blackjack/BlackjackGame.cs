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
            }

            /// <summary>
            /// Dealer plays according to standard blackjack rules
            /// </summary>
            public void DealerPlay()
            {
                Dealer.Hand.Cards[1].IsCardUp = true; // flip second card

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
             
            #endregion
        }
}
