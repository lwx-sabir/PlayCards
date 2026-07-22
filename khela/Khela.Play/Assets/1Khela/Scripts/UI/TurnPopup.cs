using System;
using System.Collections;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using TMPro;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// The "Your Turn" popup. Slides up while it's the LOCAL player's turn (the same window the camera closes in for
    /// and the other-player avatars fade out for) and slides away when the turn passes or the round ends. Shows the
    /// turn countdown as mm:ss + "s" (e.g. 00:15s). View-only — actions go through the action bar; this just signals.
    ///
    /// IMPORTANT (same rule as InsurancePopup): put this controller on an ALWAYS-ACTIVE object (e.g. TableHUD) and
    /// assign <see cref="panel"/> = the Turn_Popup visual, which it activates/deactivates. The visual may be disabled
    /// by default — a disabled object gets no Update, so the watcher can't live on the popup itself.
    /// </summary>
    public sealed class TurnPopup : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField] private TableController table;
        [Tooltip("The table view — holds the prompt until MY cards AND the dealer's have landed (the dealer deals last, " +
                 "so that's the whole deal finished). Optional (null = pops straight off the board).")]
        [SerializeField] private BlackjackTableView view;

        [Header("Refs")]
        [Tooltip("The popup VISUAL that slides + is shown/hidden. Put on a SEPARATE object from this controller; it may be disabled by default.")]
        [SerializeField] private RectTransform panel;
        [Tooltip("Turn countdown label, shown as mm:ss + \"s\" (e.g. 00:15s).")]
        [SerializeField] private TMP_Text timerLabel;

        [Header("Slide")]
        [Tooltip("How far below the shown position to park when hidden (anchored units). Tune so it's off-screen.")]
        [SerializeField] private float slideDistance = 1000f;
        [SerializeField] private float slideDuration = 0.35f;

        private Vector2 _shownPos;
        private Vector2 _hiddenPos;
        private bool _shown;
        private bool _selfHosted;    // panel is on this same object (fallback): can't deactivate without killing us
        private Coroutine _slide;

        private void Awake()
        {
            if (panel == null) panel = transform as RectTransform;
            _shownPos = panel.anchoredPosition;                 // designed = SHOWN position (read even while inactive)
            _hiddenPos = _shownPos - new Vector2(0f, slideDistance);

            _selfHosted = panel.gameObject == gameObject;
            if (_selfHosted)
            {
                Debug.LogWarning("[TurnPopup] panel is on the same object as the controller — it will hide by sliding " +
                                 "off-screen, not by deactivating. Put the controller on a separate always-active object.");
                panel.anchoredPosition = _hiddenPos;
            }
            else
            {
                panel.gameObject.SetActive(false);              // hidden by default; the controller shows it on demand
            }
        }

        private void OnEnable()
        {
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>();   // so the deal-landed gate can't be silently bypassed
            if (table == null) { Debug.LogWarning("[TurnPopup] No TableController assigned."); return; }
            table.OnBoardChanged += Apply;
            Apply(table.Board);
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= Apply;
        }

        private bool _lastReady;

        private void Update()
        {
            // Cards land BETWEEN board pushes, so re-evaluate when my decision inputs (my cards + the dealer's) settle —
            // Apply is board-driven, so without this the prompt would never appear for a turn that started mid-deal.
            if (table != null && view != null)
            {
                bool ready = view.DecisionReady(table.MySeat);
                if (ready != _lastReady) { _lastReady = ready; Apply(table.Board); }
            }

            if (!_shown || timerLabel == null) return;
            var board = table != null ? table.Board : null;
            var exp = board != null ? board.TurnExpiresAt : null;
            double remaining = exp.HasValue ? (exp.Value - DateTimeOffset.UtcNow).TotalSeconds : 0d;
            // Clamp to a real turn — the deadline is a generous ceiling until our /presented call collapses it.
            if (board != null && board.TurnDurationSeconds > 0)
                remaining = Math.Min(remaining, board.TurnDurationSeconds);
            timerLabel.text = Format(remaining);
        }

        // mm:ss + "s", e.g. 15.3s → "00:15s". Ceil so the first whole second shows the full value.
        private static string Format(double seconds)
        {
            if (seconds < 0d) seconds = 0d;
            int total = Mathf.CeilToInt((float)seconds);
            return $"{total / 60:00}:{total % 60:00}s";
        }

        private void Apply(BoardSnapshot board)
        {
            // Gate on the ANIMATION: only prompt once MY cards AND the DEALER's have landed — the dealer is dealt LAST,
            // so that means the whole deal has finished. Without this the prompt pops the instant the server says it's
            // my turn, while the dealer's second/hole card is still in the air.
            bool myTurn = table != null && table.IsMyTurn
                          && (view == null || view.DecisionReady(table.MySeat));
            if (myTurn) { if (!_shown) Show(); }
            else        { if (_shown) Hide(); }
        }

        private void Show()
        {
            _shown = true;
            if (!_selfHosted && !panel.gameObject.activeSelf) panel.gameObject.SetActive(true);
            panel.anchoredPosition = _hiddenPos;               // start below, then slide up
            StartSlide(_shownPos, deactivateAtEnd: false);
        }

        private void Hide()
        {
            _shown = false;
            StartSlide(_hiddenPos, deactivateAtEnd: !_selfHosted);
        }

        private void StartSlide(Vector2 target, bool deactivateAtEnd)
        {
            if (_slide != null) StopCoroutine(_slide);
            _slide = StartCoroutine(SlideTo(target, deactivateAtEnd));
        }

        private IEnumerator SlideTo(Vector2 target, bool deactivateAtEnd)
        {
            Vector2 start = panel.anchoredPosition;
            float t = 0f;
            while (t < slideDuration && slideDuration > 0f)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.SmoothStep(0f, 1f, t / slideDuration);
                panel.anchoredPosition = Vector2.LerpUnclamped(start, target, k);
                yield return null;
            }
            panel.anchoredPosition = target;
            _slide = null;
            if (deactivateAtEnd) panel.gameObject.SetActive(false);
        }
    }
}
