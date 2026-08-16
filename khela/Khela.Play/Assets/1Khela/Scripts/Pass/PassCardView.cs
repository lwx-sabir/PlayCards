using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Pass
{
    /// <summary>
    /// What a pass day looks like right now. The SERVER decides this — the client never infers it from dates.
    /// </summary>
    public enum PassCardState
    {
        /// <summary>Nothing to do yet, or nothing special — the prefab's own art.</summary>
        Default = 0,
        /// <summary>Already taken: the Collected badge shows and the card stops responding to taps.</summary>
        Collected = 1,
        /// <summary>A missed day only the Golden subscription reaches.</summary>
        Locked = 2,
        /// <summary>A missed day the player can buy back with rewarded ads.</summary>
        AdUnlockable = 3,
    }

    /// <summary>
    /// The component on every pass slot prefab — free or golden, one item or two, plain or collectible.
    ///
    /// A pure VIEW: no networking, no claim logic. Which prefab to spawn is the screen's decision (collectible while
    /// uncollected, plain once taken); this only fills in the art, the value and the badges.
    ///
    /// Every reference is optional. Assign <see cref="image2"/> only on the 2-item variants — the prefab itself
    /// declares whether it holds one reward or two, so nothing here needs to be told.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PassCardView : MonoBehaviour
    {
        [Header("Rewards")]
        [Tooltip("Image_Item — the reward's icon.")]
        [SerializeField] private Image image1;
        [Tooltip("Image_Item_2 — only on the 2-item variants; leave empty otherwise.")]
        [SerializeField] private Image image2;
        [Tooltip("Text_Value — the amount, or the combined label on a 2-item card (\"2M + 10\").")]
        [SerializeField] private TMP_Text value;

        [Header("States — leave empty where the variant has none")]
        [Tooltip("Collected badge; shown once the day has been taken.")]
        [SerializeField] private GameObject collected;
        [Tooltip("Padlock for a missed day that only Golden unlocks.")]
        [SerializeField] private GameObject locked;
        [Tooltip("Ad badge for a missed day the player can buy back with rewarded ads.")]
        [SerializeField] private GameObject adUnlock;
        [Tooltip("Optional label on the ad badge, e.g. \"2 ads\".")]
        [SerializeField] private TMP_Text adCostText;

        [Header("Interaction")]
        [SerializeField] private Button button;

        /// <summary>Raised when the card is tapped. The screen decides what a tap means for the current state —
        /// claim, watch ads, or open the subscribe sheet.</summary>
        public event Action<PassCardView> Clicked;

        /// <summary>Day of the cycle this card is bound to (1-based).</summary>
        public int Day { get; private set; }

        public PassCardState State { get; private set; }

        private ParticleSystemRenderer[] _fx;
        private ParticleSystem[] _systems;
        private bool _fxVisible = true;

        private void Awake()
        {
            if (button != null) button.onClick.AddListener(() => Clicked?.Invoke(this));

            // Particles are NOT UI graphics: a RectMask2D/Mask can't clip them, so a card scrolled out of the
            // viewport would still spray sparkles over the rest of the screen. The screen culls them by viewport
            // instead; renderers are cached here so that costs nothing per scroll.
            _fx = GetComponentsInChildren<ParticleSystemRenderer>(true);
            _systems = GetComponentsInChildren<ParticleSystem>(true);
        }

        /// <summary>Show/hide this card's particle FX. Toggles the RENDERER, not the GameObject, so the systems keep
        /// simulating and don't restart with a visible pop when the card scrolls back in.</summary>
        public void SetFxVisible(bool visible)
        {
            if (_fx == null) return;
            for (int i = 0; i < _fx.Length; i++)
                if (_fx[i] != null && _fx[i].enabled != visible) _fx[i].enabled = visible;
        }

        /// <summary>
        /// Show each particle system only while its OWN world bounds fit inside <paramref name="viewport"/>.
        ///
        /// Testing the card's rect isn't enough: the glow is drawn well outside the card, so a card at the edge of
        /// the ScrollRect — even one that is itself fully visible — sprays sparkles over whatever sits beside the
        /// viewport. Particle bounds are the only thing that matches what actually gets drawn.
        /// </summary>
        public void CullFxTo(Rect viewport)
        {
            if (_fx == null || _fx.Length == 0) return;

            // Judge by the CARD's rect, not the particles' bounds: a system whose particles simulate in world space
            // leaves its bounds behind when the card scrolls, so the bounds stop being a reliable test of "is this
            // card on screen". The card rect always is.
            var rect = (RectTransform)transform;
            rect.GetWorldCorners(CornerBuffer);
            bool inside = CornerBuffer[0].x >= viewport.xMin && CornerBuffer[2].x <= viewport.xMax &&
                          CornerBuffer[0].y >= viewport.yMin && CornerBuffer[2].y <= viewport.yMax;

            if (inside == _fxVisible) return;
            _fxVisible = inside;

            for (int i = 0; i < _fx.Length; i++)
                if (_fx[i] != null) _fx[i].enabled = inside;

            // Kill anything already in flight. A renderer toggle hides the system, but particles emitted in WORLD
            // simulation space live on independently of the card — clearing is what actually removes them.
            if (_systems == null) return;
            for (int i = 0; i < _systems.Length; i++)
            {
                var system = _systems[i];
                if (system == null) continue;

                if (inside) system.Play(false);
                else { system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
            }
        }

        private static readonly Vector3[] CornerBuffer = new Vector3[4];

        private void OnDestroy()
        {
            if (button != null) button.onClick.RemoveAllListeners();
            Clicked = null;
        }

        /// <summary>
        /// Fill the card. <paramref name="icon1"/>/<paramref name="icon2"/> may be null — the prefab's own sprite is
        /// kept, so a reward with no configured art still looks right.
        /// </summary>
        public void Bind(int day, string text, Sprite icon1, Sprite icon2, PassCardState state, int adCost = 0)
        {
            Day = day;   // identity only — the day NUMBER is drawn by the marker on the progress bar, not on the card
            if (value != null) value.text = text ?? string.Empty;
            if (image1 != null && icon1 != null) image1.sprite = icon1;
            if (image2 != null && icon2 != null) image2.sprite = icon2;
            SetState(state, adCost);
        }

        /// <summary>Drop in one icon once its download finishes. Slot 0 is the first reward, 1 the second.</summary>
        public void SetIcon(int slot, Sprite sprite)
        {
            if (sprite == null) return;
            var image = slot == 0 ? image1 : slot == 1 ? image2 : null;
            if (image != null) image.sprite = sprite;
        }

        /// <summary>Flip just the state — for the moment a claim lands, without re-binding the art.</summary>
        public void SetState(PassCardState state, int adCost = 0)
        {
            State = state;

            Show(collected, state == PassCardState.Collected);
            Show(locked, state == PassCardState.Locked);
            Show(adUnlock, state == PassCardState.AdUnlockable);

            if (adCostText != null && state == PassCardState.AdUnlockable)
                adCostText.text = adCost > 1 ? $"{adCost} ads" : "1 ad";

            // A collected day is finished. Everything else stays tappable — even a locked card, since tapping it
            // is what opens the subscribe sheet.
            if (button != null) button.interactable = state != PassCardState.Collected;
        }

        private static void Show(GameObject target, bool on)
        {
            if (target != null && target.activeSelf != on) target.SetActive(on);
        }
    }
}
