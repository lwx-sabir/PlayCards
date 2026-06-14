using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CardGames.Provable;

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
        /// A multi-deck shoe — e.g. 6 decks = 312 cards — the casino standard for blackjack and
        /// the fix for a single 52-card deck running dry across multiple seats and splits.
        /// </summary>
        public Deck(int numberOfDecks)
        {
            if (numberOfDecks < 1)
                throw new ArgumentOutOfRangeException(nameof(numberOfDecks), "A shoe needs at least one deck.");

            for (int d = 0; d < numberOfDecks; d++)
            {
                foreach (Suit suit in Enum.GetValues(typeof(Suit)))
                {
                    foreach (FaceValue faceVal in Enum.GetValues(typeof(FaceValue)))
                    {
                        Cards.Add(new Card(suit, faceVal, true));
                    }
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
        /// Deterministic, provably-fair shuffle from a seed. Same seed → same order, so the hand
        /// can be independently replayed and verified. Prefer this over the seedless overload.
        /// </summary>
        public void Shuffle(byte[] seed) => ProvableShuffle.Shuffle(Cards, seed);

        /// <summary>SHA-256 fingerprint of the current ordered deck (for the audit record).</summary>
        public string ComputeHash() => ProvableShuffle.DeckHash(Cards);

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
