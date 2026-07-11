using System;
using PlayCard.ThreeCardPoker.Dtos;
using PlayCard.ThreeCardPoker.Table;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.ThreeCardPoker.UI
{
    /// <summary>
    /// Pre-deal betting for the four 3CP circles — Ante (mandatory) + the three optional side bets (Pair Plus,
    /// Prime, 6-Card). A simple +/- stepper per circle (stepped by the table's per-circle minimum, clamped to its
    /// maximum) and a DEAL button that submits <see cref="TcpTableController.PlaceBetsAndDeal"/>. Shown only while
    /// betting is open (before a round, or after one settled) and hidden during the acting phase. Visibility toggles
    /// a <see cref="CanvasGroup"/> so this watcher keeps its board subscription (put it on an always-active object).
    ///
    /// This is a functional, button-driven bet UI; the 3D chip drag-and-drop (blackjack's <c>BetBuilder</c>/chip rail)
    /// onto four bet spots is a later polish.
    /// </summary>
    public sealed class TcpBetPanel : MonoBehaviour
    {
        [SerializeField] private TcpTableController controller;
        [Tooltip("The betting panel to show/hide (its CanvasGroup is used). Defaults to this GameObject.")]
        [SerializeField] private GameObject panel;

        [Header("Amount labels")]
        [SerializeField] private TMP_Text anteLabel;
        [SerializeField] private TMP_Text pairPlusLabel;
        [SerializeField] private TMP_Text primeLabel;
        [SerializeField] private TMP_Text sixCardLabel;
        [Tooltip("Optional total-stake label.")]
        [SerializeField] private TMP_Text totalLabel;

        [Header("Steppers (optional per circle)")]
        [SerializeField] private Button anteUp, anteDown;
        [SerializeField] private Button pairPlusUp, pairPlusDown;
        [SerializeField] private Button primeUp, primeDown;
        [SerializeField] private Button sixCardUp, sixCardDown;

        [Header("Actions")]
        [SerializeField] private Button dealButton;
        [SerializeField] private Button clearButton;

        [Header("Fallback limits (used until a board arrives)")]
        // NOTE: Unity's serializer does NOT support decimal, so these inspector fields are long (chips are whole
        // numbers) and widen to decimal in the accessors below.
        [SerializeField] private long defaultAnteMin = 1000;
        [SerializeField] private long defaultAnteMax = 10000;
        [SerializeField] private long defaultSideMin = 1000;
        [SerializeField] private long defaultSideMax = 10000;
        [SerializeField] private string amountFormat = "#,0";

        private CanvasGroup _group;
        private decimal _ante, _pairPlus, _prime, _sixCard;
        private bool _busy;

        private decimal AnteMin => controller?.Board?.Limits?.AnteMin ?? defaultAnteMin;
        private decimal AnteMax => controller?.Board?.Limits?.AnteMax ?? defaultAnteMax;
        private decimal SideMin => controller?.Board?.Limits?.SideMin ?? defaultSideMin;
        private decimal SideMax => controller?.Board?.Limits?.SideMax ?? defaultSideMax;

        private void Awake()
        {
            if (panel == null) panel = gameObject;
            _group = panel.GetComponent<CanvasGroup>() ?? panel.AddComponent<CanvasGroup>();

            Wire(anteUp,   () => Step(ref _ante, AnteMin, 0m, AnteMax));
            Wire(anteDown, () => Step(ref _ante, -AnteMin, 0m, AnteMax));
            Wire(pairPlusUp,   () => Step(ref _pairPlus, SideMin, 0m, SideMax));
            Wire(pairPlusDown, () => Step(ref _pairPlus, -SideMin, 0m, SideMax));
            Wire(primeUp,   () => Step(ref _prime, SideMin, 0m, SideMax));
            Wire(primeDown, () => Step(ref _prime, -SideMin, 0m, SideMax));
            Wire(sixCardUp,   () => Step(ref _sixCard, SideMin, 0m, SideMax));
            Wire(sixCardDown, () => Step(ref _sixCard, -SideMin, 0m, SideMax));
            Wire(clearButton, ClearBets);
            if (dealButton != null) dealButton.onClick.AddListener(OnDeal);

            Hide();
        }

        private void OnEnable()
        {
            if (controller == null) return;
            controller.OnBoardChanged += OnBoard;
            if (controller.Board != null) OnBoard(controller.Board);
        }

        private void OnDisable()
        {
            if (controller != null) controller.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(TcpBoard board)
        {
            // Default the ante to the table minimum the first time we see a board with no ante set yet.
            if (_ante <= 0m) _ante = AnteMin;
            if (controller.CanBet) Show(); else Hide();
            RefreshLabels();
        }

        private void OnDeal()
        {
            if (_busy || controller == null) return;
            if (_ante < AnteMin) return;   // Ante is required to be dealt
            _busy = true;
            Hide();
            _ = DealRoutine();
        }

        private async System.Threading.Tasks.Task DealRoutine()
        {
            try { await controller.PlaceBetsAndDeal(_ante, _pairPlus, _prime, _sixCard); }
            catch (Exception ex) { Debug.LogWarning($"[TcpBetPanel] deal failed: {ex.Message}"); }
            finally { _busy = false; }
        }

        private void Step(ref decimal value, decimal delta, decimal min, decimal max)
        {
            value = Math.Max(min, Math.Min(max, value + delta));
            RefreshLabels();
        }

        private void ClearBets()
        {
            _ante = AnteMin; _pairPlus = 0m; _prime = 0m; _sixCard = 0m;
            RefreshLabels();
        }

        private void RefreshLabels()
        {
            if (anteLabel != null) anteLabel.text = _ante.ToString(amountFormat);
            if (pairPlusLabel != null) pairPlusLabel.text = _pairPlus.ToString(amountFormat);
            if (primeLabel != null) primeLabel.text = _prime.ToString(amountFormat);
            if (sixCardLabel != null) sixCardLabel.text = _sixCard.ToString(amountFormat);
            if (totalLabel != null) totalLabel.text = (_ante + _pairPlus + _prime + _sixCard).ToString(amountFormat);
            if (dealButton != null) dealButton.interactable = !_busy && _ante >= AnteMin;
        }

        private static void Wire(Button b, Action onClick)
        {
            if (b != null) b.onClick.AddListener(() => onClick());
        }

        private void Show() { _group.alpha = 1f; _group.interactable = true; _group.blocksRaycasts = true; }
        private void Hide() { _group.alpha = 0f; _group.interactable = false; _group.blocksRaycasts = false; }
    }
}
