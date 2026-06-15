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
        /// Applies a round's net chip delta to the authoritative wallet and returns the new balance.
        /// Idempotent on the round+seat correlation id, so a retried settle never double-pays.
        /// </summary>
        private async Task<(string TxId, decimal Balance)> ApplyRoundNetAsync(string userId, decimal net, string tableId, string roundId, int seat)
        {
            using var scope = scopeFactory.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            var ctx = new WalletContext { TableId = tableId, RoundId = roundId, Description = $"Blackjack round {roundId} seat {seat}" };
            // roundId is itself a GUID, so it alone makes the key unique — keep it short to fit
            // WalletTransaction.CorrelationId (MaxLength 64). (table id lives in WalletContext.)
            var correlationId = $"bjr:{roundId}:{seat}";

            if (net > 0m)
            {
                var txn = await wallet.CreditAsync(userId, CurrencyType.Chips, net, TransactionType.Win, correlationId, ctx);
                return (txn.TransactionId.ToString(), txn.BalanceAfter ?? 0m);
            }
            if (net < 0m)
            {
                var txn = await wallet.DebitAsync(userId, CurrencyType.Chips, -net, TransactionType.Bet, correlationId, ctx);
                return (txn.TransactionId.ToString(), txn.BalanceAfter ?? 0m);
            }
            return (null, await wallet.GetBalanceAsync(userId, CurrencyType.Chips));
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

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.GameHandHeaders.Add(header);
                db.GameHandParticipants.AddRange(participants);
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

        // Save updated table back
        public async Task SaveTableAsync(string tableId, BlackjackTable table)
        {
            table.UpdatedAt = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(table);
            await redisService.GetDatabase().StringSetAsync(GetKey(tableId), json, TimeSpan.FromHours(2)); // TTL 2h

            // Live update: every state change pushes the masked board to this table's subscribers.
            await hubContext.Clients.Group($"table:{tableId}").SendAsync("TableUpdated", BlackjackBoard.Build(table));
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
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;
            var wasCurrentTurn = table.RoundInProgress && table.CurrentSeatNumber == seatNumber;

            // Leaving mid-round FORFEITS the in-progress wager — debit whatever was staked so a player
            // can't dodge a losing hand by leaving. Same correlation id as settle, so it's idempotent.
            if (table.RoundInProgress && player.InRound
                && table.RoundStartBalance != null
                && table.RoundStartBalance.TryGetValue(seatNumber, out var roundStart))
            {
                var net = player.Balance - roundStart; // negative: staked but not yet returned
                if (net < 0)
                    await ApplyRoundNetAsync(player.Id, net, table.TableId, table.CurrentRoundId ?? "", seatNumber);
                table.RoundStartBalance.Remove(seatNumber);
            }

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

            // Defensive: tables created before provably-fair seeds existed get one lazily.
            if (string.IsNullOrEmpty(table.ServerSeed))
            {
                var sb = RandomNumberGenerator.GetBytes(32);
                table.ServerSeed = Convert.ToHexString(sb);
                table.ServerSeedHash = Convert.ToHexString(SHA256.HashData(sb)).ToLowerInvariant();
                if (string.IsNullOrEmpty(table.ClientSeed))
                    table.ClientSeed = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            }

            // Capture each wager BEFORE dealing — DealNewGame resets the hands (which would wipe the
            // bet, so wins/pushes paid nothing); we re-apply it to the freshly dealt hand afterwards.
            // Only players who placed a bet join THIS round; anyone else (e.g. someone who sat down
            // mid-round) stays seated and waits for the next deal.
            var wagers = new Dictionary<int, decimal>();
            foreach (var player in table.Game.Players)
            {
                var bet = player.Hands.Count > 0 ? player.Hands[0].Bet : 0m;
                player.InRound = bet > 0;
                if (!player.InRound)
                    continue;

                var chips = await GetWalletChipsAsync(player.Id);
                if (chips < bet)
                    throw new InvalidOperationException($"Player {player.Id} has insufficient chips for the bet.");

                player.SetBalance(chips);
                table.RoundStartBalance[player.SeatNumber] = chips;
                wagers[player.SeatNumber] = bet;
            }

            if (wagers.Count == 0)
                throw new InvalidOperationException("No bets placed — at least one seated player must bet to deal.");

            // Provably-fair seeded 6-deck shoe for this round.
            var roundSeed = ProvableShuffle.DeriveSeed(
                Convert.FromHexString(table.ServerSeed), table.ClientSeed, table.RoundNonce);
            table.Game.DealNewGame(roundSeed, 6);

            // Audit: hash the full shuffled shoe (recomputed deterministically from the same seed)
            // and stamp the round start, both persisted to GameHandHeader at settle.
            var shoeForHash = new Deck(6);
            shoeForHash.Shuffle(roundSeed);
            table.CurrentDeckHash = shoeForHash.ComputeHash();
            table.RoundStartedAt = DateTimeOffset.UtcNow;

            // Re-apply each captured wager to the freshly dealt hand and deduct it from the mirror,
            // so the bet survives to settlement and wins/pushes actually pay out.
            foreach (var player in table.Game.Players)
            {
                if (wagers.TryGetValue(player.SeatNumber, out var bet) && bet > 0)
                {
                    player.IncreaseBet(bet, 0);
                    player.PlaceBet(0);
                    player.ClearInsurance(0);
                }
            }

            MarkNaturals(table);
            SetInitialTurn(table);
            table.RoundInProgress = true;
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<(BlackjackTable? Table, HitResult? Result)> HitAsync(string tableId, string userId, int seatNumber, int handIndex = 0)
        {
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

            var result = player.DoubleDown(handIndex);
            AdvanceTurn(table);
            await SaveTableAsync(tableId, table);
            return (table, result);
        }

        public async Task<BlackjackTable?> PlaceInsuranceAsync(string tableId, string userId, int seatNumber, decimal amount, int handIndex = 0)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            var player = seat.Player;
            if (player.GetHand(handIndex).InsuranceBet > 0) throw new InvalidOperationException("Insurance already placed.");

            var upCard = table.Game.Dealer.Hand.Cards.FirstOrDefault(c => c.IsCardUp);
            if (upCard == null || upCard.FaceVal != CardGames.Platforms.FaceValue.Ace)
                throw new InvalidOperationException("Insurance available only when dealer shows an Ace.");

            player.PlaceInsurance(amount, handIndex);
            RefreshTurn(table);
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> SplitAsync(string tableId, string userId, int seatNumber, int handIndex = 0)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            seat.Player.Split(handIndex);
            RefreshTurn(table);
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> DealerPlayAndSettleAsync(string tableId)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            // Resolve any still-pending player turns (auto-stand them) before the dealer plays, so a
            // dealerPlay call can't settle other seats before they've acted.
            int guard = 0;
            while (table.CurrentSeatNumber > 0 && guard++ < 64)
            {
                AdvanceTurn(table);
            }

            table.Game.DealerPlay();

            // Capture gross wager + final hand state per seat BEFORE settle zeroes the bets.
            var preSettle = table.Game.Players.ToDictionary(
                p => p.SeatNumber,
                p => (
                    Wagered: p.Hands.Sum(h => h.Bet + h.InsuranceBet),
                    FinalValue: p.Hands[0].Hand.GetSumOfHand(),
                    Bust: p.Hands.Any(h => h.Hand.GetSumOfHand() > 21),
                    Blackjack: p.HasBlackJack(0)
                ));

            SettleRound(table.Game);

            var roundId = table.CurrentRoundId ?? "";
            var participants = new List<GameHandParticipant>();
            var statResults = new List<RoundResult>();

            // Reconcile each player's NET result to the authoritative wallet, sync the mirror, audit it.
            foreach (var player in table.Game.Players)
            {
                if (!player.InRound) continue; // waiting players didn't play this round — no settle/audit

                var start = table.RoundStartBalance != null
                            && table.RoundStartBalance.TryGetValue(player.SeatNumber, out var s)
                    ? s
                    : player.Balance;
                var net = player.Balance - start;
                var (txId, newBalance) = await ApplyRoundNetAsync(player.Id, net, table.TableId, roundId, player.SeatNumber);
                player.SetBalance(newBalance);

                var pre = preSettle.TryGetValue(player.SeatNumber, out var ps)
                    ? ps
                    : (Wagered: 0m, FinalValue: 0, Bust: false, Blackjack: false);

                Guid.TryParse(player.Id, out var uid);
                participants.Add(new GameHandParticipant
                {
                    UserId = uid,
                    SeatNumber = player.SeatNumber,
                    Bet = pre.Wagered,
                    Payout = pre.Wagered + net,            // gross returned (wins + pushes); 0 on a loss
                    FinalHandValue = pre.FinalValue,
                    Bust = pre.Bust,
                    Blackjack = pre.Blackjack,
                    Outcome = net > 0 ? "win" : net < 0 ? "lose" : "push",
                    WalletCreditTxId = net > 0 ? txId : null,
                    WalletDebitTxId = net < 0 ? txId : null,
                    BalanceBefore = start,
                    BalanceAfter = newBalance
                });
                statResults.Add(new RoundResult(uid, pre.Wagered, net));
            }

            await PersistHandAsync(table, roundId, participants);
            await RecordStatsAsync(statResults);

            table.RoundStartBalance?.Clear();
            table.CurrentRoundId = null;
            table.CurrentDeckHash = null;
            table.RoundInProgress = false;
            await SaveTableAsync(tableId, table);
            return table;
        }

        public async Task<BlackjackTable?> StandAsync(string tableId, string userId, int seatNumber, int handIndex = 0)
        {
            var table = await GetTableAsync(tableId);
            if (table == null) return null;

            if (!table.RoundInProgress)
                throw new InvalidOperationException("Round not in progress.");

            EnsureTurn(table, seatNumber, handIndex);

            var seat = table.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber);
            if (seat == null || seat.Player == null || seat.Player.Id != userId)
                throw new InvalidOperationException("Seat not occupied by this player.");

            seat.Player.Stand(handIndex);
            AdvanceTurn(table);
            await SaveTableAsync(tableId, table);
            return table;
        }

        private static void SettleRound(BlackJackGame game)
        {
            var dealerTotal = game.Dealer.Hand.GetSumOfHand();
            var dealerBust = dealerTotal > 21;
            var dealerBlackjack = dealerTotal == 21 && game.Dealer.Hand.Cards.Count == 2;

            foreach (var player in game.Players)
            {
                if (!player.InRound) continue; // waiting players didn't play this round

                for (int i = 0; i < player.Hands.Count; i++)
                {
                    var handState = player.Hands[i];
                    var playerTotal = handState.Hand.GetSumOfHand();

                    if (handState.InsuranceBet > 0)
                    {
                        if (dealerBlackjack)
                        {
                            player.AddInsurancePayout(handState.InsuranceBet);
                        }
                        handState.InsuranceBet = 0;
                    }

                    if (playerTotal > 21)
                    {
                        player.AddLoss(i);
                        continue;
                    }

                    // A natural blackjack is a 2-card 21 on an unsplit hand; it pays 3:2.
                    var playerNatural = player.Hands.Count == 1 && handState.Hand.Cards.Count == 2 && playerTotal == 21;
                    if (playerNatural)
                    {
                        if (dealerBlackjack) player.AddPush(i);   // both naturals -> push
                        else player.AddWin(2.5m, i);              // 3:2 (returns 2.5x the stake)
                        continue;
                    }

                    if (dealerBlackjack)                          // dealer natural beats any non-natural hand
                    {
                        player.AddLoss(i);
                        continue;
                    }

                    if (dealerBust)
                    {
                        player.AddWin(2, i);
                        continue;
                    }

                    if (playerTotal > dealerTotal)
                    {
                        player.AddWin(2, i);
                    }
                    else if (playerTotal == dealerTotal)
                    {
                        player.AddPush(i);
                    }
                    else
                    {
                        player.AddLoss(i);
                    }
                }
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

        private static void AdvanceTurn(BlackjackTable table)
        {
            var active = GetOrderedHands(table).ToList();
            var idx = active.FindIndex(x => x.seat == table.CurrentSeatNumber && x.hand == table.CurrentHandIndex);
            var next = idx >= 0 && idx + 1 < active.Count ? active[idx + 1] : (seat: -1, hand: 0);

            table.CurrentSeatNumber = next.seat;
            table.CurrentHandIndex = next.hand;
            table.TurnExpiresAt = next.seat == -1 ? null : DateTimeOffset.UtcNow.AddSeconds(table.TurnDurationSeconds);
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
    }

    public class Seat
    {
        public int SeatNumber { get; set; }
        public Player? Player { get; set; }
    }
}
