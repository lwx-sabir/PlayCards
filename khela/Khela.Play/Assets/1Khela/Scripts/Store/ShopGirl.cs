using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using Sonity;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PlayCard.Store
{
    /// <summary>
    /// The shop's host: she walks in from the right when the shop opens, says one line, waits a beat, and drops out of
    /// frame.
    ///
    /// She is deliberately NOT guaranteed. A greeter who appears every single time stops being a greeting and becomes
    /// a loading step — so past the opening few visits she shows only <see cref="showChancePercent"/> of the time. The
    /// first <see cref="guaranteedOpens"/> opens ignore the roll, because a player who has never seen the shop should
    /// meet her, and a 70% chance would leave nearly a third of new players wondering what everyone is talking about.
    ///
    /// ⚠ Put this on the shop panel root, NOT on the girl. She starts inactive, and a component on a disabled
    /// GameObject never runs — it would never hear the shop open, so she would never arrive.
    ///
    /// Her exit is timed off the VOICE rather than a fixed duration: Sonity can report the length of the clip it just
    /// played, so a longer line simply holds her on screen longer. Nothing here waits on the voice existing — with no
    /// SoundEvent assigned she still arrives, holds, and leaves.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShopGirl : MonoBehaviour
    {
        [Header("Refs (auto-found if empty)")]
        [SerializeField] private ShopScreen screen;
        [Tooltip("Girl_Image. Authored where she should COME TO REST — this reads her position, never sets it.")]
        [SerializeField] private RectTransform girl;
        [Tooltip("The Image her artwork lands on. Empty = found on the girl herself. Leave the authored sprite in " +
                 "place: it is what shows if the server or the network never answers.")]
        [SerializeField] private Image girlImage;

        [Header("Artwork (server-chosen)")]
        [Tooltip("Ask the server which hosts this player may see. OFF = whatever sprite is authored on the Image, " +
                 "every time.")]
        [SerializeField] private bool useRemoteArt = true;
        [Tooltip("How long ONE host sticks before a different one is drawn. The picture itself is disk-cached by " +
                 "RemoteImage independently of this — what expires here is the CHOICE.")]
        [SerializeField] private float rotateAfterHours = 24f;
        [Tooltip("Longest she will be held back waiting for her artwork. She is a greeting — past this she walks on " +
                 "with the authored sprite rather than not appearing because a CDN was slow.")]
        [SerializeField] private float artTimeoutSeconds = 3f;
        [SerializeField] private string chosenKeyPref = "shop.girl.url";
        [SerializeField] private string chosenAtPref = "shop.girl.at";

        [Header("Odds")]
        [Tooltip("Chance she appears, once the guaranteed opens are used up.")]
        [SerializeField, Range(0, 100)] private int showChancePercent = 70;
        [Tooltip("Opens that ALWAYS show her, before the roll starts applying. Counted across sessions — this is about " +
                 "a player's first visits, not this run of the app.")]
        [SerializeField] private int guaranteedOpens = 3;
        [SerializeField] private string openCountKey = "shop.girl.opens";

        [Header("1 — she arrives")]
        [Tooltip("When she starts, RELATIVE TO THE PANEL LANDING. 0 = the instant the shop's own open tween finishes. " +
                 "NEGATIVE overlaps the tail of it, so she is already moving as the panel settles — usually the " +
                 "livelier read, try -0.15. Positive leaves a clear beat between the two.")]
        [SerializeField] private float startDelay = 0f;
        [Tooltip("How long the walk-on takes.")]
        [SerializeField] private float entrySeconds = 0.55f;
        [Tooltip("How far off the right edge she starts. 0 = worked out from the canvas, which is what you want unless " +
                 "she has to clear something specific.")]
        [SerializeField] private float entryFromOffsetX = 0f;
        [Tooltip("OutBack overshoots her mark and settles — that slight rebound is most of the 'juice'.")]
        [SerializeField] private Ease entryEase = Ease.OutBack;
        [Tooltip("OutBack's 'back' amount — how far past her mark she carries before settling. DOTween's own default " +
                 "is 1.7; below ~1 the rebound stops reading, 2.5+ is a hard bounce. 0 removes it entirely.")]
        [SerializeField] private float entryOvershoot = 1.7f;
        [Tooltip("Degrees she leans INTO the walk, straightening as she lands. Reads as momentum. 0 = no lean.")]
        [SerializeField] private float entryTiltDegrees = 6f;

        [Header("2 — she speaks")]
        [Tooltip("Her line. The exit is timed from how long this actually runs.")]
        [SerializeField] private SoundEvent voice;
        [Tooltip("Beat between her landing and the line starting — she should arrive, then speak.")]
        [SerializeField] private float voiceDelayAfterArrive = 0.15f;
        [Tooltip("Used when Sonity can't report a clip length (no SoundEvent, or it didn't play).")]
        [SerializeField] private float fallbackVoiceSeconds = 2.5f;

        [Header("3 — she stays alive")]
        [Tooltip("The idle. Runs from the moment she lands until she starts to leave.")]
        [SerializeField] private bool idleEnabled = true;
        [Tooltip("One full breath. Slow — a person at rest breathes every 3–5 seconds, and anything faster reads as " +
                 "panting.")]
        [SerializeField] private float breathSeconds = 3.4f;
        [Tooltip("How much she rises at the top of a breath, as a fraction of height. The chest is the widest part of " +
                 "her silhouette, so this is where it reads. 0.012 is about 12px on this art — small on purpose; " +
                 "breathing you can measure is breathing you can see is fake.")]
        [SerializeField] private float breathRise = 0.012f;
        [Tooltip("Width at the top of a breath. Slightly less than the rise, so she swells rather than inflates.")]
        [SerializeField] private float breathWiden = 0.004f;
        [Tooltip("One full sway, hip to hip. Deliberately NOT a multiple of the breath — two loops on a shared beat " +
                 "lock into a visible cycle within seconds.")]
        [SerializeField] private float swaySeconds = 5.3f;
        [Tooltip("How far she leans. This is the shift of weight the hip and thigh read from. ⚠ Every degree lifts " +
                 "her lower corner about 8.7px, and only ~50px of her sits below the screen edge — past ~2° the cut " +
                 "at her legs starts to climb out from behind Glow_Bottom.")]
        [SerializeField] private float swayDegrees = 1f;

        [Tooltip("OPTIONAL, and only useful once a limb is its own Image. A single sprite cannot move one arm — " +
                 "everything here deforms the whole of her. Slice the right forearm out, pivot it at the elbow, and " +
                 "add it here to get a real hand.")]
        [SerializeField] private List<IdlePart> parts = new List<IdlePart>();

        [Header("4 — she leaves")]
        [Tooltip("How long she stays after the line FINISHES.")]
        [SerializeField] private float exitDelayAfterVoice = 2f;
        [SerializeField] private float exitSeconds = 0.45f;
        [Tooltip("How far she drops. 0 = worked out from the canvas, far enough to clear the bottom.")]
        [SerializeField] private float exitDropY = 0f;
        [Tooltip("InBack dips her up a little before she falls — the anticipation that makes a drop read as a drop.")]
        [SerializeField] private Ease exitEase = Ease.InBack;
        [SerializeField] private float exitTiltDegrees = -5f;

        /// <summary>One limb, once it is its own Image — see <see cref="parts"/>.</summary>
        [System.Serializable]
        public sealed class IdlePart
        {
            [Tooltip("The limb. It rotates about ITS OWN pivot, so put the pivot where the joint is — the elbow for a " +
                     "forearm, the shoulder for a whole arm.")]
            public RectTransform part;
            [Tooltip("How far it swings each way.")]
            public float degrees = 2f;
            [Tooltip("One full swing out and back.")]
            public float seconds = 4f;
            [Tooltip("Offsets this limb's cycle so it does not move in lockstep with her body or the other limbs.")]
            public float phase;

            [System.NonSerialized] public Vector3 rest;
            [System.NonSerialized] public bool restCaptured;
        }

        /// <summary>Where she was authored. Read once — a run interrupted mid-tween must not become her new home.</summary>
        private Vector2 home;
        private Sequence move;
        private Coroutine routine;
        /// <summary>One visit per opening, whichever of the two entry paths gets here first — see OnEnable.</summary>
        private bool visitedThisOpen;
        /// <summary>Driving the idle in Update — see <see cref="TickIdle"/> for why this is not a tween.</summary>
        private bool idling;
        private float idleSince;
        private bool leaving;
        /// <summary>True from the moment her artwork is asked for until it has arrived OR failed.</summary>
        private bool artPending;
        /// <summary>Reused by the press test so a tap costs no allocation.</summary>
        private readonly List<RaycastResult> hits = new List<RaycastResult>();

        private void Awake()
        {
            if (screen == null) screen = GetComponentInParent<ShopScreen>();
            if (screen == null) screen = GetComponentInChildren<ShopScreen>(true);

            if (girl == null)
            {
                Debug.LogWarning($"{name}: no Girl_Image assigned — the shop host will never appear.", this);
                return;
            }
            if (girl.gameObject == gameObject)
                Debug.LogWarning($"{name}: ShopGirl is ON the girl. She starts inactive, so this component will never " +
                                 "run. Move it to the shop panel root and point Girl at her.", this);

            if (girlImage == null) girlImage = girl.GetComponent<Image>();

            home = girl.anchoredPosition;
            girl.gameObject.SetActive(false);
        }

        private void OnEnable()
        {
            visitedThisOpen = false;
            if (screen != null) screen.Opened += OnShopOpened;
            if (screen != null) screen.Closing += Dismiss;

            // Anchored on the OPEN rather than on OpenFinished, because startDelay has to be able to go NEGATIVE and
            // nothing can be scheduled backwards from an event that has already fired. She waits out the panel's own
            // tween herself, which puts both leading and trailing entrances on one knob.
            //
            // Entered HERE as well as on the event: ShopScreen raises Opened from inside its OWN OnEnable and sits on
            // this same GameObject, and nothing orders two components' OnEnable — when the screen goes first the event
            // reaches nobody. This object is active exactly while the shop is, so its own enable IS the open.
            OnShopOpened();
        }

        private void OnDisable()
        {
            if (screen != null) screen.Opened -= OnShopOpened;
            if (screen != null) screen.Closing -= Dismiss;
            Dismiss();
        }

        private void OnShopOpened()
        {
            if (girl == null || visitedThisOpen) return;
            visitedThisOpen = true;
            Dismiss();
            routine = StartCoroutine(Visit());
        }

        /// <summary>
        /// Does she show this time? The open is counted either way, so the guarantee is spent by visits rather than by
        /// appearances — three opens without her would defeat the point of guaranteeing anything.
        /// </summary>
        private bool RollForAppearance()
        {
            int opens = PlayerPrefs.GetInt(openCountKey, 0) + 1;
            PlayerPrefs.SetInt(openCountKey, opens);
            if (opens <= guaranteedOpens) return true;
            return Random.Range(0, 100) < showChancePercent;
        }

        private IEnumerator Visit()
        {
            // Wait out the panel, then apply the lead or trail. The roll happens AFTER the wait so the throwaway pass
            // on the very first open — ShopButton instantiates the shop active and disables it in the same frame — is
            // stopped by OnDisable before it can spend one of the guaranteed opens.
            float wait = (screen != null ? screen.OpenDuration : 0f) + startDelay;
            if (wait > 0f) yield return new WaitForSecondsRealtime(wait);
            else yield return null;

            if (!RollForAppearance()) { routine = null; yield break; }

            // --- 0. she gets dressed ---------------------------------------------------------------------------
            // Asked for only once the roll has PASSED: fetching art for a host who is not going to appear spends a
            // couple of megabytes of someone's mobile data on nothing.
            //
            // And waited for BEFORE she is shown, so she never walks on wearing last week's face and swaps mid-stride.
            // Bounded, though — she is a greeting, and a slow CDN must not be able to stop her appearing at all.
            ChooseArt();
            float artDeadline = Time.unscaledTime + Mathf.Max(0f, artTimeoutSeconds);
            while (artPending && Time.unscaledTime < artDeadline) yield return null;

            // --- 1. she arrives -------------------------------------------------------------------------------
            girl.gameObject.SetActive(true);
            girl.anchoredPosition = home + new Vector2(OffscreenRight(), 0f);
            girl.localEulerAngles = new Vector3(0f, 0f, -entryTiltDegrees);

            move?.Kill();
            move = DOTween.Sequence().SetUpdate(true);
            move.Append(girl.DOAnchorPos(home, entrySeconds).SetEase(entryEase, entryOvershoot));
            if (entryTiltDegrees != 0f)
                move.Join(girl.DOLocalRotate(Vector3.zero, entrySeconds).SetEase(Ease.OutCubic));
            yield return move.WaitForCompletion();
            StartIdle();

            // --- 2. she speaks --------------------------------------------------------------------------------
            if (voiceDelayAfterArrive > 0f) yield return new WaitForSecondsRealtime(voiceDelayAfterArrive);

            float spoken = fallbackVoiceSeconds;
            if (voice != null)
            {
                voice.UIPlay();
                // Sonity reports the clip it just played; `true` scales by pitch, so a pitched-down line still holds
                // her for as long as it actually sounds. 0 means it could not answer — keep the authored fallback.
                float length = voice.UIGetLastPlayedClipLength(true);
                if (length > 0f) spoken = length;
            }

            yield return new WaitForSecondsRealtime(spoken + Mathf.Max(0f, exitDelayAfterVoice));

            // --- 3. she leaves --------------------------------------------------------------------------------
            yield return Leave();
            routine = null;
        }

        // ---------------------------------------------------------------- the idle
        //
        // Driven in Update rather than by looping tweens, and that is forced by the art rather than a preference.
        //
        // Her sprite is CUT at the shins. Her rect bottom sits ~50px below the screen edge with Glow_Bottom over the
        // seam, so the one thing this may never do is lift that bottom edge — the cut would climb out from behind the
        // glow and she would visibly end at nothing. But her pivot is at her CENTRE, so every scale and every rotation
        // moves the bottom by default: breathing 1.2% taller drops it 6px, and a degree of lean swings it sideways.
        //
        // So each frame the deformation is applied AND a position offset that pins her bottom-centre exactly where it
        // was authored. Solving that per frame is why this is arithmetic and not three tweens fighting over
        // anchoredPosition — which the entry and exit tweens also own.
        //
        // What survives the constraint, and why these are the right three:
        //   · a breath that only ever grows UPWARD from her feet — the chest is the widest part of her silhouette, so
        //     that is where a uniform rise actually reads
        //   · a slow lean about her feet — the top travels, the hips travel half as far, the shins barely move: a
        //     shift of weight rather than a wobble
        //   · nothing vertical on her position at all, ever
        //
        // What CANNOT be done here: one arm. A single sprite has no arm to move — everything above deforms all of her
        // at once. The right hand needs to be its own Image; see `parts`.

        private void Update()
        {
            if (idling) TickIdle();
            WatchForButtonPress();
        }

        private void TickIdle()
        {
            if (girl == null) return;
            float t = Time.unscaledTime - idleSince;

            // Breath rides 0→1→0 rather than -1→1: she may swell from rest but never shrink below it, because
            // shrinking is the one direction that lifts her feet off the bottom of the screen.
            float breath = breathSeconds > 0f
                ? 0.5f - 0.5f * Mathf.Cos(t / breathSeconds * Mathf.PI * 2f)
                : 0f;
            float sway = swaySeconds > 0f ? Mathf.Sin(t / swaySeconds * Mathf.PI * 2f) : 0f;

            float sy = 1f + breathRise * breath;
            float sx = 1f + breathWiden * breath;
            float degrees = swayDegrees * sway;
            float rad = degrees * Mathf.Deg2Rad;

            girl.localScale = new Vector3(sx, sy, 1f);
            girl.localEulerAngles = new Vector3(0f, 0f, degrees);

            // Pin the bottom-centre. Her bottom-centre sits at (0, -half) from the pivot; scale then rotation carry it
            // to (half·sy·sin, -half·sy·cos), so the offset that puts it back is the negation of that drift.
            float half = girl.rect.height * 0.5f;
            girl.anchoredPosition = home + new Vector2(
                -half * sy * Mathf.Sin(rad),
                half * (sy * Mathf.Cos(rad) - 1f));

            foreach (var p in parts)
            {
                if (p?.part == null || p.seconds <= 0f) continue;
                if (!p.restCaptured) { p.rest = p.part.localEulerAngles; p.restCaptured = true; }
                float swing = Mathf.Sin((t + p.phase) / p.seconds * Mathf.PI * 2f) * p.degrees;
                p.part.localEulerAngles = new Vector3(p.rest.x, p.rest.y, p.rest.z + swing);
            }
        }

        private void StartIdle()
        {
            if (!idleEnabled) return;
            // A random start means two visits in a row do not breathe in step with each other.
            idleSince = Time.unscaledTime - Random.Range(0f, Mathf.Max(0.01f, breathSeconds));
            idling = true;
        }

        /// <summary>Stop deforming her and hand the transform back, so the exit tween starts from a known pose.</summary>
        private void StopIdle()
        {
            if (!idling) return;
            idling = false;
            if (girl == null) return;
            girl.localScale = Vector3.one;
            girl.localEulerAngles = Vector3.zero;
            girl.anchoredPosition = home;
            foreach (var p in parts)
                if (p?.part != null && p.restCaptured) p.part.localEulerAngles = p.rest;
        }

        /// <summary>
        /// The exit, shared by the two ways it can happen: her line finishing, and the player touching a button.
        /// </summary>
        private IEnumerator Leave()
        {
            StopIdle();
            // Her line goes with her. allowFadeOut hands the length to the SoundEvent, so a voice that outlived her
            // exit would be a disembodied one.
            if (voice != null) voice.UIStop(allowFadeOut: true);

            move?.Kill();
            move = DOTween.Sequence().SetUpdate(true);
            move.Append(girl.DOAnchorPos(home - new Vector2(0f, OffscreenDown()), exitSeconds).SetEase(exitEase));
            if (exitTiltDegrees != 0f)
                move.Join(girl.DOLocalRotate(new Vector3(0f, 0f, exitTiltDegrees), exitSeconds).SetEase(Ease.InCubic));
            yield return move.WaitForCompletion();

            girl.gameObject.SetActive(false);
        }

        /// <summary>
        /// The player has touched something — she is done, mid-sentence if need be.
        ///
        /// She LEAVES rather than vanishing. Popping her out of existence on the frame of a tap reads as a glitch, and
        /// she draws OVER the shop panel, so what matters is that she is on her way out immediately rather than that
        /// she is gone instantly.
        /// </summary>
        public void LeaveNow()
        {
            if (girl == null || leaving || !girl.gameObject.activeSelf) return;
            leaving = true;
            if (routine != null) StopCoroutine(routine);
            routine = StartCoroutine(LeaveNowRoutine());
        }

        private IEnumerator LeaveNowRoutine()
        {
            yield return Leave();
            leaving = false;
            routine = null;
        }

        /// <summary>
        /// Watch for a press on any BUTTON — a card, a tab, back, anything with a Button on it or above it.
        ///
        /// Asked of the EventSystem rather than wired per button, because the lanes instantiate their cards at runtime:
        /// anything wired at open would miss every card built afterwards, which is exactly how the card sounds ended up
        /// silent. This costs one raycast on the frames a press actually happens, and only while she is on screen.
        /// </summary>
        private void WatchForButtonPress()
        {
            if (girl == null || leaving || !girl.gameObject.activeSelf) return;

            var pointer = Pointer.current;
            if (pointer == null || !pointer.press.wasPressedThisFrame) return;

            var events = EventSystem.current;
            if (events == null) return;

            hits.Clear();
            events.RaycastAll(new PointerEventData(events) { position = pointer.position.ReadValue() }, hits);
            foreach (var hit in hits)
            {
                if (hit.gameObject == null) continue;
                // GetComponentInParent: a tap lands on the label or the icon INSIDE a card, not on the card itself.
                if (hit.gameObject.GetComponentInParent<Button>() == null) continue;
                LeaveNow();
                return;
            }
        }

        // ---------------------------------------------------------------- which host, and for how long
        //
        // The set comes from the SERVER, which reads the player's level and hands back only what they may see — the
        // client never says what level it is. Rotation is the client's job: the server stays stateless and the choice
        // is a cosmetic that does not need a row in a table.
        //
        // Two caches sit on top of each other and they are NOT the same thing. The CHOICE is remembered here for
        // rotateAfterHours, so the same face greets you all day rather than shuffling every time you open the shop.
        // The PICTURE is remembered by RemoteImage on disk, independently and for longer — so a rotation costs a
        // download only the first time that particular host comes up.

        /// <summary>Ask for the set and settle on one, then hand it to the Image. Silent on any failure — the
        /// authored sprite is a perfectly good host.</summary>
        private void ChooseArt()
        {
            artPending = false;
            if (!useRemoteArt || girlImage == null) return;
            var client = PlayCard.Game.Net.BlackjackRestClient.Instance;
            if (client == null) return;

            artPending = true;
            _ = ChooseArtAsync(client);
        }

        private async System.Threading.Tasks.Task ChooseArtAsync(PlayCard.Game.Net.BlackjackRestClient client)
        {
            PlayCard.Game.Net.ApiResult<PlayCard.Game.Net.ShopGirlsDto> result = default;
            try { result = await client.GetShopGirlsAsync(); }
            catch { artPending = false; return; }
            if (this == null) return;

            var images = result.Ok && result.Value != null ? result.Value.Images : null;
            if (images == null || images.Count == 0) { artPending = false; return; }

            var url = Settled(images);
            if (string.IsNullOrWhiteSpace(url)) { artPending = false; return; }

            // RemoteImage ALWAYS calls back — with null when there is nothing usable — so clearing the flag in the
            // callback covers the failure path as well as the success one, and she is never held back forever.
            PlayCard.Core.RemoteImage.Load(url, sprite =>
            {
                artPending = false;
                if (this == null || girlImage == null || sprite == null) return;   // null = keep the authored sprite
                girlImage.sprite = sprite;
                girlImage.enabled = true;
            });
        }

        /// <summary>
        /// The host for today: the one already chosen if it is still in date and still on offer, otherwise a fresh one.
        ///
        /// A re-pick deliberately EXCLUDES the outgoing host — "a new one" that can hand back the same face is not a
        /// rotation. The exclusion is dropped when it would leave nothing, so a single uploaded host still works.
        /// </summary>
        private string Settled(List<string> images)
        {
            string previous = PlayerPrefs.GetString(chosenKeyPref, null);
            double chosenAt = 0d;
            double.TryParse(PlayerPrefs.GetString(chosenAtPref, "0"), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out chosenAt);

            // Still in date AND still listed: a host withdrawn from storage must not keep being asked for.
            double hours = (Now() - chosenAt) / 3600d;
            if (!string.IsNullOrWhiteSpace(previous) && hours < rotateAfterHours && images.Contains(previous))
                return previous;

            var pool = images;
            if (!string.IsNullOrWhiteSpace(previous) && images.Count > 1)
            {
                pool = new List<string>(images);
                pool.Remove(previous);
            }

            var picked = pool[Random.Range(0, pool.Count)];
            PlayerPrefs.SetString(chosenKeyPref, picked);
            PlayerPrefs.SetString(chosenAtPref, Now().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return picked;
        }

        /// <summary>Seconds since the epoch. Wall-clock on purpose: the rotation is a DAY, which has to survive the
        /// app being closed — Time.unscaledTime restarts at zero every launch and would rotate her on every boot.</summary>
        private static double Now()
            => (System.DateTime.UtcNow - new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)).TotalSeconds;

        /// <summary>Send her away NOW, wherever she is — the shop closed, or a second open arrived on top of the first.</summary>
        public void Dismiss()
        {
            if (routine != null) { StopCoroutine(routine); routine = null; }
            leaving = false;
            StopIdle();
            move?.Kill();
            move = null;

            // Cut her off, but not abruptly — a line chopped mid-word as the panel leaves sounds like a fault. This
            // FADES: allowFadeOut hands the length to the SoundEvent's own Fade Out Length, so with that left at 0 it
            // still stops in one frame. Set it on the SoundEvent, not here, or every caller would need its own number.
            if (voice != null) voice.UIStop(allowFadeOut: true);
            if (girl == null) return;
            // Put her back before hiding: she is positioned by tween, and leaving her off-screen would mean the next
            // visit starts its entry from wherever this one was abandoned.
            girl.anchoredPosition = home;
            girl.localEulerAngles = Vector3.zero;
            girl.gameObject.SetActive(false);
        }

        /// <summary>Far enough right to be off-screen, worked out from the canvas when not authored.</summary>
        private float OffscreenRight()
        {
            if (entryFromOffsetX > 0f) return entryFromOffsetX;
            var canvas = girl.GetComponentInParent<Canvas>();
            float width = canvas != null ? ((RectTransform)canvas.transform).rect.width : Screen.width;
            return width * 0.5f + girl.rect.width;
        }

        private float OffscreenDown()
        {
            if (exitDropY > 0f) return exitDropY;
            var canvas = girl.GetComponentInParent<Canvas>();
            float height = canvas != null ? ((RectTransform)canvas.transform).rect.height : Screen.height;
            return height * 0.5f + girl.rect.height;
        }
    }
}
