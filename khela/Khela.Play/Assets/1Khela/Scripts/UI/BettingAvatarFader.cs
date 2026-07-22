using System.Collections;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Smoothly fades the other-player avatar cards OUT while the camera is CLOSED IN (betting + the local player's
    /// turn) and back IN whenever it pulls WIDE — including the deal-in and the whole round-end ceremony. It mirrors
    /// <see cref="TableCameraController"/>'s "close" state EXACTLY (same betting / my-turn / RoundEndSettling /
    /// DecisionReady rules), so the fade and the camera move together and the avatars always come back when the FOV
    /// releases. Put this on the parent that holds the avatar layouts (NOT the local bottom HUD) — it adds a
    /// CanvasGroup and tweens its alpha.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class BettingAvatarFader : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [Tooltip("The table view — so the fade tracks the camera through the DEAL and the ROUND-END (DecisionReady / " +
                 "RoundEndSettling), not just the raw board. Auto-found if empty.")]
        [SerializeField] private BlackjackTableView view;
        [SerializeField] private CanvasGroup group;
        [Tooltip("Fade time in seconds — match the camera's Move Time so they move together.")]
        [SerializeField] private float fadeDuration = 0.45f;
        [Tooltip("Optional: the LOCAL player's avatar CanvasGroup. If set, it fades together with the other-player " +
                 "avatars (same alpha + timing); leave empty to keep your own avatar visible.")]
        [SerializeField] private CanvasGroup playerAvatar;

        private Coroutine _fade;
        private bool _closeInit;
        private bool _lastClose;

        private void Awake() { if (group == null) group = GetComponent<CanvasGroup>(); }

        private void OnEnable()
        {
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>();
            if (table != null) table.OnBoardChanged += OnBoard;
            Refresh();
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board) => Refresh();

        // DecisionReady + RoundEndSettling change BETWEEN board pushes (cards land; the round-end ceremony ends), so a
        // board-only trigger would leave the fade a beat behind the camera. Re-check every frame; the change-guard in
        // Refresh keeps it free when nothing moved.
        private void Update() => Refresh();

        // TRUE when the camera is closed in — betting, OR my turn once my cards have landed. It stays WIDE (false)
        // through the deal-in and the whole round-end ceremony, so this must NOT treat those as "betting". Matches
        // TableCameraController.Resolve exactly. The OLD check used raw !RoundInProgress, so it faded the avatars out
        // the instant a round settled and left them out through the entire round-end — the "never comes back" bug.
        private bool ComputeClose()
        {
            var board = table != null ? table.Board : null;
            if (board == null) return false;
            int seat = table != null ? table.MySeat : -1;
            bool roundEndSettling = view != null && view.RoundEndSettling;
            bool dealt = view == null || view.DecisionReady(seat);
            bool betting = !board.RoundInProgress && !roundEndSettling;
            bool myTurn = board.RoundInProgress && seat >= 1 && board.CurrentSeatNumber == seat && dealt;
            return betting || myTurn;
        }

        private void Refresh()
        {
            bool close = ComputeClose();
            if (_closeInit && close == _lastClose) return;
            _closeInit = true;
            _lastClose = close;
            FadeTo(close ? 0f : 1f);
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
