using System.Text.Json;
using Khela.Game.Services.Chests;
using Khela.Game.Services.Missions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// Admin editor for the chest catalog. Chests live as JSON in the Redis key <c>khela:chests</c> (the same overlay the
    /// game reads via <see cref="ChestService"/>), with <see cref="ChestCatalog.Defaults"/> as the fallback. Edits apply
    /// on the next open — no redeploy. A chest may never grant the tradeable token; that's validated on save
    /// (<see cref="ChestCatalog.Validate"/>) and re-checked when the chest is opened.
    /// </summary>
    [Authorize(Policy = "Admin")]
    public sealed class ChestsController : Controller
    {
        private readonly IConnectionMultiplexer _redis;

        public ChestsController(IConnectionMultiplexer redis) => _redis = redis;

        [HttpGet]
        public IActionResult Index() => View(BuildModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Save(string json)
        {
            var cfg = ChestCatalog.TryParse(json);
            if (cfg == null)
                return View(nameof(Index), BuildModel(json, "Invalid JSON — it must be a chest config with at least one chest. Nothing saved."));

            var problem = ChestCatalog.Validate(cfg);
            if (problem != null)
                return View(nameof(Index), BuildModel(json, problem + " Nothing saved."));

            try
            {
                // Re-serialize canonically so the stored JSON is clean + identical to what the game reads back.
                var canonical = JsonSerializer.Serialize(cfg, ChestCatalog.JsonOptions);
                _redis.GetDatabase().StringSet(ChestCatalog.RedisKey, canonical);
                TempData["Saved"] = $"Saved {cfg.Chests.Count} chests — live on the next open (no restart)." + BundleWarning(cfg);
            }
            catch { TempData["Error"] = "Could not reach Redis to save."; }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Reset()
        {
            try
            {
                _redis.GetDatabase().KeyDelete(ChestCatalog.RedisKey);
                TempData["Saved"] = "Reset to the built-in default chests." + BundleWarning(ChestCatalog.Defaults());
            }
            catch { TempData["Error"] = "Could not reach Redis."; }
            return RedirectToAction(nameof(Index));
        }

        private ChestsVm BuildModel(string jsonOverride = null, string error = null)
        {
            ChestConfig cfg = null;
            bool overridden = false;
            try
            {
                var raw = _redis.GetDatabase().StringGet(ChestCatalog.RedisKey);
                if (raw.HasValue)
                {
                    cfg = ChestCatalog.TryParse(raw);
                    overridden = cfg != null;
                }
            }
            catch { /* Redis down — show defaults */ }
            cfg ??= ChestCatalog.Defaults();

            return new ChestsVm
            {
                Config = cfg,
                Json = jsonOverride ?? JsonSerializer.Serialize(cfg, ChestCatalog.JsonOptions),   // keep the admin's rejected text on a failed save
                Overridden = overridden,
                Saved = error == null ? TempData["Saved"] as string : null,
                Error = error ?? TempData["Error"] as string,
            };
        }

        // If the daily-mission bundle points at a chest that isn't in the given set, return a warning suffix (else "").
        // Keeps the two overlays honest: editing/removing/resetting chests surfaces an orphaned bundle reference.
        private string BundleWarning(ChestConfig chests)
        {
            try
            {
                var b = EffectiveMissions()?.Bundle;
                if (b != null && chests.Find(b.Key, b.Tier) == null)
                    return $" ⚠ Heads-up: the daily-mission bundle points at {b.Key} / {b.Tier}, which is not in this chest set — fix it on the Missions page.";
            }
            catch { /* best-effort warning only */ }
            return "";
        }

        private MissionConfig EffectiveMissions()
        {
            try
            {
                var raw = _redis.GetDatabase().StringGet(MissionCatalog.RedisKey);
                if (raw.HasValue)
                {
                    var c = MissionCatalog.TryParse(raw);
                    if (c != null) return c;
                }
            }
            catch { /* Redis down — fall back to defaults */ }
            return MissionCatalog.Defaults();
        }
    }

    public sealed class ChestsVm
    {
        public ChestConfig Config { get; set; }
        public string Json { get; set; }
        public bool Overridden { get; set; }   // true if a Redis override is active (vs built-in defaults)
        public string Saved { get; set; }
        public string Error { get; set; }
    }
}
