using System;
using System.Collections.Generic;
using Khela.Common.Pass;
using Khela.Common.Piggy;
using Khela.Common.Rewards;

namespace Khela.Common.Store
{
    /// <summary>
    /// Which store a purchase came through. Persisted as <c>int</c> (StorePurchases.Platform) and sent on the wire —
    /// <b>APPEND ONLY</b>, never renumber. Adding a store vendor = a new member here + one verifier adapter on the
    /// server + a store-product id per product in the catalog; nothing else changes (docs/IAP_SPEC.md §9).
    /// </summary>
    public enum StorePlatform
    {
        Unknown = 0,
        GooglePlay = 1,
        AppStore = 2,
        Web = 3,
        Amazon = 4,
        /// <summary>Unity's Editor fake store. The server accepts it ONLY in Development.</summary>
        Fake = 99,
    }

    /// <summary>1:1 with Unity IAP's ProductType. Persisted as <c>int</c> — append only.</summary>
    public enum StoreProductType
    {
        Consumable = 0,
        NonConsumable = 1,
        Subscription = 2,
    }

    /// <summary>
    /// What a redeem call concluded. The endpoint answers 200 with <c>Ok</c> + this status in the body; the client
    /// confirms the store order on <see cref="Granted"/>, <see cref="AlreadyGranted"/> and <see cref="Invalid"/>
    /// (a bad receipt must not re-deliver forever) and keeps it pending on everything else.
    /// </summary>
    public enum RedeemStatus
    {
        Granted = 0,
        AlreadyGranted = 1,
        Invalid = 2,
        Pending = 3,
        ProductUnavailable = 4,
        PlatformDisabled = 5,
        StoreDisabled = 6,
        Error = 7,
    }

    /// <summary>A kind-specific fulfilment beyond plain reward lines (piggy break, golden pass, VIP booster).</summary>
    public sealed class StoreEffectDto
    {
        /// <summary>"PiggyBreak" | "GoldenPass" | "VipBooster" — one grant handler per type on the server.</summary>
        public string Type { get; set; }
        /// <summary>Type-specific argument: the piggy option, the pass key, the booster kind.</summary>
        public string Arg { get; set; }
        /// <summary>Extra type-specific parameters (e.g. "tier" for a piggy product).</summary>
        public Dictionary<string, string> Params { get; set; }
    }

    public sealed class StoreSectionDto
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// How a time-limited sale changes a product. Persisted in the catalog document and sent on the wire — append only.
    /// The two kinds exist because the stores own the PRICE: a server can make a product pay more, but it cannot make
    /// Google or Apple charge less — a lower price is a different SKU in their consoles.
    /// </summary>
    public enum StoreSaleKind
    {
        None = 0,
        /// <summary>Same SKU, same price, every currency/XP line pays <c>+Percent%</c>. Server-side, instant, reversible.</summary>
        ValueBonus = 1,
        /// <summary>The card sells a second SKU priced lower in the consoles (<see cref="StoreSaleDto.SaleProductId"/>); the regular price shows struck through.</summary>
        PriceOff = 2,
    }

    /// <summary>A sale that is ACTIVE right now on a product, as the client should show it. Absent = no sale.</summary>
    public sealed class StoreSaleDto
    {
        public StoreSaleKind Kind { get; set; }
        /// <summary>ValueBonus: the bonus applied to the lines. PriceOff: the advertised discount (display only — the SKU's store price is the truth).</summary>
        public int Percent { get; set; }
        /// <summary>When the sale ends (server clock). Count down from <see cref="StoreCatalogDto.ServerTimeUtc"/>, never the device clock.</summary>
        public DateTime EndsAtUtc { get; set; }
        /// <summary>Optional ribbon text ("WEEKEND", "DIWALI"). Empty = show the percent.</summary>
        public string Label { get; set; }
        /// <summary>ValueBonus: what the product pays DURING the sale — the exact amounts the server will grant (<see cref="StoreSaleMath"/>).</summary>
        public List<RewardGrant> Lines { get; set; }
        /// <summary>PriceOff: the SKU to buy instead of this product's own. Its localized price is the sale price.</summary>
        public string SaleProductId { get; set; }
    }

    /// <summary>One product as ONE platform sees it: our stable id, the store-product id to buy on this platform,
    /// what it pays (for display — the server grants from its own catalog, never from this), and per-user availability.</summary>
    public sealed class StoreProductDto
    {
        public string Id { get; set; }
        public string StoreProductId { get; set; }
        public StoreProductType ProductType { get; set; }
        public string Section { get; set; }
        public int SortOrder { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        /// <summary>The card's RIBBON text ("2X VALUE", "BEST VALUE").</summary>
        public string Badge { get; set; }
        /// <summary>A second, independent badge for the card's other corner (crown / "POPULAR") — see <c>StoreProductDef.Badge2</c>.</summary>
        public string Badge2 { get; set; }
        public int BonusPercent { get; set; }
        public bool Featured { get; set; }
        public List<string> Images { get; set; }
        /// <summary>What the product pays (currency/XP/… lines). Empty for effect-only products (piggy, pass, boosters).</summary>
        public List<RewardGrant> Lines { get; set; }
        public StoreEffectDto Effect { get; set; }
        /// <summary>Reference price in USD — for display FALLBACK only; the store's localized price wins on device.</summary>
        public decimal UsdReference { get; set; }
        public DateTime? AvailableToUtc { get; set; }
        /// <summary>False when the player can't buy it right now (limit reached, level gate, window closed, piggy not full…).</summary>
        public bool Purchasable { get; set; }
        /// <summary>Why not, when <see cref="Purchasable"/> is false. Human-readable.</summary>
        public string Reason { get; set; }
        public int PurchasedCount { get; set; }
        public int MaxPerUser { get; set; }
        public int MaxPerUserPerDay { get; set; }
        public int MinLevel { get; set; }
        /// <summary>The sale active on this product RIGHT NOW, or null. Drives the ribbon, the struck price and the countdown.</summary>
        public StoreSaleDto Sale { get; set; }
        /// <summary>
        /// Set when this product exists only as the cheaper SKU of another product's PriceOff sale: the id of that regular
        /// product. Such a product is NEVER a card of its own — the regular product's card sells it while the sale is on.
        /// It is still listed so the store fetches its price and it can be bought by id.
        /// </summary>
        public string SaleOf { get; set; }
    }

    public sealed class StoreCatalogDto
    {
        public StorePlatform Platform { get; set; }
        /// <summary>The store as a whole is on (kill switch).</summary>
        public bool Enabled { get; set; }
        /// <summary>This platform is on (kill switch + credentials loaded server-side).</summary>
        public bool PlatformEnabled { get; set; }
        public int Version { get; set; }
        public List<StoreSectionDto> Sections { get; set; }
        public List<StoreProductDto> Products { get; set; }
        public DateTime ServerTimeUtc { get; set; }
    }

    /// <summary>"May I buy this now?" — asked BEFORE the store sheet opens, so limits are enforced where a refusal costs nothing.</summary>
    public sealed class StoreIntentRequest
    {
        public string ProductId { get; set; }
        public StorePlatform Platform { get; set; }
    }

    public sealed class StoreIntentResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string StoreProductId { get; set; }
        /// <summary>Opaque id the client echoes as <see cref="RedeemPurchaseRequest.ClientPurchaseId"/> (funnel analytics).</summary>
        public string IntentId { get; set; }
    }

    /// <summary>
    /// The receipt, handed to the server for verification. Nothing in here is trusted on its own: the server
    /// verifies with the store, cross-checks the product against its catalog, and grants from the catalog.
    /// </summary>
    public sealed class RedeemPurchaseRequest
    {
        public StorePlatform Platform { get; set; }
        /// <summary>OUR product id (a hint — the verified store product is what is granted).</summary>
        public string ProductId { get; set; }
        public string StoreProductId { get; set; }
        /// <summary>Unity's <c>order.Info.TransactionID</c>.</summary>
        public string TransactionId { get; set; }
        /// <summary>Unity's unified receipt JSON (<c>order.Info.Receipt</c>) — Google Play / Fake.</summary>
        public string Receipt { get; set; }
        /// <summary>Apple StoreKit 2 signed transaction (<c>order.Info.Apple.jwsRepresentation</c>).</summary>
        public string Jws { get; set; }
        /// <summary>Client-reported localized price — informational, recorded beside the verified purchase.</summary>
        public long? ClientPriceMicros { get; set; }
        public string ClientPriceCurrency { get; set; }
        public string ClientPurchaseId { get; set; }
        public string ClientVersion { get; set; }
    }

    public sealed class RedeemPurchaseResultDto
    {
        public bool Ok { get; set; }
        public RedeemStatus Status { get; set; }
        public string Error { get; set; }
        /// <summary>True when the failure was transient (store API unreachable, server busy): keep the order pending and retry.</summary>
        public bool Transient { get; set; }
        public Guid? PurchaseId { get; set; }
        public string ProductId { get; set; }
        /// <summary>What was actually applied (the ledger's numbers, not the catalog's).</summary>
        public List<GrantedLineDto> Grants { get; set; }
        /// <summary>Post-credit balances, computed under the wallet's row lock.</summary>
        public decimal NewChipBalance { get; set; }
        public decimal NewKashBalance { get; set; }
        public bool IsTest { get; set; }
        /// <summary>Set when the product was a piggy break — the bank's payout + new state for the celebration.</summary>
        public PiggyBreakResultDto Piggy { get; set; }
        /// <summary>Set when the product was the golden pass.</summary>
        public PassPurchaseResultDto Pass { get; set; }
    }

    /// <summary>Re-run previously completed store transactions (subscriptions / non-consumables) through the same
    /// idempotent path — the store-mandated "Restore purchases".</summary>
    public sealed class StoreRestoreRequest
    {
        public StorePlatform Platform { get; set; }
        public List<RedeemPurchaseRequest> Items { get; set; }
    }

    public sealed class StoreRestoreResultDto
    {
        public List<RedeemPurchaseResultDto> Results { get; set; }
    }

    /// <summary>One past purchase, as the player may see it (support / history).</summary>
    public sealed class StorePurchaseDto
    {
        public Guid PurchaseId { get; set; }
        public string ProductId { get; set; }
        public StorePlatform Platform { get; set; }
        public string Status { get; set; }
        public decimal UsdReference { get; set; }
        public bool IsTest { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? GrantedAtUtc { get; set; }
    }
}
