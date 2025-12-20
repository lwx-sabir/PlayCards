using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CardGames.Platforms
{
    public class Deck
    {
        // Creates a list of cards
        [JsonInclude]
        public List<Card> Cards = [];

        // Returns the card at the given position
        public Card this[int position] { get { return (Card)Cards[position]; } }

        /// <summary>
        /// One complete deck with every face value and suit
        /// </summary>
        public Deck()
        {
            foreach (Suit suit in Enum.GetValues(typeof(Suit)))
            {
                foreach (FaceValue faceVal in Enum.GetValues(typeof(FaceValue)))
                {
                    Cards.Add(new Card(suit, faceVal, true));
                }
            }
        }

        /// <summary>
        /// Draws one card and removes it from the deck
        /// </summary>
        /// <returns></returns>
        public Card Draw()
        {
            Card card = Cards[0];
            Cards.RemoveAt(0);
            return card;
        }

        /// <summary>
        /// Shuffles the cards in the deck
        /// </summary>
        public void Shuffle()
        {
            // Fisher-Yates shuffle using crypto RNG for fairness
            for (int i = Cards.Count - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                SwapCard(i, j);
            }
        }

        /// <summary>
        /// Swaps the placement of two cards
        /// </summary>
        /// <param name="index1"></param>
        /// <param name="index2"></param>
        private void SwapCard(int index1, int index2)
        {
            Card card = Cards[index1];
            Cards[index1] = Cards[index2];
            Cards[index2] = card;
        }

        public List<Card> GetRemainingDeck()
        {
            return Cards;
        }
    }
}
