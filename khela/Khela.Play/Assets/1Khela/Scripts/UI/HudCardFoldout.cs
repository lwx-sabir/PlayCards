using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// A HUD card that lives folded inside a small side button and unfolds out of it on tap — the pass and piggy
    /// cards on Home. Two motions sell it, and they must run together:
    ///
    ///  • The CARD grows from the button: its position starts at the button's centre and travels to the authored
    ///    spot while the scale runs up staggered — width first, height a beat later with a settle — plus a small
    ///    hinge rotation. The stagger is what reads as "unfolding"; a uniform scale just reads as "zoomed".
    ///
    ///  • The button's ICON flies into the card's authored icon slot — a rect-to-rect morph in the fly layer. The
    ///    card's own icon stays hidden until the flyer lands on it, and the button's icon stays gone while its card
    ///    is open, so at every moment there is exactly ONE of this icon on screen. That continuity is the whole
    ///    trick: the eye tracks one object changing place, and the two layouts become one thing.
    ///
    /// Close plays the same film backwards, ending with the icon shrinking back into the button. A tap anywhere
    /// outside closes every open card (one shared transparent catcher, created on demand behind the open panels).
    ///
    /// MUST live on an always-active object (the button, or the side menu) — it deactivates the panel, and a
    /// component on the panel would deactivate its own watcher with it. Other systems may call <see cref="Open"/>
    /// directly for the occasional unprompted show; the animation is identical.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HudCardFoldout : MonoBehaviour
    {
        [Header("The pair")]
        [Tooltip("The compact side button the card folds into. Its onClick is wired here — no manual hookup.")]
        [SerializeField] private Button button;
        [Tooltip("The icon INSIDE the button — the thing that appears to travel into the card. Needs an Image.")]
        [SerializeField] private RectTransform buttonIcon;
        [Tooltip("The authored card (e.g. GoldenPass_Info / Piggy_Bank group). Authored OPEN at its final spot; " +
                 "it is captured and closed on Awake when Start Closed is on.")]
        [SerializeField] private RectTransform panel;
        [Tooltip("The icon INSIDE the card that the button icon becomes. Needs an Image.")]
        [SerializeField] private RectTransform panelIcon;

        [Header("Layers")]
        [Tooltip("Canvas-space overlay the morphing icon flies in — the scene's FlyLayer. Must draw ABOVE the card.")]
        [SerializeField] private RectTransform flyLayer;

        [Header("Feel")]
        [SerializeField] private float openSeconds = 0.38f;
        [Tooltip("A touch quicker than the open — an exit that takes as long as an entrance reads as slow UI.")]
        [SerializeField] private float closeSeconds = 0.3f;
        [Tooltip("How far past full size the height settles on open. The 'card lands' beat.")]
        [Range(0f, 0.4f)][SerializeField] private float overshoot = 0.14f;
        [Tooltip("Opening hinge, in degrees around X — the fold. Small: on an overlay canvas a big X-rotation " +
                 "reads as a skew, not a fold.")]
        [Range(0f, 70f)][SerializeField] private float hingeDegrees = 32f;
        [Tooltip("How far the height lags the width, as a fraction of the open. THE unfold knob: 0 = plain zoom.")]
        [Range(0f, 0.6f)][SerializeField] private float unfoldStagger = 0.3f;
        [Tooltip("The handoff: once the travelling icon has fully taken the destination icon's place, it fades away " +
                 "over this long, revealing the real icon it was covering — the moment one icon 'becomes' the other. " +
                 "Also what absorbs any difference between the two icons' art.")]
        [SerializeField] private float iconHandoffFade = 0.16f;

        [Header("Behaviour")]
        [Tooltip("Close the panel on Awake and open only by tap / code. Leave the panel authored OPEN in the scene " +
                 "so its layout stays editable; this captures the authored pose first.")]
        [SerializeField] private bool startClosed = true;
        [Tooltip("Opening this card closes the others. Off = the cards stack, as authored.")]
        [SerializeField] private bool closeOthersOnOpen = false;

        /// <summary>Every foldout that is currently open — what the outside-tap catcher closes.</summary>
        private static readonly List<HudCardFoldout> s_open = new();
        private static GameObject s_catcher;

        public bool IsOpen { get; private set; }

        // The authored pose. Position is kept as anchoredPosition3D — the SERIALIZED truth — never localPosition:
        // localPosition is a derived value the layout system recomputes from the anchors when the CanvasScaler
        // applies the real screen size (after Awake), so a localPosition snapshot quietly goes stale and the card
        // would open to a place the author never chose.
        private Vector3 _restAnchored;
        private Vector3 _restScale;
        private Quaternion _restRot;
        private CanvasGroup _panelGroup;
        private CanvasGroup _buttonGroup;   // fades the WHOLE button (bg, label, icon) out while its card is open
        private Image _buttonImage;    // the button's real icon…
        private Image _panelImage;     // …the card's authored icon (hidden while the stand-in covers for it)…
        private Image _morph;          // …and the stand-in that flies between button and card
        private Sequence _seq;

        private void Awake()
        {
            if (panel != null)
            {
                _restAnchored = panel.anchoredPosition3D;
                _restScale = panel.localScale;
                _restRot = panel.localRotation;
                _panelGroup = panel.GetComponent<CanvasGroup>();
                if (_panelGroup == null) _panelGroup = panel.gameObject.AddComponent<CanvasGroup>();
            }

            if (buttonIcon != null) _buttonImage = buttonIcon.GetComponent<Image>();
            if (panelIcon != null) _panelImage = panelIcon.GetComponent<Image>();
            if (button != null)
            {
                button.onClick.AddListener(Toggle);
                _buttonGroup = button.GetComponent<CanvasGroup>();
                if (_buttonGroup == null) _buttonGroup = button.gameObject.AddComponent<CanvasGroup>();
            }

            if (startClosed && panel != null) panel.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (button != null) button.onClick.RemoveListener(Toggle);
            _seq?.Kill();
            s_open.Remove(this);
            RefreshCatcher();
        }

        private void OnDisable()
        {
            // Leave everything at rest — a tween cut short mid-close would otherwise store the panel shrunk and
            // rotated, and that is the pose the NEXT open would run from.
            _seq?.Kill();
            _seq = null;
            if (panel != null)
            {
                panel.anchoredPosition3D = _restAnchored;
                panel.localScale = _restScale;
                panel.localRotation = _restRot;
                if (_panelGroup != null) { _panelGroup.alpha = 1f; _panelGroup.blocksRaycasts = true; }
                panel.gameObject.SetActive(IsOpen);
            }
            SetIcon(_buttonImage, !IsOpen);
            SetIcon(_panelImage, true);
            if (_buttonGroup != null)
            {
                _buttonGroup.DOKill();
                _buttonGroup.alpha = IsOpen ? 0f : 1f;
                _buttonGroup.interactable = _buttonGroup.blocksRaycasts = !IsOpen;
            }
            if (_morph != null) _morph.gameObject.SetActive(false);
        }

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        /// <summary>Unfold the card out of its button. Also the entry point for unprompted shows.</summary>
        public void Open()
        {
            if (IsOpen || panel == null) return;
            IsOpen = true;

            if (closeOthersOnOpen)
                for (int i = s_open.Count - 1; i >= 0; i--)
                    if (s_open[i] != this) s_open[i].Close();

            if (!s_open.Contains(this)) s_open.Add(this);
            RefreshCatcher();

            _seq?.Kill();
            panel.gameObject.SetActive(true);

            // The card must END exactly on the authored anchoredPosition. The button's position becomes an OFFSET
            // from that rest pose, measured NOW — at tap time the layout is settled, unlike at Awake — and applied
            // in anchored space (anchored and local share the parent's axes, so a local-space delta is valid there).
            panel.anchoredPosition3D = _restAnchored;
            Vector3 fromOffset = panel.parent.InverseTransformPoint(ButtonCentreWorld()) - panel.localPosition;
            float born = FoldedScale();

            panel.anchoredPosition3D = _restAnchored + fromOffset;
            panel.localScale = new Vector3(_restScale.x * born, _restScale.y * born * 0.6f, _restScale.z);
            panel.localRotation = _restRot * Quaternion.Euler(-hingeDegrees, 0f, 0f);
            _panelGroup.alpha = 0f;
            _panelGroup.blocksRaycasts = true;

            float lag = openSeconds * unfoldStagger;

            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Insert(0f, panel.DOAnchorPos3D(_restAnchored, openSeconds).SetEase(Ease.OutCubic));
            seq.Insert(0f, panel.DOScaleX(_restScale.x, openSeconds - lag).SetEase(Ease.OutCubic));
            seq.Insert(lag, panel.DOScaleY(_restScale.y, openSeconds - lag).SetEase(Ease.OutBack, 1f + overshoot * 5f));
            seq.Insert(0f, panel.DOLocalRotateQuaternion(_restRot, openSeconds * 0.8f).SetEase(Ease.OutCubic));
            seq.Insert(0f, _panelGroup.DOFade(1f, openSeconds * 0.4f).SetEase(Ease.OutQuad));
            seq.OnComplete(() => _seq = null);
            _seq = seq;

            FlyIcon(opening: true, openSeconds);

            // The whole button gives way as its card takes over — quick, so the card doesn't unfold over a still-
            // solid button. The icon has already lifted off separately; this fades the bg and label after it.
            FadeButton(0f, openSeconds * 0.45f);
        }

        /// <summary>Fold the card back into its button — the opening, backwards.</summary>
        public void Close()
        {
            if (!IsOpen || panel == null) return;
            IsOpen = false;

            s_open.Remove(this);
            RefreshCatcher();

            _seq?.Kill();
            _panelGroup.blocksRaycasts = false;

            // Same anchored-space arithmetic as the open, and valid even mid-flight: the rest localPosition is
            // recovered from where the panel is NOW minus how far its anchoredPosition currently sits from rest.
            Vector3 restLocal = panel.localPosition - (panel.anchoredPosition3D - _restAnchored);
            Vector3 toAnchored = _restAnchored + (panel.parent.InverseTransformPoint(ButtonCentreWorld()) - restLocal);
            float born = FoldedScale();
            float lag = closeSeconds * unfoldStagger;

            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Insert(0f, panel.DOAnchorPos3D(toAnchored, closeSeconds).SetEase(Ease.InCubic));
            // Height folds first, width follows — the exact reverse of the open's stagger.
            seq.Insert(0f, panel.DOScaleY(_restScale.y * born * 0.6f, closeSeconds - lag).SetEase(Ease.InCubic));
            seq.Insert(lag, panel.DOScaleX(_restScale.x * born, closeSeconds - lag).SetEase(Ease.InCubic));
            seq.Insert(0f, panel.DOLocalRotateQuaternion(_restRot * Quaternion.Euler(-hingeDegrees, 0f, 0f), closeSeconds)
                .SetEase(Ease.InCubic));
            seq.Insert(closeSeconds * 0.55f, _panelGroup.DOFade(0f, closeSeconds * 0.45f).SetEase(Ease.InQuad));
            seq.OnComplete(() =>
            {
                _seq = null;
                panel.gameObject.SetActive(false);
                panel.anchoredPosition3D = _restAnchored;   // park at the authored pose for the next open
                panel.localScale = _restScale;
                panel.localRotation = _restRot;
                _panelGroup.alpha = 1f;
                _panelGroup.blocksRaycasts = true;
            });
            _seq = seq;

            FlyIcon(opening: false, closeSeconds);

            // The button rematerialises UNDER the arriving card — starting a beat in and finishing with the fold,
            // so the flyer's handoff at the end fades out over a fully solid button.
            FadeButton(1f, closeSeconds * 0.6f, closeSeconds * 0.35f);
        }

        /// <summary>Close every open card — what the outside-tap catcher fires.</summary>
        public static void CloseAll()
        {
            for (int i = s_open.Count - 1; i >= 0; i--) s_open[i].Close();
        }

        // ---------------- the icon morph ----------------

        /// <summary>
        /// The authored icons never move — what flies is a stand-in wearing the button icon's face, and there is
        /// only ever ONE of this icon visible. On open the button icon lifts off (the real one hides under the
        /// stand-in, 1:1), flies, and parks EXACTLY over the card's icon slot — which has stayed invisible the
        /// whole flight. Only once it has fully taken that place does the handoff run: the authored icon is revealed
        /// beneath the opaque stand-in and the stand-in fades away over it, so the button icon visibly BECOMES the
        /// card's icon. Close is the mirror: the stand-in materialises over the card's icon (which hides once the
        /// takeover completes), flies home, and fades away over the revealed button icon.
        /// </summary>
        private void FlyIcon(bool opening, float seconds)
        {
            if (buttonIcon == null || _buttonImage == null || panelIcon == null || flyLayer == null)
            {
                // Half-wired: keep both icons sane rather than flying nothing — the card works, minus the trick.
                SetIcon(_buttonImage, !IsOpen);
                SetIcon(_panelImage, true);
                return;
            }

            if (_morph == null)
            {
                var go = new GameObject("IconMorph", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(flyLayer, false);
                go.SetActive(false);   // parked. A new GameObject is born ACTIVE — left so, the first flight would
                                       // read it as "already flying", skip PlaceOver, and launch from the layer's
                                       // centre at default size instead of from the button.
                _morph = go.GetComponent<Image>();
                _morph.raycastTarget = false;
                _morph.preserveAspect = true;
            }

            bool wasFlying = _morph.gameObject.activeSelf;   // interrupted mid-flight → continue from where it is
            _morph.gameObject.SetActive(true);
            _morph.transform.SetAsLastSibling();
            _morph.sprite = _buttonImage.sprite;

            var rt = (RectTransform)_morph.transform;
            rt.DOKill();
            _morph.DOKill();

            if (opening)
            {
                if (!wasFlying) PlaceOver(rt, buttonIcon);
                SetIcon(_buttonImage, false);        // invisible lift-off; it stays gone while the card is open
                SetIcon(_panelImage, false);         // the card unfolds with an EMPTY icon slot, awaiting the flyer
                SetAlpha(_morph, 1f);

                FlyMorphTo(panelIcon, seconds, Ease.OutCubic, () =>
                {
                    // Fully in place, card fully unfolded — NOW the becoming: reveal the authored icon beneath
                    // the opaque stand-in and dissolve the stand-in over it. Overlapped and at rest, the fade
                    // reads as one object turning into the other; any art difference is absorbed by it.
                    SetIcon(_panelImage, true);
                    _morph.DOFade(0f, iconHandoffFade).SetUpdate(true)
                        .OnComplete(() => _morph.gameObject.SetActive(false));
                });
            }
            else
            {
                // Lift-off from the card. Normally the authored icon is visible and the stand-in materialises over
                // it, hiding it once the takeover completes. If a mid-open interrupt left the slot still empty, the
                // stand-in is simply the same flyer continuing — it departs opaque, no materialise.
                bool takeover = !wasFlying && _panelImage != null && _panelImage.enabled;
                if (!wasFlying) PlaceOver(rt, panelIcon);
                if (takeover)
                {
                    SetAlpha(_morph, 0f);
                    _morph.DOFade(1f, seconds * 0.35f).SetUpdate(true)
                        .OnComplete(() => SetIcon(_panelImage, false));
                }
                else SetAlpha(_morph, 1f);

                FlyMorphTo(buttonIcon, seconds, Ease.InCubic, () =>
                {
                    // Home, at button size — the mirror handoff: the real button icon appears beneath and the
                    // stand-in fades away over it. Identical art makes it seamless; differing art, a morph.
                    SetIcon(_buttonImage, true);
                    _morph.DOFade(0f, iconHandoffFade).SetUpdate(true)
                        .OnComplete(() => _morph.gameObject.SetActive(false));
                });
            }
        }

        /// <summary>
        /// Fly the stand-in from where it is NOW to a destination rect that is re-read EVERY FRAME. The destination
        /// cannot be captured up front: on open it is the icon slot of a card that is itself mid-unfold, so a fixed
        /// target would be the slot of the FOLDED card — tiny, at the button — and the flyer would shrink along a
        /// wrong trajectory toward a place the card has already left (exactly the bug this replaces). Live tracking
        /// also means the flyer rides the card's settle and lands pixel-perfect however the unfold moves.
        /// </summary>
        private void FlyMorphTo(RectTransform dest, float seconds, Ease ease, TweenCallback onArrived)
        {
            var rt = (RectTransform)_morph.transform;
            Vector2 srcPos = rt.anchoredPosition;
            Vector2 srcSize = rt.sizeDelta;

            DOVirtual.Float(0f, 1f, seconds, v =>
                {
                    RectInLayer(dest, out var dPos, out var dSize);
                    rt.anchoredPosition = Vector2.LerpUnclamped(srcPos, dPos, v);
                    rt.sizeDelta = Vector2.LerpUnclamped(srcSize, dSize, v);
                })
                .SetEase(ease).SetUpdate(true).SetTarget(rt)   // SetTarget: the rt.DOKill() above kills this too
                .OnComplete(onArrived);
        }

        /// <summary>Fade the whole button (bg, label, icon slot) — out while its card is open, back as it folds
        /// home. Interactivity follows the target state immediately, so a fading-out button can't be tapped and a
        /// returning one can.</summary>
        private void FadeButton(float to, float seconds, float delay = 0f)
        {
            if (_buttonGroup == null) return;
            bool show = to > 0.5f;
            _buttonGroup.DOKill();
            _buttonGroup.interactable = _buttonGroup.blocksRaycasts = show;
            _buttonGroup.DOFade(to, seconds).SetDelay(delay).SetUpdate(true);
        }

        private static void SetAlpha(Image img, float a)
        {
            var c = img.color;
            c.a = a;
            img.color = c;
        }

        private static void SetIcon(Image img, bool on)
        {
            if (img != null && img.enabled != on) img.enabled = on;
        }

        /// <summary>A rect's centre and size expressed in the fly layer's space, via world corners — parent, scale
        /// and anchor layout of the two ends never has to match.</summary>
        private void RectInLayer(RectTransform of, out Vector2 pos, out Vector2 size)
        {
            var corners = new Vector3[4];
            of.GetWorldCorners(corners);
            Vector3 min = flyLayer.InverseTransformPoint(corners[0]);
            Vector3 max = flyLayer.InverseTransformPoint(corners[2]);
            size = new Vector2(Mathf.Abs(max.x - min.x), Mathf.Abs(max.y - min.y));
            // anchoredPosition is measured from the layer's CENTRE (the morph anchors there), local points from its
            // pivot — subtract rect.center so a fly layer with an off-centre pivot still lands exactly.
            pos = new Vector2((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f) - flyLayer.rect.center;
        }

        private void PlaceOver(RectTransform morph, RectTransform target)
        {
            morph.anchorMin = morph.anchorMax = new Vector2(0.5f, 0.5f);
            morph.pivot = new Vector2(0.5f, 0.5f);
            RectInLayer(target, out var pos, out var size);
            morph.anchoredPosition = pos;
            morph.sizeDelta = size;
        }

        private Vector3 ButtonCentreWorld()
        {
            var src = button != null ? (RectTransform)button.transform : buttonIcon;
            if (src == null) return panel.position;
            var corners = new Vector3[4];
            src.GetWorldCorners(corners);
            return (corners[0] + corners[2]) * 0.5f;
        }

        /// <summary>The scale the card is born at: the button's height against the card's, so it literally grows
        /// out of something button-sized.</summary>
        private float FoldedScale()
        {
            var src = button != null ? (RectTransform)button.transform : buttonIcon;
            if (src == null) return 0.15f;
            float b = src.rect.height * Mathf.Abs(src.lossyScale.y);
            // The panel's height AT REST — parent lossy × the captured authored scale, so a call mid-tween (a
            // re-open during a close) measures the same card every time.
            float p = panel.rect.height * Mathf.Abs(panel.parent.lossyScale.y * _restScale.y);
            return p <= 0f ? 0.15f : Mathf.Clamp(b / p, 0.05f, 0.5f);
        }

        // ---------------- the outside-tap catcher ----------------

        /// <summary>
        /// One shared, invisible, fullscreen button that exists only while a card is open, slotted in draw order
        /// just BENEATH the lowest open panel: a tap on a card reaches the card, a tap anywhere else closes every
        /// card and is swallowed — it must not also press whatever sat under the finger.
        /// </summary>
        private static void RefreshCatcher()
        {
            if (s_open.Count == 0)
            {
                if (s_catcher != null) s_catcher.SetActive(false);
                return;
            }

            var owner = s_open[0];
            if (s_catcher == null)
            {
                s_catcher = new GameObject("FoldoutOutsideCatcher", typeof(RectTransform), typeof(Image), typeof(Button));
                var img = s_catcher.GetComponent<Image>();
                img.color = Color.clear;               // invisible but raycastable
                s_catcher.GetComponent<Button>().onClick.AddListener(CloseAll);
                var rt = (RectTransform)s_catcher.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
            }

            var parent = owner.panel.parent;
            var catcherRt = (RectTransform)s_catcher.transform;
            if (catcherRt.parent != parent) catcherRt.SetParent(parent, false);
            catcherRt.anchorMin = Vector2.zero;
            catcherRt.anchorMax = Vector2.one;
            catcherRt.offsetMin = catcherRt.offsetMax = Vector2.zero;

            // Just beneath the lowest open panel, so every open card still draws (and clicks) above it.
            int idx = int.MaxValue;
            foreach (var f in s_open)
                if (f.panel != null && f.panel.parent == parent) idx = Mathf.Min(idx, f.panel.GetSiblingIndex());
            catcherRt.SetSiblingIndex(idx == int.MaxValue ? 0 : idx);

            s_catcher.SetActive(true);
        }
    }
}
