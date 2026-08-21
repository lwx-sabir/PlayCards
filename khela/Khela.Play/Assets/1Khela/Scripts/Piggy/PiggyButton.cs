using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Piggy
{
    /// <summary>
    /// Opens the piggy popup, from any scene. Put it on the button — the HUD widget's "Open Now", a shop entry,
    /// anything — drag in the popup prefab, and it spawns once and reuses it.
    ///
    /// Mirrors <c>DailyButton</c> deliberately, including the one-instance-per-session rule: the popup is a canvas of
    /// its own, and a second copy would fight the first for the reward-fly target registry.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PiggyButton : MonoBehaviour
    {
        [Header("Popup")]
        [Tooltip("The Popup_Piggy prefab. Spawned once, then reused.")]
        [SerializeField] private PiggyPanel prefab;
        [Tooltip("Already in the scene? Assign it here instead and leave the prefab empty.")]
        [SerializeField] private PiggyPanel existingPanel;

        [Header("Behaviour")]
        [Tooltip("Hide this button while the feature is off or the bank has nothing in it. Off leaves it always " +
                 "visible, which is right when the button IS the piggy widget.")]
        [SerializeField] private bool hideWhenEmpty;

        private Button _button;
        private static PiggyPanel _spawned;   // one popup across the whole session, whatever scene opened it

        private void Awake()
        {
            _button = GetComponent<Button>();
            if (_button != null) _button.onClick.AddListener(Open);
        }

        private void OnEnable()
        {
            PiggyState.Instance.Changed += OnStateChanged;
            OnStateChanged(PiggyState.Instance.Current);
        }

        private void OnDisable() => PiggyState.Instance.Changed -= OnStateChanged;

        private void OnStateChanged(Khela.Common.Piggy.PiggyStateDto state)
        {
            if (!hideWhenEmpty) return;
            bool worth = state != null && state.Enabled && state.Amount > 0m;
            if (gameObject.activeSelf != worth) gameObject.SetActive(worth);
        }

        /// <summary>Open the popup. Public so it can also be driven from a UnityEvent or another script.</summary>
        public void Open()
        {
            var panel = ResolvePanel();
            if (panel == null)
            {
                Debug.LogWarning($"{name}: no piggy popup assigned — set either Prefab or Existing Panel.", this);
                return;
            }
            panel.Open();
        }

        private PiggyPanel ResolvePanel()
        {
            // A panel already in a SCENE can be used directly. A prefab ASSET cannot: driving it would bind labels
            // inside the asset and SetActive on it does nothing. Dragging the prefab into either field is an easy
            // slip, so an asset in `existingPanel` is treated as a prefab source rather than failing.
            if (IsInScene(existingPanel)) return existingPanel;
            if (_spawned != null) return _spawned;

            var source = prefab != null ? prefab : existingPanel;
            if (source == null) return null;

            // Spawned at the scene ROOT with its own canvas — never parented into this scene's UI, so it can't
            // inherit someone else's scaling or draw order.
            _spawned = Instantiate(source);
            _spawned.name = source.name;
            DontDestroyOnLoad(_spawned.gameObject);
            _spawned.gameObject.SetActive(false);
            return _spawned;
        }

        private static bool IsInScene(PiggyPanel panel) => panel != null && panel.gameObject.scene.IsValid();
    }
}
