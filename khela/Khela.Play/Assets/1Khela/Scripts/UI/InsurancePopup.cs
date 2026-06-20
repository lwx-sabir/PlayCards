using System;
using System.Collections;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The "Insurance Bet?" popup controller. The moment the insurance window opens — dealer's UP card is an Ace,
    /// your hand still has its 2 dealt cards, and you haven't insured yet — it slides the <see cref="panel"/> up
    /// from the bottom. YES places insurance (half your bet); NO declines. It slides back down + hides once you
    /// insure, decline, or the window closes. View-only: the server re-validates.
    ///
    /// IMPORTANT: put this component on an **always-active** object (e.g. the HUD canvas), and assign
    /// <see cref="panel"/> = the popup visual, which it activates/deactivates on demand. So the popup visual can
    /// be **disabled by default in the scene** — the controller keeps running and turns it on when needed. (A
    /// disabled GameObject gets no OnEnable/Update, which is why the watcher can't live on the popup itself.)
    /// </summary>
    public sealed class InsurancePopup : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField] private TableController table;

        [Header("Refs")]
        [Tooltip("The popup VISUAL that slides + is shown/hidden. Put this on a SEPARATE object from this " +
                 "controller; it may be disabled by default.")]
        [SerializeField] private RectTransform panel;
        [SerializeField] private Button yesButton;
        [SerializeField] private Button noButton;
        [Tooltip("Optional: shows the insurance cost (half the bet).")]
        [SerializeField] private TMP_Text amountLabel;
        [SerializeField] private string amountFormat = "#,0";
        [Tooltip("Optional: shows the insurance window countdown.")]
        [SerializeField] private TMP_Text timerLabel;

        [Header("Slide")]
        [Tooltip("How far below the shown position to park when hidden (anchored units). Tune so it's off-screen.")]
        [SerializeField] private float slideDistance = 1000f;
        [SerializeField] private float slideDuration = 0.35f;

        private Vector2 _shownPos;
        private Vector2 _hiddenPos;
        private bool _shown;
        private bool _dismissed;     // player chose NO this offer — don't re-pop until the window closes
        private bool _selfHosted;    // panel is on this same object (fallback): can't deactivate without killing us
        private Coroutine _slide;

        private const int AceFaceVal = 14; // server FaceValue.Ace

        private void Awake()
        {
            if (panel == null) panel = transform as RectTransform;
            _shownPos = panel.anchoredPosition;                 // designed = SHOWN position (read even while inactive)
            _hiddenPos = _shownPos - new Vector2(0f, slideDistance);

            if (yesButton != null) yesButton.onClick.AddListener(OnYes);
            if (noButton != null) noButton.onClick.AddListener(OnNo);

            _selfHosted = panel.gameObject == gameObject;
            if (_selfHosted)
            {
                // Fallback: panel is on this same object — we can't deactivate it (that would kill this watcher),
                // so we just park it off-screen. For a clean disabled-by-default popup, put this controller on a
                // separate always-active object and point panel at the visual.
                Debug.LogWarning("[InsurancePopup] panel is on the same object as the controller — it will hide by " +
                                 "sliding off-screen, not by deactivating. Put the controller on a separate active object.");
                panel.anchoredPosition = _hiddenPos;
            }
            else
            {
                panel.gameObject.SetActive(false);              // hidden by default; the controller shows it on demand
            }
        }

        private void OnEnable()
        {
            if (table == null) { Debug.LogWarning("[InsurancePopup] No TableController assigned."); return; }
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
            var exp = table != null && table.Board != null ? table.Board.InsuranceExpiresAt : null;
            double remaining = exp.HasValue ? (exp.Value - DateTimeOffset.UtcNow).TotalSeconds : 0d;
            timerLabel.text = remaining > 0d ? $"{remaining:0}s" : "0s";
        }

        private void Apply(BoardSnapshot board)
        {
            var hand = InsuranceHand(board);   // non-null only while the insurance window is open
            if (hand == null)
            {
                _dismissed = false;            // window closed — ready for the next round's offer
                if (_shown) Hide();
                return;
            }

            if (_dismissed) return;            // already said NO this offer
            if (amountLabel != null) amountLabel.text = (hand.Bet / 2m).ToString(amountFormat);
            if (!_shown) Show();
        }

        // The local player's main hand IFF insurance is offerable right now, else null. Insurance is a PRE-PLAY
        // decision offered to every dealt player when the dealer shows an Ace — NOT gated on whose turn it is.
        private HandView InsuranceHand(BoardSnapshot board)
        {
            if (board == null || !board.RoundInProgress || !board.InsuranceExpiresAt.HasValue) return null;
            if (!DealerShowsAce(board)) return null;

            var me = board.Seats?.Find(s => s.SeatNumber == table.MySeat)?.Player;
            if (me?.Hands == null || me.Hands.Count == 0) return null;
            var hand = me.Hands[0];   // insurance is on the main hand (decided before any split)

            // Only while the hand is untouched: its 2 dealt cards, not yet acted, not yet insured.
            if (hand.Cards == null || hand.Cards.Count != 2 || hand.Done || hand.Insurance != 0m) return null;
            return hand;
        }

        private static bool DealerShowsAce(BoardSnapshot board)
        {
            var cards = board?.Dealer?.Cards;
            if (cards == null) return false;
            foreach (var c in cards)
                if (c.IsCardUp && c.FaceVal == AceFaceVal) return true;
            return false;
        }

        private void OnYes()
        {
            var hand = InsuranceHand(table != null ? table.Board : null);
            if (hand == null) { Hide(); return; }
            _dismissed = true;                 // suppress re-pop until the board reflects the placed insurance
            Hide();
            _ = table.Insurance(hand.Bet / 2m);
        }

        private void OnNo()
        {
            _dismissed = true;
            Hide();
            if (table != null) _ = table.DeclineInsurance();   // tell the server so the window can close early
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
