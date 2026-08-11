using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Khela.Common.Rewards;
using Khela.Game.Services.Chests;

namespace Khela.Game.Services.Rewards
{
    /// <summary>
    /// Grants ONE <see cref="RewardKind"/>. This is the extension point: teaching the game to hand out a new kind of
    /// thing (lottery tickets, clothes, another currency…) is a new <see cref="RewardKind"/> value plus one class here
    /// — no change to the systems that award rewards. See docs/PASS_SPEC.md §2.
    ///
    /// CONTRACT (a granter that breaks either rule must not ship):
    /// 1. <b>Idempotent on <c>idemKey</c></b> — the same key must never pay twice, at any concurrency.
    /// 2. <b>Reports what was APPLIED</b>, not what was requested (a rolled chest, a clamped amount), so the client's
    ///    animation can never disagree with the ledger.
    /// </summary>
    public interface IRewardGranter
    {
        RewardKind Kind { get; }

        /// <summary>
        /// Grant one line. Returns what was applied — usually one entry, but a line MAY expand (a chest returns the
        /// chest plus the currencies it rolled). Empty/null means nothing was granted (skipped/invalid/no-op).
        /// Must not throw for a merely invalid line — return nothing and log.
        /// <paramref name="externalRef"/> is the AWARDING system's own key (e.g. "xp:lvlup:{user}:{level}"), recorded
        /// alongside the ledger row so a payout can be traced back to what produced it.
        /// </summary>
        Task<IReadOnlyList<GrantedLineDto>> GrantAsync(Guid userId, RewardGrant line, string idemKey, string description, string externalRef = null);
    }

    /// <summary>Parsing + key helpers shared by the granters.</summary>
    public static class RewardIds
    {
        /// <summary>
        /// Clamp an idempotency key to what <c>WalletTransactions.CorrelationId</c> can store (MaxLength 64, uniquely
        /// indexed per wallet). Short keys pass through UNCHANGED — so existing keys keep their exact historical form
        /// and stay idempotent across this change. A long key is squeezed deterministically (readable prefix + a hash
        /// of the WHOLE key), which keeps it stable and collision-safe instead of silently truncating.
        /// </summary>
        public static string WalletKey(string idemKey)
        {
            if (string.IsNullOrEmpty(idemKey) || idemKey.Length <= 64) return idemKey;
            using var sha = SHA256.Create();
            var hex = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(idemKey))).Substring(0, 16).ToLowerInvariant();
            return idemKey.Substring(0, 47) + "~" + hex;   // 47 + 1 + 16 = 64
        }

        /// <summary>Parse a chest reward id of the form "CK_Chest:Rare" (key ':' tier). The key itself may not contain ':'.</summary>
        public static bool TryParseChest(string id, out string chestKey, out ChestTier tier)
        {
            chestKey = null; tier = default;
            if (string.IsNullOrWhiteSpace(id)) return false;
            var i = id.LastIndexOf(':');
            if (i <= 0 || i == id.Length - 1) return false;
            chestKey = id.Substring(0, i).Trim();
            return chestKey.Length > 0 && Enum.TryParse(id.Substring(i + 1).Trim(), ignoreCase: true, out tier)
                   && Enum.IsDefined(typeof(ChestTier), tier);
        }
    }
}
