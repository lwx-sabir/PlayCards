using System;
using PlayCard.Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// Binds the player's VIP status (GET /api/vip/me) to a badge + optional detail panel. Pure VIEW: it fetches on
    /// enable and repaints; it never mutates state except the explicit "hide my badge" toggle. Assign only the fields
    /// your layout has — every binding is null-guarded, so unassigned slots are skipped.
    ///
    /// The BADGE (tier crest + name) shows only when the player actually has one (Silver+), it's LIT (recent activity
    /// within the badge window), and they haven't hidden it. Bronze is the floor — no badge. The benefit multiplier is
    /// the Loyalty/store boost for the tier.
    /// </summary>
    public sealed class VipBadgeBinder : MonoBehaviour
    {
        [Header("Badge")]
        [Tooltip("The whole badge visual — shown when the player has a VIP LEVEL (or a lit tier badge) and hasn't hidden it.")]
        [SerializeField] private GameObject badgeRoot;
        [Tooltip("The badge crest Image — its sprite is chosen by VIP LEVEL from Vip Level Icons.")]
        [SerializeField] private Image badgeIcon;
        [Tooltip("Badge crest per VIP LEVEL: Element 0 = VIP 1, Element 1 = VIP 2 … Element 9 = VIP 10. " +
                 "The badge reflects the VIP-LEVEL ladder (1–10), NOT the tier — the tier shows as text.")]
        [FormerlySerializedAs("tierIcons")]
        [SerializeField] private Sprite[] vipLevelIcons;
        [SerializeField] private TMP_Text tierNameText;
        [Tooltip("VIP Level headline, e.g. \"VIP 7\". Blank at VIP 0.")]
        [SerializeField] private TMP_Text vipLevelText;

        [Header("Detail (optional)")]
        [SerializeField] private TMP_Text statusPointsText;   // trailing-window SP
        [SerializeField] private TMP_Text multiplierText;     // Loyalty/store boost, e.g. "×2.2"
        [Tooltip("Progress toward the next tier's SP bar.")]
        [SerializeField] private Slider nextTierProgress;
        [SerializeField] private TMP_Text nextTierText;       // "12,345 SP to Gold" / "Top tier"

        [Header("Formatting")]
        [SerializeField] private string moneyFormat = "#,0";
        [SerializeField] private string multiplierFormat = "0.0#";   // rendered as "×{value}"

        private void OnEnable() => Refresh();

        /// <summary>Re-pull VIP status from the server and repaint.</summary>
        public async void Refresh()
        {
            try
            {
                var res = await BlackjackRestClient.Instance.GetMyVipStatusAsync();
                if (res.Ok && res.Value != null) Render(res.Value);
            }
            catch (Exception e) { Debug.LogWarning($"[VipBadgeBinder] VIP fetch failed: {e.Message}"); }
        }

        /// <summary>Toggle the "hide my VIP badge from others" opt-out, then repaint. Wire to a UI toggle.</summary>
        public async void SetHidden(bool hidden)
        {
            try { await BlackjackRestClient.Instance.SetHideVipBadgeAsync(hidden); Refresh(); }
            catch (Exception e) { Debug.LogWarning($"[VipBadgeBinder] hide-badge toggle failed: {e.Message}"); }
        }

        private void Render(VipStatusData v)
        {
            // Show the badge for owned VIP prestige: a VIP LEVEL (1+, granted/bought — no "lit" requirement) OR a
            // lit tier badge (Silver+ with recent activity). Either way, respect the player's hide opt-out.
            SetActiveSafe(badgeRoot, (v.VipLevel > 0 || (v.HasBadge && v.BadgeLit)) && !v.HideBadge);

            SetText(tierNameText, v.TierName);
            SetText(vipLevelText, v.VipLevel > 0 ? $"VIP {v.VipLevel}" : "");
            if (badgeIcon != null)
            {
                int idx = v.VipLevel - 1;   // VIP 1 → Element 0 … VIP 10 → Element 9 (the crest tracks the VIP LEVEL, not the tier)
                Sprite s = (vipLevelIcons != null && idx >= 0 && idx < vipLevelIcons.Length) ? vipLevelIcons[idx] : null;
                badgeIcon.sprite = s;
                badgeIcon.enabled = s != null;
            }

            SetText(statusPointsText, v.StatusPoints.ToString(moneyFormat));
            SetText(multiplierText, "×" + v.BenefitMultiplier.ToString(multiplierFormat));

            if (v.NextTier.HasValue)
            {
                SetText(nextTierText, $"{v.SpToNextTier.ToString(moneyFormat)} SP to {v.NextTierName}");
                if (nextTierProgress != null)
                {
                    long bar = v.StatusPoints + v.SpToNextTier;   // the next tier's SP bar
                    nextTierProgress.minValue = 0f;
                    nextTierProgress.maxValue = bar > 0 ? bar : 1f;
                    nextTierProgress.value = v.StatusPoints;
                }
            }
            else
            {
                SetText(nextTierText, "Top tier");
                if (nextTierProgress != null) { nextTierProgress.minValue = 0f; nextTierProgress.maxValue = 1f; nextTierProgress.value = 1f; }
            }
        }

        private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
        private static void SetActiveSafe(GameObject go, bool on) { if (go != null && go.activeSelf != on) go.SetActive(on); }
    }
}
