using Khela.Common.Piggy;
using PlayCard.Piggy;
using UnityEngine;

namespace PlayCard.Store.Bridges
{
    /// <summary>
    /// Wires the piggy popup to the store. <c>PiggyPanel.BreakRequested(option)</c> → buy the piggy product for the
    /// player's current tier through <see cref="IapService"/>; the server verifies the receipt and pays the bank
    /// (<c>PiggyService.BreakVerifiedAsync</c>); on success the SERVER'S payout is handed to <c>PiggyBreakDirector</c> for
    /// the celebration, on anything else the popup is unlocked again (<c>PiggyPanel.CancelBreak</c>). Sits beside
    /// <c>PiggyPanel</c> on <c>Piggy_Canvas.prefab</c>; the references auto-find on the same object when left empty.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PiggyPurchaseBridge : MonoBehaviour
    {
        [SerializeField] private PiggyPanel panel;
        [SerializeField] private PiggyBreakDirector director;
        [Tooltip("Refresh the bank state from the server after a granted purchase (the redeem result already carries it; this is the belt to its braces).")]
        [SerializeField] private bool refreshStateAfterGrant = true;

        private bool _awaiting;   // a piggy purchase is in flight from THIS panel

        private void Awake()
        {
            if (panel == null) panel = GetComponent<PiggyPanel>();
            if (director == null) director = GetComponent<PiggyBreakDirector>();
        }

        private void OnEnable()
        {
            if (panel != null) panel.BreakRequested += OnBreakRequested;
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted += OnPurchaseCompleted;
        }

        private void OnDisable()
        {
            if (panel != null) panel.BreakRequested -= OnBreakRequested;
            if (IapService.Instance != null) IapService.Instance.OnPurchaseCompleted -= OnPurchaseCompleted;
        }

        private void OnBreakRequested(PiggyBreakOption option)
        {
            var iap = IapService.Instance;
            if (iap == null)
            {
                Debug.LogWarning($"{name}: no IapService — cannot buy the piggy.", this);
                panel?.CancelBreak();
                return;
            }
            // Late subscription: IapService may have bootstrapped after our OnEnable.
            iap.OnPurchaseCompleted -= OnPurchaseCompleted;
            iap.OnPurchaseCompleted += OnPurchaseCompleted;

            _awaiting = true;
            if (!iap.TryPurchasePiggy(option))
            {
                // refused up front (store not ready, already processing…) — the result was emitted and handled below
                _awaiting = false;
                panel?.CancelBreak();
            }
        }

        private void OnPurchaseCompleted(IapService.PurchaseResult result)
        {
            if (result == null) return;
            bool isPiggy = (result.redeem != null && result.redeem.Piggy != null)
                        || (!string.IsNullOrEmpty(result.productId) && result.productId.StartsWith("piggy_", System.StringComparison.OrdinalIgnoreCase));
            if (!isPiggy) return;
            if (!_awaiting && panel != null && !panel.isActiveAndEnabled) return;   // a re-driven pending order while the popup is closed: nothing to animate here
            _awaiting = false;

            switch (result.status)
            {
                case IapService.PurchaseStatus.Success:
                    var payout = result.redeem?.Piggy?.Amount ?? 0m;
                    if (director != null && payout > 0m)
                    {
                        // The SERVER'S figure, never a locally derived one.
                        director.PlayBreak(payout, () => { if (refreshStateAfterGrant) _ = PiggyState.Instance.RefreshAsync(force: true); });
                    }
                    else
                    {
                        panel?.CancelBreak();
                        _ = PiggyState.Instance.RefreshAsync(force: true);
                    }
                    break;

                case IapService.PurchaseStatus.Pending:
                case IapService.PurchaseStatus.Deferred:
                    // Paid (or awaiting approval) but not delivered yet: unlock the popup; the server/reconciler finishes it
                    // and the next refresh shows the new bank. The player must not be able to buy it twice meanwhile —
                    // the server's intent check refuses a second break on the same full bank.
                    panel?.CancelBreak();
                    _ = PiggyState.Instance.RefreshAsync(force: true);
                    break;

                default:
                    panel?.CancelBreak();
                    break;
            }
        }
    }
}
