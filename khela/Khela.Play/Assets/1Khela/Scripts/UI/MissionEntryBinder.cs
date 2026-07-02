using System;
using PlayCard.Game.Net;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// One mission row. The row has THREE state children — Progressing / Claimable / Claimed — each a self-contained
    /// layout (its own icon/title/desc/etc.) with a <see cref="MissionStateView"/> on it. This binder just decides which
    /// state applies, activates that child, hands it the mission data, and hides the other two. Bound by
    /// <see cref="MissionsPanelBinder"/> via <see cref="Setup"/>.
    /// </summary>
    public sealed class MissionEntryBinder : MonoBehaviour
    {
        [Tooltip("State child shown while progress < target.")]
        [SerializeField] private MissionStateView progressingView;
        [Tooltip("State child shown when complete but not yet claimed (has the Claim button).")]
        [SerializeField] private MissionStateView claimableView;
        [Tooltip("State child shown after the reward is claimed.")]
        [SerializeField] private MissionStateView claimedView;

        private MissionStateView _active;

        /// <summary>Bind the mission: pick the state, activate + fill that view, hide the others.</summary>
        public void Setup(MissionData m, Sprite icon, Action<string> onClaim)
        {
            var target = m.IsClaimed ? claimedView : (m.IsClaimable ? claimableView : progressingView);

            Apply(progressingView, target, m, icon, onClaim);
            Apply(claimableView, target, m, icon, onClaim);
            Apply(claimedView, target, m, icon, onClaim);
            _active = target;
        }

        private static void Apply(MissionStateView view, MissionStateView active, MissionData m, Sprite icon, Action<string> onClaim)
        {
            if (view == null) return;
            bool on = view == active;
            if (view.gameObject.activeSelf != on) view.gameObject.SetActive(on);
            if (on) view.Bind(m, icon, onClaim);
        }

        /// <summary>Bind a pending REWARD (level-up, gift…) as a claimable row — forces the Claimable state and fills it
        /// from the reward (title + description + amount). Lets rewards live in the SAME list as missions.</summary>
        public void SetupReward(RewardData r, Sprite icon, Action<string> onClaim)
        {
            SetActive(progressingView, false);
            SetActive(claimedView, false);
            SetActive(claimableView, true);
            if (claimableView != null) claimableView.BindReward(r.Id, r.Title, RewardDescription(r.Source), r.Amount, icon, onClaim);
            _active = claimableView;
        }

        private static void SetActive(MissionStateView v, bool on) { if (v != null && v.gameObject.activeSelf != on) v.gameObject.SetActive(on); }

        // The server sends only a Title ("Level 21 reward"); this gives the row a short description line by reward source.
        private static readonly string[] RewardDescriptions = { "Level-up bonus", "Milestone bonus", "Daily bonus", "Pass reward", "Achievement", "Gift", "Reward" };
        private static string RewardDescription(int source) => (source >= 0 && source < RewardDescriptions.Length) ? RewardDescriptions[source] : "Reward";

        /// <summary>Lock/unlock the active state's claim button during an in-flight claim (prevents double-tap).</summary>
        public void SetClaimInteractable(bool on) { if (_active != null) _active.SetClaimInteractable(on); }
    }
}
