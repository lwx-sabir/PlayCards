using System;
using System.Collections;
using TMPro;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// A pure VIEW for one stat block — the rows shown for the selected profile tab (Game Played, Wagered,
    /// Biggest Win, Last Played, Started Playing, + optional extras). It holds NO data and no tab logic; it just
    /// renders whatever <see cref="Stats"/> is handed to it via <see cref="Bind"/>. The same component renders the
    /// "All" aggregate or a single game — <see cref="GameStatsTabs"/> picks the source and calls Bind.
    ///
    /// On a tab change (Bind with animate=true) the block fades out, swaps its values while hidden, then slides in
    /// from the left + fades. It animates <see cref="animTarget"/> as a single unit (defaults to this object), so a
    /// Vertical Layout Group INSIDE it just keeps the rows arranged while the whole block moves — no per-row wiring.
    ///
    /// WIRING: put this on the rows container (the object holding the stat rows) and assign the value labels. If the
    /// component sits higher up, set <see cref="animTarget"/> to the rows container so only the rows slide.
    /// </summary>
    public sealed class StatBlockView : MonoBehaviour
    {
        /// <summary>One tab's values. Field set is aligned across the "All" aggregate and a single game so the
        /// same view renders either. Nullable members render as <see cref="neverLabel"/> / hidden when absent.</summary>
        public struct Stats
        {
            public long GamesPlayed;
            public long GamesWon;
            public double? WinRate;        // 0..100; null = n/a
            public decimal Wagered;
            public decimal BiggestWin;
            public decimal? NetProfit;     // own profile only; null = hidden
            public int CurrentWinStreak;
            public int LongestWinStreak;
            public DateTime? LastPlayed;
            public DateTime? StartedPlaying;
        }

        [Header("Value labels (assign only the rows your layout shows)")]
        [SerializeField] private TMP_Text gamesPlayedText;
        [SerializeField] private TMP_Text wageredText;
        [SerializeField] private TMP_Text biggestWinText;
        [SerializeField] private TMP_Text lastPlayedText;
        [SerializeField] private TMP_Text startedPlayingText;

        [Header("Optional extra rows (leave unassigned to hide)")]
        [SerializeField] private TMP_Text gamesWonText;
        [SerializeField] private TMP_Text winRateText;
        [SerializeField] private TMP_Text netProfitText;
        [SerializeField] private TMP_Text currentStreakText;
        [SerializeField] private TMP_Text longestStreakText;

        [Header("Formatting")]
        [Tooltip("Numeric format for chip amounts / counts (matches BalanceHud / ProfilePanelBinder).")]
        [SerializeField] private string moneyFormat = "#,0";
        [Tooltip("Date format for 'Started Playing' (e.g. dd/MM/yyyy).")]
        [SerializeField] private string dateFormat = "dd/MM/yyyy";
        [Tooltip("Shown for Last Played / Started Playing when the player has never played.")]
        [SerializeField] private string neverLabel = "—";

        [Header("Transition (on tab change)")]
        [Tooltip("Play the slide+fade on tab change. Off = instant swap.")]
        [SerializeField] private bool animateOnChange = true;
        [Tooltip("What slides/fades. Leave empty to use THIS object (put the component on the rows container).")]
        [SerializeField] private RectTransform animTarget;
        [Tooltip("Pixels the block travels in. Positive = from the right, negative = from the left.")]
        [SerializeField] private float slideDistance = 70f;
        [Tooltip("Fade-out time for the old values.")]
        [SerializeField] private float outDuration = 0.10f;
        [Tooltip("Slide + fade-in time for the new values.")]
        [SerializeField] private float inDuration = 0.22f;

        private CanvasGroup _cg;
        private Vector2 _home;
        private bool _ready;
        private Coroutine _anim;
        private Stats _pending;

        private void Awake() => CacheTarget();

        // Reset to rest on enable (heals a transition interrupted by the panel closing).
        private void OnEnable()
        {
            CacheTarget();
            if (_ready) { animTarget.anchoredPosition = _home; _cg.alpha = 1f; }
            _anim = null;
        }

        private void OnDisable() => _anim = null;   // coroutine already stopped by Unity

        private void CacheTarget()
        {
            if (_ready) return;
            if (animTarget == null) animTarget = transform as RectTransform;
            if (animTarget == null) return;
            _cg = animTarget.GetComponent<CanvasGroup>();
            if (_cg == null) _cg = animTarget.gameObject.AddComponent<CanvasGroup>();
            _home = animTarget.anchoredPosition;
            _ready = true;
        }

        /// <summary>Paint the rows. <paramref name="animate"/> plays the slide/fade (use on tab change); false is instant.</summary>
        public void Bind(in Stats d, bool animate = false)
        {
            CacheTarget();
            if (animate && animateOnChange && isActiveAndEnabled && _ready)
            {
                _pending = d;
                if (_anim != null) StopCoroutine(_anim);
                _anim = StartCoroutine(Transition());
            }
            else
            {
                ApplyInstant(d);
            }
        }

        private IEnumerator Transition()
        {
            // OUT — fade the current values out.
            float a0 = _cg.alpha;
            for (float t = 0f; t < outDuration; t += Time.unscaledDeltaTime)
            {
                _cg.alpha = Mathf.Lerp(a0, 0f, t / outDuration);
                yield return null;
            }
            _cg.alpha = 0f;

            // SWAP values while hidden, then park the block off to the left.
            ApplyInstant(_pending);
            animTarget.anchoredPosition = _home + new Vector2(slideDistance, 0f);

            // IN — slide in from the left + fade (ease-out cubic).
            for (float t = 0f; t < inDuration; t += Time.unscaledDeltaTime)
            {
                float k = 1f - Mathf.Pow(1f - t / inDuration, 3f);
                animTarget.anchoredPosition = Vector2.LerpUnclamped(_home + new Vector2(slideDistance, 0f), _home, k);
                _cg.alpha = k;
                yield return null;
            }

            animTarget.anchoredPosition = _home;
            _cg.alpha = 1f;
            _anim = null;
        }

        private void ApplyInstant(in Stats d)
        {
            SetText(gamesPlayedText, d.GamesPlayed.ToString(moneyFormat));
            SetText(wageredText, d.Wagered.ToString(moneyFormat));
            SetText(biggestWinText, d.BiggestWin.ToString(moneyFormat));
            SetText(lastPlayedText, d.LastPlayed.HasValue ? Ago(d.LastPlayed.Value) : neverLabel);
            SetText(startedPlayingText, d.StartedPlaying.HasValue
                ? AsUtc(d.StartedPlaying.Value).ToLocalTime().ToString(dateFormat)
                : neverLabel);

            // Optional extras — only fill if the label is assigned.
            SetText(gamesWonText, d.GamesWon.ToString(moneyFormat));
            SetText(winRateText, d.WinRate.HasValue ? d.WinRate.Value.ToString("0.0") + "%" : neverLabel);
            if (netProfitText != null) netProfitText.text = d.NetProfit.HasValue ? FormatSigned(d.NetProfit.Value) : "";
            SetText(currentStreakText, d.CurrentWinStreak.ToString());
            SetText(longestStreakText, d.LongestWinStreak.ToString());
        }

        private string FormatSigned(decimal v) => v > 0 ? "+" + v.ToString(moneyFormat) : v.ToString(moneyFormat);

        // Relative time ("just now" / "23 mins ago" / "3 hours ago" / "5 days ago" / "2 months ago" / "1 year ago").
        private static string Ago(DateTime when)
        {
            var span = DateTime.UtcNow - AsUtc(when);
            if (span.Ticks < 0) span = TimeSpan.Zero;
            if (span.TotalMinutes < 1)  return "just now";
            if (span.TotalMinutes < 60) return Plural((int)span.TotalMinutes, "min");
            if (span.TotalHours   < 24) return Plural((int)span.TotalHours, "hour");
            if (span.TotalDays    < 30) return Plural((int)span.TotalDays, "day");
            if (span.TotalDays    < 365) return Plural((int)(span.TotalDays / 30), "month");
            return Plural((int)(span.TotalDays / 365), "year");
        }

        private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";

        private static DateTime AsUtc(DateTime dt) =>
            dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();

        private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
    }
}
