using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using CardGames.Platforms;
using CardGames.Provable;
using CardGames.ThreeCardPoker;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Progression;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Stats;
using Khela.Game.Services.Wallet;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using LbGameType = Khela.Common.Leaderboards.GameType;

namespace Khela.Game.Games.ThreeCardPoker
{
    /// <summary>
    /// Server-authoritative Three Card Poker table manager (singleton). Mirrors the blackjack money-path exactly —
    /// debit-on-bet at deal, idempotent credit-gross-on-settle keyed on <c>3cp:{round}:{seat}:{suffix}</c>, gifted
    /// taint preserved on payout, per-table Redis lock, and a per-hand audit row — but the game is far simpler:
    /// one Play/Fold decision per seat, the dealer never draws, and settlement is a pure paytable lookup
    /// (<see cref="ThreeCardPokerSettlement"/>). Wagers are Chips only. Blackjack is untouched.
    /// </summary>
    public sealed class ThreeCardPokerTableManager
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IRedisService _redis;
        private readonly IHubContext<ThreeCardPokerHub> _hub;
        private readonly ILogger<ThreeCardPokerTableManager> _logger;
        private readonly bool _progressionEnabled;   // master switch for the game-extension layer (XP/VIP/loyalty/missions), shared with blackjack

        private const string LobbySet = "threecard:tables";
        private static readonly TimeSpan TableTtl = TimeSpan.FromHours(2);
        private static string TableKey(string id) => $"threecard:table:{id}";
        private static string LockKey(string id) => $"tcplock:{id}";

        public ThreeCardPokerTableManager(IServiceScopeFactory scopes, IRedisService redis, IHubContext<ThreeCardPokerHub> hub, ILogger<ThreeCardPokerTableManager> logger, IConfiguration config)
        {
            _scopes = scopes; _redis = redis; _hub = hub; _logger = logger;
            _progressionEnabled = config.GetValue("Progression:Enabled", true);
        }

        // ─────────────────────────────── table state (Redis) ───────────────────────────────

        public Task<ThreeCardPokerTable> GetTableAsync(string tableId) => _redis.GetAsync<ThreeCardPokerTable>(TableKey(tableId));

        private async Task SaveTableAsync(ThreeCardPokerTable t)
        {
            await _redis.SetAsync(TableKey(t.TableId), t, TableTtl);
            await _redis.GetDatabase().SetAddAsync(LobbySet, t.TableId);   // keep it in the lobby index (self-heals on TTL expiry)
            try { await _hub.Clients.Group(ThreeCardPokerHub.TableGroup(t.TableId)).SendAsync("TableUpdated", ThreeCardPokerBoard.Build(t)); }
            catch (Exception ex) { _logger.LogWarning(ex, "3CP board broadcast failed for {TableId}", t.TableId); }
        }

        public async Task<IReadOnlyList<string>> GetActiveTableIdsAsync()
        {
            var members = await _redis.GetDatabase().SetMembersAsync(LobbySet);
            return members.Select(m => m.ToString()).ToList();
        }

        // ─────────────────────────────── lobby (browsable table list) ───────────────────────────────

        /// <summary>The house tables the 3CP lobby always offers (Ante min/max, side min/max), topped up on demand so
        /// a player never lands on an empty lobby. Mirrors blackjack's default-table seeding.</summary>
        private static readonly (decimal anteMin, decimal anteMax, decimal sideMin, decimal sideMax)[] DefaultTables =
        {
            (1000m, 10000m, 1000m, 10000m),
            (5000m, 25000m, 5000m, 25000m),
            (25000m, 100000m, 25000m, 100000m),
        };

        /// <summary>Browsable list of active 3CP tables for the lobby, topping up any missing house tables first (so
        /// the lobby is never empty), and self-healing the index of TTL-expired ids.</summary>
        public async Task<List<ThreeCardPokerTableSummary>> GetLobbyAsync()
        {
            await EnsureDefaultTablesAsync();

            var db = _redis.GetDatabase();
            var ids = await db.SetMembersAsync(LobbySet);
            var summaries = new List<ThreeCardPokerTableSummary>();
            var stale = new List<RedisValue>();

            foreach (var id in ids)
            {
                var t = await GetTableAsync((string)id);
                if (t == null) { stale.Add(id); continue; }   // key TTL-expired → drop from the index
                summaries.Add(new ThreeCardPokerTableSummary
                {
                    TableId = t.TableId,
                    MaxPlayers = t.MaxPlayers,
                    SeatsOccupied = t.Seats.Count(s => s.Player != null),
                    AnteMin = t.Limits.AnteMin,
                    AnteMax = t.Limits.AnteMax,
                    SideMin = t.Limits.SideMin,
                    SideMax = t.Limits.SideMax,
                    RoundInProgress = t.RoundInProgress,
                    Phase = t.Phase,
                });
            }
            if (stale.Count > 0) await db.SetRemoveAsync(LobbySet, stale.ToArray());

            return summaries.OrderBy(s => s.AnteMin).ThenBy(s => s.TableId).ToList();
        }

        /// <summary>Creates ONLY the default house tables currently missing (matched by ante+side limits), under a
        /// short NX seed-lock so concurrent lobby loads never duplicate them.</summary>
        public async Task EnsureDefaultTablesAsync()
        {
            var db = _redis.GetDatabase();
            var existing = await LoadLobbyLimitsAsync(db);
            bool Missing((decimal a, decimal b, decimal c, decimal d) x) =>
                !existing.Any(e => e.AnteMin == x.a && e.AnteMax == x.b && e.SideMin == x.c && e.SideMax == x.d);
            if (!DefaultTables.Any(Missing)) return;   // fast path — full set present

            var token = Guid.NewGuid().ToString("N");
            if (!await db.StringSetAsync("threecard:tables:seedlock", token, TimeSpan.FromSeconds(10), When.NotExists))
                return;   // another load is already seeding
            try
            {
                existing = await LoadLobbyLimitsAsync(db);   // re-check under the lock
                foreach (var d in DefaultTables)
                    if (!existing.Any(e => e.AnteMin == d.anteMin && e.AnteMax == d.anteMax && e.SideMin == d.sideMin && e.SideMax == d.sideMax))
                        await CreateTableAsync(5, 1, d.anteMin, d.anteMax, d.sideMin, d.sideMax);
            }
            finally
            {
                const string lua = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
                try { await db.ScriptEvaluateAsync(lua, new RedisKey[] { "threecard:tables:seedlock" }, new RedisValue[] { token }); } catch { }
            }
        }

        private async Task<List<TcpBetLimits>> LoadLobbyLimitsAsync(IDatabase db)
        {
            var ids = await db.SetMembersAsync(LobbySet);
            var limits = new List<TcpBetLimits>();
            foreach (var id in ids)
            {
                var t = await GetTableAsync((string)id);
                if (t != null) limits.Add(t.Limits);
            }
            return limits;
        }

        // ─────────────────────────────── per-table lock ───────────────────────────────

        private async Task<string> AcquireLockAsync(string tableId, int timeoutMs = 5000)
        {
            var db = _redis.GetDatabase();
            var token = Guid.NewGuid().ToString("N");
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (await db.StringSetAsync(LockKey(tableId), token, TimeSpan.FromSeconds(10), When.NotExists)) return token;
                await Task.Delay(25);
            }
            throw new InvalidOperationException($"Could not acquire lock for 3CP table {tableId}.");
        }

        private async Task ReleaseLockAsync(string tableId, string token)
        {
            const string lua = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
            try { await _redis.GetDatabase().ScriptEvaluateAsync(lua, new RedisKey[] { LockKey(tableId) }, new RedisValue[] { token }); }
            catch (Exception ex) { _logger.LogWarning(ex, "3CP lock release failed for {TableId}", tableId); }
        }

        // ─────────────────────────────── lifecycle ───────────────────────────────

        public async Task<ThreeCardPokerTable> CreateTableAsync(int maxPlayers, int maxSeatsPerUser, decimal anteMin, decimal anteMax, decimal sideMin, decimal sideMax)
        {
            var (seedHex, hashHex) = NewServerSeed();
            var table = new ThreeCardPokerTable
            {
                TableId = Guid.NewGuid().ToString(),
                MaxPlayers = Math.Clamp(maxPlayers, 1, 7),
                MaxSeatsPerUser = Math.Clamp(maxSeatsPerUser, 1, 7),
                Limits = new TcpBetLimits { AnteMin = anteMin, AnteMax = anteMax, SideMin = sideMin, SideMax = sideMax },
                ServerSeed = seedHex,
                ServerSeedHash = hashHex,
                ClientSeed = Guid.NewGuid().ToString("N"),
                RoundNonce = 0,
            };
            table.Seats = Enumerable.Range(0, table.MaxPlayers).Select(i => new TcpSeat { SeatNumber = i }).ToList();
            await SaveTableAsync(table);
            return table;
        }

        public async Task<ThreeCardPokerTable> AddPlayerAsync(string tableId, string userId, string name, string image, int? seatNumber)
        {
            var token = await AcquireLockAsync(tableId);
            try
            {
                var t = await GetTableAsync(tableId);
                if (t == null) return null;

                if (t.Seats.Count(s => s.Player?.Id == userId) >= t.MaxSeatsPerUser)
                    throw new InvalidOperationException("Seat limit per user reached at this table.");

                TcpSeat seat = seatNumber.HasValue
                    ? t.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber.Value)
                    : t.Seats.FirstOrDefault(s => s.Player == null);
                if (seat == null) throw new InvalidOperationException("Requested seat is unavailable.");
                if (seat.Player != null) throw new InvalidOperationException("Seat already taken.");

                seat.Player = new TcpPlayer { Id = userId, Name = name, Image = image };
                seat.IsConnected = true;
                seat.LastHeartbeatAt = DateTime.UtcNow;
                await SaveTableAsync(t);
                return t;
            }
            finally { await ReleaseLockAsync(tableId, token); }
        }

        public async Task<ThreeCardPokerTable> RemovePlayerAsync(string tableId, string userId, int seatNumber)
        {
            var token = await AcquireLockAsync(tableId);
            try
            {
                var t = await GetTableAsync(tableId);
                if (t == null) return null;
                var seat = t.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber && s.Player?.Id == userId);
                if (seat == null) throw new InvalidOperationException("You do not occupy that seat.");
                // Money-safety §5: never pull a seat with a live stake mid-round.
                if (t.RoundInProgress && seat.InRound)
                    throw new InvalidOperationException("Cannot leave mid-round with a live stake.");
                ClearSeatRound(seat);
                seat.Player = null;
                await SaveTableAsync(t);
                return t;
            }
            finally { await ReleaseLockAsync(tableId, token); }
        }

        public async Task RecordHeartbeatAsync(string tableId, string userId)
        {
            var token = await AcquireLockAsync(tableId);
            try
            {
                var t = await GetTableAsync(tableId);
                if (t == null) return;
                var seat = t.Seats.FirstOrDefault(s => s.Player?.Id == userId);
                if (seat == null) return;
                seat.LastHeartbeatAt = DateTime.UtcNow;
                seat.IsConnected = true; seat.IsStalled = false;
                await SaveTableAsync(t);
            }
            finally { await ReleaseLockAsync(tableId, token); }
        }

        // ─────────────────────────────── betting ───────────────────────────────

        public async Task<ThreeCardPokerTable> PlaceBetsAsync(string tableId, string userId, PlaceThreeCardBetsRequest req)
        {
            var token = await AcquireLockAsync(tableId);
            try
            {
                var t = await GetTableAsync(tableId);
                if (t == null) return null;
                if (t.Phase == "complete") ReopenBetting(t);   // last round's results are shown; a new bet reopens betting
                if (t.Phase != "betting") throw new InvalidOperationException("Betting is closed for this round.");
                var seat = t.Seats.FirstOrDefault(s => s.SeatNumber == req.SeatNumber && s.Player?.Id == userId)
                    ?? throw new InvalidOperationException("You do not occupy that seat.");

                ValidateBets(t.Limits, req);   // per-circle min/max; throws on out-of-range

                seat.Ante = req.Ante; seat.PairPlus = req.PairPlus; seat.Prime = req.Prime; seat.SixCard = req.SixCard;
                seat.InRound = req.Ante > 0m;   // Ante is mandatory to be dealt
                seat.Decided = false; seat.Played = false; seat.Cards = new();
                await SaveTableAsync(t);
                return t;
            }
            finally { await ReleaseLockAsync(tableId, token); }
        }

        private static void ValidateBets(TcpBetLimits l, PlaceThreeCardBetsRequest r)
        {
            if (r.Ante < 0 || r.PairPlus < 0 || r.Prime < 0 || r.SixCard < 0)
                throw new InvalidOperationException("Bets cannot be negative.");
            if (r.Ante > 0 && (r.Ante < l.AnteMin || r.Ante > l.AnteMax))
                throw new InvalidOperationException($"Ante must be between {l.AnteMin} and {l.AnteMax}.");
            foreach (var (name, v) in new[] { ("Pair Plus", r.PairPlus), ("Prime", r.Prime), ("6-Card", r.SixCard) })
                if (v > 0 && (v < l.SideMin || v > l.SideMax))
                    throw new InvalidOperationException($"{name} must be between {l.SideMin} and {l.SideMax}.");
            if ((r.PairPlus > 0 || r.Prime > 0 || r.SixCard > 0) && r.Ante <= 0)
                throw new InvalidOperationException("An Ante is required to be dealt (side-bet-only play is not offered).");
        }

        // ─────────────────────────────── deal (debit-on-bet) ───────────────────────────────

        public async Task<ThreeCardPokerTable> DealAsync(string tableId)
        {
            var token = await AcquireLockAsync(tableId);
            try
            {
                var t = await GetTableAsync(tableId);
                if (t == null) return null;
                if (t.Phase != "betting") throw new InvalidOperationException("A round is already in progress.");

                var betters = t.Seats.Where(s => s.Player != null && s.InRound && s.Ante > 0m).ToList();
                if (betters.Count == 0) throw new InvalidOperationException("No seats have posted an Ante.");

                var roundId = Guid.NewGuid().ToString();

                // Reserve every pre-deal stake up front (Ante + side bets). A seat that can't cover is refunded
                // whatever it managed to debit and dropped from the round — its chips are never stranded.
                var dealt = new List<TcpSeat>();
                foreach (var seat in betters)
                {
                    try { await ReserveSeatStakesAsync(seat, tableId, roundId); dealt.Add(seat); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "3CP seat {Seat} could not reserve stakes; sitting out round {Round}.", seat.SeatNumber, roundId);
                        ClearSeatRound(seat);
                    }
                }
                if (dealt.Count == 0) throw new InvalidOperationException("No seat could cover its stake.");

                // Deal from the provably-fair shuffle: one hand per reserved seat + the dealer's 3.
                var seed = ProvableShuffle.DeriveSeed(Convert.FromHexString(t.ServerSeed), t.ClientSeed ?? "", t.RoundNonce);
                var game = new ThreeCardPokerGame();
                game.DealNewGame(dealt.Count, seed);
                for (int i = 0; i < dealt.Count; i++) dealt[i].Cards = game.Seats[i].Cards;
                t.DealerCards = game.DealerCards;
                t.CurrentDeckHash = game.DeckHash();

                t.CurrentRoundId = roundId;
                t.RoundInProgress = true;
                t.RoundStartedAt = DateTime.UtcNow;
                t.DealerRevealed = false;
                t.Phase = "acting";
                t.DecideExpiresAt = DateTime.UtcNow.AddSeconds(t.DecideDurationSeconds);
                foreach (var s in dealt) { s.Decided = false; s.Played = false; s.Outcome = null; s.LastReturn = 0m; }

                await SaveTableAsync(t);
                return t;
            }
            finally { await ReleaseLockAsync(tableId, token); }
        }

        private async Task ReserveSeatStakesAsync(TcpSeat seat, string tableId, string roundId)
        {
            var uid = seat.Player.Id;
            var debited = new List<(decimal amt, decimal gifted, string suffix)>();
            try
            {
                async Task Reserve(decimal amt, string suffix)
                {
                    if (amt <= 0m) return;
                    var (_, _, gifted) = await DebitStakeAsync(uid, amt, tableId, roundId, seat.SeatNumber, suffix);
                    debited.Add((amt, gifted, suffix));
                }
                await Reserve(seat.Ante, "ante");
                await Reserve(seat.PairPlus, "pp");
                await Reserve(seat.Prime, "prime");
                await Reserve(seat.SixCard, "six");
            }
            catch
            {
                foreach (var d in debited)   // roll back this seat's partial reservation
                    await RefundStakeAsync(uid, d.amt, tableId, roundId, seat.SeatNumber, d.gifted, d.suffix + "rf");
                throw;
            }
        }

        // ─────────────────────────────── the one decision: Play / Fold ───────────────────────────────

        public Task<ThreeCardPokerTable> PlayAsync(string tableId, string userId, int seatNumber) => DecideAsync(tableId, userId, seatNumber, play: true);
        public Task<ThreeCardPokerTable> FoldAsync(string tableId, string userId, int seatNumber) => DecideAsync(tableId, userId, seatNumber, play: false);

        private async Task<ThreeCardPokerTable> DecideAsync(string tableId, string userId, int seatNumber, bool play)
        {
            var token = await AcquireLockAsync(tableId);
            try
            {
                var t = await GetTableAsync(tableId);
                if (t == null) return null;
                if (t.Phase != "acting") throw new InvalidOperationException("It is not the acting phase.");
                var seat = t.Seats.FirstOrDefault(s => s.SeatNumber == seatNumber && s.Player?.Id == userId)
                    ?? throw new InvalidOperationException("You do not occupy that seat.");
                if (!seat.InRound || seat.Decided) throw new InvalidOperationException("You have no pending decision.");

                if (play)
                {
                    // PLAY posts a bet exactly equal to the Ante (debit-on-bet). If it can't be covered, the caller
                    // must fold instead — nothing is half-committed.
                    await DebitStakeAsync(userId, seat.Ante, tableId, t.CurrentRoundId, seatNumber, "play");
                    seat.Played = true;
                }
                seat.Decided = true;

                await MaybeSettleAsync(t);   // reveal + settle once every seat has acted
                await SaveTableAsync(t);
                return t;
            }
            finally { await ReleaseLockAsync(tableId, token); }
        }

        // ─────────────────────────────── reveal + settle (credit-gross-on-settle) ───────────────────────────────

        private async Task MaybeSettleAsync(ThreeCardPokerTable t)
        {
            if (t.Phase != "acting" || !t.RoundInProgress) return;
            var live = t.Seats.Where(s => s.InRound).ToList();
            if (live.Any(s => !s.Decided)) return;   // still waiting on a decision

            t.DealerRevealed = true;
            var roundId = t.CurrentRoundId;
            var participants = new List<GameHandParticipant>();
            var statResults = new List<RoundResult>();

            foreach (var seat in live)
            {
                var bets = new ThreeCardPokerBets { Ante = seat.Ante, PairPlus = seat.PairPlus, Prime = seat.Prime, SixCard = seat.SixCard };
                var result = ThreeCardPokerSettlement.Settle(seat.Cards, t.DealerCards, bets, seat.Played, t.Paytables);

                // Credit the total gross return, preserving the stake's gifted fraction (no laundering on a win).
                var (totalStake, giftedStake) = await GetSeatStakeSplitAsync(seat.Player.Id, roundId, seat.SeatNumber);
                decimal giftedCredit = (totalStake > 0m && result.TotalReturn > 0m)
                    ? Math.Round(result.TotalReturn * (giftedStake / totalStake), 4, MidpointRounding.ToZero)
                    : 0m;

                string payTxId = null;
                if (result.TotalReturn > 0m)
                    (payTxId, _) = await CreditGrossWithRetryAsync(seat.Player.Id, result.TotalReturn, t.TableId, roundId, seat.SeatNumber, giftedCredit);

                seat.Outcome = result.Outcome;
                seat.LastReturn = result.TotalReturn;
                seat.PayoutTxId = payTxId;

                if (Guid.TryParse(seat.Player.Id, out var uid))
                {
                    participants.Add(new GameHandParticipant
                    {
                        UserId = uid,
                        SeatNumber = seat.SeatNumber,
                        HandIndex = 0,
                        Bet = seat.Ante,
                        Payout = result.TotalReturn,
                        Outcome = result.Outcome,
                        WalletCreditTxId = payTxId,
                        MetadataJson = JsonSerializer.Serialize(new
                        {
                            bets = new { seat.Ante, seat.PairPlus, seat.Prime, seat.SixCard, played = seat.Played },
                            returns = new
                            {
                                result.AnteReturn, result.PlayReturn, result.AnteBonus,
                                result.PairPlusReturn, result.PrimeReturn, result.SixCardReturn
                            },
                            dealerQualified = result.DealerQualified
                        }),
                    });

                    // Game-extension accrual (XP/VIP/loyalty/missions/stats). Runs AFTER the wallet has settled, so a
                    // failure here can never affect money. cleanWager = the EARNED (non-gifted) staked total across all
                    // circles — the XP basis; net = gross return − everything staked this round (signed).
                    decimal cleanWager = totalStake - giftedStake;
                    decimal net = result.TotalReturn - totalStake;
                    bool win = net > 0m;
                    var statCounters = new Dictionary<string, long>
                    {
                        ["handsWon"] = win ? 1 : 0,                          // read by WinHands missions
                        ["pairPlusWins"] = result.PairPlusReturn > 0m ? 1 : 0,   // 3CP-specific lifetime flavor
                    };
                    long grantedXp = _progressionEnabled
                        ? await AccrueProgressionAsync(uid, cleanWager, win, roundId)
                        : 0L;
                    statResults.Add(new RoundResult(uid, totalStake, net, cleanWager, grantedXp, statCounters));
                    if (_progressionEnabled)
                    {
                        await AccrueVipAsync(uid, cleanWager, roundId);
                        await AccrueLoyaltyAsync(uid, cleanWager, roundId);
                        await AccrueMissionsAsync(uid, statCounters, cleanWager, roundId);
                    }
                }
            }

            // PersistHand + RecordStats are NOT individually idempotent (unlike the per-seat :pay credits), so each
            // gets its own at-most-once guard: a crash between them only re-runs the unfinished one on retry.
            var rdb = _redis.GetDatabase();
            if (await rdb.StringSetAsync($"3cp:audited:{roundId}", "1", TimeSpan.FromHours(1), When.NotExists))
                await PersistHandAsync(t, participants);
            if (statResults.Count > 0 && await rdb.StringSetAsync($"3cp:stats:{roundId}", "1", TimeSpan.FromHours(1), When.NotExists))
                await RecordStatsAsync(statResults);

            // Round over: keep the result on the board, roll the provably-fair nonce, reopen betting.
            t.RoundInProgress = false;
            t.Phase = "complete";
            t.DecideExpiresAt = null;
            t.RoundNonce += 1;
        }

        // ─────────────────────────────── round-driver tick (auto-fold on timeout) ───────────────────────────────

        public async Task TickTableAsync(string tableId)
        {
            var peek = await GetTableAsync(tableId);
            if (peek == null || peek.Phase != "acting" || peek.DecideExpiresAt == null || peek.DecideExpiresAt > DateTime.UtcNow) return;

            var token = await AcquireLockAsync(tableId);
            try
            {
                var t = await GetTableAsync(tableId);
                if (t == null || t.Phase != "acting" || t.DecideExpiresAt == null || t.DecideExpiresAt > DateTime.UtcNow) return;

                // Timeout = auto-FOLD every undecided seat (never risk more money without consent).
                foreach (var seat in t.Seats.Where(s => s.InRound && !s.Decided)) { seat.Played = false; seat.Decided = true; }
                await MaybeSettleAsync(t);
                await SaveTableAsync(t);
            }
            finally { await ReleaseLockAsync(tableId, token); }
        }

        // ─────────────────────────────── wallet helpers (mirror blackjack) ───────────────────────────────

        private async Task<(string TxId, decimal Balance, decimal GiftedSpent)> DebitStakeAsync(string userId, decimal amount, string tableId, string roundId, int seat, string suffix)
        {
            using var scope = _scopes.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            var ctx = new WalletContext { TableId = tableId, RoundId = roundId, Description = $"3CP {suffix} round {roundId} seat {seat}" };
            var txn = await wallet.DebitAsync(userId, CurrencyType.Chips, amount, TransactionType.Bet, $"3cp:{roundId}:{seat}:{suffix}", ctx);
            return (txn.TransactionId.ToString(), txn.BalanceAfter ?? 0m, Math.Abs(txn.GiftedDelta));
        }

        private async Task<(string TxId, decimal Balance)> CreditGrossWithRetryAsync(string userId, decimal gross, string tableId, string roundId, int seat, decimal giftedCredit)
        {
            const int attempts = 3;
            for (int i = 1; ; i++)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
                    var ctx = new WalletContext { TableId = tableId, RoundId = roundId, Description = $"3CP payout round {roundId} seat {seat}", CreditGiftedAmount = giftedCredit };
                    var txn = await wallet.CreditAsync(userId, CurrencyType.Chips, gross, TransactionType.Win, $"3cp:{roundId}:{seat}:pay", ctx);
                    return (txn.TransactionId.ToString(), txn.BalanceAfter ?? 0m);
                }
                catch (Exception ex) when (i < attempts)
                {
                    _logger.LogWarning(ex, "3CP payout credit attempt {Attempt} failed for seat {Seat} round {Round}; retrying.", i, seat, roundId);
                    await Task.Delay(150 * i);
                }
            }
        }

        private async Task RefundStakeAsync(string userId, decimal amount, string tableId, string roundId, int seat, decimal giftedRestore, string suffix)
        {
            using var scope = _scopes.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            var ctx = new WalletContext { TableId = tableId, RoundId = roundId, Description = $"3CP stake refund round {roundId} seat {seat}", CreditGiftedAmount = giftedRestore };
            await wallet.CreditAsync(userId, CurrencyType.Chips, amount, TransactionType.Refund, $"3cp:{roundId}:{seat}:{suffix}", ctx);
        }

        /// <summary>Sums this seat's Bet debits for the round into (total, gifted) so a payout keeps the stake's
        /// gifted fraction. Seat-scoped so a user's other seats never pool their taint ratio.</summary>
        private async Task<(decimal Total, decimal Gifted)> GetSeatStakeSplitAsync(string userId, string roundId, int seat)
        {
            if (!Guid.TryParse(userId, out var uid) || string.IsNullOrEmpty(roundId)) return (0m, 0m);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var w = await db.PlayerWallets.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid && x.Currency == CurrencyType.Chips);
            if (w == null) return (0m, 0m);
            var prefix = $"3cp:{roundId}:{seat}:";
            var rows = await db.WalletTransactions.AsNoTracking()
                .Where(x => x.WalletId == w.WalletId && x.Type == TransactionType.Bet && x.RoundId == roundId
                            && x.CorrelationId != null && x.CorrelationId.StartsWith(prefix))
                .Select(x => new { x.Amount, x.GiftedDelta }).ToListAsync();
            decimal total = 0m, gifted = 0m;
            foreach (var r in rows) { total += Math.Abs(r.Amount); gifted += Math.Abs(r.GiftedDelta); }
            return (total, gifted);
        }

        // ─────────────────────────────── game-extension accrual (mirror blackjack; all best-effort) ───────────────────────────────

        /// <summary>Grants progression XP for a settled seat from its EARNED (clean) wager (System A; idempotent +
        /// daily-capped inside the service). Best-effort + wrapped — the wallet already settled.</summary>
        private async Task<long> AccrueProgressionAsync(Guid userId, decimal cleanWager, bool win, string roundId)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var progression = scope.ServiceProvider.GetRequiredService<IProgressionService>();
                return await progression.AccrueForRoundAsync(userId, cleanWager, win, roundId);
            }
            catch (Exception ex) { _logger.LogError(ex, "3CP progression accrual failed for user {UserId} round {RoundId}", userId, roundId); return 0; }
        }

        /// <summary>Accrues VIP Status Points from the clean wager (flat ×1, daily-capped, never from winnings; §3).
        /// Idempotent per (round, user). Best-effort — never breaks settle.</summary>
        private async Task AccrueVipAsync(Guid userId, decimal cleanWager, string roundId)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var vip = scope.ServiceProvider.GetRequiredService<Khela.Game.Services.Vip.IVipService>();
                await vip.AccrueForRoundAsync(userId, cleanWager, roundId);
            }
            catch (Exception ex) { _logger.LogError(ex, "3CP VIP accrual failed for user {UserId} round {RoundId}", userId, roundId); }
        }

        /// <summary>Accrues Loyalty Points from the clean wager × the player's VIP multiplier (§4). Idempotent per
        /// (round, user). Best-effort — never breaks settle.</summary>
        private async Task AccrueLoyaltyAsync(Guid userId, decimal cleanWager, string roundId)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var loyalty = scope.ServiceProvider.GetRequiredService<Khela.Game.Services.Loyalty.ILoyaltyService>();
                await loyalty.AccrueForRoundAsync(userId, cleanWager, roundId);
            }
            catch (Exception ex) { _logger.LogError(ex, "3CP loyalty accrual failed for user {UserId} round {RoundId}", userId, roundId); }
        }

        /// <summary>Advances daily-mission progress from the round's events (round count, clean wager, win). Idempotent
        /// per (round, user). Best-effort — never breaks settle.</summary>
        private async Task AccrueMissionsAsync(Guid userId, IReadOnlyDictionary<string, long> statCounters, decimal cleanWager, string roundId)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var missions = scope.ServiceProvider.GetRequiredService<Khela.Game.Services.Missions.IMissionService>();
                await missions.ReportRoundAsync(userId, statCounters, cleanWager, roundId);
            }
            catch (Exception ex) { _logger.LogError(ex, "3CP mission progress failed for user {UserId} round {RoundId}", userId, roundId); }
        }

        /// <summary>Rolls the settled round's per-seat results into durable player stats (UserGameStats + UserProfile
        /// + windowed daily) under the ThreeCardPoker leaderboard vertical. Best-effort — never affects money.</summary>
        private async Task RecordStatsAsync(List<RoundResult> results)
        {
            if (results.Count == 0) return;
            try
            {
                using var scope = _scopes.CreateScope();
                var stats = scope.ServiceProvider.GetRequiredService<IPlayerStatsService>();
                await stats.RecordRoundResultsAsync(LbGameType.ThreeCardPoker, results);   // map, don't cast (ledger GameType diverges)
            }
            catch (Exception ex) { _logger.LogError(ex, "3CP stats roll-up failed for round"); }
        }

        // ─────────────────────────────── audit ───────────────────────────────

        private async Task PersistHandAsync(ThreeCardPokerTable t, List<GameHandParticipant> participants)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var header = new GameHandHeader
                {
                    TableId = t.TableId,
                    GameType = GameType.ThreeCardPoker,
                    RoundId = t.CurrentRoundId,
                    StartedAt = t.RoundStartedAt ?? DateTime.UtcNow,
                    SettledAt = DateTime.UtcNow,
                    Status = HandStatus.Settled,
                    ShoeId = t.ServerSeedHash,
                    ShuffleSeed = $"{t.ClientSeed}:{t.RoundNonce}",   // public commitment; serverSeed revealed at rotation
                    DeckHash = t.CurrentDeckHash,
                    PrevHandHash = t.LastHandHash,
                };
                foreach (var p in participants) p.HandId = header.HandId;
                db.GameHandHeaders.Add(header);
                db.GameHandParticipants.AddRange(participants);
                await db.SaveChangesAsync();
                t.LastHandId = header.HandId.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "3CP audit persist failed for round {Round}", t.CurrentRoundId);   // never break settle
            }
        }

        // ─────────────────────────────── helpers ───────────────────────────────

        private static void ClearSeatRound(TcpSeat s)
        {
            s.Ante = s.PairPlus = s.Prime = s.SixCard = 0m;
            s.InRound = s.Decided = s.Played = false;
            s.Cards = new();
        }

        /// <summary>Reset a "complete" table back to open betting for a new round (keeps each seat's last-result
        /// banner via Outcome/LastReturn, which a new bet overwrites).</summary>
        private static void ReopenBetting(ThreeCardPokerTable t)
        {
            t.Phase = "betting";
            t.RoundInProgress = false;
            t.DealerCards = new();
            t.DealerRevealed = false;
            t.CurrentRoundId = null;
            t.DecideExpiresAt = null;
            foreach (var s in t.Seats) ClearSeatRound(s);
        }

        private static (string seedHex, string hashHex) NewServerSeed()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            using var sha = SHA256.Create();
            return (Convert.ToHexString(bytes).ToLowerInvariant(), Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant());
        }
    }
}
