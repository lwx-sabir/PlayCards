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
            new("Blackjack:DeckCount", "Decks in the shoe", "How many 52-card decks (1–8). 2+ persists across rounds and is replaced when the cut card is reached. Changing this rebuilds the shoe on the next deal.", "1"),
            new("Blackjack:ShoePenetrationPercent", "Shoe penetration (%)", "How much of the shoe is dealt before the cut card. 75 = cut with 25% left. Floored so a round can always finish. Ignored when reshuffling every round.", "1"),
            new("Blackjack:ReshuffleEveryRound", "Reshuffle every round", "1 = fresh shuffle each round (no cut card), whatever the deck count. 0 = persistent shoe. Note the original behaviour was 6 decks WITH this on — deck count 1 is a single 52-card deck, a different game.", "1", Min: 0),
            new("Blackjack:TurnSeconds", "Turn timer (seconds)", "How long a player has to act on their turn.", "1"),
            new("Blackjack:BettingSeconds", "Betting window (seconds)", "Between-rounds time to place a bet before auto-deal. 0 = no betting window.", "1"),
            new("Blackjack:MaxIdleBettingWindows", "Idle windows before kick", "Sit out this many betting windows with no bet ⇒ removed from the table (the warning shows one window before). 0 = never kick.", "1"),
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
                    // Whole numbers at or above the knob's own minimum. Durations keep the old positive-only rule;
                    // an on/off flag declares Min 0 so it can be switched back off and not just on.
                    if (!int.TryParse(raw, out var n) || n < def.Min) continue;
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
                Game = GameDefs.Select(d => new EditableSetting(d.Key, d.Label, d.Help, d.Step, Eff(d.Key), d.Min)).ToList(),
                GameReadOnly = GameReadOnlyDefs.Select(g => new ReadOnlySetting(g.Label, Cfg(g.Key))).ToList(),
            };
        }
    }

    /// <summary>An editable knob. <paramref name="Min"/> is the lowest value that can be SAVED — it defaults to 1
    /// because most knobs are durations where 0 is meaningless, but an on/off flag needs 0 or it becomes a switch
    /// that can only be turned on.</summary>
    public sealed record SettingDef(string Key, string Label, string Help, string Step, int Min = 1);

    public sealed class SettingsVm
    {
        public List<EditableSetting> Casino { get; set; } = new();
        public List<EditableSetting> Game { get; set; } = new();
        public List<ReadOnlySetting> GameReadOnly { get; set; } = new();
        public string? Saved { get; set; }
        public string? Error { get; set; }
    }

    public sealed record EditableSetting(string Key, string Label, string Help, string Step, string Value, int Min = 1);
    public sealed record ReadOnlySetting(string Label, string Value);
}
