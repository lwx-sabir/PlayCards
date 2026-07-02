using System.Text.Json;
using Khela.Game.Services.Chests;
using Khela.Game.Services.Missions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// Admin editor for the daily-mission catalog. The pool/counts/bundle live as JSON in the Redis key
    /// <c>khela:missions</c> (the same overlay the game reads via <see cref="MissionService"/>), with the built-in
    /// <see cref="MissionCatalog.Defaults"/> as the fallback. Edits apply on the NEXT fetch/round — no redeploy.
    /// </summary>
    [Authorize(Policy = "Admin")]
    public sealed class MissionsController : Controller
    {
        private readonly IConnectionMultiplexer _redis;

        public MissionsController(IConnectionMultiplexer redis) => _redis = redis;

        [HttpGet]
        public IActionResult Index() => View(BuildModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Save(string json)
        {
            var cfg = MissionCatalog.TryParse(json);
            if (cfg == null)
                return View(nameof(Index), BuildModel(json, "Invalid JSON — it must be a mission config with at least one mission. Nothing saved."));

            // Referential integrity: the complete-all bundle must point at a chest that EXISTS, or every all-completing
            // player is blocked from the reward. Re-check against the live chest catalog before saving.
            var chests = EffectiveChests();
            if (cfg.Bundle != null && chests.Find(cfg.Bundle.Key, cfg.Bundle.Tier) == null)
                return View(nameof(Index), BuildModel(json, $"Bundle points at a chest that doesn't exist: {cfg.Bundle.Key} / {cfg.Bundle.Tier}. Create it on the Chests page first. Nothing saved."));

            try
            {
                // Re-serialize canonically so the stored JSON is clean + identical to what the game reads back.
                var canonical = JsonSerializer.Serialize(cfg, MissionCatalog.JsonOptions);
                _redis.GetDatabase().StringSet(MissionCatalog.RedisKey, canonical);
                TempData["Saved"] = $"Saved {cfg.Missions.Count} missions — live on the next fetch (no restart).";
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
                _redis.GetDatabase().KeyDelete(MissionCatalog.RedisKey);
                TempData["Saved"] = "Reset to the built-in default missions.";
            }
            catch { TempData["Error"] = "Could not reach Redis."; }
            return RedirectToAction(nameof(Index));
        }

        private MissionsVm BuildModel(string jsonOverride = null, string error = null)
        {
            MissionConfig cfg = null;
            bool overridden = false;
            try
            {
                var raw = _redis.GetDatabase().StringGet(MissionCatalog.RedisKey);
                if (raw.HasValue)
                {
                    cfg = MissionCatalog.TryParse(raw);
                    overridden = cfg != null;
                }
            }
            catch { /* Redis down — show defaults */ }
            cfg ??= MissionCatalog.Defaults();

            return new MissionsVm
            {
                Config = cfg,
                Json = jsonOverride ?? JsonSerializer.Serialize(cfg, MissionCatalog.JsonOptions),   // keep the admin's rejected text on a failed save
                Overridden = overridden,
                Saved = error == null ? TempData["Saved"] as string : null,
                Error = error ?? TempData["Error"] as string,
            };
        }

        // The live chest catalog (Redis overlay ?? defaults) — used to validate the bundle's chest reference resolves.
        private ChestConfig EffectiveChests()
        {
            try
            {
                var raw = _redis.GetDatabase().StringGet(ChestCatalog.RedisKey);
                if (raw.HasValue)
                {
                    var c = ChestCatalog.TryParse(raw);
                    if (c != null) return c;
                }
            }
            catch { /* Redis down — fall back to defaults */ }
            return ChestCatalog.Defaults();
        }
    }

    public sealed class MissionsVm
    {
        public MissionConfig Config { get; set; }
        public string Json { get; set; }
        public bool Overridden { get; set; }   // true if a Redis override is active (vs built-in defaults)
        public string Saved { get; set; }
        public string Error { get; set; }
    }
}
