using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The lobby's stake-tier selector: a swipeable strip of bet ranges where the selected one sits centred and its
    /// neighbours are visible but clipped, so a player can see what they are stepping towards.
    ///
    /// Three things drive the design:
    ///
    /// Labels are spaced EDGE TO EDGE, not on a fixed pitch. Tier names are different widths ("1k/10k" against
    /// "250k/1m"), and a fixed centre-to-centre pitch leaves a visible gutter of pitch − widthA/2 − widthB/2 — so a
    /// wider neighbour visibly crowds the selection while a narrower one drifts away. Measuring each label and
    /// laying out from its edges is the only way the gaps actually look equal.
    ///
    /// It is VIRTUALISED. Only a handful of labels exist no matter how many tiers there are; they are repositioned
    /// and re-texted as the strip moves, so a thousand tiers costs the same as three. Widths are measured once per
    /// tier and cached, so scrolling measures nothing.
    ///
    /// Appearance lives entirely in two prefabs you assign. This script never sets a colour, size or font weight.
    /// </summary>
    public sealed class BetTierSlider : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        [Header("Wiring")]
        [Tooltip("Masked window the strip lives in — needs a RectMask2D, and a raycast-target Graphic so it can " +
                 "receive swipes (a fully transparent Image is fine).")]
        [SerializeField] private RectTransform viewport;
        [Tooltip("Parent the visible labels are spawned under. Stays at 0,0 — this script positions the labels, " +
                 "not the container. Must NOT have a LayoutGroup or ContentSizeFitter.")]
        [SerializeField] private RectTransform content;

        [Header("Label prefabs (all styling lives here)")]
        [SerializeField] private GameObject selectedLabelPrefab;
        [SerializeField] private GameObject unselectedLabelPrefab;

        [Header("Arrows (optional)")]
        [SerializeField] private Button prevButton;
        [SerializeField] private Button nextButton;

        [Header("Layout")]
        [Tooltip("Clear space BETWEEN two labels, edge to edge. This is the only spacing control — the gap you set " +
                 "is the gap you get, on both sides, whatever the labels say.")]
        [SerializeField] private float gap = 60f;
        [Tooltip("Optional floor on centre-to-centre distance. 0 = purely gap-driven. Raise it if short labels sit " +
                 "closer together than you like.")]
        [SerializeField] private float minPitch = 0f;
        [Tooltip("How many labels exist at once. 0 = work it out from the viewport width. This only sizes the " +
                 "recycling pool; it does NOT change how many tiers are visible.")]
        [Min(0)][SerializeField] private int visibleCount = 0;

        [Header("Feel")]
        [SerializeField] private bool swipeEnabled = true;
        [Tooltip("Seconds to settle onto a tier after a swipe or an arrow press. 0 = snap.")]
        [Min(0f)][SerializeField] private float slideSeconds = 0.32f;
        [Tooltip("Overshoot of the elastic settle. 0 = plain ease-out, ~1.7 = pronounced bounce-back.")]
        [Min(0f)][SerializeField] private float elasticity = 1.4f;
        [Tooltip("How far a flick carries, in tiers per unit of release speed. 0 = no momentum, always nearest.")]
        [Min(0f)][SerializeField] private float flickCarry = 0.12f;
        [Tooltip("Most tiers a single flick may cross.")]
        [Min(1)][SerializeField] private int maxFlick = 3;
        [Tooltip("Drag resistance past the first/last tier. 0 = a hard wall, 1 = no resistance.")]
        [Range(0f, 1f)][SerializeField] private float edgeResistance = 0.35f;

        /// <summary>Raised when the player lands on a different tier (swipe or arrow). Not raised by <see cref="MoveTo"/>.</summary>
        public event Action<int> OnTierSelected;

        private sealed class Slot
        {
            public GameObject Selected;
            public GameObject Unselected;
            public TMP_Text SelectedText;
            public TMP_Text UnselectedText;
            public int Index = int.MinValue;
            public bool ShowingSelected;
        }

        private readonly List<string> _tiers = new();
        private Slot[] _pool = Array.Empty<Slot>();

        // Measured label widths per tier, in each style. Filled on demand and reused — scrolling never measures.
        private float[] _widthSelected = Array.Empty<float>();
        private float[] _widthUnselected = Array.Empty<float>();

        private float _scroll;          // virtual position in tiers (2.5 = halfway between tier 2 and 3)
        private int _index;
        private bool _dragging;
        private Vector2 _dragLast;
        private float _velocity;

        private bool _tweening;
        private float _tweenFrom, _tweenTo, _tweenT;
        private bool _tweenOvershoot;

        private void Awake()
        {
            if (prevButton) prevButton.onClick.AddListener(() => Step(-1));
            if (nextButton) nextButton.onClick.AddListener(() => Step(+1));
        }

        public int Index => _index;

        /// <summary>Rebuild for a new tier list. Labels arrive pre-formatted (e.g. "1k/10k").</summary>
        public void SetTiers(IReadOnlyList<string> labels)
        {
            _tiers.Clear();
            if (labels != null) _tiers.AddRange(labels);

            _widthSelected = new float[_tiers.Count];
            _widthUnselected = new float[_tiers.Count];
            for (int i = 0; i < _tiers.Count; i++) { _widthSelected[i] = -1f; _widthUnselected[i] = -1f; }

            EnsurePool(force: true);
            _index = Mathf.Clamp(_index, 0, Mathf.Max(0, _tiers.Count - 1));
            _scroll = _index;
            StopTween();
            Layout();
        }

        /// <summary>Jump to a tier without reporting it back (used to restore state).</summary>
        public void MoveTo(int index, bool animate = true)
        {
            if (_tiers.Count == 0) return;
            index = Mathf.Clamp(index, 0, _tiers.Count - 1);
            _index = index;
            if (animate && slideSeconds > 0f && isActiveAndEnabled) StartTween(index, overshoot: true);
            else { StopTween(); _scroll = index; Layout(); }
        }

        private void Step(int delta)
        {
            if (_tiers.Count == 0) return;
            int target = Mathf.Clamp(_index + delta, 0, _tiers.Count - 1);
            if (target == _index) return;
            Commit(target, report: true);
        }

        // ---- measurement -------------------------------------------------------------------------------------

        /// <summary>
        /// Rendered width of a tier's label in one style, measured once and cached. Both styles are measured because
        /// the selected prefab is usually larger, and the layout blends between them as a label approaches the
        /// centre — without that the strip would jump the instant the selection changed.
        /// </summary>
        private float WidthOf(int tier, bool selected)
        {
            if (tier < 0 || tier >= _tiers.Count) return 0f;
            var cache = selected ? _widthSelected : _widthUnselected;
            if (cache[tier] >= 0f) return cache[tier];

            var probe = _pool.Length > 0 ? (selected ? _pool[0].SelectedText : _pool[0].UnselectedText) : null;

            // No label to measure with yet. Return 0 for THIS call but do NOT cache it — EstimatePoolSize asks for a
            // width before the pool exists, and caching that 0 pinned the selected width of the tier you open on to
            // zero for good (only -1 means "unmeasured", and 0 reads as measured). Its neighbours then closed in on it.
            if (!probe) return 0f;

            float w = probe.GetPreferredValues(_tiers[tier]).x;
            cache[tier] = w;
            return w;
        }

        /// <summary>Width a tier is drawn at right now — blended by how close it is to the centre.</summary>
        private float LiveWidth(int tier)
        {
            float closeness = 1f - Mathf.Clamp01(Mathf.Abs(tier - _scroll));
            return Mathf.Lerp(WidthOf(tier, false), WidthOf(tier, true), closeness);
        }

        /// <summary>Centre-to-centre distance between two neighbours: half of each, plus the gap you asked for.</summary>
        private float Pitch(int a, int b)
            => Mathf.Max(minPitch, LiveWidth(a) * 0.5f + gap + LiveWidth(b) * 0.5f);

        // ---- pool --------------------------------------------------------------------------------------------

        private void EnsurePool(bool force = false)
        {
            int want = visibleCount > 0 ? visibleCount : EstimatePoolSize();
            want = Mathf.Max(3, want);
            want = Mathf.Min(want, Mathf.Max(3, _tiers.Count + 2));

            if (_pool.Length == want && !force) return;

            foreach (var s in _pool)
            {
                if (s.Selected) Destroy(s.Selected);
                if (s.Unselected) Destroy(s.Unselected);
            }

            _pool = new Slot[want];
            for (int i = 0; i < want; i++)
            {
                _pool[i] = new Slot
                {
                    Selected = Spawn(selectedLabelPrefab, out var st),
                    Unselected = Spawn(unselectedLabelPrefab, out var ut),
                };
                _pool[i].SelectedText = st;
                _pool[i].UnselectedText = ut;
            }
        }

        /// <summary>Enough labels to cover the viewport plus a spare each side, from a representative pitch.</summary>
        private int EstimatePoolSize()
        {
            if (!viewport) return 5;
            float sample = _tiers.Count > 0 ? Mathf.Max(minPitch, WidthOf(_index, true) + gap) : Mathf.Max(minPitch, gap);
            if (sample <= 1f) sample = 1f;
            return Mathf.CeilToInt(viewport.rect.width / sample) + 2;
        }

        private GameObject Spawn(GameObject prefab, out TMP_Text text)
        {
            text = null;
            if (!prefab || !content) return null;

            var go = Instantiate(prefab, content);
            go.SetActive(false);
            text = go.GetComponent<TMP_Text>() ?? go.GetComponentInChildren<TMP_Text>(true);

            if (go.transform is RectTransform rt)
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            return go;
        }

        // ---- layout ------------------------------------------------------------------------------------------

        /// <summary>
        /// Place the pooled labels around the current scroll position, walking outward from the centre and adding
        /// each real pitch as it goes — so every visible gutter is exactly <see cref="gap"/>, whatever the labels
        /// happen to say.
        /// </summary>
        private void Layout()
        {
            if (_pool.Length == 0 || !content || _tiers.Count == 0) return;

            int centre = Mathf.RoundToInt(_scroll);
            centre = Mathf.Clamp(centre, 0, _tiers.Count - 1);
            int half = _pool.Length / 2;
            int first = centre - half;

            // How far the strip has slid off the anchor tier, so a part-way drag moves smoothly instead of
            // snapping between whole tiers. Scaled by the pitch of the step being crossed.
            float anchorShift = 0f;
            if (!Mathf.Approximately(_scroll, centre))
            {
                int toward = Mathf.Clamp(_scroll > centre ? centre + 1 : centre - 1, 0, _tiers.Count - 1);
                anchorShift = (_scroll - centre) * Pitch(centre, toward);
            }

            for (int j = 0; j < _pool.Length; j++)
            {
                var slot = _pool[j];
                int tier = first + j;

                if (tier < 0 || tier >= _tiers.Count)
                {
                    if (slot.Selected) slot.Selected.SetActive(false);
                    if (slot.Unselected) slot.Unselected.SetActive(false);
                    slot.Index = int.MinValue;
                    continue;
                }

                if (slot.Index != tier)
                {
                    slot.Index = tier;
                    if (slot.SelectedText) slot.SelectedText.text = _tiers[tier];
                    if (slot.UnselectedText) slot.UnselectedText.text = _tiers[tier];
                }

                bool selected = tier == centre;
                if (slot.ShowingSelected != selected || !IsShown(slot))
                {
                    slot.ShowingSelected = selected;
                    if (slot.Selected) slot.Selected.SetActive(selected);
                    if (slot.Unselected) slot.Unselected.SetActive(!selected);

                    // Every label is built INACTIVE and only switched on when it reaches a visible position, so its
                    // text was last laid out with no resolved rect. TMP re-lays out on enable, but a label that has
                    // to fit itself to a box can come back from that pass measuring nothing and draw an empty glyph
                    // run. Re-assign and force the mesh on whatever we just switched on, so a shown label always
                    // renders its current string. Only runs on a transition, so it costs nothing while scrolling.
                    var shownText = selected ? slot.SelectedText : slot.UnselectedText;
                    if (shownText)
                    {
                        shownText.text = _tiers[tier];
                        shownText.ForceMeshUpdate();
                    }
                }

                float x = CentreOffset(tier, centre) - anchorShift;

                var shown = selected ? slot.Selected : slot.Unselected;
                if (shown && shown.transform is RectTransform rt) rt.anchoredPosition = new Vector2(x, 0f);
                var hidden = selected ? slot.Unselected : slot.Selected;
                if (hidden && hidden.transform is RectTransform hrt) hrt.anchoredPosition = new Vector2(x, 0f);
            }

            RefreshArrows();
        }

        /// <summary>
        /// Distance from the anchor tier to <paramref name="tier"/>, accumulating the real pitch of every step in
        /// between. This is what makes the gaps equal rather than the centres equal.
        /// </summary>
        private float CentreOffset(int tier, int anchor)
        {
            if (tier == anchor) return 0f;

            float x = 0f;
            if (tier > anchor)
                for (int i = anchor; i < tier; i++) x += Pitch(i, i + 1);
            else
                for (int i = anchor; i > tier; i--) x -= Pitch(i, i - 1);
            return x;
        }

        private static bool IsShown(Slot s)
            => (s.Selected && s.Selected.activeSelf) || (s.Unselected && s.Unselected.activeSelf);

        private void RefreshArrows()
        {
            if (prevButton) prevButton.interactable = _index > 0;
            if (nextButton) nextButton.interactable = _index < _tiers.Count - 1;
        }

        // ---- swipe -------------------------------------------------------------------------------------------

        public void OnBeginDrag(PointerEventData e)
        {
            if (!swipeEnabled || _tiers.Count == 0) return;
            StopTween();
            _dragging = true;
            _velocity = 0f;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, e.position, e.pressEventCamera, out _dragLast);
        }

        public void OnDrag(PointerEventData e)
        {
            if (!_dragging) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, e.position, e.pressEventCamera, out var p))
                return;

            // Convert pixels to tiers using the pitch right here, so a drag tracks the finger even where the
            // neighbouring labels are much wider or narrower than average.
            int c = Mathf.Clamp(Mathf.RoundToInt(_scroll), 0, _tiers.Count - 1);
            int n = Mathf.Clamp(c + 1, 0, _tiers.Count - 1);
            float localPitch = Mathf.Max(1f, Pitch(c, n));

            float moved = -(p.x - _dragLast.x) / localPitch;
            _dragLast = p;

            float next = _scroll + moved;
            if (next < 0f || next > _tiers.Count - 1) next = _scroll + moved * edgeResistance;
            _scroll = next;

            _velocity = moved / Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
            Layout();
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (!_dragging) return;
            _dragging = false;

            float carried = _scroll + Mathf.Clamp(_velocity * flickCarry, -maxFlick, maxFlick);
            Commit(Mathf.Clamp(Mathf.RoundToInt(carried), 0, _tiers.Count - 1), report: true);
        }

        private void Commit(int target, bool report)
        {
            bool changed = target != _index;
            _index = target;
            StartTween(target, overshoot: true);
            if (changed && report) OnTierSelected?.Invoke(_index);
        }

        // ---- tween -------------------------------------------------------------------------------------------

        private void StartTween(float to, bool overshoot)
        {
            _tweenFrom = _scroll;
            _tweenTo = to;
            _tweenT = 0f;
            _tweenOvershoot = overshoot;
            _tweening = !Mathf.Approximately(_tweenFrom, _tweenTo) && slideSeconds > 0f;
            if (!_tweening) { _scroll = to; Layout(); }
        }

        private void StopTween() => _tweening = false;

        private void Update()
        {
#if UNITY_EDITOR
            // Re-apply Inspector edits while playing — the pool and the width cache are built in SetTiers, so
            // without this the strip cannot be tuned against a live lobby. Done here rather than in OnValidate
            // because that callback may not create or destroy objects, and rebuilding the pool does both.
            if (_editorDirty)
            {
                _editorDirty = false;
                for (int i = 0; i < _widthSelected.Length; i++) { _widthSelected[i] = -1f; _widthUnselected[i] = -1f; }
                EnsurePool(force: true);
                Layout();
            }
#endif
            if (!_tweening || _dragging) return;

            _tweenT += Time.unscaledDeltaTime / Mathf.Max(slideSeconds, 0.0001f);
            float t = Mathf.Clamp01(_tweenT);
            float eased = _tweenOvershoot ? EaseOutBack(t, elasticity) : 1f - Mathf.Pow(1f - t, 3f);

            _scroll = Mathf.LerpUnclamped(_tweenFrom, _tweenTo, eased);
            if (t >= 1f) { _scroll = _tweenTo; _tweening = false; }
            Layout();
        }

        // Overshoots the target then settles — this is what reads as "elastic".
        private static float EaseOutBack(float t, float s)
        {
            float u = t - 1f;
            return 1f + (s + 1f) * u * u * u + s * u * u;
        }

#if UNITY_EDITOR
        private bool _editorDirty;

        private void OnValidate()
        {
            if (gap < 0f) gap = 0f;
            if (minPitch < 0f) minPitch = 0f;
            if (visibleCount < 0) visibleCount = 0;
            if (Application.isPlaying) _editorDirty = true;
        }
#endif
    }
}
