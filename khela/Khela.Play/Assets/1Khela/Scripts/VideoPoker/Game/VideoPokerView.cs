using System.Collections.Generic;
using PlayCard.VideoPoker.Dtos;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.VideoPoker.Game
{
    /// <summary>
    /// Renders a <see cref="VideoPokerController"/>'s board onto the machine UI — the five <see cref="CardSlot"/>s, the
    /// message / balance / win labels, the paytable, and the single deal/draw button. Server-authoritative: it only
    /// draws board snapshots the controller hands it and forwards hold taps back; it decides nothing. Uses TextMeshPro
    /// (swap for UnityEngine.UI.Text if your HUD doesn't use TMP).
    /// </summary>
    public sealed class VideoPokerView : MonoBehaviour
    {
        [Header("Cards")]
        [Tooltip("The five card slots, left → right.")]
        [SerializeField] private CardSlot[] slots = new CardSlot[5];

        [Header("Labels")]
        [SerializeField] private TMP_Text variantLabel;
        [SerializeField] private TMP_Text messageLabel;
        [SerializeField] private TMP_Text balanceLabel;
        [SerializeField] private TMP_Text betLabel;
        [SerializeField] private TMP_Text winLabel;

        [Header("Action")]
        [SerializeField] private Button actionButton;
        [SerializeField] private TMP_Text actionLabel;

        [Header("Paytable + juice (optional)")]
        [SerializeField] private PaytableView paytable;
        [Tooltip("Enabled on a win — wire your UIParticle coin-shower here (see chip loading overlay / client-button-feel).")]
        [SerializeField] private GameObject winCelebration;

        private VideoPokerController _controller;

        /// <summary>Called by the controller on Start — subscribe, wire the button + slots.</summary>
        public void Bind(VideoPokerController controller)
        {
            _controller = controller;
            controller.OnBoardChanged += Render;
            controller.OnActionError += ShowError;
            controller.OnBusyChanged += SetBusy;

            if (actionButton != null)
            {
                actionButton.onClick.RemoveListener(controller.DealOrDraw);
                actionButton.onClick.AddListener(controller.DealOrDraw);
            }
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;
                slots[i].Index = i;
                slots[i].OnHoldToggled += OnSlotHoldToggled;
                slots[i].Clear();
            }
            SetMessage("Press deal");
            SetActionLabel("Deal");
            if (winCelebration != null) winCelebration.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_controller == null) return;
            _controller.OnBoardChanged -= Render;
            _controller.OnActionError -= ShowError;
            _controller.OnBusyChanged -= SetBusy;
        }

        public void SetVariants(List<VpVariantSummary> variants, string selectedId)
        {
            if (paytable == null || variants == null) return;
            var v = variants.Find(x => x.Id == selectedId) ?? (variants.Count > 0 ? variants[0] : null);
            if (v != null) paytable.Show(v, _controller != null ? _controller.Coins : v.MaxCoins);
        }

        private void Render(VpBoard board)
        {
            if (board == null) return;
            if (variantLabel) variantLabel.text = board.VariantName;
            if (balanceLabel) balanceLabel.text = board.Balance.ToString("N0");
            if (betLabel) betLabel.text = board.Bet.ToString("N0");

            var cards = board.Cards;
            bool dealt = board.IsDealt;

            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot == null || cards == null || i >= cards.Count) continue;
                var card = cards[i].ToCardId(true);
                bool wasHeld = board.Hold != null && i < board.Hold.Length && board.Hold[i];

                if (dealt)
                {
                    slot.Show(card, flip: true);       // deal-in
                    slot.SetHeld(false);
                    slot.SetHoldEnabled(true);
                }
                else   // complete: held cards stay put, the rest were replaced
                {
                    if (wasHeld) { slot.Set(card); slot.SetHeld(true); }
                    else slot.Show(card, flip: true);
                    slot.SetHoldEnabled(false);
                }
            }

            if (dealt)
            {
                SetMessage("Hold cards, then draw");
                SetActionLabel("Draw");
                if (winLabel) winLabel.text = "";
                if (winCelebration) winCelebration.SetActive(false);
                if (paytable) paytable.HighlightWinning(null);
            }
            else
            {
                SetMessage(board.IsWin ? $"{Humanize(board.Category)} — win" : Humanize(board.Category));
                SetActionLabel("Deal");
                if (winLabel) winLabel.text = board.IsWin ? "+" + board.Payout.ToString("N0") : "";
                if (winCelebration) winCelebration.SetActive(board.IsWin);
                if (paytable) paytable.HighlightWinning(board.IsWin ? board.Category : null);
            }
        }

        private void OnSlotHoldToggled(int index)
        {
            if (_controller == null || index < 0 || index >= slots.Length) return;
            _controller.SetHold(index, slots[index].Held);
        }

        private void SetBusy(bool busy) { if (actionButton) actionButton.interactable = !busy; }
        private void ShowError(string message) => SetMessage(string.IsNullOrEmpty(message) ? "Something went wrong" : message);
        private void SetMessage(string m) { if (messageLabel) messageLabel.text = m; }
        private void SetActionLabel(string m) { if (actionLabel) actionLabel.text = m; }

        // "RoyalFlush" -> "Royal flush" etc. (server sends the enum name).
        private static string Humanize(string category)
        {
            if (string.IsNullOrEmpty(category)) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < category.Length; i++)
            {
                char c = category[i];
                if (i > 0 && char.IsUpper(c)) sb.Append(' ');
                sb.Append(i == 0 ? char.ToUpper(c) : char.ToLower(c));
            }
            return sb.ToString();
        }
    }
}
