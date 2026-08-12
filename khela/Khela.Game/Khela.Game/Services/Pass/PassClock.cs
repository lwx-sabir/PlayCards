using System;

namespace Khela.Game.Services.Pass
{
    /// <summary>
    /// The pass's sense of "today", in the PLAYER's calendar rather than UTC.
    ///
    /// A daily pass that rolls over at UTC midnight rolls over at 6am in Dhaka — a player who plays their evening,
    /// sleeps, and opens the app in the morning has silently lost a day. So every day/cycle boundary here is the
    /// player's LOCAL midnight, derived from a timezone the server stores for them.
    ///
    /// Production notes:
    /// - IANA ids ("Asia/Dhaka"), not raw offsets, so DST is handled by the tz database rather than by us.
    /// - <see cref="Resolve"/> never throws: an unknown, hostile or missing id falls back to UTC.
    /// - Local midnight does not exist on some spring-forward days (Iran, Cuba, Chile…) and is ambiguous on
    ///   fall-back days. <see cref="ToUtc"/> handles both instead of letting TimeZoneInfo throw.
    /// - Timezone manipulation is NOT a payout exploit: nodes are claimed by INDEX and each index is uniquely
    ///   indexed per (user, pass, cycle), so the worst a clock-hopper achieves is reaching tomorrow's node a few
    ///   hours early. They can never claim a node twice or exceed the ladder.
    /// </summary>
    public static class PassClock
    {
        /// <summary>Resolve a stored timezone id to a <see cref="TimeZoneInfo"/>; UTC for anything unusable.</summary>
        public static TimeZoneInfo Resolve(string timeZoneId)
        {
            if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;
            try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim()); }
            catch { return TimeZoneInfo.Utc; }   // unknown id / no tzdata / hostile input
        }

        /// <summary>True if the id resolves to a real timezone — the check the profile update uses before storing it.</summary>
        public static bool IsKnown(string timeZoneId)
        {
            if (string.IsNullOrWhiteSpace(timeZoneId)) return false;
            try { TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim()); return true; }
            catch { return false; }
        }

        /// <summary>The player's wall-clock time for a UTC instant.</summary>
        public static DateTime LocalNow(DateTime utcNow, TimeZoneInfo tz)
            => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz ?? TimeZoneInfo.Utc);

        /// <summary>The player's local calendar date for a UTC instant.</summary>
        public static DateTime LocalDate(DateTime utcNow, TimeZoneInfo tz) => LocalNow(utcNow, tz).Date;

        /// <summary>
        /// The UTC instant of a local wall-clock time. Skipped local times (spring forward) step to the first instant
        /// that exists; ambiguous ones (fall back) take the EARLIER offset, so a boundary never lands twice.
        /// </summary>
        public static DateTime ToUtc(DateTime localTime, TimeZoneInfo tz)
        {
            tz = tz ?? TimeZoneInfo.Utc;
            var local = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);

            // Spring forward: 00:00 may not exist that day — walk to the first minute that does.
            for (int i = 0; i < 180 && tz.IsInvalidTime(local); i++) local = local.AddMinutes(1);

            if (tz.IsAmbiguousTime(local))
            {
                var offsets = tz.GetAmbiguousTimeOffsets(local);
                var earliest = offsets[0];
                foreach (var o in offsets) if (o > earliest) earliest = o;   // the LARGER offset is the earlier instant
                return DateTime.SpecifyKind(local - earliest, DateTimeKind.Utc);
            }
            return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        }

        /// <summary>The UTC instant at which the player's day next flips — what the client counts down to.</summary>
        public static DateTime NextLocalMidnightUtc(DateTime utcNow, TimeZoneInfo tz)
            => ToUtc(LocalDate(utcNow, tz).AddDays(1), tz);

        /// <summary>Cycle key for a monthly program: the player's LOCAL year-month, e.g. "2026-09".</summary>
        public static string MonthlyCycleKey(DateTime localNow) => localNow.ToString("yyyy-MM");
    }
}
