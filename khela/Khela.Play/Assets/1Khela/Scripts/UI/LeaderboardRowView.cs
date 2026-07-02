using PlayCard.Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// One leaderboard row: rank, avatar, country flag, name, score. Assign only the fields your row prefab has (all
    /// null-guarded). As rows surface more data (VIP tier, level, win-rate…), add a field + a line in <see cref="Bind"/>
    /// and a property on <see cref="LbEntryData"/> — old prefabs that don't have the field just ignore it. Bound by
    /// <see cref="LeaderboardBinder"/>.
    /// </summary>
    public sealed class LeaderboardRowView : MonoBehaviour
    {
        [SerializeField] private TMP_Text rankText;
        [SerializeField] private TMP_Text nameText;
        [SerializeField] private TMP_Text scoreText;
        [SerializeField] private Image avatarImage;
        [SerializeField] private Image flagImage;
        [Tooltip("Shown only when this row is the signed-in player (the highlighted self row).")]
        [SerializeField] private GameObject selfHighlight;
        [SerializeField] private string scoreFormat = "#,0";
        [SerializeField] private string youSuffix = " (You)";

        /// <summary>Fill the row. <paramref name="avatar"/>/<paramref name="flag"/> are resolved by the binder (may be null).</summary>
        public void Bind(LbEntryData e, Sprite avatar, Sprite flag, bool isSelf)
        {
            if (e == null) return;
            SetText(rankText, e.Rank.ToString());
            SetText(nameText, isSelf ? e.DisplayName + youSuffix : e.DisplayName);
            SetText(scoreText, e.Score.ToString(scoreFormat));
            if (avatarImage != null && avatar != null) avatarImage.sprite = avatar;   // else keep prefab placeholder
            if (flagImage != null && flag != null) flagImage.sprite = flag;
            SetActiveSafe(selfHighlight, isSelf);
        }

        private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }
        private static void SetActiveSafe(GameObject go, bool on) { if (go != null && go.activeSelf != on) go.SetActive(on); }
    }
}
