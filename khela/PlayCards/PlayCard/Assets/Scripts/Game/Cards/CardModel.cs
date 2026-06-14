using PlayCard.Game.Dtos;

namespace PlayCard.Game.Cards
{
    /// <summary>
    /// Suit, in the SAME enum order as the server's <c>CardGames.Platforms.Suit</c>
    /// (Diamonds, Spades, Clubs, Hearts). The board snapshot sends the suit as this integer,
    /// so a plain cast <c>(CardSuit)dto.Suit</c> is correct.
    ///
    /// IMPORTANT: this order is NOT the order the suits are laid out in any particular card
    /// atlas. The mapping "which suit is on which row of the art" lives in <see cref="CardSkin"/>,
    /// so art can be reskinned without ever touching this enum. (The bundled Atlas..png happens to
    /// be Hearts, Spades, Clubs, Diamonds top-to-bottom — note Hearts/Diamonds are swapped vs the
    /// wire order, which is exactly why we map by suit identity, never by raw int.)
    /// </summary>
    public enum CardSuit
    {
        Diamonds = 0,
        Spades = 1,
        Clubs = 2,
        Hearts = 3
    }

    /// <summary>
    /// Rank, matching the server's <c>CardGames.Platforms.FaceValue</c> (Two=2 .. Ace=14).
    /// Values are explicit so the wire integer casts straight across.
    /// </summary>
    public enum CardRank
    {
        Two = 2, Three = 3, Four = 4, Five = 5, Six = 6, Seven = 7, Eight = 8,
        Nine = 9, Ten = 10, Jack = 11, Queen = 12, King = 13, Ace = 14
    }

    /// <summary>
    /// A single card as the CLIENT needs to draw it: what it is, and whether it's face-up.
    /// Built from the server snapshot — the client never invents a card, it only renders what
    /// the authoritative server dealt.
    /// </summary>
    public readonly struct CardId
    {
        public readonly CardRank Rank;
        public readonly CardSuit Suit;
        public readonly bool FaceUp;

        public CardId(CardRank rank, CardSuit suit, bool faceUp)
        {
            Rank = rank;
            Suit = suit;
            FaceUp = faceUp;
        }

        public CardId WithFaceUp(bool faceUp) => new CardId(Rank, Suit, faceUp);

        // ---- wire mapping (the "mapper" the board DTO feeds into) ----

        public static CardId FromWire(int faceVal, int suit, bool isCardUp)
            => new CardId((CardRank)faceVal, (CardSuit)suit, isCardUp);

        /// <summary>Map a board-snapshot <see cref="CardView"/> straight to a renderable card.</summary>
        public static CardId FromWire(CardView wire)
            => FromWire(wire.FaceVal, wire.Suit, wire.IsCardUp);

        public override string ToString()
            => $"{Rank} of {Suit} ({(FaceUp ? "up" : "down")})";
    }
}
