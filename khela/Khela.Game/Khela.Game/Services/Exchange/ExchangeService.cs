using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Exchange;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Rewards;
using Khela.Game.Services.Store;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Khela.Game.Services.Exchange
{
    /// <summary>
    /// Currency exchange — A → B at an admin-authored rate (docs/EXCHANGE_SPEC.md). The ONE place two wallets move against
    /// each other outside a table: debit A, credit B, inside one transaction, idempotent on the client's request id. Nothing
    /// is minted: every unit of B was paid for in A at the catalog rate, the rate is applied exactly (the player chooses the
    /// B amount; cost = amount × rate — never a floor), Tokens can never be on either side, and a pair plus its reverse must
    /// be lossy (validator). Per-player caps are counted from <see cref="CurrencyExchange"/> rows.
    /// </summary>
    public interface IExchangeService
    {
        /// <summary>The effective catalog: the Redis override if it parses AND validates, else code defaults. Cached ~15 s.</summary>
        Task<ExchangeCatalogConfig> GetConfigAsync();
        /// <summary>Every pair with THIS player's availability, usage against the caps, and balances — one call for the screen.</summary>
        Task<ExchangeCatalogDto> GetCatalogAsync(Guid userId);
        /// <summary>"What would it cost?" — the server's arithmetic and refusal reasons, no writes.</summary>
        Task<ExchangeQuoteDto> QuoteAsync(Guid userId, ExchangeQuoteRequest req);
        /// <summary>Do it. Idempotent on <see cref="ExchangeRequest.RequestId"/>.</summary>
        Task<ExchangeResultDto> ExchangeAsync(Guid userId, ExchangeRequest req, CancellationToken ct = default);
        Task<List<ExchangeRecordDto>> HistoryAsync(Guid userId, int take);
        /// <summary>Admin save: validate fail-closed, write the Redis document, drop the cache. Returns the first error or null.</summary>
        Task<string> SaveAsync(ExchangeCatalogConfig cfg);
        void Invalidate();
    }

    public sealed class ExchangeService : IExchangeService
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
        private static readonly object Gate = new object();
        private static ExchangeCatalogConfig _cached;
        private static DateTime _cachedAtUtc;

        private readonly AppDbContext _db;
        private readonly IRedisService _redis;
        private readonly IWalletService _wallet;
        private readonly IConfiguration _config;
        private readonly ILogger<ExchangeService> _logger;

        public ExchangeService(AppDbContext db, IRedisService redis, IWalletService wallet, IConfiguration config, ILogger<ExchangeService> logger)
        {
            _db = db; _redis = redis; _wallet = wallet; _config = config; _logger = logger;
        }

        // ------------------------------------------------------------------ config

        public async Task<ExchangeCatalogConfig> GetConfigAsync()
        {
            lock (Gate)
            {
                if (_cached != null && DateTime.UtcNow - _cachedAtUtc < CacheTtl) return _cached;
            }
            ExchangeCatalogConfig cfg = null;
            try
            {
                var json = await _redis.GetDatabase().StringGetAsync(ExchangeCatalog.RedisKey);
                if (json.HasValue)
                {
                    cfg = ExchangeCatalog.TryParse(json);
                    if (cfg == null) _logger.LogWarning("khela:exchange override is unparseable — falling back to the default catalog.");
                    else
                    {
                        var err = ExchangeCatalog.Validate(cfg);
                        if (err != null)
                        {
                            // Fail CLOSED to the defaults: a catalog with a wrong rate is worse than none.
                            _logger.LogError("khela:exchange override is INVALID ({Error}) — falling back to the default catalog.", err);
                            cfg = null;
                        }
                    }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "khela:exchange could not be read — falling back to the default catalog."); }
            cfg ??= ExchangeCatalog.Defaults();
            lock (Gate) { _cached = cfg; _cachedAtUtc = DateTime.UtcNow; }
            return cfg;
        }

        public async Task<string> SaveAsync(ExchangeCatalogConfig cfg)
        {
            var err = ExchangeCatalog.Validate(cfg);
            if (err != null) return err;
            await _redis.GetDatabase().StringSetAsync(ExchangeCatalog.RedisKey, ExchangeCatalog.ToJson(cfg));
            Invalidate();
            _logger.LogInformation("Exchange catalog saved: {Count} pairs, version {Version}.", cfg.Pairs.Count, cfg.Version);
            return null;
        }

        public void Invalidate()
        {
            lock (Gate) { _cached = null; _cachedAtUtc = default; }
        }

        private Task<bool> SwitchOnAsync() => StoreSwitches.BoolAsync(_redis, _config, ExchangeCatalog.EnabledSwitch, true);

        // ------------------------------------------------------------------ catalog / quote

        public async Task<ExchangeCatalogDto> GetCatalogAsync(Guid userId)
        {
            var cfg = await GetConfigAsync();
            bool on = cfg.Enabled && await SwitchOnAsync();
            var now = DateTime.UtcNow;
            var level = await LevelAsync(userId);
            var usage = await UsageAsync(userId, null, now);
            var balances = await _wallet.GetBalancesAsync(userId.ToString());

            var pairs = new List<ExchangePairDto>();
            foreach (var p in cfg.Pairs.Where(p => p != null && p.Enabled).OrderBy(p => p.SortOrder).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                usage.TryGetValue(p.Key, out var used);
                // Availability of the PAIR for this player (amount-independent): probe with its own minimum.
                var reason = on ? ExchangeCatalog.Refusal(p, p.MinTo, used.Today, used.Lifetime, level, now) : "The exchange is closed right now.";
                pairs.Add(new ExchangePairDto
                {
                    Key = p.Key, Title = p.Title, Description = p.Description,
                    FromCurrency = p.FromCurrency, ToCurrency = p.ToCurrency, FromPerUnit = p.FromPerUnit,
                    Step = p.Step, MinTo = p.MinTo, MaxToPerTx = p.MaxToPerTx, DailyCapTo = p.DailyCapTo, LifetimeCapTo = p.LifetimeCapTo,
                    MinLevel = p.MinLevel, SortOrder = p.SortOrder, AvailableToUtc = p.ToUtc,
                    Available = reason == null, Reason = reason,
                    UsedToday = used.Today, UsedLifetime = used.Lifetime,
                });
            }

            return new ExchangeCatalogDto
            {
                Enabled = on,
                Pairs = pairs,
                Balances = balances.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                ServerTimeUtc = now,
            };
        }

        public async Task<ExchangeQuoteDto> QuoteAsync(Guid userId, ExchangeQuoteRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.PairKey)) return new ExchangeQuoteDto { Ok = false, Error = "Missing pair." };
            var cfg = await GetConfigAsync();
            var p = cfg.Find(req.PairKey);
            if (p == null || !p.Enabled) return new ExchangeQuoteDto { Ok = false, Error = "This exchange is not available.", PairKey = req.PairKey };
            if (!cfg.Enabled || !await SwitchOnAsync()) return new ExchangeQuoteDto { Ok = false, Error = "The exchange is closed right now.", PairKey = p.Key };

            RewardCurrencies.TryParse(p.FromCurrency, out var from);
            RewardCurrencies.TryParse(p.ToCurrency, out var to);
            var now = DateTime.UtcNow;
            var usage = await UsageAsync(userId, p.Key, now);
            usage.TryGetValue(p.Key, out var used);
            var reason = ExchangeCatalog.Refusal(p, req.ToAmount, used.Today, used.Lifetime, await LevelAsync(userId), now);
            var cost = reason == null ? ExchangeCatalog.Cost(p, req.ToAmount) : 0m;
            var fromBalance = await _wallet.GetBalanceAsync(userId.ToString(), from);
            var toBalance = await _wallet.GetBalanceAsync(userId.ToString(), to);
            if (reason == null && fromBalance < cost) reason = $"Not enough {p.FromCurrency}.";

            return new ExchangeQuoteDto
            {
                Ok = reason == null, Error = reason, PairKey = p.Key,
                FromCurrency = p.FromCurrency, ToCurrency = p.ToCurrency,
                FromAmount = cost, ToAmount = req.ToAmount,
                FromBalance = fromBalance, ToBalance = toBalance,
            };
        }

        // ------------------------------------------------------------------ exchange (the money path)

        public async Task<ExchangeResultDto> ExchangeAsync(Guid userId, ExchangeRequest req, CancellationToken ct = default)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.PairKey)) return Fail("Missing pair.");
            if (req.RequestId == Guid.Empty) return Fail("Missing request id.");

            // ---- replay? The request id is the idempotency key: a retry returns the original outcome and moves nothing.
            // Checked BEFORE the catalog, so a completed exchange replays even if its pair was disabled or deleted since. ----
            var row = await _db.CurrencyExchanges.FirstOrDefaultAsync(x => x.UserId == userId && x.RequestId == req.RequestId, ct);
            if (row != null && row.Status == "Completed")
                return await ResultAsync(row, replayed: true);

            var cfg = await GetConfigAsync();
            var p = cfg.Find(req.PairKey);
            if (p == null) return Fail("This exchange is not available.", req.PairKey);
            if (!p.Enabled) return Fail("This exchange is not available.", p.Key);
            if (!cfg.Enabled || !await SwitchOnAsync()) return Fail("The exchange is closed right now.", p.Key);
            RewardCurrencies.TryParse(p.FromCurrency, out var from);
            RewardCurrencies.TryParse(p.ToCurrency, out var to);
            if (!RewardCurrencies.IsAllowed(from) || !RewardCurrencies.IsAllowed(to) || from == to)   // belt-and-braces: the validator already forbids this
                return Fail("This exchange is not available.", p.Key);

            var now = DateTime.UtcNow;
            var usage = await UsageAsync(userId, p.Key, now);
            usage.TryGetValue(p.Key, out var used);
            var reason = ExchangeCatalog.Refusal(p, req.ToAmount, used.Today, used.Lifetime, await LevelAsync(userId), now);
            if (reason != null) return Fail(reason, p.Key);
            var cost = ExchangeCatalog.Cost(p, req.ToAmount);

            // ---- reserve the row (unique (UserId, RequestId) is the mutex against a double tap) ----
            if (row == null)
            {
                row = new CurrencyExchange
                {
                    UserId = userId, RequestId = req.RequestId, PairKey = p.Key,
                    FromCurrency = from, FromAmount = cost, ToCurrency = to, ToAmount = req.ToAmount,
                    RateFromPerUnit = p.FromPerUnit, Status = "Pending", CreatedAt = now,
                };
                _db.CurrencyExchanges.Add(row);
                try { await _db.SaveChangesAsync(ct); }
                catch (DbUpdateException)
                {
                    _db.ChangeTracker.Clear();
                    row = await _db.CurrencyExchanges.FirstOrDefaultAsync(x => x.UserId == userId && x.RequestId == req.RequestId, ct);
                    if (row == null) return Fail("Could not start the exchange; try again.", p.Key);
                    if (row.Status == "Completed") return await ResultAsync(row, replayed: true);
                }
            }
            else
            {
                // A Pending/Failed row being re-driven (nothing moved for it — the wallet never committed). The request id is
                // PINNED to the exchange it was first used for: same pair, same currencies, same TO amount — otherwise a Failed
                // row reserved under a cheap pair could be completed under an expensive one at the cheap cost. It is RE-PRICED
                // at the current catalog (no money was promised by a row that never paid), and its clock restarts, so Failed
                // rows can't be banked at an old rate or outside a later day's cap.
                if (!string.Equals(row.PairKey, p.Key, StringComparison.OrdinalIgnoreCase) || row.FromCurrency != from || row.ToCurrency != to)
                    return Fail("This request id was already used for a different exchange.", p.Key);
                if (row.ToAmount != req.ToAmount) return Fail("This request id was already used for a different amount.", p.Key);
                row.FromAmount = cost;
                row.RateFromPerUnit = p.FromPerUnit;
                row.CreatedAt = now;
                row.Status = "Pending"; row.Error = null; row.CompletedAt = null;
                await SaveQuietlyAsync(ct);
            }

            // Make sure both wallets exist BEFORE the money transaction (reward-claim-latency rule: never create a wallet inside it).
            await _wallet.GetOrCreateWalletAsync(userId.ToString(), from);
            await _wallet.GetOrCreateWalletAsync(userId.ToString(), to);

            // ---- ONE transaction: debit A, credit B, complete the row. WalletService joins the ambient transaction. ----
            var debitKey = "xchg:" + row.Id.ToString("N") + ":d";
            var creditKey = "xchg:" + row.Id.ToString("N") + ":c";
            var ctx = new WalletContext { ExternalRef = "exchange:" + p.Key, Description = $"Exchange {p.FromCurrency} → {p.ToCurrency} ({p.Key})" };
            decimal newFrom, newTo;
            await using (var tx = await _db.Database.BeginTransactionAsync(ct))
            {
                WalletTransaction debit;
                try
                {
                    debit = await _wallet.DebitAsync(userId.ToString(), from, row.FromAmount, TransactionType.Exchange, debitKey, ctx);
                }
                catch (InsufficientFundsException)
                {
                    await tx.RollbackAsync(ct);
                    row.Status = "Failed"; row.Error = $"Not enough {p.FromCurrency}."; row.CompletedAt = DateTime.UtcNow;
                    await SaveQuietlyAsync(ct);
                    return Fail(row.Error, p.Key);
                }

                // The caps, re-checked UNDER the FROM-wallet lock the debit just took, with a LOCKING read of this player's rows:
                // two taps with different request ids both pass the check above (plain reads), but only one holds the wallet row
                // at a time — the second sees the first's committed row here and is refused. Without this a daily cap is a
                // suggestion. (The pre-check above stays: it answers the common refusal without taking a lock.)
                if (p.DailyCapTo > 0m || p.LifetimeCapTo > 0m)
                {
                    var locked = await UsageLockedAsync(userId, p.Key, now, ct);
                    var late = ExchangeCatalog.Refusal(p, row.ToAmount, locked.Today, locked.Lifetime, int.MaxValue, now);   // level already passed; only the caps can differ
                    if (late != null)
                    {
                        await tx.RollbackAsync(ct);
                        row.Status = "Failed"; row.Error = late; row.CompletedAt = DateTime.UtcNow;
                        await SaveQuietlyAsync(ct);
                        return Fail(late, p.Key);
                    }
                }

                var credit = await _wallet.CreditAsync(userId.ToString(), to, row.ToAmount, TransactionType.Exchange, creditKey, ctx);
                if (debit == null || credit == null)
                {
                    await tx.RollbackAsync(ct);
                    row.Status = "Failed"; row.Error = "The wallet did not accept the movement."; row.CompletedAt = DateTime.UtcNow;
                    await SaveQuietlyAsync(ct);
                    return Fail("The exchange could not be applied; try again.", p.Key);
                }
                newFrom = debit.BalanceAfter ?? await _wallet.GetBalanceAsync(userId.ToString(), from);
                newTo = credit.BalanceAfter ?? await _wallet.GetBalanceAsync(userId.ToString(), to);

                row.Status = "Completed"; row.Error = null; row.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            _logger.LogInformation("Exchange {Pair} for {User}: {FromAmount} {From} → {ToAmount} {To} (rate {Rate}, {Id}).",
                p.Key, userId, row.FromAmount, from, row.ToAmount, to, row.RateFromPerUnit, row.Id);

            return await ResultAsync(row, replayed: false, newFrom, newTo);
        }

        public async Task<List<ExchangeRecordDto>> HistoryAsync(Guid userId, int take)
        {
            take = Math.Clamp(take, 1, 200);
            return await _db.CurrencyExchanges.AsNoTracking()
                .Where(x => x.UserId == userId && x.Status == "Completed")
                .OrderByDescending(x => x.CreatedAt).Take(take)
                .Select(x => new ExchangeRecordDto
                {
                    Id = x.Id, PairKey = x.PairKey, FromCurrency = x.FromCurrency.ToString(), FromAmount = x.FromAmount,
                    ToCurrency = x.ToCurrency.ToString(), ToAmount = x.ToAmount, CreatedAtUtc = x.CreatedAt,
                })
                .ToListAsync();
        }

        // ------------------------------------------------------------------ helpers

        private async Task<ExchangeResultDto> ResultAsync(CurrencyExchange row, bool replayed, decimal? newFrom = null, decimal? newTo = null)
        {
            var balances = await _wallet.GetBalancesAsync(row.UserId.ToString());
            decimal Bal(CurrencyType c) => balances.TryGetValue(c, out var v) ? v : 0m;
            return new ExchangeResultDto
            {
                Ok = true, Replayed = replayed, ExchangeId = row.Id, PairKey = row.PairKey,
                FromCurrency = row.FromCurrency.ToString(), FromAmount = row.FromAmount,
                ToCurrency = row.ToCurrency.ToString(), ToAmount = row.ToAmount,
                NewFromBalance = newFrom ?? Bal(row.FromCurrency), NewToBalance = newTo ?? Bal(row.ToCurrency),
                Balances = balances.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                NewChipBalance = Bal(CurrencyType.Chips), NewKashBalance = Bal(CurrencyType.Kash),
            };
        }

        private static ExchangeResultDto Fail(string error, string pairKey = null) => new ExchangeResultDto { Ok = false, Error = error, PairKey = pairKey };

        private struct Used { public decimal Today; public decimal Lifetime; }

        /// <summary>What the player has taken through each pair (TO units): today (UTC) and ever. One query.</summary>
        private async Task<Dictionary<string, Used>> UsageAsync(Guid userId, string pairKey, DateTime nowUtc)
        {
            var dayStart = nowUtc.Date;
            var q = _db.CurrencyExchanges.AsNoTracking().Where(x => x.UserId == userId && x.Status == "Completed");
            if (pairKey != null) q = q.Where(x => x.PairKey == pairKey);
            // "Today" is judged on COMPLETION (a re-driven row completes under today's cap, whatever day it was first reserved).
            var rows = await q.GroupBy(x => x.PairKey)
                .Select(g => new { Key = g.Key, Lifetime = g.Sum(x => x.ToAmount), Today = g.Where(x => (x.CompletedAt ?? x.CreatedAt) >= dayStart).Sum(x => (decimal?)x.ToAmount) ?? 0m })
                .ToListAsync();
            return rows.ToDictionary(r => r.Key, r => new Used { Today = r.Today, Lifetime = r.Lifetime }, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The same usage, as a LOCKING read (<c>FOR UPDATE</c>, the pattern WalletService uses on the wallet row) — inside the
        /// exchange transaction, after the FROM-wallet lock, so it sees every row a concurrent exchange by the same player has
        /// committed. Plain reads in REPEATABLE READ would show the snapshot from before the lock.
        /// </summary>
        private async Task<Used> UsageLockedAsync(Guid userId, string pairKey, DateTime nowUtc, CancellationToken ct)
        {
            var dayStart = nowUtc.Date;
            // No LINQ composition on top of the raw SQL (a Select would wrap it in a derived table and move the FOR UPDATE
            // inside a subquery) — the statement runs exactly as written; the sums are done here.
            var rows = await _db.CurrencyExchanges
                .FromSqlInterpolated($"SELECT * FROM `CurrencyExchanges` WHERE `UserId` = {userId} AND `PairKey` = {pairKey} AND `Status` = 'Completed' FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(ct);
            return new Used { Lifetime = rows.Sum(r => r.ToAmount), Today = rows.Where(r => (r.CompletedAt ?? r.CreatedAt) >= dayStart).Sum(r => r.ToAmount) };
        }

        private async Task<int> LevelAsync(Guid userId)
        {
            var level = await _db.UserProfiles.AsNoTracking().Where(u => u.UserId == userId).Select(u => (int?)u.Level).FirstOrDefaultAsync();
            return level ?? 1;
        }

        private async Task SaveQuietlyAsync(CancellationToken ct)
        {
            try { await _db.SaveChangesAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Exchange row save failed."); }
        }
    }
}
