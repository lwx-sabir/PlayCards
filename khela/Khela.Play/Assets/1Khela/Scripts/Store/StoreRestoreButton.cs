using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Store
{
    /// <summary>
    /// The store-mandated "Restore purchases" button.
    ///
    /// Apple requires a visible restore affordance in any app that sells a non-consumable or a subscription — the golden
    /// pass is both reasons at once — and a review will be rejected without one. It matters to the player too: a
    /// reinstall, a new device, or a purchase whose receipt never reached us leaves an entitlement they paid for and
    /// cannot see. <see cref="IapService.RestoreTransactions"/> asks the store to hand the completed orders back, and
    /// each one is re-driven through the server's redeem, which is idempotent — so restoring twice pays nothing twice.
    ///
    /// Consumables (chips, Kash, VIP-P) are NOT restored by the stores; they are already banked in the wallet. What
    /// comes back is the pass, and any order that was paid but never granted.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StoreRestoreButton : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] private Button restoreButton;
        [Tooltip("Shown while the store's restore sheet is up and the orders are coming back.")]
        [SerializeField] private GameObject busyRoot;
        [Tooltip("Hidden while busy, so the label does not sit under the spinner.")]
        [SerializeField] private GameObject labelRoot;
        [Tooltip("The outcome, in the player's terms. Optional.")]
        [SerializeField] private List<TMP_Text> statusTexts = new List<TMP_Text>();

        [Header("Text")]
        [SerializeField] private string busyText = "Restoring...";
        [SerializeField] private string doneText = "Purchases restored.";
        [SerializeField] private string failedText = "Couldn't restore right now. Please try again.";
        [SerializeField] private string notReadyText = "The store isn't ready yet.";
        [Tooltip("Seconds the outcome stays on screen before the label goes back to normal; 0 = leave it up.")]
        [SerializeField] private float statusSeconds = 4f;

        [Header("Visibility")]
        [Tooltip("Optional. An object hidden entirely when there is no store (an editor build, a kill switch). Leave empty " +
                 "to just grey the button out, which is usually kinder: a restore control that disappears is a support " +
                 "ticket, and a disabled one at least explains itself.\n\n" +
                 "It must NOT be this object — a disabled GameObject stops running, so nothing would be left to notice the " +
                 "store coming up and turn it back on.")]
        [SerializeField] private GameObject hideWhenNoStoreRoot;

        private bool busy;
        private float statusUntil;
        private bool warnedSelfHide;

        private void Awake()
        {
            if (restoreButton == null) restoreButton = GetComponent<Button>();
            if (restoreButton != null) restoreButton.onClick.AddListener(Restore);
            SetStatus("");
            ApplyState();
        }

        private void OnDestroy()
        {
            if (restoreButton != null) restoreButton.onClick.RemoveListener(Restore);
        }

        private void OnEnable()
        {
            busy = false;
            SetStatus("");
            ApplyState();
        }

        private void Update()
        {
            // The only thing that needs a tick: clearing the outcome line after a few seconds.
            if (statusSeconds > 0f && statusUntil > 0f && Time.unscaledTime >= statusUntil)
            {
                statusUntil = 0f;
                SetStatus("");
            }
        }

        /// <summary>Wired to the button, and callable from a menu item.</summary>
        public void Restore()
        {
            if (busy) return;
            var iap = IapService.Instance;
            if (iap == null || !iap.IsReady)
            {
                Flash(notReadyText);
                return;
            }

            busy = true;
            ApplyState();
            SetStatus(busyText);

            iap.RestoreTransactions((success, error) =>
            {
                busy = false;
                // The callback can land after the screen closed — this object may be gone, and touching it would throw
                // inside the store's own callback, where nothing is listening.
                if (this == null || !isActiveAndEnabled) return;
                ApplyState();
                // The store gives a reason often enough to be worth showing; fall back to our own line when it does not.
                if (!success) { Flash(string.IsNullOrWhiteSpace(error) ? failedText : error); return; }
                // Success only means the store answered. Whether anything came back arrives through OnPurchasesFetched
                // and is granted by the server; the honest thing to say here is that we asked and it worked.
                Flash(doneText);
            });
        }

        private void Flash(string text)
        {
            SetStatus(text);
            statusUntil = statusSeconds > 0f ? Time.unscaledTime + statusSeconds : 0f;
        }

        private void SetStatus(string text)
        {
            foreach (var t in statusTexts) if (t != null) t.text = text ?? "";
        }

        private void ApplyState()
        {
            var iap = IapService.Instance;
            bool storeUp = iap != null && iap.IsReady;

            if (busyRoot != null) busyRoot.SetActive(busy);
            if (labelRoot != null) labelRoot.SetActive(!busy);
            if (restoreButton != null) restoreButton.interactable = storeUp && !busy;

            if (hideWhenNoStoreRoot == null) return;
            if (hideWhenNoStoreRoot == gameObject)
            {
                if (!warnedSelfHide)
                {
                    warnedSelfHide = true;
                    Debug.LogWarning($"[StoreRestoreButton] {name}: the hide root is this object. Hiding it would stop this " +
                                     "component running, and nothing would turn it back on when the store comes up — ignored.", this);
                }
                return;
            }
            hideWhenNoStoreRoot.SetActive(storeUp);
        }
    }
}
