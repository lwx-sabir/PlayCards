using System;
using PlayCard.Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The "Daily Mission Rewards" bundle panel: shows the complete-all chest's TITLE + DESCRIPTION (from the server),
    /// the Claim button (enabled only when EVERY daily mission is complete), the claimed state, and a live "Resets in
    /// HH:MM" countdown. On claim the server OPENS the chest and returns the rolled rewards (handled in
    /// MissionsPanelBinder). Bound by MissionsPanelBinder.
    /// </summary>
    public sealed class DailyBundleBinder : MonoBehaviour
    {
        [Header("Claim")]
        [SerializeField] private Button claimButton;
        [Tooltip("OPTIONAL small 'CLAIMED' overlay shown ONLY after the bundle is claimed — NOT the whole panel. " +
                 "Leave EMPTY if you don't have one (the Claim button just hides on claim). Never assign the panel root here.")]
        [SerializeField] private GameObject claimedRoot;

        [Header("Reset timer")]
        [SerializeField] private TMP_Text resetText;          // "23h 59m"
        [SerializeField] private string resetPrefix = "Resets in ";

        [Header("Chest text (optional — from the server's bundle chest)")]
        [SerializeField] private TMP_Text titleText;          // chest title (e.g. "Chips & Kash Chest")
        [SerializeField] private TMP_Text descriptionText;    // chest description

        private Action _onClaim;
        private DateTime _resetAtUtc;
        private bool _hasReset;

        public void Bind(DailyMissionsData d, Action onClaim)
        {
            _onClaim = onClaim;
            _resetAtUtc = d.ResetAtUtc;
            _hasReset = true;

            if (claimButton != null)
            {
                claimButton.interactable = d.BundleClaimable;
                claimButton.gameObject.SetActive(!d.BundleClaimed);
                claimButton.onClick.RemoveAllListeners();
                claimButton.onClick.AddListener(() => _onClaim?.Invoke());
            }
            SetActiveSafe(claimedRoot, d.BundleClaimed);

            SetText(titleText, d.BundleChestTitle);
            SetText(descriptionText, d.BundleChestDescription);
        }

        private void Update()
        {
            if (!_hasReset || resetText == null) return;
            var rem = _resetAtUtc - DateTime.UtcNow;
            if (rem.TotalSeconds < 0) rem = TimeSpan.Zero;
            resetText.text = rem.TotalHours >= 1
                ? $"{resetPrefix}{(int)rem.TotalHours}h {rem.Minutes:00}m"
                : $"{resetPrefix}{rem.Minutes:00}m {rem.Seconds:00}s";
        }

        private static void SetText(TMP_Text t, string s) { if (t != null && !string.IsNullOrEmpty(s)) t.text = s; }
        private static void SetActiveSafe(GameObject go, bool on) { if (go != null && go.activeSelf != on) go.SetActive(on); }
    }
}
