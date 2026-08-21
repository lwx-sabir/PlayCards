using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Gives a STILL image a pulse — breathing, a slow sway, and the occasional twitch — so a single sprite reads as
    /// something alive rather than a sticker.
    ///
    /// Everything is procedural, and deliberately so: an artist-free way to animate art that has no frames, and one
    /// that never loops visibly, because the breath, the bob and the sway run on DIFFERENT periods. Three sine waves
    /// whose periods don't divide each other take minutes to line up again, which is why this reads as idling rather
    /// than as a two-second animation on repeat. The random phase at startup also means two of these side by side are
    /// never in step.
    ///
    /// It is the ONLY writer of the transform's scale, position offset and rotation, on purpose. A tween library
    /// punching the same values would fight this every frame and the two would visibly stutter — so the reaction to
    /// a deposit is built in (<see cref="Poke"/>) as a damped spring rather than left to DOTween.
    ///
    /// Put it on the IMAGE, not on the frame or the layout parent: it moves what it is attached to.
    /// </summary>
    [ExecuteAlways]   // for the edit-mode preview only — every play-mode path is unchanged
    [DisallowMultipleComponent]
    public sealed class IdleMotion : MonoBehaviour
    {
#if UNITY_EDITOR
        [Header("Dev")]
        [Tooltip("Preview the idle in EDIT mode, no Play needed. The authored pose is recaptured when this turns on, " +
                 "restored the moment it turns off — and restored around every scene save, so the animated pose can " +
                 "never be serialized as the artwork's resting pose. Editor-only; stripped from builds.")]
        [SerializeField] private bool previewInEditor;
        private bool _previewing;
        private double _lastEditorTime;
#endif
        [Header("Breathing")]
        [Tooltip("How much it swells, as a fraction of its size. 0.02–0.05 is a breath; past 0.1 it looks inflated.")]
        [Range(0f, 0.2f)][SerializeField] private float breatheAmount = 0.035f;
        [Tooltip("Seconds per breath. Slower reads as calm and heavy, faster as nervous — a fat piggy wants slow.")]
        [SerializeField] private float breathePeriod = 2.6f;
        [Tooltip("How much the width squeezes as the height swells. 1 = volume looks preserved (the classic squash), " +
                 "0 = it just gets bigger, which reads as zooming rather than breathing.")]
        [Range(0f, 1.5f)][SerializeField] private float squashRatio = 0.75f;

        [Tooltip("Keep the BOTTOM edge planted while it breathes, whatever the pivot is. Off, a centre-pivoted " +
                 "sprite grows downward as well as up and looks like it's floating rather than sitting on something.")]
        [SerializeField] private bool grounded = true;

        [Header("Bob")]
        [Tooltip("Vertical drift in pixels. Small — this is weight shifting, not hovering.")]
        [SerializeField] private float bobPixels = 3.5f;
        [Tooltip("Seconds per bob. Kept OFF the breathing period on purpose, so the two never sync into one motion.")]
        [SerializeField] private float bobPeriod = 3.7f;

        [Header("Sway")]
        [Tooltip("Tilt in degrees, each way. Under a degree is subliminal; over three looks drunk.")]
        [Range(0f, 8f)][SerializeField] private float swayDegrees = 1.4f;
        [Tooltip("Seconds per sway. A third period, again deliberately not a multiple of the others.")]
        [SerializeField] private float swayPeriod = 5.3f;

        [Header("Twitch — the thing that sells it")]
        [Tooltip("A little kick every so often. Idle motion alone is too regular to be alive; an unpredictable " +
                 "twitch is what makes the eye read intent. 0 = never twitch.")]
        [SerializeField] private float twitchStrength = 0.5f;
        [Tooltip("Average seconds between twitches.")]
        [SerializeField] private float twitchEvery = 4.5f;
        [Tooltip("Random spread on that interval, so it is never a metronome.")]
        [SerializeField] private float twitchJitter = 2.5f;

        [Header("Reaction")]
        [Tooltip("How hard a Poke hits — the squash when something lands in it. Call Poke() from the coin's landing, " +
                 "a UnityEvent, or a RewardFlyTarget's On Piece Arrival.")]
        [Range(0f, 1f)][SerializeField] private float pokeStrength = 0.55f;
        [Tooltip("How fast a poke settles. Higher springs back sooner; too high and the wobble is gone before it reads.")]
        [SerializeField] private float pokeDamping = 6.5f;
        [Tooltip("Wobbles per second while it settles. 2–4 gives a bouncy, rubbery feel.")]
        [SerializeField] private float pokeFrequency = 3.2f;

        /// <summary>
        /// React to something landing in it — a coin, a payout, a tap. Squashes and springs back, on top of whatever
        /// the idle is doing rather than instead of it, so the two blend rather than snapping between states.
        /// </summary>
        public void Poke() => Poke(1f);

        /// <summary><paramref name="scale"/> multiplies the authored strength: 0.3 for a small coin, 1 for a jackpot.</summary>
        public void Poke(float scale)
        {
            _impulse = Mathf.Max(_impulse, pokeStrength * Mathf.Max(0f, scale));
            _impulseTime = 0f;
        }

        private void Awake()
        {
            CaptureBase();

            // A random start means two of these in one screen — a piggy and a chest — never breathe in unison, which
            // is the single most obvious tell that motion is scripted.
            _phase = Random.Range(0f, 100f);
            ScheduleTwitch();
        }

        private void CaptureBase()
        {
            _rect = transform as RectTransform;

            _baseScale = transform.localScale;
            if (_baseScale.x <= 0.0001f || _baseScale.y <= 0.0001f) _baseScale = Vector3.one;
            _baseRotation = transform.localRotation.eulerAngles.z;
            _basePos = _rect != null ? _rect.anchoredPosition : (Vector2)transform.localPosition;
        }

        /// <summary>Back to exactly the authored pose. A panel closed mid-breath — or an editor preview switched
        /// off — would otherwise store the squashed values as the resting pose, drifting from the artwork.</summary>
        private void RestorePose()
        {
            transform.localScale = _baseScale;
            transform.localRotation = Quaternion.Euler(0f, 0f, _baseRotation);
            if (_rect != null) _rect.anchoredPosition = _basePos; else transform.localPosition = _basePos;

            _impulse = 0f;
        }

        private void OnDisable()
        {
            RestorePose();
#if UNITY_EDITOR
            if (_previewing) StopPreview(restore: false);   // pose is already restored above
#endif
        }

        private void Update()
        {
            // Unscaled: this is UI, and it must keep breathing over a paused game or a frozen table.
            float dt;
            if (Application.isPlaying) dt = Time.unscaledDeltaTime;
            else
            {
#if UNITY_EDITOR
                if (!previewInEditor)
                {
                    if (_previewing) StopPreview(restore: true);
                    return;
                }
                if (!_previewing) StartPreview();

                // Edit mode has no frame loop — Update only fires on repaints. Requesting the next player-loop tick
                // from inside the current one is what keeps it breathing without Play. deltaTime is meaningless
                // here, so time comes from the editor clock, capped so a stall doesn't land as one giant lurch.
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                double now = UnityEditor.EditorApplication.timeSinceStartup;
                dt = Mathf.Min((float)(now - _lastEditorTime), 0.05f);
                _lastEditorTime = now;
#else
                return;
#endif
            }
            _phase += dt;

            float breathe = Sine(_phase, breathePeriod);
            float bob = Sine(_phase + 11.3f, bobPeriod);      // offset so it doesn't start at the same point
            float sway = Sine(_phase + 23.7f, swayPeriod);

            // The twitch, and any poke, arrive as one decaying spring rather than as separate animations — so a poke
            // during a twitch strengthens it instead of cutting it off.
            float impulse = 0f;
            if (_impulse > 0.0001f)
            {
                _impulseTime += dt;
                float decay = Mathf.Exp(-pokeDamping * _impulseTime);
                impulse = _impulse * decay * Mathf.Cos(_impulseTime * pokeFrequency * Mathf.PI * 2f);
                if (decay < 0.01f) _impulse = 0f;
            }

            if (twitchStrength > 0f && _phase >= _nextTwitch)
            {
                Poke(twitchStrength);
                ScheduleTwitch();
            }

            // Squash and stretch: taller means narrower. Doing only one of the two is what makes procedural idles
            // look like a zoom.
            float stretch = breathe * breatheAmount + impulse * -0.5f;   // a poke squashes DOWN, hence the sign
            float sy = 1f + stretch;
            float sx = 1f - stretch * squashRatio;

            transform.localScale = new Vector3(_baseScale.x * sx, _baseScale.y * sy, _baseScale.z);
            transform.localRotation = Quaternion.Euler(0f, 0f, _baseRotation + sway * swayDegrees + impulse * 4f);

            float y = bob * bobPixels;

            // Keep the feet on the floor. Scaling by s about a pivot p moves the bottom edge by -h·p·(s-1); adding it
            // back is what turns "the sprite got bigger" into "the sprite pressed down into the ground".
            if (grounded && _rect != null)
                y += _rect.rect.height * _rect.pivot.y * (sy - 1f) * _baseScale.y;

            var pos = new Vector2(_basePos.x, _basePos.y + y);
            if (_rect != null) _rect.anchoredPosition = pos; else transform.localPosition = pos;
        }

#if UNITY_EDITOR
        private void StartPreview()
        {
            _previewing = true;
            CaptureBase();   // the pose as it stands NOW is the truth — Awake's snapshot may predate hand edits
            _lastEditorTime = UnityEditor.EditorApplication.timeSinceStartup;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += OnSceneSaving;
        }

        private void StopPreview(bool restore)
        {
            _previewing = false;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= OnSceneSaving;
            if (restore) RestorePose();
        }

        // The save guard: whichever frame a save lands on, the FILE gets the authored pose, never the animated one.
        // The preview carries on from the next tick, so visually nothing even blinks.
        private void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path) => RestorePose();
#endif

        private void ScheduleTwitch()
            => _nextTwitch = _phase + Mathf.Max(0.5f, twitchEvery + Random.Range(-twitchJitter, twitchJitter));

        private static float Sine(float t, float period)
            => period <= 0.01f ? 0f : Mathf.Sin(t / period * Mathf.PI * 2f);

        private RectTransform _rect;
        private Vector3 _baseScale = Vector3.one;
        private Vector2 _basePos;
        private float _baseRotation;
        private float _phase;
        private float _nextTwitch;
        private float _impulse;
        private float _impulseTime;
    }
}
