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
        [Header("Controls")]
        [Tooltip("Optional close button — wired in code to hide the panel on click.")]
        [SerializeField] private Button closeButton;
        [Tooltip("Object to deactivate on close. Leave empty to close THIS object (the panel root the binder sits on).")]
        [SerializeField] private GameObject panelRoot;

        [Header("3D avatar stage (toggled with the panel)")]
        [Tooltip("The AvatarStage root (the off-scene camera + model). Its AvatarCamera renders continuously, so " +
                 "it's disabled while the panel is closed and re-enabled on open — THIS is what stops the render " +
                 "texture from lingering after Back.")]
        [SerializeField] private GameObject avatarStageRoot;
        [Tooltip("Optional: the RawImage object that displays the render, if it isn't already a child that hides " +
                 "with the panel. Toggled with the panel so no stale frame shows through.")]
        [SerializeField] private GameObject avatarVisual;

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

        [Header("XP bar (bound to GET /api/progression/me)")]
        [Tooltip("XP bar Slider. The binder sets Min=0, Max=XpToNext, Value=Xp each refresh, so the max tracks " +
                 "the per-level requirement automatically — you don't pre-set it.")]
        [SerializeField] private Slider xpSlider;
        [Tooltip("Optional 'Xp / XpToNext' label.")]
        [SerializeField] private TMP_Text xpProgressText;

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
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            SetAvatarVisible(true);   // panel opened → start the avatar stage

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
            if (closeButton != null) closeButton.onClick.RemoveListener(Close);
            SetAvatarVisible(false);  // panel closed → stop the avatar stage so its RT can't linger

            var pm = ProfileManager.Instance;
            if (pm != null) pm.OnProfileChanged -= HandleProfileChanged;
            var wm = WalletManager.Instance;
            if (wm != null) wm.OnBalancesChanged -= RenderWallet;
        }

        /// <summary>Manually re-pull profile + wallet from the server (e.g. a refresh button).</summary>
        public void Refresh() => RequestRefresh();

        /// <summary>Hide the panel — the assigned <see cref="panelRoot"/>, or this object if none is set.</summary>
        public void Close() => (panelRoot != null ? panelRoot : gameObject).SetActive(false);

        // Start/stop the 3D avatar stage with the panel. The AvatarCamera renders every frame into its render
        // texture regardless of the RawImage, so disabling the stage root on close is what actually stops the
        // texture from lingering after Back; re-enabling on open brings it back live.
        private void SetAvatarVisible(bool on)
        {
            if (avatarStageRoot != null) avatarStageRoot.SetActive(on);
            if (avatarVisual != null) avatarVisual.SetActive(on);
        }

        private async void RequestRefresh()
        {
            try
            {
                var pm = ProfileManager.Instance;
                if (pm != null) await pm.EnsureLoadedAsync();
                var wm = WalletManager.Instance;
                if (wm != null) await wm.RefreshAsync();

                // The XP bar has no manager/cache — pull it straight from the server on each open.
                var prog = await BlackjackRestClient.Instance.GetProgressionAsync();
                // TEMP DIAGNOSTIC — remove once the panel binds: prints profile-load + name + progression result.
                var pmLog = ProfileManager.Instance;
                Debug.Log($"[ProfilePanelBinder] profile loaded={pmLog?.IsLoaded} name='{pmLog?.DisplayName}' | " +
                          $"prog ok={prog.Ok} status={prog.Status} xp={prog.Value?.Xp}/{prog.Value?.XpToNext} err='{prog.Error}'");
                if (prog.Ok && prog.Value != null) RenderProgression(prog.Value);
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

        private void RenderProgression(ProgressionData p)
        {
            if (p == null) return;
            if (xpSlider != null)
            {
                // Drive the slider with RAW values so its max tracks the per-level XpToNext (150, 450, 850, …);
                // the fill image inside the slider follows value/max automatically.
                xpSlider.minValue = 0f;
                xpSlider.maxValue = p.XpToNext > 0 ? p.XpToNext : 1f;
                xpSlider.value = p.Xp;
            }
            SetText(xpProgressText, $"{p.Xp:#,0} / {p.XpToNext:#,0}");
            SetText(levelText, p.Level.ToString());   // progression is the XP-authoritative level
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
