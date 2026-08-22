using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Khela.Common.Pass;
using Khela.Common.Piggy;
using Khela.Game.Database.Models;
using Khela.Game.Services.Store.Verification;

namespace Khela.Game.Services.Store.Grants
{
    /// <summary>Everything a grant handler needs about the purchase it is fulfilling. Read-only by convention.</summary>
    public sealed class StoreGrantContext
    {
        public Guid UserId { get; set; }
        public StorePurchase Purchase { get; set; }
        /// <summary>The product as it was when the purchase was reserved (the snapshot), never the live catalog.</summary>
        public StoreProductDef Product { get; set; }
        public ReceiptVerification Verification { get; set; }
        /// <summary>The ledger type for money this purchase creates: <see cref="TransactionType.PaidPurchase"/>.</summary>
        public TransactionType CreditType { get; set; } = TransactionType.PaidPurchase;
        /// <summary><c>iap:{purchaseId:N}</c> — the root of every idempotency key.</summary>
        public string IdemRoot { get; set; }
        /// <summary>Ledger ExternalRef (≤128).</summary>
        public string ExternalRef { get; set; }
        public string Description { get; set; }
    }

    /// <summary>One ledger movement the fulfilment made — stored on the purchase row so a refund knows exactly what to reverse.</summary>
    public sealed class StoreFulfilledLine
    {
        public int Kind { get; set; }
        public string Id { get; set; }
        public decimal Amount { get; set; }
        public string CorrelationId { get; set; }
        public decimal Balance { get; set; }
    }

    /// <summary>What the fulfilment actually did (persisted as <c>StorePurchases.FulfilmentJson</c>).</summary>
    public sealed class StoreFulfilment
    {
        public List<StoreFulfilledLine> Lines { get; set; } = new List<StoreFulfilledLine>();
        public string EffectType { get; set; }
        public string EffectArg { get; set; }
        public PiggyBreakResultDto Piggy { get; set; }
        public PassPurchaseResultDto Pass { get; set; }
        public bool? VipBoosterApplied { get; set; }
        /// <summary>Anomalies worth an admin's eye (limit exceeded at redeem, account-binding mismatch, capacity paid on a moved bank…).</summary>
        public List<string> Flags { get; set; } = new List<string>();
        public DateTime? CompletedAtUtc { get; set; }
    }

    /// <summary>
    /// Fulfils one product EFFECT (piggy break, golden pass, VIP booster) for a verified purchase. Plain reward lines are
    /// granted by the spine itself; an effect is the kind-specific part. CONTRACT: idempotent on the purchase (the keys
    /// derive from <see cref="StoreGrantContext.IdemRoot"/> / the store transaction id), and THROW on a failure that
    /// should be retried — the spine leaves the row incomplete and the reconciler re-drives it. Adding a purchasable
    /// effect = one class here + a catalog entry (docs/IAP_SPEC.md §5.2 g).
    /// </summary>
    public interface IStoreGrantHandler
    {
        /// <summary>The catalog <c>effect.type</c> this handles ("PiggyBreak", "GoldenPass", "VipBooster", …).</summary>
        string Effect { get; }

        Task GrantAsync(StoreGrantContext ctx, StoreFulfilment fulfilment, CancellationToken ct);
    }
}
