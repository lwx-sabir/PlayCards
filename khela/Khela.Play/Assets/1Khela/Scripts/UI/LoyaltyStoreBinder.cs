using System;
using System.Collections.Generic;
using PlayCard.Game.Net;
using TMPro;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Drives the Loyalty store screen. Fetches GET /api/loyalty on enable, paints the LP balance, and spawns one
    /// <see cref="LoyaltyItemView"/> per catalog item into <see cref="itemsContainer"/>. Redeem is server-idempotent on
    /// a per-tap key; the binder also guards against double-tap (one redeem in flight at a time) and re-pulls the store
    /// after each redeem so balances + affordability stay correct.
    ///
    /// Pure VIEW over the server — it never holds an authoritative balance and never grants chips itself; the server
    /// debits LP + credits chips and returns the new balance, which we display by re-fetching. Assign only the fields
    /// your layout has (all null-guarded).
    /// </summary>
    public sealed class LoyaltyStoreBinder : MonoBehaviour
    {
        [Header("Balance")]
        [SerializeField] private TMP_Text pointsText;          // current LP balance
        [SerializeField] private TMP_Text lifetimePointsText;  // optional — lifetime LP earned

        [Header("Catalog")]
        [Tooltip("Parent for the spawned item views (e.g. a Vertical/Grid Layout Group content rect).")]
        [SerializeField] private Transform itemsContainer;
        [Tooltip("The LoyaltyItemView prefab instantiated once per store item.")]
        [SerializeField] private LoyaltyItemView itemPrefab;

        [Header("Feedback (optional)")]
        [SerializeField] private TMP_Text messageText;         // redeem result / error line
        [SerializeField] private string moneyFormat = "#,0";

        private readonly List<LoyaltyItemView> _spawned = new List<LoyaltyItemView>();
        private bool _busy;

        private void OnEnable() => Refresh();

        /// <summary>Re-pull the store (balance + catalog) and repaint. Safe to call after a redeem or a wallet change.</summary>
        public async void Refresh()
        {
            try
            {
                var res = await BlackjackRestClient.Instance.GetLoyaltyStoreAsync();
                if (res.Ok && res.Value != null) Render(res.Value);
                else Debug.LogWarning("[LoyaltyStoreBinder] store fetch returned no data");
            }
            catch (Exception e) { Debug.LogWarning($"[LoyaltyStoreBinder] store fetch failed: {e.Message}"); }
        }

        private void Render(LoyaltyStoreData store)
        {
            SetText(pointsText, store.Points.ToString(moneyFormat));
            SetText(lifetimePointsText, store.LifetimePoints.ToString(moneyFormat));

            // Rebuild the list (simple + robust; the catalog is small and only changes on tier/balance shifts).
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null) Destroy(_spawned[i].gameObject);
            _spawned.Clear();

            if (itemsContainer == null || itemPrefab == null || store.Items == null) return;
            foreach (var item in store.Items)
            {
                var view = Instantiate(itemPrefab, itemsContainer);
                view.Setup(item, Buy);
                _spawned.Add(view);
            }
        }

        /// <summary>Redeem an item by id. One redeem at a time; the server is idempotent on the per-tap key, and we
        /// re-fetch after to reflect the authoritative balance.</summary>
        public async void Buy(string itemId)
        {
            if (_busy || string.IsNullOrEmpty(itemId)) return;
            _busy = true;
            SetItemsInteractable(false);
            try
            {
                var idem = Guid.NewGuid().ToString("N");   // one stable key per tap (retry-safe on the server)
                var res = await BlackjackRestClient.Instance.RedeemLoyaltyAsync(itemId, idem);
                if (res.Ok && res.Value != null)
                {
                    SetText(messageText, res.Value.Ok
                        ? $"Redeemed! +{res.Value.ChipAmount.ToString(moneyFormat)} chips"
                        : (string.IsNullOrEmpty(res.Value.Error) ? "Redeem failed" : res.Value.Error));
                }
                else SetText(messageText, "Network error — try again");
            }
            catch (Exception e)
            {
                SetText(messageText, "Error — try again");
                Debug.LogWarning($"[LoyaltyStoreBinder] redeem failed: {e.Message}");
            }
            finally
            {
                _busy = false;
                Refresh();   // re-pull balance + affordability (also re-enables buttons via a fresh Setup())
            }
        }

        private void SetItemsInteractable(bool on)
        {
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null) _spawned[i].SetBuyInteractable(on);
        }

        private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
    }
}
