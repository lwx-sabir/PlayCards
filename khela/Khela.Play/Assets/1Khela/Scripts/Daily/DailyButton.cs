using Khela.Common.Daily;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Daily
{
    /// <summary>
    /// The "open the daily reward" button, for any scene. Put this on the button, drag in the daily prefab, and it
    /// spawns the popup on demand — no copy of it in every scene, and no lookup by name.
    ///
    /// It also owns the notification dot. ⚠️ This component must live on an ALWAYS-ACTIVE object (the button), never
    /// on the dot it toggles: a watcher on a disabled-by-default object never runs, so the badge would never light —
    /// a trap this project has hit before.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DailyButton : MonoBehaviour
    {
        [Header("Popup")]
        [Tooltip("The Canvas_Daily_Login_Bonus prefab. Spawned once, then reused.")]
        [SerializeField] private DailyPanel prefab;
        [Tooltip("Already in the scene? Assign it here instead and leave the prefab empty.")]
        [SerializeField] private DailyPanel existingPanel;

        [Header("Badge")]
        [Tooltip("Dot/alert object shown while something is collectable. Left empty, no badge is shown.")]
        [SerializeField] private GameObject badge;
        [Tooltip("Optional count label on the badge.")]
        [SerializeField] private TMP_Text badgeCount;
        [Tooltip("Count missed days that rewarded ads could buy back, as well as the free one. Off counts only what " +
                 "costs nothing — which is the honest badge, since an ad is a price.")]
        [SerializeField] private bool countAdUnlockable;

        [Header("Behaviour")]
        [Tooltip("Refresh the snapshot when this button appears, so the badge is right on entering a scene.")]
        [SerializeField] private bool refreshOnEnable = true;
        [Tooltip("Open the popup automatically the first time a scene with this button loads and something is " +
                 "waiting. The daily reward is the one thing players expect to be shown, not to have to find.")]
        [SerializeField] private bool autoOpenWhenClaimable;

        private Button _button;
        private static DailyPanel _spawned;   // one popup across the whole session, whatever scene opened it
        private static bool _autoOpened;      // once per session, never on every scene change

        private void Awake()
        {
            _button = GetComponent<Button>();
            if (_button != null) _button.onClick.AddListener(Open);
        }

        private void OnEnable()
        {
            DailyState.Instance.Changed += OnStateChanged;
            ApplyBadge(DailyState.Instance.Current);
            if (refreshOnEnable) RefreshWhenSignedIn();
        }

        private void OnDisable()
        {
            DailyState.Instance.Changed -= OnStateChanged;
            if (_waitingForAuth && PlayCard.Account.AccountManager.Instance != null)
                PlayCard.Account.AccountManager.Instance.OnReady -= OnAuthReady;
            _waitingForAuth = false;
        }

        /// <summary>
        /// Fetch, but not before there is a token.
        ///
        /// On a cold start this button enables while the device is still registering, so an immediate fetch is a
        /// guaranteed 401 whose empty body then fails to parse. DailyState refuses the call in that state, which would
        /// leave the badge dark until something else happened to refresh — so wait for the account and go then.
        /// </summary>
        private void RefreshWhenSignedIn()
        {
            var account = PlayCard.Account.AccountManager.Instance;
            if (account == null || !string.IsNullOrEmpty(account.JwtToken))
            {
                _ = DailyState.Instance.RefreshAsync();
                return;
            }

            account.OnReady += OnAuthReady;
            _waitingForAuth = true;
        }

        private void OnAuthReady()
        {
            if (PlayCard.Account.AccountManager.Instance != null)
                PlayCard.Account.AccountManager.Instance.OnReady -= OnAuthReady;
            _waitingForAuth = false;

            if (isActiveAndEnabled) _ = DailyState.Instance.RefreshAsync();
        }

        private bool _waitingForAuth;

        private void OnDestroy()
        {
            if (_button != null) _button.onClick.RemoveListener(Open);
        }

        /// <summary>Open the daily reward. Wired to this object's Button automatically; also callable from anywhere.</summary>
        public void Open()
        {
            var panel = ResolvePanel();
            if (panel == null)
            {
                Debug.LogWarning($"{name}: no daily panel assigned — set either Prefab or Existing Panel.", this);
                return;
            }
            panel.Open();
        }

        private DailyPanel ResolvePanel()
        {
            // A panel that already lives in a SCENE can be used directly. A prefab ASSET cannot: driving it would
            // spawn tiles into the asset's own transforms and SetActive on it does nothing. Dragging the prefab into
            // either field is an easy slip, so treat an asset in `existingPanel` as a prefab source instead of failing.
            if (IsInScene(existingPanel)) return existingPanel;
            if (_spawned != null) return _spawned;

            var source = prefab != null ? prefab : existingPanel;
            if (source == null) return null;

            // Spawn at the scene ROOT with its own canvas — never parented into this scene's UI, so it can't inherit
            // someone else's scaling or draw order.
            _spawned = Instantiate(source);
            _spawned.name = source.name;
            DontDestroyOnLoad(_spawned.gameObject);
            _spawned.gameObject.SetActive(false);
            return _spawned;
        }

        private static bool IsInScene(DailyPanel panel) => panel != null && panel.gameObject.scene.IsValid();

        private void OnStateChanged(DailyStateDto state)
        {
            ApplyBadge(state);

            if (autoOpenWhenClaimable && !_autoOpened && state != null && state.Active && DailyState.Instance.HasClaimable)
            {
                _autoOpened = true;
                Open();
            }
        }

        private void ApplyBadge(DailyStateDto state)
        {
            if (badge == null) return;

            int count = CountWaiting(state);
            bool show = count > 0;
            if (badge.activeSelf != show) badge.SetActive(show);

            if (badgeCount == null) return;

            badgeCount.text = count > 0 ? count.ToString() : string.Empty;
            badgeCount.gameObject.SetActive(count > 0);
        }

        private int CountWaiting(DailyStateDto state)
        {
            if (state == null || !state.Active || state.Nodes == null) return 0;

            int count = 0;
            foreach (var node in state.Nodes)
            {
                if (node == null) continue;
                if (node.ClaimableNow || (countAdUnlockable && node.AdUnlockable)) count++;
            }
            return count;
        }
    }
}
