using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CardGames.Platforms
{
    /// <summary>
    /// Card suit values
    /// </summary>
    public enum Suit
    {
        Diamonds, Spades, Clubs, Hearts
    }

    /// <summary>
    /// Card face values
    /// </summary>
    public enum FaceValue
    {
        Two = 2, Three = 3, Four = 4, Five = 5, Six = 6, Seven = 7, Eight = 8,
        Nine = 9, Ten = 10, Jack = 11, Queen = 12, King = 13, Ace = 14
    }

    public class Card
    {
        [JsonInclude]
        public Suit Suit { get; private set; }

        [JsonInclude]
        public FaceValue FaceVal { get; private set; }

        [JsonInclude]
        public bool IsCardUp { get; set; }

        public Card()
        {
        }

        [JsonConstructor]
        public Card(Suit suit, FaceValue faceVal, bool isCardUp)
        {
            Suit = suit;
            FaceVal = faceVal;
            IsCardUp = isCardUp;
        }

        /// <summary>
        /// Return the card as a string (i.e. "The Ace of Spades")
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return "The" + FaceVal.ToString() + "of" + Suit.ToString();
        }
    }
}
