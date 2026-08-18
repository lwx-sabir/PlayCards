using System;
using System.Collections;
using PlayCard.Game.Cards;          // CardId identity only
using PlayCard.VideoPoker.Cards;    // VpCardSkin — VP's own per-card sprites
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PlayCard.VideoPoker.Game
{
    /// <summary>
    /// One of the five video-poker card slots (pure UGUI). Renders the card the SERVER dealt by setting a UGUI
    /// <see cref="Image"/>'s sprite from the VP skin (<see cref="VpCardSkin"/> = 52 individual PNGs — VP's OWN look,
    /// not the 3D tables' atlas). Tapping toggles HOLD (a client-side selection sent to the server as the draw mask);
    /// the client never draws or invents a card. A short flip tween sells the deal + the replace-on-draw.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public sealed class CardSlot : MonoBehaviour, IPointerClickHandler
    {
        [Header("Refs")]
        [Tooltip("The Image that shows the card sprite.")]
        [SerializeField] private Image face;
        [Tooltip("VP card skin: 52 face sprites + a back. Same asset the whole machine uses (swappable for a store skin).")]
        [SerializeField] private VpCardSkin skin;
        [Tooltip("Shown while this card is held (the 'HELD' ribbon). Toggled on hold.")]
        [SerializeField] private GameObject heldBadge;
        [Tooltip("Optional: a highlight/border image tinted when held.")]
        [SerializeField] private Graphic holdHighlight;

        [Header("Feel")]
        [Tooltip("Pixels the card lifts up while held.")]
        [SerializeField] private float heldLift = 14f;
        [Tooltip("Half-flip duration (seconds, unscaled). Total flip = 2x this.")]
        [SerializeField] private float halfFlipSeconds = 0.09f;

        /// <summary>Fired when the player toggles hold on this slot; arg is <see cref="Index"/>.</summary>
        public event Action<int> OnHoldToggled;

        /// <summary>0..4 — set by the view so the controller knows which card this is.</summary>
        public int Index { get; set; }
        public bool Held { get; private set; }

        private bool _holdEnabled;
        private Vector2 _restPos;
        private RectTransform _rt;
        private Coroutine _flip;

        private void Awake()
        {
            _rt = (RectTransform)transform;
            _restPos = _rt.anchoredPosition;
        }

        /// <summary>Set the skin at runtime (store / skin-picker restyle).</summary>
        public void SetSkin(VpCardSkin s) => skin = s;

        /// <summary>Render a card immediately (no flip). Use for a reconnect/resync.</summary>
        public void Set(CardId card)
        {
            if (_flip != null) { StopCoroutine(_flip); _flip = null; SetScaleX(1f); }
            Render(card);
        }

        /// <summary>Render a card with a flip (deal-in, or the replace on draw). Falls back to <see cref="Set"/> when inactive.</summary>
        public void Show(CardId card, bool flip)
        {
            if (!flip || !gameObject.activeInHierarchy) { Render(card); return; }
            if (_flip != null) StopCoroutine(_flip);
            _flip = StartCoroutine(FlipTo(card));
        }

        public void SetHoldEnabled(bool enabled) => _holdEnabled = enabled;

        public void SetHeld(bool held)
        {
            Held = held;
            if (heldBadge) heldBadge.SetActive(held);
            if (holdHighlight) holdHighlight.enabled = held;
            _rt.anchoredPosition = held ? _restPos + Vector2.up * heldLift : _restPos;
            // Hook: add your spring/squash + a hold "click" here (see client-button-feel).
        }

        /// <summary>Reset to an empty, un-held, hold-disabled slot (between hands).</summary>
        public void Clear()
        {
            SetHeld(false);
            _holdEnabled = false;
            if (face) face.enabled = false;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!_holdEnabled) return;              // holds only during the "dealt" phase
            SetHeld(!Held);
            OnHoldToggled?.Invoke(Index);
        }

        // ---- rendering off the VP skin ----

        private void Render(CardId card)
        {
            if (face == null || skin == null) return;
            var sprite = skin.For(card);
            face.enabled = sprite != null;
            face.sprite = sprite;
        }

        private IEnumerator FlipTo(CardId card)
        {
            yield return LerpScaleX(1f, 0f);
            Render(card);
            yield return LerpScaleX(0f, 1f);
            _flip = null;
        }

        private IEnumerator LerpScaleX(float from, float to)
        {
            float t = 0f;
            float dur = Mathf.Max(0.01f, halfFlipSeconds);
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                SetScaleX(Mathf.Lerp(from, to, t / dur));
                yield return null;
            }
            SetScaleX(to);
        }

        private void SetScaleX(float x)
        {
            var s = _rt.localScale;
            s.x = x;
            _rt.localScale = s;
        }
    }
}
