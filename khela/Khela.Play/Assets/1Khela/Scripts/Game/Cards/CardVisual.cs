using UnityEngine;
using PlayCard.Game.Dtos;

namespace PlayCard.Game.Cards
{
    /// <summary>
    /// Draws ONE playing card, driven by the server's board snapshot. Supersedes the asset pack's
    /// <c>CardInstance</c>.
    ///
    /// Design notes:
    /// • Server-authoritative: this only DISPLAYS a card the server dealt; it never decides a value.
    ///   A face-down card (dealer hole card) renders the back even though the snapshot may still carry
    ///   its real rank/suit — the client refuses to show it.
    /// • A <see cref="MaterialPropertyBlock"/> sets the face per-card, so we don't clone a material per
    ///   card (the pack's leak) and don't need a physical flip — face-up vs face-down just swaps the
    ///   texture on the camera-facing material slot.
    /// • All art/layout comes from <see cref="CardSkin"/>, so a new design or a runtime skin change is
    ///   a one-line <see cref="Skin"/> assignment.
    ///
    /// Requires the face material to use a shader that exposes the skin's base-map property
    /// (URP Lit/Unlit → "_BaseMap"). The bundled CardFront/CardBack materials are built-in Standard
    /// and must be converted to URP first, or they render magenta under this project's pipeline.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CardVisual : MonoBehaviour
    {
        [Tooltip("Renderer of the card mesh; material slot 0 faces the camera. Auto-found if empty.")]
        [SerializeField] private Renderer cardRenderer;

        [Tooltip("Material slot that faces the camera (front of the card).")]
        [SerializeField] private int faceMaterialIndex = 0;

        [Tooltip("Card art. Assign in the inspector; reassign at runtime to reskin.")]
        [SerializeField] private CardSkin skin;

        private MaterialPropertyBlock _mpb;
        private CardId _card;
        private bool _hasCard;

        /// <summary>Active skin. Setting it re-renders the current card.</summary>
        public CardSkin Skin
        {
            get => skin;
            set { skin = value; if (_hasCard) Apply(); }
        }

        public CardId Current => _card;

        private void Reset() => cardRenderer = GetComponentInChildren<Renderer>();

        private void Awake()
        {
            if (cardRenderer == null) cardRenderer = GetComponentInChildren<Renderer>();
        }

        // ----- public API: the bridge from board data to pixels -----

        public void SetCard(CardId card)
        {
            _card = card;
            _hasCard = true;
            Apply();
        }

        /// <summary>Map + render straight from a board-snapshot card.</summary>
        public void SetCard(CardView wire) => SetCard(CardId.FromWire(wire));

        /// <summary>Flip the current card (e.g. the dealer reveal) without re-sending its value.</summary>
        public void SetFaceUp(bool faceUp)
        {
            if (!_hasCard) return;
            _card = _card.WithFaceUp(faceUp);
            Apply();
        }

        private void Apply()
        {
            if (skin == null || cardRenderer == null) return;

            _mpb ??= new MaterialPropertyBlock();
            skin.GetFace(_card, out var texture, out var baseMapST);

            cardRenderer.GetPropertyBlock(_mpb, faceMaterialIndex);
            if (texture != null) _mpb.SetTexture(skin.baseMapProperty, texture);
            _mpb.SetVector(skin.baseMapStProperty, baseMapST);
            cardRenderer.SetPropertyBlock(_mpb, faceMaterialIndex);
        }
    }
}
