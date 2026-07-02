using System;
using System.Collections.Generic;
using System.Linq;
using PlayCard.Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// Drives the Daily Missions screen. Fetches GET /api/missions/daily on enable, spawns a
    /// <see cref="MissionEntryBinder"/> per mission (icon resolved by key), handles Claim (server-authoritative,
    /// straight to balance), and drives the <see cref="DailyBundleBinder"/> (complete-all + reset timer). Re-pulls
    /// after every claim so progress/state stay correct. Pure VIEW — it never grants; the server credits on claim.
    /// </summary>
    public sealed class MissionsPanelBinder : MonoBehaviour
    {
        [Serializable] public struct IconEntry { public string key; public Sprite sprite; }

        [Header("Missions list")]
        [Tooltip("Parent for spawned mission rows (a Vertical Layout Group content rect).")]
        [SerializeField] private Transform missionsContainer;
        [SerializeField] private MissionEntryBinder missionRowPrefab;
        [Tooltip("Mission icon by IconKey: play / win / chips / blackjack / double / split.")]
        [SerializeField] private IconEntry[] icons;
        [Tooltip("Optional reward icon by Source (0=LevelUp,1=Milestone,2=DailyBonus,3=Pass,4=Achievement,5=Gift,6=Admin). Pending rewards render as claimable rows above the missions.")]
        [SerializeField] private Sprite[] rewardSourceIcons;

        [Header("Bundle (optional)")]
        [SerializeField] private DailyBundleBinder bundle;

        [Header("Feedback (optional)")]
        [SerializeField] private TMP_Text messageText;

        [Header("Panel")]
        [Tooltip("Close button — hides the panel on click.")]
        [SerializeField] private Button closeButton;
        [Tooltip("The panel GameObject to hide on close (e.g. Rewards_Daily). Defaults to this object if unset.")]
        [SerializeField] private GameObject panelRoot;

        private readonly List<MissionEntryBinder> _rows = new List<MissionEntryBinder>();
        private bool _busy;

        private void OnEnable()
        {
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            Refresh();
        }

        private void OnDisable()
        {
            if (closeButton != null) closeButton.onClick.RemoveListener(Close);
        }

        /// <summary>Hide the panel — the assigned <see cref="panelRoot"/>, or this object if none is set.</summary>
        public void Close() => (panelRoot != null ? panelRoot : gameObject).SetActive(false);

        /// <summary>Re-pull the daily missions + bundle state and repaint.</summary>
        public async void Refresh()
        {
            try
            {
                var missionsRes = await BlackjackRestClient.Instance.GetDailyMissionsAsync();
                var rewardsRes = await BlackjackRestClient.Instance.GetRewardsAsync();   // pending level-up/gift rewards
                var missions = missionsRes.Ok ? missionsRes.Value : null;
                var rewards = rewardsRes.Ok ? rewardsRes.Value : null;
                if (missions != null) Render(missions, rewards);
                else Debug.LogWarning("[MissionsPanelBinder] daily missions fetch returned no data");
            }
            catch (Exception e) { Debug.LogWarning($"[MissionsPanelBinder] fetch failed: {e.Message}"); }
        }

        private void Render(DailyMissionsData d, List<RewardData> rewards)
        {
            for (int i = 0; i < _rows.Count; i++) if (_rows[i] != null) Destroy(_rows[i].gameObject);
            _rows.Clear();

            if (missionsContainer != null && missionRowPrefab != null)
            {
                // Pending rewards (level-up/gift) render FIRST as claimable rows — same Reward_Row prefab, Claimable state.
                if (rewards != null)
                {
                    foreach (var r in rewards)
                    {
                        var row = Instantiate(missionRowPrefab, missionsContainer);
                        row.SetupReward(r, ResolveRewardIcon(r.Source), ClaimReward);
                        _rows.Add(row);
                    }
                }
                // Then the daily missions: Easy → Medium → Hard (OrderBy is stable, so server order is kept within a tier).
                if (d.Missions != null)
                {
                    foreach (var m in d.Missions.OrderBy(x => x.Difficulty))
                    {
                        var row = Instantiate(missionRowPrefab, missionsContainer);
                        row.Setup(m, ResolveIcon(m.IconKey), ClaimMission);
                        _rows.Add(row);
                    }
                }
            }
            if (bundle != null) bundle.Bind(d, ClaimBundle);
        }

        /// <summary>Collect a pending reward (level-up/gift) — server credits the wallet idempotently, then we re-pull.</summary>
        public async void ClaimReward(string rewardId)
        {
            if (_busy || string.IsNullOrEmpty(rewardId)) return;
            _busy = true;
            SetRowsInteractable(false);
            try
            {
                var res = await BlackjackRestClient.Instance.ClaimRewardAsync(rewardId);
                SetText(messageText, (res.Ok && res.Value != null && res.Value.Ok)
                    ? "Reward collected!" : (res.Value?.Error ?? "Couldn't collect — try again"));
            }
            catch (Exception e) { SetText(messageText, "Error — try again"); Debug.LogWarning($"[MissionsPanelBinder] reward claim failed: {e.Message}"); }
            finally { _busy = false; Refresh(); }
        }

        private Sprite ResolveRewardIcon(int source)
            => (rewardSourceIcons != null && source >= 0 && source < rewardSourceIcons.Length) ? rewardSourceIcons[source] : null;

        /// <summary>Claim a completed mission — server credits straight to balance, then we re-pull.</summary>
        public async void ClaimMission(string missionInstanceId)
        {
            if (_busy || string.IsNullOrEmpty(missionInstanceId)) return;
            _busy = true;
            SetRowsInteractable(false);
            try
            {
                var res = await BlackjackRestClient.Instance.ClaimMissionAsync(missionInstanceId);
                SetText(messageText, (res.Ok && res.Value != null && res.Value.Ok)
                    ? "Reward collected!" : (res.Value?.Error ?? "Couldn't claim — try again"));
            }
            catch (Exception e) { SetText(messageText, "Error — try again"); Debug.LogWarning($"[MissionsPanelBinder] claim failed: {e.Message}"); }
            finally { _busy = false; Refresh(); }
        }

        /// <summary>Claim the complete-all bundle.</summary>
        public async void ClaimBundle()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var res = await BlackjackRestClient.Instance.ClaimMissionBundleAsync();
                SetText(messageText, (res.Ok && res.Value != null && res.Value.Ok)
                    ? "Bundle collected!" : (res.Value?.Error ?? "Complete all missions first"));
            }
            catch (Exception e) { SetText(messageText, "Error — try again"); Debug.LogWarning($"[MissionsPanelBinder] bundle claim failed: {e.Message}"); }
            finally { _busy = false; Refresh(); }
        }

        private Sprite ResolveIcon(string key)
        {
            if (icons != null && !string.IsNullOrEmpty(key))
                for (int i = 0; i < icons.Length; i++)
                    if (icons[i].key == key) return icons[i].sprite;
            return null;
        }

        // The icon keys the default missions use (MissionCatalog). Keep in sync if you add new iconKeys in the admin editor.
        private static readonly string[] DefaultIconKeys = { "blackjack_play", "blackjack_win", "blackjack_wager", "blackjack_natural", "blackjack_double", "blackjack_split" };

        /// <summary>Right-click the component header (or the ⋮ menu) → "Populate default icon keys" to fill the keys the
        /// built-in missions use; then just drop your sprites in. Preserves any keys/sprites you've already set.</summary>
        [ContextMenu("Populate default icon keys")]
        private void PopulateDefaultIconKeys()
        {
            var list = new List<IconEntry>(icons ?? new IconEntry[0]);
            foreach (var k in DefaultIconKeys)
                if (!list.Any(e => e.key == k))
                    list.Add(new IconEntry { key = k });
            icons = list.ToArray();
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        private void SetRowsInteractable(bool on)
        {
            for (int i = 0; i < _rows.Count; i++) if (_rows[i] != null) _rows[i].SetClaimInteractable(on);
        }

        private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
    }
}
