using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using CardGames.Platforms;
using CardGames.Provable;
using CardGames.VideoPoker;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Redis;
using Khela.Game.Services.Stats;
using Khela.Game.Services.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using LbGameType = Khela.Common.Leaderboards.GameType;

namespace Khela.Game.Games.VideoPoker
{
    /// <summary>
    /// Server-authoritative, single-player Video Poker (singleton). REST-only — NO table, seats, pot, or SignalR hub;
    /// each hand is one player's DEAL → HOLD → DRAW → SETTLE cycle. Reuses the SAME shared money layer as blackjack/3CP
    /// (it is the only shared thing): debit-on-bet at deal, idempotent credit-on-settle keyed <c>vp:{handId}:pay</c>,
    /// gifted taint preserved on payout, a per-hand Redis lock, and a per-hand audit row. Provably-fair by commit-
    /// reveal: the whole shuffled deck is hashed at deal (before the hold), so the draw cards are pre-committed and the
    /// house can't re-pick them; the secret server seed is revealed once the hand settles. This module references only
    /// the pure engine (<c>CardGames.VideoPoker</c>), the shared card platform, and the shared wallet/stats/audit — no
    /// other game.
    /// </summary>
    public sealed class VideoPokerService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IRedisService _redis;
        private readonly ILogger<VideoPokerService> _logger;
        private readonly bool _progressionEnabled;
        private readonly bool _reconciliationEnabled;   // opt-in DB-ledger orphan sweep (mirrors blackjack/3CP; default OFF)
        private readonly decimal _minDenomination, _maxDenomination, _maxBet;
        private readonly TimeSpan _staleTimeout;

        private const string OpenSet = "vp:open";                       // set of hand ids not yet settled (reaper scan)
        private static readonly TimeSpan HandTtl = TimeSpan.FromHours(24);   // long — the reaper settles abandoned hands, not the TTL
        private static string HandKey(string handId) => $"vp:hand:{handId}";
        private static string LockKey(string handId) => $"vplock:{handId}";
        private const string AuditTableId = "videopoker";

        public VideoPokerService(IServiceScopeFactory scopes, IRedisService redis, ILogger<VideoPokerService> logger, IConfiguration config)
        {
            _scopes = scopes; _redis = redis; _logger = logger;
            _progressionEnabled = config.GetValue("Progression:Enabled", true);
            _reconciliationEnabled = config.GetValue("Reconciliation:Enabled", false);
            _minDenomination = config.GetValue("VideoPoker:MinDenomination", 1m);
            _maxDenomination = config.GetValue("VideoPoker:MaxDenomination", 100000m);
            _maxBet = config.GetValue("VideoPoker:MaxBet", 500000m);
            _staleTimeout = TimeSpan.FromSeconds(config.GetValue("VideoPoker:StaleSeconds", 120));
        }

        // ─────────────────────────────── deal (debit-on-bet, commit the deck) ───────────────────────────────

        /// <summary>
        /// Start a hand: validate the variant/coins/denomination, shuffle a provably-fair deck, debit the stake, and
        /// commit (deckHash + serverSeedHash) BEFORE the hold. The bet is debited FIRST; if persisting the committed
        /// state then fails, the debit is rolled back so no stake is ever stranded on a deal.
        /// </summary>
        public async Task<VideoPokerBoard> DealAsync(string userId, DealVideoPokerRequest req)
        {
            var variant = VideoPokerVariants.Resolve(req?.VariantId);
            int coins = req?.Coins ?? variant.MaxCoins;
            decimal denom = req?.Denomination ?? _minDenomination;
            if (coins < variant.MinCoins || coins > variant.MaxCoins)
                throw new ArgumentException($"Coins must be {variant.MinCoins}–{variant.MaxCoins} for {variant.Id}.");
            if (denom < _minDenomination || denom > _maxDenomination)
                throw new ArgumentException($"Denomination must be {_minDenomination}–{_maxDenomination} Chips per coin.");
            decimal bet = coins * denom;
            if (bet <= 0m || bet > _maxBet)
                throw new ArgumentException($"Total bet {bet} is out of range (max {_maxBet}).");

            var handId = Guid.NewGuid().ToString("N");

            // Idempotent bet-and-deal: if the client supplies a request token, the FIRST call reserves it → this handId
            // (NX). A retry loses the NX, reads the winning handId, and returns that hand — never debiting twice.
            var reqKey = string.IsNullOrWhiteSpace(req?.ClientRequestId) ? null : $"vp:req:{userId}:{req.ClientRequestId.Trim()}";
            if (reqKey != null &&
                !await _redis.GetDatabase().StringSetAsync(reqKey, handId, TimeSpan.FromMinutes(10), When.NotExists))
            {
                var existingId = (await _redis.GetDatabase().StringGetAsync(reqKey)).ToString();
                var prior = string.IsNullOrEmpty(existingId) ? null : await GetHandAsync(userId, existingId);
                if (prior != null) return prior;
                throw new InvalidOperationException("A hand for this request is still being dealt — retry shortly.");
            }

            var serverSeed = RandomNumberGenerator.GetBytes(32);
            var serverSeedHash = Convert.ToHexString(SHA256.HashData(serverSeed)).ToLowerInvariant();
            var clientSeed = string.IsNullOrWhiteSpace(req?.ClientSeed) ? handId : req.ClientSeed.Trim();
            const long nonce = 0;

            var game = new VideoPokerGame();
            game.Deal(ProvableShuffle.DeriveSeed(serverSeed, clientSeed, nonce), variant.Jokers);
            var deckHash = game.DeckHash();

            bool committed = false;   // becomes a real, reapable hand once state+OpenSet are durably written
            try
            {
                // Debit-on-bet (idempotent on the correlation key). Insufficient funds throws → surfaced as a clean 400.
                var (betTxId, balance, giftedStake) = await DebitBetAsync(userId, bet, handId);

                var state = new VideoPokerHandState
                {
                    HandId = handId,
                    UserId = userId,
                    VariantId = variant.Id,
                    Coins = coins,
                    Denomination = denom,
                    Bet = bet,
                    ServerSeedHex = Convert.ToHexString(serverSeed).ToLowerInvariant(),
                    ServerSeedHash = serverSeedHash,
                    ClientSeed = clientSeed,
                    Nonce = nonce,
                    DeckHash = deckHash,
                    BetTxId = betTxId,
                    GiftedStake = giftedStake,
                    Status = "dealt",
                    CreatedAt = DateTime.UtcNow,
                };

                try
                {
                    await _redis.SetAsync(HandKey(handId), state, HandTtl);
                    await _redis.GetDatabase().SetAddAsync(OpenSet, handId);
                    committed = true;
                }
                catch (Exception ex)
                {
                    // Compensating refund: the state never committed, so nothing else can settle this hand — give the stake back.
                    _logger.LogError(ex, "VP deal state-commit failed for hand {HandId}; refunding the debited stake.", handId);
                    await SafeRefundAsync(userId, bet, handId, giftedStake);
                    throw;
                }

                return BuildBoard(state, game, variant, balance);
            }
            catch when (!committed)
            {
                // Deal failed BEFORE the hand was committed (insufficient funds, state-commit fault). Release the token so
                // a genuine retry isn't wedged pointing at a hand that never materialised. Once committed, the token must
                // stay put — the hand is real and reapable, so a retry should get THAT hand, never a second debit.
                if (reqKey != null) { try { await _redis.DeleteAsync(reqKey); } catch { } }
                throw;
            }
        }

        // ─────────────────────────────── draw (one draw, credit-on-settle, audit) ───────────────────────────────

        /// <summary>
        /// Complete a hand with the player's hold. Draws from the committed remainder, evaluates, pays, and audits.
        /// Idempotent: a duplicate call on an already-settled hand returns the same result (the hold is locked at the
        /// first draw); the payout credit is idempotent on its correlation key.
        /// </summary>
        public Task<VideoPokerBoard> DrawAsync(string userId, DrawVideoPokerRequest req)
        {
            if (req == null || string.IsNullOrEmpty(req.HandId)) throw new ArgumentException("Missing hand id.");
            if (req.Hold == null || req.Hold.Length != 5) throw new ArgumentException("Hold must be a length-5 mask.");
            return SettleAsync(req.HandId, userId, req.Hold, standPat: false);
        }

        /// <summary>Load a hand for reconnect/replay. Returns null if it has expired or isn't the caller's.</summary>
        public async Task<VideoPokerBoard> GetHandAsync(string userId, string handId)
        {
            var state = await _redis.GetAsync<VideoPokerHandState>(HandKey(handId));
            if (state == null || state.UserId != userId) return null;
            var (game, variant) = Rebuild(state);
            decimal balance = await GetBalanceAsync(userId);
            return BuildBoard(state, game, variant, balance);
        }

        // ─────────────────────────────── provably-fair verification (public) ───────────────────────────────

        private static readonly JsonSerializerOptions MetaJson = new() { PropertyNameCaseInsensitive = true };
        private const string VerifyAlgorithm =
            "seed = HMAC_SHA256(serverSeed, clientSeed + ':' + nonce); deck = 52 cards (+jokers) shuffled by Fisher-Yates " +
            "using DeterministicRng(seed) [block k = HMAC_SHA256(seed, int64_BE(k)), 4 bytes BE per draw, rejection-sampled]; " +
            "deckHash = sha256 of '<rank><suit>,...' (joker='JK'); dealt = first 5; draw replaces non-held from the remainder.";

        /// <summary>
        /// Recompute a settled hand entirely from its REVEALED seed and report, field by field, whether it reproduces
        /// the committed deckHash and the recorded deal/draw/result. Reads the durable audit row (survives Redis TTL),
        /// so anyone can check any hand by id — the point of provably-fair. Returns null if the hand isn't found.
        /// </summary>
        public async Task<VideoPokerVerification> VerifyAsync(string handId)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var header = await db.GameHandHeaders.AsNoTracking().FirstOrDefaultAsync(h => h.RoundId == handId && h.GameType == GameType.VideoPoker);
            if (header == null) return null;
            var participant = await db.GameHandParticipants.AsNoTracking().FirstOrDefaultAsync(p => p.HandId == header.HandId);

            HandMeta meta = null;
            try { meta = JsonSerializer.Deserialize<HandMeta>(header.MetadataJson ?? "", MetaJson); } catch { }
            var v = new VideoPokerVerification
            {
                HandId = handId,
                VariantId = meta?.variant,
                Algorithm = VerifyAlgorithm,
                Committed = new VideoPokerVerification.Commit { ServerSeedHash = header.ShoeId, DeckHash = header.DeckHash },
                Chain = new VideoPokerVerification.ChainLink
                {
                    PrevHandHash = header.PrevHandHash,
                    ResultChecksum = header.ResultChecksum,
                    HandHash = VideoPokerLedger.HandHash(handId, header.ShoeId, header.DeckHash, header.ResultChecksum,
                                                         participant?.Bet ?? 0m, participant?.Payout ?? 0m, header.PrevHandHash),
                },
            };
            if (meta == null || string.IsNullOrEmpty(meta.serverSeed))
            {
                v.Verified = false; v.Reason = "The revealed server seed is not available for this hand."; return v;
            }

            var variant = VideoPokerVariants.Resolve(meta.variant);
            v.Revealed = new VideoPokerVerification.Reveal { ServerSeed = meta.serverSeed, ClientSeed = meta.clientSeed, Nonce = meta.nonce, Jokers = variant.Jokers };

            // Re-run the shuffle purely from the revealed seed — the same deterministic function the deal used.
            var game = new VideoPokerGame();
            game.Deal(ProvableShuffle.DeriveSeed(Convert.FromHexString(meta.serverSeed), meta.clientSeed, meta.nonce), variant.Jokers);
            var reDeckHash = game.DeckHash();
            var reDealt = game.Dealt.Select(ProvableShuffle.Canonical).ToArray();

            string[] reFinal = null; string reCategory = null; int rePayoutCoins = 0; decimal rePayout = 0m;
            if (meta.hold != null && meta.hold.Length == 5)
            {
                game.Draw(meta.hold);
                var rank = variant.Score(game.Final);
                reFinal = game.Final.Select(ProvableShuffle.Canonical).ToArray();
                reCategory = rank.Category.ToString();
                rePayoutCoins = variant.Paytable.Payout(rank, meta.coins);
                rePayout = rePayoutCoins * meta.denomination;
            }

            var reServerSeedHash = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(meta.serverSeed))).ToLowerInvariant();
            var m = new VideoPokerVerification.MatchReport
            {
                SeedBindsToCommitment = string.Equals(reServerSeedHash, header.ShoeId, StringComparison.OrdinalIgnoreCase),
                DeckHashMatches = string.Equals(reDeckHash, header.DeckHash, StringComparison.OrdinalIgnoreCase),
                DealtMatches = meta.dealt != null && reDealt.SequenceEqual(meta.dealt),
                FinalMatches = reFinal != null && meta.final != null && reFinal.SequenceEqual(meta.final),
                CategoryMatches = reCategory == meta.category,
                PayoutMatches = rePayoutCoins == meta.payoutCoins && (participant == null || rePayout == participant.Payout),
            };
            v.Recomputed = new VideoPokerVerification.Redo { DeckHash = reDeckHash, Dealt = reDealt, Hold = meta.hold, Final = reFinal, Category = reCategory, PayoutCoins = rePayoutCoins, Payout = rePayout };
            v.Matches = m;
            bool drawn = meta.hold != null;
            v.Verified = m.SeedBindsToCommitment && m.DeckHashMatches && m.DealtMatches
                         && (!drawn || (m.FinalMatches && m.CategoryMatches && m.PayoutMatches));
            return v;
        }

        /// <summary>Shape of the audit MetadataJson written at settle (used only to re-read for verification).</summary>
        private sealed class HandMeta
        {
            public string variant { get; set; }
            public int coins { get; set; }
            public decimal denomination { get; set; }
            public string[] dealt { get; set; }
            public bool[] hold { get; set; }
            public string[] final { get; set; }
            public string category { get; set; }
            public int payoutCoins { get; set; }
            public string serverSeed { get; set; }
            public string clientSeed { get; set; }
            public long nonce { get; set; }
        }

        /// <summary>
        /// Core settle used by both a player draw and the stale-hand reaper. Serialized per-hand by a Redis lock so two
        /// concurrent draws can never double-settle. A hand already <c>complete</c> returns its stored result unchanged.
        /// </summary>
        private async Task<VideoPokerBoard> SettleAsync(string handId, string userId, bool[] hold, bool standPat)
        {
            var token = await AcquireLockAsync(handId);
            try
            {
                var state = await _redis.GetAsync<VideoPokerHandState>(HandKey(handId));
                if (state == null) throw new InvalidOperationException("Hand not found or expired.");
                if (userId != null && state.UserId != userId) throw new InvalidOperationException("Not your hand.");

                if (state.Status == "complete")   // idempotent replay — the hold is locked
                {
                    var (g0, v0) = Rebuild(state);
                    return BuildBoard(state, g0, v0, await GetBalanceAsync(state.UserId));
                }

                var variant = VideoPokerVariants.Resolve(state.VariantId);

                // Which hold to settle. If a prior attempt already COMMITTED a hold ("settling" — it may have crashed
                // after crediting but before finalizing), reuse THAT hold: re-deriving the same (seed, hold) reproduces
                // the identical hand + payout, and the credit is idempotent, so recovery can never diverge from — or
                // over-credit against — what was actually paid. Only a fresh "dealt" hand takes the incoming hold /
                // reaper stand-pat.
                bool[] effectiveHold = (state.Status == "settling" && state.Hold != null)
                    ? state.Hold
                    : (standPat ? new[] { true, true, true, true, true } : hold);

                var game = new VideoPokerGame();
                game.Deal(ProvableShuffle.DeriveSeed(Convert.FromHexString(state.ServerSeedHex), state.ClientSeed, state.Nonce), variant.Jokers);
                game.Draw(effectiveHold);
                var rank = variant.Score(game.Final);
                int payoutCoins = variant.Paytable.Payout(rank, state.Coins);
                decimal payout = payoutCoins * state.Denomination;

                // Phase 1: durably COMMIT the chosen hold + result as "settling" BEFORE any credit, so a crash after the
                // credit but before finalizing is healed by re-deriving THIS exact hold (never a different stand-pat).
                if (state.Status != "settling")
                {
                    state.Status = "settling";
                    state.Hold = effectiveHold;
                    state.Category = rank.Category.ToString();
                    state.PayoutCoins = payoutCoins;
                    state.Payout = payout;
                    await _redis.SetAsync(HandKey(handId), state, HandTtl);
                }

                // Phase 2: credit-on-settle (idempotent on vp:{handId}:pay). Preserve the stake's gifted fraction so a
                // win can't launder taint.
                decimal balance;
                string payTxId = null;
                if (payout > 0m)
                {
                    decimal giftedCredit = state.Bet > 0m
                        ? Math.Round(payout * (state.GiftedStake / state.Bet), 4, MidpointRounding.ToZero)
                        : 0m;
                    (payTxId, balance) = await CreditPayoutAsync(state.UserId, payout, handId, giftedCredit);
                }
                else balance = await GetBalanceAsync(state.UserId);

                state.Status = "complete";
                state.PayTxId = payTxId;
                await _redis.SetAsync(HandKey(handId), state, HandTtl);
                await _redis.GetDatabase().SetRemoveAsync(OpenSet, handId);

                // Audit + stats + progression — best-effort, AFTER money has settled, each at-most-once. A failure here
                // can never affect the wallet.
                await RecordSettlementAsync(state, game, rank);

                return BuildBoard(state, game, variant, balance);
            }
            finally { await ReleaseLockAsync(handId, token); }
        }

        // ─────────────────────────────── stale-hand reaper (never strand a bet) ───────────────────────────────

        /// <summary>
        /// Resolve hands that were dealt but never drawn (player abandoned after the debit): once older than the stale
        /// timeout they auto-settle as STAND PAT (hold all — the dealt hand plays as the final hand), so the debited
        /// stake is always resolved and never stranded. Called on a timer by <see cref="VideoPokerReaper"/>.
        /// </summary>
        public async Task<int> ResolveStaleHandsAsync()
        {
            int resolved = 0;
            RedisValue[] members;
            try { members = await _redis.GetDatabase().SetMembersAsync(OpenSet); }
            catch (Exception ex) { _logger.LogWarning(ex, "VP reaper: could not read the open-hand set."); return 0; }

            foreach (var m in members)
            {
                var handId = m.ToString();
                try
                {
                    var state = await _redis.GetAsync<VideoPokerHandState>(HandKey(handId));
                    if (state == null) { await _redis.GetDatabase().SetRemoveAsync(OpenSet, handId); continue; }   // expired → drop
                    if (state.Status == "complete") { await _redis.GetDatabase().SetRemoveAsync(OpenSet, handId); continue; }

                    // A "settling" hand crashed mid-settle (credit maybe committed, not finalized) — heal it NOW
                    // regardless of age; SettleAsync reuses the committed hold, so this only finalizes, never re-decides.
                    if (state.Status == "settling")
                    {
                        await SettleAsync(handId, userId: null, hold: null, standPat: false);
                        resolved++;
                        continue;
                    }
                    if (DateTime.UtcNow - state.CreatedAt < _staleTimeout) continue;   // a "dealt" hand still within its play window

                    await SettleAsync(handId, userId: null, hold: null, standPat: true);   // abandoned → stand pat
                    resolved++;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "VP reaper: failed to resolve stale hand {HandId}.", handId); }
            }
            return resolved;
        }

        // ─────────────────────────────── DB-ledger orphan sweep (opt-in; process-death residual) ───────────────────────────────

        /// <summary>
        /// Opt-in safety net (gated on <c>Reconciliation:Enabled</c>, default OFF) — the mate to the OpenSet reaper for
        /// the ONE case it structurally can't see: a stake debited to MySQL whose Redis hand-state (and OpenSet
        /// membership) never committed because the process died in that window. Scans the ledger for <c>vp:*:bet</c>
        /// rows that have NO settle/refund txn, NO committed Redis state, and NO audit header, and refunds them
        /// idempotently on a <c>vp:{handId}:rec</c> key. Conservative: only bets older than a few minutes (never a
        /// possibly-live hand); the triple guard means a genuinely-settled hand is never refunded.
        /// </summary>
        public async Task ReconcileStrandedStakesAsync()
        {
            if (!_reconciliationEnabled) return;
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var upper = now.AddMinutes(-3);    // never touch a possibly-live hand
                var lower = now.AddHours(-24);     // bound the scan (Redis state TTL is 24h, so a settled hand still has state in-window)

                var rows = await db.WalletTransactions.AsNoTracking()
                    .Where(x => x.CreatedAt < upper && x.CreatedAt > lower && x.CorrelationId != null && x.CorrelationId.StartsWith("vp:"))
                    .Select(x => new { x.WalletId, x.Amount, x.GiftedDelta, x.CorrelationId })
                    .ToListAsync();
                if (rows.Count == 0) return;

                foreach (var group in rows.GroupBy(r => HandIdOf(r.CorrelationId)))
                {
                    var handId = group.Key;
                    if (string.IsNullOrEmpty(handId)) continue;
                    var bet = group.FirstOrDefault(r => r.CorrelationId.EndsWith(":bet"));
                    if (bet == null) continue;                                                         // no debit → nothing to strand
                    if (group.Any(r => !r.CorrelationId.EndsWith(":bet"))) continue;                   // a :pay/:rfnd/:rec exists → already resolved
                    if (await _redis.GetAsync<VideoPokerHandState>(HandKey(handId)) != null) continue; // committed state → the reaper/normal path owns it
                    if (await db.GameHandHeaders.AsNoTracking().AnyAsync(h => h.RoundId == handId)) continue;  // settled audit → consumed

                    var uid = await db.PlayerWallets.AsNoTracking().Where(w => w.WalletId == bet.WalletId).Select(w => w.UserId).FirstOrDefaultAsync();
                    if (uid == Guid.Empty) continue;
                    try
                    {
                        await RefundStrandedAsync(uid.ToString(), Math.Abs(bet.Amount), handId, Math.Abs(bet.GiftedDelta));
                        _logger.LogWarning("VP reconciliation refunded stranded stake: hand {Hand} amount {Amt}", handId, Math.Abs(bet.Amount));
                    }
                    catch (Exception ex) { _logger.LogError(ex, "VP reconciliation refund failed for hand {Hand}", handId); }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "VP stranded-stake reconciliation sweep failed."); }
        }

        private static string HandIdOf(string correlationId)
        {
            var parts = correlationId.Split(':');   // vp:{handId}:{suffix}
            return parts.Length >= 3 ? parts[1] : null;
        }

        // ─────────────────────────────── wallet helpers (mirror 3CP) ───────────────────────────────

        private async Task<(string TxId, decimal Balance, decimal GiftedStake)> DebitBetAsync(string userId, decimal bet, string handId)
        {
            using var scope = _scopes.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            var ctx = new WalletContext { RoundId = handId, TableId = AuditTableId, Description = $"Video Poker bet hand {handId}" };
            var txn = await wallet.DebitAsync(userId, CurrencyType.Chips, bet, TransactionType.Bet, $"vp:{handId}:bet", ctx);
            return (txn.TransactionId.ToString(), txn.BalanceAfter ?? 0m, Math.Abs(txn.GiftedDelta));
        }

        private async Task<(string TxId, decimal Balance)> CreditPayoutAsync(string userId, decimal payout, string handId, decimal giftedCredit)
        {
            const int attempts = 3;
            for (int i = 1; ; i++)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
                    var ctx = new WalletContext { RoundId = handId, TableId = AuditTableId, Description = $"Video Poker payout hand {handId}", CreditGiftedAmount = giftedCredit };
                    var txn = await wallet.CreditAsync(userId, CurrencyType.Chips, payout, TransactionType.Win, $"vp:{handId}:pay", ctx);
                    return (txn.TransactionId.ToString(), txn.BalanceAfter ?? 0m);
                }
                catch (Exception ex) when (i < attempts)
                {
                    _logger.LogWarning(ex, "VP payout credit attempt {Attempt} failed for hand {Hand}; retrying.", i, handId);
                    await Task.Delay(150 * i);
                }
            }
        }

        private async Task SafeRefundAsync(string userId, decimal amount, string handId, decimal giftedRestore)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
                var ctx = new WalletContext { RoundId = handId, TableId = AuditTableId, Description = $"Video Poker stake refund hand {handId}", CreditGiftedAmount = giftedRestore };
                await wallet.CreditAsync(userId, CurrencyType.Chips, amount, TransactionType.Refund, $"vp:{handId}:rfnd", ctx);
            }
            catch (Exception ex) { _logger.LogError(ex, "VP compensating refund FAILED for hand {HandId} — flag for reconciliation.", handId); }
        }

        /// <summary>Refund a stranded stake found by the reconciliation sweep. Distinct <c>:rec</c> correlation so it's
        /// idempotent AND never collides with the deal-time <c>:rfnd</c> compensation.</summary>
        private async Task RefundStrandedAsync(string userId, decimal amount, string handId, decimal giftedRestore)
        {
            using var scope = _scopes.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            var ctx = new WalletContext { RoundId = handId, TableId = AuditTableId, Description = $"Video Poker stranded-stake refund hand {handId}", CreditGiftedAmount = giftedRestore };
            await wallet.CreditAsync(userId, CurrencyType.Chips, amount, TransactionType.Refund, $"vp:{handId}:rec", ctx);
        }

        private async Task<decimal> GetBalanceAsync(string userId)
        {
            using var scope = _scopes.CreateScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
            return await wallet.GetBalanceAsync(userId, CurrencyType.Chips);
        }

        // ─────────────────────────────── audit + stats + accrual (best-effort) ───────────────────────────────

        private async Task RecordSettlementAsync(VideoPokerHandState state, VideoPokerGame game, VideoPokerHandRank rank)
        {
            if (!Guid.TryParse(state.UserId, out var uid)) return;
            var db0 = _redis.GetDatabase();
            decimal net = state.Payout - state.Bet;
            decimal cleanWager = state.Bet - state.GiftedStake;
            bool win = net > 0m;

            // Persist audit — at-most-once (a crash before the credit re-runs nothing; after, only the unfinished step).
            try
            {
                if (await db0.StringSetAsync($"vp:aud:{state.HandId}", "1", TimeSpan.FromHours(1), When.NotExists))
                    await PersistHandAsync(state, game, rank, uid);
            }
            catch (Exception ex) { _logger.LogError(ex, "VP audit persist failed for hand {Hand}", state.HandId); }

            // Durable player stats under the VideoPoker leaderboard vertical (map, don't cast — the ledger enum diverges).
            try
            {
                if (await db0.StringSetAsync($"vp:sta:{state.HandId}", "1", TimeSpan.FromHours(1), When.NotExists))
                {
                    using var scope = _scopes.CreateScope();
                    var stats = scope.ServiceProvider.GetRequiredService<IPlayerStatsService>();
                    var counters = new Dictionary<string, long>
                    {
                        ["handsWon"] = win ? 1 : 0,
                        ["royals"] = rank.Category == VideoPokerCategory.RoyalFlush ? 1 : 0,
                    };
                    long grantedXp = _progressionEnabled ? await AccrueProgressionAsync(uid, cleanWager, win, state.HandId) : 0L;
                    await stats.RecordRoundResultsAsync(LbGameType.VideoPoker,
                        new List<RoundResult> { new RoundResult(uid, state.Bet, net, cleanWager, grantedXp, counters) });
                    if (_progressionEnabled)
                    {
                        await AccrueVipAsync(uid, cleanWager, state.HandId);
                        await AccrueLoyaltyAsync(uid, cleanWager, state.HandId);
                        await AccrueMissionsAsync(uid, counters, cleanWager, state.HandId);
                    }
                    await AccruePiggyAsync(uid, cleanWager, net, state.HandId);   // own switch — see Piggy:Enabled
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "VP stats/accrual failed for hand {Hand}", state.HandId); }
        }

        private async Task PersistHandAsync(VideoPokerHandState state, VideoPokerGame game, VideoPokerHandRank rank, Guid uid)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var finalCanon = game.Final.Select(ProvableShuffle.Canonical).ToArray();
            var meta = JsonSerializer.Serialize(new
            {
                variant = state.VariantId,
                coins = state.Coins,
                denomination = state.Denomination,
                dealt = game.Dealt.Select(ProvableShuffle.Canonical).ToArray(),
                hold = state.Hold,
                final = finalCanon,
                category = state.Category,
                payoutCoins = state.PayoutCoins,
                wildCount = rank.WildCount,
                serverSeed = state.ServerSeedHex,   // revealed at settle — the hand is over
                clientSeed = state.ClientSeed,
                nonce = state.Nonce,
            });
            var resultChecksum = VideoPokerLedger.ResultChecksum(finalCanon, state.Category, state.PayoutCoins);

            // Tamper-evident chain: link this hand to the player's previous one. Serialize the read-prev → set-head
            // per user (a short NX lock) so concurrent same-user settles keep the chain linear. Best-effort — a chain
            // gap only ever COSTS a link, never corrupts money.
            string chainLockKey = $"vpchainlock:{uid:N}", chainKey = $"vp:chain:{uid:N}", chainToken = Guid.NewGuid().ToString("N");
            var rdb = _redis.GetDatabase();
            var deadline = DateTime.UtcNow.AddMilliseconds(3000);
            while (!await rdb.StringSetAsync(chainLockKey, chainToken, TimeSpan.FromSeconds(5), When.NotExists))
            {
                if (DateTime.UtcNow > deadline) break;   // fall through un-locked rather than block settle audit
                await Task.Delay(20);
            }
            try
            {
                string prevHash = (await rdb.StringGetAsync(chainKey)).ToString() ?? string.Empty;
                var thisHash = VideoPokerLedger.HandHash(state.HandId, state.ServerSeedHash, state.DeckHash, resultChecksum, state.Bet, state.Payout, prevHash);

                var header = new GameHandHeader
                {
                    TableId = AuditTableId,
                    GameType = GameType.VideoPoker,
                    RoundId = state.HandId,
                    StartedAt = state.CreatedAt,
                    SettledAt = DateTime.UtcNow,
                    Status = HandStatus.Settled,
                    ShoeId = state.ServerSeedHash,
                    ShuffleSeed = $"{state.ClientSeed}:{state.Nonce}",
                    DeckHash = state.DeckHash,
                    ResultChecksum = resultChecksum,
                    PrevHandHash = prevHash,
                    MetadataJson = meta,
                };
                var participant = new GameHandParticipant
                {
                    HandId = header.HandId,
                    UserId = uid,
                    SeatNumber = 0,
                    HandIndex = 0,
                    Bet = state.Bet,
                    Payout = state.Payout,
                    Outcome = state.Category,
                    WalletDebitTxId = state.BetTxId,
                    WalletCreditTxId = state.PayTxId,
                    MetadataJson = meta,
                    Resolved = true,
                };
                db.GameHandHeaders.Add(header);
                db.GameHandParticipants.Add(participant);
                await db.SaveChangesAsync();
                await rdb.StringSetAsync(chainKey, thisHash);   // advance the chain head only after the row is durable
            }
            finally
            {
                const string lua = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
                try { await rdb.ScriptEvaluateAsync(lua, new RedisKey[] { chainLockKey }, new RedisValue[] { chainToken }); } catch { }
            }
        }

        private async Task<long> AccrueProgressionAsync(Guid userId, decimal cleanWager, bool win, string handId)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var progression = scope.ServiceProvider.GetRequiredService<Khela.Game.Services.Progression.IProgressionService>();
                return await progression.AccrueForRoundAsync(userId, cleanWager, win, handId);
            }
            catch (Exception ex) { _logger.LogError(ex, "VP progression accrual failed for user {UserId} hand {Hand}", userId, handId); return 0; }
        }

        private async Task AccrueVipAsync(Guid userId, decimal cleanWager, string handId)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var vip = scope.ServiceProvider.GetRequiredService<Khela.Game.Services.Vip.IVipService>();
                await vip.AccrueForRoundAsync(userId, cleanWager, handId);
            }
            catch (Exception ex) { _logger.LogError(ex, "VP VIP accrual failed for user {UserId} hand {Hand}", userId, handId); }
        }

        private async Task AccrueLoyaltyAsync(Guid userId, decimal cleanWager, string handId)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var loyalty = scope.ServiceProvider.GetRequiredService<Khela.Game.Services.Loyalty.ILoyaltyService>();
                await loyalty.AccrueForRoundAsync(userId, cleanWager, handId);
            }
            catch (Exception ex) { _logger.LogError(ex, "VP loyalty accrual failed for user {UserId} hand {Hand}", userId, handId); }
        }

        /// <summary>Banks a share of the hand's CLEAN wager into the piggy bank. Idempotent per (hand, user).
        /// Not gated on the progression flag — the piggy has its own switch. Best-effort; never breaks settle.</summary>
        private async Task AccruePiggyAsync(Guid userId, decimal cleanWager, decimal net, string handId)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var piggy = scope.ServiceProvider.GetRequiredService<Khela.Game.Services.Piggy.IPiggyService>();
                await piggy.AccrueForRoundAsync(userId, cleanWager, net < 0m ? -net : 0m, handId);
            }
            catch (Exception ex) { _logger.LogError(ex, "VP piggy accrual failed for user {UserId} hand {Hand}", userId, handId); }
        }

        private async Task AccrueMissionsAsync(Guid userId, IReadOnlyDictionary<string, long> counters, decimal cleanWager, string handId)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var missions = scope.ServiceProvider.GetRequiredService<Khela.Game.Services.Missions.IMissionService>();
                await missions.ReportRoundAsync(userId, counters, cleanWager, handId);
            }
            catch (Exception ex) { _logger.LogError(ex, "VP mission progress failed for user {UserId} hand {Hand}", userId, handId); }
        }

        // ─────────────────────────────── per-hand lock ───────────────────────────────

        private async Task<string> AcquireLockAsync(string handId, int timeoutMs = 5000)
        {
            var db = _redis.GetDatabase();
            var token = Guid.NewGuid().ToString("N");
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (await db.StringSetAsync(LockKey(handId), token, TimeSpan.FromSeconds(10), When.NotExists)) return token;
                await Task.Delay(25);
            }
            throw new InvalidOperationException($"Could not acquire lock for video-poker hand {handId}.");
        }

        private async Task ReleaseLockAsync(string handId, string token)
        {
            const string lua = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
            try { await _redis.GetDatabase().ScriptEvaluateAsync(lua, new RedisKey[] { LockKey(handId) }, new RedisValue[] { token }); }
            catch (Exception ex) { _logger.LogWarning(ex, "VP lock release failed for hand {Hand}", handId); }
        }

        // ─────────────────────────────── projection ───────────────────────────────

        /// <summary>Re-derive the game (deck + dealt, and the final hand if the hold is locked) from the committed seed.
        /// The seed is the single source of truth — the deck is never stored, only its hash.</summary>
        private static (VideoPokerGame Game, VideoPokerVariant Variant) Rebuild(VideoPokerHandState state)
        {
            var variant = VideoPokerVariants.Resolve(state.VariantId);
            var game = new VideoPokerGame();
            game.Deal(ProvableShuffle.DeriveSeed(Convert.FromHexString(state.ServerSeedHex), state.ClientSeed, state.Nonce), variant.Jokers);
            if (state.Status == "complete" && state.Hold != null) game.Draw(state.Hold);
            return (game, variant);
        }

        private static VideoPokerBoard BuildBoard(VideoPokerHandState state, VideoPokerGame game, VideoPokerVariant variant, decimal balance)
        {
            bool complete = state.Status == "complete";
            return new VideoPokerBoard
            {
                HandId = state.HandId,
                VariantId = variant.Id,
                VariantName = variant.Name,
                Phase = complete ? "complete" : "dealt",
                Coins = state.Coins,
                Denomination = state.Denomination,
                Bet = state.Bet,
                Dealt = game.Dealt.Select(VideoPokerBoard.CardView.From).ToList(),
                Final = complete && game.Final != null ? game.Final.Select(VideoPokerBoard.CardView.From).ToList() : new List<VideoPokerBoard.CardView>(),
                Hold = complete ? state.Hold : null,
                Category = complete ? state.Category : null,
                PayoutCoins = complete ? state.PayoutCoins : 0,
                Payout = complete ? state.Payout : 0m,
                Balance = balance,
                Fairness = new VideoPokerBoard.VpFairness
                {
                    ServerSeedHash = state.ServerSeedHash,
                    ClientSeed = state.ClientSeed,
                    Nonce = state.Nonce,
                    DeckHash = state.DeckHash,             // committed at deal (the whole shuffled deck)
                    ServerSeed = complete ? state.ServerSeedHex : null,   // revealed only once the hand is over
                },
            };
        }

        /// <summary>Redis-persisted per-hand state between /deal and /draw. Holds NO card list — the deck is re-derived
        /// from the seed on demand, so this can never drift from the committed hash.</summary>
        public sealed class VideoPokerHandState
        {
            public string HandId { get; set; }
            public string UserId { get; set; }
            public string VariantId { get; set; }
            public int Coins { get; set; }
            public decimal Denomination { get; set; }
            public decimal Bet { get; set; }
            public string ServerSeedHex { get; set; }
            public string ServerSeedHash { get; set; }
            public string ClientSeed { get; set; }
            public long Nonce { get; set; }
            public string DeckHash { get; set; }
            public string BetTxId { get; set; }
            public decimal GiftedStake { get; set; }
            public string Status { get; set; }        // "dealt" | "complete"
            public DateTime CreatedAt { get; set; }
            // set at settle:
            public bool[] Hold { get; set; }
            public string Category { get; set; }
            public int PayoutCoins { get; set; }
            public decimal Payout { get; set; }
            public string PayTxId { get; set; }
        }
    }
}
