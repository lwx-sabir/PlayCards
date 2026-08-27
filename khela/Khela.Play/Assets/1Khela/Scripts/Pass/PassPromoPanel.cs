using System;
using System.Collections.Generic;
using System.Globalization;
using DG.Tweening;
using Khela.Common.Pass;
using Khela.Common.Rewards;
using PlayCard.Store;
using Sonity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Pass
{
    /// <summary>
    /// The pitch for the pass: what the golden track pays over the whole cycle, added up.
    ///
    /// The ladder answers "what do I get today"; nobody scrolls thirty-one cards adding chips in their head. This is
    /// the only place the offer is stated as a total, which is the number the price gets weighed against.
    ///
    /// The rows are AUTHORED — each one points at its own label. Only the numbers are computed, from the live ladder,
    /// so re-tuning a rung in the admin updates the pitch without anyone editing text. The server also trims the
    /// ladder to the days the cycle really has, so a February total is February's rather than a promise of three days
    /// nobody can reach.
    ///
    /// It does not buy anything: it raises <see cref="SubscribeRequested"/> and something else owns the store call.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PassPromoPanel : MonoBehaviour
    {
        /// <summary>One authored row, tied to one reward.</summary>
        [Serializable]
        public sealed class RewardRow
        {
            [Tooltip("Chips · Kash · Xp · tournament_ticket — matched case-insensitively against the ladder's ids.")]
            public string rewardId;
            [Tooltip("The row's number.")]
            public TMP_Text amountText;
            [Tooltip("The whole row, hidden when the pass pays none of this. Empty = the label is hidden on its own.")]
            public GameObject row;
            [Tooltip("Optional, only needed when the icon is NOT inside Row — it is hidden and shown with the rest.")]
            public Image icon;
            [Tooltip("{0} is the total, thousands-separated.")]
            public string format = "+{0}";
        }

        [Header("Root")]
        [Tooltip("The whole popup. Empty = this object. Switched off when closed.")]
        [SerializeField] private GameObject root;

        [Header("Rewards — one row per thing the pass pays")]
        [SerializeField] private List<RewardRow> rows = new List<RewardRow>();
        [SerializeField] private string amountFormat = "#,0";

        [Header("Price")]
        [SerializeField] private TMP_Text priceText;
        [Tooltip("Used until the store answers with a real localized price. {0} is the server's USD reference.")]
        [SerializeField] private string priceFallbackFormat = "US ${0:0.00}";

        [Header("Buttons")]
        [SerializeField] private Button buyButton;
        [SerializeField] private Button closeButton;

        [Header("Sound")]
        [Tooltip("The popup ARRIVING. Fires with the open, alongside the tween rather than after it.")]
        [SerializeField] private SoundEvent openSound;
        [Tooltip("The popup LEAVING. Fires as the close STARTS, so it plays over the exit instead of after the object " +
                 "is already gone and nothing on it can run.")]
        [SerializeField] private SoundEvent closeSound;
        [Tooltip("Play the close sound when the BUY button dismisses it too. Off by default — a purchase has its own " +
                 "sound, and hearing the panel shut on top of it reads as two things happening.")]
        [SerializeField] private bool closeSoundOnBuy;

        [Header("Juice")]
        [SerializeField] private float openSeconds = 0.28f;
        [SerializeField] private float openFromScale = 0.85f;
        [SerializeField] private float closeSeconds = 0.16f;

        /// <summary>The player wants it. Something else does the buying — this panel never touches the store.</summary>
        public event Action SubscribeRequested;

        public bool IsOpen => Root.activeSelf;

        /// <summary>
        /// The live pitch, so anything can open it without holding a reference — it is a screen of its own, not a
        /// widget belonging to whichever panel happened to spawn it. Null until one exists.
        ///
        /// Last one to wake wins. There should only ever be one; if a scene copy and a spawned copy both exist, the
        /// scene one is the mistake.
        /// </summary>
        public static PassPromoPanel Current { get; private set; }

        /// <summary>Open the pitch from anywhere. False when none has been created yet — the caller decides whether
        /// that is worth spawning one for.</summary>
        public static bool OpenIfAvailable()
        {
            if (Current == null) return false;
            Current.Show();
            return true;
        }

        private CanvasGroup group;
        private Sequence tween;
        private bool inited;
        /// <summary>True across the activation inside Show — see Awake.</summary>
        private bool opening;

        private GameObject Root => root != null ? root : gameObject;

        private void Awake()
        {
            Init();

            // Do NOT fight an open that is already under way. This component lives ON the popup, so when the popup is
            // authored inactive, the SetActive(true) inside Show() is what runs Awake for the very first time — and
            // hiding unconditionally here would shut the popup on the frame it first opened, sound and all, with
            // nothing ever appearing. Every later open worked, which is what made it look like a one-off glitch.
            if (!opening) Root.SetActive(false);
        }

        /// <summary>
        /// Resolve refs and wire the buttons, once — and from Show() as well as Awake.
        ///
        /// This component is meant to live ON the popup it hides, which is the natural way to author one: the whole
        /// thing, dim included, is one object that goes away. But that means Awake has not necessarily run by the time
        /// something calls Show() — a popup left INACTIVE in the scene has never woken at all. Calling a public method
        /// on it still works, so the only thing missing is the setup, and doing it here rather than relying on Awake is
        /// what lets the popup be authored either way.
        /// </summary>
        private void Init()
        {
            Current = this;
            if (inited) return;
            inited = true;

            group = Root.GetComponent<CanvasGroup>();
            if (group == null) group = Root.AddComponent<CanvasGroup>();

            if (buyButton != null) buyButton.onClick.AddListener(OnBuy);
            if (closeButton != null) closeButton.onClick.AddListener(Close);
        }

        private void OnDestroy()
        {
            if (buyButton != null) buyButton.onClick.RemoveListener(OnBuy);
            if (closeButton != null) closeButton.onClick.RemoveListener(Close);
            if (Current == this) Current = null;
        }

        /// <summary>
        /// Open it, filled from whatever the pass currently is. Safe to call from anywhere. Does nothing when the pass
        /// is off or the player already owns it — there is nothing to sell.
        /// </summary>
        public void Show()
        {
            var state = PassState.Instance != null ? PassState.Instance.Current : null;
            if (state == null || !state.Active || state.IsGolden) return;

            Init();
            Fill(state);

            if (openSound != null) openSound.UIPlay();

            opening = true;
            Root.SetActive(true);   // may run Awake for the first time
            opening = false;

            tween?.Kill();
            group.alpha = 0f;
            group.blocksRaycasts = true;
            var rect = Root.transform as RectTransform;
            if (rect != null) rect.localScale = Vector3.one * openFromScale;

            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Join(group.DOFade(1f, openSeconds).SetEase(Ease.OutQuad));
            if (rect != null && openFromScale != 1f)
                seq.Join(rect.DOScale(Vector3.one, openSeconds).SetEase(Ease.OutBack, 1.5f));
            tween = seq;
        }

        public void Close() => Close(silent: false);

        private void Close(bool silent)
        {
            if (!Root.activeSelf) return;
            if (!silent && closeSound != null) closeSound.UIPlay();
            tween?.Kill();
            group.blocksRaycasts = false;
            if (closeSeconds <= 0f) { Root.SetActive(false); return; }

            var rect = Root.transform as RectTransform;
            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Join(group.DOFade(0f, closeSeconds).SetEase(Ease.InQuad));
            if (rect != null && openFromScale != 1f)
                seq.Join(rect.DOScale(Vector3.one * openFromScale, closeSeconds).SetEase(Ease.InQuad));
            seq.OnComplete(() => { if (this != null) Root.SetActive(false); });
            tween = seq;
        }

        private void OnBuy()
        {
            SubscribeRequested?.Invoke();
            Close(silent: !closeSoundOnBuy);
        }

        // ---------------------------------------------------------------- the numbers

        private void Fill(PassStateDto state)
        {
            var totals = Total(state);

            foreach (var r in rows)
            {
                if (r == null) continue;
                decimal amount = 0m;
                if (!string.IsNullOrWhiteSpace(r.rewardId)) totals.TryGetValue(r.rewardId, out amount);

                bool has = amount > 0m;
                if (r.row != null) r.row.SetActive(has);
                else if (r.amountText != null) r.amountText.gameObject.SetActive(has);
                if (r.icon != null) r.icon.gameObject.SetActive(has);

                if (!has || r.amountText == null) continue;
                var shown = amount.ToString(amountFormat, CultureInfo.InvariantCulture);
                r.amountText.text = SafeFormat(string.IsNullOrWhiteSpace(r.format) ? "+{0}" : r.format, shown);
            }

            // The STORE's own localized price wins — it is what the player is actually charged, in their currency.
            // The server's USD reference is a fallback for before the store has answered, never a claim.
            if (priceText == null) return;
            string price = null;
            var iap = IapService.Instance;
            var productId = StoreCatalog.Instance != null ? StoreCatalog.Instance.GoldenPassProductId : null;
            if (iap != null && !string.IsNullOrWhiteSpace(productId)) price = iap.GetLocalizedPriceString(productId, null);
            if (string.IsNullOrWhiteSpace(price) && state.GoldenPriceUsd > 0m)
                price = string.Format(CultureInfo.InvariantCulture, priceFallbackFormat, state.GoldenPriceUsd);
            if (!string.IsNullOrWhiteSpace(price)) priceText.text = price;
        }

        /// <summary>Add the golden track up, keyed by what each reward IS.</summary>
        private static Dictionary<string, decimal> Total(PassStateDto state)
        {
            var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in state.Nodes ?? new List<PassNodeDto>())
            {
                if (node?.Golden == null) continue;
                foreach (var line in node.Golden)
                {
                    if (line == null || line.Amount <= 0m) continue;
                    // XP carries a KIND and no id; everything else is keyed by its own id.
                    var key = line.Kind == RewardKind.Xp ? "Xp"
                            : string.IsNullOrWhiteSpace(line.Id) ? line.Kind.ToString()
                            : line.Id;
                    totals.TryGetValue(key, out var running);
                    totals[key] = running + line.Amount;
                }
            }
            return totals;
        }

        /// <summary>A bad format string in the inspector shows the plain number rather than throwing at the player.</summary>
        private static string SafeFormat(string format, string amount)
        {
            try { return string.Format(CultureInfo.InvariantCulture, format, amount); }
            catch (FormatException) { return amount; }
        }
    }
}
