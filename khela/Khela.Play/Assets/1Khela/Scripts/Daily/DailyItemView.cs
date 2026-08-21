using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Daily
{
    /// <summary>What one day of the daily ladder looks like right now.</summary>
    public enum DailyItemState
    {
        /// <summary>A future day. The default look — nothing switched on.</summary>
        Default = 0,

        /// <summary>Claimable RIGHT NOW, for nothing. The one thing on screen worth tapping.</summary>
        Focused = 1,

        /// <summary>The next day that opens up — "come back tomorrow".</summary>
        Tomorrow = 2,

        /// <summary>Already collected.</summary>
        Collected = 3,

        /// <summary>Missed, and buyable back with rewarded ads.</summary>
        AdUnlockable = 4,
    }

    /// <summary>
    /// One day tile in the daily login popup. Deliberately flat: a day number, a value line, an icon and a handful of
    /// state objects to switch on — the same shape as <c>PassCardView</c>, for the same reason. Everything is assigned
    /// in the inspector rather than found by name, so renaming a child in the prefab can't silently break it.
    ///
    /// It decides NOTHING. The server says which day is claimable, missed or collected; this only draws it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DailyItemView : MonoBehaviour
    {
        [Header("Content")]
        [Tooltip("\"Day 3\" — the label is whatever the screen formats, this only prints it.")]
        [SerializeField] private TMP_Text dayText;
        [Tooltip("The reward headline the SERVER decided (\"2.5K\", \"Mystery\"). Never derived here.")]
        [SerializeField] private TMP_Text valueText;
        [Tooltip("The reward icon. Left alone when the server sends no artwork, so the prefab's own art stands.")]
        [SerializeField] private Image icon;

        [Header("States — leave any unassigned and it's simply skipped")]
        [Tooltip("Shown while the day is claimable right now.")]
        [SerializeField] private GameObject focused;
        [Tooltip("Shown on the day that opens up next.")]
        [SerializeField] private GameObject tomorrow;
        [Tooltip("Shown once the day has been collected.")]
        [SerializeField] private GameObject collected;
        [Tooltip("Optional: shown on a missed day that rewarded ads can buy back. Without it, see Ad Unlockable Looks " +
                 "Focused below.")]
        [SerializeField] private GameObject adUnlock;
        [Tooltip("Optional \"1 ad\" label inside the ad state.")]
        [SerializeField] private TMP_Text adCostText;

        [Tooltip("With no Ad Unlock object assigned, should a missed-but-buyable day LOOK claimable?\n\n" +
                 "Off (default) is the honest choice: Focused means \"free, tap it\", and a player who taps expecting a " +
                 "reward and gets an ad prompt learns to distrust the glow. On makes those days obvious at the cost of " +
                 "that lie. Either way the day is still tappable and still starts the ad flow.")]
        [SerializeField] private bool adUnlockableLooksFocused;

        [Header("Input")]
        [SerializeField] private Button button;

        [Header("Juice")]
        [SerializeField] private float punchScale = 0.18f;
        [SerializeField] private float punchSeconds = 0.22f;
        [Tooltip("How hard a refused tap shakes.")]
        [SerializeField] private float denyShake = 12f;

        /// <summary>The day this tile is showing.</summary>
        public int Day { get; private set; }

        /// <summary>What it is currently drawing.</summary>
        public DailyItemState State { get; private set; }

        /// <summary>Tapped. The screen decides what that means — collect, watch an ad, or refuse.</summary>
        public event Action<DailyItemView> Clicked;

        private void Awake()
        {
            if (button == null) button = GetComponent<Button>();
            if (button != null) button.onClick.AddListener(() => Clicked?.Invoke(this));
        }

        /// <summary>Draw a day. <paramref name="dayLabel"/> and <paramref name="value"/> are both formatted by the
        /// caller — this never invents a string.</summary>
        public void Bind(int day, string dayLabel, string value, DailyItemState state, int adCost = 0)
        {
            Day = day;
            if (dayText != null) dayText.text = dayLabel ?? string.Empty;
            if (valueText != null) valueText.text = value ?? string.Empty;
            SetState(state, adCost);
        }

        /// <summary>Drop in the reward icon once its download finishes. Null is ignored, so the prefab's own art
        /// survives a server with no artwork configured.</summary>
        public void SetIcon(Sprite sprite)
        {
            if (sprite != null && icon != null) icon.sprite = sprite;
        }

        /// <summary>Flip just the state, without touching the text — for the moment a claim lands.</summary>
        public void SetState(DailyItemState state, int adCost = 0)
        {
            State = state;

            bool showAd = state == DailyItemState.AdUnlockable && adUnlock != null;
            bool showFocused = state == DailyItemState.Focused
                            || (state == DailyItemState.AdUnlockable && adUnlock == null && adUnlockableLooksFocused);

            Show(focused, showFocused);
            Show(tomorrow, state == DailyItemState.Tomorrow);
            Show(collected, state == DailyItemState.Collected);
            Show(adUnlock, showAd);

            if (adCostText != null && showAd)
                adCostText.text = adCost > 1 ? $"{adCost} ads" : "1 ad";

            // A collected day is finished. Everything else stays tappable — a future day's refusal is feedback, and a
            // missed day's tap is what starts the ad flow.
            if (button != null) button.interactable = state != DailyItemState.Collected;
        }

        private static void Show(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }

        // ---------------- juice ----------------

        /// <summary>The tap that collects. Punches, so the tile acknowledges the finger before the server answers.</summary>
        public void PlayClaimed() => Punch(1f);

        /// <summary>A tap on a day already taken — acknowledge it rather than doing nothing.</summary>
        public void PlayAlreadyCollected() => Punch(0.5f);

        /// <summary>A tap on a day that isn't available. Shakes and stays silent — the refusal is the message.</summary>
        public void PlayDenied()
        {
            if (denyShake <= 0f) return;
            var rect = (RectTransform)transform;
            rect.DOComplete();
            rect.DOShakeAnchorPos(0.28f, new Vector2(denyShake, 0f), 14, 90f, false, true).SetUpdate(true);
        }

        private void Punch(float scale)
        {
            if (punchScale <= 0f || punchSeconds <= 0f) return;

            // Complete rather than kill: a punch interrupted mid-flight would otherwise leave the tile stretched.
            transform.DOComplete();
            transform.DOPunchScale(Vector3.one * (punchScale * scale), punchSeconds, 1, 0.6f).SetUpdate(true);
        }
    }
}
