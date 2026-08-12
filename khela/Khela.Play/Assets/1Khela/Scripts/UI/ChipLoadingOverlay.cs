using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// Reusable, INDETERMINATE chip loading spinner — a juicy "loading…" built from a handful of chip sprites.
    /// Pure spinner: no progress, it just animates forever while active. The chips orbit an ELLIPTICAL ring
    /// (top-down-into-the-felt perspective): chips at the near edge draw larger, brighter and IN FRONT; far chips
    /// smaller, dimmer, behind — a cheap real-3D read from flat top-down art — with a landing pop at the front,
    /// slow face-spin, an optional UIParticle glint burst as each chip sweeps to the front, and cycling flavour
    /// text. Every feel value is inspector-tunable.
    ///
    /// Runs on UNSCALED time, so it keeps moving through timeScale=0 pauses. IMPORTANT: it can only stay smooth
    /// while the actual load is ASYNC — a synchronous SceneManager.LoadScene blocks the main thread and freezes
    /// ANY animation. Convert those flows to LoadSceneAsync and keep this alive across the load.
    ///
    /// Drop-in: it's a plain UI widget — place the prefab on ANY canvas, anywhere, at any size. Put the chip Images
    /// under a common, CENTER-ANCHORED parent (the ring centres on that parent's origin), assign the chips + the
    /// optional status label + particle glint, then leave it visible for an always-on spinner or drive it with
    /// Show()/Hide()/SetMessage(). Depth is done with scale + alpha only, so it never reorders siblings.
    ///
    /// Perf note (UGUI): animating any UI element every frame marks its Canvas dirty and rebuilds that canvas'
    /// batch. That's trivial on a loading / transition / menu canvas, but wasteful on a big, busy gameplay HUD. If
    /// you must drop it on such a canvas, add a nested Canvas component to THIS widget's root — that isolates the
    /// per-frame rebuild to just the chips (still a plain child of the host canvas, NOT a separate screen overlay).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ChipLoadingOverlay : MonoBehaviour
    {
        /// <summary>Set when Persist Across Scenes is on, so a static navigator/service can drive the overlay.</summary>
        public static ChipLoadingOverlay Instance { get; private set; }

        [Header("Persistence")]
        [Tooltip("Keep this overlay alive across scene loads and expose it as ChipLoadingOverlay.Instance. " +
                 "Requires the prefab ROOT to be its own Canvas. Leave OFF for a plain in-scene spinner.")]
        [SerializeField] private bool persistAcrossScenes = false;
        [Tooltip("Start hidden (Show() reveals it). OFF = visible + animating immediately (a plain always-on spinner).")]
        [SerializeField] private bool hiddenOnAwake = false;

        [Header("References")]
        [Tooltip("The chip Images that orbit. Any count; they're spread evenly around the ring. Parent them under a " +
                 "center-anchored container — the ring centres on that container's origin.")]
        [SerializeField] private Image[] chips;
        [Tooltip("Fades the whole overlay in/out. Auto-added if empty.")]
        [SerializeField] private CanvasGroup canvasGroup;
        [Tooltip("Optional status label; cycles the Messages below while shown.")]
        [SerializeField] private TMP_Text statusLabel;

        [Header("Orbit — size & shape")]
        [Tooltip("Horizontal radius of the ring, in canvas units.")]
        [SerializeField] private float radiusX = 150f;
        [Tooltip("Vertical radius. Smaller than radiusX = a flatter, more top-down perspective ellipse.")]
        [SerializeField] private float radiusY = 48f;
        [Tooltip("Degrees per second the ring rotates. Negative reverses direction.")]
        [SerializeField] private float orbitSpeed = 70f;

        [Header("Orbit — juice")]
        [Tooltip("How much the ring speed 'breathes' (0 = constant): it eases fast/slow each cycle so it feels alive.")]
        [Range(0f, 0.9f)] [SerializeField] private float speedBreath = 0.3f;
        [Tooltip("Breaths per second of that speed modulation.")]
        [SerializeField] private float breathHz = 0.31f;
        [Tooltip("Chip scale at the NEAR (front) edge of the ring.")]
        [SerializeField] private float nearScale = 1f;
        [Tooltip("Chip scale at the FAR (back) edge — smaller reads as further away.")]
        [SerializeField] private float farScale = 0.5f;
        [Tooltip("Chip alpha at the FAR edge (near edge is always 1). Lower = more depth.")]
        [Range(0f, 1f)] [SerializeField] private float farAlpha = 0.42f;
        [Tooltip("Extra uniform scale 'pop' as a chip passes the very front (0 = none). The landing bounce.")]
        [Range(0f, 0.5f)] [SerializeField] private float nearPop = 0.16f;
        [Tooltip("Degrees/second each chip slowly spins on its own face (adds life). 0 = no face spin.")]
        [SerializeField] private float chipSpin = 12f;
        [Tooltip("Ring breathes IN/OUT (0 = none): chips sweep outward and back together — the biggest 'alive' lever.")]
        [Range(0f, 0.3f)] [SerializeField] private float radiusPulse = 0.09f;
        [Tooltip("Breaths per second of the ring in/out pulse. Kept off-beat from the orbit so it never looks looped.")]
        [SerializeField] private float radiusPulseHz = 0.45f;
        [Tooltip("Strictly-correct near-in-front overlap. OFF (default) uses scale+alpha only — leave it OFF so the " +
                 "widget drops onto ANY canvas cleanly. ON reorders siblings when two chips swap depth, which adds a " +
                 "hierarchy change on top of the animation — only worth it on a dedicated/nested canvas.")]
        [SerializeField] private bool depthSorting = false;

        [Header("Glint particle (optional)")]
        [Tooltip("A LOOPING UIParticle system (Play On Awake ON) that follows the chip currently in the HIGHLIGHTED " +
                 "front of the ring, tweening ON while a chip is there and OFF when it leaves — the shine. The script " +
                 "fades + scales it (via a CanvasGroup it auto-adds); the particle itself just loops.")]
        [SerializeField] private ParticleSystem glint;
        [Tooltip("How near the front a chip must be to count as HIGHLIGHTED (0..1). Higher = a narrower spot, so the " +
                 "glint blinks on/off per chip instead of staying on.")]
        [Range(0.5f, 1f)] [SerializeField] private float glintAt = 0.93f;
        [Tooltip("Seconds to tween the glint in/out as a chip enters/leaves the highlight.")]
        [SerializeField] private float glintFade = 0.18f;

        [Header("Status text")]
        [Tooltip("Cycled while shown (indeterminate flavour). Empty = leave the label alone.")]
        [SerializeField] private string[] messages = { "Shuffling deck…", "Taking your seat…", "Opening table…" };
        [Tooltip("Seconds each message shows before cycling to the next.")]
        [SerializeField] private float messageInterval = 1.6f;

        [Header("Fade / input")]
        [Tooltip("Seconds to fade in on Show().")]
        [SerializeField] private float fadeIn = 0.2f;
        [Tooltip("Seconds to fade out on Hide().")]
        [SerializeField] private float fadeOut = 0.18f;
        [Tooltip("Eat clicks/taps while visible so nothing behind the overlay is touchable (full-screen loading). " +
                 "Turn OFF for a small inline spinner.")]
        [SerializeField] private bool blockInput = true;

        // ---- runtime (no per-frame allocation) ----
        private RectTransform[] _rects;
        private float[] _spinOffset;
        private int[] _order;
        private RectTransform _glintRt;
        private CanvasGroup _glintCg;
        private float _glintA;
        private float _ring, _t, _msgTimer;
        private int _msgIndex;
        private bool _cycle, _shown;
        private Coroutine _fade;

        public bool IsShown => _shown;

        private void Awake()
        {
            if (persistAcrossScenes)
            {
                if (Instance != null && Instance != this) { Destroy(gameObject); return; }
                Instance = this;
                DontDestroyOnLoad(transform.root.gameObject);
            }

            if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();

            int n = chips != null ? chips.Length : 0;
            _rects = new RectTransform[n];
            _spinOffset = new float[n];
            _order = new int[n];
            for (int i = 0; i < n; i++)
            {
                _rects[i] = chips[i] != null ? chips[i].rectTransform : null;
                _spinOffset[i] = i * 47.3f;   // deterministic stagger so the faces never spin in lockstep
                _order[i] = i;
            }
            if (n == 0) Debug.LogWarning("[ChipLoadingOverlay] No chip Images assigned.");

            if (glint != null)
            {
                _glintRt = glint.GetComponent<RectTransform>();
                _glintCg = glint.GetComponent<CanvasGroup>() ?? glint.gameObject.AddComponent<CanvasGroup>();
                _glintCg.alpha = 0f;
                glint.gameObject.SetActive(false);   // start hidden; activated + Play()'d when a chip is highlighted
            }
            _cycle = messages != null && messages.Length > 0;
            _msgTimer = messageInterval;   // show the first message immediately

            _shown = !hiddenOnAwake;
            SetGroup(_shown ? 1f : 0f);
            if (!_shown) gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ─────────────────────────────── public API ───────────────────────────────

        /// <summary>Reveal the overlay (fade in). Pass a message to pin it, or null to auto-cycle the flavour text.</summary>
        public void Show(string message = null)
        {
            gameObject.SetActive(true);
            if (message != null) SetMessage(message); else ResumeCycle();
            _shown = true;
            StartFade(1f, fadeIn);
        }

        /// <summary>Hide the overlay (fade out, then disable).</summary>
        public void Hide()
        {
            _shown = false;
            StartFade(0f, fadeOut, disableAfter: true);
        }

        /// <summary>Pin a fixed status message and stop the auto-cycle.</summary>
        public void SetMessage(string message)
        {
            _cycle = false;
            if (statusLabel != null) statusLabel.text = message;
        }

        /// <summary>Resume auto-cycling the flavour messages.</summary>
        public void ResumeCycle()
        {
            _cycle = messages != null && messages.Length > 0;
            _msgTimer = messageInterval;   // advance on the next tick
        }

        // ─────────────────────────────── animation ───────────────────────────────

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _t += dt;

            // Ring rotation with an eased 'breathing' speed so it doesn't feel robotic.
            float breath = 1f + speedBreath * Mathf.Sin(_t * breathHz * Mathf.PI * 2f);
            _ring += orbitSpeed * breath * dt;
            if (_ring >= 360f) _ring -= 360f; else if (_ring < 0f) _ring += 360f;

            AnimateChips();
            AnimateGlint(dt);
            CycleMessage(dt);
        }

        private void AnimateChips()
        {
            int n = _rects.Length;
            if (n == 0) return;
            float step = 360f / n;
            float rp = 1f + radiusPulse * Mathf.Sin(_t * radiusPulseHz * Mathf.PI * 2f);   // ring breathes in/out
            float rx = radiusX * rp, ry = radiusY * rp;

            for (int i = 0; i < n; i++)
            {
                var rt = _rects[i];
                if (rt == null) continue;

                float ang = (_ring + step * i) * Mathf.Deg2Rad;
                float sin = Mathf.Sin(ang);
                rt.anchoredPosition = new Vector2(Mathf.Cos(ang) * rx, sin * ry);

                float near = (1f - sin) * 0.5f;                                  // 0 far … 1 near
                float baseScale = Mathf.Lerp(farScale, nearScale, near);
                float pop = 1f + nearPop * Mathf.Clamp01((near - 0.8f) / 0.2f);  // bump only right at the front
                float s = baseScale * pop;
                rt.localScale = new Vector3(s, s, 1f);
                rt.localRotation = Quaternion.Euler(0f, 0f, -(_t * chipSpin + _spinOffset[i]));

                var img = chips[i];
                if (img != null)
                {
                    var c = img.color;
                    c.a = Mathf.Lerp(farAlpha, 1f, near);
                    img.color = c;
                }

            }

            if (depthSorting) SortByDepth(n, step);
        }

        // Draw near chips in front: insertion-sort the tiny index array by 'near' ascending (no alloc, no LINQ),
        // then place far→near up the sibling list. Only touches siblings that actually moved.
        private void SortByDepth(int n, float step)
        {
            for (int i = 0; i < n; i++) _order[i] = i;
            for (int i = 1; i < n; i++)
            {
                int key = _order[i];
                float k = NearOf(key, step);
                int j = i - 1;
                while (j >= 0 && NearOf(_order[j], step) > k) { _order[j + 1] = _order[j]; j--; }
                _order[j + 1] = key;
            }
            for (int i = 0; i < n; i++)
            {
                var rt = _rects[_order[i]];
                if (rt != null && rt.GetSiblingIndex() != i) rt.SetSiblingIndex(i);
            }
        }

        private float NearOf(int i, float step)
            => (1f - Mathf.Sin((_ring + step * i) * Mathf.Deg2Rad)) * 0.5f;

        // The shine: the glint follows whichever chip is in the highlighted front, tweening ON (fade + scale) while a
        // chip is there and OFF when it leaves. The particle just loops; this only drives its visibility + position.
        private void AnimateGlint(float dt)
        {
            if (glint == null) return;
            int n = _rects.Length;
            float step = n > 0 ? 360f / n : 0f;
            int hi = -1; float best = glintAt;
            for (int i = 0; i < n; i++) { float nr = NearOf(i, step); if (nr >= best) { best = nr; hi = i; } }
            bool on = hi >= 0;

            if (on && _glintRt != null && _rects[hi] != null)
                _glintRt.anchoredPosition = _rects[hi].anchoredPosition;   // follow the highlighted chip

            float target = on ? 1f : 0f;
            _glintA = Mathf.MoveTowards(_glintA, target, glintFade > 0f ? dt / glintFade : 1f);

            // Gate the particle's life to the highlight (+ its fade). SetActive guarantees it shows/hides; Play() on
            // (re)activation guarantees it emits regardless of the PS's Play-On-Awake setting; CanvasGroup does the fade.
            var go = glint.gameObject;
            bool shouldLive = on || _glintA > 0.001f;
            if (shouldLive && !go.activeSelf) { go.SetActive(true); glint.Play(); }
            else if (!shouldLive && go.activeSelf) go.SetActive(false);

            if (_glintCg != null && go.activeSelf) _glintCg.alpha = _glintA;
        }

        private void CycleMessage(float dt)
        {
            if (!_cycle || statusLabel == null || messages == null || messages.Length == 0) return;
            _msgTimer += dt;
            if (_msgTimer < messageInterval) return;
            _msgTimer = 0f;
            statusLabel.text = messages[_msgIndex % messages.Length];
            _msgIndex++;
        }

        // ─────────────────────────────── fade ───────────────────────────────

        private void StartFade(float target, float dur, bool disableAfter = false)
        {
            if (_fade != null) { StopCoroutine(_fade); _fade = null; }
            if (!gameObject.activeInHierarchy)
            {
                SetGroup(target);
                if (disableAfter && target <= 0f) gameObject.SetActive(false);
                return;
            }
            _fade = StartCoroutine(FadeRoutine(target, dur, disableAfter));
        }

        private IEnumerator FadeRoutine(float target, float dur, bool disableAfter)
        {
            float from = canvasGroup.alpha;
            for (float t = 0f; dur > 0f && t < dur; t += Time.unscaledDeltaTime)
            {
                SetGroup(Mathf.Lerp(from, target, t / dur));
                yield return null;
            }
            SetGroup(target);
            if (disableAfter && target <= 0f) gameObject.SetActive(false);
            _fade = null;
        }

        private void SetGroup(float a)
        {
            canvasGroup.alpha = a;
            bool solid = a > 0.5f;
            canvasGroup.blocksRaycasts = blockInput && solid;
            canvasGroup.interactable = blockInput && solid;
        }
    }
}
