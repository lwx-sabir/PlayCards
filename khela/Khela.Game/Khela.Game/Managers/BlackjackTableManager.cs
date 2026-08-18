using CardGames.Platforms;
using Khela.Game.Services.Redis;
using System.Text.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CardGames.Blackjack;
using CardGames.Provable;
using Khela.Common.Blackjack;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Managers.SRHubs;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Khela.Game.Services.Stats;
using Khela.Game.Services.Progression;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Security.Cryptography;
using System.Text;

namespace Khela.Game.Managers
{
    public class BlackjackTableManager
    {
        private readonly IRedisService redisService;
        private readonly IServiceScopeFactory scopeFactory;
        private readonly IHubContext<BlackjackHub> hubContext;
        private readonly ILogger<BlackjackTableManager> logger;
        // Config defaults (appsettings). The LIVE values are read via the properties below, which overlay admin
        // runtime overrides from the Redis "khela:settings" hash (cached ~15s) — the same store the dashboard and
        // ProgressionService use. A non-positive / unparseable override is ignored, so it can't break the clock.
        private readonly int cfgTurnSeconds;
        private readonly int cfgInsuranceSeconds;
        private readonly int cfgMaxPresentationSeconds;   // CAP on the scaled presentation estimate below
        private readonly int cfgPresentPerCardSeconds;    // per-CARD deal-in estimate; the anti-stall ceiling scales by the table's card count
        private readonly int cfgPlayableSeats;            // highest occupiable seat number (client only supports 1-3)
        private readonly int cfgBettingSeconds;           // between-rounds betting window; 0 disables auto-deal entirely
        private readonly int cfgMaxIdleBettingWindows;    // evict a seat after this many betting windows with no bet; 0 disables
        private readonly int cfgDeckCount;                // decks per shoe; 1 = reshuffle every round (no cut card)
        private readonly int cfgShoePenetrationPercent;   // % of the shoe dealt before the cut card
        private readonly bool cfgReshuffleEveryRound;     // force a fresh shuffle each round even with a multi-deck shoe

        // ---- Lobby tiers ------------------------------------------------------------------------------------
        // A tier is a stake bracket, NOT a single table: the lobby opens as many tables per tier as the players at
        // that stake need, and closes the surplus again when they leave (see LobbyPlan).
        private readonly List<BetTier> cfgTiers;
        private readonly int cfgMinTablesPerTier;
        private readonly int cfgMinJoinablePerTier;
        private readonly int cfgMinEmptyPerTier;
        private readonly int cfgEmptyTableGraceSeconds;
        private readonly int cfgLobbyPageSize;
        private readonly int cfgLobbyFullTablesShown;
        private readonly int cfgLobbyEmptyTablesShown;

        /// <summary>Largest shoe the audit can reconstruct: GET verify/{handId} identifies a hand's deck count by
        /// rebuilding candidate sizes until the recorded hash matches, so a shoe bigger than this is unverifiable.</summary>
        public const int MaxSupportedDecks = 8;
        private readonly int cfgStalledTimeout;     // no heartbeat for this long ⇒ stalled ⇒ §5 removal
        private readonly int cfgDisconnectGrace;    // no heartbeat for this long ⇒ show "disconnected…"
        private int turnDurationSeconds      => RuntimeInt("Blackjack:TurnSeconds", cfgTurnSeconds);
        private int insuranceDurationSeconds => RuntimeInt("Blackjack:InsuranceSeconds", cfgInsuranceSeconds);
        private int maxPresentationSeconds   => RuntimeInt("Blackjack:MaxPresentationSeconds", cfgMaxPresentationSeconds);
        // Highest seat NUMBER a player may occupy. Tables still carry MaxPlayers seat objects (5), but the Unity client
        // only has authored anchors / rails / HUD layouts / camera poses / dealer clips for seats 1-3 — a player landing
        // in seat 4 or 5 would be invisible and unplayable. Capping here keeps those seats permanently empty.
        private int playableSeats            => RuntimeInt("Blackjack:PlayableSeats", cfgPlayableSeats);
        private int presentPerCardSeconds    => RuntimeInt("Blackjack:PresentationPerCardSeconds", cfgPresentPerCardSeconds);
        // Length of the between-rounds betting window. 0 = no window: the round only starts when a human presses Deal
        // (the pre-timer behaviour). RuntimeInt ignores non-positive OVERRIDES, so the window can be retuned live from
        // the dashboard but only switched off in appsettings — an admin typo can't accidentally freeze every table.
        private int bettingDurationSeconds   => RuntimeInt("Blackjack:BettingSeconds", cfgBettingSeconds);
        // Evict a seat that has sat through this many betting windows without betting. 0 = never evict for idleness
        // (only the heartbeat reaper removes seats). This is the ONLY thing that frees a seat held by a connected but
        // non-betting client — the heartbeat reaper can't, because a live client keeps pinging regardless of betting.
        private int maxIdleBettingWindows    => RuntimeInt("Blackjack:MaxIdleBettingWindows", cfgMaxIdleBettingWindows);
        // Shoe size in 52-card decks. 1 = the classic behaviour: a fresh shuffle every round, no cut card. >1 = a real
        // casino shoe that PERSISTS across rounds and is only replaced when the cut card is reached.
        private int deckCount                => RuntimeInt("Blackjack:DeckCount", cfgDeckCount);
        // How deep the shoe is dealt before the cut card, as a PERCENT of the shoe (75 = deal 75%, cut with 25% left).
        // Stored as an int so it can be retuned live from the dashboard like every other knob.
        private int shoePenetrationPercent   => RuntimeInt("Blackjack:ShoePenetrationPercent", cfgShoePenetrationPercent);
        // Reshuffle every round even with a multi-deck shoe. This is what reproduces the ORIGINAL pre-shoe behaviour
        // (six decks, freshly shuffled each round) — DeckCount 1 does not, since that is a single 52-card deck.
        private bool reshuffleEveryRound     => RuntimeFlag("Blackjack:ReshuffleEveryRound", cfgReshuffleEveryRound);

        private int minTablesPerTier         => RuntimeInt("Blackjack:MinTablesPerTier", cfgMinTablesPerTier);
        private int minJoinablePerTier       => RuntimeInt("Blackjack:MinJoinableTablesPerTier", cfgMinJoinablePerTier);
        private int minEmptyPerTier          => RuntimeInt("Blackjack:MinEmptyTablesPerTier", cfgMinEmptyPerTier);
        private int emptyTableGraceSeconds   => RuntimeInt("Blackjack:EmptyTableGraceSeconds", cfgEmptyTableGraceSeconds);

        // How much of a tier the browser actually receives. A popular tier is dozens of tables; a carousel is not a
        // list, so the lobby sends a curated page rather than everything (see LobbyPlan.Page).
        private int lobbyPageSize            => RuntimeInt("Blackjack:LobbyPageSize", cfgLobbyPageSize);
        private int lobbyFullTablesShown     => RuntimeInt("Blackjack:LobbyFullTablesShown", cfgLobbyFullTablesShown);
        private int lobbyEmptyTablesShown    => RuntimeInt("Blackjack:LobbyEmptyTablesShown", cfgLobbyEmptyTablesShown);

        /// <summary>
        /// The stake brackets the lobby offers. Admin-overridable live as a JSON array under
        /// <c>Blackjack:Tiers</c> in the settings hash, so a tier can be added or retuned without a deploy;
        /// anything unparseable falls back to appsettings rather than leaving the lobby with no tiers at all.
        /// </summary>
        public IReadOnlyList<BetTier> Tiers
        {
            get
            {
                var raw = RuntimeString("Blackjack:Tiers");
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<List<BetTier>>(raw,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        var valid = LobbyPlan.Sanitize(parsed);
                        if (valid.Count > 0) return valid;
                        logger.LogWarning("Blackjack:Tiers override had no usable entries — using appsettings tiers.");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Blackjack:Tiers override is not valid JSON — using appsettings tiers.");
                    }
                }
                return cfgTiers;
            }
        }
        private int stalledTimeoutSeconds    => RuntimeInt("Table:StalledTimeoutSeconds", cfgStalledTimeout);
        private int disconnectGraceSeconds   => RuntimeInt("Table:DisconnectGraceSeconds", cfgDisconnectGrace);
        private const string SettingsHashKey = "khela:settings";
        private Dictionary<string, string> settingsSnapshot = new();
        private DateTime settingsCacheUntil = DateTime.MinValue;
        private readonly object settingsLock = new();
        private readonly int emoteCooldownMs;           // per-user emote anti-spam cooldown
        private readonly HashSet<string> emoteIds;      // allowed emote catalog ids (empty ⇒ safe-token guard)
        private readonly bool progressionEnabled;       // master switch for the game-extension layer (gifted-taint + XP)
        private readonly IConfiguration config;
        private readonly IWebHostEnvironment env;
        private const int DefaultMaxPlayers = 5;

        public BlackjackTableManager(IRedisService redisService, IServiceScopeFactory scopeFactory,
            IHubContext<BlackjackHub> hubContext, ILogger<BlackjackTableManager> logger, IConfiguration config,
            IWebHostEnvironment env)
        {
            this.redisService = redisService;
            this.scopeFactory = scopeFactory;
            this.hubContext = hubContext;
            this.logger = logger;
            this.config = config;
            this.env = env;
            this.cfgTurnSeconds = config.GetValue("Blackjack:TurnSeconds", 30);
            this.cfgInsuranceSeconds = config.GetValue("Blackjack:InsuranceSeconds", 12);
            this.cfgMaxPresentationSeconds = config.GetValue("Blackjack:MaxPresentationSeconds", 45);
            this.cfgPresentPerCardSeconds = config.GetValue("Blackjack:PresentationPerCardSeconds", 3);
            this.cfgPlayableSeats = config.GetValue("Blackjack:PlayableSeats", 3);
            this.cfgBettingSeconds = config.GetValue("Blackjack:BettingSeconds", 15);
            this.cfgMaxIdleBettingWindows = config.GetValue("Blackjack:MaxIdleBettingWindows", 3);
            this.cfgDeckCount = config.GetValue("Blackjack:DeckCount", 6);
            this.cfgShoePenetrationPercent = config.GetValue("Blackjack:ShoePenetrationPercent", 75);
            this.cfgReshuffleEveryRound = config.GetValue("Blackjack:ReshuffleEveryRound", false);

            // Tiers from appsettings; if the section is missing or empty, fall back to the historical three brackets
            // so an un-updated deployment still has a lobby rather than none.
            this.cfgTiers = LobbyPlan.Sanitize(config.GetSection("Blackjack:Tiers").Get<List<BetTier>>());
            if (this.cfgTiers.Count == 0)
                this.cfgTiers = new List<BetTier>
                {
                    new() { MinBet = 1000m,  MaxBet = 10000m  },
                    new() { MinBet = 5000m,  MaxBet = 25000m  },
                    new() { MinBet = 25000m, MaxBet = 100000m },
                };
            this.cfgMinTablesPerTier      = config.GetValue("Blackjack:MinTablesPerTier", 3);
            this.cfgMinJoinablePerTier    = config.GetValue("Blackjack:MinJoinableTablesPerTier", 3);
            this.cfgMinEmptyPerTier       = config.GetValue("Blackjack:MinEmptyTablesPerTier", 1);
            this.cfgEmptyTableGraceSeconds = config.GetValue("Blackjack:EmptyTableGraceSeconds", 120);
            this.cfgLobbyPageSize          = config.GetValue("Blackjack:LobbyPageSize", 15);
            this.cfgLobbyFullTablesShown   = config.GetValue("Blackjack:LobbyFullTablesShown", 3);
            this.cfgLobbyEmptyTablesShown  = config.GetValue("Blackjack:LobbyEmptyTablesShown", 1);
            this.cfgStalledTimeout = config.GetValue("Table:StalledTimeoutSeconds", 30);
            this.cfgDisconnectGrace = config.GetValue("Table:DisconnectGraceSeconds", 20);
            this.emoteCooldownMs = config.GetValue("Emotes:CooldownMs", 1500);
            this.emoteIds = new HashSet<string>(
                config.GetSection("Emotes:Ids").Get<string[]>() ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            this.progressionEnabled = config.GetValue("Progression:Enabled", true);
        }

        // Read a timing setting live: admin runtime override (Redis "khela:settings" hash, cached ~15s) overlaid
        // on the appsettings default. A non-positive or unparseable override is ignored (keeps the default), so a
        // bad value can never break the round clock. Money-safety §5 (never pull a live-stake seat) is unaffected
        // — this only moves the threshold. Redis hiccup ⇒ keep the last snapshot.
        private int RuntimeInt(string key, int fallback)
        {
            if (DateTime.UtcNow >= settingsCacheUntil)
            {
                lock (settingsLock)
                {
                    if (DateTime.UtcNow >= settingsCacheUntil)
                    {
                        try
                        {
                            var entries = redisService.GetDatabase().HashGetAll(SettingsHashKey);
                            var d = new Dictionary<string, string>(entries.Length);
                            foreach (var e in entries) d[(string)e.Name] = (string)e.Value;
                            settingsSnapshot = d;
                        }
                        catch { /* Redis hiccup — keep the prior snapshot */ }
                        settingsCacheUntil = DateTime.UtcNow.AddSeconds(15);
                    }
                }
            }
            return settingsSnapshot.TryGetValue(key, out var v) && int.TryParse(v, out var x) && x > 0 ? x : fallback;
        }

        // Same overlay as RuntimeInt, for a value that isn't a number (currently the tier list, as JSON).
        // Returns null when there is no override, so the caller falls back to appsettings.
        private string RuntimeString(string key)
        {
            RuntimeInt(key, 0);   // refresh the shared snapshot / honour the same cache window
            return settingsSnapshot.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        }

        // Same overlay as RuntimeInt, for an on/off knob. Unlike RuntimeInt this accepts 0, so a flag can be turned
        // back OFF from the admin dashboard and not just on. Anything unparseable keeps the appsettings default.
        private bool RuntimeFlag(string key, bool fallback)
        {
            RuntimeInt(key, 0);   // refresh the shared snapshot / honour the same cache window
            return settingsSnapshot.TryGetValue(key, out var v) && int.TryParse(v, out var x) ? x != 0 : fallback;
        }

        // ---- Wallet integration (this manager is a singleton; resolve the scoped wallet per op) ----

        private async Task<decimal> GetWalletChipsAsync(string userId)
        {
            using var scope = scopeFactory.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            return await wallet.GetBalanceAsync(userId, CurrencyType.Chips);
        }

        /// <summary>
        /// Debits a committed stake (initial bet / double / split / insurance) from the authoritative
        /// wallet at the moment it is staked — "debit-on-bet". Idempotent on the round+seat+suffix
        /// correlation id. Throws <see cref="InsufficientFundsException"/> if the wallet can't cover it,
        /// so the same chips can never be staked twice (across seats/tables) and a loss can never overdraw
        /// at settle.
        /// </summary>
        private async Task<(string TxId, decimal Balance, decimal GiftedSpent)> DebitStakeAsync(string userId, decimal amount, string tableId, string roundId, int seat, string suffix)
        {
            using var scope = scopeFactory.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            var ctx = new WalletContext { TableId = tableId, RoundId = roundId, Description = $"Blackjack {suffix} round {roundId} seat {seat}" };
            // roundId is a GUID (unique alone); the suffix distinguishes stk/dd/sp/ins. Fits CorrelationId(64).
            var correlationId = $"bjr:{roundId}:{seat}:{suffix}";
            var txn = await wallet.DebitAsync(userId, CurrencyType.Chips, amount, TransactionType.Bet, correlationId, ctx);
            // GiftedSpent = how much of this stake came from the tainted slice, so a refund can restore exactly it.
            return (txn.TransactionId.ToString(), txn.BalanceAfter ?? 0m, Math.Abs(txn.GiftedDelta));
        }

        /// <summary>
        /// Credits a settled hand's GROSS return (wins + pushes + insurance payouts) to the wallet. Stakes
        /// already left the wallet via <see cref="DebitStakeAsync"/>, so settle only ever returns money.
        /// Idempotent on the round+seat payout key, so a retried settle never double-pays.
        /// </summary>
        private async Task<(string TxId, decimal Balance)> CreditGrossAsync(string userId, decimal gross, string tableId, string roundId, int seat, decimal giftedCredit)
        {
            using var scope = scopeFactory.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            // Credit back the stake's gifted fraction so winnings keep their taint — no laundering on win/push.
            var ctx = new WalletContext { TableId = tableId, RoundId = roundId, Description = $"Blackjack payout round {roundId} seat {seat}", CreditGiftedAmount = giftedCredit };
            var correlationId = $"bjr:{roundId}:{seat}:pay";
            var txn = await wallet.CreditAsync(userId, CurrencyType.Chips, gross, TransactionType.Win, correlationId, ctx);
            return (txn.TransactionId.ToString(), txn.BalanceAfter ?? 0m);
        }

        /// <summary>
        /// Credits the gross payout, retrying a few times on a transient/locked-wallet failure. Safe to
        /// retry because the credit is idempotent on its <c>:pay</c> correlation id (never double-pays).
        /// </summary>
        private async Task<(string TxId, decimal Balance)> CreditGrossWithRetryAsync(string userId, decimal gross, string tableId, string roundId, int seat, decimal giftedCredit)
        {
            const int attempts = 3;
            for (int i = 1; ; i++)
            {
                try { return await CreditGrossAsync(userId, gross, tableId, roundId, seat, giftedCredit); }
                catch (Exception ex) when (i < attempts)
                {
                    logger.LogWarning(ex, "Payout credit attempt {Attempt} failed for seat {Seat} round {RoundId}; retrying.", i, seat, roundId);
                    await Task.Delay(150 * i);
                }
            }
        }

        /// <summary>
        /// Refunds a reserved stake when a round fails to start AFTER the stake was debited, so chips are
        /// never stranded. Idempotent on its own correlation id, so a retried refund never double-credits.
        /// </summary>
        private async Task RefundStakeAsync(string userId, decimal amount, string tableId, string roundId, int seat, decimal giftedRestore)
        {
            using var scope = scopeFactory.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            // Restore the EXACT gifted slice the stake was drawn from, so a refund can't launder gifted → earned.
            var ctx = new WalletContext { TableId = tableId, RoundId = roundId, Description = $"Blackjack stake refund round {roundId} seat {seat}", CreditGiftedAmount = giftedRestore };
            var correlationId = $"bjr:{roundId}:{seat}:stkrf";
            await wallet.CreditAsync(userId, CurrencyType.Chips, amount, TransactionType.Refund, correlationId, ctx);
        }

        /// <summary>
        /// Reads the seat's stake ledger for a round and splits it into clean vs gifted from the per-txn
        /// <c>GiftedDelta</c> primitive. <c>TotalStake</c>/<c>GiftedStake</c> cover EVERY Bet debit (incl.
        /// insurance) and drive the proportional payout; <c>MainStake</c>/<c>MainGiftedStake</c> EXCLUDE
        /// insurance and give the EARNED stake that is the progression XP basis (System A). All sums are
        /// positive magnitudes.
        /// </summary>
        private async Task<(decimal TotalStake, decimal GiftedStake, decimal MainStake, decimal MainGiftedStake)> GetSeatStakeSplitAsync(string userId, string roundId, int seat)
        {
            if (!Guid.TryParse(userId, out var uid) || string.IsNullOrEmpty(roundId))
                return (0m, 0m, 0m, 0m);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = await db.PlayerWallets.AsNoTracking()
                .FirstOrDefaultAsync(w => w.UserId == uid && w.Currency == CurrencyType.Chips);
            if (wallet == null) return (0m, 0m, 0m, 0m);

            // SEAT-scoped: a user holding >1 seat in a round must NOT pool both seats' stakes — that would let a
            // gifted seat's winnings inherit an earned seat's clean ratio (laundering). Stake corr ids encode
            // :{seat}:, so filter to THIS seat's Bet rows only (matches the reconciliation predicate, so settle
            // and recon can never diverge on the taint ratio).
            var prefix = $"bjr:{roundId}:{seat}:";
            var rows = await db.WalletTransactions.AsNoTracking()
                .Where(t => t.WalletId == wallet.WalletId && t.Type == TransactionType.Bet && t.RoundId == roundId
                            && t.CorrelationId != null && t.CorrelationId.StartsWith(prefix))
                .Select(t => new { t.Amount, t.GiftedDelta, t.CorrelationId })
                .ToListAsync();

            decimal total = 0m, gifted = 0m, mainTotal = 0m, mainGifted = 0m;
            foreach (var r in rows)
            {
                var amt = Math.Abs(r.Amount);          // debits are stored negative
                var g = Math.Abs(r.GiftedDelta);
                total += amt; gifted += g;
                var isInsurance = r.CorrelationId != null && r.CorrelationId.Contains(":ins");
                if (!isInsurance) { mainTotal += amt; mainGifted += g; }   // XP basis excludes the insurance side bet
            }
            return (total, gifted, mainTotal, mainGifted);
        }

        /// <summary>
        /// Accrues progression XP for a settled seat from its EARNED (clean) wager and returns the XP granted
        /// (post-cap) for the stats roll-up. Idempotent per (round, user); resolved per-call (the manager is a
        /// singleton). Wrapped so a progression failure can never break settle — the wallet already settled.
        /// </summary>
        private async Task<long> AccrueProgressionAsync(Guid userId, decimal cleanWager, bool win, string roundId)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var progression = scope.ServiceProvider.GetRequiredService<IProgressionService>();
                return await progression.AccrueForRoundAsync(userId, cleanWager, win, roundId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Progression accrual failed for user {UserId} round {RoundId}", userId, roundId);
                return 0;
            }
        }

        /// <summary>
        /// Accrues VIP Status Points for a settled seat from its EARNED (clean) wager (FLAT ×1, daily-capped, never
        /// from winnings; §3). Idempotent per (round, user). Best-effort + wrapped so a VIP failure can never break
        /// settle — the wallet already settled.
        /// </summary>
        private async Task AccrueVipAsync(Guid userId, decimal cleanWager, string roundId)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var vip = scope.ServiceProvider.GetRequiredService<Khela.Game.Services.Vip.IVipService>();
                await vip.AccrueForRoundAsync(userId, cleanWager, roundId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "VIP accrual failed for user {UserId} round {RoundId}", userId, roundId);
            }
        }

        /// <summary>
        /// Accrues Loyalty Points for a settled seat from its EARNED (clean) wager × the player's VIP benefit
        /// multiplier (§4). Idempotent per (round, user). Best-effort + wrapped so a Loyalty failure can never
        /// break settle — the wallet already settled.
        /// </summary>
        private async Task AccrueLoyaltyAsync(Guid userId, decimal cleanWager, string roundId)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var loyalty = scope.ServiceProvider.GetRequiredService<Khela.Game.Services.Loyalty.ILoyaltyService>();
                await loyalty.AccrueForRoundAsync(userId, cleanWager, roundId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Loyalty accrual failed for user {UserId} round {RoundId}", userId, roundId);
            }
        }

        /// <summary>Advances daily-mission progress for a settled seat from the round's events (round count, clean
        /// wager, hand outcomes from the stat counters). Idempotent per (round, user). Best-effort — never breaks settle.</summary>
        private async Task AccrueMissionsAsync(Guid userId, IReadOnlyDictionary<string, long> statCounters, decimal cleanWager, string roundId)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var missions = scope.ServiceProvider.GetRequiredService<Khela.Game.Services.Missions.IMissionService>();
                await missions.ReportRoundAsync(userId, statCounters, cleanWager, roundId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mission progress failed for user {UserId} round {RoundId}", userId, roundId);
            }
        }

        /// <summary>
        /// Persists the settled hand to the audit tables (provably-fair record + per-seat results).
        /// Wrapped so an audit failure can never break gameplay — the round already settled in
        /// Redis and the wallet.
        /// </summary>
        private async Task PersistHandAsync(BlackjackTable table, string roundId, List<GameHandParticipant> participants)
        {
            try
            {
                // Derive from the SHOE nonce, not the round nonce: with a persistent multi-deck shoe one shuffle spans
                // many rounds, so RoundNonce would record a seed that never produced this hand's cards.
                //
                // ShoeNonce 0 means this round was DEALT before the shoe existed — a round in flight across a deploy,
                // settling on the new build. Those cards came from a RoundNonce-derived shuffle, so record that
                // instead; otherwise the one hand straddling the upgrade would verify false forever.
                var seedNonce = table.ShoeNonce > 0 ? table.ShoeNonce : table.RoundNonce;
                var roundSeed = ProvableShuffle.DeriveSeed(
                    Convert.FromHexString(table.ServerSeed), table.ClientSeed, seedNonce);

                var header = new GameHandHeader
                {
                    TableId = table.TableId,
                    GameType = GameType.Blackjack,
                    RoundId = roundId,
                    HandNumber = (int)table.RoundNonce,
                    StartedAt = table.RoundStartedAt?.UtcDateTime ?? DateTime.UtcNow,
                    SettledAt = DateTime.UtcNow,
                    Status = HandStatus.Settled,
                    ShoeId = string.IsNullOrEmpty(table.ShoeHash) ? table.ServerSeedHash : table.ShoeHash,
                    ShoeCardsDealt = table.ShoeDealtAtRoundStart,   // where in the shoe this hand started
                    // Normally absent. A shoe that had to extend itself mid-round dealt cards BEYOND the shuffle the
                    // recorded hash covers, so replay needs to know to derive those continuation segments too —
                    // otherwise the hand verifies against a shoe that is missing some of its own cards.
                    MetadataJson = (table.Game?.ShoeExtensions ?? 0) > 0
                        ? JsonSerializer.Serialize(new { shoeExtensions = table.Game.ShoeExtensions, extensionSeedLabel = "shoe-ext" })
                        : null,
                    ShuffleSeed = Convert.ToHexString(roundSeed).ToLowerInvariant(),
                    DeckHash = table.CurrentDeckHash,
                    // Chain to the previous settled hand on this table (genesis = the published server-seed commitment),
                    // so the per-table sequence of hands is tamper-evident.
                    PrevHandHash = string.IsNullOrEmpty(table.LastHandHash) ? table.ServerSeedHash : table.LastHandHash,
                    ResultChecksum = ComputeResultChecksum(table, participants)
                };

                foreach (var p in participants) p.HandId = header.HandId;

                // Flush the buffered move-by-move log → GameHandActions, stamped with this round's HandId.
                var actions = (table.ActionLog ?? new List<GameActionEntry>()).Select(a => new GameHandAction
                {
                    HandId = header.HandId,
                    UserId = Guid.TryParse(a.UserId, out var au) ? au : (Guid?)null,
                    SeatNumber = a.SeatNumber,
                    ActionType = a.ActionType,
                    CardDrawn = a.CardDrawn,
                    HandValueAfter = a.HandValueAfter,
                    Amount = a.Amount,
                    CreatedAt = a.CreatedAt.UtcDateTime
                }).ToList();

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // Settle-stage snapshot: the canonical final board + its hash, so the hand is independently
                // reconstructable beyond the checksum — completes the provably-fair audit record.
                var snapshotJson = JsonSerializer.Serialize(new
                {
                    roundId,
                    handNumber = header.HandNumber,
                    prevHandHash = header.PrevHandHash,
                    deckHash = header.DeckHash,
                    dealer = table.Game.Dealer.Hand.Cards.Select(ProvableShuffle.Canonical),
                    seats = participants.Where(p => p.HandIndex >= 0).Select(p => new
                    {
                        p.SeatNumber, p.HandIndex, p.Bet, p.InsuranceBet, p.Payout,
                        p.FinalHandValue, p.Bust, p.Blackjack, p.Outcome
                    })
                });

                db.GameHandHeaders.Add(header);
                db.GameHandParticipants.AddRange(participants);
                if (actions.Count > 0) db.GameHandActions.AddRange(actions);
                db.GameHandSnapshots.Add(new GameHandSnapshot
                {
                    HandId = header.HandId,
                    Stage = SnapshotStage.Settle,
                    SnapshotJson = snapshotJson,
                    SnapshotHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant()
                });
                await db.SaveChangesAsync();

                table.LastHandId = header.HandId.ToString(); // surfaced in the board for one-click verify
                table.LastHandHash = header.ResultChecksum;  // the next hand chains its PrevHandHash to this one
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist blackjack hand audit for table {TableId} round {RoundId}", table.TableId, roundId);
            }
        }

        /// <summary>
        /// Rolls the settled round's per-seat net results into the durable player stats (UserGameStats +
        /// UserProfile). Best-effort + scoped — runs after the wallet has already settled, so a failure
        /// here never affects money. Maps to the LEADERBOARD GameType (distinct from the ledger enum).
        /// </summary>
        private async Task RecordStatsAsync(List<RoundResult> results)
        {
            if (results.Count == 0) return;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var stats = scope.ServiceProvider.GetRequiredService<IPlayerStatsService>();
                await stats.RecordRoundResultsAsync(Khela.Common.Leaderboards.GameType.Blackjack, results);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to record player stats for round");
            }
        }

        private static string ComputeResultChecksum(BlackjackTable table, List<GameHandParticipant> participants)
        {
            var dealer = string.Join(",", table.Game.Dealer.Hand.Cards.Select(ProvableShuffle.Canonical));
            var players = string.Join(";", participants.OrderBy(p => p.SeatNumber)
                .Select(p => $"{p.SeatNumber}:{p.FinalHandValue}:{p.Outcome}:{p.Payout}"));
            // The deck hash identifies the SHOE, which now spans many rounds — so it no longer distinguishes one
            // round from the next on its own. The round id and the shoe offset pin the checksum to this hand.
            var canonical = $"{table.CurrentDeckHash}|R={table.CurrentRoundId}|O={table.ShoeDealtAtRoundStart}|D={dealer}|{players}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        private string GetKey(string tableId) => $"blackjack:table:{tableId}";

        // Redis SET of all active table ids, so the lobby can enumerate tables without SCAN.
        private const string LobbyIndexKey = "blackjack:tables";

        // Create a new table
        public async Task<TableCreateResult> CreateTableAsync(int? maxPlayers = null, int? maxSeatsPerUser = null,
            BlackjackMode mode = BlackjackMode.Classic, decimal minBet = 0, decimal maxBet = 0)
        {
            var tableId = Guid.NewGuid().ToString();
            var game = new BlackJackGame();

            var table = new BlackjackTable
            {
                TableId = tableId,
                MaxPlayers = Math.Clamp(maxPlayers ?? DefaultMaxPlayers, 1, 10),
                Game = game,
                RoundInProgress = false,
                UpdatedAt = DateTimeOffset.UtcNow,
                MaxSeatsPerUser = Math.Clamp(maxSeatsPerUser ?? 1, 1, Math.Clamp(maxPlayers ?? DefaultMaxPlayers, 1, 10)),
                Mode = mode,
                MinBet = minBet,
                MaxBet = maxBet
            };

            table.Seats = Enumerable.Range(1, table.MaxPlayers)
                .Select(i => new Seat { SeatNumber = i })
                .ToList();

            table.TurnDurationSeconds = turnDurationSeconds;
            table.MaxPresentationSeconds = maxPresentationSeconds;
            table.PresentationPerCardSeconds = presentPerCardSeconds;

            // Provably-fair: a secret per-session server seed, committed via its hash (published to
            // clients), combined with a client seed + per-round nonce to seed each shoe's shuffle.
            var serverSeedBytes = RandomNumberGenerator.GetBytes(32);
            table.ServerSeed = Convert.ToHexString(serverSeedBytes);
            table.ServerSeedHash = Convert.ToHexString(SHA256.HashData(serverSeedBytes)).ToLowerInvariant();
            table.ClientSeed = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            table.RoundNonce = 0;

            await SaveTableAsync(tableId, table);
            await redisService.GetDatabase().SetAddAsync(LobbyIndexKey, tableId);

            return new TableCreateResult { Game = game, TableId = tableId, MaxPlayers = table.MaxPlayers, MaxSeatsPerUser = table.MaxSeatsPerUser };
        }

        // Get table by ID
        public async Task<BlackjackTable?> GetTableAsync(string tableId)
        {
            var json = await redisService.GetDatabase().StringGetAsync(GetKey(tableId));
            if (json.IsNullOrEmpty) return null;

            return ParseTable(json);
        }

        /// <summary>
        /// Turn stored JSON into a usable table. Split out so a caller that has already fetched the bytes — the
        /// round-driver tick, which reads once and only sometimes needs the full object — doesn't have to fetch
        /// them again just to get them parsed.
        /// </summary>
        private BlackjackTable ParseTable(RedisValue json)
        {
            if (json.IsNullOrEmpty) return null;

            BlackjackTable table;
            try { table = JsonSerializer.Deserialize<BlackjackTable>(json); }
            catch { return null; }
            if (table == null) return null;

            NormalizeSeats(table);
            // Object identity doesn't survive JSON: re-point the dealer and every player at the single stored shoe,
            // so mid-round draws all come off the same deck (see BlackJackGame.AttachDeck).
            table.Game?.AttachDeck();
            return table;
        }

        /// <summary>True if the given user currently holds a seat at the table (authoritative seat state).</summary>
        public async Task<bool> IsUserSeatedAsync(string tableId, string userId)
        {
            var table = await GetTableAsync(tableId);
            return table != null && table.Seats.Any(s => s.Player != null && s.Player.Id == userId);
        }

        // Save updated table back
        public async Task SaveTableAsync(string tableId, BlackjackTable table, bool broadcast = true)
        {
            table.UpdatedAt = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(table);
            await redisService.GetDatabase().StringSetAsync(GetKey(tableId), json, TimeSpan.FromHours(2)); // TTL 2h

            // Live update: every state change pushes the masked board to this table's subscribers. Heartbeat
            // writes pass broadcast:false — they only refresh LastHeartbeatAt and must NOT fan out a board push
            // (the visible board is unchanged; the reaper broadcasts the derived IsConnected/IsStalled on its tick).
            if (broadcast)
                await hubContext.Clients.Group($"table:{tableId}").SendAsync("TableUpdated", BlackjackBoard.Build(table));
        }

        // ---- Per-table concurrency lock ----
        // The table is a single JSON blob in Redis with plain read-modify-write semantics, so two
        // concurrent mutations (or the round-driver racing a player action) would clobber each other
        // (last-write-wins). A short distributed lock per table serialises all mutations across instances.

        // 30s comfortably exceeds the worst-case settle latency (N per-seat wallet credits + retries + the
        // audit SaveChanges), so the lock can't lapse mid-settle on a slow-but-alive run and re-open the
        // table-blob race; a crashed holder still self-releases within 30s.
        private static readonly TimeSpan TableLockTtl = TimeSpan.FromSeconds(30);
        private const int TableLockRetries = 100;                       // * 50ms = up to ~5s wait under contention
        private static readonly TimeSpan TableLockRetryDelay = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Acquires a short per-table distributed lock and returns a handle that releases it on dispose.
        /// Use as <c>await using var _ = await LockTableAsync(tableId);</c> at the top of any method that
        /// reads-mutates-writes the table, so concurrent actions on the same table serialise.
        /// </summary>
        /// <summary>
        /// Take the table lock only if it comes free within <paramref name="budget"/>, otherwise return null.
        ///
        /// For callers where waiting is worse than skipping — a keep-alive, a read-only refresh — because the full
        /// <see cref="LockTableAsync"/> budget is ~5 seconds and ends in an exception, which turns "the table is
        /// briefly busy" into a visible failure for the player.
        /// </summary>
        private async Task<TableLock> TryLockTableAsync(string tableId, TimeSpan budget)
        {
            var db = redisService.GetDatabase();
            var key = $"bjlock:{tableId}";
            var token = Guid.NewGuid().ToString("N");
            var deadline = DateTime.UtcNow + budget;

            while (true)
            {
                if (await db.StringSetAsync(key, token, TableLockTtl, When.NotExists))
                    return new TableLock(db, key, token);
                if (DateTime.UtcNow >= deadline) return null;
                await Task.Delay(TableLockRetryDelay);
            }
        }

        private async Task<TableLock> LockTableAsync(string tableId)
        {
            var db = redisService.GetDatabase();
            var key = $"bjlock:{tableId}";
            var token = Guid.NewGuid().ToString("N");

            for (int i = 0; i < TableLockRetries; i++)
            {
                if (await db.StringSetAsync(key, token, TableLockTtl, When.NotExists))
                    return new TableLock(db, key, token);
                await Task.Delay(TableLockRetryDelay);
            }
            throw new InvalidOperationException("The table is busy; please retry.");
        }

        /// <summary>Releases its table lock on dispose, but only if it still owns it (token match), so a
        /// lock that already expired and was re-acquired by someone else is never wrongly released.</summary>
        private sealed class TableLock : IAsyncDisposable
        {
            private const string ReleaseLua =
                "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
            private readonly IDatabase _db;
            private readonly string _key;
            private readonly string _token;

            public TableLock(IDatabase db, string key, string token) { _db = db; _key = key; _token = token; }

            public async ValueTask DisposeAsync()
            {
                 try { await _db.ScriptEvaluateAsync(ReleaseLua, new RedisKey[] { _key }, new RedisValue[] { _token }); }
                catch { /* the lock TTL will clean it up */ }
            }
        }

        // ---- Lobby ----

        /// <summary>
        /// Browsable list of blackjack tables for the lobby, optionally filtered by mode.
        /// Self-heals: ids whose table key has expired (TTL) are pruned from the index.
        /// </summary>
        /// <param name="minBet">With <paramref name="maxBet"/>, restricts the list to ONE bet tier — what the
        /// client's stake filter sends. Omit both to browse every tier.</param>
        public async Task<List<BlackjackTableSummary>> GetLobbyAsync(
            BlackjackMode? mode = null, decimal? minBet = null, decimal? maxBet = null)
        {
            // The table browser is the single most-requested screen in the game and its contents barely change
            // between one player's request and the next. Serving a shared snapshot for a moment turns "every
            // browsing player reads every table" into "one read regardless of how many are browsing" — which is
            // the difference between the lobby costing nothing at a hundred players and costing everything.
            var cacheKey = $"{mode?.ToString() ?? "*"}|{minBet?.ToString(CultureInfo.InvariantCulture) ?? "*"}|{maxBet?.ToString(CultureInfo.InvariantCulture) ?? "*"}";
            if (lobbyCache.TryGetValue(cacheKey, out var hit) && DateTimeOffset.UtcNow < hit.Expires)
                return hit.Rows;

            var rows = await BuildLobbyAsync(mode, minBet, maxBet);

            // A genuinely empty result means the tier has not been stocked yet — a cold start, before the driver's
            // first balance pass. That is the ONE case worth making the player wait for, so they never see an
            // empty lobby; every other load leaves balancing to the driver and returns immediately.
            if (rows.Count == 0)
            {
                await BalanceLobbyAsync();
                rows = await BuildLobbyAsync(mode, minBet, maxBet);
            }

            lobbyCache[cacheKey] = (DateTimeOffset.UtcNow.Add(LobbyCacheWindow), rows);
            return rows;
        }

        /// <summary>How long a lobby snapshot is shared for. Long enough that a crowd costs one read, short enough
        /// that a table filling up shows within a beat.</summary>
        private static readonly TimeSpan LobbyCacheWindow = TimeSpan.FromMilliseconds(1500);

        private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, List<BlackjackTableSummary> Rows)>
            lobbyCache = new();

        private async Task<List<BlackjackTableSummary>> BuildLobbyAsync(
            BlackjackMode? mode, decimal? minBet, decimal? maxBet)
        {
            var db = redisService.GetDatabase();

            var ids = await db.SetMembersAsync(LobbyIndexKey);
            var rows = new List<(BlackjackTableSummary Summary, TableCapacity Capacity)>();
            if (ids.Length == 0) return new List<BlackjackTableSummary>();

            // ONE round trip for the whole lobby instead of one per table. With a tier holding thirty tables and
            // several tiers open, the old per-table read made browsing cost more than playing.
            var keys = ids.Select(id => (RedisKey)GetKey((string)id)).ToArray();
            var blobs = await db.StringGetAsync(keys);
            var stale = new List<RedisValue>();

            for (int i = 0; i < ids.Length; i++)
            {
                if (blobs[i].IsNullOrEmpty) { stale.Add(ids[i]); continue; }   // key expired (TTL) -> drop from index

                // Parsed into the LIGHTWEIGHT shape. A stored table carries its whole shoe — 312 cards — plus every
                // hand; rebuilding all of that for tables we are about to filter out by stake was the real cost
                // here, not the round trips. TableLite declares only what the lobby shows and the parser skips the
                // rest of the document without allocating it.
                TableLite t;
                try { t = JsonSerializer.Deserialize<TableLite>(blobs[i]); }
                catch { continue; }
                if (t == null) continue;

                if (mode.HasValue && t.Mode != mode.Value) continue;
                if (minBet.HasValue && t.MinBet != minBet.Value) continue;
                if (maxBet.HasValue && t.MaxBet != maxBet.Value) continue;

                int occupied = t.Seats?.Count(s => s.Player != null) ?? 0;
                rows.Add((ToSummary(t), new TableCapacity
                {
                    TableId = t.TableId,
                    Occupied = occupied,
                    Capacity = Math.Clamp(playableSeats, 1, Math.Max(1, t.MaxPlayers)),
                    EmptySince = occupied == 0 ? (t.EmptySince ?? DateTimeOffset.UtcNow) : null,
                }));
            }

            if (stale.Count > 0) await db.SetRemoveAsync(LobbyIndexKey, stale.ToArray());

            // Cap PER TIER, not across the whole result — so browsing unfiltered still shows a sensible spread of
            // every bracket instead of the busiest one crowding the rest out. LobbyPlan.Page decides the mix;
            // within a tier the order is fullest-first with the empty table last, which is what makes the final
            // card in the carousel the one you sit at to play alone.
            var result = new List<BlackjackTableSummary>(rows.Count);
            foreach (var group in rows.GroupBy(r => (r.Summary.Mode, r.Summary.MinBet, r.Summary.MaxBet))
                                      .OrderBy(g => g.Key.Mode).ThenBy(g => g.Key.MinBet))
            {
                var page = LobbyPlan.Page(group, r => r.Capacity,
                    lobbyPageSize, lobbyFullTablesShown, lobbyEmptyTablesShown);
                result.AddRange(page.Select(r => r.Summary));
            }
            return result;
        }

        /// <summary>
        /// Keeps every bet tier stocked: opens tables as a tier fills up, and reclaims the surplus once it empties
        /// again. A tier is a STAKE BRACKET, not one table — ninety players at three seats is thirty tables, and the
        /// lobby has to reach that on its own.
        ///
        /// What the floors are and why is documented on <see cref="LobbyPlan"/>; this only supplies the current
        /// state and carries out the decision. A short NX lock serialises concurrent callers (every lobby load and
        /// every driver tick lands here) so a burst of them can't each open the same batch of tables.
        /// </summary>
        /// <summary>Last time a balance pass actually ran, so player-facing calls can skip a redundant one.</summary>
        private long lastBalanceTicks;
        private static readonly TimeSpan BalanceMinInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Balance only if nobody has in the last couple of seconds. The round-driver already balances on its own
        /// tick, so a player loading the lobby should not pay for a pass that just happened — at scale that is the
        /// difference between the lobby costing one Redis read and costing a full rebalance per browsing player.
        /// </summary>
        public Task BalanceLobbyIfDueAsync()
        {
            var now = DateTimeOffset.UtcNow.Ticks;
            var last = Interlocked.Read(ref lastBalanceTicks);
            if (now - last < BalanceMinInterval.Ticks) return Task.CompletedTask;
            if (Interlocked.CompareExchange(ref lastBalanceTicks, now, last) != last) return Task.CompletedTask;
            return BalanceLobbyAsync();
        }

        public async Task BalanceLobbyAsync()
        {
            Interlocked.Exchange(ref lastBalanceTicks, DateTimeOffset.UtcNow.Ticks);
            var db = redisService.GetDatabase();
            var tiers = Tiers;
            if (tiers.Count == 0) return;

            var grace = TimeSpan.FromSeconds(Math.Max(0, emptyTableGraceSeconds));
            var now = DateTimeOffset.UtcNow;

            // Cheap unlocked pre-check so a settled lobby does no work and takes no lock on most ticks.
            var snapshot = await LoadTierStateAsync(db, tiers);
            bool anyWork = tiers.Any(t =>
                LobbyPlan.TablesToCreate(snapshot[t.Key], minTablesPerTier, minJoinablePerTier, minEmptyPerTier) > 0 ||
                LobbyPlan.TablesToRemove(snapshot[t.Key], minTablesPerTier, minJoinablePerTier, minEmptyPerTier, grace, now).Count > 0);
            if (!anyWork) return;

            var lockKey = "blackjack:tables:seedlock";
            var token = Guid.NewGuid().ToString("N");
            if (!await db.StringSetAsync(lockKey, token, TimeSpan.FromSeconds(10), When.NotExists))
                return;   // another caller is already balancing
            try
            {
                snapshot = await LoadTierStateAsync(db, tiers);   // re-read under the lock
                foreach (var tier in tiers)
                {
                    var state = snapshot[tier.Key];

                    int create = LobbyPlan.TablesToCreate(state, minTablesPerTier, minJoinablePerTier, minEmptyPerTier);
                    for (int i = 0; i < create; i++)
                        await CreateTableAsync(maxPlayers: 5, maxSeatsPerUser: 1,
                            mode: BlackjackMode.Classic, minBet: tier.MinBet, maxBet: tier.MaxBet);
                    if (create > 0)
                        logger.LogInformation("Lobby: opened {N} table(s) for tier {Tier} ({Have} existing)", create, tier.Key, state.Count);

                    foreach (var id in LobbyPlan.TablesToRemove(state, minTablesPerTier, minJoinablePerTier, minEmptyPerTier, grace, now))
                    {
                        // The decision came from a snapshot, and a player can sit down between reading it and acting
                        // on it. Take the table's own lock and re-check that it is STILL empty and idle before
                        // deleting — deleting a table with someone at it would strand a live stake.
                        await using var tableLock = await LockTableAsync(id);
                        var live = await GetTableAsync(id);
                        if (live == null)
                        {
                            await db.SetRemoveAsync(LobbyIndexKey, id);   // already gone; just tidy the index
                            continue;
                        }
                        if (live.RoundInProgress || live.Seats.Any(s => s.Player != null))
                        {
                            logger.LogDebug("Lobby: table {TableId} was claimed while being reclaimed — left alone", id);
                            continue;
                        }

                        await db.SetRemoveAsync(LobbyIndexKey, id);
                        await db.KeyDeleteAsync(GetKey(id));
                        logger.LogInformation("Lobby: reclaimed empty table {TableId} from tier {Tier}", id, tier.Key);
                    }
                }
            }
            finally
            {
                // Tables were opened or reclaimed, so any shared lobby snapshot now describes a lobby that no
                // longer exists. Drop it rather than let a browsing player see a table that has just been removed.
                lobbyCache.Clear();

                await db.ScriptEvaluateAsync(
                    "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end",
                    new RedisKey[] { lockKey }, new RedisValue[] { token });
            }
        }

        /// <summary>Current tables grouped by tier key. Tables whose stakes match no configured tier are ignored —
        /// a retired tier's tables are left alone to empty out naturally rather than being deleted under players.</summary>
        private async Task<Dictionary<string, List<TableCapacity>>> LoadTierStateAsync(IDatabase db, IReadOnlyList<BetTier> tiers)
        {
            // Built with an explicit loop, not ToDictionary: a duplicate key must never be able to throw out of the
            // lobby endpoint. Sanitize already removes duplicates — this is the belt to that braces.
            var byTier = new Dictionary<string, List<TableCapacity>>(StringComparer.Ordinal);
            foreach (var t in tiers) byTier[t.Key] = new List<TableCapacity>();

            var ids = await db.SetMembersAsync(LobbyIndexKey);
            if (ids.Length == 0) return byTier;

            // ONE round trip for every table, not one per table. This runs on the driver's tick, so it must not
            // scale its latency with the size of the lobby.
            var keys = ids.Select(id => (RedisKey)GetKey((string)id)).ToArray();
            var blobs = await db.StringGetAsync(keys);
            var stale = new List<RedisValue>();

            for (int i = 0; i < ids.Length; i++)
            {
                if (blobs[i].IsNullOrEmpty) { stale.Add(ids[i]); continue; }   // TTL-expired — drop from the index

                // Parsed into a LIGHTWEIGHT shape, never the whole table. A stored table carries its entire shoe —
                // 312 cards — and every player's hands; materialising all of that just to count occupied seats
                // would mean tens of thousands of throwaway objects per pass. TableLite declares only the handful
                // of fields the balancer reads, and System.Text.Json skips the rest of the document without
                // allocating it.
                TableLite t;
                try { t = JsonSerializer.Deserialize<TableLite>(blobs[i]); }
                catch { continue; }
                if (t == null) continue;

                // Tiers describe the stakes of the tables the balancer itself opens, which are Classic. A variant
                // table with the same stakes is a different product and must not be counted as stocking this tier.
                if (t.Mode != BlackjackMode.Classic) continue;

                var tier = tiers.FirstOrDefault(x => x.Matches(t.MinBet, t.MaxBet));
                if (tier == null) continue;

                int occupied = t.Seats?.Count(s => s.Player != null) ?? 0;
                byTier[tier.Key].Add(new TableCapacity
                {
                    TableId = t.TableId,
                    Occupied = occupied,
                    Capacity = Math.Clamp(playableSeats, 1, Math.Max(1, t.MaxPlayers)),
                    // Tables that predate this field, or that emptied before it existed, are treated as empty from
                    // now — they get a full grace period rather than being reclaimed instantly on upgrade.
                    EmptySince = occupied == 0 ? (t.EmptySince ?? DateTimeOffset.UtcNow) : null,
                });
            }

            if (stale.Count > 0) await db.SetRemoveAsync(LobbyIndexKey, stale.ToArray());
            return byTier;
        }

        /// <summary>
        /// The few fields the lobby balancer needs from a stored table. Deliberately does NOT declare Game, so the
        /// shoe and every hand are skipped by the parser instead of being rebuilt as objects nobody looks at.
        /// </summary>
        private sealed class TableLite
        {
            public string TableId { get; set; }
            public BlackjackMode Mode { get; set; }
            public decimal MinBet { get; set; }
            public decimal MaxBet { get; set; }
            public int MaxPlayers { get; set; }
            public bool RoundInProgress { get; set; }
            public DateTimeOffset? EmptySince { get; set; }
            public List<SeatLite> Seats { get; set; }

            internal sealed class SeatLite
            {
                public int SeatNumber { get; set; }
                public PlayerLite Player { get; set; }
            }

            /// <summary>Just what a lobby card shows about someone sitting there — never their hand or their shoe.</summary>
            internal sealed class PlayerLite
            {
                public string Id { get; set; }
                public string Name { get; set; }
                public string Image { get; set; }
                public decimal Balance { get; set; }
                public int SeatNumber { get; set; }
            }
        }

        /// <summary>
        /// Build a lobby card from the lightweight shape. Mirrors <see cref="ToSummary(BlackjackTable)"/> — keep the
        /// two in step; this one exists so browsing never has to rebuild a table's shoe just to show its stakes.
        /// </summary>
        private BlackjackTableSummary ToSummary(TableLite t)
        {
            var occupants = (t.Seats ?? new List<TableLite.SeatLite>())
                .Where(s => s.Player != null)
                .Select(s => new TableOccupant
                {
                    SeatNumber = s.SeatNumber != 0 ? s.SeatNumber : s.Player.SeatNumber,
                    Name = s.Player.Name,
                    Image = s.Player.Image,
                    Balance = s.Player.Balance
                })
                .ToList();

            return new BlackjackTableSummary
            {
                TableId = t.TableId,
                Mode = t.Mode,
                MinBet = t.MinBet,
                MaxBet = t.MaxBet,
                // Advertise the PLAYABLE capacity, not the raw seat count — see ToSummary(BlackjackTable).
                MaxPlayers = Math.Clamp(playableSeats, 1, Math.Max(1, t.MaxPlayers)),
                SeatsOccupied = occupants.Count,
                RoundInProgress = t.RoundInProgress,
                Occupants = occupants
            };
        }

        /// <summary>Back-compat alias — the lobby used to seed a fixed set of house tables. See <see cref="BalanceLobbyAsync"/>.</summary>
        public Task EnsureDefaultTablesAsync() => BalanceLobbyAsync();

        /// <summary>DEV: wipe every lobby table (index + entries) and let the balancer rebuild each tier from
        /// scratch. Use after changing the configured tiers so the lobby reflects the new stakes.</summary>
        public async Task<List<BlackjackTableSummary>> ReseedDefaultTablesAsync()
        {
            var db = redisService.GetDatabase();
            var ids = await db.SetMembersAsync(LobbyIndexKey);
            foreach (var id in ids)
                await db.KeyDeleteAsync(GetKey((string)id));
            await db.KeyDeleteAsync(LobbyIndexKey);
            await EnsureDefaultTablesAsync();
            return await GetLobbyAsync();
        }

        private BlackjackTableSummary ToSummary(BlackjackTable table)
        {
            var occupants = table.Seats
                .Where(s => s.Player != null)
                .Select(s => new TableOccupant
                {
                    SeatNumber = s.SeatNumber,
                    Name = s.Player!.Name,
                    Image = s.Player.Image,
                    Balance = s.Player.Balance
                })
                .ToList();

            return new BlackjackTableSummary
            {
                TableId = table.TableId,
                Mode = table.Mode,
                MinBet = table.MinBet,
                MaxBet = table.MaxBet,
                // Advertise the PLAYABLE capacity, not the raw seat count — seats above the cap can never be occupied,
                // so reporting 5 would let the lobby offer a join the server then rejects as full.
                MaxPlayers = Math.Clamp(playableSeats, 1, table.MaxPlayers),
                SeatsOccupied = occupants.Count,
                RoundInProgress = table.RoundInProgress,
                Occupants = occupants
            };
        }

        public async Task<BlackjackTable?> AddPlayerAsync(string tableId, Player player, int? requestedSeat = null)
        {
            // Read the wallet BEFORE taking the table lock. It is a database round trip, and on a cold server that
            // means EF's first model build and connection open — seconds, not milliseconds. Holding a table's lock
            // across it blocks every other action on that table for the duration, and the joiner's own request can
            // outlive the client's patience. Nothing here depends on the table, so it does not belong inside.
            var chips = await GetWalletChipsAsync(player.Id);

            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            // IDEMPOTENT: joining a table you are already sitting at SUCCEEDS and returns the board.
            //
            // A join is "make sure I am seated here", not "add another seat". Treating a repeat as an error breaks
            // the one case that matters most: a slow join that the client gave up on. The request still lands, the
            // player IS seated, the client retries, and the retry is rejected — so the table reports failure while
            // the lobby shows them sitting at it. Retrying a request that already succeeded must be safe.
            var alreadyMine = table.Seats.FirstOrDefault(s => s.Player != null && s.Player.Id == player.Id);
            if (alreadyMine != null &&
                (!requestedSeat.HasValue || requestedSeat.Value == alreadyMine.SeatNumber
                 || table.Seats.Count(s => s.Player != null && s.Player.Id == player.Id) >= table.MaxSeatsPerUser))
            {
                // Treat it as a fresh arrival for liveness — a client that had to retry has not been heartbeating.
                alreadyMine.LastHeartbeatAt = DateTime.UtcNow;
                alreadyMine.IsConnected = true;
                alreadyMine.IsStalled = false;
                table.EmptySince = null;
                await SaveTableAsync(tableId, table);
                return table;
            }

            var existingSeatsForUser = table.Seats.Count(s => s.Player != null && s.Player.Id == player.Id);
            if (existingSeatsForUser >= table.MaxSeatsPerUser)
                throw new InvalidOperationException("Player has reached max seats at this table.");

            // Seat-pick: honor a specific requested seat if free; otherwise auto-assign the first open seat.
            // BOTH paths are capped at `playableSeats` — the table keeps its full seat array, but seats above the cap
            // stay permanently empty because the client has no anchors/rails/layouts/clips for them.
            int seatCap = Math.Clamp(playableSeats, 1, table.MaxPlayers);
            Seat openSeat;
            if (requestedSeat.HasValue)
            {
                if (requestedSeat.Value > seatCap)
                    throw new InvalidOperationException($"Seat {requestedSeat.Value} is not in play at this table.");
                openSeat = table.Seats.FirstOrDefault(s => s.SeatNumber == requestedSeat.Value);
                if (openSeat == null)
                    throw new InvalidOperationException($"Seat {requestedSeat.Value} does not exist at this table.");
                if (openSeat.Player != null)
                    throw new InvalidOperationException($"Seat {requestedSeat.Value} is already taken.");
            }
            else
            {
                openSeat = table.Seats.FirstOrDefault(s => s.Player == null && s.SeatNumber <= seatCap);
                if (openSeat == null)
                    throw new InvalidOperationException("Table is full.");
            }

            // Seat from the AUTHORITATIVE wallet balance — never trust a client-supplied balance. Read above the
            // lock (see the top of this method); it is only a display mirror and the board re-syncs it on every
            // money operation, so reading it a moment earlier costs nothing and keeps the lock short.
            var seatedPlayer = new Player(player.Id, chips, player.Name, player.Image, openSeat.SeatNumber);

            openSeat.Player = seatedPlayer;
            openSeat.LastHeartbeatAt = DateTime.UtcNow;   // start the heartbeat clock so a fresh seat isn't reaped
            openSeat.IsConnected = true;
            openSeat.IsStalled = false;
            openSeat.MissedBetWindows = 0;
            table.Game.Players.Add(seatedPlayer);

            // Someone is here: this table is no longer surplus, so stop its reclaim clock.
            table.EmptySince = null;

            // Start the betting clock so a player who sits and NEVER bets still accumulates idle windows toward
            // eviction. No-op mid-round (ArmBettingWindow bails on RoundInProgress) — settle arms it when the round ends.
            table.BettingDurationSeconds = bettingDurationSeconds;
            table.MaxIdleBettingWindows = maxIdleBettingWindows;
            ArmBettingWindow(table, afterRound: false);

            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> RemovePlayerAsync(string tableId, int seatNumber, string userId)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            // Leaving mid-round forfeits the in-progress wager — but with debit-on-bet the stake ALREADY
            // left the wallet at deal/action, so the forfeit is automatic: this seat simply isn't credited
            // at settle. (A player can't dodge a loss by leaving, and an abandoned winning hand is forfeit.)
            RemoveSeatCore(table, seatNumber);

            await SaveTableAsync(tableId, table);
            return table;
        }

        // Mechanical seat removal shared by the public leave and the stalled-reaper (both already hold the table
        // lock). Frees the seat, drops the player from the game + round-balance map, resets connection flags, and
        // passes the turn on if it was theirs. Callers decide WHEN it is money-safe to call this (see §5 sweep).
        private void RemoveSeatCore(BlackjackTable table, int seatNumber)
        {
            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null) return;
            var wasCurrentTurn = table.RoundInProgress && table.CurrentSeatNumber == seatNumber;

            table.RoundStartBalance?.Remove(seatNumber);
            table.Game.Players.RemoveAll(p => p.SeatNumber == seatNumber);
            seat.Player = null;
            seat.IsStalled = false;
            seat.IsConnected = true;
            seat.LastHeartbeatAt = DateTime.UtcNow;
            seat.MissedBetWindows = 0;   // a freed seat starts fresh for the next occupant

            // If it was this player's turn, pass the turn to the next active player so play continues.
            if (wasCurrentTurn)
                SetInitialTurn(table);

            // An emptied table holds no betting window. Clear the deadline (and its presentation-ack state) so the NEXT
            // player to sit gets a FRESH full window: ArmBettingWindow deliberately won't reset a live deadline, so
            // without this a fast rejoin would inherit the previous occupant's leftover countdown — the "bet timer
            // starts at a random time / where the last player left" bug. Shared windows survive while anyone remains.
            if (ShoePlan.TableIsEmpty(table))
            {
                table.BettingExpiresAt = null;
                table.BetPresentAcked = false;
                table.BetPresentAckedSeats = new List<int>();

                // A table that has gone COMPLETELY empty also retires its shoe, so whoever opens it next starts on a
                // fresh one — the way a real table opens with a new shoe rather than resuming a stranger's half-dealt
                // one. Note this is the LAST player leaving, not any player: a shoe has to survive people coming and
                // going, or a busy table would reshuffle every few minutes and the shoe would mean nothing.
                //
                // Only the shoe's identity is cleared, never Game.Deck: clearing that could pull cards out from under
                // a round still settling. Dropping the identity is enough — the next deal sees no shoe and builds one
                // (ShoePlan.Decide), and the verify endpoint sees nothing live and releases the seeds of every hand
                // played on the old shoe, which is what stops a proof being withheld indefinitely.
                table.ShoeHash = null;
                table.ShoeSize = 0;
                table.CutCardAt = 0;
                table.ShoeDealtAtRoundStart = 0;

                // Start the clock the lobby balancer uses to decide whether this table is surplus.
                table.EmptySince ??= DateTimeOffset.UtcNow;
            }
        }

        /// <summary>
        /// Stamp the caller's seat heartbeat (hub or REST keep-alive). Refreshes only LastHeartbeatAt and saves
        /// WITHOUT a broadcast — the visible board is unchanged, and the reaper derives IsConnected/IsStalled from
        /// this timestamp on its next tick. Returns the table (no-op) if the user isn't seated here.
        /// </summary>
        public async Task<BlackjackTable?> RecordHeartbeatAsync(string tableId, string userId)
        {
            // A heartbeat stamps ONE timestamp. It must never wait on, or fail because of, whatever else the table
            // is doing: a deal holds the table lock across its wallet debit, and if a keep-alive blocks for the full
            // retry budget and then throws, the player sees an error, their seat goes stale, and the reaper evicts
            // someone who was sitting right there playing. So take the lock only if it is free RIGHT NOW, and if it
            // isn't, skip this beat — the next one is seconds away, and the reaper's own mid-round guard covers the
            // gap. Losing a heartbeat is nothing; losing the seat is the game.
            await using var _tableLock = await TryLockTableAsync(tableId, TimeSpan.FromMilliseconds(400));
            if (_tableLock == null) return await GetTableAsync(tableId);   // busy — report state, write nothing

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            var seat = table.Seats.FirstOrDefault(s => s.Player != null && s.Player.Id == userId);
            if (seat == null) return table;   // spectator / already removed — nothing to stamp

            seat.LastHeartbeatAt = DateTime.UtcNow;
            await SaveTableAsync(tableId, table, broadcast: false);   // persist the timestamp; no board fan-out
            return table;
        }

        /// <summary>
        /// Broadcasts a transient EMOTE from a seated player to everyone at the table — no board mutation, no lock.
        /// Identified by a catalog id (the client maps id → visual); validated against the configured allowlist (or
        /// a safe-token guard if none is configured) and rate-limited per user. Returns false if the caller isn't
        /// seated, the id is unknown, or they're still on cooldown.
        /// </summary>
        public async Task<bool> SendEmoteAsync(string tableId, string userId, string emoteId)
        {
            if (string.IsNullOrWhiteSpace(emoteId)) return false;
            emoteId = emoteId.Trim();
            if (emoteIds.Count > 0 ? !emoteIds.Contains(emoteId) : !IsSafeEmoteToken(emoteId))
                return false;   // not in the catalog (or fails the format guard when no catalog is configured)

            var table = await GetTableAsync(tableId);
            var seat = table?.Seats.FirstOrDefault(s => s.Player != null && s.Player.Id == userId);
            if (seat == null) return false;   // only seated players may emote

            // Per-user cooldown (anti-spam): a short Redis NX key; if it already exists they're still cooling down.
            var cdKey = $"emote:cd:{tableId}:{userId}";
            if (!await redisService.GetDatabase().StringSetAsync(cdKey, "1",
                    TimeSpan.FromMilliseconds(Math.Max(100, emoteCooldownMs)), When.NotExists))
                return false;

            await hubContext.Clients.Group($"table:{tableId}")
                .SendAsync("EmoteReceived", new { seatNumber = seat.SeatNumber, emoteId });
            return true;
        }

        private static bool IsSafeEmoteToken(string id)
            => id.Length <= 32 && id.All(c => char.IsLetterOrDigit(c) || c == '_');

        public async Task<BlackjackTable?> PlaceBetAsync(string tableId, string userId, int seatNumber, decimal amount, int handIndex = 0)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (table.RoundInProgress)
                throw new InvalidOperationException("Cannot change bets during an active round.");
            if (amount <= 0)
                throw new InvalidOperationException("Bet amount must be positive.");
            if (table.MinBet > 0 && amount < table.MinBet)
                throw new InvalidOperationException($"Bet is below the table minimum of {table.MinBet}.");
            if (table.MaxBet > 0 && amount > table.MaxBet)
                throw new InvalidOperationException($"Bet is above the table maximum of {table.MaxBet}.");

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;

            player.ClearBet(handIndex);
            player.IncreaseBet(amount, handIndex);
            player.BetThisWindow = true;   // actively committed for THIS window (drives all-bet start + between-rounds bet render)
            seat.MissedBetWindows = 0;   // they bet — clear the idle-eviction counter

            // Second entry point for the betting window: a table sitting idle (nobody bet last round, or the first
            // player just sat down) has no window armed. The first bet opens it, so the round starts on its own even
            // if whoever bet never presses Deal. ArmBettingWindow won't extend an already-running window.
            table.BettingDurationSeconds = bettingDurationSeconds;
            table.MaxIdleBettingWindows = maxIdleBettingWindows;
            ArmBettingWindow(table, afterRound: false);

            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> DealAsync(string tableId, string userId)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            // Only a SEATED player may start the round. Without this any authenticated user could deal on any table —
            // forcing a round to start on strangers (and locking in whatever bets happened to be down) from outside it.
            if (!table.Seats.Any(s => s.Player != null && s.Player.Id == userId))
                throw new InvalidOperationException("You are not seated at this table.");

            // Don't let one player's DEAL cut the SHARED betting window short on the others. While the window is still
            // open and any seated player has not ACTIVELY bet this window, leave the (already-placed) bet standing and
            // let the window run: the driver deals the instant everyone has bet, and auto-deals at expiry on whatever
            // is down. Solo, or the last player to bet, makes all-bet true and falls through to deal now — so single
            // player and the final bettor keep the immediate feel.
            if (table.BettingExpiresAt.HasValue
                && DateTimeOffset.UtcNow < table.BettingExpiresAt.Value
                && !AllSeatedActivelyBet(table))
            {
                return table;   // caller's bet is already placed + broadcast by PlaceBet; the window governs the start
            }

            return await DealCoreAsync(table, tableId);
        }

        /// <summary>True if at least one player is seated AND every seated player has ACTIVELY bet during the current
        /// betting window (<see cref="Player.BetThisWindow"/>). Lets the round start early once everyone has bet,
        /// WITHOUT firing on stale auto-repeat bets carried over from the previous round.</summary>
        private static bool AllSeatedActivelyBet(BlackjackTable table)
        {
            bool any = false;
            foreach (var s in table.Seats)
            {
                if (s.Player == null) continue;
                any = true;
                if (!s.Player.BetThisWindow) return false;
            }
            return any;
        }

        /// <summary>
        /// Recover after a deal that could NOT start (e.g. every seated bet was underfunded, so DealCore threw
        /// "No funded bets" before committing any stake). Clears <see cref="Player.BetThisWindow"/> on every seat so
        /// <see cref="AllSeatedActivelyBet"/> (and the driver's all-bet early-deal) can't immediately re-trigger the
        /// same failing deal on the very next tick — an unbounded busy-loop that spammed board pushes and inflated the
        /// round nonce — and re-arms a FRESH betting window (rather than leaving it null and stranding a seated table
        /// with no clock), so players get another window and idle counters can still climb toward eviction.
        /// </summary>
        private void DisarmAfterFailedDeal(BlackjackTable table)
        {
            // Full round abort. A deal can also throw AFTER it set RoundInProgress=true in memory (e.g. a transient
            // SaveTableAsync failure whose inner catch already refunded every stake). If we didn't force the round back
            // to "not in progress", ArmBettingWindow would no-op (it bails while RoundInProgress) — leaving a phantom
            // round with a null window that settle could later credit on already-refunded stakes. Clearing the round +
            // per-seat bet state makes the abort clean in BOTH the common "no funded bets" case and that edge.
            table.RoundInProgress = false;
            table.CurrentRoundId = null;
            foreach (var s in table.Seats)
            {
                if (s.Player == null) continue;
                s.Player.BetThisWindow = false;   // disarm all-bet so the driver can't re-fire the same failing deal every tick
                s.Player.InRound = false;
                s.Player.ClearBet(0);
            }
            table.BettingDurationSeconds = bettingDurationSeconds;
            table.MaxIdleBettingWindows = maxIdleBettingWindows;
            table.BettingExpiresAt = null;
            ArmBettingWindow(table, afterRound: false);   // fresh window so seated players can bet again
        }

        /// <summary>
        /// The deal itself, with the table lock ALREADY HELD and no caller identity. Split out of
        /// <see cref="DealAsync"/> so the round-driver can start the round when the betting window expires —
        /// the auto-deal has no user to authorise, and the Redis table lock is not reentrant, so the driver
        /// cannot simply call the public method from inside its own tick.
        /// </summary>
        private async Task<BlackjackTable?> DealCoreAsync(BlackjackTable table, string tableId)
        {
            if (table.RoundInProgress)
                throw new InvalidOperationException("A round is already in progress.");

            if (!table.Game.Players.Any())
                throw new InvalidOperationException("No players seated.");

            // New round: refresh each player's mirror from the authoritative wallet and record the
            // round-start balance. The round's NET effect is reconciled back to the wallet at settle.
            table.CurrentRoundId = Guid.NewGuid().ToString("N");
            table.RoundNonce += 1;
            table.RoundStartBalance = new Dictionary<int, decimal>();
            table.LastResults = new List<SeatRoundResult>(); // new round — clear last round's result banner
            table.ActionLog = new List<GameActionEntry>();   // new round — start a fresh move log

            // Clock config is re-applied EVERY round, not just at table creation. A long-lived table (the round-driver
            // refreshes its Redis TTL each tick, so house tables never expire) would otherwise stay frozen on whatever
            // it was created with — e.g. the old 20s TurnDurationSeconds default — ignoring appsettings and admin
            // overrides forever. Re-reading here makes both live without recreating the table.
            table.TurnDurationSeconds = turnDurationSeconds;
            table.MaxPresentationSeconds = maxPresentationSeconds;
            table.PresentationPerCardSeconds = presentPerCardSeconds;
            table.BettingDurationSeconds = bettingDurationSeconds;
            table.MaxIdleBettingWindows = maxIdleBettingWindows;

            // The betting window is over — the round is starting. Cleared here (not only on the timer path) so a
            // human pressing Deal early also stops the clock, otherwise it would still be counting down mid-round.
            table.BettingExpiresAt = null;
            table.BetPresentAcked = false;

            // Defensive: tables created before provably-fair seeds existed get one lazily.
            if (string.IsNullOrEmpty(table.ServerSeed))
            {
                var sb = RandomNumberGenerator.GetBytes(32);
                table.ServerSeed = Convert.ToHexString(sb);
                table.ServerSeedHash = Convert.ToHexString(SHA256.HashData(sb)).ToLowerInvariant();
                if (string.IsNullOrEmpty(table.ClientSeed))
                    table.ClientSeed = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            }

            // Derive the provably-fair seed + shoe hash NOW, BEFORE any wallet debit. This is the throwable
            // work (a corrupt ServerSeed would make FromHexString throw); doing it first means a failure
            // here can never strand a stake that was already debited.
            var roundId = table.CurrentRoundId;

            // ---- SHOE lifecycle -------------------------------------------------------------------------------
            // Decided by ShoePlan.Decide (pure, unit-tested). NOTHING is written back to the table here: a deal can
            // still fail below (e.g. "No funded bets"), and committing a shoe that was never dealt would leave the
            // table pinned to a phantom shoe whose hash the audit would then certify against cards nobody saw.
            // The plan is applied only once DealNewGame has actually installed it.
            var shoePlan = ShoePlan.Decide(table, deckCount, shoePenetrationPercent, reshuffleEveryRound);
            int decks = shoePlan.Decks;

            byte[] roundSeed = ProvableShuffle.DeriveSeed(
                Convert.FromHexString(table.ServerSeed), table.ClientSeed, shoePlan.ShoeNonce);

            if (shoePlan.NewShoe)
            {
                var fresh = new Deck(decks);
                fresh.Shuffle(roundSeed);
                shoePlan.ShoeHash = fresh.ComputeHash();
            }
            var deckHash = shoePlan.ShoeHash;

            // Reserve each wager from the AUTHORITATIVE wallet NOW (debit-on-bet): the stake leaves the
            // wallet at deal, so the same chips can't be staked at another table/seat and a loss can never
            // overdraw at settle. A player whose debit fails (insufficient / already committed elsewhere)
            // SITS OUT this round rather than freezing settle later. Only players with a funded bet join
            // THIS round; anyone else (e.g. someone who sat down mid-round) waits for the next deal.
            var wagers = new Dictionary<int, decimal>();
            var stakeTxIds = new Dictionary<int, string>();   // seat -> the stake debit's wallet tx id (per-hand audit)
            var debited = new List<(string PlayerId, decimal Amount, int Seat, decimal GiftedSpent)>();
            foreach (var player in table.Game.Players)
            {
                var bet = player.Hands.Count > 0 ? player.Hands[0].Bet : 0m;
                if (bet <= 0) { player.InRound = false; continue; }

                try
                {
                    var (stkTx, walletAfter, stkGifted) = await DebitStakeAsync(player.Id, bet, table.TableId, roundId, player.SeatNumber, "stk");
                    player.InRound = true;
                    table.RoundStartBalance[player.SeatNumber] = walletAfter + bet; // pre-stake balance (audit)
                    player.SetBalance(walletAfter);                                  // mirror = wallet after the stake
                    wagers[player.SeatNumber] = bet;
                    stakeTxIds[player.SeatNumber] = stkTx;
                    debited.Add((player.Id, bet, player.SeatNumber, stkGifted));
                }
                catch (Exception ex)
                {
                    player.InRound = false;
                    player.ClearBet(0);
                    logger.LogWarning(ex, "Seat {Seat} sat out this round: stake debit failed for player {PlayerId} on table {TableId}",
                        player.SeatNumber, player.Id, table.TableId);
                }
            }

            if (wagers.Count == 0)
                throw new InvalidOperationException("No funded bets — at least one seated player must have chips to cover their bet.");

            // Stakes are now committed to the wallet. From here the deal must either complete and persist the
            // round, or REFUND every reserved stake — so a deal that throws after debiting can never strand
            // chips (e.g. a Redis/SignalR blip in SaveTableAsync).
            try
            {
                // newShoe: false keeps dealing from the PERSISTENT shoe; the block above already replaced it if this
                // round needed a fresh one (single deck, first deal, or the cut card was reached last round).
                table.Game.DealNewGame(roundSeed, decks, newShoe: shoePlan.NewShoe);

                // The shoe is now actually installed, so it is safe to commit its identity to the table. Doing this
                // BEFORE the deal would leave a failed deal pinned to a shoe that was never dealt.
                table.ShoeNonce = shoePlan.ShoeNonce;
                table.ShoeHash = shoePlan.ShoeHash;
                table.ShoeSize = shoePlan.ShoeSize;
                table.CutCardAt = shoePlan.CutCardAt;
                table.ShoeDealtAtRoundStart = shoePlan.ShoeDealtAtRoundStart;
                if (shoePlan.NewShoe)
                    logger.LogInformation("Table {TableId}: new {Decks}-deck shoe #{Nonce} ({Reason}); cut card at {Cut} cards left",
                        table.TableId, shoePlan.Decks, shoePlan.ShoeNonce, shoePlan.Reason, shoePlan.CutCardAt);

                ApplyDevDealerRig(table);        // DEV: force dealer cards for insurance testing (no-op unless armed)
                ApplyDevDealerTenUpRig(table);   // DEV: force a TEN up-card for PEEK testing (runs after, so it wins if both armed)
                ApplyDevPlayerPairRig(table);    // DEV: force a splittable player pair for split testing (no-op unless armed)
                table.CurrentDeckHash = deckHash;
                table.RoundStartedAt = DateTimeOffset.UtcNow;

                // Restore each reserved wager onto the freshly dealt hand. The stake already left the wallet
                // AND the mirror (mirror was set to the post-debit balance), so set the bet directly — do NOT
                // PlaceBet, which would deduct it from the mirror a second time.
                foreach (var player in table.Game.Players)
                {
                    if (wagers.TryGetValue(player.SeatNumber, out var bet) && bet > 0)
                    {
                        player.GetHand(0).Bet = bet;
                        player.ClearInsurance(0);
                        // Record the funding debit on the (freshly dealt) hand for the per-hand settle audit.
                        player.GetHand(0).StakeTxId = stakeTxIds.GetValueOrDefault(player.SeatNumber);
                        LogAction(table, HandActionType.Deal, player.SeatNumber, player.Id, amount: bet,
                            handValueAfter: player.GetHand(0).Hand.GetSumOfHand(),
                            cardDrawn: string.Join(" ", player.GetHand(0).Hand.Cards.Select(ProvableShuffle.Canonical)));
                    }
                }

                MarkNaturals(table);
                BeginPlayOrInsurance(table);
                table.RoundInProgress = true;
                await SaveTableAsync(tableId, table);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deal failed after reserving stakes on table {TableId} round {RoundId}; refunding {Count} stake(s).",
                    table.TableId, roundId, debited.Count);
                foreach (var d in debited)
                {
                    try { await RefundStakeAsync(d.PlayerId, d.Amount, table.TableId, roundId, d.Seat, d.GiftedSpent); }
                    catch (Exception rex) { logger.LogError(rex, "Stake refund FAILED for seat {Seat} player {PlayerId} round {RoundId} — needs reconciliation.", d.Seat, d.PlayerId, roundId); }
                }
                throw;
            }
            return table;
        }

        public async Task<(BlackjackTable? Table, HitResult? Result)> HitAsync(string tableId, string userId, int seatNumber, int handIndex = 0)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return (null, null);

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;
            if (player.HasBust(handIndex) || player.GetHand(handIndex).Done) throw new InvalidOperationException("Hand already finished.");

            var result = player.Hit(handIndex);
            LogAction(table, HandActionType.Hit, seatNumber, userId, handValueAfter: result.HandValue,
                cardDrawn: ProvableShuffle.Canonical(result.DrawnCard));
            // Bust ⇒ the hand is over. 21 ⇒ AUTO-STAND: there is nothing to gain by acting again (a hit can only
            // bust or, on a soft 21, hold the same total), so we don't hand the turn back and make the player
            // confirm a decision that has only one sensible answer. Both cases finish the hand and move on.
            if (result.IsBust || AutoStandOnTwentyOne(player, handIndex))
            {
                player.GetHand(handIndex).Done = true;
                AdvanceTurn(table);
            }
            else
            {
                RefreshTurn(table);
            }
            await SaveTableAsync(tableId, table);
            return (table, result);
        }

        public async Task<(BlackjackTable? Table, DoubleDownResult? Result)> DoubleDownAsync(string tableId, string userId, int seatNumber, int handIndex = 0)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return (null, null);

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;
            if (player.HasBust(handIndex) || player.GetHand(handIndex).Done) throw new InvalidOperationException("Hand already finished.");
            if (player.GetHand(handIndex).Hand.Cards.Count != 2) throw new InvalidOperationException("Double down only allowed on first action.");

            // Reserve the extra stake (equal to the current bet) from the wallet FIRST. If it fails
            // (insufficient / committed elsewhere) we throw before mutating the game — no rollback needed.
            var ddExtra = player.GetHand(handIndex).Bet;
            var (ddTx, ddWalletAfter, _) = await DebitStakeAsync(player.Id, ddExtra, table.TableId, table.CurrentRoundId ?? "", seatNumber, $"dd{handIndex}");

            var result = player.DoubleDown(handIndex);
            player.SetBalance(ddWalletAfter);
            // Append the double-down debit to this hand's funding trail (deal/split stake + this dd) for audit.
            var ddHand = player.GetHand(handIndex);
            ddHand.StakeTxId = string.IsNullOrEmpty(ddHand.StakeTxId) ? ddTx : ddHand.StakeTxId + "," + ddTx;
            LogAction(table, HandActionType.Double, seatNumber, userId, amount: ddExtra,
                handValueAfter: result.HitResult.HandValue, cardDrawn: ProvableShuffle.Canonical(result.HitResult.DrawnCard));
            AdvanceTurn(table);
            await SaveTableAsync(tableId, table);
            return (table, result);
        }

        public async Task<BlackjackTable?> PlaceInsuranceAsync(string tableId, string userId, int seatNumber, decimal amount, int handIndex = 0)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;
            if (!player.InRound) throw new InvalidOperationException("You are not in this round.");
            if (!table.InsuranceExpiresAt.HasValue) throw new InvalidOperationException("The insurance window has closed.");

            var insHand = player.GetHand(handIndex);
            if (insHand.InsuranceBet > 0) throw new InvalidOperationException("Insurance already placed.");
            // Insurance is a PRE-PLAY decision offered to EVERY dealt player the moment the dealer shows an Ace
            // — it is NOT turn-gated (multiplayer: all players decide before play). Allowed only while the hand
            // is untouched: still its two dealt cards and not yet acted.
            if (insHand.Hand.Cards.Count != 2 || insHand.Done)
                throw new InvalidOperationException("Insurance is only available before you act, on your first two cards.");

            var upCard = table.Game.Dealer.Hand.Cards.FirstOrDefault(c => c.IsCardUp);
            if (upCard == null || upCard.FaceVal != CardGames.Platforms.FaceValue.Ace)
                throw new InvalidOperationException("Insurance available only when dealer shows an Ace.");

            // Pre-validate the amount (mirrors Player.PlaceInsurance) so the wallet debit can't succeed and
            // then PlaceInsurance throw. Reserve wallet-first, then place.
            if (amount <= 0) throw new InvalidOperationException("Insurance must be positive.");
            if (amount > insHand.Bet / 2) throw new InvalidOperationException("Insurance cannot exceed half the bet.");

            var (_, insWalletAfter, _) = await DebitStakeAsync(player.Id, amount, table.TableId, table.CurrentRoundId ?? "", seatNumber, $"ins{handIndex}");
            player.PlaceInsurance(amount, handIndex);
            player.SetBalance(insWalletAfter);
            player.InsuranceDecided = true;
            LogAction(table, HandActionType.Insurance, seatNumber, userId, amount: amount);
            // Insurance neither consumes nor advances a turn, and a non-current player must NOT reset the
            // active player's turn timer — so the turn state is intentionally left untouched here. Close the
            // insurance phase early if this was the last undecided player → play starts immediately.
            MaybeCloseInsurance(table);
            await SaveTableAsync(tableId, table);
            return table;
        }

        /// <summary>
        /// Records that a player declines insurance during the insurance phase (the NO button). No money moves;
        /// it just marks them decided so the phase can close early once everyone has decided. No-op if the
        /// window isn't open.
        /// </summary>
        public async Task<BlackjackTable?> DeclineInsuranceAsync(string tableId, string userId, int seatNumber)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;
            if (!table.RoundInProgress || !table.InsuranceExpiresAt.HasValue) return table; // window closed → no-op

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            seat.Player.InsuranceDecided = true;
            MaybeCloseInsurance(table);   // if that was the last undecided player, start play now
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> SplitAsync(string tableId, string userId, int seatNumber, int handIndex = 0)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;
            // Pre-validate (mirrors Player.Split's guards) so the wallet debit can't succeed and then Split
            // throw, which would leave the wallet debited with no split.
            var splitHand = player.GetHand(handIndex);
            if (splitHand.Hand.Cards.Count != 2)
                throw new InvalidOperationException("Can only split with two cards.");
            if (!Player.CanSplitPair(splitHand.Hand.Cards[0], splitHand.Hand.Cards[1]))
                throw new InvalidOperationException("Cards must be a pair (equal value) to split.");

            // Reserve the extra stake (a second bet for the new hand) wallet-first, then split. Key the stake
            // debit on the NEW hand's index (= current hand count, since Split appends), NOT the source
            // handIndex: re-splitting the same hand would otherwise reuse `sp{handIndex}`, the wallet would
            // treat the second debit as an idempotent duplicate and skip it, and the player would get an
            // UNFUNDED extra hand. The new-hand index strictly increases per split, so every split stake is
            // unique and funded.
            var splitExtra = splitHand.Bet;
            var newHandIndex = player.Hands.Count;
            var (spTx, splitWalletAfter, _) = await DebitStakeAsync(player.Id, splitExtra, table.TableId, table.CurrentRoundId ?? "", seatNumber, $"sp{newHandIndex}");

            var splitIndex = player.Split(handIndex);        // appends at newHandIndex
            player.GetHand(splitIndex).StakeTxId = spTx;     // the split stake funded the NEW hand
            player.SetBalance(splitWalletAfter);
            RigResplitForDev(player, handIndex, splitIndex); // DEV: make the result re-splittable (no-op unless armed)
            LogAction(table, HandActionType.Split, seatNumber, userId, amount: splitExtra,
                handValueAfter: player.GetHand(handIndex).Hand.GetSumOfHand());

            // A split hand dealt straight to 21 (e.g. splitting 10s into 10+A) auto-stands, same as hitting to 21 —
            // it's an ordinary 21, not a natural, but there's still nothing to decide. Applied to BOTH hands so the
            // turn engine skips them; the check below then advances off the player if the current hand is finished.
            AutoStandOnTwentyOne(player, handIndex);
            AutoStandOnTwentyOne(player, splitIndex);

            // Hand the turn to whichever of THIS seat's hands canonical order says acts first. After a split that is
            // the RIGHT-hand position — i.e. the hand just created, NOT the hand that was split — so we can't simply
            // keep the turn on `handIndex` any more. Asking GetOrderedHands rather than assuming an index keeps this
            // correct whichever direction that ordering runs, and it already skips the hands AutoStandOnTwentyOne
            // just finished. If neither hand can act (split aces, or both auto-stood on 21) the seat is done.
            int? firstHandThisSeat = null;
            foreach (var h in GetOrderedHands(table))
            {
                if (h.seat != seatNumber) continue;
                firstHandThisSeat = h.hand;
                break;
            }

            if (firstHandThisSeat.HasValue)
            {
                table.CurrentSeatNumber = seatNumber;
                table.CurrentHandIndex = firstHandThisSeat.Value;
                StampTurn(table);
            }
            else
            {
                AdvanceTurn(table);
            }

            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> DealerPlayAndSettleAsync(string tableId, string userId)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            if (table.InsuranceExpiresAt.HasValue)
                throw new InvalidOperationException("Insurance is still open; the round isn't ready to settle.");

            // Only a seated player may trigger settle — no unseated user can force-settle/grief a table.
            if (!table.Seats.Any(s => s.Player != null && s.Player.Id == userId))
                throw new InvalidOperationException("You are not seated at this table.");

            // EVERY player hand must be resolved first. CurrentSeatNumber == -1 is the engine's "no live turn left"
            // sentinel (AdvanceTurn parks it there once GetOrderedHands is exhausted) — the same condition the client
            // uses to expose the button. Without this a player could call /dealerPlay during ANOTHER seat's turn and
            // settle the round out from under them: their hand would be scored wherever it stood, with no chance to act.
            if (table.CurrentSeatNumber != -1)
                throw new InvalidOperationException("Players are still acting; the round isn't ready to settle.");

            return await SettleInternalAsync(table, tableId);
        }

        /// <summary>
        /// Dealer-plays the round and settles every seat to the wallet, then tears the round down. The
        /// CALLER must already hold the table lock. Shared by the user-triggered DealerPlayAndSettleAsync
        /// (after seat-auth) and by the round-driver (system-triggered, no user).
        /// </summary>
        private async Task<BlackjackTable> SettleInternalAsync(BlackjackTable table, string tableId)
        {
            // Single-shot: claim the round so a raced/retried settle can't re-enter (which would
            // double-count stats + leaderboards and insert a duplicate audit row). Auto-expires so a crash
            // can't permanently wedge the round.
            var settleRoundId = table.CurrentRoundId ?? "";
            if (!await redisService.GetDatabase().StringSetAsync(
                    $"bjr:settling:{settleRoundId}", "1", TimeSpan.FromSeconds(120), When.NotExists))
                return table; // another settle for this round is already in flight

            // Resolve any still-pending player turns (auto-stand them) before the dealer plays, so a
            // dealerPlay call can't settle other seats before they've acted.
            int guard = 0;
            while (table.CurrentSeatNumber > 0 && guard++ < 64)
            {
                AdvanceTurn(table);
            }

            table.Game.DealerPlay();
            LogAction(table, HandActionType.DealerPlay, null, null,
                handValueAfter: table.Game.Dealer.Hand.GetSumOfHand(),
                cardDrawn: string.Join(" ", table.Game.Dealer.Hand.Cards.Select(ProvableShuffle.Canonical)));

            // Capture gross wager + final hand state per seat BEFORE settle zeroes the bets.
            var preSettle = table.Game.Players.ToDictionary(
                p => p.SeatNumber,
                p => (
                    Wagered: p.Hands.Sum(h => h.Bet + h.InsuranceBet),
                    FinalValue: p.Hands[0].Hand.GetSumOfHand(),
                    Bust: p.Hands.Any(h => h.Hand.GetSumOfHand() > 21),
                    Blackjack: p.HasBlackJack(0)
                ));

            // Mirror balance per seat BEFORE settle returns any money — the baseline for the gross credit.
            var preSettleBalance = table.Game.Players.ToDictionary(p => p.SeatNumber, p => p.Balance);

            // Settle decides each hand's outcome and applies payouts to the mirror; capture the per-hand
            // results so the audit can record one row per (split) hand.
            var handSettlements = BlackjackSettlement.Settle(table.Game);
            var settledBySeat = handSettlements
                .GroupBy(h => h.SeatNumber)
                .ToDictionary(g => g.Key, g => g.OrderBy(h => h.HandIndex).ToList());

            var roundId = table.CurrentRoundId ?? "";
            var participants = new List<GameHandParticipant>();
            var statResults = new List<RoundResult>();
            var lastResults = new List<SeatRoundResult>();

            // Reconcile each player's NET result to the authoritative wallet, sync the mirror, audit it.
            // try/finally guarantees the round always tears down + saves, so one seat's wallet failure
            // (an overdraw from concurrent multi-table play, or a locked wallet) can never leave the table
            // frozen at RoundInProgress=true.
            try
            {
                foreach (var player in table.Game.Players)
                {
                    if (!player.InRound) continue; // waiting players didn't play this round — no settle/audit

                    // Pre-stake wallet balance (audit BalanceBefore); the stakes already left the wallet.
                    var start = table.RoundStartBalance != null
                                && table.RoundStartBalance.TryGetValue(player.SeatNumber, out var s)
                        ? s
                        : player.Balance;
                    Guid.TryParse(player.Id, out var uid);

                    var pre = preSettle.TryGetValue(player.SeatNumber, out var ps)
                        ? ps
                        : (Wagered: 0m, FinalValue: 0, Bust: false, Blackjack: false);

                    // RULE-DERIVED payout is the payer: sum each hand's GrossReturn from the explicit rule
                    // table (BlackjackSettlement). The engine mirror delta is only a tripwire — if the two
                    // disagree, settlement math has drifted (a future AddWin/multiplier bug, a side bet); we
                    // flag it loudly and still credit the rule value.
                    var seatHands = settledBySeat.TryGetValue(player.SeatNumber, out var shs)
                        ? shs : new List<HandSettlement>();
                    var preSettleBal = preSettleBalance.TryGetValue(player.SeatNumber, out var pb) ? pb : player.Balance;
                    var computed = seatHands.Sum(h => h.GrossReturn);   // rule-derived gross (the credit)
                    var mirrorDelta = player.Balance - preSettleBal;    // engine mirror delta (the tripwire)
                    var (gross, payoutMismatch) = BlackjackSettlement.ReconcilePayout(computed, mirrorDelta);
                    if (payoutMismatch)
                        logger.LogError("Settle payout MISMATCH table {TableId} round {RoundId} seat {Seat}: rule-computed {Computed} != engine-mirror {Mirror}. Crediting the rule value.",
                            table.TableId, roundId, player.SeatNumber, computed, mirrorDelta);
                    // RULE-DERIVED stake, to match the rule-derived gross above. This used to be `start - preSettleBal`
                    // — a subtraction of two balances captured at different moments from DIFFERENT sources (`start` is
                    // the round-start WALLET balance, `preSettleBal` the engine MIRROR). Any drift between wallet and
                    // mirror (a concurrent multi-table debit, a failed credit, a mid-round leave) silently corrupted
                    // `net`, and `net` is what decides Outcome and Delta — i.e. what the client BANNER announces and
                    // what the pay/collect choreography animates. HandSettlement.Stake already includes a double's
                    // extra, and GrossReturn already includes insurance, so summing both keeps the two sides consistent.
                    var totalStaked = seatHands.Sum(h => h.Stake + h.InsuranceStake);
                    var net = gross - totalStaked;                      // true round net (gross minus staked)

                    // Capture this seat's result for the board's banner — decided by the settle math, so
                    // it's recorded whether or not the wallet credit below succeeds.
                    lastResults.Add(new SeatRoundResult
                    {
                        SeatNumber = player.SeatNumber,
                        Outcome = net > 0 ? "win" : net < 0 ? "lose" : "push",
                        Delta = net,
                        Payout = gross,
                        FinalHandValue = pre.FinalValue,
                        Bust = pre.Bust,
                        Blackjack = pre.Blackjack,
                        // Per-hand results (ordered by hand index) so the client can label AND pay/collect each split
                        // hand on its own — the seat-level Outcome/Delta above is a NET, which would call a mixed
                        // win/loss a "push" and move no chips. seatHands is already ordered by HandIndex, so this
                        // aligns 1:1 with the client's hands[] order. Stake mirrors `totalStaked`'s definition and
                        // Payout mirrors `gross`, so these per-hand values sum exactly to the seat's Delta.
                        Hands = seatHands.Select(h => new HandRoundResult
                        {
                            HandIndex = h.HandIndex,
                            Outcome = h.OutcomeCode,
                            Stake = h.Stake + h.InsuranceStake,
                            Payout = h.GrossReturn,
                            Delta = h.GrossReturn - (h.Stake + h.InsuranceStake),
                            InsuranceBet = h.InsuranceStake,
                            InsuranceReturn = h.Insurance == InsuranceResult.Win
                                ? h.InsuranceStake * BlackjackSettlement.InsuranceWinMultiplier
                                : 0m
                        }).ToList()
                    });

                    // Game-extension layer (gifted-taint + XP): when OFF, the wallet is a pure ledger and there is
                    // no XP, so skip the split entirely. When ON, scope to THIS seat (no cross-seat pooling) so the
                    // payout keeps the stake's gifted fraction and the XP basis is the EARNED, insurance-excluded stake.
                    decimal giftedCredit = 0m, cleanWager = 0m;
                    if (progressionEnabled)
                    {
                        var stakeSplit = await GetSeatStakeSplitAsync(player.Id, roundId, player.SeatNumber);
                        giftedCredit = stakeSplit.TotalStake > 0m
                            ? Math.Round(gross * (stakeSplit.GiftedStake / stakeSplit.TotalStake), 4)
                            : 0m;
                        cleanWager = stakeSplit.MainStake - stakeSplit.MainGiftedStake;
                    }

                    try
                    {
                        decimal newBalance;
                        string txId = null;
                        if (gross > 0m)
                        {
                            (txId, newBalance) = await CreditGrossWithRetryAsync(player.Id, gross, table.TableId, roundId, player.SeatNumber, giftedCredit);
                        }
                        else
                        {
                            newBalance = await GetWalletChipsAsync(player.Id); // nothing returned (full loss)
                        }
                        player.SetBalance(newBalance);

                        // One audit row PER HAND (a split yields two): each hand's own stake, rule-derived
                        // payout, and funding-debit tx id. The payout credit is a single seat-level :pay
                        // transaction, so its tx id + the BalanceBefore/After audit are recorded once, on hand 0.
                        for (int hi = 0; hi < seatHands.Count; hi++)
                        {
                            var h = seatHands[hi];
                            participants.Add(new GameHandParticipant
                            {
                                UserId = uid,
                                SeatNumber = player.SeatNumber,
                                HandIndex = h.HandIndex,
                                Bet = h.Stake,
                                InsuranceBet = h.InsuranceStake,
                                Payout = h.GrossReturn,                             // rule-derived gross for THIS hand
                                FinalHandValue = h.FinalValue,
                                Bust = h.Bust,
                                Blackjack = h.Blackjack,
                                Outcome = h.OutcomeCode,
                                WalletDebitTxId = player.GetHand(h.HandIndex).StakeTxId,
                                WalletCreditTxId = hi == 0 && gross > 0m ? txId : null,  // credit is seat-level (:pay)
                                BalanceBefore = hi == 0 ? start : (decimal?)null,
                                BalanceAfter = hi == 0 ? newBalance : (decimal?)null
                            });
                        }

                        // Tripwire row: the rule-derived payout disagreed with the engine mirror. Money is
                        // correct (we credited the rule value); record a scannable settle_mismatch marker
                        // (HandIndex -1, Payout 0 so Σ Payout stays reconcilable) for ops/reconciliation.
                        if (payoutMismatch)
                            participants.Add(new GameHandParticipant
                            {
                                UserId = uid,
                                SeatNumber = player.SeatNumber,
                                HandIndex = -1,
                                Outcome = "settle_mismatch",
                                Bet = 0m,
                                Payout = 0m,
                                BalanceBefore = start,
                                BalanceAfter = newBalance,
                                MetadataJson = JsonSerializer.Serialize(new { roundId, seat = player.SeatNumber, computed, mirrorDelta })
                            });
                    }
                    catch (Exception ex)
                    {
                        // The payout credit still failed after inline retries (persistently locked wallet /
                        // outage). Stakes already left the wallet and the payout is idempotent on its :pay key,
                        // so the seat is flagged settle_failed for reconciliation rather than double-paid.
                        // Overdraw-at-settle is no longer possible (funds were reserved at deal).
                        logger.LogError(ex, "Settle payout failed for seat {Seat} on table {TableId} after retries", player.SeatNumber, table.TableId);
                        var failHands = settledBySeat.TryGetValue(player.SeatNumber, out var fhs)
                            ? fhs : new List<HandSettlement>();
                        if (failHands.Count == 0)
                        {
                            participants.Add(new GameHandParticipant
                            {
                                UserId = uid,
                                SeatNumber = player.SeatNumber,
                                HandIndex = -2,                                   // aggregate marker (defensive path: no per-hand settlements), distinct from real hands (≥0) and the mismatch marker (-1)
                                Bet = pre.Wagered,
                                Payout = gross,                                   // owed gross, persisted so the sweeper can heal it
                                FinalHandValue = pre.FinalValue,
                                Bust = pre.Bust,
                                Blackjack = pre.Blackjack,
                                Outcome = "settle_failed",
                                BalanceBefore = start
                            });
                        }
                        else
                        {
                            for (int hi = 0; hi < failHands.Count; hi++)
                            {
                                var h = failHands[hi];
                                participants.Add(new GameHandParticipant
                                {
                                    UserId = uid,
                                    SeatNumber = player.SeatNumber,
                                    HandIndex = h.HandIndex,
                                    Bet = h.Stake,
                                    InsuranceBet = h.InsuranceStake,
                                    Payout = h.GrossReturn,                       // owed gross, persisted so the sweeper can heal it
                                    FinalHandValue = h.FinalValue,
                                    Bust = h.Bust,
                                    Blackjack = h.Blackjack,
                                    Outcome = "settle_failed",
                                    WalletDebitTxId = player.GetHand(h.HandIndex).StakeTxId,
                                    BalanceBefore = hi == 0 ? start : (decimal?)null
                                });
                            }
                        }
                    }

                    // Progression XP + the stats roll-up run whether or not the payout credit succeeded — a
                    // settle_failed seat is still money-healed by reconciliation and it DID play the round.
                    // Accrual is idempotent + best-effort; stats record games/net regardless. Gated by the flag.
                    long grantedXp = progressionEnabled
                        ? await AccrueProgressionAsync(uid, cleanWager, net > 0m, roundId)
                        : 0L;
                    var statCounters = BlackjackStatCounters.ForSeat(seatHands);   // game-specific lifetime counters (blackjacks/doubles/busts/…)
                    statResults.Add(new RoundResult(uid, pre.Wagered, net, cleanWager, grantedXp, statCounters));
                    // VIP Status Points (flat ×1, never from winnings) — best-effort, idempotent per (round, user).
                    if (progressionEnabled) await AccrueVipAsync(uid, cleanWager, roundId);
                    // Loyalty Points (clean wager × VIP multiplier) — best-effort, idempotent per (round, user).
                    if (progressionEnabled) await AccrueLoyaltyAsync(uid, cleanWager, roundId);
                    // Daily-mission progress (round + clean wager + outcomes) — idempotent per (round, user).
                    if (progressionEnabled) await AccrueMissionsAsync(uid, statCounters, cleanWager, roundId);
                }

                // The money credits above are idempotent (per-seat :pay key), but PersistHand and RecordStats
                // are NOT. Each owns its OWN at-most-once guard so a crash between them only re-runs the
                // UNFINISHED one — under the old single guard, completing the audit then crashing would skip the
                // retry's stats forever. (Cleaner long-term: a DB unique index on GameHandHeader.RoundId makes
                // the audit insert idempotent so its guard could be claimed AFTER success instead of before.)
                var settleRdb = redisService.GetDatabase();
                if (await settleRdb.StringSetAsync($"bjr:audited:{roundId}", "1", TimeSpan.FromHours(1), When.NotExists))
                    await PersistHandAsync(table, roundId, participants);
                if (await settleRdb.StringSetAsync($"bjr:stats:{roundId}", "1", TimeSpan.FromHours(1), When.NotExists))
                    await RecordStatsAsync(statResults);
            }
            finally
            {
                // PERMANENT settled marker. SettlementReconciliationService's orphan-stake refund skips a round when
                // `bjr:settling:` OR `bjr:settled:` exists — but nothing ever wrote `bjr:settled:`, and `bjr:settling:`
                // expires after 120s. So once that TTL lapsed, a round whose credits had ALREADY committed but whose
                // header never persisted (a DB/Redis failure in PersistHandAsync) looked unsettled, and the orphan
                // sweep would refund stakes on top of the payout.
                //
                // In `finally`, so it lands on the throwing paths too — those are exactly the ones the 120s window
                // used to lose. It is deliberately written AFTER the credit loop: marking earlier would suppress the
                // refund for a round that died BEFORE paying its winners, which is the case the sweep exists for.
                // A hard process kill mid-settle still skips this (no finally runs) and remains covered only by the
                // 120s `bjr:settling` marker — the known, deferred process-death window.
                //
                // 7 days: the sweep's query has no lower time bound, so the guard must outlive any round it can reach.
                if (!string.IsNullOrEmpty(settleRoundId))
                    await redisService.GetDatabase().StringSetAsync(
                        $"bjr:settled:{settleRoundId}", "1", TimeSpan.FromDays(7));

                // Always finish the round so a seat failure can never wedge the table.
                table.RoundStartBalance?.Clear();
                table.CurrentRoundId = null;
                table.CurrentDeckHash = null;
                table.RoundInProgress = false;
                // InRound now means "participating in the CURRENT round". Clear it here so between rounds every seat
                // reads false (a spectator/waiter and a just-finished player are indistinguishable once the round is
                // over), and the next deal re-sets it for whoever funds a bet. The client uses this for the
                // "waiting for next round" panel and the leave-button lock, both of which must be false between rounds.
                foreach (var p in table.Game.Players) { p.InRound = false; p.BetThisWindow = false; }   // fresh betting window next round
                table.LastResults = lastResults;   // surface per-seat outcomes to the board for the banner
                table.BettingDurationSeconds = bettingDurationSeconds;  // re-read config each round, like the turn clock
                table.MaxIdleBettingWindows = maxIdleBettingWindows;    // snapshot for the board's idle-kick warning
                ArmBettingWindow(table, afterRound: true);              // bets are open for the next round
                await SaveTableAsync(tableId, table);
            }
            return table;
        }

        public async Task<BlackjackTable?> StandAsync(string tableId, string userId, int seatNumber, int handIndex = 0)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            seat.Player.Stand(handIndex);
            LogAction(table, HandActionType.Stand, seatNumber, userId,
                handValueAfter: seat.Player.GetHand(handIndex).Hand.GetSumOfHand());
            AdvanceTurn(table);
            await SaveTableAsync(tableId, table);
            return table;
        }

        /// <summary>
        /// PRESENTATION HANDSHAKE. The current player's client calls this the moment it has finished animating the deal
        /// (or the drawn card) and the player can actually act. The turn deadline — stamped generously as
        /// (max-presentation + turn) so a slow client is never cut off mid-animation — collapses to the REAL turn
        /// length from NOW. So the decision clock is always the full configured turn no matter how long that client's
        /// deal-in took, with nothing to hand-tune.
        ///
        /// Cheat-safe: the new deadline is only ever accepted if it is EARLIER than the standing ceiling, so calling
        /// late (or not at all) can only LOSE time, never gain it. Idempotent per turn via <c>TurnPresentAcked</c>, and
        /// ignored unless it really is this caller's seat's turn.
        /// </summary>
        public async Task<BlackjackTable?> PresentedAsync(string tableId, string userId, int seatNumber)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat?.Player == null || seat.Player.Id != userId) return table;   // seat not held by this caller

            // BETWEEN ROUNDS the same handshake collapses the BETTING ceiling: the client calls this once its round-end
            // ceremony has finished, and the betting window shrinks to its real length from that moment — so players
            // always get the full window to bet in, however long their device took to animate the payout.
            if (!table.RoundInProgress)
            {
                if (table.BetPresentAcked || !table.BettingExpiresAt.HasValue) return table;   // idempotent / no window
                if (!table.BetPresentAckedSeats.Contains(seatNumber))
                    table.BetPresentAckedSeats.Add(seatNumber);

                // Wait for EVERY seat that actually played the round to finish its ceremony before collapsing.
                // Collapsing on the first ack would let one fast phone cut short a slower phone's betting time — the
                // ack is "I've finished animating", and the whole point of the window is that everyone gets the full
                // length once they can see the felt. Seats that joined after the round (InRound == false) have no
                // ceremony to play, so they're not waited on; if a client never acks at all, the generous ceiling
                // stands and the window simply runs long — never short.
                var owed = table.Seats.Where(s => s.Player is { InRound: true }).Select(s => s.SeatNumber);
                if (!owed.All(table.BetPresentAckedSeats.Contains)) { await SaveTableAsync(tableId, table); return table; }

                table.BetPresentAcked = true;
                var betCollapsed = DateTimeOffset.UtcNow.AddSeconds(table.BettingDurationSeconds);
                if (betCollapsed < table.BettingExpiresAt.Value)
                    table.BettingExpiresAt = betCollapsed;         // never EXTEND past the ceiling
                await SaveTableAsync(tableId, table);
                return table;
            }

            if (table.TurnPresentAcked) return table;              // already collapsed this turn (idempotent)
            if (table.CurrentSeatNumber != seatNumber) return table; // not this seat's turn — ignore, don't throw

            table.TurnPresentAcked = true;
            var collapsed = DateTimeOffset.UtcNow.AddSeconds(table.TurnDurationSeconds);
            if (!table.TurnExpiresAt.HasValue || collapsed < table.TurnExpiresAt.Value)
                table.TurnExpiresAt = collapsed;                   // never EXTEND past the ceiling

            await SaveTableAsync(tableId, table);
            return table;
        }

        /// <summary>
        /// Server round-driver tick for one table: if the current player's turn timer has expired, auto-stand
        /// it and advance; once all player turns are resolved, dealer-play + settle. Lets an idle table finish
        /// its round on its own (the lazy timeout in EnsureTurn only fires when the NEXT action arrives).
        /// Takes the table lock, so it's safe against a concurrent player action.
        /// </summary>
        public async Task TickTableAsync(string tableId)
        {
            // Read the table ONCE, and decide whether it is worth understanding before paying to understand it.
            //
            // This runs for every table in the lobby every couple of seconds. Fully deserializing one rebuilds its
            // entire shoe — 312 cards — plus every seat and hand, and a lobby is now dozens of tables rather than
            // three. Doing that for tables nobody is sitting at made the driver loop overrun its own tick, and a
            // driver that never finishes is holding table locks while players are waiting on them: bets and deals
            // then stall for seconds for no visible reason.
            var db0 = redisService.GetDatabase();
            var json = await db0.StringGetAsync(GetKey(tableId));
            if (json.IsNullOrEmpty)
            {
                // Prune ids whose table key has TTL-expired so the driver stops re-probing them forever.
                await db0.SetRemoveAsync(LobbyIndexKey, tableId);
                return;
            }

            // Keep every lobby table alive while the server runs (see the TTL note below) — this must happen even
            // for the idle tables we are about to skip, since that is exactly what keeps them in the lobby.
            await db0.KeyExpireAsync(GetKey(tableId), TimeSpan.FromHours(2));

            // The cheap question first: is anyone here? An empty table with no round has nothing to drive — no
            // turn to expire, no window to close, no seat to sweep — so it costs one lightweight parse and stops.
            TableLite lite = null;
            try { lite = JsonSerializer.Deserialize<TableLite>(json); } catch { }
            if (lite != null)
            {
                bool anyoneSeated = lite.Seats?.Any(s => s.Player != null) ?? false;
                if (!anyoneSeated && !lite.RoundInProgress) return;
            }

            var peek = ParseTable(json);
            if (peek == null)
            {
                await db0.SetRemoveAsync(LobbyIndexKey, tableId);
                return;
            }
            // (TTL note: an IDLE table is never re-saved — the fast-path returns without a write and reads don't
            // extend a TTL — so without the KeyExpire above its 2h key lapses and it silently vanishes from the
            // lobby, leaving only the table being actively played.)
            //
            // Idle fast-path: only take the lock when there's a round in progress OR a seated player's
            // connection state has drifted (needs a flag update or stalled-removal). Idle, all-fresh tables
            // stay lock-free.
            // BettingExpiresAt must be part of this test: an armed window is the one thing a fully-idle, all-fresh
            // table is still waiting on, and without it the fast-path would return before the auto-deal could fire.
            bool bettingDue = !peek.RoundInProgress && peek.BettingExpiresAt.HasValue
                              && DateTimeOffset.UtcNow >= peek.BettingExpiresAt.Value;
            // Everyone seated has actively bet → the round can start before the window expires, so don't fast-path out.
            bool allBetDue = !peek.RoundInProgress && AllSeatedActivelyBet(peek);
            if (!peek.RoundInProgress && !bettingDue && !allBetDue && !AnySeatNeedsSweep(peek)) return;

            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);       // authoritative re-read under the lock
            if (table == null) return;

            // STALLED-SEAT SWEEP — runs whether or not a round is in progress, so §5's between-rounds removal
            // happens here too. Derives IsConnected/IsStalled from heartbeat freshness and frees seats that are
            // stalled AND money-safe to remove; a stalled in-round player with a live stake is left to the
            // existing auto-stand → settle and removed on a later tick once the round has ended.
            var changed = SweepStalledSeats(table);

            // Between rounds the only clock is the BETTING window. When it expires the round starts on whatever bets
            // are down — nobody has to press Deal, so one player can't hold the table hostage by never starting it.
            if (!table.RoundInProgress)
            {
                // Everyone seated has ACTIVELY bet this window → start the round now instead of waiting out the rest of
                // it. The last player to press Deal usually triggers this via DealAsync; this backstops the case where
                // the final bet came in without a deal tap (or a stalled seat was just swept, unblocking all-bet).
                if (AllSeatedActivelyBet(table))
                {
                    try { await DealCoreAsync(table, tableId); }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "All-bet auto-deal failed on table {TableId}", tableId);
                        DisarmAfterFailedDeal(table);   // clear BetThisWindow + re-arm, or all-bet re-fires every tick
                        await SaveTableAsync(tableId, table);
                    }
                    return;
                }

                if (table.BettingExpiresAt.HasValue && DateTimeOffset.UtcNow >= table.BettingExpiresAt.Value)
                {
                    // The window closed. Tally who bet: reset the idle counter for every funded seat, increment it for
                    // every seat that sat this window out, and EVICT any seat that has now missed too many in a row.
                    // This is the anti-squat rule the heartbeat reaper can't provide (a connected client pings forever).
                    changed |= SweepIdleBettors(table);

                    // No funded bet ⇒ nothing to deal. Keep the window CYCLING while players are still seated so their
                    // idle counters keep climbing toward eviction; go fully idle only once the table has emptied.
                    if (!table.Game.Players.Any(p => p.Hands.Count > 0 && p.Hands[0].Bet > 0))
                    {
                        table.BettingDurationSeconds = bettingDurationSeconds;
                        table.MaxIdleBettingWindows = maxIdleBettingWindows;
                        table.BettingExpiresAt = null;                 // clear so ArmBettingWindow re-stamps a fresh window
                        ArmBettingWindow(table, afterRound: false);    // stays null if no seated players remain
                        await SaveTableAsync(tableId, table);
                        return;
                    }

                    // DealCore saves on success and refunds every reserved stake on failure. A throw here must not
                    // escape into the driver loop (it would skip the remaining tables), and it must not leave the
                    // window armed either — that would retry the deal every tick.
                    try { await DealCoreAsync(table, tableId); }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Auto-deal at betting-window expiry failed on table {TableId}", tableId);
                        DisarmAfterFailedDeal(table);   // clear BetThisWindow + re-arm so a persistent failure can't loop
                        await SaveTableAsync(tableId, table);
                    }
                    return;
                }

                if (changed) await SaveTableAsync(tableId, table);
                return;
            }

            // INSURANCE phase: hold play (and settlement) until its own timer expires or everyone has decided,
            // THEN start play. This MUST return before the settle-on-(-1) logic below, or the driver would
            // settle the round during the insurance window (CurrentSeatNumber is -1 here too).
            if (table.InsuranceExpiresAt.HasValue)
            {
                if (DateTimeOffset.UtcNow >= table.InsuranceExpiresAt.Value || AllInsuranceDecided(table))
                {
                    CloseInsurancePhase(table);   // dealer peek inside: -1 (settle) on dealer BJ, else first turn
                    if (table.CurrentSeatNumber == -1)
                    {
                        await SettleInternalAsync(table, tableId);  // dealer blackjack → settle now, no player turns
                        return;
                    }
                    await SaveTableAsync(tableId, table);
                    return;
                }
                if (changed) await SaveTableAsync(tableId, table);   // insurance window still open — persist any sweep changes
                return;
            }

            // Auto-stand the current player when their turn timer expires — OR when their seat is already flagged
            // stalled (the sweep above marked them): a known-disconnected player shouldn't hold the table for the
            // full turn timer. They still settle normally below (their InRound stake is honoured), just sooner.
            bool currentStalled = table.CurrentSeatNumber > 0
                && (table.Seats.FirstOrDefault(s => s.SeatNumber == table.CurrentSeatNumber)?.IsStalled ?? false);
            if (table.CurrentSeatNumber > 0
                && ((table.TurnExpiresAt.HasValue && DateTimeOffset.UtcNow > table.TurnExpiresAt.Value) || currentStalled))
            {
                AutoStand(table);
                changed = true;
            }

            // All player hands resolved → finish the round (dealer plays + settle). SettleInternal saves.
            if (table.CurrentSeatNumber == -1)
            {
                await SettleInternalAsync(table, tableId);
                return;
            }

            if (changed) await SaveTableAsync(tableId, table);
        }

        /// <summary>The ids of all tables in the lobby index — the round-driver ticks each one.</summary>
        public async Task<IReadOnlyList<string>> GetActiveTableIdsAsync()
        {
            var ids = await redisService.GetDatabase().SetMembersAsync(LobbyIndexKey);
            return ids.Select(v => v.ToString()).ToList();
        }

        // Unlocked, in-memory check for the tick fast-path: does any seated player's derived connection state
        // differ from what's stored, or is anyone stalled (i.e. is a removal pending)? If so the tick takes the
        // lock and sweeps. Mirrors SweepStalledSeats' thresholds so the two never disagree.
        private bool AnySeatNeedsSweep(BlackjackTable table)
        {
            var now = DateTime.UtcNow;
            foreach (var seat in table.Seats)
            {
                if (seat.Player == null) continue;
                var age = (now - seat.LastHeartbeatAt).TotalSeconds;
                bool connected = age <= disconnectGraceSeconds;
                bool stalled = age > stalledTimeoutSeconds;
                if (seat.IsConnected != connected || seat.IsStalled != stalled || stalled) return true;
            }
            return false;
        }

        /// <summary>
        /// Stalled-player reaper (runs under the table lock from TickTableAsync). For each seated player: derive
        /// IsConnected (heartbeat within DisconnectGrace) + IsStalled (no heartbeat past StalledTimeout) from the
        /// last heartbeat, then enforce §5 money-safety:
        ///  • stalled WITH a live debited stake mid-round → DO NOT remove. Leave the seat; the existing
        ///    auto-stand plays the hand out, it settles normally (wallet idempotent on bjr:{round}:{seat}:pay),
        ///    and a later tick removes the now-stake-free seat between rounds.
        ///  • stalled with NO live stake (between rounds, or seated mid-round but not InRound) → remove now.
        /// Returns true if anything changed so the caller saves + broadcasts.
        /// </summary>
        private bool SweepStalledSeats(BlackjackTable table)
        {
            var now = DateTime.UtcNow;
            bool changed = false;
            List<int> toRemove = null;

            foreach (var seat in table.Seats)
            {
                if (seat.Player == null) continue;

                var age = (now - seat.LastHeartbeatAt).TotalSeconds;
                bool connected = age <= disconnectGraceSeconds;
                bool stalled = age > stalledTimeoutSeconds;

                if (seat.IsConnected != connected) { seat.IsConnected = connected; changed = true; }
                if (seat.IsStalled != stalled)     { seat.IsStalled = stalled;     changed = true; }

                if (!stalled) continue;

                // §5: a live debited stake is NEVER pulled mid-round — auto-stand + settle handle it first.
                if (table.RoundInProgress && seat.Player.InRound) continue;

                (toRemove ??= new List<int>()).Add(seat.SeatNumber);
            }

            if (toRemove != null)
            {
                foreach (var sn in toRemove)
                {
                    logger.LogInformation("Reaping stalled seat {Seat} on table {TableId} (no heartbeat).", sn, table.TableId);
                    RemoveSeatCore(table, sn);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Called when a betting window CLOSES: reconcile each seat's idle-eviction counter and evict the squatters.
        /// A seat with a funded bet resets to 0; a seat that sat the window out is incremented; a seat that has now
        /// missed <c>MaxIdleBettingWindows</c> in a row is removed. This is the only path that frees a seat held by a
        /// CONNECTED-but-idle player — the heartbeat reaper (<see cref="SweepStalledSeats"/>) can't, because a live
        /// client keeps pinging whether or not it ever bets. Money-safe: only NO-bet seats are evicted, so there is
        /// never a live stake to forfeit. Returns whether anything changed.
        /// </summary>
        private bool SweepIdleBettors(BlackjackTable table)
        {
            var cap = maxIdleBettingWindows;
            if (cap <= 0) return false;   // idle eviction disabled

            bool changed = false;
            List<int> toRemove = null;

            foreach (var seat in table.Seats)
            {
                if (seat.Player == null) continue;

                bool hasBet = seat.Player.Hands.Count > 0 && seat.Player.Hands[0].Bet > 0;
                if (hasBet)
                {
                    if (seat.MissedBetWindows != 0) { seat.MissedBetWindows = 0; changed = true; }
                    continue;
                }

                seat.MissedBetWindows++;
                changed = true;
                if (seat.MissedBetWindows >= cap) (toRemove ??= new List<int>()).Add(seat.SeatNumber);
            }

            if (toRemove != null)
            {
                foreach (var sn in toRemove)
                {
                    logger.LogInformation("Evicting idle seat {Seat} on table {TableId} ({Cap} betting windows with no bet).",
                        sn, table.TableId, cap);
                    RemoveSeatCore(table, sn);   // safe: a no-bet seat has no live stake to forfeit
                }
            }

            return changed;
        }

        // DEV ONLY — when Blackjack:DevRigDealer is on (and we're in Development), rig EVERY deal so insurance
        // is testable without re-arming: the dealer's up card is ALWAYS an Ace (insurance always offered), and
        // the hole card cycles in blocks of 3 — 3 deals as a blackjack (Ace+King → insurance WINS), then 3 as a
        // non-blackjack (Ace+Six → insurance LOSES), repeating. Flip the flag off to stop. Breaks
        // provable-fairness for rigged hands; never enabled in prod (default false + Development-gated).
        private void ApplyDevDealerRig(BlackjackTable table)
        {
            if (!env.IsDevelopment() || !config.GetValue("Blackjack:DevRigDealer", false)) return;

            var cards = table.Game.Dealer.Hand.Cards;
            if (cards.Count < 2) return;
            cards[0] = new Card(Suit.Hearts, FaceValue.Ace, true);          // up = Ace → insurance always offered
            bool blackjackBlock = ((table.RoundNonce - 1) / 3) % 2 == 0;    // 3 blackjacks, then 3 non-blackjacks
            cards[1] = new Card(Suit.Spades, blackjackBlock ? FaceValue.King : FaceValue.Six, false);
        }

        // DEV ONLY — when Blackjack:DevRigDealer1stCardTen is on (and we're in Development), force the dealer's FIRST
        // (up) card to a TEN. That exercises the PEEK path on demand: a 10-value up-card peeks for blackjack WITHOUT
        // opening an insurance window (insurance is Ace-only), which is exactly the case the peek animation is otherwise
        // hard to hit. The hole card cycles in blocks of 3 so BOTH outcomes are covered — 3 deals as a blackjack
        // (Ten+Ace → the peek FINDS one and the round settles with no player turns), then 3 as a non-blackjack
        // (Ten+Six → the peek finds nothing and play continues), repeating. Applied AFTER ApplyDevDealerRig, so if both
        // flags are armed this one wins. Breaks provable-fairness for rigged hands; never enabled in prod (default
        // false + Development-gated).
        private void ApplyDevDealerTenUpRig(BlackjackTable table)
        {
            if (!env.IsDevelopment() || !config.GetValue("Blackjack:DevRigDealer1stCardTen", false)) return;

            var cards = table.Game.Dealer.Hand.Cards;
            if (cards.Count < 2) return;
            cards[0] = new Card(Suit.Clubs, FaceValue.Ten, true);           // up = Ten → peek, and NO insurance window
            bool blackjackBlock = ((table.RoundNonce - 1) / 3) % 2 == 0;    // 3 blackjacks, then 3 non-blackjacks
            cards[1] = new Card(Suit.Diamonds, blackjackBlock ? FaceValue.Ace : FaceValue.Six, false);
        }

        // DEV ONLY — when Blackjack:DevPlayerRigPair is on (and we're in Development), force EVERY in-round player's
        // opening hand to a splittable PAIR, so split can be tested without waiting on a natural pair. The rank cycles
        // by round for coverage — 8s (normal split) → KK (10-value split) → AA (split-aces lock) — repeating. None of
        // these is a 21, so they're always playable/splittable. Breaks provable-fairness for rigged hands; never
        // enabled in prod (default false + Development-gated).
        private void ApplyDevPlayerPairRig(BlackjackTable table)
        {
            if (!env.IsDevelopment() || !config.GetValue("Blackjack:DevPlayerRigPair", false)) return;

            var ranks = new[] { FaceValue.Eight, FaceValue.King, FaceValue.Ace };
            int idx = (int)(((long)table.RoundNonce % ranks.Length + ranks.Length) % ranks.Length);
            var rank = ranks[idx];

            foreach (var player in table.Game.Players)
            {
                if (!player.InRound) continue;
                var cards = player.Hands.FirstOrDefault()?.Hand?.Cards;
                if (cards == null || cards.Count < 2) continue;
                cards[0] = new Card(Suit.Hearts, rank, true);
                cards[1] = new Card(Suit.Spades, rank, true);   // same rank, different suit → a splittable pair
            }
        }

        // DEV ONLY — when Blackjack:DevRigResplit is on, force the two hands produced by a split back into a
        // fresh splittable 8-pair (and un-lock them), so the RE-split funding path (B2: every split must be
        // charged, never a free hand) can be exercised deterministically in a smoke test. Never enabled in prod.
        private void RigResplitForDev(Player player, int handA, int handB)
        {
            if (!env.IsDevelopment() || !config.GetValue("Blackjack:DevRigResplit", false)) return;
            foreach (var hi in new[] { handA, handB })
            {
                var hand = player.GetHand(hi);
                hand.Hand.Cards.Clear();
                hand.Hand.Cards.Add(new Card(Suit.Hearts, FaceValue.Eight, true));
                hand.Hand.Cards.Add(new Card(Suit.Spades, FaceValue.Eight, true));
                hand.Done = false;
            }
        }

        private static void MarkNaturals(BlackjackTable table)
        {
            foreach (var player in table.Game.Players)
            {
                var hand = player.Hands.FirstOrDefault();
                if (hand == null) continue;
                if (hand.Hand.Cards.Count == 2 && hand.Hand.GetSumOfHand() == 21)
                {
                    hand.Done = true;
                }
            }
        }

        private static void NormalizeSeats(BlackjackTable table)
        {
            if (table.MaxSeatsPerUser <= 0)
            {
                table.MaxSeatsPerUser = table.MaxPlayers;
            }

            table.Seats ??= new List<Seat>();
            if (table.Seats.Count == 0)
            {
                table.Seats = Enumerable.Range(1, table.MaxPlayers)
                    .Select(i => new Seat { SeatNumber = i })
                    .ToList();
            }

            foreach (var seat in table.Seats)
            {
                seat.Player = null;
            }

            var openSeats = new Queue<Seat>(table.Seats.Where(s => s.Player == null));
            foreach (var player in table.Game.Players)
            {
                // If seat already has this player, skip
                if (player.SeatNumber > 0)
                {
                    var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == player.SeatNumber);
                    if (seat != null)
                    {
                        seat.Player = player;
                        continue;
                    }
                }

                if (openSeats.Count > 0)
                {
                    var seat = openSeats.Dequeue();
                    player.SeatNumber = seat.SeatNumber;
                    seat.Player = player;
                }
            }
        }

        // After the deal: if the dealer shows an Ace and at least one player can still insure, open the
        // INSURANCE phase (its OWN timer, no play turn yet). Otherwise start play immediately. Insurance
        // decisions reset here so the early-close check is per-round.
        private void BeginPlayOrInsurance(BlackjackTable table)
        {
            foreach (var p in table.Game.Players) p.InsuranceDecided = false;

            bool dealerAce = table.Game.Dealer.Hand.Cards.Any(c => c.IsCardUp && c.FaceVal == CardGames.Platforms.FaceValue.Ace);
            if (dealerAce && AnyInsuranceEligible(table))
            {
                // + presentation ceiling: the insurance window also opens at deal, so the deal-in animation shouldn't eat
                // it. Unlike the turn clock this is NOT collapsed by /presented — it's a SHARED window across every
                // eligible player, so one fast client must not shorten it for a slower one. It closes early anyway once
                // all of them have decided (MaybeCloseInsurance).
                table.InsuranceExpiresAt = DateTimeOffset.UtcNow
                    .AddSeconds(insuranceDurationSeconds + PresentationSecondsFor(table));
                table.CurrentSeatNumber = -1;   // no play turn during the insurance phase
                table.CurrentHandIndex = 0;
                table.TurnExpiresAt = null;
            }
            else
            {
                table.InsuranceExpiresAt = null;
                StartPlayOrPeek(table);   // no insurance window → dealer peek for blackjack, else start play
            }
        }

        // A player who may still take insurance: in-round, main hand untouched (its 2 dealt cards, not done).
        // A player who may still take insurance: in-round, main hand untouched (its 2 dealt cards, not done), AND
        // able to pay for it.
        //
        // The affordability half matters because this predicate decides who the table WAITS FOR. The stake for this
        // round is already debited, so a player who bet most of their balance cannot cover half of it — PlaceInsurance
        // would refuse them, and their client does not even offer it. Without this the table sat through the whole
        // insurance window waiting on a decision nobody was able to make, and the dealer's peek was held behind it.
        //
        // Balance here is the engine's mirror, not the wallet, and that is fine for this use: it only decides whether
        // we WAIT for someone, never whether their bet is accepted. PlaceInsuranceAsync still validates wallet-first,
        // so a stale mirror can at worst make us stop waiting for a player who would have been refused anyway.
        private static bool InsuranceEligible(Player p)
            => p.InRound && p.Hands.Count > 0 && p.Hands[0].Hand.Cards.Count == 2 && !p.Hands[0].Done
               && p.Balance >= p.Hands[0].Bet / 2m;

        private static bool AnyInsuranceEligible(BlackjackTable table) => table.Game.Players.Any(InsuranceEligible);

        // Every eligible player has insured or declined (an empty eligible set counts as decided).
        private static bool AllInsuranceDecided(BlackjackTable table)
            => table.Game.Players.Where(InsuranceEligible).All(p => p.InsuranceDecided);

        private static void CloseInsurancePhase(BlackjackTable table)
        {
            table.InsuranceExpiresAt = null;
            StartPlayOrPeek(table);   // dealer peek: settle on dealer blackjack, else start play
        }

        // Dealer "peek": if the dealer has a blackjack the round is already decided (insurance pays, everyone
        // else loses) — skip player turns by leaving CurrentSeatNumber = -1 so settlement runs immediately.
        // Otherwise start the first player's turn.
        private static void StartPlayOrPeek(BlackjackTable table)
        {
            if (DealerHasBlackjack(table))
            {
                table.CurrentSeatNumber = -1;
                table.CurrentHandIndex = 0;
                table.TurnExpiresAt = null;
            }
            else
            {
                SetInitialTurn(table);
            }
        }

        private static bool DealerHasBlackjack(BlackjackTable table)
        {
            var cards = table.Game.Dealer.Hand.Cards;
            return cards.Count == 2 && table.Game.Dealer.Hand.GetSumOfHand() == 21;
        }

        // Close the insurance phase early once every eligible player has decided.
        private static void MaybeCloseInsurance(BlackjackTable table)
        {
            if (table.InsuranceExpiresAt.HasValue && AllInsuranceDecided(table))
                CloseInsurancePhase(table);
        }

        private static void SetInitialTurn(BlackjackTable table)
        {
            var active = GetOrderedHands(table);
            var current = active.FirstOrDefault();
            if (current.seat == -1)
            {
                table.CurrentSeatNumber = -1;
                table.CurrentHandIndex = 0;
                table.TurnExpiresAt = null;
                return;
            }
            table.CurrentSeatNumber = current.seat;
            table.CurrentHandIndex = current.hand;
            StampTurn(table);
        }

        /// <summary>
        /// Stamp a FRESH turn deadline as the generous CEILING (max-presentation + turn), and re-arm the presentation
        /// handshake. The player's client calls <c>/presented</c> the moment it has finished animating and can actually
        /// act, which collapses this to the real turn length — so the decision clock is always the FULL configured turn
        /// regardless of how long that client's deal-in took (device speed, table size, clip length). If no client ever
        /// calls, this ceiling still expires and the reaper/auto-stand runs, so a stalled seat can't wedge the table.
        /// Every turn transition goes through here so they all behave identically.
        /// </summary>
        private static void StampTurn(BlackjackTable table)
        {
            table.TurnPresentAcked = false;
            table.TurnExpiresAt = DateTimeOffset.UtcNow
                .AddSeconds(PresentationSecondsFor(table) + table.TurnDurationSeconds);
        }

        /// <summary>
        /// Arm the BETTING window, i.e. "bets are open; the round starts when this expires". Same anti-stall shape as
        /// <see cref="StampTurn"/>: stamped as a generous CEILING and collapsed to the real length by the client's
        /// <c>/presented</c> call. <paramref name="afterRound"/> distinguishes the two entry points —
        ///
        ///  • after a round (true): the clients are still playing the round-end ceremony (reveal → dealer draws →
        ///    per-seat collect → per-seat pay → sweep → finale), so the ceiling includes a presentation allowance.
        ///    Without it the betting clock would be several seconds into a 15s window before anyone even sees the felt.
        ///  • on a first bet at an idle table (false): nothing is animating, so the window starts immediately.
        ///
        /// No-ops when the window is disabled (0) or already armed — re-arming on every bet would let a player extend
        /// the window indefinitely by nudging their stake, which is exactly what the timer exists to prevent.
        /// </summary>
        private static void ArmBettingWindow(BlackjackTable table, bool afterRound)
        {
            if (table.RoundInProgress) return;                  // a betting window only exists BETWEEN rounds
            if (table.BettingDurationSeconds <= 0) return;      // window disabled — a human must press Deal
            if (table.BettingExpiresAt.HasValue) return;        // already counting down; never extend
            if (!table.Seats.Any(s => s.Player != null)) return; // empty table — arm it when someone sits and bets

            table.BetPresentAcked = false;
            table.BetPresentAckedSeats = new List<int>();
            var lead = afterRound ? PresentationSecondsFor(table) : 0;
            table.BettingExpiresAt = DateTimeOffset.UtcNow.AddSeconds(lead + table.BettingDurationSeconds);
        }

        /// <summary>
        /// Estimated seconds for a client to ANIMATE the deal-in, SCALED TO THE TABLE. The client throws one card at a
        /// time, so an opening deal is 2 cards per player + 2 for the dealer — a 3-seat table animates about twice as
        /// long as heads-up. A fixed constant sized for heads-up would cut a full table off mid-deal (it would look
        /// exactly like the old timer bug), so this scales with the seat count and is capped by MaxPresentationSeconds.
        ///
        /// Over-estimating is FREE: this is only the anti-stall backstop, and the /presented handshake collapses the
        /// deadline to the real turn length the moment the client is actually ready. A genuinely dead client is caught
        /// by the heartbeat reaper (Table:StalledTimeoutSeconds), not by this.
        /// </summary>
        private static int PresentationSecondsFor(BlackjackTable table)
        {
            int players = Math.Max(1, table.Game?.Players?.Count ?? 1);
            int cards = (2 * players) + 2;              // opening deal: two each, plus the dealer's two
            int estimate = (table.PresentationPerCardSeconds * cards) + 3;   // + a little fixed overhead
            return Math.Min(table.MaxPresentationSeconds, estimate);
        }

        // Called after HIT and SPLIT — both DEAL a card the client must animate before the player can act again.
        private static void RefreshTurn(BlackjackTable table)
        {
            if (table.CurrentSeatNumber <= 0) return;
            StampTurn(table);
        }

        private static void EnsureTurn(BlackjackTable table, int seatNumber, int handIndex)
        {
            if (table.TurnExpiresAt.HasValue && DateTimeOffset.UtcNow > table.TurnExpiresAt.Value)
            {
                // auto-stand timed-out hand
                AutoStand(table);
            }

            if (table.CurrentSeatNumber != seatNumber || table.CurrentHandIndex != handIndex)
            {
                throw new InvalidOperationException("Not your turn.");
            }
        }

        private static void AutoStand(BlackjackTable table)
        {
            if (table.CurrentSeatNumber <= 0) return;
            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == table.CurrentSeatNumber);
            if (seat?.Player == null) return;
            seat.Player.Stand(table.CurrentHandIndex);
            AdvanceTurn(table);
        }

        /// <summary>
        /// Finishes a hand that has reached exactly 21 (AUTO-STAND) and reports whether it did. A player can never
        /// improve on 21 — a further card either busts the hand or, on a soft 21, leaves the same total — so the
        /// turn is not handed back for a decision with one sensible answer. Applies to any 21 reached in play
        /// (hit, or a split hand dealt to 21); an opening natural is already finished by <see cref="MarkNaturals"/>,
        /// and this does NOT make a 21 a natural — <see cref="BlackjackSettlement"/> still pays it 1:1.
        /// Idempotent: returns false for a hand that is already Done or isn't 21.
        /// </summary>
        private static bool AutoStandOnTwentyOne(Player player, int handIndex)
        {
            var hand = player.GetHand(handIndex);
            if (hand.Done || hand.Hand.GetSumOfHand() != 21) return false;
            hand.Done = true;
            return true;
        }

        // Append one move to the round's buffered action log (flushed to GameHandActions at settle).
        private static void LogAction(BlackjackTable table, HandActionType type, int? seat, string userId,
            decimal? amount = null, int? handValueAfter = null, string cardDrawn = null)
        {
            (table.ActionLog ??= new List<GameActionEntry>()).Add(new GameActionEntry
            {
                UserId = userId,
                SeatNumber = seat,
                ActionType = type,
                Amount = amount,
                HandValueAfter = handValueAfter,
                CardDrawn = cardDrawn,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        private static void AdvanceTurn(BlackjackTable table)
        {
            // The current hand is usually already marked Done/bust before this runs (stand, hit-bust, double,
            // split-aces, auto-stand), so it's no longer in GetOrderedHands. Advance to the first STILL-ACTIVE
            // hand AFTER the current position in canonical turn order — never jump straight to the dealer while
            // other seats/hands still have to act. (Looking up the current position by index would miss-fire
            // here: a Done current hand isn't in the list, so it would resolve to the dealer.)
            //
            // Turn order is SEAT-DESCENDING (see GetOrderedHands: the dealer's left / highest seat acts first),
            // so "after the current position" means the next LOWER seat — h.seat < curSeat. This test must stay
            // in lockstep with GetOrderedHands' ordering: when it was flipped to descending while this still
            // read `h.seat > curSeat`, no lower seat could ever satisfy it, so the first seat to finish sent the
            // round straight to the dealer and every remaining seat was settled on a hand it never got to play
            // (with its stake already debited). Hands WITHIN a seat are now yielded DESCENDING as well (the
            // right-hand split acts first), so the same-seat clause is `h.hand < curHand` — the exact same failure
            // mode waits here for anyone who flips one of these two without the other.
            int curSeat = table.CurrentSeatNumber;
            int curHand = table.CurrentHandIndex;

            (int seat, int hand)? next = null;
            foreach (var h in GetOrderedHands(table))
            {
                if (h.seat == -1) continue; // dealer sentinel
                if (h.seat < curSeat || (h.seat == curSeat && h.hand < curHand)) { next = h; break; }
            }

            var n = next ?? (seat: -1, hand: 0);
            table.CurrentSeatNumber = n.seat;
            table.CurrentHandIndex = n.hand;
            if (n.seat == -1) { table.TurnExpiresAt = null; table.TurnPresentAcked = false; }
            else StampTurn(table);   // ceiling; the next seat's client collapses it via /presented (instantly if nothing is animating)
        }

        private static IEnumerable<(int seat, int hand)> GetOrderedHands(BlackjackTable table)
        {
            // Turn order follows the deal: the dealer serves her LEFT first (blackjack "first base"), which is the
            // player's RIGHTMOST seat = the HIGHEST seat number, then round to her right. So act highest-seat → lowest,
            // NOT join order. (Ascending here made the middle seat act before the right one when the left seat was empty.)
            // SPLIT HANDS RUN RIGHT→LEFT TOO, so iterate them DESCENDING. Player.Split APPENDS the new hand, and the
            // table lays hands out with the highest index on the player's RIGHT — so the highest index is the one
            // sitting at "first base" for that seat and must act first. Ascending here made the dealer offer the LEFT
            // hand first, against the direction the rest of the table plays in.
            foreach (var seat in table.Seats.OrderByDescending(s => s.SeatNumber))
            {
                if (seat.Player == null || !seat.Player.InRound) continue;
                for (int i = seat.Player.Hands.Count - 1; i >= 0; i--)
                {
                    var hand = seat.Player.Hands[i];
                    // >= 21 (not > 21): a bust hand is over, and a hand ON 21 auto-stands — it must never be
                    // offered a turn. The action paths mark 21 hands Done explicitly (AutoStandOnTwentyOne);
                    // this is the backstop that also covers state restored from Redis by an older server.
                    if (hand.Done || hand.Hand.GetSumOfHand() >= 21) continue;
                    yield return (seat.SeatNumber, i);
                }
            }
            yield return (-1, 0);
        }
    }

    public class TableCreateResult
    {
        public string TableId { get; set; }

        public BlackJackGame Game { get; set; }

        public int MaxPlayers { get; set; }

        public int MaxSeatsPerUser { get; set; }
    }

    public class BlackjackTable
    {
        public string TableId { get; set; }

        public BlackjackMode Mode { get; set; } = BlackjackMode.Classic;

        public decimal MinBet { get; set; }

        public decimal MaxBet { get; set; }

        public int MaxPlayers { get; set; }

        public int MaxSeatsPerUser { get; set; }

        public bool RoundInProgress { get; set; }

        public BlackJackGame Game { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public List<Seat> Seats { get; set; } = new List<Seat>();

        public int TurnDurationSeconds { get; set; } = 20;

        // ANTI-STALL CEILING for the presentation handshake. A turn deadline is first stamped generously as
        // (now + presentation + TurnDurationSeconds); the client calls /presented once it has finished animating and can
        // actually act, which collapses it to (now + TurnDurationSeconds) — never beyond the original ceiling. So a
        // client that stalls, lies, or never calls can only LOSE time, never gain it.
        //
        // The presentation estimate SCALES with the table (see PresentationSecondsFor): the client throws one card at a
        // time, so a 3-seat deal animates roughly twice as long as heads-up. These are the per-card rate and the hard cap.
        public int PresentationPerCardSeconds { get; set; } = 3;
        public int MaxPresentationSeconds { get; set; } = 45;

        // False while the current turn's deadline is still the generous ceiling; set once /presented collapses it, so
        // the collapse happens exactly once per turn. Reset by StampTurn on every turn transition.
        public bool TurnPresentAcked { get; set; }

        public int CurrentSeatNumber { get; set; } = -1;

        public int CurrentHandIndex { get; set; } = 0;

        public DateTimeOffset? TurnExpiresAt { get; set; }

        /// <summary>While set, the round is in its INSURANCE phase: cards are dealt, the dealer shows an Ace,
        /// and every dealt player may insure until this expires (or all decide). No play turn runs until it
        /// closes — the round-driver holds settlement during this window.</summary>
        public DateTimeOffset? InsuranceExpiresAt { get; set; }

        /// <summary>While set (and no round in progress), the table is in its BETTING window: when it expires the
        /// round-driver deals whatever bets are down. Null = no window armed; the table is idle waiting for a first
        /// bet. With one player a window is a convenience; with several it's the only thing stopping one person from
        /// holding everyone hostage by never pressing Deal.</summary>
        public DateTimeOffset? BettingExpiresAt { get; set; }

        /// <summary>Length of the betting window in seconds. 0 disables the window entirely (a human must press Deal).</summary>
        public int BettingDurationSeconds { get; set; } = 15;

        /// <summary>Snapshot of the idle-eviction threshold (config <c>Blackjack:MaxIdleBettingWindows</c>) so the board
        /// projection can flag a seat's FINAL betting window (<c>MissedBetWindows &gt;= this - 1</c>) as an idle-kick
        /// warning without the static board knowing the manager's live config. 0 disables idle eviction.</summary>
        public int MaxIdleBettingWindows { get; set; } = 3;

        /// <summary>Same collapse-the-ceiling handshake as <see cref="TurnPresentAcked"/>, but for the betting window:
        /// the window is armed at settle as (round-end ceremony ceiling + betting), and the client collapses it to the
        /// real betting length once its round-end ceremony has finished. Reset every time the window is armed.</summary>
        public bool BetPresentAcked { get; set; }

        /// <summary>Seats that have reported their round-end ceremony finished. The betting ceiling only collapses
        /// once every seat that PLAYED the round is in here, so a fast client can't shorten a slow client's window.</summary>
        public List<int> BetPresentAckedSeats { get; set; } = new List<int>();

        // Current round (deal -> settle). RoundStartBalance maps seat -> wallet chips captured at
        // deal, used to reconcile the round's net result to the wallet at settle.
        public string CurrentRoundId { get; set; }

        public Dictionary<int, decimal> RoundStartBalance { get; set; } = new Dictionary<int, decimal>();

        // Provably-fair seeds. ServerSeed is SECRET (never sent to clients); ServerSeedHash is the
        // public commitment; ClientSeed + RoundNonce complete each round's shuffle seed.
        public string ServerSeed { get; set; }
        public string ServerSeedHash { get; set; }
        public string ClientSeed { get; set; }
        public long RoundNonce { get; set; }

        // ---- Multi-deck SHOE (casino-grade). A shoe of >1 deck PERSISTS across rounds and is replaced only when
        // the cut card is reached; a 1-deck game reshuffles every round (the original behaviour). The shoe seed is
        // derived from ShoeNonce (not RoundNonce), because one shoe now spans many rounds.
        public long ShoeNonce { get; set; }
        /// <summary>Hash of the shoe as shuffled — identifies THIS shoe across every round dealt from it.</summary>
        public string ShoeHash { get; set; }
        /// <summary>Cards the shoe held when it was built — the denominator for penetration.</summary>
        public int ShoeSize { get; set; }
        /// <summary>Cards left when the cut card is considered reached (0 = no cut card / single-deck).</summary>
        public int CutCardAt { get; set; }
        /// <summary>Cards already dealt from this shoe when the CURRENT round started — with a persistent shoe this
        /// is what lets a single round be replayed from the shoe's seed. Persisted per hand for audit.</summary>
        public int ShoeDealtAtRoundStart { get; set; }

        /// <summary>When this table last became empty, or null while anyone is seated. The lobby balancer only
        /// reclaims a table that has been empty for a grace period, so a table can't vanish the moment its last
        /// player stands up (or flicker when they sit straight back down).</summary>
        public DateTimeOffset? EmptySince { get; set; }

        // Audit capture for the in-progress round, persisted to GameHandHeader at settle.
        public string CurrentDeckHash { get; set; }
        public DateTimeOffset? RoundStartedAt { get; set; }

        // Id of the most recently settled hand, so clients can deep-link to GET /verify/{handId}.
        public string LastHandId { get; set; }

        // Hash (ResultChecksum) of the most recently settled hand on this table; the next hand chains its
        // PrevHandHash to this, forming a tamper-evident per-table hand chain across rounds.
        public string LastHandHash { get; set; }

        // Per-seat result of the most recently settled round (for the client's result banner). Set at
        // settle, cleared when a new round is dealt.
        public List<SeatRoundResult> LastResults { get; set; } = new List<SeatRoundResult>();

        // Buffered move-by-move action log for THIS round; flushed to GameHandActions at settle (stamped with
        // the header's HandId) and reset at the next deal. Lives in the table blob so it survives Redis.
        public List<GameActionEntry> ActionLog { get; set; } = new List<GameActionEntry>();
    }

    public class Seat
    {
        public int SeatNumber { get; set; }
        public Player? Player { get; set; }

        // ---- Connection / stalled-player tracking (lives in the Redis table blob; no MySQL schema change) ----
        /// <summary>UtcNow of the seated player's last heartbeat (hub or REST). Stamped on join + each heartbeat;
        /// the reaper derives IsConnected/IsStalled from its age. Defaults to now so a freshly-loaded old table
        /// (no heartbeat field in JSON) isn't instantly reaped.</summary>
        public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;
        /// <summary>Derived from heartbeat freshness by the reaper — false ⇒ the client shows "disconnected…".</summary>
        public bool IsConnected { get; set; } = true;
        /// <summary>Reaper flag: no heartbeat for &gt; StalledTimeout. Drives the §5 money-safe auto-removal.</summary>
        public bool IsStalled { get; set; } = false;

        /// <summary>Consecutive betting windows this seat has let pass WITHOUT placing a bet. Reset to 0 the moment
        /// they bet (or are dealt in); incremented each time a betting window closes with no funded bet on the seat.
        /// At <c>Blackjack:MaxIdleBettingWindows</c> the seat is evicted — anti-squat, so a connected-but-idle player
        /// can't hold a seat forever (the heartbeat reaper only removes DISCONNECTED clients, never a non-bettor).</summary>
        public int MissedBetWindows { get; set; } = 0;
    }

    /// <summary>One seat's outcome for the most recently settled round, surfaced on the board snapshot.</summary>
    public class SeatRoundResult
    {
        public int SeatNumber { get; set; }
        public string Outcome { get; set; }    // "win" | "lose" | "push" — the seat's NET across all its hands
        public decimal Delta { get; set; }       // net chips change this round (signed: + win, - loss, 0 push)
        public decimal Payout { get; set; }      // gross returned to the wallet
        public int FinalHandValue { get; set; }
        public bool Bust { get; set; }
        public bool Blackjack { get; set; }

        /// <summary>Per-hand results for THIS seat, ordered by hand index (0 = main, 1 = split, …). Lets the client
        /// label AND pay/collect each split hand on its own, where the seat-level <see cref="Outcome"/>/<see cref="Delta"/>
        /// (a net) would call a mixed win/loss a push and move no chips at all. A single-hand seat has one entry whose
        /// values equal the seat-level ones. Consumers that want the seat's overall result keep using the seat fields.</summary>
        public List<HandRoundResult> Hands { get; set; } = new List<HandRoundResult>();
    }

    /// <summary>One HAND's outcome within a settled seat (a split has one of these per hand).</summary>
    public class HandRoundResult
    {
        public int HandIndex { get; set; }
        /// <summary>"blackjack" | "win" | "push" | "bust" | "lose" — this hand alone, not the seat net.</summary>
        public string Outcome { get; set; }
        /// <summary>Total staked on this hand (main stake incl. any double-down extra, plus insurance).</summary>
        public decimal Stake { get; set; }
        /// <summary>Gross returned for this hand (0 on a loss/bust, stake back on a push, incl. insurance).</summary>
        public decimal Payout { get; set; }
        /// <summary>Net for this hand = Payout − Stake (signed). The seat's Delta is the sum of these.</summary>
        public decimal Delta { get; set; }

        /// <summary>
        /// The insurance side bet on this hand, and what it returned gross (0 if it lost or was never placed).
        ///
        /// Broken out because <see cref="Delta"/> NETS them against the main hand, and a client presenting the round
        /// has to move the two independently: the dealer takes a losing hand's wager AND pays a winning insurance bet,
        /// as two gestures, even when the amounts cancel. Insurance against a dealer blackjack is the case that
        /// cancels exactly — that is what insurance is FOR — so a netted Delta of 0 there reads as "a push, move
        /// nothing" and the round silently skips both.
        ///
        /// Same flaw the seat-level Delta already had (see the per-hand results above, which exist because a seat's
        /// net called a mixed win/loss a push); it simply survived one level lower, inside a hand.
        ///
        /// Hand-only net, for a client that wants the main wager alone: Delta − (InsuranceReturn − InsuranceBet).
        /// </summary>
        public decimal InsuranceBet { get; set; }

        /// <inheritdoc cref="InsuranceBet"/>
        public decimal InsuranceReturn { get; set; }
    }

    /// <summary>One buffered move in a round (bet/deal/hit/stand/double/split/insurance/dealerPlay), held in
    /// the table blob during play and written to GameHandActions at settle. UserId is the player's string id;
    /// CardDrawn is space-separated canonical card tokens (e.g. "14H 10S").</summary>
    public class GameActionEntry
    {
        public string UserId { get; set; }
        public int? SeatNumber { get; set; }
        public HandActionType ActionType { get; set; }
        public string CardDrawn { get; set; }
        public int? HandValueAfter { get; set; }
        public decimal? Amount { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
