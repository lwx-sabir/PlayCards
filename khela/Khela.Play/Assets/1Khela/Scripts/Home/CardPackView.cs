using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace PlayCard.Home
{
    /// <summary>
    /// Home-screen "pack of cards": the rarity card frames sit stacked in a corner with their tops peeking out.
    /// <list type="bullet">
    /// <item><b>Sweep</b> (horizontal drag) deals the front card off and cycles the next one into place, one at a time.</item>
    /// <item><b>Tap</b> fans the whole pack open into a hand so you can see them all at once (smooth tween); tap again to collapse.</item>
    /// </list>
    /// Pure uGUI — driven by EventSystem pointer events, so it works under the new Input System's
    /// <c>InputSystemUIInputModule</c> with no legacy <c>UnityEngine.Input</c>. It arranges its <b>direct child
    /// cards</b> automatically every frame, so adding/removing a card is just (un)parenting it. Runs in edit mode
    /// (<see cref="ExecuteAlways"/>) so you can frame the pack in the corner without pressing Play. Positions cards
    /// via <c>localPosition</c> and never touches their anchors/sizeDelta, so card art is not distorted.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(RectTransform))]
    public sealed class CardPackView : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        [Header("Stack (collapsed pack)")]
        [Tooltip("How far left (px) each card behind the front nudges, so its top-left corner peeks out. Keep small — the pile stays in one place.")]
        [SerializeField] private float peekLeft = 5f;
        [Tooltip("How far up (px) each card behind the front nudges.")]
        [SerializeField] private float peekUp = 5f;
        [Tooltip("Tilt magnitude (deg) of cards behind the front. Default jitters both ways; one-sided mode adds this much PER card behind.")]
        [SerializeField] private float stackTilt = 7f;
        [Tooltip("ON = every card behind the front leans the SAME way (one-sided fanned stack), growing with depth, instead of the default jittered both-ways tilt.")]
        [SerializeField] private bool oneSidedTilt = false;
        [Tooltip("Only when One Sided Tilt is ON: checked leans the stack to the RIGHT, unchecked leans it LEFT.")]
        [SerializeField] private bool tiltRight = false;
        [Tooltip("Each card behind shrinks by this fraction (subtle depth cue).")]
        [SerializeField] private float depthScale = 0.012f;

        [Header("Placement (px from the parent's CENTRE — pivot-independent)")]
        [Tooltip("Offset of the collapsed pile's FRONT card from the parent's centre. (0,0) = dead centre. " +
                 "Move the pack with this (or by moving the parent) — the parent's pivot no longer matters.")]
        [SerializeField] private Vector2 packCenter = Vector2.zero;
        [Tooltip("Offset of the OPENED fan's centre from the parent's centre. (0,150) = 150px above centre. " +
                 "Nudge it up so the fan clears the buttons below; it stays centred as you widen the spread.")]
        [SerializeField] private Vector2 fanCenter = new Vector2(0f, 150f);
        [Tooltip("Editor-only: preview the opened fan in the Scene view so you can place Fan Center without pressing Play.")]
        [SerializeField] private bool previewFanInEditor = false;

        [Header("Fan shape (tap to reveal all)")]
        [Tooltip("Half-width (px) the fan spreads horizontally from Fan Center. Lower it (or shift Fan Center left) if the fan clips the screen edge.")]
        [SerializeField] private float fanSpreadX = 150f;
        [Tooltip("How much the ends of the fan drop below the centre, for an arc.")]
        [SerializeField] private float fanArcDrop = 40f;
        [Tooltip("Max tilt (deg) of the outermost fanned cards.")]
        [SerializeField] private float fanTiltMax = 16f;

        [Header("Feel")]
        [Tooltip("Smoothing time for all card motion. Lower = snappier.")]
        [SerializeField] private float smoothTime = 0.12f;
        [Tooltip("Drag distance (px) past which a sweep deals a card.")]
        [SerializeField] private float swipeThreshold = 60f;
        [Tooltip("How far (px) a dealt card flings out before recycling to the back of the pack.")]
        [SerializeField] private float dealFling = 320f;

        /// <summary>Fires with the card that just became the front of the pack (on deal/cycle).</summary>
        public event System.Action<RectTransform> OnFrontChanged;
        /// <summary>Fires when the pack is fanned open (true) or collapsed (false).</summary>
        public event System.Action<bool> OnFanned;

        private readonly List<RectTransform> _cards = new();
        private Vector3[] _posVel;
        private float[] _rotVel, _sclVel, _fling;
        private Vector2[] _flingDir;
        private int _collectedChildCount = -1;

        private int _deal;             // cards dealt so far; front slot for child i = (i - _deal) mod n
        private float _fan, _fanVel;   // 0 = packed, 1 = fanned
        private bool _fanned;
        private bool _dragging;
        private Vector2 _dragStart;
        private float _frontDrag;      // live x-follow of the front card while sweeping

        private int Count => _cards.Count;

        /// <summary>The card currently at the front of the pack, or null if empty.</summary>
        public RectTransform Front
        {
            get { for (int i = 0; i < Count; i++) if (SlotOf(i) == 0) return _cards[i]; return null; }
        }

        private void OnEnable()
        {
            Collect();
            ApplyImmediate();
        }

        private void Collect()
        {
            _cards.Clear();
            for (int i = 0; i < transform.childCount; i++)
            {
                if (transform.GetChild(i) is RectTransform rt && rt.gameObject.activeSelf)
                    _cards.Add(rt);
            }
            int n = _cards.Count;
            _posVel = new Vector3[n];
            _rotVel = new float[n];
            _sclVel = new float[n];
            _fling = new float[n];
            _flingDir = new Vector2[n];
            _collectedChildCount = transform.childCount;
        }

        private int SlotOf(int i)
        {
            int n = Count;
            if (n == 0) return 0;
            return ((i - _deal) % n + n) % n;   // 0 = front, n-1 = back
        }

        /// <summary>Target local pose for a card given its stack slot, blended by the current fan amount.</summary>
        private void TargetPose(int slot, out Vector2 pos, out float rot, out float scale)
        {
            int n = Count;

            // Collapsed pack: a tight, slightly messy pile — all cards in one spot. The front card (slot 0)
            // sits upright and on top; each card behind nudges up-and-left so only its top-left corner peeks,
            // with a small jittered tilt so edges show (like a hand-squared deck). Offsets are deliberately small.
            float jitter = Mathf.Sin((slot + 1) * 127.1f);   // deterministic pseudo-random in ~[-1, 1]
            // The corner-peek must follow the lean side, or the cards lean one way while stacking the other and
            // the fan flips downward. One-sided right → peek up-RIGHT (mirror of left); default/left keeps up-left.
            float peekX = (oneSidedTilt && tiltRight) ? slot * peekLeft : -slot * peekLeft;
            float peekJit = oneSidedTilt ? 0f : jitter * 2f;   // drop the jitter for a clean one-sided fan
            Vector2 packPos = packCenter + new Vector2(peekX + peekJit, slot * peekUp);
            float packRot = slot == 0 ? 0f
                          : oneSidedTilt ? slot * stackTilt * (tiltRight ? -1f : 1f)   // all lean one way, growing with depth
                          : jitter * stackTilt;                                          // default: jittered both ways
            float packScale = Mathf.Max(0.5f, 1f - slot * depthScale);

            // Fanned hand: an arc centred on fanCenter, spreading ±fanSpreadX, ends tilted outward.
            float t = n <= 1 ? 0.5f : slot / (float)(n - 1);   // 0..1 left→right
            float centered = t - 0.5f;                         // -0.5..0.5
            Vector2 fanPos = fanCenter + new Vector2(centered * 2f * fanSpreadX,
                                                     -fanArcDrop * (centered * centered) * 4f);
            float fanRot = -centered * 2f * fanTiltMax;

            // Anchor everything to THIS rect's CENTRE (not its pivot/corner) so the pile and fan sit centred in
            // the parent regardless of its pivot — a wider Fan Spread X then grows balanced both ways instead of
            // running off one edge. rect.center == (width*(0.5-pivotX), height*(0.5-pivotY)) in local space.
            Vector2 baseCenter = ((RectTransform)transform).rect.center;
            pos = baseCenter + Vector2.Lerp(packPos, fanPos, _fan);
            rot = Mathf.LerpAngle(packRot, fanRot, _fan);
            scale = Mathf.Lerp(packScale, 1f, _fan);
        }

        private void Update()
        {
            if (transform.childCount != _collectedChildCount) Collect();
            int n = Count;
            if (n == 0) return;

            bool playing = Application.isPlaying;
            if (playing) _fan = Mathf.SmoothDamp(_fan, _fanned ? 1f : 0f, ref _fanVel, smoothTime);
            else _fan = (_fanned || previewFanInEditor) ? 1f : 0f;

            for (int i = 0; i < n; i++)
            {
                int slot = SlotOf(i);
                TargetPose(slot, out Vector2 tp, out float tr, out float ts);

                // Front card follows the finger while sweeping; a just-dealt card flings out then recycles back.
                if (slot == 0 && _dragging) tp.x += _frontDrag;
                if (_fling[i] > 0f)
                {
                    tp += _flingDir[i] * (_fling[i] * dealFling);
                    if (playing) _fling[i] = Mathf.MoveTowards(_fling[i], 0f, Time.deltaTime / Mathf.Max(0.01f, smoothTime * 2f));
                    else _fling[i] = 0f;
                }

                var rt = _cards[i];
                var targetPos = PivotToCentreTarget(rt, tp, tr, ts);
                if (playing)
                {
                    rt.localPosition = Vector3.SmoothDamp(rt.localPosition, targetPos, ref _posVel[i], smoothTime);
                    float z = Mathf.SmoothDampAngle(rt.localEulerAngles.z, tr, ref _rotVel[i], smoothTime);
                    rt.localEulerAngles = new Vector3(0f, 0f, z);
                    float s = Mathf.SmoothDamp(rt.localScale.x, ts, ref _sclVel[i], smoothTime);
                    rt.localScale = new Vector3(s, s, 1f);
                }
                else
                {
                    rt.localPosition = targetPos;
                    rt.localEulerAngles = new Vector3(0f, 0f, tr);
                    rt.localScale = new Vector3(ts, ts, 1f);
                }

                rt.SetSiblingIndex(n - 1 - slot);   // front-most renders on top
            }
        }

        private void ApplyImmediate()
        {
            int n = Count;
            _fan = (_fanned || (!Application.isPlaying && previewFanInEditor)) ? 1f : 0f;
            for (int i = 0; i < n; i++)
            {
                int slot = SlotOf(i);
                TargetPose(slot, out Vector2 tp, out float tr, out float ts);
                var rt = _cards[i];
                rt.localPosition = PivotToCentreTarget(rt, tp, tr, ts);
                rt.localEulerAngles = new Vector3(0f, 0f, tr);
                rt.localScale = new Vector3(ts, ts, 1f);
                rt.SetSiblingIndex(n - 1 - slot);
            }
        }

        /// <summary>Converts a desired CENTRE position + rotation into the localPosition for a card's pivot, so a
        /// card always rotates about its geometric centre even if its RectTransform pivot is off-centre. (These
        /// cards are already centre-pivoted, so it's a no-op for them, but it keeps the layout pivot-agnostic.)</summary>
        private static Vector3 PivotToCentreTarget(RectTransform rt, Vector2 centre, float angleDeg, float scale)
        {
            Vector2 pivotToCentre = new Vector2((0.5f - rt.pivot.x) * rt.rect.width,
                                                (0.5f - rt.pivot.y) * rt.rect.height) * scale;
            float r = angleDeg * Mathf.Deg2Rad;
            float cos = Mathf.Cos(r), sin = Mathf.Sin(r);
            Vector2 rotated = new Vector2(pivotToCentre.x * cos - pivotToCentre.y * sin,
                                          pivotToCentre.x * sin + pivotToCentre.y * cos);
            return new Vector3(centre.x - rotated.x, centre.y - rotated.y, 0f);
        }

        // ---- Pointer (EventSystem) -------------------------------------------------------------------------

        public void OnBeginDrag(PointerEventData e)
        {
            _dragging = true;
            _dragStart = e.position;
            _frontDrag = 0f;
        }

        public void OnDrag(PointerEventData e)
        {
            _frontDrag = e.position.x - _dragStart.x;   // front card tracks the finger horizontally
        }

        public void OnEndDrag(PointerEventData e)
        {
            _dragging = false;
            _frontDrag = 0f;
            float dx = e.position.x - _dragStart.x;
            float dy = e.position.y - _dragStart.y;
            if (Mathf.Abs(dx) >= swipeThreshold && Mathf.Abs(dx) > Mathf.Abs(dy))
                Deal(dx < 0f ? +1 : -1, new Vector2(Mathf.Sign(dx), 0.3f).normalized);
        }

        /// <summary>Tap fans the pack open / closed. (EventSystem only raises click when no drag happened.)</summary>
        public void OnPointerClick(PointerEventData e)
        {
            if (e.dragging) return;
            SetFanned(!_fanned);
        }

        // ---- Public API ------------------------------------------------------------------------------------

        /// <summary>Deal one card: the current front flings out in <paramref name="flingDir"/> and recycles to the
        /// back, bringing the next card to the front. <paramref name="dir"/> +1 = next, -1 = previous.</summary>
        public void Deal(int dir, Vector2 flingDir)
        {
            int n = Count;
            if (n == 0) return;

            int front = -1;
            for (int i = 0; i < n; i++) if (SlotOf(i) == 0) { front = i; break; }

            if (_fanned) SetFanned(false);   // a sweep re-packs an open fan
            _deal += dir;

            if (front >= 0)
            {
                _fling[front] = 1f;
                _flingDir[front] = flingDir == Vector2.zero ? Vector2.right : flingDir.normalized;
            }
            OnFrontChanged?.Invoke(Front);
        }

        /// <summary>Open (true) or collapse (false) the fan.</summary>
        public void SetFanned(bool open)
        {
            if (_fanned == open) return;
            _fanned = open;
            OnFanned?.Invoke(open);
        }

        public void ToggleFan() => SetFanned(!_fanned);
    }
}
