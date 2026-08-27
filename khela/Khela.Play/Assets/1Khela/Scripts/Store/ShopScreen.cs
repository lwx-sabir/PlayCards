using System.Collections.Generic;
using DG.Tweening;
using Khela.Common.Store;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Store
{
    /// <summary>
    /// The shop screen itself — the one thing above the lanes that talks to the server.
    ///
    /// Each <see cref="ShopSection"/> fills itself from whatever the catalog currently holds, and each
    /// <see cref="StorePurchaseButton"/> paints itself from its product. Nobody, though, was responsible for FETCHING
    /// the catalog when the screen opens, for the moment before the first answer arrives, or for the case where the
    /// store is off. That is this.
    ///
    /// Three states, one of them showing at a time: LOADING while the first fetch is in flight (a cached catalog from
    /// disk means it is usually skipped), UNAVAILABLE when the server's kill switch is off or this platform has no
    /// store, and the shop itself otherwise.
    ///
    /// It does not touch balances. A redeem goes through <c>BalanceChangingAsync</c>, so the wallet and every balance
    /// HUD repaint themselves; a shop that also pushed a number would be a second writer racing the first. What it
    /// DOES do after a purchase is re-fetch the catalog, because availability changed: a one-per-user pack is now
    /// owned, and only the server knows that.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShopScreen : MonoBehaviour
    {
        [Header("Roots — one shows at a time")]
        [Tooltip("The shop content: the lanes, the tabs, everything the player buys from.")]
        [SerializeField] private GameObject contentRoot;
        [Tooltip("Shown only while the FIRST catalog fetch is in flight. A cached catalog means it never appears.")]
        [SerializeField] private GameObject loadingRoot;
        [Tooltip("Shown when the store is off server-side, or this platform has no store (an editor build, a kill switch).")]
        [SerializeField] private GameObject unavailableRoot;
        [Tooltip("Optional. Why the shop is unavailable, in the player's terms.")]
        [SerializeField] private List<TMP_Text> unavailableTexts = new List<TMP_Text>();

        [Header("Open / close juice")]
        [Tooltip("The panel BODY that scales — the framed sheet, not the canvas root. Empty = the first child when this " +
                 "object is a Canvas (a Canvas rewrites its own scale, so it can never be animated).")]
        [SerializeField] private RectTransform openTarget;
        [Tooltip("The CanvasGroup that fades. Empty = found on the body, then this object, and added to the body if " +
                 "neither has one. It has to cover everything, or parts of the shop fade out of step.")]
        [SerializeField] private CanvasGroup fadeGroup;
        [Tooltip("Scale it opens FROM. Lower = more of a launch; too low reads as a zoom.")]
        [SerializeField] private float openFromScale = 0.82f;
        [Tooltip("How far BELOW its resting place the sheet starts, in canvas units. The rise separates an arrival " +
                 "from a plain pop.")]
        [SerializeField] private float openRise = 70f;
        [SerializeField] private float openSeconds = 0.34f;
        [Tooltip("How hard it overshoots past full size before settling. 0 = no overshoot.")]
        [Range(0f, 0.3f)][SerializeField] private float openOvershoot = 0.06f;
        [Tooltip("Closing is quicker than opening and moves as ONE piece — a staggered exit looks like the panel is " +
                 "falling apart on the way out.")]
        [SerializeField] private float closeSeconds = 0.18f;
        [SerializeField] private float closeToScale = 0.93f;
        [Tooltip("Dip the sheet DOWN as it goes, mirroring the rise on the way in. 0 = straight shrink.")]
        [SerializeField] private float closeDrop = 30f;

        [Header("Close")]
        [Tooltip("Everything that closes the shop — the back arrow, an X, a tap-catcher behind the panel. A list because " +
                 "a screen this size usually grows more than one way out, and each would otherwise need its own wiring.")]
        [SerializeField] private List<Button> backButtons = new List<Button>();

        [Header("Text")]
        [SerializeField] private string storeOffText = "The shop is closed right now. Please try again later.";
        [SerializeField] private string platformOffText = "Purchases aren't available on this device.";
        [SerializeField] private string offlineText = "Couldn't reach the shop. Check your connection and try again.";

        [Header("Behaviour")]
        [Tooltip("Re-fetch every time the screen opens rather than trusting the 60 s freshness window. On, because a sale " +
                 "that started or ended while the player was at a table should be on the card they are looking at.")]
        [SerializeField] private bool forceRefreshOnOpen = true;
        [Tooltip("After a purchase is GRANTED, re-fetch so per-user availability is right — a one-per-user pack that is now " +
                 "owned, a limit that is now reached.")]
        [SerializeField] private bool refreshAfterPurchase = true;

        private bool fetching;
        private bool everAnswered;

        /// <summary>How long the open tween runs, so anything timing itself against the panel can lead or trail it.</summary>
        public float OpenDuration => openSeconds;

        /// <summary>Raised as the shop opens, before the fetch — what a screen-open sound and any intro tween hang off.</summary>
        public event System.Action Opened;
        /// <summary>Raised as the shop closes, BEFORE it deactivates, so a close sound plays over the exit rather than
        /// after the object is already gone and nothing on it can run.</summary>
        public event System.Action Closing;

        /// <summary>
        /// The panel has finished ANIMATING open — not merely started. Raised from the open tween's completion, a
        /// frame or more after <see cref="Opened"/>, so anything that should arrive ON TOP of a settled shop waits
        /// for this instead. Always fires once per open, including when there is nothing to animate.
        /// </summary>
        public event System.Action OpenFinished;

        private void Awake()
        {
            foreach (var b in backButtons) if (b != null) b.onClick.AddListener(Close);
        }

        private void OnDestroy()
        {
            foreach (var b in backButtons) if (b != null) b.onClick.RemoveListener(Close);
        }

        /// <summary>
        /// Close the shop, as ONE piece. The panel is kept alive rather than destroyed, so reopening is instant.
        ///
        ///
        /// Nothing staggers on the way out: a panel that cascades OUT looks like it is coming apart, and the eye reads
        /// the lag between the frame and its contents as a fault. One fade plus one scale on the same clock is what
        /// reads as decisive.
        /// </summary>
        public void Close()
        {
            if (!gameObject.activeSelf) return;

            // Before anything can deactivate this object — a listener living on it (the shop's audio) must still be
            // alive to hear it.
            Closing?.Invoke();

            var sheet = Body();
            if (sheet == null || closeSeconds <= 0f) { gameObject.SetActive(false); return; }

            tween?.Kill();

            // Dead to input the instant it is dismissed: a tap landing on a panel that is visibly leaving is a bug the
            // player will blame on lag.
            var group = Fade();
            if (group != null) { group.interactable = false; group.blocksRaycasts = false; }

            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Join(sheet.DOScale(restScale * closeToScale, closeSeconds).SetEase(Ease.InBack, 1.1f));
            if (closeDrop != 0f)
                seq.Join(sheet.DOLocalMoveY(restPos.y - closeDrop, closeSeconds).SetEase(Ease.InCubic));
            // Shorter than the movement, so the sheet is gone before it finishes travelling and the eye never watches
            // the last frames of the shrink.
            if (group != null) seq.Join(group.DOFade(0f, closeSeconds * 0.8f).SetEase(Ease.InQuad));

            // Deactivated only once it has gone: disabling on the first frame is what makes a panel blink shut.
            seq.OnComplete(() => { if (this != null) gameObject.SetActive(false); });
            tween = seq;
        }

        // ---------------------------------------------------------------- open / close juice

        private RectTransform body;
        private bool bodyResolved;
        private Vector3 restScale = Vector3.one;
        private Vector3 restPos;
        private bool restCaptured;
        private Sequence tween;
        private Coroutine openRoutine;

        private RectTransform Body()
        {
            if (bodyResolved) return body;
            bodyResolved = true;

            body = openTarget;
            if (body == null)
            {
                // A Canvas has its scale rewritten by its own CanvasScaler every frame, so animating one does nothing.
                bool isCanvas = GetComponent<Canvas>() != null;
                if (!isCanvas) body = transform as RectTransform;
                else
                {
                    for (int i = 0; i < transform.childCount && body == null; i++)
                        body = transform.GetChild(i) as RectTransform;
                    if (body == null)
                        Debug.LogWarning($"{name}: nothing to animate — this object is a Canvas with no child to scale. " +
                                         "Assign Open Target.", this);
                }
            }
            if (body != null && !restCaptured)
            {
                restCaptured = true;
                restScale = body.localScale;
                restPos = body.localPosition;
            }
            return body;
        }

        private CanvasGroup Fade()
        {
            if (fadeGroup != null) return fadeGroup;
            var b = Body();
            if (b == null) return null;
            // TryGetComponent, never `GetComponent() ?? AddComponent()`. `??` ignores Unity's overloaded null: a missing
            // component returns a live C# wrapper around a dead native pointer, so the fallback never runs and the first
            // use throws MissingComponentException.
            if (!b.TryGetComponent(out fadeGroup) && !TryGetComponent(out fadeGroup))
                fadeGroup = b.gameObject.AddComponent<CanvasGroup>();
            return fadeGroup;
        }

        /// <summary>Pose it where the open starts and hide it, without animating — so the expensive first build happens
        /// behind an invisible panel rather than a half-drawn one.</summary>
        private void PrepareOpen()
        {
            var b = Body();
            if (b == null) return;
            tween?.Kill();
            b.localScale = restScale * openFromScale;
            b.localPosition = new Vector3(restPos.x, restPos.y - openRise, restPos.z);
            var group = Fade();
            if (group != null) { group.alpha = 0f; group.interactable = false; group.blocksRaycasts = false; }
        }

        /// <summary>
        /// The open tween, started a frame LATE on purpose.
        ///
        /// Opening the shop instantiates every card in every lane and forces a canvas rebuild — one long frame. A tween
        /// started before that sits frozen through the hitch and then jumps, so the first third never draws and the
        /// screen looks like it ignored the tap. Posed hidden, built while invisible, animated on a clean frame.
        /// </summary>
        private System.Collections.IEnumerator PlayOpenNextFrame()
        {
            yield return null;
            openRoutine = null;

            var b = Body();
            // Still "finished opening" — there was simply nothing to animate. Anything waiting on this must not be
            // stranded because the panel had no body to tween.
            if (b == null) { OpenFinished?.Invoke(); yield break; }

            var group = Fade();
            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Join(b.DOScale(restScale, openSeconds)
                      .SetEase(openOvershoot > 0f ? Ease.OutBack : Ease.OutCubic, 1f + openOvershoot * 10f));
            seq.Join(b.DOLocalMove(restPos, openSeconds).SetEase(Ease.OutCubic));
            if (group != null) seq.Join(group.DOFade(1f, openSeconds * 0.6f).SetEase(Ease.OutQuad));
            seq.OnComplete(() =>
            {
                if (this == null) return;
                var g = Fade();
                if (g != null) { g.interactable = true; g.blocksRaycasts = true; }
                OpenFinished?.Invoke();
            });
            tween = seq;
        }

        private void OnEnable()
        {
            if (StoreCatalog.Instance != null) StoreCatalog.Instance.Changed += HandleCatalogChanged;
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted += HandlePurchaseCompleted;
            Open();
        }

        private void OnDisable()
        {
            if (StoreCatalog.Instance != null) StoreCatalog.Instance.Changed -= HandleCatalogChanged;
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted -= HandlePurchaseCompleted;
        }

        /// <summary>Open (or re-open) the shop: show what we already have, then go and check.</summary>
        public void Open()
        {
            if (!gameObject.activeSelf)
            {
                // OnEnable calls straight back here, so this returns and lets it do the work once rather than twice.
                gameObject.SetActive(true);
                return;
            }

            // Posed hidden BEFORE the catalog work, so the cards are built behind an invisible panel.
            PrepareOpen();

            var catalog = StoreCatalog.Instance;
            if (catalog != null)
            {
                // The disk cache is what makes the shop feel instant: last session's product list is on screen before the
                // API answers. Prices still come from the store, availability still from the fetch below.
                catalog.LoadCached();
                if (catalog.Loaded) everAnswered = true;
            }
            // The fetch is kicked FIRST and the state painted after: FetchAsync marks itself in flight synchronously,
            // before its first await, so this paints LOADING. Painting first would read "not loaded, not fetching" and
            // flash "couldn't reach the shop" for a frame every single time the shop opens.
            _ = FetchAsync();
            ApplyState();
            Opened?.Invoke();

            if (openRoutine != null) StopCoroutine(openRoutine);
            openRoutine = StartCoroutine(PlayOpenNextFrame());
        }

        /// <summary>Fetch the catalog, coalescing with anything already in flight.</summary>
        public async System.Threading.Tasks.Task FetchAsync()
        {
            var catalog = StoreCatalog.Instance;
            if (catalog == null || fetching) return;
            fetching = true;
            try
            {
                await catalog.RefreshAsync(force: forceRefreshOnOpen);
                everAnswered = everAnswered || catalog.Loaded;
            }
            finally
            {
                fetching = false;
                ApplyState();
            }
        }

        private void HandleCatalogChanged(StoreCatalogDto _)
        {
            everAnswered = true;
            ApplyState();
        }

        private void HandlePurchaseCompleted(IapService.PurchaseResult result)
        {
            if (!refreshAfterPurchase || result == null) return;
            // Only a GRANTED purchase changes what the shop may sell. A cancel or a failure leaves the catalog exactly as
            // it was, and re-fetching on those would put a spinner in front of a player who just backed out of a sheet.
            if (result.status != IapService.PurchaseStatus.Success) return;
            _ = FetchAsync();
        }

        private void ApplyState()
        {
            var catalog = StoreCatalog.Instance;
            bool loaded = catalog != null && catalog.Loaded;
            bool loading = !loaded && fetching;
            bool usable = loaded && catalog.Enabled && catalog.PlatformEnabled;

            if (loadingRoot != null) loadingRoot.SetActive(loading);
            if (contentRoot != null) contentRoot.SetActive(usable);
            if (unavailableRoot != null) unavailableRoot.SetActive(!usable && !loading);

            if (!usable && !loading && unavailableTexts.Count > 0)
            {
                // Three different failures, three different things to tell the player: we never got an answer, the shop is
                // shut, or this build simply has no store behind it.
                string reason = !everAnswered ? offlineText
                    : catalog != null && !catalog.Enabled ? storeOffText
                    : platformOffText;
                foreach (var t in unavailableTexts) if (t != null) t.text = reason;
            }
        }
    }
}
