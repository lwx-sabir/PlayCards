using System;
using PlayCard.Game.Net;       // UserProfileData, ProfileStats, WalletBalances
using PlayCard.Game.Profile;   // ProfileManager
using PlayCard.Game.Wallet;    // WalletManager
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// Data-binds the player's OWN profile panel. It is a pure VIEW: it only reads cached state from
    /// <see cref="ProfileManager"/> (identity, progression, lifetime stats, blurbs, socials) and
    /// <see cref="WalletManager"/> (spendable balances) and repaints on their change events — it never
    /// mutates data (edits go through ProfileCrud / ProfileManager.EditAsync).
    ///
    /// WIRING: drop this on the profile panel root and assign ONLY the fields your layout actually has —
    /// every binding is null-guarded, so unassigned slots are skipped (no NRE). On enable it paints from
    /// cache immediately (re-opening the panel shows data with no flicker), then kicks a server refresh;
    /// the managers' change events repaint when fresh data lands. It unsubscribes on disable.
    ///
    /// INERT-BUT-PRESENT fields (VIP tier, Loyalty points, Tokens) bind today but read 0 until their
    /// backing systems ship — their chip roots auto-hide while the value is 0, so you can wire them now.
    ///
    /// 3D AVATAR: the live character is rendered by <see cref="AvatarStage"/> (render texture), NOT here.
    /// The optional 2D <see cref="avatarImage"/>/frame/flag fields are for panels that also show a flat
    /// portrait; resolving a cosmetic id to a sprite is deferred to <see cref="ResolveCosmetic"/> (returns
    /// null until a cosmetics catalog exists, which just hides the image).
    /// </summary>
    public class ProfilePanelBinder : MonoBehaviour
    {
        [Header("Identity")]
        [SerializeField] private TMP_Text nameText;
        [Tooltip("Optional 2D portrait. The 3D avatar is driven separately by AvatarStage.")]
        [SerializeField] private Image avatarImage;
        [SerializeField] private Image avatarFrameImage;
        [SerializeField] private Image countryFlagImage;
        [SerializeField] private TMP_Text regionText;
        [SerializeField] private TMP_Text memberSinceText;

        [Header("Progression")]
        [SerializeField] private TMP_Text levelText;
        [Tooltip("Raw lifetime XP — the only XP value the server sends today.")]
        [SerializeField] private TMP_Text totalXpText;
        [Tooltip("VIP badge root — auto-hidden while VipTier == 0 (no VIP system yet).")]
        [SerializeField] private GameObject vipChip;
        [SerializeField] private TMP_Text vipText;
        [Tooltip("Loyalty badge root — auto-hidden while LoyaltyPoints == 0.")]
        [SerializeField] private GameObject loyaltyChip;
        [SerializeField] private TMP_Text loyaltyText;

        [Header("XP bar (interim — leave OFF until server exposes into-level XP)")]
        [Tooltip("Optional fill (Image set to Filled). Only driven when 'Use Interim XP Fill' is on.")]
        [SerializeField] private Image xpFillImage;
        [Tooltip("OFF by default. The server does NOT yet expose into-level XP, so a correct fill can't be " +
                 "derived. Turning this on uses (Experience % xpPerLevelInterim) — a STOPGAP that duplicates a " +
                 "server constant and rides the non-resetting Experience field. Leave off until MyProfileDto " +
                 "exposes ExperienceIntoLevel / ExperienceForNextLevel.")]
        [SerializeField] private bool useInterimXpFill = false;
        [SerializeField] private long xpPerLevelInterim = 1000;

        [Header("Currency (WalletManager — not on the profile)")]
        [SerializeField] private TMP_Text chipsText;
        [SerializeField] private TMP_Text coinsText;
        [SerializeField] private TMP_Text gemsText;
        [Tooltip("Token badge root — auto-hidden while Tokens == 0 (token not shipped).")]
        [SerializeField] private GameObject tokensChip;
        [SerializeField] private TMP_Text tokensText;

        [Header("Lifetime stats (cross-game aggregate)")]
        [SerializeField] private TMP_Text gamesPlayedText;
        [SerializeField] private TMP_Text gamesWonText;
        [SerializeField] private TMP_Text winRateText;
        [SerializeField] private TMP_Text biggestWinText;
        [SerializeField] private TMP_Text currentStreakText;
        [SerializeField] private TMP_Text longestStreakText;
        [Tooltip("Own-profile only (null on public profiles); hidden when null.")]
        [SerializeField] private TMP_Text netProfitText;

        [Header("Blurbs / Social")]
        [SerializeField] private TMP_Text bioText;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text friendCountText;

        [Header("Formatting")]
        [Tooltip("Numeric format for money/counts (matches BalanceHud).")]
        [SerializeField] private string moneyFormat = "#,0";
        [SerializeField] private string memberSinceFormat = "MMM yyyy";

        // VipTier enum mirror: index 0 = None (no label). Bronze..Elite = 1..6.
        private static readonly string[] VipNames =
            { "", "Bronze", "Silver", "Gold", "Platinum", "Diamond", "Elite" };

        private void OnEnable()
        {
            var pm = ProfileManager.Instance;
            if (pm != null)
            {
                pm.OnProfileChanged += HandleProfileChanged;
                RenderProfile();                 // paint cache now (Profile may be null pre-load — handled)
            }
            else Debug.LogWarning("[ProfilePanelBinder] No ProfileManager in scene — profile fields won't bind.");

            var wm = WalletManager.Instance;
            if (wm != null)
            {
                wm.OnBalancesChanged += RenderWallet;
                RenderWallet(wm.Balances);       // Balances may be null until first fetch — handled
            }
            else Debug.LogWarning("[ProfilePanelBinder] No WalletManager in scene — currency fields won't bind.");

            RequestRefresh();                    // pull fresh data; change events repaint when it lands
        }

        private void OnDisable()
        {
            var pm = ProfileManager.Instance;
            if (pm != null) pm.OnProfileChanged -= HandleProfileChanged;
            var wm = WalletManager.Instance;
            if (wm != null) wm.OnBalancesChanged -= RenderWallet;
        }

        /// <summary>Manually re-pull profile + wallet from the server (e.g. a refresh button).</summary>
        public void Refresh() => RequestRefresh();

        private async void RequestRefresh()
        {
            try
            {
                var pm = ProfileManager.Instance;
                if (pm != null) await pm.EnsureLoadedAsync();
                var wm = WalletManager.Instance;
                if (wm != null) await wm.RefreshAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ProfilePanelBinder] refresh failed: {e.Message}");
            }
        }

        // OnProfileChanged passes the new profile (null on clear); we re-read via the manager's null-safe accessors.
        private void HandleProfileChanged(UserProfileData _) => RenderProfile();

        private void RenderProfile()
        {
            var pm = ProfileManager.Instance;
            if (pm == null) return;

            // --- Identity (null-safe accessors default sensibly when Profile is null) ---
            SetText(nameText, pm.DisplayName);
            SetText(regionText, pm.Region == "ZZ" ? "" : pm.Region);   // "ZZ" = unknown
            SetSprite(avatarImage, ResolveCosmetic(pm.AvatarId));
            SetSprite(avatarFrameImage, ResolveCosmetic(pm.AvatarFrameId));
            SetSprite(countryFlagImage, ResolveCosmetic(pm.CountryFlagId));

            var p = pm.Profile;   // direct fields with no top-level accessor; guard for null
            if (p != null && memberSinceText != null)
                memberSinceText.text = "Member since " + p.CreatedAt.ToLocalTime().ToString(memberSinceFormat);

            // --- Progression ---
            SetText(levelText, pm.Level.ToString());
            SetText(totalXpText, pm.Experience.ToString(moneyFormat));

            int vip = pm.VipTier;
            SetActiveSafe(vipChip, vip > 0);          // inert until a VIP system writes tiers
            SetText(vipText, VipLabel(vip));

            long loyalty = pm.LoyaltyPoints;
            SetActiveSafe(loyaltyChip, loyalty > 0);  // inert until loyalty is awarded
            SetText(loyaltyText, loyalty.ToString(moneyFormat));

            UpdateXpBar(pm.Experience);

            // --- Lifetime stats (Stats is never null) ---
            var s = pm.Stats;
            SetText(gamesPlayedText, s.GamesPlayed.ToString(moneyFormat));
            SetText(gamesWonText, s.GamesWon.ToString(moneyFormat));
            SetText(winRateText, s.WinRate.ToString("0.0") + "%");
            SetText(biggestWinText, s.BiggestWin.ToString(moneyFormat));
            SetText(currentStreakText, s.CurrentWinStreak.ToString());
            SetText(longestStreakText, s.LongestWinStreak.ToString());
            if (netProfitText != null)
                netProfitText.text = s.NetProfit.HasValue ? FormatSigned(s.NetProfit.Value) : "";

            // --- Blurbs / Social ---
            SetText(bioText, pm.Bio ?? "");
            SetText(statusText, pm.StatusMessage ?? "");
            SetText(friendCountText, pm.FriendCount.ToString(moneyFormat));
        }

        private void RenderWallet(WalletBalances b)
        {
            // b is null until the first wallet fetch — treat as zeros.
            decimal chips = b?.Chips ?? 0m;
            decimal coins = b?.Coins ?? 0m;
            decimal gems = b?.Gems ?? 0m;
            decimal tokens = b?.Tokens ?? 0m;

            SetText(chipsText, chips.ToString(moneyFormat));
            SetText(coinsText, coins.ToString(moneyFormat));
            SetText(gemsText, gems.ToString(moneyFormat));
            SetActiveSafe(tokensChip, tokens > 0m);   // hidden until the token ships
            SetText(tokensText, tokens.ToString(moneyFormat));
        }

        private void UpdateXpBar(long experience)
        {
            if (xpFillImage == null || !useInterimXpFill) return;   // leave the authored bar untouched
            long per = xpPerLevelInterim > 0 ? xpPerLevelInterim : 1000;
            xpFillImage.fillAmount = Mathf.Clamp01((experience % per) / (float)per);
        }

        /// <summary>
        /// Resolve a cosmetic id (avatar / frame / flag) to a sprite. Returns null until a cosmetics
        /// catalog exists — a null sprite simply hides the image. Override or extend when the catalog lands.
        /// </summary>
        protected virtual Sprite ResolveCosmetic(string cosmeticId) => null;

        private string FormatSigned(decimal v) => v > 0 ? "+" + v.ToString(moneyFormat) : v.ToString(moneyFormat);

        private static string VipLabel(int tier) => (tier > 0 && tier < VipNames.Length) ? VipNames[tier] : "";

        private static void SetText(TMP_Text t, string s) { if (t != null) t.text = s; }

        private static void SetActiveSafe(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }

        private static void SetSprite(Image img, Sprite s)
        {
            if (img == null) return;
            img.sprite = s;
            img.enabled = s != null;   // hide when unresolved
        }
    }
}
