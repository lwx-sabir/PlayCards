using System;
using System.Collections.Generic;
using System.Linq;
using Khela.Common.Rewards;
using Khela.Game.Services.Daily;
using Khela.Game.Services.Rewards;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the daily login ladder's rules: the legal guardrail that no reward may ever be the tradeable token
    /// (CLAUDE.md NON-NEGOTIABLE #2/#4), which day is claimable and what a missed one costs, and the shared
    /// <c>Rewards:BypassAdForMissedDays</c> switch. Pure — no DB, no Redis, no clock.
    ///
    /// The rule that matters most and is asserted from several angles: NOTHING may reach a day the player's own
    /// calendar hasn't arrived at. That is what stops a changed device clock from farming the ladder.
    /// </summary>
    public class DailyCatalogTests
    {
        private static DailyNode Node(int index, params RewardGrant[] rewards)
            => new DailyNode
            {
                Index = index,
                Rewards = (rewards.Length > 0 ? rewards : new[] { RewardGrant.Currency("Chips", 100m) }).ToList(),
            };

        /// <summary>A valid ladder. The cap defaults to something the ladder can actually hold — Validate refuses a
        /// cap larger than the ladder, and that error would otherwise mask whatever a test is really asserting.</summary>
        private static DailyConfig Config(int days = 30, int adsPer = 1, int? maxCatchUps = null)
            => new DailyConfig
            {
                Enabled = true,
                Title = "Daily Rewards",
                AdsPerCatchUp = adsPer,
                MaxAdCatchUpsPerCycle = maxCatchUps ?? Math.Min(5, days),
                Nodes = Enumerable.Range(1, days).Select(i => Node(i)).ToList(),
            };

        private static ISet<int> Claimed(params int[] nodes) => new HashSet<int>(nodes);

        // ---------------- the shipped ladder ----------------

        [Fact]
        public void Defaults_are_a_valid_thirty_day_ladder()
        {
            var cfg = DailyCatalog.Defaults();

            Assert.True(cfg.Enabled);
            Assert.Equal(DailyCatalog.DefaultLadderLength, cfg.Days);
            Assert.Null(DailyCatalog.Validate(cfg));

            // Indexes run 1..N with no gaps — the claim rules index by day and would silently skip a hole.
            Assert.Equal(Enumerable.Range(1, cfg.Days), cfg.Nodes.Select(n => n.Index));
            Assert.All(cfg.Nodes, n => Assert.NotEmpty(n.Rewards));
        }

        [Fact]
        public void Defaults_never_pay_the_tradeable_token()
        {
            var cfg = DailyCatalog.Defaults();

            foreach (var line in cfg.Nodes.SelectMany(n => n.Rewards).Where(l => l.Kind == RewardKind.Currency))
            {
                Assert.True(RewardCurrencies.TryParse(line.Id, out var currency), $"'{line.Id}' is not a currency");
                Assert.True(RewardCurrencies.IsAllowed(currency), $"{currency} may never be a reward");
            }
        }

        // ---------------- what's claimable ----------------

        [Fact]
        public void Today_is_free_and_tomorrow_is_out_of_reach()
        {
            var a = DailyCatalog.Availability(Config(), dayIndex: 5, Claimed());

            Assert.Equal(5, a.DayIndex);
            Assert.Equal(5, a.MaxNode);
            Assert.Contains(5, a.Claimable);

            // The whole point: no future day is offered by any route.
            Assert.DoesNotContain(6, a.Claimable);
            Assert.DoesNotContain(6, a.AdUnlockable);
            Assert.DoesNotContain(6, a.Missed);
        }

        [Fact]
        public void A_day_already_taken_is_offered_nowhere()
        {
            var a = DailyCatalog.Availability(Config(), dayIndex: 5, Claimed(3, 5));

            Assert.DoesNotContain(5, a.Claimable);      // today, already collected
            Assert.DoesNotContain(3, a.AdUnlockable);   // a missed day, already bought back
            Assert.DoesNotContain(3, a.Missed);
        }

        [Fact]
        public void Day_index_past_the_ladder_is_clamped_to_the_last_day()
        {
            // The service rolls the run over before this is reached, but the pure function must still be total.
            var a = DailyCatalog.Availability(Config(days: 30), dayIndex: 99, Claimed());

            Assert.Equal(30, a.DayIndex);
            Assert.Equal(30, a.MaxNode);
            Assert.Contains(30, a.Claimable);
        }

        [Fact]
        public void An_empty_ladder_offers_nothing_rather_than_throwing()
        {
            var a = DailyCatalog.Availability(new DailyConfig(), dayIndex: 3, Claimed());

            Assert.Empty(a.Claimable);
            Assert.Empty(a.AdUnlockable);
            Assert.Empty(a.Missed);
        }

        // ---------------- missed days ----------------

        [Fact]
        public void Missed_days_are_ad_unlockable_while_the_cap_allows()
        {
            var a = DailyCatalog.Availability(Config(adsPer: 1, maxCatchUps: 5), dayIndex: 5, Claimed(5));

            Assert.Equal(new[] { 1, 2, 3, 4 }, a.AdUnlockable);
            Assert.Empty(a.Missed);
            Assert.Equal(1, a.AdsPerUnlock);
            Assert.Equal(5, a.AdUnlocksLeft);
        }

        [Fact]
        public void Missed_days_are_gone_once_the_cap_is_spent()
        {
            var a = DailyCatalog.Availability(Config(maxCatchUps: 2), dayIndex: 6, Claimed(6), adCatchUpsUsed: 2);

            Assert.Equal(0, a.AdUnlocksLeft);
            Assert.Empty(a.AdUnlockable);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, a.Missed);   // still listed, so the UI can show them as lost
        }

        [Fact]
        public void Catch_up_can_be_switched_off_entirely()
        {
            foreach (var cfg in new[] { Config(adsPer: 0), Config(maxCatchUps: 0) })
            {
                var a = DailyCatalog.Availability(cfg, dayIndex: 4, Claimed(4));

                Assert.Empty(a.AdUnlockable);
                Assert.Equal(new[] { 1, 2, 3 }, a.Missed);
                Assert.Equal(0, a.AdUnlocksLeft);
            }
        }

        [Fact]
        public void Ad_unlocks_left_never_goes_negative()
        {
            // A hand-edited cap, or a config shrunk after the credits were spent.
            var a = DailyCatalog.Availability(Config(maxCatchUps: 2), dayIndex: 5, Claimed(5), adCatchUpsUsed: 99);

            Assert.Equal(0, a.AdUnlocksLeft);
            Assert.Empty(a.AdUnlockable);
        }

        // ---------------- the shared bypass switch ----------------

        [Fact]
        public void Bypass_hands_missed_days_over_free()
        {
            var a = DailyCatalog.Availability(Config(), dayIndex: 5, Claimed(5), bypassAdCatchUp: true);

            Assert.Equal(new[] { 1, 2, 3, 4 }, a.Claimable);
            Assert.Empty(a.AdUnlockable);
            Assert.Empty(a.Missed);
        }

        [Fact]
        public void Bypass_still_cannot_reach_a_day_the_calendar_has_not_arrived_at()
        {
            // The switch is a testing convenience; it must only ever make an ALREADY-REACHED day cheaper.
            var a = DailyCatalog.Availability(Config(days: 30), dayIndex: 3, Claimed(), bypassAdCatchUp: true);

            Assert.Equal(new[] { 1, 2, 3 }, a.Claimable);
            Assert.DoesNotContain(4, a.Claimable);
            Assert.Equal(3, a.MaxNode);
        }

        [Fact]
        public void Bypass_ignores_the_catch_up_cap()
        {
            // Free means free: the cap exists to bound ad inventory, and there are no ads being watched here.
            var a = DailyCatalog.Availability(Config(maxCatchUps: 1), dayIndex: 6, Claimed(6),
                adCatchUpsUsed: 1, bypassAdCatchUp: true);

            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, a.Claimable);
            Assert.Empty(a.Missed);
        }

        // ---------------- validation ----------------

        [Fact]
        public void Validate_refuses_a_reward_that_would_pay_the_tradeable_token()
        {
            var cfg = Config(days: 2);
            cfg.Nodes[1].Rewards = new List<RewardGrant> { RewardGrant.Currency("Tokens", 5m) };

            var problem = DailyCatalog.Validate(cfg);

            Assert.NotNull(problem);
            Assert.Contains("Tokens", problem, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Validate_refuses_an_empty_or_oversized_ladder()
        {
            Assert.NotNull(DailyCatalog.Validate(new DailyConfig()));
            Assert.NotNull(DailyCatalog.Validate(Config(days: DailyCatalog.MaxNodes + 1)));
        }

        [Fact]
        public void Validate_refuses_a_day_with_no_reward()
        {
            var cfg = Config(days: 3);
            cfg.Nodes[1].Rewards = new List<RewardGrant>();

            var problem = DailyCatalog.Validate(cfg);

            Assert.NotNull(problem);
            Assert.Contains("Day 2", problem);
        }

        [Fact]
        public void Validate_refuses_a_non_positive_amount()
        {
            var cfg = Config(days: 1);
            cfg.Nodes[0].Rewards = new List<RewardGrant> { RewardGrant.Currency("Chips", 0m) };

            Assert.NotNull(DailyCatalog.Validate(cfg));
        }

        [Fact]
        public void Validate_refuses_a_malformed_chest_id()
        {
            var cfg = Config(days: 1);
            cfg.Nodes[0].Rewards = new List<RewardGrant> { new RewardGrant { Kind = RewardKind.Chest, Id = "NoTier", Amount = 1m } };

            var problem = DailyCatalog.Validate(cfg);

            Assert.NotNull(problem);
            Assert.Contains("Key:Tier", problem);
        }

        [Fact]
        public void Validate_refuses_an_ad_cap_larger_than_the_ladder()
        {
            var cfg = Config(days: 5, maxCatchUps: 6);

            Assert.NotNull(DailyCatalog.Validate(cfg));
        }

        [Fact]
        public void Validate_refuses_a_card_label_longer_than_the_card()
        {
            var cfg = Config(days: 1);
            cfg.Nodes[0].Text = new string('x', DailyCatalog.MaxCardLabelLength + 1);

            Assert.NotNull(DailyCatalog.Validate(cfg));
        }

        // ---------------- round trip ----------------

        [Fact]
        public void Config_round_trips_through_json()
        {
            var original = DailyCatalog.Defaults();

            var back = DailyCatalog.Parse(DailyCatalog.Serialize(original), out var error);

            Assert.Null(error);
            Assert.Equal(original.Days, back.Days);
            Assert.Equal(original.Title, back.Title);
            Assert.Equal(original.AdsPerCatchUp, back.AdsPerCatchUp);
            Assert.Equal(
                original.Nodes.SelectMany(n => n.Rewards).Select(r => $"{r.Kind}:{r.Id}:{r.Amount}"),
                back.Nodes.SelectMany(n => n.Rewards).Select(r => $"{r.Kind}:{r.Id}:{r.Amount}"));
        }

        [Fact]
        public void A_broken_overlay_falls_back_to_defaults_instead_of_taking_the_reward_down()
        {
            var cfg = DailyCatalog.Parse("{ not json", out var error);

            Assert.NotNull(error);
            Assert.Equal(DailyCatalog.DefaultLadderLength, cfg.Days);
        }

        [Fact]
        public void Normalize_renumbers_a_hand_edited_ladder()
        {
            var cfg = new DailyConfig
            {
                Nodes = new List<DailyNode> { Node(7), Node(3), Node(99) },
            };

            DailyCatalog.Normalize(cfg);

            Assert.Equal(new[] { 1, 2, 3 }, cfg.Nodes.Select(n => n.Index));
        }
    }
}
