using System;
using System.Threading.Tasks;
using PlayCard.Game.Net;
using UnityEngine;

namespace PlayCard.Game.Profile
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for the signed-in player's profile — display identity (name, avatar, frame, country
    /// flag), progression (level / XP / VIP / loyalty), blurbs (bio / status), social counters and aggregate stats.
    /// Fetched from the server-authoritative <c>GET /api/profile/me</c> and cached here; every UI surface that shows
    /// the player binds to <see cref="OnProfileChanged"/> and reads the null-safe accessors instead of fetching
    /// itself. The server is authoritative — this is a display cache AND the funnel for edits: a successful edit
    /// RE-PULLS the authoritative profile (server moderation may rewrite the name, so we never trust the local copy).
    ///
    /// Singleton, survives scene loads (drop it on a bootstrap object next to WalletManager / AccountManager). It
    /// does NOT auto-fetch — call <see cref="RefreshAsync"/> once the player is authenticated (e.g. from your boot
    /// flow after login), and <see cref="EnsureLoadedAsync"/> on screens that need it; <see cref="Clear"/> on logout.
    /// </summary>
    public sealed class ProfileManager : MonoBehaviour
    {
        public static ProfileManager Instance { get; private set; }

        /// <summary>The cached authoritative profile. Null until the first successful load.</summary>
        public UserProfileData Profile { get; private set; }

        public bool IsLoaded => Profile != null;
        public bool IsLoading { get; private set; }

        /// <summary>Raised whenever the profile is (re)loaded, edited, or cleared (arg is null on clear). Bind UI here.</summary>
        public event Action<UserProfileData> OnProfileChanged;

        // ---- Null-safe convenience accessors, so UI doesn't have to null-check Profile everywhere ----
        public string UserId        => Profile?.UserId ?? "";
        public string DisplayName   => Profile?.DisplayName ?? "";
        public string AvatarId      => Profile?.AvatarId;
        public string AvatarFrameId => Profile?.AvatarFrameId;
        public string CountryFlagId => Profile?.CountryFlagId;
        public string Region        => Profile?.Region ?? "ZZ";
        public int    Level         => Profile?.Level ?? 1;
        public long   Experience    => Profile?.Experience ?? 0;
        public int    VipTier       => Profile?.VipTier ?? 0;
        public long   LoyaltyPoints => Profile?.LoyaltyPoints ?? 0;
        public string Bio           => Profile?.Bio;
        public string StatusMessage => Profile?.StatusMessage;
        public int    FriendCount   => Profile?.FriendCount ?? 0;
        public ProfileStats Stats   => Profile?.Stats ?? new ProfileStats();

        private void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>Load the profile only if we don't already have it (idempotent). Call on screens that need it.</summary>
        public Task<bool> EnsureLoadedAsync() => IsLoaded ? Task.FromResult(true) : RefreshAsync();

        /// <summary>Re-fetch the authoritative profile from the server. Call after login, level-up, edit, etc.</summary>
        public async Task<bool> RefreshAsync()
        {
            IsLoading = true;
            try
            {
                var res = await BlackjackRestClient.Instance.GetMyProfileAsync();
                if (!res.Ok || res.Value == null)
                {
                    Debug.LogWarning($"[ProfileManager] profile fetch failed: {res.Error}");
                    return false;
                }
                Profile = res.Value;
                OnProfileChanged?.Invoke(Profile);
                return true;
            }
            finally { IsLoading = false; }
        }

        /// <summary>
        /// Edit the profile (name / avatar / frame / flag / bio / status). The server validates + MODERATES, so on
        /// success we re-pull the authoritative profile (the saved name may differ from what was sent). A null field
        /// = leave unchanged; an empty Bio/Status clears it. Returns (false, serverMessage) on rejection.
        /// </summary>
        public async Task<(bool ok, string error)> EditAsync(ProfileEditRequest edit)
        {
            if (edit == null) return (false, "No changes.");
            var res = await BlackjackRestClient.Instance.UpdateProfileAsync(edit);
            if (!res.Ok) return (false, res.Error);
            await RefreshAsync();   // server is authoritative — re-pull the moderated result
            return (true, null);
        }

        /// <summary>Drop the cached profile (call on logout so the next user never sees stale data).</summary>
        public void Clear()
        {
            Profile = null;
            OnProfileChanged?.Invoke(null);
        }
    }
}
