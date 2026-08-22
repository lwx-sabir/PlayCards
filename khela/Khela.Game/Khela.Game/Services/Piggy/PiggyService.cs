using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Khela.Common.Piggy;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Khela.Game.Services.Piggy
{
    public interface IPiggyService
    {
        /// <summary>
        /// Bank a share of a settled round's CLEAN (non-gifted) wager. Idempotent per (round, user). Returns what was
        /// actually banked, after the bank's remaining capacity and today's cap.
        /// </summary>
        Task<decimal> AccrueForRoundAsync(Guid userId, decimal cleanWager, decimal netLoss, string roundId);

        /// <summary>The bank as the player sees it. Never null — a disabled feature returns <c>Enabled = false</c>.
        /// A pure read: it settles an expired window but never starts one.</summary>
        Task<PiggyStateDto> GetStateAsync(Guid userId);

        /// <summary>
        /// The player is LOOKING at a full bank. Starts the countdown if it hasn't started, and returns the state
        /// with the clock running.
        ///
        /// Separate from <see cref="GetStateAsync"/> on purpose. A read happens for all sorts of reasons — a HUD
        /// refresh after a round, a screen the player never scrolled to — and starting a deadline on any of them
        /// would burn windows nobody was ever shown. The client calls this at the moment it renders the ready state,
        /// and nowhere else.
        /// </summary>
        Task<PiggyStateDto> MarkSeenAsync(Guid userId);

        /// <summary>
        /// The chips have just flown into the pig — bank the acknowledgement so the next celebration measures from
        /// here. Called after the animation plays, not before it: an acknowledgement that lands first would lose the
        /// moment entirely if the client died mid-burst.
        /// </summary>
        Task<PiggyStateDto> MarkCelebratedAsync(Guid userId);

        /// <summary>
        /// Buy the bank: pay out, record the sale, and reset it.
        /// </summary>
        /// <param name="option">Which offer the player took. The payout differs per option and is decided HERE.</param>
        /// <param name="purchaseId">
        /// The store's order id, and the idempotency key. Stores deliver the same purchase more than once as a
        /// matter of course, so a payout that trusted "I was called" rather than "this order has not been paid"
        /// would mint chips every time one arrived.
        /// </param>
        Task<PiggyBreakResultDto> BreakAsync(Guid userId, PiggyBreakOption option, string purchaseId);

        /// <summary>
        /// Buy the bank with a purchase the STORE has already verified (docs/IAP_SPEC.md §5.2 g). Same payout rules, same
        /// one-transaction shape and the same idempotency on <paramref name="purchaseId"/> as <see cref="BreakAsync"/>, with
        /// two differences the verified money earns: it never needs <c>Piggy:BypassPurchase</c>, and it never under-delivers —
        /// a Full/×2 bought while the bank moved underneath the tap (expired, reset) pays the bank's capacity rather than
        /// refusing a charged card. The credit is typed <paramref name="creditType"/> (PaidPurchase for real money).
        /// <paramref name="soldTier"/> is the rung the product was sold for (its catalog <c>params.tier</c>; 0 = unknown):
        /// a product for another rung than the bank's caps the payout at the sold rung's capacity — see
        /// <see cref="PiggyMath.PayoutBase"/> for why that is a money rule, not a nicety.
        /// </summary>
        Task<PiggyBreakResultDto> BreakVerifiedAsync(Guid userId, PiggyBreakOption option, string purchaseId, string priceSku,
            Guid storePurchaseId, Khela.Game.Database.Models.TransactionType creditType, int soldTier);
    }

    /// <summary>
    /// The piggy bank: chips banked by PLAYING, released only by PAYING.
    ///
    /// The single fact that shapes this class is that accruing mints nothing. The number on the bar is a promise the
    /// player can buy, not a balance they hold — so this is a running row rather than a ledger, its idempotency guard
    /// lives in Redis rather than costing a database round trip, and a lost accrual is a cosmetic bug rather than a
    /// money one. The audit lives on <see cref="PiggyBreak"/>, where the chips are actually created.
    ///
    /// Runs off the settle roll-up alongside progression / VIP / loyalty, and like them it is BEST-EFFORT: the wallet
    /// has already moved the player's money by the time this is called, and no bonus feature may endanger that.
    /// </summary>
    public sealed class PiggyService : IPiggyService
    {
        private const string SettingsHashKey = "khela:settings";

        private readonly AppDbContext _db;
        private readonly IRedisService _redis;
        private readonly IWalletService _wallet;
        private readonly ILogger<PiggyService> _logger;
        private readonly PiggyConfig _cfg;

        public PiggyService(AppDbContext db, IRedisService redis, IWalletService wallet, IConfiguration config,
            ILogger<PiggyService> logger)
        {
            _db = db; _redis = redis; _wallet = wallet; _logger = logger;
            _cfg = new PiggyConfig
            {
                Enabled                 = config.GetValue("Piggy:Enabled", false),
                Mode                    = config.GetValue("Piggy:Mode", PiggyMode.Wager),
                WagerRatePercent        = config.GetValue("Piggy:WagerRatePercent", 50m),
                LossRatePercent         = config.GetValue("Piggy:LossRatePercent", 0m),
                MaxAccrualPerDayPercent = config.GetValue("Piggy:MaxAccrualPerDayPercent", 25m),
                MinBreakPercent         = config.GetValue("Piggy:MinBreakPercent", 100m),
                MinFlyAmount            = config.GetValue("Piggy:MinFlyAmount", 100_000m),
                CycleHours              = config.GetValue("Piggy:CycleHours", 72),
                BypassPurchase          = config.GetValue("Piggy:BypassPurchase", false),
                // Tiers keep the PiggyConfig code defaults.
            };
        }

        // ---------------- accrual ----------------

        public async Task<decimal> AccrueForRoundAsync(Guid userId, decimal cleanWager, decimal netLoss, string roundId)
        {
            var cfg = await EffectiveAsync();
            if (!cfg.Enabled || string.IsNullOrEmpty(roundId)) return 0m;

            var wanted = PiggyMath.Accrual(cleanWager, netLoss, cfg);
            if (wanted <= 0m) return 0m;

            // Idempotency in REDIS, not in a table. One round trip to a local server instead of a row per hand in a
            // table that would earn nothing: a replayed accrual can unlock the offer marginally early, it cannot
            // create chips. Same guard LoyaltyService uses, same 30-day life.
            var guard = $"piggyacc:{roundId}:{userId}";
            try
            {
                if (!await _redis.GetDatabase().StringSetAsync(guard, "1", TimeSpan.FromDays(30), When.NotExists))
                    return 0m;
            }
            catch (Exception ex)
            {
                // Redis down: bank it anyway. A double accrual here is worth far less than a player whose bar stops
                // moving for the duration of an outage.
                _logger.LogWarning(ex, "Piggy idempotency guard unavailable for round {RoundId}; accruing anyway", roundId);
            }

            for (int attempt = 1; ; attempt++)
            {
                var bank = await _db.PlayerPiggyBanks.FirstOrDefaultAsync(p => p.UserId == userId);
                if (bank == null)
                {
                    bank = await CreateAsync(userId, cfg);
                    if (bank == null) return 0m;
                }

                var now = DateTime.UtcNow;
                Expire(bank, now, cfg);         // the window may have run out since the last round

                var today = now.Date;
                var accruedToday = bank.AccrualDateUtc?.Date == today ? bank.AccruedToday : 0m;

                var fits = PiggyMath.Fit(wanted, bank.Amount, bank.MaxAmount, accruedToday, cfg);
                if (fits <= 0m) return 0m;      // full, or done for today

                bank.Amount += fits;

                // It just became buyable. Record WHEN, but start no clock: the deadline belongs to the player's
                // decision, and they cannot decide about an offer they have not been shown yet.
                if (bank.ReadyAtUtc == null && PiggyMath.CanBreak(bank.Amount, bank.MaxAmount, cfg))
                    bank.ReadyAtUtc = now;

                bank.LifetimeAccrued += fits;
                bank.AccrualDateUtc = today;
                bank.AccruedToday = accruedToday + fits;
                bank.UpdatedAt = now;

                try
                {
                    await _db.SaveChangesAsync();
                    return fits;
                }
                catch (DbUpdateConcurrencyException) when (attempt < 4)
                {
                    // Two seats of the same player settling together. Re-read and re-apply — the amount is a running
                    // total, so the loser of the race simply adds on top of the winner's write.
                    _db.ChangeTracker.Clear();
                }
            }
        }

        // ---------------- read ----------------

        public async Task<PiggyStateDto> GetStateAsync(Guid userId)
        {
            var cfg = await EffectiveAsync();
            if (!cfg.Enabled) return new PiggyStateDto { Enabled = false };

            // TRACKED, not AsNoTracking: a window that ran out while the player was away has to be settled here too,
            // or the screen shows a bank the next wager is about to wipe. Reading is the only moment many players
            // give us — a sweeper job would either lag behind this or have to run for every player who never returns.
            var bank = await _db.PlayerPiggyBanks.FirstOrDefaultAsync(p => p.UserId == userId);
            if (bank == null)
            {
                // Nothing banked yet — show the empty bank they would get rather than hiding the feature until their
                // first wager, so the widget is on screen when the first chips land in it.
                var newTier = PiggyMath.TierFor(await LevelAsync(userId), cfg);
                return new PiggyStateDto
                {
                    Enabled = true, Tier = newTier.Tier, Max = newTier.Max,
                    PriceSku = newTier.PriceSku, MinFlyAmount = cfg.MinFlyAmount,
                };
            }

            var now = DateTime.UtcNow;
            bool dirty = Expire(bank, now, cfg);

            // Re-read the ladder while we have the player's level in hand.
            //
            // Capacity is snapshotted so a config edit can't strand someone mid-fill — but a snapshot that NEVER
            // updates means a bank keeps the size it was opened with for the life of the account, however far the
            // player levels. So it is raised here, on the read: a bigger bank can't harm anyone, it only means more
            // to fill.
            //
            // Never while the bank is BUYABLE, though. Retracting an offer the player has already been shown — a full
            // bank that silently becomes a third full — is the one version of this that costs trust.
            var level = await LevelAsync(userId);
            var (tier, max, _) = PiggyMath.TierFor(level, cfg);

            if (max > bank.MaxAmount && !PiggyMath.CanBreak(bank.Amount, bank.MaxAmount, cfg))
            {
                _logger.LogInformation("Piggy tier raised for {UserId}: level {Level} → tier {Tier}, {Old} → {New}",
                    userId, level, tier, bank.MaxAmount, max);

                bank.Tier = tier;
                bank.MaxAmount = max;
                bank.UpdatedAt = now;
                dirty = true;
            }

            if (dirty)
            {
                try { await _db.SaveChangesAsync(); }
                catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); }   // a settle beat us to it; its write stands
            }

            var today = now.Date;
            var accruedToday = bank.AccrualDateUtc?.Date == today ? bank.AccruedToday : 0m;
            var dailyCap = PiggyMath.DailyCap(bank.MaxAmount, cfg);
            var (_, _, priceSku) = PiggyMath.TierFor(level, cfg);   // level already read above — no second round trip

            return new PiggyStateDto
            {
                Enabled = true,
                Amount = bank.Amount,
                Max = bank.MaxAmount,
                Percent = PiggyMath.Percent01(bank.Amount, bank.MaxAmount),
                Tier = bank.Tier,
                CanBreak = PiggyMath.CanBreak(bank.Amount, bank.MaxAmount, cfg),
                PriceSku = priceSku,
                AccruedToday = accruedToday,
                DailyCapReached = dailyCap > 0m && accruedToday >= dailyCap,
                MinFlyAmount = cfg.MinFlyAmount,
                UnseenAccrued = bank.Amount > bank.CelebratedAmount ? bank.Amount - bank.CelebratedAmount : 0m,
                ExpiresAtUtc = bank.ExpiresAtUtc,
                SecondsLeft = PiggyMath.SecondsLeft(now, bank.ExpiresAtUtc),
                WindowSeconds = cfg.CycleHours > 0 ? cfg.CycleHours * 3600L : 0L,
                // The client shows the timer label off THIS, not off SecondsLeft: a full bank the player has not been
                // shown yet has no clock, and zero seconds would read as "expired" rather than "not started".
                TimerRunning = bank.ExpiresAtUtc != null,
                LifetimeAccrued = bank.LifetimeAccrued,
                BreaksCount = bank.BreaksCount,
            };
        }

        public async Task<PiggyStateDto> MarkSeenAsync(Guid userId)
        {
            var cfg = await EffectiveAsync();
            if (!cfg.Enabled) return new PiggyStateDto { Enabled = false };

            var bank = await _db.PlayerPiggyBanks.FirstOrDefaultAsync(p => p.UserId == userId);
            if (bank != null)
            {
                var now = DateTime.UtcNow;
                bool dirty = Expire(bank, now, cfg);

                // Only a bank that is actually buyable starts a clock, and only the first sighting counts — a player
                // re-opening the screen must not keep pushing their own deadline back.
                if (bank.SeenAtUtc == null && PiggyMath.CanBreak(bank.Amount, bank.MaxAmount, cfg))
                {
                    bank.SeenAtUtc = now;
                    bank.ExpiresAtUtc = PiggyMath.WindowEnd(now, cfg);
                    bank.UpdatedAt = now;
                    dirty = true;

                    _logger.LogInformation("Piggy shown full to {UserId}: {Amount} chips, decide by {Expires}",
                        userId, bank.Amount, bank.ExpiresAtUtc?.ToString("u") ?? "never");
                }

                if (dirty)
                {
                    try { await _db.SaveChangesAsync(); }
                    catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); }
                }
            }

            return await GetStateAsync(userId);
        }

        /// <summary>
        /// Buy the bank. The one method here that moves money, so everything about it is deliberately paranoid.
        ///
        /// What it will NOT do:
        ///  • trust the client about how full the bank is — <see cref="PiggyMath.CanBreak"/> is re-checked here
        ///  • trust the client about the payout — the amount is derived from the row, never accepted
        ///  • pay out without a verified purchase unless <c>Piggy:BypassPurchase</c> is explicitly on
        ///  • pay twice for one order id
        ///
        /// The wallet credit, the audit row and the reset all share ONE transaction. Half of this happening is the
        /// failure that costs real money in both directions: a reset without a payout robs the player, and a payout
        /// without a reset lets them sell the same bank again.
        /// </summary>
        public Task<PiggyBreakResultDto> BreakAsync(Guid userId, PiggyBreakOption option, string purchaseId)
            => BreakCoreAsync(userId, option, purchaseId, verified: false, priceSku: null, storePurchaseId: null, creditType: TransactionType.Purchase, soldTier: 0);

        public Task<PiggyBreakResultDto> BreakVerifiedAsync(Guid userId, PiggyBreakOption option, string purchaseId, string priceSku,
            Guid storePurchaseId, TransactionType creditType, int soldTier)
            => BreakCoreAsync(userId, option, purchaseId, verified: true, priceSku: priceSku, storePurchaseId: storePurchaseId, creditType: creditType, soldTier: soldTier);

        /// <summary>
        /// The one break path. <paramref name="verified"/> = the money has been taken by a store and verified by the server
        /// (the store spine), so the purchase gate is satisfied and the bank must never under-deliver; unverified = the
        /// dev/test path, which is fail-closed behind <c>Piggy:BypassPurchase</c>.
        /// </summary>
        private async Task<PiggyBreakResultDto> BreakCoreAsync(Guid userId, PiggyBreakOption option, string purchaseId,
            bool verified, string priceSku, Guid? storePurchaseId, TransactionType creditType, int soldTier)
        {
            if (string.IsNullOrWhiteSpace(purchaseId))
                return Fail("A purchase id is required.");

            var cfg = await EffectiveAsync();
            if (!cfg.Enabled && !verified) return Fail("The piggy bank is not available.");

            // ---- already paid? Return the original outcome rather than doing anything again ----
            var prior = await _db.PiggyBreaks.FirstOrDefaultAsync(b => b.PurchaseId == purchaseId);
            if (prior != null && prior.Granted)
            {
                _logger.LogInformation("Piggy break {PurchaseId} replayed for {User}; returning the original payout.",
                    purchaseId, userId);

                return new PiggyBreakResultDto
                {
                    Ok = true,
                    Amount = prior.Amount,
                    NewChipBalance = await ChipBalanceAsync(userId),
                    Piggy = await GetStateAsync(userId),
                };
            }

            // ---- the bank, with any expired window settled FIRST ----
            //
            // Tracked, and expiry applied before anything is decided: a window that ran out while the player was in
            // the store must not still be sellable when they come back with a receipt.
            var bank = await _db.PlayerPiggyBanks.FirstOrDefaultAsync(p => p.UserId == userId);
            if (bank == null) return Fail("No piggy bank yet.");

            if (Expire(bank, DateTime.UtcNow, cfg)) await _db.SaveChangesAsync();

            bool full = PiggyMath.CanBreak(bank.Amount, bank.MaxAmount, cfg);
            bool paidCapacity = false;   // a VERIFIED Full/×2 found the bank moved underneath the tap → pay capacity, never refuse

            switch (option)
            {
                case PiggyBreakOption.Full:
                case PiggyBreakOption.FullDouble:
                    if (!full)
                    {
                        if (!verified) return Fail("The piggy bank is not full yet.");
                        // The store already charged for a full bank. The window expired / the bank reset between the tap and the
                        // receipt — principle 4 (never under-deliver): pay what the offer promised, the bank's capacity.
                        paidCapacity = true;
                    }
                    break;

                case PiggyBreakOption.Early:
                    // Refused when it IS full, not merely pointless: the early offer costs more, so selling it to
                    // somebody who already qualifies for the cheaper one is taking money for nothing.
                    // (A VERIFIED early purchase on a now-full bank still pays what it holds — the money is taken.)
                    if (full && !verified) return Fail("The bank is already full - take the full offer.");
                    if (bank.Amount <= 0m && !verified) return Fail("There is nothing in the piggy bank yet.");
                    break;

                default:
                    return Fail("Unknown break option.");
            }

            decimal banked = bank.Amount;
            decimal multiplier = option == PiggyBreakOption.FullDouble ? 2m : 1m;

            // WHAT THE BANK HOLDS, times the multiplier. Every option, including the early one.
            //
            // Early used to pay the bank's CAPACITY, which meant a player with a nearly empty bank could buy a full
            // one outright - and that guts the whole premise, because if a full bank is purchasable at any moment
            // then wagering to fill it is pointless. Paying only what is actually in there needs no minimum-fill
            // guard to close that: an early break on an empty bank simply buys almost nothing, so nobody takes it.
            //
            // What Early sells is therefore the TIMING, at a higher price for the same chips - not a bigger payout.
            //
            // Then capped at the capacity of the rung the product was SOLD for, when that differs from the bank's
            // own rung. The store verifies that a product was paid for, not which bank it lands on, and every break
            // pays out of the player's own bank — so the cheapest rung's product would otherwise buy the biggest bank
            // at the smallest price. Same rung (the only thing an unmodified client sends) is never capped, so a bank
            // snapshotted before a ladder edit still pays what the player filled. PiggyMath.PayoutBase has the rest.
            decimal bankRule = paidCapacity ? bank.MaxAmount : banked;
            decimal payoutBase = PiggyMath.PayoutBase(bankRule, bank.Tier, soldTier, cfg, out bool cappedToSoldTier);
            decimal payout = payoutBase * multiplier;
            if (payout <= 0m && !verified) return Fail("There is nothing to pay out.");

            string note = null;
            if (cappedToSoldTier)
            {
                note = $"sold as tier {soldTier} but applied to a tier {bank.Tier} bank — payout capped at the sold rung's capacity " +
                       $"({payout:N0} paid; the bank rule would have paid {bankRule * multiplier:N0}).";
                _logger.LogWarning("Piggy break {Option} for {User}: product tier {SoldTier} on a tier {BankTier} bank — capped {From} → {To} (store purchase {Store}).",
                    option, userId, soldTier, bank.Tier, bankRule * multiplier, payout, storePurchaseId);
            }
            else if (soldTier > 0 && soldTier != bank.Tier)
            {
                // Mismatch that did not bite (a higher or since-removed rung). Not a money problem — the bank rule
                // paid — but it is not something an unmodified client produces either, so it goes on the record.
                note = $"sold as tier {soldTier} but applied to a tier {bank.Tier} bank; the bank rule paid ({payout:N0}).";
                _logger.LogInformation("Piggy break {Option} for {User}: product tier {SoldTier} on a tier {BankTier} bank, not capped (store purchase {Store}).",
                    option, userId, soldTier, bank.Tier, storePurchaseId);
            }

            // ---- the purchase itself ----
            //
            // FAIL CLOSED. Without a verified purchase the only way through is the explicit dev switch,
            // and a free break is precisely the thing that turns this feature into a chip faucet.
            if (!verified && !cfg.BypassPurchase)
                return Fail("Purchase verification is not available yet.");

            // Wallet correlation id (≤ 64): a store purchase keys on its own row id (a Play purchase token is 150+ chars);
            // the dev path keeps its short order id. Never re-key a spent id.
            var correlationId = storePurchaseId.HasValue ? "piggy:break:" + storePurchaseId.Value.ToString("N") : "piggy:break:" + purchaseId;
            if (correlationId.Length > 64) correlationId = "piggy:break:" + Services.Store.StoreMath.Sha256Hex(purchaseId).Substring(0, 48);
            var row = prior ?? new PiggyBreak { UserId = userId, PurchaseId = purchaseId };

            row.Tier = bank.Tier;
            row.Amount = payout;
            row.BankedAmount = banked;
            row.Option = option.ToString();
            row.Multiplier = multiplier;
            row.PriceSku = priceSku ?? PiggyMath.TierFor(await LevelAsync(userId), cfg).PriceSku ?? "";
            row.Status = "Pending";
            if (prior == null) _db.PiggyBreaks.Add(row);

            if (paidCapacity)
                _logger.LogWarning("Piggy break {Option} for {User}: bank had moved under a VERIFIED purchase (held {Banked}); paying capacity {Paid} (store purchase {Store}).",
                    option, userId, banked, payoutBase, storePurchaseId);

            if (payout <= 0m)
            {
                // Verified money, empty bank: record the sale with nothing to pay rather than loop forever; the store flags it for a comp.
                row.Status = "Completed"; row.Granted = true; row.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                _logger.LogWarning("Piggy break {Option} for {User}: verified purchase found an EMPTY bank; recorded with no payout (store purchase {Store}).", option, userId, storePurchaseId);
                return new PiggyBreakResultDto { Ok = true, Amount = 0m, NewChipBalance = await ChipBalanceAsync(userId), Piggy = await GetStateAsync(userId), Note = note };
            }

            decimal newBalance;

            // ONE transaction over the payout, the audit row and the reset. WalletService joins an ambient
            // transaction rather than opening its own, so the credit lands inside this and nothing here can commit
            // half a sale.
            await using (var tx = await _db.Database.BeginTransactionAsync())
            {
                var txn = await _wallet.CreditAsync(userId.ToString(), CurrencyType.Chips, payout,
                    creditType, correlationId,
                    new WalletContext
                    {
                        Description = "Piggy bank (" + row.Option + ")",
                        ExternalRef = purchaseId.Length <= 128 ? purchaseId : Services.Store.StoreMath.FitOrHash(purchaseId, 128),
                    });

                if (txn == null)
                {
                    await tx.RollbackAsync();
                    row.Status = "Failed";
                    await _db.SaveChangesAsync();
                    return Fail("The payout could not be applied.");
                }

                // The ledger computed it under the row lock, so prefer it; the read is only there for the case
                // where a provider somehow returned a transaction without one, and 0 would be a lie.
                newBalance = txn.BalanceAfter ?? await ChipBalanceAsync(userId);

                row.Status = "Completed";
                row.Granted = true;
                row.CompletedAt = DateTime.UtcNow;

                // ---- the bank starts again ----
                //
                // The tier is re-read because the level that decides capacity may have risen while this bank was
                // filling, and the next one should be the bank they have earned rather than the one they started.
                var level = await LevelAsync(userId);
                var (tier, max, _) = PiggyMath.TierFor(level, cfg);

                bank.Tier = tier;
                bank.MaxAmount = max;
                bank.Amount = 0m;
                bank.CelebratedAmount = 0m;
                bank.ReadyAtUtc = null;
                bank.SeenAtUtc = null;
                bank.ExpiresAtUtc = null;
                bank.BreaksCount += 1;
                bank.UpdatedAt = DateTime.UtcNow;

                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }

            _logger.LogInformation(
                "Piggy break {Option} for {User}: paid {Payout} chips (bank held {Banked}, tier {Tier}), order {Order}.",
                row.Option, userId, payout, banked, row.Tier, purchaseId);

            return new PiggyBreakResultDto
            {
                Ok = true,
                Amount = payout,
                NewChipBalance = newBalance,
                Piggy = await GetStateAsync(userId),
                Note = note,
            };
        }

        private static PiggyBreakResultDto Fail(string why) => new PiggyBreakResultDto { Ok = false, Error = why };

        private async Task<decimal> ChipBalanceAsync(Guid userId)
        {
            var wallet = await _db.PlayerWallets
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.UserId == userId && w.Currency == CurrencyType.Chips);
            return wallet?.Balance ?? 0m;
        }

        public async Task<PiggyStateDto> MarkCelebratedAsync(Guid userId)
        {
            var cfg = await EffectiveAsync();
            if (!cfg.Enabled) return new PiggyStateDto { Enabled = false };

            var bank = await _db.PlayerPiggyBanks.FirstOrDefaultAsync(p => p.UserId == userId);
            if (bank != null && bank.CelebratedAmount < bank.Amount)
            {
                bank.CelebratedAmount = bank.Amount;
                bank.UpdatedAt = DateTime.UtcNow;

                try { await _db.SaveChangesAsync(); }
                catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); }
            }

            return await GetStateAsync(userId);
        }

        // ---------------- helpers ----------------

        /// <summary>
        /// Settle an expired window in place: the bank empties and waits for the next chip to start a new one.
        /// Returns true when something changed, so the caller knows whether it needs to save.
        ///
        /// The reset is where the tier is re-read, so a player who levelled up during the window gets the bigger bank
        /// on the next one. It is NOT re-read mid-window: <see cref="PlayerPiggyBank.MaxAmount"/> is snapshotted so a
        /// bank can't move its own goalposts while someone is filling it.
        /// </summary>
        private bool Expire(PlayerPiggyBank bank, DateTime nowUtc, PiggyConfig cfg)
        {
            if (!PiggyMath.IsExpired(nowUtc, bank.ExpiresAtUtc)) return false;

            if (bank.Amount > 0m)
            {
                bank.ExpiredCount++;
                bank.LastExpiredAmount = bank.Amount;
                bank.LastExpiredAtUtc = nowUtc;

                // Worth a log line: nothing was lost that ever existed as money, but a player who fills a bank and
                // watches it evaporate is the clearest signal that the countdown is tuned tighter than the fill.
                _logger.LogInformation("Piggy window expired for {UserId} holding {Amount} of {Max} (expiry #{Count})",
                    bank.UserId, bank.Amount, bank.MaxAmount, bank.ExpiredCount);
            }

            bank.Amount = 0m;
            bank.CelebratedAmount = 0m;   // a new bank starts a new story; nothing is owed a celebration
            bank.ReadyAtUtc = null;
            bank.SeenAtUtc = null;
            bank.ExpiresAtUtc = null;
            bank.UpdatedAt = nowUtc;
            return true;
        }

        /// <summary>
        /// Open a player's first bank, sized for their level. The unique index on UserId settles a race between two
        /// seats settling at once; the loser reloads the winner's row.
        /// </summary>
        private async Task<PlayerPiggyBank> CreateAsync(Guid userId, PiggyConfig cfg)
        {
            var level = await LevelAsync(userId);
            var (tier, max, _) = PiggyMath.TierFor(level, cfg);
            if (max <= 0m) return null;

            var bank = new PlayerPiggyBank { UserId = userId, Tier = tier, MaxAmount = max };
            _db.PlayerPiggyBanks.Add(bank);

            try
            {
                await _db.SaveChangesAsync();
                return bank;
            }
            catch (DbUpdateException)
            {
                _db.ChangeTracker.Clear();
                return await _db.PlayerPiggyBanks.FirstOrDefaultAsync(p => p.UserId == userId);
            }
        }

        private async Task<int> LevelAsync(Guid userId)
            => await _db.UserProfiles.AsNoTracking().Where(p => p.UserId == userId)
                .Select(p => p.Level).FirstOrDefaultAsync();

        /// <summary>Config with the live Redis overlay applied. Redis being unreachable must never take the feature
        /// offline — it falls back to the built-in defaults, exactly like the pass and daily ladders.</summary>
        private async Task<PiggyConfig> EffectiveAsync()
        {
            try
            {
                var entries = await _redis.GetDatabase().HashGetAllAsync(SettingsHashKey);
                if (entries == null || entries.Length == 0) return _cfg;

                var map = new Dictionary<string, string>(entries.Length);
                foreach (var e in entries) map[(string)e.Name] = (string)e.Value;
                return PiggyConfig.Overlay(_cfg, map);
            }
            catch { return _cfg; }
        }
    }
}
