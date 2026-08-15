using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// Touch feel for one button: squash on the finger DOWN, spring back with a decaying bounce on the release, and a
    /// refusal shake when a dead button is tapped. The sibling of <see cref="PlayCard.Audio.ButtonSound"/> — same shape
    /// (a component per button, authored in the Inspector, applied in bulk from
    /// <c>Khela ▸ UI ▸ Add Button Juice To Selection</c>), so a button's look and its sound live side by side.
    ///
    /// Driven from <see cref="IPointerDownHandler"/>, NOT <c>Button.onClick</c>. onClick fires on the release, after
    /// the finger has already lifted — juice hung off it is a report that something happened rather than a response to
    /// the touch. PointerDown lands on the frame the finger touches glass, which is the entire point.
    ///
    /// TWO things make this read as juice rather than as a zoom, and both matter:
    ///
    ///  • SQUASH AND STRETCH. Uniform scale is a camera move; the eye reads it as the button getting nearer, not as
    ///    being pressed. Squashing one axis while the other GROWS is what sells a physical press — see
    ///    <see cref="stretch"/>. It is the single biggest difference between "it scales" and "it feels good".
    ///
    ///  • A REAL SPRING on the release, not an ease. An ease with an overshoot gives you exactly one bump, over in
    ///    ~80ms, which is under the threshold where an overshoot registers as bounce at all. A damped spring
    ///    overshoots, comes back under, and overshoots again, decaying — three or four events the eye can actually
    ///    follow. It is also why the release is integrated rather than evaluated: a spring carries VELOCITY, so
    ///    pressing again mid-bounce continues from wherever it is instead of restarting.
    ///
    /// Scale only for the resting state: <c>localScale</c> is untouched by layout, so this is safe inside a
    /// LayoutGroup. The refusal shake does move the transform — see <see cref="shakePixels"/>.
    ///
    /// Do not put this on the same object as <see cref="UiPulse"/>; both write <c>localScale</c> and the last one to
    /// run wins. Pulse the parent, juice the button.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ButtonJuice : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        [Header("Target")]
        [Tooltip("What actually moves. Empty = this object. Point it at an inner visual if you need the button's own " +
                 "transform left alone.")]
        [SerializeField] private Transform target;

        [Header("Press")]
        [Tooltip("Height at the bottom of the press. 0.92 is a firm push. This is the size of the whole effect — the " +
                 "bounce is driven by how far the press displaced it, so a timid press gives a timid spring.")]
        [SerializeField, Range(0.6f, 1f)] private float pressScale = 0.92f;

        [Tooltip("Seconds to reach the pressed size. Keep this SHORT — it is the perceived response time of the " +
                 "button. 0.05s is ~3 frames at 60fps, fast enough to read as instant. The press is a crisp ease, " +
                 "not a spring: only the RELEASE should bounce.")]
        [SerializeField, Range(0.01f, 0.3f)] private float pressSeconds = 0.05f;

        [Tooltip("How much the other axis moves the OPPOSITE way — squash and stretch. 0 = uniform scale, which " +
                 "reads as a zoom rather than a press. 0.5 means an 8% squash in height also widens it by 4%.")]
        [SerializeField, Range(0f, 1.5f)] private float stretch = 0.5f;

        [Header("Release spring")]
        [Tooltip("Bounce speed in Hz. 5.5 gives a first overshoot about 0.09s after release — fast enough to feel " +
                 "connected to the finger, slow enough to see. Higher gets tighter and buzzier.")]
        [SerializeField, Range(1f, 15f)] private float springFrequency = 5.5f;

        [Tooltip("How fast the bounce dies. 0.18 gives three visible swings over about half a second. Toward 1 it " +
                 "stops bouncing entirely and just eases home; below ~0.1 it wobbles long enough to look broken.")]
        [SerializeField, Range(0.05f, 1f)] private float springDamping = 0.18f;

        [Header("Denied (tapped while not interactable)")]
        [Tooltip("Sideways shake distance in LOCAL units — for a UI button under a Canvas that is pixels. 0 disables " +
                 "the shake. NOTE: this writes localPosition, so on a button driven by a LayoutGroup a layout rebuild " +
                 "during the shake would snap it home. Rebuilds do not happen mid-shake unless something dirties the " +
                 "layout, and the button lands in the right place either way.")]
        [SerializeField, Range(0f, 40f)] private float shakePixels = 7f;

        [Tooltip("Seconds for the shake to play out and decay to nothing.")]
        [SerializeField, Range(0.05f, 1f)] private float shakeSeconds = 0.28f;

        [Tooltip("How many times it crosses back and forth. ~2.5 over 0.28s is about 9Hz, which is what a human head " +
                 "shaking 'no' looks like.")]
        [SerializeField, Range(0.5f, 6f)] private float shakeOscillations = 2.5f;

        [Tooltip("Minimum seconds between refusal shakes, so holding a finger down on a dead button rattles once " +
                 "instead of vibrating continuously.")]
        [SerializeField, Range(0f, 2f)] private float shakeCooldown = 0.25f;

        private enum Phase { None, Press, Spring }

        private Selectable _selectable;
        private Vector3 _baseScale = Vector3.one;

        // Everything is expressed as DISPLACEMENT from the resting size, not as an absolute scale. That is what lets
        // the spring work in one line and what makes an interrupted press continue smoothly: -0.08 means "8% squashed"
        // regardless of what the button was doing a frame ago.
        private Phase _phase;
        private float _d;          // displacement: 0 = rest, negative = squashed, positive = stretched
        private float _v;          // its velocity, carried across frames by the spring
        private float _pressFrom;  // displacement the press started from
        private float _t;
        private bool _held;

        // Scale and shake run INDEPENDENTLY: a refusal shake must not cancel a spring, and vice versa.
        private float _shakeT = -1f;   // < 0 = not shaking
        private Vector3 _shakeRest;
        private float _lastShake = -99f;

        private Transform Tr => target != null ? target : transform;

        private void Awake()
        {
            _selectable = GetComponent<Selectable>();

            // The authored scale is the rest pose, so a button built at 0.8 stays 0.8. Guard against zero: a button
            // inside a panel that animates in FROM zero would otherwise cache 0 as its rest pose and be permanently
            // invisible — everything below multiplies this.
            var s = Tr.localScale;
            _baseScale = (Mathf.Abs(s.x) < 0.0001f || Mathf.Abs(s.y) < 0.0001f) ? Vector3.one : s;
        }

        // IsInteractable(), not .interactable: a panel greyed out by a parent CanvasGroup still reports interactable
        // true on the button itself. IsInteractable() folds in the group, so a button dead by inheritance refuses the
        // tap like any other instead of squashing as though it worked.
        private bool Usable => _selectable == null || _selectable.IsInteractable();

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!Usable) return;   // a dead button does not squash; the refusal is the shake on click
            _held = true;
            _pressFrom = _d;       // wherever the last bounce had got to — a fast double-tap never snaps
            _t = 0f;
            _phase = Phase.Press;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            // Unity delivers PointerUp to whatever was PRESSED even if the finger has since dragged off, and even if
            // the button went non-interactable in between (which is now routine — decisions kill their own buttons on
            // the tap frame). _held is what makes this an answer to a real press rather than a pop out of nowhere.
            if (!_held) return;
            _held = false;
            _v = 0f;               // released from rest at the squashed size; the spring does the rest
            _phase = Phase.Spring;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            // interactable gates Button.onClick, not pointer events, so this still arrives on a dead button — which is
            // the only reason the refusal can be detected at all.
            if (Usable) return;
            if (shakePixels <= 0f || Time.unscaledTime - _lastShake < shakeCooldown) return;

            _lastShake = Time.unscaledTime;
            _shakeRest = Tr.localPosition;   // captured NOW, so we return to wherever layout last put it
            _shakeT = 0f;
        }

        private void Update()
        {
            if (_phase != Phase.None) TickScale();
            if (_shakeT >= 0f) TickShake();
        }

        private void TickScale()
        {
            // Clamped so a frame spike (a hitch, or the editor pausing) cannot blow the spring up — a stiff spring
            // integrated with a huge dt goes unstable and the button explodes.
            float dt = Mathf.Min(Time.unscaledDeltaTime, 1f / 30f);

            if (_phase == Phase.Press)
            {
                _t += dt;
                float t = Mathf.Clamp01(_t / Mathf.Max(0.0001f, pressSeconds));
                _d = Mathf.Lerp(_pressFrom, pressScale - 1f, UITween.EaseOutCubic(t));
                _v = 0f;
                if (t >= 1f) _phase = Phase.None;   // sits squashed under the finger until the release
                Apply();
                return;
            }

            // Damped harmonic motion toward rest. Frequency and damping are the authored knobs; stiffness and drag are
            // the physics they imply, so the feel stays the same whatever the frame rate.
            float w = 2f * Mathf.PI * springFrequency;
            float stiffness = w * w;
            float drag = 2f * springDamping * w;

            _v += (-stiffness * _d - drag * _v) * dt;
            _d += _v * dt;

            // Settled: below this the movement is sub-pixel on any real button, and left running it would keep the
            // transform dirty forever.
            if (Mathf.Abs(_d) < 0.0005f && Mathf.Abs(_v) < 0.005f)
            {
                _d = 0f;
                _v = 0f;
                _phase = Phase.None;
            }
            Apply();
        }

        private void TickShake()
        {
            _shakeT += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_shakeT / Mathf.Max(0.0001f, shakeSeconds));

            if (t >= 1f)
            {
                Tr.localPosition = _shakeRest;
                _shakeT = -1f;
                return;
            }

            // Decaying sine: full swing at the start, nothing left at the end. The (1 - t) envelope is what stops it
            // ending on a visible jolt back to centre.
            float swing = Mathf.Sin(t * Mathf.PI * 2f * shakeOscillations) * (1f - t);
            Tr.localPosition = _shakeRest + new Vector3(swing * shakePixels, 0f, 0f);
        }

        /// <summary>Turn the current displacement into a squashed/stretched scale.</summary>
        private void Apply()
        {
            // Y takes the displacement, X takes the opposite — pressed = shorter AND wider, the overshoot = taller AND
            // narrower. Z is left alone; scaling it on a UI element does nothing but confuse anything parented below.
            float y = 1f + _d;
            float x = 1f - _d * stretch;
            Tr.localScale = new Vector3(_baseScale.x * x, _baseScale.y * y, _baseScale.z);
        }

        private void OnDisable()
        {
            // A button hidden mid-press would come back squashed and/or off-centre — its tween is never going to get
            // another Update to finish. Reset to the authored pose every time.
            _phase = Phase.None;
            _held = false;
            _d = 0f;
            _v = 0f;
            Tr.localScale = _baseScale;
            if (_shakeT >= 0f) { Tr.localPosition = _shakeRest; _shakeT = -1f; }
        }

        /// <summary>Apply a house tuning from the bulk editor tool without making the fields public.</summary>
        public void Configure(float press, float pressTime, float squashStretch,
                              float frequency, float damping, float shake, float shakeTime)
        {
            pressScale = press;
            pressSeconds = pressTime;
            stretch = squashStretch;
            springFrequency = frequency;
            springDamping = damping;
            shakePixels = shake;
            shakeSeconds = shakeTime;
        }
    }
}
