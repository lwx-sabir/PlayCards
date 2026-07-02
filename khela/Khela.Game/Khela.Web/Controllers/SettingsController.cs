using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// Admin settings, split into Casino and Game sections. The CASINO progression knobs are runtime-tunable:
    /// they're stored in the Redis hash <c>khela:settings</c> (keyed by config key), which ProgressionService
    /// overlays onto its appsettings base on every accrual — so a save applies on the NEXT round, no restart.
    /// The GAME timing settings are shown read-only (still appsettings-bound; live editing is a follow-up).
    /// </summary>
    [Authorize(Policy = "Admin")]
    public sealed class SettingsController : Controller
    {
        private const string SettingsHashKey = "khela:settings";   // must match ProgressionService.SettingsHashKey

        private readonly IConfiguration _config;
        private readonly IConnectionMultiplexer _redis;

        public SettingsController(IConfiguration config, IConnectionMultiplexer redis)
        {
            _config = config;
            _redis = redis;
        }

        // Editable casino (progression) knobs. Key = the config key (also the Redis hash field + form field name).
        private static readonly SettingDef[] CasinoDefs =
        {
            new("Progression:XpChipsPerPoint", "Chips wagered per 1 XP", "Higher = slower leveling.", "0.01"),
            new("Progression:MaxWagerPerBet", "Max XP-eligible wager / bet", "Caps XP per hand. 0 = uncapped.", "100"),
            new("Progression:WinXpBonus", "Win XP bonus (fraction)", "Extra XP on a win, e.g. 0.1 = +10%.", "0.05"),
            new("Progression:MinBetEarly", "Min bet for full XP (early levels)", "Below this, XP is reduced at low levels.", "100"),
            new("Progression:MinBetLate", "Min bet for full XP (later levels)", "Below this, XP is reduced at higher levels.", "100"),
            new("Progression:LvlupBase", "Chips per level-up", "Reward = round100(base × level / 100).", "1000"),
            new("Progression:XpBase", "XP curve base", "Coefficient of the level curve (bigger = slower).", "10"),
            new("Progression:XpExp", "XP curve exponent", "Steepness of the curve (1.6 = super-linear).", "0.1"),
            new("Progression:DailyXpCap", "Daily XP cap per player", "Max XP per UTC day.", "1000"),
        };

        // Editable game timing — live via BlackjackTableManager's runtime overlay (same Redis hash, cached ~15s).
        private static readonly SettingDef[] GameDefs =
        {
            new("Blackjack:TurnSeconds", "Turn timer (seconds)", "How long a player has to act on their turn.", "1"),
            new("Blackjack:InsuranceSeconds", "Insurance window (seconds)", "How long the insurance offer stays open.", "1"),
            new("Table:StalledTimeoutSeconds", "Stalled-player timeout (seconds)", "No heartbeat this long ⇒ stalled (money-safe §5 reap).", "1"),
            new("Table:DisconnectGraceSeconds", "Disconnect grace (seconds)", "No heartbeat this long ⇒ shown as disconnected.", "1"),
        };

        // Read-only (no live consumer yet).
        private static readonly (string Key, string Label)[] GameReadOnlyDefs =
        {
            ("Table:HeartbeatIntervalSeconds", "Heartbeat interval (seconds)"),
        };

        [HttpGet]
        public IActionResult Index() => View(BuildModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCasino()
        {
            try
            {
                var entries = new List<HashEntry>();
                foreach (var def in CasinoDefs)
                {
                    var raw = Request.Form[def.Key].ToString().Trim();
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) continue;   // skip non-numeric
                    entries.Add(new HashEntry(def.Key, raw));
                }
                if (entries.Count > 0)
                    await _redis.GetDatabase().HashSetAsync(SettingsHashKey, entries.ToArray());
                TempData["Saved"] = "Casino settings saved — live on the next round.";
            }
            catch
            {
                TempData["Error"] = "Could not reach Redis to save settings.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveGame()
        {
            try
            {
                var entries = new List<HashEntry>();
                foreach (var def in GameDefs)
                {
                    var raw = Request.Form[def.Key].ToString().Trim();
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (!int.TryParse(raw, out var n) || n <= 0) continue;   // positive integer seconds only
                    entries.Add(new HashEntry(def.Key, n.ToString()));
                }
                if (entries.Count > 0)
                    await _redis.GetDatabase().HashSetAsync(SettingsHashKey, entries.ToArray());
                TempData["Saved"] = "Game settings saved — live within ~15s (engine caches the overlay).";
            }
            catch
            {
                TempData["Error"] = "Could not reach Redis to save settings.";
            }
            return RedirectToAction(nameof(Index));
        }

        private SettingsVm BuildModel()
        {
            // Effective value = Redis override (khela:settings) ?? appsettings default — exactly what the engine reads.
            string Eff(string key)
            {
                try { var v = _redis.GetDatabase().HashGet(SettingsHashKey, key); if (v.HasValue) return v!; }
                catch { /* Redis down — fall back to config */ }
                return _config[key] ?? "";
            }
            string Cfg(string key) => string.IsNullOrEmpty(_config[key]) ? "—" : _config[key]!;

            return new SettingsVm
            {
                Saved = TempData["Saved"] as string,
                Error = TempData["Error"] as string,
                Casino = CasinoDefs.Select(d => new EditableSetting(d.Key, d.Label, d.Help, d.Step, Eff(d.Key))).ToList(),
                Game = GameDefs.Select(d => new EditableSetting(d.Key, d.Label, d.Help, d.Step, Eff(d.Key))).ToList(),
                GameReadOnly = GameReadOnlyDefs.Select(g => new ReadOnlySetting(g.Label, Cfg(g.Key))).ToList(),
            };
        }
    }

    public sealed record SettingDef(string Key, string Label, string Help, string Step);

    public sealed class SettingsVm
    {
        public List<EditableSetting> Casino { get; set; } = new();
        public List<EditableSetting> Game { get; set; } = new();
        public List<ReadOnlySetting> GameReadOnly { get; set; } = new();
        public string? Saved { get; set; }
        public string? Error { get; set; }
    }

    public sealed record EditableSetting(string Key, string Label, string Help, string Step, string Value);
    public sealed record ReadOnlySetting(string Label, string Value);
}
