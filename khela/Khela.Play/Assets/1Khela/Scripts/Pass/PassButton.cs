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

        /// <summary>What the button's bar is measuring.</summary>
        public enum ProgressShows
        {
            /// <summary>DEFAULT. How far through the cycle the player is — the same value the pass screen's own bar
            /// shows, so the two never disagree.</summary>
            CycleDay = 0,

            /// <summary>How many days they've actually COLLECTED. A weaker bar (it lags the month) but a stronger
            /// prompt: it only moves when they act, and an unfilled bar is a job unfinished.</summary>
            DaysCollected = 1,
        }

        [Header("Info — every field optional, wire only what the art has")]
        [Tooltip("The pass's name, as the SERVER titles it (\"Monthly Pass\"). Never hardcode it here: an admin can " +
                 "retitle a cycle, and a button reading last month's name is worse than one reading nothing.")]
        [SerializeField] private TMP_Text titleText;
        [Tooltip("Which day the player is on. Formatted with Day Format.")]
        [SerializeField] private TMP_Text dayText;
        [Tooltip("{0} = current day, {1} = days in the cycle. \"{0} / {1}\" → \"12 / 31\"; \"Day {0}\" → \"Day 12\".")]
        [SerializeField] private string dayFormat = "{0} / {1}";
        [Tooltip("Time left in the cycle (\"12d 4h\"). Ticks once a minute — a button doesn't need a second hand.")]
        [SerializeField] private TMP_Text endsInText;

        [Header("Progress bar — assign EITHER; the fill alone is fine")]
        [Tooltip("The Slider, if the bar is one.")]
        [SerializeField] private Slider progressSlider;
        [Tooltip("The filled Image. If it lives inside a Slider, that Slider is found and driven instead — writing " +
                 "the Image directly would be overwritten by it. See PassProgressBar.")]
        [SerializeField] private Image progressFill;
        [SerializeField] private ProgressShows progressShows = ProgressShows.CycleDay;

        [Header("Behaviour")]
        [Tooltip("Refresh the snapshot when this button appears, so the badge is right on entering a scene.")]
        [SerializeField] private bool refreshOnEnable = true;
        [Tooltip("Hide this whole object while no pass is running, instead of showing an empty widget. Leave empty " +
                 "to always show the button.")]
        [SerializeField] private GameObject hideWhileInactive;

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
            Apply(PassState.Instance.Current);
            if (refreshOnEnable) _ = PassState.Instance.RefreshAsync();

            // The countdown is the only thing here that changes without the server saying so, so it's the only thing
            // that needs a clock — once a minute, because this label is never showing seconds.
            if (endsInText != null) _countdown = StartCoroutine(TickCountdown());
        }

        private void OnDisable()
        {
            PassState.Instance.Changed -= OnStateChanged;
            if (_countdown != null) { StopCoroutine(_countdown); _countdown = null; }
        }

        private System.Collections.IEnumerator TickCountdown()
        {
            var wait = new WaitForSeconds(60f);
            while (true)
            {
                ApplyEndsIn(PassState.Instance.Current);
                yield return wait;
            }
        }

        private Coroutine _countdown;

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

        private void OnStateChanged(PassStateDto state) => Apply(state);

        /// <summary>
        /// Paint everything this button shows from ONE snapshot.
        ///
        /// Every value here is the server's — the title it authored, the day IT decided the player is on, the cycle
        /// length it resolved in the player's own timezone. None of it is recomputed from the device clock, which is
        /// what stops a changed phone date from showing a day the player can't claim. This is a mirror, nothing else.
        /// </summary>
        private void Apply(PassStateDto state)
        {
            bool active = state != null && state.Active;

            if (hideWhileInactive != null && hideWhileInactive.activeSelf != active)
                hideWhileInactive.SetActive(active);

            ApplyBadge(state);
            ApplyEndsIn(state);

            if (titleText != null) titleText.text = active ? (state.Title ?? string.Empty) : string.Empty;

            if (dayText != null)
                dayText.text = active && state.Days > 0
                    ? SafeFormat(dayFormat, state.DayIndex, state.Days)
                    : string.Empty;

            Bar().Set(active ? Progress(state) : 0f);
        }

        private float Progress(PassStateDto state)
        {
            if (state.Days <= 0) return 0f;

            if (progressShows == ProgressShows.DaysCollected)
            {
                if (state.Nodes == null || state.Nodes.Count == 0) return 0f;
                int claimed = 0;
                foreach (var node in state.Nodes)
                    if (node != null && node.Claimed) claimed++;
                return Mathf.Clamp01((float)claimed / state.Nodes.Count);
            }

            return Mathf.Clamp01((float)state.DayIndex / state.Days);
        }

        private void ApplyEndsIn(PassStateDto state)
        {
            if (endsInText == null) return;

            if (state == null || !state.Active) { endsInText.text = string.Empty; return; }

            // CycleEndUtc is the server's boundary, resolved in the PLAYER's timezone — subtracting UTC now is the one
            // safe way to turn it into a duration without the device's own calendar getting a vote.
            var left = state.CycleEndUtc - System.DateTime.UtcNow;
            if (left < System.TimeSpan.Zero) left = System.TimeSpan.Zero;

            endsInText.text = left.TotalDays >= 1
                ? $"{(int)left.TotalDays}d {left.Hours}h"
                : $"{(int)left.TotalHours}h {left.Minutes}m";
        }

        /// <summary>A bad format string in the inspector must not throw every time the pass refreshes.</summary>
        private static string SafeFormat(string format, int day, int days)
        {
            if (string.IsNullOrEmpty(format)) return day.ToString();
            try { return string.Format(format, day, days); }
            catch (System.FormatException) { return $"{day} / {days}"; }
        }

        private PassProgressBar Bar() => _bar ?? (_bar = new PassProgressBar(progressSlider, progressFill));

        private PassProgressBar _bar;

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
