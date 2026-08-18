using System;
using PlayCard.Game.Cards;   // reuses only the CARD IDENTITY types (CardId/CardRank/CardSuit), NOT the table atlas skin
using UnityEngine;

namespace PlayCard.VideoPoker.Cards
{
    /// <summary>
    /// A video-poker card look: 52 individual face sprites + a back. This is VP's OWN skin system, separate from the
    /// 3D tables' atlas <c>CardSkin</c> — VP cards are one PNG each (imported as Sprites) and rendered on a UGUI
    /// <see cref="UnityEngine.UI.Image"/>. Adding a new deck design = create another VpCardSkin asset and drop in a new
    /// set of 52 sprites; no code changes, and this doubles as the unit a store/skin-picker hands the machine to
    /// restyle every card at runtime.
    ///
    /// Create via: Assets ▸ Create ▸ Khela ▸ Video Poker ▸ Card Skin.
    ///
    /// Fill order for <see cref="faces"/>: suit-major — all 13 of <see cref="suitOrder"/>[0] (Two→Ace), then [1], etc.
    /// So index = suitSlot × 13 + (rank − 2). Use the auto-populate editor tool if your PNGs follow a name convention.
    /// </summary>
    [CreateAssetMenu(menuName = "Khela/Video Poker/Card Skin", fileName = "VpCardSkin")]
    public sealed class VpCardSkin : ScriptableObject
    {
        [Tooltip("Shown in skin pickers / the store.")]
        public string displayName = "Default";

        [Tooltip("Which suit occupies each block of 13. Reorder to match how you filled 'faces'.")]
        public CardSuit[] suitOrder = { CardSuit.Hearts, CardSuit.Spades, CardSuit.Clubs, CardSuit.Diamonds };

        [Tooltip("52 face sprites, suit-major (see class doc): [suitOrder[0] Two..Ace][suitOrder[1] Two..Ace]…")]
        public Sprite[] faces = new Sprite[52];

        [Tooltip("The card back (shown for a face-down card).")]
        public Sprite back;

        /// <summary>The sprite for a card — its face when up, the back when down. Null if the slot isn't assigned.</summary>
        public Sprite For(CardId card) => card.FaceUp ? Face(card.Rank, card.Suit) : back;

        /// <summary>The face sprite for a (rank, suit), by identity — immune to enum ordering.</summary>
        public Sprite Face(CardRank rank, CardSuit suit)
        {
            int s = Array.IndexOf(suitOrder, suit);
            if (s < 0) s = 0;
            int r = Mathf.Clamp((int)rank - 2, 0, 12);   // Two→0 .. Ace→12
            int idx = s * 13 + r;
            return faces != null && idx >= 0 && idx < faces.Length ? faces[idx] : null;
        }
    }
}
