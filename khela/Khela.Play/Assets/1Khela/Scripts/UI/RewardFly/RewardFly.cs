using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
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

        private RectTransform Layer => flyLayer != null ? flyLayer : (RectTransform)transform;

        private void OnDisable() => ReleaseHolds();

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
            if (items == null || piecePrefab == null) { onComplete?.Invoke(); return; }
            StartCoroutine(PlayRoutine(items, sharedSource, onComplete));
        }

        private IEnumerator PlayRoutine(IEnumerable<RewardFlyItem> items, RectTransform sharedSource, Action onComplete)
        {
            int flying = 0;
            bool first = true;
            _boundsValid = false;   // re-measure once per burst: the screen may have rotated or resized since the last

            foreach (var item in items)
            {
                var from = item.From != null ? item.From : sharedSource;
                var to = item.To != null ? item.To : RewardFlyTarget.Find(item.RewardId);
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
                RewardFlyTarget.NotifyBurstStarted(id, pieces);

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
            Vector3 dst = layer.InverseTransformPoint(to.position);
            float startDist = Mathf.Max(1f, (dst - pos).magnitude);
            vel = Vector3.zero;

            int guard = 0;
            while (guard++ < 1000)
            {
                float dt = Step();
                if (to != null) dst = layer.InverseTransformPoint(to.position);
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
            piece.gameObject.SetActive(false);
            _pool.Push(piece);
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
