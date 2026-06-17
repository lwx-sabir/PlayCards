using UnityEngine;

namespace PlayCard.Game.Cards
{
    /// <summary>
    /// A complete card look: the face atlas + how it's laid out + the back design.
    /// Adding a new card design later = create another CardSkin asset and drop in new textures.
    /// No code changes, and (because backs are a classic cosmetic) this doubles as the unit a
    /// store / skin-picker hands to the table to restyle every card at runtime.
    ///
    /// Create via: Assets ▸ Create ▸ PlayCard ▸ Cards ▸ Card Skin.
    ///
    /// Layout model: the card mesh's front face is UV-mapped to exactly ONE cell of the atlas
    /// grid, so a face is selected by OFFSET alone (tiling stays 1). That matches the bundled
    /// CardBase mesh + Atlas..png. A reskin only needs an atlas with the same grid shape.
    /// </summary>
    [CreateAssetMenu(menuName = "Khela/Cards/Card Skin", fileName = "CardSkin")]
    public sealed class CardSkin : ScriptableObject
    {
        [Tooltip("Shown in skin pickers / the store.")]
        public string displayName = "Default";

        [Header("Front — single atlas holding every face")]
        public Texture frontAtlas;

        [Tooltip("Rank columns across the sheet (Two..Ace = 13).")]
        [Min(1)] public int columns = 13;

        [Tooltip("Rows in the sheet. The bundled atlas is a 13x5 grid (4 suit rows used).")]
        [Min(1)] public int rows = 5;

        [Tooltip("Atlas rows TOP→BOTTOM, i.e. which suit sits on which row of THIS art. " +
                 "Bundled Atlas..png = Hearts, Spades, Clubs, Diamonds. Reorder to match new art.")]
        public CardSuit[] rowOrderTopToBottom =
        {
            CardSuit.Hearts, CardSuit.Spades, CardSuit.Clubs, CardSuit.Diamonds
        };

        [Tooltip("Bundled atlas pages rows with a negative V offset. Flip if a new atlas reads upside-down.")]
        public bool invertV = true;

        [Header("Back design")]
        public Texture back;

        [Header("Shader property names")]
        [Tooltip("URP Lit/Unlit defaults. For the legacy built-in Standard shader use _MainTex / _MainTex_ST.")]
        public string baseMapProperty = "_BaseMap";
        public string baseMapStProperty = "_BaseMap_ST";

        /// <summary>Atlas column for a rank (Two→0 .. Ace→columns-1).</summary>
        public int ColumnFor(CardRank rank) => Mathf.Clamp((int)rank - 2, 0, columns - 1);

        /// <summary>Atlas row for a suit, by identity — immune to enum/int ordering differences.</summary>
        public int RowFor(CardSuit suit)
        {
            int i = System.Array.IndexOf(rowOrderTopToBottom, suit);
            return i < 0 ? 0 : i;
        }

        /// <summary>
        /// Texture + tiling/offset (packed as _BaseMap_ST: xy = tiling, zw = offset) for the
        /// camera-facing face. Face-up shows one atlas cell; face-down shows the full back.
        /// </summary>
        public void GetFace(CardId card, out Texture texture, out Vector4 baseMapST)
        {
            if (card.FaceUp)
            {
                texture = frontAtlas;
                float u = ColumnFor(card.Rank) / (float)columns;
                float v = RowFor(card.Suit) / (float)rows;
                if (invertV) v = -v;
                // mesh UV island == one cell, so tiling stays 1 and we only slide the offset
                baseMapST = new Vector4(1f, 1f, u, v);
            }
            else
            {
                texture = back;
                // zoom the one-cell UV island back out to the whole image for the card back
                baseMapST = new Vector4(columns, rows, 0f, 0f);
            }
        }
    }
}
