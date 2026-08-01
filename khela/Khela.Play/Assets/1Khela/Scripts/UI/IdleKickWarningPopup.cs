using System;
using System.Collections;
using PlayCard.Game.Betting;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using PlayCard.Game.Wallet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The "bet or you'll be removed" warning, shown during the LOCAL player's FINAL betting window before an idle
    /// eviction (server flag <c>IdleKickWarning</c>). It gives them a one-tap out:
    ///  • BET {min} — drops the table minimum onto their seat and lets the betting window auto-deal (does NOT force a
    ///    deal, so in multiplayer it can't cut off other players still betting). Clearing the warning hides the popup.
    ///  • LEAVE — leaves the table now.
    /// If they do neither by the window's close, the server removes them and <see cref="TableController"/> returns to
    /// the lobby on its own.
    ///
    /// Put this controller on an ALWAYS-ACTIVE object (e.g. TableHUD); the <see cref="panel"/> is a separate child you
    /// set inactive by default. Buttons are wired in code — no UnityEvent setup needed, just drag the refs.
    /// </summary>
    public sealed class IdleKickWarningPopup : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField] private TableController table;

        [Header("Panel")]
        [Tooltip("The warning panel to show. May be disabled by default.")]
        [SerializeField] private GameObject panel;
        [Tooltip("Optional headline label (\"Place a bet or you'll be removed…\"). Left as-authored if unassigned.")]
        [SerializeField] private TMP_Text messageLabel;
        [TextArea, SerializeField]
        private string message = "Place a bet or you'll be removed from the table.";

        [Header("Timer")]
        [Tooltip("Countdown for the CURRENT (final) betting window before eviction, shown as mm:ss + \"s\" (e.g. 00:12s) " +
                 "— the same clock as the Bet Timer popup, which auto-hides while this warning is up. Optional.")]
        [SerializeField] private TMP_Text timerLabel;

        [Header("Min-bet action")]
        [Tooltip("Label showing the table minimum, e.g. \"100K Min Bet\".")]
        [SerializeField] private TMP_Text minBetLabel;
        [SerializeField] private string minBetSuffix = " Min Bet";
        [Tooltip("Drops a min-bet chip on the felt and deals immediately — same as Repeat. Auto-found if unassigned; " +
                 "null ⇒ falls back to a bare PlaceBet that waits for the betting timer to auto-deal.")]
        [SerializeField] private BetRepeater betRepeater;
        [Tooltip("Places the table minimum on our seat and deals (via BetRepeater), like the Repeat button.")]
        [SerializeField] private Button betButton;
        [Tooltip("Label on the bet button, e.g. \"BET 100K\".")]
        [SerializeField] private TMP_Text betButtonLabel;
        [SerializeField] private string betPrefix = "BET ";
        [Tooltip("Leaves the table immediately.")]
        [SerializeField] private Button leaveButton;

        [Header("Slide")]
        [Tooltip("Slide in from the TOP (park above the shown spot, drop down into view). Uncheck to slide up from below.")]
        [SerializeField] private bool slideFromTop = true;
        [Tooltip("How far OFF-SCREEN to park when hidden (anchored units) — direction set by Slide From Top. Tune so it's fully off-screen.")]
        [SerializeField] private float slideDistance = 1000f;
        [SerializeField] private float slideDuration = 0.35f;
        [Tooltip("Juice: overshoot bounce on the slide. 0 = clean, ~1.7 = ~10% overshoot, higher = bouncier.")]
        [SerializeField] private float overshoot = 1.7f;

        private bool _shown;
        private RectTransform _panelRt;
        private Vector2 _shownPos;
        private Vector2 _hiddenPos;
        private bool _selfHosted;
        private Coroutine _slide;

#if UNITY_EDITOR
        // Guard the recurring "disabled-watcher" trap: a controller placed ON the panel it toggles never runs its
        // Update while that panel is hidden, so it can never show itself. Catch it loudly on scene load / edit.
        private void OnValidate()
        {
            if (panel == gameObject)
                Debug.LogError($"[{nameof(IdleKickWarningPopup)}] is on the SAME GameObject as its 'panel', which is " +
                    "off by default — its Update() will never run, so the warning can never appear. Move this component " +
                    "to an ALWAYS-ACTIVE object (e.g. TableHUD) and set 'panel' to the hidden popup GameObject.", this);
        }
#endif

        private void Awake()
        {
            if (table == null) table = FindAnyObjectByType<TableController>();
            if (betRepeater == null) betRepeater = FindAnyObjectByType<BetRepeater>();
            if (betButton != null) betButton.onClick.AddListener(OnBetClicked);
            if (leaveButton != null) leaveButton.onClick.AddListener(OnLeaveClicked);

            if (panel != null)
            {
                _panelRt = panel.GetComponent<RectTransform>();
                if (_panelRt != null) _shownPos = _panelRt.anchoredPosition;   // AUTHORED position = the shown/final spot
                _hiddenPos = _shownPos + (slideFromTop ? Vector2.up : Vector2.down) * slideDistance;
                _selfHosted = panel == gameObject;
                if (!_selfHosted && panel.activeSelf) panel.SetActive(false);  // hidden by default; the slide-in shows it
            }
        }

        private void OnDestroy()
        {
            if (betButton != null) betButton.onClick.RemoveListener(OnBetClicked);
            if (leaveButton != null) leaveButton.onClick.RemoveListener(OnLeaveClicked);
        }

        private void Update()
        {
            if (panel == null || table == null) return;

            bool show = table.AmIIdleKickWarned;
            if (show != _shown)
            {
                _shown = show;
                if (show) { RefreshContent(); Show(); }
                else Hide();
            }

            // Live countdown for the final betting window (same clock as BetTimerPopup, which hides while we're up).
            if (_shown && timerLabel != null)
                timerLabel.text = FormatWindow(table.Board);
        }

        // Seconds left in the current betting window, clamped to its configured length (the server stamps a generous
        // ceiling until /presented collapses it), rendered mm:ss + "s" — matches BetTimerPopup exactly.
        private static string FormatWindow(BoardSnapshot board)
        {
            double left = 0d;
            if (board?.BettingExpiresAt != null)
            {
                left = (board.BettingExpiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds;
                if (board.BettingDurationSeconds > 0) left = Math.Min(left, board.BettingDurationSeconds);
                if (left < 0d) left = 0d;
            }
            int total = Mathf.CeilToInt((float)left);
            return $"{total / 60:00}:{total % 60:00}s";
        }

        // Fill the labels + gate the bet button off the current table minimum and the player's balance. Called each
        // time the popup appears, so a table min that changed since last time is always current.
        private void RefreshContent()
        {
            if (messageLabel != null && !string.IsNullOrEmpty(message)) messageLabel.text = message;

            long min = MinBet();
            string minText = ChipView.Format(min);
            if (minBetLabel != null) minBetLabel.text = minText + minBetSuffix;
            if (betButtonLabel != null) betButtonLabel.text = betPrefix + minText;
            if (betButton != null) betButton.interactable = min > 0 && CanAfford(min);
        }

        private long MinBet() => table?.Board != null ? (long)table.Board.MinBet : 0;

        private static bool CanAfford(long amount)
            => WalletManager.Instance == null || WalletManager.Instance.Chips >= amount;   // null ⇒ let the server decide

        private void OnBetClicked()
        {
            if (table?.Board == null) return;
            decimal min = table.Board.MinBet;
            if (min <= 0m) return;

            // Behave EXACTLY like Repeat: drop a min chip on the felt (physics) and deal now — don't wait out the
            // betting timer. The popup hides on its own once the board comes back with the round in progress.
            if (betRepeater != null) { betRepeater.BetMinimumAndDeal(); return; }

            // Fallback (no BetRepeater wired): bare bet, no felt chips, and the betting window auto-deals at expiry.
            _ = table.PlaceBet(min);
        }

        private void OnLeaveClicked() => _ = table != null ? table.Leave() : System.Threading.Tasks.Task.CompletedTask;

        // ---- Juicy slide (from the top by default), same pattern as TurnPopup / BetTimerPopup / InsurancePopup ----

        private void Show()
        {
            if (_panelRt == null) { if (panel != null) panel.SetActive(true); return; }   // no RectTransform → plain toggle
            if (!_selfHosted && !panel.activeSelf) panel.SetActive(true);
            _panelRt.anchoredPosition = _hiddenPos;                 // start off-screen (top by default), then slide in
            StartSlide(_shownPos, deactivateAtEnd: false, showing: true);
        }

        private void Hide()
        {
            if (_panelRt == null) { if (panel != null) panel.SetActive(false); return; }
            StartSlide(_hiddenPos, deactivateAtEnd: !_selfHosted, showing: false);
        }

        private void StartSlide(Vector2 target, bool deactivateAtEnd, bool showing)
        {
            if (_slide != null) StopCoroutine(_slide);
            _slide = StartCoroutine(SlideTo(target, deactivateAtEnd, showing));
        }

        private IEnumerator SlideTo(Vector2 target, bool deactivateAtEnd, bool showing)
        {
            Vector2 start = _panelRt.anchoredPosition;
            float t = 0f;
            while (t < slideDuration && slideDuration > 0f)
            {
                t += Time.unscaledDeltaTime;
                float raw = Mathf.Clamp01(t / slideDuration);
                float k = showing ? UITween.EaseOutBack(raw, overshoot) : UITween.EaseInBack(raw, overshoot);
                _panelRt.anchoredPosition = Vector2.LerpUnclamped(start, target, k);
                yield return null;
            }
            _panelRt.anchoredPosition = target;
            _slide = null;
            if (deactivateAtEnd && panel != null) panel.SetActive(false);
        }
    }
}
