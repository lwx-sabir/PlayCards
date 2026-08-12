using System;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Binds YOUR own banner (Bottom_Action_BAR ▸ Banner_Player_main) — the portrait and the win celebration.
    ///
    /// Deliberately NOT a <see cref="SeatPlate"/>. The seat plates are the OTHER players' cards and their driver
    /// hides whichever one is yours, so reusing that here would mean fighting the thing it exists to do. Your chips
    /// are already bound (ChipCountJuice reads WalletManager) and your name lives in the profile badge, so this owns
    /// only the two pieces that were left static.
    ///
    /// Put it on an ALWAYS-ACTIVE object — the banner root is fine, since nothing hides that.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LocalPlayerBanner : MonoBehaviour
    {
        [Header("Refs (auto-found if empty)")]
        [SerializeField] private TableController table;
        [SerializeField] private BlackjackTableView view;

        [Header("Portrait")]
        [Tooltip("The avatar ELEMENT on your banner — the object carrying SeatAvatar (Player_Avatar).")]
        [SerializeField] private SeatAvatar avatar;
        [Tooltip("Portrait to show. Leave empty to use the SAME shared default the opponents' banners use " +
                 "(Avatar_Panel ▸ BettingAvatarFader ▸ Default Avatar), so every seat at the table matches.")]
        [SerializeField] private Sprite portrait;

        [Header("Win")]
        [Tooltip("Win celebration particle. Left OFF by default, switched on when YOU win, off when the next round " +
                 "deals — the same lifetime the opponents' banners use.")]
        [SerializeField] private GameObject winFx;

        private void Awake()
        {
            if (table == null) table = FindAnyObjectByType<TableController>(FindObjectsInactive.Include);
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>(FindObjectsInactive.Include);
            if (avatar == null) avatar = GetComponentInChildren<SeatAvatar>(true);

            SetWinFx(false);
            ApplyPortrait();
        }

        private void ApplyPortrait()
        {
            if (avatar == null) return;

            var sprite = portrait;
            if (sprite == null)
            {
                // Same shared reference the seat plates read, so your banner and theirs can't drift apart.
                var panel = FindAnyObjectByType<BettingAvatarFader>(FindObjectsInactive.Include);
                if (panel != null) sprite = panel.DefaultAvatar;
            }

            if (sprite != null) avatar.SetPortrait(sprite);
        }

        /// <summary>
        /// PAY beat — the win badge has just appeared and the chips have landed. Celebrate if this round was a win.
        /// Driven by <c>RoundEndDirector</c>, NOT by the board: the server flips the round over the instant it
        /// resolves, before the dealer has even revealed, so anything board-driven fires over a face-down hole card.
        /// </summary>
        public void RevealNow(BoardSnapshot board)
        {
            var b = board ?? (table != null ? table.Board : null);
            int mySeat = table != null ? table.MySeat : -1;
            if (mySeat <= 0 || b?.LastResults == null) return;

            foreach (var r in b.LastResults)
            {
                if (r == null || r.SeatNumber != mySeat) continue;
                // Outcome is the seat's NET across all hands, so a split that nets a win celebrates once.
                SetWinFx(string.Equals(r.Outcome, "win", StringComparison.OrdinalIgnoreCase));
                return;
            }
        }

        /// <summary>Payout finished — chips landed and the count has rolled. Stop the celebration.</summary>
        public void ClearWinFx() => SetWinFx(false);

        public void SetWinFx(bool on)
        {
            if (winFx != null && winFx.activeSelf != on) winFx.SetActive(on);
        }
    }
}
