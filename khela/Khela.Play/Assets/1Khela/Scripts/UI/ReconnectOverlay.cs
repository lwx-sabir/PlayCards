using PlayCard.App;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The GLOBAL "reconnecting…" overlay. One prefab, dropped in once, alive for the whole session.
    ///
    /// Put this on the prefab ROOT and give that root its own Canvas (Screen Space - Overlay, a high Sort Order so it
    /// sits above table UI) plus a GraphicRaycaster. The root keeps itself alive across scene loads and de-duplicates,
    /// so you can either place it in Boot once or leave a copy in several scenes — whichever is convenient, only one
    /// will ever survive.
    ///
    /// It reads <see cref="NetworkStatus"/> rather than any table object, because the connection it reports on
    /// outlives any single scene and the overlay must be able to show during a scene transition.
    ///
    /// Every reference is optional: a prefab that only wants a spinner leaves the label empty, and so on.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ReconnectOverlay : MonoBehaviour
    {
        [Header("Visuals (all optional)")]
        [Tooltip("The thing that actually shows/hides. Leave empty to toggle this GameObject's first child. Keeping " +
                 "this separate from the root matters — the root must stay ACTIVE to keep listening, so it can't be " +
                 "the thing being switched off (see the disabled-watcher trap that has bitten several popups here).")]
        [SerializeField] private GameObject panel;

        [Tooltip("Optional message line, e.g. \"Reconnecting…\".")]
        [SerializeField] private TMP_Text label;

        [Tooltip("Optional transform spun while reconnecting — any icon works, this just rotates it.")]
        [SerializeField] private RectTransform spinner;
        [SerializeField] private float spinnerDegreesPerSecond = 180f;

        [Header("Behaviour")]
        [Tooltip("Text shown while a reconnect is in flight.")]
        [SerializeField] private string reconnectingMessage = "Reconnecting…";

        [Tooltip("Seconds the connection must stay down before the overlay appears. A brief blip recovers on its own, " +
                 "and flashing this up for a quarter of a second reads worse than not showing it at all.")]
        [SerializeField] private float showAfterSeconds = 1.0f;

        [Tooltip("Block input underneath while it's up. Needs a full-screen Graphic (an Image, alpha 0 is fine) on the " +
                 "panel with Raycast Target on.")]
        [SerializeField] private bool blockInput = true;

        [Tooltip("Optional OK / dismiss button. Hides the overlay for THIS outage — it does not stop reconnecting, " +
                 "which continues in the background, and it comes back if the connection drops again. Leave empty " +
                 "for an overlay the player can't dismiss.")]
        [SerializeField] private Button dismissButton;

        private static ReconnectOverlay _instance;
        private float _downSince = -1f;   // unscaled time the connection went down, or -1 when up
        private bool _shown;
        private bool _dismissed;          // player pressed OK during THIS outage; cleared when we come back up

        private void Awake()
        {
            // De-dupe FIRST: a second copy arriving from another scene must die before it touches anything.
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            if (panel == null && transform.childCount > 0)
                panel = transform.GetChild(0).gameObject;

            if (dismissButton != null) dismissButton.onClick.AddListener(Dismiss);

            Show(false);
        }

        private void OnEnable()
        {
            if (NetworkStatus.Instance != null)
            {
                NetworkStatus.Instance.OnChanged += OnNetChanged;
                OnNetChanged(NetworkStatus.Instance.State, NetworkStatus.Instance.Reason);   // adopt current state
            }
        }

        private void OnDisable()
        {
            if (NetworkStatus.Instance != null) NetworkStatus.Instance.OnChanged -= OnNetChanged;
        }

        private void OnDestroy()
        {
            if (dismissButton != null) dismissButton.onClick.RemoveListener(Dismiss);
            if (_instance == this) _instance = null;
        }

        /// <summary>
        /// Hide the overlay without pretending the connection is back. Reconnecting continues underneath — this only
        /// stops nagging about it — and the overlay returns on the NEXT drop. Public so a scene can also bind it to a
        /// back button or a gesture.
        /// </summary>
        public void Dismiss()
        {
            _dismissed = true;
            Show(false);
        }

        private void OnNetChanged(NetState state, string reason)
        {
            if (state == NetState.Reconnecting)
            {
                if (_downSince < 0f) _downSince = Time.unscaledTime;   // start the grace window
            }
            else
            {
                _downSince = -1f;
                _dismissed = false;   // back up (or deliberately offline) — a later drop is a NEW outage, so nag again
                Show(false);
            }
        }

        private void Update()
        {
            // Delayed show: only once the drop has outlasted the grace window. Driven from Update rather than a
            // coroutine so it keeps working if the panel itself is inactive.
            if (_downSince >= 0f && !_shown && !_dismissed && Time.unscaledTime - _downSince >= showAfterSeconds)
                Show(true);

            // unscaledDeltaTime — a reconnect can happen while the game is paused (Time.timeScale 0).
            if (_shown && spinner != null)
                spinner.Rotate(0f, 0f, -spinnerDegreesPerSecond * Time.unscaledDeltaTime);
        }

        private void Show(bool visible)
        {
            _shown = visible;
            if (panel != null && panel.activeSelf != visible) panel.SetActive(visible);
            if (visible && label != null) label.text = reconnectingMessage;

            if (blockInput && panel != null)
            {
                var group = panel.GetComponent<CanvasGroup>();
                if (group != null) group.blocksRaycasts = visible;
            }
        }
    }
}
