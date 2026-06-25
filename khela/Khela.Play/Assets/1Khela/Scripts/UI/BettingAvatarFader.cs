using System.Collections;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Smoothly fades the other-player avatar cards OUT while betting (when the camera eases in to the bet pose)
    /// and back IN when the round starts and the camera returns to the table pose. Both react to the same board
    /// change as the camera, so the fade and the camera move stay in sync. Put this on the parent that holds the
    /// avatar layouts (NOT the local bottom HUD) — it adds a CanvasGroup and tweens its alpha.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class BettingAvatarFader : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [SerializeField] private CanvasGroup group;
        [Tooltip("Fade time in seconds — match the camera's Move Time so they move together.")]
        [SerializeField] private float fadeDuration = 0.45f;
        [Tooltip("Optional: the LOCAL player's avatar CanvasGroup. If set, it fades together with the other-player " +
                 "avatars (same alpha + timing); leave empty to keep your own avatar visible.")]
        [SerializeField] private CanvasGroup playerAvatar;

        private Coroutine _fade;

        private void Awake() { if (group == null) group = GetComponent<CanvasGroup>(); }

        private void OnEnable()
        {
            if (table == null) return;
            table.OnBoardChanged += OnBoard;
            if (table.Board != null) OnBoard(table.Board);
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            // Fade the other-player avatars OUT both while BETTING and while it's the local player's TURN (the camera
            // closes in for both), and back IN otherwise — kept in sync with TableCameraController's `close`.
            int seat = table != null ? table.MySeat : -1;
            bool betting = board != null && !board.RoundInProgress;
            bool myTurn = board != null && board.RoundInProgress && seat >= 1 && board.CurrentSeatNumber == seat;
            FadeTo((betting || myTurn) ? 0f : 1f);
        }

        private void FadeTo(float target)
        {
            if (group == null) return;
            if (!isActiveAndEnabled) { Apply(target); return; }       // can't run a coroutine while inactive
            if (_fade != null) StopCoroutine(_fade);
            _fade = StartCoroutine(FadeRoutine(target));
        }

        private IEnumerator FadeRoutine(float target)
        {
            float start = group.alpha;
            if (target < start) SetInteractable(false);               // dropping → kill input immediately

            float t = 0f;
            while (t < fadeDuration && fadeDuration > 0f)
            {
                t += Time.unscaledDeltaTime;
                SetAlpha(Mathf.Lerp(start, target, Mathf.SmoothStep(0f, 1f, t / fadeDuration)));
                yield return null;
            }
            SetAlpha(target);
            if (target > 0.99f) SetInteractable(true);                // fully shown → input back on
            _fade = null;
        }

        private void Apply(float a)
        {
            SetAlpha(a);
            SetInteractable(a > 0.99f);
        }

        // Drive the other-player group AND (if assigned) the local player's avatar together.
        private void SetAlpha(float a)
        {
            group.alpha = a;
            if (playerAvatar != null) playerAvatar.alpha = a;
        }

        private void SetInteractable(bool on)
        {
            group.blocksRaycasts = on;
            group.interactable = on;
            if (playerAvatar != null) { playerAvatar.blocksRaycasts = on; playerAvatar.interactable = on; }
        }
    }
}
