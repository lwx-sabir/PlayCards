using System.Threading.Tasks;
using PlayCard.Game.Net;

namespace PlayCard.Game.Profile
{
    /// <summary>
    /// CRUD operations for the player profile, layered over the server profile API + the <see cref="ProfileManager"/>
    /// cache. The profile is 1:1 with the account, so the verbs map specially:
    ///
    ///  • CREATE — the profile is created SERVER-SIDE at account bootstrap; there is no standalone client create.
    ///    <see cref="EnsureAsync"/> just confirms it exists (loads it into the master).
    ///  • READ   — <see cref="ReadMineAsync"/> (own, into the master) and <see cref="ReadPublicAsync"/> (another
    ///    player's public, block-aware).
    ///  • UPDATE — full <see cref="UpdateAsync"/> plus granular field setters; the server validates + MODERATES and
    ///    the master re-pulls, so the cache never trusts the local value.
    ///  • DELETE — NOT a profile-level op. Deleting a profile means deleting the account (data-retention / AML
    ///    obligations), which is a separate, regulated flow — intentionally not exposed here.
    ///
    /// Own-profile reads/edits flow through <see cref="ProfileManager"/> so the cache + OnProfileChanged stay the
    /// single source of truth. Stateless — call the static methods from anywhere (await the result).
    /// </summary>
    public static class ProfileCrud
    {
        // ---- CREATE (server bootstrap; client only confirms existence) -------------------------------------------

        /// <summary>Confirm the signed-in player's profile exists (created server-side at account bootstrap) and is
        /// loaded into the master. Returns false if it couldn't be fetched.</summary>
        public static Task<bool> EnsureAsync()
            => ProfileManager.Instance != null ? ProfileManager.Instance.EnsureLoadedAsync() : Task.FromResult(false);

        // ---- READ ------------------------------------------------------------------------------------------------

        /// <summary>(Re)read the OWN profile from the server into the master (raises OnProfileChanged).</summary>
        public static Task<bool> ReadMineAsync()
            => ProfileManager.Instance != null ? ProfileManager.Instance.RefreshAsync() : Task.FromResult(false);

        /// <summary>Read ANOTHER player's PUBLIC profile. Returns null if blocked / not found / offline error.</summary>
        public static async Task<PublicProfileData> ReadPublicAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            var res = await BlackjackRestClient.Instance.GetPublicProfileAsync(userId);
            return res.Ok ? res.Value : null;
        }

        // ---- UPDATE (own profile; server validates + moderates, then the master re-pulls) ------------------------

        /// <summary>Apply a multi-field edit. Null field = leave unchanged; empty Bio/Status clears it.</summary>
        public static Task<(bool ok, string error)> UpdateAsync(ProfileEditRequest edit) => Edit(edit);

        public static Task<(bool ok, string error)> SetDisplayNameAsync(string name)
            => Edit(new ProfileEditRequest { DisplayName = name });

        public static Task<(bool ok, string error)> SetAvatarAsync(string avatarId)
            => Edit(new ProfileEditRequest { AvatarId = avatarId });

        public static Task<(bool ok, string error)> SetFrameAsync(string frameId)
            => Edit(new ProfileEditRequest { AvatarFrameId = frameId });

        public static Task<(bool ok, string error)> SetFlagAsync(string flagId)
            => Edit(new ProfileEditRequest { CountryFlagId = flagId });

        /// <summary>Set all three cosmetics at once (null leaves a slot unchanged).</summary>
        public static Task<(bool ok, string error)> SetCosmeticsAsync(string avatarId, string frameId, string flagId)
            => Edit(new ProfileEditRequest { AvatarId = avatarId, AvatarFrameId = frameId, CountryFlagId = flagId });

        /// <summary>Set the bio. Pass "" to clear, null to leave unchanged. Server-moderated.</summary>
        public static Task<(bool ok, string error)> SetBioAsync(string bio)
            => Edit(new ProfileEditRequest { Bio = bio });

        /// <summary>Set the status line. Pass "" to clear, null to leave unchanged. Server-moderated.</summary>
        public static Task<(bool ok, string error)> SetStatusAsync(string status)
            => Edit(new ProfileEditRequest { StatusMessage = status });

        // ---- DELETE: intentionally absent — account deletion is a separate regulated flow, not a profile edit. ---

        private static Task<(bool ok, string error)> Edit(ProfileEditRequest edit)
            => ProfileManager.Instance != null
                ? ProfileManager.Instance.EditAsync(edit)
                : Task.FromResult((false, "ProfileManager not in scene."));
    }
}
