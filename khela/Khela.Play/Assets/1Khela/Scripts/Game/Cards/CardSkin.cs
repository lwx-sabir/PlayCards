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

        [Tooltip("Set this for a card MESH whose front face is UV-mapped to the WHOLE card ([0,1]) — a standard card " +
                 "model such as KayKit's card_base — instead of to ONE atlas cell (the bundled CardBase mesh). It scales " +
                 "the sheet DOWN onto a cell rather than only sliding the offset. Leave OFF for the bundled mesh; " +
                 "invertV is ignored when this is on.")]
        public bool fullCardUv = false;

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
            int col = ColumnFor(card.Rank);
            int row = RowFor(card.Suit);
            if (card.FaceUp)
            {
                texture = frontAtlas;
                if (fullCardUv)
                {
                    // Front face UV covers the WHOLE card [0,1]: scale it DOWN onto one atlas cell — tiling
                    // 1/columns × 1/rows, offset to the cell. Row 0 sits at the TOP of the sheet (high V).
                    baseMapST = new Vector4(1f / columns, 1f / rows, col / (float)columns, (rows - 1 - row) / (float)rows);
                }
                else
                {
                    // Front face UV is already ONE cell, so tiling stays 1 and we only slide the offset.
                    float v = row / (float)rows;
                    if (invertV) v = -v;
                    baseMapST = new Vector4(1f, 1f, col / (float)columns, v);
                }
            }
            else
            {
                texture = back;
                if (fullCardUv)
                {
                    // Full-card UV → the whole back image shows at tiling 1, offset 0.
                    baseMapST = new Vector4(1f, 1f, 0f, 0f);
                }
                else
                {
                    // Zoom the one-cell UV island back out to the whole back image. When invertV, the front selects rows
                    // with a NEGATIVE V offset, so the mesh's UV island sits at the TOP row of the sheet (V ≈
                    // [(rows-1)/rows, 1]). Shift the V origin DOWN by (rows-1) before the ×rows zoom, or the scaled V
                    // wraps onto the sheet's edge strip and the back renders as just its border colour.
                    float offsetV = invertV ? -(rows - 1) : 0f;
                    baseMapST = new Vector4(columns, rows, 0f, offsetV);
                }
            }
        }
    }
}
