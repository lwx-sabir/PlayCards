using System;
using System.Threading.Tasks;
using PlayCard.Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The signed-in player's info card — name, level, XP bar, avatar. Built to be a PREFAB you can drop into any
    /// scene: it sources its own data, so there is nothing to wire per scene.
    ///
    /// Every instance shares ONE cached snapshot and ONE in-flight request (both static). Drop the prefab into Home,
    /// Lobby and the Table and you still get a single fetch, and all copies repaint together. On enable it paints the
    /// cache FIRST so a scene load shows the last known values instead of blanks, then refreshes in the background.
    ///
    /// Assign only the fields your card actually has — all are optional, so a compact variant that shows just name +
    /// level works with the same script. Adding a field later means one SerializeField and one line in Apply().
    /// </summary>
    public sealed class UserInfoBinder : MonoBehaviour
    {
        [Header("Text")]
        [SerializeField] private TMP_Text nameText;
        [Tooltip("Level label. {0} is the level number.")]
        [SerializeField] private TMP_Text levelText;
        [SerializeField] private string levelFormat = "Lvl. {0}";

        [Header("XP bar")]
        [Tooltip("The fill Image, driven 0..1 from XP into the current level. Its Image Type MUST be Filled — on a " +
                 "Sliced or Simple image Unity ignores fillAmount and the bar can never move. If the bar is built as " +
                 "a Slider, leave this empty and use Xp Slider instead.")]
        [SerializeField] private Image xpFill;
        [Tooltip("A Slider for the bar, when the bar is built as one. A Slider positions its own Fill Rect from its " +
                 "VALUE, so writing fillAmount on that fill graphic does nothing — this is the field to use instead. " +
                 "Set both and both are driven; set neither and there is simply no bar.")]
        [SerializeField] private Slider xpSlider;
        [Tooltip("Optional \"1,200 / 5,000\" style label.")]
        [SerializeField] private TMP_Text xpText;
        [SerializeField] private string xpFormat = "{0:#,0} / {1:#,0}";
        [Tooltip("Seconds for the bar to slide to its new value. 0 = snap.")]
        [SerializeField] private float xpTweenSeconds = 0.35f;

        [Header("Avatar")]
        [Tooltip("The portrait Image. NOT auto-populated — the server sends an AvatarId, and there is no id→sprite " +
                 "catalog wired yet, so the authored sprite is kept until something calls SetAvatar().")]
        [SerializeField] private Image avatarImage;

        // ---- shared across every instance -------------------------------------------------------------------

        private sealed class Snapshot
        {
            public string Name;
            public int Level = 1;
            public long Xp;
            public long XpToNext;
        }

        private static Snapshot _cache;
        private static Task _inFlight;                 // one request no matter how many cards are alive
        private static event Action OnDataChanged;     // all live cards repaint together

        /// <summary>Force a re-fetch and repaint of every card — call after anything that grants XP or renames.</summary>
        public static void RefreshAll() => _ = FetchAsync(force: true);

        private float _shownFill;
        private Coroutine _barTween;

        private void OnEnable()
        {
            OnDataChanged += Apply;
            Apply();                       // cached values first: no blank frame on scene load
            _ = FetchAsync(force: false);  // then bring it up to date
        }

        private void OnDisable() => OnDataChanged -= Apply;

        // ---- data -------------------------------------------------------------------------------------------

        private static async Task FetchAsync(bool force)
        {
            // Someone is already fetching — ride along rather than firing a duplicate request per card.
            if (_inFlight != null && !_inFlight.IsCompleted) { await _inFlight; return; }
            if (_cache != null && !force) return;   // already have it and nobody asked for fresh

            _inFlight = LoadAsync();
            try { await _inFlight; } finally { _inFlight = null; }
        }

        private static async Task LoadAsync()
        {
            var rest = BlackjackRestClient.Instance;
            if (rest == null) return;

            var snap = _cache ?? new Snapshot();

            // Progression owns level + the bar's numerator/denominator; profile owns the display name. Fetched
            // separately because they're separate endpoints — a failure in one must not blank the other.
            var prog = await rest.GetProgressionAsync();
            if (prog.Ok && prog.Value != null)
            {
                snap.Level = prog.Value.Level;
                snap.Xp = prog.Value.Xp;
                snap.XpToNext = prog.Value.XpToNext;
            }

            var profile = await rest.GetMyProfileAsync();
            if (profile.Ok && profile.Value != null && !string.IsNullOrEmpty(profile.Value.DisplayName))
                snap.Name = profile.Value.DisplayName;

            _cache = snap;
            OnDataChanged?.Invoke();
        }

        // ---- paint ------------------------------------------------------------------------------------------

        private void Apply()
        {
            var s = _cache;
            if (s == null) return;   // nothing fetched yet — leave the authored placeholders visible

            if (nameText != null && !string.IsNullOrEmpty(s.Name)) nameText.text = s.Name;
            if (levelText != null) levelText.text = string.Format(levelFormat, s.Level);

            float target = s.XpToNext > 0 ? Mathf.Clamp01((float)s.Xp / s.XpToNext) : 0f;
            if (xpText != null) xpText.text = string.Format(xpFormat, s.Xp, s.XpToNext);
            SetBar(target);
        }

        /// <summary>
        /// Write the bar wherever it actually lives — a Filled Image, a Slider, or both.
        ///
        /// The two are not interchangeable. A Slider positions its own Fill Rect from its VALUE, so writing
        /// fillAmount on that fill graphic does nothing at all; and fillAmount itself is ignored by Unity unless the
        /// Image's type is Filled. A bar wired one way and driven the other simply never moves.
        /// </summary>
        private void PaintBar(float value01)
        {
            if (xpFill != null) xpFill.fillAmount = value01;
            // normalizedValue, not value: it honours whatever Min/Max the slider was authored with rather than
            // assuming 0..1.
            if (xpSlider != null) xpSlider.normalizedValue = value01;
        }

        private float CurrentBar()
        {
            if (xpFill != null) return xpFill.fillAmount;
            if (xpSlider != null) return xpSlider.normalizedValue;
            return 0f;
        }

        private void SetBar(float target)
        {
            if (xpFill == null && xpSlider == null) return;

            // Snap when it can't animate (disabled object, no tween time) or when the bar is being set for the first
            // time — sliding up from 0 on every scene load would read as if XP had just been earned.
            if (xpTweenSeconds <= 0f || !isActiveAndEnabled || _shownFill <= 0f)
            {
                _shownFill = target;
                PaintBar(target);
                return;
            }

            if (_barTween != null) StopCoroutine(_barTween);
            _barTween = StartCoroutine(BarRoutine(target));
        }

        private System.Collections.IEnumerator BarRoutine(float target)
        {
            float from = CurrentBar();
            float t = 0f;
            while (t < xpTweenSeconds)
            {
                t += Time.unscaledDeltaTime;
                float u = Mathf.Clamp01(t / xpTweenSeconds);
                PaintBar(Mathf.Lerp(from, target, 1f - (1f - u) * (1f - u)));   // ease-out
                yield return null;
            }
            PaintBar(target);
            _shownFill = target;
            _barTween = null;
        }

        /// <summary>Set the portrait once you have a sprite for the player's AvatarId.</summary>
        public void SetAvatar(Sprite sprite)
        {
            if (avatarImage != null && sprite != null) avatarImage.sprite = sprite;
        }
    }
}
