using System;
using System.Collections.Generic;
using System.Linq;
using Khela.Common.Rewards;
using Khela.Game.Services.Chests;
using Khela.Game.Services.Config;
using Khela.Game.Services.Daily;
using Khela.Game.Services.Pass;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// Admin editor for the daily login reward. The ladder lives as JSON in the Redis key <c>khela:daily</c> — the
    /// same overlay the game reads — with <see cref="DailyCatalog.Defaults"/> as the fallback. Every save runs
    /// <see cref="DailyCatalog.Validate"/> (which refuses, above all, any reward that would pay the tradeable token)
    /// and takes a config backup first, so a bad edit is always one restore away.
    ///
    /// One page, not a list: unlike the pass there is exactly one daily ladder, so there is nothing to choose between.
    /// </summary>
    [Authorize(Policy = "Admin")]
    public sealed class DailyController : Controller
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly IConfigBackupService _backups;

        public DailyController(IConnectionMultiplexer redis, IConfigBackupService backups)
        {
            _redis = redis; _backups = backups;
        }

        [HttpGet]
        public IActionResult Index()
        {
            var vm = BuildVm();
            vm.Saved = TempData["Saved"] as string;
            vm.Error = TempData["Error"] as string;
            return View(vm);
        }

        /// <summary>Save the settings and the whole ladder (the day cards post one hidden row per day, in order).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Save(DailyEditForm form)
        {
            var cfg = Effective();

            cfg.Enabled = form.Enabled;
            cfg.Title = string.IsNullOrWhiteSpace(form.Title) ? "Daily Rewards" : form.Title.Trim();
            cfg.AdsPerCatchUp = form.AdsPerCatchUp;
            cfg.MaxAdCatchUpsPerCycle = form.MaxAdCatchUpsPerCycle;

            var nodes = new List<DailyNode>();
            for (int i = 0; i < (form.Rewards?.Count ?? 0); i++)
            {
                var rewards = PassRewardText.Parse(form.Rewards[i], out var error);
                if (rewards == null) return Back($"Day {i + 1}: {error}");

                nodes.Add(new DailyNode
                {
                    Index = i + 1,
                    IsMilestone = form.Milestone != null && form.Milestone.Contains(i + 1),
                    Rewards = rewards,
                    Text = At(form.Text, i),
                });
            }
            cfg.Nodes = nodes;

            return SaveConfig(cfg, $"Saved — {nodes.Count} days, live on the next request.");
        }

        /// <summary>Bulk edit: write the same payload across a range of days. Blank leaves the rewards alone.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ApplyRange(int from, int to, string rewards, bool? milestone)
        {
            var cfg = Effective();
            if (to < from) (from, to) = (to, from);

            List<RewardGrant> lines = null;
            if (!string.IsNullOrWhiteSpace(rewards))
            {
                lines = PassRewardText.Parse(rewards, out var e);
                if (lines == null) return Back($"Rewards: {e}");
            }

            int touched = 0;
            foreach (var node in cfg.Nodes.Where(n => n.Index >= from && n.Index <= to))
            {
                if (lines != null) node.Rewards = Copy(lines);
                if (milestone.HasValue) node.IsMilestone = milestone.Value;
                touched++;
            }
            return SaveConfig(cfg, $"Applied to days {from}–{to} ({touched} day(s)).");
        }

        /// <summary>Grow or shrink the ladder. New days start as a copy of the last one.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Resize(int length)
        {
            var cfg = Effective();
            length = Math.Clamp(length, 1, DailyCatalog.MaxNodes);

            while (cfg.Nodes.Count > length) cfg.Nodes.RemoveAt(cfg.Nodes.Count - 1);
            while (cfg.Nodes.Count < length)
            {
                var last = cfg.Nodes.LastOrDefault();
                cfg.Nodes.Add(new DailyNode
                {
                    Index = cfg.Nodes.Count + 1,
                    Rewards = Copy(last?.Rewards) ?? new List<RewardGrant> { RewardGrant.Currency("Chips", 500m) },
                });
            }
            for (int i = 0; i < cfg.Nodes.Count; i++) cfg.Nodes[i].Index = i + 1;

            return SaveConfig(cfg, $"The ladder is now {length} days.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SaveJson(string json)
        {
            var cfg = DailyCatalog.Parse(json, out var error);
            if (error != null) return Back(error + " Nothing saved.");
            return SaveConfig(cfg, "Saved from JSON.");
        }

        /// <summary>Drop the override and fall back to the built-in ladder. The old value is backed up first.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Reset()
        {
            try
            {
                _backups.BackupAsync(DailyCatalog.RedisKey).GetAwaiter().GetResult();   // keep what we're about to drop
                _redis.GetDatabase().KeyDelete(DailyCatalog.RedisKey);
                TempData["Saved"] = "Reset to the built-in daily ladder. The previous config was backed up first.";
            }
            catch { TempData["Error"] = "Could not reach Redis."; }
            return RedirectToAction(nameof(Index));
        }

        /// <summary>Load a snapshot into the editor. It is NOT written to Redis here — it comes back through the same
        /// validated save path, so an old bad config can't slip past.</summary>
        [HttpGet]
        public IActionResult Restore(string file)
        {
            var json = _backups.Read(DailyCatalog.RedisKey, file);
            if (json == null) return Back("That backup no longer exists.");

            var vm = BuildVm(json);
            vm.Error = $"Loaded backup {file} into the editor below. Review it, then press Save JSON to make it live.";
            return View(nameof(Index), vm);
        }

        // ---------------- helpers ----------------

        private DailyConfig Effective()
        {
            try
            {
                var json = _redis.GetDatabase().StringGet(DailyCatalog.RedisKey);
                if (json.HasValue)
                {
                    var cfg = DailyCatalog.Parse(json, out var error);
                    if (error == null) return cfg;
                }
            }
            catch { /* Redis down → defaults, same as the game */ }
            return DailyCatalog.Defaults();
        }

        private bool Overridden()
        {
            try { return _redis.GetDatabase().KeyExists(DailyCatalog.RedisKey); }
            catch { return false; }
        }

        /// <summary>Validate, back up, then write. Nothing reaches Redis that the game wouldn't accept.</summary>
        private IActionResult SaveConfig(DailyConfig cfg, string message)
        {
            DailyCatalog.Normalize(cfg);

            var problem = DailyCatalog.Validate(cfg);
            if (problem != null) return Back(problem + " Nothing saved.");

            try
            {
                _backups.BackupAsync(DailyCatalog.RedisKey).GetAwaiter().GetResult();   // snapshot the OLD value first
                _redis.GetDatabase().StringSet(DailyCatalog.RedisKey, DailyCatalog.Serialize(cfg));
                TempData["Saved"] = message;
            }
            catch { TempData["Error"] = "Could not reach Redis to save."; }

            return RedirectToAction(nameof(Index));
        }

        private DailyVm BuildVm(string json = null)
        {
            var cfg = Effective();
            return new DailyVm
            {
                Config = cfg,
                Overridden = Overridden(),
                Json = json ?? DailyCatalog.Serialize(cfg),
                Backups = SafeBackups(),
                ChestIds = ChestIds(),
                Totals = Totals(cfg),
            };
        }

        private List<ConfigBackupInfo> SafeBackups()
        {
            try { return _backups.List(DailyCatalog.RedisKey).ToList(); }
            catch { return new List<ConfigBackupInfo>(); }
        }

        private List<string> ChestIds()
        {
            try
            {
                var json = _redis.GetDatabase().StringGet(ChestCatalog.RedisKey);
                var cfg = json.HasValue ? (ChestCatalog.TryParse(json) ?? ChestCatalog.Defaults()) : ChestCatalog.Defaults();
                // One row per (key, tier) already, so the id is just the pair — same as the pass editor builds it.
                return cfg.Chests?.Select(c => $"{c.Key}:{c.Tier}").Distinct().OrderBy(s => s).ToList()
                       ?? new List<string>();
            }
            catch { return new List<string>(); }
        }

        /// <summary>What a player who collects every day of one full run walks away with — the number that decides
        /// whether this ladder is affordable.</summary>
        private static Dictionary<string, decimal> Totals(DailyConfig cfg)
        {
            var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in cfg.Nodes ?? new List<DailyNode>())
            foreach (var line in node.Rewards ?? new List<RewardGrant>())
            {
                if (line == null) continue;
                var key = line.Kind == RewardKind.Xp ? "XP" : (line.Id ?? line.Kind.ToString());
                totals[key] = totals.TryGetValue(key, out var v) ? v + line.Amount : line.Amount;
            }
            return totals;
        }

        private static List<RewardGrant> Copy(List<RewardGrant> lines)
            => lines?.Select(l => new RewardGrant { Kind = l.Kind, Id = l.Id, Amount = l.Amount, Images = l.Images?.ToList() }).ToList();

        private static string At(List<string> list, int i) => list != null && i < list.Count ? list[i] : null;

        private IActionResult Back(string error)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index));
        }
    }

    /// <summary>The day grid posts one hidden row per day, in order — position IS the day number.</summary>
    public sealed class DailyEditForm
    {
        public bool Enabled { get; set; }
        public string Title { get; set; }
        public int AdsPerCatchUp { get; set; }
        public int MaxAdCatchUpsPerCycle { get; set; }

        public List<string> Rewards { get; set; }
        public List<string> Text { get; set; }
        public List<int> Milestone { get; set; }
    }

    public sealed class DailyVm
    {
        public DailyConfig Config { get; set; }
        public bool Overridden { get; set; }
        public string Json { get; set; }
        public List<ConfigBackupInfo> Backups { get; set; } = new List<ConfigBackupInfo>();
        public List<string> ChestIds { get; set; } = new List<string>();
        public Dictionary<string, decimal> Totals { get; set; } = new Dictionary<string, decimal>();

        public string Saved { get; set; }
        public string Error { get; set; }
    }
}
