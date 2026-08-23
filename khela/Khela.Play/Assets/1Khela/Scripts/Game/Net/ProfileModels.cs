using System;
using System.Collections.Generic;

namespace PlayCard.Game.Net
{
    /// <summary>
    /// Client mirror of the server's MyProfileDto (GET /api/profile/me) — the signed-in player's authoritative
    /// profile. Held by ProfileManager. The server is the source of truth; this is a display cache. Kept as a
    /// standalone client type (like <see cref="WalletBalances"/>) so the client doesn't depend on Khela.Common.
    /// </summary>
    public sealed class UserProfileData
    {
        public string UserId { get; set; } = "";
        public string DisplayName { get; set; } = "";

        // Cosmetics — catalog ids; the client maps id → asset (avatar image, frame/border, country flag).
        public string AvatarId { get; set; }
        public string AvatarFrameId { get; set; }
        public string CountryFlagId { get; set; }

        public string Region { get; set; } = "ZZ";   // ISO-3166 alpha-2, "ZZ" = unknown

        // Progression / VIP.
        public int Level { get; set; } = 1;
        public long Experience { get; set; }          // XP toward next level
        public int VipTier { get; set; }
        public long LoyaltyPoints { get; set; }

        // Blurbs (user-editable, moderated server-side).
        public string Bio { get; set; }
        public string StatusMessage { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public int FriendCount { get; set; }

        public ProfileStats Stats { get; set; } = new ProfileStats();
        public List<LinkedSocial> LinkedSocials { get; set; } = new List<LinkedSocial>();
        /// <summary>Per-game breakdown (one per game played, newest-first) — the per-game stat tabs.</summary>
        public List<GameStatsDto> PerGame { get; set; } = new List<GameStatsDto>();
    }

    /// <summary>Aggregate stats block on a profile. <see cref="NetProfit"/> is own-profile only (null elsewhere).</summary>
    public sealed class ProfileStats
    {
        public long GamesPlayed { get; set; }
        public long GamesWon { get; set; }
        public double WinRate { get; set; }          // 0..100, one decimal
        public decimal BiggestWin { get; set; }
        public int CurrentWinStreak { get; set; }
        public int LongestWinStreak { get; set; }
        public decimal? NetProfit { get; set; }      // lifetime net, own profile only
        public decimal TotalWagered { get; set; }                 // lifetime wagered (cross-game)
        public DateTime? LastPlayedAt { get; set; }               // most recent play, any game
        public DateTime? StartedPlayingAt { get; set; }           // first ever play (else account created)
    }

    /// <summary>
    /// Client mirror of the server's GameStatsDto — ONE game's stats (a per-game tab). Field names mirror
    /// <see cref="ProfileStats"/> so the same UI stat block binds to the "All" aggregate or a single game.
    /// <see cref="NetProfit"/> is own-profile only (null on public); <see cref="WinRate"/> is null when there
    /// are no games yet (or for games with no per-hand win/loss). <see cref="Game"/> is the GameType as an int.
    /// </summary>
    public sealed class GameStatsDto
    {
        public int Game { get; set; }                // GameType as int — keep int, do NOT use a client enum
        public string DisplayName { get; set; } = "";
        public long GamesPlayed { get; set; }
        public long GamesWon { get; set; }
        public double? WinRate { get; set; }         // 0..100, one decimal; null = n/a (nullable, unlike ProfileStats.WinRate)
        public decimal TotalWagered { get; set; }
        public decimal BiggestWin { get; set; }
        public decimal? NetProfit { get; set; }      // own profile only
        public int CurrentWinStreak { get; set; }
        public int LongestWinStreak { get; set; }
        public long ExperienceEarned { get; set; }   // per-game lifetime XP
        public DateTime? LastPlayedAt { get; set; }
        public DateTime? StartedPlayingAt { get; set; }
    }

    /// <summary>A linked social account shown on the profile (Provider id + handle).</summary>
    public sealed class LinkedSocial
    {
        public int Provider { get; set; }
        public string Handle { get; set; }
    }

    /// <summary>
    /// Client mirror of the server's PublicProfileDto (GET /api/profile/{userId}) — ANOTHER player's public profile.
    /// Excludes account/contact fields and exact net worth; adds the viewer-relationship flags. Returned block-aware
    /// (the server gives 404 if either party has blocked the other).
    /// </summary>
    public sealed class PublicProfileData
    {
        public string UserId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string AvatarId { get; set; }
        public string AvatarFrameId { get; set; }
        public string CountryFlagId { get; set; }
        public string Region { get; set; } = "ZZ";
        public int Level { get; set; } = 1;
        public int VipTier { get; set; }
        public string Bio { get; set; }
        public string StatusMessage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public int FriendCount { get; set; }
        public bool IsOnline { get; set; }
        // Relationship to the viewer.
        public bool IsFriend { get; set; }
        public bool RequestFromMePending { get; set; }
        public bool RequestToMePending { get; set; }
        public ProfileStats Stats { get; set; } = new ProfileStats();
        public List<LinkedSocial> LinkedSocials { get; set; } = new List<LinkedSocial>();
        /// <summary>Per-game breakdown (NetProfit per game is null on public profiles).</summary>
        public List<GameStatsDto> PerGame { get; set; } = new List<GameStatsDto>();
    }

    /// <summary>
    /// Profile edit body (PATCH /api/profile/me). A null field = leave unchanged; an empty string clears Bio/Status.
    /// DisplayName + cosmetics are server-validated/moderated, so re-pull the profile after a successful edit.
    /// </summary>
    public sealed class ProfileEditRequest
    {
        public string DisplayName { get; set; }
        public string AvatarId { get; set; }
        public string AvatarFrameId { get; set; }
        public string CountryFlagId { get; set; }
        public string Bio { get; set; }
        public string StatusMessage { get; set; }

        /// <summary>The device's IANA timezone ("Asia/Dhaka"). Daily systems (the pass) roll over at the player's
        /// LOCAL midnight, so without this the server falls back to UTC and a Dhaka player's day ends at 6am.
        /// Validated server-side; an unknown id is ignored, never rejected.</summary>
        public string TimeZoneId { get; set; }
    }

    /// <summary>
    /// Client mirror of the server's ProgressionDto (GET /api/progression/me) — the live XP-bar state.
    /// <see cref="Xp"/> is INTO-LEVEL progress, so the bar fill = Xp / XpToNext.
    /// </summary>
    public sealed class ProgressionData
    {
        public int Level { get; set; } = 1;
        public long Xp { get; set; }                // into-level XP (0..XpToNext)
        public long XpToNext { get; set; }          // XP needed to reach the next level (bar denominator)
        public long DailyXpRemaining { get; set; }  // XP still earnable today before the cap
    }

    /// <summary>
    /// Client mirror of the server's VipStatusDto (GET /api/vip/me) — the signed-in player's VIP status. Tier is
    /// server-computed from the trailing-window Status Points (+ spend floor on upper bands); the badge shows only
    /// when LIT (recent activity) and not hidden. The benefit multiplier boosts the Loyalty/store track, never SP.
    /// </summary>
    public sealed class VipStatusData
    {
        public int Tier { get; set; }                   // VipTier as int (0=None … 1=Bronze floor … 7=Black Diamond)
        public string TierName { get; set; }
        public bool HasBadge { get; set; }              // Silver+ (Bronze/None carry no badge)
        public bool BadgeLit { get; set; }              // active within the badge window (cosmetic)
        public bool HideBadge { get; set; }             // player opt-out
        public long StatusPoints { get; set; }          // trailing-window SP (the rank basis)
        public long LifetimeStatusPoints { get; set; }
        public decimal BenefitMultiplier { get; set; }  // Loyalty/store boost at this tier
        public int? NextTier { get; set; }              // null at the top
        public string NextTierName { get; set; }
        public long SpToNextTier { get; set; }
        public decimal SpendToNextTierUsd { get; set; }
        // VIP Level (docs/VIP_SPEC.md §4) — the MONEY ladder, bought with VIP-P; BenefitMultiplier already includes its bonus.
        public int VipLevel { get; set; }
        public long VipPointsWindow { get; set; }        // VIP-P bought inside the trailing window (the band basis)
        public long VipPointsToNextLevel { get; set; }   // more VIP-P for the next level (0 at the top)
        public int VipWindowDays { get; set; }           // how long a purchase counts toward the level
        public int VipHeldLevel { get; set; }            // the level a purchase snapshotted (0 = none held)
        public DateTime? VipLevelMaintainedThrough { get; set; }   // the held level stands until this
    }

    /// <summary>Client mirror of the server's LoyaltyStoreItemDto (one store item).</summary>
    public sealed class LoyaltyStoreItemData
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; } = "chips";
        public long CostLp { get; set; }
        public decimal ChipAmount { get; set; }
        public int MinVipTier { get; set; }
        public bool Affordable { get; set; }   // balance >= cost
        public bool Unlocked { get; set; }      // VIP tier meets MinVipTier
    }

    /// <summary>Client mirror of the server's LoyaltyStoreDto (GET /api/loyalty) — balance + catalog.</summary>
    public sealed class LoyaltyStoreData
    {
        public long Points { get; set; }
        public long LifetimePoints { get; set; }
        public List<LoyaltyStoreItemData> Items { get; set; } = new List<LoyaltyStoreItemData>();
    }

    /// <summary>Redeem body (POST /api/loyalty/redeem). IdempotencyKey is a client-generated id per redemption intent.</summary>
    public sealed class RedeemRequestData
    {
        public string ItemId { get; set; }
        public string IdempotencyKey { get; set; }
    }

    /// <summary>Client mirror of the server's RedeemResultDto.</summary>
    public sealed class RedeemResultData
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string ItemId { get; set; }
        public long Points { get; set; }         // new LP balance after redeem
        public decimal ChipAmount { get; set; }  // chips granted
    }

    /// <summary>Client mirror of the server's VipMaintainResultDto (POST /api/vip/maintain).</summary>
    public sealed class VipMaintainResultData
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public bool AlreadyMaintained { get; set; }   // already maintained → no LP charged
        public long LoyaltyPoints { get; set; }       // LP balance after
        public int VipLevel { get; set; }
        public DateTime? MaintainedThrough { get; set; }
    }
}
