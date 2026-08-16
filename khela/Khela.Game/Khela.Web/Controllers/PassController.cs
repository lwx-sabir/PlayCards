using System.Text;
using Khela.Common.Rewards;
using Khela.Game.Services.Chests;
using Khela.Game.Services.Config;
using Khela.Game.Services.Pass;
using Khela.Game.Services.Rewards;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// Admin editor for the pass (docs/PASS_SPEC.md). Programs live as JSON in the Redis key <c>khela:pass</c> — the
    /// same overlay the game reads — with <see cref="PassCatalog.Defaults"/> as the fallback. Every save runs
    /// <see cref="PassCatalog.Validate"/> (which refuses, above all, any reward that would pay the tradeable token)
    /// and takes a config backup, so a bad edit is always one restore away.
    /// </summary>
    [Authorize(Policy = "Admin")]
    public sealed class PassController : Controller
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly IConfigBackupService _backups;
        private readonly IRewardGrantService _grants;

        public PassController(IConnectionMultiplexer redis, IConfigBackupService backups, IRewardGrantService grants)
        {
            _redis = redis; _backups = backups; _grants = grants;
        }

        // ---------------- list ----------------

        [HttpGet]
        public IActionResult Index()
        {
            var vm = BuildIndex();
            vm.Saved = TempData["Saved"] as string;
            vm.Error = TempData["Error"] as string;
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(string key, string title, PassCadence cadence)
        {
            var cfg = Effective();
            key = (key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key)) return Back("A program needs a key (e.g. \"season1\").");
            if (cfg.Find(key) != null) return Back($"A program called '{key}' already exists.");

            var program = PassCatalog.MonthlyProgram();
            program.Key = key;
            program.Title = string.IsNullOrWhiteSpace(title) ? key : title.Trim();
            program.Cadence = cadence;
            program.Enabled = false;                        // a new program starts dark — enable it when the ladder is right
            if (cadence == PassCadence.Fixed)
            {
                program.StartUtc = DateTime.UtcNow.Date;
                program.EndUtc = DateTime.UtcNow.Date.AddDays(60);
            }
            cfg.Programs.Add(program);
            return SaveConfig(cfg, $"Created '{key}' (disabled — enable it once the ladder is right).", redirectToEdit: key);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Clone(string key, string newKey)
        {
            var cfg = Effective();
            var source = cfg.Find(key);
            if (source == null) return Back("Program not found.");
            newKey = (newKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newKey)) return Back("The copy needs a new key.");
            if (cfg.Find(newKey) != null) return Back($"A program called '{newKey}' already exists.");

            var copy = PassCatalog.TryParse(PassCatalog.ToJson(new PassConfig { Programs = new List<PassProgram> { source } }))
                       ?.Programs?.FirstOrDefault();                    // deep copy via the same round-trip the game uses
            if (copy == null) return Back("Could not copy that program.");
            copy.Key = newKey;
            copy.Title = source.Title + " (copy)";
            copy.Enabled = false;
            cfg.Programs.Add(copy);
            return SaveConfig(cfg, $"Cloned '{key}' → '{newKey}' (disabled).", redirectToEdit: newKey);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Toggle(string key)
        {
            var cfg = Effective();
            var program = cfg.Find(key);
            if (program == null) return Back("Program not found.");
            program.Enabled = !program.Enabled;
            return SaveConfig(cfg, $"'{key}' is now {(program.Enabled ? "LIVE" : "disabled")}.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(string key)
        {
            var cfg = Effective();
            var program = cfg.Find(key);
            if (program == null) return Back("Program not found.");
            // Claim rows reference the key forever; deleting one would orphan a player's history. Disable instead.
            if (string.Equals(key, PassCatalog.MonthlyKey, StringComparison.OrdinalIgnoreCase))
                return Back("The monthly pass can't be deleted — disable it instead, so past claims stay readable.");
            cfg.Programs.Remove(program);
            return SaveConfig(cfg, $"Deleted '{key}'. Past claims for it stay in the ledger.");
        }

        // ---------------- edit one program ----------------

        [HttpGet]
        public IActionResult Edit(string key)
        {
            var cfg = Effective();
            var program = cfg.Find(key);
            if (program == null) return Back("Program not found.");

            var vm = BuildEdit(program);
            vm.Saved = TempData["Saved"] as string;
            vm.Error = TempData["Error"] as string;
            return View(vm);
        }

        /// <summary>Save the program's settings and its whole ladder (the node grid posts one row per node).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Save(PassEditForm form)
        {
            var cfg = Effective();
            var program = cfg.Find(form.Key);
            if (program == null) return Back("Program not found.");

            program.Title = (form.Title ?? program.Key).Trim();
            program.Enabled = form.Enabled;
            program.Cadence = form.Cadence;
            program.CatchUp = form.CatchUp;
            program.AdsPerCatchUp = form.AdsPerCatchUp;
            program.MaxAdCatchUpsPerCycle = form.MaxAdCatchUpsPerCycle;
            program.GoldenProductIdApple = Trim(form.GoldenProductIdApple);
            program.GoldenProductIdGoogle = Trim(form.GoldenProductIdGoogle);
            program.GoldenPriceUsd = form.GoldenPriceUsd;
            program.StartUtc = form.Cadence == PassCadence.Fixed ? form.StartUtc : null;
            program.EndUtc = form.Cadence == PassCadence.Fixed ? form.EndUtc : null;

            var nodes = new List<PassNode>();
            for (int i = 0; i < (form.Free?.Count ?? 0); i++)
            {
                var free = PassRewardText.Parse(form.Free[i], out var freeError);
                if (free == null) return BackToEdit(form.Key, $"Day {i + 1} (Free): {freeError}");
                var golden = PassRewardText.Parse(form.Golden != null && i < form.Golden.Count ? form.Golden[i] : null, out var goldenError);
                if (golden == null) return BackToEdit(form.Key, $"Day {i + 1} (Golden): {goldenError}");

                nodes.Add(new PassNode
                {
                    Index = i + 1,
                    IsMilestone = form.Milestone != null && form.Milestone.Contains(i + 1),
                    Free = free,
                    Golden = golden,
                    FreeText = At(form.FreeText, i),
                    GoldenText = At(form.GoldenText, i),
                });
            }
            program.Nodes = nodes;

            return SaveConfig(cfg, $"Saved '{program.Key}' — {nodes.Count} days, live on the next request.", redirectToEdit: program.Key);
        }

        /// <summary>Bulk edit: write the same payload across a range of days. Blank keeps that track as it was.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ApplyRange(string key, int from, int to, string free, string golden, bool? milestone)
        {
            var cfg = Effective();
            var program = cfg.Find(key);
            if (program == null) return Back("Program not found.");
            if (to < from) (from, to) = (to, from);

            List<RewardGrant> freeLines = null, goldenLines = null;
            if (!string.IsNullOrWhiteSpace(free))
            {
                freeLines = PassRewardText.Parse(free, out var e);
                if (freeLines == null) return BackToEdit(key, $"Free: {e}");
            }
            if (!string.IsNullOrWhiteSpace(golden))
            {
                goldenLines = PassRewardText.Parse(golden, out var e);
                if (goldenLines == null) return BackToEdit(key, $"Golden: {e}");
            }

            int touched = 0;
            foreach (var node in program.Nodes.Where(n => n.Index >= from && n.Index <= to))
            {
                if (freeLines != null) node.Free = Copy(freeLines);
                if (goldenLines != null) node.Golden = Copy(goldenLines);
                if (milestone.HasValue) node.IsMilestone = milestone.Value;
                touched++;
            }
            return SaveConfig(cfg, $"Applied to days {from}–{to} ({touched} day(s)).", redirectToEdit: key);
        }

        /// <summary>Grow or shrink the ladder. New days start as a copy of the last one.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Resize(string key, int length)
        {
            var cfg = Effective();
            var program = cfg.Find(key);
            if (program == null) return Back("Program not found.");

            int max = program.Cadence == PassCadence.Monthly ? PassCatalog.MaxMonthlyNodes : PassCatalog.MaxFixedNodes;
            length = Math.Clamp(length, 1, max);

            while (program.Nodes.Count > length) program.Nodes.RemoveAt(program.Nodes.Count - 1);
            while (program.Nodes.Count < length)
            {
                var last = program.Nodes.LastOrDefault();
                program.Nodes.Add(new PassNode
                {
                    Index = program.Nodes.Count + 1,
                    Free = Copy(last?.Free) ?? new List<RewardGrant> { RewardGrant.Currency("Chips", 1000m) },
                    Golden = Copy(last?.Golden) ?? new List<RewardGrant>(),
                });
            }
            for (int i = 0; i < program.Nodes.Count; i++) program.Nodes[i].Index = i + 1;

            return SaveConfig(cfg, $"'{key}' is now {length} days.", redirectToEdit: key);
        }

        // ---------------- raw JSON + backups ----------------

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SaveJson(string json)
        {
            var cfg = PassCatalog.TryParse(json);
            if (cfg == null) return Back("Invalid JSON — nothing saved.");
            return SaveConfig(cfg, "Saved from JSON.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Reset()
        {
            try
            {
                _backups.BackupAsync(PassCatalog.RedisKey).GetAwaiter().GetResult();   // keep what we're about to drop
                _redis.GetDatabase().KeyDelete(PassCatalog.RedisKey);
                TempData["Saved"] = "Reset to the built-in monthly pass. The previous config was backed up first.";
            }
            catch { TempData["Error"] = "Could not reach Redis."; }
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public IActionResult Download(string file)
        {
            var json = _backups.Read(PassCatalog.RedisKey, file);
            if (json == null) return NotFound();
            return File(Encoding.UTF8.GetBytes(json), "application/json", $"khela-pass-{file}");
        }

        /// <summary>Load a snapshot into the editor. It is NOT written to Redis here — it comes back through the same
        /// validate-and-save path as any other edit, so a bad old file can't go live unchecked.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Restore(string file)
        {
            var json = _backups.Read(PassCatalog.RedisKey, file);
            if (json == null) return Back("That backup is gone.");

            var cfg = PassCatalog.TryParse(json);
            if (cfg == null) return Back("That backup won't parse — download it and check by hand.");

            var vm = BuildIndex(json);
            vm.Error = $"Loaded backup {file} into the editor below. Review it, then press Save JSON to make it live.";
            return View(nameof(Index), vm);
        }

        // ---------------- helpers ----------------

        private PassConfig Effective()
        {
            try
            {
                var json = _redis.GetDatabase().StringGet(PassCatalog.RedisKey);
                if (json.HasValue)
                {
                    var cfg = PassCatalog.TryParse(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { /* Redis down → defaults, same as the game */ }
            return PassCatalog.Defaults();
        }

        private bool Overridden()
        {
            try { return _redis.GetDatabase().KeyExists(PassCatalog.RedisKey); }
            catch { return false; }
        }

        private ChestConfig Chests()
        {
            try
            {
                var json = _redis.GetDatabase().StringGet(ChestCatalog.RedisKey);
                if (json.HasValue) return ChestCatalog.TryParse(json) ?? ChestCatalog.Defaults();
            }
            catch { }
            return ChestCatalog.Defaults();
        }

        /// <summary>Validate, back up, then write. Nothing reaches Redis that the game wouldn't accept.</summary>
        private IActionResult SaveConfig(PassConfig cfg, string message, string redirectToEdit = null)
        {
            var payable = new HashSet<RewardKind>(Enum.GetValues<RewardKind>().Where(k => _grants.CanGrant(k)));
            var problem = PassCatalog.Validate(cfg, Chests(), payable);
            if (problem != null)
                return redirectToEdit != null ? BackToEdit(redirectToEdit, problem + " Nothing saved.") : Back(problem + " Nothing saved.");

            try
            {
                _backups.BackupAsync(PassCatalog.RedisKey).GetAwaiter().GetResult();   // snapshot the OLD value first
                _redis.GetDatabase().StringSet(PassCatalog.RedisKey, PassCatalog.ToJson(cfg));
                TempData["Saved"] = message;
            }
            catch { TempData["Error"] = "Could not reach Redis to save."; }

            return redirectToEdit != null
                ? RedirectToAction(nameof(Edit), new { key = redirectToEdit })
                : RedirectToAction(nameof(Index));
        }

        private PassIndexVm BuildIndex(string json = null)
        {
            var cfg = Effective();
            var now = DateTime.UtcNow;
            return new PassIndexVm
            {
                Config = cfg,
                Overridden = Overridden(),
                Json = json ?? PassCatalog.ToJson(cfg),
                Backups = _backups.List(PassCatalog.RedisKey).Take(20).ToList(),
                Cycles = cfg.Programs.ToDictionary(p => p.Key, p => PassCatalog.CurrentCycle(p, now, TimeZoneInfo.Utc)),
            };
        }

        private PassEditVm BuildEdit(PassProgram program)
        {
            var cycle = PassCatalog.CurrentCycle(program, DateTime.UtcNow, TimeZoneInfo.Utc);
            return new PassEditVm
            {
                Program = program,
                Cycle = cycle,
                FreeTotals = PassCatalog.Totals(program.Nodes, golden: false),
                GoldenTotals = PassCatalog.Totals(program.Nodes, golden: true),
                ChestIds = Chests().Chests.Select(c => $"{c.Key}:{c.Tier}").Distinct().OrderBy(s => s).ToList(),
            };
        }

        private static List<RewardGrant> Copy(List<RewardGrant> lines)
            => lines?.Select(l => new RewardGrant { Kind = l.Kind, Id = l.Id, Amount = l.Amount }).ToList();

        private static string Trim(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        /// <summary>The node grid posts one entry per day; a blank label means "derive it" and is stored as null.</summary>
        private static string At(List<string> values, int index)
            => (values != null && index < values.Count) ? Trim(values[index]) : null;

        private IActionResult Back(string error)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index));
        }

        private IActionResult BackToEdit(string key, string error)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Edit), new { key });
        }
    }

    /// <summary>The node grid posts one entry per day, plus the program's settings.</summary>
    public sealed class PassEditForm
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public bool Enabled { get; set; }
        public PassCadence Cadence { get; set; }
        public CatchUpPolicy CatchUp { get; set; }
        public int AdsPerCatchUp { get; set; }
        public int MaxAdCatchUpsPerCycle { get; set; }
        public string GoldenProductIdApple { get; set; }
        public string GoldenProductIdGoogle { get; set; }
        public decimal GoldenPriceUsd { get; set; }
        public DateTime? StartUtc { get; set; }
        public DateTime? EndUtc { get; set; }

        public List<string> Free { get; set; } = new();
        public List<string> Golden { get; set; } = new();
        public List<string> FreeText { get; set; } = new();     // the card's headline; blank = derive from the rewards
        public List<string> GoldenText { get; set; } = new();
        public List<int> Milestone { get; set; } = new();
    }

    public sealed class PassIndexVm
    {
        public PassConfig Config { get; set; }
        public bool Overridden { get; set; }
        public string Json { get; set; }
        public List<ConfigBackupInfo> Backups { get; set; } = new();
        public Dictionary<string, PassCycle> Cycles { get; set; } = new();
        public string Saved { get; set; }
        public string Error { get; set; }
    }

    public sealed class PassEditVm
    {
        public PassProgram Program { get; set; }
        public PassCycle Cycle { get; set; }
        public Dictionary<string, decimal> FreeTotals { get; set; } = new();
        public Dictionary<string, decimal> GoldenTotals { get; set; } = new();
        public List<string> ChestIds { get; set; } = new();
        public string Saved { get; set; }
        public string Error { get; set; }
    }
}
