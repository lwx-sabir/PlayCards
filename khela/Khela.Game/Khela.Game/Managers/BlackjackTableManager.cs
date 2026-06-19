using CardGames.Blackjack.CardGames.Blackjack;
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
using Khela.Game.Services.Stats;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
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
        private readonly int turnDurationSeconds;
        private const int DefaultMaxPlayers = 5;

        public BlackjackTableManager(IRedisService redisService, IServiceScopeFactory scopeFactory,
            IHubContext<BlackjackHub> hubContext, ILogger<BlackjackTableManager> logger, IConfiguration config)
        {
            this.redisService = redisService;
            this.scopeFactory = scopeFactory;
            this.hubContext = hubContext;
            this.logger = logger;
            this.turnDurationSeconds = config.GetValue("Blackjack:TurnSeconds", 30);
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
        private async Task<(string TxId, decimal Balance)> DebitStakeAsync(string userId, decimal amount, string tableId, string roundId, int seat, string suffix)
        {
            using var scope = scopeFactory.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            var ctx = new WalletContext { TableId = tableId, RoundId = roundId, Description = $"Blackjack {suffix} round {roundId} seat {seat}" };
            // roundId is a GUID (unique alone); the suffix distinguishes stk/dd/sp/ins. Fits CorrelationId(64).
            var correlationId = $"bjr:{roundId}:{seat}:{suffix}";
            var txn = await wallet.DebitAsync(userId, CurrencyType.Chips, amount, TransactionType.Bet, correlationId, ctx);
            return (txn.TransactionId.ToString(), txn.BalanceAfter ?? 0m);
        }

        /// <summary>
        /// Credits a settled hand's GROSS return (wins + pushes + insurance payouts) to the wallet. Stakes
        /// already left the wallet via <see cref="DebitStakeAsync"/>, so settle only ever returns money.
        /// Idempotent on the round+seat payout key, so a retried settle never double-pays.
        /// </summary>
        private async Task<(string TxId, decimal Balance)> CreditGrossAsync(string userId, decimal gross, string tableId, string roundId, int seat)
        {
            using var scope = scopeFactory.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            var ctx = new WalletContext { TableId = tableId, RoundId = roundId, Description = $"Blackjack payout round {roundId} seat {seat}" };
            var correlationId = $"bjr:{roundId}:{seat}:pay";
            var txn = await wallet.CreditAsync(userId, CurrencyType.Chips, gross, TransactionType.Win, correlationId, ctx);
            return (txn.TransactionId.ToString(), txn.BalanceAfter ?? 0m);
        }

        /// <summary>
        /// Credits the gross payout, retrying a few times on a transient/locked-wallet failure. Safe to
        /// retry because the credit is idempotent on its <c>:pay</c> correlation id (never double-pays).
        /// </summary>
        private async Task<(string TxId, decimal Balance)> CreditGrossWithRetryAsync(string userId, decimal gross, string tableId, string roundId, int seat)
        {
            const int attempts = 3;
            for (int i = 1; ; i++)
            {
                try { return await CreditGrossAsync(userId, gross, tableId, roundId, seat); }
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
        private async Task RefundStakeAsync(string userId, decimal amount, string tableId, string roundId, int seat)
        {
            using var scope = scopeFactory.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            var ctx = new WalletContext { TableId = tableId, RoundId = roundId, Description = $"Blackjack stake refund round {roundId} seat {seat}" };
            var correlationId = $"bjr:{roundId}:{seat}:stkrf";
            await wallet.CreditAsync(userId, CurrencyType.Chips, amount, TransactionType.Refund, correlationId, ctx);
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
                db.GameHandHeaders.Add(header);
                db.GameHandParticipants.AddRange(participants);
                if (actions.Count > 0) db.GameHandActions.AddRange(actions);
                await db.SaveChangesAsync();

                table.LastHandId = header.HandId.ToString(); // surfaced in the board for one-click verify
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
        public async Task SaveTableAsync(string tableId, BlackjackTable table)
        {
            table.UpdatedAt = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(table);
            await redisService.GetDatabase().StringSetAsync(GetKey(tableId), json, TimeSpan.FromHours(2)); // TTL 2h

            // Live update: every state change pushes the masked board to this table's subscribers.
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

            var ids = await db.SetMembersAsync(LobbyIndexKey);
            if (ids.Length == 0)
            {
                await EnsureDefaultTablesAsync();
                ids = await db.SetMembersAsync(LobbyIndexKey);
            }

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

        // Temporary "house tables" so the lobby is never empty in dev. Replace with proper table
        // lifecycle + bot seeding later. NX-guarded so concurrent first hits don't duplicate.
        private static readonly (BlackjackMode mode, decimal min, decimal max)[] DefaultTables =
        {
            (BlackjackMode.Classic, 1000m, 10000m),
            (BlackjackMode.Classic, 5000m, 25000m),
            (BlackjackMode.Classic, 25000m, 100000m),
        };

        public async Task EnsureDefaultTablesAsync()
        {
            var db = redisService.GetDatabase();
            if (!await db.StringSetAsync("blackjack:tables:seeded", "1", TimeSpan.FromHours(2), When.NotExists))
                return;

            foreach (var t in DefaultTables)
                await CreateTableAsync(maxPlayers: 5, maxSeatsPerUser: 1, mode: t.mode, minBet: t.min, maxBet: t.max);
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
            await db.KeyDeleteAsync("blackjack:tables:seeded");
            await EnsureDefaultTablesAsync();
            return await GetLobbyAsync();
        }

        private static BlackjackTableSummary ToSummary(BlackjackTable table)
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
                MaxPlayers = table.MaxPlayers,
                SeatsOccupied = occupants.Count,
                RoundInProgress = table.RoundInProgress,
                Occupants = occupants
            };
        }

        public async Task<BlackjackTable?> AddPlayerAsync(string tableId, Player player)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            var existingSeatsForUser = table.Seats.Count(s => s.Player != null && s.Player.Id == player.Id);
            if (existingSeatsForUser >= table.MaxSeatsPerUser)
                throw new InvalidOperationException("Player has reached max seats at this table.");

            var openSeat = table.Seats.FirstOrDefault(s => s.Player == null);
            if (openSeat == null)
                throw new InvalidOperationException("Table is full.");

            // Seat from the AUTHORITATIVE wallet balance — never trust a client-supplied balance.
            var chips = await GetWalletChipsAsync(player.Id);
            var seatedPlayer = new Player(player.Id, chips, player.Name, player.Image, openSeat.SeatNumber);

            openSeat.Player = seatedPlayer;
            table.Game.Players.Add(seatedPlayer);
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

            var player = seat.Player;
            var wasCurrentTurn = table.RoundInProgress && table.CurrentSeatNumber == seatNumber;

            // Leaving mid-round forfeits the in-progress wager — but with debit-on-bet the stake ALREADY
            // left the wallet at deal/action, so the forfeit is automatic: this seat simply isn't credited
            // at settle. (A player can't dodge a loss by leaving, and an abandoned winning hand is forfeit.)
            table.RoundStartBalance?.Remove(seatNumber);

            table.Game.Players.RemoveAll(p => p.SeatNumber == seatNumber);
            seat.Player = null;

            // If it was this player's turn, pass the turn to the next active player so play continues.
            if (wasCurrentTurn)
                SetInitialTurn(table);

            await SaveTableAsync(tableId, table);
            return table;
        }

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
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> DealAsync(string tableId)
        {
            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);
            if (table == null) return null;

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
            var debited = new List<(string PlayerId, decimal Amount, int Seat)>();
            foreach (var player in table.Game.Players)
            {
                var bet = player.Hands.Count > 0 ? player.Hands[0].Bet : 0m;
                if (bet <= 0) { player.InRound = false; continue; }

                try
                {
                    var (stkTx, walletAfter) = await DebitStakeAsync(player.Id, bet, table.TableId, roundId, player.SeatNumber, "stk");
                    player.InRound = true;
                    table.RoundStartBalance[player.SeatNumber] = walletAfter + bet; // pre-stake balance (audit)
                    player.SetBalance(walletAfter);                                  // mirror = wallet after the stake
                    wagers[player.SeatNumber] = bet;
                    stakeTxIds[player.SeatNumber] = stkTx;
                    debited.Add((player.Id, bet, player.SeatNumber));
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
                SetInitialTurn(table);
                table.RoundInProgress = true;
                await SaveTableAsync(tableId, table);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deal failed after reserving stakes on table {TableId} round {RoundId}; refunding {Count} stake(s).",
                    table.TableId, roundId, debited.Count);
                foreach (var d in debited)
                {
                    try { await RefundStakeAsync(d.PlayerId, d.Amount, table.TableId, roundId, d.Seat); }
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
            if (result.IsBust)
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
            var (ddTx, ddWalletAfter) = await DebitStakeAsync(player.Id, ddExtra, table.TableId, table.CurrentRoundId ?? "", seatNumber, $"dd{handIndex}");

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

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;
            var insHand = player.GetHand(handIndex);
            if (insHand.InsuranceBet > 0) throw new InvalidOperationException("Insurance already placed.");

            var upCard = table.Game.Dealer.Hand.Cards.FirstOrDefault(c => c.IsCardUp);
            if (upCard == null || upCard.FaceVal != CardGames.Platforms.FaceValue.Ace)
                throw new InvalidOperationException("Insurance available only when dealer shows an Ace.");

            // Pre-validate the amount (mirrors Player.PlaceInsurance) so the wallet debit can't succeed and
            // then PlaceInsurance throw. Reserve wallet-first, then place.
            if (amount <= 0) throw new InvalidOperationException("Insurance must be positive.");
            if (amount > insHand.Bet / 2) throw new InvalidOperationException("Insurance cannot exceed half the bet.");

            var (_, insWalletAfter) = await DebitStakeAsync(player.Id, amount, table.TableId, table.CurrentRoundId ?? "", seatNumber, $"ins{handIndex}");
            player.PlaceInsurance(amount, handIndex);
            player.SetBalance(insWalletAfter);
            LogAction(table, HandActionType.Insurance, seatNumber, userId, amount: amount);
            RefreshTurn(table);
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

            // Reserve the extra stake (a second bet for the new hand) wallet-first, then split.
            var splitExtra = splitHand.Bet;
            var (spTx, splitWalletAfter) = await DebitStakeAsync(player.Id, splitExtra, table.TableId, table.CurrentRoundId ?? "", seatNumber, $"sp{handIndex}");

            var newHandIndex = player.Split(handIndex);
            player.GetHand(newHandIndex).StakeTxId = spTx;   // the split stake funded the NEW hand
            player.SetBalance(splitWalletAfter);
            LogAction(table, HandActionType.Split, seatNumber, userId, amount: splitExtra,
                handValueAfter: player.GetHand(handIndex).Hand.GetSumOfHand());

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

            // Only a seated player may trigger settle — no unseated user can force-settle/grief a table.
            if (!table.Seats.Any(s => s.Player != null && s.Player.Id == userId))
                throw new InvalidOperationException("You are not seated at this table.");

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

                    // Stakes already left the wallet at deal/action, so settle only RETURNS money: credit the
                    // GROSS the settlement added to the mirror (gross = post-settle balance - pre-settle).
                    var preSettleBal = preSettleBalance.TryGetValue(player.SeatNumber, out var pb) ? pb : player.Balance;
                    var gross = player.Balance - preSettleBal;
                    if (gross < 0m) gross = 0m;        // defensive — settlement never reduces the mirror
                    var net = player.Balance - start;  // true round net (gross minus everything staked)

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
                        Blackjack = pre.Blackjack
                    });

                    try
                    {
                        decimal newBalance;
                        string txId = null;
                        if (gross > 0m)
                        {
                            (txId, newBalance) = await CreditGrossWithRetryAsync(player.Id, gross, table.TableId, roundId, player.SeatNumber);
                        }
                        else
                        {
                            newBalance = await GetWalletChipsAsync(player.Id); // nothing returned (full loss)
                        }
                        player.SetBalance(newBalance);

                        // One audit row PER HAND (a split yields two): each hand's own stake, outcome, payout,
                        // and funding-debit tx id. The payout credit is a single seat-level :pay transaction, so
                        // its tx id and the BalanceBefore/After audit are recorded once, on the first hand's row.
                        var seatHands = settledBySeat.TryGetValue(player.SeatNumber, out var shs)
                            ? shs : new List<HandSettlement>();
                        for (int hi = 0; hi < seatHands.Count; hi++)
                        {
                            var h = seatHands[hi];
                            participants.Add(new GameHandParticipant
                            {
                                UserId = uid,
                                SeatNumber = player.SeatNumber,
                                HandIndex = h.HandIndex,
                                Bet = h.Bet,
                                InsuranceBet = h.InsuranceBet,
                                Payout = h.Payout,                                  // gross returned for THIS hand
                                FinalHandValue = h.FinalValue,
                                Bust = h.Bust,
                                Blackjack = h.Blackjack,
                                Outcome = h.Outcome,
                                WalletDebitTxId = player.GetHand(h.HandIndex).StakeTxId,
                                WalletCreditTxId = hi == 0 && gross > 0m ? txId : null,  // credit is seat-level (:pay)
                                BalanceBefore = hi == 0 ? start : (decimal?)null,
                                BalanceAfter = hi == 0 ? newBalance : (decimal?)null
                            });
                        }
                        statResults.Add(new RoundResult(uid, pre.Wagered, net));
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
                                Bet = pre.Wagered,
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
                                    Bet = h.Bet,
                                    InsuranceBet = h.InsuranceBet,
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
                }

                // The money credits above are idempotent (per-seat :pay key), but the stats roll-up and the
                // audit insert are NOT — run them at most once per round, even if the settling claim's TTL
                // lapsed mid-settle and a retry re-entered.
                if (await redisService.GetDatabase().StringSetAsync(
                        $"bjr:settled:{roundId}", "1", TimeSpan.FromHours(1), When.NotExists))
                {
                    await PersistHandAsync(table, roundId, participants);
                    await RecordStatsAsync(statResults);
                }
            }
            finally
            {
                // Always finish the round so a seat failure can never wedge the table.
                table.RoundStartBalance?.Clear();
                table.CurrentRoundId = null;
                table.CurrentDeckHash = null;
                table.RoundInProgress = false;
                table.LastResults = lastResults;   // surface per-seat outcomes to the board for the banner
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
            if (!peek.RoundInProgress) return;

            await using var _tableLock = await LockTableAsync(tableId);

            var table = await GetTableAsync(tableId);       // authoritative re-read under the lock
            if (table == null || !table.RoundInProgress) return;

            var changed = false;

            // Auto-stand a player whose turn timer expired, then advance to the next hand.
            if (table.CurrentSeatNumber > 0 && table.TurnExpiresAt.HasValue
                && DateTimeOffset.UtcNow > table.TurnExpiresAt.Value)
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
            table.TurnExpiresAt = DateTimeOffset.UtcNow.AddSeconds(table.TurnDurationSeconds);
        }

        private static void RefreshTurn(BlackjackTable table)
        {
            if (table.CurrentSeatNumber <= 0) return;
            table.TurnExpiresAt = DateTimeOffset.UtcNow.AddSeconds(table.TurnDurationSeconds);
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
            // hand AFTER the current position in canonical (seat asc, hand asc) order — never jump straight to
            // the dealer while later seats/hands still have to act. (Looking up the current position by index
            // would miss-fire here: a Done current hand isn't in the list, so it would resolve to the dealer.)
            int curSeat = table.CurrentSeatNumber;
            int curHand = table.CurrentHandIndex;

            (int seat, int hand)? next = null;
            foreach (var h in GetOrderedHands(table))
            {
                if (h.seat == -1) continue; // dealer sentinel
                if (h.seat > curSeat || (h.seat == curSeat && h.hand > curHand)) { next = h; break; }
            }

            var n = next ?? (seat: -1, hand: 0);
            table.CurrentSeatNumber = n.seat;
            table.CurrentHandIndex = n.hand;
            table.TurnExpiresAt = n.seat == -1 ? null : DateTimeOffset.UtcNow.AddSeconds(table.TurnDurationSeconds);
        }

        private static IEnumerable<(int seat, int hand)> GetOrderedHands(BlackjackTable table)
        {
            foreach (var seat in table.Seats.OrderBy(s => s.SeatNumber))
            {
                if (seat.Player == null || !seat.Player.InRound) continue;
                for (int i = 0; i < seat.Player.Hands.Count; i++)
                {
                    var hand = seat.Player.Hands[i];
                    if (hand.Done || hand.Hand.GetSumOfHand() > 21) continue;
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

        public int CurrentSeatNumber { get; set; } = -1;

        public int CurrentHandIndex { get; set; } = 0;

        public DateTimeOffset? TurnExpiresAt { get; set; }

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
    }

    /// <summary>One seat's outcome for the most recently settled round, surfaced on the board snapshot.</summary>
    public class SeatRoundResult
    {
        public int SeatNumber { get; set; }
        public string Outcome { get; set; }    // "win" | "lose" | "push"
        public decimal Delta { get; set; }       // net chips change this round (signed: + win, - loss, 0 push)
        public decimal Payout { get; set; }      // gross returned to the wallet
        public int FinalHandValue { get; set; }
        public bool Bust { get; set; }
        public bool Blackjack { get; set; }
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
