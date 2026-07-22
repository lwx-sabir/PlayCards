using System;
using System.Collections;
using UnityEngine;

namespace PlayCard.Game.Cards
{
    /// <summary>
    /// Card juice settings — authored ONCE on the DealerAnimator (she drives every reveal and peek), not per card
    /// prefab. Passed into <see cref="CardFlip"/>, which is a pure runtime executor with no settings of its own.
    /// </summary>
    [Serializable]
    public sealed class CardFlipTuning
    {
        [Header("Reveal flip")]
        [Tooltip("How far the card lifts off the felt at the mid-point (local units). Peaks at the apex, back to 0 flat.")]
        public float lift = 0.05f;
        [Tooltip("Scale 'pop' toward the apex (1 = none, 1.12 = 12% bigger), settling back to rest.")]
        public float scalePop = 1.12f;
        [Tooltip("Landing spring: ease-out-back strength on the return. 0 = clean stop, 1.7 = classic overshoot, higher = springier.")]
        public float overshoot = 1.7f;

        [Header("Peek (she checks the hole card)")]
        [Tooltip("Local-euler the near edge tilts up TOWARD THE DEALER. Keep it away from the player camera so the face " +
                 "is never flashed (e.g. tilt the top edge up on X).")]
        public Vector3 peekTiltEuler = new Vector3(-24f, 0f, 0f);
        [Tooltip("Lift during the peek (local units).")]
        public float peekLift = 0.03f;
        [Tooltip("Time to tilt up, seconds.")]
        public float peekInSeconds = 0.28f;
        [Tooltip("Hold at the top (she reads it), seconds.")]
        public float peekHoldSeconds = 0.35f;
        [Tooltip("Time to lower back, seconds.")]
        public float peekOutSeconds = 0.22f;
    }

    /// <summary>
    /// Runs the card's flip + peek motion. Added AUTOMATICALLY at runtime (<see cref="On"/>) — you never put it on the
    /// card prefab, and it holds NO settings: all juice is authored on the DealerAnimator and passed in, so there's one
    /// place to tune.
    ///
    /// The card is a single-sided TEXTURE swap, so <see cref="Reveal"/> FAKES the turn: rotate edge-on (~90°, the thin
    /// edge to camera = invisible), swap the art AT THE APEX via the caller's callback, then rotate back flat with an
    /// ease-out-back OVERSHOOT + a scale pop + a lift off the felt. Ease-out family throughout (never linear), a
    /// different shape per beat, overshoot-and-settle on the landing.
    ///
    /// <see cref="PlayPeek"/> is the dealer secretly checking the hole card: tilt/lift the near edge TOWARD THE DEALER
    /// (never toward the player camera, so the face is never flashed), hold, lower. No shader, no reveal.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CardFlip : MonoBehaviour
    {
        private static readonly CardFlipTuning Fallback = new CardFlipTuning();

        private Coroutine _run;

        /// <summary>Get-or-add the executor on a card, so callers get the juice whether or not one exists yet.</summary>
        public static CardFlip On(GameObject card)
            => card.GetComponent<CardFlip>() ?? card.AddComponent<CardFlip>();

        /// <summary>
        /// Juicy reveal flip. Fires <paramref name="swapAtApex"/> exactly ONCE, at the invisible edge-on apex, to swap
        /// the face art + data. <paramref name="flipSeconds"/> is the total turn time and <paramref name="edgeEuler"/>
        /// the axis that turns the face away; <paramref name="tuning"/> supplies the lift / pop / overshoot (null =
        /// defaults). Yields until the flip and its settle finish — run it with <c>yield return</c> so the caller can
        /// await the reveal.
        /// </summary>
        public IEnumerator Reveal(Action swapAtApex, float flipSeconds, Vector3 edgeEuler, CardFlipTuning tuning)
        {
            var t = tuning ?? Fallback;
            var tr = transform;
            Vector3 restPos = tr.localPosition;
            Quaternion rest = tr.localRotation;
            Vector3 restScale = tr.localScale;
            Quaternion edge = rest * Quaternion.Euler(edgeEuler);
            float half = Mathf.Max(0.02f, flipSeconds * 0.5f);

            // 1) REST → EDGE-ON: accelerate into the turn (ease-in), rising + popping scale (both peak at the apex).
            float e = 0f;
            while (e < half)
            {
                e += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(e / half);
                tr.localRotation = Quaternion.SlerpUnclamped(rest, edge, k * k);            // ease-in
                tr.localPosition = restPos + Vector3.up * (t.lift * Mathf.Sin(k * Mathf.PI * 0.5f));
                tr.localScale = Vector3.LerpUnclamped(restScale, restScale * t.scalePop, k);
                yield return null;
            }
            tr.localRotation = edge;

            // 2) SWAP at the invisible apex — the face art turns over while the card is edge-on.
            swapAtApex?.Invoke();

            // 3) EDGE-ON → REST: ease-out-back so it springs a touch past flat and settles; scale + lift ease home.
            e = 0f;
            while (e < half)
            {
                e += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(e / half);
                tr.localRotation = Quaternion.SlerpUnclamped(edge, rest, EaseOutBack(k, t.overshoot));
                tr.localPosition = restPos + Vector3.up * (t.lift * Mathf.Sin((1f - k) * Mathf.PI * 0.5f));
                tr.localScale = Vector3.LerpUnclamped(restScale * t.scalePop, restScale, k);
                yield return null;
            }
            tr.localPosition = restPos;
            tr.localRotation = rest;
            tr.localScale = restScale;
        }

        /// <summary>Fire-and-forget PEEK: tilt the card up toward the dealer, hold, lower. Bind the dealer's peek clip
        /// event to trigger this on the hole card. Restarts cleanly if already peeking.</summary>
        public void PlayPeek(CardFlipTuning tuning)
        {
            if (_run != null) StopCoroutine(_run);
            _run = StartCoroutine(PeekRoutine(tuning ?? Fallback));
        }

        private IEnumerator PeekRoutine(CardFlipTuning t)
        {
            var tr = transform;
            Vector3 restPos = tr.localPosition;
            Quaternion rest = tr.localRotation;
            Vector3 upPos = restPos + Vector3.up * t.peekLift;
            Quaternion up = rest * Quaternion.Euler(t.peekTiltEuler);

            yield return Ease(tr, rest, up, restPos, upPos, t.peekInSeconds, true);      // tilt up (ease-out)
            if (t.peekHoldSeconds > 0f) yield return new WaitForSecondsRealtime(t.peekHoldSeconds);
            yield return Ease(tr, up, rest, upPos, restPos, t.peekOutSeconds, false);    // lower back (ease-in-out)

            tr.localPosition = restPos;
            tr.localRotation = rest;
            _run = null;
        }

        private static IEnumerator Ease(Transform tr, Quaternion fromR, Quaternion toR, Vector3 fromP, Vector3 toP,
                                        float seconds, bool easeOut)
        {
            float dur = Mathf.Max(0.01f, seconds);
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / dur);
                float e = easeOut ? 1f - (1f - k) * (1f - k) : Mathf.SmoothStep(0f, 1f, k);
                tr.localPosition = Vector3.LerpUnclamped(fromP, toP, e);
                tr.localRotation = Quaternion.SlerpUnclamped(fromR, toR, e);
                yield return null;
            }
            tr.localPosition = toP;
            tr.localRotation = toR;
        }

        // Overshoot-and-settle: crosses 1 near the end and springs back. s = back strength (0 none, ~1.7 classic).
        private static float EaseOutBack(float x, float s)
        {
            float c3 = s + 1f;
            float p = x - 1f;
            return 1f + c3 * p * p * p + s * p * p;
        }
    }
}
