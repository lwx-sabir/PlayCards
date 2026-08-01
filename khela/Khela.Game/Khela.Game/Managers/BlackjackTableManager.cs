using CardGames.Platforms;
using Khela.Game.Services.Redis;
using System.Text.Json;
using System;
using System.Collections.Generic;
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
                var roundSeed = ProvableShuffle.DeriveSeed(
                    Convert.FromHexString(table.ServerSeed), table.ClientSeed, table.RoundNonce);

                var header = new GameHandHeader
                {
                    TableId = table.TableId,
                    GameType = GameType.Blackjack,
                    RoundId = roundId,
                    HandNumber = (int)table.RoundNonce,
                    StartedAt = table.RoundStartedAt?.UtcDateTime ?? DateTime.UtcNow,
                    SettledAt = DateTime.UtcNow,
                    Status = HandStatus.Settled,
                    ShoeId = table.ServerSeedHash,
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
            var canonical = $"{table.CurrentDeckHash}|D={dealer}|{players}";
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

            var table = JsonSerializer.Deserialize<BlackjackTable>(json);
            if (table == null) return null;

            NormalizeSeats(table);
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
        public async Task<List<BlackjackTableSummary>> GetLobbyAsync(BlackjackMode? mode = null)
        {
            var db = redisService.GetDatabase();

            // Always ensure the full set of default house tables exists before listing — so a player never lands
            // on an empty OR partial lobby (e.g. after some idle tables expired). Tops up only what's missing.
            await EnsureDefaultTablesAsync();

            var ids = await db.SetMembersAsync(LobbyIndexKey);
            var summaries = new List<BlackjackTableSummary>();
            var stale = new List<RedisValue>();

            foreach (var id in ids)
            {
                var table = await GetTableAsync((string)id);
                if (table == null) { stale.Add(id); continue; } // key expired (TTL) -> drop from index
                if (mode.HasValue && table.Mode != mode.Value) continue;
                summaries.Add(ToSummary(table));
            }

            if (stale.Count > 0) await db.SetRemoveAsync(LobbyIndexKey, stale.ToArray());

            return summaries
                .OrderBy(s => s.Mode)
                .ThenBy(s => s.MinBet)
                .ThenBy(s => s.TableId)
                .ToList();
        }

        // The "house tables" the lobby always offers. EnsureDefaultTablesAsync tops up any that are missing
        // (matched by mode + min/max), under an NX seed-lock so concurrent loads don't duplicate. Replace with
        // proper table lifecycle + bot seeding later.
        private static readonly (BlackjackMode mode, decimal min, decimal max)[] DefaultTables =
        {
            (BlackjackMode.Classic, 1000m, 10000m),
            (BlackjackMode.Classic, 5000m, 25000m),
            (BlackjackMode.Classic, 25000m, 100000m),
        };

        /// <summary>
        /// Ensures every default house table exists, creating ONLY the ones currently missing (matched by mode +
        /// min/max) — so a player always gets the complete lobby, even if some idle tables had expired. A short NX
        /// lock serialises concurrent lobby loads so the missing set is created once, never duplicated.
        /// </summary>
        public async Task EnsureDefaultTablesAsync()
        {
            var db = redisService.GetDatabase();
            if ((await MissingDefaultsAsync(db)).Count == 0) return;   // fast path — the full set is already present

            var lockKey = "blackjack:tables:seedlock";
            var token = Guid.NewGuid().ToString("N");
            if (!await db.StringSetAsync(lockKey, token, TimeSpan.FromSeconds(10), When.NotExists))
                return;   // another lobby load is already creating them
            try
            {
                foreach (var d in await MissingDefaultsAsync(db))   // re-check under the lock
                    await CreateTableAsync(maxPlayers: 5, maxSeatsPerUser: 1, mode: d.mode, minBet: d.min, maxBet: d.max);
            }
            finally
            {
                await db.ScriptEvaluateAsync(
                    "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end",
                    new RedisKey[] { lockKey }, new RedisValue[] { token });
            }
        }

        // The default-table configs that have no matching live table right now (matched by mode + min/max).
        private async Task<List<(BlackjackMode mode, decimal min, decimal max)>> MissingDefaultsAsync(IDatabase db)
        {
            var ids = await db.SetMembersAsync(LobbyIndexKey);
            var live = new List<BlackjackTable>();
            foreach (var id in ids)
            {
                var t = await GetTableAsync((string)id);
                if (t != null) live.Add(t);
            }
            return DefaultTables
                .Where(d => !live.Any(t => t.Mode == d.mode && t.MinBet == d.min && t.MaxBet == d.max))
                .ToList();
        }

        /// <summary>DEV: wipe the seeded house tables (lobby index + table entries + seed guard) and re-create
        /// them. Use after editing <see cref="DefaultTables"/> so the lobby reflects the new stakes/seats.</summary>
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
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

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

            // Seat from the AUTHORITATIVE wallet balance — never trust a client-supplied balance.
            var chips = await GetWalletChipsAsync(player.Id);
            var seatedPlayer = new Player(player.Id, chips, player.Name, player.Image, openSeat.SeatNumber);

            openSeat.Player = seatedPlayer;
            openSeat.LastHeartbeatAt = DateTime.UtcNow;   // start the heartbeat clock so a fresh seat isn't reaped
            openSeat.IsConnected = true;
            openSeat.IsStalled = false;
            openSeat.MissedBetWindows = 0;
            table.Game.Players.Add(seatedPlayer);

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
            if (!table.Seats.Any(s => s.Player != null))
            {
                table.BettingExpiresAt = null;
                table.BetPresentAcked = false;
                table.BetPresentAckedSeats = new List<int>();
            }
        }

        /// <summary>
        /// Stamp the caller's seat heartbeat (hub or REST keep-alive). Refreshes only LastHeartbeatAt and saves
        /// WITHOUT a broadcast — the visible board is unchanged, and the reaper derives IsConnected/IsStalled from
        /// this timestamp on its next tick. Returns the table (no-op) if the user isn't seated here.
        /// </summary>
        public async Task<BlackjackTable?> RecordHeartbeatAsync(string tableId, string userId)
        {
            await using var _tableLock = await LockTableAsync(tableId);

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
            var roundSeed = ProvableShuffle.DeriveSeed(
                Convert.FromHexString(table.ServerSeed), table.ClientSeed, table.RoundNonce);
            var shoeForHash = new Deck(6);
            shoeForHash.Shuffle(roundSeed);
            var deckHash = shoeForHash.ComputeHash();

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
                table.Game.DealNewGame(roundSeed, 6);
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

            // Split aces are locked to one card each (both hands Done) → advance off this player. A normal
            // split leaves the current hand playable → keep the turn here so the player plays hand `handIndex`.
            if (player.GetHand(handIndex).Done)
                AdvanceTurn(table);
            else
                RefreshTurn(table);

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
                            Delta = h.GrossReturn - (h.Stake + h.InsuranceStake)
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
            // Cheap unlocked peek so idle tables aren't locked every tick. Prune ids whose table key has
            // TTL-expired so the driver stops re-probing them forever (mirrors GetLobbyAsync's self-heal).
            var peek = await GetTableAsync(tableId);
            if (peek == null)
            {
                await redisService.GetDatabase().SetRemoveAsync(LobbyIndexKey, tableId);
                return;
            }
            // Keep every lobby table alive while the server runs: an IDLE table is never re-saved (the fast-path
            // below returns without a write, and reads don't extend TTL), so without this its 2h key TTL lapses
            // and it silently vanishes from the lobby — leaving only the table being actively played. Refreshing
            // the TTL each tick (cheap O(1)) holds the house tables in place.
            await redisService.GetDatabase().KeyExpireAsync(GetKey(tableId), TimeSpan.FromHours(2));
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
        private static bool InsuranceEligible(Player p)
            => p.InRound && p.Hands.Count > 0 && p.Hands[0].Hand.Cards.Count == 2 && !p.Hands[0].Done;

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
            // (with its stake already debited). Hands WITHIN a seat are still yielded ascending, so the
            // same-seat clause stays `h.hand > curHand`.
            int curSeat = table.CurrentSeatNumber;
            int curHand = table.CurrentHandIndex;

            (int seat, int hand)? next = null;
            foreach (var h in GetOrderedHands(table))
            {
                if (h.seat == -1) continue; // dealer sentinel
                if (h.seat < curSeat || (h.seat == curSeat && h.hand > curHand)) { next = h; break; }
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
            foreach (var seat in table.Seats.OrderByDescending(s => s.SeatNumber))
            {
                if (seat.Player == null || !seat.Player.InRound) continue;
                for (int i = 0; i < seat.Player.Hands.Count; i++)
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
