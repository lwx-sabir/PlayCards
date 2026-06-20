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
            bool betting = board != null && !board.RoundInProgress;   // betting → faded out; in-round → faded in
            FadeTo(betting ? 0f : 1f);
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
                group.alpha = Mathf.Lerp(start, target, Mathf.SmoothStep(0f, 1f, t / fadeDuration));
                yield return null;
            }
            group.alpha = target;
            if (target > 0.99f) SetInteractable(true);                // fully shown → input back on
            _fade = null;
        }

        private void Apply(float a)
        {
            group.alpha = a;
            SetInteractable(a > 0.99f);
        }

        private void SetInteractable(bool on)
        {
            group.blocksRaycasts = on;
            group.interactable = on;
        }
    }
}
