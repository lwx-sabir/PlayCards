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
            if (table == null) { Debug.LogWarning("[TurnPopup] No TableController assigned."); return; }
            table.OnBoardChanged += Apply;
            Apply(table.Board);
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= Apply;
        }

        private void Update()
        {
            if (!_shown || timerLabel == null) return;
            var exp = table != null && table.Board != null ? table.Board.TurnExpiresAt : null;
            double remaining = exp.HasValue ? (exp.Value - DateTimeOffset.UtcNow).TotalSeconds : 0d;
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
            bool myTurn = table != null && table.IsMyTurn;   // RoundInProgress && my seat is the current turn
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
