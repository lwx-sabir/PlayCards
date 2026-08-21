using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.UI.RewardFly
{
    /// <summary>
    /// Marks a HUD counter as the destination for a reward's flight — "Chips land here, Kash lands there".
    ///
    /// Registers itself by key, so whatever is paying out (a pass day, a chest, a gift, a mission) never needs a
    /// reference to the scene's HUD: it asks the registry for the target of the reward it's paying. A scene without a
    /// counter for some currency simply has no target, and the flight is skipped rather than throwing.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RewardFlyTarget : MonoBehaviour
    {
        [Tooltip("Reward id this counter shows: Chips / Coins / Gems / Kash / XP, a chest key, or an item key. " +
                 "Matched case-insensitively against the reward the server paid out.")]
        [SerializeField] private string rewardId = "Chips";

        [Tooltip("Where chips should land. Empty = this object's own RectTransform.")]
        [SerializeField] private RectTransform landingPoint;

        [Tooltip("Fired when a reward finishes landing here — punch the counter, play a coin sound, start a count " +
                 "roll. Optional: the flight and the balance update happen with or without it.")]
        [SerializeField] private UnityEngine.Events.UnityEvent onArrival;

        [Header("Impact — the hit felt on EVERY piece")]
        [Tooltip("What visibly reacts when a piece lands: usually the counter's icon or its whole frame. " +
                 "Empty = this object.")]
        [SerializeField] private RectTransform punchTarget;
        [Tooltip("How hard the counter kicks per piece, as a fraction of its size. 0 = no built-in punch.")]
        [SerializeField] private float punchScale = 0.16f;
        [Tooltip("How long one kick takes. Shorter than the gap between pieces, or the kicks blur into one swell.")]
        [SerializeField] private float punchDuration = 0.18f;
        [Tooltip("Fired for EVERY piece that lands, not just the last one — the beat to tick a counter, play a coin " +
                 "chink or spawn a spark on.")]
        [SerializeField] private UnityEngine.Events.UnityEvent onPieceArrival;

        [Header("Impact sound (optional)")]
        [Tooltip("Played once per piece that lands HERE. Leave empty and this counter is silent — useful when a " +
                 "screen already owns its audio (PassAudio, DailyAudio), which would otherwise play the same chink " +
                 "twice for every chip.")]
        [SerializeField] private Sonity.SoundEvent impactSound;

        [Tooltip("How many impacts may ring at once. Sonity treats (event, OWNER) as ONE voice and re-triggering it " +
                 "STOPS the playing instance — so without a rotating owner per hit a stream of chips silences itself " +
                 "into a single stuttering tick. Around the number of pieces in a burst.")]
        [Range(1, 32)][SerializeField] private int impactVoices = 12;

        /// <summary>
        /// Global off switch for every counter's impact sound.
        ///
        /// For screens that own their own mix: a panel with an audio component already playing per-piece landings can
        /// turn this off for the duration rather than having each target assigned nothing. The sound still only plays
        /// where one is authored — this is the override, not the enable.
        /// </summary>
        public static bool ImpactSoundsEnabled = true;

        /// <summary>
        /// Log every piece that reports in, from anywhere. Set it once and the whole chain becomes visible: whether
        /// pieces land at all, which target caught them, and whether that target has a sound to play.
        ///
        /// Static rather than a field because the interesting case is when NOTHING happens — and a per-instance
        /// toggle can only be ticked on a target you already know is running.
        /// </summary>
        public static bool LogPieces;

        /// <summary>
        /// Every LIVE counter per reward id, oldest first — a STACK, not a single slot.
        ///
        /// Two counters for the same currency are the normal case, not an edge one: the Home HUD has a chip counter,
        /// and a modal popup opened over it has its own. With a single slot the popup's registration overwrote Home's,
        /// and closing the popup then deleted the entry outright — leaving NO target for chips even though Home's
        /// counter was still on screen. Keeping them stacked means the topmost one wins while it is open and the one
        /// underneath resumes when it closes, which is exactly what the player sees.
        /// </summary>
        private static readonly Dictionary<string, List<RewardFlyTarget>> Registry =
            new Dictionary<string, List<RewardFlyTarget>>(System.StringComparer.OrdinalIgnoreCase);

        // ---------------- the burst channel ----------------
        //
        // A balance HUD needs to know two things that only the flight knows: that a burst is COMING (so it can hold the
        // number instead of snapping to the server's value seconds before the first chip lands) and HOW FAR THROUGH it
        // is (so the number ticks up with the pieces). Both are published here rather than through a direct reference,
        // because the paying screen is usually a runtime-instantiated panel and the HUD is scene furniture — neither
        // can hold a reference to the other.
        //
        // Keying it off THIS registry is not just convenience. RewardFly skips any reward with no target, so "a target
        // exists for this currency" is precisely the condition under which pieces will actually fly — and therefore
        // precisely the condition under which holding the number is correct.

        private static readonly HashSet<string> ArmedBursts =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>The pieces for this reward just launched: the reward id and how many are flying.</summary>
        public static event System.Action<string, int> BurstStarted;

        /// <summary>
        /// The same launch, plus WHAT THE PIECES ARE WORTH — a separate event so that listeners which only care that
        /// a burst happened (a sound bank) are not dragged along by a signature they have no use for.
        ///
        /// The amount matters because a counter cannot wait for the wallet. A queued claim against a distant server
        /// lands seconds after its pieces do, so a HUD that only moves when the balance moves shows nothing while the
        /// chips arrive and then jumps later. Carrying the value lets it walk up with them and reconcile when the real
        /// number appears.
        /// </summary>
        public static event System.Action<string, int, decimal> BurstValue;

        /// <summary>A piece landed: the reward id and how far through that reward's burst it is (0..1).</summary>
        public static event System.Action<string, float> BurstProgress;

        /// <summary>The burst for this reward finished, or gave up. Any hold on it must be released.</summary>
        public static event System.Action<string> BurstEnded;

        /// <summary>
        /// Announce that a burst for this reward is about to fly, so a HUD can hold its number. Returns false — and
        /// arms nothing — when no counter is registered for the id, because then nothing will fly and the balance
        /// should simply update as usual.
        ///
        /// Call this as EARLY as the payout is known (the moment the claim response arrives), not when the pieces
        /// spawn: the wallet push that follows a claim is only milliseconds behind, and a hold armed after it is too
        /// late to be worth anything.
        /// </summary>
        public static bool ArmBurst(string rewardId)
        {
            if (string.IsNullOrWhiteSpace(rewardId)) return false;
            var key = rewardId.Trim();
            if (Lookup(key) == null) return false;
            ArmedBursts.Add(key);
            return true;
        }

        /// <summary>Is a burst for this reward armed or in flight? The HUD's "should I hold this credit" test.</summary>
        public static bool IsBurstArmed(string rewardId)
            => !string.IsNullOrWhiteSpace(rewardId) && ArmedBursts.Contains(rewardId.Trim());

        /// <summary>The pieces are away. Raised by the flight itself, so anything hanging off it (a burst sound, a
        /// camera shake) fires on the launch rather than on the decision to launch.</summary>
        public static void NotifyBurstStarted(string rewardId, int pieces, decimal amount)
        {
            if (string.IsNullOrWhiteSpace(rewardId)) return;
            var key = rewardId.Trim();
            BurstStarted?.Invoke(key, pieces);
            BurstValue?.Invoke(key, pieces, amount);
        }

        /// <summary>Release the hold on this reward — its last piece landed, or nothing is coming after all.</summary>
        public static void EndBurst(string rewardId)
        {
            if (string.IsNullOrWhiteSpace(rewardId)) return;
            var key = rewardId.Trim();
            if (!ArmedBursts.Remove(key)) return;
            BurstEnded?.Invoke(key);
        }

        public string RewardId => rewardId;
        public RectTransform Landing => landingPoint != null ? landingPoint : (RectTransform)transform;

        private void OnEnable()
        {
            if (string.IsNullOrWhiteSpace(rewardId)) return;
            var key = rewardId.Trim();

            if (!Registry.TryGetValue(key, out var list)) Registry[key] = list = new List<RewardFlyTarget>(2);
            list.Remove(this);   // re-enabled without a disable (a pooled panel) must not queue twice
            list.Add(this);      // newest wins — the popup over the HUD
        }

        private void OnDisable()
        {

            if (string.IsNullOrWhiteSpace(rewardId)) return;
            if (Registry.TryGetValue(rewardId.Trim(), out var list)) list.Remove(this);
        }

        /// <summary>The landing point for a reward id, or null when this scene shows no counter for it.</summary>
        public static RectTransform Find(string rewardId)
        {
            var target = Lookup(rewardId);
            return target != null ? target.Landing : null;
        }

        /// <summary>Tell the counter something landed — lets a HUD play its own punch/roll without this system
        /// knowing what a counter is.</summary>
        public static void NotifyArrived(string rewardId)
        {
            var target = Lookup(rewardId);
            if (target != null) target.onArrival?.Invoke();
        }

        /// <summary>
        /// One piece hit the counter, <paramref name="progress01"/> of the way through this reward's burst. The beat
        /// the impact is felt on — every chip, not just the last — and the beat a held balance ticks up on.
        /// A progress of 1 ends the burst, so a hold can never outlive the pieces that justified it.
        /// </summary>
        public static void NotifyPiece(string rewardId, float progress01 = 1f, bool punch = true)
        {
            float p = Mathf.Clamp01(progress01);

            var target = Lookup(rewardId);

            if (LogPieces)
                Debug.Log($"[RewardFlyTarget] piece '{rewardId}' p={p:0.00} " +
                          $"target={(target != null ? target.name : "NONE")} " +
                          $"impactSound={(target != null && target.impactSound != null ? target.impactSound.name : "none")} " +
                          $"progressListeners={(BurstProgress != null ? BurstProgress.GetInvocationList().Length : 0)}");
            if (target != null && punch)
            {
                target.onPieceArrival?.Invoke();
                target.Punch();
            }

            // Its own gate, deliberately not the punch's: whether the counter kicks and whether it makes a noise are
            // separate decisions, and a screen that wants one without the other should not have to give up both.
            if (target != null) target.PlayImpact();

            // Published even with no target instance left alive (a HUD torn down mid-flight): a listener holding a
            // number still has to be told, or it waits out its timeout showing a stale figure.
            BurstProgress?.Invoke(rewardId, p);
            if (p >= 1f) EndBurst(rewardId);
        }

        /// <summary>The counter that should receive this reward: the most recently enabled one still alive. That is
        /// the topmost on screen — a popup's own counter while it's open, the HUD's underneath once it closes.</summary>
        private static RewardFlyTarget Lookup(string rewardId)
        {
            if (string.IsNullOrWhiteSpace(rewardId)) return null;
            if (!Registry.TryGetValue(rewardId.Trim(), out var list)) return null;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] != null) return list[i];
                list.RemoveAt(i);   // destroyed without OnDisable (a scene unload) — tidy as we go
            }
            return null;
        }

        /// <summary>
        /// A single kick, restarted on each hit.
        ///
        /// Restarting rather than layering matters: twenty pieces arriving would otherwise stack twenty punches into
        /// one slow bulge. Restarting makes each landing its own snap, which is what makes a stream of chips feel like
        /// it is being *caught*. The base scale is remembered from the first punch so repeated kicks can never drift
        /// the counter's size.
        /// </summary>
        public void Punch()
        {
            if (punchScale <= 0f || punchDuration <= 0f) return;

            var rect = punchTarget != null ? punchTarget : (RectTransform)transform;
            if (rect == null || !isActiveAndEnabled) return;

            if (!_baseCaptured) { _baseScale = rect.localScale; _baseCaptured = true; }
            if (_punch != null) StopCoroutine(_punch);
            _punch = StartCoroutine(PunchRoutine(rect));
        }

        /// <summary>
        /// One chip's landing sound, on a ROTATING owner.
        ///
        /// The rotation is the whole trick. Sonity keys a voice on (SoundEvent, owner Transform) and allows one per
        /// key — re-triggering the same pair stops the instance that was playing. Firing twenty landings against this
        /// one transform would therefore produce a single stuttering tick rather than a stream of coins, and a poly
        /// group cannot fix it because a poly group only ever LOWERS the limit.
        ///
        /// The owners are parked on the listener because this lives on a Canvas, whose world coordinates run to
        /// hundreds of units: a 3D SoundContainer played out there attenuates to nothing, logs a cheerful "Play", and
        /// is silent in the headphones. On the listener a 3D container is centred and a 2D one ignores position.
        /// </summary>
        public void PlayImpact()
        {
            if (!ImpactSoundsEnabled || impactSound == null || !isActiveAndEnabled) return;

            if (_voices == null || _voices.Length != impactVoices)
            {
                var old = _voices;
                _voices = new Transform[Mathf.Max(1, impactVoices)];
                for (int i = 0; i < _voices.Length; i++)
                {
                    if (old != null && i < old.Length && old[i] != null) { _voices[i] = old[i]; continue; }
                    var go = new GameObject($"ImpactVoice_{i}");
                    go.transform.SetParent(transform, false);
                    _voices[i] = go.transform;
                }
            }

            _voice = (_voice + 1) % _voices.Length;
            var voice = _voices[_voice] != null ? _voices[_voice] : transform;

            if (_listener == null) _listener = FindAnyObjectByType<AudioListener>();
            if (_listener != null) voice.position = _listener.transform.position;

            impactSound.Play(voice);
        }

        private IEnumerator PunchRoutine(RectTransform rect)
        {
            float t = 0f;
            while (t < punchDuration)
            {
                t += Mathf.Min(Time.unscaledDeltaTime, 0.034f);
                float u = Mathf.Clamp01(t / punchDuration);
                // Out fast, back slower — a hit, not a pulse.
                float kick = u < 0.35f ? u / 0.35f : 1f - (u - 0.35f) / 0.65f;
                rect.localScale = _baseScale * (1f + punchScale * kick * kick);
                yield return null;
            }
            rect.localScale = _baseScale;
            _punch = null;
        }

        private Coroutine _punch;
        private Vector3 _baseScale = Vector3.one;
        private bool _baseCaptured;

        private Transform[] _voices;
        private int _voice;
        private AudioListener _listener;
    }
}
