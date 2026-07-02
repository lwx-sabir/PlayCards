using System;
using System.Collections.Generic;
using PlayCard.Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The pending-reward inbox — passive rewards (level-up, gifts, achievements…) that "come here to collect". Fetches
    /// GET /api/rewards on enable, spawns a <see cref="RewardItemBinder"/> per reward; Collect credits the wallet
    /// server-side (idempotent) and re-pulls. Optional Collect-All + an unread badge/count. Pure VIEW over the server.
    /// </summary>
    public sealed class RewardInboxBinder : MonoBehaviour
    {
        [SerializeField] private Transform itemsContainer;
        [SerializeField] private RewardItemBinder itemPrefab;
        [SerializeField] private Button collectAllButton;
        [SerializeField] private GameObject emptyRoot;        // shown when there are no pending rewards
        [SerializeField] private GameObject badgeRoot;        // optional "you have rewards" badge
        [SerializeField] private TMP_Text countText;          // optional pending count
        [SerializeField] private TMP_Text messageText;

        private readonly List<RewardItemBinder> _rows = new List<RewardItemBinder>();
        private bool _busy;

        private void OnEnable()
        {
            if (collectAllButton != null) { collectAllButton.onClick.RemoveAllListeners(); collectAllButton.onClick.AddListener(CollectAll); }
            Refresh();
        }

        /// <summary>Re-pull pending rewards and repaint. Call after a level-up or returning to the screen.</summary>
        public async void Refresh()
        {
            try
            {
                var res = await BlackjackRestClient.Instance.GetRewardsAsync();
                if (res.Ok && res.Value != null) Render(res.Value);
            }
            catch (Exception e) { Debug.LogWarning($"[RewardInboxBinder] fetch failed: {e.Message}"); }
        }

        private void Render(List<RewardData> rewards)
        {
            for (int i = 0; i < _rows.Count; i++) if (_rows[i] != null) Destroy(_rows[i].gameObject);
            _rows.Clear();

            int n = rewards != null ? rewards.Count : 0;
            if (itemsContainer != null && itemPrefab != null && rewards != null)
            {
                foreach (var r in rewards)
                {
                    var row = Instantiate(itemPrefab, itemsContainer);
                    row.Setup(r, Collect);
                    _rows.Add(row);
                }
            }
            SetActiveSafe(emptyRoot, n == 0);
            SetActiveSafe(badgeRoot, n > 0);
            SetText(countText, n.ToString());
            if (collectAllButton != null) collectAllButton.interactable = n > 0;
        }

        public async void Collect(string rewardId)
        {
            if (_busy || string.IsNullOrEmpty(rewardId)) return;
            _busy = true; SetRowsInteractable(false);
            try
            {
                var res = await BlackjackRestClient.Instance.ClaimRewardAsync(rewardId);
                SetText(messageText, (res.Ok && res.Value != null && res.Value.Ok) ? "Collected!" : (res.Value?.Error ?? "Couldn't collect"));
            }
            catch (Exception e) { Debug.LogWarning($"[RewardInboxBinder] collect failed: {e.Message}"); }
            finally { _busy = false; Refresh(); }
        }

        public async void CollectAll()
        {
            if (_busy) return;
            _busy = true; SetRowsInteractable(false);
            try
            {
                var res = await BlackjackRestClient.Instance.ClaimAllRewardsAsync();
                SetText(messageText, (res.Ok && res.Value != null) ? $"Collected {res.Value.ClaimedCount}!" : "Couldn't collect");
            }
            catch (Exception e) { Debug.LogWarning($"[RewardInboxBinder] collect-all failed: {e.Message}"); }
            finally { _busy = false; Refresh(); }
        }

        private void SetRowsInteractable(bool on) { for (int i = 0; i < _rows.Count; i++) if (_rows[i] != null) _rows[i].SetInteractable(on); }
        private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
        private static void SetActiveSafe(GameObject go, bool on) { if (go != null && go.activeSelf != on) go.SetActive(on); }
    }
}
