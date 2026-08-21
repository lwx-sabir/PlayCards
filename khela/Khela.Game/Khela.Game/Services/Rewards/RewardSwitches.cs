using System.Threading.Tasks;
using Khela.Game.Services.Redis;
using Microsoft.Extensions.Options;

namespace Khela.Game.Services.Rewards
{
    /// <summary>
    /// The reward switches, read LIVE.
    ///
    /// <see cref="RewardOptions"/> binds from appsettings, which means a change needs a file edit and a restart — fine
    /// for a deployment default, useless for the thing these switches are actually for: flipping a rule mid-test and
    /// seeing the next claim behave differently. So the Redis <c>khela:settings</c> hash wins when it holds a value,
    /// exactly like every other admin-tunable knob, and appsettings is the fallback underneath.
    ///
    /// One switch drives BOTH ladders — the pass and the daily reward — on purpose: a build where one of them charges
    /// for a missed day and the other doesn't is a bug that only shows up in a player's confusion.
    /// </summary>
    public static class RewardSwitches
    {
        public const string SettingsHashKey = "khela:settings";
        public const string BypassAdField = "Rewards:BypassAdForMissedDays";

        /// <summary>
        /// Are missed days free right now? Redis first, appsettings second, false last.
        ///
        /// Costs one hash read against a local Redis — cheaper than the config lookup it replaces is not the point;
        /// the point is that it can change without a restart. Redis being unreachable falls back rather than throwing,
        /// because a reward ladder must never go down over a settings read.
        /// </summary>
        public static async Task<bool> BypassAdForMissedDaysAsync(IRedisService redis, IOptionsMonitor<RewardOptions> options)
        {
            var fallback = options?.CurrentValue?.BypassAdForMissedDays ?? false;
            if (redis == null) return fallback;

            try
            {
                var value = await redis.GetDatabase().HashGetAsync(SettingsHashKey, BypassAdField);
                if (value.HasValue && bool.TryParse(value, out var live)) return live;
            }
            catch { /* fall through to the deployment default */ }

            return fallback;
        }
    }
}
