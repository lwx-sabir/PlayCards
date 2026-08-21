using System;
using System.Collections;
using DG.Tweening;
using TMPro;
using UnityEngine;
using PlayCard.UI.RewardFly;

namespace PlayCard.Piggy
{
    /// <summary>
    /// Conducts the break payoff on the BUSY view: the pig detonates, and once the wreckage starts to clear the
    /// amount itself travels up to where the pig stood and bursts into the chips it represents.
    ///
    /// The sequence:
    ///
    ///  • BANG — pig comes apart, bang particles fire, pieces launch.
    ///  • DEBRIS FADING — the shell has been seen and is on its way out. Only now does the money move; before it,
    ///    two things would be competing for the same space.
    ///  • THE AMOUNT TRAVELS. The chip-and-value item leaves its slot at the bottom and flies up to the pig's
    ///    position. This is the idea that makes the whole thing work: the number the player was promised moves to
    ///    the spot the bank broke, so the payout is delivered FROM the break rather than merely happening near it.
    ///  • THE BURST. Chips erupt out of that item and magnetise to the balance. The number does not fade and get
    ///    replaced by chips — it BECOMES them, which is why the burst originates from the item itself.
    ///  • THE LINE. "You have collected X" plus the reason to come back: a bigger bank at a higher level.
    ///
    /// It is a conductor, not an effect. <see cref="PiggyBlast"/> owns the explosion, <see cref="RewardFly"/> owns
    /// the chips; both are tuned alone. What lives here is the ORDER and the beats between. Nothing here decides an
    /// amount — the server said what the bank paid; this shows it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PiggyBreakDirector : MonoBehaviour
    {
        [Header("Pieces (leave empty to find on this object)")]
        [SerializeField] private PiggyBlast blast;
        [Tooltip("Without one, the chips beat is skipped and the sequence still completes.")]
        [SerializeField] private RewardFly fly;
        [Tooltip("Leave empty to use the PiggyScreen on this object. The payoff plays inside the BUSY view, so this " +
                 "holds that view up for the whole sequence and releases it at the end.")]
        [SerializeField] private PiggyScreen screen;

        [Header("The amount that travels")]
        [Tooltip("The chip icon + value item — the thing that flies up to the pig and then bursts. Its authored " +
                 "position is captured on the first run and restored afterwards, so replays are identical.")]
        [SerializeField] private RectTransform valueMover;
        [Tooltip("Where it travels TO. Leave empty to use the blast's pig, which is the spot the bank broke.")]
        [SerializeField] private RectTransform moveTarget;
        [SerializeField] private float moveSeconds = 0.55f;
        [Tooltip("Overshoot slightly and settle — it should arrive, not stop.")]
        [SerializeField] private Ease moveEase = Ease.OutBack;
        [Tooltip("Punch as it lands in the pig. This is the beat that reads as the impact setting the bank off.")]
        [SerializeField] private float arrivePunch = 0.25f;
        [Tooltip("How long BEFORE the bang the amount should land. The travel is started early enough to arrive " +
                 "with this much of the charge left, so the money goes IN and then the pig blows - cause, then " +
                 "effect. Too large and it sits there waiting; too small and it is still moving when the pig goes.")]
        [SerializeField] private float arriveLead = 0.15f;

        [Header("Text")]
        [SerializeField] private TMP_Text amountText;
        [Tooltip("The chip icon beside the amount - the Image child of Text_Value. Switched OFF the instant the " +
                 "chips burst out of it: the icon stood in for the money, so once real chips are flying, keeping it " +
                 "puts the same thing on screen twice.\n\nLeave empty to keep the icon throughout.")]
        [SerializeField] private GameObject chipIcon;
        [Tooltip("The status line. Shows Collecting Text while the payout runs, then Collected Format.")]
        [SerializeField] private TMP_Text infoText;
        [SerializeField] private string collectingText = "Collecting Reward";
        [Tooltip("THE FINAL MESSAGE. Author it however you like and leave it INACTIVE - it is switched on when the " +
                 "chips are away, and the status line above is switched off at the same moment.\n\n" +
                 "Leave this empty to write the message into Info Text instead, reusing the status line.")]
        [SerializeField] private TMP_Text collectedText;
        [Tooltip("{0} = the amount, already formatted. TMP rich text works here, and the amount is worth colouring: " +
                 "in a white sentence the number is the only part that is the reward, and an amber against the deep " +
                 "blue panel is the one colour that reads as money without fighting the gold pig.")]
        [SerializeField] private string collectedFormat =
            "You have collected <color=#FFA600>{0}</color>\nLevel up more to get a bigger piggy";
        [Tooltip("The spinner. Hidden once the chips are away.")]
        [SerializeField] private GameObject spinner;

        [Header("Number format")]
        [Tooltip("Compact to 1M / 250K. OFF by default and it usually should stay off here: compacting exists for " +
                 "cramped HUDs where the digits do not fit. On a receipt with a line to itself the digits ARE the " +
                 "reward, and 10,000,000 reads as far more money than 10M.")]
        [SerializeField] private bool compactAmount;
        [SerializeField] private string moneyFormat = "#,0";

        [Header("Chips")]
        [Tooltip("Must match a RewardFlyTarget that is LIVE while this popup is open.")]
        [SerializeField] private string chipRewardId = "Chips";
        [Tooltip("0 = let RewardFly derive the piece count from the amount.")]
        [SerializeField] private int chipPieces;
        [Tooltip("Fade the value item out as the chips leave it — the number turning INTO the money. Off leaves it " +
                 "sitting where the pig was.")]
        [SerializeField] private bool fadeValueOnBurst = true;
        [SerializeField] private float valueFadeSeconds = 0.35f;

        [Tooltip("Optional. The reward artwork that appears with the receipt - Collected_Rewards_Image. Author it " +
                 "INACTIVE; it is switched on as the spinner and the status line go.")]
        [SerializeField] private GameObject collectedImage;

        [Header("The receipt's entrance")]
        [Tooltip("Scale it pops in FROM. It arrives on the same beat as the chips leaving, so it has to announce " +
                 "itself against a screen that is already busy - a fade would simply be lost in the burst.")]
        [SerializeField] private float popFromScale = 0.6f;
        [SerializeField] private float popSeconds = 0.45f;
        [Tooltip("Overshoot on the settle. 1.7 is DOTween's default OutBack; higher is springier.")]
        [SerializeField] private float popOvershoot = 1.7f;

        [Tooltip("Scale the reward image grows from.")]
        [SerializeField] private float imagePopFrom = 0.7f;
        [SerializeField] private float imagePopSeconds = 0.4f;
        [Tooltip("Beat AFTER the text lands before the image follows. The stagger is what makes them read as two " +
                 "beats rather than one busy flash - simultaneous, they compete and neither registers.")]
        [SerializeField] private float imagePopDelay = 0.12f;

        [Header("Finish")]
        [Tooltip("Beat after the receipt appears before the sequence reports done. This is NOT how long the receipt " +
                 "is shown - the busy view stays up until the player closes it. This only paces the callback the " +
                 "purchase layer is waiting on.")]
        [SerializeField] private float holdSeconds = 0.8f;

        [Header("Testing")]
        [SerializeField] private long previewAmount = 10_000_000;

        /// <summary>True from the start of the break until the payout has settled.</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>The payoff is over and the screen may repaint. Mirrors <see cref="PiggyBlast.Finished"/>.</summary>
        public event Action Finished;

        private void Awake()
        {
            if (blast == null) blast = GetComponent<PiggyBlast>();
            if (fly == null) fly = GetComponent<RewardFly>();
            if (screen == null) screen = GetComponent<PiggyScreen>();

            // Put the card into its waiting state NOW rather than trusting how it was authored: the final message
            // is a thing you build by looking at it, so it will be left switched ON in the prefab as often as not.
            ShowStatus();
        }

        private void OnDisable()
        {
            // Closed part-way through. Put everything back so the next open starts clean rather than on the tail of
            // the last payout, and hand back any balance hold.
            if (_run != null) { StopCoroutine(_run); _run = null; }
            if (blast != null) blast.RestorePig = true;

            RestoreMover();
            ShowStatus();

            if (_armed) { RewardFlyTarget.EndBurst(chipRewardId); _armed = false; }
            if (screen != null) { screen.SetBusy(false); screen.SetBuyInteractable(true); }

            if (IsPlaying)
            {
                IsPlaying = false;
                Finished?.Invoke();
            }
        }

        /// <summary>
        /// Run the payoff for a completed purchase. <paramref name="chips"/> is what the SERVER paid, never a figure
        /// worked out here. <paramref name="onDone"/> fires once everything has settled.
        /// </summary>
        public void PlayBreak(decimal chips, Action onDone = null)
        {
            if (IsPlaying || blast == null) { onDone?.Invoke(); return; }

            // ARM THE COUNTER FIRST, synchronously, before anything else runs.
            //
            // The caller's next act after a successful break is refreshing the wallet, and that push is what would
            // otherwise snap the balance to its new value seconds before the first chip lands — the payout would be
            // over before the animation began. Arming inside RewardFly is far too late here: the chips do not leave
            // until the pig has broken and the amount has travelled. Same contract the pass binder uses.
            if (fly != null && chips > 0m) _armed = RewardFlyTarget.ArmBurst(chipRewardId);

            IsPlaying = true;
            _run = StartCoroutine(Run(chips, onDone));
        }

        /// <summary>
        /// Run the REAL sequence with a fake amount and no purchase. Needs PiggyPanel's Test Mode.
        ///
        /// Public so PiggyBlast's own menu item can hand off to it: there should be one preview, and it should be
        /// the thing that actually ships. A preview that runs a cut-down version of the sequence is worse than no
        /// preview, because it is tuned against something the player never sees.
        /// </summary>
        [ContextMenu("Preview Break Payoff (needs PiggyPanel Test Mode)")]
        public void PreviewBreak()
        {
            var panel = GetComponent<PiggyPanel>();
            if (panel == null || !panel.TestMode)
            {
                Debug.LogWarning($"{name}: turn Test Mode ON in PiggyPanel first — the payoff runs in the FULL " +
                                 "view, and the preview is deliberately inert without it.", this);
                return;
            }

            if (!isActiveAndEnabled)
            {
                Debug.LogWarning($"{name}: open the piggy popup first — this runs as a coroutine on this component.",
                                 this);
                return;
            }

            PlayBreak(previewAmount);
        }

        private IEnumerator Run(decimal chips, Action onDone)
        {
            // Bought and paid out, so the pig must NOT come back whole when the debris clears.
            blast.RestorePig = false;

            // The pig, the pieces and the amount all live in the busy view, so it stays up until this is over —
            // far longer than the network call it was named for.
            if (screen != null) screen.SetBusy(true);

            CaptureMover();
            RestoreMover();

            string money = Format(chips);
            if (amountText != null) amountText.text = money;
            ShowStatus();

            // ---- CHARGE begins, and the money goes in while it does ----
            //
            // The travel used to happen AFTER the break, which told the story backwards: the bank burst and then
            // the amount wandered over to it. Landing the money during the wind-up makes the pig look like it is
            // being loaded past what it can hold - the break becomes the consequence of the amount arriving.
            bool banged = false, fading = false;
            void OnBang() => banged = true;
            void OnFading() => fading = true;
            blast.Banged += OnBang;
            blast.DebrisFading += OnFading;

            blast.Play();

            // Start late enough that it ARRIVES with arriveLead of the charge still to run.
            float startAt = blast.ChargeSeconds - arriveLead - moveSeconds;
            if (startAt > 0f) yield return new WaitForSecondsRealtime(startAt);

            var target = moveTarget != null ? moveTarget : blast.IntactPig;
            if (valueMover != null && target != null && moveSeconds > 0f)
            {
                // Move by the DELTA between the two centres, in world space, converted back into the mover's own
                // parent space.
                //
                // Not by computing an absolute anchoredPosition for the destination: the two live under different
                // parents with their own anchors and pivots, and anchoredPosition is measured from an anchor, not
                // from the parent's origin. A delta is the one quantity that means the same thing on both sides.
                var fromWorld = valueMover.TransformPoint(valueMover.rect.center);
                var toWorld = target.TransformPoint(target.rect.center);

                var parent = valueMover.parent as RectTransform;
                Vector2 delta = parent != null
                    ? (Vector2)parent.InverseTransformVector(toWorld - fromWorld)
                    : (Vector2)(toWorld - fromWorld);

                yield return valueMover
                    .DOAnchorPos(valueMover.anchoredPosition + delta, moveSeconds)
                    .SetEase(moveEase).SetUpdate(true)
                    .WaitForCompletion();

                if (arrivePunch > 0f)
                    valueMover.DOPunchScale(Vector3.one * arrivePunch, 0.3f, 1, 0.6f).SetUpdate(true);
            }

            // ---- the pig gives way ----
            while (!banged) yield return null;
            blast.Banged -= OnBang;

            // Let the wreckage be seen and start clearing before any money comes back out. Debris and chips erupting
            // together are indistinguishable - both are small objects flying outward from the same point.
            while (!fading) yield return null;
            blast.DebrisFading -= OnFading;

            // ---- the burst: the number becomes the chips ----
            bool chipsDone = true;
            if (fly != null && chips > 0m)
            {
                chipsDone = false;
                _armed = false;   // handed over — RewardFly owns the hold from here

                // The icon leaves WITH the chips.
                if (chipIcon != null) chipIcon.SetActive(false);

                fly.Play(new RewardFlyItem
                {
                    RewardId = chipRewardId,
                    Amount = chips,
                    Pieces = chipPieces,
                    From = valueMover,
                }, onComplete: () => chipsDone = true);

                if (fadeValueOnBurst && valueMover != null && valueFadeSeconds > 0f)
                {
                    var group = valueMover.GetComponent<CanvasGroup>();
                    if (group == null) group = valueMover.gameObject.AddComponent<CanvasGroup>();
                    _moverGroup = group;
                    group.DOFade(0f, valueFadeSeconds).SetUpdate(true);
                }
            }
            else if (_armed)
            {
                // Armed but nothing will fly — hand the hold straight back rather than making the HUD wait out its
                // timeout on a burst that is never coming.
                RewardFlyTarget.EndBurst(chipRewardId);
                _armed = false;
            }

            // ---- the receipt, ON the burst ----
            //
            // Not after the chips have landed. The burst IS the payout; making the player watch a spinner for
            // another second and a half after it, before being told what happened, puts the confirmation in the
            // wrong place. It lands as the chips leave and settles while they fly.
            ShowReceipt(money);

            // Hard ceiling: this gates the popup closing and the refresh behind it, so a fly that never reports back
            // must not strand the player in a screen with no way out.
            float waited = 0f;
            while (!chipsDone && waited < 6f) { waited += Time.unscaledDeltaTime; yield return null; }

            if (holdSeconds > 0f) yield return new WaitForSecondsRealtime(holdSeconds);

            // The busy view STAYS UP. It is now a receipt, and the player dismisses it themselves.
            //
            // Dropping back to the filling view here was wrong: it replaced the confirmation of a purchase with an
            // empty piggy asking to be filled again, seconds after someone paid. The close button on this view is
            // the way out, and OnDisable does the releasing when they take it.
            IsPlaying = false;
            _run = null;
            onDone?.Invoke();
            Finished?.Invoke();
        }

        /// <summary>Back to the waiting state: status line up, final message away, spinner turning.</summary>
        /// <summary>The receipt arrives: spinner away, message in, popping so it registers against a busy screen.</summary>
        private void ShowReceipt(string money)
        {
            if (spinner != null) spinner.SetActive(false);

            string line = string.Format(collectedFormat, money);

            // A dedicated message steps in and the status line steps ASIDE rather than being overwritten, so the two
            // can be styled as what they are: a transient "working on it" and a permanent receipt.
            TMP_Text shown = null;
            if (collectedText != null)
            {
                collectedText.text = line;
                collectedText.gameObject.SetActive(true);
                if (infoText != null) infoText.gameObject.SetActive(false);
                shown = collectedText;
            }
            else if (infoText != null)
            {
                infoText.text = line;
                shown = infoText;
            }

            if (shown == null || popSeconds <= 0f) return;

            var rt = shown.rectTransform;
            if (!_receiptCaptured) { _receiptCaptured = true; _receiptScale = rt.localScale; }

            rt.DOKill();
            rt.localScale = _receiptScale * popFromScale;
            rt.DOScale(_receiptScale, popSeconds).SetEase(Ease.OutBack, popOvershoot).SetUpdate(true);

            RevealImage();
        }

        /// <summary>
        /// Bring the reward artwork in behind the text, a beat later and fading as it grows.
        ///
        /// Fading as well as scaling, unlike the text: the text has an edge and reads instantly at any size, while
        /// artwork popping in at full opacity from nothing looks like a glitch. The delay is the important part -
        /// arriving with the text, the two would compete and neither would land.
        /// </summary>
        private void RevealImage()
        {
            if (collectedImage == null) return;

            var rt = collectedImage.transform as RectTransform;
            if (rt == null) { collectedImage.SetActive(true); return; }

            if (!_imageCaptured) { _imageCaptured = true; _imageScale = rt.localScale; }

            _imageGroup = collectedImage.GetComponent<CanvasGroup>();
            if (_imageGroup == null) _imageGroup = collectedImage.AddComponent<CanvasGroup>();

            collectedImage.SetActive(true);
            rt.DOKill();
            _imageGroup.DOKill();

            if (imagePopSeconds <= 0f)
            {
                rt.localScale = _imageScale;
                _imageGroup.alpha = 1f;
                return;
            }

            rt.localScale = _imageScale * imagePopFrom;
            _imageGroup.alpha = 0f;

            rt.DOScale(_imageScale, imagePopSeconds)
              .SetEase(Ease.OutBack, popOvershoot).SetDelay(imagePopDelay).SetUpdate(true);
            _imageGroup.DOFade(1f, imagePopSeconds * 0.6f)
              .SetDelay(imagePopDelay).SetUpdate(true);
        }

        private void ShowStatus()
        {
            if (spinner != null) spinner.SetActive(true);
            if (chipIcon != null) chipIcon.SetActive(true);

            if (collectedImage != null)
            {
                var irt = collectedImage.transform as RectTransform;
                if (irt != null) { irt.DOKill(); if (_imageCaptured) irt.localScale = _imageScale; }
                if (_imageGroup != null) { _imageGroup.DOKill(); _imageGroup.alpha = 1f; }
                collectedImage.SetActive(false);
            }

            // Undo the pop, or the next receipt starts from whatever scale the last one was interrupted at.
            if (_receiptCaptured)
            {
                var rrt = collectedText != null ? collectedText.rectTransform
                                                : (infoText != null ? infoText.rectTransform : null);
                if (rrt != null) { rrt.DOKill(); rrt.localScale = _receiptScale; }
            }

            if (collectedText != null) collectedText.gameObject.SetActive(false);
            if (infoText != null)
            {
                infoText.gameObject.SetActive(true);
                infoText.text = collectingText;
            }
        }

        private void CaptureMover()
        {
            if (_moverCaptured || valueMover == null) return;
            _moverCaptured = true;
            _moverPos = valueMover.anchoredPosition;
            _moverScale = valueMover.localScale;
        }

        /// <summary>Put the amount back where it was authored. Its layout IS the card, so this has to be exact.</summary>
        private void RestoreMover()
        {
            if (!_moverCaptured || valueMover == null) return;

            valueMover.DOKill();
            valueMover.anchoredPosition = _moverPos;
            valueMover.localScale = _moverScale;

            if (_moverGroup != null)
            {
                _moverGroup.DOKill();
                _moverGroup.alpha = 1f;
            }
        }

        /// <summary>1,000,000 as "1M". Trimmed, so 1.0M reads as 1M while 1.4M keeps its digit.</summary>
        private string Format(decimal amount)
        {
            if (!compactAmount) return amount.ToString(moneyFormat);

            decimal abs = Math.Abs(amount);
            if (abs >= 1_000_000_000m) return Trim(amount / 1_000_000_000m) + "B";
            if (abs >= 1_000_000m) return Trim(amount / 1_000_000m) + "M";
            if (abs >= 1_000m) return Trim(amount / 1_000m) + "K";
            return amount.ToString(moneyFormat);
        }

        private static string Trim(decimal v)
        {
            v = Math.Round(v, 1);
            return v == Math.Truncate(v) ? v.ToString("0") : v.ToString("0.0");
        }

        private Coroutine _run;
        private bool _armed;
        private bool _moverCaptured;
        private Vector2 _moverPos;
        private Vector3 _moverScale;
        private CanvasGroup _moverGroup;
        private bool _receiptCaptured;
        private bool _imageCaptured;
        private Vector3 _imageScale = Vector3.one;
        private CanvasGroup _imageGroup;
        private Vector3 _receiptScale = Vector3.one;
    }
}
