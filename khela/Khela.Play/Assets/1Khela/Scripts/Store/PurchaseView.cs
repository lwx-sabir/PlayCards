using System;
using System.Collections.Generic;
using DG.Tweening;
using Khela.Common.Rewards;
using Khela.Common.Store;
using PlayCard.UI;
using PlayCard.UI.RewardFly;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Store
{
    /// <summary>
    /// What the player sees after they have paid: the store sheet is done, the receipt is with our server, and this is
    /// the only thing standing between "money left my account" and knowing what it bought.
    ///
    /// Three states over one panel:
    /// <list type="bullet">
    /// <item><b>Verifying</b> — the receipt is being checked. A spinner and a line of copy, nothing else.</item>
    /// <item><b>Complete</b> — the server GRANTED. The item and its rewards arrive with juice; this is the celebration.</item>
    /// <item><b>Problem</b> — pending or rejected. Money may have been taken and we cannot confirm yet, so this state
    /// exists to say so in words and give them a way out. A spinner that never resolves is the worst possible answer.</item>
    /// </list>
    ///
    /// Two rules it never breaks, because this is the money path:
    /// <list type="bullet">
    /// <item>It is DISPLAY ONLY. Nothing here grants, retries or confirms anything — the purchase completes whether this
    /// panel exists or not, and closing it early costs the player nothing.</item>
    /// <item>It only says "complete" when the SERVER said granted, and the rewards it lists are the ledger's numbers
    /// (<c>Grants</c>), never the card's advertised ones.</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PurchaseView : MonoBehaviour
    {
        /// <summary>When the rewards leave for their counters.</summary>
        public enum FlyTrigger
        {
            /// <summary>The end of the ceremony: the last card has stamped down, and the rewards leave the cards.</summary>
            AfterCardsSettle = 0,
            /// <summary>Only when the player dismisses — the rewards leave as the panel does.</summary>
            OnContinue = 1,
            /// <summary>No flight. The counters just update on their own.</summary>
            Never = 2,
        }

        /// <summary>An icon for one reward id — the same convention RewardFly and the reward rows use.</summary>
        [Serializable]
        public sealed class IconEntry
        {
            [Tooltip("Chips · Kash · Gems · Coins · VipPoints · Xp — or a chest/item id.")]
            public string rewardId;
            public Sprite icon;
        }

        [Header("Root")]
        [Tooltip("The whole view. Empty = this object. Turned off between purchases.")]
        [SerializeField] private GameObject root;

        [Header("State — verifying")]
        [Tooltip("Shown while the receipt is being checked: the 'Purchasing…' line and the spinner.")]
        [SerializeField] private List<GameObject> verifyingObjects = new List<GameObject>();

        [Header("State — complete")]
        [Tooltip("Shown when the server granted: the 'Complete' line, the item art and its glow, the granted rows, Continue.")]
        [SerializeField] private List<GameObject> completeObjects = new List<GameObject>();

        [Header("State — problem (pending / rejected)")]
        [Tooltip("Error_View — the whole group shown when the receipt could not be confirmed.")]
        [SerializeField] private GameObject errorView;
        [Tooltip("Text_Purchase_Error. This one IS written at runtime: what went wrong is the server's words, not copy " +
                 "that can be authored in advance.")]
        [SerializeField] private TMP_Text errorText;
        [Tooltip("Btn_Try_Restore — asks the store for the completed orders again. The honest thing to offer someone " +
                 "whose payment we could not confirm.")]
        [SerializeField] private Button tryRestoreButton;

        [Header("Content")]
        [Tooltip("The art of the pack that was bought, from the catalog.")]
        [SerializeField] private Image itemImage;
        [Tooltip("The pack's name.")]
        [SerializeField] private List<TMP_Text> itemNameTexts = new List<TMP_Text>();
        [Tooltip("Where the granted rows spawn — give it a layout group.")]
        [SerializeField] private RectTransform grantedGroup;
        [Tooltip("One row: a BundleRewardView (icon + amount), the same row the daily bundle uses.")]
        [SerializeField] private BundleRewardView rowPrefab;
        [SerializeField] private List<IconEntry> icons = new List<IconEntry>();
        [Tooltip("Rows beyond this are not shown. They are still granted — the ledger is not what this limits.")]
        [SerializeField] private int maxRows = 6;
        [Tooltip("Hide XP rows. XP is a side effect of buying rather than the thing bought.")]
        [SerializeField] private bool hideXp;

        [Header("Continue")]
        [SerializeField] private List<Button> continueButtons = new List<Button>();

        [Header("Fly to the counters")]
        [Tooltip("The RewardFly that owns the burst and the balance counters. Empty = the panel just closes, which is " +
                 "a perfectly good shop; the flight is the flourish, not the payout.")]
        [SerializeField] private RewardFly fly;
        [Tooltip("Fallback source, used only for a reward that has no card of its own. Empty = the granted group, " +
                 "then the item art.")]
        [SerializeField] private RectTransform flyFrom;
        [Tooltip("Pieces per reward. More reads as more, up to a point — past a dozen it is noise.")]
        [SerializeField] private int piecesPerLine = 12;
        [Tooltip("When the rewards leave. After the cards settle is the end of the ceremony — each reward bursts out " +
                 "of its OWN card, which is the payoff the cards were built for.")]
        [SerializeField] private FlyTrigger flyTrigger = FlyTrigger.AfterCardsSettle;
        [Tooltip("Give up waiting for the flight and close anyway. A panel that will not dismiss because an effect " +
                 "never reported back is worse than a missed animation.")]
        [SerializeField] private float flyTimeoutSeconds = 4f;

        [Header("Text")]
        [Tooltip("Shown when the store took the payment but the server could not confirm it yet — it will be granted " +
                 "when it clears. Never says the purchase failed, because it has not.")]
        [SerializeField] private string pendingText = "Payment received. This will arrive shortly — you can keep playing.";
        [SerializeField] private string rejectedText = "We couldn't confirm that purchase. Nothing was charged for it.";
        [SerializeField] private string restoringText = "Checking with the store…";
        [Tooltip("After a restore is asked for. It does not claim anything arrived — whatever the store returns is being " +
                 "redeemed now, and a grant reopens this view on its own.")]
        [SerializeField] private string restoreAskedText = "Asked the store to re-send your purchases. Anything owed will arrive shortly.";

        [Header("Juice — the panel")]
        [Tooltip("Panel fade in. The panel itself never scales — it is a full-screen overlay, and scaling one reads as a wobble.")]
        [SerializeField] private float fadeSeconds = 0.18f;
        [Tooltip("Shortest time the verifying state is ever shown. Verification is often a couple of hundred " +
                 "milliseconds, and a spinner that appears and vanishes reads as a glitch rather than as work.")]
        [SerializeField] private float minVerifySeconds = 0.4f;

        [Header("Juice — 1. the item emerges")]
        [Tooltip("The item swelling up out of nothing, before it comes down.")]
        [SerializeField] private float itemEmergeSeconds = 0.24f;
        [SerializeField] private float itemEmergeFromScale = 0.25f;
        [Tooltip("How far past its size the item rises before the stamp. This is the wind-up — the bigger it is, the " +
                 "harder the stamp reads.")]
        [SerializeField] private float itemOvershootScale = 1.28f;
        [Tooltip("Offset the item rises to and stamps down FROM. Leave at zero for a stamp in place; a small +Y makes " +
                 "it drop onto its spot.")]
        [SerializeField] private Vector2 itemEmergeOffset = Vector2.zero;

        [Header("Juice — 2. the stamp")]
        [Tooltip("The HANG at the top before it drops. Small, but it is most of what makes the drop read as a drop — " +
                 "a stamp rises, hesitates, then falls. Set to 0 and the whole move becomes one smooth pop.")]
        [SerializeField] private float itemAnticipateSeconds = 0.08f;
        [Tooltip("The slam itself. Short — a stamp is an impact, and anything over ~0.12s reads as a descent.")]
        [SerializeField] private float itemStampSeconds = 0.09f;
        [Tooltip("How hard it SQUASHES on contact, as a fraction of scale: 0.25 lands at 125% wide and 75% tall for an " +
                 "instant. This is the impact — it is not a wobble afterwards, it is the shape of the hit itself.")]
        [SerializeField] private float itemPunchScale = 0.25f;
        [Tooltip("Springing back out of the squash. Elastic, so it rings down rather than easing to a stop.")]
        [SerializeField] private float itemPunchSeconds = 0.45f;
        [Tooltip("Optional: something that JOLTS on impact — the content root, so the whole panel takes the hit. " +
                 "Do not point this at the item itself; it is already punching.")]
        [SerializeField] private RectTransform stampShakeTarget;
        [SerializeField] private float stampShakeStrength = 14f;
        [SerializeField] private float stampShakeSeconds = 0.28f;

        [Header("Juice — 3. particles, on the stamp settling")]
        [Tooltip("Fx_Item_BG, the burst inside the item art, dust — everything that goes off ON IMPACT. They are " +
                 "stopped and cleared when the complete state opens, so Play On Awake cannot fire them early.")]
        [SerializeField] private List<ParticleSystem> stampParticles = new List<ParticleSystem>();

        [Header("Juice — 4. the granted cards")]
        [Tooltip("Beat between the stamp settling and the first card leaving the item.")]
        [SerializeField] private float cardsDelayAfterStamp = 0.12f;
        [Tooltip("How long one card takes to travel from the middle of the item art to its slot.")]
        [SerializeField] private float cardFlySeconds = 0.30f;
        [Tooltip("Gap between one card LAUNCHING and the next. At or above Card Fly Seconds they settle strictly one " +
                 "by one; below it they overlap into a stream.")]
        [SerializeField] private float cardStagger = 0.30f;
        [SerializeField] private float cardFromScale = 0.4f;
        [SerializeField] private Ease cardEase = Ease.OutCubic;
        [Tooltip("How far past its size a card grows on the way over, so it arrives with something to give up.")]
        [SerializeField] private float cardOvershootScale = 1.3f;
        [Tooltip("The card's contact squash — same idea as the item's, quicker.")]
        [SerializeField] private float cardStampSeconds = 0.07f;
        [Tooltip("How hard a card squashes as it lands, as a fraction of scale.")]
        [SerializeField] private float cardPunchScale = 0.28f;
        [Tooltip("Springing back out of that squash.")]
        [SerializeField] private float cardPunchSeconds = 0.38f;

        /// <summary>True while a purchase is being shown — one ceremony at a time.</summary>
        public static bool Handling { get; private set; }

        // The beats of the ceremony, announced rather than sounded. This view stays display-only and ShopAudio stays
        // the single owner of the shop's sound — the same split the table and the pass use. A listener is optional;
        // nothing here waits on one.

        /// <summary>The receipt went to the server and the verifying state is up.</summary>
        public event Action Verifying;
        /// <summary>The server GRANTED and the celebration has started — the fanfare belongs here, not on the
        /// purchase result, which lands before the minimum-verify delay has even elapsed.</summary>
        public event Action CeremonyStarted;
        /// <summary>The item hit its mark. The impact — the loudest thing in the sequence.</summary>
        public event Action Stamped;
        /// <summary>A granted card leaving the item, by row index.</summary>
        public event Action<int> CardLaunched;
        /// <summary>A granted card stamping into its slot, by row index.</summary>
        public event Action<int> CardLanded;
        /// <summary>Pending or rejected — the state that needs saying, not celebrating.</summary>
        public event Action Problem;

        /// <summary>
        /// What the server granted, announced at the START of the celebration — well before any of it flies.
        ///
        /// The timing is the point. A RewardFlyTarget registers itself in OnEnable, so a counter that is switched off
        /// is not in the registry and the flight skips it with a warning. Anything that has to REVEAL a counter before
        /// rewards can land on it needs telling now, not when the burst begins.
        /// </summary>
        public event Action<IReadOnlyList<string>> RewardsIncoming;

        private CanvasGroup group;
        private readonly List<BundleRewardView> spawnedRows = new List<BundleRewardView>();
        /// <summary>The grants that got a row, in row order. Pairs 1:1 with <see cref="spawnedRows"/>.</summary>
        private readonly List<GrantedLineDto> shownGrants = new List<GrantedLineDto>();
        /// <summary>Layout components frozen for the card flight, to be switched back on when the rows are cleared.</summary>
        private readonly List<Behaviour> frozenLayout = new List<Behaviour>();
        private Sequence juice;
        /// <summary>The product this view is following. Anything else's result is not ours to show.</summary>
        private string watchingProductId;
        private float verifyingSince;
        /// <summary>The rewards have already left for their counters — Continue must not send them twice.</summary>
        private bool flyDone;
        private bool closing;
        /// <summary>The item art's authored resting spot, captured once — see PlayCelebration.</summary>
        private Vector2 itemHome;

        private GameObject Root => root != null ? root : gameObject;

        private void Awake()
        {
            // ⚠ This component must NOT live on the object it hides. It listens for a purchase while the view is
            // closed, and a component on a disabled GameObject does not run — it would switch itself off after the
            // first purchase and never hear another. Put it on the shop panel and point Root at Purchase_View.
            if (root == null || root == gameObject)
                Debug.LogWarning($"{name}: Root is this object, so hiding the view also stops this component listening " +
                                 "for the next purchase. Put PurchaseView on an always-active object (the shop panel) " +
                                 "and assign Root = Purchase_View.", this);

            // TryGetComponent, never `GetComponent() ?? AddComponent()`. `??` does not respect Unity's overloaded
            // null: a missing component comes back as a live C# wrapper around a dead native pointer, so `??` takes
            // the left branch and the next line throws MissingComponentException.
            if (!Root.TryGetComponent(out group)) group = Root.AddComponent<CanvasGroup>();
            if (itemImage != null) itemHome = itemImage.rectTransform.anchoredPosition;
            foreach (var b in continueButtons) if (b != null) b.onClick.AddListener(Continue);
            if (tryRestoreButton != null) tryRestoreButton.onClick.AddListener(TryRestore);
            Root.SetActive(false);
        }

        private void OnDestroy()
        {
            foreach (var b in continueButtons) if (b != null) b.onClick.RemoveListener(Continue);
            if (tryRestoreButton != null) tryRestoreButton.onClick.RemoveListener(TryRestore);
            if (Handling) Handling = false;
        }

        private void OnEnable()
        {
            var iap = IapService.Instance;
            if (iap == null) return;
            iap.OnRedeemStarted += HandleRedeemStarted;
            iap.OnPurchaseCompleted += HandleCompleted;
        }

        private void OnDisable()
        {
            var iap = IapService.Instance;
            if (iap != null)
            {
                iap.OnRedeemStarted -= HandleRedeemStarted;
                iap.OnPurchaseCompleted -= HandleCompleted;
            }
            juice?.Kill();
            Handling = false;
        }

        // ---------------------------------------------------------------- the purchase

        /// <summary>
        /// The money is gone and the receipt is with our server. THIS is when the view opens — not when Buy was pressed.
        ///
        /// Everything before this point is the store's own sheet, and it can still be cancelled there; a panel that says
        /// "verifying your purchase" over a payment dialog is claiming something that has not happened. The card that
        /// was tapped owns that earlier stretch, through its own loading state.
        /// </summary>
        private void HandleRedeemStarted(string productId)
        {
            if (Handling) return;
            watchingProductId = productId;
            ShowVerifying(productId);
        }

        private void HandleCompleted(IapService.PurchaseResult result)
        {
            if (result == null) return;
            // Only the purchase this view actually opened for. A result with no ceremony behind it — an old order
            // re-driven at start-up, a restore — would otherwise run the whole celebration on a hidden panel.
            if (!Handling || string.IsNullOrEmpty(watchingProductId)) return;
            if (!string.Equals(result.productId, watchingProductId, StringComparison.OrdinalIgnoreCase)) return;

            // Backing out of the sheet is not an outcome worth a screen.
            if (result.Cancelled) { Hide(); return; }

            switch (result.status)
            {
                case IapService.PurchaseStatus.Success:
                    AfterMinimumVerify(() => ShowComplete(result));
                    break;
                case IapService.PurchaseStatus.Pending:
                    AfterMinimumVerify(() => ShowProblem(pendingText));
                    break;
                default:
                    // Anything else took no money we can see, but the player pressed BUY and deserves an answer.
                    AfterMinimumVerify(() => ShowProblem(string.IsNullOrWhiteSpace(result.message) ? rejectedText : result.message));
                    break;
            }
        }

        /// <summary>
        /// Run <paramref name="then"/> once the verifying state has been up long enough to have been seen.
        ///
        /// Verification usually answers faster than a person can read a spinner. Cutting straight to the result makes
        /// the screen flicker and, on the money path, look unreliable — the one impression it must never give.
        /// </summary>
        private void AfterMinimumVerify(Action then)
        {
            float shown = Time.unscaledTime - verifyingSince;
            float wait = Mathf.Max(0f, minVerifySeconds - shown);
            if (wait <= 0f) { then(); return; }
            // A COROUTINE, not DOVirtual.DelayedCall. DOTween's safe mode catches anything thrown inside a tween
            // callback and downgrades it to a warning it may not even print — so a real exception in the celebration
            // vanished completely, leaving a half-played ceremony and an empty console. On the money path the failure
            // has to be loud.
            StartCoroutine(WaitThen(wait, then));
        }

        private System.Collections.IEnumerator WaitThen(float seconds, Action then)
        {
            yield return new WaitForSecondsRealtime(seconds);
            if (this == null || !Root.activeSelf) yield break;
            then();
        }

        // ---------------------------------------------------------------- states

        private void ShowVerifying(string productId)
        {
            Handling = true;
            flyDone = false;
            verifyingSince = Time.unscaledTime;

            Root.SetActive(true);
            SetState(verifyingObjects, completeObjects);
            if (errorView != null) errorView.SetActive(false);
            StopParticles();
            FillProduct(productId);
            ClearRows();

            juice?.Kill();
            group.alpha = 0f;
            group.blocksRaycasts = true;
            juice = DOTween.Sequence().SetUpdate(true).Append(group.DOFade(1f, fadeSeconds).SetEase(Ease.OutQuad));
            Verifying?.Invoke();
        }

        private void ShowComplete(IapService.PurchaseResult result)
        {
            SetState(completeObjects, verifyingObjects);
            if (errorView != null) errorView.SetActive(false);
            // AFTER the activation, not before it: these systems come on with the complete state, and one with Play On
            // Awake starts the moment its object is enabled — which is the emerge, not the impact it is meant to mark.
            StopParticles();
            SpawnRows(result.redeem?.Grants);

            // The celebration is a flourish over a payout that already happened, so it is never allowed to take the
            // screen down with it. If it throws, the rows still stand where the layout put them, Continue still works,
            // and the failure is LOUD rather than swallowed.
            if (RewardsIncoming != null)
            {
                var ids = new List<string>(shownGrants.Count);
                foreach (var g in shownGrants)
                    if (g != null) ids.Add(string.IsNullOrEmpty(g.Id) ? "Xp" : g.Id);
                RewardsIncoming(ids);
            }

            try
            {
                CeremonyStarted?.Invoke();
                PlayCelebration();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
                juice?.Kill();
                juice = null;
                ThawRowLayout();
                foreach (var row in spawnedRows)
                {
                    if (row == null) continue;
                    row.transform.localScale = Vector3.one;
                    var cg = row.GetComponent<CanvasGroup>();
                    if (cg != null) cg.alpha = 1f;
                }
                if (itemImage != null)
                {
                    itemImage.rectTransform.localScale = Vector3.one;
                    itemImage.rectTransform.anchoredPosition = itemHome;
                }
                PlayStampParticles();
            }
        }

        /// <summary>
        /// Payment taken, outcome unknown. This state exists so that never looks like a hang: it says what happened in
        /// the server's own words, offers a restore, and always leaves Continue reachable so nobody is trapped on a
        /// screen about money they have already spent.
        /// </summary>
        private void ShowProblem(string message)
        {
            juice?.Kill();
            foreach (var go in verifyingObjects) if (go != null) go.SetActive(false);
            foreach (var go in completeObjects) if (go != null) go.SetActive(false);
            StopParticles();
            if (errorView != null) errorView.SetActive(true);
            if (errorText != null) errorText.text = message ?? string.Empty;
            // Continue lives in the complete set, and it is the only way out of this state.
            foreach (var b in continueButtons) if (b != null) b.gameObject.SetActive(true);
            ClearRows();
            Problem?.Invoke();
        }

        /// <summary>
        /// Ask the store for its completed orders again.
        ///
        /// This is the one button here that DOES something, and what it does is re-drive: every order the store still
        /// holds goes back through the server's redeem, which is idempotent, so a purchase that was already granted
        /// pays nothing twice and one that was stranded finally lands.
        /// </summary>
        public void TryRestore()
        {
            var iap = IapService.Instance;
            if (iap == null) return;
            if (tryRestoreButton != null) tryRestoreButton.interactable = false;
            if (errorText != null) errorText.text = restoringText;

            iap.RestoreTransactions((ok, error) =>
            {
                if (this == null) return;
                if (tryRestoreButton != null) tryRestoreButton.interactable = true;
                // Success only means the STORE answered. Anything it handed back is being redeemed now, quietly — a
                // restored order was not bought in this session, so it credits the wallet without a ceremony.
                if (errorText != null)
                    errorText.text = ok ? restoreAskedText : (string.IsNullOrWhiteSpace(error) ? rejectedText : error);
            });
        }

        /// <summary>Show one set, hide the other. Objects the incoming state also wants are never hidden.</summary>
        private static void SetState(List<GameObject> show, List<GameObject> hide)
        {
            // Hidden FIRST, and never something the incoming state also wants — the two lists share objects (Continue
            // is in more than one), and hiding after showing would switch off what was just turned on.
            if (hide != null) foreach (var go in hide) if (go != null && !Contains(show, go)) go.SetActive(false);
            if (show != null) foreach (var go in show) if (go != null) go.SetActive(true);
        }

        private static bool Contains(List<GameObject> list, GameObject go)
            => list != null && list.Contains(go);

        // ================================================================== the celebration
        //
        // One sequence, in the order it is read:
        //
        //   1. the item EMERGES — swells up out of nothing, past its own size
        //   2. it STAMPS down onto its spot, and the impact punches and jolts
        //   3. on that settling, the particles go off — not before, or the impact has nothing left to cause
        //   4. the granted cards fly OUT OF THE MIDDLE OF THE ITEM, one at a time, each stamping into its slot
        //   5. when the last one has landed, every card bursts its own reward at its own counter
        //
        // The whole thing is a flourish over a payout that already happened. Every step is skippable, every ref is
        // optional, and Continue works at any point in it — a stuck celebration must never be able to trap someone on
        // a screen about money they have already spent.

        private void PlayCelebration()
        {
            juice?.Kill();
            var seq = DOTween.Sequence().SetUpdate(true);

            float stampAt = 0f;
            float stampEnd = 0f;

            // Put the item back where it belongs before ANYTHING is measured. A ceremony killed part-way through — the
            // player closed the panel mid-stamp — leaves it displaced and shrunk, and the next one must not read that
            // as its resting spot.
            if (itemImage != null)
            {
                itemImage.rectTransform.anchoredPosition = itemHome;
                itemImage.rectTransform.localScale = Vector3.one;
            }

            // Where the item COMES TO REST is where the cards are thrown from — not the raised spot it stamps down from.
            Vector3 origin = ItemCentreWorld();

            if (itemImage != null)
            {
                var t = itemImage.rectTransform;
                t.localScale = Vector3.one * Mathf.Max(0.01f, itemEmergeFromScale);
                t.anchoredPosition = itemHome + itemEmergeOffset;

                // 1 — emerge, deliberately PAST its size. The overshoot is the wind-up the stamp then spends.
                seq.Append(t.DOScale(Vector3.one * itemOvershootScale, itemEmergeSeconds).SetEase(Ease.OutCubic));

                // The HANG. A stamp is not one continuous motion — it rises, hesitates, then drops. Take the pause out
                // and the whole thing reads as a pop that happens to end smaller.
                if (itemAnticipateSeconds > 0f) seq.AppendInterval(itemAnticipateSeconds);

                // 2 — the slam, and it lands STRAIGHT INTO THE SQUASH rather than onto its own size. Arriving at 1.0
                //     and wobbling afterwards is a bounce; deforming ON CONTACT — wide and flat, in one accelerating
                //     move — is an impact. This is the single thing that separates a stamp from a scale-down.
                stampAt = itemEmergeSeconds + Mathf.Max(0f, itemAnticipateSeconds);
                float squash = Mathf.Max(0f, itemPunchScale);
                seq.Append(t.DOScale(new Vector3(1f + squash, 1f - squash, 1f), itemStampSeconds).SetEase(Ease.InQuad));
                if (itemEmergeOffset != Vector2.zero)
                    seq.Join(t.DOAnchorPos(itemHome, itemStampSeconds).SetEase(Ease.InQuad));
                stampEnd = stampAt + itemStampSeconds;

                // 3 — springing back out of the squash. Elastic, so it overshoots and rings down instead of easing
                //     politely to a stop; that ring-out is what the eye reads as weight.
                if (itemPunchSeconds > 0f)
                    seq.Append(t.DOScale(Vector3.one, itemPunchSeconds).SetEase(Ease.OutElastic, 1f, 0.45f));
            }

            // The jolt, and 3 — the particles, both hung off the moment of impact rather than off the emerge.
            if (stampShakeTarget != null && stampShakeStrength > 0f && stampShakeSeconds > 0f)
                seq.Insert(stampEnd, stampShakeTarget
                    .DOShakeAnchorPos(stampShakeSeconds, stampShakeStrength, vibrato: 12, randomness: 60f, fadeOut: true));
            seq.InsertCallback(stampEnd, () => { PlayStampParticles(); Stamped?.Invoke(); });

            // 4 — the cards, out of the middle of the item.
            float lastEnd = stampEnd + Mathf.Max(0f, itemPunchSeconds);
            var homes = FreezeRowLayout();

            for (int i = 0; i < spawnedRows.Count; i++)
            {
                var row = spawnedRows[i];
                if (row == null) continue;
                var rt = (RectTransform)row.transform;
                if (!row.TryGetComponent<CanvasGroup>(out var cg)) cg = row.gameObject.AddComponent<CanvasGroup>();

                // Both ends of the journey are world points, so nothing here depends on what the layout group chose
                // for this particular child's anchors.
                rt.position = origin;
                rt.localScale = Vector3.one * Mathf.Max(0.01f, cardFromScale);
                cg.alpha = 0f;

                float at = stampEnd + Mathf.Max(0f, cardsDelayAfterStamp) + i * Mathf.Max(0f, cardStagger);
                float landed = at + cardFlySeconds;
                float cardSquash = Mathf.Max(0f, cardPunchScale);

                int index = i;
                seq.InsertCallback(at, () => CardLaunched?.Invoke(index));
                seq.InsertCallback(landed, () => CardLanded?.Invoke(index));

                seq.Insert(at, rt.DOMove(homes[i], cardFlySeconds).SetEase(cardEase));
                seq.Insert(at, cg.DOFade(1f, cardFlySeconds * 0.5f).SetEase(Ease.OutQuad));

                // The card grows PAST its size on the way over, so it arrives carrying something to give up. A card
                // that scales straight to 1 has already finished by the time it lands, and lands with nothing.
                seq.Insert(at, rt.DOScale(Vector3.one * cardOvershootScale, cardFlySeconds).SetEase(cardEase));

                // The landing: squash on contact, then spring out of it. Same shape as the item's stamp, smaller.
                seq.Insert(landed, rt.DOScale(new Vector3(1f + cardSquash, 1f - cardSquash, 1f), cardStampSeconds)
                                     .SetEase(Ease.InQuad));
                if (cardPunchSeconds > 0f)
                    seq.Insert(landed + cardStampSeconds,
                               rt.DOScale(Vector3.one, cardPunchSeconds).SetEase(Ease.OutElastic, 1f, 0.45f));

                lastEnd = Mathf.Max(lastEnd, landed + cardStampSeconds + Mathf.Max(0f, cardPunchSeconds));
            }

            // 5 — everything has settled; the rewards leave.
            if (flyTrigger == FlyTrigger.AfterCardsSettle)
                seq.InsertCallback(lastEnd, FlyFromCards);

            juice = seq;
        }

        /// <summary>
        /// Every reward bursts out of ITS OWN card at ITS OWN counter.
        ///
        /// <see cref="RewardFly"/> already staggers one reward behind the next, so the cards empty in the order they
        /// landed without this having to time anything.
        /// </summary>
        private void FlyFromCards()
        {
            if (flyDone || closing || fly == null) return;
            var items = BuildFlyItems();
            if (items.Count == 0) return;
            flyDone = true;
            fly.Play(items, FlySource());
        }

        /// <summary>Stop and CLEAR, so a system with Play On Awake cannot fire the instant the complete state opens.</summary>
        private void StopParticles()
        {
            foreach (var ps in stampParticles)
                if (ps != null) ps.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private void PlayStampParticles()
        {
            foreach (var ps in stampParticles)
            {
                if (ps == null) continue;
                if (!ps.gameObject.activeSelf) ps.gameObject.SetActive(true);
                ps.Clear(withChildren: true);
                ps.Play(withChildren: true);
            }
        }

        /// <summary>The middle of the item art in world space — where the cards come out of.</summary>
        private Vector3 ItemCentreWorld()
        {
            var src = itemImage != null ? itemImage.rectTransform
                    : grantedGroup != null ? grantedGroup
                    : Root.transform as RectTransform;
            return src != null ? src.TransformPoint(src.rect.center) : Vector3.zero;
        }

        /// <summary>
        /// Measure where the rows belong, then switch the layout off for the flight.
        ///
        /// A layout group rewrites its children's positions every time it rebuilds, so it and a position tween cannot
        /// both be running — the group would drag every card back to its slot mid-flight. The homes are read once from
        /// a real rebuild, the group is frozen, and it comes back on when the rows are cleared, by which point the
        /// cards are already sitting exactly where it would have put them.
        /// </summary>
        private List<Vector3> FreezeRowLayout()
        {
            var homes = new List<Vector3>(spawnedRows.Count);
            if (grantedGroup == null)
            {
                foreach (var row in spawnedRows)
                    homes.Add(row != null ? row.transform.position : Vector3.zero);
                return homes;
            }

            // TWICE, deliberately. A ContentSizeFitter and a LayoutGroup on the same object need two passes: the first
            // gives the group its size, the second places the children against that size. One pass leaves the children
            // positioned for a width the group no longer has.
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(grantedGroup);
            LayoutRebuilder.ForceRebuildLayoutImmediate(grantedGroup);

            // WORLD space, not anchored. A layout group rewrites each child's anchors as it places them, so an anchored
            // home is only meaningful against the anchors that produced it; a world point means the same thing whatever
            // the layout did.
            foreach (var row in spawnedRows)
                homes.Add(row != null ? row.transform.position : Vector3.zero);

            // Only the group's OWN layout components — the rows have their own inner layouts and those must keep working.
            frozenLayout.Clear();
            foreach (var lg in grantedGroup.GetComponents<LayoutGroup>()) if (lg.enabled) { lg.enabled = false; frozenLayout.Add(lg); }
            foreach (var f in grantedGroup.GetComponents<ContentSizeFitter>()) if (f.enabled) { f.enabled = false; frozenLayout.Add(f); }
            return homes;
        }

        private void ThawRowLayout()
        {
            foreach (var c in frozenLayout) if (c != null) c.enabled = true;
            frozenLayout.Clear();
        }

        // ---------------------------------------------------------------- continue → close

        /// <summary>
        /// What Continue does: close, and — if the ceremony never got as far as sending them — let the rewards out on
        /// the way.
        ///
        /// It stays live through the whole celebration on purpose. This is a screen about money that has already left
        /// the player's account; nothing about dismissing it may wait on an animation.
        /// </summary>
        public void Continue()
        {
            if (closing) return;

            var items = !flyDone && flyTrigger != FlyTrigger.Never ? BuildFlyItems() : null;
            if (fly == null || items == null || items.Count == 0) { Hide(); return; }

            closing = true;
            flyDone = true;
            foreach (var b in continueButtons) if (b != null) b.interactable = false;
            if (group != null) group.blocksRaycasts = false;

            // The panel fades WITH the flight rather than after it, so the pieces are already crossing an emptying
            // screen by the time the counters take them.
            juice?.Kill();
            if (group != null) group.DOFade(0f, Mathf.Max(0.05f, fadeSeconds)).SetEase(Ease.InQuad).SetUpdate(true);

            fly.Play(items, FlySource(), Hide);

            // The flight is someone else's coroutine and its callback is not a guarantee. Nothing about dismissing a
            // paid-for purchase may depend on an effect reporting back.
            if (flyTimeoutSeconds > 0f)
                DOVirtual.DelayedCall(flyTimeoutSeconds, () => { if (this != null && closing) Hide(); }, ignoreTimeScale: true);
        }

        /// <summary>One flight per granted row, each leaving the row that is showing it.</summary>
        private List<RewardFlyItem> BuildFlyItems()
        {
            var items = new List<RewardFlyItem>();
            for (int i = 0; i < shownGrants.Count; i++)
            {
                var g = shownGrants[i];
                if (g == null || g.Amount <= 0m) continue;
                var row = i < spawnedRows.Count ? spawnedRows[i] : null;
                // No Icon and no To: RewardFly answers both from its own per-currency mapping, which is where "what a
                // chip looks like and where its counter is" belongs.
                items.Add(new RewardFlyItem
                {
                    RewardId = string.IsNullOrEmpty(g.Id) ? "Xp" : g.Id,
                    Amount = g.Amount,
                    Pieces = Mathf.Max(1, piecesPerLine),
                    From = row != null ? (RectTransform)row.transform : null,
                });
            }
            return items;
        }

        /// <summary>The fallback source, for a reward with no card of its own.</summary>
        private RectTransform FlySource()
        {
            if (flyFrom != null) return flyFrom;
            if (grantedGroup != null && grantedGroup.gameObject.activeInHierarchy) return grantedGroup;
            if (itemImage != null) return itemImage.rectTransform;
            return Root.transform as RectTransform;
        }

        public void Hide()
        {
            juice?.Kill();
            StopParticles();
            Handling = false;
            closing = false;
            flyDone = false;
            watchingProductId = null;
            // The card OBJECTS are deliberately left alone. A flight staggers one reward behind the next and each one
            // holds the card it leaves from — and Hide is what a finished flight calls, and what Continue calls while
            // an auto-flight may still be in the air. Destroying them here is a MissingReference on the money path's
            // last screen. They are cleared when the next purchase opens, behind a hidden panel either way.
            shownGrants.Clear();
            foreach (var b in continueButtons) if (b != null) b.interactable = true;
            if (group != null) { group.blocksRaycasts = false; group.alpha = 1f; }
            Root.SetActive(false);
        }

        // ---------------------------------------------------------------- content

        private void FillProduct(string productId)
        {
            if (!StoreCatalog.Instance.TryGet(productId, out var product) || product == null) return;

            foreach (var t in itemNameTexts) if (t != null) t.text = product.Title ?? "";

            if (itemImage == null) return;
            var url = product.Images != null && product.Images.Count > 0 ? product.Images[0] : null;
            if (string.IsNullOrWhiteSpace(url)) return;
            // Already cached in the shop that sold it, so this is usually instant; the callback guards against the
            // player closing the view while it is in flight.
            PlayCard.Core.RemoteImage.Load(url, sprite =>
            {
                if (this == null || itemImage == null || sprite == null) return;
                itemImage.sprite = sprite;
                itemImage.enabled = true;
            });
        }

        /// <summary>
        /// One row per reward the SERVER said it granted. <see cref="shownGrants"/> is filled in lockstep even when
        /// there is no row prefab, so the flight still knows what to send when the rows are not there to show it.
        /// </summary>
        private void SpawnRows(List<GrantedLineDto> grants)
        {
            ClearRows();
            if (grants == null) return;

            foreach (var g in grants)
            {
                if (g == null || g.Amount <= 0m) continue;
                if (hideXp && g.Kind == (int)RewardKind.Xp) continue;
                if (maxRows > 0 && shownGrants.Count >= maxRows) break;

                shownGrants.Add(g);
                if (rowPrefab == null || grantedGroup == null) continue;

                var row = Instantiate(rowPrefab, grantedGroup, false);
                row.transform.localScale = Vector3.one;
                row.Setup(IconFor(string.IsNullOrEmpty(g.Id) ? "Xp" : g.Id), g.Amount);
                spawnedRows.Add(row);
            }
        }

        private void ClearRows()
        {
            foreach (var row in spawnedRows) if (row != null) Destroy(row.gameObject);
            spawnedRows.Clear();
            shownGrants.Clear();
            ThawRowLayout();
        }

        private Sprite IconFor(string rewardId)
        {
            if (string.IsNullOrWhiteSpace(rewardId)) return null;
            foreach (var entry in icons)
                if (entry != null && string.Equals(entry.rewardId, rewardId, StringComparison.OrdinalIgnoreCase))
                    return entry.icon;
            return null;
        }
    }
}
