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

        /// <summary>
        /// True only for the wild JOKER (the 53rd card, used by Joker Poker). Additive + defaults false, so every
        /// existing 52-card game (blackjack, 3CP, standard video poker) is byte-identical. A joker is only ever read as
        /// "wild" — never by <see cref="Suit"/>/<see cref="FaceVal"/> — but it carries a distinct identity so the deck
        /// hash stays unique (see <c>ProvableShuffle.Canonical</c>).
        /// </summary>
        [JsonInclude]
        public bool IsJoker { get; set; }

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

        /// <summary>A wild joker. Its suit/face are placeholders (never scored by rank/suit); only <see cref="IsJoker"/> matters.</summary>
        public static Card Joker() => new Card(Suit.Spades, FaceValue.Ace, true) { IsJoker = true };

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
