using Khela.Common.Pass;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Pass
{
    /// <summary>
    /// The "open the pass" button, for any scene. Put this on the button itself, drag in the pass prefab, and it
    /// spawns the panel on demand — no copy of the pass in every scene, and no lookup by name.
    ///
    /// It also owns the notification dot. ⚠️ This component must live on an ALWAYS-ACTIVE object (the button), never
    /// on the dot it toggles: a watcher on a disabled-by-default object never runs, so the badge would never light —
    /// a trap this project has hit before.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PassButton : MonoBehaviour
    {
        [Header("Panel")]
        [Tooltip("The Monthly_Pass_Canvas prefab. Spawned once, then reused.")]
        [SerializeField] private PassPanel passPrefab;
        [Tooltip("Already in the scene? Assign it here instead and leave the prefab empty.")]
        [SerializeField] private PassPanel existingPanel;

        /// <summary>What the badge counts as "waiting for you".</summary>
        public enum BadgeScope
        {
            /// <summary>Only what costs nothing — today's day, and backfill a subscriber already owns.</summary>
            FreeOnly = 0,
            /// <summary>DEFAULT. Free days plus the missed days ads can buy back — everything obtainable right now
            /// without spending money.</summary>
            FreeAndAds = 1,
            /// <summary>Everything unclaimed, including subscription-only days. Makes the badge a number that never
            /// clears for a free player, which teaches them to ignore it — use with care.</summary>
            Everything = 2,
        }

        [Header("Badge")]
        [Tooltip("Dot/alert object shown while something is claimable. Left empty, no badge is shown.")]
        [SerializeField] private GameObject badge;
        [Tooltip("Optional count label on the badge.")]
        [SerializeField] private TMP_Text badgeCount;
        [SerializeField] private BadgeScope badgeCounts = BadgeScope.FreeAndAds;

        [Header("Behaviour")]
        [Tooltip("Refresh the snapshot when this button appears, so the badge is right on entering a scene.")]
        [SerializeField] private bool refreshOnEnable = true;

        private Button _button;
        private static PassPanel _spawned;   // one panel across the whole session, whatever scene opened it

        private void Awake()
        {
            _button = GetComponent<Button>();
            if (_button != null) _button.onClick.AddListener(Open);
        }

        private void OnEnable()
        {
            PassState.Instance.Changed += OnStateChanged;
            ApplyBadge(PassState.Instance.Current);
            if (refreshOnEnable) _ = PassState.Instance.RefreshAsync();
        }

        private void OnDisable() => PassState.Instance.Changed -= OnStateChanged;

        private void OnDestroy()
        {
            if (_button != null) _button.onClick.RemoveListener(Open);
        }

        /// <summary>Open the pass. Wired to this object's Button automatically; also callable from any other event.</summary>
        public void Open()
        {
            var panel = ResolvePanel();
            if (panel == null)
            {
                Debug.LogWarning($"{name}: no pass panel assigned — set either Pass Prefab or Existing Panel.", this);
                return;
            }
            panel.Open();
        }

        private PassPanel ResolvePanel()
        {
            // A panel that already lives in a SCENE can be used directly. A prefab ASSET cannot: driving it would
            // spawn cards into the asset's own transforms ("Cannot instantiate objects with a parent which is
            // persistent") and SetActive on it does nothing. Dragging the prefab into either field is an easy slip,
            // so treat an asset in `existingPanel` as a prefab source instead of failing.
            if (IsInScene(existingPanel)) return existingPanel;
            if (_spawned != null) return _spawned;

            var source = passPrefab != null ? passPrefab : existingPanel;
            if (source == null) return null;

            // Spawn at the scene ROOT with its own canvas — never parented into this scene's UI, so it can't inherit
            // someone else's scaling or draw order.
            _spawned = Instantiate(source);
            _spawned.name = source.name;
            DontDestroyOnLoad(_spawned.gameObject);
            _spawned.gameObject.SetActive(false);
            return _spawned;
        }

        /// <summary>True only for a real scene object — false for a prefab asset dragged in from the Project window.</summary>
        private static bool IsInScene(PassPanel panel) => panel != null && panel.gameObject.scene.IsValid();

        private void OnStateChanged(PassStateDto state) => ApplyBadge(state);

        private void ApplyBadge(PassStateDto state)
        {
            if (badge == null) return;

            int count = CountWaiting(state);
            bool show = count > 0;
            if (badge.activeSelf != show) badge.SetActive(show);

            if (badgeCount == null) return;

            // Show the number whenever there IS one — a badge reading "1" is the normal case, and hiding it left the
            // dot looking like an empty background.
            badgeCount.text = count > 0 ? count.ToString() : string.Empty;
            badgeCount.gameObject.SetActive(count > 0);
        }

        /// <summary>How many reward slots are waiting, per <see cref="badgeCounts"/>.</summary>
        private int CountWaiting(PassStateDto state)
        {
            if (state == null || !state.Active || state.Nodes == null) return 0;

            int count = 0;
            foreach (var node in state.Nodes)
            {
                if (node == null) continue;

                bool waiting = node.ClaimableNow
                    || (badgeCounts >= BadgeScope.FreeAndAds && node.AdUnlockable)
                    || (badgeCounts == BadgeScope.Everything && node.GoldenLocked);

                if (waiting) count++;
            }
            return count;
        }
    }
}
