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

        // The piggy bank (docs/PIGGY_BANK_SPEC.md). Percentages, so Min 0 throughout — every one of these is a knob
        // that can legitimately be turned off, and the default Min of 1 would make them switches that only turn on.
        private static readonly SettingDef[] PiggyDefs =
        {
            new("Piggy:WagerRatePercent", "Wager banked (%)", "Percent of CLEAN (non-gifted) wager banked per settled round. This is a PACING knob, not an economic one — nothing is minted until a bank is bought — so it sets how often a full piggy appears, not what it costs you.", "1", Min: 0),
            new("Piggy:LossRatePercent", "Loss banked (%)", "Percent of a losing round's net loss banked. Only used when the mode includes Loss.", "1", Min: 0),
            new("Piggy:MaxAccrualPerDayPercent", "Daily cap (% of capacity)", "The floor on how fast a bank can fill: 25 means four days minimum whatever the stake. Without it a high roller fills it in one sitting and the offer stops being an event. 0 = uncapped.", "1", Min: 0),
            new("Piggy:MinBreakPercent", "Buyable at (% full)", "How full the bank must be before it can be bought. 100 = completely full.", "1", Min: 0),
            new("Piggy:MinFlyAmount", "Celebrate from (chips)", "How much must have gone into the bank since the last celebration before chips FLY into it on the player's return. Below it the bar just fills quietly. Purely presentational — a celebration that fires for every trivial amount stops being one. 0 = always fly.", "1000", Min: 0),
            new("Piggy:CycleHours", "Window (hours)", "Hours the player has to buy a full bank AFTER they have been shown it. The clock does NOT start when the bank fills — a window that ran while they were offline would take away an offer they were never given. 72 = three days, 168 = a week. 0 = never expires.", "1", Min: 0),
        };

        // VIP (status points, tiers, levels) + Loyalty (LP comp) — every SCALAR the two services overlay live from the
        // settings hash (VipConfig.Overlay / LoyaltyConfig.Overlay). The per-tier ARRAYS (thresholds, bonus %, factors)
        // and both Enabled flags are appsettings/code only — deliberately not here. Step "1" ⇒ saved as a whole
        // number (the engine parses those with Int/Lng); anything else is saved as an invariant decimal.
        private static readonly SettingDef[] VipLoyaltyDefs =
        {
            new("Vip:SpChipsPerPoint", "Chips wagered per 1 SP", "Status Points per clean chip wagered. Default 50. Lower = faster VIP climb.", "0.5", Min: 1),
            new("Vip:SpPerUsd", "SP per $1 spent", "Status Points per real dollar of IAP. Default 100. Dormant until the store takes money.", "1", Min: 0),
            new("Vip:SpFromWagerDailyCap", "Daily SP cap from wagering", "Max SP a player can earn from play per UTC day — the floor on 'slowly'. Default 100,000. 0 = uncapped.", "1", Min: 0),
            new("Vip:VipEntryLevel", "VIP entry level", "Player level at which the VIP ladder starts counting. Default 20.", "1", Min: 1),
            new("Vip:TierWindowMonths", "Tier window (months)", "Rolling window the tier review looks back over. Default 12.", "1", Min: 1),
            new("Vip:DemoteHysteresis", "Demotion hysteresis", "A tier is only LOST once SP falls below this fraction of its threshold — stops flapping at the boundary. Default 0.85 (0–1).", "0.01", Min: 0),
            new("Vip:VipMaintainDays", "VIP level maintained for (days)", "A settled round / LP spend / IAP top-up within this window holds the VIP level. Default 30.", "1", Min: 1),
            new("Vip:BadgeWindowDays", "Badge lit for (days)", "How long the VIP badge stays lit after qualifying activity. Default 30.", "1", Min: 1),
            new("Vip:VipBoosterTimeDays", "Booster: Time adds (days)", "Days the cheap 'VIP Booster: Time' IAP adds to the current level. Default 30.", "1", Min: 1),
            new("Loyalty:LpChipsPerPoint", "Chips wagered per 1 LP", "Clean wager chips per Loyalty Point (~1% comp at 100). Default 100. ⚠️ the LP store pays chips — lowering this makes play MORE chip-positive; watch the peg.", "1", Min: 1),
            new("Loyalty:LpPerUsd", "LP per $1 spent", "IAP drip into LP. Default 0 = off until IAP.", "0.1", Min: 0),
        };

        // Read-only (no live consumer yet).
        private static readonly (string Key, string Label)[] GameReadOnlyDefs =
        {
            ("Table:HeartbeatIntervalSeconds", "Heartbeat interval (seconds)"),
        };
        // The in-app store (docs/IAP_SPEC.md §7.1): switches the game reads LIVE from the overlay (StoreSwitches). Credentials stay in
        // the game server's appsettings. Turning a platform ON here additionally needs its verifier configured — the Store page
        // shows what the game process actually registered.
        private static readonly (string Key, string Label, string Help)[] StoreFlagDefs =
        {
            ("Store:Enabled", "Store open", "Master switch. Off = every redeem answers StoreDisabled (the client keeps orders pending), catalog shows nothing purchasable."),
            ("Store:GooglePlay:Enabled", "Google Play", "Android purchases. Needs the Play service-account JSON on the game server to actually verify."),
            ("Store:AppStore:Enabled", "App Store", "iOS/macOS purchases (StoreKit 2). Needs the Apple root certificate (+ API key for refresh) on the game server."),
            ("Store:Fake:Enabled", "Fake store (Editor)", "Unity's fake store — honoured ONLY when the game runs in the Development environment; harmless elsewhere."),
            ("Store:Web:Enabled", "Web checkout", "The later web store feed. No verifier yet — leave off."),
            ("Store:GooglePlay:AcceptTestPurchases", "Accept licence-tester purchases", "Play licence testers pay nothing; their purchases grant normally, flagged IsTest, excluded from revenue and spend hooks."),
            ("Store:TestPurchasesFeedSpend", "Test purchases feed VIP/LP spend", "Normally off: a free tester purchase must not buy VIP status."),
            ("Store:AllowRandomPayloads", "Allow random (chest) payloads for real money", "Off until the loot-box / odds-disclosure question is settled. The catalog validator refuses chest lines while this is off."),
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SavePiggy()
        {
            try
            {
                var entries = new List<HashEntry>();
                foreach (var def in PiggyDefs)
                {
                    var raw = Request.Form[def.Key].ToString().Trim();
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (!int.TryParse(raw, out var n) || n < def.Min) continue;
                    entries.Add(new HashEntry(def.Key, n.ToString()));
                }

                // The two flags and the mode are written unconditionally: an unchecked checkbox posts nothing, so
                // "absent" has to mean false here or a switch could never be turned back off.
                entries.Add(new HashEntry("Piggy:Enabled", Request.Form["Piggy:Enabled"].Count > 0 ? "true" : "false"));
                entries.Add(new HashEntry("Piggy:BypassPurchase", Request.Form["Piggy:BypassPurchase"].Count > 0 ? "true" : "false"));

                var mode = Request.Form["Piggy:Mode"].ToString().Trim();
                if (mode is "Wager" or "Loss" or "Both") entries.Add(new HashEntry("Piggy:Mode", mode));

                await _redis.GetDatabase().HashSetAsync(SettingsHashKey, entries.ToArray());

                TempData["Saved"] = Request.Form["Piggy:BypassPurchase"].Count > 0
                    ? "Piggy settings saved — ⚠️ BREAK WITHOUT PURCHASE IS ON. Every full bank is free chips; turn it off before this reaches players."
                    : "Piggy settings saved — live on the next round.";
            }
            catch
            {
                TempData["Error"] = "Could not reach Redis to save settings.";
            }
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Save the piggy tier ladder — add, remove or retune the rungs.
        ///
        /// Stored as JSON in the settings hash under <c>Piggy:Tiers</c>, so it travels with the piggy group in an
        /// export like every other <c>Piggy:</c> key. Rows with no capacity are dropped rather than saved: a bank of
        /// zero can never fill and never pays, and to a player that reads as broken rather than as mistuned.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SavePiggyTiers()
        {
            try
            {
                var levels = Request.Form["tierMinLevel"];
                var amounts = Request.Form["tierMaxAmount"];
                var skus = Request.Form["tierSku"];

                var tiers = new List<Khela.Game.Services.Piggy.PiggyTier>();
                for (int i = 0; i < levels.Count && i < amounts.Count; i++)
                {
                    if (!decimal.TryParse(amounts[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var max) || max <= 0m)
                        continue;   // a blank or zero row is how a tier is DELETED

                    int.TryParse(levels[i], out var level);
                    tiers.Add(new Khela.Game.Services.Piggy.PiggyTier
                    {
                        MinLevel = level < 1 ? 1 : level,
                        MaxAmount = max,
                        PriceSku = i < skus.Count ? (skus[i] ?? "").Trim() : "",
                    });
                }

                if (tiers.Count == 0)
                {
                    TempData["Error"] = "Refused: that would leave no tiers at all, and a player with no bank capacity " +
                                        "sees a feature that looks broken. Keep at least one rung, or clear " +
                                        "Piggy:Tiers from Redis to fall back to the built-in ladder.";
                    return RedirectToAction(nameof(Index));
                }

                tiers.Sort((a, b) => a.MinLevel.CompareTo(b.MinLevel));

                await _redis.GetDatabase().HashSetAsync(SettingsHashKey, "Piggy:Tiers",
                    Khela.Game.Services.Piggy.PiggyConfig.SerializeTiers(tiers));

                TempData["Saved"] = $"Saved {tiers.Count} tier(s). Note: a player's capacity is SNAPSHOTTED on their " +
                                    "bank — existing banks keep the size they were opened with until they reset or are " +
                                    "bought. Use Testing → Empty it to re-read a tier on a test account.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Could not save tiers: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Save the VIP TIER ladder (8 rows, None…BlackDiamond) as one JSON document under <c>Vip:Tiers</c> — a tier's
        /// SP bar, spend floor, comp bonus and grind factor are one row of one decision, so they travel together.
        /// The game refuses the whole document if any row is unusable, so a bad save falls back to the built-in ladder
        /// rather than ranking players by a half-applied one.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveVipTiers()
        {
            try
            {
                var sp = Request.Form["tierSp"]; var floor = Request.Form["tierFloor"];
                var bonus = Request.Form["tierBonus"]; var factor = Request.Form["tierFactor"];
                var rows = new List<Khela.Game.Services.Vip.VipConfig.TierRow>();
                for (int i = 0; i < 8 && i < sp.Count; i++)
                    rows.Add(new Khela.Game.Services.Vip.VipConfig.TierRow
                    {
                        SpThreshold = Lng(sp, i), SpendFloorUsd = Dec(floor, i),
                        BonusPct = Pct(bonus, i), Factor = Dec(factor, i),
                    });

                if (rows.Count != 8) { TempData["Error"] = "The tier table needs all 8 rows (None … Black Diamond)."; return RedirectToAction(nameof(Index)); }
                for (int i = 1; i < 8; i++)
                    if (rows[i].SpThreshold < rows[i - 1].SpThreshold)
                    { TempData["Error"] = "Refused: the SP bars must not go DOWN as the tier rises — the game would refuse this ladder and fall back to the built-in one."; return RedirectToAction(nameof(Index)); }
                if (rows[0].Factor != 0m)
                    { TempData["Error"] = "Refused: tier None must have grind factor 0 — anything else lets players below the VIP entry level grind VIP Levels."; return RedirectToAction(nameof(Index)); }

                await _redis.GetDatabase().HashSetAsync(SettingsHashKey, "Vip:Tiers", Khela.Game.Services.Vip.VipConfig.SerializeTiers(rows));
                TempData["Saved"] = "Saved the VIP tier ladder — live on the next accrual (~15 s). Existing players are re-banded by the tier review, not instantly.";
            }
            catch (Exception ex) { TempData["Error"] = $"Could not save the tier ladder: {ex.Message}"; }
            return RedirectToAction(nameof(Index));
        }

        /// <summary>Save the VIP LEVEL ladder (one row per level 1…N) under <c>Vip:Levels</c>. A row with no threshold is
        /// dropped, which is how a level is removed; an empty table is refused (a ladder with no rungs).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveVipLevels()
        {
            try
            {
                var need = Request.Form["lvlThreshold"]; var bonus = Request.Form["lvlBonus"]; var lp = Request.Form["lvlMaintain"];
                var rows = new List<Khela.Game.Services.Vip.VipConfig.LevelRow>();
                for (int i = 0; i < need.Count; i++)
                {
                    var threshold = Lng(need, i);
                    if (threshold <= 0L) continue;   // a blank/zero row is how a level is DELETED
                    rows.Add(new Khela.Game.Services.Vip.VipConfig.LevelRow
                    {
                        Threshold = threshold, BonusPct = Pct(bonus, i), MaintainLp = Lng(lp, i),
                    });
                }
                if (rows.Count == 0)
                { TempData["Error"] = "Refused: that would leave no VIP levels. Keep at least one, or delete Vip:Levels from Redis to fall back to the built-in ladder."; return RedirectToAction(nameof(Index)); }

                await _redis.GetDatabase().HashSetAsync(SettingsHashKey, "Vip:Levels", Khela.Game.Services.Vip.VipConfig.SerializeLevels(rows));
                TempData["Saved"] = $"Saved {rows.Count} VIP level(s) — live on the next accrual (~15 s). Players already ABOVE the new top level keep it until it lapses.";
            }
            catch (Exception ex) { TempData["Error"] = $"Could not save the VIP levels: {ex.Message}"; }
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Save the LP store under <c>Loyalty:Catalog</c>. The redemption rate is what decides what an LP is WORTH:
        /// earning 1 LP per <c>Loyalty:LpChipsPerPoint</c> chips wagered and redeeming it for R chips is a comp of
        /// R / LpChipsPerPoint — at 100 earned and 100 redeemed that is 100% of wager back, a loop rather than a comp.
        /// The page shows that percentage per row so the number is never set by accident.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveLoyaltyCatalog()
        {
            try
            {
                var id = Request.Form["lpId"]; var name = Request.Form["lpName"];
                var cost = Request.Form["lpCost"]; var amount = Request.Form["lpAmount"]; var tier = Request.Form["lpMinTier"];
                var items = new List<Khela.Game.Services.Loyalty.LoyaltyStoreItem>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < id.Count; i++)
                {
                    var key = (id[i] ?? "").Trim();
                    var lp = Lng(cost, i); var chips = Dec(amount, i);
                    if (key.Length == 0 || lp <= 0L || chips <= 0m) continue;   // blank/zero row = DELETE
                    if (!seen.Add(key)) { TempData["Error"] = $"Two items share the id '{key}' — the game refuses the whole list."; return RedirectToAction(nameof(Index)); }
                    items.Add(new Khela.Game.Services.Loyalty.LoyaltyStoreItem
                    {
                        Id = key, Kind = "chips", Name = i < name.Count ? (name[i] ?? "").Trim() : "",
                        CostLp = lp, ChipAmount = chips, MinVipTier = (int)Lng(tier, i),
                    });
                }
                if (items.Count == 0)
                { TempData["Error"] = "Refused: that would leave the LP store empty, which reads as broken. Delete Loyalty:Catalog from Redis to fall back to the built-in list."; return RedirectToAction(nameof(Index)); }

                await _redis.GetDatabase().HashSetAsync(SettingsHashKey, "Loyalty:Catalog", Khela.Game.Services.Loyalty.LoyaltyConfig.SerializeCatalog(items));
                TempData["Saved"] = $"Saved {items.Count} LP item(s) — live within ~15 s.";
            }
            catch (Exception ex) { TempData["Error"] = $"Could not save the LP store: {ex.Message}"; }
            return RedirectToAction(nameof(Index));
        }

        private static long Lng(Microsoft.Extensions.Primitives.StringValues v, int i)
            => i < v.Count && long.TryParse(v[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : 0L;
        private static decimal Dec(Microsoft.Extensions.Primitives.StringValues v, int i)
            => i < v.Count && decimal.TryParse(v[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : 0m;
        /// <summary>A bonus typed as a PERCENT in the form (170) is stored as the fraction the engine multiplies by (1.70).</summary>
        private static decimal Pct(Microsoft.Extensions.Primitives.StringValues v, int i) => Dec(v, i) / 100m;

        /// <summary>
        /// Download the selected tuning as a seed file for another environment.
        ///
        /// Only what is TICKED goes in, and the receiving server merges rather than replaces — so a partial export can
        /// never wipe the groups it didn't carry. The game/table knobs are their own group and default OFF: table
        /// timing is usually environment-specific, and copying it across is how a dev server's shortened timers end up
        /// somewhere they shouldn't.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveVipLoyalty()
        {
            try
            {
                var entries = new List<HashEntry>();
                foreach (var def in VipLoyaltyDefs)
                {
                    var raw = Request.Form[def.Key].ToString().Trim();
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) || v < def.Min) continue;
                    // Whole-number knobs are parsed by the engine with Int/Lng — a "20.0" would silently fall back to
                    // the default there, so store exactly the shape each side expects.
                    var stored = def.Step == "1"
                        ? Math.Floor(v).ToString(CultureInfo.InvariantCulture)
                        : v.ToString(CultureInfo.InvariantCulture);
                    entries.Add(new HashEntry(def.Key, stored));
                }
                if (entries.Count > 0)
                    await _redis.GetDatabase().HashSetAsync(SettingsHashKey, entries.ToArray());
                TempData["Saved"] = "VIP & Loyalty settings saved — live on the next accrual / settle (services overlay per call).";
            }
            catch
            {
                TempData["Error"] = "Could not reach Redis to save settings.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveStore()
        {
            try
            {
                var entries = new List<HashEntry>();
                // Checkboxes post nothing when unchecked — write every flag explicitly so a switch can be turned OFF as well as on.
                foreach (var d in StoreFlagDefs)
                    entries.Add(new HashEntry(d.Key, Request.Form[d.Key].Count > 0 ? "true" : "false"));
                var policy = Request.Form["Store:Refunds:Policy"].ToString().Trim();
                if (policy is "Rollback" or "Flag") entries.Add(new HashEntry("Store:Refunds:Policy", policy));
                var xp = Request.Form["Store:XpPerUsd"].ToString().Trim();
                if (decimal.TryParse(xp, NumberStyles.Any, CultureInfo.InvariantCulture, out var xpv) && xpv >= 0m)
                    entries.Add(new HashEntry("Store:XpPerUsd", xpv.ToString(CultureInfo.InvariantCulture)));
                await _redis.GetDatabase().HashSetAsync(SettingsHashKey, entries.ToArray());
                TempData["Saved"] = Request.Form["Store:Enabled"].Count > 0
                    ? "Store settings saved — live on the next request (the game reads these switches per call)."
                    : "Store settings saved — ⚠️ THE STORE IS CLOSED: every purchase attempt answers StoreDisabled until it is reopened.";
            }
            catch
            {
                TempData["Error"] = "Could not reach Redis to save settings.";
            }
            return RedirectToAction(nameof(Index));
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Export()
        {
            var groups = Request.Form["groups"].ToString();
            bool Want(string g) => groups.Contains(g, StringComparison.OrdinalIgnoreCase);

            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            var documents = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                var db = _redis.GetDatabase();

                // Scalars: read the live hash and keep only the prefixes asked for. Read from REDIS rather than from
                // config, because what a receiving server needs is what this one is actually running — an appsettings
                // default it already has is not worth carrying.
                var all = await db.HashGetAllAsync(SettingsHashKey);
                foreach (var e in all)
                {
                    var key = (string)e.Name;
                    if (string.IsNullOrEmpty(key)) continue;

                    bool take =
                        (Want("piggy")       && key.StartsWith("Piggy:", StringComparison.OrdinalIgnoreCase)) ||
                        (Want("progression") && (key.StartsWith("Progression:", StringComparison.OrdinalIgnoreCase)
                                              || key.StartsWith("Loyalty:", StringComparison.OrdinalIgnoreCase)
                                              || key.StartsWith("Vip:", StringComparison.OrdinalIgnoreCase))) ||
                        (Want("rewards")     && key.StartsWith("Rewards:", StringComparison.OrdinalIgnoreCase)) ||
                        (Want("game")        && (key.StartsWith("Blackjack:", StringComparison.OrdinalIgnoreCase)
                                              || key.StartsWith("Table:", StringComparison.OrdinalIgnoreCase))) ||
                        (Want("store")       && key.StartsWith("Store:", StringComparison.OrdinalIgnoreCase));

                    if (take) settings[key] = (string)e.Value;
                }

                // Documents: the hand-authored ladders and catalogs, exactly as their catalogs store them.
                async Task TakeDoc(string group, string redisKey)
                {
                    if (!Want(group)) return;
                    var value = await db.StringGetAsync(redisKey);
                    if (value.HasValue) documents[redisKey] = (string)value;
                }

                await TakeDoc("pass", "khela:pass");
                await TakeDoc("daily", "khela:daily");
                await TakeDoc("missions", "khela:missions");
                await TakeDoc("chests", "khela:chests");
                await TakeDoc("store", "khela:store");
            }
            catch
            {
                TempData["Error"] = "Could not reach Redis to export settings.";
                return RedirectToAction(nameof(Index));
            }

            if (settings.Count == 0 && documents.Count == 0)
            {
                TempData["Error"] = "Nothing to export — those groups have no live overrides yet. " +
                                    "A knob only enters Redis once it has been SAVED here; until then the server is " +
                                    "running its appsettings default, which the target already has.";
                return RedirectToAction(nameof(Index));
            }

            var file = new
            {
                khelaConfig = 1,
                exportedAtUtc = DateTime.UtcNow,
                note = Request.Form["note"].ToString(),
                settings,
                documents,
            };

            var json = System.Text.Json.JsonSerializer.Serialize(file, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            // SAVE TO DISK, not a download. The admin runs on the same machine as the person using it, so writing the
            // file straight out sidesteps the browser entirely — and browser downloads from a localhost dev-certificate
            // site are exactly the kind of thing that fails for reasons that have nothing to do with this feature.
            if (string.Equals(Request.Form["mode"].ToString(), "save", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dir = _config["Config:ExportDir"];
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = Path.Combine(Directory.GetCurrentDirectory(), "exports");

                    Directory.CreateDirectory(dir);
                    var full = Path.Combine(dir, "khela-settings.json");
                    await System.IO.File.WriteAllTextAsync(full, json);

                    TempData["Saved"] = $"Wrote {settings.Count} setting(s) and {documents.Count} document(s) to {full}";
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"Could not write the export file: {ex.Message}";
                }
                return RedirectToAction(nameof(Index));
            }

            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "khela-settings.json");
        }

        // Which store flags are ON when neither Redis nor appsettings says anything — mirrors StoreOptions' defaults.
        private static bool StoreDefaultOn(string key)
            => key is "Store:Enabled" or "Store:GooglePlay:Enabled" or "Store:Fake:Enabled" or "Store:GooglePlay:AcceptTestPurchases";
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

            // appsettings writes true/false, a checkbox posts "on". Accept both so a value set by hand — in Redis or
            // in the config file — round-trips through this page unchanged instead of silently reading as off.
            static bool Flag(string v) => v is "true" or "True" or "1" or "on";

            var vm = new SettingsVm
            {
                Saved = TempData["Saved"] as string,
                Error = TempData["Error"] as string,
                Casino = CasinoDefs.Select(d => new EditableSetting(d.Key, d.Label, d.Help, d.Step, Eff(d.Key))).ToList(),
                Game = GameDefs.Select(d => new EditableSetting(d.Key, d.Label, d.Help, d.Step, Eff(d.Key), d.Min)).ToList(),
                GameReadOnly = GameReadOnlyDefs.Select(g => new ReadOnlySetting(g.Label, Cfg(g.Key))).ToList(),
                Piggy = PiggyDefs.Select(d => new EditableSetting(d.Key, d.Label, d.Help, d.Step, Eff(d.Key), d.Min)).ToList(),
                PiggyEnabled = Flag(Eff("Piggy:Enabled")),
                PiggyBypassPurchase = Flag(Eff("Piggy:BypassPurchase")),
                PiggyMode = string.IsNullOrWhiteSpace(Eff("Piggy:Mode")) ? "Wager" : Eff("Piggy:Mode"),
                StoreFlags = StoreFlagDefs.Select(d => new StoreFlag(d.Key, d.Label, d.Help, Flag(Eff(d.Key)) || (string.IsNullOrEmpty(Eff(d.Key)) && StoreDefaultOn(d.Key)))).ToList(),
                StoreRefundPolicy = string.IsNullOrWhiteSpace(Eff("Store:Refunds:Policy")) ? "Rollback" : Eff("Store:Refunds:Policy"),
                StoreXpPerUsd = string.IsNullOrWhiteSpace(Eff("Store:XpPerUsd")) ? "0" : Eff("Store:XpPerUsd"),
                VipLoyalty = VipLoyaltyDefs.Select(d => new EditableSetting(d.Key, d.Label, d.Help, d.Step, Eff(d.Key), d.Min)).ToList(),
                // The EFFECTIVE ladder: what the admin authored if it parses, else the built-in one — so the editor
                // always shows what the server is actually running rather than an empty table.
                PiggyTiers = Khela.Game.Services.Piggy.PiggyConfig.ParseTiers(
                    Eff("Piggy:Tiers"), new Khela.Game.Services.Piggy.PiggyConfig().Tiers).ToList(),
            };
            // The EFFECTIVE VIP + LP ladders, built by the game's own overlay so the editor always shows what the server
            // is actually running (a document the game refuses shows here as the built-in ladder, which is the truth).
            var vipBase = new Khela.Game.Services.Vip.VipConfig();
            var vipTiers = Khela.Game.Services.Vip.VipConfig.ParseTiers(Eff("Vip:Tiers"), vipBase);
            var vipLevels = Khela.Game.Services.Vip.VipConfig.ParseLevels(Eff("Vip:Levels"), vipBase);
            var vipEffective = new Khela.Game.Services.Vip.VipConfig
            {
                SpThresholds = vipTiers.SpThresholds, SpendFloorsUsd = vipTiers.SpendFloorsUsd,
                TierBonusPct = vipTiers.TierBonusPct, TierFactors = vipTiers.TierFactors,
                VipLevelBonusPct = vipLevels.VipLevelBonusPct, VipLevelThresholds = vipLevels.VipLevelThresholds,
                VipMaintainLpCost = vipLevels.VipMaintainLpCost,
            };
            vm.VipTiers = Khela.Game.Services.Vip.VipConfig.TierRows(vipEffective);
            vm.VipLevels = Khela.Game.Services.Vip.VipConfig.LevelRows(vipEffective);
            vm.LoyaltyCatalog = Khela.Game.Services.Loyalty.LoyaltyConfig.ParseCatalog(
                Eff("Loyalty:Catalog"), new Khela.Game.Services.Loyalty.LoyaltyConfig().Catalog).ToList();
            vm.LpChipsPerPoint = decimal.TryParse(Eff("Loyalty:LpChipsPerPoint"), NumberStyles.Any, CultureInfo.InvariantCulture, out var lpRate) && lpRate > 0m
                ? lpRate : new Khela.Game.Services.Loyalty.LoyaltyConfig().LpChipsPerPoint;
            return vm;
        }
    }

    /// <summary>An editable knob. <paramref name="Min"/> is the lowest value that can be SAVED — it defaults to 1
    /// because most knobs are durations where 0 is meaningless, but an on/off flag needs 0 or it becomes a switch
    /// that can only be turned on.</summary>
    public sealed record SettingDef(string Key, string Label, string Help, string Step, int Min = 1);
    public sealed record StoreFlag(string Key, string Label, string Help, bool On);

    public sealed class SettingsVm
    {
        public List<EditableSetting> Casino { get; set; } = new();
        public List<EditableSetting> Game { get; set; } = new();
        public List<ReadOnlySetting> GameReadOnly { get; set; } = new();
        public List<EditableSetting> Piggy { get; set; } = new();
        public bool PiggyEnabled { get; set; }
        public bool PiggyBypassPurchase { get; set; }
        public string PiggyMode { get; set; } = "Wager";
        public List<Khela.Game.Services.Piggy.PiggyTier> PiggyTiers { get; set; } = new();
        public List<StoreFlag> StoreFlags { get; set; } = new();
        public string StoreRefundPolicy { get; set; } = "Rollback";
        public string StoreXpPerUsd { get; set; } = "0";
        public List<EditableSetting> VipLoyalty { get; set; } = new();
        /// <summary>The EFFECTIVE VIP tier ladder (8 rows, None…BlackDiamond) — what the server is actually running.</summary>
        public List<Khela.Game.Services.Vip.VipConfig.TierRow> VipTiers { get; set; } = new();
        /// <summary>The EFFECTIVE VIP level ladder — index 0 is level 1.</summary>
        public List<Khela.Game.Services.Vip.VipConfig.LevelRow> VipLevels { get; set; } = new();
        public List<Khela.Game.Services.Loyalty.LoyaltyStoreItem> LoyaltyCatalog { get; set; } = new();
        /// <summary>Chips of clean wager per 1 LP — the denominator that turns an LP price into a comp percentage.</summary>
        public decimal LpChipsPerPoint { get; set; } = 100m;
        public string? Saved { get; set; }
        public string? Error { get; set; }
    }

    public sealed record EditableSetting(string Key, string Label, string Help, string Step, string Value, int Min = 1);
    public sealed record ReadOnlySetting(string Label, string Value);
}
