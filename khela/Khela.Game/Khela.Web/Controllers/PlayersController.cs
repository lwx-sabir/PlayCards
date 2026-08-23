using Khela.Common.Leaderboards;
using Khela.Game.Database;
using Khela.Game.Database.Models;
using Khela.Game.Services.Wallet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// Player management. Search/list players, then grant any VIP level (0–10) and tier to a player directly. This is
    /// an ADMIN OVERRIDE that writes <c>UserProfile</c> straight through <see cref="AppDbContext"/> (it does not go via
    /// the purchase/grind paths): setting a VIP level also refreshes the maintenance window so the grant doesn't decay
    /// at the next monthly review. The VIP TIER is normally auto-computed from Status Points and may re-derive on the
    /// monthly review — the VIP LEVEL is the durable grant.
    /// </summary>
    [Authorize(Policy = "Admin")]
    public sealed class PlayersController : Controller
    {
        private const int MaxRows = 50;

        private readonly AppDbContext _db;
        private readonly IWalletService _wallet;
        private readonly Khela.Game.Services.Pass.IPassService _pass;
        private readonly UserManager<ApplicationUser> _users;

        public PlayersController(AppDbContext db, IWalletService wallet, Khela.Game.Services.Pass.IPassService pass,
            UserManager<ApplicationUser> users)
        {
            _db = db;
            _wallet = wallet;
            _pass = pass;
            _users = users;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string q)
        {
            var vm = new PlayersVm { Query = q };
            var query = _db.UserProfiles.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(q))
            {
                q = q.Trim();
                if (Guid.TryParse(q, out var g))
                {
                    query = query.Where(p => p.UserId == g);
                }
                else
                {
                    // Match display name OR (email / username) — resolve the latter to user ids first.
                    var idStrings = await _db.Users.AsNoTracking()
                        .Where(u => (u.Email != null && u.Email.Contains(q)) || (u.UserName != null && u.UserName.Contains(q)))
                        .Select(u => u.Id)
                        .ToListAsync();
                    var ids = new List<Guid>();
                    foreach (var s in idStrings) if (Guid.TryParse(s, out var gg)) ids.Add(gg);
                    query = query.Where(p => p.DisplayName.Contains(q) || ids.Contains(p.UserId));
                }
            }

            var rows = await query
                .OrderByDescending(p => p.UpdatedAt)
                .Take(MaxRows)
                .Select(p => new PlayerRow
                {
                    UserId = p.UserId,
                    DisplayName = p.DisplayName,
                    Level = p.Level,
                    VipTier = p.VipTier,
                    VipLevel = p.VipLevel,
                    LoyaltyPoints = 0,   // filled from the Lp wallet below — the profile column is retired (docs/VIP_SPEC.md §3)
                    MaintainedThrough = p.VipLevelMaintainedThrough,
                })
                .ToListAsync();

            // Attach emails (AspNetUsers.Id is a string mirror of UserProfile.UserId).
            var idStrs = rows.Select(r => r.UserId.ToString()).ToList();
            var emails = await _db.Users.AsNoTracking()
                .Where(u => idStrs.Contains(u.Id))
                .Select(u => new { u.Id, u.Email, u.LockoutEnabled, u.LockoutEnd })
                .ToListAsync();
            var userById = emails.ToDictionary(e => e.Id);
            var nowOff = DateTimeOffset.UtcNow;
            foreach (var r in rows)
            {
                if (!userById.TryGetValue(r.UserId.ToString(), out var u)) continue;
                r.Email = u.Email;
                // Suspended = Identity lockout in force — exactly what CheckPasswordSignInAsync refuses at login.
                r.SuspendedUntil = u.LockoutEnabled && u.LockoutEnd != null && u.LockoutEnd > nowOff ? u.LockoutEnd : null;
            }

            // LP balances in ONE query (the profile column is retired — docs/VIP_SPEC.md §3), rather than a lookup per row.
            var rowIds = rows.Select(r => r.UserId).ToList();
            var lp = await _db.PlayerWallets.AsNoTracking()
                .Where(w => w.Currency == CurrencyType.Lp && rowIds.Contains(w.UserId))
                .Select(w => new { w.UserId, w.Balance })
                .ToListAsync();
            var lpByUser = lp.ToDictionary(x => x.UserId, x => (long)Math.Floor(x.Balance));
            foreach (var r in rows) if (lpByUser.TryGetValue(r.UserId, out var bal)) r.LoyaltyPoints = bal;

            vm.Players = rows;
            vm.Saved = TempData["Saved"] as string;
            vm.Error = TempData["Error"] as string;
            return View(vm);
        }

        /// <summary>
        /// Comp a player the Golden pass for N days (support, QA, influencers). Goes through the SAME
        /// <c>GrantGoldenAsync</c> seam a validated IAP receipt uses — including the missed-days unlock — so this path
        /// exercises the real thing rather than a shortcut, and works before IAP ships. No money moves.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GrantPass(Guid userId, int days, string q)
        {
            days = Math.Clamp(days <= 0 ? 30 : days, 1, 365);
            var now = DateTime.UtcNow;

            var res = await _pass.GrantGoldenAsync(userId, null, "admin", $"admin:{Guid.NewGuid():N}",
                startsAt: now, expiresAt: now.AddDays(days), originalTransactionId: null, autoRenew: false);

            if (res.Ok)
                TempData["Saved"] = $"Golden pass granted for {days} days (until {res.GoldenUntilUtc:yyyy-MM-dd}) — {res.UnlockedNodes} missed day(s) unlocked.";
            else
                TempData["Error"] = res.Error ?? "Grant failed.";

            return RedirectToAction(nameof(Index), new { q });
        }

        /// <summary>Revoke every un-revoked Golden window (refund, chargeback, mistake). Collected rewards stay
        /// collected; golden rewards still uncollected in the inbox expire with the entitlement.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevokePass(Guid userId, string reason, string q)
        {
            var res = await _pass.RevokeGoldenAsync(userId, null, null, string.IsNullOrWhiteSpace(reason) ? "admin" : reason.Trim());
            if (res.Ok) TempData["Saved"] = "Golden pass revoked.";
            else TempData["Error"] = res.Error ?? "Revoke failed.";
            return RedirectToAction(nameof(Index), new { q });
        }

        /// <summary>Admin override: set a player's VIP level (0–10) and tier. Refreshes the level's maintenance window.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetVip(Guid userId, int vipLevel, int vipTier, string q)
        {
            vipLevel = Math.Clamp(vipLevel, 0, 10);
            vipTier = Math.Clamp(vipTier, 0, 7);
            var now = DateTime.UtcNow;
            DateTime? maintained = vipLevel > 0 ? now.AddDays(30) : (DateTime?)null;

            var n = await _db.UserProfiles
                .Where(p => p.UserId == userId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.VipLevel, vipLevel)
                    .SetProperty(p => p.VipLevelProgress, 0L)
                    .SetProperty(p => p.VipLevelMaintainedThrough, maintained)
                    .SetProperty(p => p.VipTier, (VipTier)vipTier)
                    .SetProperty(p => p.BadgeLitUntil, now.AddDays(30))   // light the tier badge so the grant is visible immediately
                    .SetProperty(p => p.UpdatedAt, now));

            if (n > 0)
                TempData["Saved"] = $"VIP updated — level {vipLevel}, tier {(VipTier)vipTier}.";
            else
                TempData["Error"] = "Player not found.";

            return RedirectToAction(nameof(Index), new { q });
        }

        /// <summary>Admin grant. Chips/Coins/Gems/Kash go through the idempotent, row-locked wallet ledger
        /// (<see cref="TransactionType.AdminAdjustment"/>, fully audited) — NEVER a raw balance write. Loyalty Points
        /// increment <c>UserProfile</c> (soft, non-wagerable). Tokens are never grantable here. Each submit is one
        /// deliberate, audited grant (the unique correlationId is the ledger reference, not a dedupe of admin intent).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GrantBalance(Guid userId, string currency, decimal amount, string q)
        {
            if (amount <= 0m) { TempData["Error"] = "Amount must be positive."; return RedirectToAction(nameof(Index), new { q }); }
            currency = (currency ?? "").Trim();
            try
            {
                if (string.Equals(currency, "LP", StringComparison.OrdinalIgnoreCase))
                {
                    // LP is a wallet currency now (docs/VIP_SPEC.md §3): the balance moves through the ledger like any
                    // other grant, and only the LP SCORE (lifetime, never falls) stays on the profile.
                    long lp = (long)amount;
                    await _wallet.CreditAsync(userId.ToString(), CurrencyType.Lp, lp, TransactionType.AdminAdjustment,
                        $"web-admin-grant:{Guid.NewGuid():N}", new WalletContext { Description = "Admin LP grant" });
                    var n = await _db.UserProfiles
                        .Where(p => p.UserId == userId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(p => p.LifetimeLoyaltyPoints, p => p.LifetimeLoyaltyPoints + lp)
                            .SetProperty(p => p.UpdatedAt, DateTime.UtcNow));
                    TempData[n > 0 ? "Saved" : "Error"] = n > 0 ? $"Granted {lp:#,0} LP." : "Player not found.";
                }
                else if (Enum.TryParse<CurrencyType>(currency, ignoreCase: true, out var cur) && cur != CurrencyType.Tokens)
                {
                    var corr = $"web-admin-grant:{Guid.NewGuid():N}";   // unique audit ref per grant
                    await _wallet.CreditAsync(userId.ToString(), cur, amount, TransactionType.AdminAdjustment, corr,
                        new WalletContext { Description = $"Admin grant ({cur}) via dashboard" });
                    TempData["Saved"] = $"Granted {amount:#,0.####} {cur}.";
                }
                else
                {
                    TempData["Error"] = "Unsupported currency.";
                }
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Grant failed: " + ex.Message;
            }
            return RedirectToAction(nameof(Index), new { q });
        }

        // ---------------------------------------------------------------- moderation

        /// <summary>
        /// Suspend a player: Identity lockout, which the game's password login refuses (CheckPasswordSignInAsync answers
        /// LockedOut). 0 days = until lifted. The security stamp is rotated so any dashboard cookie dies; a game JWT
        /// already issued runs to its expiry (≤60 min) — per-request enforcement in the game is the follow-up that
        /// makes this instant. Never yourself. Reason lands in the audit log with the rest of the form.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Suspend(Guid userId, int days, string reason, string q)
        {
            var user = await _users.FindByIdAsync(userId.ToString());
            if (user == null) { TempData["Error"] = "Player not found."; return RedirectToAction(nameof(Index), new { q }); }
            if (string.Equals(user.Id, User.FindFirstValue(ClaimTypes.NameIdentifier), StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Refused: you cannot suspend yourself.";
                return RedirectToAction(nameof(Index), new { q });
            }
            try
            {
                var until = days <= 0 ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddDays(days);
                await _users.SetLockoutEnabledAsync(user, true);
                var res = await _users.SetLockoutEndDateAsync(user, until);
                if (!res.Succeeded)
                {
                    TempData["Error"] = "Could not suspend: " + string.Join("; ", res.Errors.Select(e => e.Description));
                    return RedirectToAction(nameof(Index), new { q });
                }
                await _users.UpdateSecurityStampAsync(user);
                TempData["Saved"] = $"Suspended {user.Email ?? user.UserName} {(days <= 0 ? "until lifted" : $"for {days} day(s)")}" +
                                    (string.IsNullOrWhiteSpace(reason) ? "." : $" — {reason.Trim()}.") +
                                    " New logins are refused; an already-issued game token lasts until it expires (≤60 min).";
            }
            catch (Exception ex) { TempData["Error"] = $"Could not suspend: {ex.Message}"; }
            return RedirectToAction(nameof(Index), new { q });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Unsuspend(Guid userId, string q)
        {
            var user = await _users.FindByIdAsync(userId.ToString());
            if (user == null) { TempData["Error"] = "Player not found."; return RedirectToAction(nameof(Index), new { q }); }
            try
            {
                var res = await _users.SetLockoutEndDateAsync(user, null);
                TempData[res.Succeeded ? "Saved" : "Error"] = res.Succeeded
                    ? $"Suspension lifted for {user.Email ?? user.UserName}."
                    : "Could not lift: " + string.Join("; ", res.Errors.Select(e => e.Description));
            }
            catch (Exception ex) { TempData["Error"] = $"Could not lift: {ex.Message}"; }
            return RedirectToAction(nameof(Index), new { q });
        }

        /// <summary>Replace an offensive display name with a neutral one (Player + their public id — unique by
        /// construction, so the unique normalized-name index is always satisfied). The player can rename later.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetDisplayName(Guid userId, string q)
        {
            try
            {
                var p = await _db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == userId);
                if (p == null) { TempData["Error"] = "Player not found."; return RedirectToAction(nameof(Index), new { q }); }
                var old = p.DisplayName;
                var fresh = "Player" + (string.IsNullOrEmpty(p.PublicId) ? userId.ToString("N")[..6].ToUpperInvariant() : p.PublicId);
                p.DisplayName = fresh;
                p.DisplayNameNormalized = fresh.ToUpperInvariant();
                p.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                TempData["Saved"] = $"Display name reset: “{old}” → “{fresh}”.";
            }
            catch (Exception ex) { TempData["Error"] = $"Could not reset the name: {ex.Message}"; }
            return RedirectToAction(nameof(Index), new { q });
        }

        /// <summary>Full read-only detail for one player (identity, wallets, progression, VIP, loyalty, lifetime
        /// stats), rendered as a partial for the details modal — fetched on demand so the list stays light.</summary>
        [HttpGet]
        public async Task<IActionResult> Details(Guid id)
        {
            var p = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == id);
            if (p == null) return NotFound();

            var idStr = id.ToString();
            var user = await _db.Users.AsNoTracking().Where(u => u.Id == idStr)
                .Select(u => new { u.Email, u.UserName, u.LockoutEnabled, u.LockoutEnd }).FirstOrDefaultAsync();

            var walletRows = await _db.PlayerWallets.AsNoTracking()
                .Where(w => w.UserId == id).OrderBy(w => w.Currency)
                .Select(w => new { w.Currency, w.Balance, w.GiftedBalance })
                .ToListAsync();

            var gameRows = await _db.UserGameStats.AsNoTracking()
                .Where(g => g.UserId == id)
                .OrderByDescending(g => g.LastPlayedAt)
                .ToListAsync();

            // Reports AGAINST this player — the moderation signal, next to the stats. Counts by status + the last few.
            var reportCounts = await _db.Reports.AsNoTracking().Where(r => r.ReportedUserId == id)
                .GroupBy(r => r.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
            var recentReports = (await _db.Reports.AsNoTracking().Where(r => r.ReportedUserId == id)
                    .OrderByDescending(r => r.CreatedAt).Take(5)
                    .Select(r => new { r.Reason, r.Status, r.TargetType, r.CreatedAt }).ToListAsync())
                .Select(r => new PlayerDetailsVm.ReportRow   // enum→text in memory; not every provider translates ToString()
                {
                    Reason = r.Reason.ToString(),
                    Status = r.Status.ToString(),
                    TargetType = r.TargetType.ToString(),
                    CreatedAt = r.CreatedAt,
                }).ToList();

            var vm = new PlayerDetailsVm
            {
                UserId = p.UserId,
                DisplayName = p.DisplayName,
                Email = user?.Email,
                UserName = user?.UserName,
                SuspendedUntil = user != null && user.LockoutEnabled && user.LockoutEnd != null && user.LockoutEnd > DateTimeOffset.UtcNow ? user.LockoutEnd : null,
                Region = p.Region,
                Bio = p.Bio,
                StatusMessage = p.StatusMessage,
                CreatedAt = p.CreatedAt,
                LastSeenAt = p.LastSeenAt,
                LastPlayedAt = p.LastPlayedAt,
                FriendCount = p.FriendCount,
                ReferralCount = p.ReferralCount,

                OpenReports = reportCounts.Where(c => c.Key == Khela.Common.Reports.ReportStatus.Open || c.Key == Khela.Common.Reports.ReportStatus.Reviewing).Sum(c => c.N),
                TotalReports = reportCounts.Sum(c => c.N),
                ActionTakenReports = reportCounts.Where(c => c.Key == Khela.Common.Reports.ReportStatus.ActionTaken).Sum(c => c.N),
                RecentReports = recentReports,

                Level = p.Level,
                Experience = p.Experience,
                LifetimeExperience = p.LifetimeExperience,

                VipTier = p.VipTier,
                VipLevel = p.VipLevel,
                VipLevelProgress = p.VipLevelProgress,
                VipMaintainedThrough = p.VipLevelMaintainedThrough,
                LifetimeStatusPoints = p.LifetimeStatusPoints,
                BadgeLitUntil = p.BadgeLitUntil,
                HideVipBadge = p.HideVipBadge,

                LoyaltyPoints = (long)Math.Floor(_db.PlayerWallets.AsNoTracking().Where(w => w.UserId == p.UserId && w.Currency == CurrencyType.Lp).Select(w => (decimal?)w.Balance).FirstOrDefault() ?? 0m),
                LifetimeLoyaltyPoints = p.LifetimeLoyaltyPoints,   // the LP Score

                GamesPlayed = p.GamesPlayed,
                GamesWon = p.GamesWon,
                TotalWagered = p.TotalWagered,
                TotalWon = p.TotalWon,
                NetProfit = p.NetProfit,
                BiggestWin = p.BiggestWin,
                CurrentWinStreak = p.CurrentWinStreak,
                LongestWinStreak = p.LongestWinStreak,

                Wallets = walletRows.Select(w => new PlayerDetailsVm.WalletRow
                {
                    Currency = w.Currency.ToString(),
                    Balance = w.Balance,
                    Gifted = w.GiftedBalance
                }).ToList(),

                Games = gameRows.Select(g => new PlayerDetailsVm.GameTab
                {
                    Game = (int)g.GameType,
                    DisplayName = GameName(g.GameType),
                    GamesPlayed = g.GamesPlayed,
                    GamesWon = g.GamesWon,
                    WinRate = g.GamesPlayed > 0 ? Math.Round(100.0 * g.GamesWon / g.GamesPlayed, 1) : 0,
                    TotalWagered = g.TotalWagered,
                    BiggestWin = g.BiggestSingleWin,
                    NetProfit = g.NetProfit,
                    CurrentWinStreak = g.CurrentWinStreak,
                    LongestWinStreak = g.LongestWinStreak,
                    Counters = BuildCounters(g.GameType, g.StatCountersJson),
                }).ToList(),
            };
            return PartialView("_PlayerDetails", vm);
        }

        private static string GameName(Khela.Common.Leaderboards.GameType g) => g switch
        {
            Khela.Common.Leaderboards.GameType.Blackjack => "Blackjack",
            Khela.Common.Leaderboards.GameType.Poker     => "Poker",
            Khela.Common.Leaderboards.GameType.TeenPatti => "Teen Patti",
            Khela.Common.Leaderboards.GameType.Roulette  => "Roulette",
            _ => g.ToString(),
        };

        // The per-game stat-counter list: catalog (key→label, ordered) joined with the stored JSON bag (0 if unlogged).
        private static List<Khela.Common.Stats.StatCounterDto> BuildCounters(Khela.Common.Leaderboards.GameType game, string json)
        {
            var catalog = Khela.Common.Stats.GameStatCatalog.For(game);
            if (catalog.Count == 0) return new List<Khela.Common.Stats.StatCounterDto>();

            Dictionary<string, long> bag = null;
            if (!string.IsNullOrEmpty(json))
            {
                try { bag = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(json); }
                catch { /* corrupt → empty */ }
            }
            var list = new List<Khela.Common.Stats.StatCounterDto>(catalog.Count);
            foreach (var (key, label) in catalog)
                list.Add(new Khela.Common.Stats.StatCounterDto
                {
                    Key = key,
                    Label = label,
                    Value = bag != null && bag.TryGetValue(key, out var v) ? v : 0L,
                });
            return list;
        }
    }

    public sealed class PlayerDetailsVm
    {
        public Guid UserId { get; set; }
        public string DisplayName { get; set; }
        public string Email { get; set; }
        public string UserName { get; set; }
        public string Region { get; set; }
        public string Bio { get; set; }
        public string StatusMessage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public DateTime? LastPlayedAt { get; set; }
        public int FriendCount { get; set; }
        public int ReferralCount { get; set; }

        /// <summary>Identity lockout in force (suspended) — null when not suspended; year 9999 = until lifted.</summary>
        public DateTimeOffset? SuspendedUntil { get; set; }

        // Reports AGAINST this player.
        public int OpenReports { get; set; }
        public int TotalReports { get; set; }
        public int ActionTakenReports { get; set; }
        public List<ReportRow> RecentReports { get; set; } = new();
        public sealed class ReportRow
        {
            public string Reason { get; set; }
            public string Status { get; set; }
            public string TargetType { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        public int Level { get; set; }
        public long Experience { get; set; }
        public long LifetimeExperience { get; set; }

        public VipTier VipTier { get; set; }
        public int VipLevel { get; set; }
        public long VipLevelProgress { get; set; }
        public DateTime? VipMaintainedThrough { get; set; }
        public long LifetimeStatusPoints { get; set; }
        public DateTime? BadgeLitUntil { get; set; }
        public bool HideVipBadge { get; set; }

        public long LoyaltyPoints { get; set; }
        public long LifetimeLoyaltyPoints { get; set; }

        public long GamesPlayed { get; set; }
        public long GamesWon { get; set; }
        public decimal TotalWagered { get; set; }
        public decimal TotalWon { get; set; }
        public decimal NetProfit { get; set; }
        public decimal BiggestWin { get; set; }
        public int CurrentWinStreak { get; set; }
        public int LongestWinStreak { get; set; }

        public List<WalletRow> Wallets { get; set; } = new();
        public List<GameTab> Games { get; set; } = new();

        public sealed class WalletRow
        {
            public string Currency { get; set; }
            public decimal Balance { get; set; }
            public decimal Gifted { get; set; }
        }

        /// <summary>One game's stats for the per-game tab — summary + the playstyle counter list.</summary>
        public sealed class GameTab
        {
            public int Game { get; set; }
            public string DisplayName { get; set; }
            public long GamesPlayed { get; set; }
            public long GamesWon { get; set; }
            public double WinRate { get; set; }
            public decimal TotalWagered { get; set; }
            public decimal BiggestWin { get; set; }
            public decimal NetProfit { get; set; }
            public int CurrentWinStreak { get; set; }
            public int LongestWinStreak { get; set; }
            public List<Khela.Common.Stats.StatCounterDto> Counters { get; set; } = new();
        }
    }

    public sealed class PlayersVm
    {
        public string Query { get; set; }
        public List<PlayerRow> Players { get; set; } = new();
        public string Saved { get; set; }
        public string Error { get; set; }
    }

    public sealed class PlayerRow
    {
        public Guid UserId { get; set; }
        public string DisplayName { get; set; }
        public string Email { get; set; }
        public int Level { get; set; }
        public VipTier VipTier { get; set; }
        public int VipLevel { get; set; }
        public long LoyaltyPoints { get; set; }
        public DateTime? MaintainedThrough { get; set; }
        public DateTimeOffset? SuspendedUntil { get; set; }
        public bool Suspended => SuspendedUntil != null;
        public string SuspendedLabel => SuspendedUntil == null ? "" : SuspendedUntil.Value.Year > 9000 ? "until lifted" : "until " + SuspendedUntil.Value.ToString("yyyy-MM-dd HH:mm") + " UTC";
    }
}
