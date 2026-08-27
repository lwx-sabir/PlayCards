using System;
using System.Collections.Generic;
using DG.Tweening;
using PlayCard.UI.RewardFly;
using UnityEngine;

namespace PlayCard.Store
{
    /// <summary>
    /// Reveals the player's info card in the SHOP when a reward is on its way to it, and switches on only the counters
    /// that reward will actually land in.
    ///
    /// A separate component on purpose. <c>UserInfoBinder</c> is a shared prefab dropped into Home, the Lobby and the
    /// Table, and none of those want a card that hides itself or knows what a purchase is. Teaching the shared
    /// component about the shop would put that behaviour — and a reference to the shop — into every copy of it. This
    /// lives on the shop's INSTANCE alone; delete it and the card is an ordinary always-there card again.
    ///
    /// The dependency also points the right way: the shop knows about a UI card, never the other way round.
    ///
    /// ⚠ The card's GameObject must stay ACTIVE. A disabled component hears nothing, so it could never learn a reward
    /// was coming — this hides the card itself (alpha 0, raycasts off) rather than switching it off. That is also what
    /// keeps its counters out of the fly registry until they are wanted.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShopUserInfoReveal : MonoBehaviour
    {
        [Header("Who announces the rewards")]
        [Tooltip("The purchase ceremony. It says what the server granted BEFORE any of it flies, which is the only " +
                 "moment early enough to switch a counter on — a RewardFlyTarget registers itself in OnEnable, so one " +
                 "that is off is not a destination yet. Empty = found in the parents.")]
        [SerializeField] private PurchaseView purchaseView;

        [Header("What to switch on")]
        [Tooltip("Vip_Hit_Target — enabled only when VIP points are actually coming.")]
        [SerializeField] private GameObject vipTarget;
        [Tooltip("Xp_Hit_Target — enabled only when XP is actually coming.")]
        [SerializeField] private GameObject xpTarget;
        [Tooltip("Xp_Text — off by default, shown alongside the XP counter when XP is on its way.")]
        [SerializeField] private GameObject xpTextObject;

        [Header("Reward ids (as the SERVER spells them)")]
        [SerializeField] private string vipRewardId = "VipPoints";
        [SerializeField] private string xpRewardId = "Xp";

        [Header("Reveal")]
        [Tooltip("The group that fades. Empty = this object's own CanvasGroup, added if it has none.")]
        [SerializeField] private CanvasGroup group;
        [SerializeField] private float revealSeconds = 0.32f;
        [Tooltip("Scale it grows from. 1 = fade only.")]
        [SerializeField] private float fromScale = 0.88f;
        [Tooltip("Where it travels IN from, as a fraction of the card's OWN size — (0,-1) is one card-height below, " +
                 "(-1,0) is one card-width to the left. Proportional rather than pixels because this canvas is " +
                 "2560x1440: 40px is under 3% of the height and reads as no movement at all.")]
        [SerializeField] private Vector2 enterFrom = new Vector2(0f, -0.9f);
        [Tooltip("Where it travels OUT to, same units. Deliberately NOT the reverse of the entrance — leaving the way " +
                 "it came reads as a rewind. Sideways out is the usual answer.")]
        [SerializeField] private Vector2 exitTo = new Vector2(0.7f, 0.25f);

        [Header("Hide again")]
        [Tooltip("How long it stays after the LAST reward has finished landing. Long enough to read the new number, " +
                 "short enough that it does not sit there as furniture.")]
        [SerializeField] private float hideAfterSeconds = 1.2f;
        [Tooltip("Hide regardless after this long. The flight is someone else's coroutine and its end is not a " +
                 "guarantee — without a backstop a dropped burst would leave the card up for the whole session.")]
        [SerializeField] private float maxVisibleSeconds = 15f;

        private Tween tween;
        private bool revealed;
        private Vector2 home;
        private Coroutine hideRoutine;
        /// <summary>The rewards this reveal is waiting on. Empty again = everything has landed.</summary>
        private readonly System.Collections.Generic.HashSet<string> pending =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void Awake()
        {
            if (purchaseView == null) purchaseView = GetComponentInParent<PurchaseView>();
            if (group == null && !TryGetComponent(out group)) group = gameObject.AddComponent<CanvasGroup>();
            var rect = transform as RectTransform;
            if (rect != null) home = rect.anchoredPosition;   // read ONCE: a run cut short must not become its new place
        }

        private void OnEnable()
        {
            if (purchaseView != null) purchaseView.RewardsIncoming += OnRewardsIncoming;
            RewardFlyTarget.BurstEnded += OnBurstEnded;
            Conceal(instant: true);
        }

        private void OnDisable()
        {
            if (purchaseView != null) purchaseView.RewardsIncoming -= OnRewardsIncoming;
            RewardFlyTarget.BurstEnded -= OnBurstEnded;
            tween?.Kill();
            tween = null;
            hideRoutine = null;
        }

        private void OnRewardsIncoming(IReadOnlyList<string> rewardIds) => PrepareFor(rewardIds);

        /// <summary>
        /// Something is about to fly here: switch on the counters it will land in, then show the card.
        ///
        /// A counter is enabled ONLY when its own reward is in the list. Leaving one on would advertise a destination
        /// for a reward that is not coming, and every other payout in the game would start flying into a shop card.
        /// </summary>
        public void PrepareFor(IReadOnlyList<string> rewardIds)
        {
            if (rewardIds == null) return;

            bool xp = false, vip = false;
            foreach (var id in rewardIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (string.Equals(id, xpRewardId, StringComparison.OrdinalIgnoreCase)) xp = true;
                else if (string.Equals(id, vipRewardId, StringComparison.OrdinalIgnoreCase)) vip = true;
            }
            if (!xp && !vip) return;   // nothing here is a destination — stay hidden

            if (xpTarget != null) xpTarget.SetActive(xp);
            if (xpTextObject != null) xpTextObject.SetActive(xp);
            if (vipTarget != null) vipTarget.SetActive(vip);

            // Remember what we are waiting on, so the card knows when it is done rather than guessing at a duration.
            pending.Clear();
            if (xp) pending.Add(xpRewardId);
            if (vip) pending.Add(vipRewardId);

            Reveal();
            if (hideRoutine != null) StopCoroutine(hideRoutine);
            hideRoutine = StartCoroutine(HideWhenSpent());
        }

        /// <summary>Hide again and put the counters back to sleep.</summary>
        public void Conceal(bool instant = false)
        {
            tween?.Kill();
            revealed = false;

            if (xpTarget != null) xpTarget.SetActive(false);
            if (xpTextObject != null) xpTextObject.SetActive(false);
            if (vipTarget != null) vipTarget.SetActive(false);

            pending.Clear();
            if (hideRoutine != null) { StopCoroutine(hideRoutine); hideRoutine = null; }

            if (group == null) return;
            group.blocksRaycasts = false;
            if (instant || revealSeconds <= 0f) { group.alpha = 0f; Pose(); return; }

            var outSeq = DOTween.Sequence().SetUpdate(true);
            outSeq.Join(group.DOFade(0f, revealSeconds * 0.7f).SetEase(Ease.InQuad));
            var r = transform as RectTransform;
            if (r != null)
            {
                // Accelerating away, where the entrance eased in — an exit that decelerates looks like it changed
                // its mind halfway.
                outSeq.Join(r.DOAnchorPos(home + Offset(exitTo), revealSeconds * 0.7f).SetEase(Ease.InBack, 1.2f));
                outSeq.Join(r.DOScale(Vector3.one * fromScale, revealSeconds * 0.7f).SetEase(Ease.InQuad));
            }
            outSeq.OnComplete(Pose);   // parked ready for the next reveal
            tween = outSeq;
        }

        /// <summary>
        /// Wait for every reward to finish landing, then hide.
        ///
        /// Driven by the flight's own BurstEnded rather than a fixed delay, so a long payout is not cut off and a short
        /// one does not leave the card sitting there. The timeout is the backstop: the flight is someone else's
        /// coroutine, and a burst that never reports back must not strand the card on screen for the session.
        /// </summary>
        private System.Collections.IEnumerator HideWhenSpent()
        {
            float deadline = Time.unscaledTime + Mathf.Max(1f, maxVisibleSeconds);
            while (pending.Count > 0 && Time.unscaledTime < deadline) yield return null;

            // A beat to read the number that just changed.
            float wait = Mathf.Max(0f, hideAfterSeconds);
            if (wait > 0f) yield return new WaitForSecondsRealtime(wait);

            hideRoutine = null;
            Conceal();
        }

        private void OnBurstEnded(string rewardId)
        {
            if (string.IsNullOrWhiteSpace(rewardId)) return;
            pending.Remove(rewardId);
        }

        private void Reveal()
        {
            if (group == null || revealed) return;
            revealed = true;

            tween?.Kill();
            group.blocksRaycasts = true;
            Pose();   // start pose, so the reveal has somewhere to travel FROM

            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Join(group.DOFade(1f, revealSeconds).SetEase(Ease.OutQuad));
            var rect = transform as RectTransform;
            if (rect != null)
            {
                if (fromScale != 1f) seq.Join(rect.DOScale(Vector3.one, revealSeconds).SetEase(Ease.OutBack, 1.6f));
                // The travel is what actually catches the eye — a fade on a card this size reads as it simply being there.
                if (enterFrom != Vector2.zero)
                    seq.Join(rect.DOAnchorPos(home, revealSeconds).SetEase(Ease.OutBack, 1.4f));
            }
            tween = seq;
        }

        /// <summary>Put it in its start pose: shrunk, and offset to wherever the entrance travels in from.</summary>
        private void Pose()
        {
            var rect = transform as RectTransform;
            if (rect == null) return;
            if (fromScale != 1f) rect.localScale = Vector3.one * fromScale;
            rect.anchoredPosition = home + Offset(enterFrom);
        }

        /// <summary>A fraction of the card's own size, in canvas units — so the same numbers read the same on any card.</summary>
        private Vector2 Offset(Vector2 fraction)
        {
            var rect = transform as RectTransform;
            if (rect == null || fraction == Vector2.zero) return Vector2.zero;
            var size = rect.rect.size;
            return new Vector2(fraction.x * size.x, fraction.y * size.y);
        }
    }
}
