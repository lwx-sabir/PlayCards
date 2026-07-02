using Khela.Game.Database;
using Khela.Game.Database.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Khela.Web.Controllers
{
    /// <summary>
    /// Read-only per-player wallet + ledger audit viewer. Search a player (display name, email, or AspNetUsers.Id),
    /// then see their per-currency balances and a paginated, newest-first view of their <c>WalletTransactions</c>
    /// (the audited money ledger). Strictly READ-ONLY — no mutations here; balance changes only ever go through
    /// IWalletService on the game side.
    /// </summary>
    [Authorize(Policy = "Admin")]
    public sealed class WalletsController : Controller
    {
        private const int PageSize = 25;

        private readonly AppDbContext _db;

        public WalletsController(AppDbContext db) => _db = db;

        [HttpGet]
        public async Task<IActionResult> Index(string q, int page = 0)
        {
            var vm = new WalletsVm { Query = q, Page = page < 0 ? 0 : page };
            if (string.IsNullOrWhiteSpace(q)) return View(vm);
            q = q.Trim();

            // Resolve the player: Guid id → email (has '@') → display-name contains.
            Guid? userId = null;
            if (Guid.TryParse(q, out var g))
            {
                userId = g;
            }
            else if (q.Contains('@'))
            {
                var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == q);
                if (u != null && Guid.TryParse(u.Id, out var ug)) { userId = ug; vm.PlayerName = u.UserName; }
            }
            else
            {
                var prof = await _db.UserProfiles.AsNoTracking()
                    .Where(p => p.DisplayName.Contains(q))
                    .OrderBy(p => p.DisplayName)
                    .FirstOrDefaultAsync();
                if (prof != null) { userId = prof.UserId; vm.PlayerName = prof.DisplayName; }
            }

            if (userId == null) { vm.NotFound = true; return View(vm); }

            vm.Resolved = true;
            vm.UserId = userId.Value;
            if (string.IsNullOrEmpty(vm.PlayerName))
                vm.PlayerName = (await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId))?.DisplayName
                                ?? userId.Value.ToString();

            vm.Wallets = await _db.PlayerWallets.AsNoTracking()
                .Where(w => w.UserId == userId)
                .OrderBy(w => w.Currency)
                .Select(w => new WalletBalance { Currency = w.Currency, Balance = w.Balance, Gifted = w.GiftedBalance })
                .ToListAsync();

            var ledger = from t in _db.WalletTransactions.AsNoTracking()
                         join w in _db.PlayerWallets on t.WalletId equals w.WalletId
                         where w.UserId == userId
                         orderby t.CreatedAt descending
                         select new LedgerRow
                         {
                             CreatedAt = t.CreatedAt,
                             Currency = w.Currency,
                             Type = t.Type,
                             Amount = t.Amount,
                             BalanceAfter = t.BalanceAfter,
                             Status = t.Status,
                             RoundId = t.RoundId,
                             TableId = t.TableId,
                             Description = t.Description
                         };

            vm.TotalTx = await ledger.CountAsync();
            vm.Ledger = await ledger.Skip(vm.Page * PageSize).Take(PageSize).ToListAsync();
            return View(vm);
        }
    }

    public sealed class WalletsVm
    {
        public string Query { get; set; }
        public bool Resolved { get; set; }
        public bool NotFound { get; set; }
        public string PlayerName { get; set; }
        public Guid UserId { get; set; }
        public List<WalletBalance> Wallets { get; set; } = new();
        public List<LedgerRow> Ledger { get; set; } = new();
        public int Page { get; set; }
        public int PageSize => 25;
        public int TotalTx { get; set; }
        public bool HasPrev => Page > 0;
        public bool HasNext => (Page + 1) * 25 < TotalTx;
    }

    public sealed class WalletBalance
    {
        public CurrencyType Currency { get; set; }
        public decimal Balance { get; set; }
        public decimal Gifted { get; set; }
    }

    public sealed class LedgerRow
    {
        public DateTime CreatedAt { get; set; }
        public CurrencyType Currency { get; set; }
        public TransactionType Type { get; set; }
        public decimal Amount { get; set; }
        public decimal? BalanceAfter { get; set; }
        public TransactionStatus Status { get; set; }
        public string RoundId { get; set; }
        public string TableId { get; set; }
        public string Description { get; set; }
    }
}
