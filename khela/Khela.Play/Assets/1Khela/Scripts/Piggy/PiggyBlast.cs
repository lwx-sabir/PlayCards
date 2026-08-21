using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Piggy
{
    /// <summary>
    /// The bank-break payoff: the pig charges up, detonates into its pre-fractured pieces, and the debris cloud
    /// drifts apart and dissolves. Call <see cref="Play"/> from the purchase-success handler.
    ///
    /// Three phases, on unscaled time:
    ///  • CHARGE — the intact pig trembles and swells, intensity ramping as the square of time so it visibly
    ///    builds rather than starting at full rattle. Long enough to be read as "something is about to happen".
    ///  • BANG — one frame: pig out, pieces in, each launched radially from the centre. The pieces are INTEGRATED,
    ///    not tweened (velocity bled off by drag), the same model as the chip burst and the table win juice — a
    ///    tween to a point reads as choreography, integration reads as physics.
    ///  • AFTERMATH — drag kills the launch speed down to a slow constant drift, so the cloud keeps expanding
    ///    gently while every piece swells a touch and fades to nothing. Big bang, then embers.
    ///
    /// The audio is started so its BANG TRANSIENT lands exactly on the visual bang: clips rarely begin at the hit,
    /// so <see cref="bangLeadIn"/> is how far into the clip the transient sits, and the sound simply starts that
    /// long before the swap. A lead-in longer than the charge clamps to "play immediately".
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PiggyBlast : MonoBehaviour
    {
        [Header("The pair")]
        [Tooltip("The intact pig this replaces — the popup's pig image. Hidden on the bang, restored after.")]
        [SerializeField] private RectTransform intactPig;
        [Tooltip("The Piggy_Fractured prefab instance, authored INACTIVE and aligned exactly over the pig. Its " +
                 "children are the pieces; their authored layout is recaptured every play, so replays are exact.")]
        [SerializeField] private RectTransform piecesRoot;

        [Header("Charge — the wind-up")]
        [Tooltip("How long the pig shakes before it goes. Under a second reads as a glitch, not a fuse.")]
        [SerializeField] private float chargeSeconds = 1.25f;
        [Tooltip("Shake radius at full charge, as a fraction of the PIG'S WIDTH - so it reads the same on every " +
                 "aspect. 0.05 = a twentieth of the pig. Canvas units were the old unit and were the bug: the " +
                 "canvas is 2560 REFERENCE wide but only ~1290 actual units wide in portrait, so anything tuned " +
                 "against 2560 came out roughly half the intended size.")]
        [SerializeField] private float chargeShakeWidths = 0.05f;
        [Tooltip("How hard it is already trembling at t=0, as a fraction of the full shake. The ramp is t-squared, " +
                 "which back-loads almost everything into the last third - at 0 a 1.25s charge is a full second of " +
                 "NOTHING and then a bang. 0.18 means it starts alive and builds.")]
        [Range(0f, 1f)][SerializeField] private float chargeFloor = 0.18f;
        [Tooltip("How much the pig inflates by the end of the charge. 1.12 = 12% over authored size.")]
        [SerializeField] private float chargeSwell = 1.12f;

        [Header("Bang — the big bang")]
        [Tooltip("How far the fastest piece ends up from the centre, in PIG-WIDTHS. 1.1 = about one pig out, which " +
                 "reads as debris; past ~2 the cloud is off-screen before you see it. This is a DISTANCE, not a " +
                 "speed, on purpose - speed is derived from it and Drag, so changing Drag now changes how VIOLENT " +
                 "the throw is without also changing where everything lands.")]
        [SerializeField] private float blastTravelWidths = 1.1f;
        [Tooltip("How unequal the launch speeds are: 0 = a perfect ring (never want that), 0.4 = the slowest piece " +
                 "leaves at 60% of the fastest, so the cloud is ragged like debris and not a firework shell.")]
        [Range(0f, 0.9f)][SerializeField] private float speedVariance = 0.4f;
        [Tooltip("How fast the launch speed bleeds off. Higher = the violence is over sooner.")]
        [SerializeField] private float drag = 3.4f;
        [Tooltip("Tumble at launch, degrees/second, either way.")]
        [SerializeField] private float spin = 540f;

        [Header("Aftermath — embers")]
        [Tooltip("The slow OUTWARD drift left after drag has eaten the launch speed — what keeps the cloud gently " +
                 "expanding instead of freezing mid-air.")]
        [SerializeField] private float driftWidthsPerSecond = 0.06f;
        [Tooltip("Beat between the bang and the fade starting — the pieces should be SEEN before they start dying.")]
        [SerializeField] private float fadeDelay = 0.45f;
        [Tooltip("How long the pieces take to dissolve once fading starts.")]
        [SerializeField] private float fadeSeconds = 1.3f;
        [Tooltip("How much each piece grows while it fades — the 'expanding smoke' read. 0.25 = +25%.")]
        [SerializeField] private float growAmount = 0.25f;

        [Header("Particles")]
        [Tooltip("Sparks, dust, glow - anything that should go off ON THE BANG, not before it. Each one is stopped " +
                 "and CLEARED before it plays, so the second preview looks exactly like the first. Turn Play On " +
                 "Awake OFF on all of them, or they fire once at startup and are spent by the time you need them.")]
        [SerializeField] private ParticleSystem[] bangParticles;

        [Header("Audio")]
        [Tooltip("The blast sound. Played so its bang transient lands exactly on the visual bang.")]
        [SerializeField] private Sonity.SoundEvent blastSound;
        [Tooltip("Seconds INTO the clip where the bang transient actually sits (clips often open with a fuse or " +
                 "silence). 0 = the clip starts with the bang.")]
        [SerializeField] private float bangLeadIn = 0f;

        /// <summary>True while a blast is running — callers should not re-trigger or re-render over it.</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>
        /// The pig has just come apart - the exact frame the pieces appear.
        ///
        /// This, not the start of <see cref="Play"/>, is what the payoff sequence hangs off: everything after the
        /// break is timed from the moment of the break, and the charge in front of it is separately tunable. Timing
        /// the follow-up from Play() would silently re-time it every time the charge length changed.
        /// </summary>
        public event Action Banged;

        /// <summary>
        /// The debris has started to FADE - the break has been seen and the wreckage is on its way out.
        ///
        /// This is the beat the payoff hangs the money off: the shell is gone and the eye is free. Raised from
        /// inside the aftermath rather than by a listener re-deriving it, because it is a function of fadeDelay and
        /// would silently drift out of sync every time that was tuned.
        /// </summary>
        public event Action DebrisFading;

        /// <summary>
        /// Put the intact pig back once the debris has gone. TRUE while previewing and tuning; the break director
        /// sets it FALSE, because a bank that was just bought and paid out must not reappear whole a second later.
        /// </summary>
        public bool RestorePig { get; set; } = true;

        /// <summary>Where the pig is - so the payoff can send the amount to the exact spot it broke.</summary>
        public RectTransform IntactPig => intactPig;

        /// <summary>How long the wind-up lasts - so a payoff can time something to land ON the bang.</summary>
        public float ChargeSeconds => chargeSeconds;

        /// <summary>
        /// The debris has died and everything is back at rest - the screen is free to repaint.
        ///
        /// Separate from Play's onDone callback on purpose: onDone belongs to whoever STARTED this blast, while
        /// anything that merely has to stay out of the way (the screen) needs the same beat without owning the
        /// call. One caller, many listeners.
        /// </summary>
        public event Action Finished;

        private sealed class Piece
        {
            public RectTransform Rt;
            public Image Img;
            public Vector2 RestPos;
            public Vector3 RestScale;
            public Quaternion RestRot;
            public Vector2 Dir;
            public float Speed;
            public float Spin;
        }

        private readonly List<Piece> _pieces = new();
        private Coroutine _run;
        private AudioListener _listener;

        /// <summary>Run the whole break: charge → bang → embers. <paramref name="onDone"/> fires when the last
        /// piece has faded and everything is restored — refresh state / pay the chips out from there.</summary>
        public void Play(Action onDone = null)
        {
            if (IsPlaying || intactPig == null || piecesRoot == null) { onDone?.Invoke(); return; }
            IsPlaying = true;
            _run = StartCoroutine(Run(onDone));
        }

        /// <summary>
        /// Fire the blast for TUNING, with no full bank and no purchase - right-click the component in play mode.
        ///
        /// Requires PiggyPanel's Test Mode, which is what puts the popup into the FULL state; the pig and its pieces
        /// live in that view, so without it there is nothing on screen to blow up. Gated rather than merely useless
        /// without it, so the preview cannot fire in a production build even by accident.
        /// </summary>
        [ContextMenu("Preview Break - full payoff (needs PiggyPanel Test Mode)")]
        private void PreviewBlast()
        {
            if (IsPlaying) return;

            // Hand off to the real sequence whenever there is one. Clicking this should show what a player will
            // see - the break, the amount travelling, the chips, the line - not a rehearsal of one part of it,
            // which would be tuned against something that never ships.
            var director = GetComponent<PiggyBreakDirector>();
            if (director != null)
            {
                director.PreviewBreak();
                return;
            }

            var panel = GetComponent<PiggyPanel>();
            if (panel == null || !panel.TestMode)
            {
                Debug.LogWarning($"{name}: turn Test Mode ON in PiggyPanel first. It is what opens the popup in the " +
                                 "FULL state, and the preview is deliberately inert without it.", this);
                return;
            }

            if (!isActiveAndEnabled)
            {
                Debug.LogWarning($"{name}: open the piggy popup first - the blast runs as a coroutine on this " +
                                 "component, and a disabled object can't start one.", this);
                return;
            }

            // The pig lives in the BUSY view now, so raise it for the preview and drop it again when the debris has
            // gone. Without this the blast detonates inside a hidden hierarchy and shows nothing.
            var screen = GetComponent<PiggyScreen>();
            if (screen != null && !screen.Busy)
            {
                screen.SetBusy(true);
                void Clear() { Finished -= Clear; if (screen != null) screen.SetBusy(false); }
                Finished += Clear;
            }

            Play();
        }

        private void OnDisable()
        {
            if (_run != null) { StopCoroutine(_run); _run = null; }

            if (IsPlaying)
            {
                IsPlaying = false;

                // Release the screen even on the cut-short path, or a snapshot held back during the blast would sit
                // unpainted until something else happened to refresh.
                Finished?.Invoke();
            }

            // ALWAYS put the pig back, finished run or not.
            //
            // RestorePig governs the END OF THE SEQUENCE - a bank that was bought and paid out must not pop back
            // whole a second later - and it has no business governing TEARDOWN. Gating this on IsPlaying meant a
            // COMPLETED break left the pig switched off for good: the next open had nothing to explode, so the
            // blast silently did nothing while the rest of the payoff carried on as normal.
            RestorePieces();
            if (piecesRoot != null) piecesRoot.gameObject.SetActive(false);

            if (intactPig != null)
            {
                intactPig.gameObject.SetActive(true);
                if (_posed)
                {
                    // Only once a run has actually captured them. Before that these are zero, and writing a zero
                    // scale would flatten the pig rather than restore it.
                    intactPig.localScale = _pigScale;
                    intactPig.anchoredPosition = _pigPos;
                }
            }

            RestorePig = true;

            // Put the burst objects away too. Any looping system inside one keeps emitting until something stops it,
            // and switching the object off is what ends that when the popup closes - which is exactly the lifetime
            // Reza asked for: sparkles linger through the payoff, gone by the next open.
            if (bangParticles != null)
                foreach (var ps in bangParticles)
                    if (ps != null && ps.gameObject.activeSelf) ps.gameObject.SetActive(false);
        }

        /// <summary>
        /// One frame's worth of time, never more than <see cref="MaxStep"/>.
        ///
        /// Hand-integrated motion MUST clamp its timestep. The frame right after the pieces are first activated is
        /// routinely a long one - fourteen objects switching on, a canvas rebuild, the first sprite upload - and at
        /// launch speed an unclamped 250ms frame moves a piece most of its entire journey in a single step. The
        /// blast then appears to happen in one frame, but only sometimes, which is what makes it look like a
        /// haunting rather than a bug. Clamping makes a hitch SLOW the blast instead of skipping it forward.
        ///
        /// Editor frames are the most erratic of all, so this shows up there first - it is not an editor artefact.
        /// </summary>
        private const float MaxStep = 1f / 30f;

        private static float Step() => Mathf.Min(Time.unscaledDeltaTime, MaxStep);

        private IEnumerator Run(Action onDone)
        {
            // ---- capture the pig's rest pose ----
            //
            // The pig's own idle is deliberately LEFT RUNNING. The intact pig is the live one right up to the bang:
            // it keeps its animation and the charge shake composes on top (the idle drives the pig image, this drives
            // the container above it). Silencing it would make the pig go still exactly when it should look most
            // alive — and the bang only reads as the REAL pig coming apart if the real pig was moving until it did.

            // Start from a WHOLE pig however the last run ended. A real break deliberately leaves it hidden
            // (RestorePig false), so without this a second run in the same session would shake and hide something
            // already invisible - no explosion, while everything downstream carried on as if there had been one.
            intactPig.gameObject.SetActive(true);
            if (piecesRoot != null) piecesRoot.gameObject.SetActive(false);

            _pigScale = intactPig.localScale;
            _pigPos = intactPig.anchoredPosition;
            _posed = true;

            // Everything below is measured in PIG-WIDTHS. The pig is the only thing on screen whose size means
            // anything here, and it is the thing the debris has to look proportionate to; the canvas is the wrong
            // yardstick because its real width depends on the device aspect, not on the reference resolution.
            float unit = Mathf.Max(1f, piecesRoot.rect.width);

            bool audioFired = false;
            float audioAt = Mathf.Max(0f, chargeSeconds - Mathf.Max(0f, bangLeadIn));

            // ---- CHARGE: intensity ramps as t², so the last third carries most of the violence ----
            for (float t = 0f; t < chargeSeconds; t += Step())
            {
                if (!audioFired && t >= audioAt) { FireAudio(); audioFired = true; }

                float k = t / chargeSeconds;
                float intensity = Mathf.Lerp(chargeFloor, 1f, k * k);
                intactPig.anchoredPosition =
                    _pigPos + UnityEngine.Random.insideUnitCircle * (unit * chargeShakeWidths * intensity);
                intactPig.localScale = _pigScale * Mathf.Lerp(1f, chargeSwell, intensity);
                yield return null;
            }
            if (!audioFired) FireAudio();   // lead-in longer than the charge: late is worse than early

            // ---- BANG: swap, and launch every piece radially from the centre ----
            intactPig.anchoredPosition = _pigPos;
            intactPig.localScale = _pigScale;
            intactPig.gameObject.SetActive(false);

            CapturePieces();
            RestorePieces();
            piecesRoot.gameObject.SetActive(true);

            FireParticles();
            Banged?.Invoke();

            // Radial directions. The builder anchors every piece to the root's TOP-LEFT with a (0,1) pivot, so a
            // piece's centre in anchored space is its rest position plus half its size (Y down), and the document
            // centre is (w/2, -h/2) in the same convention. Dead-centre pieces get a random direction.
            var docCentre = new Vector2(piecesRoot.rect.width * 0.5f, -piecesRoot.rect.height * 0.5f);
            foreach (var p in _pieces)
            {
                var centre = p.RestPos + new Vector2(p.Rt.rect.width * 0.5f, -p.Rt.rect.height * 0.5f);
                var offset = centre - docCentre;
                p.Dir = offset.sqrMagnitude > 1f ? offset.normalized : UnityEngine.Random.insideUnitCircle.normalized;
                // Integrating v0*e^(-drag*t) to infinity gives v0/drag, so the speed that lands a piece at the
                // requested distance is simply distance*drag. Solve it here rather than asking anyone to.
                p.Speed = unit * blastTravelWidths * drag * (1f - speedVariance * UnityEngine.Random.value);
                p.Spin = UnityEngine.Random.Range(-spin, spin);
            }

            // ---- AFTERMATH: integrate — drag bleeds the bang down to the drift, then the embers dissolve ----
            float total = fadeDelay + fadeSeconds;
            bool fading = false;
            for (float t = 0f; t < total; )
            {
                // ONE dt for the step and the clock. They used to be read separately, so the fade's clock and the
                // motion could advance by different amounts on the same frame.
                float dt = Step();
                t += dt;

                float decay = Mathf.Exp(-drag * dt);
                float fade = Mathf.Clamp01((t - fadeDelay) / fadeSeconds);

                if (!fading && t >= fadeDelay) { fading = true; DebrisFading?.Invoke(); }

                foreach (var p in _pieces)
                {
                    p.Speed *= decay;
                    p.Spin *= Mathf.Exp(-drag * 0.5f * dt);   // tumble outlives the launch speed a little
                    p.Rt.anchoredPosition += p.Dir * ((p.Speed + unit * driftWidthsPerSecond) * dt);
                    p.Rt.localRotation *= Quaternion.Euler(0f, 0f, p.Spin * dt);
                    p.Rt.localScale = p.RestScale * (1f + growAmount * fade);
                    if (p.Img != null)
                    {
                        var c = p.Img.color;
                        c.a = 1f - fade;
                        p.Img.color = c;
                    }
                }
                yield return null;
            }

            // ---- done: everything back at rest, hidden, ready for the next bank ----
            RestorePieces();
            piecesRoot.gameObject.SetActive(false);
            if (RestorePig) intactPig.gameObject.SetActive(true);

            IsPlaying = false;
            _run = null;
            onDone?.Invoke();
            Finished?.Invoke();
        }

        /// <summary>The pieces as authored, captured once — their layout IS the intact pig, so it must be exact.</summary>
        private void CapturePieces()
        {
            if (_pieces.Count > 0) return;
            foreach (Transform child in piecesRoot)
            {
                var rt = child as RectTransform;
                if (rt == null) continue;
                _pieces.Add(new Piece
                {
                    Rt = rt,
                    Img = child.GetComponent<Image>(),
                    RestPos = rt.anchoredPosition,
                    RestScale = rt.localScale,
                    RestRot = rt.localRotation,
                });
            }
        }

        private void RestorePieces()
        {
            foreach (var p in _pieces)
            {
                if (p.Rt == null) continue;
                p.Rt.anchoredPosition = p.RestPos;
                p.Rt.localScale = p.RestScale;
                p.Rt.localRotation = p.RestRot;
                if (p.Img != null)
                {
                    var c = p.Img.color;
                    c.a = 1f;
                    p.Img.color = c;
                }
            }
        }

        /// <summary>
        /// Every bang particle, restarted from nothing.
        ///
        /// Stop-and-CLEAR rather than a bare Play: a system still holding particles from the last blast would keep
        /// them, so the second preview would look busier than the first and no amount of tuning would make the two
        /// agree. Clearing is what makes the effect repeatable, which is what makes it tunable.
        /// </summary>
        private void FireParticles()
        {
            if (bangParticles == null) return;

            foreach (var ps in bangParticles)
            {
                if (ps == null) continue;

                // ACTIVATE FIRST. Play() on a system whose GameObject is inactive does nothing whatsoever, and it
                // does it silently - no warning, no error, no particles. Inactive is also exactly how you would
                // author a burst that must not be visible before the bang, so this is the normal case, not an edge.
                if (!ps.gameObject.activeSelf) ps.gameObject.SetActive(true);

                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Play(true);
            }
        }

        private void FireAudio()
        {
            if (blastSound == null) return;

            // Parked on the ENABLED listener — same rule as RewardFly: a 3D container played at canvas coordinates
            // attenuates to silence, and a blind find can return a disabled listener on an active object. The owner
            // is a dedicated child, never this transform — moving THIS transform would drag the popup around.
            if (_listener == null || !_listener.isActiveAndEnabled)
            {
                _listener = null;
                foreach (var l in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                    if (l.isActiveAndEnabled) { _listener = l; break; }
            }

            if (_audioOwner == null)
            {
                var go = new GameObject("BlastVoice");
                go.transform.SetParent(transform, false);
                _audioOwner = go.transform;
            }
            if (_listener != null) _audioOwner.position = _listener.transform.position;
            blastSound.Play(_audioOwner);
        }

        private Transform _audioOwner;
        private bool _posed;
        private Vector3 _pigScale;
        private Vector2 _pigPos;
    }
}
