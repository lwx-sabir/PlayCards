using System;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace PlayCard.UI.RewardFly
{
    /// <summary>One reward flying from somewhere to its counter.</summary>
    public struct RewardFlyItem
    {
        /// <summary>Reward id — "Chips", "Kash", a chest or item key. Picks the target and the fallback icon.</summary>
        public string RewardId;
        /// <summary>How much was granted. Drives the floating label and, unless overridden, how many pieces fly.</summary>
        public decimal Amount;
        /// <summary>Artwork for the flying pieces. Null falls back to the icon set, then the prefab's own sprite.</summary>
        public Sprite Icon;
        /// <summary>Override the number of pieces for this reward. 0 = derive it from the amount.</summary>
        public int Pieces;
        /// <summary>Where it flies FROM. Null uses the request's shared source.</summary>
        public RectTransform From;
        /// <summary>Where it flies TO. Null resolves through <see cref="RewardFlyTarget"/>.</summary>
        public RectTransform To;
    }

    /// <summary>
    /// The collect juice, as a service: a burst of icons explodes out of whatever was collected and is pulled into the
    /// matching HUD counter — several currencies at once, each to its own counter, each with its own art.
    ///
    /// The motion is INTEGRATED, not tweened, and that is the whole point. Tweening every piece to a ring over a fixed
    /// duration makes them travel in lockstep and land as a rotating circle; launching each with its own velocity and
    /// letting gravity and drag act on it scatters them to genuinely different distances, so when the magnet switches
    /// on they arrive one by one on their own. Same model the table's win juice uses, which is the bar for feel here.
    ///
    /// Standalone by design: knows nothing about any game, so a pass day, a chest, a gift or a mission can all pay
    /// through it. Pieces are pooled and pre-warmed (spawning a burst mid-frame is what causes a hitch), and
    /// everything runs on unscaled time so the juice still plays over a paused game.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RewardFly : MonoBehaviour
    {
        [Serializable]
        public sealed class IconEntry
        {
            [Tooltip("Reward id: Chips / Kash / Gems / XP, a chest key, an item key.")]
            public string rewardId;
            public Sprite icon;
        }

        [Header("Setup")]
        [Tooltip("A UI piece prefab — an Image. Pooled, so this is spawned a handful of times and reused forever.")]
        [SerializeField] private GameObject piecePrefab;
        [Tooltip("Canvas-space parent the pieces live under while flying. Empty = this object.")]
        [SerializeField] private RectTransform flyLayer;
        [Tooltip("Default art per reward id, used when the caller doesn't supply one.")]
        [SerializeField] private IconEntry[] icons;

        [Header("Landing sound — plays on EVERY piece that lands")]
        [Tooltip("One row per reward id: the chip chink, the Kash chime. Matched case-insensitively against the id " +
                 "the server paid ('Chips', 'Kash', 'Piggy', a chest key).\n\n" +
                 "Played from HERE, by the component that owns the pieces — so it needs no listener subscribed, no " +
                 "counter registered and no panel audio script alive. If a piece lands, this plays.")]
        [SerializeField] private LandingSound[] landingSounds;

        [Tooltip("Used for any reward with no row above. Leave empty to let unlisted rewards land silently.")]
        [SerializeField] private Sonity.SoundEvent defaultLandingSound;

        [Tooltip("How many impacts may ring at once. Sonity keys a voice on (event, OWNER) and allows one per key — " +
                 "re-triggering the same pair STOPS what was playing — so each hit needs its own owner or a stream of " +
                 "chips collapses into one stuttering tick. Around the number of pieces in a burst.")]
        [Range(1, 32)][SerializeField] private int landingSoundVoices = 12;

        [Serializable]
        public sealed class LandingSound
        {
            [Tooltip("The reward id, exactly as the server pays it: Chips / Kash / Gems / Piggy, a chest or item key.")]
            public string rewardId;
            public Sonity.SoundEvent sound;
        }

        [Header("Amount label (optional)")]
        [Tooltip("A prefab with a TMP_Text — floats up from the source showing what was won, e.g. \"+3,000\".")]
        [SerializeField] private GameObject amountLabelPrefab;
        [SerializeField] private float labelRise = 120f;
        [SerializeField] private float labelDuration = 0.9f;
        [SerializeField] private string labelFormat = "+{0}";
        [Tooltip("Compact big numbers on the label: 10000 → 10K, 2000000 → 2M.")]
        [SerializeField] private bool compactLabel = true;

        [Header("How many pieces")]
        [SerializeField] private int minPieces = 8;
        [Tooltip("The cap that stops a 2M payout spawning 2M chips.")]
        [SerializeField] private int maxPieces = 20;
        [Tooltip("Amount that earns the maximum number of pieces; below it the count scales down.")]
        [SerializeField] private decimal amountForMaxPieces = 10000m;

        [Header("Blast — the scatter")]
        [Tooltip("Peak launch speed in CANVAS units per second. On a 2560-wide canvas this needs to be in the " +
                 "thousands: the distance a piece actually covers is roughly blastSpeed / drag, so 900/3.2 is a " +
                 "200-unit puff, not a blast.")]
        [SerializeField] private float blastSpeed = 2600f;
        [Tooltip("HOW WIDE the fan spreads, in degrees around straight up. 360 = a full sphere, 180 = the upper " +
                 "half, 90 = a narrow geyser. This is the spread knob.")]
        [Range(10f, 360f)][SerializeField] private float spreadAngle = 250f;
        [Tooltip("How unequal the launch speeds are. 0 = every piece flies exactly as far (a ring — never want this); " +
                 "0.7 = the slowest piece leaves at 30% of the fastest, so the cloud is ragged and the pieces reach " +
                 "the counter one at a time instead of as a clump.")]
        [Range(0f, 0.9f)][SerializeField] private float speedVariance = 0.7f;
        [Tooltip("Downward pull during the blast — what gives the pieces weight. Keep it WELL below blastSpeed or " +
                 "the fan is pulled flat and the burst reads as spilling downward.")]
        [SerializeField] private float gravity = 900f;
        [Tooltip("How fast the launch speed bleeds off. Together with blastSpeed this sets the radius: lower drag " +
                 "throws further. Higher = the cloud settles sooner.")]
        [SerializeField] private float drag = 2.2f;
        [Tooltip("How long the pieces fly free before the magnet takes over.")]
        [SerializeField] private float blastTime = 0.42f;
        [SerializeField] private float spinSpeed = 520f;
        [Tooltip("Random extra delay before a piece appears, up to this many seconds — the burst erupts instead of " +
                 "materialising all at once.")]
        [SerializeField] private float spawnJitter = 0.04f;
        [Range(0f, 0.6f)][SerializeField] private float scaleJitter = 0.22f;
        [Tooltip("Turn a piece back at the screen edge instead of letting it sail out of view. It changes NOTHING " +
                 "about the launch or the motion inside the screen — a piece only notices this once it would have " +
                 "left. Needed because a card near an edge throws half its burst past it.")]
        [SerializeField] private bool keepOnScreen = true;
        [Tooltip("How far inside the screen edge a piece turns around.")]
        [SerializeField] private float screenMargin = 70f;
        [Range(0f, 1f)]
        [Tooltip("How much speed survives the turn. Low keeps the piece near the edge instead of firing it back " +
                 "across the screen.")]
        [SerializeField] private float edgeBounce = 0.3f;

        [Header("Magnet — the collect")]
        [Tooltip("Acceleration toward the counter. THIS is the responsiveness knob — higher yanks harder.")]
        [SerializeField] private float pullForce = 16000f;
        [Tooltip("Damping, so a fast piece doesn't overshoot and orbit.")]
        [SerializeField] private float pullDamping = 9f;
        [Tooltip("Up to this many extra seconds before an individual piece is grabbed. The blast's own spread already " +
                 "makes them arrive apart; this exaggerates it so they're collected one by one rather than sucked in " +
                 "as a sheet. 0 = every piece is grabbed the instant its blast ends.")]
        [SerializeField] private float magnetStagger = 0.14f;
        [Tooltip("Size at the counter, as a fraction — pieces shrink by PROXIMITY, so it reads as distance.")]
        [SerializeField] private float endScale = 0.5f;


        [Header("Spawned targets — a receipt widget instead of the HUD counter")]
        [Tooltip("Fly into a widget this component SPAWNS, rather than into whatever counter the scene registered.\n\n" +
                 "Use this when the payout screen is a popup over the HUD: the real counter sits behind the dim, so " +
                 "chips would fly over the overlay and land behind it.")]
        [SerializeField] private bool useSpawnedTargets;

        [Tooltip("One row per currency. The widget is spawned AT its Spawn At transform and nowhere else — no anchors, " +
                 "no stacking, no measuring. Author the positions in the scene and they are exactly what you get.")]
        [SerializeField] private SpawnedTarget[] spawnedTargets;

        [Tooltip("Slide-in time. The widget must have arrived before the first piece does, and the blast alone is " +
                 "~0.4s, so anything under that is safe.")]
        [SerializeField] private float openSeconds = 0.32f;
        [Tooltip("How long the widget stays after the last piece lands, so the new balance can be read.")]
        [SerializeField] private float holdSeconds = 0.9f;
        [Tooltip("Slide-out time. Shorter than the entrance — an exit that dawdles reads as the UI being slow.")]
        [SerializeField] private float closeSeconds = 0.26f;
        [Range(0f, 0.5f)]
        [Tooltip("How much the widget STRETCHES along its travel: it comes in long and thin and squares up as it " +
                 "lands, which is what sells the speed. 0 = a rigid slide.")]
        [SerializeField] private float slideStretch = 0.2f;

        /// <summary>A currency, the widget that stands in for its counter, and exactly where it goes.</summary>
        [Serializable]
        public sealed class SpawnedTarget
        {
            [Tooltip("The reward id, exactly as the server pays it: Chips / Coins / Gems / Kash.")]
            public string rewardId;

            [Tooltip("The icon + balance widget for this currency. Needs a RewardFlyTarget and a BalanceBinder.")]
            [FormerlySerializedAs("prefab")]
            public RectTransform balanceWidget;

            [Tooltip("The AREA the widget appears in. It is spawned as a child of this rect and its own RectTransform " +
                     "is left untouched — so this rect is the frame, and the PREFAB's anchors/pivot decide how it " +
                     "sits in that frame. Anchor + pivot on the left edge = every widget starts at the same x, " +
                     "whatever width it ends up.")]
            public RectTransform spawnAt;
        }

        [Header("Impact — what the counter does when a piece lands")]
        [Tooltip("Kick the counter on EVERY piece (RewardFlyTarget's own punch + its onPieceArrival event). Without " +
                 "this the pieces just vanish into the HUD and the collect has no payoff.")]
        [SerializeField] private bool punchTargetPerPiece = true;
        [Tooltip("Optional spark/flash spawned at the counter as each piece lands. Destroyed after Impact Life.")]
        [SerializeField] private GameObject impactPrefab;
        [SerializeField] private float impactLife = 0.6f;

        [Header("Sequencing")]
        [Tooltip("Seconds between one reward's burst and the next, so chips+Kash read as two gestures.")]
        [SerializeField] private float staggerBetweenRewards = 0.16f;

        [Header("Performance")]
        [Tooltip("Pieces created up front. Keep at or above maxPieces — spawning a full burst mid-frame is the hitch.")]
        [SerializeField] private int prewarm = 24;

        /// <summary>Raised as each reward's last piece lands — hook counters, sounds, punches here.</summary>
        public event Action<string> RewardLanded;

        /// <summary>Raised as EVERY piece lands, with the reward id and how far through that reward's burst it is
        /// (0..1). This is the beat a counter should tick on: the last piece arrives at exactly 1.</summary>
        public event Action<string, float> PieceLanded;

        private readonly Stack<RectTransform> _pool = new Stack<RectTransform>();

        // Bursts this component has armed a balance hold for. Tracked on the COMPONENT, not inside the play coroutine,
        // because the failure that matters most — the panel being closed mid-flight — kills the coroutine outright and
        // its own cleanup with it. A HUD left holding a number shows a stale balance, so the release has to survive
        // teardown; OnDisable is the one callback that still runs.
        private readonly HashSet<string> _armed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A spawned widget that is currently on screen, and how many payouts are relying on it.
        ///
        /// Kept per reward id because the widget outlives the payout that spawned it: taps arrive faster than the
        /// hold expires, and each new one must find the widget already standing rather than build a second.
        /// </summary>
        private sealed class LiveWidget
        {
            public string Key;
            public RectTransform Rect;
            public Vector2 RestPos;      // where it belongs — what a revive tweens back to
            public Vector3 RestScale;
            public int Uses;             // payouts still showing it; it closes when this reaches zero
            public bool Closing;
            public Tween Exit;           // the exit sequence, kept so a revive can cancel the destroy that ends it
        }

        private readonly Dictionary<string, LiveWidget> _live =
            new Dictionary<string, LiveWidget>(StringComparer.OrdinalIgnoreCase);

        private RectTransform Layer => flyLayer != null ? flyLayer : (RectTransform)transform;

        private void OnDisable()
        {
            ReleaseHolds();
            RecallPieces();   // the coroutines carrying them are already dead — see RecallPieces

            // Nothing survives the panel closing. A widget stranded mid-slide would otherwise be reused on the next
            // payout at whatever position its tween was killed at — off screen, most likely.
            foreach (var pair in _live)
            {
                var rect = pair.Value.Rect;
                if (rect == null) continue;
                rect.DOKill();
                Destroy(rect.gameObject);
            }
            _live.Clear();
        }

        private void ReleaseHolds()
        {
            if (_armed.Count == 0) return;
            foreach (var id in _armed) RewardFlyTarget.EndBurst(id);
            _armed.Clear();
        }

        private void Awake()
        {
            for (int i = 0; i < prewarm && piecePrefab != null; i++) Return(Create());
        }

        /// <summary>Fly one reward.</summary>
        public void Play(RewardFlyItem item, RectTransform sharedSource = null, Action onComplete = null)
            => Play(new[] { item }, sharedSource, onComplete);

        /// <summary>
        /// Fly several rewards — each to its own counter, staggered so they read one after another. A reward with no
        /// counter in this scene is skipped silently: a missing HUD is a reason to show nothing, not to throw.
        /// </summary>
        public void Play(IEnumerable<RewardFlyItem> items, RectTransform sharedSource = null, Action onComplete = null)
        {
            // Nothing flying is the hardest failure to diagnose from the outside, because every step is silent by
            // design (a missing counter SHOULD be skipped, not thrown). So the entry point says out loud when it is
            // structurally impossible for anything to happen.
            if (piecePrefab == null)
            {
                Debug.LogError($"{name}: RewardFly has no Piece Prefab — nothing can fly. Assign the chip/coin " +
                               "Image prefab it should spawn.", this);
                onComplete?.Invoke();
                return;
            }
            if (items == null) { onComplete?.Invoke(); return; }

            StartCoroutine(PlayRoutine(items, sharedSource, onComplete));
        }

        private IEnumerator PlayRoutine(IEnumerable<RewardFlyItem> items, RectTransform sharedSource, Action onComplete)
        {
            int flying = 0;
            bool first = true;
            _boundsValid = false;   // re-measure once per burst: the screen may have rotated or resized since the last

            // Spawn the receipt widgets UP FRONT, all of them, before any piece launches. They stack together and
            // roll open as one gesture — staggering them the way the bursts are staggered would read as the UI being
            // assembled in front of you. The bursts stay staggered; only the targets appear at once.
            var spawned = SpawnTargetsFor(items);

            foreach (var item in items)
            {
                var from = item.From != null ? item.From : sharedSource;
                var to = TargetFor(item, spawned);

                // Name the exact reason this reward isn't flying. Both of these are legitimate configurations, so
                // they're warnings rather than errors — but "nothing happened" is never a useful thing to be told.
                if (from == null)
                    Debug.LogWarning($"{name}: '{item.RewardId}' has no source to fly FROM — the caller passed no " +
                                     "shared source and the item carries none.", this);
                else if (to == null)
                    Debug.LogWarning($"{name}: '{item.RewardId}' has nowhere to fly TO. " +
                                     (useSpawnedTargets
                                        ? "Use Spawned Targets is ON but no Balance Widget is assigned for that id — " +
                                          "check the Reward Id spelling matches the wallet's ('Chips', 'Kash')."
                                        : "No RewardFlyTarget with that Reward Id is enabled in the scene."), this);

                if (from == null || to == null)
                {
                    // Nothing will fly for this reward, so hand back any hold armed for it rather than leaving a HUD
                    // frozen on a stale balance until its timeout expires.
                    RewardFlyTarget.EndBurst(item.RewardId);
                    continue;
                }

                if (!first && staggerBetweenRewards > 0f) yield return new WaitForSecondsRealtime(staggerBetweenRewards);
                first = false;

                // Arm the hold for callers that didn't pre-arm at payout time. Harmless when they did (it's a set) —
                // but a caller that CAN arm earlier should, because the wallet push lands within milliseconds of the
                // claim response and a hold armed here, a frame or more later, has already lost the race.
                if (RewardFlyTarget.ArmBurst(item.RewardId)) _armed.Add(item.RewardId);

                ShowAmountLabel(item, from);

                int pieces = item.Pieces > 0 ? item.Pieces : PiecesFor(item.Amount);
                var icon = item.Icon != null ? item.Icon : IconFor(item.RewardId);
                string id = item.RewardId;
                int landed = 0;

                flying += pieces;
                RewardFlyTarget.NotifyBurstStarted(id, pieces, item.Amount);

                // All pieces launch TOGETHER — the scatter comes from their velocities, not from staggering them.
                for (int i = 0; i < pieces; i++)
                {
                    var piece = Rent(icon);
                    StartCoroutine(FlyOne(piece, from, to, () =>
                    {
                        flying--;
                        landed++;
                        float progress = (float)landed / pieces;

                        // The hit. Every piece, not just the last — a stream of chips being caught one by one is the
                        // whole reason the burst was staggered in the first place. This also walks any held balance
                        // up by one slice, so the number ticks WITH the chips instead of having finished before them.
                        PlayLanding(id);
                        RewardFlyTarget.NotifyPiece(id, progress, punch: punchTargetPerPiece);
                        SpawnImpact(to);

                        PieceLanded?.Invoke(id, progress);
                        if (landed < pieces) return;
                        RewardFlyTarget.NotifyArrived(id);
                        RewardLanded?.Invoke(id);
                    }));
                }
            }

            while (flying > 0) yield return null;
            ReleaseHolds();

            // Let the widget hold the new number for a beat before it leaves — the count roll is the payoff, and
            // closing on the frame the last piece lands throws it away.
            if (spawned != null && spawned.Count > 0)
            {
                if (holdSeconds > 0f) yield return new WaitForSecondsRealtime(holdSeconds);
                CloseSpawned(spawned);
            }

            onComplete?.Invoke();
        }

        /// <summary>
        /// (1) blast: launched with its own random velocity, flung by gravity and bled off by drag, popping in and
        /// spinning; (2) magnet: velocity reset, then accelerate at the counter — the pieces are at different
        /// distances by now, so they land one by one without any stagger; shrinking by PROXIMITY, not by time.
        /// </summary>
        private IEnumerator FlyOne(RectTransform piece, RectTransform from, RectTransform to, Action onLanded)
        {
            var layer = Layer;
            float sizeMul = 1f + UnityEngine.Random.Range(-scaleJitter, scaleJitter);
            Vector3 baseScale = Vector3.one * sizeMul;

            // Capture the SOURCE as a value right now, before any yield. Whatever paid out is usually destroyed
            // moments later — a claimed pass card is respawned as its collected variant — so holding the reference
            // and reading it a frame later is a guaranteed MissingReferenceException.
            Vector3 startWorld = from.position;

            // Same for the DESTINATION, and for a sharper reason: the spawned receipt widget is destroyed when its
            // payout is done, and a piece from an overlapping payout can still be in the air. Remembering where the
            // counter was means such a piece finishes its flight instead of throwing on a dead transform — and
            // therefore still reports its landing, which is what releases the held balance.
            Vector3 dstWorld = to != null ? to.position : startWorld;

            piece.localScale = Vector3.zero;
            piece.position = startWorld;

            if (spawnJitter > 0f) yield return new WaitForSecondsRealtime(UnityEngine.Random.Range(0f, spawnJitter));
            if (piece == null) yield break;

            Vector3 pos = layer.InverseTransformPoint(startWorld);
            float spinDir = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            float angle = UnityEngine.Random.Range(0f, 360f);

            // Direction: a fan of `spreadAngle` centred on STRAIGHT UP. Centring it upward is what stops gravity
            // dumping half the burst out of the bottom of the card; widening it past 180° is what makes it read as an
            // explosion rather than a fountain.
            float half = spreadAngle * 0.5f;
            float a = (90f + UnityEngine.Random.Range(-half, half)) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);

            // Speed: deliberately UNEQUAL. Equal speeds put every piece the same distance out, which is the ring, and
            // then the magnet collects them all at the same moment. The variance is the whole scatter.
            Vector3 vel = dir * (blastSpeed * UnityEngine.Random.Range(1f - speedVariance, 1f));

            float t = 0f;
            // Fast pop-in, independent of blastTime: a piece must be at full size while it is still travelling, or the
            // eye reads the first third of the flight as "nothing happened yet".
            const float popIn = 0.11f;
            float life = blastTime * UnityEngine.Random.Range(0.85f, 1.15f);

            while (t < life)
            {
                float dt = Step();
                t += dt;
                vel.y -= gravity * dt;
                vel *= Mathf.Exp(-drag * dt);
                pos += vel * dt;
                KeepInView(ref pos, ref vel);

                piece.position = layer.TransformPoint(pos);
                piece.localScale = baseScale * EaseOutElastic(Mathf.Clamp01(t / popIn));
                angle += spinDir * spinSpeed * dt;
                piece.localRotation = Quaternion.Euler(0f, 0f, angle);
                yield return null;
            }
            piece.localScale = baseScale;

            // A beat of hang time before the grab, different for every piece, so the counter eats them one by one.
            if (magnetStagger > 0f)
            {
                float hold = UnityEngine.Random.Range(0f, magnetStagger);
                float held = 0f;
                while (held < hold)
                {
                    float dt = Step();
                    held += dt;
                    vel.y -= gravity * dt;
                    vel *= Mathf.Exp(-drag * dt);
                    pos += vel * dt;
                    KeepInView(ref pos, ref vel);
                    piece.position = layer.TransformPoint(pos);
                    angle += spinDir * spinSpeed * dt;
                    piece.localRotation = Quaternion.Euler(0f, 0f, angle);
                    yield return null;
                }
            }

            // (2) magnet. Reset the blast velocity first, or the leftover momentum swings the piece into an orbit.
            // The destination is re-read each frame so a moving HUD still catches the pieces, but it is also remembered
            // as a value: a scene change can destroy the counter mid-flight, and the piece must still land somewhere.
            if (to != null) dstWorld = to.position;
            Vector3 dst = layer.InverseTransformPoint(dstWorld);
            float startDist = Mathf.Max(1f, (dst - pos).magnitude);
            vel = Vector3.zero;

            int guard = 0;
            while (guard++ < 1000)
            {
                float dt = Step();
                if (to != null) dstWorld = to.position;
                dst = layer.InverseTransformPoint(dstWorld);
                Vector3 delta = dst - pos;
                float dist = delta.magnitude;
                if (dist <= 10f) break;

                vel += (delta / dist) * (pullForce * dt);
                vel *= Mathf.Exp(-pullDamping * dt);

                // Never step PAST the counter. The pull accelerates to well over a thousand units a second, so a
                // single frame's move can easily be bigger than the distance left — the piece shoots through, the
                // pull turns it around, and it swings back in from off screen. Landing exactly on the target the
                // frame it would have overshot removes the whole orbit.
                Vector3 step = vel * dt;
                if (step.sqrMagnitude >= dist * dist) { pos = dst; piece.position = layer.TransformPoint(pos); break; }
                pos += step;

                piece.position = layer.TransformPoint(pos);
                float proximity = 1f - Mathf.Clamp01(dist / startDist);
                piece.localScale = baseScale * Mathf.Lerp(1f, endScale, proximity);
                angle += spinDir * spinSpeed * dt;
                piece.localRotation = Quaternion.Euler(0f, 0f, angle);
                yield return null;
            }

            Return(piece);
            onLanded?.Invoke();
        }

        /// <summary>
        /// The integration step, CLAMPED. One long frame — a panel rebuilding, a texture landing, the editor stalling —
        /// otherwise gets multiplied straight into position, teleporting every piece the same huge distance at once.
        /// That is what turns a scatter into "they hang, then snap into a ring": the whole burst advances in one step.
        /// Capping it at ~30fps of motion costs a little accuracy on a slow frame and keeps the shape honest.
        /// </summary>
        private static float Step() => Mathf.Min(Time.unscaledDeltaTime, 0.034f);

        /// <summary>
        /// Turn a piece back at the screen edge. Applied only during the BLAST — the magnet is free to take a piece
        /// anywhere, including a counter tucked into a corner.
        /// </summary>
        private void KeepInView(ref Vector3 pos, ref Vector3 vel)
        {
            if (!keepOnScreen) return;
            var b = ViewBounds();
            if (b.width <= 1f || b.height <= 1f) return;

            float bounce = -edgeBounce;
            if (pos.x < b.xMin) { pos.x = b.xMin; vel.x *= bounce; }
            else if (pos.x > b.xMax) { pos.x = b.xMax; vel.x *= bounce; }

            if (pos.y < b.yMin) { pos.y = b.yMin; vel.y *= bounce; }
            else if (pos.y > b.yMax) { pos.y = b.yMax; vel.y *= bounce; }
        }

        /// <summary>
        /// The visible screen, expressed in the fly layer's own local space.
        ///
        /// Measured off the ROOT CANVAS, not the fly layer: the layer is often a child panel that doesn't cover the
        /// screen, and bounding to that would pen the burst into a box the player can't see. Cached per burst, since
        /// it can only change with the resolution.
        /// </summary>
        private Rect ViewBounds()
        {
            if (_boundsValid) return _bounds;
            _boundsValid = true;
            _bounds = new Rect();

            var canvas = GetComponentInParent<Canvas>();
            var root = canvas != null ? canvas.rootCanvas.transform as RectTransform : Layer;
            if (root == null) return _bounds;

            root.GetWorldCorners(Corners);
            var layer = Layer;
            Vector3 a = layer.InverseTransformPoint(Corners[0]);
            Vector3 c = layer.InverseTransformPoint(Corners[2]);

            float m = Mathf.Max(0f, screenMargin);
            _bounds = Rect.MinMaxRect(Mathf.Min(a.x, c.x) + m, Mathf.Min(a.y, c.y) + m,
                                      Mathf.Max(a.x, c.x) - m, Mathf.Max(a.y, c.y) - m);
            return _bounds;
        }

        private static readonly Vector3[] Corners = new Vector3[4];
        private Rect _bounds;
        private bool _boundsValid;

        // ---------------- spawned receipt widgets ----------------

        /// <summary>
        /// One widget per DISTINCT reward in this payout, each parented to its own authored transform.
        ///
        /// No anchors, no stacking, no measuring: the widget goes where <see cref="SpawnedTarget.spawnAt"/> is, full
        /// stop. Everything this used to compute — anchor corners, stack gaps, sizes read off self-sizing prefabs —
        /// was guessing at numbers you can simply place by hand in the scene and see.
        ///
        /// Distinct, not per item: two chip lines are still one chip counter.
        /// </summary>
        private List<RectTransform> SpawnTargetsFor(IEnumerable<RewardFlyItem> items)
        {
            if (!useSpawnedTargets || spawnedTargets == null || spawnedTargets.Length == 0) return null;

            var made = new List<RectTransform>();
            var used = new List<string>();

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.RewardId)) continue;

                bool already = false;
                foreach (var id in used)
                    if (string.Equals(id, item.RewardId, StringComparison.OrdinalIgnoreCase)) { already = true; break; }
                if (already) continue;

                var entry = EntryFor(item.RewardId);
                if (entry?.balanceWidget == null) continue;          // no widget authored: fall through to the HUD
                if (entry.spawnAt == null)
                {
                    Debug.LogWarning($"{name}: '{item.RewardId}' has a Balance Widget but no Spawn At transform — " +
                                     "it has nowhere to appear, so the HUD counter is used instead.", this);
                    continue;
                }

                used.Add(item.RewardId);

                // ALREADY ON SCREEN: reuse it, and do NOT replay the entrance.
                //
                // Tapping a second day while the widget is still up is the normal case, not an edge one. The widget is
                // already exactly where it belongs, so sliding it in again reads as a glitch — and worse, it would
                // yank the counter out from under the chips still arriving at it. It simply stays, keeps counting, and
                // lives until the LAST payout using it is finished, which is what the use count below is for.
                var key = item.RewardId.Trim();
                if (_live.TryGetValue(key, out var live))
                {
                    if (live.Rect == null) _live.Remove(key);      // destroyed with its panel — spawn a fresh one
                    else
                    {
                        if (live.Closing) Revive(live);            // caught on the way out: bring it back, don't restart it
                        live.Uses++;
                        made.Add(live.Rect);
                        continue;
                    }
                }

                // A CHILD of the authored rect, in that rect's own space — the `false` is the whole point.
                //
                // Instantiate(prefab, parent) defaults to worldPositionStays TRUE, which does not mean "leave it
                // alone": it REWRITES the new child's local scale and rotation to preserve the prefab asset's world
                // pose under the new parent. Under a Canvas, whose scale is driven by its CanvasScaler and is nowhere
                // near 1, that hands the widget a compensating scale of several times its authored size — which then
                // gets captured below as its "rest" scale and rolled open to. Parenting locally instead means the
                // widget is exactly the prefab, positioned by the rect it lives in.
                var widget = Instantiate(entry.balanceWidget, entry.spawnAt, false);
                widget.name = $"RewardTarget_{item.RewardId}";

                // Its RectTransform is NOT touched — not the anchors, not the pivot, not the offset.
                //
                // The prefab is the authority on how it sits inside its area: set its anchors and pivot to the left
                // edge and every widget lines up on that edge no matter how wide it measures, which is the one thing
                // centring can't do. Code that rewrites those values here would silently undo that authoring, and did.

                // Slide IN from off the left of the screen, rather than opening in place.
                //
                // Arriving from outside the frame reads as the widget being brought TO the player; growing in place
                // reads as it being dropped on top of them. It also means the entrance never fights the payout — the
                // widget is already travelling before the first chip is thrown, so the eye is on it when they land.
                float travel = OffscreenLeftTravel(widget);
                var restPos = widget.anchoredPosition;
                var restScale = widget.localScale;
                if (restScale.x <= 0.0001f || restScale.y <= 0.0001f) restScale = Vector3.one;

                widget.anchoredPosition = new Vector2(restPos.x - travel, restPos.y);
                widget.localScale = Stretched(restScale);

                float open = Mathf.Max(0.01f, openSeconds);
                var enter = DOTween.Sequence().SetUpdate(true).SetTarget(widget);
                // OutBack on the POSITION: it runs a little past its mark and settles back, which is the difference
                // between a slide and a landing.
                enter.Append(widget.DOAnchorPosX(restPos.x, open).SetEase(Ease.OutBack, 1.4f));
                // The stretch resolves FASTER than the travel, so it has squared up by the time it stops moving.
                enter.Insert(0f, widget.DOScale(restScale, open * 0.7f).SetEase(Ease.OutBack, 2.2f));

                _live[key] = new LiveWidget
                {
                    Key = key,
                    Rect = widget,
                    RestPos = restPos,
                    RestScale = restScale,
                    Uses = 1,
                };
                made.Add(widget);
            }
            return made.Count > 0 ? made : null;
        }

        /// <summary>
        /// Send the widgets back out the way they came — right to left, off the screen — and destroy them. They are
        /// transient by design: nothing outlives the payout.
        ///
        /// A widget several payouts are sharing is only closed by the LAST of them. Without that count, the first
        /// collect's hold expiring would tear the widget away from a second collect that is still landing chips in it.
        ///
        /// InBack on the exit gives it a beat of anticipation to the RIGHT before it leaves, so the widget looks like
        /// it winds up and goes rather than being switched off.
        /// </summary>
        private void CloseSpawned(List<RectTransform> spawned)
        {
            foreach (var widget in spawned)
            {
                if (widget == null) continue;

                var live = LiveFor(widget);
                if (live != null)
                {
                    live.Uses--;
                    if (live.Uses > 0) continue;   // someone else is still showing it
                    live.Closing = true;
                }

                var go = widget.gameObject;
                float close = Mathf.Max(0.01f, closeSeconds);
                float travel = OffscreenLeftTravel(widget);

                widget.DOKill();   // the entrance may still be settling if the payout was very short

                // SetTarget so DOKill(widget) takes the SEQUENCE with it, not only the tweens inside it. Without it a
                // killed sequence still runs to its end and fires the destroy below — which is exactly how a widget
                // that had just been revived got torn down anyway, with pieces still flying at it.
                var exit = DOTween.Sequence().SetUpdate(true).SetTarget(widget);
                exit.Append(widget.DOAnchorPosX(widget.anchoredPosition.x - travel, close).SetEase(Ease.InBack, 1.7f));
                exit.Join(widget.DOScale(Stretched(widget.localScale), close * 0.8f).SetEase(Ease.InQuad));
                exit.OnComplete(() =>
                {
                    // Still leaving? A revive between the kill and this callback clears the flag, and then the widget
                    // must survive whatever the tween thought it was doing.
                    if (live != null && !live.Closing) return;

                    if (live != null && _live.TryGetValue(live.Key, out var still) && still == live) _live.Remove(live.Key);
                    if (go != null) Destroy(go);
                });

                if (live != null) live.Exit = exit;
            }
        }

        /// <summary>
        /// Caught on the way out — pull it back to its place instead of letting it leave and spawning a replacement.
        ///
        /// Shorter than the entrance on purpose: it is already most of the way there, and a full-length slide from
        /// halfway would be slower than the arrival it is standing in for.
        /// </summary>
        private void Revive(LiveWidget live)
        {
            live.Closing = false;

            // Kill the exit EXPLICITLY as well as by target: it owns the callback that destroys the widget, and a
            // revived widget that gets destroyed a quarter second later is worse than one that never came back.
            live.Exit?.Kill();
            live.Exit = null;
            live.Rect.DOKill();

            float back = Mathf.Max(0.01f, openSeconds) * 0.6f;
            var seq = DOTween.Sequence().SetUpdate(true).SetTarget(live.Rect);
            seq.Append(live.Rect.DOAnchorPos(live.RestPos, back).SetEase(Ease.OutBack, 1.2f));
            seq.Join(live.Rect.DOScale(live.RestScale, back).SetEase(Ease.OutQuad));
        }

        private LiveWidget LiveFor(RectTransform widget)
        {
            foreach (var pair in _live)
                if (pair.Value.Rect == widget) return pair.Value;
            return null;
        }

        /// <summary>Long and thin along the direction of travel — the classic squash that reads as speed.</summary>
        private Vector3 Stretched(Vector3 scale)
            => new Vector3(scale.x * (1f + slideStretch), scale.y * (1f - slideStretch * 0.6f), scale.z);

        /// <summary>
        /// How far LEFT the widget must travel, in its OWN parent's units, to be completely off the left of the screen.
        ///
        /// Measured rather than guessed because these widgets size themselves: a fixed distance either leaves a wide
        /// pill's edge poking into frame or throws a narrow one so far out that the slide becomes a teleport. The
        /// layout is forced first — a freshly spawned widget's rect is still the prefab's (zero, with a fitter on it)
        /// until the next layout pass, so measuring before that gives the distance to a point rather than to an edge.
        /// </summary>
        private static float OffscreenLeftTravel(RectTransform widget)
        {
            const float fallback = 900f;

            var parent = widget.parent as RectTransform;
            var canvas = widget.GetComponentInParent<Canvas>();
            if (parent == null || canvas == null) return fallback;

            var canvasRect = canvas.rootCanvas != null
                ? canvas.rootCanvas.transform as RectTransform
                : canvas.transform as RectTransform;
            if (canvasRect == null) return fallback;

            LayoutRebuilder.ForceRebuildLayoutImmediate(widget);

            var corners = new Vector3[4];
            widget.GetWorldCorners(corners);
            float leftEdge = canvasRect.InverseTransformPoint(corners[0]).x;          // bottom-left, in canvas space
            float canvasLeft = -canvasRect.rect.width * canvasRect.pivot.x;

            // Clear the edge by a margin, so the widget is gone rather than only mostly gone.
            float travel = (leftEdge - canvasLeft) + widget.rect.width * 0.35f + 40f;

            // Canvas units → the widget's parent units. Normally 1:1; not if anything between them is scaled.
            float ratio = parent.lossyScale.x / Mathf.Max(0.0001f, canvasRect.lossyScale.x);
            travel /= Mathf.Max(0.0001f, ratio);

            return travel > 1f ? travel : fallback;
        }

        /// <summary>Where the flight should land: the spawned widget if there is one, else the item's own target, else
        /// whatever counter the scene registered.</summary>
        // ---------------- landing sound ----------------

        /// <summary>
        /// One piece's impact sound, played by the component that owns the pieces.
        ///
        /// Deliberately not routed through the target or through a panel's audio script: both of those depend on
        /// something being registered or subscribed at the right moment, and when either isn't, the failure is
        /// silence — indistinguishable from a missing clip, a wrong id, or a broken mix. Here there is one condition:
        /// a piece landed, so it plays.
        /// </summary>
        private void PlayLanding(string rewardId)
        {
            var sound = LandingSoundFor(rewardId);
            if (sound == null) return;

            // A DIFFERENT owner per hit — see the Landing Sound Voices tooltip. The pieces themselves are pooled and
            // already returned by now, so there is no per-piece transform to borrow; these stand in for them.
            if (_soundVoices == null || _soundVoices.Length != landingSoundVoices)
            {
                var old = _soundVoices;
                _soundVoices = new Transform[Mathf.Max(1, landingSoundVoices)];
                for (int i = 0; i < _soundVoices.Length; i++)
                {
                    if (old != null && i < old.Length && old[i] != null) { _soundVoices[i] = old[i]; continue; }
                    var go = new GameObject($"LandingVoice_{i}");
                    go.transform.SetParent(transform, false);
                    _soundVoices[i] = go.transform;
                }
            }

            _soundVoice = (_soundVoice + 1) % _soundVoices.Length;
            var voice = _soundVoices[_soundVoice] != null ? _soundVoices[_soundVoice] : transform;

            // Parked on the listener: this lives on a Canvas whose world coordinates run to hundreds of units, and a
            // 3D SoundContainer played out there attenuates to nothing while logging a cheerful "Play".
            //
            // The listener must be the ENABLED one, re-validated every play. FindAnyObjectByType also returns
            // DISABLED components on active objects — and Home carries a second, disabled listener on the avatar
            // StageCamera — so a blind first-hit, cached for the session, sometimes parked every chink at the stage
            // instead of the ear: attenuated to silence while Sonity logged "Play". Which listener won the find was
            // luck, which is why the bug came and went between runs.
            if (_listener == null || !_listener.isActiveAndEnabled)
            {
                _listener = null;
                foreach (var l in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                    if (l.isActiveAndEnabled) { _listener = l; break; }
            }
            if (_listener != null) voice.position = _listener.transform.position;

            sound.Play(voice);
        }

        private Sonity.SoundEvent LandingSoundFor(string rewardId)
        {
            if (landingSounds != null && !string.IsNullOrWhiteSpace(rewardId))
            {
                foreach (var entry in landingSounds)
                    if (entry != null && entry.sound != null &&
                        string.Equals(entry.rewardId, rewardId, StringComparison.OrdinalIgnoreCase))
                        return entry.sound;
            }
            return defaultLandingSound;
        }

        private Transform[] _soundVoices;
        private int _soundVoice;
        private AudioListener _listener;

        private RectTransform TargetFor(RewardFlyItem item, List<RectTransform> spawned)
        {
            if (spawned != null)
            {
                var wanted = $"RewardTarget_{item.RewardId}";
                foreach (var widget in spawned)
                {
                    if (widget == null || !string.Equals(widget.name, wanted, StringComparison.OrdinalIgnoreCase)) continue;

                    // Land on the widget's OWN landing point if it declares one — the icon, rather than the middle of
                    // a wide pill.
                    var target = widget.GetComponentInChildren<RewardFlyTarget>(true);
                    return target != null ? target.Landing : widget;
                }
            }

            if (item.To != null) return item.To;
            return RewardFlyTarget.Find(item.RewardId);
        }

        private SpawnedTarget EntryFor(string rewardId)
        {
            if (spawnedTargets == null || string.IsNullOrWhiteSpace(rewardId)) return null;
            foreach (var entry in spawnedTargets)
                if (entry != null && string.Equals(entry.rewardId, rewardId, StringComparison.OrdinalIgnoreCase))
                    return entry;
            return null;
        }

        private void SpawnImpact(RectTransform at)
        {
            if (impactPrefab == null || at == null) return;
            var fx = Instantiate(impactPrefab, Layer);
            fx.transform.position = at.position;
            fx.transform.SetAsLastSibling();
            Destroy(fx, Mathf.Max(0.05f, impactLife));
        }

        private void ShowAmountLabel(RewardFlyItem item, RectTransform from)
        {
            if (amountLabelPrefab == null || item.Amount <= 0m) return;

            var go = Instantiate(amountLabelPrefab, Layer);
            var rect = (RectTransform)go.transform;
            rect.position = from.position;

            var text = go.GetComponentInChildren<TMP_Text>();
            if (text != null) text.text = string.Format(labelFormat, Format(item.Amount));

            StartCoroutine(FloatLabel(rect, text));
        }

        private IEnumerator FloatLabel(RectTransform rect, TMP_Text text)
        {
            Vector3 start = rect.position;
            float t = 0f;
            while (t < labelDuration)
            {
                t += Time.unscaledDeltaTime;
                float u = Mathf.Clamp01(t / labelDuration);
                rect.position = start + Vector3.up * (labelRise * u);
                if (text != null)
                {
                    var c = text.color;
                    c.a = 1f - u * u;      // hold, then fade away quickly
                    text.color = c;
                }
                yield return null;
            }
            Destroy(rect.gameObject);
        }

        /// <summary>More pieces for a bigger reward, but bounded — the burst is a feeling, not a count.</summary>
        private int PiecesFor(decimal amount)
        {
            if (amount <= 0m) return minPieces;
            float ratio = amountForMaxPieces > 0m ? Mathf.Clamp01((float)(amount / amountForMaxPieces)) : 1f;
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(minPieces, maxPieces, Mathf.Sqrt(ratio))), minPieces, maxPieces);
        }

        private Sprite IconFor(string rewardId)
        {
            if (icons == null || string.IsNullOrWhiteSpace(rewardId)) return null;
            foreach (var entry in icons)
                if (entry != null && string.Equals(entry.rewardId, rewardId, StringComparison.OrdinalIgnoreCase))
                    return entry.icon;
            return null;
        }

        private RectTransform Create()
        {
            var go = Instantiate(piecePrefab, Layer);

            // A layout group on the fly layer would rewrite every piece's position each rebuild, holding them all in
            // one clump until the rebuilds settle — the "they hang together, then burst" symptom. Opting out of layout
            // makes a piece's position ours alone.
            var element = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            element.ignoreLayout = true;

            go.SetActive(false);
            return go.transform as RectTransform;
        }

        private void Start()
        {
            // Say it out loud too: ignoreLayout saves the pieces, but a layout group here still costs a rebuild per
            // frame while dozens of them move.
            if (Layer.GetComponent<LayoutGroup>() != null)
                Debug.LogWarning($"{name}: the RewardFly layer '{Layer.name}' has a LayoutGroup. Use a plain " +
                                 "full-screen RectTransform for the fly layer — layout has nothing to do here.", this);
        }

        private RectTransform Rent(Sprite icon)
        {
            RectTransform piece = null;
            while (_pool.Count > 0 && piece == null) piece = _pool.Pop();
            if (piece == null) piece = Create();

            piece.SetParent(Layer, false);
            piece.SetAsLastSibling();
            piece.localScale = Vector3.zero;
            piece.localRotation = Quaternion.identity;
            piece.gameObject.SetActive(true);
            _inFlight.Add(piece);

            if (icon != null)
            {
                var image = piece.GetComponent<Image>() ?? piece.GetComponentInChildren<Image>();
                if (image != null) image.sprite = icon;
            }
            return piece;
        }

        private void Return(RectTransform piece)
        {
            if (piece == null) return;
            _inFlight.Remove(piece);
            piece.gameObject.SetActive(false);
            _pool.Push(piece);
        }

        /// <summary>
        /// Every piece currently OUT of the pool — mid-flight, owned by a coroutine.
        ///
        /// Needed because the coroutines are not the ones who get to decide when they stop. Closing the panel disables
        /// this component, Unity kills its coroutines where they stand, and every piece they were carrying is left
        /// sitting on the canvas forever: a drift of chips over the UI that nothing will ever clean up, growing by one
        /// burst every time it happens. The pieces belong to the component, so the component takes them back.
        /// </summary>
        private readonly List<RectTransform> _inFlight = new List<RectTransform>();

        private void RecallPieces()
        {
            if (_inFlight.Count == 0) return;

            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                var piece = _inFlight[i];
                if (piece == null) continue;
                piece.gameObject.SetActive(false);
                _pool.Push(piece);
            }
            _inFlight.Clear();
        }

        private string Format(decimal amount)
        {
            if (!compactLabel) return amount.ToString("#,0");
            if (amount >= 1_000_000m) return Trim(amount / 1_000_000m) + "M";
            if (amount >= 10_000m) return Trim(amount / 1_000m) + "K";
            return amount.ToString("#,0");
        }

        private static string Trim(decimal value)
        {
            var rounded = Math.Round(value, 1);
            return rounded == Math.Truncate(rounded) ? ((long)rounded).ToString() : rounded.ToString("0.#");
        }

        private static float EaseOutElastic(float u)
        {
            if (u <= 0f) return 0f;
            if (u >= 1f) return 1f;
            const float p = 0.3f;
            return Mathf.Pow(2f, -10f * u) * Mathf.Sin((u - p / 4f) * (2f * Mathf.PI) / p) + 1f;
        }
    }
}
