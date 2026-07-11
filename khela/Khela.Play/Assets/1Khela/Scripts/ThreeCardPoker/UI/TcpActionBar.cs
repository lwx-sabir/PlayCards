using System;
using PlayCard.ThreeCardPoker.Dtos;
using PlayCard.ThreeCardPoker.Table;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.ThreeCardPoker.UI
{
    /// <summary>
    /// The single 3CP in-round decision: PLAY (match the Ante) or FOLD (forfeit it; side bets still settle). Shows
    /// only while it's this player's turn to decide (<see cref="TcpTableController.CanDecide"/>) and hides the instant
    /// they act. Visibility toggles a <see cref="CanvasGroup"/> (NOT SetActive) so this watcher keeps its board
    /// subscription — the recurring disabled-watcher trap. Put this component on an always-active object.
    /// </summary>
    public sealed class TcpActionBar : MonoBehaviour
    {
        [SerializeField] private TcpTableController controller;
        [Tooltip("The buttons container to show/hide (its CanvasGroup is used). Defaults to this GameObject.")]
        [SerializeField] private GameObject panel;
        [SerializeField] private Button playButton;
        [SerializeField] private Button foldButton;
        [Tooltip("Optional Play/Fold countdown label (seconds).")]
        [SerializeField] private TMP_Text timerLabel;

        private CanvasGroup _group;

        private void Awake()
        {
            if (panel == null) panel = gameObject;
            _group = panel.GetComponent<CanvasGroup>() ?? panel.AddComponent<CanvasGroup>();
            Hide();
            if (playButton != null) playButton.onClick.AddListener(OnPlay);
            if (foldButton != null) foldButton.onClick.AddListener(OnFold);
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
            if (controller.CanDecide) Show(); else Hide();
        }

        private void Update()
        {
            if (timerLabel == null || _group.alpha <= 0f) return;
            var board = controller != null ? controller.Board : null;
            if (board?.DecideEpochMs == null) { timerLabel.text = string.Empty; return; }
            double nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            double left = (board.DecideEpochMs.Value - nowMs) / 1000.0;
            timerLabel.text = left > 0 ? Mathf.CeilToInt((float)left).ToString() : "0";
        }

        private void OnPlay() { Hide(); _ = controller.Play(); }   // hide immediately for snappy feedback; the board confirms
        private void OnFold() { Hide(); _ = controller.Fold(); }

        private void Show() { _group.alpha = 1f; _group.interactable = true; _group.blocksRaycasts = true; }
        private void Hide() { _group.alpha = 0f; _group.interactable = false; _group.blocksRaycasts = false; }
    }
}
